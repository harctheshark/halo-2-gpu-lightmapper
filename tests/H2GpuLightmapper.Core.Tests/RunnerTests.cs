using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class RunnerTests
    {
        private static ResolvedRuntime _rt;

        /// <summary>A fake ROOT whose INST comes from the test payload (fake gpu_bake.ps1), run with the REAL portable pwsh 7.6.6
        /// (S0.2 cache) and the REAL Python bundle (S0.3 build).</summary>
        internal static ResolvedRuntime Rt
        {
            get
            {
                if (_rt != null) return _rt;
                string pd = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_r"), TestPayload.Standard(C, "r"));
                var inst = new PayloadInstaller(new DirectoryPayloadSource(pd), ShellPaths.ForSandbox(C.Sb.Dir("lad_r"), C.Short.Dir("RL"), C.Short.Dir("RV")));
                string root = C.Sb.Dir("rroot");
                C.Sb.Dir(@"rroot\tags\scenarios\multi\fake");
                inst.EnsureRuntimes();
                inst.EnsureInst(root);
                var rt = inst.Resolve(root);
                rt.PwshDir = C.PwshDir; rt.PwshExe = C.PwshExe; rt.PyDir = C.PyDir; rt.PyExe = C.PyExe;
                _rt = rt;
                return rt;
            }
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("runner: progress parser (banners, notes, GUARD FAIL, INSTALL, pwsh error block reason)", Parser);
            yield return T("runner: pwsh argv guards (-?, -Cleanup, worker 149 / outside 200-299)", ArgvGuards);
            yield return T("runner: ok bake -> phases, notes, bake.log, status.json, current_run.json lifecycle", OkRun);
            yield return T("runner: failing bake -> exit 1 + the thrown message as the reason; exit 7 kept", FailRun);
            yield return T("runner: child environment built from scratch (H2LM_*, PATH, bundle py, no GPULM_/OSOYOOS_)", EnvRun);
            yield return T("runner: cancel outside a tag write -> TerminateJobObject at once", CancelNow);
            yield return T("runner: cancel during [4b] is deferred until the write ends, then applied", CancelDeferred);
            yield return T("runner: cancel kills the whole tree (Start-Process grandchild + blocking child)", CancelTree);
            yield return T("runner: run header = pwsh 7.6.6 + bundle sys.executable + numpy 2.2.6", Header);
            yield return T("runner: re-attach: current_run.json Inspect + LogTail", Reattach);
            yield return T("runner: IsAlive fails closed (access denied = alive, no such pid = dead)", IsAliveFailsClosed);
        }

        private static void Parser()
        {
            var p = new ProgressParser();
            var phases = new List<string>();
            p.PhaseChanged += (a, b) => phases.Add(b);
            p.Feed("", false);
            Assert.Eq(LineKind.Phase, p.Feed("[1] CAPTURE - CPU loads the map  [sever=3]", false).Kind, "banner");
            Assert.Eq(LineKind.Plain, p.Feed("      [GPULM SKEL] not a banner", false).Kind, "indented bracket is not a banner");
            Assert.Eq(LineKind.Note, p.Feed("      *** NOT THE VALIDATED CONFIGURATION: x", false).Kind, "note");
            Assert.Eq(LineKind.GuardFail, p.Feed("      GUARD: *** FAIL seam lists stale", false).Kind, "guard fail");
            Assert.Eq(LineKind.Phase, p.Feed("\u001b[36m[4b] INSTALL - back up the live set\u001b[0m", false).Kind, "SGR stripped banner");
            Assert.True(p.InTagWrite, "4b is a tag write");
            Assert.Eq(LineKind.Install, p.Feed("      INSTALL: installed and verified 3 file(s)", false).Kind, "install line");
            p.Feed("[4c] A/B - x", false);
            Assert.True(!p.InTagWrite, "4c is not");
            p.Feed("[4] WRITE-BACK - inject", false);
            Assert.True(p.InTagWrite, "4 (tool write-back) is a tag write");
            p.ProcessEnded();
            Assert.True(!p.InTagWrite, "process end clears it");
            Assert.Eq("1,4b,4c,4", string.Join(",", phases), "phase events");
            foreach (var l in new[] { "Exception: C:\\x\\gpu_bake.ps1:1765", "Line |", " 1765 |  throw \"PHASE 4b: x\"", "     |  ~~~~~~~~~~~~~~~~~~", "     | PHASE 4b: lmw_install.py REFUSED before changing anything" })
                p.Feed(l, true);
            Assert.Eq("PHASE 4b: lmw_install.py REFUSED before changing anything", p.FailureReason(), "reason from the error block");
            Assert.Eq(StepId.Capture, ProgressParser.StepFor("1").Value, "1 -> Capture");
            Assert.Eq(StepId.ProbeCapture, ProgressParser.StepFor("1", true).Value, "probe worker 1 -> ProbeCapture");
            Assert.Eq(StepId.Install, ProgressParser.StepFor("4").Value, "4 -> Install");
            var ring = new LogRing(3);
            for (int i = 0; i < 5; i++) ring.Add(new ParsedLine { Text = "l" + i });
            Assert.Eq("l2,l3,l4", string.Join(",", ring.Snapshot().Select(x => x.Text)), "ring keeps the last N");
        }

        private static void ArgvGuards()
        {
            var rt = new ResolvedRuntime { PwshExe = "pwsh.exe", GpuBakePs1 = "gpu_bake.ps1", Work = "C:\\w" };
            Assert.Throws<ArgumentException>(() => PwshInvocation.GpuBake(rt, null, new[] { "-Scenario", "x", "-?" }), "-? refused");
            Assert.Throws<ArgumentException>(() => PwshInvocation.GpuBake(rt, null, new[] { "-Cleanup" }), "-Cleanup refused");
            Assert.Throws<SafetyException>(() => PwshInvocation.GpuBake(rt, null, new[] { "-Worker", "149" }), "149 refused");
            Assert.Throws<SafetyException>(() => PwshInvocation.GpuBake(rt, null, new[] { "-Worker", "60" }), "60 (launcher range) refused");
            var ok = PwshInvocation.GpuBake(rt, null, new[] { "-Scenario", "s", "-Bsp", "b", "-Worker", "210", "-Quality", "super" });
            Assert.Eq("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File gpu_bake.ps1 -Scenario s -Bsp b -Worker 210 -Quality super", string.Join(" ", ok.Args), "argv");
            Assert.Eq("C:\\w", ok.Cwd, "cwd = WORK");
        }

        private static BakeRun NewRun(string tag)
        {
            return BakeRun.Start(new RunSpec { Work = Rt.Work, ScenarioRel = @"scenarios\multi\fake\fake", Bsp = tag, Worker = 201, AppVersion = "test" });
        }

        private static ProcSpec Bake(string mode, params string[] extra)
        {
            var args = new List<string> { "-Scenario", @"scenarios\multi\fake\fake", "-Bsp", "fake", "-Worker", "201", "-Mode", mode };
            args.AddRange(extra);
            return PwshInvocation.GpuBake(Rt, BakeEnv.Build(Rt), args);
        }

        private static void OkRun()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var run = NewRun("okbsp");
            bool currentDuring = false;
            var seen = new List<string>();
            run.Line += l => { if (l.Kind == LineKind.Phase) { seen.Add(l.PhaseId); if (l.PhaseId == "1") currentDuring = File.Exists(CurrentRun.PathFor(Rt.Work)); } };
            var oc = run.RunProcess(Bake("ok"));
            Assert.True(oc.Ok, "exit 0: " + oc.ExitCode + " " + oc.FailureReason);
            Assert.Eq("1,2,3,4a", string.Join(",", seen), "phases");
            Assert.Eq(1, oc.Parser.Notes.Count, "one note");
            Assert.True(currentDuring, "current_run.json exists during the run");
            var st = RunStatus.Load(run.RunDir);
            Assert.Eq("4a", st.Phase, "status phase");
            Assert.Eq("TagWriter", st.Step, "status step");
            Assert.Contains(string.Join("|", st.Notes), "fake note one", "notes in status.json");
            Assert.Eq(0, run.Finish(RunState.Done), "exit code 0");
            Assert.True(!File.Exists(CurrentRun.PathFor(Rt.Work)), "current_run.json cleared");
            Assert.Eq(RunState.Done, RunStatus.Load(run.RunDir).State, "DONE");
            string log = Json.ReadAllTextShared(run.LogPath);
            Assert.Contains(log, "=== H2 GPU Lightmapper test run ", "header");
            Assert.Contains(log, "[3] GPU BAKE - fake bake", "banner in the log");
            Assert.Contains(log, "worker farm_w201", "script output");
            Assert.Contains(log, "=== DONE", "final line");
            Assert.True(run.RunDir.StartsWith(RunFolder.RunsDir(Rt.Work), StringComparison.OrdinalIgnoreCase) && run.RunTag.EndsWith("_fake_okbsp"), "run folder " + run.RunDir);
            Assert.True(run.Ring.Snapshot().Count > 5, "ring buffer filled");
        }

        private static void FailRun()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var run = NewRun("failbsp");
            var oc = run.RunProcess(Bake("fail"));
            Assert.Eq(1, oc.ExitCode, "uncaught throw = exit 1");
            Assert.Eq("PHASE 1: the fake capture failed on purpose", oc.FailureReason, "reason");
            Assert.Eq(1, oc.Parser.GuardFails.Count, "guard fail seen");
            Assert.Eq(3, run.Finish(RunState.Failed, oc.FailureReason), "exit code 3");
            var st = RunStatus.Load(run.RunDir);
            Assert.Eq(RunState.Failed, st.State, "FAILED");
            Assert.Eq(oc.FailureReason, st.Reason, "reason saved");
            Assert.Contains(Json.ReadAllTextShared(run.LogPath), "ERR| ", "stderr lines tagged in bake.log");
            var run2 = NewRun("exit7bsp");
            Assert.Eq(7, run2.RunProcess(Bake("exit7")).ExitCode, "exit 7");
            run2.Finish(RunState.Failed, "exit 7");
        }

        private static void EnvRun()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            Environment.SetEnvironmentVariable("GPULM_S2_TEST", "leak");
            Environment.SetEnvironmentVariable("OSOYOOS_S2_TEST", "leak");
            Environment.SetEnvironmentVariable("PYTHONPATH", @"C:\bogus\pythonpath");
            Environment.SetEnvironmentVariable("H2LM_ROOT", @"C:\bogus\root");
            Environment.SetEnvironmentVariable("DEVP_SB_PAD", "3");
            Environment.SetEnvironmentVariable("DEVP_PROF_EXIT", "1");
            Environment.SetEnvironmentVariable("V370_KEYF", @"C:\bogus\keyf.json");
            try
            {
                // review fix (findings 19 + 18): fullgpu's research knobs never reach a child; the plan logs names only for the rest
                var benv = BakeEnv.Build(Rt);
                var leaked = benv.Vars.Select(kv => kv.Key).Where(k => k.StartsWith("DEVP_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("V370_", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.Eq(0, leaked.Count, "DEVP_* / V370_* stripped: " + string.Join(", ", leaked));
                Assert.True(H2Lm.Proc.ChildEnvironment.IsLoggable("H2LM_ROOT") && H2Lm.Proc.ChildEnvironment.IsLoggable("PATH") && H2Lm.Proc.ChildEnvironment.IsLoggable("PYTHONIOENCODING")
                            && !H2Lm.Proc.ChildEnvironment.IsLoggable("CLAUDE_CODE_MESSAGING_TOKEN") && !H2Lm.Proc.ChildEnvironment.IsLoggable("USERPROFILE"), "loggable = only the shell's own variables");
                var run = NewRun("envbsp");
                var lines = new List<string>();
                run.Line += l => { if (l.Text.StartsWith("ENV ", StringComparison.Ordinal)) lock (lines) lines.Add(l.Text.Substring(4)); };
                var oc = run.RunProcess(Bake("env"));
                run.Finish(oc.Ok ? RunState.Done : RunState.Failed, oc.FailureReason);
                Assert.True(oc.Ok, "env run ok: " + oc.FailureReason);
                Func<string, string> v = k => { var l = lines.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal)); return l == null ? null : l.Substring(k.Length + 1); };
                Assert.Eq(Rt.Root, v("H2LM_ROOT"), "H2LM_ROOT = the real root (the inherited bogus one is gone)");
                Assert.Eq(Rt.Work, v("H2LM_WORK"), "H2LM_WORK = the REAL work");
                Assert.Eq(Rt.InstDir, v("H2LM_CODE"), "H2LM_CODE = INST");
                Assert.Eq("1", v("H2LM_NOWINDOW"), "NOWINDOW");
                Assert.Eq("utf-8", v("PYTHONIOENCODING"), "PYTHONIOENCODING");
                Assert.Eq("1", v("PYTHONDONTWRITEBYTECODE"), "no bytecode in INST");
                Assert.Eq("", v("PYTHONPATH"), "PYTHONPATH stripped");
                Assert.Eq("Bypass", v("PSExecutionPolicyPreference"), "policy");
                Assert.Eq("1", v("NO_COLOR"), "NO_COLOR");
                Assert.Eq(Path.Combine(C.PwshDir, "Modules"), v("PSModulePath").Split(';').Last(), "PSModulePath ends with the bundle's Modules (pwsh prepends user paths, S0.2)");
                Assert.Eq("0", v("pipeline-vars-count"), "no GPULM_ / OSOYOOS_ inherited");
                // inside pwsh, PATH = $PSHOME (pwsh prepends its own folder) + exactly what the shell set: py; pwsh; System32...
                string sys = Environment.GetEnvironmentVariable("SystemRoot");
                Assert.Eq((C.PwshDir + ";" + C.PyDir + ";" + C.PwshDir + ";" + Path.Combine(sys, "System32") + ";" + sys + ";" + Path.Combine(sys, @"System32\Wbem")).ToLowerInvariant(),
                          v("PATH").ToLowerInvariant(), "PATH = PSHOME + py;pwsh;System32;Windows;Wbem");
                Assert.Eq(C.PyExe, v("py"), "py resolves to the bundle");
                Assert.True(v("pwsh").StartsWith("7.6.6 ", StringComparison.Ordinal) && v("pwsh").EndsWith(C.PwshDir, StringComparison.OrdinalIgnoreCase), "pwsh 7.6.6 from the bundle: " + v("pwsh"));
                Assert.Contains(v("pyver"), "2.2.6", "numpy 2.2.6");
                Assert.Contains(v("pyver"), C.PyDir, "sys.executable in the bundle");
                Assert.Eq(Rt.Work, v("cwd"), "cwd = WORK");
            }
            finally
            {
                foreach (var n in new[] { "GPULM_S2_TEST", "OSOYOOS_S2_TEST", "PYTHONPATH", "H2LM_ROOT", "DEVP_SB_PAD", "DEVP_PROF_EXIT", "V370_KEYF" }) Environment.SetEnvironmentVariable(n, null);
            }
        }

        private static void CancelNow()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var run = NewRun("cancelbsp");
            run.Line += l => { if (l.Kind == LineKind.Phase && l.PhaseId == "3") ThreadPool.QueueUserWorkItem(_ => run.RequestCancel()); };
            var sw = Stopwatch.StartNew();
            var oc = run.RunProcess(Bake("cancel3"));
            Assert.True(oc.Cancelled, "cancelled");
            Assert.True(sw.Elapsed.TotalSeconds < 20, "terminated quickly (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
            Assert.Throws<OperationCanceledException>(() => run.ThrowIfCancelled(), "the orchestrator stops");
            Assert.Throws<OperationCanceledException>(() => run.RunProcess(Bake("ok")), "no further process starts");
            Assert.Eq(4, run.Finish(RunState.Cancelled), "exit 4");
            string log = Json.ReadAllTextShared(run.LogPath);
            Assert.NotContains(log, "SHOULD NOT PRINT", "the script did not continue");
            Assert.Eq(RunState.Cancelled, RunStatus.Load(run.RunDir).State, "CANCELLED");
        }

        private static void CancelDeferred()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var run = NewRun("deferbsp");
            bool deferred = false;
            var t4b = new ManualResetEventSlim();
            run.Line += l =>
            {
                if (l.Kind == LineKind.Phase && l.PhaseId == "4b") ThreadPool.QueueUserWorkItem(_ => { deferred = run.RequestCancel(); t4b.Set(); });
            };
            var sw = Stopwatch.StartNew();
            var oc = run.RunProcess(Bake("defer4b"));
            t4b.Wait(5000);
            Assert.True(deferred, "the cancel was deferred during [4b]");
            Assert.True(oc.Cancelled, "then applied");
            Assert.True(sw.Elapsed.TotalSeconds >= 4 && sw.Elapsed.TotalSeconds < 30, "the 4 s write was allowed to finish (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
            string log = Json.ReadAllTextShared(run.LogPath);
            Assert.Contains(log, "INSTALL: installed and verified", "the write completed");
            Assert.Contains(log, "deferred until the write ends", "deferral logged");
            Assert.Contains(log, "applying the deferred cancel now", "applied at the next banner");
            Assert.NotContains(log, "SHOULD NOT PRINT", "stopped right after the write");
            run.Finish(RunState.Cancelled);
        }

        private static void CancelTree()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var run = NewRun("treebsp");
            string pidFile = Path.Combine(run.RunDir, "grandchild.pid");
            run.Line += l => { if (l.Text.Contains("blocking child up")) ThreadPool.QueueUserWorkItem(_ => run.RequestCancel()); };
            var oc = run.RunProcess(Bake("tree", "-PidFile", pidFile));
            Assert.True(oc.Cancelled, "cancelled");
            int gpid = int.Parse(File.ReadAllText(pidFile).Trim());
            Thread.Sleep(500);
            Assert.True(!ProcessInfo.IsAlive(gpid, 0), "the Start-Process grandchild " + gpid + " is dead");
            Assert.NotContains(Json.ReadAllTextShared(run.LogPath), "SHOULD NOT PRINT", "nothing ran on");
            run.Finish(RunState.Cancelled);
        }

        private static void Header()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var h = RunHeader.Collect(C.PwshExe, C.PyExe, BakeEnv.Build(Rt), Rt.Work);
            Assert.True(h.Problems.Count == 0, "no problems: " + string.Join("; ", h.Problems));
            Assert.True(h.PwshVersion.StartsWith("7.6.6", StringComparison.Ordinal), "pwsh " + h.PwshVersion);
            Assert.Contains(h.PwshVersion, C.PyExe, "py on the child PATH is the bundle");
            Assert.True(h.PyExecutable.StartsWith(C.PyDir, StringComparison.OrdinalIgnoreCase), "sys.executable " + h.PyExecutable);
            Assert.Eq("2.2.6", h.NumpyVersion, "numpy");
            // review fix (finding 20): the header is CHECKED against the pin, not only logged
            Assert.Eq(0, h.Mismatches(C.PyExe, C.PyDir).Count, "the bundle matches the pin: " + string.Join("; ", h.Mismatches(C.PyExe, C.PyDir)));
            var bad = new RunHeaderInfo { PwshVersionOnly = "7.4.1", PyOnPath = @"C:\Windows\py.exe", PyExecutable = @"C:\Program Files\Python310\python.exe", NumpyVersion = "1.26.4" };
            var mm = bad.Mismatches(C.PyExe, C.PyDir);
            Assert.Eq(4, mm.Count, "pwsh, & py, sys.executable and numpy all flagged: " + string.Join("; ", mm));
            Assert.Eq(4, new RunHeaderInfo().Mismatches(C.PyExe, C.PyDir).Count, "an empty header (the probes failed) fails closed");
        }

        /// <summary>Review fix (finding 8): only "no such process" is dead; a process we may not open is alive (fail closed).</summary>
        private static void IsAliveFailsClosed()
        {
            Assert.True(ProcessInfo.IsAlive(ProcessInfo.CurrentPid, ProcessInfo.CurrentStartUtcTicks), "this process is alive");
            Assert.True(!ProcessInfo.IsAlive(ProcessInfo.CurrentPid, ProcessInfo.CurrentStartUtcTicks + TimeSpan.TicksPerSecond), "a recycled pid (other start time) is not");
            Assert.True(!ProcessInfo.IsAlive(999999, 0), "no such pid: dead");
            Assert.True(ProcessInfo.IsAlive(4, 0), "the System process (pid 4; OpenProcess with SYNCHRONIZE is denied) counts as alive");
        }

        private static void Reattach()
        {
            string work = C.Sb.Dir("reattach_work");
            CurrentRun cur;
            Assert.Eq(AttachKind.None, CurrentRun.Inspect(work, out cur), "nothing running");
            var run = BakeRun.Start(new RunSpec { Work = work, ScenarioRel = @"scenarios\x\y", Bsp = "b", Worker = 202 });
            Assert.Eq(AttachKind.LiveThisShell, CurrentRun.Inspect(work, out cur), "this shell owns it");
            Assert.Eq(run.RunDir, cur.RunDir, "names the run");
            var tail = new LogTail(run.LogPath);
            var first = tail.ReadNewLines();
            Assert.True(first.Count >= 2 && first[0].StartsWith("=== H2 GPU Lightmapper"), "tail reads the header");
            run.Shell("one more line");
            var more = tail.ReadNewLines();
            Assert.Eq("=== one more line", more.Single(), "only the new line");
            run.Finish(RunState.Done);
            Assert.Eq(AttachKind.None, CurrentRun.Inspect(work, out cur), "cleared");
            // a record written by ANOTHER shell process
            SafeFs.WriteAllTextAtomic(CurrentRun.PathFor(work), "{\"runDir\":\"" + run.RunDir.Replace("\\", "\\\\") + "\",\"runTag\":\"x\",\"shellPid\":999999,\"shellStartUtc\":\"2026-10-14T00:00:00.0000000Z\"}");
            Assert.Eq(AttachKind.LiveOtherShell, CurrentRun.Inspect(work, out cur, new FakeProbe { Alive = true }), "another live shell: attach read-only");
            Assert.Eq(AttachKind.Stale, CurrentRun.Inspect(work, out cur, new FakeProbe { Alive = false }), "its shell is gone: recovery");
            Assert.Eq(AttachKind.Stale, CurrentRun.Inspect(work, out cur), "pid 999999 really is not running");
            CurrentRun.Clear(work, run.RunDir);
        }
    }

    internal sealed class FakeProbe : IProcessProbe
    {
        public bool Busy, Alive;
        public bool AnyRunning(params string[] names) { return Busy; }
        public bool IsAlive(int pid, long start) { return Alive; }
    }
}
