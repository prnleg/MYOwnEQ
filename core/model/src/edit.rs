//! Editing helpers shared by every app (Mac, Windows, Linux): converting between
//! parametric and graphic layouts without changing the sound, and placing a new
//! band. Pure functions; the app core keeps the per-profile stash.

use crate::profile::{EqLayer, GraphicPoint, GRAPHIC_10, GRAPHIC_31};
use myowneq_dsp::response::response_db;
use myowneq_dsp::{Band, FilterKind};
use serde::{Deserialize, Serialize};

/// Target layout of [`convert`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Layout {
    Parametric,
    Graphic10,
    Graphic31,
}

impl Layout {
    pub fn of(eq: &EqLayer) -> Layout {
        match eq {
            EqLayer::Parametric { .. } => Layout::Parametric,
            EqLayer::Graphic { points } if points.len() > 10 => Layout::Graphic31,
            EqLayer::Graphic { .. } => Layout::Graphic10,
        }
    }
}

/// Parametric bands kept while the user tries a graphic layout, so switching
/// back without touching the sliders restores them exactly.
#[derive(Clone, Debug, PartialEq)]
pub struct Stash {
    pub bands: Vec<Band>,
    /// The graphic layer as it was when stashed (any edit invalidates the stash).
    pub graphic: EqLayer,
}

/// Graphic sliders are limited to ±12 dB in 0.5 dB steps.
pub const GRAPHIC_RANGE_DB: f64 = 12.0;

/// Gain of an EQ layer alone (no preamp, correction or macros) at `freqs`.
pub fn layer_response(eq: &EqLayer, sample_rate: f64, freqs: &[f64]) -> Vec<f64> {
    response_db(&eq.to_bands(), 0.0, sample_rate, freqs)
}

/// Switch layout keeping the sound: the current curve is sampled at the graphic
/// frequencies; graphic sliders become peaking bands. Returns the new layer and
/// the stash to keep for this profile (None = forget it).
pub fn convert(
    eq: &EqLayer,
    to: Layout,
    sample_rate: f64,
    stash: Option<&Stash>,
) -> (EqLayer, Option<Stash>) {
    if Layout::of(eq) == to {
        return (eq.clone(), stash.cloned());
    }
    match to {
        Layout::Parametric => {
            let EqLayer::Graphic { points } = eq else {
                return (eq.clone(), None);
            };
            if let Some(s) = stash.filter(|s| &s.graphic == eq) {
                return (
                    EqLayer::Parametric {
                        bands: s.bands.clone(),
                    },
                    None,
                ); // untouched: exact
            }
            let q = if points.len() > 10 { 4.3 } else { 1.41 };
            let bands = points
                .iter()
                .filter(|p| p.gain_db != 0.0)
                .map(|p| Band::peak(p.freq, p.gain_db, q))
                .collect();
            (EqLayer::Parametric { bands }, None)
        }
        Layout::Graphic10 | Layout::Graphic31 => {
            let freqs: &[f64] = if to == Layout::Graphic10 {
                &GRAPHIC_10
            } else {
                &GRAPHIC_31
            };
            let gains = layer_response(eq, sample_rate, freqs);
            let new = EqLayer::Graphic {
                points: freqs
                    .iter()
                    .zip(gains)
                    .map(|(&freq, g)| GraphicPoint {
                        freq,
                        gain_db: ((g * 2.0).round() / 2.0)
                            .clamp(-GRAPHIC_RANGE_DB, GRAPHIC_RANGE_DB),
                    })
                    .collect(),
            };
            let stash = match (eq, stash) {
                (EqLayer::Parametric { bands }, _) => Some(Stash {
                    bands: bands.clone(),
                    graphic: new.clone(),
                }),
                // Graphic → other graphic, still untouched: keep the way back.
                (_, Some(s)) if &s.graphic == eq => Some(Stash {
                    bands: s.bands.clone(),
                    graphic: new.clone(),
                }),
                _ => None,
            };
            (new, stash)
        }
    }
}

/// A new peaking band (0 dB, Q 1) in the widest gap between existing bands.
/// None for graphic layouts or when the 64-band limit is reached.
pub fn suggest_band(eq: &EqLayer) -> Option<Band> {
    let EqLayer::Parametric { bands } = eq else {
        return None;
    };
    if bands.len() >= myowneq_dsp::MAX_BANDS {
        return None;
    }
    let mut fs = vec![20.0];
    let mut used: Vec<f64> = bands.iter().map(|b| b.freq).collect();
    used.sort_by(f64::total_cmp);
    fs.extend(used);
    fs.push(20_000.0);
    let (mut best, mut gap) = (1000.0, 0.0);
    for w in fs.windows(2) {
        let g = (w[1] / w[0]).ln();
        if g > gap {
            gap = g;
            best = (w[0] * w[1]).sqrt();
        }
    }
    Some(Band {
        kind: FilterKind::Peak,
        freq: best.round(),
        gain_db: 0.0,
        q: 1.0,
        enabled: true,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fps() -> EqLayer {
        EqLayer::Parametric {
            bands: vec![
                Band::low_shelf(150.0, -4.0, 0.707),
                Band::peak(2500.0, 4.0, 1.0),
                Band::peak(5000.0, 3.0, 1.2),
            ],
        }
    }

    #[test]
    fn graphic_keeps_the_sound_and_untouched_round_trip_is_exact() {
        let eq = fps();
        let (g, stash) = convert(&eq, Layout::Graphic31, 48_000.0, None);
        let EqLayer::Graphic { points } = &g else {
            panic!()
        };
        assert_eq!(points.len(), 31);
        let at = |f: f64| points.iter().find(|p| p.freq == f).unwrap().gain_db;
        // Boost at 2.5 kHz and the low shelf cut are still there.
        assert!(
            at(2500.0) > 3.0 && at(100.0) < -2.0,
            "shape kept: 2.5k {} 100 {}",
            at(2500.0),
            at(100.0)
        );
        // 31 → 10 → back to parametric without edits: exact original bands.
        let (g10, stash) = convert(&g, Layout::Graphic10, 48_000.0, stash.as_ref());
        let (back, stash) = convert(&g10, Layout::Parametric, 48_000.0, stash.as_ref());
        assert_eq!(back, eq);
        assert!(stash.is_none());
    }

    #[test]
    fn edited_graphic_becomes_peaks() {
        let (mut g, stash) = convert(&fps(), Layout::Graphic10, 48_000.0, None);
        if let EqLayer::Graphic { points } = &mut g {
            points.iter_mut().for_each(|p| p.gain_db = 0.0);
            points[5].gain_db = 3.0;
        }
        let (p, _) = convert(&g, Layout::Parametric, 48_000.0, stash.as_ref());
        let EqLayer::Parametric { bands } = p else {
            panic!()
        };
        assert_eq!(bands.len(), 1);
        assert_eq!((bands[0].freq, bands[0].gain_db), (1000.0, 3.0));
    }

    #[test]
    fn new_band_goes_in_widest_gap() {
        let eq = EqLayer::Parametric {
            bands: vec![Band::peak(100.0, 0.0, 1.0), Band::peak(200.0, 0.0, 1.0)],
        };
        let b = suggest_band(&eq).unwrap();
        assert!(b.freq > 1000.0 && b.freq < 3000.0, "{}", b.freq); // between 200 and 20k
        assert!(suggest_band(&EqLayer::graphic(&GRAPHIC_10)).is_none());
    }
}
