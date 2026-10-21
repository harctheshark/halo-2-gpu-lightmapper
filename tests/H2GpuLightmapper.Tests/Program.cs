using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// S2.5 + S3.1-S3.4 tests. Usage:
    ///   H2GpuLightmapper.Tests.exe [--filter text] [--keep] [--exe path] [--pwsh dir] [--py dir] [--mine root]
    /// Production is only READ (md5-checked copies of lmw_install.py, lmw_tagio + defs, the estimator DLLs; copies of the
    /// cyclotron / triplicate scenario + triplicate lightmap; a read-only dry run on ProdRoot). Scratch: build\s25\t\&lt;id&gt;
    /// (payload, fixtures, UI snapshots) and a short %TEMP%\s25\&lt;hh&gt;\ (sandbox H2EK roots, LOCALAPPDATA, alias volume).
    /// No GPU, no tool_fast run, no live-tag write: every install goes into a sandbox tags folder.
    /// </summary>
    internal static class Program
    {
        internal static Ctx C;

        private static int Main(string[] args)
        {
            var c = new Ctx();
            string filter = null;
            var skips = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => { if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value"); return args[++i]; };
                if (a == "--filter") filter = next();
                else if (a == "--skip") skips.Add(next());
                else if (a == "--keep") c.Keep = true;
                else if (a == "--exe") c.Exe = next();
                else if (a == "--pwsh") c.PwshDir = next();
                else if (a == "--py") c.PyDir = next();
                else if (a == "--mine") c.MineRoot = next();
                else if (a == "--clean-kept") return CleanKept();
                else { Console.Error.WriteLine("unknown argument " + a); return 2; }
            }
            try { c.Init(); }
            catch (Exception e) { Console.WriteLine("SETUP REFUSED: " + e.Message); return 2; }
            C = c;
            Console.WriteLine("S2.5 + S3 shell tests  project=" + c.Project);
            Console.WriteLine("  exe=" + c.Exe);
            Console.WriteLine("  sandbox=" + c.Sb.Root + "  short=" + c.Short.Root);

            var tests = new List<KeyValuePair<string, Action>>();
            tests.AddRange(UnitTests.All());
            tests.AddRange(DryRunTests.All());
            tests.AddRange(E2eTests.All());
            tests.AddRange(UiAndRulesTests.All());
            tests.AddRange(SceneryFixTests.All());

            int pass = 0, fail = 0, skip = 0;
            var sw = Stopwatch.StartNew();
            foreach (var t in tests)
            {
                if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (skips.Any(k => t.Key.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) { Console.WriteLine("SKIPPED(--skip)  " + t.Key); continue; }
                var tsw = Stopwatch.StartNew();
                try
                {
                    t.Value();
                    pass++;
                    Console.WriteLine("PASS  " + t.Key + "  (" + tsw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
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

        /// <summary>Removes sandboxes left by --keep runs: junctions first (RemoveDirectory on the link only), then the tree with
        /// the reparse-point-refusing remover. Only direct children of the two sandbox parents are touched.</summary>
        private static int CleanKept()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "docs", "FINAL_DESIGN.md"))) d = Path.GetDirectoryName(d);
            if (d == null) return 2;
            foreach (var parent in new[] { Path.Combine(d, @"build\s25\t"), Path.Combine(Path.GetTempPath(), "s25") })
            {
                if (!Directory.Exists(parent)) continue;
                foreach (var sb in Directory.GetDirectories(parent))
                {
                    try
                    {
                        foreach (var j in Core.Util.SafeFs.FindReparsePoints(sb, 100))
                        {
                            string t;
                            if (DllSlots.Junction.TryGetTarget(j, out t)) DllSlots.Junction.Remove(j);
                        }
                        int n = Core.Util.SafeFs.RemoveTreeNoReparse(sb, parent);
                        Console.WriteLine("removed " + sb + " (" + n + " files)");
                    }
                    catch (Exception e) { Console.WriteLine("LEFT " + sb + ": " + e.Message); }
                }
            }
            return 0;
        }
    }
}
