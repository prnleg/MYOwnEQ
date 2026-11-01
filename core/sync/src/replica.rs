//! Per-machine sync bookkeeping: entity stamps, unsent ops, last seen seq.

use crate::entity::{self, Snapshot};
use crate::proto::{Acked, Op, SeqOp};
use crate::stamp::{Clock, Stamp};
use myowneq_model::{presets, Library};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::Path;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Replica {
    pub clock: Clock,
    /// Version of each entity as known locally.
    pub stamps: BTreeMap<String, Stamp>,
    /// Local changes not yet acknowledged by the server (one per key).
    pub outbox: BTreeMap<String, Op>,
    /// Highest server seq applied.
    pub since: u64,
    /// Server this state belongs to; switching servers resets `since`.
    #[serde(default)]
    pub server_id: String,
}

impl Replica {
    pub fn new(machine: &str) -> Replica {
        Replica {
            clock: Clock::new(machine),
            stamps: BTreeMap::new(),
            outbox: BTreeMap::new(),
            since: 0,
            server_id: String::new(),
        }
    }

    pub fn load(path: &Path, machine: &str) -> Replica {
        std::fs::read_to_string(path)
            .ok()
            .and_then(|s| serde_json::from_str::<Replica>(&s).ok())
            .filter(|r| r.clock.machine == machine)
            .unwrap_or_else(|| Replica::new(machine))
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_string(self)?)?;
        std::fs::rename(tmp, path)
    }

    /// Point at a (possibly different) server. A new server means a full
    /// re-sync: everything local is queued so the server learns it.
    pub fn bind_server(&mut self, server_id: &str, lib: &Library) {
        if self.server_id == server_id {
            return;
        }
        self.server_id = server_id.into();
        self.since = 0;
        self.seed(lib, true);
    }

    /// Give every entity a stamp. Untouched built-in presets get the zero
    /// stamp so two fresh machines don't fight over identical data.
    /// With `queue_all`, every entity is (re)queued for upload.
    pub fn seed(&mut self, lib: &Library, queue_all: bool) {
        let builtin: Snapshot = entity::snapshot(&Library {
            profiles: presets::builtin(),
            ..Default::default()
        });
        for (k, v) in entity::snapshot(lib) {
            let pristine = builtin.get(&k) == Some(&v);
            let stamp = match self.stamps.get(&k) {
                Some(s) => s.clone(),
                None if pristine => Stamp::zero(),
                None => self.clock.now(),
            };
            self.stamps.insert(k.clone(), stamp.clone());
            if queue_all && stamp != Stamp::zero() {
                self.outbox.insert(
                    k.clone(),
                    Op {
                        key: k,
                        stamp,
                        value: Some(v),
                    },
                );
            }
        }
    }

    /// Record local edits (library went from `before` to `after`).
    /// Returns the new ops (also queued in the outbox).
    pub fn record(&mut self, before: &Snapshot, after: &Snapshot) -> Vec<Op> {
        let ops: Vec<Op> = entity::diff(before, after)
            .into_iter()
            .map(|(key, value)| Op {
                stamp: self.clock.now(),
                key,
                value,
            })
            .collect();
        for op in &ops {
            self.stamps.insert(op.key.clone(), op.stamp.clone());
            self.outbox.insert(op.key.clone(), op.clone());
        }
        ops
    }

    /// Server stored these versions. Ops edited again since sending stay queued.
    pub fn ack(&mut self, acked: &[Acked]) {
        for a in acked {
            if self.outbox.get(&a.key).is_some_and(|o| o.stamp == a.stamp) {
                self.outbox.remove(&a.key);
            }
        }
    }

    /// Apply ops from the server. Returns true if the library changed.
    pub fn apply_remote(&mut self, lib: &mut Library, ops: &[SeqOp]) -> bool {
        let mut changed = false;
        for SeqOp { seq, op } in ops {
            self.since = self.since.max(*seq);
            self.clock.observe(&op.stamp);
            let newer = self.stamps.get(&op.key).is_none_or(|s| op.stamp > *s);
            if !newer {
                continue;
            }
            if entity::apply(lib, &op.key, op.value.as_ref()).is_ok() {
                self.stamps.insert(op.key.clone(), op.stamp.clone());
                // A newer remote version supersedes our pending edit.
                if self.outbox.get(&op.key).is_some_and(|o| o.stamp < op.stamp) {
                    self.outbox.remove(&op.key);
                }
                changed = true;
            }
        }
        changed
    }

    pub fn pending(&self) -> Vec<Op> {
        self.outbox.values().cloned().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::entity::snapshot;

    /// Minimal in-memory LWW server for convergence tests.
    #[derive(Default)]
    struct FakeServer {
        log: BTreeMap<String, SeqOp>,
        seq: u64,
    }

    impl FakeServer {
        fn push(&mut self, ops: Vec<Op>) -> (Vec<Acked>, Vec<SeqOp>) {
            let (mut acked, mut winners) = (vec![], vec![]);
            for op in ops {
                match self.log.get(&op.key) {
                    Some(cur) if cur.op.stamp >= op.stamp => winners.push(cur.clone()),
                    _ => {
                        self.seq += 1;
                        acked.push(Acked {
                            key: op.key.clone(),
                            stamp: op.stamp.clone(),
                        });
                        self.log.insert(op.key.clone(), SeqOp { seq: self.seq, op });
                    }
                }
            }
            (acked, winners)
        }
        fn since(&self, s: u64) -> Vec<SeqOp> {
            let mut v: Vec<_> = self.log.values().filter(|o| o.seq > s).cloned().collect();
            v.sort_by_key(|o| o.seq);
            v
        }
    }

    fn sync(r: &mut Replica, lib: &mut Library, srv: &mut FakeServer) {
        let (acked, winners) = srv.push(r.pending());
        r.ack(&acked);
        r.apply_remote(lib, &winners);
        let ops = srv.since(r.since);
        r.apply_remote(lib, &ops);
    }

    #[test]
    fn two_machines_converge_with_conflict() {
        let mut srv = FakeServer::default();
        let (mut la, mut lb) = (Library::with_presets(), Library::with_presets());
        let (mut ra, mut rb) = (Replica::new("mac"), Replica::new("pc"));
        ra.bind_server("s", &la);
        rb.bind_server("s", &lb);
        // Presets are never queued.
        assert!(ra.outbox.is_empty());

        // A shared profile reaches B first.
        let shared = myowneq_model::Profile::new("FPS mine");
        let sid = shared.id;
        let before = snapshot(&la);
        la.upsert_profile(shared);
        ra.record(&before, &snapshot(&la));
        sync(&mut ra, &mut la, &mut srv);
        sync(&mut rb, &mut lb, &mut srv);
        assert!(lb.profile(sid).is_some());

        // Both edit it offline; B edits later and must win.
        let before = snapshot(&la);
        la.profile_mut(sid).unwrap().name = "FPS mac".into();
        ra.record(&before, &snapshot(&la));
        std::thread::sleep(std::time::Duration::from_millis(2));
        let before = snapshot(&lb);
        lb.profile_mut(sid).unwrap().name = "FPS pc".into();
        lb.profiles.push(myowneq_model::Profile::new("Only on PC"));
        rb.record(&before, &snapshot(&lb));

        for _ in 0..2 {
            sync(&mut ra, &mut la, &mut srv);
            sync(&mut rb, &mut lb, &mut srv);
        }
        assert_eq!(snapshot(&la), snapshot(&lb));
        assert_eq!(la.profile(sid).unwrap().name, "FPS pc");
        assert!(la.profiles.iter().any(|p| p.name == "Only on PC"));
        assert!(ra.outbox.is_empty() && rb.outbox.is_empty());
    }

    #[test]
    fn deletion_propagates() {
        let mut srv = FakeServer::default();
        let (mut la, mut lb) = (Library::with_presets(), Library::with_presets());
        let (mut ra, mut rb) = (Replica::new("a"), Replica::new("b"));
        ra.bind_server("s", &la);
        rb.bind_server("s", &lb);
        let p = myowneq_model::Profile::new("Temp");
        let id = p.id;
        let before = snapshot(&la);
        la.upsert_profile(p);
        ra.record(&before, &snapshot(&la));
        sync(&mut ra, &mut la, &mut srv);
        sync(&mut rb, &mut lb, &mut srv);
        assert!(lb.profile(id).is_some());
        let before = snapshot(&la);
        la.delete_profile(id);
        ra.record(&before, &snapshot(&la));
        sync(&mut ra, &mut la, &mut srv);
        sync(&mut rb, &mut lb, &mut srv);
        assert!(lb.profile(id).is_none());
    }

    #[test]
    fn edit_while_in_flight_is_not_lost() {
        let mut r = Replica::new("a");
        let mut lib = Library::with_presets();
        let mine = myowneq_model::Profile::new("Mine");
        let id = mine.id;
        lib.upsert_profile(mine);
        r.seed(&lib, false);
        let s0 = snapshot(&lib);
        lib.profile_mut(id).unwrap().name = "one".into();
        let first = r.record(&s0, &snapshot(&lib)).remove(0);
        let s1 = snapshot(&lib);
        lib.profile_mut(id).unwrap().name = "two".into();
        let second = r.record(&s1, &snapshot(&lib)).remove(0);
        // Ack of the first (in-flight) version must keep the second queued.
        r.ack(&[Acked {
            key: first.key.clone(),
            stamp: first.stamp,
        }]);
        assert_eq!(r.outbox.len(), 1);
        r.ack(&[Acked {
            key: second.key,
            stamp: second.stamp,
        }]);
        assert!(r.outbox.is_empty());
    }
}
