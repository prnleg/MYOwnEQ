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
    let bg = &v["background"];
    if let Some(img) = bg.get("image").filter(|i| !i.is_null()) {
        match img.as_str() {
            Some(f) if is_image_name(f) => {}
            _ => {
                return Err(
                    "\"background.image\" must be a .png/.jpg file name in Themes/images".into(),
                )
            }
        }
    }
    for key in ["image_opacity", "image_blur", "intensity", "speed"] {
        if let Some(x) = bg.get(key).filter(|x| !x.is_null()) {
            if !x.is_number() {
                return Err(format!("\"background.{key}\" must be a number"));
            }
        }
    }
    Ok(id.to_string())
}

/// Background images live in `<user Themes>/images/`; a theme names only the file.
pub fn is_image_name(f: &str) -> bool {
    let lower = f.to_ascii_lowercase();
    !f.is_empty()
        && f.len() <= 120
        && !f.contains(['/', '\\', ':'])
        && !f.starts_with('.')
        && [".png", ".jpg", ".jpeg"].iter().any(|e| lower.ends_with(e))
}

/// Id for a theme made in the app (`user-1a2b3c4d`).
pub fn new_theme_id() -> String {
    format!("user-{}", &uuid::Uuid::new_v4().simple().to_string()[..8])
}

fn safe_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
}

/// Write a user theme as `<dir>/<id>.json` (validated, atomic). Returns the id.
pub fn save_theme(dir: &Path, theme: &Value) -> Result<String, String> {
    let id = check_theme(theme)?;
    if !safe_id(&id) {
        return Err("theme id must be lowercase letters, digits and dashes".into());
    }
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let path = dir.join(format!("{id}.json"));
    let tmp = dir.join(format!(".{id}.json.tmp"));
    let text = serde_json::to_string_pretty(theme).map_err(|e| e.to_string())? + "\n";
    std::fs::write(&tmp, text).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, &path).map_err(|e| e.to_string())?;
    Ok(id)
}

/// Delete a user theme and its background image (unless another theme uses it).
pub fn delete_theme(dir: &Path, id: &str) -> Result<(), String> {
    if !safe_id(id) {
        return Err("bad theme id".into());
    }
    let path = dir.join(format!("{id}.json"));
    let image = std::fs::read_to_string(&path)
        .ok()
        .and_then(|s| serde_json::from_str::<Value>(&s).ok())
        .and_then(|v| v["background"]["image"].as_str().map(str::to_string));
    std::fs::remove_file(&path).map_err(|e| e.to_string())?;
    if let Some(img) = image.filter(|f| is_image_name(f)) {
        let used = json_files(dir).iter().any(|f| {
            std::fs::read_to_string(f)
                .ok()
                .and_then(|s| serde_json::from_str::<Value>(&s).ok())
                .is_some_and(|v| v["background"]["image"].as_str() == Some(img.as_str()))
        });
        if !used {
            let _ = std::fs::remove_file(dir.join("images").join(img));
        }
    }
    Ok(())
}

// ------------------------------------------------------------- theme codes

/// Theme share code prefix. Same idea as profile codes (`MEQ1-`): the whole theme
/// (colors and numbers, not the background image) packed into the text.
///
/// Layout (v1), then Fletcher-16 of everything before it, base64url:
/// ```text
/// u8  flags  bit0 dark, bit1 has panel, bit2 colors carry alpha (4 bytes, else 3)
/// str name, str author                   (u8 length + UTF-8)
/// color accent
/// u8 n + colors  curve      u8 n + colors  spectrum
/// color base     u8 n + colors  blobs     color panel (bit1)
/// u8 intensity ×100, u8 speed ×20, u8 glow ×100
/// ```
pub const THEME_PREFIX: &str = "MEQT-";
const MAX_LIST: usize = 12;

fn rgba(hex: &str) -> [u8; 4] {
    let h = hex.trim().trim_start_matches('#');
    let v = u32::from_str_radix(h, 16).unwrap_or(0);
    if h.len() == 8 {
        v.to_be_bytes()
    } else {
        let [_, r, g, b] = v.to_be_bytes();
        [r, g, b, 255]
    }
}

fn hex_of(c: [u8; 4]) -> String {
    if c[3] == 255 {
        format!("#{:02X}{:02X}{:02X}", c[0], c[1], c[2])
    } else {
        format!("#{:02X}{:02X}{:02X}{:02X}", c[0], c[1], c[2], c[3])
    }
}

fn colors(v: &Value) -> Vec<String> {
    v.as_array()
        .map(|a| {
            a.iter()
                .filter_map(|c| c.as_str().filter(|s| is_hex_color(s)))
                .take(MAX_LIST)
                .map(str::to_string)
                .collect()
        })
        .unwrap_or_default()
}

fn unit(v: &Value, default: f64, scale: f64) -> u8 {
    (v.as_f64().unwrap_or(default) * scale)
        .round()
        .clamp(0.0, 255.0) as u8
}

/// Pack a (valid) theme into `MEQT-…`. The background image isn't included.
pub fn encode_theme(theme: &Value) -> Result<String, String> {
    use base64::Engine;
    check_theme(theme)?;
    let bg = &theme["background"];
    let accent = theme["accent"].as_str().unwrap_or("#FF2E88").to_string();
    let base = bg["base"].as_str().unwrap_or("#000000").to_string();
    let panel = theme["panel"].as_str().map(str::to_string);
    let (curve, spectrum, blobs) = (
        colors(&theme["curve"]),
        colors(&theme["spectrum"]),
        colors(&bg["blobs"]),
    );
    let all: Vec<&String> = [&accent, &base]
        .into_iter()
        .chain(&curve)
        .chain(&spectrum)
        .chain(&blobs)
        .chain(panel.iter())
        .collect();
    let alpha = all.iter().any(|c| rgba(c)[3] != 255);
    let mut w = Vec::with_capacity(96);
    w.push(
        u8::from(theme["dark"].as_bool().unwrap_or(true))
            | u8::from(panel.is_some()) << 1
            | u8::from(alpha) << 2,
    );
    let name = theme["name"]
        .as_str()
        .or(theme["id"].as_str())
        .unwrap_or("");
    crate::share::put_str(&mut w, name);
    crate::share::put_str(&mut w, theme["author"].as_str().unwrap_or(""));
    let put = |w: &mut Vec<u8>, c: &str| {
        let b = rgba(c);
        w.extend_from_slice(if alpha { &b[..] } else { &b[..3] });
    };
    put(&mut w, &accent);
    for list in [&curve, &spectrum] {
        w.push(list.len() as u8);
        list.iter().for_each(|c| put(&mut w, c));
    }
    put(&mut w, &base);
    w.push(blobs.len() as u8);
    blobs.iter().for_each(|c| put(&mut w, c));
    if let Some(p) = &panel {
        put(&mut w, p);
    }
    w.push(unit(&bg["intensity"], 0.4, 100.0));
    w.push(unit(&bg["speed"], 1.0, 20.0));
    w.push(unit(&theme["glow"], 0.8, 100.0));
    let sum = crate::share::fletcher16(&w);
    w.extend_from_slice(&sum.to_le_bytes());
    Ok(format!(
        "{THEME_PREFIX}{}",
        base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(w)
    ))
}

/// First theme code in `text`, decoded into a new theme (fresh `user-…` id).
pub fn decode_theme(text: &str) -> Result<Value, crate::share::ShareError> {
    use crate::share::ShareError;
    use base64::Engine;
    let start = text.find(THEME_PREFIX).ok_or(ShareError::NotACode)?;
    let body = &text[start + THEME_PREFIX.len()..];
    let end = body
        .find(|c: char| !(c.is_ascii_alphanumeric() || c == '-' || c == '_'))
        .unwrap_or(body.len());
    let raw = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(&body[..end])
        .map_err(|_| ShareError::Damaged)?;
    if raw.len() < 3 {
        return Err(ShareError::Damaged);
    }
    let (data, sum) = raw.split_at(raw.len() - 2);
    if crate::share::fletcher16(data) != u16::from_le_bytes([sum[0], sum[1]]) {
        return Err(ShareError::Damaged);
    }
    read_theme(data).ok_or(ShareError::Damaged)
}

fn read_color(r: &mut crate::share::Reader, alpha: bool) -> Option<String> {
    Some(hex_of([
        r.u8()?,
        r.u8()?,
        r.u8()?,
        if alpha { r.u8()? } else { 255 },
    ]))
}

fn read_colors(r: &mut crate::share::Reader, alpha: bool) -> Option<Vec<String>> {
    let n = r.u8()? as usize;
    if n > MAX_LIST {
        return None;
    }
    (0..n).map(|_| read_color(r, alpha)).collect()
}

fn read_theme(data: &[u8]) -> Option<Value> {
    let mut r = crate::share::Reader { d: data, i: 0 };
    let flags = r.u8()?;
    let alpha = flags & 4 != 0;
    let name = r.str()?;
    let author = r.str()?;
    let accent = read_color(&mut r, alpha)?;
    let curve = read_colors(&mut r, alpha)?;
    let spectrum = read_colors(&mut r, alpha)?;
    let base = read_color(&mut r, alpha)?;
    let blobs = read_colors(&mut r, alpha)?;
    let panel = if flags & 2 != 0 {
        Some(read_color(&mut r, alpha)?)
    } else {
        None
    };
    let intensity = r.u8()? as f64 / 100.0;
    let speed = r.u8()? as f64 / 20.0;
    let glow = r.u8()? as f64 / 100.0;
    if r.i != data.len() {
        return None;
    }
    let mut v = serde_json::json!({
        "id": new_theme_id(),
        "name": if name.trim().is_empty() { "Shared theme".to_string() } else { name },
        "dark": flags & 1 != 0,
        "accent": accent,
        "curve": curve,
        "spectrum": spectrum,
        "background": { "base": base, "blobs": blobs, "intensity": intensity, "speed": speed },
        "glow": glow,
    });
    if !author.is_empty() {
        v["author"] = author.into();
    }
    if let Some(p) = panel {
        v["panel"] = p.into();
    }
    check_theme(&v).ok()?;
    Some(v)
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
    fn theme_codes_round_trip() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let t = load_themes(&[root.join("themes")]);
        for theme in &t.items {
            let code = encode_theme(theme).unwrap();
            assert!(code.starts_with("MEQT-") && code.len() < 140, "{code}");
            let back = decode_theme(&format!("olha esse tema: {code} !!")).unwrap();
            assert!(back["id"].as_str().unwrap().starts_with("user-"));
            for k in [
                "name", "dark", "accent", "curve", "spectrum", "panel", "glow",
            ] {
                assert_eq!(back[k], theme[k], "{k} of {}", theme["id"]);
            }
            for k in ["base", "blobs", "intensity", "speed"] {
                assert_eq!(back["background"][k], theme["background"][k], "{k}");
            }
        }
        // Alpha colors survive; image is not part of the code.
        let v = serde_json::json!({"id":"x","name":"A","accent":"#11223344",
            "background":{"base":"#000000","image":"bg.jpg","image_opacity":0.5}});
        let back = decode_theme(&encode_theme(&v).unwrap()).unwrap();
        assert_eq!(back["accent"], "#11223344");
        assert!(back["background"].get("image").is_none());
        // Profile codes aren't theme codes and vice versa.
        assert!(decode_theme("MEQ1-AAAA").is_err());
        let code = encode_theme(&v).unwrap();
        assert!(crate::share::find(&code).is_none());
        let mut bad = code.clone();
        bad.pop();
        assert!(decode_theme(&bad).is_err());
    }

    #[test]
    fn save_delete_and_image_names() {
        let d = dir("save", &[]);
        let v = serde_json::json!({"id":"user-1","name":"Mine","accent":"#FF0000",
            "background":{"base":"#000000","image":"user-1.jpg"}});
        std::fs::create_dir_all(d.join("images")).unwrap();
        std::fs::write(d.join("images/user-1.jpg"), b"x").unwrap();
        assert_eq!(save_theme(&d, &v).unwrap(), "user-1");
        assert_eq!(load_themes(&[&d]).items.len(), 1);
        let bad =
            serde_json::json!({"id":"../x","accent":"#FF0000","background":{"base":"#000000"}});
        assert!(save_theme(&d, &bad).is_err());
        let bad = serde_json::json!({"id":"y","accent":"#FF0000","background":{"base":"#000000","image":"../../etc/passwd"}});
        assert!(save_theme(&d, &bad).is_err());
        delete_theme(&d, "user-1").unwrap();
        assert!(load_themes(&[&d]).items.is_empty());
        assert!(!d.join("images/user-1.jpg").exists());
        assert!(is_image_name("a b.PNG") && !is_image_name("x.gif") && !is_image_name("a/b.png"));
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
