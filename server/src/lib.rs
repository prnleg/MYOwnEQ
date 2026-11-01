//! MyOwnEQ sync server.
//!
//! * Stores the highest-stamped version of every entity in SQLite.
//! * WebSocket at `/sync` (see `myowneq_sync::proto`), health check at `/health`.
//! * Runs standalone (Docker, `main.rs`) or embedded inside the desktop app
//!   as a LAN host ([`EmbeddedServer`]).

use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::State;
use axum::response::IntoResponse;
use axum::routing::get;
use axum::Router;
use futures_util::{SinkExt, StreamExt};
use myowneq_sync::{Acked, ClientMsg, Op, SeqOp, ServerMsg, Stamp};
use rusqlite::{params, Connection, OptionalExtension};
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::net::TcpListener;
use tokio::sync::{broadcast, oneshot};

const CATCHUP_CHUNK: usize = 500;

// ------------------------------------------------------------------ store

pub struct Store {
    conn: Mutex<Connection>,
    pub server_id: String,
}

pub struct PushResult {
    pub acked: Vec<Acked>,
    pub stored: Vec<SeqOp>,
    pub winners: Vec<SeqOp>,
}

impl Store {
    pub fn open(path: Option<&Path>) -> rusqlite::Result<Store> {
        let conn = match path {
            Some(p) => Connection::open(p)?,
            None => Connection::open_in_memory()?,
        };
        conn.execute_batch(
            "PRAGMA journal_mode=WAL;
             CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS entities (
                 key TEXT PRIMARY KEY,
                 t INTEGER NOT NULL,
                 m TEXT NOT NULL,
                 value TEXT,
                 seq INTEGER NOT NULL
             );
             CREATE INDEX IF NOT EXISTS entities_seq ON entities(seq);",
        )?;
        let id: Option<String> = conn
            .query_row("SELECT v FROM meta WHERE k='server_id'", [], |r| r.get(0))
            .optional()?;
        let server_id = match id {
            Some(id) => id,
            None => {
                let id = random_hex(16);
                conn.execute(
                    "INSERT INTO meta (k, v) VALUES ('server_id', ?1)",
                    params![id],
                )?;
                id
            }
        };
        Ok(Store {
            conn: Mutex::new(conn),
            server_id,
        })
    }

    pub fn seq(&self) -> u64 {
        let c = self.conn.lock().expect("db lock");
        c.query_row("SELECT COALESCE(MAX(seq), 0) FROM entities", [], |r| {
            r.get::<_, i64>(0)
        })
        .unwrap_or(0) as u64
    }

    /// Last-writer-wins upsert of a batch, in one transaction.
    pub fn push(&self, ops: Vec<Op>) -> rusqlite::Result<PushResult> {
        let mut c = self.conn.lock().expect("db lock");
        let tx = c.transaction()?;
        let mut seq: i64 = tx.query_row("SELECT COALESCE(MAX(seq), 0) FROM entities", [], |r| {
            r.get(0)
        })?;
        let mut res = PushResult {
            acked: vec![],
            stored: vec![],
            winners: vec![],
        };
        for op in ops {
            let cur: Option<(i64, String, Option<String>, i64)> = tx
                .query_row(
                    "SELECT t, m, value, seq FROM entities WHERE key=?1",
                    params![op.key],
                    |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
                )
                .optional()?;
            if let Some((t, m, value, s)) = cur {
                let cur_stamp = Stamp { t: t as u64, m };
                if cur_stamp >= op.stamp {
                    if cur_stamp == op.stamp {
                        // Duplicate resend: already stored.
                        res.acked.push(Acked {
                            key: op.key,
                            stamp: op.stamp,
                        });
                    } else {
                        res.winners.push(SeqOp {
                            seq: s as u64,
                            op: Op {
                                key: op.key,
                                stamp: cur_stamp,
                                value: value.and_then(|v| serde_json::from_str(&v).ok()),
                            },
                        });
                    }
                    continue;
                }
            }
            seq += 1;
            let value = op.value.as_ref().map(|v| v.to_string());
            tx.execute(
                "INSERT INTO entities (key, t, m, value, seq) VALUES (?1, ?2, ?3, ?4, ?5)
                 ON CONFLICT(key) DO UPDATE SET t=excluded.t, m=excluded.m, value=excluded.value, seq=excluded.seq",
                params![op.key, op.stamp.t as i64, op.stamp.m, value, seq],
            )?;
            res.acked.push(Acked {
                key: op.key.clone(),
                stamp: op.stamp.clone(),
            });
            res.stored.push(SeqOp {
                seq: seq as u64,
                op,
            });
        }
        tx.commit()?;
        Ok(res)
    }

    pub fn since(&self, since: u64) -> rusqlite::Result<Vec<SeqOp>> {
        let c = self.conn.lock().expect("db lock");
        let mut st =
            c.prepare("SELECT key, t, m, value, seq FROM entities WHERE seq > ?1 ORDER BY seq")?;
        let rows = st.query_map(params![since as i64], |r| {
            let value: Option<String> = r.get(3)?;
            Ok(SeqOp {
                seq: r.get::<_, i64>(4)? as u64,
                op: Op {
                    key: r.get(0)?,
                    stamp: Stamp {
                        t: r.get::<_, i64>(1)? as u64,
                        m: r.get(2)?,
                    },
                    value: value.and_then(|v| serde_json::from_str(&v).ok()),
                },
            })
        })?;
        rows.collect()
    }
}

fn random_hex(bytes: usize) -> String {
    // No extra deps: mix time, pid and a stack address through SipHash.
    use std::collections::hash_map::RandomState;
    use std::hash::{BuildHasher, Hasher};
    let mut out = String::new();
    while out.len() < bytes * 2 {
        let mut h = RandomState::new().build_hasher();
        h.write_u128(
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos()),
        );
        h.write_u32(std::process::id());
        out.push_str(&format!("{:016x}", h.finish()));
    }
    out.truncate(bytes * 2);
    out
}

/// Random token suitable for LAN pairing codes (e.g. "7KQ2-M9XD").
pub fn pairing_code() -> String {
    const ALPHABET: &[u8] = b"23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    let hex = random_hex(8);
    let mut n = u64::from_str_radix(&hex, 16).unwrap_or(0);
    let mut s = String::new();
    for i in 0..8 {
        if i == 4 {
            s.push('-');
        }
        s.push(ALPHABET[(n % 32) as usize] as char);
        n /= 32;
    }
    s
}

// ------------------------------------------------------------------ server

#[derive(Clone)]
struct AppState {
    store: Arc<Store>,
    token: Arc<String>,
    bus: broadcast::Sender<Arc<Vec<SeqOp>>>,
}

pub fn router(store: Arc<Store>, token: String) -> Router {
    let (bus, _) = broadcast::channel(256);
    let state = AppState {
        store,
        token: Arc::new(token),
        bus,
    };
    Router::new()
        .route("/sync", get(ws_upgrade))
        .route("/health", get(|| async { "ok" }))
        .with_state(state)
}

pub async fn serve(
    listener: TcpListener,
    store: Arc<Store>,
    token: String,
    shutdown: impl std::future::Future<Output = ()> + Send + 'static,
) -> std::io::Result<()> {
    axum::serve(listener, router(store, token))
        .with_graceful_shutdown(shutdown)
        .await
}

async fn ws_upgrade(ws: WebSocketUpgrade, State(st): State<AppState>) -> impl IntoResponse {
    ws.on_upgrade(move |socket| session(socket, st))
}

fn same(a: &str, b: &str) -> bool {
    // Constant-time compare.
    a.len() == b.len()
        && a.bytes()
            .zip(b.bytes())
            .fold(0u8, |acc, (x, y)| acc | (x ^ y))
            == 0
}

async fn send(
    sink: &mut futures_util::stream::SplitSink<WebSocket, Message>,
    msg: &ServerMsg,
) -> bool {
    let text = serde_json::to_string(msg).unwrap_or_default();
    sink.send(Message::Text(text.into())).await.is_ok()
}

async fn session(socket: WebSocket, st: AppState) {
    let (mut sink, mut stream) = socket.split();

    // Hello (10 s deadline).
    let hello = tokio::time::timeout(Duration::from_secs(10), stream.next()).await;
    let since = match hello {
        Ok(Some(Ok(Message::Text(t)))) => match serde_json::from_str::<ClientMsg>(t.as_str()) {
            Ok(ClientMsg::Hello { token, since, .. }) if same(&token, &st.token) => since,
            Ok(ClientMsg::Hello { .. }) => {
                send(
                    &mut sink,
                    &ServerMsg::Error {
                        msg: "wrong sync code/token".into(),
                    },
                )
                .await;
                return;
            }
            _ => return,
        },
        _ => return,
    };

    let mut bus = st.bus.subscribe();
    if !send(
        &mut sink,
        &ServerMsg::Welcome {
            seq: st.store.seq(),
            server_id: st.store.server_id.clone(),
        },
    )
    .await
    {
        return;
    }
    // Catch-up.
    match st.store.since(since) {
        Ok(ops) => {
            for chunk in ops.chunks(CATCHUP_CHUNK) {
                if !send(
                    &mut sink,
                    &ServerMsg::Ops {
                        ops: chunk.to_vec(),
                    },
                )
                .await
                {
                    return;
                }
            }
        }
        Err(e) => {
            send(
                &mut sink,
                &ServerMsg::Error {
                    msg: format!("db: {e}"),
                },
            )
            .await;
            return;
        }
    }

    loop {
        tokio::select! {
            m = stream.next() => {
                let text = match m {
                    Some(Ok(Message::Text(t))) => t,
                    Some(Ok(Message::Close(_))) | None | Some(Err(_)) => return,
                    Some(Ok(_)) => continue,
                };
                if let Ok(ClientMsg::Push { ops }) = serde_json::from_str::<ClientMsg>(text.as_str()) {
                    let res = match st.store.push(ops) {
                        Ok(r) => r,
                        Err(e) => {
                            send(&mut sink, &ServerMsg::Error { msg: format!("db: {e}") }).await;
                            return;
                        }
                    };
                    if !send(&mut sink, &ServerMsg::Ack { acked: res.acked }).await {
                        return;
                    }
                    if !res.winners.is_empty() && !send(&mut sink, &ServerMsg::Ops { ops: res.winners }).await {
                        return;
                    }
                    if !res.stored.is_empty() {
                        let _ = st.bus.send(Arc::new(res.stored));
                    }
                }
            }
            b = bus.recv() => match b {
                Ok(ops) => {
                    if !send(&mut sink, &ServerMsg::Ops { ops: ops.as_ref().clone() }).await {
                        return;
                    }
                }
                // Too slow to keep up: drop it, the client reconnects and catches up.
                Err(broadcast::error::RecvError::Lagged(_)) | Err(broadcast::error::RecvError::Closed) => return,
            }
        }
    }
}

// ------------------------------------------------------------------ embedded

/// Server running on a background thread inside the desktop app (LAN host).
pub struct EmbeddedServer {
    stop: Option<oneshot::Sender<()>>,
    thread: Option<std::thread::JoinHandle<()>>,
    pub port: u16,
}

impl EmbeddedServer {
    pub fn start(port: u16, db: &Path, token: String) -> std::io::Result<EmbeddedServer> {
        let store = Arc::new(Store::open(Some(db)).map_err(std::io::Error::other)?);
        let std_listener = std::net::TcpListener::bind(("0.0.0.0", port))?;
        std_listener.set_nonblocking(true)?;
        let port = std_listener.local_addr()?.port();
        let (stop, stop_rx) = oneshot::channel::<()>();
        let thread = std::thread::Builder::new()
            .name("myowneq-host".into())
            .spawn(move || {
                let Ok(rt) = tokio::runtime::Builder::new_multi_thread()
                    .worker_threads(2)
                    .enable_all()
                    .build()
                else {
                    return;
                };
                rt.block_on(async move {
                    let Ok(listener) = TcpListener::from_std(std_listener) else {
                        return;
                    };
                    let _ = serve(listener, store, token, async move {
                        let _ = stop_rx.await;
                    })
                    .await;
                });
            })?;
        Ok(EmbeddedServer {
            stop: Some(stop),
            thread: Some(thread),
            port,
        })
    }
}

impl Drop for EmbeddedServer {
    fn drop(&mut self) {
        if let Some(s) = self.stop.take() {
            let _ = s.send(());
        }
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn op(key: &str, t: u64, m: &str, v: serde_json::Value) -> Op {
        Op {
            key: key.into(),
            stamp: Stamp { t, m: m.into() },
            value: Some(v),
        }
    }

    #[test]
    fn lww_and_winners() {
        let s = Store::open(None).unwrap();
        let r = s.push(vec![op("profile:a", 10, "mac", json!(1))]).unwrap();
        assert_eq!(r.stored.len(), 1);
        // Older write loses and gets the winner back.
        let r = s.push(vec![op("profile:a", 5, "pc", json!(2))]).unwrap();
        assert!(r.stored.is_empty() && r.acked.is_empty());
        assert_eq!(r.winners[0].op.value, Some(json!(1)));
        // Duplicate is acked, not re-stored.
        let r = s.push(vec![op("profile:a", 10, "mac", json!(1))]).unwrap();
        assert_eq!(r.acked.len(), 1);
        assert!(r.stored.is_empty());
        // Newer wins, seq increases.
        s.push(vec![op("profile:a", 11, "pc", json!(3))]).unwrap();
        assert_eq!(s.seq(), 2);
        assert_eq!(s.since(1).unwrap()[0].op.value, Some(json!(3)));
    }

    #[test]
    fn pairing_code_shape() {
        let c = pairing_code();
        assert_eq!(c.len(), 9);
        assert_eq!(&c[4..5], "-");
    }
}
