//! Equalizer APO config parsing and generation.
//!
//! Supported on import: `Preamp`, `Filter` (PK/PEQ, LS/LSC, HS/HSC, LP/LPQ,
//! HP/HPQ, NO, BP with Fc / Gain / Q / BW Oct), `GraphicEQ`. Other commands
//! are reported as warnings and skipped.

use super::fmt_num;
use crate::profile::{CorrectionSource, EqLayer, GraphicPoint, Preamp, Profile};
use myowneq_dsp::{Band, FilterKind};

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ApoConfig {
    pub preamp_db: Option<f64>,
    pub bands: Vec<Band>,
    pub graphic: Option<Vec<GraphicPoint>>,
    pub warnings: Vec<String>,
}

impl ApoConfig {
    /// As a user-editable profile (Equalizer APO / Peace import).
    pub fn into_profile(self, name: impl Into<String>) -> Profile {
        let mut p = Profile::new(name);
        p.preamp = self.preamp_db.map_or(Preamp::Auto, Preamp::Manual);
        p.eq = match (self.bands.is_empty(), self.graphic) {
            (true, Some(points)) => EqLayer::Graphic { points },
            (_, Some(points)) => {
                // Both present: fold graphic into parametric bands.
                let mut bands = self.bands;
                bands.extend(EqLayer::Graphic { points }.to_bands());
                EqLayer::Parametric { bands }
            }
            (_, None) => EqLayer::Parametric { bands: self.bands },
        };
        p
    }

    /// As a headphone correction (AutoEQ `ParametricEQ.txt`). Preamp is left
    /// on Auto because macros / user bands may add boost on top.
    pub fn into_correction(
        self,
        profile_name: impl Into<String>,
        source: CorrectionSource,
    ) -> Profile {
        let mut p = Profile::new(profile_name);
        p.correction = self.bands;
        if let Some(points) = self.graphic {
            p.correction.extend(EqLayer::Graphic { points }.to_bands());
        }
        p.correction_source = Some(source);
        p
    }
}

pub fn parse(text: &str) -> ApoConfig {
    let mut cfg = ApoConfig::default();
    for (lineno, raw) in text.lines().enumerate() {
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let Some((cmd, rest)) = line.split_once(':') else {
            cfg.warnings
                .push(format!("line {}: not a command: {line}", lineno + 1));
            continue;
        };
        let cmd = cmd.trim().to_ascii_lowercase();
        let rest = rest.trim();
        let res = if cmd == "preamp" {
            parse_preamp(rest).map(|db| cfg.preamp_db = Some(cfg.preamp_db.unwrap_or(0.0) + db))
        } else if cmd.starts_with("filter") {
            parse_filter(rest).map(|b| cfg.bands.extend(b))
        } else if cmd == "graphiceq" {
            parse_graphic(rest).map(|g| cfg.graphic = Some(g))
        } else {
            Err(format!("unsupported command '{cmd}' ignored"))
        };
        if let Err(e) = res {
            cfg.warnings.push(format!("line {}: {e}", lineno + 1));
        }
    }
    cfg
}

fn num(tok: Option<&str>) -> Result<f64, String> {
    let t = tok.ok_or("missing number")?;
    t.replace(',', ".")
        .parse::<f64>()
        .map_err(|_| format!("bad number '{t}'"))
}

fn parse_preamp(rest: &str) -> Result<f64, String> {
    num(rest.split_whitespace().next())
}

/// `ON PK Fc 100 Hz Gain 3 dB Q 1.0`. Returns `None` for unsupported-but-harmless filters.
fn parse_filter(rest: &str) -> Result<Option<Band>, String> {
    let toks: Vec<&str> = rest.split_whitespace().collect();
    let mut it = toks.iter().copied().peekable();
    let enabled = match it.next().map(|s| s.to_ascii_uppercase()) {
        Some(s) if s == "ON" => true,
        Some(s) if s == "OFF" => false,
        other => return Err(format!("expected ON/OFF, got {other:?}")),
    };
    let ty = it.next().ok_or("missing filter type")?.to_ascii_uppercase();
    let (kind, default_q) = match ty.as_str() {
        "PK" | "PEQ" | "MODAL" => (FilterKind::Peak, 1.41),
        "LS" | "LSC" => (FilterKind::LowShelf, 0.707),
        "HS" | "HSC" => (FilterKind::HighShelf, 0.707),
        "LP" | "LPQ" => (FilterKind::LowPass, std::f64::consts::FRAC_1_SQRT_2),
        "HP" | "HPQ" => (FilterKind::HighPass, std::f64::consts::FRAC_1_SQRT_2),
        "NO" => (FilterKind::Notch, 30.0),
        "BP" => (FilterKind::BandPass, 1.0),
        "NONE" => return Ok(None),
        other => return Err(format!("unsupported filter type '{other}'")),
    };
    // Optional slope token after type, e.g. "LSC 12dB": not supported, accept and ignore.
    if it
        .peek()
        .is_some_and(|t| t.to_ascii_lowercase().ends_with("db"))
    {
        it.next();
    }
    let (mut freq, mut gain, mut q) = (None, 0.0, None);
    while let Some(key) = it.next() {
        match key.to_ascii_lowercase().as_str() {
            "fc" => {
                freq = Some(num(it.next())?);
                it.next_if(|t| t.eq_ignore_ascii_case("hz"));
            }
            "gain" => {
                gain = num(it.next())?;
                it.next_if(|t| t.eq_ignore_ascii_case("db"));
            }
            "q" => q = Some(num(it.next())?),
            "bw" => {
                it.next_if(|t| t.eq_ignore_ascii_case("oct"));
                let oct = num(it.next())?;
                q = Some(crate::profile::q_for_ratio(2f64.powf(oct)));
            }
            other => return Err(format!("unexpected token '{other}'")),
        }
    }
    let freq = freq.ok_or("missing Fc")?;
    Ok(Some(Band {
        kind,
        freq,
        gain_db: gain,
        q: q.unwrap_or(default_q),
        enabled,
    }))
}

fn parse_graphic(rest: &str) -> Result<Vec<GraphicPoint>, String> {
    rest.split(';')
        .filter(|s| !s.trim().is_empty())
        .map(|pair| {
            let mut p = pair.split_whitespace();
            Ok(GraphicPoint {
                freq: num(p.next())?,
                gain_db: num(p.next())?,
            })
        })
        .collect()
}

/// One `Device:` section of a generated config.
pub struct Section<'a> {
    /// Value for the `Device:` line (`None` = applies to all devices).
    pub device: Option<&'a str>,
    pub preamp_db: f64,
    pub bands: &'a [Band],
}

/// Generate an Equalizer APO include file.
pub fn write(sections: &[Section]) -> String {
    let mut out = String::from("# Generated by MyOwnEQ. Do not edit: changes are overwritten.\n");
    for s in sections {
        out.push('\n');
        out.push_str(&format!("Device: {}\n", s.device.unwrap_or("all")));
        out.push_str(&format!("Preamp: {} dB\n", fmt_num(s.preamp_db)));
        for (i, b) in s.bands.iter().enumerate() {
            out.push_str(&format!("Filter {}: {}\n", i + 1, filter_line(b)));
        }
    }
    out
}

fn filter_line(b: &Band) -> String {
    let state = if b.enabled { "ON" } else { "OFF" };
    let (f, g, q) = (fmt_num(b.freq), fmt_num(b.gain_db), fmt_num(b.q));
    match b.kind {
        FilterKind::Peak => format!("{state} PK Fc {f} Hz Gain {g} dB Q {q}"),
        FilterKind::LowShelf => format!("{state} LSC Fc {f} Hz Gain {g} dB Q {q}"),
        FilterKind::HighShelf => format!("{state} HSC Fc {f} Hz Gain {g} dB Q {q}"),
        FilterKind::LowPass => format!("{state} LPQ Fc {f} Hz Q {q}"),
        FilterKind::HighPass => format!("{state} HPQ Fc {f} Hz Q {q}"),
        FilterKind::Notch => format!("{state} NO Fc {f} Hz Q {q}"),
        FilterKind::BandPass => format!("{state} BP Fc {f} Hz Q {q}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const AUTOEQ: &str = include_str!("../../tests/fixtures/autoeq_parametric.txt");
    const PEACE: &str = include_str!("../../tests/fixtures/apo_peace.txt");

    #[test]
    fn parses_autoeq() {
        let c = parse(AUTOEQ);
        assert!(c.warnings.is_empty(), "{:?}", c.warnings);
        assert_eq!(c.preamp_db, Some(-6.4));
        assert_eq!(c.bands.len(), 6);
        assert_eq!(c.bands[0], Band::low_shelf(105.0, 5.6, 0.70));
        assert_eq!(c.bands[4].kind, FilterKind::HighShelf);
        assert!(!c.bands[5].enabled);
    }

    #[test]
    fn parses_peace_with_warnings() {
        let c = parse(PEACE);
        assert_eq!(c.preamp_db, Some(-3.0));
        assert_eq!(c.bands.len(), 4);
        assert_eq!(c.bands[1].kind, FilterKind::HighPass);
        assert_eq!(c.bands[2].q, 30.0);
        assert!(
            (c.bands[3].q - std::f64::consts::SQRT_2).abs() < 1e-3,
            "BW 1 oct -> Q 1.414"
        );
        assert_eq!(c.graphic.as_ref().unwrap().len(), 3);
        // Device: and Copy: are reported, not fatal.
        assert_eq!(c.warnings.len(), 2, "{:?}", c.warnings);
    }

    #[test]
    fn write_then_parse_roundtrip() {
        let bands = parse(AUTOEQ).bands;
        let text = write(&[Section {
            device: Some("KZ"),
            preamp_db: -6.4,
            bands: &bands,
        }]);
        let back = parse(&text);
        assert_eq!(back.bands, bands);
        assert_eq!(back.preamp_db, Some(-6.4));
    }

    #[test]
    fn autoeq_into_correction() {
        let p = parse(AUTOEQ).into_correction(
            "Momentum 4",
            CorrectionSource {
                provider: "AutoEQ".into(),
                name: "Sennheiser Momentum 4 Wireless".into(),
            },
        );
        assert_eq!(p.correction.len(), 6);
        assert_eq!(p.preamp, Preamp::Auto);
    }
}
