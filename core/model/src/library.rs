//! The persisted library (synced between machines) and machine-local state.

use crate::device::{DeviceGroup, DeviceKey, DeviceMember, MachineId};
use crate::profile::{Profile, RenderContext};
use crate::rules::{self, AppSnapshot, Platform, Resolution, Rule, RuleOverride};
use crate::Id;
use myowneq_dsp::EqParams;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::Path;

pub const SCHEMA_VERSION: u32 = 1;

#[derive(Debug, thiserror::Error)]
pub enum LibraryError {
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
    #[error("json: {0}")]
    Json(#[from] serde_json::Error),
    #[error("library schema v{found} is newer than supported v{SCHEMA_VERSION}; update MyOwnEQ")]
    TooNew { found: u32 },
    #[error("unknown id {0}")]
    UnknownId(Id),
}

/// Everything that syncs between machines.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Library {
    pub schema: u32,
    pub profiles: Vec<Profile>,
    pub groups: Vec<DeviceGroup>,
    pub rules: Vec<Rule>,
    /// Manually selected profile per device group. Mirrors across machines.
    #[serde(default)]
    pub selected: BTreeMap<Id, Id>,
}

/// State that never leaves this machine.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct LocalState {
    pub machine: MachineId,
    /// Per group: user override while a rule is active.
    #[serde(default)]
    pub rule_overrides: BTreeMap<Id, RuleOverride>,
}

impl LocalState {
    pub fn new(machine: MachineId) -> Self {
        LocalState {
            machine,
            rule_overrides: BTreeMap::new(),
        }
    }
}

impl Default for Library {
    fn default() -> Self {
        Library {
            schema: SCHEMA_VERSION,
            profiles: Vec::new(),
            groups: Vec::new(),
            rules: Vec::new(),
            selected: BTreeMap::new(),
        }
    }
}

impl Library {
    /// New library pre-filled with built-in presets.
    pub fn with_presets() -> Self {
        Library {
            profiles: crate::presets::builtin(),
            ..Default::default()
        }
    }

    // ---------- persistence ----------

    pub fn from_json(s: &str) -> Result<Self, LibraryError> {
        #[derive(Deserialize)]
        struct Probe {
            schema: u32,
        }
        let probe: Probe = serde_json::from_str(s)?;
        if probe.schema > SCHEMA_VERSION {
            return Err(LibraryError::TooNew {
                found: probe.schema,
            });
        }
        Ok(serde_json::from_str(s)?)
    }

    pub fn to_json(&self) -> Result<String, LibraryError> {
        Ok(serde_json::to_string_pretty(self)?)
    }

    pub fn load(path: &Path) -> Result<Self, LibraryError> {
        Self::from_json(&std::fs::read_to_string(path)?)
    }

    /// Atomic save: write temp file next to target, then rename.
    pub fn save(&self, path: &Path) -> Result<(), LibraryError> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, self.to_json()?)?;
        std::fs::rename(&tmp, path)?;
        Ok(())
    }

    // ---------- profiles ----------

    pub fn profile(&self, id: Id) -> Option<&Profile> {
        self.profiles.iter().find(|p| p.id == id)
    }

    pub fn profile_mut(&mut self, id: Id) -> Option<&mut Profile> {
        self.profiles.iter_mut().find(|p| p.id == id)
    }

    pub fn upsert_profile(&mut self, profile: Profile) {
        match self.profile_mut(profile.id) {
            Some(p) => *p = profile,
            None => self.profiles.push(profile),
        }
    }

    /// Remove a profile and every reference to it.
    pub fn delete_profile(&mut self, id: Id) {
        self.profiles.retain(|p| p.id != id);
        for g in &mut self.groups {
            if g.default_profile == Some(id) {
                g.default_profile = None;
            }
            g.favorites.retain(|f| *f != id);
        }
        self.rules.retain(|r| r.profile != id);
        self.selected.retain(|_, p| *p != id);
    }

    // ---------- devices ----------

    pub fn group(&self, id: Id) -> Option<&DeviceGroup> {
        self.groups.iter().find(|g| g.id == id)
    }

    pub fn group_mut(&mut self, id: Id) -> Option<&mut DeviceGroup> {
        self.groups.iter_mut().find(|g| g.id == id)
    }

    pub fn group_for(&self, key: &DeviceKey) -> Option<&DeviceGroup> {
        self.groups.iter().find(|g| g.contains(key))
    }

    /// Register a device seen by the OS; returns its group id. Updates the
    /// stored OS name if it changed.
    pub fn ensure_device(&mut self, member: DeviceMember) -> Id {
        for g in &mut self.groups {
            if let Some(m) = g.members.iter_mut().find(|m| m.key == member.key) {
                m.os_name = member.os_name;
                m.kind = member.kind;
                return g.id;
            }
        }
        let g = DeviceGroup::single(member);
        let id = g.id;
        self.groups.push(g);
        id
    }

    /// Merge group `other` into `into` ("these are the same headphones").
    pub fn link_groups(&mut self, into: Id, other: Id) -> Result<(), LibraryError> {
        if into == other {
            return Ok(());
        }
        let idx = self
            .groups
            .iter()
            .position(|g| g.id == other)
            .ok_or(LibraryError::UnknownId(other))?;
        if self.group(into).is_none() {
            return Err(LibraryError::UnknownId(into));
        }
        let src = self.groups.remove(idx);
        let dst = self.group_mut(into).expect("checked");
        dst.members.extend(src.members);
        for f in src.favorites {
            if !dst.favorites.contains(&f) {
                dst.favorites.push(f);
            }
        }
        if dst.default_profile.is_none() {
            dst.default_profile = src.default_profile;
        }
        for r in &mut self.rules {
            if r.group == other {
                r.group = into;
            }
        }
        if let Some(sel) = self.selected.remove(&other) {
            self.selected.entry(into).or_insert(sel);
        }
        Ok(())
    }

    /// Split a device out of its group into a fresh group of its own.
    pub fn unlink_device(&mut self, key: &DeviceKey) -> Option<Id> {
        let g = self
            .groups
            .iter_mut()
            .find(|g| g.contains(key) && g.members.len() > 1)?;
        let pos = g.members.iter().position(|m| &m.key == key)?;
        let member = g.members.remove(pos);
        let mut fresh = DeviceGroup::single(member);
        fresh.default_profile = g.default_profile;
        fresh.favorites = g.favorites.clone();
        let id = fresh.id;
        self.groups.push(fresh);
        Some(id)
    }

    /// Favorites of currently connected devices only (Easy-mode quick panel).
    pub fn connected_favorites<'a>(
        &'a self,
        connected: &'a [DeviceKey],
    ) -> impl Iterator<Item = (&'a DeviceGroup, Vec<&'a Profile>)> + 'a {
        self.groups
            .iter()
            .filter(|g| connected.iter().any(|k| g.contains(k)))
            .map(move |g| {
                let favs = g
                    .favorites
                    .iter()
                    .filter_map(|id| self.profile(*id))
                    .collect();
                (g, favs)
            })
    }

    // ---------- selection & resolution ----------

    /// Decide which profile applies to `device` right now.
    pub fn resolve(
        &self,
        device: &DeviceKey,
        platform: Platform,
        apps: &[AppSnapshot],
        local: &LocalState,
    ) -> Resolution {
        let Some(g) = self.group_for(device) else {
            return Resolution::Flat;
        };
        let rule = rules::matching_rule(&self.rules, g.id, platform, apps);
        let res = rules::resolve(
            rule,
            local.rule_overrides.get(&g.id).copied(),
            self.selected.get(&g.id).copied(),
            g.default_profile,
        );
        // Dangling ids (profile deleted on another machine) fall back to flat.
        match res.profile() {
            Some(p) if self.profile(p).is_none() => Resolution::Flat,
            _ => res,
        }
    }

    /// User picked `profile` for `group` in the UI. If a rule is currently
    /// active, the choice is a local override of that rule; otherwise it
    /// becomes the mirrored selection.
    pub fn select(
        &mut self,
        group: Id,
        profile: Id,
        active_rule: Option<Id>,
        local: &mut LocalState,
    ) {
        match active_rule {
            Some(rule) => {
                local
                    .rule_overrides
                    .insert(group, RuleOverride { rule, profile });
            }
            None => {
                local.rule_overrides.remove(&group);
                self.selected.insert(group, profile);
            }
        }
    }

    /// Final DSP parameters for `device`.
    pub fn render(
        &self,
        device: &DeviceKey,
        platform: Platform,
        apps: &[AppSnapshot],
        local: &LocalState,
        ctx: &RenderContext,
    ) -> (Resolution, EqParams) {
        let res = self.resolve(device, platform, apps, local);
        let enabled = self.group_for(device).is_none_or(|g| g.enabled);
        let mut params = match res.profile().and_then(|id| self.profile(id)) {
            Some(p) => p.render(ctx),
            None => EqParams::default(),
        };
        params.bypass = !enabled;
        (res, params)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::device::DeviceKind;
    use crate::rules::{AppMatch, Trigger};

    fn key(machine: &str, uid: &str) -> DeviceKey {
        DeviceKey {
            machine: MachineId(machine.into()),
            uid: uid.into(),
        }
    }

    fn member(machine: &str, uid: &str, name: &str) -> DeviceMember {
        DeviceMember {
            key: key(machine, uid),
            os_name: name.into(),
            kind: DeviceKind::Analog,
        }
    }

    fn named(lib: &Library, name: &str) -> Id {
        lib.profiles.iter().find(|p| p.name == name).unwrap().id
    }

    /// The scenario from planning: Momentum 4 on BT, KZ on the jack.
    /// KZ default = Default; CS2 running -> FPS on KZ only.
    #[test]
    fn kz_cs2_scenario() {
        let mut lib = Library::with_presets();
        let mac = LocalState::new(MachineId("mac".into()));
        let kz = lib.ensure_device(member(
            "mac",
            "BuiltInHeadphoneOutputDevice",
            "External Headphones",
        ));
        let m4 = lib.ensure_device(member("mac", "80-C3-BA-4A-28-CB:output", "M4"));
        let default = named(&lib, "Flat");
        let fps = named(&lib, "FPS");
        lib.group_mut(kz).unwrap().default_profile = Some(default);
        lib.rules.push(Rule {
            id: crate::new_id(),
            group: kz,
            app: AppMatch {
                name: "CS2".into(),
                mac_bundle_id: Some("com.valvesoftware.cs2".into()),
                win_exe: Some("cs2.exe".into()),
            },
            trigger: Trigger::Running,
            profile: fps,
            priority: 0,
            enabled: true,
        });

        let kz_key = key("mac", "BuiltInHeadphoneOutputDevice");
        let m4_key = key("mac", "80-C3-BA-4A-28-CB:output");
        let none: Vec<AppSnapshot> = vec![];
        let cs2 = vec![AppSnapshot {
            id: "com.valvesoftware.cs2".into(),
            focused: false,
        }];

        assert_eq!(
            lib.resolve(&kz_key, Platform::Mac, &none, &mac),
            Resolution::Default(default)
        );
        assert!(
            matches!(lib.resolve(&kz_key, Platform::Mac, &cs2, &mac), Resolution::Rule { profile, .. } if profile == fps)
        );
        assert_eq!(
            lib.resolve(&m4_key, Platform::Mac, &cs2, &mac),
            Resolution::Flat
        );
        let _ = m4;
    }

    #[test]
    fn linking_mirrors_selection() {
        let mut lib = Library::with_presets();
        let mut mac = LocalState::new(MachineId("mac".into()));
        let pc = LocalState::new(MachineId("pc".into()));
        let g_mac = lib.ensure_device(member("mac", "jack", "External Headphones"));
        let g_pc = lib.ensure_device(member("pc", "{0.0.0.00000000}.{abc}", "Realtek Speakers"));
        lib.link_groups(g_mac, g_pc).unwrap();
        assert_eq!(lib.groups.len(), 1);
        assert!(lib.groups[0].is_linked());

        let music = named(&lib, "Music");
        lib.select(g_mac, music, None, &mut mac);
        let r = lib.resolve(
            &key("pc", "{0.0.0.00000000}.{abc}"),
            Platform::Windows,
            &[],
            &pc,
        );
        assert_eq!(r, Resolution::Selected(music));
    }

    #[test]
    fn manual_pick_during_rule_is_local_override() {
        let mut lib = Library::with_presets();
        let mut local = LocalState::new(MachineId("mac".into()));
        let g = lib.ensure_device(member("mac", "jack", "Jack"));
        let fps = named(&lib, "FPS");
        let music = named(&lib, "Music");
        let rule_id = crate::new_id();
        lib.rules.push(Rule {
            id: rule_id,
            group: g,
            app: AppMatch {
                name: "Spotify".into(),
                mac_bundle_id: Some("com.spotify.client".into()),
                win_exe: None,
            },
            trigger: Trigger::Focused,
            profile: fps,
            priority: 0,
            enabled: true,
        });
        let focused = vec![AppSnapshot {
            id: "com.spotify.client".into(),
            focused: true,
        }];
        let background = vec![AppSnapshot {
            id: "com.spotify.client".into(),
            focused: false,
        }];
        lib.select(g, music, Some(rule_id), &mut local);
        let k = key("mac", "jack");
        assert_eq!(
            lib.resolve(&k, Platform::Mac, &focused, &local),
            Resolution::RuleOverride {
                rule: rule_id,
                profile: music
            }
        );
        // Not focused -> rule inactive -> override ignored, nothing selected -> flat.
        assert_eq!(
            lib.resolve(&k, Platform::Mac, &background, &local),
            Resolution::Flat
        );
        assert!(lib.selected.is_empty(), "rule override must not mirror");
    }

    #[test]
    fn delete_profile_cleans_references() {
        let mut lib = Library::with_presets();
        let mut local = LocalState::new(MachineId("m".into()));
        let g = lib.ensure_device(member("m", "d", "D"));
        let fps = named(&lib, "FPS");
        lib.group_mut(g).unwrap().default_profile = Some(fps);
        lib.group_mut(g).unwrap().favorites.push(fps);
        lib.select(g, fps, None, &mut local);
        lib.delete_profile(fps);
        let grp = lib.group(g).unwrap();
        assert!(
            grp.default_profile.is_none() && grp.favorites.is_empty() && lib.selected.is_empty()
        );
    }

    #[test]
    fn disabled_group_bypasses() {
        let mut lib = Library::with_presets();
        let local = LocalState::new(MachineId("m".into()));
        let g = lib.ensure_device(member("m", "d", "D"));
        lib.group_mut(g).unwrap().enabled = false;
        let (_, p) = lib.render(
            &key("m", "d"),
            Platform::Mac,
            &[],
            &local,
            &RenderContext::default(),
        );
        assert!(p.bypass);
    }

    #[test]
    fn save_load_roundtrip_and_version_guard() {
        let dir = std::env::temp_dir().join(format!("myowneq-test-{}", crate::new_id()));
        let path = dir.join("library.json");
        let lib = Library::with_presets();
        lib.save(&path).unwrap();
        assert_eq!(Library::load(&path).unwrap(), lib);
        let future = lib
            .to_json()
            .unwrap()
            .replacen("\"schema\": 1", "\"schema\": 99", 1);
        assert!(matches!(
            Library::from_json(&future),
            Err(LibraryError::TooNew { found: 99 })
        ));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn connected_favorites_filters_disconnected() {
        let mut lib = Library::with_presets();
        let a = lib.ensure_device(member("m", "a", "A"));
        let _b = lib.ensure_device(member("m", "b", "B"));
        let fps = named(&lib, "FPS");
        lib.group_mut(a).unwrap().favorites.push(fps);
        let connected = [key("m", "a")];
        let v: Vec<_> = lib.connected_favorites(&connected).collect();
        assert_eq!(v.len(), 1);
        assert_eq!(v[0].1[0].name, "FPS");
    }
}
