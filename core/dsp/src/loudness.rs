//! Volume-aware loudness compensation.
//!
//! Human hearing loses bass (and some treble) sensitivity at low listening
//! levels (equal-loudness contours, ISO 226). We approximate the correction
//! with a low shelf and a gentle high shelf whose gain grows as the system
//! volume drops below the reference level.
//!
//! The system volume comes from the OS slider (0..1). macOS and Windows sliders are
//! audio-tapered (50% is roughly -12 dB, not -6 dB), so we map the slider position
//! to attenuation with that taper instead of treating it as linear amplitude.

use crate::biquad::Band;

/// Bass shelf corner (Hz) and treble shelf corner (Hz).
const BASS_HZ: f64 = 100.0;
const TREBLE_HZ: f64 = 10_000.0;
/// Max boosts at full `amount` and very low volume.
const MAX_BASS_DB: f64 = 10.0;
const MAX_TREBLE_DB: f64 = 4.0;
/// Attenuation (dB below reference) at which the max boost is reached.
const FULL_EFFECT_AT_DB: f64 = 30.0;
/// Slider taper: dB of attenuation per decade of slider position (50% ≈ -12 dB).
const TAPER_DB_PER_DECADE: f64 = 40.0;

/// Listening attenuation (dB, >= 0) for an OS volume slider position 0..1.
pub fn slider_attenuation_db(volume: f64) -> f64 {
    -TAPER_DB_PER_DECADE * volume.clamp(1e-4, 1.0).log10()
}

/// `amount` 0..1 (user strength), `volume` 0..1 (OS volume slider). Returns two
/// bands to append to the chain; both are 0 dB at full volume.
pub fn loudness_bands(amount: f64, volume: f64) -> [Band; 2] {
    let amount = amount.clamp(0.0, 1.0);
    let atten_db = slider_attenuation_db(volume); // 0 at full volume
    let t = (atten_db / FULL_EFFECT_AT_DB).clamp(0.0, 1.0) * amount;
    [
        Band::low_shelf(BASS_HZ, MAX_BASS_DB * t, 0.707),
        Band::high_shelf(TREBLE_HZ, MAX_TREBLE_DB * t, 0.707),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flat_at_full_volume_or_zero_amount() {
        for b in loudness_bands(1.0, 1.0)
            .iter()
            .chain(loudness_bands(0.0, 0.05).iter())
        {
            assert_eq!(b.gain_db, 0.0);
        }
    }

    #[test]
    fn audible_at_everyday_volumes() {
        // The old linear mapping gave only +1.5 dB at 50%: nobody could hear it.
        let at = |v| loudness_bands(1.0, v)[0].gain_db;
        assert!(at(0.5) >= 3.5, "50%: {}", at(0.5));
        assert!(at(0.3) >= 6.0, "30%: {}", at(0.3));
        assert!(at(0.15) >= MAX_BASS_DB - 0.01, "15%: {}", at(0.15));
    }

    #[test]
    fn grows_as_volume_drops() {
        let quiet = loudness_bands(1.0, 0.5)[0].gain_db;
        let quieter = loudness_bands(1.0, 0.3)[0].gain_db;
        assert!(quiet > 0.0 && quieter > quiet && quieter <= MAX_BASS_DB);
    }
}
