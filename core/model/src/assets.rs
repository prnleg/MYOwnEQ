//! Theme and language files (themes/*.json, lang/*.json) shared by every app.
//! The core parses, validates and merges them; apps only turn colors into
//! their toolkit's types. Same rules everywhere:
//! - folders are read in order; a later folder overrides by `id` (themes) or
//!   merges key by key by `code` (languages) — so user folders win;
//! - a broken file is reported in `errors` and skipped, never fatal.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;

#[derive(Debug, Default, Serialize)]
pub struct Loaded<T> {
    pub items: Vec<T>,
    pub errors: Vec<String>,
}

fn json_files(dir: &Path) -> Vec<std::path::PathBuf> {
    let mut v: Vec<_> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|e| e == "json"))
        .collect();
    v.sort();
    v
}

fn name(p: &Path) -> String {
    p.file_name()
        .map_or_else(String::new, |n| n.to_string_lossy().into_owned())
}

/// `#RRGGBB` or `#RRGGBBAA`.
pub fn is_hex_color(s: &str) -> bool {
    let h = s.trim().trim_start_matches('#');
    (h.len() == 6 || h.len() == 8) && h.chars().all(|c| c.is_ascii_hexdigit())
}

/// Validate a theme document; returns it unchanged (apps decode their own struct).
fn check_theme(v: &Value) -> Result<String, String> {
    let id = v["id"]
        .as_str()
        .filter(|s| !s.trim().is_empty())
        .ok_or("missing \"id\"")?;
    let color = |path: &str, val: &Value| -> Result<(), String> {
        match val.as_str() {
            Some(s) if is_hex_color(s) => Ok(()),
            _ => Err(format!("\"{path}\" must be a #RRGGBB color")),
        }
    };
    color("accent", &v["accent"])?;
    color("background.base", &v["background"]["base"])?;
    for key in ["curve", "spectrum"] {
        if let Some(arr) = v[key].as_array() {
            for (i, c) in arr.iter().enumerate() {
                color(&format!("{key}[{i}]"), c)?;
            }
        }
    }
    if let Some(arr) = v["background"]["blobs"].as_array() {
        for (i, c) in arr.iter().enumerate() {
            color(&format!("background.blobs[{i}]"), c)?;
        }
    }
    if let Some(p) = v.get("panel") {
        color("panel", p)?;
    }
    Ok(id.to_string())
}

/// Themes from `dirs` in order; later folders replace a theme with the same id.
pub fn load_themes(dirs: &[impl AsRef<Path>]) -> Loaded<Value> {
    let mut order: Vec<String> = Vec::new();
    let mut by_id: HashMap<String, Value> = HashMap::new();
    let mut errors = Vec::new();
    for dir in dirs {
        for f in json_files(dir.as_ref()) {
            let parsed = std::fs::read_to_string(&f)
                .map_err(|e| e.to_string())
                .and_then(|s| serde_json::from_str::<Value>(&s).map_err(|e| e.to_string()))
                .and_then(|v| check_theme(&v).map(|id| (id, v)));
            match parsed {
                Ok((id, v)) => {
                    if !by_id.contains_key(&id) {
                        order.push(id.clone());
                    }
                    by_id.insert(id, v);
                }
                Err(e) => errors.push(format!("{}: {e}", name(&f))),
            }
        }
    }
    Loaded {
        items: order
            .into_iter()
            .filter_map(|id| by_id.remove(&id))
            .collect(),
        errors,
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct LangPack {
    pub code: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub author: Option<String>,
    #[serde(default)]
    pub strings: HashMap<String, String>,
}

/// Language packs from `dirs`; same `code` in a later folder merges key by key.
/// en-US always exists (keys are the English text).
pub fn load_langs(dirs: &[impl AsRef<Path>]) -> Loaded<LangPack> {
    let mut by_code: HashMap<String, LangPack> = HashMap::new();
    by_code.insert(
        "en-US".into(),
        LangPack {
            code: "en-US".into(),
            name: "English (US)".into(),
            author: Some("MyOwnEQ".into()),
            strings: HashMap::new(),
        },
    );
    let mut errors = Vec::new();
    for dir in dirs {
        for f in json_files(dir.as_ref()) {
            let parsed = std::fs::read_to_string(&f)
                .map_err(|e| e.to_string())
                .and_then(|s| serde_json::from_str::<LangPack>(&s).map_err(|e| e.to_string()))
                .and_then(|p| {
                    if p.code.trim().is_empty() {
                        Err("missing \"code\"".into())
                    } else {
                        Ok(p)
                    }
                });
            match parsed {
                Ok(p) => match by_code.get_mut(&p.code) {
                    Some(existing) => {
                        existing.strings.extend(p.strings);
                        if !p.name.is_empty() {
                            existing.name = p.name;
                        }
                    }
                    None => {
                        by_code.insert(p.code.clone(), p);
                    }
                },
                Err(e) => errors.push(format!("{}: {e}", name(&f))),
            }
        }
    }
    let mut items: Vec<LangPack> = by_code.into_values().collect();
    items.sort_by_key(|p| p.name.to_lowercase());
    Loaded { items, errors }
}

/// Which pack to use: the user's choice if it exists, else the first system
/// language we have (exact, then same language), else en-US.
pub fn pick_lang(packs: &[LangPack], choice: Option<&str>, preferred: &[String]) -> String {
    let has = |c: &str| {
        packs
            .iter()
            .find(|p| p.code.eq_ignore_ascii_case(c))
            .map(|p| p.code.clone())
    };
    if let Some(c) = choice.and_then(has) {
        return c;
    }
    for pref in preferred {
        if let Some(c) = has(pref) {
            return c;
        }
        let lang = pref.split(['-', '_']).next().unwrap_or(pref).to_lowercase();
        if let Some(p) = packs
            .iter()
            .find(|p| p.code.to_lowercase().split('-').next() == Some(lang.as_str()))
        {
            return p.code.clone();
        }
    }
    "en-US".into()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dir(name: &str, files: &[(&str, &str)]) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("meq-assets-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        for (f, body) in files {
            std::fs::write(d.join(f), body).unwrap();
        }
        d
    }

    #[test]
    fn repo_themes_and_langs_are_valid() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let t = load_themes(&[root.join("themes")]);
        assert!(t.errors.is_empty(), "{:?}", t.errors);
        assert!(t.items.iter().any(|v| v["id"] == "neon-sunset"));
        let l = load_langs(&[root.join("lang")]);
        assert!(l.errors.is_empty(), "{:?}", l.errors);
        assert!(l.items.iter().any(|p| p.code == "pt-BR"));
    }

    #[test]
    fn user_folder_overrides_and_bad_files_are_reported() {
        let base = r##"{"id":"a","accent":"#FF0000","background":{"base":"#000000"}}"##;
        let user =
            r##"{"id":"a","name":"Mine","accent":"#00FF00","background":{"base":"#000000"}}"##;
        let d1 = dir("t1", &[("a.json", base)]);
        let d2 = dir(
            "t2",
            &[
                ("a.json", user),
                ("bad.json", r#"{"id":"b","accent":"red"}"#),
            ],
        );
        let t = load_themes(&[&d1, &d2]);
        assert_eq!(t.items.len(), 1);
        assert_eq!(t.items[0]["accent"], "#00FF00");
        assert_eq!(t.errors.len(), 1);
        assert!(t.errors[0].contains("bad.json"));
    }

    #[test]
    fn langs_merge_and_pick() {
        let d1 = dir(
            "l1",
            &[(
                "pt.json",
                r#"{"code":"pt-BR","name":"Português","strings":{"Save":"Salvar","Quit":"Sair"}}"#,
            )],
        );
        let d2 = dir(
            "l2",
            &[(
                "pt.json",
                r#"{"code":"pt-BR","name":"Português","strings":{"Save":"Gravar"}}"#,
            )],
        );
        let l = load_langs(&[&d1, &d2]);
        let pt = l.items.iter().find(|p| p.code == "pt-BR").unwrap();
        assert_eq!(pt.strings["Save"], "Gravar");
        assert_eq!(pt.strings["Quit"], "Sair");
        assert_eq!(pick_lang(&l.items, None, &["pt-PT".into()]), "pt-BR");
        assert_eq!(
            pick_lang(&l.items, Some("en-US"), &["pt-BR".into()]),
            "en-US"
        );
        assert_eq!(pick_lang(&l.items, None, &["de-DE".into()]), "en-US");
    }
}
