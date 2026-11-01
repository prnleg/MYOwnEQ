//! One plugin instance = one output. Equalizer APO creates it inside the Windows
//! audio service; it runs MyOwnEQ's DSP (EQ + lookahead limiter + boost).
//!
//! - Which output: set through the plugin chunk (APO's `ChunkData`).
//! - Parameters: a watcher thread polls `engine.json` and pushes them into the
//!   processor through the lock-free channel (no APO config reload needed).
//! - Heartbeat: the watcher writes `engine-status-<output>.json` so the app knows
//!   the engine is really processing audio.

use crate::schema::EngineFile;
use myowneq_dsp::{param_channel, EqParams, ParamReceiver, Processor, MAX_CHANNELS};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Folder shared with the app: `%ProgramData%\MyOwnEQ` (override: `MYOWNEQ_ENGINE_DIR`).
pub fn engine_dir() -> PathBuf {
    if let Ok(d) = std::env::var("MYOWNEQ_ENGINE_DIR") {
        if !d.is_empty() {
            return PathBuf::from(d);
        }
    }
    let base = std::env::var("ProgramData").unwrap_or_else(|_| {
        if cfg!(windows) {
            r"C:\ProgramData".into()
        } else {
            std::env::temp_dir().to_string_lossy().into_owned()
        }
    });
    PathBuf::from(base).join("MyOwnEQ")
}

/// File-name-safe form of an output id ("{guid}" → "guid").
pub fn status_name(device: &str) -> String {
    let id: String = device
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || *c == '-')
        .collect();
    format!(
        "engine-status-{}.json",
        if id.is_empty() { "unknown".into() } else { id }
    )
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_millis() as u64)
}

struct Watcher {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Drop for Watcher {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

pub struct Plugin {
    device: Arc<Mutex<String>>,
    proc: Processor,
    rx: ParamReceiver,
    sender: Arc<Mutex<myowneq_dsp::ParamSender>>,
    /// Interleaved work buffer, sized from the host's max block size.
    scratch: Vec<f32>,
    channels: usize,
    sample_rate: f64,
    last_process: Arc<AtomicU64>,
    dir: PathBuf,
    watcher: Option<Watcher>,
}

/// Until parameters arrive: pass audio through untouched.
fn passthrough() -> EqParams {
    EqParams {
        bypass: true,
        limiter: false,
        ..EqParams::default()
    }
}

impl Plugin {
    pub fn new(channels: usize, dir: PathBuf) -> Plugin {
        let (tx, rx) = param_channel();
        let mut p = Plugin {
            device: Arc::new(Mutex::new(String::new())),
            proc: Processor::new(48_000.0, passthrough()),
            rx,
            sender: Arc::new(Mutex::new(tx)),
            scratch: Vec::new(),
            channels: channels.clamp(1, MAX_CHANNELS),
            sample_rate: 48_000.0,
            last_process: Arc::new(AtomicU64::new(0)),
            dir,
            watcher: None,
        };
        p.set_block_size(1024);
        p
    }

    pub fn device(&self) -> String {
        self.device.lock().map(|d| d.clone()).unwrap_or_default()
    }

    /// Which output this instance serves; (re)starts the parameter watcher.
    pub fn set_device(&mut self, id: &str) {
        if let Ok(mut d) = self.device.lock() {
            *d = id.trim().to_string();
        }
        self.start_watcher();
    }

    pub fn set_sample_rate(&mut self, sr: f64) {
        if sr.is_finite() && sr > 1000.0 && (sr - self.sample_rate).abs() > 0.5 {
            self.sample_rate = sr;
            self.proc.set_sample_rate(sr);
        }
    }

    /// Not RT: allocate the work buffer for the largest block the host will send.
    pub fn set_block_size(&mut self, frames: usize) {
        let n = frames.clamp(16, 1 << 16) * self.channels;
        if self.scratch.len() < n {
            self.scratch = vec![0.0; n];
        }
    }

    /// Planar in → planar out (they may alias). RT-safe: no allocation, no locks.
    ///
    /// # Safety
    /// `inputs`/`outputs` must each point to `channels` valid pointers of `frames` floats.
    pub unsafe fn process(
        &mut self,
        inputs: *const *const f32,
        outputs: *mut *mut f32,
        frames: usize,
    ) {
        if let Some(p) = self.rx.latest() {
            self.proc.set_params(&p);
        }
        let ch = self.channels;
        let cap = self.scratch.len() / ch;
        let mut done = 0;
        while done < frames {
            let n = (frames - done).min(cap);
            for c in 0..ch {
                let src = *inputs.add(c);
                for i in 0..n {
                    self.scratch[i * ch + c] = *src.add(done + i);
                }
            }
            self.proc.process(&mut self.scratch[..n * ch], ch);
            for c in 0..ch {
                let dst = *outputs.add(c);
                for i in 0..n {
                    *dst.add(done + i) = self.scratch[i * ch + c];
                }
            }
            done += n;
        }
        self.last_process.store(now_ms(), Ordering::Relaxed);
    }

    fn start_watcher(&mut self) {
        self.watcher = None; // joins the old one
        let stop = Arc::new(AtomicBool::new(false));
        let (stop2, device, sender, last, dir) = (
            stop.clone(),
            self.device.clone(),
            self.sender.clone(),
            self.last_process.clone(),
            self.dir.clone(),
        );
        let sr = self.sample_rate;
        let thread = std::thread::Builder::new()
            .name("myowneq-engine-watch".into())
            .spawn(move || watch(stop2, device, sender, last, dir, sr))
            .ok();
        self.watcher = Some(Watcher { stop, thread });
    }

    /// Read the parameter file once right now (tests and first load).
    pub fn reload_now(&mut self) -> bool {
        let id = self.device();
        match load(&self.dir, &id) {
            Some(p) => {
                self.proc.set_params(&p);
                true
            }
            None => false,
        }
    }
}

fn load(dir: &std::path::Path, device: &str) -> Option<EqParams> {
    let text = std::fs::read_to_string(dir.join("engine.json")).ok()?;
    let f = EngineFile::parse(&text)?;
    Some(
        f.devices
            .get(device)
            .map_or_else(passthrough, |d| d.to_params()),
    )
}

fn watch(
    stop: Arc<AtomicBool>,
    device: Arc<Mutex<String>>,
    sender: Arc<Mutex<myowneq_dsp::ParamSender>>,
    last_process: Arc<AtomicU64>,
    dir: PathBuf,
    sample_rate: f64,
) {
    // Compare contents, not mtimes: two quick writes (hold-to-listen press and
    // release) can share a timestamp on coarse file systems. The file is tiny.
    let file = dir.join("engine.json");
    let mut seen: Option<String> = None;
    let mut last_beat = 0u64;
    while !stop.load(Ordering::Relaxed) {
        let id = device.lock().map(|d| d.clone()).unwrap_or_default();
        if let Ok(text) = std::fs::read_to_string(&file) {
            if seen.as_deref() != Some(text.as_str()) {
                if let Some(f) = EngineFile::parse(&text) {
                    let p = f
                        .devices
                        .get(&id)
                        .map_or_else(passthrough, |d| d.to_params());
                    if let Ok(mut s) = sender.lock() {
                        s.send(p);
                    }
                    seen = Some(text);
                }
            }
        }
        // Heartbeat once a second: "loaded" (written_ms) even in silence, and when
        // audio last went through (last_process_ms, 0 = not yet).
        let lp = last_process.load(Ordering::Relaxed);
        let now = now_ms();
        if !id.is_empty() && now.saturating_sub(last_beat) >= 1000 {
            last_beat = now;
            let beat = serde_json::json!({
                "device": id,
                "written_ms": now,
                "last_process_ms": lp,
                "sample_rate": sample_rate,
                "version": env!("CARGO_PKG_VERSION"),
            });
            let path = dir.join(status_name(&id));
            let tmp = path.with_extension("json.tmp");
            if std::fs::write(&tmp, beat.to_string()).is_ok() {
                let _ = std::fs::rename(&tmp, &path);
            }
        }
        std::thread::sleep(Duration::from_millis(50));
    }
}
