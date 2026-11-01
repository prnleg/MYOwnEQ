//! Output devices and cross-machine device groups.
//!
//! Every physical output a machine sees is a [`DeviceKey`] (machine + OS UID).
//! Keys are collected into [`DeviceGroup`]s. A fresh device gets its own group;
//! linking "KZ on Mac" with "KZ on PC" merges their groups. All settings
//! (default profile, rules, favorites, active profile) live on the group, so
//! mirroring between machines falls out naturally.

use crate::Id;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub struct MachineId(pub String);

#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub struct DeviceKey {
    pub machine: MachineId,
    /// OS-stable device UID (CoreAudio UID / Windows endpoint ID).
    pub uid: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum DeviceKind {
    BuiltIn,
    Bluetooth,
    Usb,
    Hdmi,
    Analog,
    Virtual,
    #[default]
    Other,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct DeviceMember {
    pub key: DeviceKey,
    /// Name as reported by the OS on that machine.
    pub os_name: String,
    #[serde(default)]
    pub kind: DeviceKind,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct DeviceGroup {
    pub id: Id,
    /// User-facing name ("KZ ZSN Pro").
    pub name: String,
    pub members: Vec<DeviceMember>,
    #[serde(default)]
    pub default_profile: Option<Id>,
    /// Profiles shown as quick favorites for this device.
    #[serde(default)]
    pub favorites: Vec<Id>,
    /// When false, EQ is bypassed for this device.
    #[serde(default = "default_true")]
    pub enabled: bool,
}

fn default_true() -> bool {
    true
}

impl DeviceGroup {
    pub fn single(member: DeviceMember) -> Self {
        DeviceGroup {
            id: crate::new_id(),
            name: member.os_name.clone(),
            members: vec![member],
            default_profile: None,
            favorites: Vec::new(),
            enabled: true,
        }
    }

    pub fn contains(&self, key: &DeviceKey) -> bool {
        self.members.iter().any(|m| &m.key == key)
    }

    pub fn is_linked(&self) -> bool {
        let mut machines: Vec<_> = self.members.iter().map(|m| &m.key.machine).collect();
        machines.sort();
        machines.dedup();
        machines.len() > 1
    }
}
