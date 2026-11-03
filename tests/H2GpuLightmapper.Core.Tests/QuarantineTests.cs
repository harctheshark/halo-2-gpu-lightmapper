// 0.6.8 RC: the runtime-file re-check against antivirus quarantine (QuarantineGuard + PayloadInstaller.VerifyAndRepairBeforeBake /
// FindRemovedAfterBake). Windows Security removed INST's tool loader (then inject.exe) in the middle of a bake on 2026-10-29; these tests SIMULATE that (a deleted
// file, a changed file, an error code) in this run's sandbox. Nothing here touches Windows Security.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class QuarantineTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("quarantine (RC): ERROR_VIRUS_INFECTED / _DELETED recognised (HResult, Win32Exception, inner); the message names the file, 'unchanged since 0.6.8' for H2GpuLightmapper.ToolLoader.exe (0.5.0 for the old inject.exe), Protection history, 'Nothing was written'", Basics);
            yield return T("quarantine (RC): a MISSING INST file and an md5 MISMATCH are re-extracted before the bake; the record is cleared once all verifies", RepairOnce);
            yield return T("quarantine (RC): a re-extracted file that vanishes again during the settle -> DefenderRemovedException before the bake, recorded; the next check refuses WITHOUT extracting; restored -> verifies, record gone", VanishesAgain);
            yield return T("quarantine (RC): a file removed while the bake ran is found after it (FindRemovedAfterBake); the next pre-bake check re-extracts it ONCE; removed again during that bake -> recorded -> the bake after refuses without extracting", AfterBake);
            yield return T("quarantine (0.6.9): the worker folder's own fullgpu.exe copy removed by the antivirus after phase 3 started is found (before the cleanup deletes it); not claimed before phase 3 or when the copy is there", FarmCopyRemoved);
        }

        private static void FarmCopyRemoved()
        {
            string farm = C.Sb.Dir("farm_q");
            Assert.True(QuarantineGuard.FarmExeRemoved(farm) == null, "a worker that never reached phase 3 (no gpu_bake.log): nothing is claimed");
            Assert.True(QuarantineGuard.FarmExeRemoved(Path.Combine(farm, "nope")) == null, "no worker folder");
            File.WriteAllText(Path.Combine(farm, "gpu_bake.log"), "phase 3 started");
            Assert.True(string.Equals(QuarantineGuard.FarmExeRemoved(farm), Path.Combine(farm, "fullgpu.exe"), StringComparison.OrdinalIgnoreCase),
                        "phase 3 started and the worker's own fullgpu.exe copy is gone (the antivirus removed it, the INST copy may be fine)");
            File.WriteAllText(Path.Combine(farm, "fullgpu.exe"), "x");
            Assert.True(QuarantineGuard.FarmExeRemoved(farm) == null, "the copy is there: nothing is claimed");
            string m = QuarantineGuard.Message(Path.Combine(farm, "fullgpu.exe"), null, "0.6.9", true, true);
            Assert.Contains(m, "Windows Security (Defender) removed " + Path.Combine(farm, "fullgpu.exe") + " during the bake", "the message names the farm copy");
            Assert.Contains(m, "Nothing was written.", "nothing written");
        }

        private static PayloadInstaller NewInst(string tag, out string root)
        {
            string pd = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_q" + tag), TestPayload.Standard(C, "q" + tag));
            var inst = new PayloadInstaller(new DirectoryPayloadSource(pd), ShellPaths.ForSandbox(C.Sb.Dir("lad_q" + tag), C.Short.Dir("Q" + tag + "L"), C.Short.Dir("Q" + tag + "V")));
            root = C.Sb.Dir("qroot" + tag);
            inst.SettleMs = 50;
            var q = inst.StartupEnsure(root);
            Assert.True(q.Ok, "extracted: " + q);
            return inst;
        }

        private static string RecordOf(PayloadInstaller inst, string root)
        {
            return QuarantineGuard.RecordPath(ShellPaths.InstParentOf(ShellPaths.WorkOf(root)), inst.Manifest.InstComponent.DirName);
        }

        private static void Basics()
        {
            Assert.True(QuarantineGuard.IsVirusError(new IOException("x", unchecked((int)0x800700E1))), "HResult 0x800700E1 (225)");
            Assert.True(QuarantineGuard.IsVirusError(new IOException("x", unchecked((int)0x800700E2))), "HResult 0x800700E2 (226)");
            Assert.True(QuarantineGuard.IsVirusError(new Win32Exception(225)), "Win32Exception 225");
            Assert.True(QuarantineGuard.IsVirusError(new InvalidOperationException("outer", new IOException("x", unchecked((int)0x800700E2)))), "inner exception");
            Assert.True(!QuarantineGuard.IsVirusError(new IOException("x", unchecked((int)0x80070020))), "a sharing violation (32) is not");
            Assert.True(!QuarantineGuard.IsVirusError(new IOException("x", unchecked((int)0x800A00E1))), "225 in another facility is not");
            string m = QuarantineGuard.Message(@"C:\x\v014-inject-estimator\H2GpuLightmapper.ToolLoader.exe", "43872D6BD93B3E891BCA7F2F8908DD8C", "0.6.8");
            Assert.Contains(QuarantineGuard.Message(@"C:\x\inject.exe", "4be2dfa6a2670f7690b125b2e8b44340", "0.6.8"), "unchanged since 0.5.0.", "the old inject.exe");
            Assert.Eq("Windows Security (Defender) removed C:\\x\\v014-inject-estimator\\H2GpuLightmapper.ToolLoader.exe. It is part of the H2 GPU Lightmapper and is unchanged since 0.6.8. "
                      + "Restore it from Windows Security > Protection history, or allow it, then run the bake again. Nothing was written.", m, "the message");
            string d = QuarantineGuard.Message(@"C:\x\a.dll", "00", "0.6.8", true, false);
            Assert.Contains(d, "removed C:\\x\\a.dll during the bake.", "during the bake");
            Assert.Contains(d, "part of the H2 GPU Lightmapper 0.6.8", "an unknown md5 names the app version");
            Assert.NotContains(d, "Nothing was written", "not claimed after an install");
        }

        private static void RepairOnce()
        {
            string root;
            var inst = NewInst("a", out root);
            var ic = inst.Manifest.InstComponent;
            string dir = inst.Resolve(root).InstDir;
            string dll = Path.Combine(dir, @"engine_v373\v014-inject-estimator\gpulm_estimator.dll");
            string ps1 = Path.Combine(dir, @"engine_v373\gpu_bake.ps1");
            File.Delete(dll);                                          // simulated: missing
            var b = File.ReadAllBytes(ps1); b[5] ^= 0x01; File.WriteAllBytes(ps1, b);   // simulated: md5 mismatch
            var v = inst.VerifyBeforeBake(root);
            var r = v.Components.First(x => x.Component == ic).Result;
            Assert.True(!r.Ok && r.Missing.SequenceEqual(new[] { "engine_v373/v014-inject-estimator/gpulm_estimator.dll" }) && r.BadFiles.Count == 2, "missing + changed flagged: " + string.Join("; ", r.Problems));
            File.WriteAllLines(RecordOf(inst, root), new[] { "# stale", ic.DirName + "/osoyoos/readme.txt" });   // a file that vanished once and is back
            var ok = inst.VerifyAndRepairBeforeBake(root);
            Assert.True(ok.Ok && File.Exists(dll), "re-extracted and verified");
            Assert.True(!File.Exists(RecordOf(inst, root)), "everything verifies: the record is cleared");
        }

        private static void VanishesAgain()
        {
            string root;
            var inst = NewInst("b", out root);
            var ic = inst.Manifest.InstComponent;
            string dir = inst.Resolve(root).InstDir;
            const string rel = "engine_v373/v014-inject-estimator/gpulm_estimator.dll";
            string dll = Path.Combine(dir, rel.Replace('/', '\\'));
            File.Delete(dll);
            inst.SimulateQuarantineRel = rel;                          // the "antivirus" removes it again right after the re-extraction
            var e = Assert.Throws<DefenderRemovedException>(() => inst.VerifyAndRepairBeforeBake(root), "refused before the bake");
            Assert.Eq(dll, e.FilePath, "the file");
            Assert.Contains(e.Message, "Windows Security (Defender) removed " + dll + ". It is part of the H2 GPU Lightmapper", "the message");
            Assert.Contains(e.Message, "Protection history", "how to restore");
            Assert.True(QuarantineGuard.LoadRecord(RecordOf(inst, root)).Contains(ic.DirName + "/" + rel), "recorded");
            // the next bake: still gone -> refused at once, NOT extracted a third time (also BEFORE StartupEnsure, which would re-extract it)
            inst.SimulateQuarantineRel = null;
            Assert.Throws<DefenderRemovedException>(() => inst.RefuseIfRemovedAgain(root), "refused before StartupEnsure");
            Assert.Throws<DefenderRemovedException>(() => inst.VerifyAndRepairBeforeBake(root), "refused again");
            Assert.True(!File.Exists(dll), "not re-extracted again");
            // the user restores it (here: RepairEverything stands in for Windows Security's Restore) -> the bake check passes, the record goes
            Assert.True(inst.Repair(root, inst.VerifyBeforeBake(root)).Ok && File.Exists(dll), "restored");
            Assert.True(inst.VerifyAndRepairBeforeBake(root).Ok, "verifies");
            Assert.True(!File.Exists(RecordOf(inst, root)), "record cleared");
        }

        private static void AfterBake()
        {
            string root;
            var inst = NewInst("c", out root);
            string dir = inst.Resolve(root).InstDir;
            Assert.Eq(0, inst.FindRemovedAfterBake(root).Count, "nothing removed");
            string exe = inst.Manifest.InstComponent.Files.Any(f => f.Path == "gpucheck/gpucheck.exe") ? Path.Combine(dir, @"gpucheck\gpucheck.exe") : Path.Combine(dir, @"engine_v373\v014-inject-estimator\gpulm_estimator.dll");
            File.Delete(exe);                                          // simulated: removed while the bake ran
            var gone = inst.FindRemovedAfterBake(root);
            Assert.True(gone.Count == 1 && string.Equals(gone[0].Key, exe, StringComparison.OrdinalIgnoreCase), "found: " + string.Join(", ", gone.Select(g => g.Key)));
            Assert.True(QuarantineGuard.LoadRecord(RecordOf(inst, root)).Count == 0, "not recorded: it was never re-extracted");
            Assert.True(inst.VerifyAndRepairBeforeBake(root).Ok && File.Exists(exe), "the next bake re-extracts it once");
            File.Delete(exe);                                          // ... and the antivirus removes it again during that bake
            Assert.Eq(1, inst.FindRemovedAfterBake(root).Count, "found again");
            Assert.True(QuarantineGuard.LoadRecord(RecordOf(inst, root)).Count == 1, "recorded: it vanished after its one re-extraction");
            var e = Assert.Throws<DefenderRemovedException>(() => inst.VerifyAndRepairBeforeBake(root), "the bake after that is refused before it starts");
            Assert.True(!File.Exists(exe), "not extracted a third time");
            Assert.Contains(e.Message, "Windows Security (Defender) removed " + exe, "the message");
        }
    }
}
