//! Live spectrum analyzer.
//!
//! The audio thread pushes a mono mix through a lock-free ring buffer
//! ([`SpectrumTap`], RT-safe, drops samples if the UI is not reading).
//! The UI thread runs the FFT ([`SpectrumAnalyzer`]) at display rate and gets
//! log-spaced bins in dBFS with peak-hold style decay.

use realfft::{RealFftPlanner, RealToComplex};
use std::f32::consts::PI;
use std::sync::Arc;

pub const FFT_SIZE: usize = 4096;
/// Floor of the reported range (dBFS).
pub const FLOOR_DB: f32 = -110.0;

pub struct SpectrumTap(rtrb::Producer<f32>);

pub struct SpectrumAnalyzer {
    rx: rtrb::Consumer<f32>,
    /// Circular window of the most recent FFT_SIZE samples.
    history: Vec<f32>,
    pos: usize,
    fft: Arc<dyn RealToComplex<f32>>,
    input: Vec<f32>,
    output: Vec<realfft::num_complex::Complex<f32>>,
    scratch: Vec<realfft::num_complex::Complex<f32>>,
    hann: Vec<f32>,
    mags_db: Vec<f32>,
    smoothed: Vec<f32>,
}

/// Create the tap (audio side) and analyzer (UI side).
pub fn spectrum_channel() -> (SpectrumTap, SpectrumAnalyzer) {
    let (tx, rx) = rtrb::RingBuffer::new(FFT_SIZE * 4);
    let fft = RealFftPlanner::<f32>::new().plan_fft_forward(FFT_SIZE);
    let hann = (0..FFT_SIZE)
        .map(|i| 0.5 - 0.5 * (2.0 * PI * i as f32 / FFT_SIZE as f32).cos())
        .collect();
    let analyzer = SpectrumAnalyzer {
        rx,
        history: vec![0.0; FFT_SIZE],
        pos: 0,
        input: fft.make_input_vec(),
        output: fft.make_output_vec(),
        scratch: fft.make_scratch_vec(),
        fft,
        hann,
        mags_db: vec![FLOOR_DB; FFT_SIZE / 2 + 1],
        smoothed: Vec::new(),
    };
    (SpectrumTap(tx), analyzer)
}

impl SpectrumTap {
    /// Push the mono average of an interleaved buffer. RT-safe; drops what does not fit.
    #[inline]
    pub fn push_interleaved(&mut self, buf: &[f32], channels: usize) {
        if channels == 0 {
            return;
        }
        let inv = 1.0 / channels as f32;
        let free = self.0.slots();
        for frame in buf.chunks_exact(channels).take(free) {
            let _ = self.0.push(frame.iter().sum::<f32>() * inv);
        }
    }
}

impl SpectrumAnalyzer {
    /// Fill `out` with `out.len()` log-spaced bins from 20 Hz to 20 kHz (dBFS).
    /// `decay_db` is how far each bin may fall per call (visual smoothing).
    pub fn analyze(&mut self, sample_rate: f64, out: &mut [f32], decay_db: f32) {
        while let Ok(s) = self.rx.pop() {
            self.history[self.pos] = s;
            self.pos = (self.pos + 1) % FFT_SIZE;
        }
        for i in 0..FFT_SIZE {
            self.input[i] = self.history[(self.pos + i) % FFT_SIZE] * self.hann[i];
        }
        if self
            .fft
            .process_with_scratch(&mut self.input, &mut self.output, &mut self.scratch)
            .is_err()
        {
            return;
        }
        // Normalize so a full-scale sine reads ~0 dBFS (Hann coherent gain = 0.5).
        let norm = 2.0 / (FFT_SIZE as f32 * 0.5);
        for (m, c) in self.mags_db.iter_mut().zip(&self.output) {
            *m = (20.0 * (c.norm() * norm).max(1e-9).log10()).max(FLOOR_DB);
        }

        if self.smoothed.len() != out.len() {
            self.smoothed = vec![FLOOR_DB; out.len()];
        }
        let n = out.len();
        let bin_hz = sample_rate as f32 / FFT_SIZE as f32;
        let hi = (20_000.0f32).min(sample_rate as f32 * 0.5);
        let (l, h) = (20f32.ln(), hi.ln());
        for (i, o) in out.iter_mut().enumerate() {
            let f0 = (l + (h - l) * i as f32 / n as f32).exp();
            let f1 = (l + (h - l) * (i + 1) as f32 / n as f32).exp();
            let b0 = (f0 / bin_hz).floor() as usize;
            let b1 = ((f1 / bin_hz).ceil() as usize).clamp(b0 + 1, self.mags_db.len());
            let v = self.mags_db[b0.min(self.mags_db.len() - 1)..b1]
                .iter()
                .copied()
                .fold(FLOOR_DB, f32::max);
            let s = &mut self.smoothed[i];
            *s = v.max(*s - decay_db);
            *o = *s;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sine_peaks_in_right_bin() {
        let (mut tap, mut an) = spectrum_channel();
        let sr = 48_000.0;
        let buf: Vec<f32> = (0..FFT_SIZE)
            .flat_map(|n| {
                let s = (2.0 * std::f64::consts::PI * 1000.0 * n as f64 / sr).sin() as f32;
                [s, s]
            })
            .collect();
        tap.push_interleaved(&buf, 2);
        let mut out = vec![0.0; 100];
        an.analyze(sr, &mut out, 100.0);
        let (imax, vmax) =
            out.iter()
                .enumerate()
                .fold((0, f32::MIN), |a, (i, &v)| if v > a.1 { (i, v) } else { a });
        // bin i covers 20*1000^(i/100) .. ; 1 kHz ~ i = 56
        assert!((55..=57).contains(&imax), "peak at {imax}");
        assert!(vmax > -3.0 && vmax < 1.0, "level {vmax}");
    }
}
