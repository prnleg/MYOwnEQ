//! Share codes: a whole profile packed into a short text, `MEQ1-<base64url>`.
//!
//! No server: the code *is* the EQ. Paste it anywhere (chat, Reddit, an issue) and
//! anyone can import it. Values are quantized finely enough to be inaudible
//! (frequency ~0.01%, gain 0.01 dB, Q ~0.02%), so a profile round-trips by ear.
//!
//! Layout (v1, little endian), then a Fletcher-16 checksum of everything before it:
//! ```text
//! u8   flags   bit0 limiter, bit1 manual preamp, bit2 graphic layout,
//!              bit3 has comfort, bit4 has macros (absent = all zero)
//! str  name                          (u8 length + UTF-8, max 48 bytes)
//! i16  preamp  (0.01 dB)             only with bit1
//! i8   balance (×100)                } only with bit3
//! u8   loudness (×100)               }
//! u8   crossfeed (0 off … 3 strong)  }
//! i16×5 macros bass, mids, clarity, treble, tilt (0.01 dB)   only with bit4
//! eq   parametric: u8 count + bands
//!      graphic:    u8 grid (10 / 31 = standard, 0 = custom) + u8 count +
//!                  gains i16 (+ u16 log freq each when custom)
//! u8   correction band count + bands, str correction name ("" = none)
//! band = u8 (kind | enabled<<7) + u16 log freq + i16 gain + u16 log Q
//! ```

use crate::profile::{
    CorrectionSource, EqLayer, GraphicPoint, Macros, Preamp, Profile, GRAPHIC_10, GRAPHIC_31,
};
use base64::Engine;
use myowneq_dsp::{Band, Crossfeed, FilterKind};

pub const PREFIX: &str = "MEQ1-";
const MAX_NAME: usize = 48;
const F_LO: f64 = 10.0;
const F_HI: f64 = 24_000.0;
const Q_LO: f64 = 0.02;
const Q_HI: f64 = 100.0;

#[derive(Debug, PartialEq, Eq, thiserror::Error)]
pub enum ShareError {
    #[error("not a MyOwnEQ code")]
    NotACode,
    #[error("code is damaged or incomplete (copy it again)")]
    Damaged,
    #[error("code is from a newer MyOwnEQ, update the app")]
    Newer,
}

/// Encode `p` into a share code.
pub fn encode(p: &Profile) -> String {
    let mut w = Vec::with_capacity(96);
    let graphic = matches!(p.eq, EqLayer::Graphic { .. });
    let manual = matches!(p.preamp, Preamp::Manual(_));
    let balance = (p.balance.clamp(-1.0, 1.0) * 100.0).round() as i8;
    let loudness = (p.loudness.clamp(0.0, 1.0) * 100.0).round() as u8;
    let crossfeed = match p.crossfeed {
        Crossfeed::Off => 0,
        Crossfeed::Light => 1,
        Crossfeed::Normal => 2,
        Crossfeed::Strong => 3,
    };
    let comfort = balance != 0 || loudness != 0 || crossfeed != 0;
    let m = &p.macros;
    let macros = [m.bass, m.mids, m.clarity, m.treble, m.tilt].map(cdb);
    let has_macros = macros.iter().any(|&v| v != 0);
    w.push(
        u8::from(p.limiter)
            | u8::from(manual) << 1
            | u8::from(graphic) << 2
            | u8::from(comfort) << 3
            | u8::from(has_macros) << 4,
    );
    put_str(&mut w, &p.name);
    if let Preamp::Manual(db) = p.preamp {
        put_i16(&mut w, cdb(db));
    }
    if comfort {
        w.extend_from_slice(&[balance as u8, loudness, crossfeed]);
    }
    if has_macros {
        for v in macros {
            put_i16(&mut w, v);
        }
    }
    match &p.eq {
        EqLayer::Parametric { bands } => put_bands(&mut w, bands),
        EqLayer::Graphic { points } => {
            let freqs: Vec<f64> = points.iter().map(|pt| pt.freq).collect();
            let grid = if freqs == GRAPHIC_10 {
                10
            } else if freqs == GRAPHIC_31 {
                31
            } else {
                0
            };
            w.push(grid);
            let n = points.len().min(255);
            w.push(n as u8);
            for pt in &points[..n] {
                put_i16(&mut w, cdb(pt.gain_db));
                if grid == 0 {
                    put_u16(&mut w, log_q(pt.freq, F_LO, F_HI));
                }
            }
        }
    }
    put_bands(&mut w, &p.correction);
    put_str(
        &mut w,
        p.correction_source.as_ref().map_or("", |s| s.name.as_str()),
    );
    let sum = fletcher16(&w);
    w.extend_from_slice(&sum.to_le_bytes());
    format!(
        "{PREFIX}{}",
        base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(w)
    )
}

/// First share code found in `text` (it may be inside a longer message).
pub fn find(text: &str) -> Option<&str> {
    let start = text.find(PREFIX).or_else(|| text.find("MEQ"))?;
    let rest = &text[start..];
    let end = rest
        .find(|c: char| !(c.is_ascii_alphanumeric() || c == '-' || c == '_'))
        .unwrap_or(rest.len());
    Some(&rest[..end])
}

/// Decode a share code into a new profile (fresh id, no tags).
pub fn decode(text: &str) -> Result<Profile, ShareError> {
    let code = find(text.trim()).ok_or(ShareError::NotACode)?;
    let body = match code.strip_prefix(PREFIX) {
        Some(b) => b,
        None if code.starts_with("MEQ") => return Err(ShareError::Newer),
        None => return Err(ShareError::NotACode),
    };
    let raw = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(body)
        .map_err(|_| ShareError::Damaged)?;
    if raw.len() < 3 {
        return Err(ShareError::Damaged);
    }
    let (data, sum) = raw.split_at(raw.len() - 2);
    if fletcher16(data) != u16::from_le_bytes([sum[0], sum[1]]) {
        return Err(ShareError::Damaged);
    }
    read(data).ok_or(ShareError::Damaged)
}

fn read(data: &[u8]) -> Option<Profile> {
    let mut r = Reader { d: data, i: 0 };
    let flags = r.u8()?;
    let mut p = Profile::new(r.str()?);
    p.limiter = flags & 1 != 0;
    if flags & 2 != 0 {
        p.preamp = Preamp::Manual(db(r.i16()?));
    }
    if flags & 8 != 0 {
        p.balance = (r.u8()? as i8) as f64 / 100.0;
        p.loudness = r.u8()? as f64 / 100.0;
        p.crossfeed = match r.u8()? {
            1 => Crossfeed::Light,
            2 => Crossfeed::Normal,
            3 => Crossfeed::Strong,
            _ => Crossfeed::Off,
        };
    }
    if flags & 16 != 0 {
        p.macros = Macros {
            bass: db(r.i16()?),
            mids: db(r.i16()?),
            clarity: db(r.i16()?),
            treble: db(r.i16()?),
            tilt: db(r.i16()?),
        };
    }
    p.eq = if flags & 4 != 0 {
        let grid = r.u8()?;
        let n = r.u8()? as usize;
        let mut points = Vec::with_capacity(n);
        for i in 0..n {
            let gain_db = db(r.i16()?);
            let freq = match grid {
                10 => *GRAPHIC_10.get(i)?,
                31 => *GRAPHIC_31.get(i)?,
                _ => unlog_q(r.u16()?, F_LO, F_HI),
            };
            points.push(GraphicPoint { freq, gain_db });
        }
        EqLayer::Graphic { points }
    } else {
        EqLayer::Parametric { bands: r.bands()? }
    };
    p.correction = r.bands()?;
    let src = r.str()?;
    if !src.is_empty() || !p.correction.is_empty() {
        p.correction_source = Some(CorrectionSource {
            provider: "MyOwnEQ code".into(),
            name: src,
        });
    }
    (r.i == data.len()).then_some(p)
}

// ------------------------------------------------------------- encoding bits

fn cdb(v: f64) -> i16 {
    (v.clamp(-300.0, 300.0) * 100.0).round() as i16
}

fn db(v: i16) -> f64 {
    v as f64 / 100.0
}

/// Log-scale `v` in lo..hi to 0..=65535.
fn log_q(v: f64, lo: f64, hi: f64) -> u16 {
    let t = (v.clamp(lo, hi) / lo).ln() / (hi / lo).ln();
    (t * 65535.0).round() as u16
}

fn unlog_q(q: u16, lo: f64, hi: f64) -> f64 {
    let v = lo * (hi / lo).powf(q as f64 / 65535.0);
    // Undo quantization noise on "nice" values (1000 Hz, Q 0.707…).
    let r = (v * 1000.0).round() / 1000.0;
    if (r - v).abs() / v < 2e-4 {
        r
    } else {
        v
    }
}

fn kind_id(k: FilterKind) -> u8 {
    match k {
        FilterKind::Peak => 0,
        FilterKind::LowShelf => 1,
        FilterKind::HighShelf => 2,
        FilterKind::LowPass => 3,
        FilterKind::HighPass => 4,
        FilterKind::Notch => 5,
        FilterKind::BandPass => 6,
    }
}

fn kind_of(id: u8) -> Option<FilterKind> {
    Some(match id {
        0 => FilterKind::Peak,
        1 => FilterKind::LowShelf,
        2 => FilterKind::HighShelf,
        3 => FilterKind::LowPass,
        4 => FilterKind::HighPass,
        5 => FilterKind::Notch,
        6 => FilterKind::BandPass,
        _ => return None,
    })
}

fn put_i16(w: &mut Vec<u8>, v: i16) {
    w.extend_from_slice(&v.to_le_bytes());
}

fn put_u16(w: &mut Vec<u8>, v: u16) {
    w.extend_from_slice(&v.to_le_bytes());
}

fn put_str(w: &mut Vec<u8>, s: &str) {
    let mut end = s.len().min(MAX_NAME);
    while !s.is_char_boundary(end) {
        end -= 1;
    }
    w.push(end as u8);
    w.extend_from_slice(&s.as_bytes()[..end]);
}

fn put_bands(w: &mut Vec<u8>, bands: &[Band]) {
    let n = bands.len().min(255);
    w.push(n as u8);
    for b in &bands[..n] {
        w.push(kind_id(b.kind) | u8::from(b.enabled) << 7);
        put_u16(w, log_q(b.freq, F_LO, F_HI));
        put_i16(w, cdb(b.gain_db));
        put_u16(w, log_q(b.q, Q_LO, Q_HI));
    }
}

fn fletcher16(d: &[u8]) -> u16 {
    let (mut a, mut b) = (0u16, 0u16);
    for &x in d {
        a = (a + x as u16) % 255;
        b = (b + a) % 255;
    }
    b << 8 | a
}

struct Reader<'a> {
    d: &'a [u8],
    i: usize,
}

impl Reader<'_> {
    fn u8(&mut self) -> Option<u8> {
        let v = *self.d.get(self.i)?;
        self.i += 1;
        Some(v)
    }

    fn u16(&mut self) -> Option<u16> {
        Some(u16::from_le_bytes([self.u8()?, self.u8()?]))
    }

    fn i16(&mut self) -> Option<i16> {
        Some(self.u16()? as i16)
    }

    fn str(&mut self) -> Option<String> {
        let n = self.u8()? as usize;
        let s = self.d.get(self.i..self.i + n)?;
        self.i += n;
        String::from_utf8(s.to_vec()).ok()
    }

    fn bands(&mut self) -> Option<Vec<Band>> {
        let n = self.u8()? as usize;
        (0..n)
            .map(|_| {
                let k = self.u8()?;
                Some(Band {
                    kind: kind_of(k & 0x7f)?,
                    enabled: k & 0x80 != 0,
                    freq: unlog_q(self.u16()?, F_LO, F_HI),
                    gain_db: db(self.i16()?),
                    q: unlog_q(self.u16()?, Q_LO, Q_HI),
                })
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> Profile {
        let mut p = Profile::new("Gaming – KZ ZSN 🎧");
        p.eq = EqLayer::Parametric {
            bands: vec![
                Band::low_shelf(105.0, 4.5, 0.707),
                Band::peak(2500.0, -3.25, 1.41),
                Band::peak(6800.0, 2.0, 4.0),
                Band {
                    enabled: false,
                    ..Band::high_shelf(10_000.0, -2.0, 0.7)
                },
            ],
        };
        p.macros.tilt = -1.5;
        p.macros.bass = 2.0;
        p.preamp = Preamp::Manual(-4.5);
        p.balance = -0.1;
        p.loudness = 0.4;
        p.crossfeed = Crossfeed::Normal;
        p.limiter = false;
        p.correction = vec![
            Band::peak(30.0, 6.2, 0.5),
            Band::high_shelf(9000.0, -3.1, 0.9),
        ];
        p.correction_source = Some(CorrectionSource {
            provider: "AutoEQ".into(),
            name: "KZ ZSN Pro".into(),
        });
        p
    }

    fn close(a: f64, b: f64, rel: f64) -> bool {
        (a - b).abs() <= rel * a.abs().max(1e-9)
    }

    #[test]
    fn round_trips_by_ear() {
        let p = sample();
        let code = encode(&p);
        assert!(code.starts_with(PREFIX));
        let q = decode(&code).unwrap();
        assert_ne!(q.id, p.id, "fresh id");
        assert_eq!(q.name, p.name);
        assert_eq!(q.preamp, p.preamp);
        assert_eq!(
            (q.balance, q.loudness, q.crossfeed, q.limiter),
            (p.balance, p.loudness, p.crossfeed, p.limiter)
        );
        assert_eq!(q.macros, p.macros);
        let (EqLayer::Parametric { bands: a }, EqLayer::Parametric { bands: b }) = (&p.eq, &q.eq)
        else {
            panic!("layout")
        };
        for (x, y) in a
            .iter()
            .chain(&p.correction)
            .zip(b.iter().chain(&q.correction))
        {
            assert_eq!(
                (x.kind, x.enabled, x.gain_db),
                (y.kind, y.enabled, y.gain_db)
            );
            assert!(
                close(x.freq, y.freq, 2e-4) && close(x.q, y.q, 3e-4),
                "{x:?} {y:?}"
            );
        }
        assert_eq!(q.correction_source.as_ref().unwrap().name, "KZ ZSN Pro");
        // Re-encoding the decoded profile gives the same code (stable).
        assert_eq!(encode(&q), code);
    }

    #[test]
    fn short_enough_to_paste() {
        let mut p = Profile::new("Rock");
        p.eq = EqLayer::Parametric {
            bands: (0..10)
                .map(|i| Band::peak(31.25 * 2f64.powi(i), 1.0, 1.0))
                .collect(),
        };
        let code = encode(&p);
        assert!(code.len() <= 120, "{} chars: {code}", code.len());
        let mut g = Profile::new("Graphic");
        g.eq = EqLayer::graphic(&GRAPHIC_31);
        let code = encode(&g);
        assert!(code.len() < 120, "{} chars", code.len());
        assert_eq!(decode(&code).unwrap().eq, g.eq);
    }

    #[test]
    fn finds_code_in_a_message_and_rejects_damage() {
        let code = encode(&sample());
        let msg = format!("try my EQ: {code} — sounds great!");
        assert_eq!(decode(&msg).unwrap().name, sample().name);
        let cut = &code[..code.len() - 5];
        assert_eq!(decode(cut).unwrap_err(), ShareError::Damaged);
        let mut flipped = code.clone().into_bytes();
        let i = flipped.len() - 10;
        flipped[i] = if flipped[i] == b'A' { b'B' } else { b'A' };
        assert_eq!(
            decode(std::str::from_utf8(&flipped).unwrap()).unwrap_err(),
            ShareError::Damaged
        );
        assert_eq!(decode("hello").unwrap_err(), ShareError::NotACode);
        assert_eq!(decode("MEQ9-abc").unwrap_err(), ShareError::Newer);
    }
}
