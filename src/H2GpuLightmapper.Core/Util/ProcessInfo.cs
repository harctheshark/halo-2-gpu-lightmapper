using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace H2GpuLightmapper.Core.Util
{
    /// <summary>"Is anything named X running?" -- injectable so the lock's stale rule can be unit-tested.</summary>
    public interface IProcessProbe
    {
        /// <summary>True when at least one process with one of these names (no ".exe", case-insensitive) is running.</summary>
        bool AnyRunning(params string[] names);
        /// <summary>True when process <paramref name="pid"/> exists and (when <paramref name="startUtcTicks"/> is non-zero) was
        /// started at that exact time -- a recycled pid is "not alive".</summary>
        bool IsAlive(int pid, long startUtcTicks);
    }

    public sealed class SystemProcessProbe : IProcessProbe
    {
        public static readonly SystemProcessProbe Instance = new SystemProcessProbe();

        public bool AnyRunning(params string[] names)
        {
            foreach (var n in names)
            {
                var ps = Process.GetProcessesByName(n);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public bool IsAlive(int pid, long startUtcTicks) { return ProcessInfo.IsAlive(pid, startUtcTicks); }
    }

    public static class ProcessInfo
    {
        private static int _pid;
        private static long _startTicks;

        public static int CurrentPid
        {
            get { if (_pid == 0) using (var p = Process.GetCurrentProcess()) _pid = p.Id; return _pid; }
        }

        /// <summary>This process's start time (UTC ticks), the pid-reuse discriminator written into lock / run records.</summary>
        public static long CurrentStartUtcTicks
        {
            get { if (_startTicks == 0) _startTicks = StartUtcTicks(CurrentPid); return _startTicks; }
        }

        /// <summary>Start time of a process (UTC ticks, rounded to whole milliseconds), or 0 if it cannot be read.</summary>
        public static long StartUtcTicks(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                long c, e, k, u;
                if (!GetProcessTimes(h, out c, out e, out k, out u)) return 0;
                long t = DateTime.FromFileTimeUtc(c).Ticks;
                return t - t % TimeSpan.TicksPerMillisecond;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// Fails CLOSED (review fix): only "no such process" (OpenProcess -> ERROR_INVALID_PARAMETER) counts as dead. A process
        /// we may not open (ERROR_ACCESS_DENIED: another user's, a protected process, a hardened DACL) or any other error is
        /// treated as ALIVE -- judging a live shell dead would delete its GPU lock, mark its run INTERRUPTED and offer its live
        /// install marker for a restore. Without SYNCHRONIZE access the exit is read with GetExitCodeProcess instead.
        /// </summary>
        public static bool IsAlive(int pid, long startUtcTicks)
        {
            if (pid <= 0) return false;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
            bool canWait = h != IntPtr.Zero;
            if (!canWait)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_INVALID_PARAMETER) return false;              // no such pid
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() != ERROR_INVALID_PARAMETER;   // access denied etc.: alive
            }
            try
            {
                if (canWait) { if (WaitForSingleObject(h, 0) == 0) return false; }    // signalled = exited
                else { uint code; if (GetExitCodeProcess(h, out code) && code != STILL_ACTIVE) return false; }
                if (startUtcTicks == 0) return true;
                long c, e, k, u;
                if (!GetProcessTimes(h, out c, out e, out k, out u)) return true;
                long t = DateTime.FromFileTimeUtc(c).Ticks;
                t -= t % TimeSpan.TicksPerMillisecond;
                return t == startUtcTicks;
            }
            finally { CloseHandle(h); }
        }

        public static string FormatTicks(long ticks)
        {
            return ticks == 0 ? "0" : new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000, STILL_ACTIVE = 259;
        private const int ERROR_INVALID_PARAMETER = 87;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    }
}
