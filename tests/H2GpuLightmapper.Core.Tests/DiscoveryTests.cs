using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class DiscoveryTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("discovery: VDF reader (quotes, escapes, comments, conditions, nesting, old format)", VdfParse);
            yield return T("discovery: Steam on this PC finds H2EK (app 1613450) [read-only]", SteamReal);
            yield return T("discovery: Steam from a fake Steam folder (two libraries, one H2EK)", SteamFake);
            yield return T("discovery: Osoyoos Settings.JSON -> H2 MCC profiles only [read-only]", OsoyoosReal);
            yield return T("discovery: merge order + dedupe (remembered first), Browse accepts tool_fast.exe / tags", MergeBrowse);
            yield return T("discovery: settings.json remembers roots (capped, deduped) and keeps unknown keys", Settings);
        }

        private static void VdfParse()
        {
            string t = "// comment\n\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\"say \\\"hi\\\"\"\n\t\t\"apps\" { \"1613450\" \"5724084276\" }\n\t}\n\t\"1\" \"D:\\\\Old Format\" [$WIN32]\n\tbare token\n}\n";
            var r = Vdf.Parse(t);
            var lf = r.Node("LIBRARYFOLDERS");
            Assert.True(lf != null, "case-insensitive top node");
            Assert.Eq(@"C:\Program Files (x86)\Steam", lf.Node("0").Str("path"), "escaped backslashes");
            Assert.Eq("say \"hi\"", lf.Node("0").Str("label"), "escaped quotes");
            Assert.Eq("5724084276", lf.Node("0").Node("apps").Str("1613450"), "nested block");
            Assert.Eq(@"D:\Old Format", lf.Str("1"), "old-format value with a condition suffix");
            Assert.Eq("token", lf.Str("bare"), "bare tokens");
            Assert.Throws<FormatException>(() => Vdf.Parse("\"a\" { \"b\" \"c\""), "unterminated block refused");
        }

        private static void SteamReal()
        {
            string sp = H2ekDiscovery.SteamPathFromRegistry();
            if (sp == null) throw new TestSkip("no Steam on this PC");
            var libs = H2ekDiscovery.SteamLibraries(sp);
            string lib = Environment.GetEnvironmentVariable("H2LM_TEST_STEAM_LIB");
            if (string.IsNullOrEmpty(lib)) throw new TestSkip("set H2LM_TEST_STEAM_LIB to the Steam library folder that holds the stock H2EK");
            Assert.True(libs.Any(l => string.Equals(l, lib, StringComparison.OrdinalIgnoreCase)), "the Steam library listed: " + string.Join(", ", libs));
            var h = H2ekDiscovery.FromSteam(sp);
            var h2ek = h.FirstOrDefault(x => string.Equals(x.Root, Path.Combine(lib, @"steamapps\common\H2EK"), StringComparison.OrdinalIgnoreCase));
            Assert.True(h2ek != null, "Steam H2EK found: " + string.Join(" | ", h));
            Assert.Contains(h2ek.Label, "11518694", "build id in the label");
            Assert.Contains(h2ek.Label, "Halo 2 Mod Tools", "app name in the label");
        }

        private static void SteamFake()
        {
            string steam = C.Sb.Dir(@"steamfake\Steam");
            string lib2 = C.Sb.Dir(@"steamfake\Lib Two");
            C.Sb.File(@"steamfake\Steam\steamapps\libraryfolders.vdf",
                "\"libraryfolders\"\n{\n \"0\" { \"path\" \"" + steam.Replace("\\", "\\\\") + "\" }\n \"1\" { \"path\" \"" + lib2.Replace("\\", "\\\\") + "\" }\n}\n");
            C.Sb.File(@"steamfake\Lib Two\steamapps\appmanifest_1613450.acf",
                "\"AppState\"\n{\n \"appid\" \"1613450\"\n \"name\" \"Halo 2 Mod Tools - MCC\"\n \"installdir\" \"H2EK\"\n \"buildid\" \"42\"\n}\n");
            C.Sb.Dir(@"steamfake\Lib Two\steamapps\common\H2EK\tags");
            C.Sb.File(@"steamfake\Lib Two\steamapps\common\H2EK\tool_fast.exe", "fake");
            var h = H2ekDiscovery.FromSteam(steam);
            Assert.Eq(1, h.Count, "one H2EK");
            Assert.Eq(Path.Combine(lib2, @"steamapps\common\H2EK"), h[0].Root, "root path");
            var d = H2ekDiscovery.Discover(new DiscoveryOptions { SteamPath = steam, SkipOsoyoos = true });
            Assert.True(d.Count == 1 && d[0].HasToolFast && d[0].Source == RootSource.Steam, "Discover with the fake Steam");
        }

        private static void OsoyoosReal()
        {
            string p = H2ekDiscovery.DefaultOsoyoosSettings();
            if (!File.Exists(p)) throw new TestSkip("no Osoyoos Settings.JSON on this PC");
            var r = H2ekDiscovery.FromOsoyoos(p);
            var roots = r.Select(x => x.Root).ToList();
            Assert.True(roots.Any(x => string.Equals(x.TrimEnd('\\'), C.MineRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)), "the production-root profile: " + string.Join(", ", roots));
            string longRoot = Environment.GetEnvironmentVariable("H2LM_TEST_LONGPATH_ROOT");
            if (!string.IsNullOrEmpty(longRoot)) Assert.True(roots.Any(x => string.Equals(x.TrimEnd('\\'), longRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)), "the long-path profile");
            Assert.True(!r.Any(x => x.Label.Contains("H2Codez")), "the release_standalone (H2V) profile is excluded");
            // a synthetic file: array form, tag_path fallback, a non-H2 profile
            string f = C.Sb.File(@"osoy\Settings.JSON", "[{\"profile_name\":\"A\",\"generation\":\"halo2\",\"build_type\":\"release_mcc\",\"tool_fast_path\":\"\",\"tool_path\":\"\",\"tag_path\":\"X:\\\\Mods\\\\H2EK\\\\tags\"}," +
                "{\"profile_name\":\"B\",\"generation\":\"halo3\",\"build_type\":\"release_mcc\",\"tool_fast_path\":\"Y:\\\\H3EK\\\\tool_fast.exe\"}]");
            var s = H2ekDiscovery.FromOsoyoos(f);
            Assert.Eq(1, s.Count, "only the halo2 MCC profile");
            Assert.Eq(@"X:\Mods\H2EK", s[0].Root, "root from tag_path");
        }

        private static void MergeBrowse()
        {
            string mine = C.MineRoot;
            string p = H2ekDiscovery.DefaultOsoyoosSettings();
            var d = H2ekDiscovery.Discover(new DiscoveryOptions { Remembered = new[] { mine.ToLowerInvariant() + "\\" }, OsoyoosSettings = File.Exists(p) ? p : null, SkipOsoyoos = !File.Exists(p) });
            var first = d.First();
            Assert.Eq(RootSource.Remembered, first.Source, "remembered first");
            Assert.True(d.Count(x => string.Equals(x.Root, mine, StringComparison.OrdinalIgnoreCase)) == 1, "ProdRoot listed once (deduped)");
            Assert.True(first.HasToolFast, "ProdRoot has tool_fast.exe");
            var b1 = H2ekDiscovery.FromBrowse(Path.Combine(mine, "tool_fast.exe"));
            Assert.True(string.Equals(b1.Root, mine, StringComparison.OrdinalIgnoreCase) && b1.Source == RootSource.Browse, "browse tool_fast.exe");
            var b2 = H2ekDiscovery.FromBrowse(Path.Combine(mine, "tags"));
            Assert.True(string.Equals(b2.Root, mine, StringComparison.OrdinalIgnoreCase), "browse tags folder");
        }

        private static void Settings()
        {
            string p = Path.Combine(C.Sb.Dir("settings"), "settings.json");
            var s = AppSettings.Load(p);
            Assert.Eq(0, s.Roots.Count, "empty");
            for (int i = 0; i < 15; i++) s.Remember(@"X:\root" + i);
            s.Remember(@"x:\ROOT3\");
            s["futureKey"] = "kept";
            s.LastScenario = @"scenarios\multi\cyclotron\cyclotron";
            s.Save();
            var t = AppSettings.Load(p);
            Assert.Eq(AppSettings.MaxRoots, t.Roots.Count, "capped");
            Assert.Eq(@"x:\ROOT3", t.Roots[0], "most recent first, deduped case-insensitively");
            Assert.Eq(1, t.Roots.Count(r => r.Equals(@"X:\root3", StringComparison.OrdinalIgnoreCase)), "no duplicate");
            Assert.Eq("kept", t["futureKey"] as string, "unknown key preserved");
            Assert.Eq(@"scenarios\multi\cyclotron\cyclotron", t.LastScenario, "last scenario");
            File.WriteAllText(p, "{ not json");
            Assert.Eq(0, AppSettings.Load(p).Roots.Count, "a damaged file loads as empty, never fatal");
        }
    }
}
