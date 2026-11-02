//! The file the app writes and the plugin reads (`engine.json`). One schema for
//! both sides so they can't drift.

use myowneq_dsp::{Band, Crossfeed, EqParams};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// Bump when the format changes incompatibly; the plugin ignores newer files.
pub const SCHEMA: u32 = 1;

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct EngineFile {
    pub schema: u32,
    /// Keyed by output id as used in Equalizer APO's `Device:` lines ("{guid}").
    pub devices: BTreeMap<String, DeviceParams>,
}

/// Everything the DSP needs for one output (boost already folded into the preamp).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct DeviceParams {
    pub bands: Vec<Band>,
    pub preamp_db: f64,
    #[serde(default)]
    pub balance: f64,
    #[serde(default = "yes")]
    pub limiter: bool,
    #[serde(default)]
    pub bypass: bool,
    #[serde(default)]
    pub crossfeed: Crossfeed,
}

fn yes() -> bool {
    true
}

impl DeviceParams {
    pub fn from_params(p: &EqParams) -> Self {
        DeviceParams {
            bands: p.active_bands().to_vec(),
            preamp_db: p.preamp_db,
            balance: p.balance,
            limiter: p.limiter,
            bypass: p.bypass,
            crossfeed: p.crossfeed,
        }
    }

    /// Clamped to what the DSP accepts (the file is input from another process).
    pub fn to_params(&self) -> EqParams {
        let mut p = EqParams::from_bands(&self.bands);
        p.preamp_db = if self.preamp_db.is_finite() {
            self.preamp_db.clamp(-48.0, 24.0)
        } else {
            0.0
        };
        p.balance = if self.balance.is_finite() {
            self.balance.clamp(-1.0, 1.0)
        } else {
            0.0
        };
        p.limiter = self.limiter || p.preamp_db > 0.0; // never boost without the limiter
        p.bypass = self.bypass;
        p.crossfeed = self.crossfeed;
        p
    }
}

impl EngineFile {
    pub fn parse(text: &str) -> Option<EngineFile> {
        let f: EngineFile = serde_json::from_str(text).ok()?;
        (f.schema <= SCHEMA).then_some(f)
    }

    pub fn to_json(&self) -> String {
        serde_json::to_string(self).unwrap_or_default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_and_safety_clamps() {
        let mut p = EqParams::from_bands(&[Band::peak(1000.0, 3.0, 1.0)]);
        p.preamp_db = 6.0;
        p.limiter = false;
        let mut f = EngineFile {
            schema: SCHEMA,
            ..Default::default()
        };
        f.devices
            .insert("{abc}".into(), DeviceParams::from_params(&p));
        let back = EngineFile::parse(&f.to_json()).unwrap();
        let q = back.devices["{abc}"].to_params();
        assert_eq!(q.band_count, 1);
        assert!(q.limiter, "boost forces the limiter on");
        let bad = r#"{"schema":1,"devices":{"x":{"bands":[],"preamp_db":1e9}}}"#;
        assert_eq!(
            EngineFile::parse(bad).unwrap().devices["x"]
                .to_params()
                .preamp_db,
            24.0
        );
        assert!(EngineFile::parse(r#"{"schema":99,"devices":{}}"#).is_none());
        assert!(EngineFile::parse("not json").is_none());
    }
}
