//! Wire protocol (JSON text frames over WebSocket at `/sync`).

use crate::stamp::Stamp;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// One entity version. `value: None` is a deletion (tombstone).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Op {
    pub key: String,
    pub stamp: Stamp,
    pub value: Option<Value>,
}

/// An op as stored by the server, with its global sequence number.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SeqOp {
    pub seq: u64,
    #[serde(flatten)]
    pub op: Op,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "t", rename_all = "snake_case")]
pub enum ClientMsg {
    /// First message. `since` = highest seq this client has applied.
    Hello {
        token: String,
        machine: String,
        since: u64,
    },
    Push {
        ops: Vec<Op>,
    },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "t", rename_all = "snake_case")]
pub enum ServerMsg {
    Welcome {
        seq: u64,
        server_id: String,
    },
    /// New ops (from catch-up, other clients, or winners of rejected pushes).
    Ops {
        ops: Vec<SeqOp>,
    },
    /// The server stored these versions from your push.
    Ack {
        acked: Vec<Acked>,
    },
    Error {
        msg: String,
    },
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Acked {
    pub key: String,
    pub stamp: Stamp,
}
