// n3_palloc_guard.h -- H2 GPU Lightmapper standalone, step S1.7 (N3): the host-only additions to fullgpu.exe.
// Included by the build COPY of gather.cpp only (build_fullgpu_guard.py applies n3_guard_edits.py); production is never edited.
//
// (a) palloc overflow FATAL. gather.cpp's photon-candidate allocation `palloc` is a table of uint32 offsets built from a
//     uint64 running total `run`, and each light's candidate count is `(uint32_t)((float)alv/pwv)`. A light whose count
//     reaches 2^32, or a running total past 2^32-1, used to WRAP silently (a Custom photon budget near 100 M on a heavy map
//     can get there). gather.cpp now refuses instead: it prints a "DIRECT: *** FATAL PALLOC OVERFLOW (N3 guard)" line and
//     returns 9 (fullgpu's existing refusal code; gpu_bake echoes every FATAL line and throws on any non-zero exit).
//     Below the limit every computed value is bit-identical to the unguarded build: the same float quotient is converted
//     with the same (uint32_t) cast, and the total is only compared.
// (b) NvOptimusEnablement / AmdPowerXpressRequestHighPerformance exports: on a hybrid (Optimus / PowerXpress) laptop the
//     driver gives this process the discrete GPU, which is what gpucheck.exe (same exports) reports as the default adapter.
//     On a single-GPU PC (the dev PC: one RTX 4090) they change nothing.
#pragma once
#include <cstdint>
#include <cstdio>

#if !defined(GATHER_AS_LIB) && !defined(N3_NO_EXPORTS)
extern "C" {
__declspec(dllexport) unsigned long NvOptimusEnablement = 0x00000001;   // DWORD
__declspec(dllexport) int AmdPowerXpressRequestHighPerformance = 1;
}
#endif

// (uint32_t)q of a float is an exact truncation only for 0 <= q < 2^32; at or above 2^32 (or for a NaN) it wraps or is
// undefined. 4294967040.0f is the largest float below 2^32.
static inline bool n3_cnt_fits(float q) { return q < 4294967296.0f; }

// palloc stores (uint32_t)run; `run` only grows, so every stored offset is <= the final total: checking the final total
// covers every push.
static inline bool n3_run_fits(uint64_t run) { return run <= 0xFFFFFFFFull; }

static inline void n3_palloc_fatal_light(const char* kind, unsigned long long idx, float q) {
    printf("DIRECT: *** FATAL PALLOC OVERFLOW (N3 guard): %s light %llu wants %.0f photon candidates -- more than a uint32"
           " palloc count holds (max 4294967295); REFUSING instead of a silent wrap. Lower the photon budget.\n",
           kind, idx, (double)q);
    fflush(stdout);
}

static inline void n3_palloc_fatal_total(unsigned long long run, unsigned nLights) {
    printf("DIRECT: *** FATAL PALLOC OVERFLOW (N3 guard): %llu photon candidates over %u cast lights do not fit the uint32"
           " palloc offsets (max 4294967295); REFUSING instead of a silent wrap. Lower the photon budget.\n",
           run, nLights);
    fflush(stdout);
}
