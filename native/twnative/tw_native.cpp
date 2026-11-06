// tw_native.cpp -- native helper of the direct tag writer (0.6.9 dev_TW).  A SEPARATE EXE that lm_tagwriter / lmw_vertex call, in the
// pattern of p8group.exe.  Every routine reproduces a numpy-float32 routine of lmw_vertex.py operation for operation:
//   * every float32 operation is rounded to float32 after each step (C `float` arithmetic, SSE scalar single, no x87);
//   * built with /fp:strict (no fast-math, no FMA contraction, no reassociation);
//   * same iteration order and the same strict-less tie-breaks as the Python.
// usage:
//   tw_native.exe dirs <vertdir.bin> <job.bin> <out.bin>      cluster / lightmapped-instance direction lookups   (lmw_vertex.VD.lookup + emit_dir)
//   tw_native.exe vlit <serve.bin> <job.bin> <out.bin>        vertex-lit (flags=3) bucket records                (lmw_vertex.vertex_lit)
// All files little-endian.  Exit 0 = ok, 2 = bad input.
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <unordered_map>
#include <string>

typedef uint32_t u32;
typedef int32_t i32;
typedef int64_t i64;
typedef uint64_t u64;
typedef uint8_t u8;

static std::vector<u8> readfile(const char* p) {
    FILE* f = fopen(p, "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", p); exit(2); }
    fseek(f, 0, SEEK_END);
    long long n = _ftelli64(f);
    fseek(f, 0, SEEK_SET);
    std::vector<u8> b((size_t)n);
    if (n && fread(b.data(), 1, (size_t)n, f) != (size_t)n) { fprintf(stderr, "short read %s\n", p); exit(2); }
    fclose(f);
    return b;
}
static void writefile(const char* p, const std::vector<u8>& b) {
    FILE* f = fopen(p, "wb");
    if (!f) { fprintf(stderr, "cannot write %s\n", p); exit(2); }
    if (!b.empty()) fwrite(b.data(), 1, b.size(), f);
    fclose(f);
}
struct Rd {
    const u8* p; size_t n, o;
    Rd(const std::vector<u8>& v) : p(v.data()), n(v.size()), o(0) {}
    u32 u() { if (o + 4 > n) { fprintf(stderr, "job truncated\n"); exit(2); } u32 x; memcpy(&x, p + o, 4); o += 4; return x; }
    float f() { u32 x = u(); float y; memcpy(&y, &x, 4); return y; }
    const float* fp(size_t cnt) { if (o + 4 * cnt > n) { fprintf(stderr, "job truncated\n"); exit(2); } const float* r = (const float*)(p + o); o += 4 * cnt; return r; }
    const u8* bp(size_t cnt) { if (o + cnt > n) { fprintf(stderr, "job truncated\n"); exit(2); } const u8* r = p + o; o += cnt; return r; }
};

// ---- constants exactly as numpy builds them: F(<python double>) = (float)<double literal>
static const float K_INV = 1.0f / (float)0.30;              // VD.inv = F(1.0) / F(0.30)
static const float K_D2MAX = (float)0.0025;
static const float K_NL = (float)0.25;
static const float K_DOT = (float)0.8;
static const float K_W = (float)0.000099999997;              // emit_dir / vertex_lit threshold
static const float K_NN = (float)1e-9;

// ---- vertdir.bin (lmw_vertex.VD): 0.30 wu cell hash, head-insertion chains (descending k)
static inline u32 cellhash(i64 x, i64 y, i64 z) {
    u32 h = 2166136261u;
    h = (h ^ (u32)x) * 16777619u;
    h = (h ^ (u32)y) * 16777619u;
    h = (h ^ (u32)z) * 16777619u;
    h ^= h >> 13;
    return h;
}
struct VD {
    u32 n = 0, mask = 0;
    const float* R = nullptr;           // n x 11
    std::vector<i32> head, nxt;
    void load(const std::vector<u8>& b) {
        if (b.size() < 16) { fprintf(stderr, "vertdir too small\n"); exit(2); }
        u32 mg, rw, crc;
        memcpy(&mg, b.data(), 4); memcpy(&n, b.data() + 4, 4); memcpy(&rw, b.data() + 8, 4); memcpy(&crc, b.data() + 12, 4);
        if (mg != 0x31524456u || rw != 11 || b.size() < 16 + (size_t)n * 44) { fprintf(stderr, "not a vertdir.bin\n"); exit(2); }
        R = (const float*)(b.data() + 16);
        u32 cap = 16;
        while (cap < (u32)(n * 2)) cap <<= 1;
        mask = cap - 1;
        head.assign(cap, -1);
        nxt.assign(n, -1);
        for (u32 k = 0; k < n; k++) {
            const float* r = R + (size_t)k * 11;
            i64 cx = (i64)floorf(r[0] * K_INV), cy = (i64)floorf(r[1] * K_INV), cz = (i64)floorf(r[2] * K_INV);
            u32 b_ = cellhash(cx, cy, cz) & mask;
            nxt[k] = head[b_];
            head[b_] = (i32)k;
        }
    }
    // returns tier (0 none, 1, 2) and the row index
    inline int lookup(const float* p, const float* nr, bool has_nr, bool t1only, u32& krow) const {
        float b1 = 1e30f, b2 = 1e30f;
        i32 k1 = -1, k2 = -1;
        i64 cx = (i64)floorf(p[0] * K_INV), cy = (i64)floorf(p[1] * K_INV), cz = (i64)floorf(p[2] * K_INV);
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++) {
                    u32 b_ = cellhash(cx + dx, cy + dy, cz + dz) & mask;
                    for (i32 k = head[b_]; k >= 0; k = nxt[k]) {
                        const float* r = R + (size_t)k * 11;
                        float ex = r[0] - p[0];
                        float ey = r[1] - p[1];
                        float ez = r[2] - p[2];
                        float xx = ex * ex, yy = ey * ey, zz = ez * ez;
                        float d2 = (xx + yy) + zz;
                        if (d2 > K_D2MAX) continue;
                        float a = r[3] * r[3], b = r[4] * r[4], c = r[5] * r[5];
                        float nl = (a + b) + c;
                        if (t1only) {
                            if (!(nl > K_NL)) continue;
                            float q0 = r[3] * nr[0], q1 = r[4] * nr[1], q2 = r[5] * nr[2];
                            if (((q0 + q1) + q2) < K_DOT) continue;
                            if (d2 < b1) { b1 = d2; k1 = k; }
                        } else {
                            if (nl > K_NL) {
                                if (has_nr) {
                                    float q0 = r[3] * nr[0], q1 = r[4] * nr[1], q2 = r[5] * nr[2];
                                    if (((q0 + q1) + q2) < K_DOT) continue;
                                }
                                if (d2 < b1) { b1 = d2; k1 = k; }
                            } else {
                                if (d2 < b2) { b2 = d2; k2 = k; }
                            }
                        }
                    }
                }
        i32 k = k1 >= 0 ? k1 : k2;
        if (k < 0) return 0;
        krow = (u32)k;
        return k == k1 ? 1 : 2;
    }
};

// ---- lmw_vertex helpers (float32 operation order preserved)
static inline bool emit_dir(const float* acc, float* d) {      // acc = row[6..10]
    float w = acc[4];
    if (!(w > K_W)) return false;
    float iw = 1.0f / w;
    float t = acc[3] * iw;
    float s = t * iw;
    d[0] = s * acc[0];
    d[1] = acc[1] * s;
    d[2] = acc[2] * s;
    return true;
}
// xf_pt(M, p, scale=False)
static inline void xf_pt(const float* M, const float* p, float* o) {
    float a, b, c;
    a = M[4] * p[1]; b = M[1] * p[0]; c = M[7] * p[2]; o[0] = ((a + b) + c) + M[10];
    a = M[5] * p[1]; b = M[2] * p[0]; c = M[8] * p[2]; o[1] = ((a + b) + c) + M[11];
    a = M[6] * p[1]; b = M[3] * p[0]; c = M[9] * p[2]; o[2] = ((a + b) + c) + M[12];
}
// xf_pt(M, p, True): the scale branch
static inline void xf_pt_s(const float* M, const float* p, float* o) {
    float q[3];
    if (M[0] != 1.0f) { q[0] = p[0] * M[0]; q[1] = p[1] * M[0]; q[2] = p[2] * M[0]; xf_pt(M, q, o); }
    else xf_pt(M, p, o);
}
static inline void xf_dir(const float* M, const float* n, float* o) {
    float a, b, c;
    a = M[4] * n[1]; b = M[1] * n[0]; c = M[7] * n[2]; o[0] = (a + b) + c;
    a = M[5] * n[1]; b = M[2] * n[0]; c = M[8] * n[2]; o[1] = (a + b) + c;
    a = M[6] * n[1]; b = M[3] * n[0]; c = M[9] * n[2]; o[2] = (a + b) + c;
}
// vnorm(v) -> (unit or copy, length)
static inline float vnorm(const float* v, float* o) {
    float a = v[0] * v[0], b = v[1] * v[1], c = v[2] * v[2];
    float s = (a + b) + c;
    float ln = (float)sqrt((double)s);
    if (fabs((double)(ln - 0.0f)) < 0.00009999999747378752) { o[0] = v[0]; o[1] = v[1]; o[2] = v[2]; return 0.0f; }
    float inv = 1.0f / ln;
    o[0] = v[0] * inv; o[1] = v[1] * inv; o[2] = v[2] * inv;
    return ln;
}
static inline void fallback_half(const float* n, float* o) {
    float v[3];
    vnorm(n, v);
    o[0] = v[0] * (float)0.5; o[1] = v[1] * (float)0.5; o[2] = v[2] * (float)0.5;
}

// ================================================================================================ dirs
// job: 'TWD1', nsec, ntask; sections: nv, nv x 6 floats (pos, normal); tasks: kind(0 cluster, 1 instance), sec, M[13], (kind 0: nv mask bytes)
// out: per task: nv x 3 floats, then nv status bytes (0 = row found, 1 = no tier row / fallback), padded to 4
static int cmd_dirs(const char* vdp, const char* jobp, const char* outp) {
    std::vector<u8> vb = readfile(vdp);
    VD vd; vd.load(vb);
    std::vector<u8> jb = readfile(jobp);
    Rd rd(jb);
    if (rd.u() != 0x31445754u) { fprintf(stderr, "bad job magic\n"); return 2; }
    u32 nsec = rd.u(), ntask = rd.u();
    std::vector<u32> snv(nsec);
    std::vector<const float*> sdat(nsec);
    for (u32 s = 0; s < nsec; s++) { snv[s] = rd.u(); sdat[s] = rd.fp((size_t)snv[s] * 6); }
    std::vector<u8> out;
    const char* t2env = getenv("TW_INST_TIER2");
    const bool inst_tier2 = t2env && strcmp(t2env, "1") == 0;
    for (u32 t = 0; t < ntask; t++) {
        u32 kind = rd.u(), sec = rd.u();
        const float* M = rd.fp(13);
        if (sec >= nsec) { fprintf(stderr, "bad section\n"); return 2; }
        u32 nv = snv[sec];
        const u8* mask = nullptr;
        if (kind == 0) { mask = rd.bp(nv); rd.bp((4 - (nv & 3)) & 3); }
        size_t o0 = out.size();
        out.resize(o0 + (size_t)nv * 12 + ((size_t)nv + 3 & ~(size_t)3), 0);
        float* od = (float*)(out.data() + o0);
        u8* st = out.data() + o0 + (size_t)nv * 12;
        for (u32 j = 0; j < nv; j++) {
            const float* V = sdat[sec] + (size_t)j * 6;
            const float* pos = V; const float* nrm = V + 3;
            if (kind == 0) {
                if (!mask[j]) { st[j] = 2; continue; }       // not looked up (no UV)
                float a = nrm[0] * nrm[0], b = nrm[1] * nrm[1], c = nrm[2] * nrm[2];
                float nn = sqrtf((a + b) + c);              // norm3: float32 sqrt
                u32 krow = 0; int tier = 0;
                if (nn > K_NN) {
                    float nr[3] = { nrm[0] / nn, nrm[1] / nn, nrm[2] / nn };
                    tier = vd.lookup(pos, nr, true, true, krow);
                }
                float d[3];
                if (tier && emit_dir(vd.R + (size_t)krow * 11 + 6, d)) { od[j * 3] = d[0]; od[j * 3 + 1] = d[1]; od[j * 3 + 2] = d[2]; st[j] = 0; }
                else st[j] = 1;
            } else {
                float wp[3], wn[3];
                xf_pt(M, pos, wp);
                xf_dir(M, nrm, wn);
                float a = wn[0] * wn[0], b = wn[1] * wn[1], c = wn[2] * wn[2];
                float nn = sqrtf((a + b) + c);
                float nr[3];
                if (nn > K_NN) { nr[0] = wn[0] / nn; nr[1] = wn[1] / nn; nr[2] = wn[2] / nn; }
                else { nr[0] = wn[0]; nr[1] = wn[1]; nr[2] = wn[2]; }
                u32 krow = 0;
                // 2026-11-05 (v377 dirgap): tier 1 only, as lmw_vertex.inst_dirs (see there); TW_INST_TIER2=1 restores the two-tier lookup
                int tier = vd.lookup(wp, nr, true, !inst_tier2, krow);
                float d[3];
                if (tier && emit_dir(vd.R + (size_t)krow * 11 + 6, d)) { od[j * 3] = d[0]; od[j * 3 + 1] = d[1]; od[j * 3 + 2] = d[2]; st[j] = 0; }
                else { fallback_half(wn, d); od[j * 3] = d[0]; od[j * 3 + 1] = d[1]; od[j * 3 + 2] = d[2]; st[j] = 1; }
            }
        }
    }
    writefile(outp, out);
    return 0;
}

// ================================================================================================ vlit
// lmw_atlas.encode for ONE linear rgb (float32 op order of numpy; double pow)
static const double GEXP = 0.45454543828964233;
static inline float npmax(float a, float b) { return (a >= b || a != a) ? a : b; }
static inline float npmin(float a, float b) { return (a <= b || a != a) ? a : b; }
static void encode1(const float* c, float WHITE, float BLACK, float KSCH, float* o) {
    float R = c[0], G = c[1], B = c[2];
    float X = (R * (float)0.412453 + G * (float)0.35758001) + B * (float)0.18042301;
    float Y = (R * (float)0.212671 + G * (float)0.71516001) + B * (float)0.072168998;
    float Z = (R * (float)0.019334 + G * (float)0.119193) + B * (float)0.95022702;
    float t = (Y - BLACK) / (WHITE - BLACK);
    t = (t >= 0.0f) ? npmin(1.0f, t) : 0.0f;
    float s = (X + Y) + Z;
    float v8 = Y / s;
    float v9 = X / s;
    float tk = t * KSCH;
    float tkt = tk - t;
    float q = (float)((double)tk / ((double)tkt + 1.0));
    float qy = q / Y;
    float Yn = qy * Y;
    float Xn = ((X / s) / v8) * Yn;
    float Zn = (((1.0f - v9) - v8) / v8) * Yn;
    float r, g, b;
    { float a1 = Xn * (float)3.240479, a2 = Yn * (float)1.53715, a3 = Zn * (float)0.49853501; r = npmax((a1 - a2) - a3, 0.0f); }
    { float a1 = Yn * (float)1.8759919, a2 = Xn * (float)0.96925598, a3 = Zn * (float)0.041556001; g = npmax((a1 - a2) + a3, 0.0f); }
    { float a1 = Xn * (float)0.055647999, a2 = Yn * (float)0.204043, a3 = Zn * (float)1.0573111; b = npmax((a1 - a2) + a3, 0.0f); }
    float lin[3] = { r, g, b };
    if (Y <= (float)1e-8) { lin[0] = lin[1] = lin[2] = 0.0f; }
    for (int k = 0; k < 3; k++) {
        float x = npmin(npmax(lin[k], 0.0f), 1.0f);
        float e = (float)pow((double)x, GEXP);
        o[k] = npmin(npmax(e, 0.0f), 1.0f);
    }
}
static inline float Yof(const float* c) {
    float a = c[0] * (float)0.212671, b = c[1] * (float)0.71516001, d = c[2] * (float)0.072168998;
    return (a + b) + d;
}
static inline void acc_dir(float* acc, float w, const float* d) {
    float dd[3], v[3];
    memcpy(dd, d, 12);
    float ln = vnorm(dd, v);
    acc[0] = (v[0] * w) + acc[0];
    acc[1] = (v[1] * w) + acc[1];
    acc[2] = (v[2] * w) + acc[2];
    acc[3] = (ln * w) + acc[3];
    acc[4] = w + acc[4];
}
static inline void qpt(const float* n, const float* p, float sgn, float* o) {
    for (int k = 0; k < 3; k++) {
        float a = sgn * n[k];
        float b = a * (float)0.001;
        o[k] = b + p[k];
    }
}
struct Key3 { u32 a, b, c; bool operator==(const Key3& o) const { return a == o.a && b == o.b && c == o.c; } };
struct Key3H { size_t operator()(const Key3& k) const { return ((size_t)k.a * 0x9E3779B97F4A7C15ull) ^ ((size_t)k.b * 0xC2B2AE3D27D4EB4Full) ^ ((size_t)k.c * 0x165667B19E3779F9ull); } };
struct Key8 { u32 w[8]; bool operator==(const Key8& o) const { return !memcmp(w, o.w, 32); } };
struct Key8H { size_t operator()(const Key8& k) const { size_t h = 1469598103934665603ull; for (int i = 0; i < 8; i++) { h ^= k.w[i]; h *= 1099511628211ull; } return h; } };

struct Serve {
    std::vector<u8> buf;
    const float* rows = nullptr;
    std::unordered_map<Key3, u32, Key3H> m;
    void load(const char* path) {
        buf = readfile(path);
        if (buf.size() < 16) { fprintf(stderr, "vertex_out too small\n"); exit(2); }
        u32 mg, n, rw;
        memcpy(&mg, buf.data(), 4); memcpy(&n, buf.data() + 4, 4); memcpy(&rw, buf.data() + 8, 4);
        if (rw != 9 || buf.size() < 16 + (size_t)n * 36) { fprintf(stderr, "bad vertex_out\n"); exit(2); }
        rows = (const float*)(buf.data() + 16);
        m.reserve((size_t)n * 2);
        for (u32 i = 0; i < n; i++) {
            Key3 k; memcpy(&k, rows + (size_t)i * 9, 12);
            m.emplace(k, i);               // emplace keeps the FIRST row (dict.setdefault)
        }
    }
    inline const float* get(const float* q) const {
        Key3 k; memcpy(&k, q, 12);
        auto it = m.find(k);
        return it == m.end() ? nullptr : rows + (size_t)it->second * 9;
    }
};

// job: 'TWV1', weld, nsec, ntask, WHITE, BLACK, KSCH; sections: nv, ntri, nv x 8 floats (pos3, V[17..21]), ntri*3 int32 (+pad to even count? no: 4-byte items);
// tasks: sec, M[13], t, nw.   out: per task nw x 6 floats
static int cmd_vlit(const char* servep, const char* jobp, const char* outp) {
    Serve sv; sv.load(servep);
    std::vector<u8> jb = readfile(jobp);
    Rd rd(jb);
    u32 mg = rd.u();
    if (mg != 0x31565754u) { fprintf(stderr, "bad job magic\n"); return 2; }
    u32 weld = rd.u(), nsec = rd.u(), ntask = rd.u();
    float WHITE = rd.f(), BLACK = rd.f(), KSCH = rd.f();
    struct Sec { u32 nv, nt; const float* V; const i32* I; };
    std::vector<Sec> secs(nsec);
    for (u32 s = 0; s < nsec; s++) {
        secs[s].nv = rd.u(); secs[s].nt = rd.u();
        secs[s].V = rd.fp((size_t)secs[s].nv * 8);
        secs[s].I = (const i32*)rd.fp((size_t)secs[s].nt * 3);
    }
    std::vector<u8> out;
    std::vector<float> col, dac;
    std::vector<i32> wid;
    for (u32 ti = 0; ti < ntask; ti++) {
        u32 si = rd.u();
        const float* M = rd.fp(13);
        float t = rd.f();
        u32 nw_ = rd.u();
        if (si >= nsec) return 2;
        const Sec& S = secs[si];
        u32 nv = S.nv;
        col.assign((size_t)nv * 4, 0.0f);
        dac.assign((size_t)nv * 5, 0.0f);
        wid.assign(nv, 0);
        // weld: first vertex with an identical (position, texcoord, normal) bit pattern
        if (weld) {
            std::unordered_map<Key8, i32, Key8H> wm;
            wm.reserve(nv * 2);
            for (u32 j = 0; j < nv; j++) { Key8 k; memcpy(k.w, S.V + (size_t)j * 8, 32); auto r = wm.emplace(k, (i32)j); wid[j] = r.first->second; }
        } else for (u32 j = 0; j < nv; j++) wid[j] = (i32)j;
        const float tcl = t;
        for (u32 j = 0; j < nv; j++) {
            const float* P = S.V + (size_t)j * 8;           // pos
            const float* N = S.V + (size_t)j * 8 + 5;       // normal (V[19..21])
            float p[3], tmp[3], nw[3];
            xf_pt_s(M, P, p);
            xf_dir(M, N, tmp);
            vnorm(tmp, nw);
            float qk[3];
            qpt(nw, p, 1.0f, qk);
            const float* r = sv.get(qk);
            float* cj = &col[(size_t)j * 4];
            if (!r) {
                if (cj[3] > -1.0f) { cj[0] = cj[0] + 1.0f; cj[1] = cj[1] + 1.0f; cj[2] = cj[2] + 1.0f; cj[3] = cj[3] - 1.0f; }
                continue;
            }
            float c3[3] = { r[3], r[4], r[5] };
            acc_dir(&dac[(size_t)wid[j] * 5], 1.0f * Yof(c3), r + 6);
            float qb[3];
            qpt(nw, p, -1.0f, qb);
            const float* rb = sv.get(qb);
            if (rb && tcl > 0.0f) {
                for (int q = 0; q < 3; q++) { float a = tcl * rb[3 + q]; float b = (1.0f - tcl) * c3[q]; c3[q] = a + b; }
            }
            if (cj[3] <= -1.0f) continue;
            cj[0] = c3[0] + cj[0]; cj[1] = c3[1] + cj[1]; cj[2] = c3[2] + cj[2];
            cj[3] = cj[3] + 1.0f;
        }
        // edges: first-seen order of the unordered edges of the triangle list (a != c, key = (min, max))
        {
            std::unordered_map<u64, char> seen;
            seen.reserve((size_t)S.nt * 4);
            for (u32 ti2 = 0; ti2 < S.nt; ti2++) {
                const i32* tri = S.I + (size_t)ti2 * 3;
                for (int e = 0; e < 3; e++) {
                    i32 a = tri[e], c = tri[(e + 1) % 3];
                    i32 lo = a < c ? a : c, hi = a < c ? c : a;
                    u64 key = ((u64)(u32)lo << 32) | (u32)hi;
                    if (a == c || !seen.emplace(key, 1).second) continue;
                    const float* Pa = S.V + (size_t)a * 8; const float* Pc = S.V + (size_t)c * 8;
                    const float* Na = Pa + 5; const float* Nc = Pc + 5;
                    float mid[3], ns[3];
                    for (int q = 0; q < 3; q++) { float s1 = Pc[q] + Pa[q]; mid[q] = s1 * (float)0.5; }
                    for (int q = 0; q < 3; q++) { float s1 = Na[q] + Nc[q]; ns[q] = s1 * (float)0.5; }
                    float p[3], tmp[3], nw[3], qk[3];
                    xf_pt_s(M, mid, p);
                    xf_dir(M, ns, tmp);
                    vnorm(tmp, nw);
                    qpt(nw, p, 1.0f, qk);
                    const float* r = sv.get(qk);
                    if (!r) continue;
                    float c3[3] = { r[3], r[4], r[5] };
                    float Y = Yof(c3);
                    float w = (float)0.5 * Y;
                    acc_dir(&dac[(size_t)wid[a] * 5], w, r + 6);
                    acc_dir(&dac[(size_t)wid[c] * 5], w, r + 6);
                    float qb[3];
                    qpt(nw, p, -1.0f, qb);
                    const float* rb = sv.get(qb);
                    if (rb && tcl > 0.0f) {
                        for (int q = 0; q < 3; q++) { float a1 = (1.0f - tcl) * c3[q]; float b1 = tcl * rb[3 + q]; c3[q] = a1 + b1; }
                    }
                    for (int k2 = 0; k2 < 2; k2++) {
                        float* ce = &col[(size_t)(k2 == 0 ? a : c) * 4];
                        if (ce[3] <= -1.0f) { ce[0] = ce[1] = ce[2] = ce[3] = 0.0f; }
                        if (ce[3] > -1.0f) { ce[0] = ce[0] + c3[0]; ce[1] = ce[1] + c3[1]; ce[2] = ce[2] + c3[2]; ce[3] = ce[3] + (float)0.5; }
                    }
                }
            }
        }
        for (u32 j = 0; j < nv; j++) { float* cj = &col[(size_t)j * 4]; if (cj[3] <= -1.0f) { cj[0] = cj[1] = cj[2] = cj[3] = 0.0f; } }
        size_t o0 = out.size();
        out.resize(o0 + (size_t)nw_ * 24);
        float* res = (float*)(out.data() + o0);
        for (u32 j = 0; j < nw_ && j < nv; j++) {
            float* r6 = res + (size_t)j * 6;
            for (int q = 0; q < 6; q++) r6[q] = 0.0f;
            const float* cj = &col[(size_t)j * 4];
            float cnt = cj[3];
            if (!(cnt <= K_W || cnt <= -1.0f)) {
                float inv = 1.0f / cnt;
                float c3[3] = { inv * cj[0], cj[1] * inv, cj[2] * inv };
                encode1(c3, WHITE, BLACK, KSCH, r6);
            }
            const float* A = &dac[(size_t)wid[j] * 5];
            float w = A[4];
            if (w <= K_W) {
                float tmp[3], fb[3];
                xf_dir(M, S.V + (size_t)j * 8 + 5, tmp);
                fallback_half(tmp, fb);
                r6[3] = fb[0]; r6[4] = fb[1]; r6[5] = fb[2];
            } else {
                float iw = 1.0f / w;
                float s1 = A[3] * iw;
                float s = s1 * iw;
                r6[3] = s * A[0]; r6[4] = A[1] * s; r6[5] = A[2] * s;
            }
        }
    }
    writefile(outp, out);
    return 0;
}

// ================================================================================================ comp
// lmw_atlas.composite for many pages.  job: 'TWC1', WHITE, BLACK, KSCH, npages; per page: W, H, nch; per chart: h, w, X, Y, tr, h*w*3 floats
// out: per page W*H*4 bytes (B,G,R,A memory order).  Per chart, in chart order: covered texels (first channel != -1.0) get the quantised
// encode(rgb) and alpha 0xFF; uncovered texels get alpha 0x80 unless the page already holds 0xFF there.
static inline u8 quant1(float e) {
    double v = nearbyint((double)e * 255.0);
    return (u8)((i64)v & 0xFF);
}
static int cmd_comp(const char* jobp, const char* outp) {
    std::vector<u8> jb = readfile(jobp);
    Rd rd(jb);
    if (rd.u() != 0x31435754u) { fprintf(stderr, "bad job magic\n"); return 2; }
    float WHITE = rd.f(), BLACK = rd.f(), KSCH = rd.f();
    u32 np = rd.u();
    std::vector<u8> out;
    for (u32 pi = 0; pi < np; pi++) {
        u32 W = rd.u(), H = rd.u(), nch = rd.u();
        size_t o0 = out.size();
        out.resize(o0 + (size_t)W * H * 4);
        u8* px = out.data() + o0;
        for (u32 y = 0; y < H; y++)
            for (u32 x = 0; x < W; x++) {
                u8* q = px + ((size_t)y * W + x) * 4;
                q[0] = 0x40; q[1] = (((y + x) & 1) == 0) ? 0x80 : 0x40; q[2] = 0x80; q[3] = 0;
            }
        for (u32 c = 0; c < nch; c++) {
            u32 h = rd.u(), w = rd.u(), X = rd.u(), Y = rd.u(), tr = rd.u();
            const float* g = rd.fp((size_t)h * w * 3);
            for (u32 gy = 0; gy < h; gy++)
                for (u32 gx = 0; gx < w; gx++) {
                    const float* t = g + ((size_t)gy * w + gx) * 3;
                    u32 ry = tr ? gx + Y : gy + Y;
                    u32 rx = tr ? gy + X : gx + X;
                    if (ry >= H || rx >= W) { fprintf(stderr, "chart outside the page\n"); return 2; }
                    u8* q = px + ((size_t)ry * W + rx) * 4;
                    if (t[0] == -1.0f) {
                        q[3] = (q[3] == 0xFF) ? 0xFF : 0x80;
                    } else {
                        float rgb[3] = { t[0], t[1], t[2] }, e[3];
                        encode1(rgb, WHITE, BLACK, KSCH, e);
                        q[0] = quant1(e[2]); q[1] = quant1(e[1]); q[2] = quant1(e[0]); q[3] = 0xFF;
                    }
                }
        }
    }
    writefile(outp, out);
    return 0;
}

int main(int argc, char** argv) {
    if (argc >= 4 && !strcmp(argv[1], "comp")) return cmd_comp(argv[2], argv[3]);
    if (argc >= 5 && !strcmp(argv[1], "dirs")) return cmd_dirs(argv[2], argv[3], argv[4]);
    if (argc >= 5 && !strcmp(argv[1], "vlit")) return cmd_vlit(argv[2], argv[3], argv[4]);
    fprintf(stderr, "usage: tw_native dirs <vertdir.bin> <job.bin> <out.bin>\n");
    return 2;
}
