using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace H2GpuLightmapper.Ui
{
    /// <summary>
    /// Console output for the GUI-subsystem exe in --cli mode, without ever opening a window:
    /// 1. if stdout is already a pipe or file (Bash, Python subprocess, redirect), keep the inherited handles;
    /// 2. else AttachConsole(ATTACH_PARENT_PROCESS) (launched from cmd / pwsh in a console);
    /// 3. else run silent (double-clicked with --cli): output only goes to the log file the caller writes.
    /// Never AllocConsole.
    /// </summary>
    public static class ConsoleHost
    {
        const int STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);

        /// <summary>"inherited-pipe", "inherited-file", "attached", or "none".</summary>
        public static string Mode { get; private set; } = "unset";

        public static string Attach()
        {
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            uint t = (h == IntPtr.Zero || h == new IntPtr(-1)) ? 0 : GetFileType(h);
            if (t == FILE_TYPE_PIPE || t == FILE_TYPE_DISK)
            {
                Mode = t == FILE_TYPE_PIPE ? "inherited-pipe" : "inherited-file";
            }
            else if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Mode = "attached";
                // re-bind Console.Out/Err to the freshly attached console
                var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                var e = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(o); Console.SetError(e);
                return Mode;
            }
            else
            {
                Mode = "none";
                return Mode;
            }
            var so = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var se = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(so); Console.SetError(se);
            return Mode;
        }
    }
}
