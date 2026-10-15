using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>A fake H2EK root with one scenario folder holding a lightmap set (two BSPs), for backup / install / restore.</summary>
    internal sealed class FakeTags
    {
        public string Root, Work, TagDir;
        public TagSet Set;

        public static FakeTags Make(string leaf, bool withLightmap = true)
        {
            var f = new FakeTags();
            f.Root = C.Sb.Dir("troot_" + leaf);
            f.Work = ShellPaths.WorkOf(f.Root);
            Directory.CreateDirectory(f.Work);
            f.TagDir = C.Sb.Dir(@"troot_" + leaf + @"\tags\scenarios\multi\fake");
            File.WriteAllText(Path.Combine(f.TagDir, "fake.scenario"), "scenario v1");
            if (withLightmap)
            {
                File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap"), "ltmp v1");
                File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), "p8 v1");
                File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_truecolor_bitmaps.bitmap"), "truecolor v1");
            }
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_radiance_bitmaps_0.bitmap"), "radiance 0");
            File.WriteAllText(Path.Combine(f.TagDir, "fake_other_lightmap.scenario_structure_lightmap"), "another bsp");
            f.Set = TagSet.For(f.Root, @"scenarios\multi\fake\fake", "fake");
            return f;
        }

        public string Md5(string name) { return SafeFs.Md5File(Path.Combine(TagDir, name)); }

        /// <summary>A direct-writer stage (tagw_report.json status "written" + the files) as lm_tagwriter leaves it.</summary>
        public string MakeStage(string farm, string version, bool firstBakeScenario = false)
        {
            string stage = Path.Combine(Work, farm, "tagw_stage");
            Directory.CreateDirectory(stage);
            var outputs = new JObj();
            var names = new List<string> { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" };
            if (firstBakeScenario) names.Add("fake.scenario");
            foreach (var n in names)
            {
                string p = Path.Combine(stage, n);
                File.WriteAllText(p, n + " " + version);
                outputs[n] = new JObj().Add("md5", SafeFs.Md5File(p)).Add("size", new FileInfo(p).Length);
            }
            File.WriteAllText(Path.Combine(stage, "tagw_report.json"), Json.Write(new JObj().Add("status", "written").Add("outputs", outputs)));
            return stage;
        }
    }

    internal static class SafetyTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("safety.lock: acquire, contention, release only ours, heartbeat", LockBasics);
            yield return T("safety.lock: run_bake stale rule (60 min + no fullgpu/tool_fast) and dead-shell rule", LockStale);
            yield return T("safety.lock: wait gives up after the limit; cancel stops the wait", LockWait);
            yield return T("safety.lock: a run WAITS (status names the holder), then holds the lock until Finish releases it", LockInRun);
            yield return T("safety.workers: 200-299, per scenario|bsp (+|probes), skips existing farms, 149 denied", Workers);
            yield return T("safety.workers: exhaustion and a hand-edited registry pointing at 149 are refused", WorkersRefuse);
            yield return T("safety.guards: stage never under tags, target inside tags, farm_w149 paths refused", GuardsTest);
            yield return T("safety.backup: lightmap set without radiance, md5-verified, source.txt + backup.json", Backup);
            yield return T("safety.backup: first bake backs up the .scenario; LtmpOnly copies only the ltmp", BackupFirstAndLtmp);
            yield return T("safety.install: real lmw_install into a scratch tags folder (bundle py) -> installed", InstallReal);
            yield return T("safety.install: a held target -> exit 2 -> Retry offered, nothing changed", InstallLocked);
            yield return T("safety.install: exit 1 -> restore offered; undo-last-install via lmw_install restore", InstallFailAndUndo);
            yield return T("safety.install: a cancel during the shell's install waits for it (TagWrite)", InstallCancelDeferred);
            yield return T("safety.restore: newest _TAGBAK_ui set restored, current saved aside, first-bake files renamed aside", RestorePrevious);
            yield return T("safety.restore: refused while a target is held; legacy (source.txt only) backups work", RestoreRefusedAndLegacy);
        }

        // ------------------------------------------------------------------ lock

        private static void LockBasics()
        {
            string work = C.Sb.Dir("lockwork");
            string lp = GpuLock.PathFor(work);
            var a = new GpuLock(lp, "runA", @"scenarios\a b\c", new FakeProbe { Alive = true });
            Assert.True(a.TryAcquireOnce(), "A acquires");
            Assert.True(File.ReadAllText(lp).StartsWith("H2LM BAKE runA scenarios\\a_b\\c ", StringComparison.Ordinal), "content: " + File.ReadAllText(lp));
            var info = GpuLock.Inspect(lp);
            Assert.True(info.IsStandalone && info.RunTag == "runA" && info.Pid == ProcessInfo.CurrentPid, "our format parsed");
            var b = new GpuLock(lp, "runB", "x", new FakeProbe { Alive = true });
            Assert.True(!b.TryAcquireOnce(), "B cannot while A holds it");
            Assert.True(!b.Release(), "B cannot release A's lock");
            Assert.True(File.Exists(lp), "A's lock still there");
            File.SetLastWriteTimeUtc(lp, DateTime.UtcNow.AddHours(-3));
            Assert.True(a.Heartbeat(), "heartbeat");
            Assert.True((DateTime.UtcNow - File.GetLastWriteTimeUtc(lp)).TotalMinutes < 1, "heartbeat refreshed the mtime");
            Assert.True(a.Release() && !File.Exists(lp), "A releases");
            File.WriteAllText(lp, "UI BAKE 20261008_011948_cyclotron_cyclotron scenarios\\multi\\cyclotron\\cyclotron 2026-10-08T01:19:48 pid 1234");
            Assert.True(!a.Release(), "a launcher lock is never released by us");
            Assert.True(File.Exists(lp), "launcher lock kept");
            Assert.True(!GpuLock.Inspect(lp).IsStandalone, "launcher format is not ours");
            File.Delete(lp);
        }

        private static void LockStale()
        {
            string work = C.Sb.Dir("lockstale");
            string lp = GpuLock.PathFor(work);
            File.WriteAllText(lp, "UI BAKE oldrun scen 2026-10-08T01:19:48 pid 1234");
            File.SetLastWriteTimeUtc(lp, DateTime.UtcNow.AddMinutes(-61));
            var info = GpuLock.Inspect(lp);
            Assert.True(!GpuLock.IsStale(info, new FakeProbe { Busy = true }, DateTime.UtcNow), "old but fullgpu/tool_fast running: NOT stale");
            Assert.True(GpuLock.IsStale(info, new FakeProbe { Busy = false }, DateTime.UtcNow), "old and nothing running: stale");
            File.SetLastWriteTimeUtc(lp, DateTime.UtcNow.AddMinutes(-59));
            Assert.True(!GpuLock.IsStale(GpuLock.Inspect(lp), new FakeProbe { Busy = false }, DateTime.UtcNow), "59 min: not stale");
            File.SetLastWriteTimeUtc(lp, DateTime.UtcNow.AddMinutes(-61));
            var logs = new List<string>();
            var g = new GpuLock(lp, "runC", "s", new FakeProbe { Busy = false, Alive = true });
            g.Log += m => logs.Add(m);
            Assert.Eq(LockAcquire.Acquired, g.Acquire(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), CancellationToken.None), "stale lock replaced");
            Assert.Contains(logs.Single(), "removed a stale GPU lock", "logged");
            Assert.True(g.IsOurs(), "ours now");
            g.Release();
            // a standalone lock whose shell is gone is stale at once (its job object already killed the bake tree)
            File.WriteAllText(lp, "H2LM BAKE deadrun s 2026-10-14T00:00:00 pid 999999 start 638000000000000000");
            Assert.True(GpuLock.IsStale(GpuLock.Inspect(lp), new FakeProbe { Busy = true, Alive = false }, DateTime.UtcNow), "dead standalone shell: stale even with tools running");
            Assert.True(!GpuLock.IsStale(GpuLock.Inspect(lp), new FakeProbe { Busy = true, Alive = true }, DateTime.UtcNow), "live standalone shell: not stale");
            File.Delete(lp);
        }

        private static void LockWait()
        {
            string work = C.Sb.Dir("lockwait");
            string lp = GpuLock.PathFor(work);
            File.WriteAllText(lp, "RESEARCH fullgpu holder");
            var g = new GpuLock(lp, "runD", "s", new FakeProbe { Busy = true });
            int waits = 0;
            string holder = null;
            var r = g.Acquire(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), CancellationToken.None, (h, w) => { waits++; holder = h; });
            Assert.Eq(LockAcquire.TimedOut, r, "gave up");
            Assert.True(waits >= 2 && holder == "RESEARCH fullgpu holder", "reported the holder while waiting");
            Assert.Eq("RESEARCH fullgpu holder", File.ReadAllText(lp), "the foreign lock is untouched");
            var cts = new CancellationTokenSource(200);
            Assert.Eq(LockAcquire.Cancelled, g.Acquire(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), cts.Token), "cancel stops the wait");
            File.Delete(lp);
            Assert.Eq(LockAcquire.Acquired, g.Acquire(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), CancellationToken.None), "free lock acquired");
            g.Release();
        }

        private static void LockInRun()
        {
            string work = C.Sb.Dir(@"lockrun\GPU Lightmap Work");
            string lp = GpuLock.PathFor(work);
            File.WriteAllText(lp, "RESEARCH BAKE tie_A scenarios\\solo\\x 2026-10-14T07:00:00 pid 4242");
            var run = BakeRun.Start(new RunSpec { Work = work, ScenarioRel = @"scenarios\multi\fake\fake", Bsp = "fake", Worker = 205 });
            var lk = new GpuLock(lp, run.RunTag, @"scenarios\multi\fake\fake", new FakeProbe { Busy = true, Alive = true });
            string seen = null;
            run.StatusChanged += s => { if (s.State == RunState.Waiting && seen == null) { seen = s.WaitingFor; ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(150); File.Delete(lp); }); } };
            var r = run.AcquireGpuLock(lk, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
            Assert.Eq(LockAcquire.Acquired, r, "acquired once the holder released it");
            Assert.Eq("GPU lock held by RESEARCH BAKE tie_A scenarios\\solo\\x 2026-10-14T07:00:00 pid 4242", seen, "status.json waitingFor named the holder");
            Assert.Eq(RunState.Running, RunStatus.Load(run.RunDir).State, "RUNNING after the wait");
            Assert.True(lk.IsOurs(), "the lock names this run");
            run.Finish(RunState.Done);
            Assert.True(!File.Exists(lp), "Finish released our lock");
            string log = Json.ReadAllTextShared(run.LogPath);
            Assert.Contains(log, "waiting for the GPU lock held by: RESEARCH BAKE tie_A", "logged");
            Assert.Contains(log, "GPU lock released", "release logged");
        }

        // ------------------------------------------------------------------ workers

        private static void Workers()
        {
            string work = C.Sb.Dir("wwork");
            var reg = new WorkerRegistry(work);
            Assert.Eq(200, reg.GetOrAllocate(@"scenarios\multi\cyclotron\cyclotron", "cyclotron"), "first = 200");
            Assert.Eq(200, reg.GetOrAllocate("scenarios/multi/cyclotron/CYCLOTRON.scenario", "Cyclotron"), "same key (separators, case, extension)");
            Assert.Eq(201, reg.GetOrAllocate(@"scenarios\multi\cyclotron\cyclotron", "cyclotron", probes: true), "probe worker is separate");
            Directory.CreateDirectory(Path.Combine(work, "farm_w202"));
            Directory.CreateDirectory(Path.Combine(work, "_gpulm_skel", "w203"));
            Assert.Eq(204, reg.GetOrAllocate(@"scenarios\multi\headlong\headlong", "headlong"), "existing farm_w202 / skel w203 skipped");
            var reg2 = new WorkerRegistry(work);
            Assert.Eq(204, reg2.Find(@"scenarios\multi\headlong\headlong", "headlong").Value, "persisted");
            Assert.True(reg2.Find(@"scenarios\x\y", "z") == null, "unknown key");
            var j = Json.ReadObjFile(reg.FilePath);
            Assert.Eq(200L, j[@"scenarios\multi\cyclotron\cyclotron|cyclotron"], "flat JSON map, canonical key");
            Assert.Eq(201L, j[@"scenarios\multi\cyclotron\cyclotron|cyclotron|probes"], "probe key");
            Assert.True(!Guards.WorkerAllowed(149) && Guards.WorkerAllowed(200) && !Guards.WorkerAllowed(300) && !Guards.WorkerAllowed(60), "range + 149");
            Assert.Throws<SafetyException>(() => Guards.AssertWorker(149), "149 denied");
            Assert.Throws<SafetyException>(() => reg.FarmDir(149), "no farm dir for 149");
            Assert.Throws<ArgumentException>(() => new WorkerRegistry(work, 140, 160), "a range reaching outside 200-299 is refused");
            Assert.Eq(Path.Combine(work, "farm_w200"), reg.FarmDir(200), "farm dir name");
        }

        private static void WorkersRefuse()
        {
            string work = C.Sb.Dir("wwork2");
            var reg = new WorkerRegistry(work, 200, 202);
            reg.GetOrAllocate("s\\a", "a"); reg.GetOrAllocate("s\\b", "b"); reg.GetOrAllocate("s\\c", "c");
            Assert.Throws<SafetyException>(() => reg.GetOrAllocate("s\\d", "d"), "exhausted");
            File.WriteAllText(reg.FilePath, "{\"s\\\\x|x\": 149}");
            Assert.Throws<SafetyException>(() => reg.ReadAll(), "a registry entry of 149 is refused");
        }

        private static void GuardsTest()
        {
            string root = @"X:\H2EK", work = @"X:\H2EK\GPU Lightmap Work";
            Assert.Throws<SafetyException>(() => Guards.AssertStageNotUnderTags(@"X:\H2EK\tags\scenarios\stage", root), "stage under tags");
            Guards.AssertStageNotUnderTags(@"X:\H2EK\GPU Lightmap Work\farm_w200\tagw_stage", root);
            Assert.Throws<SafetyException>(() => Guards.AssertTargetUnderTags(@"X:\H2EK\GPU Lightmap Work\x", root), "target outside tags");
            Assert.Throws<SafetyException>(() => Guards.AssertTargetUnderTags(@"X:\H2EK\tags", root), "the tags root itself");
            Assert.Throws<SafetyException>(() => Guards.AssertTargetUnderTags(@"X:\H2EK\tagsX\a", root), "a sibling with a common prefix");
            Guards.AssertTargetUnderTags(@"X:\H2EK\tags\scenarios\multi\cyclotron", root);
            Assert.Throws<SafetyException>(() => Guards.AssertNotDeniedFarm(@"X:\H2EK\GPU Lightmap Work\farm_w149\tagw_stage", work), "farm_w149");
            Assert.Throws<SafetyException>(() => Guards.AssertNotDeniedFarm(@"X:\H2EK\GPU Lightmap Work\_gpulm_skel\w149", work), "skel w149");
            Guards.AssertNotDeniedFarm(@"X:\H2EK\GPU Lightmap Work\farm_w1490", work);
        }

        // ------------------------------------------------------------------ backup

        private static void Backup()
        {
            var f = FakeTags.Make("bk");
            var b = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 12, 0, 0));
            Assert.Eq(Path.Combine(f.Work, "_TAGBAK_ui", "fake_fake_20261014_120000"), b.Dir, "folder name");
            Assert.True(!b.FirstBake, "not a first bake");
            Assert.Eq("fake_fake_lightmap.scenario_structure_lightmap,fake_fake_lightmap_bitmaps.bitmap,fake_fake_lightmap_truecolor_bitmaps.bitmap",
                string.Join(",", b.Files.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal)), "the set without radiance, without the other BSP");
            foreach (var x in b.Files) Assert.Eq(f.Md5(x.Key), SafeFs.Md5File(Path.Combine(b.Dir, x.Key)), x.Key + " verified");
            Assert.Eq(1, b.RadianceMd5.Count, "radiance md5 recorded");
            Assert.Eq(f.TagDir + "\r\nfake_fake_lightmap", File.ReadAllText(Path.Combine(b.Dir, "source.txt")), "launcher-compatible source.txt");
            var j = Json.ReadObjFile(Path.Combine(b.Dir, "backup.json"));
            Assert.Eq("fake_fake_lightmap", j.Str("prefix"), "backup.json prefix");
            Assert.Eq(4, j.Arr("existedNames").Count, "existed names incl. radiance");
            var b2 = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 12, 0, 0));
            Assert.Eq(b.Dir + "_2", b2.Dir, "same second -> unique folder");
            Assert.Eq(0, TagBackup.ChangedRadiance(b).Count, "radiance unchanged");
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_radiance_bitmaps_0.bitmap"), "radiance CHANGED");
            Assert.Eq("fake_fake_lightmap_radiance_bitmaps_0.bitmap", TagBackup.ChangedRadiance(b).Single(), "radiance change detected");
        }

        private static void BackupFirstAndLtmp()
        {
            var f = FakeTags.Make("bk1", withLightmap: false);
            var b = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, DateTime.Now);
            Assert.True(b.FirstBake, "first bake");
            Assert.Eq("fake.scenario", b.Files.Single().Key, "only the scenario");
            var g = FakeTags.Make("bk2");
            var l = TagBackup.Create(g.Work, g.Set, BackupKind.LtmpOnly, DateTime.Now);
            Assert.Eq("fake_fake_lightmap.scenario_structure_lightmap", l.Files.Single().Key, "LtmpOnly");
        }

        // ------------------------------------------------------------------ install

        private static InstallRequest Req(FakeTags f, string stage, string runDir, string lmw = null)
        {
            return new InstallRequest
            {
                PyExe = C.PyExe, LmwInstallPy = lmw ?? C.FxLmwInstall, Root = f.Root, Work = f.Work, Stage = stage, TargetDir = f.TagDir,
                BackupDir = Path.Combine(runDir, "tag_backup_before_writeback"), AutoBackupRoot = Path.Combine(f.Work, "_TAGBAK_auto"),
                Marker = Path.Combine(Path.GetDirectoryName(stage), StartupRecovery.MarkerName),
                Env = BakeEnv.Build(new H2Lm.Proc.BakeEnvSpec { PwshDir = C.PwshDir, PyDir = C.PyDir, Root = f.Root, Work = f.Work }),
            };
        }

        private static void InstallReal()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("inst");
            string stage = f.MakeStage("farm_w201", "v2");
            string runDir = C.Sb.Dir(@"troot_inst\run1");
            string oldLtmp = f.Md5("fake_fake_lightmap.scenario_structure_lightmap");
            var r = TagInstaller.Install(Req(f, stage, runDir));
            Assert.Eq(InstallOutcome.Installed, r.Outcome, "installed: " + r.Message + " / " + string.Join(" / ", r.Lines));
            Assert.Eq(SafeFs.Md5File(Path.Combine(stage, "fake_fake_lightmap.scenario_structure_lightmap")), f.Md5("fake_fake_lightmap.scenario_structure_lightmap"), "live ltmp = staged");
            Assert.Eq(oldLtmp, SafeFs.Md5File(Path.Combine(runDir, "tag_backup_before_writeback", "fake_fake_lightmap.scenario_structure_lightmap")), "old ltmp backed up");
            Assert.True(!File.Exists(Path.Combine(f.Work, "farm_w201", StartupRecovery.MarkerName)), "marker gone (renamed .done_)");
            Assert.True(Directory.GetDirectories(Path.Combine(f.Work, "_TAGBAK_auto")).Length == 1, "auto backup made");
            Assert.Eq("another bsp", File.ReadAllText(Path.Combine(f.TagDir, "fake_other_lightmap.scenario_structure_lightmap")), "the other BSP untouched");
            Assert.True(r.Lines.Any(l => l.StartsWith("INSTALL: installed and verified", StringComparison.Ordinal)), "lmw_install's own output captured");
            Assert.Throws<SafetyException>(() => TagInstaller.Install(Req(f, f.TagDir, runDir)), "a stage under ROOT\\tags is refused before running");
            var bad = Req(f, stage, runDir); bad.TargetDir = f.Work;
            Assert.Throws<SafetyException>(() => TagInstaller.Install(bad), "a target outside ROOT\\tags is refused");
        }

        private static void InstallLocked()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("lock");
            string stage = f.MakeStage("farm_w202", "v2");
            string runDir = C.Sb.Dir(@"troot_lock\run1");
            string before = f.Md5("fake_fake_lightmap_bitmaps.bitmap");
            InstallResult r;
            using (new FileStream(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), FileMode.Open, FileAccess.Read, FileShare.None))
                r = TagInstaller.Install(Req(f, stage, runDir));
            Assert.Eq(InstallOutcome.Refused, r.Outcome, "refused: " + r.Message);
            Assert.True(r.OfferRetry && !r.OfferRestore, "Retry offered");
            Assert.Contains(r.Message, "Retry", "message");
            Assert.Eq(before, f.Md5("fake_fake_lightmap_bitmaps.bitmap"), "nothing changed");
            Assert.True(Directory.Exists(stage) && File.Exists(Path.Combine(stage, "tagw_report.json")), "the stage is kept");
            var r2 = TagInstaller.Install(Req(f, stage, runDir));
            Assert.Eq(InstallOutcome.Installed, r2.Outcome, "Retry works once the file is free");
        }

        private static void InstallFailAndUndo()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("fail");
            string stage = f.MakeStage("farm_w203", "v2");
            string runDir = C.Sb.Dir(@"troot_fail\run1");
            var r = TagInstaller.Install(Req(f, stage, runDir, C.Fixture("fake_lmw_install_exit1.py")));
            Assert.Eq(InstallOutcome.FailedAfterChange, r.Outcome, "exit 1 mapped");
            Assert.True(r.OfferRestore && !r.OfferRetry, "restore offered");
            // a real install, then "Undo last install" = lmw_install restore from the run's tag_backup_before_writeback
            var before = new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" }.ToDictionary(n => n, f.Md5);
            var ok = TagInstaller.Install(Req(f, stage, runDir));
            Assert.Eq(InstallOutcome.Installed, ok.Outcome, "installed");
            var u = TagInstaller.Restore(new TagRestoreRequest { PyExe = C.PyExe, LmwInstallPy = C.FxLmwInstall, Root = f.Root, Work = f.Work,
                BackupDir = ok.BackupDir, TargetDir = f.TagDir, Env = Req(f, stage, runDir).Env });
            Assert.Eq(InstallOutcome.Installed, u.Outcome, "restore exit 0: " + u.Message);
            foreach (var kv in before) Assert.Eq(kv.Value, f.Md5(kv.Key), kv.Key + " back to the original");
            Assert.True(Directory.GetFiles(f.TagDir, "*.gpulm_replaced_*").Length == 3, "lmw_install kept copies of what it restored over");
        }

        private static void InstallCancelDeferred()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("cx");
            string stage = f.MakeStage("farm_w204", "v2");
            var run = BakeRun.Start(new RunSpec { Work = f.Work, ScenarioRel = f.Set.ScenarioRel, Bsp = "fake", Worker = 204 });
            bool deferred = false;
            run.Line += l => { if (l.Text.StartsWith("INSTALL: replacing", StringComparison.Ordinal)) ThreadPool.QueueUserWorkItem(_ => deferred = run.RequestCancel()); };
            var sw = Stopwatch.StartNew();
            var res = TagInstaller.Install(Req(f, stage, run.RunDir, C.Fixture("fake_lmw_install_slow.py")), run);
            Assert.True(deferred, "deferred while the install runs");
            Assert.Eq(InstallOutcome.Installed, res.Outcome, "the install was allowed to finish");
            Assert.True(sw.Elapsed.TotalSeconds >= 3, "it ran its 3 s");
            Assert.True(run.CancelRequested, "the cancel is still pending for the orchestrator");
            Assert.Throws<OperationCanceledException>(() => run.ThrowIfCancelled(), "the next step does not start");
            Assert.Contains(Json.ReadAllTextShared(run.LogPath), "INSTALL: installed and verified (fake)", "logged to bake.log");
            run.Finish(RunState.Cancelled);
        }

        // ------------------------------------------------------------------ restore

        private static void RestorePrevious()
        {
            var f = FakeTags.Make("rs", withLightmap: false);
            // first bake: the backup holds only the .scenario; the bake then creates the lightmap set and edits the scenario
            var b0 = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 10, 0, 0));
            File.WriteAllText(Path.Combine(f.TagDir, "fake.scenario"), "scenario v2 (references the lightmap)");
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap"), "ltmp v2");
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), "p8 v2");
            var cands = TagRestore.Candidates(f.Work, f.Set);
            Assert.Eq(b0.Dir, cands.First().Dir, "the newest set");
            var r = TagRestore.Restore(f.Work, f.Set, TagRestore.Newest(f.Work, f.Set), new DateTime(2026, 10, 14, 11, 0, 0));
            Assert.Eq(RestoreOutcome.Restored, r.Outcome, "restored: " + r.Message);
            Assert.Eq("scenario v1", File.ReadAllText(Path.Combine(f.TagDir, "fake.scenario")), "scenario restored");
            Assert.True(!File.Exists(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap")), "the first-bake ltmp is no longer live");
            Assert.True(File.Exists(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap.gpulm_removed_20261014_110000")), "... it was renamed aside, not deleted");
            Assert.Eq(2, r.RenamedAside.Count, "ltmp + p8 renamed aside");
            Assert.True(r.SavedAsideDir != null && r.SavedAsideDir.Contains("_before_restore_20261014_110000"), "current files saved aside");
            Assert.Eq("ltmp v2", File.ReadAllText(Path.Combine(r.SavedAsideDir, "fake_fake_lightmap.scenario_structure_lightmap")), "saved-aside content");
            Assert.Eq("radiance 0", File.ReadAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_radiance_bitmaps_0.bitmap")), "radiance untouched");
            Assert.True(!TagRestore.Candidates(f.Work, f.Set).Any(c => c.Dir == r.SavedAsideDir), "a _before_restore_ folder is never a restore candidate");
            // a normal (non-first) bake backup restores the set
            var g = FakeTags.Make("rs2");
            TagBackup.Create(g.Work, g.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));
            File.WriteAllText(Path.Combine(g.TagDir, "fake_fake_lightmap_truecolor_bitmaps.bitmap"), "truecolor BROKEN");
            var r2 = TagRestore.Restore(g.Work, g.Set, TagRestore.Newest(g.Work, g.Set), DateTime.Now);
            Assert.Eq(RestoreOutcome.Restored, r2.Outcome, "restored");
            Assert.Eq("truecolor v1", File.ReadAllText(Path.Combine(g.TagDir, "fake_fake_lightmap_truecolor_bitmaps.bitmap")), "truecolor back");
            Assert.Eq(0, r2.RenamedAside.Count, "nothing renamed aside");
            Assert.True(Directory.GetFiles(g.TagDir, "*.gpulm_new").Length == 0, "no temp left");
        }

        private static void RestoreRefusedAndLegacy()
        {
            var f = FakeTags.Make("rr");
            TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), "p8 v2");
            RestoreResult r;
            using (new FileStream(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), FileMode.Open, FileAccess.Read, FileShare.None))
                r = TagRestore.Restore(f.Work, f.Set, TagRestore.Newest(f.Work, f.Set), DateTime.Now);
            Assert.Eq(RestoreOutcome.Refused, r.Outcome, "refused while held");
            Assert.Eq("p8 v2", File.ReadAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap")), "nothing changed");
            Assert.True(r.SavedAsideDir == null, "nothing saved aside either");
            // a damaged backup copy is refused before any change
            var nb = TagRestore.Newest(f.Work, f.Set);
            File.WriteAllText(Path.Combine(nb.Dir, "fake_fake_lightmap_bitmaps.bitmap"), "rotted");
            Assert.Eq(RestoreOutcome.Failed, TagRestore.Restore(f.Work, f.Set, nb, DateTime.Now).Outcome, "md5 mismatch refused");
            Assert.Eq("p8 v2", File.ReadAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap")), "still unchanged");
            // the PowerShell launcher's backups (source.txt only, other files alongside) are restorable
            var g = FakeTags.Make("legacy");
            string legacy = Path.Combine(g.Work, "_TAGBAK_ui", "fake_fake_20261013_172118_before_ao_s05");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "fake_fake_lightmap.scenario_structure_lightmap"), "ltmp OLD");
            File.WriteAllText(Path.Combine(legacy, "copy_md5.txt"), "not a tag");
            File.WriteAllText(Path.Combine(legacy, "source.txt"), g.TagDir + "\r\nfake_fake_lightmap");
            string otherBsp = Path.Combine(g.Work, "_TAGBAK_ui", "fake_fake_20261014_000000");
            Directory.CreateDirectory(otherBsp);
            File.WriteAllText(Path.Combine(otherBsp, "source.txt"), g.TagDir + "\r\nfake_fake_x_lightmap");   // a colliding name of another BSP
            File.WriteAllText(Path.Combine(otherBsp, "fake_fake_x_lightmap.scenario_structure_lightmap"), "x");
            var c = TagRestore.Newest(g.Work, g.Set);
            Assert.Eq(legacy, c.Dir, "legacy set found; the colliding other-BSP folder is skipped");
            Assert.Eq("fake_fake_lightmap.scenario_structure_lightmap", c.Files.Single().Key, "only tag files");
            var lr = TagRestore.Restore(g.Work, g.Set, c, DateTime.Now);
            Assert.Eq(RestoreOutcome.Restored, lr.Outcome, "restored: " + lr.Message);
            Assert.Eq("ltmp OLD", File.ReadAllText(Path.Combine(g.TagDir, "fake_fake_lightmap.scenario_structure_lightmap")), "legacy content back");
            Assert.Eq("p8 v1", File.ReadAllText(Path.Combine(g.TagDir, "fake_fake_lightmap_bitmaps.bitmap")), "files not in the legacy set are left as they are");
        }
    }
}
