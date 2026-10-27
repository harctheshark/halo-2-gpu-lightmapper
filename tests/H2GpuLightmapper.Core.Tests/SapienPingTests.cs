using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>
    /// The Sapien reload signal (SapienReloadPing). Sapien watches tags\ with ReadDirectoryChangesW, filter
    /// FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_LAST_WRITE (sapien.exe sub_7F36A0, dwNotifyFilter 0x14): FileSystemWatcher with
    /// NotifyFilter = Size | LastWrite issues exactly that call, so it stands in for Sapien here. No Sapien, no window, no live tag.
    /// </summary>
    internal static class SapienPingTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return new KeyValuePair<string, Action>("sapien.ping: a rename-over install raises NO event for the tag name (Sapien's filter); the ping does", WatcherRenameVsPing);
            yield return new KeyValuePair<string, Action>("sapien.ping: touches metadata only (bytes unchanged), ignores absent / read-only files, never throws", TouchSemantics);
            yield return new KeyValuePair<string, Action>("sapien.ping: TagNamesIn lists only tag files (not reports, _files, .older_ copies)", TagNames);
            yield return new KeyValuePair<string, Action>("sapien.ping: TagInstaller.Install / Restore (real lmw_install) signal the watcher for every installed tag", InstallSignals);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string from, string to, int flags);

        private sealed class Watch : IDisposable
        {
            private readonly FileSystemWatcher _w;
            private readonly List<string> _names = new List<string>();
            public Watch(string dir)
            {
                _w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 64 * 1024 };
                _w.Changed += (s, e) => { lock (_names) _names.Add(Path.GetFileName(e.FullPath)); };
                _w.EnableRaisingEvents = true;
            }
            public void Clear() { Thread.Sleep(400); lock (_names) _names.Clear(); }
            public List<string> Names(int settleMs = 1500) { Thread.Sleep(settleMs); lock (_names) return _names.ToList(); }
            public void Dispose() { _w.Dispose(); }
        }

        private static void WatcherRenameVsPing()
        {
            string dir = C.Sb.Dir(@"sapienping\a");
            string tag = Path.Combine(dir, "x_lightmap.scenario_structure_lightmap");
            File.WriteAllBytes(tag, new byte[4096]);
            using (var w = new Watch(Path.GetDirectoryName(dir)))
            {
                w.Clear();
                // what lmw_install does: <name>.gpulm_new, then MoveFileEx REPLACE_EXISTING over the tag
                string tmp = tag + ".gpulm_new";
                File.WriteAllBytes(tmp, Enumerable.Repeat((byte)7, 8192).ToArray());
                Assert.True(MoveFileExW(tmp, tag, 1), "MoveFileEx replace");
                var ev = w.Names();
                Assert.True(!ev.Contains(Path.GetFileName(tag)), "Sapien's filter gets NO event for the replaced tag name: " + string.Join(",", ev));
                // the signal
                w.Clear();
                var p = SapienReloadPing.Touch(dir, new[] { Path.GetFileName(tag) });
                Assert.Eq(1, p.Touched.Count, "touched");
                ev = w.Names();
                Assert.True(ev.Contains(Path.GetFileName(tag)), "the ping raises a change event for the real tag name: " + string.Join(",", ev));
            }
        }

        private static void TouchSemantics()
        {
            string dir = C.Sb.Dir(@"sapienping\b");
            string a = Path.Combine(dir, "a_lightmap.scenario_structure_lightmap"), ro = Path.Combine(dir, "a_lightmap_bitmaps.bitmap");
            File.WriteAllBytes(a, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(ro, new byte[10]);
            File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string before = SafeFs.Md5File(a);
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            try
            {
                var r = SapienReloadPing.Touch(dir, new[] { "a_lightmap.scenario_structure_lightmap", "a_lightmap_bitmaps.bitmap", "absent.bitmap", "a_lightmap.scenario_structure_lightmap" });
                Assert.Eq(1, r.Touched.Count, "one touched (duplicate collapsed, absent ignored): " + string.Join(",", r.Touched));
                Assert.Eq(1, r.Skipped.Count, "the read-only file is reported, not changed: " + string.Join(",", r.Skipped));
                Assert.Eq(before, SafeFs.Md5File(a), "bytes unchanged");
                Assert.True(File.GetLastWriteTimeUtc(a) > new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), "last-write bumped");
                Assert.Contains(r.Note, "1 tag file(s) signalled", "note");
                Assert.True(SapienReloadPing.Touch(Path.Combine(dir, "nope"), new[] { "x.bitmap" }).Note == null, "a missing folder: nothing, no throw");
                Assert.True(SapienReloadPing.Touch(null, null).Note == null, "null args");
                var ps = SapienReloadPing.TouchPaths(new[] { a, Path.Combine(dir, "zz.bitmap") });
                Assert.Eq(1, ps.Touched.Count, "TouchPaths");
            }
            finally { File.SetAttributes(ro, FileAttributes.Normal); }
        }

        private static void TagNames()
        {
            string dir = C.Sb.Dir(@"sapienping\c");
            foreach (var n in new[] { "q_lightmap.scenario_structure_lightmap", "q_lightmap_bitmaps.bitmap", "q_lightmap_truecolor_bitmaps.bitmap", "q.scenario", "tagw_report.json", "_install_manifest.json",
                                      "q_lightmap.scenario_structure_lightmap.older_20261026_010101", "q_lightmap.scenario_structure_lightmap.gpulm_new", "atlas_out.bin" })
                File.WriteAllText(Path.Combine(dir, n), "x");
            var l = SapienReloadPing.TagNamesIn(dir).OrderBy(x => x).ToList();
            Assert.Seq(new[] { "q.scenario", "q_lightmap.scenario_structure_lightmap", "q_lightmap_bitmaps.bitmap", "q_lightmap_truecolor_bitmaps.bitmap" }, l, "tag names");
            Assert.Eq(0, SapienReloadPing.TagNamesIn(Path.Combine(dir, "nope")).Count, "missing folder");
        }

        private static void InstallSignals()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var f = FakeTags.Make("sping");
            string stage = f.MakeStage("farm_w203", "v2");
            string runDir = C.Sb.Dir(@"troot_sping\run1");
            var req = new InstallRequest
            {
                PyExe = C.PyExe, LmwInstallPy = C.FxLmwInstall, Root = f.Root, Work = f.Work, Stage = stage, TargetDir = f.TagDir,
                BackupDir = Path.Combine(runDir, "tag_backup_before_writeback"), AutoBackupRoot = Path.Combine(f.Work, "_TAGBAK_auto"),
                Marker = Path.Combine(Path.GetDirectoryName(stage), StartupRecovery.MarkerName),
                Env = BakeEnv.Build(new H2Lm.Proc.BakeEnvSpec { PwshDir = C.PwshDir, PyDir = C.PyDir, Root = f.Root, Work = f.Work }),
            };
            using (var w = new Watch(f.TagDir))
            {
                w.Clear();
                var r = TagInstaller.Install(req);
                Assert.Eq(InstallOutcome.Installed, r.Outcome, "installed: " + r.Message);
                Assert.True(r.SapienPing != null && r.SapienPing.Contains("3 tag file(s) signalled"), "install note: " + r.SapienPing);
                var ev = w.Names();
                foreach (var n in new[] { "fake_fake_lightmap.scenario_structure_lightmap", "fake_fake_lightmap_bitmaps.bitmap", "fake_fake_lightmap_truecolor_bitmaps.bitmap" })
                    Assert.True(ev.Contains(n), "the watcher saw " + n + " after the install: " + string.Join(",", ev));
                Assert.True(!ev.Contains("fake_other_lightmap.scenario_structure_lightmap"), "the other BSP is not signalled");
                Assert.True(!ev.Contains("fake_fake_lightmap_radiance_bitmaps_0.bitmap"), "radiance is not signalled");

                // undo: the same signal after the restore
                w.Clear();
                var u = TagInstaller.Restore(new TagRestoreRequest { PyExe = C.PyExe, LmwInstallPy = C.FxLmwInstall, Root = f.Root, Work = f.Work, BackupDir = req.BackupDir, TargetDir = f.TagDir, Marker = null, Env = req.Env });
                Assert.Eq(InstallOutcome.Installed, u.Outcome, "restored: " + u.Message);
                Assert.True(u.SapienPing != null && u.SapienPing.Contains("tag file(s) signalled"), "restore note: " + u.SapienPing);
                ev = w.Names();
                Assert.True(ev.Contains("fake_fake_lightmap.scenario_structure_lightmap"), "the watcher saw the ltmp after the restore: " + string.Join(",", ev));
            }
        }
    }
}
