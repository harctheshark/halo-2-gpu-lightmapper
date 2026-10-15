/* osoy_launch.c -- v370: launch STOCK tool_fast.exe SUSPENDED, load the user's Osoyoos H2ToolHooks.dll into it BEFORE its main
   thread runs one instruction, PROVE the requested hooks are in place, then resume it.

   usage:
     osoy_launch.exe [--plucker] [--scenery] --dll <H2ToolHooks.dll> --exe <tool_fast.exe> --argsfile <file>
                     [--cwd <dir>] [--stdout <file> --stderr <file>] [--pidfile <file>] [--timeout-ms <n>]
                     [--test-seed-stock]

   --plucker   OSOYOOS_INJECTOR_LIGHTMAP_PRECISION_FIX=1 + _PRECISION_FIX_FAST=1 (what the Osoyoos launcher passes: FAST is
               always on when the precision fix is on): FP64 build_surface_edge_plucker 0x49C09C + FP64 per-ray moment 0x4B2F27.
   --scenery   OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX=1: scenery gate 0x49CF55, vbuf decompress 0x49DA6A, emissive 0x499603.
   --argsfile  ONE line = the child's arguments (the exe path is prepended quoted). A file, so no shell re-quotes it.
   --stdout/--stderr  redirect the child's std handles to these files (the skeleton run does; capture/write-back do not).
   --hold      do everything but resume: print PID=<n> and leave the child SUSPENDED, so the caller can open its own handle first
               (no exit race), then `osoy_launch.exe --resume <pid>` (NtResumeProcess). gpu_bake.ps1 uses this pair.
   --wait      (self-test) after resuming, wait for the child and print its exit code.
   --test-seed-stock  SELF-TEST ONLY: writes the stock tool_fast prologue bytes of the five sites into a dummy target (a fake
               image at 0x400000) before injecting, so the real DLL's install path can be exercised without running the tool.

   P4 (H2 GPU Lightmapper standalone, 2026-10-14; both switches are OFF by default, and with both off every launch is the v370 one):
   --nowindow  create the child with CREATE_NO_WINDOW instead of CREATE_NEW_CONSOLE: it gets its own console, with no window.
               NEVER DETACHED_PROCESS: H2ToolHooks' DllMain calls AllocConsole(), which would then open a visible console; under
               CREATE_NO_WINDOW the process already owns a (windowless) console, so that AllocConsole is a no-op.
   --quality-row <P> <MC>   Custom quality without any shared conf file. While the child is still SUSPENDED and after the
               H2ToolHooks proof (when a fix is requested): find tool_fast's hidden 'cuban' quality row by its STOCK signature
               {"cuban",1,1,0,50000,0,1.0f,0} (exactly one 4-aligned match in the image's initialized non-code sections;
               0xA30970 in tool_fast 3a889d37), VirtualProtectEx it writable, write {4, MC, 0, P, 0, 4.0f, 0} KEEPING the name
               pointer, restore the protection and read the 32 bytes back. Not found / write / restore / read-back mismatch ->
               the child is TERMINATED (it never ran) and the exit code is 4. P = 1..100000000 photons, MC = 1..64 (plain decimal),
               else exit 2. The tool's `cuban` quality is then super's row with P and MC substituted (subpixel 4, gather quality
               4.0), so `--quality-row 20000000 8` + quality `cuban` IS super. Without --plucker/--scenery no DLL is injected
               and the row is written before the loader runs (row-only mode; --dll is then optional).
               Prints `OSOYROW QUALITY cuban photons=<P> mc=<MC> ss=4 gq=4.0 READBACK OK ...` on success.
   --test-terminate  SELF-TEST ONLY: do everything (inject + prove, row write + read-back) but NEVER resume: dump the quality
               table (OSOYTABLE lines) and TERMINATE the still-suspended child -- not one tool instruction runs. Exit 0.

   Mechanism (mirrors the Osoyoos launcher's DLLInjector early mode, EarlyProcessInject): CreateProcess(CREATE_SUSPENDED |
   CREATE_NEW_CONSOLE) -> a remote thread runs ntdll!LdrLoadDll(full path) -- that thread performs the process's loader
   initialisation first, then loads the DLL, whose DllMain installs the hooks -- -> the helper waits for that thread, checks the
   DLL's OSOYOOS_INJECTOR_EVENT (set only when H2ToolHooks::hook() returned true), reads every patch site back out of the child
   and verifies it (E9 jump into the DLL image / a private RWX stub, nop padding), checks the sites it did NOT ask for are still
   stock, and only THEN resumes the main thread. Any failure terminates the child (exit code 0xDEAD) and returns nonzero, so an
   un-hooked tool can never run a bake that claims the fix.

   The child's environment = this process's environment with every OSOYOOS_* variable removed, plus exactly the requested
   flags + the event name. GPULM_* etc. pass through unchanged (same as Start-Process).

   Exit codes: 0 ok (child running, PID printed + written to --pidfile), 2 usage, 3 launch failed, 4 child image not stock at a
   site, 5 injection failed, 6 hook() did not report success (event), 7 a requested hook is not installed / an unrequested one
   is, 8 resume failed. P4: 4 also = --quality-row could not find / write / read back the stock 'cuban' row (child terminated).
   Build: build_osoy_launch.bat (vcvarsall x86, cl /O2 /MT). P4 build: H2 GPU Lightmapper\native\osoy_launch\build_osoy_launch_p4.bat
   (same flags, output into H2 GPU Lightmapper\build\out\ only -- never over the production osoyoos\osoy_launch.exe). */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

typedef struct { DWORD va; int len; unsigned char stock[8]; int group; const char* name; } Site;   /* group 1 plucker, 2 scenery */
static const Site SITES[] = {
    { 0x49C09C, 6, { 0x83,0xEC,0x30,0x53,0x8B,0xD9 },           1, "build_surface_edge_plucker (FP64 edge coefficients)" },
    { 0x4B2F27, 6, { 0xF3,0x0F,0x11,0x44,0x24,0x60 },           1, "traverse 0x4B2DD4 per-ray Plucker moment (FP64)" },
    { 0x49CF55, 8, { 0x55,0x8D,0x6C,0x24,0xA0,0x83,0xEC,0x60 }, 2, "scenery_occluder_insert gate" },
    { 0x49DA6A, 8, { 0x83,0xEC,0x3C,0xA1,0xFC,0x7B,0xBC,0x00 }, 2, "add_occluder_vertex (scenery vbuf decompress)" },
    { 0x499603, 7, { 0x6A,0x60,0xB8,0xB2,0x25,0x9A,0x00 },      2, "surface-light collect (scenery emissive decompress)" },
};
#define NSITES (int)(sizeof(SITES) / sizeof(SITES[0]))

static HANDLE g_proc = NULL, g_thread = NULL;
static void die(int code, const char* fmt, ...)
{
    va_list ap; va_start(ap, fmt);
    fprintf(stderr, "OSOYLAUNCH *** ");
    vfprintf(stderr, fmt, ap); fprintf(stderr, "\n"); va_end(ap);
    if (g_proc) { TerminateProcess(g_proc, 0xDEAD); WaitForSingleObject(g_proc, 10000);
                  fprintf(stderr, "OSOYLAUNCH *** the child was TERMINATED before it ran (it never executed tool code)\n"); }
    exit(code);
}

static void strip_osoyoos_env(void)
{
    /* collect the names first (deleting while walking the block would invalidate it) */
    char* blk = GetEnvironmentStringsA(); char names[64][256]; int n = 0;
    for (char* p = blk; p && *p; p += strlen(p) + 1) {
        if (_strnicmp(p, "OSOYOOS_", 8) == 0 && n < 64) {
            const char* eq = strchr(p, '=');
            size_t l = eq ? (size_t)(eq - p) : strlen(p); if (l > 255) l = 255;
            memcpy(names[n], p, l); names[n][l] = 0; n++; }
    }
    if (blk) FreeEnvironmentStringsA(blk);
    for (int i = 0; i < n; i++) { SetEnvironmentVariableA(names[i], NULL); printf("OSOYLAUNCH env: removed inherited %s\n", names[i]); }
}

static int rpm(DWORD va, void* out, SIZE_T n)
{
    SIZE_T got = 0;
    return ReadProcessMemory(g_proc, (LPCVOID)(ULONG_PTR)va, out, n, &got) && got == n;
}

static char* read_args_file(const char* path)
{
    FILE* f = NULL; if (fopen_s(&f, path, "rb") || !f) return NULL;
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    char* b = (char*)calloc(1, (size_t)n + 1); fread(b, 1, (size_t)n, f); fclose(f);
    while (n > 0 && (b[n - 1] == '\r' || b[n - 1] == '\n' || b[n - 1] == ' ')) b[--n] = 0;
    char* s = b; if ((unsigned char)s[0] == 0xEF && (unsigned char)s[1] == 0xBB && (unsigned char)s[2] == 0xBF) s += 3;   /* UTF-8 BOM */
    return s;
}

static HANDLE open_out(const char* path)
{
    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE };
    HANDLE h = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) die(3, "cannot create %s (err %lu)", path, GetLastError());
    return h;
}

/* ===== P4 --quality-row: tool_fast's quality preset row (lm_quality_setup 0x4AE9A8 parses the table @0xA30950, stride 0x20) =====
   +0 name ptr | +4 subpixel (supersamples per axis) | +8 secondary monte-carlo | +0xC is_draft | +0x10 photon budget |
   +0x14 unknown/farm-hidden | +0x18 gather quality (f32; kNN radius x sqrt(1/gq)) | +0x1C is_checkerboard  (= Osoyoos lightmap_settings) */
#define TOOL_IMAGE_BASE 0x400000u
static void row_fields(const unsigned char* r, LONG f[8]) { memcpy(f, r, 32); }
static void row_fmt(const unsigned char* r, char* out, size_t cap)
{
    LONG f[8]; float gq; row_fields(r, f); memcpy(&gq, &f[6], 4);
    char nm[24] = "?"; SIZE_T got = 0;
    if (ReadProcessMemory(g_proc, (LPCVOID)(ULONG_PTR)(DWORD)f[0], nm, sizeof(nm) - 1, &got)) nm[sizeof(nm) - 1] = 0; else strcpy_s(nm, sizeof(nm), "?");
    for (char* p = nm; *p; p++) if ((unsigned char)*p < 0x20 || (unsigned char)*p > 0x7E) { *p = 0; break; }
    char hex[65]; for (int i = 0; i < 32; i++) sprintf_s(hex + 2 * i, 3, "%02x", r[i]);
    sprintf_s(out, cap, "name=%s ss=%ld mc=%ld is_draft=%ld photons=%ld unknown=%ld gather_quality=%.6f checkerboard=%ld bytes=%s",
              nm, f[1], f[2], f[3], f[4], f[5], (double)gq, f[7], hex);
}
static DWORD find_cuban_row(void)
{
    /* the stock signature, exactly what Osoyoos' PATCH_QUALITY scans for: {"cuban", 1, 1, 0, 50000, 0, 1.0f, 0} */
    LONG tail[7] = { 1, 1, 0, 50000, 0, 0, 0 }; float one = 1.0f; memcpy(&tail[5], &one, 4);
    IMAGE_DOS_HEADER dh; IMAGE_NT_HEADERS32 nh;
    if (!rpm(TOOL_IMAGE_BASE, &dh, sizeof(dh)) || dh.e_magic != IMAGE_DOS_SIGNATURE ||
        !rpm(TOOL_IMAGE_BASE + dh.e_lfanew, &nh, sizeof(nh)) || nh.Signature != IMAGE_NT_SIGNATURE)
        die(4, "--quality-row: no PE image at 0x%08X in the child", TOOL_IMAGE_BASE);
    DWORD secTab = TOOL_IMAGE_BASE + dh.e_lfanew + FIELD_OFFSET(IMAGE_NT_HEADERS32, OptionalHeader) + nh.FileHeader.SizeOfOptionalHeader;
    int nfound = 0; DWORD found = 0;
    for (int s = 0; s < nh.FileHeader.NumberOfSections && s < 96; s++) {
        IMAGE_SECTION_HEADER sh;
        if (!rpm(secTab + s * (DWORD)sizeof(sh), &sh, sizeof(sh))) die(4, "--quality-row: cannot read section header %d", s);
        if ((sh.Characteristics & IMAGE_SCN_MEM_EXECUTE) || !(sh.Characteristics & IMAGE_SCN_CNT_INITIALIZED_DATA)) continue;
        DWORD n = sh.Misc.VirtualSize < sh.SizeOfRawData ? sh.Misc.VirtualSize : sh.SizeOfRawData;   /* file-backed bytes only */
        if (n < 32) continue;
        if (n > 0x4000000) die(4, "--quality-row: section %d is %lu bytes -- not tool_fast", s, n);
        unsigned char* buf = (unsigned char*)malloc(n);
        if (!buf || !rpm(TOOL_IMAGE_BASE + sh.VirtualAddress, buf, n)) die(4, "--quality-row: cannot read section %d (%lu bytes)", s, n);
        for (DWORD o = 0; o + 32 <= n; o += 4) {
            if (memcmp(buf + o + 4, tail, 28)) continue;
            char nm[6]; DWORD np; memcpy(&np, buf + o, 4);
            if (rpm(np, nm, 6) && !memcmp(nm, "cuban", 6)) { nfound++; found = TOOL_IMAGE_BASE + sh.VirtualAddress + o; }
        }
        free(buf);
    }
    if (nfound != 1)
        die(4, "--quality-row: the STOCK 'cuban' quality row {\"cuban\",1,1,0,50000,0,1.0f,0} was found %d time(s), want exactly 1 "
               "-- not the stock tool_fast, or the row was already rewritten (Osoyoos PATCH_QUALITY?)", nfound);
    return found;
}
static void write_quality_row(DWORD va, DWORD photons, DWORD mc)
{
    unsigned char before[32], want[32], back[32]; char line[400];
    if (!rpm(va, before, 32)) die(4, "--quality-row: cannot read the cuban row at 0x%08lX", va);
    row_fmt(before, line, sizeof(line)); printf("OSOYROW stock   va=0x%08lX %s\n", va, line);
    LONG f[8]; float gq = 4.0f;
    memcpy(&f[0], before, 4);                                          /* KEEP the name pointer: the tool still parses "cuban" */
    f[1] = 4; f[2] = (LONG)mc; f[3] = 0; f[4] = (LONG)photons; f[5] = 0; memcpy(&f[6], &gq, 4); f[7] = 0;
    memcpy(want, f, 32);
    MEMORY_BASIC_INFORMATION mi; DWORD protBefore = 0, protAfter = 0, old = 0, tmp = 0;
    if (VirtualQueryEx(g_proc, (LPCVOID)(ULONG_PTR)va, &mi, sizeof(mi))) protBefore = mi.Protect;
    if (!VirtualProtectEx(g_proc, (LPVOID)(ULONG_PTR)va, 32, PAGE_READWRITE, &old))
        die(4, "--quality-row: VirtualProtectEx(0x%08lX, RW) failed (%lu)", va, GetLastError());
    SIZE_T w = 0; BOOL okw = WriteProcessMemory(g_proc, (LPVOID)(ULONG_PTR)va, want, 32, &w); DWORD werr = GetLastError();
    BOOL okp = VirtualProtectEx(g_proc, (LPVOID)(ULONG_PTR)va, 32, old, &tmp); DWORD perr = GetLastError();
    if (!okw || w != 32) die(4, "--quality-row: WriteProcessMemory(0x%08lX) failed (%lu, %lu bytes)", va, werr, (unsigned long)w);
    if (!okp) die(4, "--quality-row: could not restore protection 0x%02lX at 0x%08lX (%lu)", old, va, perr);
    if (VirtualQueryEx(g_proc, (LPCVOID)(ULONG_PTR)va, &mi, sizeof(mi))) protAfter = mi.Protect;
    if (!rpm(va, back, 32)) die(4, "--quality-row: cannot read the cuban row back at 0x%08lX", va);
    row_fmt(back, line, sizeof(line)); printf("OSOYROW written va=0x%08lX %s\n", va, line);
    if (memcmp(back, want, 32)) die(4, "--quality-row: READ-BACK MISMATCH at 0x%08lX -- the row is not {4, %lu, 0, %lu, 0, 4.0f, 0}", va, mc, photons);
    printf("OSOYROW QUALITY cuban photons=%lu mc=%lu ss=4 gq=4.0 READBACK OK (va 0x%08lX, protection 0x%02lX -> RW -> 0x%02lX restored, now 0x%02lX)\n",
           photons, mc, va, protBefore, old, protAfter);
}
static int parse_dec(const char* s, unsigned long lo, unsigned long hi, DWORD* out)
{
    if (!s || !*s || strlen(s) > 9) return 0;
    for (const char* p = s; *p; p++) if (*p < '0' || *p > '9') return 0;
    unsigned long v = strtoul(s, NULL, 10);
    if (v < lo || v > hi) return 0;
    *out = (DWORD)v; return 1;
}

int main(int argc, char** argv)
{
    int plucker = 0, scenery = 0, seed = 0, waitChild = 0, hold = 0; DWORD timeout = 60000, resumePid = 0;
    const char *dll = NULL, *exe = NULL, *argsf = NULL, *cwd = NULL, *outf = NULL, *errf = NULL, *pidf = NULL;
    int nowindow = 0, qrow = 0, testTerm = 0; DWORD qPhotons = 0, qMc = 0;   /* P4 */
    for (int i = 1; i < argc; i++) {
        const char* a = argv[i];
#define NEXT() ((i + 1 < argc) ? argv[++i] : (die(2, "%s needs a value", a), (char*)0))
        if (!strcmp(a, "--plucker")) plucker = 1;
        else if (!strcmp(a, "--scenery")) scenery = 1;
        else if (!strcmp(a, "--nowindow")) nowindow = 1;
        else if (!strcmp(a, "--test-terminate")) testTerm = 1;
        else if (!strcmp(a, "--quality-row")) {
            const char* sp = NEXT(); const char* sm = NEXT();
            if (!parse_dec(sp, 1, 100000000, &qPhotons)) die(2, "--quality-row: photons '%s' is not a plain decimal 1..100000000", sp);
            if (!parse_dec(sm, 1, 64, &qMc)) die(2, "--quality-row: MC samples '%s' is not a plain decimal 1..64", sm);
            qrow = 1; }
        else if (!strcmp(a, "--test-seed-stock")) seed = 1;
        else if (!strcmp(a, "--wait")) waitChild = 1;
        else if (!strcmp(a, "--hold")) hold = 1;
        else if (!strcmp(a, "--resume")) resumePid = strtoul(NEXT(), NULL, 10);
        else if (!strcmp(a, "--dll")) dll = NEXT();
        else if (!strcmp(a, "--exe")) exe = NEXT();
        else if (!strcmp(a, "--argsfile")) argsf = NEXT();
        else if (!strcmp(a, "--cwd")) cwd = NEXT();
        else if (!strcmp(a, "--stdout")) outf = NEXT();
        else if (!strcmp(a, "--stderr")) errf = NEXT();
        else if (!strcmp(a, "--pidfile")) pidf = NEXT();
        else if (!strcmp(a, "--timeout-ms")) timeout = (DWORD)strtoul(NEXT(), NULL, 10);
        else die(2, "unknown argument %s", a);
    }
    if (resumePid) {   /* second half of --hold: resume a child this helper left suspended */
        typedef LONG (NTAPI *NtResumeProcess_t)(HANDLE);
        NtResumeProcess_t nrp = (NtResumeProcess_t)GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtResumeProcess");
        HANDLE h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, resumePid);
        if (!h || !nrp) { fprintf(stderr, "OSOYLAUNCH *** --resume: cannot open pid %lu (%lu)\n", resumePid, GetLastError()); return 8; }
        char img[MAX_PATH] = "?"; DWORD il = MAX_PATH; QueryFullProcessImageNameA(h, 0, img, &il);
        LONG st = nrp(h); CloseHandle(h);
        if (st != 0) { fprintf(stderr, "OSOYLAUNCH *** --resume: NtResumeProcess(%lu) = 0x%08lX\n", resumePid, (unsigned long)st); return 8; }
        printf("OSOYLAUNCH resumed pid %lu (%s)\n", resumePid, img);
        return 0;
    }
    int fixes = plucker || scenery;
    if ((fixes && !dll) || !exe || !argsf)
        die(2, "usage: osoy_launch [--plucker] [--scenery] --dll D --exe E --argsfile F [--cwd C] [--stdout O --stderr R] [--pidfile P] "
               "[--nowindow] [--quality-row PHOTONS MC] (--dll is required with --plucker/--scenery)");
    if (!fixes && !qrow) die(2, "no fix requested -- launch the tool the normal way (this helper exists only to inject H2ToolHooks.dll / write --quality-row)");
    if ((outf == NULL) != (errf == NULL)) die(2, "--stdout and --stderr go together");
    if (testTerm && (hold || waitChild)) die(2, "--test-terminate never resumes the child: it does not go with --hold / --wait");

    char dllFull[MAX_PATH] = "", exeFull[MAX_PATH];
    if (dll && (!GetFullPathNameA(dll, MAX_PATH, dllFull, NULL) || GetFileAttributesA(dllFull) == INVALID_FILE_ATTRIBUTES)) die(2, "no DLL %s", dll);
    if (!GetFullPathNameA(exe, MAX_PATH, exeFull, NULL) || GetFileAttributesA(exeFull) == INVALID_FILE_ATTRIBUTES) die(2, "no exe %s", exe);
    char* args = read_args_file(argsf); if (!args) die(2, "cannot read %s", argsf);

    /* ---- the child's environment ---- */
    strip_osoyoos_env();
    char evName[96]; sprintf_s(evName, sizeof(evName), "OSOYOOS_INJECT_v370_%lu_%lu", GetCurrentProcessId(), GetTickCount());
    HANDLE ev = CreateEventA(NULL, TRUE, FALSE, evName); if (!ev) die(3, "CreateEvent failed (%lu)", GetLastError());
    SetEnvironmentVariableA("OSOYOOS_INJECTOR_EVENT", evName);
    if (scenery) SetEnvironmentVariableA("OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX", "1");
    if (plucker) { SetEnvironmentVariableA("OSOYOOS_INJECTOR_LIGHTMAP_PRECISION_FIX", "1");
                   SetEnvironmentVariableA("OSOYOOS_INJECTOR_LIGHTMAP_PRECISION_FIX_FAST", "1"); }
    printf("OSOYLAUNCH env: plucker=%d (PRECISION_FIX + PRECISION_FIX_FAST) scenery=%d (LIGHTMAP_SCENERY_FIX) event=%s\n", plucker, scenery, evName);

    /* ---- launch suspended, in its own console (what Start-Process gives the tool today) ---- */
    size_t cl = strlen(exeFull) + strlen(args) + 8; char* cmd = (char*)calloc(1, cl);
    sprintf_s(cmd, cl, "\"%s\" %s", exeFull, args);
    /* 2026-10-05 FIX: with --stdout/--stderr the child used to be created with bInheritHandles=TRUE and NO handle list, so it
       inherited EVERY inheritable handle of this helper -- including the helper's own stdout/stderr PIPE from the caller
       (gpu_bake.ps1 reads `& osoy_launch ... 2>&1 | ForEach-Object`). With --hold the child stays SUSPENDED, the pipe never
       reaches EOF, the caller never gets to `--resume`: a deadlock (v370 A/B step P, skeleton run, 2026-10-05 04:16-05:11).
       Now an explicit PROC_THREAD_ATTRIBUTE_HANDLE_LIST passes exactly the two output files; stdin is not handed over. */
    STARTUPINFOEXA six; PROCESS_INFORMATION pi; memset(&six, 0, sizeof(six)); memset(&pi, 0, sizeof(pi));
    six.StartupInfo.cb = sizeof(six.StartupInfo);
    /* P4 --nowindow: CREATE_NO_WINDOW (own console, no window) replaces CREATE_NEW_CONSOLE -- never DETACHED_PROCESS (see the header) */
    BOOL inherit = FALSE; DWORD cflags = CREATE_SUSPENDED | (nowindow ? CREATE_NO_WINDOW : CREATE_NEW_CONSOLE);
    /* P4 self-test only: a private tool copy whose loader init fails must not raise a hard-error box (the child inherits the mode) */
    if (testTerm) SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    LPPROC_THREAD_ATTRIBUTE_LIST alist = NULL; HANDLE hlist[2];
    if (outf) {
        six.StartupInfo.dwFlags |= STARTF_USESTDHANDLES; six.StartupInfo.hStdInput = NULL;
        six.StartupInfo.hStdOutput = open_out(outf); six.StartupInfo.hStdError = open_out(errf);
        hlist[0] = six.StartupInfo.hStdOutput; hlist[1] = six.StartupInfo.hStdError;
        SIZE_T asz = 0; InitializeProcThreadAttributeList(NULL, 1, 0, &asz);
        alist = (LPPROC_THREAD_ATTRIBUTE_LIST)HeapAlloc(GetProcessHeap(), 0, asz);
        if (!alist || !InitializeProcThreadAttributeList(alist, 1, 0, &asz)) die(3, "InitializeProcThreadAttributeList failed (%lu)", GetLastError());
        if (!UpdateProcThreadAttribute(alist, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, hlist, sizeof(hlist), NULL, NULL))
            die(3, "UpdateProcThreadAttribute(HANDLE_LIST) failed (%lu)", GetLastError());
        six.StartupInfo.cb = sizeof(six); six.lpAttributeList = alist;
        cflags |= EXTENDED_STARTUPINFO_PRESENT; inherit = TRUE; }
    if (!CreateProcessA(exeFull, cmd, NULL, NULL, inherit, cflags, NULL, cwd, &six.StartupInfo, &pi))
        die(3, "CreateProcess(%s) failed (%lu)", exeFull, GetLastError());
    g_proc = pi.hProcess; g_thread = pi.hThread;
    if (outf) { CloseHandle(six.StartupInfo.hStdOutput); CloseHandle(six.StartupInfo.hStdError);
                DeleteProcThreadAttributeList(alist); HeapFree(GetProcessHeap(), 0, alist); }
    printf("OSOYLAUNCH child pid %lu created SUSPENDED: %s\n", pi.dwProcessId, cmd);
    if (nowindow) printf("OSOYLAUNCH --nowindow: CREATE_NO_WINDOW (the child owns a console with no window; never DETACHED_PROCESS)\n");

    /* ---- the image must be the stock tool at every site (also proves base 0x400000) ---- */
    for (int s = 0; s < NSITES; s++) {
        unsigned char b[8];
        if (seed) { DWORD o; VirtualProtectEx(g_proc, (LPVOID)(ULONG_PTR)SITES[s].va, SITES[s].len, PAGE_EXECUTE_READWRITE, &o);
                    SIZE_T w = 0; if (!WriteProcessMemory(g_proc, (LPVOID)(ULONG_PTR)SITES[s].va, SITES[s].stock, SITES[s].len, &w))
                        die(4, "--test-seed-stock: cannot write 0x%06lX (%lu)", SITES[s].va, GetLastError());
                    VirtualProtectEx(g_proc, (LPVOID)(ULONG_PTR)SITES[s].va, SITES[s].len, o, &o); }
        if (!rpm(SITES[s].va, b, SITES[s].len)) die(4, "cannot read 0x%06lX in the child (%lu) -- not a 0x400000 tool_fast image", SITES[s].va, GetLastError());
        if (memcmp(b, SITES[s].stock, SITES[s].len)) die(4, "0x%06lX is not the stock tool_fast prologue -- refusing to inject", SITES[s].va);
    }
    printf("OSOYLAUNCH pre-inject: all %d patch sites carry the STOCK tool_fast bytes%s\n", NSITES, seed ? " (SEEDED: self-test)" : "");
    if (!fixes) {   /* P4 row-only mode: nothing to inject; the row is written below, before the loader ever runs */
        printf("OSOYLAUNCH no Osoyoos fix requested: H2ToolHooks.dll is NOT injected (--quality-row only)\n");
        goto p4_quality_row; }

    /* ---- remote LdrLoadDll(NULL, NULL, &name, &handle) ---- */
    FARPROC ldr = GetProcAddress(GetModuleHandleA("ntdll.dll"), "LdrLoadDll");   /* same WOW64 ntdll base in every process */
    if (!ldr) die(5, "no ntdll!LdrLoadDll");
    WCHAR wpath[MAX_PATH]; int wl = MultiByteToWideChar(CP_ACP, 0, dllFull, -1, wpath, MAX_PATH);
    if (wl <= 1) die(5, "bad DLL path");
    DWORD nbytes = (DWORD)((wl - 1) * sizeof(WCHAR));
    /* block: +0 HMODULE out | +4 UNICODE_STRING {USHORT Len, USHORT Max, PWSTR Buf} | +12 shellcode (32 B) | +48 name */
    SIZE_T blkSize = 48 + nbytes + 2;
    unsigned char* loc = (unsigned char*)calloc(1, blkSize);
    BYTE* rem = (BYTE*)VirtualAllocEx(g_proc, NULL, blkSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!rem) die(5, "VirtualAllocEx failed (%lu)", GetLastError());
    DWORD remB = (DWORD)(ULONG_PTR)rem;
    *(USHORT*)(loc + 4) = (USHORT)nbytes; *(USHORT*)(loc + 6) = (USHORT)(nbytes + 2); *(DWORD*)(loc + 8) = remB + 48;
    memcpy(loc + 48, wpath, nbytes);
    unsigned char* sc = loc + 12; int k = 0;
    sc[k++] = 0x68; *(DWORD*)(sc + k) = remB + 0; k += 4;          /* push &handle            */
    sc[k++] = 0x68; *(DWORD*)(sc + k) = remB + 4; k += 4;          /* push &UNICODE_STRING    */
    sc[k++] = 0x6A; sc[k++] = 0x00;                                /* push 0  (DllCharacteristics = NULL) */
    sc[k++] = 0x6A; sc[k++] = 0x00;                                /* push 0  (search path = NULL)        */
    sc[k++] = 0xB8; *(DWORD*)(sc + k) = (DWORD)(ULONG_PTR)ldr; k += 4;   /* mov eax, LdrLoadDll  */
    sc[k++] = 0xFF; sc[k++] = 0xD0;                                /* call eax (stdcall, 4 args)          */
    sc[k++] = 0xC2; sc[k++] = 0x04; sc[k++] = 0x00;                /* ret 4 -> thread exit code = NTSTATUS */
    SIZE_T w = 0;
    if (!WriteProcessMemory(g_proc, rem, loc, blkSize, &w) || w != blkSize) die(5, "WriteProcessMemory failed (%lu)", GetLastError());
    FlushInstructionCache(g_proc, rem, blkSize);
    DWORD t0 = GetTickCount();
    HANDLE th = CreateRemoteThread(g_proc, NULL, 0, (LPTHREAD_START_ROUTINE)(rem + 12), NULL, 0, NULL);
    if (!th) die(5, "CreateRemoteThread failed (%lu)", GetLastError());
    if (WaitForSingleObject(th, timeout) != WAIT_OBJECT_0) die(5, "the loader thread did not finish in %lu ms", timeout);
    DWORD st = 0xFFFFFFFF; GetExitCodeThread(th, &st); CloseHandle(th);
    DWORD hmod = 0; rpm(remB, &hmod, 4);
    if (st != 0 || !hmod) die(5, "LdrLoadDll(%s) failed: NTSTATUS 0x%08lX, module 0x%08lX", dllFull, st, hmod);
    printf("OSOYLAUNCH injected %s at 0x%08lX in %lu ms (LdrLoadDll NTSTATUS 0; the main thread has NOT run yet)\n", dllFull, hmod, GetTickCount() - t0);
    VirtualFreeEx(g_proc, rem, 0, MEM_RELEASE);

    /* ---- DllMain's verdict: the event is set only when H2ToolHooks::hook(flags) returned true ---- */
    if (WaitForSingleObject(ev, 0) != WAIT_OBJECT_0)
        die(6, "H2ToolHooks did NOT signal %s -- hook() failed (a prologue mismatch, or not the stock tool_fast)", evName);
    printf("OSOYLAUNCH DllMain: H2ToolHooks::hook() returned true (event signalled)\n");

    /* ---- read every site back ---- */
    IMAGE_DOS_HEADER dh; IMAGE_NT_HEADERS32 nh; DWORD modEnd = 0;
    if (rpm(hmod, &dh, sizeof(dh)) && rpm(hmod + dh.e_lfanew, &nh, sizeof(nh))) modEnd = hmod + nh.OptionalHeader.SizeOfImage;
    if (!modEnd) die(7, "cannot read the injected module's headers");
    int bad = 0;
    for (int s = 0; s < NSITES; s++) {
        const Site* S = &SITES[s]; unsigned char b[8];
        int want = (S->group == 1) ? plucker : scenery;
        if (!rpm(S->va, b, S->len)) die(7, "cannot read back 0x%06lX", S->va);
        if (!want) {
            int stock = !memcmp(b, S->stock, S->len);
            printf("OSOYHOOK 0x%06lX %-52s not requested: %s\n", S->va, S->name, stock ? "stock (untouched)" : "*** MODIFIED");
            if (!stock) bad++;
            continue;
        }
        int ok = (b[0] == 0xE9); for (int i = 5; i < S->len; i++) ok = ok && b[i] == 0x90;
        DWORD tgt = S->va + 5 + *(LONG*)(b + 1);
        const char* where = "?";
        if (ok) {
            if (tgt >= hmod && tgt < modEnd) where = "H2ToolHooks.dll image";
            else { MEMORY_BASIC_INFORMATION mi;
                   if (VirtualQueryEx(g_proc, (LPCVOID)(ULONG_PTR)tgt, &mi, sizeof(mi)) && mi.State == MEM_COMMIT && mi.Type == MEM_PRIVATE &&
                       (mi.Protect & (PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE)))
                       where = "private executable detour stub";
                   else ok = 0; }
        }
        printf("OSOYHOOK 0x%06lX %-52s %s  E9 -> 0x%08lX (%s)\n", S->va, S->name, ok ? "INSTALLED" : "*** NOT INSTALLED", tgt, where);
        if (!ok) bad++;
    }
    if (bad) die(7, "%d patch site(s) wrong -- see the OSOYHOOK lines", bad);

p4_quality_row:
    /* ---- P4 --quality-row: the child is still SUSPENDED (and, with a fix, its hooks are proven): rewrite the 'cuban' row ---- */
    if (qrow || testTerm) {
        DWORD rowVa = find_cuban_row();
        if (qrow) write_quality_row(rowVa, qPhotons, qMc);
        else { unsigned char r[32]; char line[400];
               if (!rpm(rowVa, r, 32)) die(4, "cannot read the cuban row at 0x%08lX", rowVa);
               row_fmt(r, line, sizeof(line)); printf("OSOYROW stock   va=0x%08lX %s\n", rowVa, line); }
        if (testTerm) {   /* SELF-TEST ONLY: dump the whole preset table (cuban is row 1 in tool_fast 3a889d37), then kill the child */
            for (int t = 0; t < 11; t++) {
                unsigned char r[32]; char line[400]; DWORD va = rowVa - 0x20 + 0x20 * (DWORD)t;
                if (!rpm(va, r, 32)) { printf("OSOYTABLE [%d] va=0x%08lX unreadable\n", t, va); continue; }
                row_fmt(r, line, sizeof(line)); printf("OSOYTABLE [%d] va=0x%08lX %s\n", t, va, line); }
            fflush(stdout);
            TerminateProcess(g_proc, 0xDEAD); WaitForSingleObject(g_proc, 10000);
            DWORD tec = 0; GetExitCodeProcess(g_proc, &tec); g_proc = NULL;
            printf("OSOYLAUNCH --test-terminate: child pid %lu TERMINATED while still SUSPENDED (exit code 0x%08lX) -- it never ran a tool instruction\n",
                   pi.dwProcessId, tec);
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(ev);
            return 0; }
    }

    /* ---- go ---- */
    if (pidf) { FILE* f = NULL; if (!fopen_s(&f, pidf, "w") && f) { fprintf(f, "%lu\n", pi.dwProcessId); fclose(f); } }
    if (fixes)
        printf("OSOYOOS HOOKS INSTALLED BEFORE THE FIRST TOOL INSTRUCTION: plucker=%s scenery=%s  (pid %lu, %s)\n",
               plucker ? "ON" : "off", scenery ? "ON" : "off", pi.dwProcessId, hold ? "HELD suspended for --resume" : "resuming");
    else
        printf("OSOYLAUNCH QUALITY ROW WRITTEN BEFORE THE FIRST TOOL INSTRUCTION, no Osoyoos hooks  (pid %lu, %s)\n",
               pi.dwProcessId, hold ? "HELD suspended for --resume" : "resuming");
    fflush(stdout);
    if (hold) {        /* the caller opens its own handle first (no exit race), then runs `osoy_launch --resume <pid>` */
        g_proc = NULL;
        printf("PID=%lu\n", pi.dwProcessId);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(ev);
        return 0;
    }
    if (ResumeThread(g_thread) == (DWORD)-1) die(8, "ResumeThread failed (%lu)", GetLastError());
    g_proc = NULL;   /* from here on the child is the caller's */
    printf("PID=%lu\n", pi.dwProcessId);
    fflush(stdout);
    if (waitChild) {   /* self-test only: report how the child ended */
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD ec = 0; GetExitCodeProcess(pi.hProcess, &ec);
        printf("OSOYLAUNCH --wait: child exit code 0x%08lX\n", ec);
    }
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(ev);
    return 0;
}
