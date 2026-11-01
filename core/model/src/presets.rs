//! Built-in presets for Easy mode. Ids are deterministic so the same preset
//! is the same entity on every machine (no duplicates after sync).

use crate::profile::{Macros, Profile};
use crate::Id;
use myowneq_dsp::{Band, FilterKind};

fn preset_id(n: u8) -> Id {
    // Fixed namespace: 6d796f77-6e65-7100-0000-0000000000NN ("myowneq")
    uuid::Uuid::from_bytes([
        0x6d, 0x79, 0x6f, 0x77, 0x6e, 0x65, 0x71, 0x00, 0, 0, 0, 0, 0, 0, 0, n,
    ])
}

fn preset(n: u8, name: &str, bands: Vec<Band>, macros: Macros) -> Profile {
    let mut p = Profile::new(name);
    p.id = preset_id(n);
    p.eq = crate::profile::EqLayer::Parametric { bands };
    p.macros = macros;
    p.tags = vec!["builtin".into()];
    p
}

pub fn builtin() -> Vec<Profile> {
    vec![
        preset(1, "Flat", vec![], Macros::default()),
        preset(
            2,
            "Bass Boost",
            vec![Band::low_shelf(105.0, 6.0, 0.707)],
            Macros::default(),
        ),
        // Footsteps live around 1–5 kHz; tame rumble/explosions, lift presence.
        preset(
            3,
            "FPS",
            vec![
                Band {
                    kind: FilterKind::HighPass,
                    freq: 35.0,
                    gain_db: 0.0,
                    q: 0.707,
                    enabled: true,
                },
                Band::low_shelf(150.0, -4.0, 0.707),
                Band::peak(2500.0, 4.0, 1.0),
                Band::peak(5000.0, 3.0, 1.2),
                Band::high_shelf(10000.0, -1.5, 0.707),
            ],
            Macros::default(),
        ),
        preset(
            4,
            "Music",
            vec![
                Band::low_shelf(90.0, 3.5, 0.707),
                Band::peak(2000.0, -1.0, 0.8),
                Band::high_shelf(9000.0, 2.5, 0.707),
            ],
            Macros::default(),
        ),
        preset(
            5,
            "Vocal",
            vec![
                Band::low_shelf(150.0, -2.0, 0.707),
                Band::peak(1500.0, 2.0, 0.9),
                Band::peak(3500.0, 3.0, 1.2),
            ],
            Macros::default(),
        ),
        preset(
            6,
            "Movie",
            vec![
                Band::low_shelf(80.0, 4.0, 0.707),
                Band::peak(2500.0, 2.0, 1.0),
                Band::high_shelf(10000.0, 1.0, 0.707),
            ],
            Macros::default(),
        ),
        preset(
            7,
            "Podcast",
            vec![
                Band {
                    kind: FilterKind::HighPass,
                    freq: 80.0,
                    gain_db: 0.0,
                    q: 0.707,
                    enabled: true,
                },
                Band::peak(250.0, -2.0, 1.0),
                Band::peak(3000.0, 3.0, 1.0),
            ],
            Macros::default(),
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
        ids.dedup();
        assert_eq!(ids.len(), a.len());
    }
}
