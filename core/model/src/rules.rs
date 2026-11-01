//! App rules: "when CS2 runs while the KZ is the output, use FPS".
//!
//! Resolution order for a device group:
//! 1. highest-priority matching rule (focused-trigger beats running-trigger
//!    at equal priority), unless the user manually overrode that rule;
//! 2. the group's manually selected profile (this is what mirrors);
//! 3. the group's default profile;
//! 4. none (flat).

use crate::Id;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Platform {
    Mac,
    Windows,
}

impl Platform {
    pub fn current() -> Platform {
        if cfg!(windows) {
            Platform::Windows
        } else {
            Platform::Mac
        }
    }
}

/// Identifies one app on each platform.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct AppMatch {
    /// Display name ("Counter-Strike 2").
    pub name: String,
    /// macOS bundle id ("com.valvesoftware.cs2").
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mac_bundle_id: Option<String>,
    /// Windows executable file name ("cs2.exe").
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub win_exe: Option<String>,
}

impl AppMatch {
    pub fn matches(&self, platform: Platform, app_id: &str) -> bool {
        let want = match platform {
            Platform::Mac => self.mac_bundle_id.as_deref(),
            Platform::Windows => self.win_exe.as_deref(),
        };
        want.is_some_and(|w| w.eq_ignore_ascii_case(app_id))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum Trigger {
    /// Active while the app is running at all.
    #[default]
    Running,
    /// Active only while the app is frontmost.
    Focused,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Rule {
    pub id: Id,
    /// Device group this rule belongs to.
    pub group: Id,
    pub app: AppMatch,
    #[serde(default)]
    pub trigger: Trigger,
    pub profile: Id,
    #[serde(default)]
    pub priority: i32,
    #[serde(default = "default_true")]
    pub enabled: bool,
}

fn default_true() -> bool {
    true
}

/// One running app as seen by the platform watcher.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AppSnapshot {
    /// Bundle id on macOS, exe name on Windows.
    pub id: String,
    pub focused: bool,
}

/// Why a profile was chosen (shown in the UI: "FPS — because CS2 is running").
#[derive(Clone, Debug, PartialEq)]
pub enum Resolution {
    Rule { rule: Id, profile: Id },
    RuleOverride { rule: Id, profile: Id },
    Selected(Id),
    Default(Id),
    Flat,
}

impl Resolution {
    pub fn profile(&self) -> Option<Id> {
        match *self {
            Resolution::Rule { profile, .. }
            | Resolution::RuleOverride { profile, .. }
            | Resolution::Selected(profile)
            | Resolution::Default(profile) => Some(profile),
            Resolution::Flat => None,
        }
    }
}

/// Pick the rule that should currently apply to `group`, if any.
pub fn matching_rule<'a>(
    rules: &'a [Rule],
    group: Id,
    platform: Platform,
    apps: &[AppSnapshot],
) -> Option<&'a Rule> {
    rules
        .iter()
        .enumerate()
        .filter(|(_, r)| r.enabled && r.group == group)
        .filter(|(_, r)| {
            apps.iter().any(|a| {
                r.app.matches(platform, &a.id) && (r.trigger == Trigger::Running || a.focused)
            })
        })
        // max_by_key returns the last max; use (priority, focused, -index) so
        // earlier rules win ties.
        .max_by_key(|(i, r)| {
            (
                r.priority,
                r.trigger == Trigger::Focused,
                std::cmp::Reverse(*i),
            )
        })
        .map(|(_, r)| r)
}

/// Machine-local override: user manually picked `profile` while `rule` was active.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct RuleOverride {
    pub rule: Id,
    pub profile: Id,
}

pub fn resolve(
    rule: Option<&Rule>,
    rule_override: Option<RuleOverride>,
    selected: Option<Id>,
    default_profile: Option<Id>,
) -> Resolution {
    if let Some(r) = rule {
        if let Some(o) = rule_override.filter(|o| o.rule == r.id) {
            return Resolution::RuleOverride {
                rule: r.id,
                profile: o.profile,
            };
        }
        return Resolution::Rule {
            rule: r.id,
            profile: r.profile,
        };
    }
    if let Some(p) = selected {
        return Resolution::Selected(p);
    }
    if let Some(p) = default_profile {
        return Resolution::Default(p);
    }
    Resolution::Flat
}
