// S0.2 spike -> reusable by S2.3 (the shell's process runner).
//   CreateProcessW(CREATE_SUSPENDED | CREATE_NO_WINDOW | BELOW_NORMAL | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT)
//   -> AssignProcessToJobObject(job with KILL_ON_JOB_CLOSE [+ DIE_ON_UNHANDLED_EXCEPTION]) -> ResumeThread.
//   stdin = NUL, stdout / stderr = two anonymous pipes read on two threads, split into lines, decoded per line
//   (strict UTF-8, else the console OEM code page). PROC_THREAD_ATTRIBUTE_HANDLE_LIST limits what the child inherits to
//   exactly those three handles, so a concurrent run's pipe ends never leak into this child.
//   Rules proven by tests\S0.2_pwsh_spike (see its PROGRESS.md):
//   * completion = the MAIN process exits; then the pipes are drained for DrainTimeoutMs. A grandchild that inherited the
//     pipe (pwsh Start-Process with -Redirect*, any '& native') keeps it open, so EOF is NOT a completion signal.
//   * whatever is still in the job after the main exit is an orphan: reported, and killed when KillOrphansAfterExit.
//   * Terminate() = TerminateJobObject (the whole tree); Dispose() closes the job handle, and KILL_ON_JOB_CLOSE kills
//     anything left -- also when the shell itself dies.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static H2Lm.Proc.NativeMethods;

namespace H2Lm.Proc
{
    public sealed class JobProcessOptions
    {
        public string FileName;                                  // absolute path of the exe (also argv[0])
        public IList<string> Arguments = new List<string>();     // quoted by JobProcess.QuoteArg (CommandLineToArgvW rules)
        public string WorkingDirectory;                          // required, absolute
        public ChildEnvironment Environment;                     // null = a copy of the shell's environment
        public bool NoWindow = true;                             // CREATE_NO_WINDOW (a hidden console the whole tree shares)
        public bool BelowNormal = true;                          // BELOW_NORMAL_PRIORITY_CLASS (inherited by the tree)
        public bool NoGpFaultErrorBox = true;                    // JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION: no WER dialog
        public int DrainTimeoutMs = 3000;                        // after the main exit, wait this long for pipe EOF
        public bool KillOrphansAfterExit = true;                 // TerminateJobObject on whatever outlived the main process
        public Action<string> OnStdoutLine;                      // called on the stdout reader thread
        public Action<string> OnStderrLine;                      // called on the stderr reader thread
        public string LogPath;                                   // optional UTF-8 log: stdout lines as-is, stderr lines "ERR| "
        public bool CaptureRaw;                                  // keep the raw bytes (tests)
    }

    public sealed class OrphanInfo
    {
        public int Pid; public string Name;
        public override string ToString() { return Name + "(" + Pid + ")"; }
    }

    public sealed class JobProcess : IDisposable
    {
        private static readonly object CreateLock = new object();
        private IntPtr _job, _proc;
        private Thread _tOut, _tErr;
        private readonly JobProcessOptions _o;
        private readonly object _logLock = new object();
        private StreamWriter _log;
        private readonly MemoryStream _rawOut = new MemoryStream(), _rawErr = new MemoryStream();
        private bool _exited;
        private uint _exitCode;

        public int ProcessId { get; private set; }
        public string CommandLine { get; private set; }
        public bool PipesClosed { get; private set; }
        /// <summary>True when the pipes were still open DrainTimeoutMs after the main exit (a survivor inherited them).</summary>
        public bool DrainTimedOut { get; private set; }
        public List<OrphanInfo> Orphans { get; private set; }
        public uint ExitCode { get { if (!_exited) throw new InvalidOperationException("not exited"); return _exitCode; } }
        public bool HasExited { get { return _exited || WaitForSingleObject(_proc, 0) == WAIT_OBJECT_0; } }
        public byte[] RawStdout { get { lock (_rawOut) return _rawOut.ToArray(); } }
        public byte[] RawStderr { get { lock (_rawErr) return _rawErr.ToArray(); } }

        private JobProcess(JobProcessOptions o) { _o = o; Orphans = new List<OrphanInfo>(); }

        public static JobProcess Start(JobProcessOptions o)
        {
            if (o == null || string.IsNullOrEmpty(o.FileName)) throw new ArgumentException("FileName is required");
            if (string.IsNullOrEmpty(o.WorkingDirectory)) throw new ArgumentException("WorkingDirectory is required");
            var jp = new JobProcess(o);
            jp.StartCore();
            return jp;
        }

        private void StartCore()
        {
            var env = _o.Environment ?? ChildEnvironment.FromCurrentProcess();
            CommandLine = BuildCommandLine(_o.FileName, _o.Arguments);
            if (CommandLine.Length >= 32767) throw new ArgumentException("command line too long (" + CommandLine.Length + ")");
            if (!string.IsNullOrEmpty(_o.LogPath))
                _log = new StreamWriter(new FileStream(_o.LogPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

            // the job first: a failure here must not leave a suspended process behind
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
            var lim = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            lim.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | (_o.NoGpFaultErrorBox ? JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION : 0);
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref lim, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");

            IntPtr inR = IntPtr.Zero, outR = IntPtr.Zero, outW = IntPtr.Zero, errR = IntPtr.Zero, errW = IntPtr.Zero;
            IntPtr attr = IntPtr.Zero, hlist = IntPtr.Zero, envBlock = IntPtr.Zero;
            var pi = new PROCESS_INFORMATION();
            lock (CreateLock)
            {
                try
                {
                    var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = 1 };
                    if (!CreatePipe(out outR, out outW, ref sa, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stdout)");
                    if (!CreatePipe(out errR, out errW, ref sa, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stderr)");
                    SetHandleInformation(outR, HANDLE_FLAG_INHERIT, 0);
                    SetHandleInformation(errR, HANDLE_FLAG_INHERIT, 0);
                    inR = CreateFile("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (inR == INVALID_HANDLE_VALUE) { inR = IntPtr.Zero; throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile(NUL)"); }

                    // inherit EXACTLY these three handles
                    hlist = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(hlist, 0, inR);
                    Marshal.WriteIntPtr(hlist, IntPtr.Size, outW);
                    Marshal.WriteIntPtr(hlist, IntPtr.Size * 2, errW);
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attr = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attr, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                    if (!UpdateProcThreadAttribute(attr, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, hlist, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute(HANDLE_LIST)");

                    var si = new STARTUPINFOEX();
                    si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                    si.StartupInfo.hStdInput = inR;
                    si.StartupInfo.hStdOutput = outW;
                    si.StartupInfo.hStdError = errW;
                    si.lpAttributeList = attr;

                    envBlock = Marshal.StringToHGlobalUni(env.ToBlock());
                    uint flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
                               | (_o.NoWindow ? CREATE_NO_WINDOW : 0) | (_o.BelowNormal ? BELOW_NORMAL_PRIORITY_CLASS : 0);
                    var cmd = new StringBuilder(CommandLine, CommandLine.Length + 1);
                    if (!CreateProcess(_o.FileName, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, envBlock, _o.WorkingDirectory, ref si, out pi))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW " + _o.FileName);
                }
                finally
                {
                    // the parent never keeps the child's ends
                    if (outW != IntPtr.Zero) CloseHandle(outW);
                    if (errW != IntPtr.Zero) CloseHandle(errW);
                    if (inR != IntPtr.Zero) CloseHandle(inR);
                    if (attr != IntPtr.Zero) { DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); }
                    if (hlist != IntPtr.Zero) Marshal.FreeHGlobal(hlist);
                    if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
                    if (pi.hProcess == IntPtr.Zero)
                    {
                        if (outR != IntPtr.Zero) CloseHandle(outR);
                        if (errR != IntPtr.Zero) CloseHandle(errR);
                    }
                }
            }
            _proc = pi.hProcess;
            ProcessId = pi.dwProcessId;
            if (!AssignProcessToJobObject(_job, pi.hProcess))
            {
                int err = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                CloseHandle(outR); CloseHandle(errR);
                throw new Win32Exception(err, "AssignProcessToJobObject");
            }
            _tOut = StartReader(outR, false);
            _tErr = StartReader(errR, true);
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
        }

        private Thread StartReader(IntPtr h, bool isErr)
        {
            var t = new Thread(() => ReadLoop(new SafeFileHandle(h, true), isErr)) { IsBackground = true, Name = isErr ? "stderr" : "stdout" };
            t.Start();
            return t;
        }

        private void ReadLoop(SafeFileHandle h, bool isErr)
        {
            var raw = isErr ? _rawErr : _rawOut;
            var cb = isErr ? _o.OnStderrLine : _o.OnStdoutLine;
            var line = new MemoryStream();
            var buf = new byte[8192];
            try
            {
                using (var fs = new FileStream(h, FileAccess.Read, 4096, false))
                {
                    while (true)
                    {
                        int n;
                        try { n = fs.Read(buf, 0, buf.Length); } catch (IOException) { n = 0; }
                        if (n <= 0) break;
                        if (_o.CaptureRaw) lock (raw) raw.Write(buf, 0, n);
                        int s = 0;
                        for (int i = 0; i < n; i++)
                        {
                            if (buf[i] != (byte)'\n') continue;
                            line.Write(buf, s, i - s);
                            Emit(line, isErr, cb);
                            s = i + 1;
                        }
                        if (s < n) line.Write(buf, s, n - s);
                    }
                }
                if (line.Length > 0) Emit(line, isErr, cb);
            }
            catch (Exception ex)
            {
                try { if (cb != null) cb("[runner] reader error: " + ex.Message); } catch { }
            }
        }

        private void Emit(MemoryStream line, bool isErr, Action<string> cb)
        {
            var b = line.GetBuffer();
            int len = (int)line.Length;
            if (len > 0 && b[len - 1] == (byte)'\r') len--;
            string s = LineDecoder.Decode(b, 0, len);
            line.SetLength(0);
            if (_log != null) lock (_logLock) _log.WriteLine(isErr ? "ERR| " + s : s);
            if (cb != null) { try { cb(s); } catch { } }
        }

        /// <summary>Waits for the MAIN process to exit, then drains the pipes (bounded) and handles orphans.
        /// Returns false on timeout (nothing is killed then; call Terminate()).</summary>
        public bool WaitForExit(int timeoutMs)
        {
            if (_exited) return true;
            uint w = WaitForSingleObject(_proc, timeoutMs < 0 ? INFINITE : (uint)timeoutMs);
            if (w != WAIT_OBJECT_0) return false;
            uint code;
            GetExitCodeProcess(_proc, out code);
            _exitCode = code;
            bool drained = _tOut.Join(_o.DrainTimeoutMs) & _tErr.Join(Math.Max(1, _o.DrainTimeoutMs / 4));
            DrainTimedOut = !drained;
            var left = new List<int>();
            foreach (int pid in GetJobProcessIds()) if (pid != ProcessId) left.Add(pid);
            if (left.Count > 0)
            {
                // S2.5 (2026-10-14): a console child's own conhost.exe (CREATE_NO_WINDOW still gets a headless one, in the job)
                // exits a moment AFTER the child, so a single snapshot reported it on every py.exe run as "1 process outlived
                // it: ?(pid)". Only processes still in the job a short grace later are orphans.
                Thread.Sleep(300);
                var again = new HashSet<int>(GetJobProcessIds());
                left.RemoveAll(pid => !again.Contains(pid));
            }
            foreach (int pid in left) Orphans.Add(new OrphanInfo { Pid = pid, Name = NameOf(pid) });
            if (Orphans.Count > 0 && _o.KillOrphansAfterExit)
            {
                TerminateJobObject(_job, 0xC000013A);
                drained = _tOut.Join(5000) & _tErr.Join(5000);
            }
            PipesClosed = drained;
            _exited = true;
            return true;
        }

        /// <summary>Kills the whole tree (TerminateJobObject). Safe to call at any time.</summary>
        public void Terminate(uint exitCode = 0xC000013A)
        {
            if (_job != IntPtr.Zero) TerminateJobObject(_job, exitCode);
        }

        public int[] GetJobProcessIds()
        {
            if (_job == IntPtr.Zero) return new int[0];
            const int max = 4096;
            int cb = 8 + IntPtr.Size * max;
            IntPtr buf = Marshal.AllocHGlobal(cb);
            try
            {
                int ret;
                if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buf, cb, out ret)) return new int[0];
                int n = Marshal.ReadInt32(buf, 4);
                var r = new int[n];
                for (int i = 0; i < n; i++) r[i] = (int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64();
                return r;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>(total processes ever in the job, active now, terminated by a job/limit kill)</summary>
        public uint[] GetAccounting()
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION a;
            int ret;
            if (_job == IntPtr.Zero || !QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out a, Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), out ret))
                return new uint[] { 0, 0, 0 };
            return new uint[] { a.TotalProcesses, a.ActiveProcesses, a.TotalTerminatedProcesses };
        }

        public bool IsInJob(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try { bool r; return IsProcessInJob(h, _job, out r) && r; } finally { CloseHandle(h); }
        }

        public static string NameOf(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; } catch { return "?"; }
        }

        public void Dispose()
        {
            // closing the last job handle kills anything still in the job (KILL_ON_JOB_CLOSE)
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
            if (_tOut != null) _tOut.Join(5000);
            if (_tErr != null) _tErr.Join(5000);
            if (_proc != IntPtr.Zero) { CloseHandle(_proc); _proc = IntPtr.Zero; }
            if (_log != null) { lock (_logLock) { _log.Dispose(); _log = null; } }
        }

        /// <summary>One argument, quoted for CommandLineToArgvW / the MSVC CRT (what pwsh.exe and python.exe parse with).</summary>
        public static string QuoteArg(string a)
        {
            if (a == null) a = "";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            for (int i = 0; ; i++)
            {
                int bs = 0;
                while (i < a.Length && a[i] == '\\') { i++; bs++; }
                if (i == a.Length) { sb.Append('\\', bs * 2); break; }
                if (a[i] == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(a[i]); }
            }
            return sb.Append('"').ToString();
        }

        public static string BuildCommandLine(string exe, IEnumerable<string> args)
        {
            var sb = new StringBuilder(QuoteArg(exe));
            if (args != null) foreach (var a in args) sb.Append(' ').Append(QuoteArg(a));
            return sb.ToString();
        }
    }

    /// <summary>Per-line decode of a child's pipe bytes. pwsh writes in its console's output code page (the OEM code page in a
    /// fresh CREATE_NO_WINDOW console, the same as production's hidden console); a native child that writes straight into the
    /// inherited pipe may write UTF-8 (python with PYTHONIOENCODING=utf-8). ASCII is identical in both; a line that is valid
    /// UTF-8 is taken as UTF-8, any other line as OEM.</summary>
    public static class LineDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static readonly Encoding Oem = Encoding.GetEncoding((int)GetOEMCP());
        private static readonly Regex Sgr = new Regex(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

        public static string Decode(byte[] b, int off, int len)
        {
            if (len >= 3 && b[off] == 0xEF && b[off + 1] == 0xBB && b[off + 2] == 0xBF) { off += 3; len -= 3; }
            bool ascii = true;
            for (int i = off; i < off + len; i++) if (b[i] >= 0x80) { ascii = false; break; }
            if (ascii) return Encoding.ASCII.GetString(b, off, len);
            try { return StrictUtf8.GetString(b, off, len); }
            catch (DecoderFallbackException) { return Oem.GetString(b, off, len); }
        }

        /// <summary>The same SGR strip run_bake.ps1 applies (a safety net; NO_COLOR=1 already makes pwsh plain).</summary>
        public static string StripAnsi(string s) { return Sgr.Replace(s, ""); }
    }
}
