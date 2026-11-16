using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// 1.4.0 single capture: a Standard bake's light probes run on the LIGHTMAP bake's own capture when the engine has it (the bake program names
    /// -KeepLattice / -ReuseLattice, the probe driver carries REUSE_LATTICE_SUPPORT = 1): the lightmap capture is made probe-ready
    /// (GPULM_SKY_HI=1), the lightmap gpu_bake gets -KeepLattice, the probe driver gets the lightmap worker with --skip-capture --reuse-lattice,
    /// and there is no probe worker. The default test payload is an older engine: every other test keeps the two-capture flow unchanged.
    /// </summary>
    internal static class SingleCaptureTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("single 1.4.0 dry: Standard + probes on an engine with -KeepLattice / -ReuseLattice: ONE capture (SKY_HI on the lightmap bake), -KeepLattice last, the probe driver on worker N with --skip-capture --reuse-lattice, no probe worker; the opt-outs and the older engine keep two captures", DryPlan);
            yield return T("single 1.4.0 e2e: Standard on triplicate bakes with one capture (the probe bake reuses the kept lattice), installs the SH tag; the reuse bake does it again on the probe-ready capture; Lightprobes only + reuse takes the lightmap worker's capture", EndToEnd);
            yield return T("wide fill 1.4.0 dry: --fill-wide-holes = -FillWideHoles 16 on the lightmap bake (before -KeepLattice), the note and the option in the plan; refused on an engine without the parameter; no effect (a warning) in Lightprobes mode; unticked plans never mention it", WideFill);
            yield return T("small card 1.4.0 dry: a card under the engine's fixed need (3,935 MiB) gets the plain 'too small' warning, 4 GB the tight-memory one, 8 GB neither", SmallCard);
        }

        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
        private const string Tri = @"scenarios\multi\triplicate\triplicate";
        private static string _pd;
        private static string Payload { get { return _pd ?? (_pd = C.BuildPayload(C.Sb.Dir("payload_single"), true, true, singleCaptureEngine: true)); } }
        private static string Inst(TestRoot r) { return Path.Combine(r.Work, @"_runtime\0.5.0-s25sngl"); }

        private static CliResult Dry(string payload, TestRoot r, params string[] extra)
        {
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = payload;
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json" }); l.AddRange(extra);
            return C.Run(l, 120000);
        }

        private static CliResult Bake(string payload, TestRoot r, params string[] extra)
        {
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = payload;
            l.AddRange(new[] { "--h2ek", r.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--lock-wait-min", "1", "--lock-poll-s", "0.5" });
            l.AddRange(extra);
            return C.Run(l);
        }

        private static JObj Plan(string payload, TestRoot r, params string[] extra)
        {
            var res = Dry(payload, r, extra);
            if (res.Exit != 0) throw new TestFailure("dry run failed: " + res);
            return res.Json();
        }

        private static List<JObj> Steps(JObj p) { return p.Arr("steps").OfType<JObj>().ToList(); }
        private static JObj Step(JObj p, string id) { var s = Steps(p).FirstOrDefault(x => x.Str("id") == id); if (s == null) throw new TestFailure("no step " + id); return s; }
        private static List<string> Args(JObj s) { return s.Arr("args").Select(x => (string)x).ToList(); }
        private static string EnvOf(JObj step, string name) { var e = step.Obj("envExtra"); return e == null ? null : e.Str(name); }

        private static void DryPlan()
        {
            var r = C.NewRoot();
            string[] std = { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11" };
            var p = Plan(Payload, r, std);
            Assert.Seq(new[] { "map-query", "backup", "gpu-bake", "stage-ready", "copy-final-stage", "probe-bake", "sh-merge", "validate-final", "install", "verify" },
                       Steps(p).Select(s => s.Str("id")).ToList(), "the same steps as the two-capture plan");
            Assert.Eq(true, p.Bool("singleCapture"), "singleCapture");
            Assert.Eq(200L, p.Long("worker"), "lightmap worker");
            Assert.True(!p.Long("probeWorker").HasValue, "no probe worker");
            Assert.True(p.Arr("notes").Cast<string>().Any(n => n == BakeOptions.SingleCaptureNote), "the plan says it");
            var g = Step(p, "gpu-bake");
            var ga = Args(g);
            Assert.Eq("-KeepLattice", ga[ga.Count - 1], "-KeepLattice is the lightmap bake's last argument");
            Assert.True(!ga.Contains("-SkipCapture") && !ga.Contains("-ReuseLattice"), "a new capture; the lightmap bake never reuses");
            Assert.Eq("1", EnvOf(g, "GPULM_SKY_HI"), "the lightmap capture is made probe-ready");
            Assert.Eq(1, g.Obj("envExtra").Count, "and gets nothing else");
            Assert.Eq(true, g.Obj("capture").Bool("skyHi"), "its capture record will be probe-ready");
            Assert.Eq(true, g.Obj("capture").Bool("fresh"), "a new capture");
            string run = p.Str("runDir"), ltmp = "triplicate_triplicate_lightmap.scenario_structure_lightmap";
            var pb = Step(p, "probe-bake");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"_research\lightprobes\gpu_lightprobes.py"), "--scenario", Tri, "--bsp", "triplicate", "--worker", "200", "--quality", "super",
                               "--skip-capture", "--reuse-lattice",
                               "--stage", Path.Combine(Inst(r), "engine_v373"), "--ltmp-src", Path.Combine(run, "final_stage", ltmp), "--out", Path.Combine(run, "probes"),
                               "--h2-root", r.Root, "--farm", r.Farm(200), "--plucker-fix", "--scenery-fix" }, Args(pb), "the probe driver runs on the lightmap worker's folder");
            Assert.Eq(false, pb.Obj("capture").Bool("fresh"), "no second capture");
            Assert.Eq(200L, pb.Obj("capture").Long("worker"), "the lightmap worker's capture");
            Assert.Eq(r.Farm(200), p.Str("outProbe"), "outProbe = the lightmap worker's folder");
            // the tool write-back fallback argv never keeps a lattice (it ends the run before any probe pass)
            Assert.True(!Json.Write(Steps(p).Where(s => s.Str("id") != "gpu-bake" && s.Str("id") != "probe-bake").Cast<object>().ToList(), false).Contains("Lattice"), "no other step mentions the lattice switches");

            // Custom: the probe driver gets the photons / rays too, after the two switches' position (--photons / --mc-samples before --skip-capture)
            var pc = Plan(C.BuildPayload(C.Sb.Dir("payload_single_custom"), true, true, customEngine: true, singleCaptureEngine: true), r,
                          std.Concat(new[] { "--quality", "custom", "--photons", "50000000", "--rays", "32" }).ToArray());
            Assert.Eq(true, pc.Bool("singleCapture"), "Custom (engine_custom names the switches): single capture");
            var ca = Args(Step(pc, "gpu-bake"));
            Assert.Eq("-KeepLattice", ca[ca.Count - 1], "Custom: -KeepLattice last");
            var cb = Args(Step(pc, "probe-bake"));
            Assert.True(cb.IndexOf("--mc-samples") < cb.IndexOf("--skip-capture") && cb.IndexOf("--skip-capture") + 1 == cb.IndexOf("--reuse-lattice"), "Custom probe argv order");

            // the dev opt-out: the probe worker's own capture, exactly the plan before 1.4.0
            var po = Plan(Payload, r, std.Concat(new[] { "--probe-own-capture" }).ToArray());
            Assert.Eq(false, po.Bool("singleCapture"), "--probe-own-capture: two captures");
            Assert.Eq(201L, po.Long("probeWorker"), "probe worker 201");
            Assert.True(!Json.Write(po.Arr("steps"), false).Contains("Lattice"), "no lattice switch anywhere");
            Assert.True(Step(po, "gpu-bake").Obj("envExtra").Count == 0, "the lightmap capture gets no SKY_HI");
            Assert.True(!Args(Step(po, "probe-bake")).Contains("--skip-capture"), "the probe worker captures");
            // the scenery permutation fix keeps two captures (its capture is the lightmap's alone)
            var ps = Plan(Payload, r, std.Concat(new[] { "--scenery-permutation-fix" }).ToArray());
            Assert.Eq(false, ps.Bool("singleCapture"), "scenery permutation fix: two captures");
            Assert.Eq(201L, ps.Long("probeWorker"), "probe worker 201 there");
            // no probes -> nothing changes: Lightmap Only on the probe BSP, Standard on a BSP without points
            foreach (var ex in new[] { new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "lightmap" }, new[] { "--scenario", Cyc, "--bsp", "cyclotron" } })
            {
                var pn = Plan(Payload, r, ex);
                Assert.Eq(false, pn.Bool("singleCapture"), "no probe path: not single (" + string.Join(" ", ex) + ")");
                Assert.True(!Json.Write(pn.Arr("steps"), false).Contains("Lattice") && Step(pn, "gpu-bake").Obj("envExtra").Count == 0, "and the lightmap bake is untouched");
            }
            // Lightprobes only with a new capture: the probe worker's own capture, as before
            var pp = Plan(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "probes");
            Assert.Eq(false, pp.Bool("singleCapture"), "Lightprobes only: not single");
            Assert.True(!Args(Step(pp, "probe-bake")).Contains("--reuse-lattice") && !Args(Step(pp, "probe-bake")).Contains("--skip-capture"), "its own capture, no lattice to reuse");
            // an older engine (the default test payload): the two-capture plan
            var pd = C.DryJson(r, std.Concat(new[] { "--dev-probes" }).ToArray());
            Assert.Eq(0, pd.Exit, "older engine: " + pd);
            Assert.Eq(false, pd.Json().Bool("singleCapture"), "older engine: two captures");
            Assert.Eq(201L, pd.Json().Long("probeWorker"), "older engine: probe worker 201");
        }

        private static void WideFill()
        {
            var r = C.NewRoot();
            var lm = Plan(Payload, r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--fill-wide-holes");
            var a = Args(Step(lm, "gpu-bake"));
            Assert.True(a.Count >= 2 && a[a.Count - 2] == "-FillWideHoles" && a[a.Count - 1] == "16", "Lightmap Only: -FillWideHoles 16 last: " + string.Join(" ", a));
            Assert.Eq(true, lm.Obj("options").Bool("fillWideHoles"), "the plan's options say it");
            Assert.True(lm.Arr("notes").Cast<string>().Any(n => n.StartsWith(BakeOptions.FillWideHolesNote, StringComparison.Ordinal)), "the note");
            var st = Plan(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--fill-wide-holes");
            var sa = Args(Step(st, "gpu-bake"));
            Assert.True(sa[sa.Count - 1] == "-KeepLattice" && sa[sa.Count - 3] == "-FillWideHoles" && sa[sa.Count - 2] == "16", "Standard + probes: -FillWideHoles 16 -KeepLattice: " + string.Join(" ", sa));
            Assert.True(!Args(Step(st, "probe-bake")).Contains("-FillWideHoles") && !Json.Write(Args(Step(st, "probe-bake")).Cast<object>().ToList(), false).Contains("FillWide"), "the probe driver never gets it");
            var off = Plan(Payload, r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.True(!Json.Write(off, false).Contains("FillWideHoles") && off.Obj("options").Bool("fillWideHoles") == false, "unticked: the plan never mentions the parameter");
            var pr = Plan(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "probes", "--fill-wide-holes");
            Assert.True(pr.Arr("warnings").Cast<string>().Any(w => w.Contains("has no effect in this mode")), "Lightprobes only: a warning");
            Assert.True(!Json.Write(pr.Arr("steps"), false).Contains("FillWideHoles"), "and no step carries it");
            // an engine whose bake program does not name the parameter (the default test payload): refused before anything, never passed on
            var old = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--fill-wide-holes");
            Assert.Eq(2, old.Exit, "older engine: refused (exit 2): " + old);
            Assert.Contains(old.Out + old.Err, "has no -FillWideHoles parameter", "with the plain reason");
        }

        private static void SmallCard()
        {
            var r = C.NewRoot();
            Func<string, List<string>> warn = mb => C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--assume-vram-mb", mb).Json().Arr("warnings").Cast<string>().ToList();
            var w39 = warn("3935");
            Assert.True(w39.Any(w => w.Contains("too small for this bake") && w.Contains("ignore it here")), "3,935 MiB: the plain warning: " + string.Join(" | ", w39));
            Assert.True(!w39.Any(w => w.Contains("very little free memory")), "3,935 MiB: not the tight-memory wording as well");
            var w40 = warn("4096");
            Assert.True(!w40.Any(w => w.Contains("too small for this bake")) && w40.Any(w => w.Contains("very little free memory")), "4,096 MiB: the engine's fit test passes with no rows to spare: the tight-memory warning: " + string.Join(" | ", w40));
            var w80 = warn("8192");
            Assert.True(!w80.Any(w => w.Contains("too small for this bake") || w.Contains("very little free memory")), "8 GB: neither");
        }

        private static void EndToEnd()
        {
            var r = C.NewRoot();
            var res = Bake(Payload, r, "--scenario", Tri, "--bsp", "triplicate");
            if (res.Exit != 0) throw new TestFailure("single-capture standard bake failed: " + res);
            Assert.Contains(res.Out, "RESULT DONE", "done");
            Assert.Contains(res.Out, "light probes: baked on the lightmap worker's capture", "the session says it");
            var bakes = r.Calls("gpu_bake");
            Assert.Eq(2, bakes.Count, "lightmap bake + probe pass");
            string b0 = string.Join(" ", bakes[0].Arr("bound")), b1 = string.Join(" ", bakes[1].Arr("bound"));
            Assert.Contains(string.Join(" ", bakes[0].Arr("gpulm")), "GPULM_SKY_HI=1", "the lightmap capture is made probe-ready");
            Assert.Contains(b0, "Worker=200", "lightmap worker 200");
            Assert.Contains(b0, "KeepLattice=True", "the lightmap bake keeps its lattice");
            Assert.NotContains(b0, "SkipCapture=True", "a new capture");
            Assert.Contains(b1, "Worker=200", "the probe pass runs on the same worker");
            Assert.Contains(b1, "SkipCapture=True", "no second capture");
            Assert.Contains(b1, "ReuseLattice=True", "and reuses the lattice");
            Assert.True(!Directory.Exists(r.Farm(201)), "no probe worker folder");
            var rec = Json.ReadObjFile(Path.Combine(r.Farm(200), "standalone_capture.json"));
            Assert.Eq(true, rec.Bool("skyHi"), "the lightmap worker's capture record is probe-ready");
            Assert.True(!File.Exists(Path.Combine(r.Farm(200), "lattice_keep.txt")) && !File.Exists(Path.Combine(r.Farm(200), "cast_cache.bin")), "the probe pass removed the kept lattice files");
            string run = res.Lines.Last(x => x.StartsWith("run folder: ", StringComparison.Ordinal)).Substring(12).Trim();
            string log = File.ReadAllText(Path.Combine(run, "bake.log"));
            Assert.Contains(log, "lattice: REUSED from this capture's lightmap bake", "the probe pass reused the lattice");
            Assert.Contains(log, "side|       PROBE: traced", "probe pass");
            byte[] b = File.ReadAllBytes(r.TriLtmp);
            Assert.Eq("SHSHSHSH", System.Text.Encoding.ASCII.GetString(b, b.Length - 8, 8), "the SH tag was merged and installed");

            // reuse: the lightmap worker's capture is probe-ready -> single capture again (no SKY_HI to set: nothing is captured)
            var pj = Plan(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--reuse-capture", "--yes");
            Assert.Eq(true, pj.Bool("singleCapture"), "reuse: single capture");
            Assert.Eq(true, pj.Bool("reuseLightmapCapture"), "reuse: the lightmap capture is reused");
            Assert.True(!pj.Long("probeWorker").HasValue, "reuse: no probe worker");
            var ga = pj.Arr("steps").OfType<JObj>().Single(s => s.Str("id") == "gpu-bake");
            var gargs = ga.Arr("args").Cast<string>().ToList();
            Assert.True(gargs.Contains("-SkipCapture") && gargs[gargs.Count - 1] == "-KeepLattice", "reuse: -SkipCapture ... -KeepLattice");
            Assert.Eq(0, ga.Obj("envExtra").Count, "reuse: no capture, so no SKY_HI");
            var pa = pj.Arr("steps").OfType<JObj>().Single(s => s.Str("id") == "probe-bake").Arr("args").Cast<string>().ToList();
            Assert.True(pa.Contains("--skip-capture") && pa.Contains("--reuse-lattice") && pa[pa.IndexOf("--worker") + 1] == "200", "reuse: the probe driver on worker 200");
            var res2 = Bake(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--reuse-capture", "--yes");
            if (res2.Exit != 0) throw new TestFailure("single-capture reuse bake failed: " + res2);
            Assert.Contains(res2.Out, "RESULT DONE", "reuse done");
            var all = r.Calls("gpu_bake");
            Assert.Eq(4, all.Count, "two more bake program runs");
            Assert.Contains(string.Join(" ", all[2].Arr("bound")), "SkipCapture=True", "reuse: the lightmap bake skips the capture");
            Assert.Contains(string.Join(" ", all[3].Arr("bound")), "ReuseLattice=True", "reuse: the probe pass reuses the lattice");

            // an older-engine reuse of that capture (the default payload) is another engine: its own gate decides; and Lightprobes only + reuse on the
            // single-capture engine takes the lightmap worker's probe-ready capture (the probe worker has none)
            var pp = Plan(Payload, r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "probes", "--reuse-capture", "--yes");
            Assert.Eq(200L, pp.Long("probeWorker"), "Lightprobes only + reuse: the lightmap worker's capture");
            Assert.Eq(true, pp.Bool("reuseProbeCapture"), "and it is reused");
            var ppa = pp.Arr("steps").OfType<JObj>().Single(s => s.Str("id") == "probe-bake").Arr("args").Cast<string>().ToList();
            Assert.True(ppa.Contains("--skip-capture") && !ppa.Contains("--reuse-lattice") && ppa[ppa.IndexOf("--worker") + 1] == "200", "Lightprobes only + reuse argv (no lattice is kept for it)");

            // the same with a lightmap worker the registry does not hold (--worker, as the GPU gates bake): --worker names the folder here too
            var r2 = C.NewRoot();
            var res3 = Bake(Payload, r2, "--scenario", Tri, "--bsp", "triplicate", "--worker", "242");
            if (res3.Exit != 0) throw new TestFailure("single-capture standard bake on --worker 242 failed: " + res3);
            var pw = Plan(Payload, r2, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "probes", "--reuse-capture", "--worker", "242", "--yes");
            Assert.Eq(242L, pw.Long("probeWorker"), "Lightprobes only + reuse + --worker: that worker's probe-ready capture");
            Assert.Eq(true, pw.Bool("reuseProbeCapture"), "and it is reused");
        }
    }
}
