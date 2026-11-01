//! Splitting a [`Library`] into syncable entities and applying them back.
//!
//! Keys: `profile:<id>`, `group:<id>`, `rule:<id>`, `selected:<group id>`.

use myowneq_model::{Id, Library};
use serde_json::Value;
use std::collections::BTreeMap;

pub type Snapshot = BTreeMap<String, Value>;

pub fn snapshot(lib: &Library) -> Snapshot {
    let mut m = Snapshot::new();
    for p in &lib.profiles {
        m.insert(
            format!("profile:{}", p.id),
            serde_json::to_value(p).expect("profile json"),
        );
    }
    for g in &lib.groups {
        m.insert(
            format!("group:{}", g.id),
            serde_json::to_value(g).expect("group json"),
        );
    }
    for r in &lib.rules {
        m.insert(
            format!("rule:{}", r.id),
            serde_json::to_value(r).expect("rule json"),
        );
    }
    for (g, p) in &lib.selected {
        m.insert(format!("selected:{g}"), Value::String(p.to_string()));
    }
    m
}

/// Entities that differ between two snapshots: `(key, new value or None if deleted)`.
pub fn diff(old: &Snapshot, new: &Snapshot) -> Vec<(String, Option<Value>)> {
    let mut out = Vec::new();
    for (k, v) in new {
        if old.get(k) != Some(v) {
            out.push((k.clone(), Some(v.clone())));
        }
    }
    for k in old.keys() {
        if !new.contains_key(k) {
            out.push((k.clone(), None));
        }
    }
    out
}

#[derive(Debug, thiserror::Error)]
pub enum ApplyError {
    #[error("bad key {0}")]
    BadKey(String),
    #[error("bad value for {0}: {1}")]
    BadValue(String, serde_json::Error),
}

fn upsert<T: Clone>(list: &mut Vec<T>, id: Id, get: impl Fn(&T) -> Id, v: Option<T>) {
    let pos = list.iter().position(|e| get(e) == id);
    match (pos, v) {
        (Some(i), Some(v)) => list[i] = v,
        (None, Some(v)) => list.push(v),
        (Some(i), None) => {
            list.remove(i);
        }
        (None, None) => {}
    }
}

/// Apply one entity (or its deletion) to the library.
pub fn apply(lib: &mut Library, key: &str, value: Option<&Value>) -> Result<(), ApplyError> {
    let (kind, id) = key
        .split_once(':')
        .ok_or_else(|| ApplyError::BadKey(key.into()))?;
    let id: Id = id.parse().map_err(|_| ApplyError::BadKey(key.into()))?;
    fn parse<T: serde::de::DeserializeOwned>(
        key: &str,
        v: Option<&Value>,
    ) -> Result<Option<T>, ApplyError> {
        v.map(|v| {
            serde_json::from_value(v.clone()).map_err(|e| ApplyError::BadValue(key.into(), e))
        })
        .transpose()
    }
    match kind {
        "profile" => upsert(&mut lib.profiles, id, |p| p.id, parse(key, value)?),
        "group" => upsert(&mut lib.groups, id, |g| g.id, parse(key, value)?),
        "rule" => upsert(&mut lib.rules, id, |r| r.id, parse(key, value)?),
        "selected" => match parse::<Id>(key, value)? {
            Some(p) => {
                lib.selected.insert(id, p);
            }
            None => {
                lib.selected.remove(&id);
            }
        },
        _ => return Err(ApplyError::BadKey(key.into())),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use myowneq_model::Profile;

    #[test]
    fn diff_and_apply_roundtrip() {
        let a = Library::with_presets();
        let mut b = a.clone();
        b.profiles[0].name = "Renamed".into();
        b.profiles.remove(1);
        b.profiles.push(Profile::new("New"));
        let gid = myowneq_model::new_id();
        b.selected.insert(gid, b.profiles[0].id);

        let changes = diff(&snapshot(&a), &snapshot(&b));
        assert_eq!(changes.len(), 4);

        let mut c = a.clone();
        for (k, v) in &changes {
            apply(&mut c, k, v.as_ref()).unwrap();
        }
        assert_eq!(snapshot(&c), snapshot(&b));
    }
}
