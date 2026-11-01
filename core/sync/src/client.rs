//! Background WebSocket client. Owns a small tokio runtime on its own
//! thread; the app talks to it through non-blocking channels and polls
//! events from its UI loop.

use crate::proto::{Acked, ClientMsg, Op, Peer, PeerInfo, SeqOp, ServerMsg};
use futures_util::{SinkExt, StreamExt};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{mpsc, Arc};
use std::time::Duration;
use tokio::sync::mpsc as tmpsc;
use tokio_tungstenite::tungstenite::Message;

#[derive(Clone, Debug, PartialEq)]
pub enum Event {
    Connected {
        server_id: String,
        seq: u64,
    },
    Ops(Vec<SeqOp>),
    Ack(Vec<Acked>),
    /// Machines using this server (online now or seen before).
    Peers(Vec<Peer>),
    /// Connection dropped or failed; the client retries with backoff.
    Disconnected(String),
    /// Server refused us (bad token). Retries slowly.
    Rejected(String),
}

enum Cmd {
    Push(Vec<Op>),
    Shutdown,
}

pub struct SyncClient {
    tx: tmpsc::UnboundedSender<Cmd>,
    rx: mpsc::Receiver<Event>,
    since: Arc<AtomicU64>,
    thread: Option<std::thread::JoinHandle<()>>,
    pub url: String,
}

/// Accepts `host`, `host:port`, `ws(s)://…`, `http(s)://…`; returns a ws URL ending in `/sync`.
pub fn normalize_url(input: &str) -> String {
    let s = input.trim().trim_end_matches('/');
    let s = if let Some(r) = s.strip_prefix("https://") {
        format!("wss://{r}")
    } else if let Some(r) = s.strip_prefix("http://") {
        format!("ws://{r}")
    } else if s.starts_with("ws://") || s.starts_with("wss://") {
        s.to_string()
    } else {
        format!("ws://{s}")
    };
    let after_scheme = s.split_once("://").map_or("", |x| x.1);
    let has_port = after_scheme
        .split('/')
        .next()
        .is_some_and(|h| h.contains(':'));
    let mut s = if !has_port && s.starts_with("ws://") && !after_scheme.contains('/') {
        format!("{s}:{}", crate::DEFAULT_PORT)
    } else {
        s
    };
    if !s.ends_with("/sync") {
        s.push_str("/sync");
    }
    s
}

impl SyncClient {
    pub fn start(url: &str, token: &str, machine: &str, info: PeerInfo, since: u64) -> SyncClient {
        let url = normalize_url(url);
        let (tx, mut cmd_rx) = tmpsc::unbounded_channel();
        let (ev_tx, rx) = mpsc::channel();
        let since = Arc::new(AtomicU64::new(since));
        let (u, t, m, s) = (
            url.clone(),
            token.to_string(),
            machine.to_string(),
            since.clone(),
        );
        let thread = std::thread::Builder::new()
            .name("myowneq-sync".into())
            .spawn(move || {
                let rt = match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(rt) => rt,
                    Err(e) => {
                        let _ = ev_tx.send(Event::Disconnected(format!("runtime: {e}")));
                        return;
                    }
                };
                rt.block_on(run(u, t, m, info, s, &mut cmd_rx, ev_tx));
            })
            .ok();
        SyncClient {
            tx,
            rx,
            since,
            thread,
            url,
        }
    }

    /// Queue ops for upload (dropped if offline; the replica's outbox resends on reconnect).
    pub fn push(&self, ops: Vec<Op>) {
        if !ops.is_empty() {
            let _ = self.tx.send(Cmd::Push(ops));
        }
    }

    /// Highest applied seq, used on reconnect.
    pub fn set_since(&self, seq: u64) {
        self.since.store(seq, Ordering::Relaxed);
    }

    pub fn poll(&self) -> Vec<Event> {
        self.rx.try_iter().collect()
    }
}

impl Drop for SyncClient {
    fn drop(&mut self) {
        let _ = self.tx.send(Cmd::Shutdown);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

async fn run(
    url: String,
    token: String,
    machine: String,
    info: PeerInfo,
    since: Arc<AtomicU64>,
    cmds: &mut tmpsc::UnboundedReceiver<Cmd>,
    events: mpsc::Sender<Event>,
) {
    let mut backoff = 1u64;
    loop {
        let outcome = session(&url, &token, &machine, &info, &since, cmds, &events).await;
        let wait = match outcome {
            Outcome::Shutdown => return,
            Outcome::Dropped(e) => {
                let _ = events.send(Event::Disconnected(e));
                backoff
            }
            Outcome::Rejected(e) => {
                let _ = events.send(Event::Rejected(e));
                60
            }
            Outcome::WasConnected(e) => {
                let _ = events.send(Event::Disconnected(e));
                backoff = 1;
                1
            }
        };
        backoff = (backoff * 2).min(30);
        // Sleep, but wake up for shutdown. Pushes while offline are dropped
        // (the outbox is resent after reconnect).
        let sleep = tokio::time::sleep(Duration::from_secs(wait));
        tokio::pin!(sleep);
        loop {
            tokio::select! {
                _ = &mut sleep => break,
                c = cmds.recv() => match c {
                    None | Some(Cmd::Shutdown) => return,
                    Some(Cmd::Push(_)) => {}
                },
            }
        }
    }
}

enum Outcome {
    Shutdown,
    Dropped(String),
    WasConnected(String),
    Rejected(String),
}

async fn session(
    url: &str,
    token: &str,
    machine: &str,
    info: &PeerInfo,
    since: &AtomicU64,
    cmds: &mut tmpsc::UnboundedReceiver<Cmd>,
    events: &mpsc::Sender<Event>,
) -> Outcome {
    let connect = tokio::time::timeout(
        Duration::from_secs(10),
        tokio_tungstenite::connect_async(url),
    )
    .await;
    let ws = match connect {
        Ok(Ok((ws, _))) => ws,
        Ok(Err(e)) => return Outcome::Dropped(e.to_string()),
        Err(_) => return Outcome::Dropped("connect timeout".into()),
    };
    let (mut sink, mut stream) = ws.split();
    let hello = ClientMsg::Hello {
        token: token.into(),
        machine: machine.into(),
        since: since.load(Ordering::Relaxed),
        info: info.clone(),
    };
    if let Err(e) = sink
        .send(Message::Text(
            serde_json::to_string(&hello).unwrap_or_default().into(),
        ))
        .await
    {
        return Outcome::Dropped(e.to_string());
    }
    let mut connected = false;
    let mut ping = tokio::time::interval(Duration::from_secs(20));
    ping.tick().await;
    loop {
        tokio::select! {
            c = cmds.recv() => match c {
                None | Some(Cmd::Shutdown) => {
                    let _ = sink.close().await;
                    return Outcome::Shutdown;
                }
                Some(Cmd::Push(ops)) if connected => {
                    let msg = ClientMsg::Push { ops };
                    if let Err(e) = sink.send(Message::Text(serde_json::to_string(&msg).unwrap_or_default().into())).await {
                        return Outcome::WasConnected(e.to_string());
                    }
                }
                Some(Cmd::Push(_)) => {}
            },
            _ = ping.tick() => {
                if let Err(e) = sink.send(Message::Ping(Vec::new().into())).await {
                    return if connected { Outcome::WasConnected(e.to_string()) } else { Outcome::Dropped(e.to_string()) };
                }
            }
            m = stream.next() => {
                let text = match m {
                    Some(Ok(Message::Text(t))) => t,
                    Some(Ok(Message::Close(_))) | None => {
                        return if connected { Outcome::WasConnected("closed".into()) } else { Outcome::Dropped("closed".into()) };
                    }
                    Some(Ok(_)) => continue,
                    Some(Err(e)) => {
                        return if connected { Outcome::WasConnected(e.to_string()) } else { Outcome::Dropped(e.to_string()) };
                    }
                };
                match serde_json::from_str::<ServerMsg>(text.as_str()) {
                    Ok(ServerMsg::Welcome { seq, server_id }) => {
                        connected = true;
                        let _ = events.send(Event::Connected { server_id, seq });
                    }
                    Ok(ServerMsg::Ops { ops }) => {
                        let _ = events.send(Event::Ops(ops));
                    }
                    Ok(ServerMsg::Ack { acked }) => {
                        let _ = events.send(Event::Ack(acked));
                    }
                    Ok(ServerMsg::Peers { peers }) => {
                        let _ = events.send(Event::Peers(peers));
                    }
                    Ok(ServerMsg::Error { msg }) => {
                        return if connected { Outcome::WasConnected(msg) } else { Outcome::Rejected(msg) };
                    }
                    Err(_) => {}
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::normalize_url;

    #[test]
    fn urls() {
        assert_eq!(normalize_url("192.168.1.5"), "ws://192.168.1.5:7878/sync");
        assert_eq!(
            normalize_url("192.168.1.5:9000"),
            "ws://192.168.1.5:9000/sync"
        );
        assert_eq!(
            normalize_url("https://eq.example.com/"),
            "wss://eq.example.com/sync"
        );
        assert_eq!(normalize_url("ws://h:1/sync"), "ws://h:1/sync");
    }
}
