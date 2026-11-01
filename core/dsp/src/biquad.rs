//! Biquad filter design (RBJ Audio EQ Cookbook) and analytic magnitude response.

use serde::{Deserialize, Serialize};
use std::f64::consts::PI;

/// Filter shapes supported by the EQ. Matches the common Equalizer APO / AutoEQ set.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FilterKind {
    Peak,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
    BandPass,
}

impl FilterKind {
    /// Filters whose effect disappears at 0 dB gain (so they can be faded in/out smoothly).
    pub fn is_gain_based(self) -> bool {
        matches!(
            self,
            FilterKind::Peak | FilterKind::LowShelf | FilterKind::HighShelf
        )
    }
}

/// One EQ band as the user sees it.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Band {
    pub kind: FilterKind,
    /// Center / corner frequency in Hz.
    pub freq: f64,
    /// Gain in dB (ignored by pass/notch/band-pass filters).
    #[serde(default)]
    pub gain_db: f64,
    /// Quality factor. For shelves this is the RBJ "Q" (0.707 = slope 1).
    pub q: f64,
    #[serde(default = "default_true")]
    pub enabled: bool,
}

fn default_true() -> bool {
    true
}

impl Band {
    pub const fn peak(freq: f64, gain_db: f64, q: f64) -> Self {
        Band {
            kind: FilterKind::Peak,
            freq,
            gain_db,
            q,
            enabled: true,
        }
    }
    pub const fn low_shelf(freq: f64, gain_db: f64, q: f64) -> Self {
        Band {
            kind: FilterKind::LowShelf,
            freq,
            gain_db,
            q,
            enabled: true,
        }
    }
    pub const fn high_shelf(freq: f64, gain_db: f64, q: f64) -> Self {
        Band {
            kind: FilterKind::HighShelf,
            freq,
            gain_db,
            q,
            enabled: true,
        }
    }
    /// A neutral placeholder used to fill fixed-size arrays.
    pub const DISABLED: Band = Band {
        kind: FilterKind::Peak,
        freq: 1000.0,
        gain_db: 0.0,
        q: 0.707,
        enabled: false,
    };
}

/// Normalized biquad coefficients (a0 == 1).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Coeffs {
    pub b0: f64,
    pub b1: f64,
    pub b2: f64,
    pub a1: f64,
    pub a2: f64,
}

impl Default for Coeffs {
    fn default() -> Self {
        Coeffs::IDENTITY
    }
}

impl Coeffs {
    pub const IDENTITY: Coeffs = Coeffs {
        b0: 1.0,
        b1: 0.0,
        b2: 0.0,
        a1: 0.0,
        a2: 0.0,
    };

    /// Design coefficients for `band` at `sample_rate`. Disabled bands return identity.
    pub fn design(band: &Band, sample_rate: f64) -> Coeffs {
        if !band.enabled {
            return Coeffs::IDENTITY;
        }
        let freq = band.freq.clamp(1.0, sample_rate * 0.499);
        let q = band.q.max(0.01);
        let a = 10f64.powf(band.gain_db / 40.0);
        let w0 = 2.0 * PI * freq / sample_rate;
        let (sin, cos) = w0.sin_cos();
        let alpha = sin / (2.0 * q);

        let (b0, b1, b2, a0, a1, a2) = match band.kind {
            FilterKind::Peak => (
                1.0 + alpha * a,
                -2.0 * cos,
                1.0 - alpha * a,
                1.0 + alpha / a,
                -2.0 * cos,
                1.0 - alpha / a,
            ),
            FilterKind::LowShelf => {
                let s = 2.0 * a.sqrt() * alpha;
                (
                    a * ((a + 1.0) - (a - 1.0) * cos + s),
                    2.0 * a * ((a - 1.0) - (a + 1.0) * cos),
                    a * ((a + 1.0) - (a - 1.0) * cos - s),
                    (a + 1.0) + (a - 1.0) * cos + s,
                    -2.0 * ((a - 1.0) + (a + 1.0) * cos),
                    (a + 1.0) + (a - 1.0) * cos - s,
                )
            }
            FilterKind::HighShelf => {
                let s = 2.0 * a.sqrt() * alpha;
                (
                    a * ((a + 1.0) + (a - 1.0) * cos + s),
                    -2.0 * a * ((a - 1.0) + (a + 1.0) * cos),
                    a * ((a + 1.0) + (a - 1.0) * cos - s),
                    (a + 1.0) - (a - 1.0) * cos + s,
                    2.0 * ((a - 1.0) - (a + 1.0) * cos),
                    (a + 1.0) - (a - 1.0) * cos - s,
                )
            }
            FilterKind::LowPass => (
                (1.0 - cos) / 2.0,
                1.0 - cos,
                (1.0 - cos) / 2.0,
                1.0 + alpha,
                -2.0 * cos,
                1.0 - alpha,
            ),
            FilterKind::HighPass => (
                (1.0 + cos) / 2.0,
                -(1.0 + cos),
                (1.0 + cos) / 2.0,
                1.0 + alpha,
                -2.0 * cos,
                1.0 - alpha,
            ),
            FilterKind::Notch => (1.0, -2.0 * cos, 1.0, 1.0 + alpha, -2.0 * cos, 1.0 - alpha),
            FilterKind::BandPass => (alpha, 0.0, -alpha, 1.0 + alpha, -2.0 * cos, 1.0 - alpha),
        };

        Coeffs {
            b0: b0 / a0,
            b1: b1 / a0,
            b2: b2 / a0,
            a1: a1 / a0,
            a2: a2 / a0,
        }
    }

    /// Magnitude response in dB at `freq` Hz.
    pub fn magnitude_db(&self, freq: f64, sample_rate: f64) -> f64 {
        let w = 2.0 * PI * freq / sample_rate;
        let (s1, c1) = w.sin_cos();
        let (s2, c2) = (2.0 * w).sin_cos();
        // H(z) evaluated at z = e^{jw}; z^-1 = cos w - j sin w
        let nr = self.b0 + self.b1 * c1 + self.b2 * c2;
        let ni = -(self.b1 * s1 + self.b2 * s2);
        let dr = 1.0 + self.a1 * c1 + self.a2 * c2;
        let di = -(self.a1 * s1 + self.a2 * s2);
        let num = nr * nr + ni * ni;
        let den = dr * dr + di * di;
        10.0 * (num / den).max(1e-30).log10()
    }
}

/// Transposed Direct Form II state for one channel of one biquad.
#[derive(Clone, Copy, Debug, Default)]
pub struct State {
    pub z1: f64,
    pub z2: f64,
}

impl State {
    #[inline(always)]
    pub fn process(&mut self, c: &Coeffs, x: f64) -> f64 {
        let y = c.b0 * x + self.z1;
        self.z1 = c.b1 * x - c.a1 * y + self.z2;
        self.z2 = c.b2 * x - c.a2 * y;
        y
    }

    pub fn reset(&mut self) {
        *self = State::default();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    const SR: f64 = 48_000.0;

    fn close(a: f64, b: f64, tol: f64) -> bool {
        (a - b).abs() <= tol
    }

    #[test]
    fn peak_hits_gain_at_center() {
        for g in [-12.0, -3.0, 0.0, 6.0, 15.0] {
            let c = Coeffs::design(&Band::peak(1000.0, g, 1.41), SR);
            assert!(close(c.magnitude_db(1000.0, SR), g, 0.01), "gain {g}");
            assert!(close(c.magnitude_db(20.0, SR), 0.0, 0.1));
        }
    }

    #[test]
    fn shelves_reach_gain_at_extremes() {
        let ls = Coeffs::design(&Band::low_shelf(105.0, 6.0, 0.707), SR);
        assert!(close(ls.magnitude_db(10.0, SR), 6.0, 0.05));
        assert!(close(ls.magnitude_db(10_000.0, SR), 0.0, 0.05));
        // RBJ shelf is half gain at corner frequency
        assert!(close(ls.magnitude_db(105.0, SR), 3.0, 0.05));

        let hs = Coeffs::design(&Band::high_shelf(8000.0, -4.0, 0.707), SR);
        assert!(close(hs.magnitude_db(23_000.0, SR), -4.0, 0.1));
        assert!(close(hs.magnitude_db(50.0, SR), 0.0, 0.05));
    }

    #[test]
    fn butterworth_pass_filters_are_minus_3db_at_corner() {
        for kind in [FilterKind::LowPass, FilterKind::HighPass] {
            let b = Band {
                kind,
                freq: 2000.0,
                gain_db: 0.0,
                q: std::f64::consts::FRAC_1_SQRT_2,
                enabled: true,
            };
            let c = Coeffs::design(&b, SR);
            assert!(close(c.magnitude_db(2000.0, SR), -3.0103, 0.01), "{kind:?}");
        }
    }

    #[test]
    fn disabled_is_identity() {
        let mut b = Band::peak(1000.0, 12.0, 1.0);
        b.enabled = false;
        assert_eq!(Coeffs::design(&b, SR), Coeffs::IDENTITY);
    }

    #[test]
    fn time_domain_matches_analytic_response() {
        // Feed a sine through the filter and compare steady-state amplitude.
        let c = Coeffs::design(&Band::peak(1000.0, 9.0, 2.0), SR);
        let mut st = State::default();
        let mut peak: f64 = 0.0;
        for n in 0..48_000 {
            let x = (2.0 * PI * 1000.0 * n as f64 / SR).sin();
            let y = st.process(&c, x);
            if n > 24_000 {
                peak = peak.max(y.abs());
            }
        }
        assert!(close(20.0 * peak.log10(), 9.0, 0.02));
    }
}
