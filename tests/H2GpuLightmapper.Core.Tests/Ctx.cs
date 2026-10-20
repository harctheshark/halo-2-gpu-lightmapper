using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>Shared test context: paths, md5-checked fixture copies of production files, the test payload.</summary>
    internal sealed class Ctx : IDisposable
    {
        public const string ProdDllMd5 = "b2370477f36db8c74e6749d5389f2a57";      // W1 2026-10-20: the promoted engine_v373 DLL (was aaf10a0f)
        public const string SkelDllMd5 = "f0fb73ac9cd42f8f716f26b64584759b";
        public const string LmwInstallMd5 = "4608af880327453b6e07844d6e7e95b9";

        public string Project, SandboxParent, ShortParent, PwshDir, PyDir, MineRoot, FixturesDir;
        public bool Keep, NoProcesses;
        public Sandbox Sb, Short;
        public string FxProdDll, FxSkelDll, FxLmwInstall, FxBoundsDb, FxGpuCheck, FxGpuCheckJson;

        public string MineWork { get { return Path.Combine(MineRoot, "GPU Lightmap Work"); } }
        public string PwshExe { get { return Path.Combine(PwshDir, "pwsh.exe"); } }
        public string PyExe { get { return Path.Combine(PyDir, "py.exe"); } }

        public void Init()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "src", "H2GpuLightmapper.Core"))) d = Path.GetDirectoryName(d);
            if (d == null) throw new InvalidOperationException("cannot find the project folder (src\\H2GpuLightmapper.Core) above " + AppDomain.CurrentDomain.BaseDirectory);
            Project = d;
            FixturesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
            SandboxParent = SandboxParent ?? Path.Combine(Project, "build", "s2", "t");
            ShortParent = ShortParent ?? Path.Combine(Path.GetTempPath(), "s2");
            PwshDir = PwshDir ?? Path.Combine(Project, "cache", "pwsh-7.6.6-win-x64");
            PyDir = PyDir ?? Path.Combine(Project, "build", "s03", "out", "py310-np226");
            MineRoot = MineRoot ?? Environment.GetEnvironmentVariable("H2LM_TEST_MINE_ROOT");
            if (string.IsNullOrEmpty(MineRoot)) throw new InvalidOperationException("no production-like H2EK root: pass --mine DIR or set H2LM_TEST_MINE_ROOT (a tool_fast 3a889d37 H2EK whose GPU Lightmap Work holds engine_v373; it is only read)");
            if (!File.Exists(PwshExe)) throw new InvalidOperationException("no portable pwsh at " + PwshDir + " (run tests\\S0.2_pwsh_spike\\run_spike.sh once)");
            if (!File.Exists(PyExe)) throw new InvalidOperationException("no Python bundle at " + PyDir + " (build it with build\\s03\\make_pybundle.py)");

            Sb = new Sandbox(SandboxParent, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4)) { Keep = Keep };
            Short = new Sandbox(ShortParent, Guid.NewGuid().ToString("N").Substring(0, 2)) { Keep = Keep };

            // fixtures: COPIES of production files, md5-checked (production is only read)
            string engine = Path.Combine(MineWork, "engine_v373");
            FxProdDll = CopyChecked(Path.Combine(engine, @"v014-inject-estimator\gpulm_estimator.dll"), "fx_prod.dll", ProdDllMd5);
            FxSkelDll = CopyChecked(Path.Combine(engine, @"v043-tagwriter\dll\gpulm_estimator.dll"), "fx_skel.dll", SkelDllMd5);
            FxLmwInstall = CopyChecked(Path.Combine(engine, @"v043-tagwriter\lmw_install.py"), "lmw_install.py", LmwInstallMd5);
            FxBoundsDb = CopyChecked(Path.Combine(MineRoot, "h2v_scenery_bounds.db"), "h2v_scenery_bounds.db", null);
            if (!SafeFs.Md5File(FxBoundsDb).StartsWith("6814f29a", StringComparison.Ordinal))
            {
                // SF1 2026-10-20: the live H2EK's database has been extended by the scenery scanner (it is no longer the shipped
                // 6814f29a); the tests need the SHIPPED bytes, which the payload staging keeps (read-only copy of that file)
                string shipped = Path.Combine(Project, "build", "s19", "stage", "inst", "standalone", "h2v_scenery_bounds.db");
                if (File.Exists(shipped) && SafeFs.Md5File(shipped).StartsWith("6814f29a", StringComparison.Ordinal))
                {
                    File.Delete(FxBoundsDb);
                    File.Copy(shipped, FxBoundsDb);
                }
            }
            if (!SafeFs.Md5File(FxBoundsDb).StartsWith("6814f29a", StringComparison.Ordinal)) throw new InvalidOperationException("the bounds DB fixture is not 6814f29a");
            string gc = Path.Combine(Project, @"build\gpucheck\gpucheck.exe");
            if (File.Exists(gc)) FxGpuCheck = CopyChecked(gc, "gpucheck.exe", null);
            string gcDir = Path.Combine(Project, @"build\gpucheck");
            string gj = Directory.Exists(gcDir) ? Directory.GetFiles(gcDir, "gpucheck_*.json").Where(f => !Path.GetFileName(f).StartsWith("gpucheck_plain_", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("gpucheck_build", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
            if (gj != null) FxGpuCheckJson = gj;
        }

        private string CopyChecked(string src, string leaf, string md5)
        {
            string dst = Path.Combine(Sb.Dir("fx"), leaf);
            File.Copy(src, dst);
            if (md5 != null && SafeFs.Md5File(dst) != md5) throw new InvalidOperationException("fixture " + src + " md5 is not " + md5);
            return dst;
        }

        public string Fixture(string name) { return Path.Combine(FixturesDir, name); }

        public void Dispose()
        {
            Short.Dispose();
            Sb.Dispose();
        }
    }

    internal sealed class FileSpec
    {
        public string Path; public byte[] Data; public string Role;
        public FileSpec(string p, byte[] d, string role = null) { Path = p; Data = d; Role = role; }
        public FileSpec(string p, string text, string role = null) : this(p, new UTF8Encoding(false).GetBytes(text), role) { }
    }

    internal sealed class CompSpec
    {
        public string Name, DirName, Target;
        public List<FileSpec> Files = new List<FileSpec>();
    }

    /// <summary>Builds a format-1 payload folder (deterministic zips + MANIFEST.json) exactly as S0.1 / S1.9 define it.</summary>
    internal static class TestPayload
    {
        public static string Build(string dir, IEnumerable<CompSpec> comps, string appVersion = "0.0.0-test", Action<JObj> tweak = null)
        {
            Directory.CreateDirectory(dir);
            var carr = new List<object>();
            foreach (var c in comps)
            {
                string zipName = c.Name + ".zip";
                string zipPath = Path.Combine(dir, zipName);
                using (var fs = new FileStream(zipPath, FileMode.CreateNew))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var f in c.Files.OrderBy(x => x.Path, StringComparer.Ordinal))
                    {
                        var e = z.CreateEntry(f.Path, CompressionLevel.Optimal);
                        e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using (var s = e.Open()) s.Write(f.Data, 0, f.Data.Length);
                    }
                }
                var files = c.Files.OrderBy(x => x.Path, StringComparer.Ordinal).Select(f =>
                {
                    var o = new JObj().Add("path", f.Path).Add("size", (long)f.Data.Length).Add("md5", SafeFs.Md5Hex(f.Data)).Add("sourceMd5", SafeFs.Md5Hex(f.Data));
                    if (f.Role != null) o.Add("role", f.Role);
                    return (object)o;
                }).ToList();
                var jc = new JObj().Add("name", c.Name).Add("resource", "payload/" + zipName).Add("dirName", c.DirName).Add("target", c.Target)
                    .Add("zipSize", new FileInfo(zipPath).Length).Add("zipMd5", SafeFs.Md5File(zipPath)).Add("fileCount", (long)c.Files.Count)
                    .Add("totalBytes", c.Files.Sum(f => (long)f.Data.Length)).Add("maxPathLen", (long)c.Files.Max(f => f.Path.Length)).Add("files", files);
                carr.Add(jc);
            }
            var m = new JObj().Add("format", 1L).Add("appVersion", appVersion).Add("components", carr);
            if (tweak != null) tweak(m);
            File.WriteAllText(Path.Combine(dir, "MANIFEST.json"), Json.Write(m, true), new UTF8Encoding(false));
            return dir;
        }

        /// <summary>The standard test payload: two small fake runtimes and an INST with the real (copied) estimator DLLs,
        /// lmw_install.py, the bounds DB, gpucheck and the fake gpu_bake.ps1.</summary>
        public static List<CompSpec> Standard(Ctx c, string tag = "a")
        {
            var rnd = new Random(1234);
            Func<int, byte[]> blob = n => { var b = new byte[n]; rnd.NextBytes(b); return b; };
            var pw = new CompSpec { Name = "rt-pwsh-test", DirName = "pwsh-test-" + tag + "1111111", Target = "rt" };
            pw.Files.Add(new FileSpec("pwsh.exe", blob(5000)));
            pw.Files.Add(new FileSpec("System.Management.Automation.dll", blob(9000)));
            pw.Files.Add(new FileSpec("Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1", "@{ fake = 1 }"));
            pw.Files.Add(new FileSpec("en-US/notes.txt", "not a key file"));
            var py = new CompSpec { Name = "rt-py-test", DirName = "py310-" + tag + "2222222", Target = "rt" };
            py.Files.Add(new FileSpec("py.exe", blob(4000)));
            py.Files.Add(new FileSpec("python310.dll", blob(7000)));
            py.Files.Add(new FileSpec("python310._pth", "python310.zip\n.\nsite-packages\nimport site\n"));
            py.Files.Add(new FileSpec("site-packages/numpy/_core/_multiarray_umath.cp310-win_amd64.pyd", blob(3000)));
            py.Files.Add(new FileSpec("site-packages/numpy/__init__.py", "# fake"));
            var inst = new CompSpec { Name = "engine-test", DirName = "0.0.0-" + tag + "3333333", Target = "inst" };
            inst.Files.Add(new FileSpec("engine_v373/gpu_bake.ps1", File.ReadAllBytes(c.Fixture("fake_gpu_bake.ps1"))));
            inst.Files.Add(new FileSpec("engine_v373/v014-inject-estimator/gpulm_estimator.dll", File.ReadAllBytes(c.FxProdDll), "dllslot"));
            inst.Files.Add(new FileSpec("engine_v373/v043-tagwriter/dll/gpulm_estimator.dll", File.ReadAllBytes(c.FxSkelDll), "dllslot"));
            inst.Files.Add(new FileSpec("engine_v373/v043-tagwriter/lmw_install.py", File.ReadAllBytes(c.FxLmwInstall)));
            inst.Files.Add(new FileSpec("_research/ao/dev_AO/v014-inject-estimator/gpulm_estimator.dll", File.ReadAllBytes(c.FxProdDll)));   // no role: found by name
            inst.Files.Add(new FileSpec("h2v_scenery_bounds.db", File.ReadAllBytes(c.FxBoundsDb), "boundsdb"));
            if (c.FxGpuCheck != null) inst.Files.Add(new FileSpec("gpucheck/gpucheck.exe", File.ReadAllBytes(c.FxGpuCheck), "gpucheck"));
            inst.Files.Add(new FileSpec("osoyoos/readme.txt", "fake engine file"));
            return new List<CompSpec> { pw, py, inst };
        }
    }
}
