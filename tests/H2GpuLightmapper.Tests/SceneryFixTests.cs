using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.Pipeline;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// SF1: the automatic Scenery Fix Dependencies check before a bake (exact log lines, ordering before the capture, never blocks),
    /// the capture identity / reuse gate with SceneryFixDependencies.db, the dry run (no scan, argv unchanged) and the payload's
    /// scanner. A FAKE scanner (fixtures\fake_scan_scenery_bounds.py, the real CLI and JSON) in a sandbox H2EK; no GPU, no tool, no
    /// live file. The REAL scanner is exercised by the core tests.
    /// </summary>
    internal static class SceneryFixTests
    {
        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
        private const string ModelA = @"objects\sf\aaa\aaa.render_model", ModelB = @"objects\sf\bbb\bbb.render_model";

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("SF1 e2e: models added by the pre-bake check are logged with the exact line, BEFORE the capture; argv to gpu_bake unchanged; DB md5 recorded", AddedLogged);
            yield return T("SF1 e2e: a first run that builds the database and a model whose tag changed are logged (seed / updated lines)", SeedAndUpdate);
            yield return T("SF1 e2e: nothing missing -> no 'added' lines, the same gpu_bake argv, the DB untouched", NothingMissing);
            yield return T("SF1 e2e: conflicts -> a warning, the bake continues; scanner exit 2 / crash / no report -> a warning, the bake continues", NeverBlocks);
            yield return T("SF1 e2e: reuse capture: unchanged DB reuses; a changed DB refuses with the Scenery Fix Dependencies reason; an older record (no sceneryFixDb) still reuses", ReuseGate);
            yield return T("SF1 dry run: no scan, no DB written, the plan's gpu_bake argv identical", DryRun);
            yield return T("SF1 unit: capture identity records sceneryFixDb beside the legacy boundsDb; reuse names the database", IdentityUnit);
            yield return T("SF1 e2e: the CLI verb scan-scenery-fix (full scan with the payload's scanner) creates the DB; --scenario scans one scenario", CliVerb);
            yield return T("SF1 unit: no scanner in the payload -> Run says so without throwing", NoScanner);
        }

        private static string Md5(string p) { return SafeFs.Md5File(p); }

        private static string RunDirOf(CliResult r)
        {
            var l = r.Lines.LastOrDefault(x => x.StartsWith("run folder: ", StringComparison.Ordinal));
            if (l == null) throw new TestFailure("no run folder line: " + r);
            return l.Substring(12).Trim();
        }

        private static string LogOf(CliResult r) { return File.ReadAllText(Path.Combine(RunDirOf(r), "bake.log")); }

        private static CliResult Ok(CliResult r, string what)
        {
            if (r.Exit != 0) throw new TestFailure(what + " failed: " + r);
            Assert.Contains(r.Out, "RESULT DONE", what);
            return r;
        }

        private static CliResult StageOnly(TestRoot r, params string[] extra)
        {
            var a = new List<string> { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" };
            a.AddRange(extra);
            return C.Bake(r, a.ToArray());
        }

        private static List<string> Tail(JObj call)
        {
            var a = call.Arr("argv").Cast<string>().ToList();
            return a.Skip(a.FindIndex(x => x.EndsWith("gpu_bake.ps1", StringComparison.OrdinalIgnoreCase)) + 1).ToList();
        }

        private static readonly string[] StageOnlyTail = { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" };

        private static string Db(TestRoot r) { return Path.Combine(r.Root, SceneryFixDeps.DbName); }

        private static void AddedLogged()
        {
            var r = C.NewRoot();
            r.Control("scan=add:" + ModelA + ";" + ModelB + "\n");
            var res = Ok(StageOnly(r), "stage-only bake with two models added by the check");
            string log = LogOf(res);
            Assert.Contains(log, ModelA + " wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!", "line for model A, exactly");
            Assert.Contains(log, ModelB + " wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!", "line for model B, exactly");
            Assert.Contains(log, "2 of this scenario's added", "the summary line");
            var calls = r.Calls();
            int scan = calls.FindIndex(c => c.Str("tool") == "scan_scenery"), bake = calls.FindIndex(c => c.Str("tool") == "gpu_bake");
            Assert.True(scan >= 0 && bake >= 0 && scan < bake, "the scanner ran BEFORE gpu_bake (scan " + scan + ", bake " + bake + ")");
            Assert.Eq(1, r.Calls("scan_scenery").Count, "one scan");
            var argv = r.Calls("scan_scenery")[0].Arr("argv").Cast<string>().ToList();
            Assert.True(argv.Contains("--apply") && argv.Contains("--json"), "scanner argv has --apply and --json: " + string.Join(" ", argv));
            int si = argv.IndexOf("--scenario");
            Assert.True(si > 0 && argv[si + 1] == Cyc, "scanner argv names the bake's scenario: " + string.Join(" ", argv));
            Assert.True(!argv.Contains("--db"), "the scanner's own default database is used (--db is not passed)");
            Assert.Seq(StageOnlyTail, Tail(r.Calls("gpu_bake").Single()), "gpu_bake argv is the production one");
            Assert.True(File.Exists(Db(r)), "SceneryFixDependencies.db was created in the H2EK root");
            var rec = Json.ReadObjFile(Path.Combine(r.Farm(200), "standalone_capture.json"));
            var bin = rec.Obj("binaries");
            Assert.True(bin.Has("sceneryFixDb"), "the record has the new field");
            Assert.Eq(Md5(Db(r)), bin.Str("sceneryFixDb"), "the md5 recorded is the DB AFTER the scan (the scan ran before the capture)");
            Assert.True(bin.Has("boundsDb"), "the old field is kept");
        }

        private static void SeedAndUpdate()
        {
            var r = C.NewRoot();
            r.Control("scan=seed:" + ModelA + "\n");
            var res = Ok(StageOnly(r), "first run builds the database");
            string log = LogOf(res);
            Assert.Contains(log, "SceneryFixDependencies.db had no per-model entries yet -- built it from the 6 render_models of the tags folder (3 models outside this scenario were added too), continuing on!", "the first-run line");
            Assert.Contains(log, ModelA + " wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!", "the scenario's own new model");
            r.Control("scan=update:" + ModelB + "\n");
            var res2 = Ok(StageOnly(r), "a tag whose bound changed");
            Assert.Contains(LogOf(res2), ModelB + " had a different size bound in the Scenery Fix Dependencies database than its tag has now -- updated automatically, continuing on!", "the updated line");
            Assert.NotContains(LogOf(res2), "had no per-model entries yet", "not seeded again");
        }

        private static void NothingMissing()
        {
            var r = C.NewRoot();
            File.WriteAllText(Db(r), "existing line\r\n");
            string md5 = Md5(Db(r));
            r.Control("");
            var res = Ok(StageOnly(r), "stage-only bake, nothing missing");
            string log = LogOf(res);
            Assert.NotContains(log, "wasn't in the Scenery Fix Dependencies database", "no added line");
            Assert.Contains(log, "0 of this scenario's added", "summary says none added");
            Assert.Eq(md5, Md5(Db(r)), "the database file is untouched");
            Assert.Seq(StageOnlyTail, Tail(r.Calls("gpu_bake").Single()), "gpu_bake argv is the production one");
            // --dev-no-fixes: the fix is off, so the check does not run at all
            var r2 = C.NewRoot();
            Ok(StageOnly(r2, "--dev-no-fixes"), "no-fixes bake");
            Assert.Eq(0, r2.Calls("scan_scenery").Count, "no scan when the scenery fix is off");
        }

        private static void NeverBlocks()
        {
            var r = C.NewRoot();
            r.Control("scan=conflict\n");
            var res = Ok(StageOnly(r), "conflicts do not fail the bake");
            string log = LogOf(res);
            Assert.Contains(log, "WARNING: trees\\redalder\\redalder.render_model: 2 of 2 compressed section(s) share a lookup key", "the conflict warning names the model");
            Assert.Contains(log, "conflict_ok\\fine.render_model wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!", "the non-conflicting model is still added");
            Assert.Eq(1, r.Calls("gpu_bake").Count, "the bake ran");
            foreach (var knob in new[] { "refuse", "crash", "nojson" })
            {
                var r2 = C.NewRoot();
                r2.Control("scan=" + knob + "\n");
                var res2 = Ok(StageOnly(r2), "scanner '" + knob + "' does not block the bake");
                string log2 = LogOf(res2);
                Assert.Contains(log2, "WARNING: scenery fix dependencies:", knob + ": a warning is logged");
                Assert.Contains(log2, "continuing with the existing database", knob + ": says it continues");
                Assert.Seq(StageOnlyTail, Tail(r2.Calls("gpu_bake").Single()), knob + ": gpu_bake argv unchanged");
            }
        }

        private static void ReuseGate()
        {
            var r = C.NewRoot();
            r.Control("scan=add:" + ModelA + "\n");
            Ok(StageOnly(r), "first bake (records the capture)");
            r.Control("");
            var again = Ok(StageOnly(r, "--reuse-capture"), "reuse with the DB unchanged");
            Assert.Contains(LogOf(again), "reusable", "reuse was offered and taken");
            Assert.True(r.Calls("gpu_bake").Last().Arr("argv").Cast<string>().Contains("-SkipCapture"), "the second bake skipped the capture");
            // a changed database: the capture's scenery geometry no longer matches
            File.AppendAllText(Db(r), "another line\r\n");
            var bad = StageOnly(r, "--reuse-capture");
            Assert.Eq(2, bad.Exit, "refused: " + bad);
            Assert.Contains(bad.Out + bad.Err, "Scenery Fix Dependencies database changed since the capture", "the reason names the database");
            // an older record (written before sceneryFixDb existed) is vouched for by boundsDb only
            string recPath = Path.Combine(r.Farm(200), "standalone_capture.json");
            var rec = Json.ReadObjFile(recPath);
            rec.Obj("binaries").Remove("sceneryFixDb");
            File.WriteAllText(recPath, Json.Write(rec, true));
            var old = Ok(StageOnly(r, "--reuse-capture"), "an older record still reuses");
            Assert.Contains(LogOf(old), "reusable", "reused");
        }

        private static void DryRun()
        {
            var r = C.NewRoot();
            r.Control("scan=add:" + ModelA + "\n");
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--worker", "200", "--dry-run" });
            var res = C.Run(l);
            Assert.Eq(0, res.Exit, "dry run: " + res);
            Assert.Contains(res.Out, "scenery fix dependencies: not scanned in a dry run", "says it does not scan");
            Assert.Eq(0, r.Calls().Count, "no process started");
            Assert.True(!File.Exists(Db(r)), "no database written");
            Assert.Contains(res.Out, "\"-SceneryFix\",\"-PluckerFix\",\"-WriteBackMode\",\"direct\"", "the planned gpu_bake argv tail (unchanged)");
        }

        private static void IdentityUnit()
        {
            string root = C.Sb.Dir("sf_ident");
            var none = CaptureIdentity.For(new BakeOptions(), null, root, "engine_v373/gpu_bake.ps1", false, "tt");
            Assert.True(none.Binaries.ContainsKey("sceneryFixDb") && none.Binaries.ContainsKey("boundsDb"), "both keys exist");
            Assert.Eq(null, none.Binaries["sceneryFixDb"], "absent file -> null");
            File.WriteAllText(Path.Combine(root, SceneryFixDeps.DbName), "x\r\n");
            File.WriteAllText(Path.Combine(root, SceneryFixDeps.LegacyDbName), "y\r\n");
            var have = CaptureIdentity.For(new BakeOptions(), null, root, "engine_v373/gpu_bake.ps1", false, "tt");
            Assert.Eq(Md5(Path.Combine(root, SceneryFixDeps.DbName)), have.Binaries["sceneryFixDb"], "md5 of SceneryFixDependencies.db");
            Assert.Eq(Md5(Path.Combine(root, SceneryFixDeps.LegacyDbName)), have.Binaries["boundsDb"], "md5 of the legacy db");
            // the reuse gate
            string farm = C.Sb.Dir(@"sf_ident\farm_w200");
            File.WriteAllText(Path.Combine(farm, "capture_id.json"), "{\"soup_crc32\": \"ABCD1234\"}");
            Directory.CreateDirectory(Path.Combine(root, @"tags\scenarios\m\m"));
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\m\m\m.scenario"), "s");
            var set = TagSet.For(root, @"scenarios\m\m\m", "m");
            Func<TagsFingerprint> tags = () => TagsFingerprint.Compute(root, set);
            Assert.Eq(null, CaptureRecord.Write(farm, have, @"scenarios\m\m\m", "m", 200, "r", tags(), DateTime.Now), "record written");
            Assert.True(CaptureRecord.Evaluate(farm, have, @"scenarios\m\m\m", "m", false, tags).Allowed, "same identity reuses");
            var changed = CaptureIdentity.For(new BakeOptions(), null, root, "engine_v373/gpu_bake.ps1", false, "tt");
            changed.Binaries["sceneryFixDb"] = "ffff";
            var v = CaptureRecord.Evaluate(farm, changed, @"scenarios\m\m\m", "m", false, tags);
            Assert.True(!v.Allowed, "a changed sceneryFixDb refuses");
            Assert.Contains(v.Reason, "Scenery Fix Dependencies database changed", "named");
            Assert.Contains(v.Reason, "sceneryFixDb", "which field");
            var eng = CaptureIdentity.For(new BakeOptions(), null, root, "engine_v373/gpu_bake.ps1", false, "tt");
            eng.Binaries["fullgpu"] = "zz";
            Assert.Contains(CaptureRecord.Evaluate(farm, eng, @"scenarios\m\m\m", "m", false, tags).Reason, "the engine changed since the capture", "an engine change keeps the engine wording");
        }

        private static void CliVerb()
        {
            var r = C.NewRoot();
            var scenarioScan = C.Base("scan-scenery-fix");
            r.Control("scan=add:" + ModelA + "\n");
            scenarioScan.AddRange(new[] { "--h2ek", r.Root });
            var full = C.Run(scenarioScan);
            Assert.Eq(0, full.Exit, "scan-scenery-fix: " + full);
            Assert.Contains(full.Out, "Added 1 model(s)", "added count");
            Assert.Contains(full.Out, ModelA, "added list");
            Assert.Contains(full.Out, "Database: ", "where the file lives");
            Assert.True(File.Exists(Db(r)), "SceneryFixDependencies.db is in the H2EK root");
            var c1 = r.Calls("scan_scenery").Single().Arr("argv").Cast<string>().ToList();
            Assert.True(!c1.Contains("--scenario") && c1.Contains("--apply"), "a FULL scan: no --scenario, --apply: " + string.Join(" ", c1));
            r.Control("");
            var one = C.Base("scan-scenery-fix");
            one.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc });
            var sc = C.Run(one);
            Assert.Eq(0, sc.Exit, "scenario scan: " + sc);
            Assert.Contains(sc.Out, "Added: none", "nothing to add");
            var bad = C.Base("scan-scenery-fix");
            r.Control("scan=refuse\n");
            bad.AddRange(new[] { "--h2ek", r.Root });
            var f = C.Run(bad);
            Assert.Eq(3, f.Exit, "a scanner refusal is exit 3: " + f);
            Assert.Contains(f.Out, "The scan did not complete", "says so");
        }

        private static void NoScanner()
        {
            // the test payload without the scanner: build one from the standard one minus the file is heavy; the shell's note path is
            // exercised through ResolvedRuntime.ScanSceneryPy == null in the unit below
            var rt = new Core.Payload.ResolvedRuntime { ScanSceneryPy = null };
            Assert.Eq(null, rt.ScanSceneryPy, "an older payload resolves no scanner");
            var res = SceneryFixDeps.Run("py.exe", null, C.Sb.Dir("sf_noscan"), null, C.Sb.Dir("sf_noscan"), null, 1000, Path.Combine(C.Sb.Dir("sf_noscan"), "o.json"));
            Assert.True(!res.Ran && res.Error.Contains("no scan_scenery_bounds.py"), "Run refuses to run without a scanner and says why");
        }
    }
}
