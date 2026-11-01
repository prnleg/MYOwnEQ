//! Release helper.
//!
//!   keygen <private-key-file>   create signing key (file, mode 600) and
//!                               write core/update/update_public_key.txt
//!   sign <file>                 print base64 signature (key from env MYOWNEQ_SIGNING_KEY)
//!   verify <file> <sig>         check a signature against the embedded public key
//!   manifest <version> <base-url> [--mac <zip>] [--windows <exe>]
//!                               print latest.json for the given artifacts

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use ed25519_dalek::{Signer, SigningKey};
use myowneq_update::{sha256_hex, Artifact, Manifest};
use std::path::Path;
use std::process::exit;

fn die(msg: &str) -> ! {
    eprintln!("error: {msg}");
    exit(1)
}

fn signing_key() -> SigningKey {
    let b64 =
        std::env::var("MYOWNEQ_SIGNING_KEY").unwrap_or_else(|_| die("MYOWNEQ_SIGNING_KEY not set"));
    let seed: [u8; 32] = B64
        .decode(b64.trim())
        .ok()
        .and_then(|v| v.try_into().ok())
        .unwrap_or_else(|| die("MYOWNEQ_SIGNING_KEY must be base64 of 32 bytes"));
    SigningKey::from_bytes(&seed)
}

fn sign_file(key: &SigningKey, path: &Path) -> (Vec<u8>, String) {
    let data = std::fs::read(path).unwrap_or_else(|e| die(&format!("{}: {e}", path.display())));
    let sig = B64.encode(key.sign(&data).to_bytes());
    (data, sig)
}

fn artifact(key: &SigningKey, path: &str, base_url: &str) -> Artifact {
    let p = Path::new(path);
    let (data, sig) = sign_file(key, p);
    let name = p
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or_else(|| die("bad file name"));
    Artifact {
        url: format!("{}/{name}", base_url.trim_end_matches('/')),
        sha256: sha256_hex(&data),
        sig,
        size: data.len() as u64,
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("keygen") => {
            let out = args
                .get(1)
                .unwrap_or_else(|| die("usage: keygen <private-key-file>"));
            if Path::new(out).exists() {
                die("refusing to overwrite an existing key file");
            }
            let mut seed = [0u8; 32];
            getrandom::fill(&mut seed).unwrap_or_else(|e| die(&format!("rng: {e}")));
            let key = SigningKey::from_bytes(&seed);
            std::fs::write(out, B64.encode(seed)).unwrap_or_else(|e| die(&e.to_string()));
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                let _ = std::fs::set_permissions(out, std::fs::Permissions::from_mode(0o600));
            }
            let pk = B64.encode(key.verifying_key().to_bytes());
            let root = Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../../core/update/update_public_key.txt");
            std::fs::write(&root, format!("{pk}\n")).unwrap_or_else(|e| die(&e.to_string()));
            println!("private key: {out}  (put its contents in the GitHub secret MYOWNEQ_SIGNING_KEY, then keep it offline)");
            println!("public key:  {pk}  (written to core/update/update_public_key.txt)");
        }
        Some("sign") => {
            let f = args.get(1).unwrap_or_else(|| die("usage: sign <file>"));
            println!("{}", sign_file(&signing_key(), Path::new(f)).1);
        }
        Some("manifest") => {
            let version = args.get(1).unwrap_or_else(|| {
                die("usage: manifest <version> <base-url> [--mac zip] [--windows exe]")
            });
            let base = args.get(2).unwrap_or_else(|| die("missing base url"));
            let key = signing_key();
            let mut m = Manifest {
                version: version.trim_start_matches('v').to_string(),
                notes_url: format!("https://github.com/prnleg/MYOwnEQ/releases/tag/{version}"),
                mac: None,
                windows: None,
            };
            let mut i = 3;
            while i + 1 < args.len() {
                match args[i].as_str() {
                    "--mac" => m.mac = Some(artifact(&key, &args[i + 1], base)),
                    "--windows" => m.windows = Some(artifact(&key, &args[i + 1], base)),
                    other => die(&format!("unknown flag {other}")),
                }
                i += 2;
            }
            println!("{}", serde_json::to_string_pretty(&m).expect("json"));
        }
        Some("verify") => {
            let (f, sig) = match (args.get(1), args.get(2)) {
                (Some(f), Some(s)) => (f, s),
                _ => die("usage: verify <file> <base64-signature>"),
            };
            let data = std::fs::read(f).unwrap_or_else(|e| die(&e.to_string()));
            match myowneq_update::verify(myowneq_update::PUBLIC_KEY, &data, sig) {
                Ok(()) => println!("OK: signed by the key the apps trust"),
                Err(e) => die(&e.to_string()),
            }
        }
        _ => die("commands: keygen | sign | manifest | verify"),
    }
}
