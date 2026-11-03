//! AutoEQ download + cache. The index (~850 KB) is fetched on first search and
//! refreshed weekly in the background; picked results are cached forever, so
//! everything keeps working offline once used.
//!
//! The UI never blocks: commands start a background download and answer
//! `"loading"`; the UI asks again a moment later.

use myowneq_model::autoeq::{self, Entry};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

const INDEX_MAX: u64 = 8 * 1024 * 1024;
const FILE_MAX: u64 = 64 * 1024;
const REFRESH_AFTER: Duration = Duration::from_secs(7 * 24 * 3600);

#[derive(Clone, Debug, PartialEq)]
pub enum Job {
    Loading,
    Failed(String),
}

#[derive(Default)]
struct Shared {
    entries: Option<Arc<Vec<Entry>>>,
    /// When the cached index was downloaded.
    fetched: Option<SystemTime>,
    index_job: Option<Job>,
    files: HashMap<String, Job>,
}

pub struct AutoEq {
    dir: Option<PathBuf>,
    shared: Arc<Mutex<Shared>>,
    loaded_disk: bool,
}

pub enum Fetch {
    Ready(String),
    Loading,
    Failed(String),
}

impl AutoEq {
    /// `dir`: cache folder (`<data>/autoeq`); `None` = memory only (tests).
    pub fn new(dir: Option<PathBuf>) -> AutoEq {
        AutoEq {
            dir,
            shared: Arc::default(),
            loaded_disk: false,
        }
    }

    /// Tests: use this index text as if it was downloaded just now.
    #[cfg(test)]
    pub fn seed(&self, index: &str) {
        let mut s = self.shared.lock().expect("autoeq");
        s.entries = Some(Arc::new(autoeq::parse_index(index)));
        s.fetched = Some(SystemTime::now());
    }

    fn index_path(&self) -> Option<PathBuf> {
        self.dir.as_ref().map(|d| d.join("INDEX.md"))
    }

    #[cfg(test)]
    pub fn cache_file(&self, path: &str) -> Option<PathBuf> {
        self.file_path(path)
    }

    fn file_path(&self, path: &str) -> Option<PathBuf> {
        self.dir
            .as_ref()
            .map(|d| d.join("eq").join(format!("{:016x}.txt", fnv1a(path))))
    }

    /// Current entries (if any) + whether a download is running/failed.
    /// Loads the disk cache once, and starts a download when missing or stale.
    pub fn index(&mut self) -> (Option<Arc<Vec<Entry>>>, Option<Job>, Option<SystemTime>) {
        if !self.loaded_disk {
            self.loaded_disk = true;
            if let Some(p) = self.index_path() {
                if let Ok(text) = std::fs::read_to_string(&p) {
                    let mut s = self.shared.lock().expect("autoeq");
                    if s.entries.is_none() {
                        s.entries = Some(Arc::new(autoeq::parse_index(&text)));
                        s.fetched = std::fs::metadata(&p).and_then(|m| m.modified()).ok();
                    }
                }
            }
        }
        let stale = {
            let s = self.shared.lock().expect("autoeq");
            s.index_job.is_none()
                && (s.entries.is_none()
                    || s.fetched
                        .and_then(|t| t.elapsed().ok())
                        .is_none_or(|age| age > REFRESH_AFTER))
        };
        if stale {
            self.refresh();
        }
        let s = self.shared.lock().expect("autoeq");
        (s.entries.clone(), s.index_job.clone(), s.fetched)
    }

    /// Download the index again (background). No-op if already downloading.
    pub fn refresh(&self) {
        {
            let mut s = self.shared.lock().expect("autoeq");
            if s.index_job == Some(Job::Loading) {
                return;
            }
            s.index_job = Some(Job::Loading);
        }
        let (shared, path) = (self.shared.clone(), self.index_path());
        std::thread::spawn(move || {
            let result = get(autoeq::INDEX_URL, INDEX_MAX).and_then(|text| {
                let entries = autoeq::parse_index(&text);
                if entries.len() < 100 {
                    return Err("the AutoEQ index looks broken".into());
                }
                if let Some(p) = &path {
                    let _ = write_atomic(p, &text);
                }
                Ok(entries)
            });
            let mut s = shared.lock().expect("autoeq");
            match result {
                Ok(entries) => {
                    s.entries = Some(Arc::new(entries));
                    s.fetched = Some(SystemTime::now());
                    s.index_job = None;
                }
                Err(e) => s.index_job = Some(Job::Failed(e)),
            }
        });
    }

    /// The `ParametricEQ.txt` of a result. `path` must be in the index.
    pub fn fetch(&mut self, path: &str) -> Fetch {
        let known = self
            .index()
            .0
            .is_some_and(|e| e.iter().any(|x| x.path == path));
        if !known || !autoeq::valid_path(path) {
            return Fetch::Failed("not in the AutoEQ list".into());
        }
        if let Some(text) = self
            .file_path(path)
            .and_then(|p| std::fs::read_to_string(p).ok())
        {
            self.shared.lock().expect("autoeq").files.remove(path);
            return Fetch::Ready(text);
        }
        let mut s = self.shared.lock().expect("autoeq");
        match s.files.get(path) {
            Some(Job::Loading) => return Fetch::Loading,
            Some(Job::Failed(e)) => {
                let e = e.clone();
                s.files.remove(path); // next ask retries
                return Fetch::Failed(e);
            }
            None => {}
        }
        s.files.insert(path.to_string(), Job::Loading);
        drop(s);
        let (shared, key, file) = (self.shared.clone(), path.to_string(), self.file_path(path));
        let mem_only = file.is_none();
        std::thread::spawn(move || {
            let result = get(&autoeq::file_url(&key), FILE_MAX).and_then(|text| {
                if !text.contains("Filter") {
                    return Err("unexpected file from AutoEQ".into());
                }
                Ok(text)
            });
            let mut s = shared.lock().expect("autoeq");
            match (result, &file) {
                (Ok(text), Some(f)) if write_atomic(f, &text).is_ok() => {
                    s.files.remove(&key);
                }
                (Ok(_), _) if mem_only => {
                    s.files.insert(key, Job::Failed("no cache folder".into()));
                }
                (Ok(_), _) => {
                    s.files
                        .insert(key, Job::Failed("couldn't save the file".into()));
                }
                (Err(e), _) => {
                    s.files.insert(key, Job::Failed(e));
                }
            }
        });
        Fetch::Loading
    }
}

fn get(url: &str, max: u64) -> Result<String, String> {
    use std::io::Read;
    let tls = native_tls::TlsConnector::new().map_err(|e| e.to_string())?;
    let agent = ureq::AgentBuilder::new()
        .tls_connector(Arc::new(tls))
        .timeout_connect(Duration::from_secs(10))
        .timeout_read(Duration::from_secs(30))
        .user_agent(concat!("MyOwnEQ/", env!("CARGO_PKG_VERSION")))
        .build();
    let resp = agent.get(url).call().map_err(|e| match e {
        ureq::Error::Status(code, _) => format!("AutoEQ answered {code}"),
        ureq::Error::Transport(_) => "no connection to AutoEQ (GitHub)".to_string(),
    })?;
    let mut text = String::new();
    resp.into_reader()
        .take(max + 1)
        .read_to_string(&mut text)
        .map_err(|e| e.to_string())?;
    if text.len() as u64 > max {
        return Err("file too large".into());
    }
    Ok(text)
}

fn write_atomic(path: &Path, text: &str) -> std::io::Result<()> {
    if let Some(d) = path.parent() {
        std::fs::create_dir_all(d)?;
    }
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, text)?;
    std::fs::rename(&tmp, path)
}

/// Stable file name for a cached result.
fn fnv1a(s: &str) -> u64 {
    s.bytes().fold(0xcbf29ce484222325, |h, b| {
        (h ^ b as u64).wrapping_mul(0x100000001b3)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Talks to GitHub: `cargo test -p myowneq-ffi autoeq_live -- --ignored`.
    #[test]
    #[ignore]
    fn autoeq_live() {
        let dir = std::env::temp_dir().join(format!("meq-autoeq-live-{}", std::process::id()));
        let mut a = AutoEq::new(Some(dir.clone()));
        let deadline = std::time::Instant::now() + Duration::from_secs(60);
        let entries = loop {
            if let (Some(e), _, _) = a.index() {
                break e;
            }
            assert!(std::time::Instant::now() < deadline, "index timeout");
            std::thread::sleep(Duration::from_millis(200));
        };
        assert!(entries.len() > 5000, "{} entries", entries.len());
        for q in [
            "hd 600",
            "wh-1000xm4",
            "zsn pro",
            "airpods pro",
            "momentum 4",
        ] {
            let r = autoeq::search(&entries, q, 3);
            eprintln!(
                "{q:>12} → {:?}",
                r.iter()
                    .map(|m| (&m.name, m.measurements.len()))
                    .collect::<Vec<_>>()
            );
        }
        let path = autoeq::search(&entries, "hd 600", 1)[0].measurements[0]
            .path
            .clone();
        let text = loop {
            match a.fetch(&path) {
                Fetch::Ready(t) => break t,
                Fetch::Failed(e) => panic!("{e}"),
                Fetch::Loading => std::thread::sleep(Duration::from_millis(200)),
            }
        };
        assert!(text.contains("Filter 1:"), "{text}");
        let _ = std::fs::remove_dir_all(dir);
    }
}
