using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class PreflightTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("preflight: ProdRoot (production, read-only) passes; status line; Direct slot", Mine);
            yield return T("preflight: Steam H2EK (read-only): supported tool, no bounds DB -> 'will be copied'", SteamStock);
            yield return T("preflight: H2EKLongPathFolder (read-only): slot needs the alias, still passes", Unedited);
            yield return T("preflight: tool_fast classification (supported / known other build / unknown / missing)", ToolClassify);
            yield return T("preflight: fake root: non-stock tool, no tags, no fbxsdk, no VC++ x86 / d3dx -> refusals", FakeRootRefusals);
            yield return T("preflight: path text: braces, brackets, non-ASCII refused", PathText);
            yield return T("preflight: path budget refused on a deep root", Budget);
            yield return T("preflight: write probe on a scratch root (leaves nothing)", WriteProbe);
            yield return T("preflight: bounds DB copied only if absent; never overwritten", BoundsDb);
            yield return T("preflight: gpucheck JSON parse (ok and no-FP64) and a real gpucheck run", GpuCheckTests);
            yield return T("preflight: pwsh policy / language mode through the bundled pwsh", PwshPolicy);
            yield return T("preflight: tool_fast smoke run of a COPY in a scratch root (10 s, hidden)", ToolSmoke);
        }

        private static PreflightOptions ReadOnly()
        {
            return new PreflightOptions { ProbeWrite = false, RunToolSmoke = false, InstDirName = "0.1.0-12345678", InstMaxPathLen = 90,
                                          AliasLocalAppData = C.Short.Dir("PL"), AliasVolumeRoot = C.Short.Dir("PV") };
        }

        private static void Mine()
        {
            var listing = Directory.GetFileSystemEntries(C.MineRoot).Length;
            var r = PreflightChecks.Run(C.MineRoot, ReadOnly());
            Assert.True(r.Ok, "ProdRoot passes: " + string.Join(" | ", r.Failures));
            Assert.Eq("tool_fast.exe 2023.06.20 (3a889d37) \u2014 supported", r.Tool.StatusLine, "status line");
            Assert.Eq(CheckLevel.Ok, r.Get("prereq.vcx86").Level, "VC++ x86");
            Assert.Eq(CheckLevel.Ok, r.Get("h2ek.data").Level, "data\\ present");
            Assert.Eq(CheckLevel.Ok, r.Get("fs.ntfs").Level, "NTFS");
            Assert.Eq(CheckLevel.Ok, r.Get("path.ascii").Level, "ASCII");
            Assert.Eq(SlotBaseKind.Direct, r.SlotPlan.Kind, "ProdRoot WORK fits the slot");
            Assert.Eq(DllSlotPatcher.PristineBase, r.SlotPlan.SlotBase, "slot base == the pristine string (no-op patch)");
            // SF1 2026-10-20: the live DB has since been extended by the scenery scanner (no longer the shipped 6814f29a); an existing
            // DB is kept as is and reported as Info, which is the rule under test
            bool shippedDb = SafeFs.Md5File(Path.Combine(C.MineRoot, "h2v_scenery_bounds.db")).StartsWith("6814f29a", StringComparison.Ordinal);
            Assert.Eq(shippedDb ? CheckLevel.Ok : CheckLevel.Info, r.Get("boundsdb").Level, "bounds DB present (" + (shippedDb ? "the shipped 6814f29a" : "an extended one, kept as is") + ")");
            Assert.True(r.Get("fs.writable") == null && r.Get("tool.smoke") == null, "no write probe / no tool run on production");
            Assert.Eq(listing, Directory.GetFileSystemEntries(C.MineRoot).Length, "nothing created in the production root");
            Assert.Contains(r.ToJson(), "\"ok\": true", "json");
        }

        private static void SteamStock()
        {
            string lib = Environment.GetEnvironmentVariable("H2LM_TEST_STEAM_LIB");
            string root = string.IsNullOrEmpty(lib) ? null : Path.Combine(lib, @"steamapps\common\H2EK");
            if (root == null || !Directory.Exists(root)) throw new TestSkip("no Steam H2EK (set H2LM_TEST_STEAM_LIB)");
            var r = PreflightChecks.Run(root, ReadOnly());
            Assert.Eq(ToolVerdict.Supported, r.Tool.Verdict, "Steam's tool_fast is the supported build");
            string db = Path.Combine(root, PreflightChecks.BoundsDbName);
            if (File.Exists(db))
            {
                // R1: the V6 gate T5 run (2026-10-14) let the exe copy the shipped DB into this root (recorded in tests\t6); the
                // read-only preflight now sees it as present
                Assert.Eq(CheckLevel.Ok, r.Get("boundsdb").Level, "bounds DB present (copied by the exe in V6): " + r.Get("boundsdb").Message);
                Assert.True(SafeFs.Md5File(db).StartsWith(PreflightChecks.BoundsDbMd5, StringComparison.OrdinalIgnoreCase), "it is the shipped 6814f29a");
                return;
            }
            Assert.Eq(CheckLevel.Info, r.Get("boundsdb").Level, "no bounds DB: info");
            Assert.Contains(r.Get("boundsdb").Message, "will be copied", "copy note");
            Assert.True(!File.Exists(db), "preflight itself copied nothing");
        }

        private static void Unedited()
        {
            string root = Environment.GetEnvironmentVariable("H2LM_TEST_LONGPATH_ROOT");
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new TestSkip("no long-path H2EK (set H2LM_TEST_LONGPATH_ROOT to a stock H2EK whose WORK folder is 70 characters)");
            var r = PreflightChecks.Run(root, ReadOnly());
            Assert.True(r.SlotPlan.IsAlias, "alias needed (WORK\\ is 70 chars)");
            Assert.True(r.SlotPlan.SlotBase.StartsWith(Path.Combine(C.Short.Root, "PL"), StringComparison.OrdinalIgnoreCase), "planned under the sandbox LocalAppData");
            Assert.Eq(CheckLevel.Ok, r.Get("path.slot").Level, "slot ok via alias");
            Assert.True(!Directory.Exists(Path.Combine(C.Short.Root, "PL", "H2GLM")), "planning created nothing");
        }

        private static void ToolClassify()
        {
            var s = ToolFast.Classify(new ToolIdentity { Md5 = ToolFast.SupportedMd5, Size = ToolFast.SupportedSize });
            Assert.Eq(ToolVerdict.Supported, s.Verdict, "supported");
            var k = ToolFast.Classify(new ToolIdentity { Md5 = "8df7d90caacd84347d2315520a4ea40b", Size = 8000000 });
            Assert.Eq(ToolVerdict.KnownUnsupported, k.Verdict, "known other build");
            Assert.Contains(k.StatusLine, "address-specific to build 2023.06.20", "message");
            var u = ToolFast.Classify(new ToolIdentity { Md5 = "ffffffffffffffffffffffffffffffff", Size = 123 });
            Assert.Eq(ToolVerdict.Unknown, u.Verdict, "unknown");
            Assert.Contains(u.StatusLine, "not a stock tool_fast", "message");
            var sz = ToolFast.Classify(new ToolIdentity { Md5 = ToolFast.SupportedMd5, Size = 1 });
            Assert.Eq(ToolVerdict.Unknown, sz.Verdict, "md5 right but size wrong is not accepted");
            Assert.Eq(ToolVerdict.Missing, ToolFast.Identify(C.Sb.Dir("no_tool_here")).Verdict, "missing");
        }

        private static void FakeRootRefusals()
        {
            string root = C.Sb.Dir("fakeroot1");
            File.WriteAllBytes(Path.Combine(root, "tool_fast.exe"), new byte[] { 1, 2, 3 });
            var o = ReadOnly();
            o.SystemX86Dir = C.Sb.Dir("empty_syswow64");
            var r = PreflightChecks.Run(root, o);
            Assert.True(!r.Ok, "refused");
            Assert.Eq(CheckLevel.Fail, r.Get("tool.build").Level, "tool");
            Assert.Contains(r.Get("tool.build").Message, "not a stock tool_fast", "tool message");
            Assert.Eq(CheckLevel.Fail, r.Get("h2ek.tags").Level, "tags");
            Assert.Eq(CheckLevel.Fail, r.Get("h2ek.data").Level, "R1: a missing data\\ is a FAIL (tool_fast refuses -data_dir data)");
            Assert.Contains(r.Get("h2ek.data").Message, "create an empty folder named data", "data message");
            Assert.Eq(CheckLevel.Fail, r.Get("h2ek.fbxsdk").Level, "fbxsdk");
            Assert.Eq(CheckLevel.Fail, r.Get("prereq.vcx86").Level, "vc++");
            Assert.Contains(r.Get("prereq.vcx86").Message, "Visual C++ 2015-2022 x86 redistributable", "vc message");
            Assert.Eq(CheckLevel.Fail, r.Get("prereq.d3dx11").Level, "d3dx");
            Assert.Contains(r.Get("prereq.d3dx11").Message, "June 2010", "d3dx message");
            Assert.Eq(CheckLevel.Fail, PreflightChecks.Run(Path.Combine(root, "missing_dir"), o).Get("root.exists").Level, "missing root");
        }

        private static void PathText()
        {
            foreach (var leaf in new[] { "brace{1}root", "brack[1]root", "unicod\u00e9root" })
            {
                string root = C.Sb.Dir(leaf);
                var r = PreflightChecks.Run(root, ReadOnly());
                var item = r.Get("path.chars") ?? r.Get("path.ascii");
                Assert.True(item != null && item.Level == CheckLevel.Fail, leaf + " refused");
            }
            // review fix (finding 23): an H2EK at a drive root is refused (read-only checks of C:\ itself)
            string sysDrive = Path.GetPathRoot(Environment.SystemDirectory);
            Assert.True(ShellPaths.IsDriveRoot(sysDrive) && ShellPaths.IsDriveRoot(sysDrive.TrimEnd('\\') + @"\") && !ShellPaths.IsDriveRoot(C.MineRoot), "IsDriveRoot");
            Assert.Eq(Path.Combine(sysDrive, "GPU Lightmap Work"), ShellPaths.WorkOf(sysDrive), "WorkOf keeps the drive root's backslash (never the relative 'C:GPU Lightmap Work')");
            var dr = PreflightChecks.Run(sysDrive, ReadOnly());
            Assert.True(dr.Get("path.root") != null && dr.Get("path.root").Level == CheckLevel.Fail, "a drive-root H2EK is refused: " + string.Join(" | ", dr.Items));
            // X1: a payload whose gpu_bake has the P1b handling (features.driveRoot) lets the same root through (an info, not a failure)
            var allow = ReadOnly(); allow.AllowDriveRoot = true;
            var dok = PreflightChecks.Run(sysDrive, allow);
            Assert.True(dok.Get("path.root") != null && dok.Get("path.root").Level == CheckLevel.Info, "a drive-root H2EK is allowed with AllowDriveRoot: " + string.Join(" | ", dok.Items));
            Assert.Eq(sysDrive, ShellPaths.NormalizeRoot(sysDrive), "NormalizeRoot keeps the drive root's backslash");
            Assert.Eq(sysDrive, ShellPaths.NormalizeRoot(sysDrive.TrimEnd('\\') + "/"), "... from D:/ too");
            Assert.Eq(C.MineRoot.TrimEnd('\\'), ShellPaths.NormalizeRoot(C.MineRoot.TrimEnd('\\') + @"\"), "a folder loses its trailing backslash");
        }

        private static void Budget()
        {
            string deep = C.Sb.Dir(Path.Combine("deep", new string('d', 60), new string('e', 60)));
            var o = ReadOnly();
            o.ScenarioRel = @"scenarios\multi\a_rather_long_scenario_folder\a_rather_long_scenario_name";
            o.Bsp = "a_long_bsp_name";
            var r = PreflightChecks.Run(deep, o);
            var b = r.Get("path.budget");
            Assert.Eq(CheckLevel.Fail, b.Level, "budget refused (" + b.Message + ")");
            Assert.True(b.Detail.Length >= PathBudget.Limit, "the example path is the long one");
            var ok = PreflightChecks.Run(C.MineRoot, ReadOnly()).Get("path.budget");
            Assert.Eq(CheckLevel.Ok, ok.Level, "ProdRoot within budget: " + ok.Message);
            var o2 = ReadOnly();
            o2.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o2.Bsp = "cyclotron";
            Assert.Eq(CheckLevel.Ok, PreflightChecks.Run(C.MineRoot, o2).Get("path.budget").Level, "cyclotron on ProdRoot fits");
            string mid = C.Short.Dir("warnroot_" + new string('m', 12));   // nominal map names overflow, INST still fits
            var w = PreflightChecks.Run(mid, ReadOnly()).Get("path.budget");
            Assert.Eq(CheckLevel.Warn, w.Level, "without a scenario an overrun is a warning (" + w.Message + ")");
            var o3 = ReadOnly();
            o3.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o3.Bsp = "cyclotron";
            Assert.Eq(CheckLevel.Ok, PreflightChecks.Run(mid, o3).Get("path.budget").Level, "... and a short real map then fits");
        }

        private static void WriteProbe()
        {
            string root = C.Sb.Dir("writeroot");
            var o = ReadOnly();
            o.ProbeWrite = true;
            var r = PreflightChecks.Run(root, o);
            Assert.Eq(CheckLevel.Ok, r.Get("fs.writable").Level, "writable");
            Assert.True(!File.Exists(Path.Combine(root, "GPU Lightmap Work", ".write_probe")), "probe removed");
        }

        private static void BoundsDb()
        {
            string root = C.Sb.Dir("boundsroot");
            var a = PreflightChecks.EnsureBoundsDb(root, C.FxBoundsDb);
            Assert.Eq(PreflightChecks.BoundsDbAction.Copied, a.Action, "copied");
            Assert.Eq(SafeFs.Md5File(C.FxBoundsDb), SafeFs.Md5File(Path.Combine(root, PreflightChecks.BoundsDbName)), "copy md5");
            var b = PreflightChecks.EnsureBoundsDb(root, C.FxBoundsDb);
            Assert.Eq(PreflightChecks.BoundsDbAction.AlreadyPresent, b.Action, "second call keeps it");
            string other = C.Sb.Dir("boundsroot2");
            File.WriteAllText(Path.Combine(other, PreflightChecks.BoundsDbName), "the user's own DB");
            string before = SafeFs.Md5File(Path.Combine(other, PreflightChecks.BoundsDbName));
            var c = PreflightChecks.EnsureBoundsDb(other, C.FxBoundsDb);
            Assert.Eq(PreflightChecks.BoundsDbAction.AlreadyPresent, c.Action, "an existing different DB is kept");
            Assert.Eq(before, SafeFs.Md5File(Path.Combine(other, PreflightChecks.BoundsDbName)), "never overwritten");
            Assert.True(!Directory.GetFiles(root).Any(f => f.Contains(".h2lm_tmp_")), "no temp left");
            var r = PreflightChecks.Run(other, ReadOnly());
            Assert.Eq(CheckLevel.Info, r.Get("boundsdb").Level, "a foreign DB is reported as kept");
        }

        private static void GpuCheckTests()
        {
            if (C.FxGpuCheckJson == null) throw new TestSkip("no gpucheck JSON from S1.6");
            string json = File.ReadAllText(C.FxGpuCheckJson);
            var g = GpuCheck.Parse(json, 0);
            Assert.True(g.Ok, "dev PC verdict ok");
            Assert.Contains(g.Line, "RTX 4090", "GPU line");
            Assert.Contains(g.Line, "FP64 yes", "FP64");
            var bad = Json.ParseObj(json);
            bad.Obj("default_adapter")["double_precision_float_shader_ops"] = false;
            bad.Obj("default_adapter")["extended_doubles_shader_instructions"] = false;
            bad.Obj("verdict")["ok"] = false;
            bad.Obj("verdict")["reasons"] = new List<object> { "default adapter lacks DoublePrecisionFloatShaderOps" };
            var gb = GpuCheck.Parse(Json.Write(bad), 1);
            Assert.True(!gb.Ok, "no FP64 refused");
            var o = ReadOnly();
            o.GpuCheckJson = Json.Write(bad);
            o.GpuCheckExitCode = 1;
            var r = PreflightChecks.Run(C.MineRoot, o);
            Assert.Eq(CheckLevel.Fail, r.Get("gpu").Level, "preflight refuses");
            Assert.Contains(r.Get("gpu").Message, "lacks DoublePrecisionFloatShaderOps", "reason shown");
            Assert.Eq(2, GpuCheck.Parse("not json", 2).ExitCode, "garbage output handled");
            // hybrid laptop: the exports pick the dGPU, but an exe WITHOUT them (production fullgpu) gets the iGPU
            var hy = Json.ParseObj(json);
            hy["hybrid"] = true;
            var plain = Json.ParseObj(json);
            plain["hybrid"] = true;
            plain.Obj("default_adapter")["name"] = "Intel(R) Iris(R) Xe Graphics";
            plain.Obj("default_adapter")["extended_doubles_shader_instructions"] = false;
            plain.Obj("verdict")["ok"] = false;
            plain.Obj("verdict")["reasons"] = new List<object> { "default adapter lacks ExtendedDoublesShaderInstructions" };
            var oh = ReadOnly();
            oh.GpuCheckJson = Json.Write(hy); oh.GpuCheckPlainJson = Json.Write(plain); oh.GpuCheckPlainExitCode = 1;
            var rh = PreflightChecks.Run(C.MineRoot, oh);
            Assert.Eq(CheckLevel.Ok, rh.Get("gpu").Level, "the export-carrying check passes");
            // review fix (finding 17): Low / Super run the export-less production fullgpu -> the plain verdict GATES
            Assert.Eq(CheckLevel.Fail, rh.Get("gpu.hybrid").Level, "hybrid without a preference: refused (the bake's fullgpu has no exports)");
            Assert.Contains(rh.Get("gpu.hybrid").Message, "High performance", "the Graphics settings route is explained");
            Assert.Contains(rh.Get("gpu.hybrid").Message, "Iris", "names the adapter");
            Assert.Contains(rh.Get("gpu.hybrid").Message, @"farm_w200\fullgpu.exe", "names the path that really runs (the worker folder's copy)");
            Assert.True(!rh.Ok, "preflight not ok");
            oh.FullgpuSelectsDgpu = true;
            var rx = PreflightChecks.Run(C.MineRoot, oh);
            Assert.Eq(CheckLevel.Info, rx.Get("gpu.hybrid").Level, "an fullgpu WITH the exports (N3 guarded engine): information only");
            Assert.True(!PreflightChecks.HighPerformancePreferenceSet(Path.Combine(C.Sb.Root, @"nowhere\farm_w299\fullgpu.exe")), "no per-app preference for a path nobody registered");
            if (C.NoProcesses || C.FxGpuCheck == null) return;
            var real = GpuCheck.Run(C.FxGpuCheck);
            Assert.True(real.Ran && real.ExitCode == 0 && real.Ok, "real gpucheck: " + real.Line + " " + real.Error);
        }

        private static void PwshPolicy()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            var o = ReadOnly();
            o.PwshExe = C.PwshExe;
            o.PwshEnv = BakeEnv.Build(new H2Lm.Proc.BakeEnvSpec { PwshDir = C.PwshDir, PyDir = C.PyDir });
            var r = PreflightChecks.Run(C.MineRoot, o);
            var p = r.Get("pwsh.policy");
            Assert.Eq(CheckLevel.Ok, p.Level, "policy: " + p.Message + " / " + p.Detail);
            Assert.Contains(p.Detail, "MachinePolicy=", "scopes listed");
        }

        private static void ToolSmoke()
        {
            if (C.NoProcesses) throw new TestSkip("--no-processes");
            string root = C.Sb.Dir("smokeroot");
            File.Copy(Path.Combine(C.MineRoot, "tool_fast.exe"), Path.Combine(root, "tool_fast.exe"));
            File.Copy(Path.Combine(C.MineRoot, "libfbxsdk.dll"), Path.Combine(root, "libfbxsdk.dll"));
            Assert.Eq(ToolFast.SupportedMd5, SafeFs.Md5File(Path.Combine(root, "tool_fast.exe")), "the copy is the supported build");
            var t = PreflightChecks.RunToolSmoke(root);
            Console.WriteLine("        smoke: exit " + ProcessRunner.FormatExit(t.ExitCode) + " in " + t.Seconds.ToString("0.0") + " s, first output: " + (t.Output ?? "").Split('\n').FirstOrDefault());
            Assert.True(!t.TimedOut, "finished within 10 s");
            Assert.Eq(CheckLevel.Ok, t.Level, "smoke ok: " + t.Message);
            Assert.Eq(1, t.ExitCode, "the usage exit code");
            Assert.Contains(t.Output, "usage: tool_fast.exe verb", "usage printed");
            string smoke = Path.Combine(root, "GPU Lightmap Work", "standalone", "smoke");
            Assert.True(!File.Exists(Path.Combine(root, "debug.txt")) && !Directory.Exists(Path.Combine(root, "reports")), "nothing written into ROOT");
            Assert.True(File.Exists(Path.Combine(smoke, "debug.txt")) && Directory.Exists(Path.Combine(smoke, "reports")), "the tool's debug.txt / reports\\ went to WORK\\standalone\\smoke");
            var o = ReadOnly();
            o.RunToolSmoke = true;
            o.ProbeWrite = true;
            var r = PreflightChecks.Run(root, o);
            Assert.Eq(CheckLevel.Ok, r.Get("tool.smoke").Level, "preflight item");
        }
    }
}
