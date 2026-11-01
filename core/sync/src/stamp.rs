//! Hybrid logical clock stamps.

use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
use std::time::{SystemTime, UNIX_EPOCH};

/// Version of an entity. Ordered by `t`, then machine id. `t` is
/// milliseconds since epoch, bumped past anything already observed so a
/// slightly-behind wall clock still produces newer stamps.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct Stamp {
    pub t: u64,
    pub m: String,
}

impl Stamp {
    /// Stamp of untouched built-in data (identical on every machine).
    pub fn zero() -> Stamp {
        Stamp {
            t: 0,
            m: String::new(),
        }
    }
}

impl Ord for Stamp {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.t, &self.m).cmp(&(other.t, &other.m))
    }
}

impl PartialOrd for Stamp {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Clock {
    pub machine: String,
    pub last: u64,
}

impl Clock {
    pub fn new(machine: impl Into<String>) -> Clock {
        Clock {
            machine: machine.into(),
            last: 0,
        }
    }

    pub fn now(&mut self) -> Stamp {
        let wall = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| d.as_millis() as u64);
        self.last = wall.max(self.last + 1);
        Stamp {
            t: self.last,
            m: self.machine.clone(),
        }
    }

    /// Advance past a stamp seen from another machine.
    pub fn observe(&mut self, s: &Stamp) {
        self.last = self.last.max(s.t);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn monotonic_and_observes() {
        let mut c = Clock::new("a");
        let a = c.now();
        let b = c.now();
        assert!(b > a);
        c.observe(&Stamp {
            t: b.t + 1_000_000,
            m: "z".into(),
        });
        assert!(c.now().t > b.t + 1_000_000);
    }

    #[test]
    fn machine_breaks_ties() {
        let a = Stamp {
            t: 5,
            m: "a".into(),
        };
        let b = Stamp {
            t: 5,
            m: "b".into(),
        };
        assert!(b > a && a > Stamp::zero());
    }
}
