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
   is, 8 resume failed.
   Build: build_osoy_launch.bat (vcvarsall x86, cl /O2 /MT). */
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

int main(int argc, char** argv)
{
    int plucker = 0, scenery = 0, seed = 0, waitChild = 0, hold = 0; DWORD timeout = 60000, resumePid = 0;
    const char *dll = NULL, *exe = NULL, *argsf = NULL, *cwd = NULL, *outf = NULL, *errf = NULL, *pidf = NULL;
    for (int i = 1; i < argc; i++) {
        const char* a = argv[i];
#define NEXT() ((i + 1 < argc) ? argv[++i] : (die(2, "%s needs a value", a), (char*)0))
        if (!strcmp(a, "--plucker")) plucker = 1;
        else if (!strcmp(a, "--scenery")) scenery = 1;
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
    if (!dll || !exe || !argsf) die(2, "usage: osoy_launch [--plucker] [--scenery] --dll D --exe E --argsfile F [--cwd C] [--stdout O --stderr R] [--pidfile P]");
    if (!plucker && !scenery) die(2, "no fix requested -- launch the tool the normal way (this helper exists only to inject H2ToolHooks.dll)");
    if ((outf == NULL) != (errf == NULL)) die(2, "--stdout and --stderr go together");

    char dllFull[MAX_PATH], exeFull[MAX_PATH];
    if (!GetFullPathNameA(dll, MAX_PATH, dllFull, NULL) || GetFileAttributesA(dllFull) == INVALID_FILE_ATTRIBUTES) die(2, "no DLL %s", dll);
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
    BOOL inherit = FALSE; DWORD cflags = CREATE_SUSPENDED | CREATE_NEW_CONSOLE;
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

    /* ---- go ---- */
    if (pidf) { FILE* f = NULL; if (!fopen_s(&f, pidf, "w") && f) { fprintf(f, "%lu\n", pi.dwProcessId); fclose(f); } }
    printf("OSOYOOS HOOKS INSTALLED BEFORE THE FIRST TOOL INSTRUCTION: plucker=%s scenery=%s  (pid %lu, %s)\n",
           plucker ? "ON" : "off", scenery ? "ON" : "off", pi.dwProcessId, hold ? "HELD suspended for --resume" : "resuming");
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
