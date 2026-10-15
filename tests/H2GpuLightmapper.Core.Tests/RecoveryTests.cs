using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class RecoveryTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("recovery: interrupted run (dead shell) -> INTERRUPTED + current_run.json cleared", Interrupted);
            yield return T("recovery: lmw_install marker -> restore offer -> lmw_install restore puts the set back", InstallMarker);
            yield return T("recovery: tool write-back text marker; stale own lock released, foreign locks never", MarkersAndLocks);
            yield return T("recovery: cancel aftermath reads the install marker", Aftermath);
        }

        private static (int pid, long start) DeadProcess()
        {
            int pid = 0; long start = 0;
            ProcessRunner.RunCapture(C.PyExe, new[] { "-c", "pass" }, C.Sb.Root, null, 30000, null, jp => { pid = jp.ProcessId; start = ProcessInfo.StartUtcTicks(jp.ProcessId); });
            return (pid, start);
        }

        private static void Interrupted()
        {
            string work = C.Sb.Dir(@"rec1\GPU Lightmap Work");
            string runDir = Path.Combine(RunFolder.RunsDir(work), "20261014_010000_fake_fake");
            Directory.CreateDirectory(runDir);
            var st = new RunStatus { RunDir = runDir, RunTag = "20261014_010000_fake_fake", State = RunState.Running, Phase = "3", PhaseTitle = "GPU BAKE - x" };
            st.Save();
            var (pid, start) = C.NoProcesses ? (999999, 0L) : DeadProcess();
            SafeFs.WriteAllTextAtomic(CurrentRun.PathFor(work), Json.Write(new JObj().Add("runDir", runDir).Add("runTag", st.RunTag).Add("shellPid", pid).Add("shellStartUtc", ProcessInfo.FormatTicks(start))));
            var items = StartupRecovery.Scan(work);
            var it = items.Single(x => x.Kind == RecoveryKind.InterruptedRun);
            Assert.Contains(it.Message, "phase [3]", "message names the phase");
            StartupRecovery.MarkInterrupted(work, it);
            Assert.Eq(RunState.Interrupted, RunStatus.Load(runDir).State, "INTERRUPTED");
            Assert.True(!File.Exists(CurrentRun.PathFor(work)), "current_run.json cleared");
            Assert.Eq(0, StartupRecovery.Scan(work).Count, "nothing left to recover");
            // our own live run is never "interrupted"
            CurrentRun.Write(work, runDir, st.RunTag);
            Assert.Eq(0, StartupRecovery.Scan(work).Count(x => x.Kind == RecoveryKind.InterruptedRun), "a live run is not recovered");
            CurrentRun.Clear(work, runDir);
        }

        private static void InstallMarker()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("rec2");
            string stage = f.MakeStage("farm_w205", "v2");
            string runDir = C.Sb.Dir(@"troot_rec2\run");
            string bak = Path.Combine(runDir, "tag_backup_before_writeback");
            var before = new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" }.ToDictionary(n => n, f.Md5);
            var env = BakeEnv.Build(new H2Lm.Proc.BakeEnvSpec { PwshDir = C.PwshDir, PyDir = C.PyDir, Root = f.Root, Work = f.Work });
            var ok = TagInstaller.Install(new InstallRequest { PyExe = C.PyExe, LmwInstallPy = C.FxLmwInstall, Root = f.Root, Work = f.Work, Stage = stage, TargetDir = f.TagDir,
                BackupDir = bak, Marker = Path.Combine(f.Work, "farm_w205", StartupRecovery.MarkerName), Env = env });
            Assert.Eq(InstallOutcome.Installed, ok.Outcome, "install: " + ok.Message);
            // simulate the power cut after the first replace: the marker lmw_install keeps on failure (same shape as Marker.write)
            string marker = Path.Combine(f.Work, "farm_w205", StartupRecovery.MarkerName);
            var files = new JObj();
            foreach (var kv in before) files[kv.Key] = new JObj().Add("staged_md5", "x").Add("previous_md5", kv.Value).Add("state", kv.Key.EndsWith("_truecolor_bitmaps.bitmap") ? "installed" : "pending");
            File.WriteAllText(marker, Json.Write(new JObj().Add("started", "2026-10-14 02:00:00").Add("stage", stage).Add("target_dir", f.TagDir)
                .Add("backups", new List<object> { bak, Path.Combine(f.Work, "_TAGBAK_auto", "x") }).Add("note", "n").Add("files", files)));
            var it = StartupRecovery.Scan(f.Work).Single(x => x.Kind == RecoveryKind.InstallMarker);
            Assert.Eq(205, it.Worker, "farm");
            Assert.Eq(bak, it.BackupDir, "first backup = the restore source");
            Assert.Eq(f.TagDir, it.TargetDir, "target");
            Assert.Contains(it.Message, "not installed: fake_fake_lightmap.scenario_structure_lightmap", "pending files listed");
            var req = StartupRecovery.RestoreRequestFor(it, f.Root, f.Work);
            req.PyExe = C.PyExe; req.LmwInstallPy = C.FxLmwInstall; req.Env = env;
            var r = TagInstaller.Restore(req);
            Assert.Eq(InstallOutcome.Installed, r.Outcome, "lmw_install restore exit 0: " + r.Message);
            foreach (var kv in before) Assert.Eq(kv.Value, f.Md5(kv.Key), kv.Key + " restored");
            Assert.True(!File.Exists(marker), "marker renamed .restored_ by lmw_install");
            Assert.Eq(0, StartupRecovery.Scan(f.Work).Count, "clean afterwards");
        }

        private static void MarkersAndLocks()
        {
            string work = C.Sb.Dir(@"rec3\GPU Lightmap Work");
            string m = Path.Combine(work, "farm_w206", StartupRecovery.MarkerName);
            Directory.CreateDirectory(Path.GetDirectoryName(m));
            File.WriteAllText(m, "started 10/14/2026 02:00:00; previous tags: X:\\bak (3 files from X:\\tags\\a\\b_c_lightmap*). Copy them back if the new tag is broken.");
            string outside = Path.Combine(work, "farm_w60", StartupRecovery.MarkerName);   // launcher range: not ours, never scanned
            Directory.CreateDirectory(Path.GetDirectoryName(outside));
            File.WriteAllText(outside, "launcher marker");
            var items = StartupRecovery.Scan(work, new FakeProbe { Alive = true });
            Assert.Eq(1, items.Count, "only the standalone farm's marker: " + string.Join(" | ", items));
            Assert.Eq(RecoveryKind.ToolWriteBackMarker, items[0].Kind, "text marker");
            Assert.Contains(items[0].Message, "Restore previous lightmap", "advice");
            string lp = GpuLock.PathFor(work);
            File.WriteAllText(lp, "H2LM BAKE deadrun s 2026-10-14T00:00:00 pid 999999 start 638000000000000000");
            var it2 = StartupRecovery.Scan(work).Where(x => x.Kind == RecoveryKind.StaleOwnLock).ToList();
            Assert.Eq(1, it2.Count, "dead standalone lock reported");
            Assert.True(!StartupRecovery.ReleaseStaleOwnLock(work, new FakeProbe { Alive = true }), "not released while its shell lives");
            Assert.True(StartupRecovery.ReleaseStaleOwnLock(work) && !File.Exists(lp), "released");
            File.WriteAllText(lp, "UI BAKE launcherrun s 2026-10-14T00:00:00 pid 999999");
            Assert.Eq(0, StartupRecovery.Scan(work).Count(x => x.Kind == RecoveryKind.StaleOwnLock), "a launcher / research lock is never ours");
            Assert.True(!StartupRecovery.ReleaseStaleOwnLock(work) && File.Exists(lp), "and never released by recovery");
            File.Delete(lp);
        }

        private static void Aftermath()
        {
            string d = C.Sb.Dir("aftermath");
            string m = Path.Combine(d, StartupRecovery.MarkerName);
            Assert.True(!CancelAftermath.Evaluate(m).MarkerExists, "no marker");
            File.WriteAllText(m, "{\"target_dir\": \"X:\\\\tags\\\\a\", \"backups\": [\"X:\\\\bak1\", \"X:\\\\bak2\"], \"files\": {}}");
            var o = CancelAftermath.Evaluate(m);
            Assert.True(o.MarkerExists && o.IsInstallMarker && o.BackupDir == @"X:\bak1" && o.TargetDir == @"X:\tags\a", "install marker parsed");
            File.WriteAllText(m, "started ...; previous tags: ...");
            var t = CancelAftermath.Evaluate(m);
            Assert.True(t.MarkerExists && !t.IsInstallMarker, "text marker");
        }
    }
}
