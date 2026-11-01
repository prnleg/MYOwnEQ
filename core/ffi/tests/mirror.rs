//! Two app cores (Mac hosting on LAN, PC joining) mirror a linked device's profile.

use myowneq_ffi::service::Core;
use serde_json::{json, Value};
use std::time::{Duration, Instant};

fn cmd(c: &mut Core, v: Value) -> Value {
    serde_json::from_str(&c.command(&v.to_string())).unwrap()
}

fn state(c: &mut Core) -> Value {
    cmd(c, json!({"cmd": "get_state"}))
}

fn until(mac: &mut Core, pc: &mut Core, mut f: impl FnMut(&mut Core, &mut Core) -> bool) {
    let end = Instant::now() + Duration::from_secs(15);
    while Instant::now() < end {
        cmd(mac, json!({"cmd": "sync_tick"}));
        cmd(pc, json!({"cmd": "sync_tick"}));
        if f(mac, pc) {
            return;
        }
        std::thread::sleep(Duration::from_millis(25));
    }
    panic!(
        "timeout\nmac: {}\npc: {}",
        state(mac)["sync"],
        state(pc)["sync"]
    );
}

#[test]
fn linked_device_mirrors_selection() {
    let root = std::env::temp_dir().join(format!("meq-mirror-{}", std::process::id()));
    let (da, db) = (root.join("mac"), root.join("pc"));
    let mut mac = Core::new(Some(da), "mac");
    let mut pc = Core::new(Some(db), "pc");

    let s = cmd(
        &mut mac,
        json!({"cmd":"sync_configure","config":{"mode":"host","port":0,"token":"ABCD-EFGH"}}),
    );
    let port = s["sync"]["host_port"].as_u64().expect("host port");
    cmd(
        &mut pc,
        json!({"cmd":"sync_configure","config":{"mode":"server","url":format!("127.0.0.1:{port}"),"token":"ABCD-EFGH"}}),
    );

    let gm = cmd(&mut mac, json!({"cmd":"set_device","device":{"uid":"BuiltInHeadphoneOutputDevice","name":"External Headphones","kind":"analog"}}))["group"].as_str().unwrap().to_string();
    let gp = cmd(&mut pc, json!({"cmd":"set_device","device":{"uid":"{0.0.0.00000000}.{kz}","name":"Realtek Speakers","kind":"analog"}}))["group"].as_str().unwrap().to_string();

    // Both see both groups.
    until(&mut mac, &mut pc, |m, p| {
        state(m)["library"]["groups"].as_array().unwrap().len() == 2
            && state(p)["library"]["groups"].as_array().unwrap().len() == 2
    });

    // Link "KZ on PC" into "KZ on Mac".
    cmd(&mut mac, json!({"cmd":"link_groups","into":gm,"other":gp}));
    until(&mut mac, &mut pc, |_, p| {
        state(p)["library"]["groups"].as_array().unwrap().len() == 1
    });
    assert_eq!(
        state(&mut pc)["group"],
        json!(gm),
        "PC's device now belongs to the linked group"
    );

    // Mac selects Music -> PC follows.
    let music = state(&mut mac)["library"]["profiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["name"] == "Music")
        .unwrap()["id"]
        .clone();
    cmd(&mut mac, json!({"cmd":"select","group":gm,"profile":music}));
    until(&mut mac, &mut pc, |_, p| {
        let s = state(p);
        s["resolution"]["kind"] == "selected" && s["resolution"]["profile"] == music
    });

    // And back: PC selects FPS -> Mac follows.
    let fps = state(&mut pc)["library"]["profiles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["name"] == "FPS")
        .unwrap()["id"]
        .clone();
    cmd(&mut pc, json!({"cmd":"select","group":gm,"profile":fps}));
    until(&mut mac, &mut pc, |m, _| {
        state(m)["resolution"]["profile"] == fps
    });

    drop(mac);
    drop(pc);
    std::fs::remove_dir_all(root).ok();
}
