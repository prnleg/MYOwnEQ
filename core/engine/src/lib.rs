//! MyOwnEQ's DSP as a plugin that Equalizer APO loads on Windows (VST 2.4 ABI,
//! written by hand: no SDK, no extra crates). Same processing as the Mac app:
//! EQ, lookahead limiter, volume boost.
//!
//! The ABI is plain C, so the library also builds and is tested on macOS/Linux
//! (InteropTests load it through `VSTPluginMain`, exactly like APO does).

pub mod plugin;
pub mod schema;

use plugin::{engine_dir, Plugin};
use std::ffi::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};

pub type HostCallback = extern "C" fn(*mut AEffect, i32, i32, isize, *mut c_void, f32) -> isize;
type Dispatcher = extern "C" fn(*mut AEffect, i32, i32, isize, *mut c_void, f32) -> isize;
type Process = extern "C" fn(*mut AEffect, *const *const f32, *mut *mut f32, i32);
type ProcessDouble = extern "C" fn(*mut AEffect, *const *const f64, *mut *mut f64, i32);
type SetParam = extern "C" fn(*mut AEffect, i32, f32);
type GetParam = extern "C" fn(*mut AEffect, i32) -> f32;

/// VST 2.4 `AEffect`, field for field (x64 natural alignment = the SDK's pack(8)).
#[repr(C)]
pub struct AEffect {
    pub magic: i32,
    pub dispatcher: Dispatcher,
    pub process: Process,
    pub set_parameter: SetParam,
    pub get_parameter: GetParam,
    pub num_programs: i32,
    pub num_params: i32,
    pub num_inputs: i32,
    pub num_outputs: i32,
    pub flags: i32,
    pub resvd1: isize,
    pub resvd2: isize,
    pub initial_delay: i32,
    pub real_qualities: i32,
    pub off_qualities: i32,
    pub io_ratio: f32,
    pub object: *mut c_void,
    pub user: *mut c_void,
    pub unique_id: i32,
    pub version: i32,
    pub process_replacing: Process,
    pub process_double_replacing: ProcessDouble,
    pub future: [u8; 56],
}

const MAGIC: i32 = i32::from_be_bytes(*b"VstP");
const UNIQUE_ID: i32 = i32::from_be_bytes(*b"MyEQ");
const CHANNELS: i32 = 2;
const FLAG_CAN_REPLACING: i32 = 1 << 4;
const FLAG_PROGRAM_CHUNKS: i32 = 1 << 5;

// Dispatcher opcodes we answer (VST 2.4 numbering).
const EFF_OPEN: i32 = 0;
const EFF_CLOSE: i32 = 1;
const EFF_SET_SAMPLE_RATE: i32 = 10;
const EFF_SET_BLOCK_SIZE: i32 = 11;
const EFF_MAINS_CHANGED: i32 = 12;
const EFF_GET_CHUNK: i32 = 23;
const EFF_SET_CHUNK: i32 = 24;
const EFF_GET_PLUG_CATEGORY: i32 = 35;
const EFF_GET_EFFECT_NAME: i32 = 45;
const EFF_GET_VENDOR_STRING: i32 = 47;
const EFF_GET_PRODUCT_STRING: i32 = 48;
const EFF_GET_VENDOR_VERSION: i32 = 49;
const EFF_GET_TAIL_SIZE: i32 = 52;
const EFF_GET_VST_VERSION: i32 = 58;
const PLUG_CATEGORY_EFFECT: isize = 1;

/// What `AEffect.object` points to. `effect` must stay the first field.
#[repr(C)]
struct Instance {
    effect: AEffect,
    plugin: Plugin,
    /// Bytes handed out by effGetChunk (must outlive the call).
    chunk: Vec<u8>,
}

unsafe fn instance<'a>(e: *mut AEffect) -> Option<&'a mut Instance> {
    if e.is_null() {
        return None;
    }
    let obj = (*e).object as *mut Instance;
    obj.as_mut()
}

unsafe fn write_str(ptr: *mut c_void, s: &str, max: usize) {
    if ptr.is_null() {
        return;
    }
    let bytes = s.as_bytes();
    let n = bytes.len().min(max - 1);
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr as *mut u8, n);
    *(ptr as *mut u8).add(n) = 0;
}

extern "C" fn dispatcher(
    e: *mut AEffect,
    opcode: i32,
    _index: i32,
    value: isize,
    ptr: *mut c_void,
    opt: f32,
) -> isize {
    catch_unwind(AssertUnwindSafe(|| unsafe {
        if opcode == EFF_CLOSE {
            if !e.is_null() && !(*e).object.is_null() {
                drop(Box::from_raw((*e).object as *mut Instance));
            }
            return 1;
        }
        let Some(inst) = instance(e) else { return 0 };
        match opcode {
            EFF_OPEN | EFF_MAINS_CHANGED => 0,
            EFF_SET_SAMPLE_RATE => {
                inst.plugin.set_sample_rate(opt as f64);
                0
            }
            EFF_SET_BLOCK_SIZE => {
                inst.plugin.set_block_size(value.max(0) as usize);
                0
            }
            EFF_GET_CHUNK => {
                if ptr.is_null() {
                    return 0;
                }
                inst.chunk = inst.plugin.device().into_bytes();
                *(ptr as *mut *mut c_void) = inst.chunk.as_mut_ptr() as *mut c_void;
                inst.chunk.len() as isize
            }
            EFF_SET_CHUNK => {
                if ptr.is_null() || value <= 0 || value > 4096 {
                    return 0;
                }
                let bytes = std::slice::from_raw_parts(ptr as *const u8, value as usize);
                let id = String::from_utf8_lossy(bytes);
                inst.plugin.set_device(id.trim_end_matches('\0'));
                1
            }
            EFF_GET_PLUG_CATEGORY => PLUG_CATEGORY_EFFECT,
            EFF_GET_EFFECT_NAME | EFF_GET_PRODUCT_STRING => {
                write_str(ptr, "MyOwnEQ Engine", 64);
                1
            }
            EFF_GET_VENDOR_STRING => {
                write_str(ptr, "MyOwnEQ", 64);
                1
            }
            EFF_GET_VENDOR_VERSION => 1,
            EFF_GET_TAIL_SIZE => 1, // no tail beyond the limiter lookahead
            EFF_GET_VST_VERSION => 2400,
            _ => 0,
        }
    }))
    .unwrap_or(0)
}

extern "C" fn process_replacing(
    e: *mut AEffect,
    inputs: *const *const f32,
    outputs: *mut *mut f32,
    frames: i32,
) {
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe {
        if inputs.is_null() || outputs.is_null() || frames <= 0 {
            return;
        }
        if let Some(inst) = instance(e) {
            inst.plugin.process(inputs, outputs, frames as usize);
        }
    }));
}

/// Old accumulating `process`: not used by modern hosts; replace instead of add.
extern "C" fn process_accumulating(
    e: *mut AEffect,
    inputs: *const *const f32,
    outputs: *mut *mut f32,
    frames: i32,
) {
    process_replacing(e, inputs, outputs, frames);
}

extern "C" fn process_double(_e: *mut AEffect, _i: *const *const f64, _o: *mut *mut f64, _n: i32) {}
extern "C" fn set_parameter(_e: *mut AEffect, _index: i32, _value: f32) {}
extern "C" fn get_parameter(_e: *mut AEffect, _index: i32) -> f32 {
    0.0
}

/// Build an instance (also used by tests without going through the C entry point).
pub fn create() -> *mut AEffect {
    let inst = Box::new(Instance {
        effect: AEffect {
            magic: MAGIC,
            dispatcher,
            process: process_accumulating,
            set_parameter,
            get_parameter,
            num_programs: 1,
            num_params: 0,
            num_inputs: CHANNELS,
            num_outputs: CHANNELS,
            flags: FLAG_CAN_REPLACING | FLAG_PROGRAM_CHUNKS,
            resvd1: 0,
            resvd2: 0,
            initial_delay: 0,
            real_qualities: 0,
            off_qualities: 0,
            io_ratio: 1.0,
            object: std::ptr::null_mut(),
            user: std::ptr::null_mut(),
            unique_id: UNIQUE_ID,
            version: 1,
            process_replacing,
            process_double_replacing: process_double,
            future: [0; 56],
        },
        plugin: Plugin::new(CHANNELS as usize, engine_dir()),
        chunk: Vec::new(),
    });
    let raw = Box::into_raw(inst);
    unsafe {
        (*raw).effect.object = raw as *mut c_void;
        &mut (*raw).effect
    }
}

/// VST 2.x entry point. Equalizer APO calls this once per output it hooks.
///
/// # Safety
/// Called by the host with a valid (or null) host callback.
#[cfg(feature = "plugin")]
#[no_mangle]
pub unsafe extern "C" fn VSTPluginMain(_host: Option<HostCallback>) -> *mut AEffect {
    catch_unwind(create).unwrap_or(std::ptr::null_mut())
}

/// Plugin version as a C string (the app compares it with the copy it ships).
#[cfg(feature = "plugin")]
#[no_mangle]
pub extern "C" fn myowneq_engine_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

#[cfg(test)]
mod tests {
    use super::*;
    use schema::{DeviceParams, EngineFile, SCHEMA};

    fn call(e: *mut AEffect, op: i32, value: isize, ptr: *mut c_void, opt: f32) -> isize {
        unsafe { ((*e).dispatcher)(e, op, 0, value, ptr, opt) }
    }

    fn sine(n: usize, amp: f32) -> Vec<f32> {
        (0..n).map(|i| amp * (i as f32 * 0.07).sin()).collect()
    }

    /// Hosts read AEffect by offset: pin the 64-bit layout of the VST 2.4 SDK.
    #[test]
    #[cfg(target_pointer_width = "64")]
    fn aeffect_layout_matches_the_sdk() {
        use std::mem::offset_of;
        assert_eq!(offset_of!(AEffect, dispatcher), 8);
        assert_eq!(offset_of!(AEffect, num_inputs), 48);
        assert_eq!(offset_of!(AEffect, flags), 56);
        assert_eq!(offset_of!(AEffect, resvd1), 64);
        assert_eq!(offset_of!(AEffect, initial_delay), 80);
        assert_eq!(offset_of!(AEffect, object), 96);
        assert_eq!(offset_of!(AEffect, unique_id), 112);
        assert_eq!(offset_of!(AEffect, process_replacing), 120);
        assert_eq!(offset_of!(AEffect, future), 136);
        assert_eq!(std::mem::size_of::<AEffect>(), 192);
    }

    #[test]
    #[cfg(feature = "plugin")]
    fn host_flow_passthrough_then_params_from_file() {
        let dir = std::env::temp_dir().join(format!("meq-engine-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::env::set_var("MYOWNEQ_ENGINE_DIR", &dir);

        let e = unsafe { VSTPluginMain(None) };
        assert!(!e.is_null());
        unsafe {
            assert_eq!((*e).magic, MAGIC);
            assert_eq!((*e).num_inputs, 2);
        }
        call(e, EFF_OPEN, 0, std::ptr::null_mut(), 0.0);
        call(e, EFF_SET_SAMPLE_RATE, 0, std::ptr::null_mut(), 48_000.0);
        call(e, EFF_SET_BLOCK_SIZE, 512, std::ptr::null_mut(), 0.0);
        let mut id = b"{kz-guid}".to_vec();
        assert_eq!(
            call(
                e,
                EFF_SET_CHUNK,
                id.len() as isize,
                id.as_mut_ptr() as *mut c_void,
                0.0
            ),
            1
        );
        let mut out_ptr: *mut c_void = std::ptr::null_mut();
        let n = call(
            e,
            EFF_GET_CHUNK,
            0,
            &mut out_ptr as *mut _ as *mut c_void,
            0.0,
        );
        assert_eq!(
            unsafe { std::slice::from_raw_parts(out_ptr as *const u8, n as usize) },
            b"{kz-guid}"
        );

        let run = |amp: f32| -> f32 {
            let (l, r) = (sine(2048, amp), sine(2048, amp));
            let (mut ol, mut or) = (vec![0f32; 2048], vec![0f32; 2048]);
            let ins = [l.as_ptr(), r.as_ptr()];
            let mut outs = [ol.as_mut_ptr(), or.as_mut_ptr()];
            unsafe { ((*e).process_replacing)(e, ins.as_ptr(), outs.as_mut_ptr(), 2048) };
            ol[1024..].iter().fold(0f32, |m, s| m.max(s.abs()))
        };
        // No parameter file yet: untouched.
        assert!((run(0.5) - 0.5).abs() < 0.01);

        // App writes -6 dB preamp for this output → the watcher picks it up.
        let mut p = myowneq_dsp::EqParams {
            preamp_db: -6.0,
            ..Default::default()
        };
        let mut f = EngineFile {
            schema: SCHEMA,
            ..Default::default()
        };
        f.devices
            .insert("{kz-guid}".into(), DeviceParams::from_params(&p));
        std::fs::write(dir.join("engine.json"), f.to_json()).unwrap();
        let mut got = 0.0;
        for _ in 0..60 {
            std::thread::sleep(std::time::Duration::from_millis(50));
            got = run(0.5);
            if (got - 0.25).abs() < 0.02 {
                break;
            }
        }
        assert!((got - 0.25).abs() < 0.02, "expected -6 dB, got peak {got}");

        // Boost +12 dB on a loud signal never clips (limiter forced on).
        p.preamp_db = 12.0;
        f.devices
            .insert("{kz-guid}".into(), DeviceParams::from_params(&p));
        std::thread::sleep(std::time::Duration::from_millis(20));
        std::fs::write(dir.join("engine.json"), f.to_json()).unwrap();
        let mut peak = 0.0;
        for _ in 0..60 {
            std::thread::sleep(std::time::Duration::from_millis(50));
            peak = run(0.9);
            if peak > 0.95 {
                break;
            }
        }
        assert!(
            peak > 0.95 && peak <= 0.9886,
            "boosted and limited, got {peak}"
        );

        // Heartbeat file appears for the app.
        let status = dir.join(plugin::status_name("{kz-guid}"));
        for _ in 0..40 {
            if status.exists() {
                break;
            }
            run(0.1);
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
        assert!(status.exists(), "no heartbeat");

        assert_eq!(call(e, EFF_CLOSE, 0, std::ptr::null_mut(), 0.0), 1);
        std::env::remove_var("MYOWNEQ_ENGINE_DIR");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
