using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.Pipeline;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// 0.6.8 xc8 -- AO only (--mode ao): the plan, the refusals (no lightmap yet, AO already in it, not for a .map, --force only here), the install record
    /// (written after a verified install, matched by md5, lifted by any change of the files), and an end-to-end run through the fake engine
    /// (capture only -> trace -> precheck / stage -> the shell's install -> record), twice (refused) and with --force. The real arithmetic is tested by
    /// tests\rc068\aoonly\ (Python) on a real cyclotron capture.
    /// </summary>
    internal static class AoOnlyTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("ao only: --mode ao plan = precheck, backup, capture-only gpu_bake, ao trace, ao_existing stage, copy, install, verify; no lighting steps (no tag writer, no apply / stage of gpu_ao)", Plan);
            yield return T("ao only: plan variants: --no-install, --reuse-capture (no gpu_bake), low quality traces at low, strength / exponent make the tag, --force reaches the precheck and the stage", PlanVariants);
            yield return T("ao only: refusals before anything: --force outside --mode ao, a .map, no lightmap yet, AO already in the lightmap (install record), strength out of range; --force and a changed lightmap lift the record", Refusals);
            yield return T("ao only: the install record: append / load / find by the md5 of the three installed files; any change of a file stops the match; the file shape ao_existing.py reads", Records);
            yield return T("ao only e2e: capture only + trace + ao_existing + the shell's install + record; the second run is refused (exit 2, nothing changed, no engine call); --force adds it again; a restored (changed) set is allowed", E2e);
        }

        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
        private const string CycPrefix = "cyclotron_cyclotron_lightmap";

        private static JObj Plan(TestRoot r, params string[] extra)
        {
            var res = C.DryJson(r, extra);
            if (res.Exit != 0) throw new TestFailure("dry run failed: " + res);
            return res.Json();
        }

        private static List<JObj> Steps(JObj p) { return p.Arr("steps").OfType<JObj>().ToList(); }
        private static List<string> Ids(JObj p) { return Steps(p).Select(s => s.Str("id")).ToList(); }
        private static JObj Step(JObj p, string id) { var s = Steps(p).FirstOrDefault(x => x.Str("id") == id); if (s == null) throw new TestFailure("no step " + id + " in " + string.Join(",", Ids(p))); return s; }
        private static List<string> Args(JObj s) { return s.Arr("args").Select(x => (string)x).ToList(); }
        private static string Md5(string p) { return SafeFs.Md5File(p); }
        private static string Next(List<string> a, string flag) { int i = a.IndexOf(flag); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }

        private static void Plan()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            Assert.Seq(new[] { "ao-precheck", "backup", "gpu-bake", "ao-trace", "ao-existing", "copy-final-stage", "install", "verify" }, Ids(p), "AO only steps");
            Assert.Eq("ao", p.Obj("options").Str("mode"), "options.mode");
            Assert.Eq(true, p.Obj("options").Bool("ao"), "AO is implied");
            var ui = p.Arr("uiSteps").Cast<string>().ToList();
            Assert.True(!ui.Contains("Derive") && !ui.Contains("GpuBake"), "no lighting UI steps: " + string.Join(",", ui));
            Assert.True(ui.Contains("Capture") && ui.Contains("AO") && ui.Contains("Install") && ui.Contains("Verify"), "capture, AO, install, verify: " + string.Join(",", ui));
            // the capture: gpu_bake stops after phase 1; nothing that belongs to a lighting bake
            var g = Args(Step(p, "gpu-bake"));
            Assert.True(g.Contains("-CaptureOnly"), "-CaptureOnly");
            Assert.True(!g.Contains("-WriteBack") && !g.Contains("-SkipCapture") && !g.Contains("-ExactVertexLighting"), "no -WriteBack / -SkipCapture / -ExactVertexLighting: " + string.Join(" ", g));
            Assert.Eq("direct", Next(g, "-WriteBackMode"), "the capture is stamped for the direct writer (capture_id.json gets the BSP md5)");
            Assert.Eq(true, Step(p, "gpu-bake").Obj("capture").Bool("fresh"), "a new capture is recorded");
            // the trace is the full bake's own; no apply / stage of gpu_ao, no stage-ready
            var tr = Args(Step(p, "ao-trace"));
            Assert.Eq("trace", tr[1], "gpu_ao trace");
            Assert.Eq("super", Next(tr, "--quality"), "trace quality");
            Assert.True(tr.Contains("--plucker-fix") && tr.Contains("--scenery-fix"), "fixes");
            Assert.True(!Ids(p).Contains("ao-apply") && !Ids(p).Contains("ao-stage") && !Ids(p).Contains("stage-ready") && !Ids(p).Contains("stage-or-tool-fallback"), "no lighting stage steps");
            // the AO step: the installed set + the trace -> OUT\aoonly\<tag>\stage
            var ex = Args(Step(p, "ao-existing"));
            Assert.Eq("stage", ex[1], "ao_existing stage");
            Assert.True(ex[0].EndsWith(@"_research\ao\ao_existing.py", StringComparison.OrdinalIgnoreCase), "script: " + ex[0]);
            Assert.Eq(r.Farm(200), Next(ex, "--farm"), "--farm");
            Assert.Eq("1.0", Next(ex, "--strength"), "--strength"); Assert.Eq("0.5", Next(ex, "--exponent"), "--exponent");
            Assert.Eq("s10e05", Next(ex, "--tag"), "--tag");
            Assert.Eq(r.Root, Next(ex, "--h2-root"), "--h2-root");
            Assert.Eq(Path.Combine(r.Root, "tool_fast.exe"), Next(ex, "--tool"), "--tool");
            Assert.Eq(Path.Combine(r.Work, @"standalone\ao_installed.json"), Next(ex, "--records"), "--records: the install records");
            Assert.True(!ex.Contains("--force"), "no --force");
            var pc = Args(Step(p, "ao-precheck"));
            Assert.Eq("precheck", pc[1], "ao_existing precheck");
            Assert.Eq(Next(ex, "--records"), Next(pc, "--records"), "the precheck reads the same records");
            // before the backup: a refusal costs no backup
            Assert.Eq(1, Steps(p).First(s => s.Str("id") == "ao-precheck").Long("n").Value, "the precheck is step 1");
            // the shell copies the stage to KEEP and installs that copy
            string run = p.Str("runDir");
            Assert.Eq(Path.Combine(r.Farm(200), "aoonly", "s10e05", "stage"), Step(p, "copy-final-stage").Obj("params").Str("from"), "copied from OUT\\aoonly\\s10e05\\stage");
            Assert.Eq(Path.Combine(run, "final_stage"), p.Str("installStage"), "the install source");
            Assert.Eq(Path.Combine(run, "tagbak"), p.Str("installBackupDir"), "the backup is RUN\\tagbak");
            Assert.True(p.Arr("notes").Cast<string>().Any(n => n.StartsWith("AO only: no lighting is baked", StringComparison.Ordinal)), "the plan says no lighting is baked");
        }

        private static void PlanVariants()
        {
            var r = C.NewRoot();
            var ni = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--no-install");
            Assert.Seq(new[] { "ao-precheck", "gpu-bake", "ao-trace", "ao-existing", "copy-final-stage" }, Ids(ni), "--no-install: no backup / install / verify");
            Assert.Contains(ni.Arr("notes").Cast<string>().First(n => n.StartsWith("stage only", StringComparison.Ordinal)), "final stage", "stage only note");
            var lo = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--quality", "low", "--ao-strength", "0.75", "--ao-exponent", "1");
            Assert.Eq("low", Next(Args(Step(lo, "ao-trace")), "--quality"), "low traces at low");
            Assert.Eq("s075e10", Next(Args(Step(lo, "ao-existing")), "--tag"), "tag from strength / exponent");
            Assert.Eq("0.75", Next(Args(Step(lo, "ao-existing")), "--strength"), "strength");
            Assert.True(Args(Step(lo, "gpu-bake")).Contains("low"), "-Quality low on the capture");
            // reuse: a dev unrecorded capture in worker 200 -> no gpu_bake step at all
            Directory.CreateDirectory(r.Farm(200));
            File.WriteAllText(Path.Combine(r.Farm(200), "capture_id.json"), "{\"soup_crc32\": \"12345678\"}");
            var ru = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--reuse-capture", "--reuse-unrecorded", "--worker", "200");
            Assert.Seq(new[] { "ao-precheck", "backup", "ao-trace", "ao-existing", "copy-final-stage", "install", "verify" }, Ids(ru), "reuse: no capture step");
            // --force
            var fo = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--force");
            Assert.Eq(true, fo.Obj("options").Bool("aoForce"), "options.aoForce");
            Assert.True(Args(Step(fo, "ao-existing")).Contains("--force") && Args(Step(fo, "ao-precheck")).Contains("--force"), "--force reaches both steps");
            // --responsive: the trace gets the environment variable, the capture the switch
            var rs = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--responsive");
            Assert.Eq("1", Step(rs, "ao-trace").Obj("envExtra").Str("GPULM_RESPONSIVE"), "ao-trace: GPULM_RESPONSIVE=1");
            Assert.True(Args(Step(rs, "gpu-bake")).Contains("-Responsive"), "capture: -Responsive");
        }

        private static void Refusals()
        {
            var r = C.NewRoot();
            var x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--force");
            Assert.Eq(2, x.Exit, "--force outside --mode ao: " + x);
            Assert.Contains(x.Err + x.Out, "only for --mode ao", "reason");
            var mo = H2GpuLightmapper.App.BakeOptions.Defaults();
            mo.Root = r.Root; mo.MapPath = @"C:\nope\x.map"; mo.Mode = H2GpuLightmapper.App.BakeMode.AoOnly; mo.Ao = true;
            Assert.True(mo.Validate(true).Any(e => e.Contains("not available for a .map bake")), "AO only with a .map is refused by the options");
            x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--ao-strength", "9");
            Assert.Eq(2, x.Exit, "strength out of range: " + x);
            // no lightmap yet
            var nl = C.NewRoot(withLightmaps: false);
            x = C.DryJson(nl, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            Assert.Eq(2, x.Exit, "no lightmap: " + x);
            string why = x.Json().Str("refused");
            Assert.Contains(why, "has none yet", "plain words");
            Assert.Contains(why, "Bake the lightmap first", "what to do");
            Assert.Contains(why, "Nothing was changed", "and nothing was written");
            Assert.True(!Directory.Exists(Path.Combine(nl.Work, "standalone", "runs")) || !Directory.GetDirectories(Path.Combine(nl.Work, "standalone", "runs")).Any(), "no run folder");
            // an unrecorded set is allowed (and the plan says so by the dry run's log line)
            Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            // AO already in it: the install record names the md5s of the installed set
            var set = new TagSetLite(r);
            AppendRecord(r, set.Md5s(), "ao-only", 1.0, 0.5);
            x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            Assert.Eq(2, x.Exit, "AO already in it: " + x);
            why = x.Json().Str("refused");
            Assert.Contains(why, "already been added to the lightmap that is installed now", "plain words");
            Assert.Contains(why, "AO only, strength 1.0, exponent 0.5", "which AO");
            Assert.Contains(why, "darken the same places twice", "why");
            Assert.Contains(why, "--force", "how to override");
            Assert.Contains(why, "Restore previous lightmap", "how to start again");
            Assert.Contains(why, "Nothing was changed", "nothing written");
            // --force lifts it
            var fp = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--force");
            Assert.Eq(true, fp.Obj("options").Bool("aoForce"), "forced plan");
            // any change of the lightmap files lifts the record (a restored backup / a new bake)
            File.WriteAllText(r.CycLtmp, "LIVE ltmp cyclotron v2 (a restored or re-baked set)");
            Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            // a full bake with AO is recorded too: the same refusal for the set it installed
            File.WriteAllText(r.CycLtmp, "LIVE ltmp cyclotron v3 (a full bake with AO)");
            AppendRecord(r, new TagSetLite(r).Md5s(), "bake", 0.75, 1.0);
            x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            Assert.Eq(2, x.Exit, "a full bake's AO is in it: " + x);
            Assert.Contains(x.Json().Str("refused"), "AO with a full bake, strength 0.75, exponent 1.0", "names the full bake");
        }

        // ------------------------------------------------------------------ the install records

        private sealed class TagSetLite
        {
            private readonly TestRoot _r;
            public TagSetLite(TestRoot r) { _r = r; }
            public SortedDictionary<string, string> Md5s()
            {
                var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in new[] { CycPrefix + ".scenario_structure_lightmap", CycPrefix + "_truecolor_bitmaps.bitmap", CycPrefix + "_bitmaps.bitmap" }) d[n] = Md5(Path.Combine(_r.CycDir, n));
                return d;
            }
        }

        private static void AppendRecord(TestRoot r, SortedDictionary<string, string> files, string mode, double s, double e)
        {
            var rec = new AoRecord { Scenario = Cyc, Bsp = "cyclotron", Mode = mode, RunTag = "t", Utc = DateTime.UtcNow.ToString("o"), Strength = s, Exponent = e };
            foreach (var kv in files) rec.Files[kv.Key] = kv.Value;
            AoRecords.Append(r.Work, rec);
        }

        private static void Records()
        {
            var r = C.NewRoot();
            var set = new TagSetLite(r);
            Assert.Eq(0, AoRecords.Load(r.Work).Count, "no records yet");
            var md5 = set.Md5s();
            Assert.True(AoRecords.Find(AoRecords.Load(r.Work), md5) == null, "nothing matches yet");
            AppendRecord(r, md5, "ao-only", 1.0, 0.5);
            var all = AoRecords.Load(r.Work);
            Assert.Eq(1, all.Count, "one record");
            var f = AoRecords.Find(all, md5);
            Assert.True(f != null && f.Mode == "ao-only" && f.Strength == 1.0 && f.Exponent == 0.5, "found by the md5s of the three files");
            Assert.Contains(f.Describe(), "AO only, strength 1.0, exponent 0.5", "Describe");
            // the file ao_existing.py reads: {"sets": [{"files": {name: md5}}]}
            var j = Json.ReadObjFile(AoRecords.PathFor(r.Work));
            var fj = j.Arr("sets").OfType<JObj>().Single().Obj("files");
            Assert.Eq(3, fj.Count, "three files in the record");
            Assert.Eq(md5[CycPrefix + ".scenario_structure_lightmap"], fj.Str(CycPrefix + ".scenario_structure_lightmap"), "md5 value");
            // one byte of one file changes -> no match (the md5 is the key)
            File.AppendAllText(Path.Combine(r.CycDir, CycPrefix + "_bitmaps.bitmap"), "x");
            Assert.True(AoRecords.Find(AoRecords.Load(r.Work), set.Md5s()) == null, "a changed file stops the match");
            // a missing file -> null md5s -> no match, no crash
            File.Delete(Path.Combine(r.CycDir, CycPrefix + "_bitmaps.bitmap"));
            var ts = Core.Safety.TagSet.For(r.Root, Cyc, "cyclotron");
            Assert.True(AoRecords.SetMd5s(ts) == null, "an incomplete set has no md5s");
            Assert.True(AoRecords.Find(AoRecords.Load(r.Work), null) == null, "null md5s never match");
            // the cap
            for (int i = 0; i < AoRecords.Max + 5; i++) AppendRecord(r, new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "a", i.ToString() } }, "bake", 1, 1);
            Assert.Eq(AoRecords.Max, AoRecords.Load(r.Work).Count, "capped");
        }

        // ------------------------------------------------------------------ end to end through the fake engine

        private static string RunDirOf(CliResult r)
        {
            var l = r.Lines.LastOrDefault(x => x.StartsWith("run folder: ", StringComparison.Ordinal));
            if (l == null) throw new TestFailure("no run folder line: " + r);
            return l.Substring(12).Trim();
        }

        private static void E2e()
        {
            var r = C.NewRoot();
            var set = new TagSetLite(r);
            var orig = set.Md5s();
            var res = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao");
            if (res.Exit != 0) throw new TestFailure("AO only failed: " + res);
            Assert.Contains(res.Out, "RESULT DONE", "AO only");
            string run = RunDirOf(res);
            // the engine calls: a capture-only gpu_bake, gpu_ao trace (no apply / stage), ao_existing precheck + stage
            var gb = r.Calls("gpu_bake");
            Assert.Eq(1, gb.Count, "one gpu_bake");
            Assert.True(gb[0].Arr("bound").Cast<string>().Contains("CaptureOnly=True"), "-CaptureOnly bound: " + string.Join(",", gb[0].Arr("bound").Cast<string>()));
            Assert.True(!gb[0].Arr("bound").Cast<string>().Any(b => b.StartsWith("WriteBack=", StringComparison.Ordinal) || b.StartsWith("SkipCapture=", StringComparison.Ordinal)), "no -WriteBack / -SkipCapture");
            Assert.Seq(new[] { "trace" }, r.Calls("gpu_ao").Select(c => c.Arr("argv").Cast<string>().ElementAt(1)).ToList(), "gpu_ao: the trace only");
            Assert.Seq(new[] { "precheck", "stage" }, r.Calls("ao_existing").Select(c => c.Arr("argv").Cast<string>().ElementAt(1)).ToList(), "ao_existing: precheck, then stage");
            // the shell installed the stage: every file = the old file + the marker
            foreach (var kv in orig)
            {
                string cur = File.ReadAllText(Path.Combine(r.CycDir, kv.Key));
                Assert.True(cur.EndsWith("|AO ONLY", StringComparison.Ordinal), "installed: " + kv.Key);
            }
            Assert.True(Directory.GetFiles(Path.Combine(run, "tagbak")).Any(), "the backup is in RUN\\tagbak");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(run, "tagbak", kv.Key)), "the backup is the set before: " + kv.Key);
            Assert.True(File.Exists(Path.Combine(r.Work, @"standalone\ao_installed.json")), "the install record was written");
            var recs = AoRecords.Load(r.Work);
            Assert.Eq(1, recs.Count, "one record");
            Assert.Eq("ao-only", recs[0].Mode, "mode");
            var cur5 = set.Md5s();
            foreach (var kv in cur5) Assert.Eq(kv.Value, recs[0].Files[kv.Key], "the record has the md5 of the installed " + kv.Key);
            Assert.Contains(File.ReadAllText(Path.Combine(run, "bake.log")), "AO install record written", "bake.log says so");
            Assert.True(Pipeline.History.Load(r.Work).Any(h => h.Mode == "ao" && h.Ao && h.State == "DONE"), "history: mode ao, AO on, DONE");
            Func<int> engineCalls = () => r.Calls().Count(c => c.Str("tool") == "gpu_bake" || c.Str("tool") == "gpu_ao" || c.Str("tool") == "ao_existing");
            int calls = engineCalls();
            // the second AO only on the same set: refused up front (exit 2), nothing changed, no engine call
            var again = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--reuse-capture");
            Assert.Eq(2, again.Exit, "the second AO only is refused: " + again);
            Assert.Contains(again.Out, "already been added to the lightmap that is installed now", "plain words");
            Assert.Contains(again.Out, "=== answer: no", "the CLI asked and answered no (no --force)");
            Assert.Eq(calls, engineCalls(), "no engine call (gpu_bake / gpu_ao / ao_existing)");
            foreach (var kv in cur5) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "the lightmap is untouched: " + kv.Key);
            // --force adds it again (the capture of the first run is reused)
            var forced = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--reuse-capture", "--force");
            if (forced.Exit != 0) throw new TestFailure("forced AO only failed: " + forced);
            Assert.True(File.ReadAllText(r.CycLtmp).EndsWith("|AO ONLY|AO ONLY", StringComparison.Ordinal), "AO added a second time");
            Assert.Eq(2, AoRecords.Load(r.Work).Count, "two records");
            Assert.Eq(1, r.Calls("gpu_bake").Count, "the second run reused the capture (no new gpu_bake)");
            // a restored / re-baked set (other md5s) is allowed again
            File.WriteAllText(r.CycLtmp, "LIVE ltmp cyclotron v9");
            var third = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--reuse-capture");
            if (third.Exit != 0) throw new TestFailure("AO only on a changed set failed: " + third);
        }
    }
}
