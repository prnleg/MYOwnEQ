//! C ABI. See `include/myowneq.h` for the contract.
//!
//! Threading:
//! * `MeqCore` functions: call from one control thread (the UI main thread).
//! * `MeqAudio` functions: call from the audio thread only. `meq_audio_process`
//!   is real-time safe.
//!
//! No Rust panic ever crosses the boundary: every entry point is wrapped in
//! `catch_unwind`; on panic the audio path passes audio through untouched.

pub mod control;
pub mod service;
pub mod syncing;

use service::{Audio, Core};
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;

pub struct MeqCore(Core);
pub struct MeqAudio(Audio);

unsafe fn cstr<'a>(p: *const c_char) -> Option<&'a str> {
    if p.is_null() {
        None
    } else {
        CStr::from_ptr(p).to_str().ok()
    }
}

/// Create the core. `data_dir` may be NULL (in-memory). Returns NULL on failure.
///
/// # Safety
/// Pointers must be NULL or valid NUL-terminated UTF-8 strings.
#[no_mangle]
pub unsafe extern "C" fn meq_core_new(
    data_dir: *const c_char,
    machine_id: *const c_char,
) -> *mut MeqCore {
    catch_unwind(|| {
        let dir = cstr(data_dir).map(PathBuf::from);
        let machine = cstr(machine_id).unwrap_or("unknown");
        Box::into_raw(Box::new(MeqCore(Core::new(dir, machine))))
    })
    .unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `core` must come from `meq_core_new` and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn meq_core_free(core: *mut MeqCore) {
    if !core.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(core))));
    }
}

/// Run a JSON command. Returns a JSON string to be freed with `meq_string_free`.
///
/// # Safety
/// `core` valid; `json` NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn meq_core_command(core: *mut MeqCore, json: *const c_char) -> *mut c_char {
    let out = catch_unwind(AssertUnwindSafe(|| match (core.as_mut(), cstr(json)) {
        (Some(c), Some(j)) => c.0.command(j),
        _ => r#"{"ok":false,"error":"null argument"}"#.to_string(),
    }))
    .unwrap_or_else(|_| r#"{"ok":false,"error":"internal panic"}"#.to_string());
    CString::new(out)
        .map(CString::into_raw)
        .unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `s` must come from `meq_core_command` (or be NULL).
#[no_mangle]
pub unsafe extern "C" fn meq_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

/// Fill `out[0..n]` with spectrum bins in dBFS. Returns 0 if no audio handle exists.
///
/// # Safety
/// `core` valid; `out` points to `n` writable floats.
#[no_mangle]
pub unsafe extern "C" fn meq_core_spectrum(core: *mut MeqCore, out: *mut f32, n: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match core.as_mut() {
        Some(c) if !out.is_null() && n > 0 => {
            let bins = std::slice::from_raw_parts_mut(out, n as usize);
            c.0.spectrum(bins) as u32
        }
        _ => 0,
    }))
    .unwrap_or(0)
}

/// Create an audio handle for a device running at `sample_rate`. Each call
/// replaces the previous handle's connection; free the old one with
/// `meq_audio_free` after its IOProc has stopped.
///
/// # Safety
/// `core` valid.
#[no_mangle]
pub unsafe extern "C" fn meq_core_make_audio(
    core: *mut MeqCore,
    sample_rate: f64,
) -> *mut MeqAudio {
    catch_unwind(AssertUnwindSafe(|| match core.as_mut() {
        Some(c) => Box::into_raw(Box::new(MeqAudio(c.0.make_audio(sample_rate)))),
        None => std::ptr::null_mut(),
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `audio` from `meq_core_make_audio`, not in use by any thread.
#[no_mangle]
pub unsafe extern "C" fn meq_audio_free(audio: *mut MeqAudio) {
    if !audio.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(audio))));
    }
}

/// Process interleaved float audio in place. Real-time safe.
///
/// # Safety
/// `audio` valid and used only from this thread; `buf` has `frames * channels` floats.
#[no_mangle]
pub unsafe extern "C" fn meq_audio_process(
    audio: *mut MeqAudio,
    buf: *mut f32,
    frames: u32,
    channels: u32,
) {
    if audio.is_null() || buf.is_null() {
        return;
    }
    let len = frames as usize * channels as usize;
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let samples = std::slice::from_raw_parts_mut(buf, len);
        (*audio).0.process(samples, channels as usize);
    }));
}

/// Change sample rate. Call only while the IOProc is stopped.
///
/// # Safety
/// `audio` valid and not concurrently processing.
#[no_mangle]
pub unsafe extern "C" fn meq_audio_set_sample_rate(audio: *mut MeqAudio, sample_rate: f64) {
    if let Some(a) = audio.as_mut() {
        let _ = catch_unwind(AssertUnwindSafe(|| a.0.set_sample_rate(sample_rate)));
    }
}

/// Browse the LAN for MyOwnEQ hosts (blocks up to `timeout_ms`). Independent of
/// any core, so it may be called from a background thread. Returns a JSON
/// array `[{"name","addr"}]`; free with `meq_string_free`.
#[no_mangle]
pub extern "C" fn meq_discover(timeout_ms: u32) -> *mut c_char {
    let out = catch_unwind(|| {
        let found = myowneq_sync::discovery::browse(std::time::Duration::from_millis(
            timeout_ms.min(5000) as u64,
        ));
        serde_json::to_string(&found).unwrap_or_else(|_| "[]".into())
    })
    .unwrap_or_else(|_| "[]".into());
    CString::new(out)
        .map(CString::into_raw)
        .unwrap_or(std::ptr::null_mut())
}

/// Feed interleaved audio to the spectrum analyzer without processing it.
///
/// # Safety
/// `audio` valid and used from one thread; `buf` has `frames * channels` floats.
#[no_mangle]
pub unsafe extern "C" fn meq_audio_analyze(
    audio: *mut MeqAudio,
    buf: *const f32,
    frames: u32,
    channels: u32,
) {
    if audio.is_null() || buf.is_null() {
        return;
    }
    let len = frames as usize * channels as usize;
    let _ = catch_unwind(AssertUnwindSafe(|| {
        (*audio)
            .0
            .analyze(std::slice::from_raw_parts(buf, len), channels as usize);
    }));
}
