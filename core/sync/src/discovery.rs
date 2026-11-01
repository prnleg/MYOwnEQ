//! LAN discovery over mDNS (`_myowneq._tcp.local.`).

use mdns_sd::{ServiceDaemon, ServiceEvent, ServiceInfo};
use std::time::{Duration, Instant};

const SERVICE: &str = "_myowneq._tcp.local.";

#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct Found {
    pub name: String,
    pub addr: String,
}

/// Keeps a LAN host advertised while alive.
pub struct Advertiser {
    daemon: ServiceDaemon,
    fullname: String,
}

impl Advertiser {
    pub fn start(instance: &str, port: u16) -> Result<Advertiser, mdns_sd::Error> {
        let daemon = ServiceDaemon::new()?;
        let host = format!("myowneq-{}.local.", sanitize(instance));
        let info = ServiceInfo::new(
            SERVICE,
            &sanitize(instance),
            &host,
            "",
            port,
            &[("v", "1")][..],
        )?
        .enable_addr_auto();
        let fullname = info.get_fullname().to_string();
        daemon.register(info)?;
        Ok(Advertiser { daemon, fullname })
    }
}

impl Drop for Advertiser {
    fn drop(&mut self) {
        let _ = self.daemon.unregister(&self.fullname);
        let _ = self.daemon.shutdown();
    }
}

fn sanitize(s: &str) -> String {
    let s: String = s
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' {
                c
            } else {
                '-'
            }
        })
        .collect();
    if s.is_empty() {
        "host".into()
    } else {
        s
    }
}

/// Browse the LAN for `timeout`. Blocking; call off the UI thread or with a short timeout.
pub fn browse(timeout: Duration) -> Vec<Found> {
    let Ok(daemon) = ServiceDaemon::new() else {
        return vec![];
    };
    let Ok(rx) = daemon.browse(SERVICE) else {
        return vec![];
    };
    let deadline = Instant::now() + timeout;
    let mut found = Vec::new();
    while let Some(left) = deadline.checked_duration_since(Instant::now()) {
        match rx.recv_timeout(left) {
            Ok(ServiceEvent::ServiceResolved(info)) => {
                let name = info
                    .get_fullname()
                    .trim_end_matches(SERVICE)
                    .trim_end_matches('.')
                    .to_string();
                // Prefer IPv4 for readability.
                let mut addrs: Vec<_> = info.get_addresses().iter().copied().collect();
                addrs.sort_by_key(|a| !a.is_ipv4());
                if let Some(ip) = addrs.first() {
                    let addr = if ip.is_ipv4() {
                        format!("{ip}:{}", info.get_port())
                    } else {
                        format!("[{ip}]:{}", info.get_port())
                    };
                    let f = Found { name, addr };
                    if !found.contains(&f) {
                        found.push(f);
                    }
                }
            }
            Ok(_) => {}
            Err(_) => break,
        }
    }
    let _ = daemon.shutdown();
    found
}
