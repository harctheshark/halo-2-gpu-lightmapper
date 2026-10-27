using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>
    /// SF1 (Scenery Fix Dependencies): the report parser, the scanner runner (a FAKE scanner for every exit-code path, the REAL
    /// tools\scan_scenery_bounds.py on a scratch H2EK for the end-to-end path), the legacy-to-new seed, and the exact log
    /// strings. Everything that --apply's runs in the sandbox; the production H2EK is only READ (three render_models + one
    /// scenario are copied out of it). The live database is never touched.
    /// </summary>
    internal static class SceneryFixTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("scenery fix: log strings (the added-model line, the conflict warning)", Strings);
            yield return T("scenery fix: argv = -B scan.py ROOT [--scenario S] --apply --json OUT", ArgsShape);
            yield return T("scenery fix: report parser (added_models strings / objects; older report -> missing[] when applied; conflicts)", ParseVariants);
            yield return T("scenery fix: fake scanner exit 0 -> models added, report read", FakeOk);
            yield return T("scenery fix: fake scanner exit 3 (conflicts) still Ran, conflicts listed, nothing blocks", FakeConflicts);
            yield return T("scenery fix: scanner failures (exit 2, crash, no report, bad JSON, timeout, missing script) -> Ran=false with a reason, no throw", FakeFailures);
            yield return T("scenery fix: REAL scanner, scenario scan on a scratch H2EK: added once, nothing the second time", RealScenario);
            yield return T("scenery fix: REAL scanner, full scan on a scratch H2EK: the scenario's model is a no-op afterwards", RealFull);
            yield return T("scenery fix: Summarize lists scanned / added / conflicts / file", SummarizeText);
            yield return T("scenery fix 0.6.4: REAL scanner skips sky models (sky tag -> model -> render_model, and a 'skies' folder); the result lists the added names and the skipped skies", RealSkySkipped);
            yield return T("scenery fix 0.6.4: Summarize lists every added model name (past 60), the skipped skies, and the one-line status shows the first names", SummarizeNames);
        }

        private static string Py { get { return Path.Combine(C.PyDir, "py.exe"); } }

        private static void Strings()
        {
            Assert.Eq(@"h1\vehicles\lifepod\lifepod.render_model wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!",
                      SceneryFixDeps.AddedLine(@"h1\vehicles\lifepod\lifepod.render_model"), "the added line, exactly");
            var w = SceneryFixDeps.ConflictLine(new SceneryConflict { Model = "m", Sections = 3, Conflicts = 2 });
            Assert.True(w.StartsWith("WARNING: m: "), "warning prefix");
            Assert.Contains(w, "2 of 3 compressed section(s)", "counts");
            Assert.Contains(w, "inside this ONE model", "a true conflict is within one model");
            Assert.Contains(w, "Continuing on", "says it continues");
            Assert.Eq("m had a different size bound in the Scenery Fix Dependencies database than its tag has now -- updated automatically, continuing on!", SceneryFixDeps.UpdatedLine("m"), "the updated line");
            Assert.Eq("SceneryFixDependencies.db", SceneryFixDeps.DbName, "the file name the user asked for");
            Assert.Eq("h2v_scenery_bounds.db", SceneryFixDeps.LegacyDbName, "the legacy name is kept");
        }

        private static void ArgsShape()
        {
            Assert.Seq(new[] { "-B", "s.py", "R", "--apply", "--json", "o.json" }, SceneryFixDeps.Args("s.py", "R", null, "o.json"), "full scan argv");
            Assert.Seq(new[] { "-B", "s.py", "R", "--scenario", @"scenarios\a\a", "--apply", "--json", "o.json" }, SceneryFixDeps.Args("s.py", "R", @"scenarios\a\a", "o.json"), "scenario scan argv");
        }

        private static void ParseVariants()
        {
            var r = new SceneryScanResult();
            Assert.True(SceneryFixDeps.ParseReport("{\"models\":4,\"lines_to_add\":2,\"applied\":true,\"db\":\"X\\\\d.db\",\"added_models\":[\"a\\\\a.render_model\",{\"model\":\"b\\\\b.render_model\"},\"A\\\\A.RENDER_MODEL\"],\"missing\":[{\"model\":\"zzz\"}],\"conflict\":[{\"model\":\"c\",\"sections\":5,\"conflict\":2}],\"nobound\":[{\"model\":\"n\"}],\"nosec\":[\"u\"],\"updated_models\":[\"up\\\\u.render_model\"],\"also_added_models\":[\"o1\",\"o2\",\"o3\"],\"seeding\":true,\"key_collisions\":121,\"legacy_db\":\"X\\\\l.db\"}", r) == null, "parses");
            Assert.Seq(new[] { @"up\u.render_model" }, r.Updated, "updated_models");
            Assert.Eq(3, r.OtherAdded, "also_added_models counted"); Assert.True(r.Seeding, "seeding"); Assert.Eq(121, r.KeyCollisions, "key collisions"); Assert.Eq(@"X\l.db", r.LegacyDb, "legacy db");
            Assert.Eq(4, r.Models, "models"); Assert.Eq(2, r.LinesToAdd, "lines"); Assert.True(r.Applied, "applied");
            Assert.Seq(new[] { @"a\a.render_model", @"b\b.render_model" }, r.Added, "added_models: strings and objects, case-insensitive dedup, 'missing' ignored when added_models exists");
            Assert.Eq(1, r.Conflicts.Count, "one conflict"); Assert.Eq(2, r.Conflicts[0].Conflicts, "conflict count"); Assert.Eq(5, r.Conflicts[0].Sections, "sections");
            Assert.Eq(1, r.NoBound, "nobound"); Assert.Eq(1, r.Unparsed, "nosec");
            Assert.Eq(@"X\d.db", r.Db, "db from the report");

            var old = new SceneryScanResult();
            SceneryFixDeps.ParseReport("{\"models\":1,\"applied\":true,\"missing\":[{\"model\":\"m1\"},{\"model\":\"m2\"}]}", old);
            Assert.Seq(new[] { "m1", "m2" }, old.Added, "an older report (no added_models) that was applied: missing[] = the added models");

            var dry = new SceneryScanResult();
            SceneryFixDeps.ParseReport("{\"models\":1,\"missing\":[{\"model\":\"m1\"}]}", dry);
            Assert.Eq(0, dry.Added.Count, "not applied: nothing was added");

            var empty = new SceneryScanResult();
            SceneryFixDeps.ParseReport("{\"models\":1,\"applied\":true,\"added_models\":[]}", empty);
            Assert.Eq(0, empty.Added.Count, "empty added_models");
            Assert.True(SceneryFixDeps.ParseReport("not json", new SceneryScanResult()) != null, "bad JSON is reported");
        }

        private static string Fake(string name, string body)
        {
            // argv: -B script ROOT [--scenario S] --apply --json OUT   (this fake only needs the --json path)
            string code = "import sys, json\n"
                        + "out = sys.argv[sys.argv.index('--json') + 1]\n"
                        + body + "\n";
            return C.Sb.File("sf\\" + name + ".py", code);
        }

        private static SceneryScanResult RunFake(string script, int timeoutMs = 60000, string scenario = null)
        {
            string root = C.Sb.Dir("sf\\fakeroot");
            return SceneryFixDeps.Run(Py, script, root, scenario, C.Sb.Dir("sf"), null, timeoutMs, Path.Combine(C.Sb.Dir("sf\\out"), "scan.json"));
        }

        private static void FakeOk()
        {
            string s = Fake("ok", "json.dump({'models': 7, 'lines_to_add': 3, 'applied': True, 'added_models': ['x\\\\x.render_model', 'y\\\\y.render_model'], 'conflict': [], 'nobound': [], 'nosec': [], 'missing': [], 'ok': []}, open(out, 'w'))\nprint('scanned 7')\nsys.exit(0)");
            var r = RunFake(s, 60000, "scenarios/a/a");
            Assert.True(r.Ran, "ran: " + r.Error);
            Assert.Eq(0, r.ExitCode, "exit 0");
            Assert.Eq(7, r.Models, "models");
            Assert.Seq(new[] { @"x\x.render_model", @"y\y.render_model" }, r.Added, "added");
            Assert.True(r.Applied, "applied");
            Assert.Eq("scenarios/a/a", r.Scenario, "scenario kept");
            Assert.Contains(string.Join("\n", r.Output), "scanned 7", "stdout kept");
        }

        private static void FakeConflicts()
        {
            string s = Fake("conf", "json.dump({'models': 3, 'applied': True, 'lines_to_add': 1, 'added_models': ['q\\\\q.render_model'], 'conflict': [{'model': 'k\\\\k.render_model', 'sections': 4, 'missing': 0, 'conflict': 4, 'nobound': 0}]}, open(out, 'w'))\nsys.exit(3)");
            var r = RunFake(s);
            Assert.True(r.Ran, "exit 3 is not a failure: " + r.Error);
            Assert.Eq(3, r.ExitCode, "exit 3");
            Assert.Eq(1, r.Added.Count, "the non-conflicting model was added");
            Assert.Eq(@"k\k.render_model", r.Conflicts.Single().Model, "conflict listed");
        }

        private static void FakeFailures()
        {
            var two = RunFake(Fake("e2", "print('scenario not found: X')\nsys.exit(2)"));
            Assert.True(!two.Ran, "exit 2"); Assert.Contains(two.Error, "exit 2", "reason"); Assert.Contains(two.Error, "scenario not found", "scanner's own words");
            var crash = RunFake(Fake("crash", "raise RuntimeError('boom')"));
            Assert.True(!crash.Ran, "crash"); Assert.True(crash.Error != null && crash.Error.Length > 0, "reason given");
            var nojson = RunFake(Fake("nojson", "sys.exit(0)"));
            Assert.True(!nojson.Ran, "no report"); Assert.Contains(nojson.Error, "no report", "says so");
            var bad = RunFake(Fake("badjson", "open(out, 'w').write('{nope')\nsys.exit(0)"));
            Assert.True(!bad.Ran, "bad json"); Assert.Contains(bad.Error, "JSON", "says so");
            var slow = RunFake(Fake("slow", "import time\ntime.sleep(60)"), 2000);
            Assert.True(!slow.Ran && slow.TimedOut, "timeout"); Assert.Contains(slow.Error, "timed out", "says so");
            var none = SceneryFixDeps.Run(Py, Path.Combine(C.Sb.Root, "sf", "nope.py"), C.Sb.Dir("sf\\fakeroot"), null, C.Sb.Dir("sf"), null, 5000, Path.Combine(C.Sb.Root, "sf", "out", "n.json"));
            Assert.True(!none.Ran, "missing script"); Assert.Contains(none.Error, "no scan_scenery_bounds.py", "says so");
            // a stale report from an earlier scan must not be read as this scan's
            string stale = Path.Combine(C.Sb.Dir("sf\\out"), "scan.json");
            File.WriteAllText(stale, "{\"models\":99,\"applied\":true,\"added_models\":[\"stale\"]}");
            var again = RunFake(Fake("nojson2", "sys.exit(0)"));
            Assert.True(!again.Ran, "the stale report was deleted before the run");
        }

        // ------------------------------------------------------------------ the real scanner on a scratch H2EK

        private static string Scanner { get { return Path.Combine(C.Project, "tools", "scan_scenery_bounds.py"); } }

        /// <summary>The small test map's scenario (tags-relative, without extension): the environment variable H2LM_TEST_SCENARIO, e.g. scenarios\multi\NAME\NAME. It must place one
        /// scenery chain, the lifepod. Without it the tests that need it are skipped.</summary>
        private static string TestScenario
        {
            get
            {
                string s = Environment.GetEnvironmentVariable("H2LM_TEST_SCENARIO");
                if (string.IsNullOrWhiteSpace(s)) throw new TestSkip("set H2LM_TEST_SCENARIO to a small test map's scenario (tags-relative, no extension) that places one lifepod scenery chain");
                return s.Trim();
            }
        }

        /// <summary>A scratch H2EK holding the small test map's scenario and its one scenery chain (lifepod), copied (READ) from the mine root.</summary>
        private static string MakeRoot(string leaf, bool withChain)
        {
            string root = C.Sb.Dir("sf\\" + leaf);
            string src = Path.Combine(C.MineRoot, "tags");
            Func<string, string> cp = rel =>
            {
                string from = Path.Combine(src, rel), to = Path.Combine(root, "tags", rel);
                if (!File.Exists(from)) throw new TestSkip("no " + from + " in the mine root");
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to);
                return to;
            };
            cp(TestScenario + ".scenario");
            if (withChain) foreach (var e in new[] { "scenery", "model", "render_model" }) cp(@"h1\vehicles\lifepod\lifepod." + e);
            return root;
        }

        private static SceneryScanResult RunReal(string root, string scenario)
        {
            if (!File.Exists(Scanner)) throw new TestSkip("no " + Scanner);
            return SceneryFixDeps.Run(Py, Scanner, root, scenario, C.Sb.Dir("sf"), null, 120000, Path.Combine(C.Sb.Dir("sf\\out"), "real_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json"));
        }

        private static void RealScenario()
        {
            string root = MakeRoot("realroot", true);
            // a legacy database the user already has: its lines stay byte-for-byte, a timestamped backup is made before it changes
            string legacy = Path.Combine(root, SceneryFixDeps.LegacyDbName);
            File.WriteAllBytes(legacy, System.Text.Encoding.ASCII.GetBytes("# keep me\r\n"));
            var r1 = RunReal(root, TestScenario);
            Assert.True(r1.Ran, "ran: " + r1.Error + " | " + string.Join(" | ", r1.Output));
            Assert.Eq(1, r1.Models, "the scenario places one render_model");
            Assert.Seq(new[] { @"h1\vehicles\lifepod\lifepod.render_model" }, r1.Added, "the lifepod was not in an empty database: added");
            Assert.True(r1.Applied && File.Exists(r1.Db), "the database was written: " + r1.Db);
            Assert.Eq(SceneryFixDeps.DbPath(root), r1.Db, "SceneryFixDependencies.db in the H2EK root is the file");
            Assert.True(r1.Seeding, "the first run builds the database");
            Assert.True(File.ReadAllText(legacy).StartsWith("# keep me\r\n"), "the legacy file's existing line is kept byte-for-byte");
            Assert.Eq(1, Directory.GetFiles(root, SceneryFixDeps.LegacyDbName + ".bak_*").Length, "the legacy file was backed up before it changed");
            string md5 = SafeFs.Md5File(r1.Db);
            var r2 = RunReal(root, TestScenario);
            Assert.True(r2.Ran, "second run: " + r2.Error);
            Assert.Eq(0, r2.Added.Count, "nothing to add the second time");
            Assert.Eq(md5, SafeFs.Md5File(r2.Db), "the database file is byte-identical after a no-op scan");
            Assert.Eq(0, Directory.GetFiles(Path.GetDirectoryName(r2.Db), Path.GetFileName(r2.Db) + ".bak_*").Length, "no backup is made by the first write (nothing to back up) or by a no-op scan");
            Assert.Eq(1, Directory.GetFiles(root, SceneryFixDeps.LegacyDbName + ".bak_*").Length, "a no-op scan makes no further backup");
            Assert.True(!r2.Seeding, "the second run does not seed again");
            var bad = RunReal(root, @"scenarios\multi\nope\nope");
            Assert.True(!bad.Ran && bad.ExitCode == 2, "an unknown scenario is the scanner's exit 2: " + bad.Error);
        }

        private static void RealFull()
        {
            string root = MakeRoot("fullroot", true);
            var full = RunReal(root, null);
            Assert.True(full.Ran, "full scan ran: " + full.Error);
            Assert.True(full.Models >= 1, "scanned the tags folder");
            Assert.Contains(string.Join("|", full.Added), "lifepod", "the lifepod was added by the full scan");
            var scen = RunReal(root, TestScenario);
            Assert.True(scen.Ran, "scenario scan after the full scan: " + scen.Error);
            Assert.Eq(0, scen.Added.Count, "already in the database: nothing added");
            Assert.True(full.Db != null && scen.Db == full.Db, "both scans name the same database file");
        }

        /// <summary>0.6.4: a sky never needs the scenery fix. Two ways a render_model is a sky model: a .sky tag reaches it (here a synthetic sky tag whose bytes hold the
        /// lifepod's tag path -- the scanner finds tag references as null-terminated path strings, like it does for a scenario), or it lives in a folder named 'skies'
        /// (the stock layout; the real cyclotron sky chain is copied when the mine root has it).</summary>
        private static void RealSkySkipped()
        {
            string root = MakeRoot("skyroot", true);
            string skyTag = Path.Combine(root, "tags", @"effects\fakesky\fakesky.sky");
            Directory.CreateDirectory(Path.GetDirectoryName(skyTag));
            File.WriteAllBytes(skyTag, System.Text.Encoding.ASCII.GetBytes("junk\0" + @"h1\vehicles\lifepod\lifepod" + "\0tail\0"));
            string src = Path.Combine(C.MineRoot, "tags");
            bool haveReal = true;
            foreach (var e in new[] { "sky", "model", "render_model" })
            {
                string from = Path.Combine(src, @"scenarios\skies\multi\cyclotron\cyclotron." + e), to = Path.Combine(root, "tags", @"scenarios\skies\multi\cyclotron\cyclotron." + e);
                if (!File.Exists(from)) { haveReal = false; break; }
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to);
            }
            var full = RunReal(root, null);
            Assert.True(full.Ran, "full scan ran: " + full.Error + " | " + string.Join(" | ", full.Output));
            Assert.True(full.SkyExcluded.Any(m => m.StartsWith(@"h1\vehicles\lifepod\lifepod", StringComparison.OrdinalIgnoreCase)), "the sky tag's render_model is skipped: " + string.Join(", ", full.SkyExcluded));
            Assert.True(!full.Added.Any(m => m.IndexOf("lifepod", StringComparison.OrdinalIgnoreCase) >= 0), "a sky model is not added to the database: " + string.Join(", ", full.Added));
            string db = File.Exists(full.Db) ? File.ReadAllText(full.Db) : "";
            Assert.True(db.IndexOf("lifepod", StringComparison.OrdinalIgnoreCase) < 0, "and has no line in the database file");
            if (haveReal)
            {
                Assert.True(full.SkyExcluded.Any(m => m.IndexOf(@"scenarios\skies\multi\cyclotron\cyclotron", StringComparison.OrdinalIgnoreCase) >= 0), "the stock cyclotron sky model is skipped");
                Assert.True(!full.Added.Any(m => m.IndexOf(@"skies\", StringComparison.OrdinalIgnoreCase) >= 0), "no sky model among the added");
            }
            string text = string.Join("\n", SceneryFixDeps.Summarize(full));
            Assert.Contains(text, "Skipped " + full.SkyExcluded.Count + " sky model(s)", "the dialog says how many skies were skipped");
            Assert.Contains(text, @"h1\vehicles\lifepod\lifepod", "and names them");
            // the same root WITHOUT the sky tag: the lifepod is an ordinary model again (added), so the skip really comes from the sky reference
            string root2 = MakeRoot("skyroot2", true);
            var plain = RunReal(root2, null);
            Assert.True(plain.Ran && plain.Added.Any(m => m.IndexOf("lifepod", StringComparison.OrdinalIgnoreCase) >= 0) && plain.SkyExcluded.Count == 0, "without a sky reference the lifepod is added and nothing is skipped");
        }

        private static void SummarizeNames()
        {
            var r = new SceneryScanResult { Ran = true, Models = 500, Db = @"X\SceneryFixDependencies.db", Seconds = 1.0, JsonPath = @"X\scan.json" };
            for (int i = 0; i < 120; i++) r.Added.Add(@"objects\thing" + i.ToString("D3") + @"\thing" + i.ToString("D3") + ".render_model");
            r.SkyExcluded.AddRange(new[] { @"scenarios\skies\a\a.render_model", @"scenarios\skies\b\b.render_model" });
            string t = string.Join("\n", SceneryFixDeps.Summarize(r));
            Assert.Contains(t, "Added 120 model(s) the database did not know", "count");
            Assert.Contains(t, @"    objects\thing000\thing000.render_model", "the first name");
            Assert.Contains(t, @"    objects\thing119\thing119.render_model", "the 120th name is listed too (no cap at 60)");
            Assert.NotContains(t, "... and", "nothing is cut off at this size");
            Assert.Contains(t, "Skipped 2 sky model(s)", "skipped skies");
            Assert.Contains(t, @"    scenarios\skies\b\b.render_model", "named");
            Assert.Eq(" (thing000, thing001, thing002, ...)", SceneryFixDeps.AddedSample(r), "the status line's first names");
            Assert.Eq("", SceneryFixDeps.AddedSample(new SceneryScanResult()), "nothing added: no sample");
            // a long list is capped with a pointer to the report
            var big = new SceneryScanResult { Ran = true, JsonPath = @"X\scan.json" };
            for (int i = 0; i < 1005; i++) big.Added.Add("m" + i);
            string bt = string.Join("\n", SceneryFixDeps.Summarize(big));
            Assert.Contains(bt, "... and 5 more (all of them are in X\\scan.json)", "the cap points at the report");
            // the report parser reads sky_excluded
            var p = new SceneryScanResult();
            Assert.True(SceneryFixDeps.ParseReport("{\"models\":3,\"sky_excluded\":[\"s\\\\a.render_model\",\"S\\\\A.RENDER_MODEL\",\"t\\\\b.render_model\"]}", p) == null, "parsed");
            Assert.Seq(new[] { @"s\a.render_model", @"t\b.render_model" }, p.SkyExcluded, "sky_excluded (case-insensitive dedup)");
        }

        private static void SummarizeText()
        {
            var r = new SceneryScanResult { Ran = true, Models = 1234, Db = @"X\SceneryFixDependencies.db", Seconds = 1.5 };
            r.Added.AddRange(new[] { "a", "b" });
            r.Conflicts.Add(new SceneryConflict { Model = "c", Sections = 4, Conflicts = 2 });
            r.Updated.Add("u"); r.Seeding = true; r.OtherAdded = 1337;
            string t = string.Join("\n", SceneryFixDeps.Summarize(r));
            Assert.Contains(t, "Updated 1 model(s)", "updated"); Assert.Contains(t, "built from every render_model", "seeding noted"); Assert.Contains(t, "1,337 more model(s)", "other added");
            Assert.Contains(t, "Scanned 1,234 render_model(s)", "scanned");
            Assert.Contains(t, "Added 2 model(s) the database did not know", "added count"); Assert.Contains(t, "    a", "added list");
            Assert.Contains(t, "Conflicts: 1 model(s) with sections the database cannot tell apart", "conflicts"); Assert.Contains(t, "wrong scale", "the explanation");
            Assert.Contains(t, @"Database: X\SceneryFixDependencies.db", "where the file lives");
            var f = string.Join("\n", SceneryFixDeps.Summarize(new SceneryScanResult { Error = "boom" }));
            Assert.Contains(f, "did not complete: boom", "failure text");
        }
    }
}
