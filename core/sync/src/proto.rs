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
        /// Who this machine is, for the "connected machines" list. Optional so
        /// older apps (which don't send it) still connect.
        #[serde(default)]
        info: PeerInfo,
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
    /// Every machine that has used this server, with live online state.
    /// Older apps ignore it (unknown messages are skipped).
    Peers {
        peers: Vec<Peer>,
    },
}

/// What a machine says about itself when connecting.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct PeerInfo {
    /// Computer name ("Pernalonga's MacBook").
    #[serde(default)]
    pub name: String,
    /// "mac" | "windows" | "linux".
    #[serde(default)]
    pub platform: String,
    /// App version.
    #[serde(default)]
    pub version: String,
}

/// A machine as the server sees it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Peer {
    pub machine: String,
    #[serde(flatten)]
    pub info: PeerInfo,
    pub online: bool,
    /// Unix ms of the last connect/disconnect.
    pub last_seen: u64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Acked {
    pub key: String,
    pub stamp: Stamp,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Apps from before presence don't send `info`: they must still connect.
    #[test]
    fn old_hello_without_info_still_parses() {
        let old = r#"{"t":"hello","token":"x","machine":"mac-1","since":4}"#;
        match serde_json::from_str::<ClientMsg>(old).unwrap() {
            ClientMsg::Hello {
                machine,
                since,
                info,
                ..
            } => {
                assert_eq!((machine.as_str(), since), ("mac-1", 4));
                assert_eq!(info, PeerInfo::default());
            }
            _ => panic!("not a hello"),
        }
    }

    #[test]
    fn peers_round_trip_flat() {
        let p = Peer {
            machine: "win-1".into(),
            info: PeerInfo {
                name: "PC".into(),
                platform: "windows".into(),
                version: "1.0".into(),
            },
            online: true,
            last_seen: 5,
        };
        let v = serde_json::to_value(&p).unwrap();
        assert_eq!(v["name"], "PC");
        assert_eq!(serde_json::from_value::<Peer>(v).unwrap(), p);
    }
}
