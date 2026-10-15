/* fake_tool.c -- SELF-TEST target for osoy_launch.exe (v370). NOT a tool: an x86 console exe fixed at ImageBase 0x400000
   whose zero-filled .bss spans 0x499603..0x4B2F2D, so osoy_launch --test-seed-stock can write the five stock tool_fast
   prologues there and the REAL H2ToolHooks.dll can run its install path (memcmp + E9 writes) against them without the tool
   ever running. The hooked bytes are data here and are never executed. main() reports what it finds at the five sites. */
#include <stdio.h>
#include <string.h>
static unsigned char g_span[0x180000];   /* .bss: starts a few pages after 0x400000, covers past 0x4B3000 */
int main(int argc, char** argv)
{
    static const unsigned long va[5] = { 0x49C09C, 0x4B2F27, 0x49CF55, 0x49DA6A, 0x499603 };
    unsigned long lo = (unsigned long)(size_t)g_span, hi = lo + sizeof(g_span);
    printf("fake_tool: span 0x%08lX..0x%08lX, args:", lo, hi);
    for (int i = 1; i < argc; i++) printf(" [%s]", argv[i]);
    printf("\n");
    for (int i = 0; i < 5; i++) {
        if (va[i] < lo || va[i] + 8 > hi) { printf("fake_tool: 0x%06lX OUTSIDE the span\n", va[i]); return 3; }
        const unsigned char* p = (const unsigned char*)(size_t)va[i];
        printf("fake_tool: 0x%06lX = %02X %02X %02X %02X %02X %02X %02X %02X\n", va[i], p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
    }
    return 0;
}
