// 0.6.12: ROOT\GPU Lightmap Work -> ROOT\GPU Work, WORK\Temp (safe to delete) -> WORK\Temp (WorkMigration). Sandbox only (C.Sb), no GPU.
using System;
using System.Collections.Generic;
using System.IO;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class WorkMigrationTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("work 0.6.12: an app-only GPU Lightmap Work is renamed to GPU Work, its Temp (safe to delete) to Temp; recorded paths rebase", () => On(WholeRename));
            yield return T("work 0.6.12: a GPU Lightmap Work with other contents: only the app's folders move, the rest stays", () => On(MixedMove));
            yield return T("work 0.6.12: an unfinished install marker or a live bake keeps the old folder (its Temp keeps its old name)", () => On(Deferred));
            yield return T("work 0.6.12: a fresh root gets GPU Work; an existing GPU Work wins", () => On(Fresh));
            yield return T("work 0.6.12: a --dry-run (ReadOnly) only looks: the old folder is used and nothing moves; a later real run still migrates", () => On(DryRunLooks));
        }

        /// <summary>Migration on for ONE sandbox test (Program.Main turns it off for every other test: they read the production H2EK).</summary>
        private static void On(Action a)
        {
            WorkMigration.ResetForTests();
            WorkMigration.Disabled = false;
            try { a(); }
            finally { WorkMigration.Disabled = true; WorkMigration.ResetForTests(); }
        }

        private static string Write(string path, string text = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            return path;
        }

        private static void WholeRename()
        {
            WorkMigration.ResetForTests();
            string root = C.Sb.Dir(@"wm1\H2EK");
            string old = Path.Combine(root, "GPU Lightmap Work");
            string bak = Write(Path.Combine(old, @"_TAGBAK_ui\a_b_20261101_000000\x.bitmap"));
            string run = Path.Combine(old, @"standalone\runs\20261101_000000_a_b");
            Write(Path.Combine(run, @"tagbak\x.bitmap"));
            Write(Path.Combine(old, @"Temp (safe to delete)\farm_w200\capture_id.json"));
            Write(Path.Combine(old, @"_runtime\0.6.11-00000000\.complete"));
            Write(Path.Combine(old, @"standalone\markers\install_w200.json.done_20261101_000000"));
            var st = new RunStatus { RunDir = run, RunTag = "20261101_000000_a_b", State = RunState.Done, InstallBackupDir = Path.Combine(run, "tagbak"), BackupDir = Path.GetDirectoryName(bak) };
            st.Save();

            string work = ShellPaths.WorkOf(root);
            Assert.Eq(Path.Combine(root, "GPU Work"), work, "WORK is GPU Work");
            Assert.True(!Directory.Exists(old), "the old folder is gone (renamed)");
            Assert.True(File.Exists(Path.Combine(work, @"_TAGBAK_ui\a_b_20261101_000000\x.bitmap")), "the backups moved");
            Assert.True(File.Exists(Path.Combine(work, @"Temp\farm_w200\capture_id.json")), "Temp (safe to delete) is now Temp");
            Assert.True(!Directory.Exists(Path.Combine(work, "Temp (safe to delete)")), "no old Temp left");
            Assert.Eq(Path.Combine(work, "Temp"), ShellPaths.TempOf(work), "TempOf is WORK\\Temp");
            var back = RunStatus.Load(Path.Combine(work, @"standalone\runs\20261101_000000_a_b"));
            Assert.Eq(Path.Combine(work, @"standalone\runs\20261101_000000_a_b\tagbak"), back.InstallBackupDir, "installBackupDir rebased to GPU Work");
            Assert.Eq(Path.Combine(work, @"_TAGBAK_ui\a_b_20261101_000000"), back.BackupDir, "backupDir rebased to GPU Work");
            Assert.Eq(Path.Combine(work, @"Temp\farm_w200"), WorkMigration.Rebase(Path.Combine(old, @"Temp (safe to delete)\farm_w200")), "a Temp (safe to delete) path rebases to Temp");
            Assert.Eq(@"Z:\nowhere\GPU Lightmap Work\x", WorkMigration.Rebase(@"Z:\nowhere\GPU Lightmap Work\x"), "a path with no new counterpart stays");
            Assert.True(string.Join(" | ", WorkMigration.TakeMessages()).Contains("renamed"), "the rename is reported");
        }

        private static void MixedMove()
        {
            WorkMigration.ResetForTests();
            string root = C.Sb.Dir(@"wm2\ProdRoot");
            string old = Path.Combine(root, "GPU Lightmap Work");
            Write(Path.Combine(old, @"_TAGBAK_ui\set\x.bitmap"));
            Write(Path.Combine(old, @"standalone\history.json"), "{}");
            Write(Path.Combine(old, @"Temp (safe to delete)\farm_w201\x.bin"));
            string research = Write(Path.Combine(old, @"H2GPULM (Max Size - 100GBs)\engine_v373\gpu_bake.ps1"));
            string loose = Write(Path.Combine(old, "gather_out.bin"));
            string work = ShellPaths.WorkOf(root);
            Assert.Eq(Path.Combine(root, "GPU Work"), work, "WORK is GPU Work");
            Assert.True(File.Exists(Path.Combine(work, @"_TAGBAK_ui\set\x.bitmap")) && File.Exists(Path.Combine(work, @"standalone\history.json")), "the app's folders moved");
            Assert.True(File.Exists(Path.Combine(work, @"Temp\farm_w201\x.bin")), "the temporary folder moved as Temp");
            Assert.True(File.Exists(research) && File.Exists(loose), "everything else stays in the old folder");
            Assert.True(!Directory.Exists(Path.Combine(old, "_TAGBAK_ui")) && !Directory.Exists(Path.Combine(old, "standalone")), "nothing of the app's is left behind");
            Assert.True(string.Join(" | ", WorkMigration.TakeMessages()).Contains("stay where they are"), "the partial move is reported");
        }

        private static void Deferred()
        {
            WorkMigration.ResetForTests();
            string root = C.Sb.Dir(@"wm3\H2EK");
            string old = Path.Combine(root, "GPU Lightmap Work");
            Write(Path.Combine(old, @"standalone\markers\install_w200.json"), "{\"backups\":[],\"target_dir\":\"x\"}");
            Write(Path.Combine(old, @"Temp (safe to delete)\farm_w200\x.bin"));
            string work = ShellPaths.WorkOf(root);
            Assert.Eq(old, work, "an unfinished install marker: the old WORK is used");
            Assert.Eq(Path.Combine(old, "Temp (safe to delete)"), ShellPaths.TempOf(work), "and its Temp keeps the old name");
            Assert.True(!Directory.Exists(Path.Combine(root, "GPU Work")), "nothing was created");
            Assert.True(string.Join(" | ", WorkMigration.TakeMessages()).Contains("unfinished"), "the deferral is reported");
            // after the marker is resolved, the next start moves it
            File.Move(Path.Combine(old, @"standalone\markers\install_w200.json"), Path.Combine(old, @"standalone\markers\install_w200.json.done_x"));
            WorkMigration.ResetForTests();
            Assert.Eq(Path.Combine(root, "GPU Work"), ShellPaths.WorkOf(root), "the next start renames it");

            WorkMigration.ResetForTests();
            string root2 = C.Sb.Dir(@"wm3b\H2EK");
            string old2 = Path.Combine(root2, "GPU Lightmap Work");
            Directory.CreateDirectory(ShellPaths.StandaloneOf(old2));
            SafeFs.WriteAllTextAtomic(CurrentRun.PathFor(old2), Json.Write(new JObj().Add("runDir", Path.Combine(old2, "x")).Add("runTag", "x").Add("shellPid", ProcessInfo.CurrentPid)
                .Add("shellStartUtc", ProcessInfo.FormatTicks(ProcessInfo.CurrentStartUtcTicks))));
            Assert.Eq(old2, ShellPaths.WorkOf(root2), "a live bake in the old WORK: it is used as is");
        }

        private static void Fresh()
        {
            WorkMigration.ResetForTests();
            string root = C.Sb.Dir(@"wm4\H2EK");
            Assert.Eq(Path.Combine(root, "GPU Work"), ShellPaths.WorkOf(root), "a fresh root");
            WorkMigration.ResetForTests();
            string root2 = C.Sb.Dir(@"wm5\H2EK");
            Directory.CreateDirectory(Path.Combine(root2, "GPU Work", "standalone"));
            Write(Path.Combine(root2, @"GPU Lightmap Work\standalone\history.json"), "{}");
            Write(Path.Combine(root2, @"GPU Lightmap Work\_TAGBAK_ui\s\x.bitmap"));
            Assert.Eq(Path.Combine(root2, "GPU Work"), ShellPaths.WorkOf(root2), "an existing GPU Work wins");
            Assert.True(File.Exists(Path.Combine(root2, @"GPU Lightmap Work\standalone\history.json")), "a folder already in the new WORK is never overwritten");
            Assert.True(File.Exists(Path.Combine(root2, @"GPU Work\_TAGBAK_ui\s\x.bitmap")), "the folders the new WORK lacks move in");
            WorkMigration.ResetForTests();
        }

        private static void DryRunLooks()
        {
            WorkMigration.ResetForTests();
            string root = C.Sb.Dir(@"wm6\H2EK");
            string old = Path.Combine(root, "GPU Lightmap Work");
            Write(Path.Combine(old, @"_TAGBAK_ui\s\x.bitmap"));
            Write(Path.Combine(old, @"standalone\history.json"), "{}");
            WorkMigration.ReadOnly = true;
            try
            {
                Assert.Eq(old, ShellPaths.WorkOf(root), "a dry run uses the old folder");
                Assert.True(!Directory.Exists(Path.Combine(root, "GPU Work")), "a dry run creates no GPU Work");
                Assert.True(File.Exists(Path.Combine(old, @"_TAGBAK_ui\s\x.bitmap")) && File.Exists(Path.Combine(old, @"standalone\history.json")), "a dry run moves nothing");
            }
            finally { WorkMigration.ReadOnly = false; }
            Assert.Eq(Path.Combine(root, "GPU Work"), ShellPaths.WorkOf(root), "the next real run migrates (the dry run cached nothing)");
            Assert.True(File.Exists(Path.Combine(root, @"GPU Work\_TAGBAK_ui\s\x.bitmap")), "moved by the real run");
            WorkMigration.ResetForTests();
        }
    }
}
