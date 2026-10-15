using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>
    /// S2.1-S2.4 unit tests. Usage:
    ///   H2GpuLightmapper.Core.Tests.exe [--filter text] [--keep] [--sandbox-parent dir] [--short-parent dir]
    ///                                   [--pwsh dir] [--py dir] [--mine root] [--no-processes]
    /// Production files are only READ (md5-checked copies of the estimator DLLs, lmw_install.py, the bounds DB; discovery
    /// and preflight on ProdRoot / H2EK read-only). Scratch: &lt;project&gt;\build\s2\t\&lt;id&gt;\ and a short
    /// %TEMP%\s2\&lt;hh&gt;\ (DLL-slot alias candidates and a root whose WORK fits the 60-char slot). No GPU, no bake, no
    /// tool_fast run except the 10 s no-argument smoke run of a COPY in the scratch folder.
    /// </summary>
    internal static class Program
    {
        internal static Ctx C;

        private static int Main(string[] args)
        {
            var c = new Ctx();
            string filter = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => { if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value"); return args[++i]; };
                if (a == "--filter") filter = next();
                else if (a == "--keep") c.Keep = true;
                else if (a == "--sandbox-parent") c.SandboxParent = next();
                else if (a == "--short-parent") c.ShortParent = next();
                else if (a == "--pwsh") c.PwshDir = next();
                else if (a == "--py") c.PyDir = next();
                else if (a == "--mine") c.MineRoot = next();
                else if (a == "--no-processes") c.NoProcesses = true;
                else if (a == "--clean-kept") return CleanKept(c);
                else { Console.Error.WriteLine("unknown argument " + a); return 2; }
            }
            try { c.Init(); }
            catch (Exception e) { Console.WriteLine("SETUP REFUSED: " + e.Message); return 2; }
            C = c;
            Console.WriteLine("S2.1-S2.4 core tests  project=" + c.Project);
            Console.WriteLine("  sandbox=" + c.Sb.Root + "  short=" + c.Short.Root);
            Console.WriteLine("  pwsh=" + c.PwshDir + "  py=" + c.PyDir);

            var tests = new List<KeyValuePair<string, Action>>();
            tests.AddRange(PayloadTests.All());
            tests.AddRange(DiscoveryTests.All());
            tests.AddRange(PreflightTests.All());
            tests.AddRange(RunnerTests.All());
            tests.AddRange(SafetyTests.All());
            tests.AddRange(RecoveryTests.All());
            tests.AddRange(ReviewFixTests.All());
            tests.AddRange(RulesTests.All());

            int pass = 0, fail = 0, skip = 0;
            var sw = Stopwatch.StartNew();
            foreach (var t in tests)
            {
                if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var tsw = Stopwatch.StartNew();
                try
                {
                    t.Value();
                    pass++;
                    Console.WriteLine("PASS  " + t.Key + "  (" + tsw.Elapsed.TotalSeconds.ToString("0.00") + " s)");
                }
                catch (TestSkip e) { skip++; Console.WriteLine("SKIP  " + t.Key + "  -- " + e.Message); }
                catch (Exception e)
                {
                    fail++;
                    Console.WriteLine("FAIL  " + t.Key + "  -- " + (e is TestFailure ? e.Message : e.ToString()));
                }
            }
            c.Dispose();
            Console.WriteLine();
            Console.WriteLine("RESULT  " + pass + " passed, " + fail + " failed, " + skip + " skipped  (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
            foreach (var l in c.Sb.Leftovers.Concat(c.Short.Leftovers)) Console.WriteLine("LEFTOVER " + l);
            return fail > 0 ? 1 : 0;
        }

        internal static KeyValuePair<string, Action> T(string name, Action a) { return new KeyValuePair<string, Action>(name, a); }

        /// <summary>Removes sandboxes left by --keep runs: junctions first (RemoveDirectory on the link only), then the tree
        /// with the reparse-point-refusing remover. Only direct children of the two sandbox parents are touched.</summary>
        private static int CleanKept(Ctx c)
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "docs", "FINAL_DESIGN.md"))) d = Path.GetDirectoryName(d);
            if (d == null) return 2;
            foreach (var parent in new[] { c.SandboxParent ?? Path.Combine(d, "build", "s2", "t"), c.ShortParent ?? Path.Combine(Path.GetTempPath(), "s2") })
            {
                if (!Directory.Exists(parent)) continue;
                foreach (var sb in Directory.GetDirectories(parent))
                {
                    try
                    {
                        foreach (var j in H2GpuLightmapper.Core.Util.SafeFs.FindReparsePoints(sb, 100))
                        {
                            string t;
                            if (H2GpuLightmapper.DllSlots.Junction.TryGetTarget(j, out t)) H2GpuLightmapper.DllSlots.Junction.Remove(j);
                        }
                        int n = H2GpuLightmapper.Core.Util.SafeFs.RemoveTreeNoReparse(sb, parent);
                        Console.WriteLine("removed " + sb + " (" + n + " files)");
                    }
                    catch (Exception e) { Console.WriteLine("LEFT " + sb + ": " + e.Message); }
                }
            }
            return 0;
        }
    }
}
