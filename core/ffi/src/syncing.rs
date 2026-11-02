//! Sync glue for the app core: config, replica bookkeeping, client and
//! optional embedded LAN host.

use myowneq_model::Library;
use myowneq_server::EmbeddedServer;
use myowneq_sync::client::{Event, SyncClient};
use myowneq_sync::discovery::Advertiser;
use myowneq_sync::entity::{snapshot, Snapshot};
use myowneq_sync::{Peer, PeerInfo, Replica};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::path::PathBuf;

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(tag = "mode", rename_all = "snake_case")]
pub enum SyncConfig {
    #[default]
    Off,
    /// Connect to a server (self-hosted, cloud, or another machine's LAN host).
    Server { url: String, token: String },
    /// Run the server inside this app and connect to it.
    Host { port: u16, token: String },
}

pub struct SyncGlue {
    dir: Option<PathBuf>,
    machine: String,
    pub config: SyncConfig,
    pub replica: Replica,
    client: Option<SyncClient>,
    host: Option<(EmbeddedServer, Option<Advertiser>)>,
    status: &'static str,
    detail: String,
    /// What this machine tells the server about itself (name, platform, version).
    info: PeerInfo,
    /// Machines using the same sync, from the server.
    peers: Vec<Peer>,
}

impl SyncGlue {
    pub fn load(dir: Option<PathBuf>, machine: &str, lib: &Library) -> SyncGlue {
        let config: SyncConfig = dir
            .as_ref()
            .and_then(|d| std::fs::read_to_string(d.join("sync_config.json")).ok())
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default();
        let mut replica = match &dir {
            Some(d) => Replica::load(&d.join("sync.json"), machine),
            None => Replica::new(machine),
        };
        // Give stamps to anything new (first run, or entities created while sync was off).
        replica.seed(lib, false);
        let mut g = SyncGlue {
            dir,
            machine: machine.into(),
            config: SyncConfig::Off,
            replica,
            client: None,
            host: None,
            status: "off",
            detail: String::new(),
            info: PeerInfo::default(),
            peers: Vec::new(),
        };
        g.apply_config(config);
        g
    }

    pub fn info(&self) -> &PeerInfo {
        &self.info
    }

    /// Sync section of the diagnostics report. Never includes the token or server URL.
    pub fn diagnostics(&self) -> Vec<String> {
        let mode = match &self.config {
            SyncConfig::Off => "off",
            SyncConfig::Server { .. } => "server",
            SyncConfig::Host { .. } => "host (this machine)",
        };
        let mut v = vec![
            format!("mode: {mode}"),
            format!(
                "status: {}{}",
                self.status,
                if self.detail.is_empty() {
                    String::new()
                } else {
                    format!(" ({})", self.detail)
                }
            ),
            format!("pending changes: {}", self.replica.outbox.len()),
        ];
        for p in &self.peers {
            v.push(format!(
                "peer: {} [{} {}] {}",
                p.info.name,
                p.info.platform,
                p.info.version,
                if p.online { "online" } else { "offline" }
            ));
        }
        v
    }

    /// Name / platform / version shown to the other machines. Reconnects if it changed.
    pub fn set_info(&mut self, info: PeerInfo) {
        if info == self.info {
            return;
        }
        self.info = info;
        if self.config != SyncConfig::Off {
            self.apply_config(self.config.clone());
        }
    }

    /// Switch mode. Starts/stops the embedded host and the client.
    /// Hosting without a token gets a fresh pairing code ("7KQ2-M9XD").
    pub fn apply_config(&mut self, mut config: SyncConfig) {
        if let SyncConfig::Host { token, .. } = &mut config {
            if token.trim().is_empty() {
                *token = myowneq_server::pairing_code();
            }
        }
        self.peers.clear();
        self.client = None;
        self.host = None;
        self.detail.clear();
        self.status = "off";
        match &config {
            SyncConfig::Off => {}
            SyncConfig::Server { url, token } => {
                self.client = Some(SyncClient::start(
                    url,
                    token,
                    &self.machine,
                    self.info.clone(),
                    self.replica.since,
                ));
                self.status = "connecting";
            }
            SyncConfig::Host { port, token } => {
                let db = self
                    .dir
                    .clone()
                    .unwrap_or_else(std::env::temp_dir)
                    .join("host.sqlite");
                match EmbeddedServer::start(*port, &db, token.clone()) {
                    Ok(server) => {
                        let adv = Advertiser::start(&self.machine, server.port).ok();
                        let url = format!("127.0.0.1:{}", server.port);
                        self.client = Some(SyncClient::start(
                            &url,
                            token,
                            &self.machine,
                            self.info.clone(),
                            self.replica.since,
                        ));
                        self.host = Some((server, adv));
                        self.status = "connecting";
                    }
                    Err(e) => {
                        self.status = "error";
                        self.detail = format!("could not host on port {port}: {e}");
                    }
                }
            }
        }
        self.config = config;
        self.save_config();
    }

    /// Record local library edits and push them.
    pub fn record(&mut self, before: &Snapshot, lib: &Library) {
        let ops = self.replica.record(before, &snapshot(lib));
        if ops.is_empty() {
            return;
        }
        if let Some(c) = &self.client {
            c.push(ops);
        }
        self.save_replica();
    }

    /// Process network events. Returns true if the library changed.
    pub fn tick(&mut self, lib: &mut Library) -> bool {
        let Some(client) = &self.client else {
            return false;
        };
        let mut changed = false;
        let mut dirty = false;
        for ev in client.poll() {
            match ev {
                Event::Connected { server_id, .. } => {
                    self.replica.bind_server(&server_id, lib);
                    client.push(self.replica.pending());
                    self.status = "connected";
                    self.detail.clear();
                    dirty = true;
                }
                Event::Ops(ops) => {
                    changed |= self.replica.apply_remote(lib, &ops);
                    client.set_since(self.replica.since);
                    dirty = true;
                }
                Event::Ack(a) => {
                    self.replica.ack(&a);
                    dirty = true;
                }
                Event::Disconnected(e) => {
                    self.status = "connecting";
                    self.detail = e;
                }
                Event::Rejected(e) => {
                    self.status = "error";
                    self.detail = e;
                }
                Event::Peers(p) => self.peers = p,
            }
        }
        if dirty {
            self.save_replica();
        }
        changed
    }

    pub fn status_json(&self) -> Value {
        let (mode, url, token, port) = match &self.config {
            SyncConfig::Off => ("off", None, None, None),
            SyncConfig::Server { url, token } => {
                ("server", Some(url.clone()), Some(token.clone()), None)
            }
            SyncConfig::Host { token, .. } => (
                "host",
                None,
                Some(token.clone()),
                self.host.as_ref().map(|(s, _)| s.port),
            ),
        };
        json!({
            "mode": mode,
            "status": self.status,
            "detail": self.detail,
            "url": url,
            "token": token,
            "host_port": port,
            "host_addresses": port.map(|p| local_ips().into_iter().map(|ip| format!("{ip}:{p}")).collect::<Vec<_>>()),
            "pending": self.replica.outbox.len(),
            "peers": self.peers,
        })
    }

    fn save_config(&self) {
        if let Some(d) = &self.dir {
            let _ = std::fs::create_dir_all(d);
            if let Ok(s) = serde_json::to_string_pretty(&self.config) {
                let _ = std::fs::write(d.join("sync_config.json"), s);
            }
        }
    }

    fn save_replica(&self) {
        if let Some(d) = &self.dir {
            let _ = self.replica.save(&d.join("sync.json"));
        }
    }
}

/// Primary LAN address(es) of this machine, for "type this on the other machine".
fn local_ips() -> Vec<std::net::IpAddr> {
    // Connecting a UDP socket sends nothing; it just picks the outbound interface.
    std::net::UdpSocket::bind("0.0.0.0:0")
        .and_then(|s| s.connect("192.0.2.1:9").map(|_| s))
        .and_then(|s| s.local_addr())
        .map(|a| vec![a.ip()])
        .unwrap_or_default()
}
