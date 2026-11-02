//! Fixed-size parameter snapshot passed from UI/control thread to the audio
//! thread through a lock-free SPSC ring buffer.

use crate::biquad::Band;
use crate::crossfeed::Crossfeed;

/// Upper bound on bands in one chain (AutoEQ base + 31-band graphic + macros fits).
pub const MAX_BANDS: usize = 64;

/// Complete, `Copy` description of what the audio thread should do.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EqParams {
    pub bands: [Band; MAX_BANDS],
    pub band_count: usize,
    pub preamp_db: f64,
    /// -1.0 = full left, 0.0 = center, 1.0 = full right.
    pub balance: f64,
    pub limiter: bool,
    pub bypass: bool,
    /// Headphone crossfeed (stereo only), after the EQ, before the limiter.
    pub crossfeed: Crossfeed,
}

impl Default for EqParams {
    fn default() -> Self {
        EqParams {
            bands: [Band::DISABLED; MAX_BANDS],
            band_count: 0,
            preamp_db: 0.0,
            balance: 0.0,
            limiter: true,
            bypass: false,
            crossfeed: Crossfeed::Off,
        }
    }
}

impl EqParams {
    /// Build from a band slice. Extra bands beyond `MAX_BANDS` are dropped
    /// (returned count tells the caller how many were kept).
    pub fn from_bands(bands: &[Band]) -> Self {
        let mut p = EqParams::default();
        p.set_bands(bands);
        p
    }

    pub fn set_bands(&mut self, bands: &[Band]) -> usize {
        let n = bands.len().min(MAX_BANDS);
        self.bands[..n].copy_from_slice(&bands[..n]);
        for b in &mut self.bands[n..] {
            *b = Band::DISABLED;
        }
        self.band_count = n;
        n
    }

    pub fn active_bands(&self) -> &[Band] {
        &self.bands[..self.band_count]
    }
}

pub struct ParamSender(rtrb::Producer<EqParams>);
pub struct ParamReceiver(rtrb::Consumer<EqParams>);

/// Create a sender/receiver pair. Sender lives on the control thread,
/// receiver is owned by the audio callback.
pub fn param_channel() -> (ParamSender, ParamReceiver) {
    let (p, c) = rtrb::RingBuffer::new(16);
    (ParamSender(p), ParamReceiver(c))
}

impl ParamSender {
    /// Non-blocking. Returns false if the queue is full (audio thread stalled);
    /// caller may retry later with the newest params.
    pub fn send(&mut self, params: EqParams) -> bool {
        self.0.push(params).is_ok()
    }
}

impl ParamReceiver {
    /// Drain the queue and return the newest snapshot, if any. RT-safe.
    #[inline]
    pub fn latest(&mut self) -> Option<EqParams> {
        let mut last = None;
        while let Ok(p) = self.0.pop() {
            last = Some(p);
        }
        last
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn receiver_returns_newest() {
        let (mut tx, mut rx) = param_channel();
        assert!(rx.latest().is_none());
        for g in 1..=3 {
            let p = EqParams {
                preamp_db: g as f64,
                ..Default::default()
            };
            assert!(tx.send(p));
        }
        assert_eq!(rx.latest().unwrap().preamp_db, 3.0);
        assert!(rx.latest().is_none());
    }

    #[test]
    fn truncates_extra_bands() {
        let many = vec![Band::peak(1000.0, 1.0, 1.0); MAX_BANDS + 5];
        assert_eq!(EqParams::from_bands(&many).band_count, MAX_BANDS);
    }
}
