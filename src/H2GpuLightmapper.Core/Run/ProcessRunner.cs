using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Run
{
    /// <summary>
    /// Short helper processes (gpucheck, the tool smoke run, the run header probes, lmw_install): S0.2's JobProcess
    /// (CREATE_SUSPENDED | CREATE_NO_WINDOW | BELOW_NORMAL, job with KILL_ON_JOB_CLOSE, stdin NUL, piped output), run to
    /// completion with a timeout; on timeout the whole tree is terminated.
    /// </summary>
    public static class ProcessRunner
    {
        public sealed class Result
        {
            public int ExitCode = -1;
            public bool TimedOut;
            public double Seconds;
            public List<string> StdoutLines = new List<string>();
            public List<string> StderrLines = new List<string>();
            public List<OrphanInfo> Orphans = new List<OrphanInfo>();
            public string CommandLine;
            public string Stdout { get { lock (StdoutLines) return string.Join("\n", StdoutLines); } }
            public string Stderr { get { lock (StderrLines) return string.Join("\n", StderrLines); } }
        }

        private const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] private static extern uint GetErrorMode();

        /// <summary>
        /// Children inherit the error mode of the process that creates them. With SEM_FAILCRITICALERRORS the loader's "a DLL
        /// was not found" hard-error box (and the GP-fault / open-file boxes) are never shown: the child just fails with
        /// 0xC0000135, which preflight reports. Called before every child the shell starts.
        /// </summary>
        public static void EnsureQuietErrorMode()
        {
            uint want = SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX;
            uint cur = GetErrorMode();
            if ((cur & want) != want) SetErrorMode(cur | want);
        }

        public static Result RunCapture(string exe, IList<string> args, string cwd, ChildEnvironment env, int timeoutMs,
            Action<string, bool> onLine = null, Action<JobProcess> onStarted = null)
        {
            EnsureQuietErrorMode();
            var r = new Result();
            var sw = Stopwatch.StartNew();
            var o = new JobProcessOptions
            {
                FileName = exe,
                Arguments = args ?? new string[0],
                WorkingDirectory = cwd,
                Environment = env,
                OnStdoutLine = l => { lock (r.StdoutLines) r.StdoutLines.Add(l); if (onLine != null) onLine(l, false); },
                OnStderrLine = l => { lock (r.StderrLines) r.StderrLines.Add(l); if (onLine != null) onLine(l, true); },
            };
            using (var jp = JobProcess.Start(o))
            {
                r.CommandLine = jp.CommandLine;
                if (onStarted != null) onStarted(jp);
                if (!jp.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    jp.Terminate(1);
                    jp.WaitForExit(10000);
                }
                if (jp.HasExited)
                {
                    try { r.ExitCode = unchecked((int)jp.ExitCode); } catch (InvalidOperationException) { }
                }
                r.Orphans = jp.Orphans;
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        /// <summary>"0xC0000135" style for NTSTATUS-like exit codes, the decimal otherwise.</summary>
        public static string FormatExit(int code)
        {
            return code < 0 || code > 0xFFFF ? "0x" + code.ToString("X8") : code.ToString();
        }

        public static string Describe(int code)
        {
            switch (unchecked((uint)code))
            {
                case 0xC0000135: return "a DLL it needs was not found (STATUS_DLL_NOT_FOUND)";
                case 0xC000007B: return "a DLL it loaded has the wrong bitness (STATUS_INVALID_IMAGE_FORMAT)";
                case 0xC0000139: return "a DLL entry point was not found (STATUS_ENTRYPOINT_NOT_FOUND)";
                case 0xC0000142: return "DLL initialisation failed (STATUS_DLL_INIT_FAILED)";
                case 0xC0000005: return "access violation";
                case 0xC000013A: return "terminated (cancelled)";
                default: return null;
            }
        }
    }
}
