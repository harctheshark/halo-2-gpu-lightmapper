using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>The window: XAML loads and renders (off-screen, never activated; software rendering) with the defaults of
    /// section 4, and the code rules (no recursive delete) over this step's sources.</summary>
    internal static class UiAndRulesTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("ui: the window loads, renders off-screen and exits (snapshot PNG, sandbox root)", Snapshot);
            yield return T("ui: the XAML carries the section-4 defaults (Super, Standard, AO off 1.0 / 0.5, reuse off, 50M / 16)", XamlDefaults);
            yield return T("ui: the window's Bake (defaults: Super / Standard / install) on triplicate runs the whole probe path (sandbox)", UiAutoBake);
            yield return T("rules: no recursive delete in the exe and its tests (code rule)", NoRecursiveDelete);
        }

        private static void Snapshot()
        {
            var r = C.NewRoot();
            // E1 (0.6.1): Custom is shown (as "Experimental") whenever the payload carries the guarded engine; no Custom switch
            string guarded = C.BuildPayload(C.Sb.Dir("payload_ui_guarded"), customEngine: true);
            foreach (var dev in new[] { false, true })
            {
                string png = Path.Combine(C.Sb.Dir("ui"), dev ? "window_custom.png" : "window.png");
                var a = new List<string> { "--payload-dir", dev ? guarded : C.PayloadDir, "--localappdata", C.Lad, "--alias-volume-root", C.Vol, "--rt-pwsh", C.PwshDir, "--rt-py", C.PyDir,
                                           "--h2ek", r.Root, "--scenario", @"scenarios\multi\triplicate\triplicate", "--bsp", "triplicate",
                                           "--dev-ui-snapshot", png, "--dev-ui-snapshot-delay-ms", "6000" };
                if (dev) a.AddRange(new[] { "--dev-probes", "--dev-ui-preset", "custom,ao,advanced,lightmap" });
                var res = C.Run(a, 120000);
                Assert.Eq(0, res.Exit, "the window exited cleanly: " + res);
                Assert.True(!File.Exists(png + ".error.txt"), "snapshot error: " + (File.Exists(png + ".error.txt") ? File.ReadAllText(png + ".error.txt") : ""));
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 20000, "snapshot written: " + png);
                var dec = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var f = dec.Frames[0];
                Assert.True(f.PixelWidth >= 700 && f.PixelHeight >= 600, "size " + f.PixelWidth + "x" + f.PixelHeight);
                var px = new byte[4];
                f.CopyPixels(new System.Windows.Int32Rect(5, f.PixelHeight / 2, 1, 1), px, 4, 0);
                Assert.True(px[0] < 60 && px[1] < 60 && px[2] < 60, "dark background at the left edge: " + string.Join(",", px));
            }
            var shots = Path.Combine(C.Project, @"tests\H2GpuLightmapper.Tests\results");
            Directory.CreateDirectory(shots);
            File.Copy(Path.Combine(C.Sb.Dir("ui"), "window.png"), Path.Combine(shots, "window_default.png"), true);
            File.Copy(Path.Combine(C.Sb.Dir("ui"), "window_custom.png"), Path.Combine(shots, "window_custom_experimental.png"), true);
            Assert.True(Directory.Exists(Path.Combine(r.Work, @"_runtime\0.5.0-s25cccc")), "the window prepared the runtime for the root");
        }

        private static void UiAutoBake()
        {
            var r = C.NewRoot();
            string png = Path.Combine(C.Sb.Dir("ui"), "window_after_bake.png");
            var a = new List<string> { "--payload-dir", C.PayloadDir, "--localappdata", C.Lad, "--alias-volume-root", C.Vol, "--rt-pwsh", C.PwshDir, "--rt-py", C.PyDir,
                                       "--h2ek", r.Root, "--scenario", @"scenarios\multi\triplicate\triplicate", "--bsp", "triplicate",
                                       "--dev-ui-snapshot", png, "--dev-ui-snapshot-delay-ms", "6000", "--dev-ui-autobake", "--dev-skip-preflight", "--dev-skip-gpucheck", "--dev-probes" };
            var res = C.Run(a, 240000);
            Assert.Eq(0, res.Exit, "the window exited cleanly: " + res);
            Assert.True(!File.Exists(png + ".timeout.png"), "the bake finished before the guard");
            Assert.True(File.Exists(png), "snapshot after the bake");
            byte[] b = File.ReadAllBytes(r.TriLtmp);
            Assert.Eq("SHSHSHSH", System.Text.Encoding.ASCII.GetString(b, b.Length - 8, 8), "Standard on triplicate installed the merged SH lightmap");
            var h = Core.Util.Json.ReadObjFile(Path.Combine(r.Work, @"standalone\history.json")).Arr("runs").OfType<Core.Util.JObj>().Last();
            Assert.Eq("DONE", h.Str("state"), "history DONE");
            Assert.Eq("standard", h.Str("mode"), "default mode");
            Assert.Eq("super", h.Str("quality"), "default quality");
            var st = Core.Util.Json.ReadObjFile(Path.Combine(C.Lad, @"H2GpuLightmapper\settings.json"));
            Assert.Eq(r.Root, st.Str("lastRoot"), "the window remembers the root");
            Assert.Eq(@"scenarios\multi\triplicate\triplicate", st.Str("lastScenario"), "and the map");
            File.Copy(png, Path.Combine(C.Project, @"tests\H2GpuLightmapper.Tests\results\window_after_bake.png"), true);
        }

        private static void XamlDefaults()
        {
            string x = File.ReadAllText(Path.Combine(C.Project, @"src\H2GpuLightmapper\MainWindow.xaml"));
            Func<string, string> tag = name => { var m = Regex.Match(x, "<[A-Za-z]+ x:Name=\"" + name + "\"[^>]*>", RegexOptions.Singleline); if (!m.Success) throw new TestFailure("no " + name); return m.Value; };
            Assert.Contains(tag("QSuper"), "IsChecked=\"True\"", "Super is the default");
            Assert.NotContains(tag("QLow"), "IsChecked", "Low not default");
            // E1 (0.6.1): collapsed in the XAML, shown by code when the payload carries the guarded engine; labelled Experimental
            Assert.Contains(tag("QCustom"), "Visibility=\"Collapsed\"", "Custom starts collapsed (shown only with the guarded engine)");
            Assert.Contains(tag("QCustom"), "Content=\"Custom (Experimental)\"", "Custom is labelled Experimental");
            Assert.Contains(tag("CustomNote"), "Experimental", "the Custom note says Experimental");
            Assert.Contains(tag("CustomNote"), "very long bakes", "the Custom note warns about long bakes");
            Assert.Contains(tag("MStandard"), "IsChecked=\"True\"", "Standard is the default");
            Assert.Contains(tag("MStandard"), "lightmaps + light probes if this BSP has probes", "Standard label");
            Assert.Contains(tag("MProbes"), "probes only", "Lightprobes label");
            Assert.Contains(tag("MLightmap"), "Lightmap Only", "Lightmap Only label");
            Assert.NotContains(tag("AoCheck"), "IsChecked=\"True\"", "AO off by default");
            Assert.Contains(tag("AoStrengthBox"), "Text=\"1.0\"", "strength 1.0");
            Assert.Contains(tag("AoExponentBox"), "Text=\"0.5\"", "exponent 0.5");
            Assert.NotContains(tag("ReuseCheck"), "IsChecked=\"True\"", "reuse off by default");
            Assert.Contains(tag("PhotonsBox"), "Text=\"50,000,000\"", "photons 50M");
            Assert.Contains(tag("RaysBox"), "Text=\"16\"", "rays 16");
            Assert.Contains(tag("AdvancedExpander"), "IsExpanded=\"False\"", "Advanced collapsed");
            Assert.NotContains(tag("StageOnlyCheck"), "IsChecked=\"True\"", "stage only off");
            foreach (var b in new[] { "BakeBtn", "CancelBtn", "RestoreBtn", "LogsBtn" }) tag(b);
            Assert.Contains(x, "Reset to 50M / 16", "reset link");
            Assert.Contains(x, "Gather rays (Monte-Carlo samples)", "rays label");
            Assert.NotContains(x.ToLowerInvariant(), "plucker", "the fixes are not shown");
        }

        private static IEnumerable<string> Sources()
        {
            foreach (var d in new[] { @"src\H2GpuLightmapper", @"tests\H2GpuLightmapper.Tests" })
                foreach (var f in Directory.GetFiles(Path.Combine(C.Project, d), "*.*", SearchOption.AllDirectories))
                    if ((f.EndsWith(".cs") || f.EndsWith(".py") || f.EndsWith(".ps1") || f.EndsWith(".xaml")) && f.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) < 0
                        && f.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) < 0) yield return f;
        }

        private static void NoRecursiveDelete()
        {
            string dd = "Directory" + "." + "Delete";
            var pats = new[]
            {
                new Regex(Regex.Escape(dd) + @"\s*\([^;]*,\s*(true|recursive\s*:\s*true)\s*\)"),
                new Regex(@"\." + "Delete" + @"\s*\(\s*(true|recursive\s*:\s*true)\s*\)"),
                new Regex("rm" + "tree"),
                new Regex("Delete" + "Directory" + @"\s*\("),
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
            Assert.True(files > 15, "scanned " + files + " files");
            Assert.True(hits.Count == 0, "recursive delete found:\n    " + string.Join("\n    ", hits));
            string remover = "RemoveTree" + "NoReparse(";      // assembled so this file does not match itself
            var callers = Sources().Where(f => File.ReadAllText(f).Contains(remover)).Select(f => f.Substring(C.Project.Length + 1)).ToList();
            var allowed = new[] { @"tests\H2GpuLightmapper.Tests\Harness.cs", @"tests\H2GpuLightmapper.Tests\Program.cs" };
            Assert.True(callers.All(c => allowed.Contains(c, StringComparer.OrdinalIgnoreCase)), "the tree remover is only used by the test sandbox cleanup: " + string.Join(", ", callers));
        }
    }
}
