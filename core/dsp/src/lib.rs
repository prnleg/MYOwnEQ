//! MyOwnEQ DSP: biquad filters, frequency response helpers and a
//! real-time safe processing chain (no allocation or locking in `process`).

pub mod biquad;
pub mod crossfeed;
pub mod loudness;
pub mod params;
pub mod processor;
pub mod response;
pub mod spectrum;

pub use biquad::{Band, Coeffs, FilterKind};
pub use crossfeed::Crossfeed;
pub use params::{param_channel, EqParams, ParamReceiver, ParamSender, MAX_BANDS};
pub use processor::{Processor, MAX_CHANNELS};
