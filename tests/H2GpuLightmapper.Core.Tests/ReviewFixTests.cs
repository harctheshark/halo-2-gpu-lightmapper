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
    /// <summary>The review-fix step: live markers never offered, the tag-write gate, unfinished backups, no-op restores,
    /// the restore marker + its recovery, markers cleared by a full restore.</summary>
    internal static class ReviewFixTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("fix.recovery: a live run's farm markers are InUse; a live GPU lock marks every marker; RestoreRequestFor refuses them", LiveMarkers);
            yield return T("fix.gate: TagWriteGate refuses during a live run / a live lock holder, holds the lock, releases it; a stale lock is cleared", Gate);
            yield return T("fix.backup: an unfinished backup (.tmp_, no metadata) is never a candidate; a legacy one is unverified", UnfinishedBackup);
            yield return T("fix.restore: Newest skips a backup equal to the live set; a no-op restore says so; installedAfter; ByName", NoOpRestore);
            yield return T("fix.restore: marker before the first replace (.done_ after); an interrupted restore rolls back via lmw_install", RestoreMarker);
            yield return T("fix.restore: a full restore clears this set's write-back markers (text + JSON), never another set's; SetFor", ClearMarkers);
            yield return T("R1 (V6 K2): a tool write-back marker whose set is UNCHANGED is set aside (.verified_), nothing restored; any difference keeps it", ToolMarkerUnchanged);
            yield return T("R1 (V6 K2): a first-bake tool write-back marker (0 files) is set aside only while the set is still empty", ToolMarkerUnchangedFirstBake);
        }

        private const string ToolText = "started 10/14/2026 02:00:00; previous tags: {0} (3 files from {1}\\{2}*). Copy them back if the new tag is broken.";
        private const string ToolTextN = "started 10/14/2026 09:05:00; previous tags: {0} ({3} files from {1}\\{2}*). Copy them back if the new tag is broken.";

        /// <summary>gpu_bake's [4] exactly: copy &lt;prefix&gt;* into the farm's tag_backup_before_writeback, then write the text marker.</summary>
        private static string ToolWriteBackStart(FakeTags f, int w, DateTime at)
        {
            string farm = Farm(f.Work, w), bak = Path.Combine(farm, "tag_backup_before_writeback");
            var files = Directory.GetFiles(f.TagDir, f.Set.Prefix + "*");
            if (files.Length > 0)
            {
                Directory.CreateDirectory(bak);
                foreach (var x in files) File.Copy(x, Path.Combine(bak, Path.GetFileName(x)), true);
            }
            string marker = Path.Combine(farm, StartupRecovery.MarkerName);
            File.WriteAllText(marker, string.Format(ToolTextN, bak, f.TagDir, f.Set.Prefix, files.Length));
            File.SetLastWriteTime(marker, at);
            return marker;
        }

        private static StartupRecovery.UnchangedResult Check(FakeTags f, int w)
        {
            var it = StartupRecovery.Scan(f.Work).Single(i => i.Worker == w);
            return StartupRecovery.ClearToolMarkerIfUnchanged(f.Work, f.Root, it, new DateTime(2026, 10, 14, 9, 30, 0));
        }

        private static void ToolMarkerUnchanged()
        {
            var f = FakeTags.Make("fx7");
            var b0 = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 8, 0, 0));      // S0, then an install made S1
            foreach (var n in new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" })
                File.WriteAllText(Path.Combine(f.TagDir, n), n + " S1");
            TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));               // this bake's backup (= S1)
            string marker = ToolWriteBackStart(f, 202, new DateTime(2026, 10, 14, 9, 5, 0));                            // killed before the tool saved
            var it = StartupRecovery.Scan(f.Work).Single(i => i.Worker == 202);
            Assert.Eq(4, it.BackupFileCount ?? -1, "the marker's file count is parsed (ltmp, P8, truecolor, radiance)");
            Assert.Eq(b0.Dir, TagRestore.Newest(f.Work, f.Set).Dir, "why the check comes first: Newest skips the equal backup and would restore the OLDER S0");
            var live = SetMd5(f);

            // a live file that differs (the tool saved) -> kept
            string ltmp = Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap");
            File.WriteAllText(ltmp, "ltmp SAVED BY THE TOOL");
            var d = Check(f, 202);
            Assert.True(!d.Cleared && d.Message.Contains("differs") && File.Exists(marker), "a differing file keeps the marker: " + d.Message);
            File.WriteAllText(ltmp, "fake_fake_lightmap.scenario_structure_lightmap S1");
            // a set file the backup lacks (the tool created it) -> kept
            string extra = Path.Combine(f.TagDir, "fake_fake_lightmap_extra_bitmaps.bitmap");
            File.WriteAllText(extra, "created");
            var e = Check(f, 202);
            Assert.True(!e.Cleared && e.Message.Contains("does not") && File.Exists(marker), "a created file keeps the marker: " + e.Message);
            File.Delete(extra);
            // the backup folder is not the one the marker counted -> kept
            string stray = Path.Combine(Path.GetDirectoryName(marker), "tag_backup_before_writeback", "stray.txt");
            File.WriteAllText(stray, "x");
            var s = Check(f, 202);
            Assert.True(!s.Cleared && s.Message.Contains("holds 5 file(s)") && File.Exists(marker), "a count mismatch keeps the marker: " + s.Message);
            File.Delete(stray);
            // unchanged -> set aside, nothing restored
            var ok = Check(f, 202);
            Assert.True(ok.Cleared, "unchanged: " + ok.Message);
            Assert.Eq(marker + ".verified_20261014_093000", ok.MovedTo, "renamed .verified_<ts>");
            Assert.True(!File.Exists(marker) && File.Exists(ok.MovedTo), "never deleted");
            Assert.Contains(ok.Message, "nothing to restore", "says so");
            foreach (var kv in live) Assert.Eq(kv.Value, f.Md5(kv.Key), kv.Key + " untouched (S1 stays; S0 is NOT restored)");
            Assert.Eq(0, StartupRecovery.Scan(f.Work).Count(i => i.Kind == RecoveryKind.ToolWriteBackMarker), "no marker any more: the bake is allowed");

            // a backup NEWER than the marker -> kept (restore by hand)
            var g = FakeTags.Make("fx7b");
            string m2 = ToolWriteBackStart(g, 203, new DateTime(2026, 10, 14, 9, 5, 0));
            TagBackup.Create(g.Work, g.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 20, 0));
            var n2 = Check(g, 203);
            Assert.True(!n2.Cleared && n2.Message.Contains("newer than the marker") && File.Exists(m2), "a newer backup keeps the marker: " + n2.Message);
        }

        private static void ToolMarkerUnchangedFirstBake()
        {
            var f = FakeTags.Make("fx8", false);
            File.Move(Path.Combine(f.TagDir, "fake_fake_lightmap_radiance_bitmaps_0.bitmap"), Path.Combine(f.TagDir, "radiance.moved_by_test"));   // a BSP never lightmapped
            TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));               // first bake: the .scenario
            string marker = ToolWriteBackStart(f, 204, new DateTime(2026, 10, 14, 9, 5, 0));
            Assert.Eq(0, StartupRecovery.Scan(f.Work).Single(i => i.Worker == 204).BackupFileCount ?? -1, "0 files");
            string ltmp = Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap");
            File.WriteAllText(ltmp, "created by the tool");
            var c = Check(f, 204);
            Assert.True(!c.Cleared && File.Exists(marker), "the tool created the ltmp: kept: " + c.Message);
            File.Move(ltmp, ltmp + ".moved_by_test");
            File.WriteAllText(f.Set.ScenarioFile, "scenario CHANGED by the tool");
            var sc = Check(f, 204);
            Assert.True(!sc.Cleared && sc.Message.Contains("differs from the backup") && File.Exists(marker), "the .scenario changed (first bake backup): kept: " + sc.Message);
            File.WriteAllText(f.Set.ScenarioFile, "scenario v1");
            var ok = Check(f, 204);
            Assert.True(ok.Cleared && !File.Exists(marker), "still empty + .scenario unchanged: set aside: " + ok.Message);
        }

        private static Dictionary<string, string> SetMd5(FakeTags f)
        {
            return Directory.GetFiles(f.TagDir).Select(Path.GetFileName).ToDictionary(n => n, f.Md5, StringComparer.OrdinalIgnoreCase);
        }

        private static string Farm(string work, int w) { string d = Path.Combine(work, "farm_w" + w); Directory.CreateDirectory(d); return d; }

        private static void LiveMarkers()
        {
            string work = C.Sb.Dir(@"fx1\GPU Lightmap Work");
            foreach (var w in new[] { 205, 206, 207 })
                File.WriteAllText(Path.Combine(Farm(work, w), StartupRecovery.MarkerName), string.Format(ToolText, @"X:\bak", @"X:\tags\a", "b_c_lightmap"));
            string jm = Path.Combine(Farm(work, 208), StartupRecovery.MarkerName);
            File.WriteAllText(jm, Json.Write(new JObj().Add("started", "x").Add("target_dir", @"X:\tags\a").Add("backups", new List<object> { @"X:\bak2" })
                .Add("files", new JObj().Add("b_c_lightmap.scenario_structure_lightmap", new JObj().Add("state", "pending")))));
            // a run of THIS process (lightmap worker 205, probe worker 206) is live
            var run = BakeRun.Start(new RunSpec { Work = work, ScenarioRel = @"scenarios\a\b", Bsp = "c", Worker = 205 });
            run.Status.Extra["plan"] = new JObj().Add("probeWorker", 206L);
            run.Status.Save();
            var items = StartupRecovery.Scan(work, new FakeProbe { Alive = true });
            Func<int, RecoveryItem> at = w => items.Single(i => i.Worker == w);
            Assert.True(at(205).InUse && at(206).InUse, "the live run's lightmap + probe farms are in use");
            Assert.True(!at(207).InUse && !at(208).InUse, "other farms are not");
            Assert.Eq(0, items.Count(i => i.Kind == RecoveryKind.InterruptedRun), "a live run is never 'interrupted'");
            Assert.Eq(@"X:\tags\a", at(207).TargetDir, "text marker: tag folder parsed");
            Assert.Eq("b_c_lightmap", at(207).Prefix, "text marker: prefix parsed");
            Assert.Eq("b_c_lightmap", at(208).Prefix, "JSON marker: prefix from its files");
            run.Finish(RunState.Done);
            // a live holder of the GPU lock (research / launcher / another window) -> every marker is in use
            string lp = GpuLock.PathFor(work);
            File.WriteAllText(lp, "RESEARCH bake by another workflow");
            items = StartupRecovery.Scan(work, new FakeProbe { Alive = true, Busy = true });
            Assert.True(items.Where(i => i.Kind != RecoveryKind.StaleOwnLock).All(i => i.InUse), "all markers in use while the lock is held: " + string.Join(" | ", items));
            var j = items.Single(i => i.Worker == 208);
            Assert.Throws<SafetyException>(() => StartupRecovery.RestoreRequestFor(j, @"X:\", work), "an in-use marker is never restored");
            Assert.True(StartupRecovery.Scan(work, new FakeProbe { Alive = true, Busy = true }, "RESEARCH bake by another workflow").All(i => !i.InUse), "the caller's own lease does not count");
            File.Delete(lp);
            Assert.True(StartupRecovery.Scan(work, new FakeProbe { Alive = true }).All(i => !i.InUse), "nothing running: nothing in use");
        }

        private static void Gate()
        {
            string work = C.Sb.Dir(@"fx2\GPU Lightmap Work");
            string lp = GpuLock.PathFor(work);
            using (var lease = TagWriteGate.Enter(work, "the test restore"))
            {
                Assert.True(File.Exists(lp) && Json.ReadAllTextShared(lp).Trim() == lease.LockContent, "the lease holds the shared GPU lock");
                Assert.True(TagWriteGate.Blocked(work, "x") != null, "blocked while held");
                Assert.Throws<SafetyException>(() => TagWriteGate.Enter(work, "a second restore"), "a second tag write is refused");
            }
            Assert.True(!File.Exists(lp), "released");
            var run = BakeRun.Start(new RunSpec { Work = work, ScenarioRel = @"scenarios\a\b", Bsp = "c", Worker = 211 });
            var e = Assert.Throws<SafetyException>(() => TagWriteGate.Enter(work, "restore previous lightmap"), "refused while a bake runs");
            Assert.Contains(e.Message, "a bake is running", "reason");
            run.Finish(RunState.Done);
            File.WriteAllText(lp, "OLD launcher lock");
            File.SetLastWriteTimeUtc(lp, DateTime.UtcNow.AddHours(-2));
            using (TagWriteGate.Enter(work, "x", null, new FakeProbe { Busy = false })) Assert.True(Json.ReadAllTextShared(lp).StartsWith("H2LM BAKE tagwrite-", StringComparison.Ordinal), "a stale lock was cleared and taken");
            Assert.True(!File.Exists(lp), "released again");
            File.WriteAllText(lp, "fresh launcher lock");
            Assert.Throws<SafetyException>(() => TagWriteGate.Enter(work, "x", null, new FakeProbe { Busy = true }), "a live foreign holder refuses");
            Assert.Eq("fresh launcher lock", File.ReadAllText(lp), "and its lock is untouched");
            File.Delete(lp);
        }

        private static void UnfinishedBackup()
        {
            var f = FakeTags.Make("fx3");
            var b = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 12, 0, 0));
            string ui = TagBackup.UiRoot(f.Work);
            Assert.True(!Directory.GetDirectories(ui).Any(d => d.Contains(TagBackup.TmpMarker)), "no .tmp_ folder left by a finished backup");
            Assert.True(File.Exists(Path.Combine(b.Dir, "backup.json")) && Json.ReadObjFile(Path.Combine(b.Dir, "backup.json")).Bool("installedAfter") == false, "backup.json, installedAfter false");
            string partial = Path.Combine(ui, "fake_fake_20261014_130000");          // a copy that died (disk full / power cut): no metadata
            Directory.CreateDirectory(partial);
            File.WriteAllText(Path.Combine(partial, "fake_fake_lightmap.scenario_structure_lightmap"), "trunc");
            string tmp = Path.Combine(ui, "fake_fake_20261014_140000" + TagBackup.TmpMarker + "abcd1234");   // still being written
            Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "fake_fake_lightmap.scenario_structure_lightmap"), "half");
            File.WriteAllText(Path.Combine(tmp, "source.txt"), f.TagDir + "\r\nfake_fake_lightmap");
            var c = TagRestore.Candidates(f.Work, f.Set);
            Assert.Eq(b.Dir, string.Join("|", c.Select(x => x.Dir)), "only the finished backup is a candidate");
            Assert.True(c[0].Verified, "backup.json with md5s = verified");
            string legacy = Path.Combine(ui, "fake_fake_20261014_150000_launcher");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "fake_fake_lightmap.scenario_structure_lightmap"), "old");
            File.WriteAllText(Path.Combine(legacy, "source.txt"), f.TagDir + "\r\nfake_fake_lightmap");
            var l = TagRestore.Candidates(f.Work, f.Set).Single(x => x.Dir == legacy);
            Assert.True(!l.Verified, "a source.txt-only backup is NOT verified (the UI warns)");
            Assert.Contains(l.Describe(), "NOT VERIFIED", "described as such");
        }

        private static void NoOpRestore()
        {
            var f = FakeTags.Make("fx4");
            var b1 = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 10, 0, 0));       // S0, then a bake installs S1
            foreach (var n in new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" })
                File.WriteAllText(Path.Combine(f.TagDir, n), n + " S1");
            Assert.True(TagBackup.MarkInstalledAfter(b1.Dir, DateTime.Now), "marked installedAfter");
            var b2 = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 11, 0, 0));       // S1: a bake that then FAILED
            var cands = TagRestore.Candidates(f.Work, f.Set);
            Assert.Eq(b2.Dir, cands.First().Dir, "b2 is the newest backup");
            Assert.True(TagRestore.IsNoOp(f.Set, cands.First()), "... and equals the live set");
            Assert.Eq(b1.Dir, TagRestore.Newest(f.Work, f.Set).Dir, "Newest skips it: the pre-install state is the default");
            Assert.True(cands.Single(c => c.Dir == b1.Dir).InstalledAfter && !cands.First().InstalledAfter, "installedAfter recorded");
            var nr = TagRestore.Restore(f.Work, f.Set, cands.First(), DateTime.Now);
            Assert.Eq(RestoreOutcome.NothingToRestore, nr.Outcome, "a no-op restore is not 'restored N files'");
            Assert.Contains(nr.Message, "already equals", "says why");
            Assert.True(!Directory.GetDirectories(TagBackup.UiRoot(f.Work)).Any(d => d.Contains("_before_restore_")), "nothing saved aside for a no-op");
            Assert.Eq(b1.Dir, TagRestore.ByName(f.Work, f.Set, Path.GetFileName(b1.Dir)).Dir, "ByName (CLI --from)");
            var r = TagRestore.Restore(f.Work, f.Set, TagRestore.Newest(f.Work, f.Set), DateTime.Now);
            Assert.Eq(RestoreOutcome.Restored, r.Outcome, "restored: " + r.Message);
            Assert.Eq("ltmp v1", File.ReadAllText(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap")), "S0 back");
        }

        private static void RestoreMarker()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("fx5");
            var b = TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));
            // the live set changed after the backup, and its P8 went missing (the restore will CREATE it)
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap"), "ltmp LATER");
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_truecolor_bitmaps.bitmap"), "truecolor LATER");
            File.Move(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap"), Path.Combine(f.TagDir, "p8.moved_by_test"));
            var before = new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" }.ToDictionary(n => n, f.Md5);
            string marker = TagRestore.MarkerPathFor(f.Work, f.Set);
            var r = TagRestore.Restore(f.Work, f.Set, TagRestore.Newest(f.Work, f.Set), new DateTime(2026, 10, 14, 9, 30, 0));
            Assert.Eq(RestoreOutcome.Restored, r.Outcome, "restored: " + r.Message);
            Assert.True(!File.Exists(marker) && Directory.GetFiles(Path.GetDirectoryName(marker), Path.GetFileName(marker) + ".done_*").Length == 1, "the marker was written and renamed .done_");
            var man = Json.ReadObjFile(Path.Combine(r.SavedAsideDir, "_install_manifest.json"));
            Assert.True(man != null && man.Arr("absent_before").Cast<string>().Contains("fake_fake_lightmap_bitmaps.bitmap"), "the rollback manifest lists the file the restore created");
            Assert.Eq("p8 v1", File.ReadAllText(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap")), "P8 restored");
            // simulate the power cut mid-restore: the marker is still there
            File.Copy(Directory.GetFiles(Path.GetDirectoryName(marker), Path.GetFileName(marker) + ".done_*").Single(), marker);
            var it = StartupRecovery.Scan(f.Work).Single(x => x.Kind == RecoveryKind.InstallMarker);
            Assert.True(it.IsTagRestore && !it.InUse && it.BackupDir == r.SavedAsideDir && it.Prefix == "fake_fake_lightmap", "recovery sees the interrupted restore: " + it);
            Assert.True(it.Concerns(f.Set), "it concerns this set (a bake of it is refused)");
            var refused = TagRestore.Restore(f.Work, f.Set, b == null ? null : TagRestore.ByName(f.Work, f.Set, Path.GetFileName(b.Dir)), DateTime.Now);
            Assert.Eq(RestoreOutcome.Refused, refused.Outcome, "no new restore over an unfinished one");
            var req = StartupRecovery.RestoreRequestFor(it, f.Root, f.Work);
            req.PyExe = C.PyExe; req.LmwInstallPy = C.FxLmwInstall;
            req.Env = BakeEnv.Build(new H2Lm.Proc.BakeEnvSpec { PwshDir = C.PwshDir, PyDir = C.PyDir, Root = f.Root, Work = f.Work });
            var rr = TagInstaller.Restore(req);
            Assert.Eq(InstallOutcome.Installed, rr.Outcome, "lmw_install restore exit 0: " + rr.Message + " / " + string.Join(" / ", rr.Lines));
            foreach (var kv in before) Assert.Eq(kv.Value, f.Md5(kv.Key), kv.Key + " rolled back to before the restore");
            Assert.True(!File.Exists(Path.Combine(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap")) && Directory.GetFiles(f.TagDir, "fake_fake_lightmap_bitmaps.bitmap.gpulm_removed_*").Length == 1,
                        "the file the restore created is renamed aside (never deleted)");
            Assert.True(!File.Exists(marker), "marker renamed .restored_ by lmw_install");
            Assert.Eq(0, StartupRecovery.Scan(f.Work).Count, "clean afterwards");
        }

        private static void ClearMarkers()
        {
            var f = FakeTags.Make("fx6");
            TagBackup.Create(f.Work, f.Set, BackupKind.LightmapSet, new DateTime(2026, 10, 14, 9, 0, 0));
            File.WriteAllText(Path.Combine(f.TagDir, "fake_fake_lightmap.scenario_structure_lightmap"), "ltmp half-written by a tool write-back");
            string mine = Path.Combine(Farm(f.Work, 208), StartupRecovery.MarkerName);
            string other = Path.Combine(Farm(f.Work, 209), StartupRecovery.MarkerName);
            string json = Path.Combine(Farm(f.Work, 210), StartupRecovery.MarkerName);
            File.WriteAllText(mine, string.Format(ToolText, Path.Combine(f.Work, @"farm_w208\tag_backup_before_writeback"), f.TagDir, "fake_fake_lightmap"));
            File.WriteAllText(other, string.Format(ToolText, Path.Combine(f.Work, @"farm_w209\tag_backup_before_writeback"), f.TagDir, "fake_other_lightmap"));
            File.WriteAllText(json, Json.Write(new JObj().Add("started", "x").Add("target_dir", f.TagDir).Add("backups", new List<object> { Path.Combine(f.Work, "bak") })
                .Add("files", new JObj().Add("fake_fake_lightmap.scenario_structure_lightmap", new JObj().Add("state", "installed")))));
            var items = StartupRecovery.Scan(f.Work);
            var set = StartupRecovery.SetFor(items.Single(i => i.Worker == 208), f.Root);
            Assert.True(set != null && set.ScenarioRel == @"scenarios\multi\fake\fake" && set.Bsp == "fake", "SetFor finds the set the text marker names: " + (set == null ? "null" : set.ScenarioRel + " / " + set.Bsp));
            Assert.True(items.Single(i => i.Worker == 208).Concerns(f.Set) && !items.Single(i => i.Worker == 209).Concerns(f.Set), "Concerns: this set yes, the other BSP no");
            var r = TagRestore.Restore(f.Work, f.Set, TagRestore.Newest(f.Work, f.Set), new DateTime(2026, 10, 14, 10, 0, 0));
            Assert.Eq(RestoreOutcome.Restored, r.Outcome, "restored: " + r.Message);
            Assert.True(!File.Exists(mine) && File.Exists(mine + ".restored_20261014_100000"), "this set's tool marker renamed .restored_");
            Assert.True(!File.Exists(json) && File.Exists(json + ".restored_20261014_100000"), "this set's lmw_install marker too");
            Assert.True(File.Exists(other), "another BSP's marker is left alone");
            Assert.Eq(2, r.ClearedMarkers.Count, "reported: " + string.Join(", ", r.ClearedMarkers));
            Assert.Contains(r.Message, "cleared the unfinished write-back marker", "said so");
        }
    }
}
