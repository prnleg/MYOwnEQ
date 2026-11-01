//! eqMac preset import.
//!
//! eqMac stores "Advanced" presets as a JSON array (in a `Data` value under
//! `advancedEqualizerPresets` in `~/Library/Preferences/com.bitgapp.eqmac.plist`):
//! `{"id","name","isDefault","gains":{"global":dB,"bands":[10 x dB]}}`.
//! Bands are a fixed 10-band graphic EQ at octave centers; `global` is the preamp.

use crate::profile::{EqLayer, GraphicPoint, Preamp, Profile, GRAPHIC_10};
use serde::Deserialize;
use std::path::Path;

#[derive(Debug, thiserror::Error)]
pub enum EqMacError {
    #[error("plist: {0}")]
    Plist(#[from] plist::Error),
    #[error("json: {0}")]
    Json(#[from] serde_json::Error),
    #[error("no advanced presets found in eqMac preferences")]
    NotFound,
}

#[derive(Deserialize)]
struct Preset {
    name: String,
    #[serde(rename = "isDefault", default)]
    is_default: bool,
    gains: Gains,
}

#[derive(Deserialize)]
struct Gains {
    #[serde(default)]
    global: f64,
    bands: Vec<f64>,
}

/// Default location of eqMac preferences.
pub fn default_plist_path() -> Option<std::path::PathBuf> {
    std::env::var_os("HOME")
        .map(|h| Path::new(&h).join("Library/Preferences/com.bitgapp.eqmac.plist"))
}

/// Parse the JSON array. eqMac's own built-in presets (`isDefault`) are skipped.
pub fn parse_advanced_json(json: &str) -> Result<Vec<Profile>, EqMacError> {
    let presets: Vec<Preset> = serde_json::from_str(json)?;
    Ok(presets
        .into_iter()
        .filter(|p| !p.is_default)
        .map(|p| {
            let mut prof = Profile::new(p.name);
            prof.eq = EqLayer::Graphic {
                points: GRAPHIC_10
                    .iter()
                    .zip(p.gains.bands.iter().chain(std::iter::repeat(&0.0)))
                    .map(|(&freq, &gain_db)| GraphicPoint { freq, gain_db })
                    .collect(),
            };
            // eqMac "global" is a manual gain; 0 means the user never set one -> Auto.
            prof.preamp = if p.gains.global == 0.0 {
                Preamp::Auto
            } else {
                Preamp::Manual(p.gains.global)
            };
            prof.tags = vec!["imported:eqmac".into()];
            prof
        })
        .collect())
}

/// Read presets straight from eqMac's preferences plist.
pub fn import_plist(path: &Path) -> Result<Vec<Profile>, EqMacError> {
    let root = plist::Value::from_file(path)?;
    let v = root
        .as_dictionary()
        .and_then(|d| d.get("advancedEqualizerPresets"))
        .ok_or(EqMacError::NotFound)?;
    let json = match v {
        plist::Value::Data(bytes) => String::from_utf8_lossy(bytes).into_owned(),
        plist::Value::String(s) => s.clone(),
        _ => return Err(EqMacError::NotFound),
    };
    parse_advanced_json(&json)
}

#[cfg(test)]
mod tests {
    use super::*;

    const FIXTURE: &str = include_str!("../../tests/fixtures/eqmac_advanced.json");

    #[test]
    fn parses_real_presets() {
        let ps = parse_advanced_json(FIXTURE).unwrap();
        assert_eq!(ps.len(), 3);
        assert_eq!(ps[0].name, "Sennheiser Momentum");
        assert_eq!(ps[0].preamp, Preamp::Manual(-6.4));
        assert_eq!(ps[1].preamp, Preamp::Auto);
        let EqLayer::Graphic { points } = &ps[0].eq else {
            panic!()
        };
        assert_eq!(points.len(), 10);
        assert_eq!(
            points[9],
            GraphicPoint {
                freq: 16000.0,
                gain_db: 6.2
            }
        );
    }

    #[test]
    fn plist_roundtrip() {
        let mut dict = plist::Dictionary::new();
        dict.insert(
            "advancedEqualizerPresets".into(),
            plist::Value::Data(FIXTURE.as_bytes().to_vec()),
        );
        let path = std::env::temp_dir().join(format!("eqmac-{}.plist", crate::new_id()));
        plist::Value::Dictionary(dict)
            .to_file_binary(&path)
            .unwrap();
        assert_eq!(import_plist(&path).unwrap().len(), 3);
        std::fs::remove_file(path).ok();
    }
}
