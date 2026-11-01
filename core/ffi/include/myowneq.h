// MyOwnEQ core C API. See core/ffi/src/lib.rs for threading rules.
#ifndef MYOWNEQ_H
#define MYOWNEQ_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MeqCore MeqCore;
typedef struct MeqAudio MeqAudio;

// ---- control side (UI thread) ----
MeqCore *meq_core_new(const char *data_dir, const char *machine_id);
void meq_core_free(MeqCore *core);

// JSON command in, JSON reply out. Free reply with meq_string_free.
char *meq_core_command(MeqCore *core, const char *json);
void meq_string_free(char *s);

// Log-spaced spectrum bins (20 Hz..20 kHz) in dBFS. Returns 0 if no audio yet.
uint32_t meq_core_spectrum(MeqCore *core, float *out, uint32_t n);

// Create an audio handle bound to this core (replaces previous binding).
MeqAudio *meq_core_make_audio(MeqCore *core, double sample_rate);

// LAN discovery (any thread, blocks up to timeout_ms). JSON array; free with meq_string_free.
char *meq_discover(uint32_t timeout_ms);

// ---- audio side (audio thread) ----
void meq_audio_free(MeqAudio *audio);
// Real-time safe. Interleaved float32 in place.
void meq_audio_process(MeqAudio *audio, float *buf, uint32_t frames, uint32_t channels);
// Spectrum only, no processing (Windows: APO does the EQ).
void meq_audio_analyze(MeqAudio *audio, const float *buf, uint32_t frames, uint32_t channels);
// Only while stopped.
void meq_audio_set_sample_rate(MeqAudio *audio, double sample_rate);

#ifdef __cplusplus
}
#endif

#endif
