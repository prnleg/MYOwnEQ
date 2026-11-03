//! AutoEQ headphone database: parsing its index and searching it.
//!
//! The AutoEQ project (github.com/jaakkopasanen/AutoEq, MIT) publishes ~9k headphone
//! corrections. `results/INDEX.md` lists every one as
//! `- [Name](./source/rig type/Name) by source on rig`; each folder has a
//! `Name ParametricEQ.txt` in Equalizer APO format (see [`crate::formats::apo`]).
//! Each result is already fitted to the target its measurer chose; picking another
//! target would need our own fitter, so for now the choice is which measurement.
//!
//! This module is pure (no network): the app side downloads and caches the files.

use serde::Serialize;

pub const RESULTS_URL: &str =
    "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";
pub const INDEX_URL: &str =
    "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/INDEX.md";

/// One measurement of one headphone.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct Entry {
    pub name: String,
    /// Folder under `results/`, decoded (e.g. `oratory1990/over-ear/Sennheiser HD 600`).
    pub path: String,
    /// Who measured it (e.g. `oratory1990`).
    pub source: String,
    /// Measurement rig, when the source used several (e.g. `GRAS 43AG-7`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rig: Option<String>,
    pub kind: Kind,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Kind {
    OverEar,
    InEar,
    Earbud,
    Other,
}

/// A headphone model with every measurement of it, best first.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct Model {
    pub name: String,
    pub kind: Kind,
    pub measurements: Vec<Entry>,
}

/// Parse `INDEX.md`. Lines that don't look like entries are skipped.
pub fn parse_index(text: &str) -> Vec<Entry> {
    text.lines().filter_map(parse_line).collect()
}

fn parse_line(line: &str) -> Option<Entry> {
    let rest = line.trim().strip_prefix("- [")?;
    // Names may contain brackets/parentheses: the link starts at the last "](./".
    let split = rest.rfind("](./")?;
    let name = rest[..split].trim().to_string();
    let rest = &rest[split + 4..];
    let close = rest.rfind(") by ")?;
    let path = percent_decode(&rest[..close]);
    let by = rest[close + 5..].trim();
    let (source, rig) = match by.split_once(" on ") {
        Some((s, r)) => (s.trim().to_string(), Some(r.trim().to_string())),
        None => (by.to_string(), None),
    };
    if name.is_empty() || !valid_path(&path) {
        return None;
    }
    let kind = kind_of(&path);
    Some(Entry {
        name,
        path,
        source,
        rig,
        kind,
    })
}

/// Paths are `source/type/name`; nothing that could escape `results/`.
pub fn valid_path(path: &str) -> bool {
    let parts: Vec<&str> = path.split('/').collect();
    parts.len() >= 2
        && parts
            .iter()
            .all(|p| !p.is_empty() && *p != "." && *p != ".." && !p.contains('\\'))
        && !path.contains("://")
}

fn kind_of(path: &str) -> Kind {
    let mid = path
        .split('/')
        .rev()
        .nth(1)
        .unwrap_or("")
        .to_ascii_lowercase();
    if mid.ends_with("in-ear") {
        Kind::InEar
    } else if mid.ends_with("over-ear") {
        Kind::OverEar
    } else if mid.ends_with("earbud") {
        Kind::Earbud
    } else {
        Kind::Other
    }
}

/// URL of a result's `ParametricEQ.txt`.
pub fn file_url(path: &str) -> String {
    let file = path.rsplit('/').next().unwrap_or(path);
    let mut url = String::from(RESULTS_URL);
    for seg in path.split('/') {
        url.push_str(&percent_encode(seg));
        url.push('/');
    }
    url.push_str(&percent_encode(&format!("{file} ParametricEQ.txt")));
    url
}

/// Measurers in the order AutoEQ recommends trusting them; others after, by name.
const SOURCE_RANK: &[&str] = &[
    "oratory1990",
    "crinacle",
    "Rtings",
    "Innerfidelity",
    "Headphone.com Legacy",
];

fn source_rank(e: &Entry) -> (usize, String, String) {
    let r = SOURCE_RANK
        .iter()
        .position(|s| s.eq_ignore_ascii_case(&e.source))
        .unwrap_or(SOURCE_RANK.len());
    (
        r,
        e.source.to_ascii_lowercase(),
        e.rig.clone().unwrap_or_default(),
    )
}

/// Lowercase words, punctuation as separators ("WH-1000XM4" → ["wh", "1000xm4"]).
fn words(s: &str) -> Vec<String> {
    s.to_lowercase()
        .split(|c: char| !c.is_alphanumeric())
        .filter(|w| !w.is_empty())
        .map(str::to_string)
        .collect()
}

/// Same words glued together, so "wh1000xm4" also finds "WH-1000XM4".
fn squashed(s: &str) -> String {
    words(s).concat()
}

/// Words in output-device names that aren't part of the model name.
const DEVICE_NOISE: &[&str] = &[
    "headphones",
    "headphone",
    "headset",
    "earphones",
    "stereo",
    "hands",
    "free",
    "handsfree",
    "audio",
    "output",
    "wireless",
    "bluetooth",
    "speakers",
    "speaker",
    "the",
    "de",
    "do",
    "da",
    "fones",
    "fone",
    "usb",
    "digital",
    "analog",
    "line",
    "out",
];

/// Turn an output-device name ("Momentum 4 Headphones", "KZ ZSN Pro (USB Audio)")
/// into a search query.
pub fn query_from_device(name: &str) -> String {
    words(name)
        .into_iter()
        .filter(|w| !DEVICE_NOISE.contains(&w.as_str()))
        .collect::<Vec<_>>()
        .join(" ")
}

/// Search by name. Every query word must match (as a word prefix, or inside the
/// squashed name). Best matches first; same model from several sources grouped.
pub fn search(entries: &[Entry], query: &str, limit: usize) -> Vec<Model> {
    let q = words(query);
    if q.is_empty() {
        return Vec::new();
    }
    let q_squashed = q.concat();
    let mut scored: Vec<(i32, &Entry)> = Vec::new();
    for e in entries {
        let w = words(&e.name);
        let sq = squashed(&e.name);
        let mut score = 0;
        let mut all = true;
        for qw in &q {
            if w.iter().any(|x| x == qw) {
                score += 3;
            } else if w.iter().any(|x| x.starts_with(qw.as_str())) {
                score += 2;
            } else if sq.contains(qw.as_str()) {
                score += 1;
            } else {
                all = false;
                break;
            }
        }
        if !all {
            // "wh1000xm4" typed without separators.
            if q.len() == 1 && sq.contains(&q_squashed) {
                score = 1;
            } else {
                continue;
            }
        }
        if sq == q_squashed {
            score += 10; // exact model
        }
        // Fewer extra words = closer match ("HD 600" before "HD 600 (2020)").
        score -= w.len().saturating_sub(q.len()) as i32;
        scored.push((score, e));
    }
    scored.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.name.cmp(&b.1.name)));

    let mut models: Vec<Model> = Vec::new();
    for (_, e) in scored {
        if let Some(m) = models
            .iter_mut()
            .find(|m| m.name.eq_ignore_ascii_case(&e.name))
        {
            m.measurements.push(e.clone());
            continue;
        }
        if models.len() >= limit {
            continue;
        }
        models.push(Model {
            name: e.name.clone(),
            kind: e.kind,
            measurements: vec![e.clone()],
        });
    }
    for m in &mut models {
        m.measurements.sort_by_key(source_rank);
        m.kind = m.measurements[0].kind;
    }
    models
}

/// Label for a measurement: "oratory1990" or "crinacle · GRAS 43AG-7".
pub fn source_label(e: &Entry) -> String {
    match &e.rig {
        Some(r) => format!("{} · {}", e.source, r),
        None => e.source.clone(),
    }
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' {
            if let Some(v) = b
                .get(i + 1..i + 3)
                .and_then(|h| std::str::from_utf8(h).ok())
                .and_then(|h| u8::from_str_radix(h, 16).ok())
            {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn percent_encode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~()!*',".contains(&b) {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const INDEX: &str = "# Index\nThis is a list...\n\n\
- [Sennheiser HD 600](./oratory1990/over-ear/Sennheiser%20HD%20600) by oratory1990\n\
- [Sennheiser HD 600](./crinacle/GRAS%2043AG-7%20over-ear/Sennheiser%20HD%20600) by crinacle on GRAS 43AG-7\n\
- [Sennheiser HD 600](./Innerfidelity/over-ear/Sennheiser%20HD%20600) by Innerfidelity\n\
- [Sennheiser HD 650](./oratory1990/over-ear/Sennheiser%20HD%20650) by oratory1990\n\
- [Sony WH-1000XM4](./Rtings/over-ear/Sony%20WH-1000XM4) by Rtings\n\
- [KZ ZSN Pro](./crinacle/711%20in-ear/KZ%20ZSN%20Pro) by crinacle on 711\n\
- [KZ ZSN Pro X](./Super%20Review/in-ear/KZ%20ZSN%20Pro%20X) by Super Review\n\
- [1MORE Aero (ANC Off)](./HypetheSonics/GRAS%20RA0045%20in-ear/1MORE%20Aero%20(ANC%20Off)) by HypetheSonics on GRAS RA0045\n\
- [Apple AirPods](./Rtings/earbud/Apple%20AirPods) by Rtings\n\
- [Evil](./../../etc/passwd) by nobody\n";

    #[test]
    fn parses_entries_and_skips_bad_paths() {
        let e = parse_index(INDEX);
        assert_eq!(e.len(), 9);
        assert_eq!(e[0].path, "oratory1990/over-ear/Sennheiser HD 600");
        assert_eq!(e[1].rig.as_deref(), Some("GRAS 43AG-7"));
        assert_eq!(e[1].kind, Kind::OverEar);
        assert_eq!(e[5].kind, Kind::InEar);
        assert_eq!(e[7].name, "1MORE Aero (ANC Off)");
        assert_eq!(e[8].kind, Kind::Earbud);
    }

    #[test]
    fn search_groups_sources_best_first() {
        let e = parse_index(INDEX);
        let r = search(&e, "hd600", 10);
        assert_eq!(r[0].name, "Sennheiser HD 600");
        let srcs: Vec<_> = r[0]
            .measurements
            .iter()
            .map(|m| m.source.as_str())
            .collect();
        assert_eq!(srcs, ["oratory1990", "crinacle", "Innerfidelity"]);

        let r = search(&e, "sennheiser hd 6", 10);
        assert_eq!(r.len(), 2);

        let r = search(&e, "zsn pro", 10);
        assert_eq!(r[0].name, "KZ ZSN Pro"); // exact-ish before "Pro X"
        assert_eq!(r[1].name, "KZ ZSN Pro X");

        assert_eq!(search(&e, "wh1000xm4", 10)[0].name, "Sony WH-1000XM4");
        assert_eq!(search(&e, "1000xm4", 10)[0].name, "Sony WH-1000XM4");
        assert!(search(&e, "nothing like this", 10).is_empty());
        assert!(search(&e, "  ", 10).is_empty());
        assert_eq!(search(&e, "sennheiser", 1).len(), 1);
    }

    #[test]
    fn device_names_become_queries() {
        assert_eq!(query_from_device("Momentum 4 Headphones"), "momentum 4");
        assert_eq!(query_from_device("KZ ZSN Pro (USB Audio)"), "kz zsn pro");
        let e = parse_index(INDEX);
        let q = query_from_device("Sony WH-1000XM4 Hands-Free");
        assert_eq!(search(&e, &q, 5)[0].name, "Sony WH-1000XM4");
    }

    #[test]
    fn urls_are_encoded() {
        assert_eq!(
            file_url("oratory1990/over-ear/Sennheiser HD 600"),
            "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/oratory1990/over-ear/Sennheiser%20HD%20600/Sennheiser%20HD%20600%20ParametricEQ.txt"
        );
        assert!(file_url("a/b/Foo & Bar #2")
            .ends_with("Foo%20%26%20Bar%20%232/Foo%20%26%20Bar%20%232%20ParametricEQ.txt"));
    }

    #[test]
    fn rejects_escaping_paths() {
        assert!(!valid_path("../x"));
        assert!(!valid_path("a/../b"));
        assert!(!valid_path("single"));
        assert!(!valid_path("a//b"));
        assert!(!valid_path("https://evil/x"));
        assert!(valid_path("crinacle/711 in-ear/KZ ZSN Pro"));
    }
}
