#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && defined(LISTENN_SHARED)
#  define LN_API __declspec(dllexport)
#else
#  define LN_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ln_core* ln_core_handle;
typedef struct { int64_t timestamp_us; uint8_t channel_id; } ln_event;
typedef struct { int64_t window_us; int64_t gate_us; } ln_config;
typedef struct {
    int64_t start_us;
    int64_t end_us;
    uint64_t counts[15];
    uint64_t accepted_events;
    uint64_t ignored_channels;
    double mean_multiplicity;
    double feynman_y;
} ln_window_summary;

// All functions return zero on success and never allow C++ exceptions to cross
// the ABI. drain_windows supports the usual two-pass query (out may be NULL).
LN_API int ln_core_create(const ln_config* config, ln_core_handle* out);
LN_API void ln_core_destroy(ln_core_handle core);
LN_API int ln_core_reset(ln_core_handle core);
LN_API int ln_core_push_events(ln_core_handle core, const ln_event* events, size_t count);
LN_API int ln_core_force_step(ln_core_handle core, int64_t end_us);
LN_API int ln_core_drain_windows(ln_core_handle core, ln_window_summary* out, size_t* inout_count);
LN_API const char* ln_core_last_error(ln_core_handle core);

#ifdef __cplusplus
}
#endif
