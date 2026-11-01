//! Real server + two real WebSocket clients converging.

use myowneq_model::{Library, Profile};
use myowneq_server::EmbeddedServer;
use myowneq_sync::client::{Event, SyncClient};
use myowneq_sync::entity::snapshot;
use myowneq_sync::{Peer, PeerInfo, Replica};
use std::time::{Duration, Instant};

struct Node {
    lib: Library,
    rep: Replica,
    client: SyncClient,
    connected: bool,
    rejected: bool,
    peers: Vec<Peer>,
}

impl Node {
    fn new(machine: &str, port: u16, token: &str) -> Node {
        let lib = Library::with_presets();
        let mut rep = Replica::new(machine);
        rep.seed(&lib, false);
        let info = PeerInfo {
            name: format!("{machine} computer"),
            platform: machine.into(),
            version: "1.2.3".into(),
        };
        let client = SyncClient::start(&format!("127.0.0.1:{port}"), token, machine, info, 0);
        Node {
            lib,
            rep,
            client,
            connected: false,
            rejected: false,
            peers: vec![],
        }
    }

    fn edit(&mut self, f: impl FnOnce(&mut Library)) {
        let before = snapshot(&self.lib);
        f(&mut self.lib);
        let ops = self.rep.record(&before, &snapshot(&self.lib));
        self.client.push(ops);
    }

    fn pump(&mut self) {
        for ev in self.client.poll() {
            match ev {
                Event::Connected { server_id, .. } => {
                    self.connected = true;
                    self.rep.bind_server(&server_id, &self.lib);
                    self.client.push(self.rep.pending());
                }
                Event::Ops(ops) => {
                    self.rep.apply_remote(&mut self.lib, &ops);
                    self.client.set_since(self.rep.since);
                }
                Event::Ack(a) => self.rep.ack(&a),
                Event::Rejected(_) => self.rejected = true,
                Event::Disconnected(_) => self.connected = false,
                Event::Peers(p) => self.peers = p,
            }
        }
    }
}

fn wait(mut cond: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(10);
    while Instant::now() < end {
        if cond() {
            return;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    panic!("timed out");
}

#[test]
fn two_clients_converge_through_real_server() {
    let dir = std::env::temp_dir().join(format!("meq-e2e-{}", myowneq_model::new_id()));
    std::fs::create_dir_all(&dir).unwrap();
    let server = EmbeddedServer::start(0, &dir.join("db.sqlite"), "secret-token".into()).unwrap();

    let mut mac = Node::new("mac", server.port, "secret-token");
    let mut pc = Node::new("pc", server.port, "secret-token");
    wait(|| {
        mac.pump();
        pc.pump();
        mac.connected && pc.connected
    });

    // Mac creates a profile -> PC sees it.
    let p = Profile::new("Momentum 4 AutoEQ");
    let id = p.id;
    mac.edit(|l| l.upsert_profile(p));
    wait(|| {
        mac.pump();
        pc.pump();
        pc.lib.profile(id).is_some()
    });

    // PC renames it -> Mac sees the rename (live mirror).
    pc.edit(|l| l.profile_mut(id).unwrap().name = "M4".into());
    wait(|| {
        mac.pump();
        pc.pump();
        mac.lib.profile(id).map(|p| p.name.as_str()) == Some("M4")
    });

    // Both outboxes drain and libraries are identical.
    wait(|| {
        mac.pump();
        pc.pump();
        mac.rep.outbox.is_empty()
            && pc.rep.outbox.is_empty()
            && snapshot(&mac.lib) == snapshot(&pc.lib)
    });

    // Presence: each side sees the other online, with name / platform / version.
    wait(|| {
        mac.pump();
        pc.pump();
        mac.peers.iter().any(|p| {
            p.machine == "pc"
                && p.online
                && p.info.name == "pc computer"
                && p.info.version == "1.2.3"
        }) && pc.peers.iter().any(|p| p.machine == "mac" && p.online)
    });
    // PC quits -> Mac sees it offline (still listed, with last_seen).
    drop(pc);
    wait(|| {
        mac.pump();
        mac.peers
            .iter()
            .any(|p| p.machine == "pc" && !p.online && p.last_seen > 0)
    });

    // Wrong token is rejected (and never listed as a machine).
    let mut bad = Node::new("intruder", server.port, "nope-nope");
    wait(|| {
        bad.pump();
        bad.rejected
    });
    mac.pump();
    assert!(!mac.peers.iter().any(|p| p.machine == "intruder"));

    drop(server);
    std::fs::remove_dir_all(dir).ok();
}
