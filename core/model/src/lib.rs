//! MyOwnEQ data model: profiles, devices, app rules, the persisted library,
//! built-in presets and import/export formats.

pub mod device;
pub mod formats;
pub mod library;
pub mod presets;
pub mod profile;
pub mod rules;

pub use device::{DeviceGroup, DeviceKey, DeviceKind, MachineId};
pub use library::{Library, LibraryError, SCHEMA_VERSION};
pub use profile::{EqLayer, GraphicPoint, Macros, Preamp, Profile, RenderContext};
pub use rules::{AppMatch, AppSnapshot, Platform, Resolution, Rule, Trigger};

pub type Id = uuid::Uuid;

pub fn new_id() -> Id {
    uuid::Uuid::new_v4()
}
