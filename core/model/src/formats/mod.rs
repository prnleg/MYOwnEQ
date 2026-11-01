//! Import / export formats.
//!
//! * [`apo`]: Equalizer APO / Peace config text. AutoEQ's
//!   `ParametricEQ.txt` uses the same syntax, so it is parsed here too.
//! * [`eqmac`]: eqMac "Advanced" (10-band) presets.

pub mod apo;
pub mod eqmac;

/// Format a float compactly for text configs (max 4 decimals, no trailing zeros).
pub(crate) fn fmt_num(v: f64) -> String {
    let s = format!("{:.4}", v);
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s == "-0" {
        "0".into()
    } else {
        s.into()
    }
}
