# MYOwnEQ
VTNC CANSEI DESSES EQ PAGOS ❌🖕

Free, open-source, system-wide equalizer for **macOS** and **Windows** with live profile sync between machines.

## Status
| Phase | What | State |
|---|---|---|
| 1 | Rust core: DSP chain, profiles, devices, app rules, importers | ✅ |
| 2 | macOS engine (Core Audio Process Taps) + menubar app | ⏳ |
| 3 | Sync (LAN / self-hosted Docker / cloud), device linking | ⏳ |
| 4 | Windows app (Equalizer APO backend) | ⏳ |
| 5 | Auto-update, signed releases | ⏳ |

## Features (planned)
- **Pro mode**: parametric + graphic EQ, live spectrum, per-band highlight while editing, limiter, balance, loudness compensation.
- **Easy mode**: Bass / Mids / Clarity / Treble knobs, presets, AutoEQ headphone search, favorites for connected devices, menubar/tray quick panel.
- **Per device** profiles + **app rules** (e.g. KZ default → *FPS* while CS2 runs).
- **Sync**: link the same headphones across machines; switching the profile on one mirrors to the other.
- **Import** from eqMac and Equalizer APO / Peace.

## Layout
```
core/dsp     real-time safe biquad chain (no alloc/locks in the audio callback)
core/model   profiles, devices, rules, library persistence, formats
```

## Dev
```sh
cargo test --workspace
cargo run -p myowneq-model --example import_eqmac   # print your eqMac presets
```
