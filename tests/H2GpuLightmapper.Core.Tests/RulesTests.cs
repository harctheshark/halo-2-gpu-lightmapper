using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>
    /// The code rule of plan step S2.4: no recursive delete anywhere in the shell (old farm dirs and _gpulm_skel hold
    /// junctions into the live tags). Every .cs file of the shell core, S0.2's Proc, S0.4's DllSlots and these tests is
    /// scanned. The patterns are assembled from pieces so this file does not match itself.
    /// </summary>
    internal static class RulesTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("rules: no recursive delete in the shell sources (code rule)", NoRecursiveDelete);
            yield return T("rules: the single tree remover is only called for rt\\ / WORK\\_runtime\\ (and test sandboxes)", TreeRemoverCallers);
        }

        private static IEnumerable<string> Sources()
        {
            foreach (var d in new[] { @"src\H2GpuLightmapper.Core", @"src\Proc", @"src\H2GpuLightmapper.DllSlots", @"tests\H2GpuLightmapper.Core.Tests" })
            {
                string dir = Path.Combine(C.Project, d);
                foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                    if (f.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) < 0 && f.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) < 0) yield return f;
            }
        }

        private static void NoRecursiveDelete()
        {
            string dd = "Directory" + "." + "Delete";
            var pats = new[]
            {
                new Regex(Regex.Escape(dd) + @"\s*\([^;]*,\s*(true|recursive\s*:\s*true)\s*\)"),
                new Regex(@"\." + "Delete" + @"\s*\(\s*(true|recursive\s*:\s*true)\s*\)"),       // DirectoryInfo, recursive overload
                new Regex("rm" + "tree"),
                new Regex("Delete" + "Directory" + @"\s*\("),                                     // Microsoft.VisualBasic FileSystem
                new Regex("SHFile" + "Operation"),
                new Regex(@"(?i)\b(rd|rmdir)\s+/" + "s\\b"),
                new Regex("(?i)-Re" + @"curse\b[^\r\n]*-Force|Remove" + @"-Item[^\r\n]*-Re" + "curse"),
            };
            var hits = new List<string>();
            int files = 0;
            foreach (var f in Sources())
            {
                files++;
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                    foreach (var p in pats)
                        if (p.IsMatch(lines[i])) hits.Add(f.Substring(C.Project.Length + 1) + ":" + (i + 1) + ": " + lines[i].Trim());
            }
            Assert.True(files > 30, "scanned " + files + " files");
            Assert.True(hits.Count == 0, "recursive delete found:\n    " + string.Join("\n    ", hits));
        }

        private static void TreeRemoverCallers()
        {
            var callers = new List<string>();
            foreach (var f in Sources())
            {
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains("RemoveTreeNoReparse(") && !lines[i].TrimStart().StartsWith("///") && !lines[i].Contains("public static int RemoveTreeNoReparse"))
                        callers.Add(Path.GetFileName(f));
            }
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PayloadInstaller.cs", "Harness.cs", "Program.cs" };
            var bad = callers.Where(c => !allowed.Contains(c)).ToList();
            Assert.True(callers.Contains("PayloadInstaller.cs"), "used by RemoveOldVersion");
            Assert.True(bad.Count == 0, "unexpected callers: " + string.Join(", ", bad));
        }
    }
}
