// Minimal 32-bit LoadLibrary injector (tool_fast.exe is x86, so this must be x86 too so that
// GetProcAddress(LoadLibraryA) yields the address valid in the target's shared 32-bit kernel32).
// usage: inject <pid> <dll-path>
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>

int main(int argc, char** argv) {
    if (argc < 3) { printf("usage: inject <pid> <dll-path>\n"); return 1; }
    DWORD pid = strtoul(argv[1], nullptr, 0);
    char full[MAX_PATH]{};
    GetFullPathNameA(argv[2], MAX_PATH, full, nullptr);

    HANDLE proc = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
    if (!proc) { printf("OpenProcess(%lu) failed: %lu\n", pid, GetLastError()); return 1; }

    size_t n = strlen(full) + 1;
    void* remote = VirtualAllocEx(proc, nullptr, n, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { printf("VirtualAllocEx failed: %lu\n", GetLastError()); return 1; }
    WriteProcessMemory(proc, remote, full, n, nullptr);

    FARPROC loadlib = GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA");
    HANDLE thr = CreateRemoteThread(proc, nullptr, 0, (LPTHREAD_START_ROUTINE)loadlib, remote, 0, nullptr);
    if (!thr) { printf("CreateRemoteThread failed: %lu\n", GetLastError()); return 1; }
    WaitForSingleObject(thr, 15000);
    DWORD hmod = 0; GetExitCodeThread(thr, &hmod);
    printf("injected \"%s\" into pid %lu  (LoadLibraryA returned HMODULE=0x%08lX)\n", full, pid, hmod);

    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(thr); CloseHandle(proc);
    return hmod ? 0 : 2;
}
