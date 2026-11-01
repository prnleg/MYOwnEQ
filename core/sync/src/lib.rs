//! MyOwnEQ sync.
//!
//! Model: the [`Library`](myowneq_model::Library) is split into independent
//! *entities* (each profile, device group, rule, and per-group selection).
//! Every local change to an entity gets a [`Stamp`] (hybrid clock: wall time
//! plus counter, tie-broken by machine id). The server keeps the highest-stamped
//! version of each entity (last-writer-wins) and a global sequence number so
//! clients can fetch "everything since N". Deletions are tombstones
//! (`value: null`). Rejected (older) pushes are answered with the winning
//! version, so every replica converges.

pub mod client;
pub mod discovery;
pub mod entity;
pub mod proto;
pub mod replica;
pub mod stamp;

pub use proto::{Acked, ClientMsg, Op, SeqOp, ServerMsg};
pub use replica::Replica;
pub use stamp::{Clock, Stamp};

/// Default TCP port for servers and LAN hosts.
pub const DEFAULT_PORT: u16 = 7878;
