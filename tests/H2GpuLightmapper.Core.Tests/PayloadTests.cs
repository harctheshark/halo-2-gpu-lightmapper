using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class PayloadTests
    {
        private static string _payloadDir;
        private static PayloadInstaller _inst;
        private static string _longRoot, _shortRoot;

        private static PayloadInstaller Inst
        {
            get
            {
                if (_inst != null) return _inst;
                _payloadDir = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_a"), TestPayload.Standard(C, "a"));
                var paths = ShellPaths.ForSandbox(C.Sb.Dir("lad"), C.Short.Dir("L"), C.Short.Dir("V"));
                _inst = new PayloadInstaller(new DirectoryPayloadSource(_payloadDir), paths);
                _longRoot = C.Sb.Dir("lr");   // the sandbox path alone makes WORK\ far longer than 60
                _shortRoot = C.Short.Dir("r");
                return _inst;
            }
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("payload: manifest loads, validates, finds pwsh / py / inst by content", Manifest);
            yield return T("payload: rt first extraction, already-present fast path, quick check", RtExtract);
            yield return T("payload: INST on a long WORK -> LocalAppData alias; slot DLLs patched to it; marker", InstLongAlias);
            yield return T("payload: INST on a short WORK -> Direct slot base WORK\\", InstShortDirect);
            yield return T("payload: VerifyBeforeBake ok + Resolve paths", VerifyAndResolve);
            yield return T("payload: tampered INST script -> named by verify -> Repair restores it", TamperScript);
            yield return T("payload: DLL byte changed outside its slots -> detected -> repaired + re-patched", TamperDll);
            yield return T("payload: rt key file deleted -> quick check + verify flag it -> repaired", RtKeyFileMissing);
            yield return T("payload: alias blocked by a plain folder -> volume alias -> EnsureInst re-patches", AliasMoves);
            yield return T("payload: bad marker -> dir renamed aside (.bad_) and extracted again", BadMarker);
            yield return T("payload: refusals: unsafe manifest path, extra zip entry, wrong md5, MAX_PATH", Refusals);
            yield return T("payload: old versions: listed; reparse point refused; plain one removed", OldVersions);
            yield return T("payload: StartupEnsure from nothing; non-key file loss needs RepairEverything", StartupAndRepairAll);
        }

        private static void StartupAndRepairAll()
        {
            string pd = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_s"), TestPayload.Standard(C, "s"));
            var inst = new PayloadInstaller(new DirectoryPayloadSource(pd), ShellPaths.ForSandbox(C.Sb.Dir("lad_s"), C.Short.Dir("SL"), C.Short.Dir("SV")));
            string root = C.Sb.Dir("sroot");
            var q = inst.StartupEnsure(root);
            Assert.True(q.Ok && q.Components.Count == 3, "first start extracts and checks everything: " + q);
            string notes = Path.Combine(inst.RtDir(inst.Manifest.PwshComponent), @"en-US\notes.txt");
            File.Delete(notes);
            Assert.True(inst.VerifyBeforeBake(root).Ok, "a non-key runtime file is not part of the pre-bake check");
            Assert.True(!inst.QuickCheck(root).Ok, "... but the start-up quick check sees it");
            var r = inst.RepairEverything(root);
            Assert.True(r.Ok && File.Exists(notes), "RepairEverything restores it");
            Assert.True(inst.StartupEnsure(root).Ok, "next start is clean");
        }

        private static void Manifest()
        {
            var m = Inst.Manifest;
            Assert.Eq(1, m.Format, "format");
            Assert.Eq("rt-pwsh-test", m.PwshComponent.Name, "pwsh component");
            Assert.Eq("rt-py-test", m.PyComponent.Name, "py component");
            Assert.Eq("engine-test", m.InstComponent.Name, "inst component");
            Assert.Eq(3, m.InstComponent.Files.Count(f => f.IsDllSlot), "slot DLLs (2 by role, 1 by name)");
            Assert.True(PayloadInstaller.IsKeyFile(m.PwshComponent.Find("pwsh.exe")), "pwsh.exe is key");
            Assert.True(!PayloadInstaller.IsKeyFile(m.PwshComponent.Find("en-US/notes.txt")), "notes is not key");
            Assert.True(PayloadInstaller.IsKeyFile(m.PyComponent.Find("site-packages/numpy/_core/_multiarray_umath.cp310-win_amd64.pyd")), "numpy core is key");
        }

        private static void RtExtract()
        {
            var r1 = Inst.EnsureRuntimes();
            Assert.Eq(2, r1.Count, "two runtimes");
            Assert.True(r1.All(r => !r.AlreadyPresent && r.Files > 0), "first run extracts");
            var r2 = Inst.EnsureRuntimes();
            Assert.True(r2.All(r => r.AlreadyPresent), "second run is the fast path");
            var q = Inst.QuickCheck();
            Assert.True(q.Ok, "quick check: " + q);
            foreach (var c in Inst.Manifest.RtComponents)
            {
                string d = Inst.RtDir(c);
                Assert.True(File.Exists(Path.Combine(d, PayloadExtractor.CompleteMarker)), "marker in " + d);
                Assert.True(!Directory.GetDirectories(Inst.Paths.RtRoot).Any(x => x.Contains(".tmp_")), "no tmp dir left");
                Assert.Eq(PayloadExtractor.StampUtc, File.GetLastWriteTimeUtc(Path.Combine(d, c.Files[0].Path.Replace('/', '\\'))), "mtime stamp");
            }
        }

        private static void InstLongAlias()
        {
            Inst.EnsureRuntimes();
            string work = ShellPaths.WorkOf(_longRoot);
            Assert.True(work.Length + 1 > DllSlotPatcher.MaxBaseBytes, "the long WORK really is over 60 (" + (work.Length + 1) + ")");
            var r = Inst.EnsureInst(_longRoot);
            Assert.Eq(SlotBaseKind.AliasLocalAppData, r.Slot.Kind, "alias kind");
            Assert.True(r.Slot.SlotBase.StartsWith(Path.Combine(C.Short.Root, "L", "H2GLM"), StringComparison.OrdinalIgnoreCase), "alias under the sandbox LocalAppData: " + r.Slot.SlotBase);
            Assert.True(r.Slot.SlotBase.Length <= 60, "alias fits");
            Assert.True(Junction.IsJunction(r.Slot.AliasDir), "alias is a junction");
            Assert.True(Junction.SameDirectory(r.Slot.AliasDir, work), "alias resolves to the REAL WORK");
            foreach (var f in Inst.Manifest.InstComponent.Files.Where(x => x.IsDllSlot))
            {
                var b = File.ReadAllBytes(Path.Combine(r.InstDir, f.Path.Replace('/', '\\')));
                var id = DllSlotPatcher.Identify(b);
                Assert.True(id != null, f.Path + " identifies");
                Assert.Eq(r.Slot.SlotBase, id.Base, f.Path + " slot base");
                Assert.True(!id.IsPristine, f.Path + " is patched");
                Assert.Eq(PayloadExtractor.StampUtc, File.GetLastWriteTimeUtc(Path.Combine(r.InstDir, f.Path.Replace('/', '\\'))), f.Path + " re-stamped");
            }
            var m = PayloadExtractor.ReadMarker(r.InstDir);
            Assert.Eq(r.Slot.SlotBase, m.Str("slotBase"), "marker slotBase");
            Assert.Eq("AliasLocalAppData", m.Str("slotKind"), "marker slotKind");
            Assert.True(Inst.QuickCheck(_longRoot).Ok, "quick check incl. INST");
            var again = Inst.EnsureInst(_longRoot);
            Assert.True(again.Extract.AlreadyPresent && again.Repatched.Count == 0 && again.Slot.State == AliasState.Reused, "second EnsureInst: present, alias reused, nothing re-patched");
        }

        private static void InstShortDirect()
        {
            var inst = Inst;   // builds the payload and the roots on first use
            string work = ShellPaths.WorkOf(_shortRoot);
            if (work.Length + 1 > DllSlotPatcher.MaxBaseBytes) throw new TestSkip("the short sandbox WORK is " + (work.Length + 1) + " chars; set --short-parent to a shorter folder");
            var r = Inst.EnsureInst(_shortRoot);
            Assert.Eq(SlotBaseKind.Direct, r.Slot.Kind, "direct");
            Assert.Eq(work + "\\", r.Slot.SlotBase, "slot base = WORK\\");
            var f = Inst.Manifest.InstComponent.Files.First(x => x.IsDllSlot);
            var id = DllSlotPatcher.Identify(File.ReadAllBytes(Path.Combine(r.InstDir, f.Path.Replace('/', '\\'))));
            Assert.Eq(work + "\\", id.Base, "DLL holds WORK\\");
        }

        private static void VerifyAndResolve()
        {
            Inst.EnsureRuntimes();
            Inst.EnsureInst(_longRoot);
            var v = Inst.VerifyBeforeBake(_longRoot);
            Assert.True(v.Ok, "verify: " + v);
            Assert.True(v.Components.Count == 3, "3 components checked");
            var all = Inst.VerifyBeforeBake(_longRoot, default(System.Threading.CancellationToken), true);
            Assert.True(all.Ok, "verify all runtime files");
            var rt = Inst.Resolve(_longRoot);
            Assert.Eq(Path.Combine(rt.InstDir, @"engine_v373\gpu_bake.ps1"), rt.GpuBakePs1, "gpu_bake path");
            Assert.True(File.Exists(rt.GpuBakePs1) && File.Exists(rt.LmwInstallPy) && File.Exists(rt.BoundsDb), "resolved files exist");
            Assert.Eq(Path.Combine(Inst.Paths.RtRoot, Inst.Manifest.PwshComponent.DirName, "pwsh.exe"), rt.PwshExe, "pwsh exe");
            Assert.Eq(Path.Combine(Inst.Paths.RtRoot, Inst.Manifest.PyComponent.DirName, "py.exe"), rt.PyExe, "py exe");
            Assert.Eq(ShellPaths.WorkOf(_longRoot), rt.Work, "work is the REAL path");
            Assert.True(rt.InstDir.StartsWith(Path.Combine(rt.Work, "_runtime"), StringComparison.OrdinalIgnoreCase), "INST under WORK\\_runtime");
            var spec = rt.ToEnvSpec();
            Assert.Eq(rt.InstDir, spec.Code, "H2LM_CODE = INST");
        }

        private static void TamperScript()
        {
            Inst.EnsureRuntimes();
            var r = Inst.EnsureInst(_longRoot);
            string p = Path.Combine(r.InstDir, @"engine_v373\gpu_bake.ps1");
            var b = File.ReadAllBytes(p);
            b[10] ^= 0x20;
            File.WriteAllBytes(p, b);
            PayloadExtractor.StampMtime(p);
            Assert.True(Inst.QuickCheck(_longRoot).Ok, "same size + stamp: the quick check cannot see it (by design)");
            var v = Inst.VerifyBeforeBake(_longRoot);
            Assert.True(!v.Ok, "full verify catches it");
            var ic = v.Components.First(x => x.Component.Target == "inst");
            Assert.Eq("engine_v373/gpu_bake.ps1", ic.Result.BadFiles.Single(), "bad file named");
            var after = Inst.Repair(_longRoot, v);
            Assert.True(after.Ok, "repaired: " + after);
            Assert.True(Inst.VerifyBeforeBake(_longRoot).Ok, "verify ok after repair");
            Assert.True(!Directory.GetFiles(Path.GetDirectoryName(p)).Any(x => x.Contains(".repair_")), "no repair temp left");
        }

        private static void TamperDll()
        {
            Inst.EnsureRuntimes();
            var r = Inst.EnsureInst(_longRoot);
            var f = Inst.Manifest.InstComponent.Files.First(x => x.IsDllSlot && x.Path.Contains("v043"));
            string p = Path.Combine(r.InstDir, f.Path.Replace('/', '\\'));
            var b = File.ReadAllBytes(p);
            b[0x400] ^= 0x01;   // in the headers / code, far from both slots
            File.WriteAllBytes(p, b);
            var v = Inst.VerifyBeforeBake(_longRoot);
            var ic = v.Components.First(x => x.Component.Target == "inst");
            Assert.True(!ic.Result.Ok && ic.Result.BadFiles.Contains(f.Path), "slot verifier flags the DLL");
            Assert.Contains(string.Join("; ", ic.Result.Problems), "outside the slots", "reason");
            var vv = Inst.VerifyAndRepairBeforeBake(_longRoot);
            Assert.True(vv.Ok, "verify-and-repair ok");
            var id = DllSlotPatcher.Identify(File.ReadAllBytes(p));
            Assert.Eq(r.Slot.SlotBase, id.Base, "re-patched to the alias base");
        }

        private static void RtKeyFileMissing()
        {
            Inst.EnsureRuntimes();
            var c = Inst.Manifest.PyComponent;
            string p = Path.Combine(Inst.RtDir(c), "python310.dll");
            File.Delete(p);
            var q = Inst.QuickCheck();
            Assert.True(!q.Ok, "quick check flags the missing file");
            var v = Inst.VerifyBeforeBake(_longRoot);
            var pc = v.Components.First(x => x.Component == c);
            Assert.Eq("python310.dll", pc.Result.BadFiles.Single(), "missing key file named");
            var after = Inst.Repair(_longRoot, v);
            Assert.True(after.Ok && File.Exists(p), "repaired");
            Assert.True(Inst.QuickCheck().Ok, "quick check ok again");
        }

        private static void AliasMoves()
        {
            Inst.EnsureRuntimes();   // each test stands alone (filters)
            var r = Inst.EnsureInst(_longRoot);
            string alias = r.Slot.AliasDir;
            string work = ShellPaths.WorkOf(_longRoot);
            Junction.Remove(alias, work);
            Directory.CreateDirectory(alias);   // a plain folder now squats the alias path: SlotAlias must leave it alone
            try
            {
                var v = Inst.VerifyBeforeBake(_longRoot);
                Assert.True(v.SlotBase.StartsWith(Path.Combine(C.Short.Root, "V", "H2GLM"), StringComparison.OrdinalIgnoreCase), "slot plan moved to the volume alias: " + v.SlotBase);
                Assert.True(!v.Ok, "the DLLs still hold the old base");
                var r2 = Inst.EnsureInst(_longRoot);
                Assert.Eq(3, r2.Repatched.Count, "all 3 slot DLLs re-patched");
                Assert.Eq(SlotBaseKind.AliasVolumeRoot, r2.Slot.Kind, "volume alias");
                Assert.True(Inst.VerifyBeforeBake(_longRoot).Ok, "verify ok on the new base");
                Assert.Eq(r2.Slot.SlotBase, PayloadExtractor.ReadMarker(r2.InstDir).Str("slotBase"), "marker updated");
                Assert.True(Directory.Exists(alias) && !Junction.IsJunction(alias) && Directory.GetFileSystemEntries(alias).Length == 0, "the squatting folder was left untouched");
            }
            finally
            {
                Directory.Delete(alias, false);   // our empty test folder (non-recursive)
            }
            var r3 = Inst.EnsureInst(_longRoot);   // the LocalAppData alias is free again: back to it
            Assert.Eq(SlotBaseKind.AliasLocalAppData, r3.Slot.Kind, "back to the LocalAppData alias");
            Assert.Eq(3, r3.Repatched.Count, "re-patched back");
            Assert.True(Inst.VerifyBeforeBake(_longRoot).Ok, "verify ok");
        }

        private static void BadMarker()
        {
            Inst.EnsureRuntimes();
            var c = Inst.Manifest.PwshComponent;
            string d = Inst.RtDir(c);
            File.WriteAllText(Path.Combine(d, PayloadExtractor.CompleteMarker), "{\"zipMd5\":\"00000000000000000000000000000000\"}");
            var q = Inst.QuickCheck();
            var pc = q.Components.First(x => x.Component == c);
            Assert.True(pc.Result.MarkerBad, "marker flagged");
            var after = Inst.Repair(null, q);
            Assert.True(after.Ok, "re-extracted: " + after);
            Assert.True(Directory.GetDirectories(Inst.Paths.RtRoot, c.DirName + ".bad_*").Length == 1, "the old dir was renamed aside, not deleted");
            Assert.True(Inst.QuickCheck().Ok, "quick check ok");
        }

        private static void Refusals()
        {
            // unsafe path in the manifest
            var bad = TestPayload.Standard(C, "z");
            bad[0].Files.Add(new FileSpec("../escape.txt", "x"));
            string d1 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_bad1"), bad);
            Assert.Throws<InvalidDataException>(() => PayloadManifest.Load(new DirectoryPayloadSource(d1)), "'..' path refused");

            // a zip entry the manifest does not list
            var comps = TestPayload.Standard(C, "y");
            string d2 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_bad2"), comps, tweak: m =>
            {
                var c0 = (JObj)m.Arr("components")[0];
                c0.Arr("files").RemoveAt(c0.Arr("files").Count - 1);
                c0["fileCount"] = (long)c0.Arr("files").Count;
            });
            var src2 = new DirectoryPayloadSource(d2);
            var m2 = PayloadManifest.Load(src2);
            string parent = C.Sb.Dir("refuse_rt");
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src2, m2.Components[0], parent), "extra zip entry refused");
            Assert.True(!Directory.Exists(Path.Combine(parent, m2.Components[0].DirName)), "no final dir after a refusal");

            // a manifest md5 that does not match the zip content
            string d3 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_bad3"), TestPayload.Standard(C, "x"), tweak: m =>
            {
                var f0 = (JObj)((JObj)m.Arr("components")[1]).Arr("files")[0];
                f0["md5"] = "0123456789abcdef0123456789abcdef";
            });
            var src3 = new DirectoryPayloadSource(d3);
            var m3 = PayloadManifest.Load(src3);
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src3, m3.Components[1], parent), "md5 mismatch refused");
            Assert.True(!Directory.Exists(Path.Combine(parent, m3.Components[1].DirName)), "no final dir after the md5 refusal");

            // MAX_PATH: refused before anything is created
            var c4 = m3.Components[0];
            int saved = c4.MaxPathLen;
            c4.MaxPathLen = 250;
            string deep = Path.Combine(C.Sb.Root, "never_created_parent");
            Assert.Throws<PathTooLongException>(() => PayloadExtractor.EnsureExtracted(src3, c4, deep), "path budget refused");
            Assert.True(!Directory.Exists(deep), "the parent was not even created");
            c4.MaxPathLen = saved;
        }

        private static void OldVersions()
        {
            Inst.EnsureRuntimes();
            string rt = Inst.Paths.RtRoot;
            string old1 = Path.Combine(rt, "pwsh-7.5.0-deadbeef");
            Directory.CreateDirectory(Path.Combine(old1, "sub"));
            File.WriteAllText(Path.Combine(old1, "pwsh.exe"), "old");
            File.WriteAllText(Path.Combine(old1, "sub", "x.dll"), "old");
            string old2 = Path.Combine(rt, "py310-cafebabe");
            Directory.CreateDirectory(old2);
            File.WriteAllText(Path.Combine(old2, "py.exe"), "old");
            string target = C.Sb.Dir("junction_target_never_deleted");
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "must survive");
            string j = Path.Combine(old2, "link");
            Junction.Create(j, target);
            C.Sb.TrackJunction(j);
            var list = Inst.ListOldVersions(_longRoot);
            Assert.True(list.Any(v => v.Path == old1) && list.Any(v => v.Path == old2), "both old dirs listed");
            Assert.True(!list.Any(v => Inst.Manifest.RtComponents.Any(c => Path.GetFileName(v.Path) == c.DirName)), "current dirs never listed");
            var e = Assert.Throws<InvalidOperationException>(() => Inst.RemoveOldVersion(list.First(v => v.Path == old2)), "reparse point refused");
            Assert.Contains(e.Message, "reparse point", "message");
            Assert.True(File.Exists(Path.Combine(old2, "py.exe")) && File.Exists(Path.Combine(target, "sentinel.txt")), "nothing deleted on refusal");
            int n = Inst.RemoveOldVersion(list.First(v => v.Path == old1));
            Assert.Eq(2, n, "files removed");
            Assert.True(!Directory.Exists(old1), "old1 gone");
            Assert.Throws<InvalidOperationException>(() => Inst.RemoveOldVersion(new OldVersionInfo { Path = Path.Combine(rt, Inst.Manifest.PwshComponent.DirName), Parent = rt, Kind = "rt" }), "the current version is refused");
            Junction.Remove(j, target);
            File.Delete(Path.Combine(old2, "py.exe"));
            Directory.Delete(old2, false);
        }
    }
}
