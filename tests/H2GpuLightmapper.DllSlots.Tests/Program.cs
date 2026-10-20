using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace H2GpuLightmapper.DllSlots.Tests
{
    /// <summary>
    /// S0.4 unit tests. Usage:
    ///   H2GpuLightmapper.DllSlots.Tests.exe [--engine &lt;engine_v373 dir&gt;] [--sandbox &lt;scratch dir&gt;] [--filter &lt;substring&gt;]
    /// The two production DLLs are COPIED (read-only access to the source) into the sandbox and md5-verified; every test
    /// reads the copies only. Junction tests run inside the sandbox; nothing touches the real %LOCALAPPDATA% or a real
    /// drive root. Cleanup is non-recursive (see Sandbox).
    /// </summary>
    internal static class Program
    {
        private const string ProdMd5 = "aaf10a0fbc1b78ddefebe8248acddcca";
        private const string SkelMd5 = "f0fb73ac9cd42f8f716f26b64584759b";
        private const string SteamBase = @"G:\SteamLibrary\steamapps\common\H2EK\GPU Lightmap Work\";
        private const string SteamWork = @"G:\SteamLibrary\steamapps\common\H2EK\GPU Lightmap Work";
        private const string MineWork = @"G:\SteamLibrary\steamapps\common\H2EKMine\GPU Lightmap Work";
        private const string UneditedWork = @"G:\SteamLibrary\steamapps\common\H2EKLongPathFolder\GPU Lightmap Work";
        private const string DevLad = @"C:\Users\alice\AppData\Local";
        private const int Cp1252 = 1252, Cp932 = 932;

        private const string ProdV062Md5 = "b2370477f36db8c74e6749d5389f2a57";      // W1 2026-10-20: engine_v373's DLL after the 0.6.2 promotion

        private static byte[] Prod, Skel, ProdV062;
        private static string ProdPath, SkelPath, SandRoot;

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern bool CreateDirectoryA(string path, IntPtr sa);

        private static int Main(string[] args)
        {
            string engine = Environment.GetEnvironmentVariable("H2LM_TEST_ENGINE");   // <H2EK_ROOT>\GPU Lightmap Work\engine_v373 (read only)
            string sandbox = Path.Combine(Path.GetTempPath(), "s04", Guid.NewGuid().ToString("N").Substring(0, 4));
            string filter = null;
            // W1: --engine must hold the PRE-promotion DLLs (aaf10a0f / f0fb73ac: a copy of the old v014 DLL beside the skeleton DLL is enough);
            // --engine-v062 (or H2LM_TEST_ENGINE_V062) is the promoted engine_v373 (b2370477) for the "promoted-production layout" test
            string engineV062 = Environment.GetEnvironmentVariable("H2LM_TEST_ENGINE_V062");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--engine-v062" && i + 1 < args.Length) engineV062 = args[++i];
                else if (args[i] == "--engine" && i + 1 < args.Length) engine = args[++i];
                else if (args[i] == "--sandbox" && i + 1 < args.Length) sandbox = args[++i];
                else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else { Console.Error.WriteLine("unknown argument " + args[i]); return 2; }
            }
            if (string.IsNullOrEmpty(engine)) { Console.Error.WriteLine("pass --engine DIR or set H2LM_TEST_ENGINE (the production engine_v373 folder; it is only read)"); return 2; }
            Console.WriteLine("S0.4 DllSlots tests  engine=" + engine + "  sandbox=" + sandbox + "  systemACP=" + AnsiCodePage.System);

            using (var root = new Sandbox(Path.GetFullPath(sandbox)))
            {
                SandRoot = root.Root;
                try
                {
                    root.MkDirChain(SandRoot);
                    ProdPath = Path.Combine(SandRoot, "fx_prod.dll");
                    SkelPath = Path.Combine(SandRoot, "fx_skel.dll");
                    File.Copy(Path.Combine(engine, @"v014-inject-estimator\gpulm_estimator.dll"), ProdPath); root.TrackFile(ProdPath);
                    File.Copy(Path.Combine(engine, @"v043-tagwriter\dll\gpulm_estimator.dll"), SkelPath); root.TrackFile(SkelPath);
                    Prod = File.ReadAllBytes(ProdPath); Skel = File.ReadAllBytes(SkelPath);
                    if (!string.IsNullOrEmpty(engineV062))
                    {
                        string p62 = Path.Combine(SandRoot, "fx_prod_v062.dll");
                        File.Copy(Path.Combine(engineV062, @"v014-inject-estimator\gpulm_estimator.dll"), p62); root.TrackFile(p62);
                        ProdV062 = File.ReadAllBytes(p62);
                        if (DllSlotPatcher.Md5Hex(ProdV062) != ProdV062Md5) { Console.WriteLine("SETUP REFUSED: --engine-v062 DLL md5 is " + DllSlotPatcher.Md5Hex(ProdV062)); return 2; }
                    }
                    if (DllSlotPatcher.Md5Hex(Prod) != ProdMd5 || DllSlotPatcher.Md5Hex(Skel) != SkelMd5)
                    {
                        Console.WriteLine("SETUP REFUSED: fixture md5s are " + DllSlotPatcher.Md5Hex(Prod) + " / " + DllSlotPatcher.Md5Hex(Skel));
                        return 2;
                    }
                }
                catch (Exception e) { Console.WriteLine("SETUP FAILED: " + e); return 2; }

                var tests = new List<KeyValuePair<string, Action>>
                {
                    T("layout: discovery reproduces the known production layout", Layout_DiscoverMatchesKnown_Production),
                    T("layout: discovery reproduces the known skeleton layout", Layout_DiscoverMatchesKnown_Skeleton),
                    T("layout: promoted-production layout (b2370477) is discovered, pinned, and a patch changes only slot bytes", Layout_PromotedProduction),
                    T("layout: no relocated pointer targets a slot interior", Layout_NoInteriorPointers),
                    T("patch: ProdRoot base is a no-op (aaf10a0f / f0fb73ac unchanged)", Patch_ProdRoot_IsNoOp),
                    T("patch: Steam G:\\...\\H2EK fits and changes only slot bytes", Patch_SteamH2EK_Fits),
                    T("patch: exactly 60 bytes fits, 61 refused", Patch_60Fits_61Refused),
                    T("patch: input array is never modified", Patch_IsPure),
                    T("patch: refuses non-pristine / foreign / truncated input", Patch_RefusesNonPristine),
                    T("patch: cp932 DBCS base counts BYTES and round-trips", Patch_Cp932),
                    T("patch: default code page overload == explicit system ACP", Patch_DefaultCodePage),
                    T("base: validation rejects every malformed base", Base_Rejections),
                    T("discover: refuses a third copy of the path", Discover_RefusesThirdCopy),
                    T("discover: refuses a pointer into a slot interior", Discover_RefusesInteriorPointer),
                    T("identify: pristine, patched, and tampered", Identify_Cases),
                    T("file: PatchFile writes a verified copy, never in place", File_PatchAndVerify),
                    T("plan: ProdRoot and Steam H2EK are Direct", Plan_Direct),
                    T("plan: H2EKLongPathFolder (69 chars) takes the %LOCALAPPDATA% alias", Plan_Unedited_TakesAlias),
                    T("plan: non-ANSI or long %LOCALAPPDATA% falls back to the volume root", Plan_FallsBackToVolumeRoot),
                    T("plan: refuses when no candidate fits", Plan_Refuses),
                    T("plan: key6 is stable, case-insensitive, 6 hex", Plan_Key6),
                    T("junction: create / read / remove; sentinel survives", Junction_Lifecycle),
                    T("junction: remove refuses a plain directory", Junction_RemoveRefusesPlainDir),
                    T("junction: remove refuses a wrong expected target", Junction_RemoveRefusesWrongTarget),
                    T("junction: create refuses an existing path", Junction_CreateRefusesExisting),
                    T("junction: a dangling junction is removed without touching anything", Junction_Dangling),
                    T("alias e2e: long WORK -> junction -> patched slot -> farm_w200 lands in WORK", Alias_EndToEnd),
                    T("alias: a foreign object at the alias path is skipped, untouched", Alias_SkipsForeignObject),
                    T("alias: a short WORK needs no junction", Alias_DirectNoJunction),
                };

                int pass = 0, fail = 0, skip = 0;
                foreach (var t in tests)
                {
                    if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sw = Stopwatch.StartNew();
                    try { t.Value(); pass++; Console.WriteLine("PASS  " + t.Key + "  (" + sw.ElapsedMilliseconds + " ms)"); }
                    catch (TestSkip s) { skip++; Console.WriteLine("SKIP  " + t.Key + ": " + s.Message); }
                    catch (Exception e) { fail++; Console.WriteLine("FAIL  " + t.Key + ": " + e.GetType().Name + ": " + e.Message); if (!(e is TestFailure)) Console.WriteLine(e.StackTrace); }
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "RESULT {0} passed, {1} failed, {2} skipped", pass, fail, skip));
                root.Dispose();
                if (root.Leftovers.Count > 0) { Console.WriteLine("CLEANUP left " + root.Leftovers.Count + ":"); root.Leftovers.ForEach(l => Console.WriteLine("  " + l)); }
                // drop the shared %TEMP%\s04 parent when this was the last run in it (non-recursive; fails harmlessly otherwise)
                try { var p = Path.GetDirectoryName(SandRoot); if (Path.GetFileName(p) == "s04" && Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p, false); } catch (IOException) { }
                return fail == 0 ? 0 : 1;
            }
        }

        private static KeyValuePair<string, Action> T(string n, Action a) => new KeyValuePair<string, Action>(n, a);

        // ===================================================================== helpers

        /// <summary>Independent of the library: every differing byte must be inside one of the slots.</summary>
        private static int AssertDiffOnlyInSlots(byte[] a, byte[] b, SlotLayout l)
        {
            Assert.Eq(a.Length, b.Length, "length");
            int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                n++;
                bool inSlot = l.Slots.Any(s => i >= s.Offset && i < s.Offset + s.Length);
                Assert.True(inSlot, "byte 0x" + i.ToString("X") + " outside the slots differs");
            }
            return n;
        }

        private static void AssertSlotHolds(byte[] dll, SlotSpec s, byte[] want)
        {
            for (int i = 0; i < s.Length; i++)
            {
                byte w = i < want.Length ? want[i] : (byte)0;
                Assert.True(dll[s.Offset + i] == w, "slot " + s + " byte +" + i + " is 0x" + dll[s.Offset + i].ToString("X2") + ", want 0x" + w.ToString("X2"));
            }
        }

        // ===================================================================== layout

        private static void Layout_DiscoverMatchesKnown_Production()
        {
            var d = DllSlotPatcher.DiscoverLayout(Prod);
            Assert.True(d.SameSlotsAs(KnownSlotLayouts.Production), "discovered " + d + " vs known " + KnownSlotLayouts.Production);
            Assert.Eq(SlotLayoutSource.Discovered, d.Source, "source");
            Assert.Eq(ProdMd5, d.PristineMd5, "md5");
            Assert.True(ReferenceEquals(KnownSlotLayouts.Production, DllSlotPatcher.ResolveLayout(Prod)), "ResolveLayout returns the pinned known layout");
            Assert.Eq(0x6CBE0, KnownSlotLayouts.Production.Slots[0].Offset, "kBaseDir offset");
            Assert.Eq(0xB6608, KnownSlotLayouts.Production.Slots[1].Offset, "g_dirBuf offset");
        }

        private static void Layout_PromotedProduction()
        {
            if (ProdV062 == null) throw new TestSkip("no --engine-v062 / H2LM_TEST_ENGINE_V062 (the promoted engine_v373)");
            var d = DllSlotPatcher.DiscoverLayout(ProdV062);
            Assert.True(d.SameSlotsAs(KnownSlotLayouts.ProductionV062), "discovered " + d + " vs known " + KnownSlotLayouts.ProductionV062);
            Assert.Eq(SlotLayoutSource.Discovered, d.Source, "source");
            Assert.Eq(ProdV062Md5, d.PristineMd5, "md5");
            Assert.Eq((long)ProdV062.Length, KnownSlotLayouts.ProductionV062.FileSize, "size");
            Assert.True(ReferenceEquals(KnownSlotLayouts.ProductionV062, DllSlotPatcher.ResolveLayout(ProdV062)), "ResolveLayout returns the pinned known layout");
            var refs = DllSlotPatcher.VerifyLayoutAgainstImage(ProdV062, KnownSlotLayouts.ProductionV062);
            Assert.True(refs.All(x => x >= 1), "every slot start is referenced");
            var r = DllSlotPatcher.PatchDetailed(ProdV062, SteamBase, null, Cp1252);
            Assert.True(ReferenceEquals(r.Layout, KnownSlotLayouts.ProductionV062), "layout");
            int n = AssertDiffOnlyInSlots(ProdV062, r.Bytes, KnownSlotLayouts.ProductionV062);
            Assert.True(n > 0 && n <= 2 * 61, "changed " + n + " bytes");
            Assert.Eq(SteamBase, DllSlotPatcher.ReadSlotBase(r.Bytes, KnownSlotLayouts.ProductionV062, Cp1252), "read back");
            Assert.True(DllSlotPatcher.Repristine(r.Bytes, KnownSlotLayouts.ProductionV062).SequenceEqual(ProdV062), "repristine == input");
            var same = DllSlotPatcher.PatchDetailed(ProdV062, MineWork + "\\", null, Cp1252);
            Assert.Eq(ProdV062Md5, DllSlotPatcher.Md5Hex(same.Bytes), "the ProdRoot base is a no-op for the promoted DLL too");
        }

        private static void Layout_DiscoverMatchesKnown_Skeleton()
        {
            var d = DllSlotPatcher.DiscoverLayout(Skel);
            Assert.True(d.SameSlotsAs(KnownSlotLayouts.Skeleton), "discovered " + d + " vs known " + KnownSlotLayouts.Skeleton);
            Assert.True(ReferenceEquals(KnownSlotLayouts.Skeleton, DllSlotPatcher.ResolveLayout(Skel)), "ResolveLayout returns the pinned known layout");
            Assert.Eq(0x66F94, KnownSlotLayouts.Skeleton.Slots[0].Offset, "kBaseDir offset");
            Assert.Eq(0xA2400, KnownSlotLayouts.Skeleton.Slots[1].Offset, "g_dirBuf offset");
        }

        private static void Layout_NoInteriorPointers()
        {
            var p = DllSlotPatcher.VerifyLayoutAgainstImage(Prod, KnownSlotLayouts.Production);
            var s = DllSlotPatcher.VerifyLayoutAgainstImage(Skel, KnownSlotLayouts.Skeleton);
            Console.WriteLine("      start refs: production kBaseDir " + p[0] + ", g_dirBuf " + p[1] + "; skeleton kBaseDir " + s[0] + ", g_dirBuf " + s[1]);
            Assert.True(p.All(x => x >= 1) && s.All(x => x >= 1), "every slot start is referenced");
            var pe = new PeImage(Prod);
            Assert.Eq((ushort)0x14C, pe.Machine, "x86");
            Assert.Eq(0u, pe.CheckSum, "PE checksum is 0 (the loader never checks it; the patch leaves the header alone)");
        }

        // ===================================================================== patch

        private static void Patch_ProdRoot_IsNoOp()
        {
            var a = DllSlotPatcher.PatchDetailed(Prod, DllSlotPatcher.PristineBase, null, Cp1252);
            Assert.True(a.IsNoOp, "production no-op");
            Assert.Eq(ProdMd5, DllSlotPatcher.Md5Hex(a.Bytes), "production md5 after patch");
            Assert.Eq(ProdMd5, a.PatchedMd5, "reported md5");
            var b = DllSlotPatcher.PatchDetailed(Skel, MineWork + @"\", null, Cp1252);
            Assert.True(b.IsNoOp, "skeleton no-op");
            Assert.Eq(SkelMd5, DllSlotPatcher.Md5Hex(b.Bytes), "skeleton md5 after patch");
            Assert.True(!ReferenceEquals(a.Bytes, Prod), "a new array is returned even for a no-op");
        }

        private static void Patch_SteamH2EK_Fits()
        {
            Assert.Eq(56, SteamBase.Length, "Steam base length");
            foreach (var pair in new[] { Tuple.Create(Prod, KnownSlotLayouts.Production), Tuple.Create(Skel, KnownSlotLayouts.Skeleton) })
            {
                var r = DllSlotPatcher.PatchDetailed(pair.Item1, SteamBase, null, Cp1252);
                Assert.True(ReferenceEquals(r.Layout, pair.Item2), "layout");
                int n = AssertDiffOnlyInSlots(pair.Item1, r.Bytes, pair.Item2);
                Assert.True(n > 0 && n <= 2 * 61, "changed " + n + " bytes");
                Assert.Eq(n, r.ChangedBytes, "reported changed bytes");
                foreach (var s in pair.Item2.Slots) AssertSlotHolds(r.Bytes, s, Encoding.ASCII.GetBytes(SteamBase));
                Assert.Eq(SteamBase, DllSlotPatcher.ReadSlotBase(r.Bytes, pair.Item2, Cp1252), "read back");
                Assert.True(DllSlotPatcher.Repristine(r.Bytes, pair.Item2).SequenceEqual(pair.Item1), "repristine == input");
                Assert.True(r.PatchedMd5 != pair.Item2.PristineMd5, "md5 changed");
            }
        }

        private static void Patch_60Fits_61Refused()
        {
            string b60 = @"D:\" + new string('a', 56) + @"\";
            Assert.Eq(60, b60.Length, "len");
            var r = DllSlotPatcher.PatchDetailed(Prod, b60, null, Cp1252);
            foreach (var s in KnownSlotLayouts.Production.Slots) Assert.Eq((byte)0, r.Bytes[s.Offset + 60], "NUL at +60");
            Assert.Eq(b60, DllSlotPatcher.ReadSlotBase(r.Bytes, KnownSlotLayouts.Production, Cp1252), "read back");
            string b61 = @"D:\" + new string('a', 57) + @"\";
            var e = Assert.Throws<SlotBaseException>(() => DllSlotPatcher.PatchDetailed(Prod, b61, null, Cp1252), "61 bytes");
            Assert.True(e.Reason.Contains("61 bytes"), "reason names the byte count: " + e.Reason);
            string b3 = @"D:\x\";
            var r3 = DllSlotPatcher.PatchDetailed(Skel, b3, null, Cp1252);
            AssertDiffOnlyInSlots(Skel, r3.Bytes, KnownSlotLayouts.Skeleton);
            Assert.Eq(b3, DllSlotPatcher.ReadSlotBase(r3.Bytes, KnownSlotLayouts.Skeleton, Cp1252), "short base read back");
        }

        private static void Patch_IsPure()
        {
            var before = DllSlotPatcher.Md5Hex(Prod);
            DllSlotPatcher.PatchDetailed(Prod, SteamBase, null, Cp1252);
            DllSlotPatcher.PatchDetailed(Prod, @"C:\x\", KnownSlotLayouts.Production, Cp1252);
            Assert.Eq(before, DllSlotPatcher.Md5Hex(Prod), "input unchanged");
            Assert.Eq(ProdMd5, DllSlotPatcher.Md5Hex(File.ReadAllBytes(ProdPath)), "fixture file unchanged");
        }

        private static void Patch_RefusesNonPristine()
        {
            var patched = DllSlotPatcher.PatchDetailed(Prod, SteamBase, null, Cp1252).Bytes;
            var ap = Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(patched, @"C:\x\", null, Cp1252), "already patched");
            Assert.True(ap.Message.Contains("ALREADY slot-patched") && ap.Message.Contains(SteamBase), "names the current base: " + ap.Message);
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(patched, @"C:\x\", KnownSlotLayouts.Production, Cp1252), "already patched, explicit layout (md5 pin)");
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(Prod, @"C:\x\", KnownSlotLayouts.Skeleton, Cp1252), "wrong layout for this DLL");
            var flipped = (byte[])Prod.Clone(); flipped[0x400] ^= 0xFF;
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(flipped, @"C:\x\", KnownSlotLayouts.Production, Cp1252), "a byte outside the slots differs");
            var trunc = new byte[Prod.Length - 4096]; Buffer.BlockCopy(Prod, 0, trunc, 0, trunc.Length);
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(trunc, @"C:\x\", null, Cp1252), "truncated");
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(Encoding.ASCII.GetBytes("not a dll at all"), @"C:\x\", null, Cp1252), "not a PE");
            // a pinned-md5-free layout still demands pristine slots
            var unpinned = new SlotLayout("unpinned", null, Prod.Length, KnownSlotLayouts.Production.Slots, SlotLayoutSource.Discovered);
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(patched, @"C:\x\", unpinned, Cp1252), "unpinned layout over patched slots");
        }

        private static void Patch_Cp932()
        {
            string b60 = @"C:\" + new string('\u3042', 28) + @"\";        // 30 chars, 4 + 56 = 60 bytes in cp932
            string b62 = @"C:\" + new string('\u3042', 29) + @"\";        // 31 chars, 62 bytes
            var r = DllSlotPatcher.PatchDetailed(Prod, b60, null, Cp932);
            Assert.Eq(b60, DllSlotPatcher.ReadSlotBase(r.Bytes, KnownSlotLayouts.Production, Cp932), "cp932 read back");
            AssertDiffOnlyInSlots(Prod, r.Bytes, KnownSlotLayouts.Production);
            var e = Assert.Throws<SlotBaseException>(() => DllSlotPatcher.PatchDetailed(Prod, b62, null, Cp932), "62 bytes in cp932");
            Assert.True(e.Reason.Contains("62 bytes"), e.Reason);
            Assert.Throws<SlotBaseException>(() => DllSlotPatcher.PatchDetailed(Prod, b60, null, Cp1252), "hiragana is not cp1252");
        }

        private static void Patch_DefaultCodePage()
        {
            var a = DllSlotPatcher.Patch(Prod, SteamBase);
            var b = DllSlotPatcher.PatchDetailed(Prod, SteamBase, null, AnsiCodePage.System).Bytes;
            Assert.True(a.SequenceEqual(b), "same bytes");
        }

        private static void Base_Rejections()
        {
            var bad = new Dictionary<string, string>
            {
                { "", "empty" },
                { @"G:\SteamLibrary\H2EK", "backslash" },
                { @"SteamLibrary\H2EK\", "drive-absolute" },
                { @"\\server\share\x\", "drive-absolute" },
                { @"\\?\C:\x\", "drive-absolute" },
                { @"C:x\", "drive-absolute" },
                { @"C:\a/b\", "'/'" },
                { @"C:\a\..\b\", "relative" },
                { @"C:\a\.\b\", "relative" },
                { @"C:\a\\b\", "empty path segment" },
                { "C:\\a\tb\\", "control" },
                { @"C:\a*b\", "'*'" },
                { @"C:\a?b\", "'?'" },
                { @"C:\a:b\", "':'" },
                { @"C:\a \b\", "space or dot" },
                { @"C:\a.\b\", "space or dot" },
                { "C:\\\u03A9mega\\", "code page 1252" },
            };
            foreach (var kv in bad)
            {
                Assert.True(!DllSlotPatcher.TryValidateBase(kv.Key, Cp1252, out _, out var why), "accepted bad base '" + kv.Key + "'");
                Assert.True(why.IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase) >= 0, "'" + kv.Key + "' reason '" + why + "' lacks '" + kv.Value + "'");
            }
            foreach (var ok in new[] { @"C:\x\", @"g:\Mixed Case\with spaces\", @"C:\caf" + "\u00E9" + @"\", @"C:\a%b\", @"C:\PROGRA~1\x\" })
                Assert.True(DllSlotPatcher.TryValidateBase(ok, Cp1252, out _, out var why2), "rejected good base '" + ok + "': " + why2);
        }

        // ===================================================================== discover

        private static void Discover_RefusesThirdCopy()
        {
            var pe = new PeImage(Prod);
            var rdata = pe.Sections.First(s => s.Name == ".rdata");
            int at = -1;
            for (int i = (int)rdata.PointerToRawData; i + 64 < rdata.PointerToRawData + rdata.SizeOfRawData && at < 0; i += 4)
            {
                if (KnownSlotLayouts.Production.InAnySlot(i) || KnownSlotLayouts.Production.InAnySlot(i + 63)) continue;
                bool zero = true;
                for (int k = 0; k < 64 && zero; k++) zero = Prod[i + k] == 0;
                if (zero) at = i;
            }
            if (at < 0) throw new TestSkip("no 64-byte zero run in .rdata to plant a copy in");
            var m = (byte[])Prod.Clone();
            Buffer.BlockCopy(Encoding.ASCII.GetBytes(DllSlotPatcher.PristineBase), 0, m, at, 60);
            var e = Assert.Throws<SlotPatchException>(() => DllSlotPatcher.DiscoverLayout(m), "third copy");
            Assert.True(e.Message.Contains("exactly 2"), e.Message);
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchDetailed(m, @"C:\x\", null, Cp1252), "patch of a 3-copy build");
            // a copy that merely starts with the path (base + "farm_w%02d") is also a stale copy
            var m2 = (byte[])Prod.Clone();
            Buffer.BlockCopy(Encoding.ASCII.GetBytes(DllSlotPatcher.PristineBase + "farm"), 0, m2, at, 64);
            var e2 = Assert.Throws<SlotPatchException>(() => DllSlotPatcher.DiscoverLayout(m2), "prefix copy");
            Assert.True(e2.Message.Contains("outside the two slots"), e2.Message);
        }

        private static void Discover_RefusesInteriorPointer()
        {
            var pe = new PeImage(Prod);
            var kb = KnownSlotLayouts.Production.Slots[0];
            long kbRva = pe.FileOffsetToRva(kb.Offset);
            var site = pe.RelocatedPointerSites().First(s => s.Width == 4 && !KnownSlotLayouts.Production.InAnySlot(s.FileOffset) && !KnownSlotLayouts.Production.InAnySlot(s.FileOffset + 3));
            var m = (byte[])Prod.Clone();
            BitConverter.GetBytes((uint)(pe.ImageBase + (ulong)kbRva + 10)).CopyTo(m, (int)site.FileOffset);
            var e = Assert.Throws<SlotPatchException>(() => DllSlotPatcher.DiscoverLayout(m), "interior pointer");
            Assert.True(e.Message.Contains("relocated pointer"), e.Message);
            Assert.Throws<SlotPatchException>(() => DllSlotPatcher.VerifyLayoutAgainstImage(m, KnownSlotLayouts.Production), "verify with the known layout");
        }

        // ===================================================================== identify / file

        private static void Identify_Cases()
        {
            var a = DllSlotPatcher.Identify(Prod, null, Cp1252);
            Assert.True(a != null && a.IsPristine && ReferenceEquals(a.Layout, KnownSlotLayouts.Production), "pristine production");
            var b = DllSlotPatcher.Identify(DllSlotPatcher.PatchDetailed(Skel, SteamBase, null, Cp1252).Bytes, null, Cp1252);
            Assert.True(b != null && !b.IsPristine && ReferenceEquals(b.Layout, KnownSlotLayouts.Skeleton) && b.Base == SteamBase, "patched skeleton");
            var t = DllSlotPatcher.PatchDetailed(Prod, SteamBase, null, Cp1252).Bytes; t[0x500] ^= 1;
            Assert.True(DllSlotPatcher.Identify(t, null, Cp1252) == null, "patched + a byte outside the slots changed -> unknown");
            var u = DllSlotPatcher.PatchDetailed(Prod, SteamBase, null, Cp1252).Bytes; u[KnownSlotLayouts.Production.Slots[1].Offset + 100] = 0x41;
            Assert.True(DllSlotPatcher.Identify(u, null, Cp1252) == null, "garbage after the NUL inside a slot -> unknown");
        }

        private static void File_PatchAndVerify()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "file")))
            {
                sb.MkDirChain(sb.Root);
                string dst = Path.Combine(sb.Root, "gpulm_estimator.dll");
                sb.TrackFile(dst);
                var r = DllSlotPatcher.PatchFile(ProdPath, dst, SteamBase, null, false, Cp1252);
                Assert.Eq(r.PatchedMd5, DllSlotPatcher.Md5Hex(File.ReadAllBytes(dst)), "written md5");
                Assert.True(DllSlotPatcher.VerifyFile(dst, SteamBase, out var why, null, Cp1252), "verify: " + why);
                Assert.True(!DllSlotPatcher.VerifyFile(dst, DllSlotPatcher.PristineBase, out why, null, Cp1252) && why.Contains("expected"), "verify wrong base: " + why);
                Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchFile(ProdPath, dst, @"C:\x\", null, false, Cp1252), "dest exists");
                Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchFile(ProdPath, ProdPath, @"C:\x\", null, true, Cp1252), "in place");
                Assert.Throws<SlotPatchException>(() => DllSlotPatcher.PatchFile(dst, Path.Combine(sb.Root, "x.dll"), @"C:\x\", null, false, Cp1252), "source is not pristine");
                Assert.True(!File.Exists(Path.Combine(sb.Root, "x.dll")), "nothing written on refusal");
                var r2 = DllSlotPatcher.PatchFile(ProdPath, dst, @"C:\x\", null, true, Cp1252);
                Assert.True(DllSlotPatcher.VerifyFile(dst, @"C:\x\", out why, null, Cp1252), "overwrite: " + why);
                Assert.Eq(1, Directory.GetFiles(sb.Root).Length, "no temp files left: " + string.Join(",", Directory.GetFiles(sb.Root)));
                Assert.Eq(ProdMd5, DllSlotPatcher.Md5Hex(File.ReadAllBytes(ProdPath)), "source untouched");
            }
        }

        // ===================================================================== plan (pure)

        private static void Plan_Direct()
        {
            var a = SlotAliasPlanner.Plan(MineWork, DevLad, null, Cp1252);
            Assert.Eq(SlotBaseKind.Direct, a.Kind, "ProdRoot kind");
            Assert.Eq(DllSlotPatcher.PristineBase, a.SlotBase, "ProdRoot slot == pristine");
            var b = SlotAliasPlanner.Plan(SteamWork + @"\", DevLad, null, Cp1252);
            Assert.Eq(SlotBaseKind.Direct, b.Kind, "Steam kind");
            Assert.Eq(SteamBase, b.SlotBase, "Steam slot");
            Assert.True(a.AliasDir == null && a.Candidates.Count == 1, "no alias candidates when Direct fits");
        }

        private static void Plan_Unedited_TakesAlias()
        {
            Assert.Eq(69, UneditedWork.Length, "WORK is 69 chars (70 with the slash)");
            var p = SlotAliasPlanner.Plan(UneditedWork, DevLad, null, Cp1252);
            string k6 = SlotAliasPlanner.Key6(UneditedWork);
            Assert.Eq(SlotBaseKind.AliasLocalAppData, p.Kind, "kind");
            Assert.Eq(DevLad + @"\H2GLM\" + k6 + @"\", p.SlotBase, "slot base");
            Assert.Eq(DevLad + @"\H2GLM\" + k6, p.AliasDir, "alias dir");
            Assert.Eq(UneditedWork, p.WorkDir, "H2LM_WORK stays the REAL path");
            Assert.True(p.SlotBase.Length <= 60, "fits");
            Assert.True(p.Rejected.Count == 1 && p.Rejected[0].Contains("70 bytes"), "direct rejected for 70 bytes: " + string.Join(" | ", p.Rejected));
            Assert.Eq(2, p.Candidates.Count, "both alias candidates usable");
            Assert.Eq(@"G:\H2GLM\" + k6 + @"\", p.Candidates[1].SlotBase, "volume-root candidate");
            Console.WriteLine("      " + UneditedWork + " -> " + p.SlotBase + " (then " + p.Candidates[1].SlotBase + ")");
            // the real-environment overload agrees on the kind
            var real = SlotAliasPlanner.Plan(UneditedWork);
            Assert.True(real.IsAlias, "real-environment plan is an alias: " + real.SlotBase);
        }

        private static void Plan_FallsBackToVolumeRoot()
        {
            string k6 = SlotAliasPlanner.Key6(UneditedWork);
            var a = SlotAliasPlanner.Plan(UneditedWork, "C:\\Users\\\u0141ukasz\u03A9\\AppData\\Local", null, Cp1252);
            Assert.Eq(SlotBaseKind.AliasVolumeRoot, a.Kind, "non-ANSI user name");
            Assert.Eq(@"G:\H2GLM\" + k6 + @"\", a.SlotBase, "volume-root slot");
            Assert.True(a.Rejected.Any(r => r.Contains("%LOCALAPPDATA%") && r.Contains("code page")), string.Join(" | ", a.Rejected));
            var b = SlotAliasPlanner.Plan(UneditedWork, @"C:\Users\" + new string('n', 40) + @"\AppData\Local", null, Cp1252);
            Assert.Eq(SlotBaseKind.AliasVolumeRoot, b.Kind, "long user name");
            var c = SlotAliasPlanner.Plan(UneditedWork, null, null, Cp1252);
            Assert.Eq(SlotBaseKind.AliasVolumeRoot, c.Kind, "no LOCALAPPDATA");
        }

        private static void Plan_Refuses()
        {
            var e = Assert.Throws<SlotAliasRefusedException>(() =>
                SlotAliasPlanner.Plan(UneditedWork, "C:\\Users\\\u03A9\\AppData\\Local", @"C:\" + new string('v', 50), Cp1252), "nothing fits");
            Assert.True(e.Message.Contains("WORK") && e.Message.Contains("volume-root alias"), e.Message);
            var deep = @"Z:\" + string.Join(@"\", Enumerable.Repeat("deep folder name", 5)) + @"\GPU Lightmap Work";
            Assert.Throws<SlotAliasRefusedException>(() => SlotAliasPlanner.Plan(deep, "C:\\\u03A9", @"Z:\" + new string('q', 60), Cp1252), "deep path, nothing fits");
        }

        private static void Plan_Key6()
        {
            string k = SlotAliasPlanner.Key6(UneditedWork);
            Assert.True(k.Length == 6 && k.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "6 lowercase hex: " + k);
            Assert.Eq(k, SlotAliasPlanner.Key6(UneditedWork.ToLowerInvariant()), "case-insensitive");
            Assert.Eq(k, SlotAliasPlanner.Key6(UneditedWork + @"\"), "trailing slash");
            Assert.True(k != SlotAliasPlanner.Key6(MineWork), "different dirs differ");
        }

        // ===================================================================== junction (sandbox)

        private static void Junction_Lifecycle()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "jl")))
            {
                string target = sb.Dir("target");
                string sentinel = sb.File(@"target\sentinel.txt", "sentinel-" + Guid.NewGuid());
                string content = File.ReadAllText(sentinel);
                string link = Path.Combine(sb.Root, "link");
                Junction.Create(link, target);
                sb.TrackJunction(link);
                Assert.True(Junction.IsJunction(link), "is a junction");
                Assert.True(Junction.TryGetTarget(link, out var t) && string.Equals(t, target, StringComparison.OrdinalIgnoreCase), "target reads back: " + t);
                Assert.True(Junction.SameDirectory(link, target), "same directory object");
                Assert.Eq(content, File.ReadAllText(Path.Combine(link, "sentinel.txt")), "sentinel visible through the link");
                string through = Path.Combine(link, "written_through_link.txt");
                File.WriteAllText(through, "x");
                sb.TrackFile(Path.Combine(target, "written_through_link.txt"));
                Assert.True(File.Exists(Path.Combine(target, "written_through_link.txt")), "write through the link lands in the target");
                Junction.Remove(link, target);
                Assert.True(!Junction.PathExists(link), "link gone");
                Assert.Eq(content, File.ReadAllText(sentinel), "sentinel survives RemoveDirectory of the link");
                Assert.True(File.Exists(Path.Combine(target, "written_through_link.txt")), "target content survives");
                Assert.True(!Junction.IsJunction(target), "target is a plain directory");
            }
        }

        private static void Junction_RemoveRefusesPlainDir()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "jp")))
            {
                string plain = sb.Dir("plain");
                string sentinel = sb.File(@"plain\sentinel.txt", "keep me");
                var e = Assert.Throws<JunctionException>(() => Junction.Remove(plain), "plain dir");
                Assert.True(e.Message.Contains("not a directory junction"), e.Message);
                Assert.True(Directory.Exists(plain) && File.ReadAllText(sentinel) == "keep me", "plain dir and sentinel intact");
                Assert.Throws<JunctionException>(() => Junction.Remove(sentinel), "a file");
                Assert.True(File.Exists(sentinel), "file intact");
                Assert.Throws<JunctionException>(() => Junction.Remove(Path.Combine(sb.Root, "missing")), "missing path");
            }
        }

        private static void Junction_RemoveRefusesWrongTarget()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "jw")))
            {
                string a = sb.Dir("a"), b = sb.Dir("b");
                string link = Path.Combine(sb.Root, "link");
                Junction.Create(link, a); sb.TrackJunction(link);
                Assert.Throws<JunctionException>(() => Junction.Remove(link, b), "expected b, points at a");
                Assert.True(Junction.IsJunction(link), "still there");
                Junction.Remove(link, a.ToUpperInvariant() + @"\");
                Assert.True(!Junction.PathExists(link), "removed with a case/slash-different but equal target");
            }
        }

        private static void Junction_CreateRefusesExisting()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "jc")))
            {
                string target = sb.Dir("target");
                string plain = sb.Dir("plain");
                string file = sb.File("file.txt", "f");
                Assert.Throws<JunctionException>(() => Junction.Create(plain, target), "existing dir");
                Assert.True(Directory.Exists(plain) && !Junction.IsJunction(plain), "existing dir untouched");
                Assert.Throws<JunctionException>(() => Junction.Create(file, target), "existing file");
                Assert.True(File.ReadAllText(file) == "f", "file untouched");
                Assert.Throws<JunctionException>(() => Junction.Create(Path.Combine(sb.Root, "x"), Path.Combine(sb.Root, "nope")), "missing target");
                Assert.True(!Junction.PathExists(Path.Combine(sb.Root, "x")), "nothing created for a missing target");
                Assert.Throws<JunctionException>(() => Junction.Create(Path.Combine(target, "inner"), target), "cycle");
                Assert.True(!Junction.PathExists(Path.Combine(target, "inner")), "no cycle created");
            }
        }

        private static void Junction_Dangling()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "jd")))
            {
                sb.MkDirChain(sb.Root);
                string target = Path.Combine(sb.Root, "gone");
                Directory.CreateDirectory(target);
                string link = Path.Combine(sb.Root, "link");
                Junction.Create(link, target); sb.TrackJunction(link);
                Directory.Delete(target, false);
                Assert.True(Junction.TryGetTarget(link, out var t) && t.EndsWith("gone", StringComparison.Ordinal), "dangling target readable");
                Assert.True(!Junction.SameDirectory(link, target), "dangling does not resolve");
                Junction.Remove(link, target);
                Assert.True(!Junction.PathExists(link), "dangling junction removed");
            }
        }

        // ===================================================================== alias end-to-end (sandbox)

        private static void Alias_EndToEnd()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "e")))
            {
                string work = sb.Dir(@"H2EKLongPathFolder\GPU Lightmap Work");
                string lad = sb.Dir("L"), vol = sb.Dir("V");
                Assert.True(work.Length + 1 > 60, "sandbox WORK is long enough to need an alias: " + work.Length);
                SlotBasePlan plan;
                try { plan = SlotAliasPlanner.Plan(work, lad, vol, Cp1252); }
                catch (SlotAliasRefusedException e) { throw new TestSkip("sandbox path too long for an alias: " + e.Message); }
                Assert.Eq(SlotBaseKind.AliasLocalAppData, plan.Kind, "kind");

                sb.TrackDir(Path.Combine(lad, SlotAliasPlanner.AliasFolderName));
                var r = SlotAlias.Ensure(work, lad, vol, Cp1252);
                sb.TrackJunction(r.AliasDir);
                Assert.Eq(AliasState.Created, r.State, "created");
                Assert.Eq(plan.SlotBase, r.SlotBase, "slot base as planned");
                Assert.Eq(Junction.NormalizeDir(work), r.WorkDir, "WORK stays real");
                Assert.True(Junction.SameDirectory(r.AliasDir, work), "alias resolves to WORK");
                var r2 = SlotAlias.Ensure(work, lad, vol, Cp1252);
                Assert.Eq(AliasState.Reused, r2.State, "second Ensure reuses");
                Assert.Eq(r.SlotBase, r2.SlotBase, "same slot");

                string dll = Path.Combine(sb.Root, "gpulm_estimator.dll");
                sb.TrackFile(dll);
                DllSlotPatcher.PatchFile(ProdPath, dll, r.SlotBase, null, false, Cp1252);
                Assert.True(DllSlotPatcher.VerifyFile(dll, r.SlotBase, out var why, null, Cp1252), why);
                string slot = DllSlotPatcher.ReadSlotBase(File.ReadAllBytes(dll), KnownSlotLayouts.Production, Cp1252);

                // exactly what detect_farm_worker does with kBaseDir: "%sfarm_w%02d\\" (worker 200), CreateDirectoryA x2
                string farm = string.Format(CultureInfo.InvariantCulture, "{0}farm_w{1:D2}\\", slot, 200);
                Assert.True(CreateDirectoryA(farm, IntPtr.Zero), "CreateDirectoryA(" + farm + ") err " + Marshal.GetLastWin32Error());
                sb.TrackDir(Path.Combine(work, "farm_w200"));
                Assert.True(CreateDirectoryA(farm + "v014-inject-estimator", IntPtr.Zero), "CreateDirectoryA(sub) err " + Marshal.GetLastWin32Error());
                sb.TrackDir(Path.Combine(work, @"farm_w200\v014-inject-estimator"));
                File.WriteAllText(farm + @"v014-inject-estimator\gpulm.log", "[GPULM v031] FARM WORKER 200 of 201 -> all capture/inject I/O in " + farm);
                sb.TrackFile(Path.Combine(work, @"farm_w200\v014-inject-estimator\gpulm.log"));
                Assert.True(File.Exists(Path.Combine(work, @"farm_w200\v014-inject-estimator\gpulm.log")), "the farm dir landed in the REAL WORK through the alias");
                Assert.True(!Junction.IsJunction(Path.Combine(work, "farm_w200")), "farm_w200 is a real directory");
                Console.WriteLine("      WORK(" + work.Length + ") -> slot " + slot + " (" + slot.Length + ")");
            }
        }

        private static void Alias_SkipsForeignObject()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "f")))
            {
                string work = sb.Dir(@"H2EKLongPathFolder\GPU Lightmap Work");
                string lad = sb.Dir("L"), vol = sb.Dir("V");
                SlotBasePlan plan;
                try { plan = SlotAliasPlanner.Plan(work, lad, vol, Cp1252); }
                catch (SlotAliasRefusedException e) { throw new TestSkip("sandbox path too long for an alias: " + e.Message); }
                string squatter = plan.Candidates[0].AliasDir;
                sb.MkDirChain(squatter);
                string keep = sb.File(Path.Combine(squatter, "keep.txt"), "mine");
                sb.TrackDir(Path.Combine(vol, SlotAliasPlanner.AliasFolderName));
                var r = SlotAlias.Ensure(work, lad, vol, Cp1252);
                sb.TrackJunction(r.AliasDir);
                Assert.Eq(SlotBaseKind.AliasVolumeRoot, r.Kind, "fell through to the volume-root candidate");
                Assert.True(r.Notes.Any(n => n.Contains("not a junction")), string.Join(" | ", r.Notes));
                Assert.True(!Junction.IsJunction(squatter) && File.ReadAllText(keep) == "mine", "squatter untouched");
                Assert.True(Junction.SameDirectory(r.AliasDir, work), "volume alias resolves to WORK");
            }
        }

        private static void Alias_DirectNoJunction()
        {
            using (var sb = new Sandbox(Path.Combine(SandRoot, "d")))
            {
                string work = sb.Dir("W");
                string lad = sb.Dir("L");
                if (work.Length + 1 > 60) throw new TestSkip("sandbox too long for a Direct WORK");
                var r = SlotAlias.Ensure(work, lad, null, Cp1252);
                Assert.Eq(SlotBaseKind.Direct, r.Kind, "direct");
                Assert.Eq(AliasState.NotNeeded, r.State, "no junction");
                Assert.Eq(work + @"\", r.SlotBase, "slot = WORK\\");
                Assert.True(!Directory.Exists(Path.Combine(lad, SlotAliasPlanner.AliasFolderName)), "nothing created under LOCALAPPDATA");
            }
        }
    }
}
