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
            yield return T("preflight: W2 path budget: the 0.6.2 refusal at 243 characters (run backup) fits with margin now (short run folder label + tagbak); label rules", W2PathBudgetRunBackup);
            yield return T("preflight: W2 a GPU without FP64 is refused in plain words (NVIDIA / AMD advice, CPU tool), technical detail kept", W2NoFp64Plain);
            yield return T("preflight: DF64 a GPU without FP64 runs in Compatibility mode (DF64) when the engine has it (gpucheck 1.2.0 and older JSON); an engine without it keeps the plain refusal", Df64Compat);
            yield return T("preflight: DF64 an FP64 GPU is 'Precision: Full FP64'; ApplyEngine flips with the engine; software adapter / device failure never become Compatibility mode; hybrid rules", Df64FullAndHybrid);
            yield return T("engine: DF64 marker = the engine's gpu_bake.ps1 reads $env:GPULM_COMPAT in code (a comment or a string mention does not count)", Df64EngineMarker);
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
            string mid = C.Short.Dir("warnroot_" + new string('m', 34));   // nominal map names overflow, INST still fits (W2 0.6.3: was 12; the shorter run folder saves 20 characters)
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

        // W2 (0.6.3): the user's report "paths would reach 243 characters (run backup; limit 240)". The run backup path is
        // WORK\standalone\runs\<ts>_<label>\<backup subfolder>\<leaf>_<bsp>_lightmap_truecolor_bitmaps.bitmap. 0.6.2: label = leaf_bsp (whole), subfolder
        // tag_backup_before_writeback; 0.6.3: label <= 20 characters, subfolder tagbak.
        private static int OldRunBackupLength(string root, string leaf, string bsp)
        {
            string truecolor = leaf + "_" + bsp + "_lightmap_truecolor_bitmaps.bitmap";
            return (root.TrimEnd('\\') + "\\GPU Lightmap Work" + "\\standalone\\runs\\" + "20261014_120000" + "_" + leaf + "_" + bsp + "\\tag_backup_before_writeback\\" + truecolor).Length;
        }

        private static void W2PathBudgetRunBackup()
        {
            const string scen = "scenarios\\multi\\a_long_scenario_folder\\a_long_map_name_x1";
            string leaf = "a_long_map_name_x1", bsp = "a_long_bsp_name_1";
            // a root that makes the 0.6.2 run backup path exactly 243 characters (the user's refusal)
            string probe = "C:\\h";
            int k = 243 - OldRunBackupLength(probe, leaf, bsp);
            Assert.True(k > 0, "the example is solvable: " + k);
            string root = probe + new string('x', k);
            Assert.Eq(243, OldRunBackupLength(root, leaf, bsp), "the 0.6.2 run backup path of this root is 243 characters");
            var e = PathBudget.Estimate(root, null, 0, scen, bsp).First(x => x.What == "run backup");
            Assert.True(e.Length <= PathBudget.Limit - 10, "0.6.3: the run backup path is " + e.Length + " characters, at least 10 under the limit (" + PathBudget.Limit + ")");
            Assert.True(e.Length <= 243 - 20, "0.6.3 saves at least 20 characters (the shorter subfolder alone): " + e.Length);
            Assert.True(e.Example.Contains("\\tagbak\\") && !e.Example.Contains("tag_backup_before_writeback"), "the entry uses the tagbak subfolder: " + e.Example);
            Assert.True(e.Example.Contains("_" + RunFolder.Label(leaf, bsp) + "\\"), "the entry uses the 20-character label: " + e.Example);
            // the shipped helper agrees with the path the planner will create
            Assert.Eq(RunFolder.TagFor(new DateTime(2026, 10, 14, 12, 0, 0), scen, bsp), "20261014_120000_" + RunFolder.Label(leaf, bsp), "TagFor == the budget's run folder name");
            // the same refusal as 0.6.2 would have fired for the old numbers; now the whole preflight budget no longer names the run backup
            var o = ReadOnly(); o.ScenarioRel = scen; o.Bsp = bsp;
            var all = PathBudget.Estimate(root, null, 0, scen, bsp);
            var lg = PathBudget.Longest(all);
            Assert.True(lg.Length <= PathBudget.Limit - 10, "the user's 243-character case fits with margin now: the longest entry is '" + lg.What + "' at " + lg.Length + " characters (limit " + PathBudget.Limit + ")");
            Assert.True(PathBudget.Longest(all).What != "run backup", "the run backup is no longer the longest entry");
            // label rules
            Assert.Eq("cyclotron_cyclotron", RunFolder.Label("cyclotron", "cyclotron"), "a name that fits is kept whole");
            Assert.Eq(20, RunFolder.Label(new string('l', 30), new string('b', 30)).Length, "never more than 20 characters");
            Assert.Eq(20, RunFolder.LabelMax, "LabelMax");
        }

        private static void W2NoFp64Plain()
        {
            const string plainText = "This GPU can't run the GPU lightmapper: it has no double-precision (FP64) support (Intel integrated/Arc and Qualcomm Adreno GPUs lack it). Use a PC with an NVIDIA or AMD GPU, or bake with the CPU tool.";
            Assert.Eq(plainText, GpuCheck.NoFp64Plain, "the exact sentence");
            // an Intel iGPU as gpucheck reports it
            string json = "{\"default_adapter\": {\"name\": \"Intel(R) UHD Graphics 630\", \"feature_level\": \"11_1\", \"double_precision_float_shader_ops\": false, \"extended_doubles_shader_instructions\": false}, "
                        + "\"hybrid\": false, \"verdict\": {\"ok\": false, \"reasons\": [\"the GPU has no double-precision (FP64) shader support\", \"the GPU lacks extended double-precision shader instructions (ddiv/dfma/drcp)\"], \"advice\": []}}";
            var o = ReadOnly(); o.GpuCheckJson = json; o.GpuCheckExitCode = 1;
            var r = PreflightChecks.Run(C.MineRoot, o);
            var g = r.Get("gpu");
            Assert.Eq(CheckLevel.Fail, g.Level, "refused");
            Assert.True(g.Message.StartsWith(plainText, StringComparison.Ordinal), "the message STARTS with the plain sentence: " + g.Message);
            Assert.Contains(g.Message, "Intel(R) UHD Graphics 630", "the technical detail names the adapter");
            Assert.Contains(g.Message, "(FP64) shader support", "... and the engine's own reason after it");
            Assert.True(!r.Ok, "preflight not ok");
            Assert.Contains(r.Gpu.Line, plainText, "the Advanced-panel GPU line says it in plain words too");
            Assert.Contains(r.Gpu.Line, "FP64 NO", "... and keeps the FP64 flag");
            // a failure that is NOT about FP64 keeps the generic text (no misleading FP64 sentence)
            string sw = "{\"default_adapter\": {\"name\": \"Microsoft Basic Render Driver\", \"feature_level\": \"11_0\", \"double_precision_float_shader_ops\": false, \"extended_doubles_shader_instructions\": false}, "
                      + "\"hybrid\": false, \"verdict\": {\"ok\": false, \"reasons\": [\"the default adapter is the Microsoft Basic Render Driver (no GPU driver installed?)\", \"the GPU has no double-precision (FP64) shader support\"], \"advice\": []}}";
            var o2 = ReadOnly(); o2.GpuCheckJson = sw; o2.GpuCheckExitCode = 1;
            var g2 = PreflightChecks.Run(C.MineRoot, o2).Get("gpu");
            Assert.True(g2.Message.StartsWith("This GPU cannot run the lightmapper: ", StringComparison.Ordinal) && !g2.Message.Contains("Qualcomm"), "a software adapter keeps the generic refusal: " + g2.Message);
        }

        // ---- Compatibility mode (DF64), engine 0.6.4 ----------------------------------------------------------------------------------------
        private const string Df64Adapter = "Intel(R) Arc(TM) A380 Graphics";

        /// <summary>gpucheck 1.2.0's JSON for a GPU without FP64: verdict ok, exit 0, compat_mode, the FP64 facts as notes.</summary>
        private static string NewGpucheckNoFp64(bool hybrid = false, string name = Df64Adapter)
        {
            return "{\"default_adapter\": {\"name\": \"" + name + "\", \"feature_level\": \"12_1\", \"double_precision_float_shader_ops\": false, \"extended_doubles_shader_instructions\": false, \"fp64\": false, \"compat_mode\": true}, "
                 + "\"hybrid\": " + (hybrid ? "true" : "false") + ", \"verdict\": {\"ok\": true, \"fp64\": false, \"compat_mode\": true, \"reasons\": [], \"notes\": [\"the GPU has no double-precision (FP64) shader support\", \"the GPU lacks extended double-precision shader instructions (ddiv/dfma/drcp)\"], \"advice\": []}}";
        }

        /// <summary>gpucheck 1.1.0 (the 0.6.3 payload): the same GPU is a failing verdict, exit 1, FP64 reasons only.</summary>
        private static string OldGpucheckNoFp64(bool hybrid = false, string name = Df64Adapter)
        {
            return "{\"default_adapter\": {\"name\": \"" + name + "\", \"feature_level\": \"12_1\", \"double_precision_float_shader_ops\": false, \"extended_doubles_shader_instructions\": false}, "
                 + "\"hybrid\": " + (hybrid ? "true" : "false") + ", \"verdict\": {\"ok\": false, \"reasons\": [\"the GPU has no double-precision (FP64) shader support\", \"the GPU lacks extended double-precision shader instructions (ddiv/dfma/drcp)\"], \"advice\": []}}";
        }

        private static string Fp64Gpucheck(bool hybrid = false, string name = "NVIDIA GeForce RTX 4090", bool newFormat = true)
        {
            return "{\"default_adapter\": {\"name\": \"" + name + "\", \"feature_level\": \"12_1\", \"double_precision_float_shader_ops\": true, \"extended_doubles_shader_instructions\": true" + (newFormat ? ", \"fp64\": true, \"compat_mode\": false" : "") + "}, "
                 + "\"hybrid\": " + (hybrid ? "true" : "false") + ", \"verdict\": {\"ok\": true, \"reasons\": [], \"advice\": []}}";
        }

        private static PreflightReport Df64Run(string json, int exit, bool compatEngine)
        {
            var o = ReadOnly(); o.GpuCheckJson = json; o.GpuCheckExitCode = exit; o.CompatEngine = compatEngine;
            return PreflightChecks.Run(C.MineRoot, o);
        }

        private static void Df64Compat()
        {
            // the exact wording the window / log / plan / summary show
            Assert.Eq("Precision: Full FP64", GpuCheck.PrecisionFull, "full precision line");
            Assert.Eq("Precision: Compatibility mode (DF64) - your GPU has no FP64, using FP32 double-single maths instead. Experimental; same results in testing, may be slower.", GpuCheck.PrecisionCompat, "compat line");
            Assert.Eq("GPULM_COMPAT", GpuCheck.CompatEnvVar, "the variable the engine validates");
            foreach (var pair in new[] { new[] { "gpucheck 1.2.0 (verdict ok, exit 0)", NewGpucheckNoFp64(), "0" }, new[] { "gpucheck 1.1.0 (verdict not ok, exit 1)", OldGpucheckNoFp64(), "1" } })
            {
                string what = pair[0];
                // the engine HAS Compatibility mode: accepted, Info-level precision item, no failure
                var r = Df64Run(pair[1], int.Parse(pair[2]), true);
                var g = r.Get("gpu");
                Assert.Eq(CheckLevel.Ok, g.Level, what + ": the GPU is accepted");
                Assert.True(r.Ok && r.Failures.Count() == 0, what + ": preflight ok: " + string.Join(" | ", r.Failures));
                Assert.Eq(PrecisionMode.Compat, r.Precision, what + ": precision");
                Assert.Eq(GpuCheck.PrecisionCompat, r.Get("gpu.precision").Message, what + ": the precision item says Compatibility mode");
                Assert.Eq(CheckLevel.Info, r.Get("gpu.precision").Level, what + ": information, not a warning");
                Assert.True(r.Gpu.Ok && r.Gpu.CompatAccepted, what + ": result accepted");
                Assert.Contains(r.Gpu.Line, "FP64 NO", what + ": the line keeps the FP64 flag");
                Assert.Contains(r.Gpu.Line, "(Compatibility mode)", what + ": ... and says what happens");
                Assert.True(!r.Gpu.Line.Contains("can't run"), what + ": no refusal text");
                Assert.Contains(r.ToJson(), "Compatibility mode (DF64)", what + ": doctor JSON carries the precision");
                // the engine does NOT have it (0.6.3): the plain refusal, unchanged, whichever gpucheck said it
                var n = Df64Run(pair[1], int.Parse(pair[2]), false);
                var gn = n.Get("gpu");
                Assert.Eq(CheckLevel.Fail, gn.Level, what + ": refused on an engine without Compatibility mode");
                Assert.True(gn.Message.StartsWith(GpuCheck.NoFp64Plain, StringComparison.Ordinal), what + ": the plain 0.6.3 sentence leads: " + gn.Message);
                Assert.Contains(gn.Message, Df64Adapter, what + ": technical detail names the adapter");
                Assert.Contains(gn.Message, "(FP64) shader support", what + ": ... and the reason");
                Assert.True(!n.Ok && n.Precision == PrecisionMode.Unknown && n.Get("gpu.precision") == null, what + ": not ok, no precision");
                Assert.True(!n.Gpu.Ok && !n.Gpu.CompatAccepted, what + ": result refused");
                Assert.Contains(n.Gpu.Line, GpuCheck.NoFp64Plain, what + ": the GPU line says it too");
            }
            // gpucheck 1.2.0's parse: notes and compat_mode are read, the verdict is ok
            var p = GpuCheck.Parse(NewGpucheckNoFp64(), 0);
            Assert.True(p.Ok && p.Reasons.Count == 0 && p.Notes.Count == 2 && p.CompatModeReported == true && !p.Doubles && !p.ExtendedDoubles, "1.2.0 parse");
            Assert.True(GpuCheck.NoFp64(p), "NoFp64 from the fields");
            // only ONE of the two caps missing is still 'no FP64' (fullgpu needs both for the native kernels)
            var half = Fp64Gpucheck().Replace("\"extended_doubles_shader_instructions\": true", "\"extended_doubles_shader_instructions\": false");
            Assert.Eq(PrecisionMode.Compat, Df64Run(half, 0, true).Precision, "doubles without the extended instructions: Compatibility mode");
            Assert.Eq(CheckLevel.Fail, Df64Run(half, 0, false).Get("gpu").Level, "... and refused on an old engine");
        }

        private static void Df64FullAndHybrid()
        {
            foreach (bool newFormat in new[] { true, false })
                foreach (bool compat in new[] { true, false })
                {
                    var r = Df64Run(Fp64Gpucheck(false, "NVIDIA GeForce RTX 4090", newFormat), 0, compat);
                    string what = "FP64 GPU (" + (newFormat ? "1.2.0" : "1.1.0") + " json), engine compat " + compat;
                    Assert.Eq(CheckLevel.Ok, r.Get("gpu").Level, what + ": ok");
                    Assert.Eq(PrecisionMode.Full, r.Precision, what + ": Full");
                    Assert.Eq("Precision: Full FP64", r.Get("gpu.precision").Message, what + ": line");
                    Assert.Contains(r.Gpu.Line, "FP64 yes", what + ": FP64 yes");
                    Assert.True(!r.Gpu.CompatAccepted, what + ": never Compatibility mode");
                }
            // ApplyEngine flips with the engine on the same result (the window re-evaluates when the quality changes the engine)
            var g = GpuCheck.Parse(NewGpucheckNoFp64(), 0);
            Assert.Eq(PrecisionMode.Compat, GpuCheck.ApplyEngine(g, true), "engine with the mode");
            Assert.Eq(PrecisionMode.Compat, GpuCheck.ApplyEngine(g, true), "again (idempotent)");
            Assert.True(g.Ok && g.Notes.Count == 2, "notes not duplicated");
            Assert.Eq(PrecisionMode.Unknown, GpuCheck.ApplyEngine(g, false), "engine without");
            Assert.True(!g.Ok, "refused now");
            Assert.Eq(PrecisionMode.Compat, GpuCheck.ApplyEngine(g, true), "and back");
            Assert.Eq(PrecisionMode.Unknown, GpuCheck.ApplyEngine(null, true), "null");
            Assert.Eq(PrecisionMode.Unknown, GpuCheck.ApplyEngine(GpuCheck.Parse("not json", 2), true), "unreadable output");
            // a software adapter / a missing hardware device is never 'Compatibility mode' (they are not about FP64)
            string sw = "{\"default_adapter\": {\"name\": \"Microsoft Basic Render Driver\", \"feature_level\": \"11_0\", \"double_precision_float_shader_ops\": false, \"extended_doubles_shader_instructions\": false, \"compat_mode\": true}, "
                      + "\"hybrid\": false, \"verdict\": {\"ok\": false, \"reasons\": [\"the default adapter is the Microsoft Basic Render Driver (no GPU driver installed?)\"], \"notes\": [\"the GPU has no double-precision (FP64) shader support\"], \"advice\": []}}";
            var rs = Df64Run(sw, 1, true);
            Assert.Eq(CheckLevel.Fail, rs.Get("gpu").Level, "software adapter refused even with a Compatibility-mode engine");
            Assert.True(rs.Get("gpu").Message.StartsWith("This GPU cannot run the lightmapper: ", StringComparison.Ordinal), "generic text: " + rs.Get("gpu").Message);
            string low = "{\"default_adapter\": {\"name\": \"Old GPU\", \"feature_level\": \"10_1\"}, \"hybrid\": false, \"verdict\": {\"ok\": false, \"reasons\": [\"feature level 10_1 is below 11_0\"], \"advice\": []}}";
            Assert.Eq(CheckLevel.Fail, Df64Run(low, 1, true).Get("gpu").Level, "feature level below 11_0 refused");
            // hybrid laptop. discrete FP64 GPU (exports) + iGPU without FP64 (what an export-less fullgpu gets): Compatibility mode would silently run on the iGPU,
            // so the Graphics settings refusal stays, whichever gpucheck wrote the iGPU's verdict
            foreach (var igpu in new[] { NewGpucheckNoFp64(true, "Intel(R) Iris(R) Xe Graphics"), OldGpucheckNoFp64(true, "Intel(R) Iris(R) Xe Graphics") })
            {
                var o = ReadOnly();
                o.GpuCheckJson = Fp64Gpucheck(true); o.GpuCheckPlainJson = igpu; o.GpuCheckPlainExitCode = igpu.Contains("\"ok\": false") ? 1 : 0; o.CompatEngine = true;
                var rh = PreflightChecks.Run(C.MineRoot, o);
                Assert.Eq(CheckLevel.Fail, rh.Get("gpu.hybrid").Level, "dGPU with FP64 + iGPU without: still the Graphics settings refusal");
                Assert.Contains(rh.Get("gpu.hybrid").Message, "High performance", "the route is explained");
                Assert.Contains(rh.Get("gpu.hybrid").Message, "Compatibility mode", "and why (slow Compatibility mode on the weaker GPU)");
                Assert.True(!rh.Ok, "not ok");
            }
            // both GPUs without FP64 and an engine with the mode: nothing to choose, information only
            var both = ReadOnly();
            both.GpuCheckJson = NewGpucheckNoFp64(true, "Intel(R) Arc(TM) A370M Graphics"); both.GpuCheckPlainJson = NewGpucheckNoFp64(true, "Intel(R) UHD Graphics"); both.CompatEngine = true;
            var rb = PreflightChecks.Run(C.MineRoot, both);
            Assert.Eq(CheckLevel.Info, rb.Get("gpu.hybrid").Level, "both without FP64, engine with the mode: " + rb.Get("gpu.hybrid").Message);
            Assert.True(rb.Ok && rb.Precision == PrecisionMode.Compat, "ok, Compatibility mode");
            // and on an engine without the mode the first GPU already refuses
            both.CompatEngine = false;
            Assert.Eq(CheckLevel.Fail, PreflightChecks.Run(C.MineRoot, both).Get("gpu").Level, "old engine: refused");
            // the precision text for an unknown GPU
            Assert.Contains(GpuCheck.PrecisionText(PrecisionMode.Unknown, true), "automatic", "unknown GPU, engine with the mode");
            Assert.Contains(GpuCheck.PrecisionText(PrecisionMode.Unknown, false), "needs a GPU with FP64", "unknown GPU, old engine");
            Assert.Contains(GpuCheck.PrecisionText(PrecisionMode.CompatForced, true), "forced for testing", "forced");
        }

        private static void Df64EngineMarker()
        {
            Func<string, bool> reads = s => Core.Payload.EngineSwitches.ReadsEnvVar(s, "GPULM_COMPAT");
            // the real 0.6.4 shape (gpu_bake.ps1 of dev_DF): Test-Path Env:X ... $env:X
            Assert.True(reads("param([string]$Scenario)\nif (Test-Path Env:GPULM_COMPAT) {\n  if ($env:GPULM_COMPAT -ne '' -and $env:GPULM_COMPAT -ne '0') { throw \"GPULM_COMPAT='$($env:GPULM_COMPAT)' -- expected 0, 1 or unset\" } }"), "validation in code");
            Assert.True(reads("if ($env:gpulm_compat -eq '1') { 1 }"), "case-insensitive, code");
            Assert.True(reads("$x = Test-Path Env:GPULM_COMPAT"), "Env: provider path");
            Assert.True(!reads("param([switch]$Responsive)\nif ($env:GPULM_RESPONSIVE) { 1 }"), "an engine with other GPULM_ variables only (0.6.3)");
            Assert.True(!reads("# GPULM_COMPAT=1 arrives in 0.6.4: $env:GPULM_COMPAT\nWrite-Host hi"), "named only in a comment");
            Assert.True(!reads("<# $env:GPULM_COMPAT #>\nWrite-Host hi"), "named only in a block comment");
            Assert.True(!reads("Write-Host 'set $env:GPULM_COMPAT to 1'"), "named only inside a single-quoted string");
            Assert.True(!reads("Write-Host \"set `$env:GPULM_COMPAT\""), "named only inside a double-quoted string");
            Assert.True(!reads("$env:GPULM_COMPAT_LIB = 1"), "a longer name is not the name");
            Assert.True(!reads("$myenv:GPULM_COMPAT = 1"), "not another provider / variable ending in env");
            Assert.True(!reads(""), "empty script"); Assert.True(!reads(null), "null script");
            // the production engine of this PC (read only): promoted to 0.6.4 on 2026-10-21 it HAS the marker; the 0.6.3 script (kept as the promotion's backup) has none
            string prod = Path.Combine(C.MineRoot, @"GPU Lightmap Work\engine_v373\gpu_bake.ps1");
            if (File.Exists(prod)) Assert.True(reads(File.ReadAllText(prod)), "production engine_v373 (0.6.4) validates GPULM_COMPAT");
            string old063 = Path.Combine(C.MineRoot, @"GPU Lightmap Work\engine_v373\_backup_20261021_145623_pre_064\gpu_bake.ps1");
            if (File.Exists(old063)) Assert.True(!reads(File.ReadAllText(old063)), "engine_v373 as of 0.6.3 (the pre-promotion backup) has no Compatibility mode");
            string dev = Path.Combine(C.MineRoot, @"GPU Lightmap Work\_research\df64\dev_DF\gpu_bake.ps1");
            if (File.Exists(dev)) Assert.True(reads(File.ReadAllText(dev)), "dev_DF (engine 0.6.4 candidate) validates GPULM_COMPAT");
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
