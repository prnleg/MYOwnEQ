//! Updater against a local HTTP server: good update downloads, tampered one is refused.

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use ed25519_dalek::{Signer, SigningKey};
use myowneq_update::{sha256_hex, Artifact, Manifest, Platform, Status, Updater};
use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::time::{Duration, Instant};

/// Binds a local server, builds (manifest, artifact body) from its base URL,
/// and serves them at /latest.json and anything else respectively.
fn serve(make: impl FnOnce(&str) -> (String, Vec<u8>)) -> String {
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let base = format!("http://{}", l.local_addr().unwrap());
    let (manifest, body) = make(&base);
    std::thread::spawn(move || {
        for s in l.incoming().flatten() {
            let mut s = s;
            let mut r = BufReader::new(s.try_clone().unwrap());
            let mut line = String::new();
            r.read_line(&mut line).unwrap();
            loop {
                let mut h = String::new();
                if r.read_line(&mut h).unwrap() == 0 || h == "\r\n" {
                    break;
                }
            }
            let payload = if line.contains("latest.json") {
                manifest.as_bytes().to_vec()
            } else {
                body.clone()
            };
            let _ = write!(
                s,
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                payload.len()
            );
            let _ = s.write_all(&payload);
        }
    });
    base
}

fn wait(u: &Updater) -> Status {
    let end = Instant::now() + Duration::from_secs(10);
    loop {
        let s = u.status();
        if matches!(
            s,
            Status::Ready { .. } | Status::Error { .. } | Status::UpToDate { .. }
        ) || Instant::now() > end
        {
            return s;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// Returns (base url, public key, served artifact bytes). With `tamper`, the
/// served bytes (and their hash) differ from what was signed.
fn setup(version: &str, tamper: bool) -> (String, String, Vec<u8>) {
    let sk = SigningKey::from_bytes(&[3u8; 32]);
    let pk = B64.encode(sk.verifying_key().to_bytes());
    let signed = b"pretend installer".to_vec();
    let served = if tamper {
        b"evil installer!!!".to_vec()
    } else {
        signed.clone()
    };
    let body = served.clone();
    let base = serve(|base| {
        let art = Artifact {
            url: format!("{base}/MyOwnEQ-setup.exe"),
            sha256: sha256_hex(&body),
            sig: B64.encode(sk.sign(&signed).to_bytes()),
            size: body.len() as u64,
        };
        let m = Manifest {
            version: version.into(),
            notes_url: "n".into(),
            mac: None,
            windows: Some(art),
        };
        (serde_json::to_string(&m).unwrap(), body.clone())
    });
    (base, pk, served)
}

#[test]
fn downloads_verified_update() {
    let (base, pk, body) = setup("9.9.9", false);
    let dir = std::env::temp_dir().join(format!("meq-upd-ok-{}", std::process::id()));
    let u = Updater::new("0.1.0", Platform::Windows, dir.clone())
        .with_endpoint(&format!("{base}/latest.json"), &pk);
    u.check_async();
    match wait(&u) {
        Status::Ready { version, file, .. } => {
            assert_eq!(version, "9.9.9");
            assert_eq!(std::fs::read(file).unwrap(), body);
        }
        other => panic!("expected ready, got {other:?}"),
    }
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn refuses_tampered_update() {
    let (base, pk, _) = setup("9.9.9", true);
    let dir = std::env::temp_dir().join(format!("meq-upd-bad-{}", std::process::id()));
    let u = Updater::new("0.1.0", Platform::Windows, dir.clone())
        .with_endpoint(&format!("{base}/latest.json"), &pk);
    u.check_async();
    match wait(&u) {
        Status::Error { message } => assert!(message.contains("NOT from MyOwnEQ"), "{message}"),
        other => panic!("expected signature error, got {other:?}"),
    }
    assert!(!dir.join("MyOwnEQ-setup.exe").exists());
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn up_to_date_when_not_newer() {
    let (base, pk, _) = setup("0.1.0", false);
    let dir = std::env::temp_dir().join(format!("meq-upd-same-{}", std::process::id()));
    let u = Updater::new("0.1.0", Platform::Windows, dir)
        .with_endpoint(&format!("{base}/latest.json"), &pk);
    u.check_async();
    assert!(matches!(wait(&u), Status::UpToDate { .. }));
}
