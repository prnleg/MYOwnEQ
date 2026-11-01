//! Combined frequency-response helpers used by the UI (curve drawing) and
//! by auto-preamp (clipping protection).

use crate::biquad::{Band, Coeffs};

/// Log-spaced frequency grid from `lo` to `hi` Hz with `n` points.
pub fn log_grid(lo: f64, hi: f64, n: usize) -> Vec<f64> {
    assert!(n >= 2 && lo > 0.0 && hi > lo);
    let (l, h) = (lo.ln(), hi.ln());
    (0..n)
        .map(|i| (l + (h - l) * i as f64 / (n - 1) as f64).exp())
        .collect()
}

/// Combined response (dB) of `bands` plus `preamp_db` at each frequency.
pub fn response_db(bands: &[Band], preamp_db: f64, sample_rate: f64, freqs: &[f64]) -> Vec<f64> {
    let coeffs: Vec<Coeffs> = bands
        .iter()
        .map(|b| Coeffs::design(b, sample_rate))
        .collect();
    freqs
        .iter()
        .map(|&f| {
            preamp_db
                + coeffs
                    .iter()
                    .map(|c| c.magnitude_db(f, sample_rate))
                    .sum::<f64>()
        })
        .collect()
}

/// Response of a single band (for highlighting the band being dragged).
pub fn band_response_db(band: &Band, sample_rate: f64, freqs: &[f64]) -> Vec<f64> {
    let c = Coeffs::design(band, sample_rate);
    freqs
        .iter()
        .map(|&f| c.magnitude_db(f, sample_rate))
        .collect()
}

/// Highest boost (dB) of the combined curve over the audible range (20 Hz – 20 kHz).
pub fn max_boost_db(bands: &[Band], sample_rate: f64) -> f64 {
    let hi = 20_000f64.min(sample_rate * 0.49);
    let grid = log_grid(20.0, hi, 512);
    response_db(bands, 0.0, sample_rate, &grid)
        .into_iter()
        .fold(f64::MIN, f64::max)
}

/// Preamp that keeps the loudest frequency at or below 0 dB.
pub fn auto_preamp_db(bands: &[Band], sample_rate: f64) -> f64 {
    -max_boost_db(bands, sample_rate).max(0.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn grid_endpoints() {
        let g = log_grid(20.0, 20_000.0, 10);
        assert!((g[0] - 20.0).abs() < 1e-9 && (g[9] - 20_000.0).abs() < 1e-6);
    }

    #[test]
    fn auto_preamp_cancels_largest_boost() {
        let bands = [Band::peak(100.0, 6.0, 1.0), Band::peak(3000.0, 3.0, 1.0)];
        let p = auto_preamp_db(&bands, 48_000.0);
        assert!((p + 6.0).abs() < 0.1, "{p}");
        let cuts = [Band::peak(100.0, -6.0, 1.0)];
        assert_eq!(auto_preamp_db(&cuts, 48_000.0), 0.0);
    }

    #[test]
    fn bands_sum_in_db() {
        let a = Band::peak(1000.0, 3.0, 1.0);
        let r = response_db(&[a, a], -1.0, 48_000.0, &[1000.0]);
        assert!((r[0] - 5.0).abs() < 0.01);
    }
}
