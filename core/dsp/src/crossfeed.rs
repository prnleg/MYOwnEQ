//! Headphone crossfeed (Bauer stereophonic-to-binaural, the "bs2b" design).
//!
//! On speakers each ear hears both speakers; on headphones each ear hears only one
//! side, which makes hard-panned mixes tiring. Crossfeed feeds a low-passed copy of
//! each channel into the other ear and slightly lifts the direct highs to keep the
//! overall balance. First-order filters, so it is cheap and has no latency.

use serde::{Deserialize, Serialize};

/// User-facing strength = bs2b's low / middle / high presets. "Feed" is the level
/// difference between direct and crossed bass, so more dB = less crossfeed.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Crossfeed {
    #[default]
    Off,
    /// 650 Hz, 9.5 dB (Jan Meier): subtle, takes the edge off hard panning.
    Light,
    /// 700 Hz, 6 dB (Chu Moy).
    Normal,
    /// 700 Hz, 4.5 dB (bs2b default): closest to speakers.
    Strong,
}

impl Crossfeed {
    /// (cut frequency Hz, feed level dB), or `None` when off.
    pub fn settings(self) -> Option<(f64, f64)> {
        match self {
            Crossfeed::Off => None,
            Crossfeed::Light => Some((650.0, 9.5)),
            Crossfeed::Normal => Some((700.0, 6.0)),
            Crossfeed::Strong => Some((700.0, 4.5)),
        }
    }
}

/// Stereo crossfeed filter state. Process frame by frame (L, R).
#[derive(Clone, Copy, Debug)]
pub struct Bs2b {
    level: Crossfeed,
    a0_lo: f64,
    b1_lo: f64,
    a0_hi: f64,
    a1_hi: f64,
    b1_hi: f64,
    gain: f64,
    lo: [f64; 2],
    hi: [f64; 2],
    asis: [f64; 2],
}

impl Bs2b {
    pub fn new(level: Crossfeed, sample_rate: f64) -> Bs2b {
        let mut b = Bs2b {
            level,
            a0_lo: 0.0,
            b1_lo: 0.0,
            a0_hi: 1.0,
            a1_hi: 0.0,
            b1_hi: 0.0,
            gain: 1.0,
            lo: [0.0; 2],
            hi: [0.0; 2],
            asis: [0.0; 2],
        };
        b.design(sample_rate);
        b
    }

    pub fn level(&self) -> Crossfeed {
        self.level
    }

    /// Recompute coefficients (not RT-critical but allocation-free).
    pub fn design(&mut self, sample_rate: f64) {
        let Some((fcut, feed)) = self.level.settings() else {
            self.a0_lo = 0.0;
            self.b1_lo = 0.0;
            self.a0_hi = 1.0;
            self.a1_hi = 0.0;
            self.b1_hi = 0.0;
            self.gain = 1.0;
            return;
        };
        let gb_lo = feed * -5.0 / 6.0 - 3.0;
        let gb_hi = feed / 6.0 - 3.0;
        let g_lo = 10f64.powf(gb_lo / 20.0);
        let g_hi = 1.0 - 10f64.powf(gb_hi / 20.0);
        let fc_hi = fcut * 2f64.powf((gb_lo - 20.0 * g_hi.log10()) / 12.0);
        let tau = std::f64::consts::TAU;
        let x = (-tau * fcut / sample_rate).exp();
        self.b1_lo = x;
        self.a0_lo = g_lo * (1.0 - x);
        let x = (-tau * fc_hi / sample_rate).exp();
        self.b1_hi = x;
        self.a0_hi = 1.0 - g_hi * (1.0 - x);
        self.a1_hi = -x;
        self.gain = 1.0 / (1.0 - g_hi + g_lo);
    }

    /// Keep the filter memory of `other` (level change without a click).
    pub fn take_state(&mut self, other: &Bs2b) {
        self.lo = other.lo;
        self.hi = other.hi;
        self.asis = other.asis;
    }

    pub fn reset(&mut self) {
        self.lo = [0.0; 2];
        self.hi = [0.0; 2];
        self.asis = [0.0; 2];
    }

    #[inline]
    pub fn process(&mut self, l: f64, r: f64) -> (f64, f64) {
        for (c, x) in [l, r].into_iter().enumerate() {
            self.lo[c] = self.a0_lo * x + self.b1_lo * self.lo[c];
            self.hi[c] = self.a0_hi * x + self.a1_hi * self.asis[c] + self.b1_hi * self.hi[c];
            self.asis[c] = x;
        }
        (
            (self.hi[0] + self.lo[1]) * self.gain,
            (self.hi[1] + self.lo[0]) * self.gain,
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Steady-state RMS of each output for a sine on the left channel only.
    fn leak(level: Crossfeed, freq: f64) -> (f64, f64) {
        let sr = 48_000.0;
        let mut b = Bs2b::new(level, sr);
        let (mut el, mut er) = (0.0, 0.0);
        for i in 0..48_000 {
            let s = (std::f64::consts::TAU * freq * i as f64 / sr).sin();
            let (l, r) = b.process(s, 0.0);
            if i >= 24_000 {
                el += l * l;
                er += r * r;
            }
        }
        (el.sqrt(), er.sqrt())
    }

    #[test]
    fn off_is_transparent() {
        let mut b = Bs2b::new(Crossfeed::Off, 48_000.0);
        assert_eq!(b.process(0.5, -0.25), (0.5, -0.25));
    }

    #[test]
    fn bass_crosses_more_than_treble_and_strength_orders() {
        for level in [Crossfeed::Light, Crossfeed::Normal, Crossfeed::Strong] {
            let (l_low, r_low) = leak(level, 100.0);
            let (l_high, r_high) = leak(level, 8000.0);
            assert!(r_low / l_low > 4.0 * (r_high / l_high), "{level:?}");
        }
        let ratio = |lv| {
            let (l, r) = leak(lv, 100.0);
            r / l
        };
        assert!(ratio(Crossfeed::Light) < ratio(Crossfeed::Normal));
        assert!(ratio(Crossfeed::Normal) < ratio(Crossfeed::Strong));
    }

    #[test]
    fn mono_level_is_kept() {
        // Centered content keeps about the same level (bs2b: within ~2 dB, highs dip slightly).
        for freq in [100.0, 1000.0, 8000.0] {
            let sr = 48_000.0;
            let mut b = Bs2b::new(Crossfeed::Strong, sr);
            let (mut e_in, mut e_out) = (0.0, 0.0);
            for i in 0..48_000 {
                let s = (std::f64::consts::TAU * freq * i as f64 / sr).sin();
                let (l, _) = b.process(s, s);
                if i >= 24_000 {
                    e_in += s * s;
                    e_out += l * l;
                }
            }
            let db = 10.0 * (e_out / e_in).log10();
            assert!(db.abs() < 2.0, "{freq} Hz: {db:.2} dB");
        }
    }
}
