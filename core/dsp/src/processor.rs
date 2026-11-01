//! Real-time EQ processor.
//!
//! Guarantees inside [`Processor::process`]:
//! * no heap allocation, no locks, no syscalls;
//! * parameter changes are smoothed (no zipper noise / clicks when dragging);
//! * bypass is a short crossfade, not a hard switch;
//! * any non-finite sample makes the block fall back to the dry signal and
//!   resets filter state, so a bad coefficient can never blow up the output.

use crate::biquad::{Band, Coeffs, FilterKind, State};
use crate::params::{EqParams, MAX_BANDS};

pub const MAX_CHANNELS: usize = 8;

/// Parameters are re-evaluated every `SUB_BLOCK` frames.
const SUB_BLOCK: usize = 32;
/// Time constant for band / preamp smoothing (seconds).
const SMOOTH_TAU: f64 = 0.02;
/// Bypass crossfade length (seconds).
const FADE_SECS: f64 = 0.02;
/// Limiter ceiling, -0.1 dBFS.
const LIMIT_CEILING: f64 = 0.988_553_1;
/// Limiter release time constant (seconds).
const LIMIT_RELEASE: f64 = 0.08;
/// Limiter lookahead (seconds): gain starts dropping this long before a peak,
/// so heavy limiting (e.g. with volume boost) stays smooth instead of
/// slicing each peak in one sample. Adds this much latency while on.
const LIMIT_LOOKAHEAD: f64 = 0.001;
/// Lookahead buffer capacity in frames (1 ms up to 256 kHz).
const MAX_LOOKAHEAD: usize = 256;

#[derive(Clone, Copy, Debug, PartialEq)]
struct Shape {
    ln_freq: f64,
    gain_db: f64,
    ln_q: f64,
}

impl Shape {
    fn of(b: &Band) -> Shape {
        Shape {
            ln_freq: b.freq.max(1.0).ln(),
            gain_db: b.gain_db,
            ln_q: b.q.max(0.01).ln(),
        }
    }
    fn to_band(self, kind: FilterKind) -> Band {
        Band {
            kind,
            freq: self.ln_freq.exp(),
            gain_db: self.gain_db,
            q: self.ln_q.exp(),
            enabled: true,
        }
    }
}

#[derive(Clone, Copy)]
struct Slot {
    kind: FilterKind,
    cur: Shape,
    designed: Shape,
    coeffs: Coeffs,
    active: bool,
}

impl Default for Slot {
    fn default() -> Self {
        let s = Shape::of(&Band::DISABLED);
        Slot {
            kind: FilterKind::Peak,
            cur: s,
            designed: s,
            coeffs: Coeffs::IDENTITY,
            active: false,
        }
    }
}

pub struct Processor {
    sample_rate: f64,
    target: EqParams,
    slots: [Slot; MAX_BANDS],
    state: [[State; MAX_CHANNELS]; MAX_BANDS],
    /// Highest slot index + 1 that may be active.
    used: usize,
    preamp: f64,
    wet: f64,
    balance: f64,
    limiter_gain: f64,
    smooth_k: f64,
    fade_step: f64,
    release_k: f64,
    attack_k: f64,
    /// Lookahead delay line (per channel) and the gain each frame needs.
    la_buf: [[f32; MAX_LOOKAHEAD]; MAX_CHANNELS],
    la_want: [f64; MAX_LOOKAHEAD],
    la_len: usize,
    la_pos: usize,
    /// Whether the lookahead path was active last block (to reset on toggle).
    la_on: bool,
}

impl Processor {
    pub fn new(sample_rate: f64, params: EqParams) -> Self {
        let mut p = Processor {
            sample_rate,
            target: params,
            slots: [Slot::default(); MAX_BANDS],
            state: [[State::default(); MAX_CHANNELS]; MAX_BANDS],
            used: 0,
            preamp: 1.0,
            wet: 1.0,
            balance: 0.0,
            limiter_gain: 1.0,
            smooth_k: 0.0,
            fade_step: 0.0,
            release_k: 0.0,
            attack_k: 0.0,
            la_buf: [[0.0; MAX_LOOKAHEAD]; MAX_CHANNELS],
            la_want: [1.0; MAX_LOOKAHEAD],
            la_len: 1,
            la_pos: 0,
            la_on: false,
        };
        p.set_sample_rate(sample_rate);
        p.snap_to_target();
        p
    }

    pub fn sample_rate(&self) -> f64 {
        self.sample_rate
    }

    pub fn params(&self) -> &EqParams {
        &self.target
    }

    /// Change sample rate (device switch). Resets filter memory.
    pub fn set_sample_rate(&mut self, sample_rate: f64) {
        self.sample_rate = sample_rate;
        self.smooth_k = 1.0 - (-(SUB_BLOCK as f64) / (SMOOTH_TAU * sample_rate)).exp();
        self.fade_step = SUB_BLOCK as f64 / (FADE_SECS * sample_rate);
        self.release_k = 1.0 - (-1.0 / (LIMIT_RELEASE * sample_rate)).exp();
        self.la_len = ((LIMIT_LOOKAHEAD * sample_rate).round() as usize).clamp(1, MAX_LOOKAHEAD);
        // Reach ~99% of the needed reduction within the lookahead window.
        self.attack_k = 1.0 - (-5.0 / self.la_len as f64).exp();
        for s in &mut self.slots {
            if s.active {
                s.coeffs = Coeffs::design(&s.cur.to_band(s.kind), sample_rate);
                s.designed = s.cur;
            }
        }
        self.reset();
    }

    /// Set new target parameters. RT-safe: just a copy.
    #[inline]
    pub fn set_params(&mut self, params: &EqParams) {
        self.target = *params;
    }

    /// Clear filter memory and limiter.
    pub fn reset(&mut self) {
        for row in &mut self.state {
            for st in row.iter_mut() {
                st.reset();
            }
        }
        self.limiter_gain = 1.0;
        self.reset_lookahead();
    }

    fn reset_lookahead(&mut self) {
        for ch in &mut self.la_buf {
            ch.fill(0.0);
        }
        self.la_want.fill(1.0);
        self.la_pos = 0;
    }

    /// Jump straight to target values (no smoothing). Used at startup.
    pub fn snap_to_target(&mut self) {
        for i in 0..MAX_BANDS {
            let band = self.target_band(i);
            let slot = &mut self.slots[i];
            slot.kind = band.kind;
            slot.cur = Shape::of(&band);
            slot.active = band.enabled && !(band.kind.is_gain_based() && band.gain_db == 0.0);
            slot.coeffs = if slot.active {
                Coeffs::design(&band, self.sample_rate)
            } else {
                Coeffs::IDENTITY
            };
            slot.designed = slot.cur;
        }
        self.recount_used();
        self.preamp = db_to_lin(self.target.preamp_db);
        self.balance = self.target.balance.clamp(-1.0, 1.0);
        self.wet = if self.target.bypass { 0.0 } else { 1.0 };
        self.reset();
    }

    #[inline]
    fn target_band(&self, i: usize) -> Band {
        if i < self.target.band_count {
            self.target.bands[i]
        } else {
            Band::DISABLED
        }
    }

    fn recount_used(&mut self) {
        self.used = self
            .slots
            .iter()
            .rposition(|s| s.active)
            .map_or(0, |i| i + 1);
    }

    /// Advance smoothing by one sub-block and refresh coefficients.
    fn update_slots(&mut self) {
        let k = self.smooth_k;
        let limit = self.target.band_count.max(self.used);
        let mut changed_active = false;
        for i in 0..limit {
            let band = self.target_band(i);
            let slot = &mut self.slots[i];

            if band.kind != slot.kind {
                // Shape change: snap (cannot interpolate between filter types).
                slot.kind = band.kind;
                slot.cur = Shape::of(&band);
                slot.active = false;
                for st in &mut self.state[i] {
                    st.reset();
                }
            }

            let gain_based = band.kind.is_gain_based();
            let mut tgt = Shape::of(&band);
            if !band.enabled {
                if !gain_based {
                    if slot.active {
                        slot.active = false;
                        changed_active = true;
                        for st in &mut self.state[i] {
                            st.reset();
                        }
                    }
                    continue;
                }
                // Gain-based: fade gain to 0 at current shape.
                tgt = Shape {
                    gain_db: 0.0,
                    ..slot.cur
                };
            }

            if !slot.active {
                if gain_based && tgt.gain_db == 0.0 {
                    continue;
                }
                // Start gain-based bands from 0 dB so they fade in.
                slot.cur = if gain_based {
                    Shape {
                        gain_db: 0.0,
                        ..tgt
                    }
                } else {
                    tgt
                };
                slot.active = true;
                changed_active = true;
                slot.designed = Shape {
                    gain_db: f64::NAN,
                    ..slot.cur
                }; // force design
            }

            slot.cur.ln_freq = approach(slot.cur.ln_freq, tgt.ln_freq, k, 1e-5);
            slot.cur.gain_db = approach(slot.cur.gain_db, tgt.gain_db, k, 1e-4);
            slot.cur.ln_q = approach(slot.cur.ln_q, tgt.ln_q, k, 1e-5);

            if gain_based && slot.cur.gain_db == 0.0 && tgt.gain_db == 0.0 {
                slot.active = false;
                changed_active = true;
                for st in &mut self.state[i] {
                    st.reset();
                }
                continue;
            }

            if slot.cur != slot.designed {
                slot.coeffs = Coeffs::design(&slot.cur.to_band(slot.kind), self.sample_rate);
                slot.designed = slot.cur;
            }
        }
        if changed_active || limit < self.used {
            self.recount_used();
        }
    }

    /// Process interleaved `f32` audio in place. `channels` may exceed
    /// [`MAX_CHANNELS`]; extra channels pass through untouched.
    pub fn process(&mut self, buf: &mut [f32], channels: usize) {
        if channels == 0 {
            return;
        }
        let nch = channels.min(MAX_CHANNELS);
        let frames = buf.len() / channels;
        let mut start = 0;
        while start < frames {
            let end = (start + SUB_BLOCK).min(frames);
            self.process_sub_block(&mut buf[start * channels..end * channels], channels, nch);
            start = end;
        }
    }

    fn process_sub_block(&mut self, buf: &mut [f32], channels: usize, nch: usize) {
        let n = buf.len() / channels;
        let inv_n = 1.0 / n as f64;

        // Ramps (start -> end of this sub-block)
        let pre0 = self.preamp;
        let pre1 = approach(pre0, db_to_lin(self.target.preamp_db), self.smooth_k, 1e-6);
        let wet0 = self.wet;
        let wet1 = if self.target.bypass {
            (wet0 - self.fade_step).max(0.0)
        } else {
            (wet0 + self.fade_step).min(1.0)
        };
        let bal0 = self.balance;
        let bal1 = approach(
            bal0,
            self.target.balance.clamp(-1.0, 1.0),
            self.smooth_k,
            1e-5,
        );
        self.preamp = pre1;
        self.wet = wet1;
        self.balance = bal1;

        if wet0 == 0.0 && wet1 == 0.0 {
            // Fully bypassed: leave audio untouched, keep state clean for fade-in.
            if self.used > 0 || self.limiter_gain != 1.0 {
                self.reset();
            }
            self.update_slots();
            return;
        }

        self.update_slots();

        let mut bad = false;
        for ch in 0..nch {
            let (bal_a, bal_b) = if nch == 2 {
                (channel_balance(bal0, ch), channel_balance(bal1, ch))
            } else {
                (1.0, 1.0)
            };
            for f in 0..n {
                let t = f as f64 * inv_n;
                let idx = f * channels + ch;
                let dry = buf[idx] as f64;
                let mut y = dry * lerp(pre0, pre1, t);
                for b in 0..self.used {
                    let slot = &self.slots[b];
                    if slot.active {
                        y = self.state[b][ch].process(&slot.coeffs, y);
                    }
                }
                y *= lerp(bal_a, bal_b, t);
                let w = lerp(wet0, wet1, t);
                let mut out = dry + (y - dry) * w;
                if !out.is_finite() {
                    bad = true;
                    out = if dry.is_finite() { dry } else { 0.0 };
                }
                buf[idx] = out as f32;
            }
        }
        if bad {
            self.reset();
        }

        if self.target.limiter {
            if !self.la_on {
                self.reset_lookahead();
                self.la_on = true;
            }
            self.limit(buf, channels, nch);
        } else {
            self.limiter_gain = 1.0;
            self.la_on = false;
        }
    }

    /// Lookahead peak limiter. Each frame is delayed by `la_len`; the gain
    /// applied to the frame leaving the delay is driven by the lowest gain any
    /// frame inside the window needs, so it ramps down before a peak arrives.
    /// A final clamp guarantees the ceiling even if the ramp falls short.
    fn limit(&mut self, buf: &mut [f32], channels: usize, nch: usize) {
        let len = self.la_len;
        let mut g = self.limiter_gain;
        for frame in buf.chunks_exact_mut(channels) {
            let peak = frame[..nch]
                .iter()
                .fold(0.0f64, |m, &s| m.max((s as f64).abs()));
            let want = if peak > LIMIT_CEILING {
                LIMIT_CEILING / peak
            } else {
                1.0
            };
            let pos = self.la_pos;
            // Window = frames t-len (leaving now) ..= t (just arrived).
            let req = self.la_want[..len].iter().fold(want, |m, &w| m.min(w));
            self.la_want[pos] = want;
            if req < g {
                g += (req - g) * self.attack_k;
            } else {
                g += (req - g) * self.release_k;
            }
            for (ch, s) in frame[..nch].iter_mut().enumerate() {
                let delayed = self.la_buf[ch][pos];
                self.la_buf[ch][pos] = *s;
                let out = (delayed as f64 * g).clamp(-LIMIT_CEILING, LIMIT_CEILING);
                *s = out as f32;
            }
            self.la_pos = if pos + 1 == len { 0 } else { pos + 1 };
        }
        self.limiter_gain = g;
    }
}

#[inline(always)]
fn lerp(a: f64, b: f64, t: f64) -> f64 {
    a + (b - a) * t
}

#[inline(always)]
fn approach(cur: f64, target: f64, k: f64, eps: f64) -> f64 {
    if cur.is_nan() {
        return target;
    }
    let next = cur + (target - cur) * k;
    if (target - next).abs() < eps {
        target
    } else {
        next
    }
}

#[inline(always)]
fn channel_balance(balance: f64, ch: usize) -> f64 {
    if ch == 0 {
        (1.0 - balance).min(1.0)
    } else {
        (1.0 + balance).min(1.0)
    }
}

#[inline]
fn db_to_lin(db: f64) -> f64 {
    10f64.powf(db / 20.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f64::consts::PI;

    const SR: f64 = 48_000.0;

    fn sine(freq: f64, amp: f64, frames: usize, channels: usize) -> Vec<f32> {
        let mut v = Vec::with_capacity(frames * channels);
        for n in 0..frames {
            let s = (amp * (2.0 * PI * freq * n as f64 / SR).sin()) as f32;
            for _ in 0..channels {
                v.push(s);
            }
        }
        v
    }

    fn peak_db(buf: &[f32], channels: usize, ch: usize, skip_frames: usize) -> f64 {
        let p = buf
            .chunks_exact(channels)
            .skip(skip_frames)
            .fold(0.0f64, |m, f| m.max(f[ch].abs() as f64));
        20.0 * p.log10()
    }

    fn params(bands: &[Band]) -> EqParams {
        let mut p = EqParams::from_bands(bands);
        p.limiter = false;
        p
    }

    #[test]
    fn flat_is_transparent() {
        let mut proc = Processor::new(SR, params(&[]));
        let input = sine(440.0, 0.5, 4800, 2);
        let mut buf = input.clone();
        proc.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn steady_state_matches_response() {
        let mut proc = Processor::new(SR, params(&[Band::peak(1000.0, 6.0, 1.0)]));
        let mut buf = sine(1000.0, 0.1, 48_000, 2);
        proc.process(&mut buf, 2);
        let g = peak_db(&buf, 2, 0, 24_000) - 20.0 * 0.1f64.log10();
        assert!((g - 6.0).abs() < 0.05, "{g}");
    }

    #[test]
    fn limiter_prevents_clipping() {
        let mut p = EqParams::from_bands(&[Band::peak(1000.0, 12.0, 1.0)]);
        p.limiter = true;
        let mut proc = Processor::new(SR, p);
        let mut buf = sine(1000.0, 1.0, 48_000, 2);
        proc.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.abs() as f64 <= LIMIT_CEILING + 1e-6));
    }

    #[test]
    fn boosted_signal_is_limited_smoothly() {
        // +12 dB on a loud sine: never above the ceiling, and the gain
        // settles instead of chopping each peak (output stays a clean sine:
        // its peak hugs the ceiling with little ripple once settled).
        let mut p = EqParams::from_bands(&[]);
        p.preamp_db = 12.0;
        p.limiter = true;
        let mut proc = Processor::new(SR, p);
        let mut buf = sine(200.0, 0.8, 48_000, 2);
        proc.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.abs() as f64 <= LIMIT_CEILING + 1e-6));
        let settled = peak_db(&buf, 2, 0, 24_000);
        assert!(settled > -1.5 && settled <= 0.0, "{settled}");
    }

    #[test]
    fn transient_after_silence_never_clips() {
        let mut p = EqParams::from_bands(&[]);
        p.preamp_db = 12.0;
        p.limiter = true;
        let mut proc = Processor::new(SR, p);
        let mut buf = vec![0.0f32; 9600];
        for (i, s) in buf.iter_mut().enumerate().skip(4800) {
            *s = if (i / 2) % 40 < 20 { 0.9 } else { -0.9 };
        }
        proc.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.abs() as f64 <= LIMIT_CEILING + 1e-6));
    }

    #[test]
    fn bypass_fades_to_dry() {
        let mut proc = Processor::new(SR, params(&[Band::peak(1000.0, 9.0, 1.0)]));
        let mut p = *proc.params();
        p.bypass = true;
        proc.set_params(&p);
        let input = sine(1000.0, 0.2, 9600, 2);
        let mut buf = input.clone();
        proc.process(&mut buf, 2);
        // After the 20 ms fade, output must equal input exactly.
        let after = (FADE_SECS * SR) as usize + SUB_BLOCK;
        assert_eq!(&buf[after * 2..], &input[after * 2..]);
    }

    #[test]
    fn parameter_jump_is_smoothed() {
        // Low-freq sine: per-sample deltas are tiny, so any click shows up.
        let mut proc = Processor::new(SR, params(&[Band::peak(100.0, 0.0, 1.0)]));
        let mut buf = sine(100.0, 0.3, 4800, 1);
        proc.process(&mut buf[..2400], 1);
        proc.set_params(&params(&[Band::peak(100.0, 12.0, 1.0)]));
        proc.process(&mut buf[2400..], 1);
        let max_step = buf
            .windows(2)
            .map(|w| (w[1] - w[0]).abs())
            .fold(0.0f32, f32::max);
        // Natural max step at 12 dB boost: 0.3*4*2*pi*100/48000 ~= 0.0157
        assert!(max_step < 0.02, "{max_step}");
    }

    #[test]
    fn balance_attenuates_one_side() {
        let mut p = params(&[]);
        p.balance = 1.0; // full right
        let mut proc = Processor::new(SR, p);
        let mut buf = sine(440.0, 0.5, 4800, 2);
        proc.process(&mut buf, 2);
        assert!(buf.iter().step_by(2).all(|&s| s == 0.0));
        assert!(peak_db(&buf, 2, 1, 0) > -6.1);
    }

    #[test]
    fn disabling_band_returns_to_flat() {
        let mut proc = Processor::new(SR, params(&[Band::peak(1000.0, 6.0, 1.0)]));
        let mut off = Band::peak(1000.0, 6.0, 1.0);
        off.enabled = false;
        proc.set_params(&params(&[off]));
        let input = sine(1000.0, 0.1, 48_000, 1);
        let mut buf = input.clone();
        proc.process(&mut buf, 1);
        assert_eq!(&buf[24_000..], &input[24_000..]);
        assert_eq!(proc.used, 0);
    }

    #[test]
    fn extra_channels_pass_through() {
        let mut proc = Processor::new(SR, params(&[Band::peak(1000.0, 6.0, 1.0)]));
        let input = sine(1000.0, 0.1, 480, MAX_CHANNELS + 2);
        let mut buf = input.clone();
        proc.process(&mut buf, MAX_CHANNELS + 2);
        let c = MAX_CHANNELS + 2;
        for (a, b) in buf.chunks_exact(c).zip(input.chunks_exact(c)) {
            assert_eq!(a[MAX_CHANNELS..], b[MAX_CHANNELS..]);
        }
    }
}
