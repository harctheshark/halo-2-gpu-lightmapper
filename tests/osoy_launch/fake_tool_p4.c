/* fake_tool_p4.c -- S1.3 / P4 self-test target for osoy_launch.exe. = production osoyoos\selftest\fake_tool.c (v370) plus a
   report of whether this process has a console window and whether it is visible (the --nowindow proof: under CREATE_NO_WINDOW
   the child owns a console with no window, and H2ToolHooks' AllocConsole() in DllMain must not create one), and of what its
   std output handle is.
   The report is ALSO written to the file named by the env var FAKE_TOOL_REPORT (passes through osoy_launch, which strips only
   OSOYOOS_*), because the stdout of an injected child does NOT reach osoy_launch's --stdout file: H2ToolHooks' DllMain
   freopen()s stdout/stderr onto CONOUT$, which re-points the process's std handles at the console before this exe's CRT starts
   (also true of the production v370 deadlock test: its dl_out.txt is 0 bytes).
   NOT a tool: an x86 console exe fixed at ImageBase 0x400000 whose zero-filled .bss spans 0x499603..0x4B2F2D, so
   osoy_launch --test-seed-stock can write the five stock tool_fast prologues there and the REAL H2ToolHooks.dll can run its
   install path (memcmp + E9 writes) against them without the tool ever running. The hooked bytes are data here and are never
   executed. It has NO 'cuban' quality row, so --quality-row against it must fail (exit 4). */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static unsigned char g_span[0x180000];   /* .bss: starts a few pages after 0x400000, covers past 0x4B3000 */
static FILE* g_rep = NULL;
static void say(const char* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt); vprintf(fmt, ap); va_end(ap);
    if (g_rep) { va_start(ap, fmt); vfprintf(g_rep, fmt, ap); va_end(ap); fflush(g_rep); }
}
static const char* ftype(DWORD s)
{
    HANDLE h = GetStdHandle(s);
    if (h == NULL || h == INVALID_HANDLE_VALUE) return "none";
    switch (GetFileType(h)) { case FILE_TYPE_CHAR: return "char(console)"; case FILE_TYPE_DISK: return "disk(file)";
                              case FILE_TYPE_PIPE: return "pipe"; default: return "unknown"; }
}
int main(int argc, char** argv)
{
    static const unsigned long va[5] = { 0x49C09C, 0x4B2F27, 0x49CF55, 0x49DA6A, 0x499603 };
    const char* rep = getenv("FAKE_TOOL_REPORT");
    if (rep && *rep) fopen_s(&g_rep, rep, "w");
    unsigned long lo = (unsigned long)(size_t)g_span, hi = lo + sizeof(g_span);
    say("fake_tool: span 0x%08lX..0x%08lX, args:", lo, hi);
    for (int i = 1; i < argc; i++) say(" [%s]", argv[i]);
    say("\n");
    HWND cw = GetConsoleWindow();
    say("fake_tool: console window 0x%08lX visible=%d\n", (unsigned long)(size_t)cw, cw ? (IsWindowVisible(cw) ? 1 : 0) : 0);
    say("fake_tool: std handles: out=%s err=%s\n", ftype(STD_OUTPUT_HANDLE), ftype(STD_ERROR_HANDLE));
    int rc = 0;
    for (int i = 0; i < 5; i++) {
        if (va[i] < lo || va[i] + 8 > hi) { say("fake_tool: 0x%06lX OUTSIDE the span\n", va[i]); rc = 3; break; }
        const unsigned char* p = (const unsigned char*)(size_t)va[i];
        say("fake_tool: 0x%06lX = %02X %02X %02X %02X %02X %02X %02X %02X\n", va[i], p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
    }
    if (g_rep) fclose(g_rep);
    return rc;
}
