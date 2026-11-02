//! Local control channel: lets other programs on this computer (the `myowneq` command
//! line, `myowneq://` links, Raycast, AutoHotkey, Stream Deck…) drive the app.
//!
//! - Listens on `127.0.0.1` (random port), never on the network.
//! - Every request carries a secret token from `control.json` in the user's data
//!   folder (owner-only on Unix). Other users and web pages can't read it.
//! - One JSON object per line in, one per line out. Requests are queued and run by
//!   the app's own timer (`control_tick`), on the same thread as every other command.

use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::thread::JoinHandle;
use std::time::Duration;

pub const FILE: &str = "control.json";
/// Longest request line accepted (a share code fits many times over).
const MAX_LINE: u64 = 64 * 1024;
/// How long a client waits for the app to answer (it ticks every ~250 ms).
const REPLY_TIMEOUT: Duration = Duration::from_secs(5);

pub struct Pending {
    pub request: Value,
    pub reply: mpsc::Sender<Value>,
}

pub struct ControlServer {
    pub port: u16,
    rx: mpsc::Receiver<Pending>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
    file: PathBuf,
}

impl ControlServer {
    /// Bind, write `control.json` and start accepting connections.
    pub fn start(dir: &Path) -> std::io::Result<ControlServer> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        listener.set_nonblocking(true)?;
        let port = listener.local_addr()?.port();
        let token = format!(
            "{}{}",
            uuid::Uuid::new_v4().simple(),
            uuid::Uuid::new_v4().simple()
        );
        let file = dir.join(FILE);
        write_private(
            &file,
            &json!({ "port": port, "token": token, "pid": std::process::id() }).to_string(),
        )?;
        let (tx, rx) = mpsc::channel();
        let stop = Arc::new(AtomicBool::new(false));
        let stop2 = stop.clone();
        let thread = std::thread::Builder::new()
            .name("myowneq-control".into())
            .spawn(move || accept_loop(listener, tx, token, stop2))?;
        Ok(ControlServer {
            port,
            rx,
            stop,
            thread: Some(thread),
            file,
        })
    }

    /// Requests waiting for the app (non-blocking).
    pub fn drain(&self) -> Vec<Pending> {
        self.rx.try_iter().collect()
    }
}

impl Drop for ControlServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
        let _ = std::fs::remove_file(&self.file);
    }
}

fn write_private(path: &Path, text: &str) -> std::io::Result<()> {
    if let Some(d) = path.parent() {
        std::fs::create_dir_all(d)?;
    }
    let tmp = path.with_extension("json.tmp");
    let mut opts = std::fs::OpenOptions::new();
    opts.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        opts.mode(0o600);
    }
    opts.open(&tmp)?.write_all(text.as_bytes())?;
    std::fs::rename(&tmp, path)
}

fn accept_loop(
    listener: TcpListener,
    tx: mpsc::Sender<Pending>,
    token: String,
    stop: Arc<AtomicBool>,
) {
    let token = Arc::new(token);
    while !stop.load(Ordering::Relaxed) {
        match listener.accept() {
            Ok((stream, _)) => {
                let (tx, token, stop) = (tx.clone(), token.clone(), stop.clone());
                let _ = std::thread::Builder::new()
                    .name("myowneq-control-conn".into())
                    .spawn(move || serve(stream, &tx, &token, &stop));
            }
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(Duration::from_millis(50));
            }
            Err(_) => std::thread::sleep(Duration::from_millis(200)),
        }
    }
}

fn serve(stream: TcpStream, tx: &mpsc::Sender<Pending>, token: &str, stop: &AtomicBool) {
    let _ = stream.set_nonblocking(false);
    let _ = stream.set_read_timeout(Some(Duration::from_secs(30)));
    let Ok(mut out) = stream.try_clone() else {
        return;
    };
    let mut reader = BufReader::new(stream);
    loop {
        let mut line = String::new();
        match (&mut reader).take(MAX_LINE).read_line(&mut line) {
            Ok(0) | Err(_) => return,
            Ok(_) => {}
        }
        if stop.load(Ordering::Relaxed) {
            return;
        }
        let reply = match serde_json::from_str::<Value>(line.trim()) {
            Err(_) => json!({ "ok": false, "error": "not JSON (one object per line)" }),
            Ok(req)
                if !same(
                    req.get("token").and_then(Value::as_str).unwrap_or(""),
                    token,
                ) =>
            {
                json!({ "ok": false, "error": "bad token (read it from control.json)" })
            }
            Ok(request) => {
                let (rtx, rrx) = mpsc::channel();
                if tx
                    .send(Pending {
                        request,
                        reply: rtx,
                    })
                    .is_err()
                {
                    return;
                }
                rrx.recv_timeout(REPLY_TIMEOUT).unwrap_or_else(
                    |_| json!({ "ok": false, "error": "MyOwnEQ didn't answer in time" }),
                )
            }
        };
        if writeln!(out, "{reply}").is_err() {
            return;
        }
    }
}

/// Token comparison that doesn't stop at the first different byte.
fn same(a: &str, b: &str) -> bool {
    a.len() == b.len()
        && a.bytes()
            .zip(b.bytes())
            .fold(0u8, |acc, (x, y)| acc | (x ^ y))
            == 0
}

/// `myowneq://select/Gaming%20Night` → `{"cmd":"select","profile":"Gaming Night"}`.
pub fn parse_url(url: &str) -> Result<Value, String> {
    let lower = url.to_ascii_lowercase();
    let rest = lower
        .strip_prefix("myowneq://")
        .or_else(|| lower.strip_prefix("myowneq:"))
        .ok_or("not a myowneq:// link")?;
    // Use the original (case-preserving) text after the scheme.
    let rest = &url[url.len() - rest.len()..];
    let rest = rest.trim_end_matches('/');
    let (action, arg) = match rest.split_once('/') {
        Some((a, b)) => (a, Some(percent_decode(b))),
        None => (rest, None),
    };
    let action = action.to_ascii_lowercase();
    Ok(match (action.as_str(), arg) {
        ("on" | "off" | "toggle" | "status", None) => json!({ "cmd": action }),
        ("select", Some(p)) if !p.is_empty() => json!({ "cmd": "select", "profile": p }),
        ("import", Some(code)) if !code.is_empty() => json!({ "cmd": "import", "text": code }),
        _ => return Err(format!("unknown link: {url}")),
    })
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        let hex = b
            .get(i + 1..i + 3)
            .and_then(|h| std::str::from_utf8(h).ok())
            .and_then(|h| u8::from_str_radix(h, 16).ok());
        match (b[i], hex) {
            (b'%', Some(v)) => {
                out.push(v);
                i += 3;
                continue;
            }
            (b'+', _) => out.push(b' '),
            (c, _) => out.push(c),
        }
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn links() {
        assert_eq!(
            parse_url("myowneq://select/Gaming%20Night").unwrap(),
            json!({"cmd":"select","profile":"Gaming Night"})
        );
        assert_eq!(
            parse_url("MyOwnEQ://Toggle/").unwrap(),
            json!({"cmd":"toggle"})
        );
        assert_eq!(
            parse_url("myowneq:import/MEQ1-AbC_d-9").unwrap(),
            json!({"cmd":"import","text":"MEQ1-AbC_d-9"})
        );
        assert_eq!(
            parse_url("myowneq://select/Caf%C3%A9").unwrap()["profile"],
            "Café"
        );
        assert!(parse_url("https://example.com").is_err());
        assert!(parse_url("myowneq://select/").is_err());
        assert!(parse_url("myowneq://delete/everything").is_err());
    }

    #[test]
    fn token_check() {
        assert!(same("abc", "abc"));
        assert!(!same("abc", "abd"));
        assert!(!same("abc", "abcd"));
    }
}
