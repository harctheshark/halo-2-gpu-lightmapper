/* lpnearfast.c -- the search loop of lp_nearpoint.py's surface_terms() as a native DLL (1.4.0).
 *
 * surface_terms() tests every SURFACE-hit cube texel of every light probe (about 1 M rows on a map with 11 probes) against every
 * surface light: `dist(C_L, segment O..O+V) < R_L`, then the light's triangles in order, first hit wins (Moller-Trumbore with the
 * tool's own epsilon and ranges). In numpy that is ~10 whole-array passes per light: 16 of the compose step's 20 seconds on
 * triplicate. This file does ONLY that search and returns the hit list (light, row, triangle, u, v); the shading of the hits
 * (exp, pow 2.2, the bitmap lookups, the tone map) stays in lp_nearpoint.py, on a few thousand rows.
 *
 * Bit-exactness: everything here is single IEEE float32 operations in lp_nearpoint.py's statement order (each product and each
 * sum rounded to float32 on its own; /fp:precise, no FMA contraction, no libm call except sqrtf, which is correctly rounded).
 * The comparisons are written so that a NaN fails them exactly as it does in numpy. lp_nearfast_test.py compares the hit list
 * of this DLL with the numpy loop's on real captures (rows, triangles and the u / v bit patterns).
 *
 *   int64 lpnear_run(O, V, n, SLP, nL, SLT, nT, tmax, threads)   O, V: n x 3 float32; SLP: nL x 24; SLT: nT x 25 -> the hit count (-1 = no memory)
 *   void  lpnear_fetch(light, row, tri, u, v)                     the hits, ordered by light, then row; frees them
 * Build: build.bat (cl /O2 /fp:precise /LD /MT).
 */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <windows.h>

#pragma fp_contract(off)
#define EXPORT __declspec(dllexport)

typedef struct { int32_t row, tri; float u, v; } Hit;
typedef struct { Hit* h; int64_t n, cap; int fail; } HitList;

typedef struct {
    const float* O; const float* V; int64_t n;
    const float* SLP; int64_t nL; const float* SLT; int64_t nT;
    float tmax;
    HitList* out;            /* one list per light */
    volatile LONG64 next;    /* the next light to take */
} Job;

static HitList* g_lists = NULL;
static int64_t g_nL = 0;

static void push(HitList* L, int32_t row, int32_t tri, float u, float v)
{
    if (L->n == L->cap) {
        int64_t nc = L->cap ? L->cap * 2 : 256;
        Hit* nh = (Hit*)realloc(L->h, (size_t)nc * sizeof(Hit));
        if (!nh) { L->fail = 1; return; }
        L->h = nh; L->cap = nc;
    }
    L->h[L->n].row = row; L->h[L->n].tri = tri; L->h[L->n].u = u; L->h[L->n].v = v; L->n++;
}

/* one light against all rows: lp_nearpoint.py seg_dist() + mt(), statement for statement */
static void one_light(const Job* J, int64_t li)
{
    const float* p = J->SLP + li * 24;
    const float R = p[5];
    const float C0 = p[8], C1 = p[9], C2 = p[10];
    uint32_t ts, tc;
    HitList* L = &J->out[li];
    memcpy(&ts, &p[3], 4); memcpy(&tc, &p[4], 4);
    if ((int64_t)ts + (int64_t)tc > J->nT) { L->fail = 2; return; }
    for (int64_t r = 0; r < J->n; r++) {
        const float* O = J->O + r * 3;
        const float* V = J->V + r * 3;
        /* seg_dist: w = C - O; vv = sum(V*V); t = clip(where(vv > 0, sum(w*V) / vv, 0), 0, 1); q = (O + V*t) - C; sqrt(sum(q*q)) */
        float w0 = C0 - O[0], w1 = C1 - O[1], w2 = C2 - O[2];
        float a, b, c, vv, wv, t, q0, q1, q2, d2, dist;
        a = V[0] * V[0]; b = V[1] * V[1]; c = V[2] * V[2];
        vv = a + b; vv = vv + c;
        a = w0 * V[0]; b = w1 * V[1]; c = w2 * V[2];
        wv = a + b; wv = wv + c;
        if (vv > 0.0f) t = wv / vv; else t = 0.0f;
        if (t < 0.0f) t = 0.0f; else if (t > 1.0f) t = 1.0f;       /* a NaN stays a NaN, as in np.clip */
        a = V[0] * t; b = V[1] * t; c = V[2] * t;
        q0 = O[0] + a; q1 = O[1] + b; q2 = O[2] + c;
        q0 = q0 - C0; q1 = q1 - C1; q2 = q2 - C2;
        a = q0 * q0; b = q1 * q1; c = q2 * q2;
        d2 = a + b; d2 = d2 + c;
        dist = sqrtf(d2);
        if (!(R > dist)) continue;
        for (uint32_t k = 0; k < tc; k++) {
            const float* T = J->SLT + ((int64_t)ts + k) * 25;
            /* mt(): e1 = V1 - V0, e2 = V2 - V0; n = (e2[2]*e1[1] - e2[1]*e1[2], e2[0]*e1[2] - e2[2]*e1[0], e2[1]*e1[0] - e2[0]*e1[1]) */
            float e10 = T[3] - T[0], e11 = T[4] - T[1], e12 = T[5] - T[2];
            float e20 = T[6] - T[0], e21 = T[7] - T[1], e22 = T[8] - T[2];
            float x, y, n0, n1, n2, det, inv, s0, s1, s2, tt, u, v, qq0, qq1, qq2, uv;
            x = e22 * e11; y = e21 * e12; n0 = x - y;
            x = e20 * e12; y = e22 * e10; n1 = x - y;
            x = e21 * e10; y = e20 * e11; n2 = x - y;
            /* det = ((n1*V[1]) + (n0*V[0])) + (n2*V[2]) */
            a = n1 * V[1]; b = n0 * V[0]; c = n2 * V[2];
            det = a + b; det = det + c;
            if (!(fabsf(det) >= 9.9999997e-05f)) continue;
            inv = 1.0f / det;
            s0 = T[0] - O[0]; s1 = T[1] - O[1]; s2 = T[2] - O[2];
            /* t = (((s1*n1) + (s0*n0)) + (s2*n2)) * inv */
            a = s1 * n1; b = s0 * n0; c = s2 * n2;
            tt = a + b; tt = tt + c; tt = tt * inv;
            if (!(tt >= 0.0f) || !(tt <= J->tmax)) continue;
            /* q0 = s1*V2 - s2*V1; q2 = s0*V1 - s1*V0; q1 = s2*V0 - s0*V2 */
            x = s1 * V[2]; y = s2 * V[1]; qq0 = x - y;
            x = s0 * V[1]; y = s1 * V[0]; qq2 = x - y;
            x = s2 * V[0]; y = s0 * V[2]; qq1 = x - y;
            /* u = (((q1*e2[1]) + (q0*e2[0])) + (q2*e2[2])) * inv */
            a = qq1 * e21; b = qq0 * e20; c = qq2 * e22;
            u = a + b; u = u + c; u = u * inv;
            if (!(u >= 0.0f) || !(u <= 1.0f)) continue;
            /* v = -((((q1*e1[1]) + (q0*e1[0])) + (q2*e1[2])) * inv) */
            a = qq1 * e11; b = qq0 * e10; c = qq2 * e12;
            v = a + b; v = v + c; v = v * inv; v = -v;
            uv = v + u;
            if (!(v >= 0.0f) || !(uv <= 1.0f)) continue;
            push(L, (int32_t)r, (int32_t)k, u, v);
            break;                                              /* the first triangle in light order wins */
        }
    }
}

static DWORD WINAPI worker(LPVOID arg)
{
    Job* J = (Job*)arg;
    for (;;) {
        LONG64 li = InterlockedIncrement64(&J->next) - 1;
        if (li >= J->nL) break;
        one_light(J, li);
    }
    return 0;
}

static void free_lists(void)
{
    if (g_lists) { for (int64_t i = 0; i < g_nL; i++) free(g_lists[i].h); free(g_lists); }
    g_lists = NULL; g_nL = 0;
}

EXPORT int64_t lpnear_run(const float* O, const float* V, int64_t n, const float* SLP, int64_t nL, const float* SLT, int64_t nT, float tmax, int threads)
{
    Job J;
    HANDLE th[64];
    int64_t total = 0;
    free_lists();
    if (n < 0 || nL < 0 || n > 0x7FFFFFFF) return -2;
    g_lists = (HitList*)calloc((size_t)(nL ? nL : 1), sizeof(HitList));
    if (!g_lists) return -1;
    g_nL = nL;
    J.O = O; J.V = V; J.n = n; J.SLP = SLP; J.nL = nL; J.SLT = SLT; J.nT = nT; J.tmax = tmax; J.out = g_lists; J.next = 0;
    if (threads < 1) threads = 1;
    if (threads > 64) threads = 64;
    if (threads > nL) threads = (int)(nL ? nL : 1);
    if (threads == 1) worker(&J);
    else {
        int made = 0;
        for (int i = 0; i < threads; i++) { th[made] = CreateThread(NULL, 0, worker, &J, 0, NULL); if (th[made]) made++; }
        if (made == 0) worker(&J);
        else { WaitForMultipleObjects((DWORD)made, th, TRUE, INFINITE); for (int i = 0; i < made; i++) CloseHandle(th[i]); }
    }
    for (int64_t i = 0; i < nL; i++) {
        if (g_lists[i].fail) { int f = g_lists[i].fail; free_lists(); return f == 2 ? -3 : -1; }
        total += g_lists[i].n;
    }
    return total;
}

EXPORT void lpnear_fetch(int32_t* light, int32_t* row, int32_t* tri, float* u, float* v)
{
    int64_t o = 0;
    for (int64_t i = 0; i < g_nL; i++)
        for (int64_t j = 0; j < g_lists[i].n; j++, o++) {
            light[o] = (int32_t)i; row[o] = g_lists[i].h[j].row; tri[o] = g_lists[i].h[j].tri; u[o] = g_lists[i].h[j].u; v[o] = g_lists[i].h[j].v;
        }
    free_lists();
}
