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
        public const string SkelDllMd5 = "5c6aeea9d1ffbaaaa6351d7ba5e77360";      // W3 2026-10-21: the 0.6.4-promoted skeleton-mode DLL (was f0fb73ac)

        public string Project, Exe, PwshDir, PyDir, MineRoot, FixturesDir, SandboxParent, ShortParent;
        public bool Keep;
        public Sandbox Sb, Short;
        public string PayloadDir, Lad, Vol;
        public string FxLmwInstall, FxTagIo, FxTagDefs, FxProdDll, FxSkelDll, FxCycScenario, FxTriScenario, FxTriLtmp;
        private int _roots;

        public string PwshExe { get { return Path.Combine(PwshDir, "pwsh.exe"); } }
        public string PyExe { get { return Path.Combine(PyDir, "py.exe"); } }
        public string MineWork { get { return Path.Combine(MineRoot, "GPU Lightmap Work"); } }

        public void Init()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "src", "H2GpuLightmapper.Core"))) d = Path.GetDirectoryName(d);
            if (d == null) throw new InvalidOperationException("cannot find the project folder above " + AppDomain.CurrentDomain.BaseDirectory);
            Project = d;
            FixturesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
            Exe = Exe ?? Path.Combine(Project, @"src\H2GpuLightmapper\bin\Release\net48\H2 GPU Lightmapper.exe");
            PwshDir = PwshDir ?? Path.Combine(Project, @"cache\pwsh-7.6.6-win-x64");
            PyDir = PyDir ?? Path.Combine(Project, @"build\s03\out\py310-np226");
            MineRoot = MineRoot ?? Environment.GetEnvironmentVariable("H2LM_TEST_MINE_ROOT");
            if (string.IsNullOrEmpty(MineRoot)) throw new InvalidOperationException("no production-like H2EK root: pass --mine DIR or set H2LM_TEST_MINE_ROOT (a tool_fast 3a889d37 H2EK whose GPU Lightmap Work holds engine_v373; it is only read)");
            SandboxParent = SandboxParent ?? Path.Combine(Project, @"build\s25\t");
            ShortParent = ShortParent ?? Path.Combine(Path.GetTempPath(), "s25");
            foreach (var f in new[] { Exe, PwshExe, PyExe })
                if (!File.Exists(f)) throw new InvalidOperationException("missing " + f);
            Sb = new Sandbox(SandboxParent, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4)) { Keep = Keep };
            Short = new Sandbox(ShortParent, Guid.NewGuid().ToString("N").Substring(0, 2)) { Keep = Keep };
            Lad = Short.Dir("lad");
            Vol = Short.Dir("vol");

            // fixtures: COPIES of production files (md5-checked where the version matters); production is only read
            string glw = MineWork;
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

        /// <param name="customEngine">E1 (0.6.1): the INST carries engine_custom/gpu_bake.ps1 (the N3 guarded Custom engine), so Custom is offered
        /// with no dev switch.</param>
        /// <param name="sceneryEngine">engine 0.6.4: engine_v373/gpu_bake.ps1 declares [switch]$SceneryOwnPermutationOnly (false = an older engine: the parameter line is dropped).</param>
        /// <param name="customSceneryEngine">the same for engine_custom/gpu_bake.ps1 (only with customEngine).</param>
        /// <param name="compatEngine">engine 0.6.4 Compatibility mode (DF64): engine_v373/gpu_bake.ps1 validates GPULM_COMPAT (false = an older engine, the 0.6.3 shape: every FAKE_COMPAT_ENGINE
        /// line is dropped, so the script never mentions the variable). Default false: the default test payload is the 0.6.3 engine for this feature.</param>
        /// <param name="customCompatEngine">the same for engine_custom/gpu_bake.ps1 (only with customEngine); default = compatEngine.</param>
        /// <param name="aoCompatEngine">the same for the AO engine copy _research/ao/dev_AO/gpu_bake.ps1; default = compatEngine.</param>
        /// <param name="crossBspEngine">engine 0.6.4 cross-folder BSP (dev_S): the engine_v373 / engine_custom gpu_bake.ps1 declare [switch]$WriterOnly and [switch]$ToolOverlay (false = an older
        /// engine: the two parameter lines are dropped). Default false: the default test payload is the 0.6.3 engine for this feature.</param>
        /// <param name="splitBspDrivers">0.6.5: which research drivers carry the marker line SPLIT_BSP_SUPPORT = 1 (they read the BSP from the file the scenario's structure_bsps names):
        /// 1 = gpu_ao.py (+ the AO engine copy's gpu_bake.ps1 declares -WriterOnly / -ToolOverlay), 2 = gpu_lightprobes.py, 3 = both; 0 = a 0.6.4 payload. Use with crossBspEngine.</param>
        public string BuildPayload(string dir, bool? aoFeature = true, bool? probesFeature = null, bool customEngine = false, bool? driveRootFeature = null,
                                   bool sceneryEngine = true, bool customSceneryEngine = true, bool compatEngine = false, bool? customCompatEngine = null, bool? aoCompatEngine = null,
                                   bool crossBspEngine = false, int splitBspDrivers = 0, bool mapbakeComponent = false)
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
            Func<byte[], bool, byte[]> mark = (b, on) => on ? b.Concat(new UTF8Encoding(false).GetBytes("\nSPLIT_BSP_SUPPORT = 1\n")).ToArray() : b;
            Func<bool, bool, byte[]> fakeBake = (withScenery, withCompat) =>
            {
                string t = File.ReadAllText(Fixture("fake_gpu_bake.ps1"));
                var lines = t.Replace("\r\n", "\n").Split('\n').AsEnumerable();
                if (!withScenery) lines = lines.Where(l => l.IndexOf("SceneryOwnPermutationOnly", StringComparison.Ordinal) < 0);
                if (!withCompat) lines = lines.Where(l => l.IndexOf("FAKE_COMPAT_ENGINE", StringComparison.Ordinal) < 0);
                if (!crossBspEngine) lines = lines.Where(l => l.IndexOf("FAKE_CROSSBSP_ENGINE", StringComparison.Ordinal) < 0);
                return new UTF8Encoding(false).GetBytes(string.Join("\r\n", lines));
            };
            var rnd = new Random(25);
            Func<int, byte[]> blob = n => { var b = new byte[n]; rnd.NextBytes(b); return b; };
            Func<string, byte[]> txt = s => new UTF8Encoding(false).GetBytes(s);
            var comps = new List<Tuple<string, string, string, List<F>>>
            {
                Tuple.Create("rt-pwsh-test", "pwsh-test-s25aaaa", "rt", new List<F>
                {
                    new F { Path = "pwsh.exe", Data = blob(3000) }, new F { Path = "Modules/Microsoft.PowerShell.Utility/x.psd1", Data = txt("@{ fake = 1 }") },
                }),
                Tuple.Create("rt-py-test", "py310-s25bbbb", "rt", new List<F>
                {
                    new F { Path = "py.exe", Data = blob(2000) }, new F { Path = "python310._pth", Data = txt("python310.zip\n.\nsite-packages\nimport site\n") },
                }),
                Tuple.Create("engine-test", instDirName, "inst", new List<F>
                {
                    new F { Path = "engine_v373/gpu_bake.ps1", Data = fakeBake(sceneryEngine, compatEngine) },
                    new F { Path = "engine_v373/v043-tagwriter/lmw_install.py", Data = File.ReadAllBytes(FxLmwInstall) },
                    new F { Path = "engine_v373/v043-tagwriter/lmw_validate.py", Data = File.ReadAllBytes(Fixture("fake_lmw_validate.py")) },
                    new F { Path = "engine_v373/v014-inject-estimator/gpulm_estimator.dll", Data = File.ReadAllBytes(FxProdDll), Role = "dllslot" },
                    new F { Path = "engine_v373/v043-tagwriter/dll/gpulm_estimator.dll", Data = File.ReadAllBytes(FxSkelDll), Role = "dllslot" },
                    new F { Path = "engine_v373/v041-fullgpu/fullgpu.exe", Data = blob(4000) },
                    new F { Path = "_research/ao/gpu_ao.py", Data = mark(File.ReadAllBytes(Fixture("fake_gpu_ao.py")), (splitBspDrivers & 1) != 0) },
                    new F { Path = "_research/ao/dev_AO/gpu_bake.ps1", Data = txt(((splitBspDrivers & 1) != 0 ? "param([switch]$WriterOnly, [switch]$ToolOverlay)\n" : "") + (aoCompat ? "if (Test-Path Env:GPULM_COMPAT) { }   # the AO engine copy of an engine with Compatibility mode (DF64)\n" : "") + "throw 'the test dev_AO gpu_bake is never run directly'\n") },
                    new F { Path = "_research/lightprobes/gpu_lightprobes.py", Data = mark(File.ReadAllBytes(Fixture("fake_gpu_lightprobes.py")), (splitBspDrivers & 2) != 0) },
                    new F { Path = "standalone/sh_stage.py", Data = File.ReadAllBytes(Fixture("fake_sh_stage.py")) },
                    new F { Path = "standalone/scan_scenery_bounds.py", Data = File.ReadAllBytes(Fixture("fake_scan_scenery_bounds.py")) },
                    new F { Path = "v043-tagwriter/lmw_tagio.py", Data = File.ReadAllBytes(FxTagIo) },
                    new F { Path = "v043-tagwriter/lmw_defs.json", Data = File.ReadAllBytes(FxTagDefs) },
                }),
            };
            if (customEngine) comps[2].Item4.Add(new F { Path = "engine_custom/gpu_bake.ps1", Data = fakeBake(customSceneryEngine, custCompat) });
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
            if (aoFeature.HasValue || probesFeature.HasValue || driveRootFeature.HasValue)
            {
                var feats = new JObj();
                if (aoFeature.HasValue) feats.Add("ao", new JObj().Add("enabled", aoFeature.Value).Add("reason", aoFeature.Value ? null : "the test payload's AO engine is out of sync"));
                if (probesFeature.HasValue) feats.Add("probes", new JObj().Add("enabled", probesFeature.Value).Add("reason", probesFeature.Value ? null : "the test payload's T4 proof does not cover its engine"));
                if (driveRootFeature.HasValue) feats.Add("driveRoot", new JObj().Add("enabled", driveRootFeature.Value).Add("reason", driveRootFeature.Value ? null : "the test payload's gpu_bake has no P1b"));
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
        public TestRoot NewRoot(bool withLightmaps = true, bool withTool = true)
        {
            string leaf = "R" + (_roots++).ToString("x");
            string root = Short.Dir(leaf);
            var r = new TestRoot { Root = root, Work = Path.Combine(root, "GPU Lightmap Work") };
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
                .Set("PYTHONPATH", @"C:\bogus\pythonpath").Set("PYTHONHOME", @"C:\bogus\pythonhome").Set("H2LM_ROOT", @"C:\bogus\root").Set("PSModulePath", @"C:\bogus\modules")
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

        /// <summary>--cli VERB --payload-dir .. --localappdata .. --alias-volume-root .. --rt-pwsh .. --rt-py .. [extra]</summary>
        public List<string> Base(string verb, bool rt = true)
        {
            var l = new List<string> { "--cli", verb, "--payload-dir", PayloadDir, "--localappdata", Lad, "--alias-volume-root", Vol };
            if (rt) { l.Add("--rt-pwsh"); l.Add(PwshDir); l.Add("--rt-py"); l.Add(PyDir); }
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
