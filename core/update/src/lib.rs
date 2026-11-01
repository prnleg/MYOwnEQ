//! Signed self-update.
//!
//! Every GitHub release carries `latest.json` (an [`Manifest`]) plus one
//! artifact per platform. Each artifact is signed with an ed25519 key held
//! only in CI; apps embed the public key ([`PUBLIC_KEY`]) and refuse anything
//! whose signature doesn't verify, so a compromised download host can't push
//! code. Versions <= the running one are ignored (no downgrades).

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use ed25519_dalek::{Signature, VerifyingKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// Base64 ed25519 public key that release artifacts are signed with.
/// Replace via `cargo run -p myowneq-release -- keygen` (see RELEASING.md).
pub const PUBLIC_KEY: &str = include_str!("../update_public_key.txt");

pub const MANIFEST_URL: &str =
    "https://github.com/prnleg/MYOwnEQ/releases/latest/download/latest.json";

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Artifact {
    pub url: String,
    pub sha256: String,
    /// Base64 ed25519 signature of the artifact bytes.
    pub sig: String,
    pub size: u64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Manifest {
    pub version: String,
    pub notes_url: String,
    pub mac: Option<Artifact>,
    pub windows: Option<Artifact>,
}

#[derive(Debug, thiserror::Error)]
pub enum UpdateError {
    #[error("network: {0}")]
    Net(String),
    #[error("bad manifest: {0}")]
    Manifest(String),
    #[error("download corrupted (sha256 mismatch)")]
    Hash,
    #[error("signature check failed: this download is NOT from MyOwnEQ")]
    Signature,
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
    #[error("no public key configured")]
    NoKey,
}

/// Parse "v1.2.3" / "1.2.3" (pre-release suffix ignored) into a comparable tuple.
pub fn parse_version(v: &str) -> Option<(u64, u64, u64)> {
    let v = v.trim().trim_start_matches('v');
    let core = v.split(['-', '+']).next()?;
    let mut it = core.split('.').map(|p| p.parse::<u64>().ok());
    Some((
        it.next()??,
        it.next().flatten().unwrap_or(0),
        it.next().flatten().unwrap_or(0),
    ))
}

pub fn is_newer(candidate: &str, current: &str) -> bool {
    match (parse_version(candidate), parse_version(current)) {
        (Some(a), Some(b)) => a > b,
        _ => false,
    }
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// Verify `sig_b64` over `data` with base64 public key `pk_b64`.
pub fn verify(pk_b64: &str, data: &[u8], sig_b64: &str) -> Result<(), UpdateError> {
    let pk: [u8; 32] = B64
        .decode(pk_b64.trim())
        .ok()
        .and_then(|v| v.try_into().ok())
        .ok_or(UpdateError::NoKey)?;
    let key = VerifyingKey::from_bytes(&pk).map_err(|_| UpdateError::NoKey)?;
    let sig: [u8; 64] = B64
        .decode(sig_b64.trim())
        .ok()
        .and_then(|v| v.try_into().ok())
        .ok_or(UpdateError::Signature)?;
    key.verify_strict(data, &Signature::from_bytes(&sig))
        .map_err(|_| UpdateError::Signature)
}

/// Full check of a downloaded artifact: size, hash and signature.
pub fn check_artifact(pk_b64: &str, a: &Artifact, data: &[u8]) -> Result<(), UpdateError> {
    if data.len() as u64 != a.size || !sha256_hex(data).eq_ignore_ascii_case(&a.sha256) {
        return Err(UpdateError::Hash);
    }
    verify(pk_b64, data, &a.sig)
}

fn agent() -> Result<ureq::Agent, UpdateError> {
    let tls = native_tls::TlsConnector::new().map_err(|e| UpdateError::Net(e.to_string()))?;
    Ok(ureq::AgentBuilder::new()
        .tls_connector(Arc::new(tls))
        .timeout_connect(Duration::from_secs(15))
        .timeout_read(Duration::from_secs(60))
        .user_agent(concat!("MyOwnEQ-updater/", env!("CARGO_PKG_VERSION")))
        .build())
}

pub fn fetch_manifest(url: &str) -> Result<Manifest, UpdateError> {
    let body = agent()?
        .get(url)
        .call()
        .map_err(|e| UpdateError::Net(e.to_string()))?
        .into_string()
        .map_err(|e| UpdateError::Net(e.to_string()))?;
    serde_json::from_str(&body).map_err(|e| UpdateError::Manifest(e.to_string()))
}

/// Download, verify, and store the artifact in `dir`. Returns the file path.
pub fn download(a: &Artifact, dir: &Path, pk_b64: &str) -> Result<PathBuf, UpdateError> {
    const MAX: u64 = 512 * 1024 * 1024;
    if a.size > MAX {
        return Err(UpdateError::Manifest("artifact too large".into()));
    }
    let resp = agent()?
        .get(&a.url)
        .call()
        .map_err(|e| UpdateError::Net(e.to_string()))?;
    let mut data = Vec::with_capacity(a.size as usize);
    resp.into_reader().take(MAX).read_to_end(&mut data)?;
    check_artifact(pk_b64, a, &data)?;
    std::fs::create_dir_all(dir)?;
    let name = a
        .url
        .rsplit('/')
        .next()
        .filter(|n| !n.is_empty() && !n.contains(".."))
        .unwrap_or("update.bin");
    let path = dir.join(name);
    let tmp = path.with_extension("part");
    std::fs::write(&tmp, &data)?;
    std::fs::rename(&tmp, &path)?;
    Ok(path)
}

// ------------------------------------------------------------------ background updater

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Platform {
    Mac,
    Windows,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum Status {
    Idle,
    Checking,
    UpToDate {
        checked_at: u64,
    },
    Downloading {
        version: String,
    },
    /// Verified file on disk, applied on next quit/restart.
    Ready {
        version: String,
        file: PathBuf,
        notes_url: String,
    },
    Error {
        message: String,
    },
}

/// Persisted "an update is downloaded and verified" record.
#[derive(Clone, Debug, Serialize, Deserialize)]
struct Pending {
    version: String,
    file: PathBuf,
    notes_url: String,
    artifact: Artifact,
}

pub struct Updater {
    pub current: String,
    pub platform: Platform,
    dir: PathBuf,
    manifest_url: String,
    public_key: String,
    status: Arc<Mutex<Status>>,
    busy: Arc<Mutex<bool>>,
}

fn now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}

impl Updater {
    pub fn new(current: &str, platform: Platform, dir: PathBuf) -> Updater {
        let u = Updater {
            current: current.into(),
            platform,
            dir,
            manifest_url: MANIFEST_URL.into(),
            public_key: PUBLIC_KEY.trim().into(),
            status: Arc::new(Mutex::new(Status::Idle)),
            busy: Arc::new(Mutex::new(false)),
        };
        u.restore_pending();
        u
    }

    /// For tests / staging.
    pub fn with_endpoint(mut self, manifest_url: &str, public_key: &str) -> Updater {
        self.manifest_url = manifest_url.into();
        self.public_key = public_key.into();
        self
    }

    pub fn status(&self) -> Status {
        self.status
            .lock()
            .map(|s| s.clone())
            .unwrap_or(Status::Idle)
    }

    fn pending_path(dir: &Path) -> PathBuf {
        dir.join("pending.json")
    }

    /// A verified download from a previous session survives restarts; drop it
    /// if it's no longer newer (we were updated another way) or fails re-verification.
    fn restore_pending(&self) {
        let Some(p) = std::fs::read_to_string(Self::pending_path(&self.dir))
            .ok()
            .and_then(|s| serde_json::from_str::<Pending>(&s).ok())
        else {
            return;
        };
        // We already tried to install this exact version and are still on the
        // old one: the install failed. Don't retry forever on every launch.
        let attempted = std::fs::read_to_string(self.dir.join("attempt.txt")).ok();
        if attempted.as_deref() == Some(p.version.as_str()) && is_newer(&p.version, &self.current) {
            let _ = std::fs::remove_dir_all(&self.dir);
            *self.status.lock().expect("status") = Status::Error {
                message: format!(
                    "Installing {} failed. Download it from GitHub Releases.",
                    p.version
                ),
            };
            return;
        }
        let ok = is_newer(&p.version, &self.current)
            && std::fs::read(&p.file)
                .is_ok_and(|d| check_artifact(&self.public_key, &p.artifact, &d).is_ok());
        if ok {
            *self.status.lock().expect("status") = Status::Ready {
                version: p.version,
                file: p.file,
                notes_url: p.notes_url,
            };
        } else {
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    /// Check (and download if newer) on a background thread.
    pub fn check_async(&self) {
        {
            let mut b = self.busy.lock().expect("busy");
            if *b || matches!(self.status(), Status::Ready { .. }) {
                return;
            }
            *b = true;
        }
        // Visible immediately: the reply to "check now" already says "checking".
        *self.status.lock().expect("status") = Status::Checking;
        let (status, busy) = (self.status.clone(), self.busy.clone());
        let (url, pk, cur, plat, dir) = (
            self.manifest_url.clone(),
            self.public_key.clone(),
            self.current.clone(),
            self.platform,
            self.dir.clone(),
        );
        std::thread::spawn(move || {
            let set = |s: Status| *status.lock().expect("status") = s;
            set(Status::Checking);
            let result = (|| -> Result<Status, UpdateError> {
                let m = fetch_manifest(&url)?;
                if !is_newer(&m.version, &cur) {
                    return Ok(Status::UpToDate { checked_at: now() });
                }
                let art = match plat {
                    Platform::Mac => m.mac.clone(),
                    Platform::Windows => m.windows.clone(),
                }
                .ok_or_else(|| UpdateError::Manifest("no build for this platform".into()))?;
                set(Status::Downloading {
                    version: m.version.clone(),
                });
                let _ = std::fs::remove_dir_all(&dir);
                let file = download(&art, &dir, &pk)?;
                let pending = Pending {
                    version: m.version.clone(),
                    file: file.clone(),
                    notes_url: m.notes_url.clone(),
                    artifact: art,
                };
                std::fs::write(
                    Self::pending_path(&dir),
                    serde_json::to_string(&pending).unwrap_or_default(),
                )?;
                Ok(Status::Ready {
                    version: m.version,
                    file,
                    notes_url: m.notes_url,
                })
            })();
            set(result.unwrap_or_else(|e| Status::Error {
                message: e.to_string(),
            }));
            *busy.lock().expect("busy") = false;
        });
    }

    /// Launch the installer for a ready update. The caller must quit right
    /// after this returns Ok. `app_path` = the running .app bundle (macOS).
    pub fn apply(&self, app_path: Option<&Path>) -> Result<(), UpdateError> {
        let Status::Ready { file, .. } = self.status() else {
            return Err(UpdateError::Manifest("no update ready".into()));
        };
        // Re-verify right before running it.
        let pending: Pending =
            serde_json::from_str(&std::fs::read_to_string(Self::pending_path(&self.dir))?)
                .map_err(|e| UpdateError::Manifest(e.to_string()))?;
        check_artifact(&self.public_key, &pending.artifact, &std::fs::read(&file)?)?;
        std::fs::write(self.dir.join("attempt.txt"), &pending.version)?;
        match self.platform {
            Platform::Windows => {
                std::process::Command::new(&file)
                    .args([
                        "/VERYSILENT",
                        "/SUPPRESSMSGBOXES",
                        "/NORESTART",
                        "/RELAUNCH=1",
                    ])
                    .spawn()?;
            }
            Platform::Mac => {
                let app =
                    app_path.ok_or_else(|| UpdateError::Manifest("app path required".into()))?;
                // Wait for us to exit, unpack, swap bundles (keeping the old one
                // until the new one is in place), clear quarantine, relaunch.
                let script = r#"
                    pid="$1"; zip="$2"; app="$3"; dir="$4"
                    while kill -0 "$pid" 2>/dev/null; do sleep 0.2; done
                    tmp="$(mktemp -d)"
                    if ditto -x -k "$zip" "$tmp" && [ -d "$tmp/MyOwnEQ.app" ]; then
                        rm -rf "$app.old"
                        if mv "$app" "$app.old" && mv "$tmp/MyOwnEQ.app" "$app"; then
                            rm -rf "$app.old" "$dir"
                        else
                            [ -d "$app" ] || mv "$app.old" "$app"
                        fi
                    fi
                    xattr -dr com.apple.quarantine "$app" 2>/dev/null
                    rm -rf "$tmp"
                    open "$app"
                "#;
                std::process::Command::new("/bin/sh")
                    .arg("-c")
                    .arg(script)
                    .arg("myowneq-update")
                    .arg(std::process::id().to_string())
                    .arg(&file)
                    .arg(app)
                    .arg(&self.dir)
                    .stdin(std::process::Stdio::null())
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .spawn()?;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::{Signer, SigningKey};

    #[test]
    fn versions() {
        assert!(is_newer("v0.2.0", "0.1.9"));
        assert!(is_newer("1.0", "0.9.9"));
        assert!(!is_newer("0.1.0", "0.1.0"));
        assert!(!is_newer("0.1.0", "v0.2.0"));
        assert!(!is_newer("garbage", "0.1.0"));
        assert_eq!(parse_version("v1.2.3-beta.1"), Some((1, 2, 3)));
    }

    #[test]
    fn signature_roundtrip_and_tamper() {
        let sk = SigningKey::from_bytes(&[7u8; 32]);
        let pk = B64.encode(sk.verifying_key().to_bytes());
        let data = b"MyOwnEQ installer bytes";
        let sig = B64.encode(sk.sign(data).to_bytes());
        let art = Artifact {
            url: "x".into(),
            sha256: sha256_hex(data),
            sig: sig.clone(),
            size: data.len() as u64,
        };
        assert!(check_artifact(&pk, &art, data).is_ok());
        // Tampered bytes with matching hash/size but wrong signature.
        let evil = b"MyOwnEQ installer bytez";
        let art2 = Artifact {
            sha256: sha256_hex(evil),
            ..art.clone()
        };
        assert!(matches!(
            check_artifact(&pk, &art2, evil),
            Err(UpdateError::Signature)
        ));
        // Wrong hash.
        assert!(matches!(
            check_artifact(&pk, &art, evil),
            Err(UpdateError::Hash)
        ));
        // Other key.
        let other = B64.encode(
            SigningKey::from_bytes(&[9u8; 32])
                .verifying_key()
                .to_bytes(),
        );
        assert!(matches!(
            check_artifact(&other, &art, data),
            Err(UpdateError::Signature)
        ));
    }

    #[test]
    fn embedded_key_is_valid() {
        let pk: Vec<u8> = B64.decode(PUBLIC_KEY.trim()).expect("base64");
        assert_eq!(pk.len(), 32);
        assert!(VerifyingKey::from_bytes(&pk.try_into().unwrap()).is_ok());
    }
}
