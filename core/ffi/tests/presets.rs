//! A library damaged by an older build (presets overwritten) is healed on
//! startup, on disk too; user profiles survive; presets stay read-only.

use myowneq_ffi::service::Core;
use serde_json::{json, Value};

fn cmd(c: &mut Core, v: Value) -> Value {
    serde_json::from_str(&c.command(&v.to_string())).unwrap()
}

fn bands(p: &Value) -> usize {
    p["eq"]["bands"].as_array().map_or(0, |b| b.len())
}

#[test]
fn damaged_presets_are_healed_and_locked() {
    let dir = std::env::temp_dir().join(format!("meq-presets-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();

    // Old-version file: 7 presets, FPS and Bass Boost wiped, Flat turned graphic, one user profile.
    let mut lib = myowneq_model::Library::with_presets();
    lib.profiles.retain(|p| {
        [
            "Flat",
            "Bass Boost",
            "FPS",
            "Music",
            "Vocal",
            "Movie",
            "Podcast",
        ]
        .contains(&p.name.as_str())
    });
    let mut v: Value = serde_json::from_str(&lib.to_json().unwrap()).unwrap();
    for p in v["profiles"].as_array_mut().unwrap() {
        match p["name"].as_str().unwrap() {
            "FPS" | "Bass Boost" => p["eq"] = json!({"type": "parametric", "bands": []}),
            "Flat" => {
                p["eq"] = json!({"type": "graphic", "points": [{"freq": 1000.0, "gain_db": 0.0}]})
            }
            _ => {}
        }
    }
    v["profiles"]
        .as_array_mut()
        .unwrap()
        .push(json!({"id": "11111111-1111-1111-1111-111111111111", "name": "Mine"}));
    std::fs::write(dir.join("library.json"), v.to_string()).unwrap();

    let mut core = Core::new(Some(dir.clone()), "m1");

    let check = |lib: &Value| {
        let profiles = lib["profiles"].as_array().unwrap();
        let by = |n: &str| {
            profiles
                .iter()
                .find(|p| p["name"] == n)
                .unwrap_or_else(|| panic!("{n} missing"))
        };
        assert_eq!(bands(by("FPS")), 5);
        assert_eq!(bands(by("Bass Boost")), 1);
        assert_eq!(by("Flat")["eq"]["type"], "parametric");
        assert!(by("Rock")["tags"][0] == "builtin", "new presets added");
        assert!(by("Mine").is_object(), "user profile kept");
    };
    let state = cmd(&mut core, json!({"cmd": "get_state"}));
    check(&state["library"]);
    // Healed on disk, not only in memory.
    check(
        &serde_json::from_str(&std::fs::read_to_string(dir.join("library.json")).unwrap()).unwrap(),
    );

    // Writes and deletes to a preset are ignored.
    let fps = state["library"]["profiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["name"] == "FPS")
        .unwrap()
        .clone();
    let mut broken = fps.clone();
    broken["eq"] = json!({"type": "parametric", "bands": []});
    cmd(
        &mut core,
        json!({"cmd": "upsert_profile", "profile": broken}),
    );
    cmd(&mut core, json!({"cmd": "delete_profile", "id": fps["id"]}));
    let after = cmd(&mut core, json!({"cmd": "get_state"}));
    let fps2 = after["library"]["profiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["name"] == "FPS")
        .unwrap();
    assert_eq!(bands(fps2), 5);

    let _ = std::fs::remove_dir_all(&dir);
}
