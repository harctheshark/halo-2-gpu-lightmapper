// gpucheck.exe -- H2 GPU Lightmapper standalone, step S1.6 (N2). x64, /MT, MSVC 14.44.
//
// Read-only GPU capability probe. Prints ONE JSON document (UTF-8) on stdout:
//   * every DXGI adapter: name, vendor/device ids, VRAM, software flag, LUID, and (unless --no-per-adapter) its own
//     D3D11 feature level + FP64 caps from a short-lived device created on that adapter;
//   * the DEFAULT adapter exactly as fullgpu.exe gets it -- D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, ...)
//     (gather.cpp:1053) -- with its feature level, DoublePrecisionFloatShaderOps and ExtendedDoublesShaderInstructions;
//   * the TDR registry values (HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers), read only;
//   * a verdict: ok = default adapter is hardware, FL >= 11_0, doubles, extended doubles.
//
// Like the N3 guarded fullgpu.exe, this exe exports NvOptimusEnablement / AmdPowerXpressRequestHighPerformance, so on a
// hybrid (Optimus / PowerXpress) laptop its "default adapter" is the one the guarded fullgpu will get. The production
// fullgpu.exe (no exports) may get the integrated GPU there instead; "hybrid" in the JSON flags that case, and the
// gpucheck_plain.exe variant (built with /DGPUCHECK_NO_OPTIMUS_EXPORTS, "optimus_exports": false) reports what an
// export-less process such as the production fullgpu.exe gets.
//
// Nothing is ever written: no registry write, no file write unless --out FILE is given (then the JSON goes there too).
// Exit codes: 0 verdict ok, 1 verdict not ok (the app must refuse), 2 internal error (DXGI unavailable, bad args).
//
// Usage: gpucheck.exe [--out FILE] [--no-per-adapter] [--pretty]
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "pdh.lib")

#ifndef GPUCHECK_NO_OPTIMUS_EXPORTS
extern "C" {
__declspec(dllexport) DWORD NvOptimusEnablement = 0x00000001;
__declspec(dllexport) int AmdPowerXpressRequestHighPerformance = 1;
}
static const bool kOptimusExports = true;
#else
static const bool kOptimusExports = false;
#endif

static const char* kVersion = "1.1.0";

// ---------------------------------------------------------------- tiny JSON writer
struct Json {
    std::string s;
    bool pretty = false;
    int depth = 0;
    std::vector<bool> first;   // per open container: no member written yet
    void nl() {
        if (!pretty) return;
        s += '\n';
        s.append((size_t)depth * 2, ' ');
    }
    void sep() {
        if (!first.empty()) {
            if (!first.back()) s += ',';
            first.back() = false;
            nl();
        }
    }
    void key(const char* k) {
        sep();
        str_raw(k);
        s += pretty ? ": " : ":";
    }
    void open(char c) { s += c; ++depth; first.push_back(true); }
    void close(char c) {
        bool empty = first.back();
        first.pop_back();
        --depth;
        if (!empty) nl();
        s += c;
    }
    void str_raw(const char* v) {
        s += '"';
        for (const unsigned char* p = (const unsigned char*)v; *p; ++p) {
            unsigned char c = *p;
            if (c == '"') s += "\\\"";
            else if (c == '\\') s += "\\\\";
            else if (c == '\n') s += "\\n";
            else if (c == '\r') s += "\\r";
            else if (c == '\t') s += "\\t";
            else if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); s += b; }
            else s += (char)c;   // UTF-8 passes through
        }
        s += '"';
    }
    // value writers (inside an array: no key)
    void vstr(const char* v) { str_raw(v); }
    void vnum(unsigned long long v) { char b[32]; sprintf_s(b, "%llu", v); s += b; }
    void vint(long long v) { char b[32]; sprintf_s(b, "%lld", v); s += b; }
    void vbool(bool v) { s += v ? "true" : "false"; }
    // members
    void mstr(const char* k, const char* v) { key(k); str_raw(v); }
    void mstr(const char* k, const std::string& v) { key(k); str_raw(v.c_str()); }
    void mnum(const char* k, unsigned long long v) { key(k); vnum(v); }
    void mint(const char* k, long long v) { key(k); vint(v); }
    void mbool(const char* k, bool v) { key(k); vbool(v); }
    void mnull(const char* k) { key(k); s += "null"; }
    void mobj(const char* k) { key(k); open('{'); }
    void marr(const char* k) { key(k); open('['); }
    void aobj() { sep(); open('{'); }
    void astr(const char* v) { sep(); str_raw(v); }
};

static std::string utf8(const wchar_t* w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return std::string();
    std::string r((size_t)n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, &r[0], n, nullptr, nullptr);
    return r;
}

static const char* fl_name(D3D_FEATURE_LEVEL fl) {
    switch (fl) {
    case D3D_FEATURE_LEVEL_12_2: return "12_2";
    case D3D_FEATURE_LEVEL_12_1: return "12_1";
    case D3D_FEATURE_LEVEL_12_0: return "12_0";
    case D3D_FEATURE_LEVEL_11_1: return "11_1";
    case D3D_FEATURE_LEVEL_11_0: return "11_0";
    case D3D_FEATURE_LEVEL_10_1: return "10_1";
    case D3D_FEATURE_LEVEL_10_0: return "10_0";
    case D3D_FEATURE_LEVEL_9_3: return "9_3";
    case D3D_FEATURE_LEVEL_9_2: return "9_2";
    case D3D_FEATURE_LEVEL_9_1: return "9_1";
    default: return "unknown";
    }
}

static const char* vendor_name(UINT v) {
    switch (v) {
    case 0x10DE: return "NVIDIA";
    case 0x1002: case 0x1022: return "AMD";
    case 0x8086: return "Intel";
    case 0x1414: return "Microsoft";
    case 0x5143: return "Qualcomm";
    default: return "other";
    }
}

static std::string hex(unsigned long long v, int w) {
    char b[32];
    sprintf_s(b, "0x%0*llX", w, v);
    return b;
}

static std::string luid_str(const LUID& l) {
    char b[40];
    sprintf_s(b, "%08lX:%08lX", (unsigned long)l.HighPart, (unsigned long)l.LowPart);
    return b;
}

struct Caps {
    bool created = false;
    HRESULT hr = S_OK;
    D3D_FEATURE_LEVEL fl = (D3D_FEATURE_LEVEL)0;
    bool doubles = false;
    bool ext_doubles = false;
    bool have_luid = false;
    LUID luid{};
    std::string adapter_name;
};

// One short-lived device. adapter == nullptr reproduces fullgpu.exe's default-adapter path (driver type HARDWARE).
static Caps probe_device(IDXGIAdapter* adapter) {
    Caps c;
    const D3D_FEATURE_LEVEL want[] = {D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0, D3D_FEATURE_LEVEL_11_1,
                                      D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
    const D3D_DRIVER_TYPE dt = adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE;
    ID3D11Device* dev = nullptr;
    D3D_FEATURE_LEVEL got = (D3D_FEATURE_LEVEL)0;
    HRESULT hr = D3D11CreateDevice(adapter, dt, nullptr, 0, want, (UINT)(sizeof(want) / sizeof(want[0])),
                                   D3D11_SDK_VERSION, &dev, &got, nullptr);
    if (hr == E_INVALIDARG) {   // a pre-Win10 runtime rejects the 12_x levels in the list
        hr = D3D11CreateDevice(adapter, dt, nullptr, 0, want + 2, (UINT)(sizeof(want) / sizeof(want[0])) - 2,
                               D3D11_SDK_VERSION, &dev, &got, nullptr);
    }
    c.hr = hr;
    if (FAILED(hr) || !dev) return c;
    c.created = true;
    c.fl = got;
    D3D11_FEATURE_DATA_DOUBLES d{};
    if (SUCCEEDED(dev->CheckFeatureSupport(D3D11_FEATURE_DOUBLES, &d, sizeof(d))))
        c.doubles = d.DoublePrecisionFloatShaderOps != FALSE;
    D3D11_FEATURE_DATA_D3D11_OPTIONS o{};
    if (SUCCEEDED(dev->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS, &o, sizeof(o))))
        c.ext_doubles = o.ExtendedDoublesShaderInstructions != FALSE;
    IDXGIDevice* xd = nullptr;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&xd)) && xd) {
        IDXGIAdapter* a = nullptr;
        if (SUCCEEDED(xd->GetAdapter(&a)) && a) {
            DXGI_ADAPTER_DESC ad{};
            if (SUCCEEDED(a->GetDesc(&ad))) {
                c.have_luid = true;
                c.luid = ad.AdapterLuid;
                c.adapter_name = utf8(ad.Description);
            }
            a->Release();
        }
        xd->Release();
    }
    dev->Release();
    return c;
}

static void write_caps(Json& j, const Caps& c) {
    j.mbool("device_created", c.created);
    j.mstr("hresult", hex((unsigned long)c.hr, 8));
    if (c.created) {
        j.mstr("feature_level", fl_name(c.fl));
        j.mstr("feature_level_hex", hex((unsigned)c.fl, 4));
        j.mbool("double_precision_float_shader_ops", c.doubles);
        j.mbool("extended_doubles_shader_instructions", c.ext_doubles);
    }
}


// ---------------------------------------------------------------- X2: live video memory (read only)
// IDXGIAdapter3::QueryVideoMemoryInfo(LOCAL): "budget" is what the OS lets THIS process use right now (it shrinks when other programs
// hold video memory), "current_usage" is this process's own use (about 0 here). The Windows GPU Adapter Memory performance counter
// ("Dedicated Usage", the figure Task Manager shows) is the SYSTEM-wide use by every process; it is reported separately and is null
// when the counter is unavailable. Both are snapshots; nothing is written.
struct LiveMem {
    bool queried = false;
    unsigned long long budget = 0, usage = 0, avail_reserve = 0, cur_reserve = 0;
    bool have_sys = false;
    unsigned long long sys_dedicated_usage = 0;
};

static bool pdh_dedicated_usage(const LUID& l, unsigned long long& out) {
    wchar_t path[160];
    swprintf_s(path, L"\\GPU Adapter Memory(luid_0x%08X_0x%08X_phys_0)\\Dedicated Usage", (unsigned)l.HighPart, (unsigned)l.LowPart);
    PDH_HQUERY q = nullptr;
    if (PdhOpenQueryW(nullptr, 0, &q) != ERROR_SUCCESS) return false;
    bool ok = false;
    PDH_HCOUNTER c = nullptr;
    if (PdhAddEnglishCounterW(q, path, 0, &c) == ERROR_SUCCESS && PdhCollectQueryData(q) == ERROR_SUCCESS) {
        PDH_FMT_COUNTERVALUE v{};
        if (PdhGetFormattedCounterValue(c, PDH_FMT_LARGE, nullptr, &v) == ERROR_SUCCESS &&
            (v.CStatus == PDH_CSTATUS_VALID_DATA || v.CStatus == PDH_CSTATUS_NEW_DATA) && v.largeValue >= 0) {
            out = (unsigned long long)v.largeValue;
            ok = true;
        }
    }
    PdhCloseQuery(q);
    return ok;
}

static LiveMem live_memory(IDXGIAdapter1* a, const LUID& luid) {
    LiveMem m;
    IDXGIAdapter3* a3 = nullptr;
    if (SUCCEEDED(a->QueryInterface(__uuidof(IDXGIAdapter3), (void**)&a3)) && a3) {
        DXGI_QUERY_VIDEO_MEMORY_INFO info{};
        if (SUCCEEDED(a3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) {
            m.queried = true;
            m.budget = info.Budget;
            m.usage = info.CurrentUsage;
            m.avail_reserve = info.AvailableForReservation;
            m.cur_reserve = info.CurrentReservation;
        }
        a3->Release();
    }
    m.have_sys = pdh_dedicated_usage(luid, m.sys_dedicated_usage);
    return m;
}

static void write_live(Json& j, const LiveMem& m) {
    j.mobj("video_memory");
    j.mbool("queried", m.queried);
    if (m.queried) {
        j.mnum("budget", m.budget);
        j.mnum("current_usage", m.usage);
        j.mnum("available_for_reservation", m.avail_reserve);
        j.mnum("current_reservation", m.cur_reserve);
    }
    if (m.have_sys) j.mnum("system_dedicated_usage", m.sys_dedicated_usage); else j.mnull("system_dedicated_usage");
    j.close('}');
}

struct RegDword {
    bool present = false;
    DWORD v = 0;
};

static RegDword read_dword(const wchar_t* sub, const wchar_t* name) {
    RegDword r;
    DWORD v = 0, sz = sizeof(v);
    // RRF_SUBKEY_WOW6464KEY: always the 64-bit view; read only (RegGetValueW opens KEY_QUERY_VALUE)
    LSTATUS s = RegGetValueW(HKEY_LOCAL_MACHINE, sub, name, RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &v, &sz);
    if (s == ERROR_SUCCESS) { r.present = true; r.v = v; }
    return r;
}

int main(int argc, char** argv) {
    const char* outPath = nullptr;
    bool perAdapter = true, pretty = false;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--out") && i + 1 < argc) outPath = argv[++i];
        else if (!strcmp(argv[i], "--no-per-adapter")) perAdapter = false;
        else if (!strcmp(argv[i], "--pretty")) pretty = true;
        else if (!strcmp(argv[i], "--version")) { printf("gpucheck %s\n", kVersion); return 0; }
        else {
            fprintf(stderr, "usage: gpucheck.exe [--out FILE] [--no-per-adapter] [--pretty] [--version]\n");
            return 2;
        }
    }

    Json j;
    j.pretty = pretty;
    j.open('{');
    j.mstr("tool", "gpucheck");
    j.mstr("version", kVersion);
    j.mint("schema", 1);
    j.mbool("optimus_exports", kOptimusExports);
    {
        OSVERSIONINFOEXW ov{};
        ov.dwOSVersionInfoSize = sizeof(ov);
        typedef LONG(WINAPI * RtlGetVersionFn)(OSVERSIONINFOEXW*);
        HMODULE nt = GetModuleHandleW(L"ntdll.dll");
        RtlGetVersionFn f = nt ? (RtlGetVersionFn)GetProcAddress(nt, "RtlGetVersion") : nullptr;
        if (f && f(&ov) == 0) {
            char b[64];
            sprintf_s(b, "%lu.%lu.%lu", ov.dwMajorVersion, ov.dwMinorVersion, ov.dwBuildNumber);
            j.mstr("os_version", b);
        } else j.mnull("os_version");
    }

    std::vector<std::string> errors;
    IDXGIFactory1* fac = nullptr;
    HRESULT hf = CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    int nHardware = 0;
    std::string hiPerfLuid, loPowerLuid;
    j.marr("adapters");
    if (FAILED(hf) || !fac) {
        errors.push_back("CreateDXGIFactory1 failed " + hex((unsigned long)hf, 8));
    } else {
        for (UINT i = 0;; ++i) {
            IDXGIAdapter1* a = nullptr;
            if (fac->EnumAdapters1(i, &a) == DXGI_ERROR_NOT_FOUND) break;
            if (!a) break;
            DXGI_ADAPTER_DESC1 d{};
            a->GetDesc1(&d);
            const bool sw = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
            if (!sw) ++nHardware;
            j.aobj();
            j.mnum("index", i);
            j.mstr("name", utf8(d.Description));
            j.mstr("vendor_id", hex(d.VendorId, 4));
            j.mstr("vendor", vendor_name(d.VendorId));
            j.mstr("device_id", hex(d.DeviceId, 4));
            j.mstr("subsys_id", hex(d.SubSysId, 8));
            j.mnum("revision", d.Revision);
            j.mnum("dedicated_video_memory", (unsigned long long)d.DedicatedVideoMemory);
            j.mnum("dedicated_system_memory", (unsigned long long)d.DedicatedSystemMemory);
            j.mnum("shared_system_memory", (unsigned long long)d.SharedSystemMemory);
            j.mstr("luid", luid_str(d.AdapterLuid));
            j.mbool("software", sw);
            if (!sw) write_live(j, live_memory(a, d.AdapterLuid));   // X2: budget / usage now (additive; schema stays 1)
            if (perAdapter) {
                Caps c = probe_device(a);
                j.mobj("d3d11");
                write_caps(j, c);
                j.close('}');
            }
            j.close('}');
            a->Release();
        }
        // The OS's own GPU-preference order (Win10 1803+): which adapter a "high performance" / "minimum power" request gets.
        IDXGIFactory6* f6 = nullptr;
        if (SUCCEEDED(fac->QueryInterface(__uuidof(IDXGIFactory6), (void**)&f6)) && f6) {
            IDXGIAdapter1* a = nullptr;
            if (SUCCEEDED(f6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, __uuidof(IDXGIAdapter1),
                                                         (void**)&a)) && a) {
                DXGI_ADAPTER_DESC1 d{};
                a->GetDesc1(&d);
                hiPerfLuid = luid_str(d.AdapterLuid);
                a->Release();
            }
            a = nullptr;
            if (SUCCEEDED(f6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_MINIMUM_POWER, __uuidof(IDXGIAdapter1),
                                                         (void**)&a)) && a) {
                DXGI_ADAPTER_DESC1 d{};
                a->GetDesc1(&d);
                loPowerLuid = luid_str(d.AdapterLuid);
                a->Release();
            }
            f6->Release();
        }
        fac->Release();
    }
    j.close(']');
    j.mnum("hardware_adapter_count", (unsigned long long)nHardware);
    j.mbool("hybrid", nHardware > 1);
    if (!hiPerfLuid.empty()) j.mstr("high_performance_luid", hiPerfLuid); else j.mnull("high_performance_luid");
    if (!loPowerLuid.empty()) j.mstr("minimum_power_luid", loPowerLuid); else j.mnull("minimum_power_luid");

    // ---- the default adapter, exactly as fullgpu.exe creates its device
    Caps dc = probe_device(nullptr);
    j.mobj("default_adapter");
    write_caps(j, dc);
    if (dc.have_luid) {
        j.mstr("luid", luid_str(dc.luid));
        j.mstr("name", dc.adapter_name);
        j.mbool("is_high_performance", !hiPerfLuid.empty() && hiPerfLuid == luid_str(dc.luid));
    }
    j.close('}');

    // ---- TDR (read only). Absent TdrDelay means the OS default of 2 s.
    const wchar_t* gd = L"SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
    RegDword tdrDelay = read_dword(gd, L"TdrDelay"), tdrDdi = read_dword(gd, L"TdrDdiDelay"),
             tdrLevel = read_dword(gd, L"TdrLevel"), tdrLimitCount = read_dword(gd, L"TdrLimitCount"),
             tdrLimitTime = read_dword(gd, L"TdrLimitTime");
    j.mobj("tdr");
    if (tdrDelay.present) j.mnum("TdrDelay", tdrDelay.v); else j.mnull("TdrDelay");
    if (tdrDdi.present) j.mnum("TdrDdiDelay", tdrDdi.v); else j.mnull("TdrDdiDelay");
    if (tdrLevel.present) j.mnum("TdrLevel", tdrLevel.v); else j.mnull("TdrLevel");
    if (tdrLimitCount.present) j.mnum("TdrLimitCount", tdrLimitCount.v); else j.mnull("TdrLimitCount");
    if (tdrLimitTime.present) j.mnum("TdrLimitTime", tdrLimitTime.v); else j.mnull("TdrLimitTime");
    j.mnum("effective_delay_s", tdrDelay.present ? tdrDelay.v : 2u);
    j.mbool("detection_off", tdrLevel.present && tdrLevel.v == 0);
    j.close('}');

    // ---- verdict (design section 8: FL 11_0 + FP64 + extended doubles on the default adapter)
    std::vector<std::string> reasons, advice;
    bool defaultIsSoftware = false;
    if (!dc.created) reasons.push_back("no hardware Direct3D 11 device could be created on the default adapter (" +
                                       hex((unsigned long)dc.hr, 8) + ")");
    else {
        if (dc.adapter_name.find("Microsoft Basic Render") != std::string::npos) {
            defaultIsSoftware = true;
            reasons.push_back("the default adapter is the Microsoft Basic Render Driver (no GPU driver installed?)");
        }
        if (dc.fl < D3D_FEATURE_LEVEL_11_0) reasons.push_back(std::string("feature level ") + fl_name(dc.fl) + " is below 11_0");
        if (!dc.doubles) reasons.push_back("the GPU has no double-precision (FP64) shader support");
        if (!dc.ext_doubles) reasons.push_back("the GPU lacks extended double-precision shader instructions (ddiv/dfma/drcp)");
    }
    const unsigned eff = tdrDelay.present ? tdrDelay.v : 2u;
    // production bakes are validated at the Windows default (TdrDelay absent = 2 s, the dev PC); only a LOWER value is advice
    if (!(tdrLevel.present && tdrLevel.v == 0) && eff < 2u)
        advice.push_back("TdrDelay is " + std::to_string(eff) + " s, below the Windows default of 2 s: long GPU dispatches can be"
                         " reset by Windows. Changing TdrDelay is a system setting this app never changes.");
    if (nHardware > 1)
        advice.push_back("more than one hardware GPU: the bake runs on the default adapter shown above");
    j.mobj("verdict");
    j.mbool("ok", reasons.empty());
    j.marr("reasons");
    for (auto& r : reasons) j.astr(r.c_str());
    j.close(']');
    j.marr("advice");
    for (auto& r : advice) j.astr(r.c_str());
    j.close(']');
    j.close('}');
    j.marr("errors");
    for (auto& r : errors) j.astr(r.c_str());
    j.close(']');
    j.close('}');
    j.s += '\n';
    (void)defaultIsSoftware;

    fwrite(j.s.data(), 1, j.s.size(), stdout);
    fflush(stdout);
    if (outPath) {
        FILE* f = nullptr;
        if (fopen_s(&f, outPath, "wb") == 0 && f) {
            fwrite(j.s.data(), 1, j.s.size(), f);
            fclose(f);
        } else {
            fprintf(stderr, "gpucheck: cannot write %s\n", outPath);
            return 2;
        }
    }
    if (!errors.empty() && !dc.created) return 2;
    return reasons.empty() ? 0 : 1;
}
