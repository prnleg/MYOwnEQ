//! `myowneq`: control the running MyOwnEQ app from a terminal, a script, Raycast,
//! Shortcuts, AutoHotkey, a Stream Deck…
//!
//! It reads `control.json` (port + token) from the app's data folder and sends one
//! request over 127.0.0.1. Nothing leaves this computer.

use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

const HELP: &str = "\
myowneq: control MyOwnEQ from the command line

USAGE
  myowneq status              what's playing (output, profile, EQ on/off)
  myowneq list                all profiles (* = active)
  myowneq select <profile>    switch profile (name, part of the name, or id)
  myowneq on | off | toggle   turn the EQ on / off
  myowneq share <profile>     print the profile's share code (MEQ1-…)
  myowneq import <code>       import a share code (use - to read it from stdin)
  myowneq open <link>         run a myowneq:// link

OPTIONS
  --json    print the app's raw JSON reply

The MyOwnEQ app must be running with Preferences › \"Allow other apps to control
MyOwnEQ\" on. Exit codes: 0 ok, 1 error, 2 app not reachable.";

fn data_dir() -> Option<PathBuf> {
    if let Some(d) = std::env::var_os("MYOWNEQ_DATA_DIR").filter(|d| !d.is_empty()) {
        return Some(PathBuf::from(d));
    }
    if cfg!(target_os = "macos") {
        let home = std::env::var_os("HOME")?;
        Some(PathBuf::from(home).join("Library/Application Support/MyOwnEQ"))
    } else if cfg!(windows) {
        Some(PathBuf::from(std::env::var_os("APPDATA")?).join("MyOwnEQ"))
    } else {
        let base = std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))?;
        Some(base.join("MyOwnEQ"))
    }
}

enum Fail {
    Error(String),
    NotRunning(String),
}

fn send(mut req: Value) -> Result<Value, Fail> {
    let not_running = |why: &str| {
        Fail::NotRunning(format!(
            "MyOwnEQ isn't reachable ({why}). Is the app running, with Preferences › \
             \"Allow other apps to control MyOwnEQ\" on?"
        ))
    };
    let dir = data_dir().ok_or_else(|| not_running("no data folder"))?;
    let text = std::fs::read_to_string(dir.join("control.json"))
        .map_err(|_| not_running("no control.json"))?;
    let info: Value = serde_json::from_str(&text).map_err(|_| not_running("bad control.json"))?;
    let port = info["port"]
        .as_u64()
        .ok_or_else(|| not_running("bad control.json"))? as u16;
    req["token"] = info["token"].clone();
    let addr = ("127.0.0.1", port);
    let stream = std::net::ToSocketAddrs::to_socket_addrs(&addr)
        .ok()
        .and_then(|mut a| a.next())
        .and_then(|a| TcpStream::connect_timeout(&a, Duration::from_secs(2)).ok())
        .ok_or_else(|| not_running("connection refused"))?;
    stream.set_read_timeout(Some(Duration::from_secs(10))).ok();
    let mut w = stream.try_clone().map_err(|e| Fail::Error(e.to_string()))?;
    writeln!(w, "{req}").map_err(|e| Fail::Error(e.to_string()))?;
    let mut line = String::new();
    BufReader::new(stream)
        .read_line(&mut line)
        .map_err(|e| Fail::Error(format!("no answer: {e}")))?;
    serde_json::from_str(&line).map_err(|_| Fail::Error("bad answer from the app".into()))
}

fn run(args: &[String]) -> Result<(Value, String), Fail> {
    let rest = |from: usize| args.get(from..).map(|a| a.join(" ")).unwrap_or_default();
    let need = |what: &str| Fail::Error(format!("missing {what}. See: myowneq help"));
    let req = match args.first().map(String::as_str) {
        Some("status") | None => json!({ "cmd": "status" }),
        Some("list") | Some("ls") => json!({ "cmd": "list" }),
        Some("select") | Some("use") => {
            let p = rest(1);
            if p.is_empty() {
                return Err(need("profile name"));
            }
            json!({ "cmd": "select", "profile": p })
        }
        Some(c @ ("on" | "off" | "toggle")) => json!({ "cmd": c }),
        Some("share") => {
            let p = rest(1);
            if p.is_empty() {
                return Err(need("profile name"));
            }
            json!({ "cmd": "share", "profile": p })
        }
        Some("import") => {
            let mut text = rest(1);
            if text == "-" || text.is_empty() {
                text.clear();
                std::io::stdin()
                    .read_to_string(&mut text)
                    .map_err(|e| Fail::Error(e.to_string()))?;
            }
            json!({ "cmd": "import", "text": text })
        }
        Some("open") => json!({ "cmd": "open_url", "url": rest(1) }),
        Some(other) => {
            return Err(Fail::Error(format!(
                "unknown command \"{other}\". See: myowneq help"
            )))
        }
    };
    let r = send(req)?;
    if r["ok"] == false {
        return Err(Fail::Error(
            r["error"].as_str().unwrap_or("failed").to_string(),
        ));
    }
    Ok((
        r.clone(),
        human(args.first().map(String::as_str).unwrap_or("status"), &r),
    ))
}

fn human(cmd: &str, r: &Value) -> String {
    let s = |k: &str| r[k].as_str().unwrap_or("-").to_string();
    match cmd {
        "list" | "ls" => r["profiles"]
            .as_array()
            .map(|ps| {
                ps.iter()
                    .map(|p| {
                        format!(
                            "{} {}{}",
                            if p["active"] == true { "*" } else { " " },
                            p["name"].as_str().unwrap_or(""),
                            if p["preset"] == true {
                                "  (preset)"
                            } else {
                                ""
                            }
                        )
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            })
            .unwrap_or_default(),
        "select" | "use" => format!("Now playing: {}", s("profile")),
        "on" | "off" | "toggle" => {
            format!("EQ {}", if r["enabled"] == true { "on" } else { "off" })
        }
        "share" => s("code"),
        "import" => format!("Imported \"{}\"", s("profile")),
        "open" => "Done".into(),
        _ => format!(
            "Output:  {}\nProfile: {}\nEQ:      {}",
            s("device"),
            s("profile"),
            if r["enabled"] == true { "on" } else { "off" }
        ),
    }
}

fn main() -> ExitCode {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    if args
        .iter()
        .any(|a| matches!(a.as_str(), "-h" | "--help" | "help"))
    {
        println!("{HELP}");
        return ExitCode::SUCCESS;
    }
    if args.iter().any(|a| a == "-V" || a == "--version") {
        println!("myowneq {}", env!("CARGO_PKG_VERSION"));
        return ExitCode::SUCCESS;
    }
    let raw = args.iter().any(|a| a == "--json");
    args.retain(|a| a != "--json");
    match run(&args) {
        Ok((json, text)) => {
            println!("{}", if raw { json.to_string() } else { text });
            ExitCode::SUCCESS
        }
        Err(Fail::Error(e)) => {
            eprintln!("myowneq: {e}");
            ExitCode::from(1)
        }
        Err(Fail::NotRunning(e)) => {
            eprintln!("myowneq: {e}");
            ExitCode::from(2)
        }
    }
}
