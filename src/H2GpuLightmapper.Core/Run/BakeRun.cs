// S2.3 Shell: the process runner of one bake run (FINAL_DESIGN sections 5 and 9).
//   * every child: S0.2 JobProcess = CreateProcessW(SUSPENDED | NO_WINDOW | BELOW_NORMAL) -> job (KILL_ON_JOB_CLOSE) -> resume,
//     with the environment built from scratch (BakeEnv: ChildEnvironment.ForBake + PYTHONDONTWRITEBYTECODE=1);
//   * stdout / stderr -> RUN\bake.log (UTF-8, CRLF; stderr lines "ERR| ") + the UI ring buffer + ProgressParser;
//   * RUN\status.json and WORK\standalone\current_run.json for re-attach and recovery;
//   * cancel: TerminateJobObject at once, EXCEPT during a tag write (gpu_bake [4b] / [4], or a process the caller marks
//     TagWrite such as the shell's lmw_install): then the cancel is deferred until the write ends (the next phase banner or
//     the process exit). The caller checks ThrowIfCancelled() between steps.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Run
{
    public sealed class ProcSpec
    {
        public string Name;
        public string FileName;
        public IList<string> Args = new List<string>();
        public string Cwd;
        public ChildEnvironment Env;
        /// <summary>The whole process is a tag write (the shell's lmw_install / restore): a cancel waits for it to exit.</summary>
        public bool TagWrite;
        /// <summary>A probe-worker gpu_bake: phases 1-3 map to the ProbeCapture step.</summary>
        public bool ProbeWorker;
        /// <summary>-1 = no timeout.</summary>
        public int TimeoutMs = -1;
    }

    public sealed class ProcOutcome
    {
        public string Name;
        public int ExitCode = -1;
        public bool Cancelled, TimedOut;
        public string FailureReason;
        public List<OrphanInfo> Orphans = new List<OrphanInfo>();
        public double Seconds;
        public ProgressParser Parser;
        public string CommandLine;
        public bool Ok { get { return ExitCode == 0 && !Cancelled && !TimedOut; } }
    }

    public sealed class RunSpec
    {
        public string Work;
        public string ScenarioRel, Bsp;
        public int Worker;
        public DateTime? Now;
        /// <summary>Optional: an existing run folder to use instead of creating one.</summary>
        public string RunDir;
        public string AppVersion;
    }

    /// <summary>The child environment of every pipeline process (design section 5): S0.2's ChildEnvironment.ForBake --
    /// GPULM_*, OSOYOOS_*, H2LM_*, PYTHON*, PSModulePath... removed; H2LM_ROOT / WORK / CODE / NOWINDOW=1 set; PATH = rt py;
    /// rt pwsh; System32; Windows; Wbem -- plus PYTHONDONTWRITEBYTECODE=1 (S0.3: keeps __pycache__ out of INST).</summary>
    public static class BakeEnv
    {
        public static ChildEnvironment Build(ResolvedRuntime rt)
        {
            return ChildEnvironment.ForBake(rt.ToEnvSpec()).Set("PYTHONDONTWRITEBYTECODE", "1");
        }

        public static ChildEnvironment Build(BakeEnvSpec spec)
        {
            return ChildEnvironment.ForBake(spec).Set("PYTHONDONTWRITEBYTECODE", "1");
        }
    }

    /// <summary>pwsh argv (S0.2): -NoProfile -NonInteractive -ExecutionPolicy Bypass -File script args. Refuses "-?" (on
    /// gpu_bake it RUNS the body with defaults), "-Cleanup" (never), and a worker outside 200-299.</summary>
    public static class PwshInvocation
    {
        public static List<string> FileArgs(string script, IEnumerable<string> scriptArgs)
        {
            var a = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script };
            if (scriptArgs != null) a.AddRange(scriptArgs);
            return a;
        }

        public static ProcSpec GpuBake(ResolvedRuntime rt, ChildEnvironment env, IList<string> args, string name = "gpu_bake", bool probeWorker = false)
        {
            if (rt.GpuBakePs1 == null) throw new InvalidOperationException("the payload has no engine_v373\\gpu_bake.ps1");
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "-?" || a == "/?" || a.Equals("-Help", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("refused: '" + a + "' makes gpu_bake.ps1 RUN with its defaults (S0.2) -- use Get-Command -Syntax for help");
                if (a.Equals("-Cleanup", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("refused: -Cleanup is never passed by the standalone");
                if (a.Equals("-Worker", StringComparison.OrdinalIgnoreCase))
                {
                    int w;
                    if (i + 1 >= args.Count || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) throw new ArgumentException("-Worker needs a number");
                    Safety.Guards.AssertWorker(w);
                }
            }
            return new ProcSpec { Name = name, FileName = rt.PwshExe, Args = FileArgs(rt.GpuBakePs1, args), Cwd = rt.Work, Env = env, ProbeWorker = probeWorker };
        }
    }

    public sealed class BakeRun : IDisposable
    {
        private readonly object _lock = new object();
        private StreamWriter _log;
        private JobProcess _current;
        private ProcSpec _currentSpec;
        private ProgressParser _parser;
        private bool _terminatedByCancel;
        private Timer _heartbeat;

        public string Work { get; private set; }
        public string RunDir { get; private set; }
        public string RunTag { get; private set; }
        public string LogPath { get { return Path.Combine(RunDir, "bake.log"); } }
        public RunStatus Status { get; private set; }
        public LogRing Ring { get; } = new LogRing(5000);
        public bool CancelRequested { get { lock (_lock) return Status.CancelRequested; } }

        /// <summary>Every log line (reader threads or the caller's thread; marshal to the UI dispatcher).</summary>
        public event Action<ParsedLine> Line;
        /// <summary>status.json changed (state / step / phase / cancel flags).</summary>
        public event Action<RunStatus> StatusChanged;

        private BakeRun() { }

        public static BakeRun Start(RunSpec s)
        {
            if (string.IsNullOrEmpty(s.Work)) throw new ArgumentException("RunSpec.Work is required");
            var r = new BakeRun { Work = s.Work };
            r.RunDir = s.RunDir ?? RunFolder.Create(s.Work, s.ScenarioRel, s.Bsp, s.Now ?? DateTime.Now);
            Directory.CreateDirectory(r.RunDir);
            r.RunTag = Path.GetFileName(r.RunDir);
            r._log = new StreamWriter(new FileStream(r.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            r.Status = new RunStatus { RunDir = r.RunDir, RunTag = r.RunTag, Scenario = s.ScenarioRel, Bsp = s.Bsp, Worker = s.Worker };
            r.Status.Save();
            CurrentRun.Write(s.Work, r.RunDir, r.RunTag);
            r.Shell("H2 GPU Lightmapper " + (s.AppVersion ?? "") + " run " + r.RunTag);
            r.Shell("    scenario " + s.ScenarioRel + "   bsp " + s.Bsp + "   worker farm_w" + s.Worker.ToString("D2", CultureInfo.InvariantCulture));
            return r;
        }

        /// <summary>A line written by the shell itself ("=== " prefix in bake.log).</summary>
        public void Shell(string text)
        {
            var p = new ParsedLine { Text = text, Kind = LineKind.Shell, Utc = DateTime.UtcNow };
            lock (_lock) { if (_log != null) _log.WriteLine("=== " + text); }
            Ring.Add(p);
            var h = Line; if (h != null) { try { h(p); } catch { } }
        }

        /// <summary>A warning the shell raises itself (0.6.4: the engine's silent write-back fallback). Shown as a yellow Note line in the live log,
        /// written to bake.log, and kept in status.json's notes -- never lost in a scroll of dim Shell lines.</summary>
        public void Warn(string text)
        {
            var p = new ParsedLine { Text = "*** " + text, Kind = LineKind.Note, Utc = DateTime.UtcNow };
            lock (_lock)
            {
                if (_log != null) _log.WriteLine("=== " + p.Text);
                if (!Status.Notes.Contains(text)) Status.Notes.Add(text);
            }
            Ring.Add(p);
            var h = Line; if (h != null) { try { h(p); } catch { } }
            SaveStatus();
        }

        public void SetState(RunState st, string waitingFor = null)
        {
            lock (_lock) { Status.State = st; Status.WaitingFor = waitingFor; }
            SaveStatus();
        }

        public void SetStep(StepId step)
        {
            lock (_lock) Status.Step = step.ToString();
            SaveStatus();
        }

        /// <summary>Refreshes the GPU lock's mtime every <paramref name="period"/> while the run holds it, so run_bake's
        /// "older than 60 min and no fullgpu / tool_fast running" stale rule never fires on a live run (e.g. a long derive).</summary>
        public void KeepLockFresh(Safety.GpuLock lk, TimeSpan period)
        {
            _heartbeat?.Dispose();
            _heartbeat = new Timer(_ => { try { lk.Heartbeat(); } catch { } }, null, period, period);
        }

        private Safety.GpuLock _gpuLock;

        /// <summary>
        /// Takes the shared WORK\.gpu_bake.lock with run_bake's rules (Safety.GpuLock): while another bake holds it the run is
        /// WAITING and status.json's waitingFor says "GPU lock held by &lt;holder&gt;"; stale locks removed on the way are
        /// logged. Once acquired the run is RUNNING, the lock's mtime is refreshed every 5 min, and Finish releases it (only
        /// if it still names this run). Default wait limit 240 min, poll 20 s (run_bake's).
        /// </summary>
        public Safety.LockAcquire AcquireGpuLock(Safety.GpuLock lk, TimeSpan? maxWait = null, TimeSpan? poll = null, CancellationToken ct = default(CancellationToken))
        {
            Action<string> log = m => Shell("    " + m);
            lk.Log += log;
            var sw = Stopwatch.StartNew();
            try
            {
                bool reported = false;
                var r = lk.Acquire(maxWait ?? TimeSpan.FromMinutes(240), poll ?? TimeSpan.FromSeconds(20), ct, (holder, waited) =>
                {
                    lock (_lock) { Status.State = RunState.Waiting; Status.WaitingFor = "GPU lock held by " + holder; }
                    if (!reported) { Shell("waiting for the GPU lock held by: " + holder); reported = true; }
                    SaveStatus();
                });
                if (r == Safety.LockAcquire.Acquired)
                {
                    _gpuLock = lk;
                    Shell("GPU lock acquired after " + sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
                    SetState(RunState.Running);
                    KeepLockFresh(lk, TimeSpan.FromMinutes(5));
                }
                else Shell(r == Safety.LockAcquire.TimedOut ? "another bake held the GPU for too long -- gave up waiting" : "stopped waiting for the GPU lock");
                return r;
            }
            finally { lk.Log -= log; }
        }

        private void SaveStatus()
        {
            try { Status.Save(); } catch (Exception e) { Shell("status.json write failed: " + e.Message); }
            var h = StatusChanged; if (h != null) { try { h(Status); } catch { } }
        }

        // ------------------------------------------------------------------ processes

        public ProcOutcome RunProcess(ProcSpec spec)
        {
            ThrowIfCancelled();
            var parser = new ProgressParser();
            var oc = new ProcOutcome { Name = spec.Name, Parser = parser };
            var sw = Stopwatch.StartNew();
            var o = new JobProcessOptions
            {
                FileName = spec.FileName, Arguments = spec.Args, WorkingDirectory = spec.Cwd, Environment = spec.Env,
                OnStdoutLine = l => OnLine(spec, parser, l, false),
                OnStderrLine = l => OnLine(spec, parser, l, true),
            };
            ProcessRunner.EnsureQuietErrorMode();
            Shell("start " + spec.Name + ": " + JobProcess.BuildCommandLine(spec.FileName, spec.Args));
            Shell("    cwd " + spec.Cwd);
            JobProcess jp;
            lock (_lock)
            {
                _terminatedByCancel = false;
                _parser = parser;
                _currentSpec = spec;
                try { jp = JobProcess.Start(o); }
                catch (Exception e)
                {
                    _parser = null; _currentSpec = null;
                    if (_log != null) _log.WriteLine("=== " + spec.Name + " could not be started: " + e.Message);
                    throw;
                }
                _current = jp;
                Status.State = RunState.Running;
                Status.InTagWrite = spec.TagWrite;
            }
            SaveStatus();
            oc.CommandLine = jp.CommandLine;
            try
            {
                if (!jp.WaitForExit(spec.TimeoutMs))
                {
                    oc.TimedOut = true;
                    Shell(spec.Name + " timed out after " + spec.TimeoutMs / 1000 + " s -- terminating its process tree");
                    jp.Terminate(1);
                    jp.WaitForExit(15000);
                }
                try { oc.ExitCode = unchecked((int)jp.ExitCode); } catch (InvalidOperationException) { oc.ExitCode = -1; }
                oc.Orphans = jp.Orphans;
            }
            finally
            {
                lock (_lock)
                {
                    parser.ProcessEnded();
                    oc.Cancelled = _terminatedByCancel;
                    _current = null;
                    _currentSpec = null;
                    Status.InTagWrite = false;
                    Status.CancelDeferred = false;
                }
                jp.Dispose();
            }
            oc.Seconds = sw.Elapsed.TotalSeconds;
            if (oc.Orphans.Count > 0) Shell(spec.Name + ": " + oc.Orphans.Count + " process(es) outlived it and were stopped: " + string.Join(", ", oc.Orphans));
            if (!oc.Ok && !oc.Cancelled) oc.FailureReason = parser.FailureReason();
            Shell(spec.Name + " exit " + ProcessRunner.FormatExit(oc.ExitCode) + (oc.Cancelled ? " (cancelled)" : "") + "   " + oc.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                  + (oc.FailureReason != null ? "   reason: " + oc.FailureReason : ""));
            lock (_lock)
            {
                foreach (var n in parser.Notes) if (!Status.Notes.Contains(n)) Status.Notes.Add(n);
                foreach (var g in parser.GuardFails) if (!Status.GuardFails.Contains(g)) Status.GuardFails.Add(g);
                Status.ExitCode = oc.ExitCode;
            }
            SaveStatus();
            return oc;
        }

        private void OnLine(ProcSpec spec, ProgressParser parser, string raw, bool isErr)
        {
            ParsedLine p;
            lock (_lock)
            {
                p = parser.Feed(raw, isErr);
                if (_log != null) _log.WriteLine(isErr ? "ERR| " + p.Text : p.Text);
            }
            Ring.Add(p);
            var h = Line; if (h != null) { try { h(p); } catch { } }
            if (p.Kind == LineKind.Phase) OnPhase(spec, parser, p.PhaseId);   // outside the lock: it may terminate + raise events
        }

        private void OnPhase(ProcSpec spec, ProgressParser parser, string newId)
        {
            bool kill = false;
            lock (_lock)
            {
                Status.Phase = newId;
                Status.PhaseTitle = parser.PhaseTitle;
                var step = ProgressParser.StepFor(newId, spec.ProbeWorker);
                if (step.HasValue) Status.Step = step.Value.ToString();
                Status.InTagWrite = spec.TagWrite || parser.InTagWrite;
                if (Status.CancelRequested && Status.CancelDeferred && !Status.InTagWrite && _current != null) kill = true;
            }
            if (kill)
            {
                lock (_lock) { if (_log != null) _log.WriteLine("=== the tag write ended -- applying the deferred cancel now"); }
                TerminateCurrent();
            }
            SaveStatus();
        }

        private void TerminateCurrent()
        {
            JobProcess jp;
            lock (_lock) { jp = _current; if (jp != null) _terminatedByCancel = true; }
            if (jp != null) jp.Terminate();
        }

        /// <summary>
        /// Cancel (thread-safe; the UI calls it). Outside a tag write the running process tree is terminated now
        /// (TerminateJobObject). During a tag write the cancel is DEFERRED and applied when the write ends. Returns true when
        /// it was deferred. Between processes, the caller's next ThrowIfCancelled() stops the run.
        /// </summary>
        public bool RequestCancel()
        {
            bool deferred = false, kill = false;
            lock (_lock)
            {
                if (Status.IsFinal) return false;
                Status.CancelRequested = true;
                if (_current != null)
                {
                    bool tw = (_currentSpec != null && _currentSpec.TagWrite) || (_parser != null && _parser.InTagWrite);
                    if (tw) { Status.CancelDeferred = true; deferred = true; }
                    else kill = true;
                }
            }
            Shell(deferred ? "cancel requested during a TAG WRITE -- deferred until the write ends (the tags are never left half-written by a cancel)"
                           : "cancel requested");
            if (kill) TerminateCurrent();
            SaveStatus();
            return deferred;
        }

        public void ThrowIfCancelled()
        {
            if (CancelRequested) throw new OperationCanceledException("the run was cancelled");
        }

        /// <summary>Final state: status.json, current_run.json cleared, log closed. Returns the CLI exit code (0 done, 3 failed,
        /// 4 cancelled; 2 for a refusal before any write is the caller's to return).</summary>
        public int Finish(RunState final, string reason = null)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            if (_gpuLock != null)
            {
                bool rel = false;
                try { rel = _gpuLock.Release(); } catch { }
                Shell(rel ? "GPU lock released" : "the GPU lock no longer named this run -- left as it is");
                _gpuLock = null;
            }
            lock (_lock)
            {
                Status.State = final;
                Status.Reason = reason;
                Status.EndedUtc = DateTime.UtcNow;
                Status.InTagWrite = false;
            }
            Shell(final.ToString().ToUpperInvariant() + (reason != null ? ": " + reason : ""));
            SaveStatus();
            try { CurrentRun.Clear(Work, RunDir); } catch { }
            lock (_lock) { if (_log != null) { _log.Dispose(); _log = null; } }
            return final == RunState.Done ? 0 : final == RunState.Cancelled ? 4 : 3;
        }

        public void Dispose()
        {
            if (!Status.IsFinal) Finish(RunState.Failed, "the run object was disposed without a final state");
            _heartbeat?.Dispose();
            lock (_lock) { if (_log != null) { _log.Dispose(); _log = null; } }
        }
    }

    public sealed class RunHeaderInfo
    {
        /// <summary>"7.6.6 (py on PATH: X)" as logged; <see cref="PwshVersionOnly"/> / <see cref="PyOnPath"/> are the parts.</summary>
        public string PwshVersion, PyExecutable, NumpyVersion;
        public string PwshVersionOnly, PyOnPath;
        public List<string> Problems = new List<string>();
        public IEnumerable<string> Lines()
        {
            yield return "    pwsh " + (PwshVersion ?? "?");
            yield return "    python " + (PyExecutable ?? "?") + "   numpy " + (NumpyVersion ?? "?");
            foreach (var p in Problems) yield return "    *** header: " + p;
        }

        /// <summary>
        /// Review fix (finding 20): what the children report vs the pinned runtime -- pwsh <see cref="RunHeader.PinnedPwsh"/>,
        /// `& py` resolving to <paramref name="pyExe"/>, sys.executable inside <paramref name="pyDir"/>, numpy
        /// <see cref="RunHeader.PinnedNumpy"/>. Empty = the bake runs on the verified bundle.
        /// </summary>
        public List<string> Mismatches(string pyExe, string pyDir)
        {
            var m = new List<string>();
            if (PwshVersionOnly != RunHeader.PinnedPwsh) m.Add("pwsh is " + (PwshVersionOnly ?? "unknown") + ", not the pinned " + RunHeader.PinnedPwsh);
            if (!SamePath(PyOnPath, pyExe)) m.Add("`& py` resolves to " + (PyOnPath ?? "nothing") + ", not the bundle's " + pyExe);
            if (PyExecutable == null || pyDir == null || !SafeFs.IsUnderOrSame(SafeTrim(PyExecutable), pyDir)) m.Add("python's sys.executable is " + (PyExecutable ?? "unknown") + ", not inside " + pyDir);
            if (NumpyVersion != RunHeader.PinnedNumpy) m.Add("numpy is " + (NumpyVersion ?? "unknown") + ", not the pinned " + RunHeader.PinnedNumpy);
            return m;
        }

        private static string SafeTrim(string p) { return (p ?? "").Trim().Trim('"'); }
        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(SafeFs.Norm(SafeTrim(a)), SafeFs.Norm(SafeTrim(b)), StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
        }
    }

    /// <summary>The run header (plan S2.3): the pwsh version, sys.executable and the numpy version, as the CHILD environment
    /// sees them (proves the bundle, not a system Python, runs the bake).</summary>
    public static class RunHeader
    {
        /// <summary>The runtime versions the payload pins (FINAL_DESIGN: pwsh 7.6.6, CPython 3.10.5 + numpy 2.2.6).</summary>
        public const string PinnedPwsh = "7.6.6", PinnedNumpy = "2.2.6";

        public static RunHeaderInfo Collect(string pwshExe, string pyExe, ChildEnvironment env, string cwd)
        {
            var h = new RunHeaderInfo();
            try
            {
                var r = ProcessRunner.RunCapture(pwshExe, new[] { "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString(); (Get-Command py -CommandType Application | Select-Object -First 1).Source" }, cwd, env, 60000);
                if (r.ExitCode == 0 && r.StdoutLines.Count > 0)
                {
                    h.PwshVersionOnly = r.StdoutLines[0].Trim();
                    h.PyOnPath = r.StdoutLines.Count > 1 ? r.StdoutLines[1].Trim() : null;
                    h.PwshVersion = h.PwshVersionOnly + (h.PyOnPath != null ? " (py on PATH: " + h.PyOnPath + ")" : "");
                }
                else h.Problems.Add("pwsh version probe exit " + ProcessRunner.FormatExit(r.ExitCode) + " " + r.Stderr);
            }
            catch (Exception e) { h.Problems.Add("pwsh: " + e.Message); }
            try
            {
                var r = ProcessRunner.RunCapture(pyExe, new[] { "-c", "import sys,numpy;print(sys.executable);print(numpy.__version__)" }, cwd, env, 60000);
                if (r.ExitCode == 0 && r.StdoutLines.Count >= 2) { h.PyExecutable = r.StdoutLines[0].Trim(); h.NumpyVersion = r.StdoutLines[1].Trim(); }
                else h.Problems.Add("python probe exit " + ProcessRunner.FormatExit(r.ExitCode) + " " + r.Stderr);
            }
            catch (Exception e) { h.Problems.Add("python: " + e.Message); }
            return h;
        }
    }
}
