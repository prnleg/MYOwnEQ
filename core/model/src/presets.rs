//! Built-in presets. Ids are deterministic so the same preset is the same
//! entity on every machine (no duplicates after sync).
//!
//! Presets are read-only: the library ignores writes and deletes to any id in
//! the preset namespace and rewrites them from these definitions on load, so
//! a bug, an import or a sync from an older app can never change them.
//! Editing a preset in the apps creates a copy instead.

use crate::profile::{EqLayer, Profile};
use crate::Id;
use myowneq_dsp::{Band, FilterKind};

/// Fixed namespace: 6d796f77-6e65-7100-0000-0000000000NN ("myowneq").
const NS: [u8; 8] = [0x6d, 0x79, 0x6f, 0x77, 0x6e, 0x65, 0x71, 0x00];

fn preset_id(n: u8) -> Id {
    let mut b = [0u8; 16];
    b[..8].copy_from_slice(&NS);
    b[15] = n;
    uuid::Uuid::from_bytes(b)
}

/// True for any id in the preset namespace (including presets added by newer
/// versions of the app).
pub fn is_preset(id: Id) -> bool {
    let b = id.as_bytes();
    b[..8] == NS && b[8..15].iter().all(|&x| x == 0)
}

fn preset(n: u8, name: &str, bands: Vec<Band>) -> Profile {
    let mut p = Profile::new(name);
    p.id = preset_id(n);
    p.eq = EqLayer::Parametric { bands };
    p.tags = vec!["builtin".into()];
    p
}

fn high_pass(freq: f64) -> Band {
    Band {
        kind: FilterKind::HighPass,
        freq,
        gain_db: 0.0,
        q: 0.707,
        enabled: true,
    }
}

/// Every built-in preset, in display order. Never renumber: the number is the
/// identity across machines. Add new ones with the next free number.
pub fn builtin() -> Vec<Profile> {
    let ls = Band::low_shelf;
    let pk = Band::peak;
    let hs = Band::high_shelf;
    let mut late_night = preset(
        16,
        "Late Night",
        vec![
            high_pass(40.0),
            ls(100.0, -3.0, 0.707),
            pk(3000.0, 2.0, 1.0),
        ],
    );
    late_night.loudness = 0.5;
    vec![
        preset(1, "Flat", vec![]),
        // --- music ---
        preset(
            4,
            "Music",
            vec![
                ls(90.0, 3.5, 0.707),
                pk(2000.0, -1.0, 0.8),
                hs(9000.0, 2.5, 0.707),
            ],
        ),
        preset(
            8,
            "V-Shape",
            vec![
                ls(100.0, 5.0, 0.707),
                pk(1000.0, -3.0, 0.7),
                hs(8000.0, 4.0, 0.707),
            ],
        ),
        preset(
            9,
            "Rock",
            vec![
                ls(100.0, 3.0, 0.707),
                pk(400.0, -1.5, 1.0),
                pk(3000.0, 2.5, 1.0),
                hs(10000.0, 2.0, 0.707),
            ],
        ),
        preset(
            10,
            "Electronic",
            vec![
                ls(60.0, 5.0, 0.707),
                pk(300.0, -2.0, 1.0),
                hs(8000.0, 3.0, 0.707),
            ],
        ),
        preset(
            11,
            "Hip-Hop",
            vec![
                ls(70.0, 6.0, 0.707),
                pk(500.0, -2.0, 1.0),
                pk(3000.0, 1.5, 1.2),
            ],
        ),
        preset(
            12,
            "Acoustic",
            vec![
                ls(100.0, 1.5, 0.707),
                pk(250.0, -1.0, 1.0),
                pk(2500.0, 2.0, 1.0),
                hs(12000.0, 1.5, 0.707),
            ],
        ),
        preset(
            13,
            "Classical",
            vec![
                ls(80.0, 1.0, 0.707),
                pk(4000.0, -1.0, 0.8),
                hs(12000.0, 2.0, 0.707),
            ],
        ),
        // --- tone fixes ---
        preset(2, "Bass Boost", vec![ls(105.0, 6.0, 0.707)]),
        preset(15, "Bass Cut", vec![ls(120.0, -6.0, 0.707)]),
        preset(14, "Treble Boost", vec![hs(6000.0, 5.0, 0.707)]),
        preset(18, "Treble Cut", vec![hs(8000.0, -4.0, 0.707)]),
        // --- games, video, voice ---
        // Footsteps live around 1–5 kHz; tame rumble/explosions, lift presence.
        preset(
            3,
            "FPS",
            vec![
                high_pass(35.0),
                ls(150.0, -4.0, 0.707),
                pk(2500.0, 4.0, 1.0),
                pk(5000.0, 3.0, 1.2),
                hs(10000.0, -1.5, 0.707),
            ],
        ),
        preset(
            6,
            "Movie",
            vec![
                ls(80.0, 4.0, 0.707),
                pk(2500.0, 2.0, 1.0),
                hs(10000.0, 1.0, 0.707),
            ],
        ),
        preset(
            5,
            "Vocal",
            vec![
                ls(150.0, -2.0, 0.707),
                pk(1500.0, 2.0, 0.9),
                pk(3500.0, 3.0, 1.2),
            ],
        ),
        preset(
            7,
            "Podcast",
            vec![high_pass(80.0), pk(250.0, -2.0, 1.0), pk(3000.0, 3.0, 1.0)],
        ),
        late_night,
        // --- speakers ---
        preset(
            17,
            "Laptop Speakers",
            vec![
                high_pass(120.0),
                pk(250.0, -2.0, 1.0),
                pk(3000.0, 3.0, 1.0),
                hs(10000.0, 2.0, 0.707),
            ],
        ),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_are_stable_and_unique() {
        let a = builtin();
        let b = builtin();
        assert_eq!(
            a.iter().map(|p| p.id).collect::<Vec<_>>(),
            b.iter().map(|p| p.id).collect::<Vec<_>>()
        );
        let mut ids: Vec<_> = a.iter().map(|p| p.id).collect();
        ids.sort();
        ids.dedup();
        assert_eq!(ids.len(), a.len());
    }

    #[test]
    fn every_preset_is_in_the_namespace_and_tagged() {
        for p in builtin() {
            assert!(is_preset(p.id), "{}", p.name);
            assert!(p.tags.iter().any(|t| t == "builtin"), "{}", p.name);
        }
        assert!(!is_preset(uuid::Uuid::new_v4()));
    }

    /// The original presets must keep the tuning they shipped with.
    #[test]
    fn original_tunings_are_unchanged() {
        let get = |n: &str| builtin().into_iter().find(|p| p.name == n).unwrap();
        let bands = |p: &Profile| match &p.eq {
            EqLayer::Parametric { bands } => bands
                .iter()
                .map(|b| (b.freq, b.gain_db))
                .collect::<Vec<_>>(),
            EqLayer::Graphic { .. } => panic!("presets are parametric"),
        };
        assert!(bands(&get("Flat")).is_empty());
        assert_eq!(bands(&get("Bass Boost")), vec![(105.0, 6.0)]);
        assert_eq!(
            bands(&get("FPS")),
            vec![
                (35.0, 0.0),
                (150.0, -4.0),
                (2500.0, 4.0),
                (5000.0, 3.0),
                (10000.0, -1.5)
            ]
        );
        assert_eq!(
            bands(&get("Vocal")),
            vec![(150.0, -2.0), (1500.0, 2.0), (3500.0, 3.0)]
        );
        assert_eq!(
            bands(&get("Music")),
            vec![(90.0, 3.5), (2000.0, -1.0), (9000.0, 2.5)]
        );
        assert_eq!(
            bands(&get("Movie")),
            vec![(80.0, 4.0), (2500.0, 2.0), (10000.0, 1.0)]
        );
        assert_eq!(
            bands(&get("Podcast")),
            vec![(80.0, 0.0), (250.0, -2.0), (3000.0, 3.0)]
        );
        assert_eq!(get("FPS").id, preset_id(3));
    }
}
