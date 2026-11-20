using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// 1.5 -- the EXPERIMENTAL seam stitch: the Advanced option "Stitch lightmap seams (Experimental)" (--stitch-seams: gpu_bake -StitchSeams, gpu_ao stage
    /// --stitch-seams) and the mode "Seam stitch only" (--mode stitch: seam_stitch.py precheck / stage on the installed lightmap, no capture, no GPU).
    /// The stitch itself is tested by lmw_stitch.py selftest and tests\rel143 (Python) on a real lightmap.
    /// </summary>
    internal static class SeamStitchTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("seam stitch: unticked plans carry no StitchSeams at all; ticked = + -StitchSeams on every gpu_bake argv (incl. the fallback argv), --stitch-seams on gpu_ao stage, the note; no other step changes", SwitchPlan);
            yield return T("seam stitch: the switch in AO only / Lightprobes is a warning (no lightmap is written) and reaches no step", SwitchNoEffect);
            yield return T("seam stitch only: --mode stitch plan = precheck, backup, seam_stitch stage, copy, install, verify; no capture, no GPU step, no map query; --no-install = no backup / install", ModePlan);
            yield return T("seam stitch only: refusals before anything (exit 2): no lightmap yet, with --ao, with --reuse-capture, a .map", ModeRefusals);
        }

        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
        private const string Tri = @"scenarios\multi\triplicate\triplicate";

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
        private static string Next(List<string> a, string flag) { int i = a.IndexOf(flag); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
        private static string NoStamp(string x) { return System.Text.RegularExpressions.Regex.Replace(x, @"\d{8}_\d{6}_", "STAMP_"); }

        private static void SwitchPlan()
        {
            var r = C.NewRoot();
            foreach (var extra in new[] { new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--ao" },
                                          new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao" } })
            {
                var off = Plan(r, extra); var on = Plan(r, extra.Concat(new[] { "--stitch-seams" }).ToArray());
                Assert.Eq(false, off.Obj("options").Bool("stitchSeams"), "unticked: options.stitchSeams false");
                Assert.Eq(true, on.Obj("options").Bool("stitchSeams"), "ticked: options.stitchSeams true");
                Assert.Seq(Ids(off), Ids(on), "same steps");
                string offText = Json.Write(off, false);
                Assert.True(!offText.Contains("StitchSeams") && !offText.Contains("stitch-seams"), "unticked plan: no StitchSeams / stitch-seams anywhere (argv, params, env)");
                Assert.True(!off.Arr("notes").Cast<string>().Any(n => n.Contains("seam stitch")), "unticked: no note");
                Assert.True(on.Arr("notes").Cast<string>().Any(n => n.StartsWith("seam stitch on (EXPERIMENTAL", StringComparison.Ordinal)), "ticked: the plan says 'seam stitch on (EXPERIMENTAL ...'");
                int gb = 0;
                foreach (var st in Steps(off))
                {
                    var so = Steps(on).First(x => x.Str("id") == st.Str("id"));
                    if (st.Arr("args") == null) continue;
                    var ao = Args(st); var an = Args(so);
                    if (st.Str("id") == "gpu-bake")
                    {
                        gb++;
                        Assert.Seq(ao.Concat(new[] { "-StitchSeams" }).ToList(), an, "gpu-bake: unticked argv + -StitchSeams last");
                        var fo = st.Obj("params").Arr("fallbackArgs"); var fn = so.Obj("params").Arr("fallbackArgs");
                        if (fo != null) Assert.Seq(fo.Cast<string>().Concat(new[] { "-StitchSeams" }).ToList(), fn.Cast<string>().ToList(), "the tool write-back fallback argv gets it too");
                    }
                    else if (st.Str("id") == "ao-stage")
                        Assert.Seq(ao.Concat(new[] { "--stitch-seams" }).Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), "ao-stage: gpu_ao stage writes the AO layer's final stage -> + --stitch-seams (P6f)");
                    else Assert.Seq(ao.Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), st.Str("id") + ": argv unchanged");
                    Assert.True(st.Obj("envExtra") == null || so.Obj("envExtra").Count == st.Obj("envExtra").Count, st.Str("id") + ": env unchanged");
                }
                Assert.Eq(1, gb, "one gpu_bake step");
            }
            // with the other Advanced switches: -ExactVertexLighting, then -StitchSeams
            var both = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--responsive", "--exact-vertex-lighting", "--stitch-seams");
            var ab = Args(Step(both, "gpu-bake"));
            Assert.Seq(new[] { "-Responsive", "-ExactVertexLighting", "-StitchSeams" }, ab.Skip(ab.Count - 3).ToList(), "-Responsive, -ExactVertexLighting, -StitchSeams");
            var full = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao", "--stitch-seams");
            Assert.True(!Args(Step(full, "ao-trace")).Contains("-StitchSeams") && !Args(Step(full, "probe-bake")).Contains("-StitchSeams"), "the AO trace / probe bake write no lightmap: no -StitchSeams");
        }

        private static void SwitchNoEffect()
        {
            var r = C.NewRoot();
            var ao = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "ao", "--stitch-seams");
            Assert.True(!Json.Write(ao.Arr("steps"), false).Contains("StitchSeams") && !Json.Write(ao.Arr("steps"), false).Contains("stitch-seams"), "AO only: no step gets it");
            Assert.True(ao.Arr("warnings").Cast<string>().Any(w => w.Contains("Stitch lightmap seams (Experimental) has no effect in this mode")), "AO only: the warning");
            var pr = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--mode", "probes", "--stitch-seams");
            Assert.True(!Json.Write(pr.Arr("steps"), false).Contains("StitchSeams"), "Lightprobes: no step gets it");
            Assert.True(pr.Arr("warnings").Cast<string>().Any(w => w.Contains("Stitch lightmap seams (Experimental) has no effect in this mode")), "Lightprobes: the warning");
        }

        private static void ModePlan()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "stitch");
            Assert.Seq(new[] { "seam-stitch-precheck", "backup", "seam-stitch", "copy-final-stage", "install", "verify" }, Ids(p), "Seam stitch only steps");
            Assert.Eq("stitch", p.Obj("options").Str("mode"), "options.mode");
            Assert.Eq(false, p.Obj("options").Bool("ao"), "no AO");
            var st = Args(Step(p, "seam-stitch"));
            Assert.Eq("stage", st[1], "seam_stitch stage");
            Assert.True(st[0].EndsWith(@"standalone\seam_stitch.py", StringComparison.OrdinalIgnoreCase), "script: " + st[0]);
            Assert.Eq(Path.Combine(r.Root, @"tags\scenarios\multi\cyclotron"), Next(st, "--tag-dir"), "--tag-dir");
            Assert.Eq("cyclotron_cyclotron_lightmap", Next(st, "--prefix"), "--prefix");
            Assert.True(Next(st, "--bsp-file").EndsWith("cyclotron.scenario_structure_bsp", StringComparison.OrdinalIgnoreCase), "--bsp-file: " + Next(st, "--bsp-file"));
            Assert.True(Next(st, "--engine").EndsWith("engine_v373", StringComparison.OrdinalIgnoreCase), "--engine: the engine folder of this quality: " + Next(st, "--engine"));
            Assert.Eq(Path.Combine(r.Root, "tool_fast.exe"), Next(st, "--tool"), "--tool");
            Assert.Eq(Path.Combine(r.Farm(200), "seamstitch"), Next(st, "--out"), "--out = the worker's farm \\seamstitch");
            var pc = Args(Step(p, "seam-stitch-precheck"));
            Assert.Eq("precheck", pc[1], "precheck");
            Assert.Seq(Args(Step(p, "seam-stitch")).Skip(2).Take(10).ToList(), pc.Skip(2).ToList(), "the precheck reads the same set (and only that)");
            Assert.True(p.Arr("notes").Cast<string>().Any(n => n.StartsWith("Seam stitch only (EXPERIMENTAL)", StringComparison.Ordinal)), "the note");
            Assert.True(!Json.Write(p.Arr("steps"), false).Contains("h2lm-bake"), "no bake program runs");
            var cp = Step(p, "copy-final-stage");
            Assert.Eq(Path.Combine(r.Farm(200), "seamstitch", "stage"), cp.Obj("params").Str("from"), "copy from the stitch stage");
            var ns = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "stitch", "--no-install");
            Assert.Seq(new[] { "seam-stitch-precheck", "seam-stitch", "copy-final-stage" }, Ids(ns), "--no-install: no backup, install, verify");
        }

        private static void ModeRefusals()
        {
            var nl = C.NewRoot(withLightmaps: false);
            var x = C.DryJson(nl, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "stitch");
            Assert.Eq(2, x.Exit, "no lightmap: " + x);
            string why = x.Json().Str("refused");
            Assert.Contains(why, "has none yet", "plain words");
            Assert.Contains(why, "Bake the lightmap first", "what to do");
            Assert.Contains(why, "Nothing was changed", "and nothing was written");
            var r = C.NewRoot();
            x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "stitch", "--ao");
            Assert.Eq(2, x.Exit, "with --ao: " + x);
            Assert.Contains(x.Err + x.Out, "ambient occlusion cannot be added in the same run", "reason");
            x = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "stitch", "--reuse-capture");
            Assert.Eq(2, x.Exit, "with --reuse-capture: " + x);
            Assert.Contains(x.Err + x.Out, "makes no capture", "reason");
            var mo = H2GpuLightmapper.App.BakeOptions.Defaults();
            mo.Root = r.Root; mo.MapPath = @"C:\nope\x.map"; mo.Mode = H2GpuLightmapper.App.BakeMode.SeamStitch;
            Assert.True(mo.Validate(true).Any(e => e.Contains("Seam stitch only changes the lightmap that is already in the tags: it is not available for a .map bake")), "a .map is refused by the options");
        }
    }
}
