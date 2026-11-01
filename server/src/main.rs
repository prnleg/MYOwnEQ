//! Standalone sync server.
//!
//! Env:
//!   MYOWNEQ_TOKEN  required, shared secret typed into each app
//!   MYOWNEQ_DB     SQLite path (default ./myowneq.db, /data/myowneq.db in Docker)
//!   MYOWNEQ_BIND   listen address (default 0.0.0.0:7878)

use myowneq_server::{serve, Store};
use std::path::PathBuf;
use std::sync::Arc;

#[tokio::main]
async fn main() {
    let token = std::env::var("MYOWNEQ_TOKEN").unwrap_or_default();
    if token.trim().len() < 8 {
        eprintln!("MYOWNEQ_TOKEN must be set (8+ characters). Example: MYOWNEQ_TOKEN=$(openssl rand -hex 16)");
        std::process::exit(2);
    }
    let db = PathBuf::from(std::env::var("MYOWNEQ_DB").unwrap_or_else(|_| "myowneq.db".into()));
    let bind = std::env::var("MYOWNEQ_BIND")
        .unwrap_or_else(|_| format!("0.0.0.0:{}", myowneq_sync::DEFAULT_PORT));

    let store = match Store::open(Some(&db)) {
        Ok(s) => Arc::new(s),
        Err(e) => {
            eprintln!("cannot open {}: {e}", db.display());
            std::process::exit(1);
        }
    };
    let listener = match tokio::net::TcpListener::bind(&bind).await {
        Ok(l) => l,
        Err(e) => {
            eprintln!("cannot bind {bind}: {e}");
            std::process::exit(1);
        }
    };
    println!(
        "MyOwnEQ sync server {} on {bind} (db {}, server id {})",
        env!("CARGO_PKG_VERSION"),
        db.display(),
        store.server_id
    );
    let shutdown = async {
        let _ = tokio::signal::ctrl_c().await;
        println!("shutting down");
    };
    if let Err(e) = serve(listener, store, token.trim().to_string(), shutdown).await {
        eprintln!("server error: {e}");
        std::process::exit(1);
    }
}
