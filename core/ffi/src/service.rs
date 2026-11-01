//! The control-side service shared by native shells.
//!
//! Owns the library, machine-local state and the "current situation"
//! (output device, running apps, volume). After every change it re-resolves
//! which profile applies and pushes fresh [`EqParams`] to the audio side.

use crate::syncing::{SyncConfig, SyncGlue};
use myowneq_dsp::spectrum::{spectrum_channel, SpectrumAnalyzer, SpectrumTap};
use myowneq_dsp::{param_channel, EqParams, ParamReceiver, ParamSender, Processor};
use myowneq_model::device::{DeviceGroup, DeviceMember};
use myowneq_model::formats::{apo, eqmac};
use myowneq_model::library::LocalState;
use myowneq_model::profile::CorrectionSource;
use myowneq_model::rules::Rule;
use myowneq_model::{
    AppSnapshot, DeviceKey, DeviceKind, Id, Library, MachineId, Platform, Profile, RenderContext,
    Resolution,
};
use myowneq_sync::entity::snapshot;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::path::PathBuf;

// ---------------------------------------------------------------- audio side

/// Owned by the audio callback. Every method is RT-safe.
pub struct Audio {
    proc: Processor,
    rx: ParamReceiver,
    tap: SpectrumTap,
}

impl Audio {
    #[inline]
    pub fn process(&mut self, buf: &mut [f32], channels: usize) {
        if let Some(p) = self.rx.latest() {
            self.proc.set_params(&p);
        }
        self.proc.process(buf, channels);
        self.tap.push_interleaved(buf, channels);
    }

    /// Feed audio to the spectrum analyzer only (no processing). Used on
    /// Windows where Equalizer APO does the EQ and we just watch loopback.
    #[inline]
    pub fn analyze(&mut self, buf: &[f32], channels: usize) {
        self.tap.push_interleaved(buf, channels);
    }

    /// Not RT-safe with respect to a concurrent `process`; call while stopped.
    pub fn set_sample_rate(&mut self, sr: f64) {
        self.proc.set_sample_rate(sr);
    }
}

// ---------------------------------------------------------------- commands

#[derive(Deserialize)]
pub struct DeviceInfo {
    pub uid: String,
    pub name: String,
    #[serde(default)]
    pub kind: DeviceKind,
}

#[derive(Deserialize)]
pub struct AppInfo {
    pub id: String,
    #[serde(default)]
    pub focused: bool,
}

#[derive(Deserialize)]
#[serde(tag = "cmd", rename_all = "snake_case")]
pub enum Command {
    GetState,
    SetDevice {
        device: Option<DeviceInfo>,
    },
    SetConnected {
        devices: Vec<DeviceInfo>,
    },
    SetApps {
        apps: Vec<AppInfo>,
    },
    SetVolume {
        volume: f64,
    },
    SetEnabled {
        enabled: bool,
    },
    Select {
        group: Id,
        profile: Id,
    },
    UpsertProfile {
        profile: Profile,
    },
    DeleteProfile {
        id: Id,
    },
    /// Audition an unsaved profile (live editing). `null` ends the preview.
    Preview {
        profile: Option<Profile>,
    },
    UpdateGroup {
        group: DeviceGroup,
    },
    UpsertRule {
        rule: Rule,
    },
    DeleteRule {
        id: Id,
    },
    LinkGroups {
        into: Id,
        other: Id,
    },
    UnlinkDevice {
        key: DeviceKey,
    },
    ImportEqmac {
        path: Option<String>,
    },
    ImportApo {
        text: String,
        name: String,
        #[serde(default)]
        as_correction: bool,
    },
    ExportApo {
        profile: Id,
    },
    Curve {
        profile: Profile,
        #[serde(default = "default_points")]
        points: usize,
    },
    /// Equalizer APO include-file text for every connected device (Windows engine).
    ApoConfig,
    /// Change sync mode (`config` = SyncConfig JSON).
    SyncConfigure {
        config: SyncConfig,
    },
    /// Poll the network. Cheap; call a few times per second.
    SyncTick,
    /// Look for LAN hosts via mDNS (blocks for `timeout_ms`).
    SyncDiscover {
        #[serde(default = "default_discover_ms")]
        timeout_ms: u64,
    },
}

fn default_discover_ms() -> u64 {
    1500
}

impl Command {
    /// Commands that can never change the synced library (skip snapshotting).
    fn is_read_only(&self) -> bool {
        matches!(
            self,
            Command::GetState
                | Command::SetApps { .. }
                | Command::SetVolume { .. }
                | Command::SetEnabled { .. }
                | Command::Preview { .. }
                | Command::ExportApo { .. }
                | Command::Curve { .. }
                | Command::ApoConfig
                | Command::SyncConfigure { .. }
                | Command::SyncTick
                | Command::SyncDiscover { .. }
        )
    }
}

fn default_points() -> usize {
    256
}

#[derive(Serialize, Deserialize, Default)]
struct LocalFile {
    local: Option<LocalState>,
    #[serde(default = "yes")]
    enabled: bool,
}

fn yes() -> bool {
    true
}

// ---------------------------------------------------------------- core

pub struct Core {
    data_dir: Option<PathBuf>,
    lib: Library,
    local: LocalState,
    enabled: bool,
    device: Option<DeviceKey>,
    connected: Vec<DeviceKey>,
    apps: Vec<AppSnapshot>,
    volume: f64,
    sample_rate: f64,
    preview: Option<Profile>,
    resolution: Resolution,
    params: EqParams,
    sent: Option<EqParams>,
    sender: Option<ParamSender>,
    analyzer: Option<SpectrumAnalyzer>,
    warnings: Vec<String>,
    sync: SyncGlue,
}

impl Core {
    /// `data_dir = None` keeps everything in memory (tests).
    pub fn new(data_dir: Option<PathBuf>, machine: &str) -> Core {
        let mut warnings = Vec::new();
        let lib = data_dir
            .as_ref()
            .map(|d| d.join("library.json"))
            .filter(|p| p.exists())
            .and_then(|p| match Library::load(&p) {
                Ok(l) => Some(l),
                Err(e) => {
                    // Keep the broken file for inspection, start fresh.
                    let _ = std::fs::rename(&p, p.with_extension("json.broken"));
                    warnings.push(format!("library unreadable, started fresh: {e}"));
                    None
                }
            })
            .unwrap_or_else(Library::with_presets);
        let lf: LocalFile = data_dir
            .as_ref()
            .and_then(|d| std::fs::read_to_string(d.join("local.json")).ok())
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or(LocalFile {
                local: None,
                enabled: true,
            });
        let local = lf
            .local
            .filter(|l| l.machine.0 == machine)
            .unwrap_or_else(|| LocalState::new(MachineId(machine.into())));
        let sync = SyncGlue::load(data_dir.clone(), machine, &lib);
        let mut core = Core {
            data_dir,
            lib,
            local,
            enabled: lf.enabled,
            device: None,
            connected: Vec::new(),
            apps: Vec::new(),
            volume: 1.0,
            sample_rate: 48_000.0,
            preview: None,
            resolution: Resolution::Flat,
            params: EqParams::default(),
            sent: None,
            sender: None,
            analyzer: None,
            warnings,
            sync,
        };
        core.refresh();
        core
    }

    fn machine(&self) -> &MachineId {
        &self.local.machine
    }

    fn key(&self, uid: &str) -> DeviceKey {
        DeviceKey {
            machine: self.machine().clone(),
            uid: uid.into(),
        }
    }

    /// Create the audio-side handle for a (new) device at `sample_rate`.
    /// Replaces any previous handle's channels.
    pub fn make_audio(&mut self, sample_rate: f64) -> Audio {
        self.sample_rate = sample_rate;
        self.refresh();
        let (tx, rx) = param_channel();
        let (tap, analyzer) = spectrum_channel();
        self.sender = Some(tx);
        self.analyzer = Some(analyzer);
        self.sent = Some(self.params);
        Audio {
            proc: Processor::new(sample_rate, self.params),
            rx,
            tap,
        }
    }

    /// Fill `out` with spectrum bins (dBFS, 20 Hz..20 kHz log-spaced).
    pub fn spectrum(&mut self, out: &mut [f32]) -> bool {
        self.flush();
        match &mut self.analyzer {
            Some(a) => {
                a.analyze(self.sample_rate, out, 1.5);
                true
            }
            None => false,
        }
    }

    pub fn params(&self) -> &EqParams {
        &self.params
    }

    /// Recompute resolution + params and push them if they changed.
    fn refresh(&mut self) {
        let ctx = RenderContext {
            sample_rate: self.sample_rate,
            volume: self.volume,
        };
        let (res, mut params) = match &self.device {
            Some(dev) => self
                .lib
                .render(dev, Platform::current(), &self.apps, &self.local, &ctx),
            None => (Resolution::Flat, EqParams::default()),
        };
        if let Some(p) = &self.preview {
            let bypass = params.bypass;
            params = p.render(&ctx);
            params.bypass = bypass;
        }
        params.bypass |= !self.enabled;
        self.resolution = res;
        self.params = params;
        self.flush();
    }

    fn flush(&mut self) {
        if self.sent.as_ref() == Some(&self.params) {
            return;
        }
        if let Some(tx) = &mut self.sender {
            if tx.send(self.params) {
                self.sent = Some(self.params);
            }
        }
    }

    fn save_library(&mut self) {
        if let Some(d) = &self.data_dir {
            if let Err(e) = self.lib.save(&d.join("library.json")) {
                self.warnings.push(format!("save failed: {e}"));
            }
        }
    }

    fn save_local(&mut self) {
        if let Some(d) = &self.data_dir {
            let lf = LocalFile {
                local: Some(self.local.clone()),
                enabled: self.enabled,
            };
            let _ = std::fs::create_dir_all(d);
            if let Ok(s) = serde_json::to_string_pretty(&lf) {
                let _ = std::fs::write(d.join("local.json"), s);
            }
        }
    }

    fn register(&mut self, d: DeviceInfo) -> DeviceKey {
        let key = self.key(&d.uid);
        self.lib.ensure_device(DeviceMember {
            key: key.clone(),
            os_name: d.name,
            kind: d.kind,
        });
        key
    }

    /// Execute a JSON command, return a JSON reply.
    pub fn command(&mut self, input: &str) -> String {
        let reply = match serde_json::from_str::<Command>(input) {
            Ok(cmd) => {
                let before = (!cmd.is_read_only()).then(|| snapshot(&self.lib));
                let r = self.run(cmd);
                if let Some(b) = before {
                    self.sync.record(&b, &self.lib);
                }
                r.unwrap_or_else(|e| json!({ "ok": false, "error": e }))
            }
            Err(e) => json!({ "ok": false, "error": format!("bad command: {e}") }),
        };
        reply.to_string()
    }

    fn run(&mut self, cmd: Command) -> Result<Value, String> {
        use Command::*;
        match cmd {
            GetState => {}
            SetDevice { device } => {
                let before = self.lib.groups.len();
                self.device = device.map(|d| self.register(d));
                if self.lib.groups.len() != before {
                    self.save_library();
                }
            }
            SetConnected { devices } => {
                let before = self.lib.clone();
                self.connected = devices.into_iter().map(|d| self.register(d)).collect();
                if self.lib != before {
                    self.save_library();
                }
            }
            SetApps { apps } => {
                self.apps = apps
                    .into_iter()
                    .map(|a| AppSnapshot {
                        id: a.id,
                        focused: a.focused,
                    })
                    .collect();
                let before = self.resolution.clone();
                self.refresh();
                // Cheap reply for the frequent case.
                if before == self.resolution {
                    return Ok(json!({ "ok": true, "changed": false }));
                }
            }
            SetVolume { volume } => {
                self.volume = volume.clamp(0.0, 1.0);
                self.refresh();
                return Ok(json!({ "ok": true }));
            }
            SetEnabled { enabled } => {
                self.enabled = enabled;
                self.save_local();
            }
            Select { group, profile } => {
                if self.lib.profile(profile).is_none() {
                    return Err(format!("unknown profile {profile}"));
                }
                let active_rule = match &self.resolution {
                    Resolution::Rule { rule, .. } | Resolution::RuleOverride { rule, .. }
                        if self.current_group() == Some(group) =>
                    {
                        Some(*rule)
                    }
                    _ => None,
                };
                self.lib
                    .select(group, profile, active_rule, &mut self.local);
                self.save_library();
                self.save_local();
            }
            UpsertProfile { profile } => {
                self.lib.upsert_profile(profile);
                self.save_library();
            }
            DeleteProfile { id } => {
                self.lib.delete_profile(id);
                self.save_library();
            }
            Preview { profile } => {
                self.preview = profile;
                self.refresh();
                return Ok(json!({ "ok": true }));
            }
            UpdateGroup { group } => {
                let g = self.lib.group_mut(group.id).ok_or("unknown group")?;
                g.name = group.name;
                g.default_profile = group.default_profile;
                g.favorites = group.favorites;
                g.enabled = group.enabled;
                self.save_library();
            }
            UpsertRule { rule } => {
                match self.lib.rules.iter_mut().find(|r| r.id == rule.id) {
                    Some(r) => *r = rule,
                    None => self.lib.rules.push(rule),
                }
                self.save_library();
            }
            DeleteRule { id } => {
                self.lib.rules.retain(|r| r.id != id);
                self.save_library();
            }
            LinkGroups { into, other } => {
                self.lib
                    .link_groups(into, other)
                    .map_err(|e| e.to_string())?;
                self.save_library();
            }
            UnlinkDevice { key } => {
                self.lib.unlink_device(&key);
                self.save_library();
            }
            ImportEqmac { path } => {
                let path = path
                    .map(PathBuf::from)
                    .or_else(eqmac::default_plist_path)
                    .ok_or("no path")?;
                let profiles = eqmac::import_plist(&path).map_err(|e| e.to_string())?;
                let n = self.import(profiles);
                self.save_library();
                self.refresh();
                return Ok(self.state_with(json!({ "imported": n })));
            }
            ImportApo {
                text,
                name,
                as_correction,
            } => {
                let cfg = apo::parse(&text);
                let warnings = cfg.warnings.clone();
                let p = if as_correction {
                    cfg.into_correction(
                        name.clone(),
                        CorrectionSource {
                            provider: "AutoEQ".into(),
                            name,
                        },
                    )
                } else {
                    cfg.into_profile(name)
                };
                let id = p.id;
                self.lib.upsert_profile(p);
                self.save_library();
                self.refresh();
                return Ok(self.state_with(json!({ "imported_id": id, "warnings": warnings })));
            }
            ExportApo { profile } => {
                let p = self.lib.profile(profile).ok_or("unknown profile")?;
                let ctx = RenderContext {
                    sample_rate: self.sample_rate,
                    volume: 1.0,
                };
                let bands = p.bands(&ctx);
                let text = apo::write(&[apo::Section {
                    device: None,
                    preamp_db: p.preamp_db(&bands, ctx.sample_rate),
                    bands: &bands,
                    balance: p.balance,
                }]);
                return Ok(json!({ "ok": true, "text": text }));
            }
            Curve { profile, points } => return Ok(self.curve(&profile, points)),
            ApoConfig => return Ok(json!({ "ok": true, "text": self.apo_config() })),
            SyncConfigure { config } => self.sync.apply_config(config),
            SyncTick => {
                if !self.sync.tick(&mut self.lib) {
                    return Ok(
                        json!({ "ok": true, "changed": false, "sync": self.sync.status_json() }),
                    );
                }
                self.save_library();
            }
            SyncDiscover { timeout_ms } => {
                let found = myowneq_sync::discovery::browse(std::time::Duration::from_millis(
                    timeout_ms.min(5000),
                ));
                return Ok(json!({ "ok": true, "found": found }));
            }
        }
        self.refresh();
        Ok(self.state_with(json!({})))
    }

    /// Add imported profiles, replacing same-named imports from earlier runs.
    fn import(&mut self, profiles: Vec<Profile>) -> usize {
        let n = profiles.len();
        for mut p in profiles {
            if let Some(existing) = self
                .lib
                .profiles
                .iter()
                .find(|e| e.name == p.name && e.tags == p.tags)
            {
                p.id = existing.id;
            }
            self.lib.upsert_profile(p);
        }
        n
    }

    /// One `Device:` section per connected output, each with the profile its
    /// group resolves to right now (rules, selection, default, preview).
    fn apo_config(&self) -> String {
        let ctx = RenderContext {
            sample_rate: self.sample_rate,
            volume: self.volume,
        };
        let mut rendered = Vec::new();
        for key in &self.connected {
            let (_, mut params) =
                self.lib
                    .render(key, Platform::current(), &self.apps, &self.local, &ctx);
            if let Some(p) = self
                .preview
                .as_ref()
                .filter(|_| Some(key) == self.device.as_ref())
            {
                let bypass = params.bypass;
                params = p.render(&ctx);
                params.bypass = bypass;
            }
            params.bypass |= !self.enabled;
            rendered.push((apo::device_selector(&key.uid).to_string(), params));
        }
        let sections: Vec<apo::Section> = rendered
            .iter()
            .map(|(sel, p)| apo::Section {
                device: Some(sel),
                preamp_db: if p.bypass { 0.0 } else { p.preamp_db },
                bands: if p.bypass { &[] } else { p.active_bands() },
                balance: if p.bypass { 0.0 } else { p.balance },
            })
            .collect();
        apo::write(&sections)
    }

    fn current_group(&self) -> Option<Id> {
        self.device
            .as_ref()
            .and_then(|d| self.lib.group_for(d))
            .map(|g| g.id)
    }

    fn curve(&self, profile: &Profile, points: usize) -> Value {
        use myowneq_dsp::response::{band_response_db, log_grid, response_db};
        let ctx = RenderContext {
            sample_rate: self.sample_rate,
            volume: self.volume,
        };
        let bands = profile.bands(&ctx);
        let preamp = profile.preamp_db(&bands, ctx.sample_rate);
        let freqs = log_grid(
            20.0,
            20_000f64.min(ctx.sample_rate * 0.49),
            points.clamp(16, 2048),
        );
        let total = response_db(&bands, preamp, ctx.sample_rate, &freqs);
        let per_band: Vec<Vec<f64>> = profile
            .static_bands()
            .iter()
            .map(|b| band_response_db(b, ctx.sample_rate, &freqs))
            .collect();
        json!({ "ok": true, "freqs": freqs, "total": total, "preamp": preamp, "bands": per_band })
    }

    fn state_with(&self, extra: Value) -> Value {
        let mut v = self.state();
        if let (Value::Object(m), Value::Object(e)) = (&mut v, extra) {
            m.extend(e);
        }
        v
    }

    pub fn state(&self) -> Value {
        let (kind, profile, rule) = match &self.resolution {
            Resolution::Rule { rule, profile } => ("rule", Some(*profile), Some(*rule)),
            Resolution::RuleOverride { rule, profile } => {
                ("rule_override", Some(*profile), Some(*rule))
            }
            Resolution::Selected(p) => ("selected", Some(*p), None),
            Resolution::Default(p) => ("default", Some(*p), None),
            Resolution::Flat => ("flat", None, None),
        };
        let connected_groups: Vec<Id> = self
            .lib
            .groups
            .iter()
            .filter(|g| self.connected.iter().any(|k| g.contains(k)))
            .map(|g| g.id)
            .collect();
        json!({
            "ok": true,
            "machine": self.machine().0,
            "enabled": self.enabled,
            "device": self.device,
            "group": self.current_group(),
            "connected_groups": connected_groups,
            "resolution": { "kind": kind, "profile": profile, "rule": rule },
            "preamp_db": self.params.preamp_db,
            "sample_rate": self.sample_rate,
            "library": self.lib,
            "warnings": self.warnings,
            "sync": self.sync.status_json(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cmd(core: &mut Core, v: Value) -> Value {
        serde_json::from_str(&core.command(&v.to_string())).unwrap()
    }

    #[test]
    fn device_select_and_audio_params_flow() {
        let mut core = Core::new(None, "mac-1");
        let mut audio = core.make_audio(48_000.0);
        let s = cmd(
            &mut core,
            json!({"cmd":"set_device","device":{"uid":"jack","name":"External Headphones","kind":"analog"}}),
        );
        assert_eq!(s["resolution"]["kind"], "flat");
        let group = s["group"].as_str().unwrap().to_string();
        let bass = s["library"]["profiles"]
            .as_array()
            .unwrap()
            .iter()
            .find(|p| p["name"] == "Bass Boost")
            .unwrap()["id"]
            .as_str()
            .unwrap()
            .to_string();
        let s = cmd(
            &mut core,
            json!({"cmd":"select","group":group,"profile":bass}),
        );
        assert_eq!(s["resolution"]["kind"], "selected");
        assert!(core.params().band_count == 1);

        // Audio side picks up new params on next block.
        let mut buf = vec![0.0f32; 512];
        audio.process(&mut buf, 2);
        let mut bins = vec![0.0f32; 32];
        assert!(core.spectrum(&mut bins));
    }

    #[test]
    fn disable_sets_bypass() {
        let mut core = Core::new(None, "m");
        cmd(&mut core, json!({"cmd":"set_enabled","enabled":false}));
        assert!(core.params().bypass);
    }

    #[test]
    fn preview_overrides_until_cleared() {
        let mut core = Core::new(None, "m");
        cmd(
            &mut core,
            json!({"cmd":"set_device","device":{"uid":"d","name":"D"}}),
        );
        let mut p = Profile::new("tmp");
        p.macros.treble = 3.0;
        cmd(&mut core, json!({"cmd":"preview","profile":p}));
        assert_eq!(core.params().band_count, 1);
        cmd(&mut core, json!({"cmd":"preview","profile":null}));
        assert_eq!(core.params().band_count, 0);
    }

    #[test]
    fn bad_json_is_an_error_not_a_panic() {
        let mut core = Core::new(None, "m");
        let r = cmd(&mut core, json!({"cmd":"nope"}));
        assert_eq!(r["ok"], false);
    }

    #[test]
    fn persists_to_disk() {
        let dir = std::env::temp_dir().join(format!("meq-core-{}", uuid::Uuid::new_v4()));
        {
            let mut core = Core::new(Some(dir.clone()), "m");
            cmd(
                &mut core,
                json!({"cmd":"set_device","device":{"uid":"d","name":"D"}}),
            );
            cmd(&mut core, json!({"cmd":"set_enabled","enabled":false}));
        }
        let core = Core::new(Some(dir.clone()), "m");
        assert_eq!(core.lib.groups.len(), 1);
        assert!(!core.enabled);
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn curve_has_requested_points() {
        let mut core = Core::new(None, "m");
        let p = Profile::new("c");
        let r = cmd(&mut core, json!({"cmd":"curve","profile":p,"points":64}));
        assert_eq!(r["freqs"].as_array().unwrap().len(), 64);
    }
}
