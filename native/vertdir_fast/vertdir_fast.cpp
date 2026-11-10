// vertdir_fast.cpp -- native build of vertdir.bin, byte-identical to build_vertdir.py's DEFAULT path (exact keys, the CPU splat weights,
// the supersample splat from the engine's own per-sample record vdsamp.bin). Every float64 operation is the numpy one, in the same order:
// sums of three left-associated ((a+b)+c, as numpy's add.reduce over a 3-wide row), np.round = round half to even (nearbyint),
// np.minimum propagates NaN, argmax returns the first NaN or the first maximum, accumulations add in the bincount order.
// Exit 0 = written; 2 = bad/missing input (the Python's FATAL cases, nothing written); 3 = this input needs a path the native build does
// not carry (offline sample search, an ambiguous table, anything unexpected) -> build_vertdir.py runs its own Python path instead.
// Build: build.bat (cl /O2 /fp:precise, no FMA contraction, no debug info). Single-threaded.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cfloat>
#include <string>
#include <vector>
#include <algorithm>
#include <numeric>
#include <unordered_map>

typedef int64_t i64; typedef uint64_t u64; typedef uint32_t u32;
static bool QUIET = false; static ULONGLONG T0 = 0;
static void logf(const char* fmt, ...) {
    if (QUIET) return;
    char b[1024]; va_list ap; va_start(ap, fmt); vsnprintf(b, sizeof(b), fmt, ap); va_end(ap);
    printf("[vertdir %5.1fs] %s\n", (GetTickCount64() - T0) / 1000.0, b); fflush(stdout);
}
[[noreturn]] static void die(const std::string& m) { printf("VERTDIR-BUILD: FATAL %s\n", m.c_str()); fflush(stdout); ExitProcess(2); }
[[noreturn]] static void bail(const char* why) { if (!QUIET) { printf("[vertdir-native] %s -- the Python path runs\n", why); fflush(stdout); } ExitProcess(3); }

static std::string narrow(const std::wstring& w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr); std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, &s[0], n, nullptr, nullptr); return s;
}
static bool exists(const std::wstring& p) { DWORD a = GetFileAttributesW(p.c_str()); return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY); }
static std::wstring join(const std::wstring& a, const std::wstring& b) { if (a.empty()) return b; wchar_t c = a.back(); return (c == L'\\' || c == L'/') ? a + b : a + L"\\" + b; }
static std::wstring dirname(const std::wstring& p) { size_t k = p.find_last_of(L"\\/"); return k == std::wstring::npos ? L"." : p.substr(0, k); }
static bool readall(const std::wstring& p, std::vector<uint8_t>& out) {
    FILE* f = _wfopen(p.c_str(), L"rb"); if (!f) return false;
    _fseeki64(f, 0, SEEK_END); i64 n = _ftelli64(f); _fseeki64(f, 0, SEEK_SET); out.resize((size_t)n);
    size_t got = n ? fread(out.data(), 1, (size_t)n, f) : 0; fclose(f); return (i64)got == n;
}
static inline double nmin(double a, double b) { return (a != a || b != b) ? NAN : (a < b ? a : b); }      // np.minimum
static inline double ntonum(double x) { return x != x ? 0.0 : (x == INFINITY ? DBL_MAX : (x == -INFINITY ? -DBL_MAX : x)); }   // np.nan_to_num
static inline double norm3(double x, double y, double z) { return std::sqrt((x * x + y * y) + z * z); }   // np.linalg.norm(axis=1), 3 wide

static u32 crc_tab[256];
static u32 crc32(const uint8_t* p, size_t n) {
    for (u32 i = 0; i < 256; i++) { u32 c = i; for (int k = 0; k < 8; k++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1; crc_tab[i] = c; }
    u32 c = 0xFFFFFFFFu; for (size_t i = 0; i < n; i++) c = crc_tab[(c ^ p[i]) & 255] ^ (c >> 8); return c ^ 0xFFFFFFFFu;
}

struct Chart { i64 base, C64, C68, nTri, total; };
static const int CAND4[17][2] = { {2,2},{3,3},{4,3},{3,4},{4,4},{2,2},{3,2},{4,2},{5,2},{2,3},{5,3},{2,4},{5,4},{2,5},{3,5},{4,5},{5,5} };
// NO LAMBDAS in this file: MSVC names a lambda's closure type after a hash of the source path, which reorders the COMDATs -- two builds in
// two folders then differ (build_vertdir_fast.py requires two identical builds). Named functions / functors only.
// compact sort record (16 B): key = ((ordv*2^24 + tix)*256 + sx)*256 + sy (the Python's), r3 = (k<<24)|record, seq = the row's position in the
// kept-row sequence of the concatenated files (np.unique return_index = the FIRST occurrence of a key)
struct Smp0 { i64 key; u32 r3; u32 seq; };
struct ByKey { bool operator()(const Smp0& a, const Smp0& b) const { return a.key != b.key ? a.key < b.key : a.seq < b.seq; } };
#define S_ORDV(s) ((s).key >> 40)
#define S_TIX(s) (((s).key >> 16) & 0xFFFFFF)
#define S_SX(s) (((s).key >> 8) & 255)
#define S_SY(s) ((s).key & 255)
#define S_K(s) ((i64)((s).r3 >> 24))
#define S_REC(s) ((i64)((s).r3 & 0xFFFFFF))
// the triangle of a sample, the Python's resolution: a record below the chart's own count = that soup row, else seam_lists.bin; -1 = dropped
static i64 tri_of(const Smp0& s, const std::vector<Chart>& hdr, const std::vector<i64>& triStart, bool seamOn, const u32* ent,
                  const std::unordered_map<i64, i64>& eOf, i64 T0n, i64 nRec) {
    const i64 ordv = S_ORDV(s), rec = S_REC(s);
    if (rec < hdr[ordv].nTri) return triStart[ordv] + rec;
    if (!seamOn) return -1;
    std::unordered_map<i64, i64>::const_iterator it = eOf.find((i64)((u64)ordv * (1ull << 32) + (u64)S_TIX(s)));
    const i64 s2 = rec - hdr[ordv].nTri;
    if (it == eOf.end() || !(s2 < (i64)ent[it->second * 4 + 3])) return -1;
    const i64 srow = (i64)ent[it->second * 4 + 2] + s2; if (srow >= nRec) bail("a seam record past the table");
    return T0n + srow;
}
struct RowLess {                                                                // np.unique(axis=0): lexicographic over signed int64 rows
    const i64* qp; const i64* qn; bool withN;
    bool operator()(i64 a, i64 b) const {
        for (int q = 0; q < 3; q++) if (qp[a * 3 + q] != qp[b * 3 + q]) return qp[a * 3 + q] < qp[b * 3 + q];
        if (withN) for (int q = 0; q < 3; q++) if (qn[a * 3 + q] != qn[b * 3 + q]) return qn[a * 3 + q] < qn[b * 3 + q];
        return false;
    }
};
static i64 uniq_rows(const std::vector<i64>& qp, const std::vector<i64>& qn, i64 NV, bool withN, std::vector<i64>& inv) {
    std::vector<i64> o((size_t)NV); for (i64 i = 0; i < NV; i++) o[i] = i;
    RowLess less = { qp.data(), qn.data(), withN };
    std::sort(o.begin(), o.end(), less);
    inv.assign((size_t)NV, 0); i64 id = -1;
    for (i64 i = 0; i < NV; i++) { if (i == 0 || less(o[i - 1], o[i])) id++; inv[o[i]] = id; }
    return id + 1;
}
static const u32* load_rows(const std::wstring& path, u32 magic, u32 recMin, std::vector<uint8_t>& raw, i64& nchF, i64& n, i64& rec) {
    if (!readall(path, raw)) die("cannot read " + narrow(path));
    const u32* a = (const u32*)raw.data(); const size_t sz = raw.size() / 4;
    if (sz < 4 || a[0] != magic) { char b[400]; snprintf(b, sizeof(b), "%s: bad magic %08X (want %08X)", narrow(path).c_str(), sz ? a[0] : 0u, magic); die(b); }
    n = a[1]; rec = a[2];
    if (rec < recMin || (i64)sz < 4 + n * rec) { char b[400]; snprintf(b, sizeof(b), "%s: header n=%lld rec=%lld vs %zu words", narrow(path).c_str(), (long long)n, (long long)rec, sz); die(b); }
    nchF = a[3]; return a + 4;
}
static void flat_rows(const u32* R, i64 n, i64 rec, const char* name, const std::vector<Chart>& hdr, const std::vector<i64>& start, std::vector<i64>& fl) {
    const i64 nch = (i64)hdr.size(); fl.resize((size_t)n);
    for (i64 i = 0; i < n; i++) { const i64 o = R[i * rec], t = R[i * rec + 1];
        if (o >= nch || t >= hdr[std::min<i64>(o, nch - 1)].total) die(std::string(name) + ": (ord, texIdx) out of the soup chart table");
        fl[i] = start[o] + t; }
}
struct Sel { u32 x; u32 tri; double b1, b2; };      // the splat weights read only barycentrics 1 and 2 (w0 = ((1-b1)+b2)*0.5+0.5)
// accumulate in the Python's bincount order: every sample's corner 0, then every sample's corner 1, then corner 2
static void accumulate(const std::vector<Sel>& sel, const std::vector<i64>& vk, i64 NVk, const std::vector<double>& Yt, const std::vector<double>& DIR,
                       const std::vector<double>& AB, std::vector<double>& A0, std::vector<double>& A3, std::vector<double>& A4) {
    A0.assign((size_t)NVk * 3, 0.0); A3.assign((size_t)NVk, 0.0); A4.assign((size_t)NVk, 0.0);
    for (int k = 0; k < 3; k++) for (size_t i = 0; i < sel.size(); i++) {
        const Sel& s = sel[i];
        const double wk = k == 0 ? (((1.0 - s.b1) + s.b2) * 0.5 + 0.5) : ((k == 1 ? s.b1 : s.b2) * 0.5 + 0.5);
        if (!(wk > 1e-4)) continue;
        const double w = Yt[s.x] * wk; const i64 v = vk[(size_t)s.tri * 3 + k];
        A0[v * 3] += DIR[(size_t)s.x * 3] * w; A0[v * 3 + 1] += DIR[(size_t)s.x * 3 + 1] * w; A0[v * 3 + 2] += DIR[(size_t)s.x * 3 + 2] * w;
        A3[v] += AB[s.x] * w; A4[v] += w;
    }
}

int wmain(int argc, wchar_t** argv) {
    T0 = GetTickCount64();
    std::wstring run, atlas, adir, soup, out; bool stamp = false; int ssn = 4;
    for (int i = 1; i < argc; i++) {
        std::wstring a = argv[i];
        const bool hasVal = (a == L"--atlas" || a == L"--dir" || a == L"--soup" || a == L"--out" || a == L"--ssn");
        if (hasVal && i + 1 >= argc) bail("missing option value");
        if (a == L"--atlas") atlas = argv[++i]; else if (a == L"--dir") adir = argv[++i]; else if (a == L"--soup") soup = argv[++i]; else if (a == L"--out") out = argv[++i];
        else if (a == L"--stamp") stamp = true; else if (a == L"--quiet") QUIET = true;
        else if (a == L"--exact-keys") {}                                          // the default, as in the Python
        else if (a == L"--ssn") ssn = _wtoi(argv[++i]);
        else if (a.size() > 1 && a[0] == L'-') bail("an option the native build does not carry (--legacy / --centre-splat / ...)");
        else if (run.empty()) run = a; else bail("unexpected argument");
    }
    if (run.empty()) bail("no run directory");
    if (ssn <= 0) bail("--ssn 0 (the centre splat)");
    { wchar_t e[8] = {0}; if (GetEnvironmentVariableW(L"VD_OWN_SEARCH", e, 8) > 0 && e[0]) bail("VD_OWN_SEARCH (the offline sample search)"); }
    if (atlas.empty()) atlas = join(run, L"atlas_out.bin"); if (adir.empty()) adir = join(run, L"atlas_dir.bin");
    if (soup.empty()) soup = join(run, L"texelgen_soup.bin"); if (out.empty()) out = join(run, L"vertdir.bin");
    for (auto* p : { &atlas, &adir, &soup }) if (!exists(*p)) die("missing input " + narrow(*p));
    bool keepSeam = false; { wchar_t e[8] = {0}; keepSeam = GetEnvironmentVariableW(L"GPULM_SEAM_SOUP", e, 8) == 1 && e[0] == L'1'; }

    // ---------------- parse_soup
    std::vector<Chart> hdr; std::vector<double> TR; std::vector<i64> tch;      // TR: T x 25 float64
    {
        std::vector<uint8_t> raw; if (!readall(soup, raw)) die("cannot read " + narrow(soup));
        if (raw.size() % 4) die(narrow(soup) + ": not a whole number of words");
        const u32* u = (const u32*)raw.data(); const float* f = (const float*)raw.data(); const size_t n = raw.size() / 4; size_t p = 0;
        while (p + 5 <= n) {
            const i64 base = u[p], C64 = u[p + 1], C68 = u[p + 2], nTri = u[p + 3], ext = u[p + 4]; p += 5;
            if (p + (size_t)nTri * 25 > n) break;                              // the Python: p stays past this header -> FATAL below
            i64 kept = 0;
            for (i64 t = 0; t < nTri; t++) {
                const float* r = f + p + (size_t)t * 25;
                if (!keepSeam && r[24] < 0.0f) continue;
                for (int c = 0; c < 25; c++) TR.push_back((double)r[c]);
                tch.push_back((i64)hdr.size()); kept++;
            }
            p += (size_t)nTri * 25;
            hdr.push_back({ base, C64, C68, kept, ext ? ext : C64 * C68 });
        }
        if (p != n) {
            char b[400]; snprintf(b, sizeof(b), "%s: trailing %lld words after %zu charts -- not a soup this parser understands", narrow(soup).c_str(), (long long)(n - p), hdr.size());
            die(b);
        }
        for (auto& c : hdr) if (c.C64 <= 0) bail("a chart with a zero row pitch");
    }
    const i64 nch = (i64)hdr.size(), T0n = (i64)tch.size();
    std::vector<i64> start(nch), triStart(nch + 1, 0); i64 NT = 0;
    for (i64 c = 0; c < nch; c++) { start[c] = NT; NT += hdr[c].total; triStart[c + 1] = triStart[c] + hdr[c].nTri; }
    logf("soup: %lld charts, %lld native tris, %lld grid texels", (long long)nch, (long long)T0n, (long long)NT);
    for (size_t i = 0; i < (size_t)T0n * 25; i++) if (!std::isfinite(TR[i]) && (i % 25) < 24) bail("a non-finite soup value");

    // ---------------- atlas_out.bin / atlas_dir.bin
    std::vector<uint8_t> rawA, rawD; i64 nchA, nA, recA, nchD, nD, recD;
    const u32* RA = load_rows(atlas, 0x314C5441u, 5, rawA, nchA, nA, recA);
    const u32* RD = load_rows(adir, 0x32524944u, 6, rawD, nchD, nD, recD);
    if (nchA != nch || nchD != nch) { char b[300]; snprintf(b, sizeof(b), "chart count mismatch: soup %lld, atlas %lld, atlas_dir %lld -- files from different runs/maps", (long long)nch, (long long)nchA, (long long)nchD); die(b); }
    std::vector<i64> flA, flD; flat_rows(RA, nA, recA, "atlas_out.bin", hdr, start, flA); flat_rows(RD, nD, recD, "atlas_dir.bin", hdr, start, flD);
    std::vector<uint8_t> covered(NT, 0), HASD(NT, 0); std::vector<double> Yt(NT, 0.0), DIR(NT * 3, 0.0), AB(NT, 0.0);
    {
        std::vector<double> G3(NT * 3, NAN);
        for (i64 i = 0; i < nA; i++) { const float* r = (const float*)(RA + i * recA); for (int c = 0; c < 3; c++) G3[flA[i] * 3 + c] = (double)r[2 + c]; covered[flA[i]] = 1; }
        for (i64 x = 0; x < NT; x++) Yt[x] = ntonum((0.2126 * G3[x * 3] + 0.7152 * G3[x * 3 + 1]) + 0.0722 * G3[x * 3 + 2]);
        for (i64 i = 0; i < nD; i++) { const float* r = (const float*)(RD + i * recD); const i64 x = flD[i];
            DIR[x * 3] = r[2]; DIR[x * 3 + 1] = r[3]; DIR[x * 3 + 2] = r[4]; AB[x] = r[5]; HASD[x] = 1; }
    }

    // ---------------- rasterise (coh_c: last triangle in soup order wins)
    std::vector<int32_t> texTri(NT, -1); std::vector<double> texB(NT * 3, 0.0);
    for (i64 t = 0; t < T0n; t++) {
        const double* r = &TR[t * 25]; const Chart& C = hdr[tch[t]]; const i64 H = C.C64, Wd = C.C68;
        const double ax = r[0], ay = r[1], bx = r[2], by = r[3], cx = r[4], cy = r[5];
        const double lox = std::min(std::min(ax, bx), cx), loy = std::min(std::min(ay, by), cy), hix = std::max(std::max(ax, bx), cx), hiy = std::max(std::max(ay, by), cy);
        const i64 u0 = std::max<i64>(0, (i64)std::floor(lox - 0.5)), u1 = std::min<i64>(H - 1, (i64)std::ceil(hix + 0.5));
        const i64 v0 = std::max<i64>(0, (i64)std::floor(loy - 0.5)), v1 = std::min<i64>(Wd - 1, (i64)std::ceil(hiy + 0.5));
        const double d0x = bx - ax, d0y = by - ay, d1x = cx - ax, d1y = cy - ay, den = d0x * d1y - d1x * d0y;
        if (!(u1 >= u0 && v1 >= v0 && !(std::fabs(den) < 1e-12))) continue;
        const i64 base = start[tch[t]];
        for (i64 uu = u0; uu <= u1; uu++) for (i64 vv = v0; vv <= v1; vv++) {
            const double px = (double)uu + 0.5, py = (double)vv + 0.5, ex = px - ax, ey = py - ay;
            const double l1 = (ex * d1y - d1x * ey) / den, l2 = (d0x * ey - ex * d0y) / den, l0 = (1.0 - l1) - l2;
            if (!(l0 >= -1e-6 && l1 >= -1e-6 && l2 >= -1e-6)) continue;
            if (!((vv * H + uu) < C.total)) continue;
            const i64 x = base + vv * H + uu; texTri[x] = (int32_t)t; texB[x * 3] = l0; texB[x * 3 + 1] = l1; texB[x * 3 + 2] = l2;
        }
    }
    { i64 k = 0; for (i64 x = 0; x < NT; x++) k += texTri[x] >= 0; logf("rasterised: %lld texels have a containing triangle", (long long)k); }

    // ---------------- clamp (coh_g: the chart triangle with the largest min-barycentric, clipped and renormalised)
    i64 ncl = 0; i64 ncov = 0;
    {
        std::vector<i64> cidOf(NT); for (i64 c = 0; c < nch; c++) for (i64 j = 0; j < hdr[c].total; j++) cidOf[start[c] + j] = c;
        for (i64 x = 0; x < NT; x++) {
            ncov += covered[x];
            if (!(covered[x] && texTri[x] < 0)) continue;
            ncl++;
            const i64 c = cidOf[x], ts = triStart[c], te = triStart[c + 1]; if (te <= ts) continue;
            const i64 li = x - start[c], hh = hdr[c].C64; const double pu = (double)(li % hh) + 0.5, pv = (double)(li / hh) + 0.5;
            i64 best = -1; double bestPen = 0, b0 = 0, b1 = 0, b2 = 0; bool sawNan = false;
            for (i64 j = ts; j < te; j++) {
                const double* r = &TR[j * 25];
                const double d0x = r[2] - r[0], d0y = r[3] - r[1], d1x = r[4] - r[0], d1y = r[5] - r[1];
                double den = d0x * d1y - d1x * d0y; if (std::fabs(den) < 1e-12) den = 1e-12;
                const double ex = pu - r[0], ey = pv - r[1];
                const double l1 = (ex * d1y - d1x * ey) / den, l2 = (d0x * ey - ex * d0y) / den, l0 = (1.0 - l1) - l2;
                const double pen = nmin(l0, nmin(l1, l2));
                if (sawNan) continue;
                if (pen != pen) { best = j; b0 = l0; b1 = l1; b2 = l2; sawNan = true; continue; }    // np.argmax: the first NaN
                if (best < 0 || pen > bestPen) { best = j; bestPen = pen; b0 = l0; b1 = l1; b2 = l2; }
            }
            b0 = b0 < 0 ? 0.0 : b0; b1 = b1 < 0 ? 0.0 : b1; b2 = b2 < 0 ? 0.0 : b2;                     // np.clip(., 0, None) (NaN stays)
            const double s = (b0 + b1) + b2;
            texTri[x] = (int32_t)best; texB[x * 3] = b0 / s; texB[x * 3 + 1] = b1 / s; texB[x * 3 + 2] = b2 / s;
        }
    }
    logf("clamped %lld covered texels with no containing triangle; covered %lld", (long long)ncl, (long long)ncov);
    std::vector<uint8_t> good(NT);
    i64 nsel0 = 0;
    for (i64 x = 0; x < NT; x++) { const double L = norm3(DIR[x * 3], DIR[x * 3 + 1], DIR[x * 3 + 2]);
        good[x] = HASD[x] && L > 1e-9 && Yt[x] > 0; nsel0 += good[x] && texTri[x] >= 0; }
    if (!nsel0) die("no texel carries a direction -- atlas_dir.bin is empty/zero; refusing to write an empty vertdir");

    // ---------------- vdsamp_samples: the engine's per-sample record
    std::vector<std::wstring> files;
    if (exists(join(run, L"fullgpu_key.bin"))) files.push_back(join(run, L"vdsamp.bin"));
    else {
        WIN32_FIND_DATAW fd; HANDLE h = FindFirstFileW(join(join(run, L"parts"), L"p*").c_str(), &fd); std::vector<std::wstring> dirs;
        if (h != INVALID_HANDLE_VALUE) { do { if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && fd.cFileName[0] != L'.') dirs.push_back(fd.cFileName); } while (FindNextFileW(h, &fd)); FindClose(h); }
        std::vector<std::wstring> paths; for (auto& d : dirs) { std::wstring p = join(join(join(run, L"parts"), d), L"vdsamp.bin"); if (exists(p)) paths.push_back(p); }
        std::sort(paths.begin(), paths.end()); files = paths;
    }
    if (files.empty() || !std::all_of(files.begin(), files.end(), exists)) bail("vdsamp.bin absent (the offline sample search)");
    if ((u64)NT >= 0xFFFFFFFFull) bail("a grid past 2^32 texels");
    std::unordered_map<i64, i64> baseOf; baseOf.reserve((size_t)nch * 2);
    for (i64 c = 0; c < nch; c++) if (!baseOf.emplace(hdr[c].base, c).second) bail("duplicate gridBase in the soup");
    std::vector<Smp0> sv; i64 nall = 0, nbad = 0;
    { u64 cap = 0;                                                              // reserve exactly: no capacity doubling of a multi-GB array
      for (size_t fi = 0; fi < files.size(); fi++) { FILE* f = _wfopen(files[fi].c_str(), L"rb"); u32 h[4] = { 0, 0, 0, 0 };
          if (f) { if (fread(h, 4, 4, f) != 4) h[1] = 0; fclose(f); } cap += h[1]; }
      if (cap >= 0xFFFFFFFFull) bail("more than 2^32 vdsamp rows");
      sv.reserve((size_t)cap); }
    for (size_t fi = 0; fi < files.size(); fi++) {                            // one file pair in memory at a time
        const std::wstring& vp = files[fi];
        std::vector<uint8_t> a8, k8; const std::wstring kp = join(dirname(vp), L"fullgpu_key.bin");
        if (!exists(kp) || !readall(vp, a8) || !readall(kp, k8)) bail("vdsamp.bin without its fullgpu_key.bin");
        const u32* a = (const u32*)a8.data(); const u32* kk = (const u32*)k8.data(); const size_t as = a8.size() / 4, ks = k8.size() / 4;
        if (as < 4 || a[0] != 0x31534456u || (int)a[3] != ssn) bail("vdsamp.bin header / ssN differs");
        const size_t n = a[1];
        if (ks < 1 || kk[0] != n || as < 4 + 4 * n || ks < 1 + 4 * n) bail("vdsamp.bin / fullgpu_key.bin sizes");
        for (size_t i = 0; i < n; i++) if (a[4 + i * 4] != kk[1 + i * 4] || a[4 + i * 4 + 1] != kk[1 + i * 4 + 1]) bail("vdsamp.bin does not belong to this bake");
        std::vector<uint8_t>().swap(k8);
        nall += (i64)n;
        for (size_t i = 0; i < n; i++) {
            const u32* r = a + 4 + i * 4;
            const u32 tier = (r[2] >> 16) & 255, killed = (r[2] >> 24) & 1;
            if (!(r[0] != 0xFFFFFFFFu && r[3] != 0xFFFFFFFFu && tier == 1 && killed == 0)) continue;
            std::unordered_map<i64, i64>::const_iterator it = baseOf.find((i64)r[0]); if (it == baseOf.end()) { nbad++; continue; }
            if (r[1] >= (1u << 24) || it->second >= (1 << 23)) bail("a vdsamp key past the packed range");
            if (sv.size() >= 0xFFFFFFFFull) bail("more than 2^32 vdsamp rows");
            Smp0 s; s.key = (i64)((((u64)it->second * (1ull << 24) + (u64)r[1]) * 256ull + (u64)(r[2] & 255)) * 256ull + (u64)((r[2] >> 8) & 255));
            s.r3 = r[3]; s.seq = (u32)sv.size(); sv.push_back(s);
        }
    }
    if (nbad) logf("*** vdsamp: %lld rows with a gridBase not in the soup -- dropped", (long long)nbad);
    std::sort(sv.begin(), sv.end(), ByKey());                                   // (key, seq): equal keys keep their file order
    { size_t w = 0; for (size_t i = 0; i < sv.size(); i++) if (i == 0 || sv[i].key != sv[i - 1].key) sv[w++] = sv[i]; sv.resize(w); sv.shrink_to_fit(); }
    size_t nNonOwn = 0; for (size_t i = 0; i < sv.size(); i++) nNonOwn += S_REC(sv[i]) >= hdr[S_ORDV(sv[i])].nTri;
    i64 T = T0n;                                                                // len(TRx)
    std::vector<uint8_t> s8; const u32* ent = nullptr; std::unordered_map<i64, i64> eOf; i64 nRec = 0; bool seamOn = false;
    if (nNonOwn) {
        const std::wstring sp = join(run, L"seam_lists.bin");
        if (!exists(sp)) logf("*** vdsamp: %zu seam-record samples but no seam_lists.bin -- those samples dropped", nNonOwn);
        else {
            if (!readall(sp, s8)) bail("cannot read seam_lists.bin");
            const u32* s = (const u32*)s8.data(); const size_t ss = s8.size() / 4; if (ss < 4) bail("seam_lists.bin header");
            const i64 nE = s[2]; nRec = s[3]; if ((i64)ss < 4 + nE * 4 + nRec * 25) bail("seam_lists.bin size");
            ent = s + 4; const float* SR = (const float*)(s + 4 + nE * 4);
            eOf.reserve((size_t)nE * 2);
            for (i64 e = 0; e < nE; e++) if (!eOf.emplace((i64)((u64)ent[e * 4] * (1ull << 32) + ent[e * 4 + 1]), e).second) bail("duplicate seam_lists entry");
            TR.reserve(TR.size() + (size_t)nRec * 25);
            for (i64 j = 0; j < nRec * 25; j++) TR.push_back((double)SR[j]);
            T = T0n + nRec; seamOn = true;
            size_t nres = 0;
            for (size_t i = 0; i < sv.size(); i++) if (S_REC(sv[i]) >= hdr[S_ORDV(sv[i])].nTri && tri_of(sv[i], hdr, triStart, seamOn, ent, eOf, T0n, nRec) >= 0) nres++;
            logf("vdsamp: %zu seam-record samples (%zu resolved)", nNonOwn, nres);
        }
    }
    if ((u64)T * 3 >= 0xFFFFFFFFull) bail("more than 2^32 vertices");
    // one pass: reconstruct each sample, count it on its texel (nacc), keep it for the splat when its texel is good
    std::vector<u32> nacc((size_t)NT, 0); std::vector<Sel> sel; sel.reserve(sv.size() + 1024);
    size_t ndrop = 0, nKept = 0; i64 nSmp = 0; double bmin = INFINITY, bmax = -INFINITY;
    for (size_t i = 0; i < sv.size(); i++) {
        const Smp0& s = sv[i];
        const i64 tri = tri_of(s, hdr, triStart, seamOn, ent, eOf, T0n, nRec); if (tri < 0) continue;
        const i64 ordv = S_ORDV(s), tix = S_TIX(s), k = S_K(s); if (k >= 17) bail("a vdsamp candidate index >= 17");
        const i64 H = hdr[ordv].C64;
        const double spx = (double)(tix % H) + ((double)S_SX(s) + CAND4[k][0] / 4.0) / (double)ssn;
        const double spy = (double)(tix / H) + ((double)S_SY(s) + CAND4[k][1] / 4.0) / (double)ssn;
        const double* r = &TR[tri * 25];
        const double e1x = r[2] - r[0], e1y = r[3] - r[1], e2x = r[4] - r[0], e2y = r[5] - r[1], ddx = spx - r[0], ddy = spy - r[1];
        double tot = e1x * e2y - e1y * e2x; if (tot == 0) tot = 1e-30;
        const double w1 = (e2y * ddx - e2x * ddy) / tot, w2 = (e1x * ddy - e1y * ddx) / tot;
        if (!(nmin(nmin(w1, w2), (1.0 - w1) - w2) >= -1e-3)) { ndrop++; continue; }
        const i64 tex = start[ordv] + tix; if (tex < 0 || tex >= NT) bail("a vdsamp texel past the grid");
        nacc[tex]++; nKept++;
        bmin = std::min(bmin, std::min(std::min(w1, w2), (1.0 - w1) - w2)); bmax = std::max(bmax, std::max(w1, w2));
        if (good[tex]) { Sel e; e.x = (u32)tex; e.tri = (u32)tri; e.b1 = w1; e.b2 = w2; sel.push_back(e); nSmp++; }
    }
    std::vector<Smp0>().swap(sv);
    if (ndrop) logf("vdsamp: %zu sample(s) do not reconstruct inside their triangle -- dropped", ndrop);
    i64 nTexAcc = 0; for (i64 x = 0; x < NT; x++) nTexAcc += nacc[x] > 0;
    logf("vdsamp: %zu of %lld engine rows kept (tier-1 hits that passed the 7-ray gate), %lld texels with an accepted sample; bary range [%.4f, %.4f]",
         nKept, (long long)nall, (long long)nTexAcc, bmin, bmax);
    // ---------------- the splat list: the accepted samples of good texels, then the rescue texels (ascending)
    i64 nResc = 0;
    for (i64 x = 0; x < NT; x++) if (covered[x] && nacc[x] == 0 && texTri[x] >= 0 && good[x]) {
        Sel e; e.x = (u32)x; e.tri = (u32)texTri[x]; e.b1 = texB[x * 3 + 1]; e.b2 = texB[x * 3 + 2]; sel.push_back(e); nResc++; }
    logf("supersample N=%d: %lld accepted samples on %lld texels, %lld rescue texels", ssn, (long long)nSmp, (long long)nTexAcc, (long long)nResc);

    // ---------------- vertex keys (exact): tier 1 = (round(P*1024), round(N*8)), tier 2 = round(P*1024)
    const i64 NV = T * 3;
    std::vector<double> P(NV * 3), Nn(NV * 3); std::vector<i64> qp(NV * 3), qn(NV * 3);
    for (i64 t = 0; t < T; t++) for (int c = 0; c < 3; c++) {
        const i64 j = t * 3 + c; const double* r = &TR[t * 25];
        const double px = r[6 + 3 * c], py = r[7 + 3 * c], pz = r[8 + 3 * c], nx = r[15 + 3 * c], ny = r[16 + 3 * c], nz = r[17 + 3 * c];
        if (!(std::isfinite(px) && std::isfinite(py) && std::isfinite(pz) && std::isfinite(nx) && std::isfinite(ny) && std::isfinite(nz))) bail("a non-finite vertex");
        const double l = norm3(nx, ny, nz) + 1e-30;
        P[j * 3] = px; P[j * 3 + 1] = py; P[j * 3 + 2] = pz; Nn[j * 3] = nx / l; Nn[j * 3 + 1] = ny / l; Nn[j * 3 + 2] = nz / l;
        for (int q = 0; q < 3; q++) { qp[j * 3 + q] = (i64)std::nearbyint(P[j * 3 + q] * 1024); qn[j * 3 + q] = (i64)std::nearbyint(Nn[j * 3 + q] * 8); }
    }
    std::vector<i64> vin, vip; const i64 nUn = uniq_rows(qp, qn, NV, true, vin), nUp = uniq_rows(qp, qn, NV, false, vip);
    std::vector<double> A0n, A3n, A4n, A0p, A3p, A4p;
    accumulate(sel, vin, nUn, Yt, DIR, AB, A0n, A3n, A4n); accumulate(sel, vip, nUp, Yt, DIR, AB, A0p, A3p, A4p);
    std::vector<double> Pn(nUn * 3, 0.0), NnU(nUn * 3, 0.0), Pp(nUp * 3, 0.0);
    for (i64 j = 0; j < NV; j++) for (int q = 0; q < 3; q++) { Pn[vin[j] * 3 + q] = P[j * 3 + q]; NnU[vin[j] * 3 + q] = Nn[j * 3 + q]; Pp[vip[j] * 3 + q] = P[j * 3 + q]; }

    // ---------------- write
    std::vector<float> rows; i64 n1 = 0, n2 = 0;
    for (i64 v = 0; v < nUn; v++) if (A4n[v] > 1e-4) { n1++; for (int q = 0; q < 3; q++) rows.push_back((float)Pn[v * 3 + q]); for (int q = 0; q < 3; q++) rows.push_back((float)NnU[v * 3 + q]);
        for (int q = 0; q < 3; q++) rows.push_back((float)A0n[v * 3 + q]); rows.push_back((float)A3n[v]); rows.push_back((float)A4n[v]); }
    for (i64 v = 0; v < nUp; v++) if (A4p[v] > 1e-4) { n2++; for (int q = 0; q < 3; q++) rows.push_back((float)Pp[v * 3 + q]); for (int q = 0; q < 3; q++) rows.push_back(0.0f);
        for (int q = 0; q < 3; q++) rows.push_back((float)A0p[v * 3 + q]); rows.push_back((float)A3p[v]); rows.push_back((float)A4p[v]); }
    u32 sid = 0;
    if (stamp) { if (!readall(atlas, rawA)) die("cannot read " + narrow(atlas)); sid = crc32(rawA.data(), rawA.size()); if (!sid) sid = 1; }
    const std::wstring tmp = out + L".tmp";
    { FILE* f = _wfopen(tmp.c_str(), L"wb"); if (!f) die("cannot write " + narrow(tmp));
      const u32 h[4] = { 0x31524456u, (u32)(n1 + n2), 11u, sid }; bool ok = fwrite(h, 4, 4, f) == 4 && fwrite(rows.data(), 4, rows.size(), f) == rows.size();
      ok = (fclose(f) == 0) && ok; if (!ok) die("short write " + narrow(tmp)); }
    if (!MoveFileExW(tmp.c_str(), out.c_str(), MOVEFILE_REPLACE_EXISTING)) die("cannot replace " + narrow(out));
    // the log statistic (row-weighted tier-1 |D|; the Python's pairwise sums agree to the printed 4 decimals)
    std::vector<double> mult(nUn, 0.0); for (i64 j = 0; j < NV; j++) mult[vin[j]] += 1.0;
    long double sm = 0, sw = 0;
    for (i64 v = 0; v < nUn; v++) if (A4n[v] > 1e-4) { const double mn = norm3(A0n[v * 3], A0n[v * 3 + 1], A0n[v * 3 + 2]) / A4n[v] * (A3n[v] / A4n[v]); sm += (long double)mn * mult[v]; sw += mult[v]; }
    printf("VERTDIR-BUILD: wrote %s: %lld rows (tier1 %lld, tier2 %lld), %zu texels fed, stamp %08X, tier1 |D| row-weighted %.4f\n",
           narrow(out).c_str(), (long long)(n1 + n2), (long long)n1, (long long)n2, sel.size(), sid, (double)(sm / sw)); fflush(stdout);
    return 0;
}
