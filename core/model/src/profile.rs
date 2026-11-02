//! EQ profiles and how they render into DSP parameters.

use crate::Id;
use myowneq_dsp::{loudness::loudness_bands, response::auto_preamp_db, Band, Crossfeed, EqParams};
use serde::{Deserialize, Serialize};

/// Preamp mode. `Auto` keeps the loudest boost at 0 dB to avoid clipping.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, Default)]
#[serde(tag = "mode", content = "db", rename_all = "snake_case")]
pub enum Preamp {
    #[default]
    Auto,
    Manual(f64),
}

/// One point of a graphic EQ (fixed frequency slider).
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct GraphicPoint {
    pub freq: f64,
    pub gain_db: f64,
}

/// The user-editable EQ layer.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum EqLayer {
    Parametric { bands: Vec<Band> },
    Graphic { points: Vec<GraphicPoint> },
}

impl Default for EqLayer {
    fn default() -> Self {
        EqLayer::Parametric { bands: Vec::new() }
    }
}

/// Easy-mode knobs: (name, center frequency). The apps draw a dot per knob here.
pub const MACRO_SPOTS: [(&str, f64); 4] = [
    ("Bass", 105.0),
    ("Mids", 1000.0),
    ("Clarity", 3000.0),
    ("Treble", 8000.0),
];

pub const GRAPHIC_10: [f64; 10] = [
    32.0, 64.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0,
];

pub const GRAPHIC_31: [f64; 31] = [
    20.0, 25.0, 31.5, 40.0, 50.0, 63.0, 80.0, 100.0, 125.0, 160.0, 200.0, 250.0, 315.0, 400.0,
    500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0, 2000.0, 2500.0, 3150.0, 4000.0, 5000.0, 6300.0,
    8000.0, 10000.0, 12500.0, 16000.0, 20000.0,
];

impl EqLayer {
    pub fn graphic(freqs: &[f64]) -> Self {
        EqLayer::Graphic {
            points: freqs
                .iter()
                .map(|&freq| GraphicPoint { freq, gain_db: 0.0 })
                .collect(),
        }
    }

    /// Convert to biquads. Graphic points become peaking filters whose Q
    /// matches the spacing to their neighbours (octave spacing -> Q 1.41).
    pub fn to_bands(&self) -> Vec<Band> {
        match self {
            EqLayer::Parametric { bands } => bands.clone(),
            EqLayer::Graphic { points } => {
                let n = points.len();
                points
                    .iter()
                    .enumerate()
                    .filter(|(_, p)| p.gain_db != 0.0)
                    .map(|(i, p)| {
                        let ratio = if n < 2 {
                            2.0
                        } else if i == 0 {
                            points[1].freq / p.freq
                        } else if i == n - 1 {
                            p.freq / points[n - 2].freq
                        } else {
                            (points[i + 1].freq / points[i - 1].freq).sqrt()
                        };
                        Band::peak(p.freq, p.gain_db, q_for_ratio(ratio))
                    })
                    .collect()
            }
        }
    }
}

/// Q of a peaking filter whose bandwidth equals the frequency `ratio` between bands.
pub fn q_for_ratio(ratio: f64) -> f64 {
    let ratio = ratio.max(1.01);
    ratio.sqrt() / (ratio - 1.0)
}

/// Easy-mode macro knobs, in dB (typ. -12..=12). Rendered on top of everything.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, Default)]
pub struct Macros {
    #[serde(default)]
    pub bass: f64,
    #[serde(default)]
    pub mids: f64,
    #[serde(default)]
    pub clarity: f64,
    #[serde(default)]
    pub treble: f64,
    /// Whole-spectrum slope in dB (20 Hz → 20 kHz): + brighter, − warmer.
    /// Pivots around 1 kHz, handy for "AutoEQ, but a bit warmer".
    #[serde(default)]
    pub tilt: f64,
}

/// Tilt pivot and shelf slope (very gentle shelves = a smooth slope).
const TILT_PIVOT_HZ: f64 = 1000.0;
const TILT_Q: f64 = 0.4;

impl Macros {
    pub fn to_bands(&self) -> Vec<Band> {
        [
            Band::low_shelf(MACRO_SPOTS[0].1, self.bass, 0.707),
            Band::peak(MACRO_SPOTS[1].1, self.mids, 0.7),
            Band::peak(MACRO_SPOTS[2].1, self.clarity, 1.0),
            Band::high_shelf(MACRO_SPOTS[3].1, self.treble, 0.707),
            Band::low_shelf(TILT_PIVOT_HZ, -self.tilt / 2.0, TILT_Q),
            Band::high_shelf(TILT_PIVOT_HZ, self.tilt / 2.0, TILT_Q),
        ]
        .into_iter()
        .filter(|b| b.gain_db != 0.0)
        .collect()
    }
}

/// Where a correction base came from (for display and refresh).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CorrectionSource {
    /// e.g. "AutoEQ" or "Imported"
    pub provider: String,
    /// e.g. "Sennheiser Momentum 4 Wireless (oratory1990)"
    pub name: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Profile {
    pub id: Id,
    pub name: String,
    /// Headphone correction (AutoEQ), applied before the user layer.
    #[serde(default)]
    pub correction: Vec<Band>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub correction_source: Option<CorrectionSource>,
    /// User EQ layer (Pro mode).
    #[serde(default)]
    pub eq: EqLayer,
    /// Easy-mode knobs.
    #[serde(default)]
    pub macros: Macros,
    #[serde(default)]
    pub preamp: Preamp,
    #[serde(default)]
    pub balance: f64,
    #[serde(default = "default_true")]
    pub limiter: bool,
    /// Loudness compensation strength 0..1 (0 = off).
    #[serde(default)]
    pub loudness: f64,
    /// Headphone crossfeed (needs our DSP: Mac, Windows engine).
    #[serde(default)]
    pub crossfeed: Crossfeed,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tags: Vec<String>,
}

fn default_true() -> bool {
    true
}

/// Runtime inputs needed to render a profile.
#[derive(Clone, Copy, Debug)]
pub struct RenderContext {
    pub sample_rate: f64,
    /// System output volume 0..1 (for loudness compensation).
    pub volume: f64,
    /// The DSP has a limiter after the EQ (Mac, Windows engine). Then the automatic
    /// preamp ignores loudness compensation, so turning the volume down doesn't
    /// also pull everything down by the bass boost; the limiter catches peaks.
    /// False for Equalizer APO's own filters (no limiter): stay clip-safe.
    pub limiter_headroom: bool,
}

impl Default for RenderContext {
    fn default() -> Self {
        RenderContext {
            sample_rate: 48_000.0,
            volume: 1.0,
            limiter_headroom: false,
        }
    }
}

impl Profile {
    pub fn new(name: impl Into<String>) -> Self {
        Profile {
            id: crate::new_id(),
            name: name.into(),
            correction: Vec::new(),
            correction_source: None,
            eq: EqLayer::default(),
            macros: Macros::default(),
            preamp: Preamp::Auto,
            balance: 0.0,
            limiter: true,
            loudness: 0.0,
            crossfeed: Crossfeed::Off,
            tags: Vec::new(),
        }
    }

    /// All filters in processing order: correction, user layer, macros.
    /// Loudness is excluded because it depends on runtime volume.
    pub fn static_bands(&self) -> Vec<Band> {
        let mut v = self.correction.clone();
        v.extend(self.eq.to_bands());
        v.extend(self.macros.to_bands());
        v
    }

    /// Full chain including loudness for the given context.
    pub fn bands(&self, ctx: &RenderContext) -> Vec<Band> {
        let mut v = self.static_bands();
        if self.loudness > 0.0 {
            v.extend(
                loudness_bands(self.loudness, ctx.volume)
                    .into_iter()
                    .filter(|b| b.gain_db != 0.0),
            );
        }
        v
    }

    pub fn preamp_db(&self, bands: &[Band], sample_rate: f64) -> f64 {
        match self.preamp {
            Preamp::Auto => auto_preamp_db(bands, sample_rate),
            Preamp::Manual(db) => db,
        }
    }

    /// Render into a DSP parameter snapshot.
    pub fn render(&self, ctx: &RenderContext) -> EqParams {
        let bands = self.bands(ctx);
        let mut p = EqParams::from_bands(&bands);
        p.preamp_db = if ctx.limiter_headroom && self.limiter && self.loudness > 0.0 {
            self.preamp_db(&self.static_bands(), ctx.sample_rate)
        } else {
            self.preamp_db(&bands, ctx.sample_rate)
        };
        p.balance = self.balance.clamp(-1.0, 1.0);
        p.limiter = self.limiter;
        p.crossfeed = self.crossfeed;
        p
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use myowneq_dsp::response::response_db;

    #[test]
    fn graphic_octave_q() {
        assert!((q_for_ratio(2.0) - std::f64::consts::SQRT_2).abs() < 1e-3);
        let mut layer = EqLayer::graphic(&GRAPHIC_10);
        if let EqLayer::Graphic { points } = &mut layer {
            points[5].gain_db = 4.0;
        }
        let bands = layer.to_bands();
        assert_eq!(bands.len(), 1);
        assert!((bands[0].q - std::f64::consts::SQRT_2).abs() < 1e-3);
    }

    #[test]
    fn render_auto_preamp_and_macros() {
        let mut p = Profile::new("Test");
        p.macros.bass = 6.0;
        let params = p.render(&RenderContext::default());
        assert_eq!(params.band_count, 1);
        assert!((params.preamp_db + 6.0).abs() < 0.1);
        let r = response_db(params.active_bands(), params.preamp_db, 48_000.0, &[30.0]);
        assert!(r[0] <= 0.01);
    }

    #[test]
    fn loudness_only_when_quiet() {
        let mut p = Profile::new("L");
        p.loudness = 1.0;
        assert_eq!(
            p.bands(&RenderContext {
                volume: 1.0,
                ..Default::default()
            })
            .len(),
            0
        );
        assert_eq!(
            p.bands(&RenderContext {
                volume: 0.1,
                ..Default::default()
            })
            .len(),
            2
        );
    }

    #[test]
    fn loudness_not_cancelled_by_auto_preamp_when_limited() {
        let mut p = Profile::new("L");
        p.loudness = 1.0;
        let quiet = |limiter_headroom| {
            p.render(&RenderContext {
                volume: 0.3,
                limiter_headroom,
                ..Default::default()
            })
        };
        let bass_vs_mid = |params: &EqParams| {
            let r = response_db(
                params.active_bands(),
                params.preamp_db,
                48_000.0,
                &[40.0, 1000.0],
            );
            (r[0], r[1])
        };
        // With a limiter: mids stay at 0 dB, bass goes up.
        let (bass, mid) = bass_vs_mid(&quiet(true));
        assert!(mid.abs() < 0.5 && bass > 5.0, "bass {bass} mid {mid}");
        // Without (Equalizer APO filters): clip-safe, nothing above 0 dB.
        let (bass, mid) = bass_vs_mid(&quiet(false));
        assert!(bass <= 0.01 && mid < -5.0, "bass {bass} mid {mid}");
    }

    #[test]
    fn tilt_slopes_around_1k() {
        let mut p = Profile::new("T");
        p.macros.tilt = 6.0;
        p.preamp = Preamp::Manual(0.0);
        let params = p.render(&RenderContext::default());
        let r = response_db(
            params.active_bands(),
            0.0,
            48_000.0,
            &[30.0, 1000.0, 16_000.0],
        );
        assert!(r[0] < -2.0 && r[1].abs() < 0.5 && r[2] > 2.0, "{r:?}");
    }

    #[test]
    fn json_roundtrip() {
        let mut p = Profile::new("RT");
        p.preamp = Preamp::Manual(-3.0);
        p.eq = EqLayer::graphic(&GRAPHIC_31);
        let s = serde_json::to_string(&p).unwrap();
        assert_eq!(serde_json::from_str::<Profile>(&s).unwrap(), p);
    }
}
