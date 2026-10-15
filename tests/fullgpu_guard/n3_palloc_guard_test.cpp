// n3_palloc_guard_test.cpp -- CPU-only unit test of native\fullgpu_guard\n3_palloc_guard.h (step S1.7, N3).
// Built and run by build_fullgpu_guard.py with fullgpu's own flags (cl /O2 /EHsc /W3, static CRT) so the float -> uint32
// conversion is compiled the way gather.cpp compiles it. Prints one line per check and "N3 GUARD UNIT: PASS" / "FAIL".
#define N3_NO_EXPORTS
#include "n3_palloc_guard.h"
#include <cstdio>
#include <cstdint>
#include <cmath>
#include <limits>

static int g_fail = 0;
static void check(bool ok, const char* what) {
    printf("%s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++g_fail;
}

// the exact pre-guard expression of gather.cpp, kept out of line so /O2 cannot constant-fold the wrap away
__declspec(noinline) static uint32_t unguarded_cnt(uint32_t alv, float pwv) { return (uint32_t)((float)alv / pwv); }
__declspec(noinline) static bool guarded_fits(uint32_t alv, float pwv, uint32_t* out) {
    const float q = (float)alv / pwv;
    if (!n3_cnt_fits(q)) return false;
    *out = (uint32_t)q;
    return true;
}

int main() {
    // boundaries of the per-light predicate
    check(n3_cnt_fits(0.0f), "cnt 0 fits");
    check(n3_cnt_fits(4294967040.0f), "cnt 4294967040 (largest float below 2^32) fits");
    check(!n3_cnt_fits(4294967296.0f), "cnt 2^32 refused");
    check(!n3_cnt_fits(5.0e9f), "cnt 5e9 refused");
    check(!n3_cnt_fits(std::numeric_limits<float>::infinity()), "cnt +inf refused");
    check(!n3_cnt_fits(std::numeric_limits<float>::quiet_NaN()), "cnt NaN refused");
    // boundaries of the running-total predicate
    check(n3_run_fits(0ull), "run 0 fits");
    check(n3_run_fits(0xFFFFFFFFull), "run 4294967295 fits");
    check(!n3_run_fits(0x100000000ull), "run 2^32 refused");
    check(!n3_run_fits(0xFFFFFFFFFFFFFFFFull), "run 2^64-1 refused");

    // below the limit the guarded count is bit-identical to the unguarded expression (same quotient, same cast)
    {
        const uint32_t alvs[] = {0u, 1u, 7u, 1000u, 50000u, 123456789u, 4000000000u, 4294967295u};
        const float pwvs[] = {1.0f, 0.5f, 0.0142f, 0.25f, 1.0e-4f * 1.0001f, 0.9999999f, 3.0f};
        int n = 0, same = 0, refused = 0;
        for (uint32_t a : alvs)
            for (float p : pwvs) {
                uint32_t g = 0;
                if (guarded_fits(a, p, &g)) { ++n; same += (g == unguarded_cnt(a, p)); }
                else ++refused;
            }
        char msg[160];
        sprintf_s(msg, "guarded == unguarded on all %d in-range (alv, pwv) pairs (%d refused as >= 2^32)", n, refused);
        check(n > 0 && same == n, msg);
    }
    // the hazard the guard removes: the unguarded expression WRAPS silently at 2^32 (MSVC x64 truncates through 64 bits)
    {
        const uint32_t alv = 1000000000u;   // 1e9 attempts at Y = 0.2 -> 5e9 candidates
        const float pwv = 0.2f;
        const uint32_t wrapped = unguarded_cnt(alv, pwv);
        uint32_t g = 0;
        char msg[200];
        sprintf_s(msg, "1e9/0.2 = 5e9: unguarded gives %u with no error (5e9 is not a uint32), guarded refuses", wrapped);
        check(!guarded_fits(alv, pwv, &g), msg);
    }
    // the total: a uint64 run of 3 lights x 1.5e9 = 4.5e9 wraps as (uint32_t)run, the guard refuses it
    {
        uint64_t run = 0;
        for (int k = 0; k < 3; ++k) run += 1500000000u;
        char msg[200];
        sprintf_s(msg, "run 4.5e9: (uint32_t)run = %u (silent wrap), guard refuses", (uint32_t)run);
        check((uint32_t)run != run && !n3_run_fits(run), msg);
    }
    printf("N3 GUARD UNIT: %s (%d failure%s)\n", g_fail ? "FAIL" : "PASS", g_fail, g_fail == 1 ? "" : "s");
    return g_fail ? 1 : 0;
}
