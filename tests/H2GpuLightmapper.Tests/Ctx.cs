using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Tests
{
    internal sealed class CliResult
    {
        public int Exit;
        public bool TimedOut;
        public string Out, Err;
        public List<string> Lines;
        public string CommandLine;
        public JObj Json() { try { return Core.Util.Json.ParseObj(Out); } catch (Exception e) { throw new TestFailure("stdout is not JSON (" + e.Message + "):\n" + Assert.Trim(Out) + "\nERR: " + Assert.Trim(Err)); } }
        public override string ToString() { return "exit " + Exit + "\n--- stdout\n" + Assert.Trim(Out) + "\n--- stderr\n" + Assert.Trim(Err); }
    }

    /// <summary>A sandbox H2EK root (short path under %TEMP%\s25: WORK fits the 60-byte DLL slot, so the slot is Direct).</summary>
    internal sealed class TestRoot
    {
        public string Root, Work;
        public string Tags { get { return Path.Combine(Root, "tags"); } }
        public string CycDir { get { return Path.Combine(Tags, @"scenarios\multi\cyclotron"); } }
        public string TriDir { get { return Path.Combine(Tags, @"scenarios\multi\triplicate"); } }
        public string CycLtmp { get { return Path.Combine(CycDir, "cyclotron_cyclotron_lightmap.scenario_structure_lightmap"); } }
        public string TriLtmp { get { return Path.Combine(TriDir, "triplicate_triplicate_lightmap.scenario_structure_lightmap"); } }
        public string Farm(int w) { return Path.Combine(Work, "farm_w" + w.ToString("D2")); }
        public void Control(string text) { Directory.CreateDirectory(Work); File.WriteAllText(Path.Combine(Work, "fake_control.txt"), text); }
        public List<JObj> Calls()
        {
            string p = Path.Combine(Work, "fake_calls.log");
            if (!File.Exists(p)) return new List<JObj>();
            return File.ReadAllLines(p).Where(l => l.Trim().Length > 0).Select(l => Core.Util.Json.ParseObj(l)).ToList();
        }
        public List<JObj> Calls(string tool) { return Calls().Where(c => c.Str("tool") == tool).ToList(); }
    }

    /// <summary>Shared test context: paths, md5-checked fixture copies of production files (production is only READ), the test
    /// payload, sandbox H2EK roots, and the exe runner.</summary>
    internal sealed class Ctx : IDisposable
    {
        public const string LmwInstallMd5 = "4608af880327453b6e07844d6e7e95b9";
        public const string TagIoMd5 = "e8aeb703b786b8c995297834abdc73f6";
        public const string ProdDllMd5 = "d6147fce6e3979a37178d2e14d092227";      // W3 2026-10-21: the 0.6.4-promoted engine_v373 DLL (W1: b2370477, was aaf10a0f)
        public const string SkelDllMd5 = "56b774df2fce703a9a63da793aab7c8c";      // 0.6.9: engine_v374 skeleton-mode DLL with the fast NeuQuant (was 5c6aeea9 in 0.6.4-0.6.8)

        public string Project, Exe, PyDir, MineRoot, FixturesDir, SandboxParent, ShortParent;
        public bool Keep;
        public Sandbox Sb, Short;
        public string PayloadDir, Lad, Vol;
        public string FxLmwInstall, FxTagIo, FxTagDefs, FxProdDll, FxSkelDll, FxCycScenario, FxTriScenario, FxTriLtmp;
        private int _roots;

        public string PyExe { get { return Path.Combine(PyDir, "py.exe"); } }
        /// <summary>1.0: the fake bake program (tests\FakeBakeEngine, built next to this runner as h2lm-bake.exe). The test payload ships it as
        /// h2lm-bake.exe beside fixtures\fake_gpu_bake.py and a bake_params.txt in every engine folder (engine_v373, engine_custom, the AO engine copy).</summary>
        public string FakeBakeExe { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "h2lm-bake.exe"); } }

        private string _fakeParams;
        /// <summary>The parameter list the fake bake program reports (`--list-params` of the python fake, run with the bundle py), as the payload builder
        /// writes it to bake_params.txt: a comment line, then "Name&lt;TAB&gt;type" per parameter and "NAME&lt;TAB&gt;env" per environment variable the engine validates.
        /// <paramref name="drop"/> names are left out: an older engine, which then refuses the parameter (the fake takes only what its list names).</summary>
        public string ParamsText(IEnumerable<string> drop)
        {
            if (_fakeParams == null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(PyExe, "-I -B \"" + Fixture("fake_gpu_bake.py") + "\" --list-params")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    _fakeParams = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                    if (p.ExitCode != 0) throw new InvalidOperationException("the fake bake program's --list-params exited " + p.ExitCode);
                }
            }
            var dropped = new HashSet<string>(drop ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder("# bake_params.txt: the parameters h2lm-bake.exe takes (--list-params), written by the payload builder\n");
            foreach (var l in _fakeParams.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!dropped.Contains(l.Split('\t')[0])) sb.Append(l).Append('\n');
            return sb.ToString();
        }
        public string MineWork { get { return Path.Combine(MineRoot, "GPU Lightmap Work"); } }
        /// <summary>0.6.8 (2026-10-29): the folder that holds the production engine files the tests COPY (engine_v373\, v043-tagwriter\, _research\):
        /// H2LM_TEST_MINE_GLW when set (a read-only mirror of those files), else MineWork itself when it holds engine_v373 (the old dev layout),
        /// else the one subfolder of MineWork that does (the dev layout of 2026-10-29: GPU Lightmap Work\H2GPULM (Max Size - 100GBs)\engine_v373).
        /// Only read.</summary>
        public string MineGlw
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("H2LM_TEST_MINE_GLW");
                if (!string.IsNullOrEmpty(env)) return env;
                if (Directory.Exists(Path.Combine(MineWork, "engine_v373"))) return MineWork;
                if (Directory.Exists(MineWork))
                    foreach (var d in Directory.GetDirectories(MineWork))
                        if (Directory.Exists(Path.Combine(d, "engine_v373"))) return d;
                return MineWork;
            }
        }

        public void Init()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "src", "H2GpuLightmapper.Core"))) d = Path.GetDirectoryName(d);
            if (d == null) throw new InvalidOperationException("cannot find the project folder above " + AppDomain.CurrentDomain.BaseDirectory);
            Project = d;
            FixturesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
            Exe = Exe ?? Path.Combine(Project, @"src\H2GpuLightmapper\bin\Release\net48\H2 GPU Lightmapper.exe");
            PyDir = PyDir ?? Path.Combine(Project, @"build\s03\out\py310-np226");
            MineRoot = MineRoot ?? Environment.GetEnvironmentVariable("H2LM_TEST_MINE_ROOT");
            if (string.IsNullOrEmpty(MineRoot)) throw new InvalidOperationException("no production-like H2EK root: pass --mine DIR or set H2LM_TEST_MINE_ROOT (a tool_fast 3a889d37 H2EK whose GPU Lightmap Work holds engine_v373; it is only read)");
            SandboxParent = SandboxParent ?? Path.Combine(Project, @"build\s25\t");
            ShortParent = ShortParent ?? Path.Combine(Path.GetTempPath(), "s25");
            foreach (var f in new[] { Exe, FakeBakeExe, PyExe })
                if (!File.Exists(f)) throw new InvalidOperationException("missing " + f);
            Sb = new Sandbox(SandboxParent, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4)) { Keep = Keep };
            Short = new Sandbox(ShortParent, Guid.NewGuid().ToString("N").Substring(0, 2)) { Keep = Keep };
            Lad = Short.Dir("lad");
            Vol = Short.Dir("vol");

            // fixtures: COPIES of production files (md5-checked where the version matters); production is only read
            string glw = MineGlw;
            FxLmwInstall = CopyChecked(Path.Combine(glw, @"engine_v373\v043-tagwriter\lmw_install.py"), "lmw_install.py", LmwInstallMd5);
            FxTagIo = CopyChecked(Path.Combine(glw, @"v043-tagwriter\lmw_tagio.py"), "lmw_tagio.py", TagIoMd5);
            FxTagDefs = CopyChecked(Path.Combine(glw, @"v043-tagwriter\lmw_defs.json"), "lmw_defs.json", null);
            FxProdDll = CopyChecked(Path.Combine(glw, @"engine_v373\v014-inject-estimator\gpulm_estimator.dll"), "fx_prod.dll", ProdDllMd5);
            FxSkelDll = CopyChecked(Path.Combine(glw, @"engine_v373\v043-tagwriter\dll\gpulm_estimator.dll"), "fx_skel.dll", SkelDllMd5);
            FxCycScenario = CopyChecked(Path.Combine(MineRoot, @"tags\scenarios\multi\cyclotron\cyclotron.scenario"), "cyclotron.scenario", null);
            FxTriScenario = CopyChecked(Path.Combine(MineRoot, @"tags\scenarios\multi\triplicate\triplicate.scenario"), "triplicate.scenario", null);
            FxTriLtmp = CopyChecked(Path.Combine(MineRoot, @"tags\scenarios\multi\triplicate\triplicate_triplicate_lightmap.scenario_structure_lightmap"), "triplicate.ltmp", null);
            PayloadDir = BuildPayload(Sb.Dir("payload"));
        }

        private string CopyChecked(string src, string leaf, string md5)
        {
            string dst = Path.Combine(Sb.Dir("fx"), leaf);
            File.Copy(src, dst);
            if (md5 != null && SafeFs.Md5File(dst) != md5) throw new InvalidOperationException("fixture " + src + " md5 is not " + md5);
            return dst;
        }

        public string Fixture(string name) { return Path.Combine(FixturesDir, name); }

        // ------------------------------------------------------------------ the test payload (format 1, S0.1 / S1.9)

        private sealed class F { public string Path; public byte[] Data; public string Role; }

        /// <param name="aoFeature">MANIFEST features.ao.enabled (null = no features.ao, an old payload).</param>
        /// <param name="probesFeature">V5: MANIFEST features.probes.enabled (null = no features.probes, a payload built before gate T4).</param>
        /// <summary>The INST dir name of a payload built with customEngine (another file set, so another extraction dir).</summary>
        public const string GuardedInstDir = "0.5.0-s25cccg";

        /// <param name="customEngine">E1 (0.6.1): the INST carries engine_custom/h2lm-bake.exe (the N3 guarded Custom engine), so Custom is offered
        /// with no dev switch.</param>
        /// <param name="sceneryEngine">engine 0.6.4: engine_v373's bake_params.txt lists the switch SceneryOwnPermutationOnly (false = an older engine: the name is left out of the list, and the fake refuses it).</param>
        /// <param name="customSceneryEngine">the same for engine_custom (only with customEngine).</param>
        /// <param name="compatEngine">engine 0.6.4 Compatibility mode (DF64): engine_v373's bake_params.txt has the line "GPULM_COMPAT" + TAB + "env" (false = an older engine, the 0.6.3 shape: the
        /// list never names the variable, the fake never validates it and prints no Compatibility line). Default false: the default test payload is the 0.6.3 engine for this feature.</param>
        /// <param name="customCompatEngine">the same for engine_custom (only with customEngine); default = compatEngine.</param>
        /// <param name="aoCompatEngine">the same for the AO engine copy _research/ao/dev_AO; default = compatEngine.</param>
        /// <param name="crossBspEngine">engine 0.6.4 cross-folder BSP (dev_S): the engine_v373 / engine_custom parameter lists name the switches WriterOnly and ToolOverlay (false = an older
        /// engine: the two names are left out). Default false: the default test payload is the 0.6.3 engine for this feature.</param>
        /// <param name="splitBspDrivers">0.6.5: which research drivers carry the marker line SPLIT_BSP_SUPPORT = 1 (they read the BSP from the file the scenario's structure_bsps names):
        /// 1 = gpu_ao.py (+ the AO engine copy's parameter list names -WriterOnly / -ToolOverlay), 2 = gpu_lightprobes.py, 3 = both; 0 = a 0.6.4 payload. Use with crossBspEngine.</param>
        public string BuildPayload(string dir, bool? aoFeature = true, bool? probesFeature = null, bool customEngine = false, bool? driveRootFeature = null,
                                   bool sceneryEngine = true, bool customSceneryEngine = true, bool compatEngine = false, bool? customCompatEngine = null, bool? aoCompatEngine = null,
                                   bool crossBspEngine = false, int splitBspDrivers = 0, bool mapbakeComponent = false, bool? farmsFeature = null)
        {
            bool custCompat = customCompatEngine ?? compatEngine, aoCompat = aoCompatEngine ?? compatEngine;
            // another file set needs another extraction dir name (same length: the DLL slot / path budget must not change)
            string instDirName = customEngine ? (sceneryEngine && customSceneryEngine ? GuardedInstDir : "0.5.0-s25ccc" + (sceneryEngine ? "x" : "y")) : (sceneryEngine ? "0.5.0-s25cccc" : "0.5.0-s25cccn");
            if (compatEngine || custCompat || aoCompat)
            {
                // Compatibility-mode variants: another file set, another dir name of the same length (13): the last 2-3 characters carry the variant
                char kind = customEngine ? (sceneryEngine && customSceneryEngine ? 'c' : 'd') : (sceneryEngine ? 'n' : 'm');
                instDirName = "0.5.0-s25" + kind + (compatEngine ? 'k' : 'q') + (custCompat ? 'k' : 'q') + (aoCompat ? 'k' : 'q');     // 13 characters, like the others
            }
            if (crossBspEngine)
            {
                // cross-folder BSP variants: another file set, another dir name of the same length (13): "0.5.0-s25z" + engine kind + scenery + compat
                instDirName = "0.5.0-s25z" + (customEngine ? 'c' : 'e') + (sceneryEngine && customSceneryEngine ? 's' : 'o') + (compatEngine ? 'k' : 'q');
            }
            if (splitBspDrivers != 0)
            {
                // 0.6.5 variants: another file set, another dir name of the same length (13): "0.5.0-s25y" + driver flags + engine kind + scenery
                instDirName = "0.5.0-s25y" + splitBspDrivers.ToString() + (customEngine ? 'c' : 'e') + (sceneryEngine && customSceneryEngine ? 's' : 'o');
            }
            // 0.6.8 bounded temp: the farms in WORK\Temp (safe to delete) (MANIFEST features.farms) + the engine-owned slim list: another file set, another dir name (13)
            if (farmsFeature == true) instDirName = "0.5.0-s25ffff";
            Func<byte[], bool, byte[]> mark = (b, on) => on ? b.Concat(new UTF8Encoding(false).GetBytes("\nSPLIT_BSP_SUPPORT = 1\n")).ToArray() : b;
            // One engine folder = the fake bake program + the fake script it runs + its parameter list. The variants (older engines) leave names out of the list.
            Func<string, bool, bool, bool, List<F>> engineFiles = (folder, withScenery, withCrossBsp, withCompat) =>
            {
                var drop = new List<string>();
                if (!withScenery) drop.Add("SceneryOwnPermutationOnly");
                if (!withCrossBsp) { drop.Add("WriterOnly"); drop.Add("ToolOverlay"); }
                if (!withCompat) drop.Add("GPULM_COMPAT");
                return new List<F>
                {
                    new F { Path = folder + "/h2lm-bake.exe", Data = File.ReadAllBytes(FakeBakeExe) },
                    new F { Path = folder + "/fake_gpu_bake.py", Data = File.ReadAllBytes(Fixture("fake_gpu_bake.py")) },
                    new F { Path = folder + "/bake_params.txt", Data = new UTF8Encoding(false).GetBytes(ParamsText(drop)) },
                };
            };
            var rnd = new Random(25);
            Func<int, byte[]> blob = n => { var b = new byte[n]; rnd.NextBytes(b); return b; };
            Func<string, byte[]> txt = s => new UTF8Encoding(false).GetBytes(s);
            var comps = new List<Tuple<string, string, string, List<F>>>
            {
                Tuple.Create("rt-py-test", "py310-s25bbbb", "rt", new List<F>
                {
                    new F { Path = "py.exe", Data = blob(2000) }, new F { Path = "python310._pth", Data = txt("python310.zip\n.\nsite-packages\nimport site\n") },
                }),
                Tuple.Create("engine-test", instDirName, "inst", new List<F>
                {
                    new F { Path = "engine_v373/v043-tagwriter/lmw_install.py", Data = File.ReadAllBytes(FxLmwInstall) },
                    new F { Path = "engine_v373/v043-tagwriter/lmw_validate.py", Data = File.ReadAllBytes(Fixture("fake_lmw_validate.py")) },
                    new F { Path = "engine_v373/v014-inject-estimator/gpulm_estimator.dll", Data = File.ReadAllBytes(FxProdDll), Role = "dllslot" },
                    new F { Path = "engine_v373/v043-tagwriter/dll/gpulm_estimator.dll", Data = File.ReadAllBytes(FxSkelDll), Role = "dllslot" },
                    new F { Path = "engine_v373/v041-fullgpu/fullgpu.exe", Data = blob(4000) },
                    new F { Path = "_research/ao/gpu_ao.py", Data = mark(File.ReadAllBytes(Fixture("fake_gpu_ao.py")), (splitBspDrivers & 1) != 0) },
                    new F { Path = "_research/ao/ao_existing.py", Data = File.ReadAllBytes(Fixture("fake_ao_existing.py")) },      // 0.6.8 xc8: AO only
                    new F { Path = "_research/lightprobes/gpu_lightprobes.py", Data = mark(File.ReadAllBytes(Fixture("fake_gpu_lightprobes.py")), (splitBspDrivers & 2) != 0) },
                    new F { Path = "standalone/sh_stage.py", Data = File.ReadAllBytes(Fixture("fake_sh_stage.py")) },
                    new F { Path = "standalone/scan_scenery_bounds.py", Data = File.ReadAllBytes(Fixture("fake_scan_scenery_bounds.py")) },
                    new F { Path = "v043-tagwriter/lmw_tagio.py", Data = File.ReadAllBytes(FxTagIo) },
                    new F { Path = "v043-tagwriter/lmw_defs.json", Data = File.ReadAllBytes(FxTagDefs) },
                }),
            };
            comps[1].Item4.AddRange(engineFiles("engine_v373", sceneryEngine, crossBspEngine, compatEngine));
            comps[1].Item4.AddRange(engineFiles("_research/ao/dev_AO", false, (splitBspDrivers & 1) != 0, aoCompat));      // the AO engine copy: the gpu_ao driver starts its bake program
            if (customEngine) comps[1].Item4.AddRange(engineFiles("engine_custom", customSceneryEngine, crossBspEngine, custCompat));
            if (farmsFeature == true) comps[1].Item4.Add(new F { Path = "standalone/farm_products.json", Data = File.ReadAllBytes(Path.Combine(Project, @"src\standalone\farm_products.json")) });
            // milestone 2: the rt component with the mapbake entry point (stubs: the dry-run tests never start it)
            if (mapbakeComponent)
                comps.Add(Tuple.Create("rt-mapbake-test", "mapbake-test-s25dddd", "rt", new List<F>
                {
                    new F { Path = "mapbake.py", Data = txt("# test stub: the real entry point is src/standalone/mapbake/mapbake.py\n") },
                    new F { Path = "h2conv_run.py", Data = txt("# test stub\n") },
                }));
            Directory.CreateDirectory(dir);
            var carr = new List<object>();
            foreach (var c in comps)
            {
                string zipPath = Path.Combine(dir, c.Item1 + ".zip");
                var files = c.Item4.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
                using (var fs = new FileStream(zipPath, FileMode.CreateNew))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                    foreach (var f in files)
                    {
                        var e = z.CreateEntry(f.Path, CompressionLevel.Optimal);
                        e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using (var s = e.Open()) s.Write(f.Data, 0, f.Data.Length);
                    }
                var fl = files.Select(f =>
                {
                    var o = new JObj().Add("path", f.Path).Add("size", (long)f.Data.Length).Add("md5", SafeFs.Md5Hex(f.Data)).Add("sourceMd5", SafeFs.Md5Hex(f.Data));
                    if (f.Role != null) o.Add("role", f.Role);
                    return (object)o;
                }).ToList();
                carr.Add(new JObj().Add("name", c.Item1).Add("resource", "payload/" + c.Item1 + ".zip").Add("dirName", c.Item2).Add("target", c.Item3)
                    .Add("zipSize", new FileInfo(zipPath).Length).Add("zipMd5", SafeFs.Md5File(zipPath)).Add("fileCount", (long)files.Count)
                    .Add("totalBytes", files.Sum(f => (long)f.Data.Length)).Add("maxPathLen", (long)files.Max(f => f.Path.Length)).Add("files", fl));
            }
            var man = new JObj().Add("format", 1L).Add("appVersion", "0.5.0-test").Add("components", carr);
            if (aoFeature.HasValue || probesFeature.HasValue || driveRootFeature.HasValue || farmsFeature.HasValue)
            {
                var feats = new JObj();
                if (aoFeature.HasValue) feats.Add("ao", new JObj().Add("enabled", aoFeature.Value).Add("reason", aoFeature.Value ? null : "the test payload's AO engine is out of sync"));
                if (probesFeature.HasValue) feats.Add("probes", new JObj().Add("enabled", probesFeature.Value).Add("reason", probesFeature.Value ? null : "the test payload's T4 proof does not cover its engine"));
                if (driveRootFeature.HasValue) feats.Add("driveRoot", new JObj().Add("enabled", driveRootFeature.Value).Add("reason", driveRootFeature.Value ? null : "the test payload's gpu_bake has no P1b"));
                if (farmsFeature.HasValue) feats.Add("farms", new JObj().Add("enabled", farmsFeature.Value).Add("reason", farmsFeature.Value ? null : "the test payload's gpu_bake has no P13"));
                man.Add("features", feats);
            }
            File.WriteAllText(Path.Combine(dir, "MANIFEST.json"), Core.Util.Json.Write(man, true), new UTF8Encoding(false));
            return dir;
        }

        /// <summary>The sandbox roots' tool_fast.exe: ONE copy of ProdRoot's (the supported 3a889d37; production is only read),
        /// hard-linked into each root -- the shell checks tool_fast's identity even with --dev-skip-preflight (finding 24).</summary>
        private string _toolCopy;
        public string ToolCopy
        {
            get
            {
                if (_toolCopy == null)
                {
                    _toolCopy = Path.Combine(Short.Dir("toolsrc"), "tool_fast.exe");
                    File.Copy(Path.Combine(MineRoot, "tool_fast.exe"), _toolCopy);
                    if (SafeFs.Md5File(_toolCopy) != "3a889d370a7be537af47ff8035acd201") throw new InvalidOperationException("ProdRoot's tool_fast.exe is not the supported build");
                }
                return _toolCopy;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFile, string existing, IntPtr sa);

        /// <summary>A planted "secret": it must reach the children (inherited unchanged) but never a log or a plan (finding 18).</summary>
        public const string SecretName = "H2LMTEST_API_TOKEN", SecretValue = "sk-test-0123456789-must-never-be-logged";

        // ------------------------------------------------------------------ sandbox H2EK roots

        /// <summary>A fresh sandbox H2EK: cyclotron (real scenario, no probe points; fake BSP / lightmap set incl. a radiance
        /// bitmap) and triplicate (real scenario + real lightmap: 11 points, 11 environments; fake BSP / bitmaps), + other tags.</summary>
        public TestRoot NewRoot(bool withLightmaps = true, bool withTool = true, int rootLength = 0)
        {
            string leaf = "R" + (_roots++).ToString("x");
            // 0.6.8 RC (F1): a root of an exact length (padded leaf), for the path-budget tests
            if (rootLength > 0) { int pad = rootLength - (Short.Root.Length + 1 + leaf.Length + 1); if (pad > 0) leaf += "_" + new string('p', pad); }
            string root = Short.Dir(leaf);
            var r = new TestRoot { Root = root, Work = Path.Combine(root, "GPU Work") };
            if (withTool && !CreateHardLink(Path.Combine(root, "tool_fast.exe"), ToolCopy, IntPtr.Zero)) File.Copy(ToolCopy, Path.Combine(root, "tool_fast.exe"));
            Directory.CreateDirectory(r.CycDir);
            Directory.CreateDirectory(r.TriDir);
            File.Copy(FxCycScenario, Path.Combine(r.CycDir, "cyclotron.scenario"));
            File.WriteAllText(Path.Combine(r.CycDir, "cyclotron.scenario_structure_bsp"), "fake bsp cyclotron");
            File.Copy(FxTriScenario, Path.Combine(r.TriDir, "triplicate.scenario"));
            File.WriteAllText(Path.Combine(r.TriDir, "triplicate.scenario_structure_bsp"), "fake bsp triplicate");
            if (withLightmaps)
            {
                File.WriteAllText(r.CycLtmp, "LIVE ltmp cyclotron v1");
                File.WriteAllText(Path.Combine(r.CycDir, "cyclotron_cyclotron_lightmap_bitmaps.bitmap"), "LIVE p8 cyclotron v1");
                File.WriteAllText(Path.Combine(r.CycDir, "cyclotron_cyclotron_lightmap_truecolor_bitmaps.bitmap"), "LIVE truecolor cyclotron v1");
                File.WriteAllText(Path.Combine(r.CycDir, "cyclotron_cyclotron_lightmap_radiance_bitmaps_0.bitmap"), "LIVE radiance cyclotron");
                File.Copy(FxTriLtmp, r.TriLtmp);
                File.WriteAllText(Path.Combine(r.TriDir, "triplicate_triplicate_lightmap_bitmaps.bitmap"), "LIVE p8 triplicate");
                File.WriteAllText(Path.Combine(r.TriDir, "triplicate_triplicate_lightmap_truecolor_bitmaps.bitmap"), "LIVE truecolor triplicate");
            }
            Directory.CreateDirectory(Path.Combine(r.Tags, @"objects\thing"));
            File.WriteAllText(Path.Combine(r.Tags, @"objects\thing\thing.model"), "a model");
            File.WriteAllText(Path.Combine(r.Tags, @"objects\thing\thing.shader"), "a shader");
            return r;
        }

        // ------------------------------------------------------------------ the exe

        /// <summary>The exe's environment: this process's, plus pipeline variables a careless shell would leak (the child
        /// environment must be built from scratch, design section 5).</summary>
        public ChildEnvironment ExeEnv()
        {
            return ChildEnvironment.FromCurrentProcess().Set("GPULM_SKY_HI", "1").Set("GPULM_SURF_SAMPCHUNK", "8").Set("OSOYOOS_INJECTOR_PATCH_QUALITY", "x")
                .Set("PYTHONPATH", @"C:\bogus\pythonpath").Set("PYTHONHOME", @"C:\bogus\pythonhome").Set("H2LM_ROOT", @"C:\bogus\root").Set("PSModulePath", @"C:\bogus\modules")     // PSModulePath: an inherited shell variable never reaches the children
                .Set("DEVP_SB_PAD", "3").Set("DEVP_PROF_EXIT", "1").Set(SecretName, SecretValue);
        }

        public CliResult Run(IEnumerable<string> args, int timeoutMs = 300000, string cwd = null)
        {
            var a = args.ToList();
            var r = ProcessRunner.RunCapture(Exe, a, cwd ?? Short.Root, ExeEnv(), timeoutMs);
            var res = new CliResult { Exit = r.ExitCode, TimedOut = r.TimedOut, Out = r.Stdout, Err = r.Stderr, Lines = r.StdoutLines.ToList(), CommandLine = r.CommandLine };
            if (r.TimedOut) throw new TestFailure("the exe timed out: " + res);
            return res;
        }

        /// <summary>--cli VERB --payload-dir .. --localappdata .. --alias-volume-root .. --rt-py .. [extra]</summary>
        public List<string> Base(string verb, bool rt = true)
        {
            var l = new List<string> { "--cli", verb, "--payload-dir", PayloadDir, "--localappdata", Lad, "--alias-volume-root", Vol };
            if (rt) { l.Add("--rt-py"); l.Add(PyDir); }
            return l;
        }

        /// <summary>A bake on a sandbox root (dev: no preflight / gpucheck there: the sandbox has no real tool_fast).</summary>
        public CliResult Bake(TestRoot r, params string[] extra)
        {
            var l = Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--lock-wait-min", "1", "--lock-poll-s", "0.5" });
            l.AddRange(extra);
            return Run(l);
        }

        public CliResult DryJson(TestRoot r, params string[] extra)
        {
            var l = Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json" });
            l.AddRange(extra);
            return Run(l, 120000);
        }

        public void Dispose()
        {
            Short.Dispose();
            Sb.Dispose();
        }
    }
}
