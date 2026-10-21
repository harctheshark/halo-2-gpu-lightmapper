using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// The exe's --cli bake --dry-run --json (what T0 / T1 use): for each option combination, the EXACT child file name, argv,
    /// working folder and environment, with nothing started and nothing written.
    /// </summary>
    internal static class DryRunTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("dry: Lightmap Only / Super / install = backup + the exact production gpu_bake argv + verify; env from scratch", LightmapSuper);
            yield return T("dry: Lightmap Only / Low / stage only = gpu_bake without -WriteBack, nothing else", LowStageOnly);
            yield return T("dry: Lightmap Only + AO = stage-only bake, stage check, gpu_ao trace / apply / stage, shell install", LightmapAo);
            yield return T("dry: AO on Low traces at low; strength / exponent passed explicitly (tag s075e20)", AoLow);
            yield return T("dry: Standard on a BSP without probe points = Lightmap Only (+ the map query)", StandardNoProbes);
            yield return T("dry: Standard with probe points = probe worker M, gpu_lightprobes, sh_stage merge, ONE install", StandardProbes);
            yield return T("dry: Standard + AO + probes: the AO stage is the final stage", StandardAoProbes);
            yield return T("dry: Lightprobes only = ltmp-only backup, live copy, probe worker, sh_stage single, check, install", ProbesOnly);
            yield return T("dry: Custom (Experimental) needs no switch with the guarded engine; refused without it; -Photons / -MCSamples last, probes get them too", Custom);
            yield return T("dry: refusals before anything (exit 2): AO in probes mode, ranges, workers 149 / 60, bad BSP / scenario", Refusals);
            yield return T("dry: reuse without a capture record is refused; dry run is strictly read-only (tree + LOCALAPPDATA unchanged)", ReadOnlyAndReuse);
            yield return T("dry: text mode prints DRY RUN, the env block and every step", TextMode);
            yield return T("dry: on the real ProdRoot (read-only): Direct slot, nothing created in its WORK", RealMine);
            yield return T("dry: privacy (env values only for the shell's own variables) + scenario / BSP canonical case", PrivacyAndCase);
            yield return T("dry: X1 VRAM-aware parts: 24 GB leaves every argv unchanged; 16 GB adds -MaxCallsPerPart; 6 GB adds -BufCapMB too", VramParts);
            yield return T("dry: X2 Keep PC responsive: unticked plans carry no 'Responsive' at all; ticked = + -Responsive last on gpu_bake (incl. the fallback argv), GPULM_RESPONSIVE=1 on the AO trace / probe driver", ResponsivePlan);
            yield return T("dry: W2 2004 Exact Vertex Lighting?: unticked plans carry no -ExactVertexLighting; ticked = + it on every gpu_bake argv (after -Responsive), the note, no other step changes", ExactVertexPlan);
            yield return T("dry: Scenery Multi Permutation Fix: unticked plans carry no -SceneryOwnPermutationOnly; ticked = + it last on every gpu_bake argv (after the other two), the note, warnings for AO / probes, no other step changes", SceneryPermPlan);
            yield return T("dry: Scenery Multi Permutation Fix is engine-gated: a payload whose gpu_bake.ps1 lacks the parameter refuses the flag (exit 2, 'Needs engine 0.6.4 or newer.'), never plans it; Custom uses engine_custom's own answer", SceneryPermGate);
            yield return T("dry: DF64 Compatibility mode: every plan states the precision (JSON + text); --assume-no-fp64 plans Compatibility mode on an engine with it and refuses on one without; --force-compat = GPULM_COMPAT=1 on exactly the GPU steps, argv unchanged, refused (dry too) on an engine without the mode", Df64Plan);
            yield return T("dry: W2 run folder =<ts>_<label <= 20 chars> and the run's install backup is 'tagbak' (the worker's [4b] backup keeps its name)", RunFolderPlan);
            yield return T("dry: X2 live VRAM: a 4090 with <= 3 GiB held by others = today's argv; a game on top shrinks -MaxCallsPerPart with a plain note", LiveVramPlan);
            yield return T("dry: X1 light-probe preflight refuses a BSP past the GPU probe pass's limit (282+) before anything runs", ProbePreflight);
            yield return T("dry: X1 H2EK at a drive root (a SUBST drive): refused without features.driveRoot, planned with it, no drive-relative or doubled paths", DriveRoot);
        }


        // ------------------------------------------------------------------ X1 (Monday 2026-10-19 robustness list, items 2 / 4 / 7)

        // ------------------------------------------------------------------ X2: Keep PC responsive + live VRAM through the exe's dry run

        // the predicted run folder carries the clock (yyyyMMdd_HHmmss_): two dry runs a second apart differ only there
        private static string NoStamp(string x) { return System.Text.RegularExpressions.Regex.Replace(x, @"\d{8}_\d{6}_", "STAMP_"); }

        private static void ResponsivePlan()
        {
            var r = C.NewRoot();
            var strip = new Func<IEnumerable<string>, List<string>>(a => a.Where(x => x != "-Responsive").ToList());
            Func<string[], Tuple<JObj, JObj>> pair = extra => Tuple.Create(Plan(r, extra), Plan(r, extra.Concat(new[] { "--responsive" }).ToArray()));
            // every step's argv: ticked == unticked + -Responsive on the gpu_bake steps only; the unticked plan has no -Responsive anywhere
            foreach (var extra in new[] { new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--ao" },
                                          new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao" } })
            {
                var t = pair(extra);
                var off = t.Item1; var on = t.Item2;
                Assert.Eq(false, off.Obj("options").Bool("responsive"), "unticked: options.responsive false");
                Assert.Eq(true, on.Obj("options").Bool("responsive"), "ticked: options.responsive true");
                Assert.Seq(Ids(off), Ids(on), "same steps");
                Assert.True(!Json.Write(off, false).Contains("Responsive"), "unticked plan: no 'Responsive' anywhere (argv, params, env)");
                Assert.True(!off.Arr("notes").Cast<string>().Any(n => n.Contains("responsive on")), "unticked: no note");
                Assert.True(on.Arr("notes").Cast<string>().Any(n => n.StartsWith("responsive on", StringComparison.Ordinal)), "ticked: the plan says 'responsive on'");
                int gb = 0;
                foreach (var st in Steps(off))
                {
                    var so = Steps(on).First(x => x.Str("id") == st.Str("id"));
                    if (st.Arr("args") == null) continue;
                    var ao = Args(st); var an = Args(so);
                    if (st.Str("id") == "gpu-bake")
                    {
                        gb++;
                        Assert.Seq(ao.Concat(new[] { "-Responsive" }).ToList(), an, "gpu-bake: unticked argv + -Responsive last");
                        var fo = st.Obj("params").Arr("fallbackArgs"); var fn = so.Obj("params").Arr("fallbackArgs");
                        if (fo != null) Assert.Seq(fo.Cast<string>().Concat(new[] { "-Responsive" }).ToList(), fn.Cast<string>().ToList(), "the tool write-back fallback argv gets it too");
                    }
                    else Assert.Seq(ao.Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), st.Str("id") + ": argv unchanged");
                }
                Assert.Eq(1, gb, "one gpu_bake step");
            }
            // the driver scripts (gpu_ao trace, gpu_lightprobes) run their own gpu_bake and cannot take the switch: they get the environment variable
            var full = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao", "--responsive");
            Assert.Eq("1", Step(full, "ao-trace").Obj("envExtra").Str("GPULM_RESPONSIVE"), "ao-trace: GPULM_RESPONSIVE=1");
            Assert.Eq("1", Step(full, "probe-bake").Obj("envExtra").Str("GPULM_RESPONSIVE"), "probe-bake: GPULM_RESPONSIVE=1");
            Assert.Eq(0, Step(full, "ao-apply").Obj("envExtra").Count, "ao-apply (no GPU): no env");
            Assert.Eq(0, Step(full, "gpu-bake").Obj("envExtra").Count, "gpu-bake: the switch is an argument, not env");
            var offFull = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao");
            Assert.Eq(0, Step(offFull, "ao-trace").Obj("envExtra").Count, "unticked: ao-trace env empty");
            Assert.Eq(0, Step(offFull, "probe-bake").Obj("envExtra").Count, "unticked: probe-bake env empty");
            // the sizing from the VRAM comes first, -Responsive last
            var six = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--assume-vram-mb", "6144", "--responsive");
            var a6 = Args(Step(six, "gpu-bake"));
            Assert.Seq(new[] { "-BufCapMB", "1536", "-Responsive" }, a6.Skip(a6.Count - 3).ToList(), "VRAM sizing then -Responsive");
        }

        // ------------------------------------------------------------------ W2 (0.6.3): 2004 Exact Vertex Lighting? + the short run folder

        private static void ExactVertexPlan()
        {
            var r = C.NewRoot();
            foreach (var extra in new[] { new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--ao" },
                                          new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao" } })
            {
                var off = Plan(r, extra); var on = Plan(r, extra.Concat(new[] { "--exact-vertex-lighting" }).ToArray());
                Assert.Eq(false, off.Obj("options").Bool("exactVertexLighting"), "unticked: options.exactVertexLighting false");
                Assert.Eq(true, on.Obj("options").Bool("exactVertexLighting"), "ticked: options.exactVertexLighting true");
                Assert.Seq(Ids(off), Ids(on), "same steps");
                Assert.True(!Json.Write(off, false).Contains("-ExactVertexLighting"), "unticked plan: no -ExactVertexLighting anywhere (argv, params, env)");
                Assert.True(!off.Arr("notes").Cast<string>().Any(n => n.Contains("exact vertex lighting")), "unticked: no note");
                Assert.True(on.Arr("notes").Cast<string>().Any(n => n.StartsWith("exact vertex lighting on (slower tag writer)", StringComparison.Ordinal)), "ticked: the plan says 'exact vertex lighting on (slower tag writer)'");
                int gb = 0;
                foreach (var st in Steps(off))
                {
                    var so = Steps(on).First(x => x.Str("id") == st.Str("id"));
                    if (st.Arr("args") == null) continue;
                    var ao = Args(st); var an = Args(so);
                    if (st.Str("id") == "gpu-bake")
                    {
                        gb++;
                        Assert.Seq(ao.Concat(new[] { "-ExactVertexLighting" }).ToList(), an, "gpu-bake: unticked argv + -ExactVertexLighting last");
                        var fo = st.Obj("params").Arr("fallbackArgs"); var fn = so.Obj("params").Arr("fallbackArgs");
                        if (fo != null) Assert.Seq(fo.Cast<string>().Concat(new[] { "-ExactVertexLighting" }).ToList(), fn.Cast<string>().ToList(), "the tool write-back fallback argv gets it too");
                    }
                    else if (st.Str("id") == "ao-stage")
                        Assert.Seq(ao.Concat(new[] { "--exact-vertex-lighting" }).Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), "ao-stage: gpu_ao stage writes the AO layer's final stage -> + --exact-vertex-lighting (P6b)");
                    else Assert.Seq(ao.Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), st.Str("id") + ": argv unchanged");
                    Assert.True(st.Obj("envExtra") == null || so.Obj("envExtra").Count == st.Obj("envExtra").Count, st.Str("id") + ": env unchanged");
                }
                Assert.Eq(1, gb, "one gpu_bake step");
            }
            // both options: -Responsive first, -ExactVertexLighting last; the AO trace / probe bake get only GPULM_RESPONSIVE (they write no lightmap)
            var both = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--responsive", "--exact-vertex-lighting");
            var ab = Args(Step(both, "gpu-bake"));
            Assert.Seq(new[] { "-Responsive", "-ExactVertexLighting" }, ab.Skip(ab.Count - 2).ToList(), "-Responsive then -ExactVertexLighting");
            var full = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao", "--responsive", "--exact-vertex-lighting");
            Assert.True(Args(Step(full, "ao-stage")).Contains("--exact-vertex-lighting") && !Args(Step(offFullEvl(r), "ao-stage")).Contains("--exact-vertex-lighting"), "AO: only the ticked plan's gpu_ao stage gets --exact-vertex-lighting");
            Assert.True(!Args(Step(full, "ao-trace")).Contains("-ExactVertexLighting") && !Args(Step(full, "probe-bake")).Contains("-ExactVertexLighting"), "the AO trace / probe bake write no lightmap: no -ExactVertexLighting");
            Assert.Eq("1", Step(full, "probe-bake").Obj("envExtra").Str("GPULM_RESPONSIVE"), "probe-bake still GPULM_RESPONSIVE=1");
            var a2 = Args(Step(full, "gpu-bake"));
            Assert.Eq("-ExactVertexLighting", a2[a2.Count - 1], "the lightmap bake of Standard + probes + AO gets it");
            // the sizing from the VRAM comes first
            var six = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--assume-vram-mb", "6144", "--exact-vertex-lighting");
            var a6 = Args(Step(six, "gpu-bake"));
            Assert.Seq(new[] { "-BufCapMB", "1536", "-ExactVertexLighting" }, a6.Skip(a6.Count - 3).ToList(), "VRAM sizing then -ExactVertexLighting");
        }

        private static JObj offFullEvl(TestRoot r) { return Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao", "--responsive"); }

        // ------------------------------------------------------------------ Scenery Multi Permutation Fix (Experimental Improvement)

        private static void SceneryPermPlan()
        {
            const string sw = "-SceneryOwnPermutationOnly";
            var r = C.NewRoot();
            foreach (var extra in new[] { new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--ao" },
                                          new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao" } })
            {
                var off = Plan(r, extra); var on = Plan(r, extra.Concat(new[] { "--scenery-permutation-fix" }).ToArray());
                Assert.Eq(false, off.Obj("options").Bool("sceneryPermutationFix"), "unticked: options.sceneryPermutationFix false");
                Assert.Eq(true, on.Obj("options").Bool("sceneryPermutationFix"), "ticked: options.sceneryPermutationFix true");
                Assert.Seq(Ids(off), Ids(on), "same steps");
                Assert.True(!Json.Write(off, false).Contains("SceneryOwnPermutationOnly"), "unticked plan: no SceneryOwnPermutationOnly anywhere (argv, params, env, notes)");
                Assert.True(!off.Arr("notes").Cast<string>().Any(n => n.Contains("permutation fix")), "unticked: no note");
                Assert.True(on.Arr("notes").Cast<string>().Any(n => n.StartsWith("scenery multi permutation fix on (experimental; differs from the 2004 tool on purpose)", StringComparison.Ordinal)), "ticked: the plan says 'scenery multi permutation fix on'");
                Assert.Contains(on.Str("engine"), "gpu_bake.ps1", "engine named");
                bool layered = extra.Contains("--ao") || extra.Contains("--probe-points");
                Assert.Eq(layered, on.Arr("warnings").Cast<string>().Any(w => w.Contains("Scenery Multi Permutation Fix") && w.Contains("untested")), "the AO / probe layers get a warning, a plain lightmap bake does not");
                int gb = 0;
                foreach (var st in Steps(off))
                {
                    var so = Steps(on).First(x => x.Str("id") == st.Str("id"));
                    if (st.Arr("args") == null) continue;
                    var ao = Args(st); var an = Args(so);
                    if (st.Str("id") == "gpu-bake")
                    {
                        gb++;
                        Assert.Seq(ao.Concat(new[] { sw }).ToList(), an, "gpu-bake: unticked argv + " + sw + " last");
                        var fo = st.Obj("params").Arr("fallbackArgs"); var fn = so.Obj("params").Arr("fallbackArgs");
                        if (fo != null) Assert.Seq(fo.Cast<string>().Concat(new[] { sw }).ToList(), fn.Cast<string>().ToList(), "the tool write-back fallback argv gets it too");
                    }
                    else Assert.Seq(ao.Select(NoStamp).ToList(), an.Select(NoStamp).ToList(), st.Str("id") + ": argv unchanged (the AO trace / stage and the probe bake never get the switch)");
                    Assert.True(st.Obj("envExtra") == null || so.Obj("envExtra").Count == st.Obj("envExtra").Count, st.Str("id") + ": env unchanged");
                }
                Assert.Eq(1, gb, "one gpu_bake step");
            }
            // after the other two Advanced options, behind the VRAM sizing
            var all = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--assume-vram-mb", "6144", "--responsive", "--exact-vertex-lighting", "--scenery-permutation-fix");
            var aa = Args(Step(all, "gpu-bake"));
            Assert.Seq(new[] { "-BufCapMB", "1536", "-Responsive", "-ExactVertexLighting", sw }, aa.Skip(aa.Count - 5).ToList(), "VRAM sizing, -Responsive, -ExactVertexLighting, then the new switch");
            // Lightprobes only: no lightmap bake, so nothing to pass it to -- said plainly
            var pr = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--mode", "probes", "--scenery-permutation-fix");
            Assert.True(!Json.Write(pr.Arr("steps"), false).Contains(sw), "Lightprobes only: no step carries the switch");
            Assert.True(pr.Arr("warnings").Cast<string>().Any(w => w.Contains("no effect in Lightprobes mode")), "and the plan warns");
            // the text form (what --dry-run prints without --json) and the bake log header list the option
            var l = C.Base("bake"); l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--scenery-permutation-fix" });
            var tx = C.Run(l);
            Assert.Eq(0, tx.Exit, "text dry run: " + tx);
            Assert.Contains(tx.Out, "scenery multi permutation fix on", "the text plan's options line");
            Assert.Contains(tx.Out, sw, "and the gpu_bake argv");
        }

        private static void SceneryPermGate()
        {
            var r = C.NewRoot();
            var cyc = new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" };
            string oldPayload = C.BuildPayload(C.Sb.Dir("payload_gate_old"), sceneryEngine: false);
            Func<string, string[], CliResult> run = (payload, extra) =>
            {
                var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = payload;
                l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json" }); l.AddRange(extra);
                return C.Run(l, 120000);
            };
            // an older engine: the flag is refused plainly, in a dry run too -- nothing is planned, so no argv carries the unknown parameter
            var bad = run(oldPayload, cyc.Concat(new[] { "--scenery-permutation-fix" }).ToArray());
            Assert.Eq(2, bad.Exit, "refused: " + bad);
            string reason = bad.Json().Str("refused");
            Assert.Contains(reason, "Scenery Multi Permutation Fix (Experimental Improvement) is not available", "plain reason");
            Assert.Contains(reason, "-SceneryOwnPermutationOnly", "names the missing parameter");
            Assert.Contains(reason, "Needs engine 0.6.4 or newer.", "and the requirement");
            Assert.True(!bad.Out.Contains("\"steps\""), "no plan was produced");
            // the text form refuses the same way, never a plan
            var l2 = C.Base("bake"); l2[l2.IndexOf("--payload-dir") + 1] = oldPayload;
            l2.AddRange(new[] { "--h2ek", r.Root, "--dry-run" }); l2.AddRange(cyc); l2.Add("--scenery-permutation-fix");
            var tx = C.Run(l2);
            Assert.Eq(2, tx.Exit, "text refusal: " + tx);
            Assert.Contains(tx.Out + tx.Err, "Needs engine 0.6.4 or newer.", "plain message in the text form");
            // the same old payload without the flag plans exactly as before (the unticked path is untouched)
            var fine = run(oldPayload, cyc);
            Assert.Eq(0, fine.Exit, "old engine, option off: plans: " + fine);
            Assert.True(!fine.Out.Contains("SceneryOwnPermutationOnly"), "and carries nothing of it");
            // a payload whose engine declares it plans it
            var ok = run(C.PayloadDir, cyc.Concat(new[] { "--scenery-permutation-fix" }).ToArray());
            Assert.Eq(0, ok.Exit, "new engine: " + ok);
            Assert.True(Args(Step(ok.Json(), "gpu-bake")).Contains("-SceneryOwnPermutationOnly"), "planned");
            // Custom runs engine_custom: its own answer decides (engine_v373 declaring it is not enough)
            string mixed = C.BuildPayload(C.Sb.Dir("payload_gate_mixed"), customEngine: true, customSceneryEngine: false);
            var cust = new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom", "--photons", "20000000", "--rays", "8" };
            var cbad = run(mixed, cust.Concat(new[] { "--scenery-permutation-fix" }).ToArray());
            Assert.Eq(2, cbad.Exit, "Custom on an engine_custom without the parameter: refused: " + cbad);
            Assert.Contains(cbad.Json().Str("refused"), "engine_custom/gpu_bake.ps1", "names engine_custom");
            Assert.Eq(0, run(mixed, cyc.Concat(new[] { "--scenery-permutation-fix" }).ToArray()).Exit, "Super on the same payload: engine_v373 declares it: planned");
            Assert.Eq(0, run(C.BuildPayload(C.Sb.Dir("payload_gate_cust_ok"), customEngine: true), cust.Concat(new[] { "--scenery-permutation-fix" }).ToArray()).Exit, "Custom with a declaring engine_custom: planned");
        }

        // ------------------------------------------------------------------ Compatibility mode (DF64), engine 0.6.4

        private static CliResult DfDry(string payload, TestRoot r, params string[] extra)
        {
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = payload;
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json" }); l.AddRange(extra);
            return C.Run(l, 120000);
        }

        private static string EnvOf(JObj step, string name) { var e = step.Obj("envExtra"); return e == null ? null : e.Str(name); }

        private static void Df64Plan()
        {
            const string Var = "GPULM_COMPAT";
            var r = C.NewRoot();
            string oldP = C.PayloadDir;                                                         // the default test payload = the 0.6.3 engine: no Compatibility mode
            string newP = C.BuildPayload(C.Sb.Dir("payload_df64_new"), compatEngine: true);     // engine 0.6.4: gpu_bake.ps1 validates GPULM_COMPAT
            var cyc = new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" };
            Func<string[], string[], string[]> cat = (a, b) => a.Concat(b).ToArray();

            // ---- every plan states the precision. Engine with the mode, GPU unknown at planning time: automatic. Engine without: FP64 required.
            var auto = DfDry(newP, r, cyc); Assert.Eq(0, auto.Exit, "plan: " + auto);
            var pa = auto.Json();
            Assert.Eq("automatic", pa.Obj("precision").Str("mode"), "new engine: automatic");
            Assert.Eq(true, pa.Obj("precision").Bool("engineHasCompatMode"), "engine has the mode");
            Assert.Eq(GpuCheck.PrecisionAutomatic, pa.Obj("precision").Str("text"), "text");
            Assert.Eq(false, pa.Obj("options").Bool("forceCompat"), "not forced");
            Assert.True(!Json.Write(pa, false).Contains(Var), "an unforced plan never mentions " + Var + " (nothing is passed; the engine chooses by the GPU)");
            Assert.True(Steps(pa).All(s => s.Obj("envExtra") == null || s.Obj("envExtra").Count == 0), "no step gets any env");
            var po = DfDry(oldP, r, cyc).Json();
            Assert.Eq("full-fp64-required", po.Obj("precision").Str("mode"), "old engine");
            Assert.Eq(false, po.Obj("precision").Bool("engineHasCompatMode"), "old engine has no mode");
            Assert.Eq(GpuCheck.PrecisionNeedsFp64, po.Obj("precision").Str("text"), "old engine text");
            // the text form (what --dry-run prints) carries the same line, in the header block
            var lt = C.Base("bake"); lt[lt.IndexOf("--payload-dir") + 1] = newP; lt.AddRange(new[] { "--h2ek", r.Root, "--dry-run" }); lt.AddRange(cyc);
            var tx = C.Run(lt); Assert.Eq(0, tx.Exit, "text: " + tx);
            Assert.Contains(tx.Out, "  precision " + GpuCheck.PrecisionAutomatic, "the text plan's precision line");

            // ---- --assume-no-fp64 (dry run only): Compatibility mode on an engine with it; no argv / env / step change
            var nf = DfDry(newP, r, cat(cyc, new[] { "--assume-no-fp64" })); Assert.Eq(0, nf.Exit, "assume-no-fp64 on the new engine: " + nf);
            var pn = nf.Json();
            Assert.Eq("compat-df64", pn.Obj("precision").Str("mode"), "mode");
            Assert.Eq(GpuCheck.PrecisionCompat, pn.Obj("precision").Str("text"), "the user-facing sentence");
            Assert.True(pn.Arr("notes").Cast<string>().Any(n => n.StartsWith("compatibility mode (DF64): this GPU has no FP64", StringComparison.Ordinal)), "a plan note says it");
            Assert.Seq(Ids(pa), Ids(pn), "same steps");
            foreach (var s in Steps(pn)) if (s.Arr("args") != null) Assert.Seq(Args(Step(pa, s.Str("id"))).Select(NoStamp).ToList(), Args(s).Select(NoStamp).ToList(), s.Str("id") + ": argv identical (the engine picks the kernels itself)");
            Assert.True(!Json.Write(pn, false).Contains(Var), "nothing passed");
            var nfOld = DfDry(oldP, r, cat(cyc, new[] { "--assume-no-fp64" }));
            Assert.Eq(2, nfOld.Exit, "an engine without the mode refuses a GPU without FP64, as 0.6.3 did: " + nfOld);
            Assert.Contains(nfOld.Json().Str("refused"), GpuCheck.NoFp64Plain, "with the plain sentence");
            Assert.True(!nfOld.Out.Contains("\"steps\""), "no plan");
            var bad = C.Base("bake"); bad.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--assume-no-fp64", "--dev-skip-preflight", "--dev-skip-gpucheck" });
            var badR = C.Run(bad); Assert.Eq(2, badR.Exit, "a real run asks gpucheck: " + badR);
            Assert.Contains(badR.Err + badR.Out, "--assume-no-fp64 is only for --dry-run", "dry-run only");

            // ---- --force-compat: GPULM_COMPAT=1 on exactly the steps that run the GPU engine, nothing else changes
            foreach (var extra in new[] { cyc, cat(cyc, new[] { "--no-install" }), cat(cyc, new[] { "--ao" }), new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--ao" },
                                          new[] { "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--mode", "probes" } })
            {
                var off = DfDry(newP, r, extra).Json();
                var onR = DfDry(newP, r, cat(extra, new[] { "--force-compat" })); Assert.Eq(0, onR.Exit, "forced plan: " + onR);
                var on = onR.Json();
                Assert.Eq(true, on.Obj("options").Bool("forceCompat"), "options.forceCompat");
                Assert.Eq("compat-df64-forced", on.Obj("precision").Str("mode"), "mode");
                Assert.Eq(GpuCheck.PrecisionCompatForced, on.Obj("precision").Str("text"), "forced text");
                Assert.True(on.Arr("notes").Cast<string>().Any(n => n.StartsWith("Compatibility mode (DF64) FORCED for testing (--force-compat: GPULM_COMPAT=1", StringComparison.Ordinal)), "plan note");
                Assert.Seq(Ids(off), Ids(on), "same steps");
                int gpuSteps = 0;
                foreach (var st in Steps(on))
                {
                    string id = st.Str("id");
                    bool gpu = id == "gpu-bake" || id == "ao-trace" || id == "probe-bake";
                    Assert.Eq(gpu ? "1" : null, EnvOf(st, Var), id + ": " + Var + (gpu ? "=1" : " absent"));
                    if (gpu) gpuSteps++;
                    var so = Step(off, id);
                    Assert.True(EnvOf(so, Var) == null, id + ": the unforced plan has none");
                    if (st.Arr("args") != null) Assert.Seq(Args(so).Select(NoStamp).ToList(), Args(st).Select(NoStamp).ToList(), id + ": argv unchanged");
                    if (id == "stage-or-tool-fallback") Assert.Eq(Var + "=1", st.Obj("params").Str("fallbackEnv"), "the tool write-back fallback (a gpu_bake re-run) is told");
                }
                Assert.True(gpuSteps >= 1, "at least one GPU step carries it");
                // only the intended variable was added to any step's env
                Assert.True(Steps(on).All(s => s.Obj("envExtra") == null || s.Obj("envExtra").Keys.All(k => k == Var || (off != null && Step(off, s.Str("id")).Obj("envExtra") != null && Step(off, s.Str("id")).Obj("envExtra").Keys.Contains(k)))), "no other env added");
            }
            var l2 = C.Base("bake"); l2[l2.IndexOf("--payload-dir") + 1] = newP; l2.AddRange(new[] { "--h2ek", r.Root, "--dry-run" }); l2.AddRange(cat(cyc, new[] { "--force-compat" }));
            var tx2 = C.Run(l2); Assert.Eq(0, tx2.Exit, "text forced: " + tx2);
            Assert.Contains(tx2.Out, "    env+  GPULM_COMPAT=1", "the step's env line");
            Assert.Contains(tx2.Out, "compatibility mode (DF64) FORCED", "and the options line");
            Assert.Contains(tx2.Out, "  precision " + GpuCheck.PrecisionCompatForced, "and the precision line");

            // ---- an engine without the mode: --force-compat is refused (dry too), the variable is never passed, nothing is planned
            var fo = DfDry(oldP, r, cat(cyc, new[] { "--force-compat" }));
            Assert.Eq(2, fo.Exit, "old engine: refused: " + fo);
            string why = fo.Json().Str("refused");
            Assert.Contains(why, "--force-compat is not available", "plain reason");
            Assert.Contains(why, "engine_v373/gpu_bake.ps1", "names the engine script");
            Assert.Contains(why, "Compatibility mode needs engine 0.6.4 or newer.", "and the requirement");
            Assert.True(!fo.Out.Contains("\"steps\"") && !fo.Out.Contains("env+"), "no plan, no env");
            var lf = C.Base("bake"); lf.AddRange(new[] { "--h2ek", r.Root, "--dry-run" }); lf.AddRange(cat(cyc, new[] { "--force-compat" }));
            var tf = C.Run(lf); Assert.Eq(2, tf.Exit, "text refusal: " + tf);
            Assert.Contains(tf.Out + tf.Err, "Compatibility mode needs engine 0.6.4 or newer.", "plain message in the text form");

            // ---- the engines a bake runs ALL need the mode: Custom runs engine_custom, AO adds the AO engine copy
            string mixedCustom = C.BuildPayload(C.Sb.Dir("payload_df64_cust"), customEngine: true, compatEngine: true, customCompatEngine: false);
            var cust = new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom", "--photons", "20000000", "--rays", "8" };
            var cf = DfDry(mixedCustom, r, cat(cust, new[] { "--force-compat" }));
            Assert.Eq(2, cf.Exit, "Custom on an engine_custom without the mode: refused: " + cf);
            Assert.Contains(cf.Json().Str("refused"), "engine_custom/gpu_bake.ps1", "names engine_custom");
            Assert.Eq(0, DfDry(mixedCustom, r, cat(cyc, new[] { "--force-compat" })).Exit, "Super on the same payload (engine_v373 has it): planned");
            Assert.Eq(2, DfDry(mixedCustom, r, cat(cust, new[] { "--assume-no-fp64" })).Exit, "and a GPU without FP64 under Custom is refused plainly");
            string mixedAo = C.BuildPayload(C.Sb.Dir("payload_df64_ao"), compatEngine: true, aoCompatEngine: false);
            var af = DfDry(mixedAo, r, cat(cyc, new[] { "--ao", "--force-compat" }));
            Assert.Eq(2, af.Exit, "AO with an AO engine copy without the mode: refused: " + af);
            Assert.Contains(af.Json().Str("refused"), "dev_AO/gpu_bake.ps1", "names the AO engine");
            Assert.Eq(0, DfDry(mixedAo, r, cat(cyc, new[] { "--force-compat" })).Exit, "the same payload without AO: planned");
            Assert.Eq("full-fp64-required", DfDry(mixedAo, r, cat(cyc, new[] { "--ao" })).Json().Obj("precision").Str("mode"), "with AO the plan says the AO engine needs FP64");
        }

        private static void RunFolderPlan()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            string rd = Path.GetFileName(p.Str("runDir"));
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(rd, @"^\d{8}_\d{6}_cyclotron_cyclotron$"), "run folder <ts>_<leaf>_<bsp> when it fits 20 characters: " + rd);
            // a long name is cut to 20 characters of label
            var q = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes");
            string rq = Path.GetFileName(q.Str("runDir"));
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(rq, @"^\d{8}_\d{6}_triplicate_triplicat$"), "label cut to 20 characters: " + rq);
            Assert.Eq(20, rq.Substring(16).Length, "20 label characters");
            // the run's install backup is RUN\tagbak where the run folder is used; the worker's [4b] backup keeps tag_backup_before_writeback
            Assert.Eq(Path.Combine(q.Str("runDir"), "tagbak"), q.Str("installBackupDir"), "Standard + probes: the run's install backup = RUN/tagbak");
            var pr = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes", "--mode", "probes");
            Assert.Eq(Path.Combine(pr.Str("runDir"), "tagbak"), pr.Str("installBackupDir"), "Lightprobes only: RUN/tagbak");
            Assert.Eq(Path.Combine(r.Farm(200), "tag_backup_before_writeback"), p.Str("installBackupDir"), "Lightmap Only: [4b]'s worker backup keeps its name");
            Assert.True(!Json.Write(q, false).Contains("tag_backup_before_writeback"), "no long run-folder backup name left in the Standard + probes plan");
        }

        private static void LiveVramPlan()
        {
            var r = C.NewRoot();
            string[] bake = { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" };
            Func<string, string, JObj> plan = (vram, other) =>
            {
                var l = bake.ToList();
                if (vram != null) l.AddRange(new[] { "--assume-vram-mb", vram });
                if (other != null) l.AddRange(new[] { "--assume-other-vram-mb", other });
                return Plan(r, l.ToArray());
            };
            Func<JObj, string> argv = p => NoStamp(string.Join("\n", Steps(p).Where(s => s.Arr("args") != null).Select(s => s.Str("id") + ": " + string.Join(" ", Args(s)))));
            string baseline = argv(plan(null, null));
            // the RTX 4090 (24,138 MiB), a normal desktop (0 - 3,072 MiB held): every step's argv is today's, and nothing is said about other programs
            foreach (var other in new[] { "0", "1024", "2048", "3072" })
            {
                var p = plan("24138", other);
                Assert.Eq(baseline, argv(p), "4090 with " + other + " MiB held by others: argv byte-identical to no figure at all");
                Assert.True(!p.Arr("notes").Cast<string>().Any(n => n.Contains("in use by other programs")), other + " MiB: no note");
                Assert.Eq(0L, p.Obj("gpuLimits").Long("maxCallsPerPart"), other + " MiB: nothing passed");
            }
            // a game holds 14 GB: the parts shrink, the plan says so plainly
            var g = plan("24138", "14336");
            long n = g.Obj("gpuLimits").Long("maxCallsPerPart").Value;
            Assert.True(n > 0 && n < 52195783L, "14 GiB held on a 4090: -MaxCallsPerPart " + n);
            var a = Args(Step(g, "gpu-bake"));
            Assert.Eq(n.ToString(), a[a.IndexOf("-MaxCallsPerPart") + 1], "the argv carries it");
            Assert.Eq(14336L * 1048576L, g.Obj("gpuLimits").Long("otherUseBytes"), "the plan records what was held");
            Assert.True(g.Obj("gpuLimits").Long("effectiveVramBytes") > 0, "and what the parts were sized for");
            string note = g.Arr("notes").Cast<string>().FirstOrDefault(x => x.Contains("in use by other programs"));
            Assert.True(note != null && note.StartsWith("14,336 MiB of VRAM is in use by other programs; parts sized for ", StringComparison.Ordinal), "note: " + note);
            var fb = Step(g, "stage-or-tool-fallback").Obj("params").Arr("fallbackArgs").Cast<string>().ToList();
            Assert.True(fb.Contains("-MaxCallsPerPart") && fb.Contains("-WriteBack"), "the fallback argv carries it too");
            // more held = smaller parts
            long n20 = plan("24138", "20480").Obj("gpuLimits").Long("maxCallsPerPart").Value;
            Assert.True(n20 > 0 && n20 < n, "20 GiB held < 14 GiB held: " + n20 + " < " + n);
            // the dry-run switches are validated
            var l1 = C.Base("bake"); l1.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--dry-run", "--assume-other-vram-mb", "4096", "--dev-skip-preflight", "--dev-skip-gpucheck" });
            var b1 = C.Run(l1);
            Assert.True(b1.Exit != 0 && (b1.Out + b1.Err).Contains("needs --assume-vram-mb"), "--assume-other-vram-mb alone: " + b1);
            var l2 = C.Base("bake"); l2.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--assume-vram-mb", "8192", "--assume-other-vram-mb", "4096", "--dev-skip-preflight", "--dev-skip-gpucheck" });
            var b2 = C.Run(l2);
            Assert.True(b2.Exit != 0 && (b2.Out + b2.Err).Contains("only for --dry-run"), "outside --dry-run: " + b2);
        }

        private static void VramParts()
        {
            var r = C.NewRoot();
            string[] bakeArgs = { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" };
            Func<string, JObj> plan = mb => mb == null ? Plan(r, bakeArgs) : Plan(r, bakeArgs.Concat(new[] { "--assume-vram-mb", mb }).ToArray());
            var none = plan(null);
            var baseArgs = Args(Step(none, "gpu-bake"));
            Assert.True(none.Obj("gpuLimits") == null, "no VRAM figure in a dry run without --assume-vram-mb: no gpuLimits, the production argv");
            // 24 GB (the RTX 4090's gpucheck figure, 24,138 MiB): the planner's own cap binds first -> the argv is unchanged
            var p24 = plan("24138");
            Assert.Seq(baseArgs, Args(Step(p24, "gpu-bake")), "24 GB: the gpu_bake argv is byte-for-byte the production one");
            Func<JObj, string> argv = st => st.Arr("args") == null ? "" : string.Join(" ", Args(st));
            Assert.Seq(Steps(none).Select(argv).ToList(), Steps(p24).Select(argv).ToList(), "24 GB: every step's argv is unchanged");
            Assert.Eq(0L, p24.Obj("gpuLimits").Long("maxCallsPerPart"), "24 GB: maxCallsPerPart 0 (not passed)");
            Assert.Eq(0L, p24.Obj("gpuLimits").Long("bufCapMB"), "24 GB: bufCapMB 0 (not passed)");
            // 16 GB: -MaxCallsPerPart n appended after the production argv, nothing else
            var p16 = plan("16384");
            var a16 = Args(Step(p16, "gpu-bake"));
            Assert.Seq(baseArgs, a16.Take(baseArgs.Count).ToList(), "16 GB: the production argv is the prefix");
            Assert.Seq(new[] { "-MaxCallsPerPart" }, a16.Skip(baseArgs.Count).Take(1).ToList(), "16 GB: -MaxCallsPerPart follows");
            long n16 = long.Parse(a16[baseArgs.Count + 1]);
            Assert.True(n16 > 25000000L && n16 < 40000000L, "16 GB rows " + n16);
            Assert.Eq(baseArgs.Count + 2, a16.Count, "16 GB: no -BufCapMB (D3D11 allows 2048 MB)");
            Assert.Eq((long)n16, p16.Obj("gpuLimits").Long("maxCallsPerPart"), "the plan records it");
            Assert.True(p16.Arr("notes").Cast<string>().Any(n => n.Contains("-MaxCallsPerPart")), "a note says why");
            // the tool write-back fallback (the production -WriteBack argv) carries the same sizing
            var fb = Step(plan("16384"), "stage-or-tool-fallback").Obj("params").Arr("fallbackArgs").Cast<string>().ToList();
            Assert.True(fb.Contains("-MaxCallsPerPart") && fb.Contains("-WriteBack"), "fallback argv: " + string.Join(" ", fb));
            // 6 GB: both knobs (D3D11's documented per-resource limit on 6144 MiB is 1536 MiB < 1792)
            var a6 = Args(Step(plan("6144"), "gpu-bake"));
            Assert.Seq(baseArgs, a6.Take(baseArgs.Count).ToList(), "6 GB: the production argv is the prefix");
            Assert.Eq("-BufCapMB", a6[a6.Count - 2], "6 GB: -BufCapMB last");
            Assert.Eq("1536", a6[a6.Count - 1], "6 GB: 1536");
            Assert.True(a6.Contains("-MaxCallsPerPart"), "6 GB: also -MaxCallsPerPart");
            // an integrated GPU (512 MiB dedicated) is not sized
            Assert.Seq(baseArgs, Args(Step(plan("512"), "gpu-bake")), "512 MiB: not sized, unchanged");
            // a real run asks gpucheck: the dry-run switch is refused outside --dry-run
            var l = C.Base("bake"); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--assume-vram-mb", "8192", "--dev-skip-preflight", "--dev-skip-gpucheck" });
            var bad = C.Run(l);
            Assert.True(bad.Exit != 0 && (bad.Out + bad.Err).Contains("only for --dry-run"), "--assume-vram-mb outside --dry-run: " + bad);
        }

        private static void ProbePreflight()
        {
            var r = C.NewRoot();
            string pd = C.BuildPayload(C.Sb.Dir("payload_x1_probes"), true, true);
            Func<int, string[], CliResult> dry = (pts, extra) =>
            {
                var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = pd;
                l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--scenario", Tri, "--bsp", "triplicate", "--probe-points", pts.ToString() });
                l.AddRange(extra);
                return C.Run(l, 120000);
            };
            var ok = dry(11, new string[0]);
            Assert.Eq(0, ok.Exit, "11 points (triplicate) plans: " + ok);
            Assert.True(!ok.Json().Arr("warnings").Cast<string>().Any(w => w.Contains("light probe points")), "no probe warning at 11");
            var edge = dry(281, new string[0]);
            Assert.Eq(0, edge.Exit, "281 points still plans: " + edge);
            Assert.True(edge.Json().Arr("warnings").Cast<string>().Any(w => w.Contains("close to the 281")), "but warns that the largest chart shares the buffer");
            var over = dry(282, new string[0]);
            Assert.Eq(2, over.Exit, "282 points refused before anything: " + over);
            string why = over.Json().Str("refused");
            Assert.Contains(why, "282 light probe points", "reason names the count");
            Assert.Contains(why, "at most 281", "reason names the limit");
            Assert.Contains(why, "--mode lightmap", "reason names the alternative");
            Assert.Eq(2, dry(1024, new[] { "--mode", "probes" }).Exit, "Lightprobes only: 1,024 points refused too");
            Assert.Eq(0, dry(1024, new[] { "--mode", "lightmap" }).Exit, "Lightmap Only bakes no probes: not refused");
        }

        // a free drive letter, or null
        private static string FreeLetter()
        {
            var used = new HashSet<string>(DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 1).ToUpperInvariant()));
            foreach (char c in "YXWVUTSRQPONM") if (!used.Contains(c.ToString())) return c.ToString();
            return null;
        }

        private static string Subst(params string[] args)
        {
            var res = Core.Run.ProcessRunner.RunCapture(Path.Combine(Environment.SystemDirectory, "subst.exe"), args, C.Short.Root, null, 30000);
            return res.Stdout + res.Stderr;
        }

        private static void DriveRoot()
        {
            var r = C.NewRoot();
            string letter = FreeLetter();
            if (letter == null) throw new TestFailure("no free drive letter for the SUBST test");
            string drive = letter + @":\";
            string pdOn = C.BuildPayload(C.Sb.Dir("payload_dr_on"), true, null, false, true);
            string pdOff = C.BuildPayload(C.Sb.Dir("payload_dr_off"), true, null, false, false);
            Subst(letter + ":", r.Root);
            try
            {
                Assert.True(Directory.Exists(Path.Combine(drive, "tags")), "the SUBST drive shows the sandbox H2EK: " + drive);
                Func<string, string[], CliResult> dry = (pd, extra) =>
                {
                    var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = pd;
                    l.AddRange(new[] { "--h2ek", drive, "--dry-run", "--json", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" });
                    l.AddRange(extra);
                    return C.Run(l, 120000);
                };
                // a payload whose gpu_bake lacks the P1b handling (features.driveRoot false / absent): the old refusal, exit 2
                foreach (var pd in new[] { pdOff, C.PayloadDir })
                {
                    var no = dry(pd, new string[0]);
                    Assert.Eq(2, no.Exit, "drive root refused without features.driveRoot: " + no);
                    Assert.Contains(no.Out + no.Err, "root of a drive", "reason");
                }
                // a payload that records it: planned, every path well-formed
                var res = dry(pdOn, new string[0]);
                Assert.Eq(0, res.Exit, "drive root planned with features.driveRoot: " + res);
                var p = res.Json();
                Assert.Eq(drive, p.Str("root"), "root keeps its backslash");
                Assert.Eq(drive + "GPU Lightmap Work", p.Str("work"), "work");
                var e = p.Obj("env");
                Assert.Eq(drive, e.Str("H2LM_ROOT"), "H2LM_ROOT = D:\\ (what P1b accepts)");
                Assert.Eq(drive + "GPU Lightmap Work", e.Str("H2LM_WORK"), "H2LM_WORK");
                var g = Step(p, "gpu-bake");
                Assert.Eq(drive + "GPU Lightmap Work", g.Str("cwd"), "cwd");
                Assert.Contains(string.Join(" ", Args(g)), drive + @"GPU Lightmap Work\_runtime\", "the script is under WORK");
                Assert.True((p.Str("out") ?? "").StartsWith(drive + @"GPU Lightmap Work\farm_w", StringComparison.OrdinalIgnoreCase), "OUT: " + p.Str("out"));
                string all = Core.Util.Json.Write(p, false).Replace(@"\\", @"\");
                // no drive-relative path ("Y:tags") and no doubled separator ("Y:\\tags") anywhere in the plan
                var rel = System.Text.RegularExpressions.Regex.Matches(all, "(?i)\\b" + letter + @":[^\\/:]");
                Assert.Eq(0, rel.Count, "a drive-relative path in the plan: " + (rel.Count > 0 ? all.Substring(Math.Max(0, rel[0].Index - 40), 100) : ""));
                Assert.True(!all.Contains(letter + @":\\"), "a doubled separator after the drive (Y:\\tags)");
                // an ordinary (non-root) H2EK is planned exactly as before: the same argv with and without the feature
                var plain = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
                Assert.Eq(r.Root, plain.Str("root"), "non-root unchanged");
                // doctor on the drive root: refused without the feature (the old message), accepted with it
                var dl = C.Base("doctor", false); dl[dl.IndexOf("--payload-dir") + 1] = pdOff; dl.AddRange(new[] { "--h2ek", drive, "--no-gpu", "--no-extract", "--json" });
                var dno = C.Run(dl);
                Assert.Eq(2, dno.Exit, "doctor: " + dno);
                // restore / cancel never start gpu_bake: a drive root is fine for them (nothing to restore here, but no root refusal)
                var sl = C.Base("restore", false); sl.AddRange(new[] { "--h2ek", drive, "--scenario", Cyc, "--bsp", "cyclotron", "--list" });
                var sr = C.Run(sl);
                Assert.True(!(sr.Out + sr.Err).Contains("root of a drive"), "restore --list at a drive root is not refused for being a drive root: " + sr);
            }
            finally
            {
                Subst(letter + ":", "/d");
            }
            Assert.True(!Directory.Exists(drive), "the SUBST drive " + drive + " was removed");
        }

        // ------------------------------------------------------------------ helpers

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
        private static string Inst(TestRoot r) { return Path.Combine(r.Work, @"_runtime\0.5.0-s25cccc"); }

        private static List<string> Pw(TestRoot r, string engineRel, params string[] a)
        {
            var l = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Inst(r), engineRel) };
            l.AddRange(a);
            return l;
        }

        // ------------------------------------------------------------------ tests

        private static void LightmapSuper()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            // U3: stage-only bake, then the SHELL installs the direct writer's stage with [4b]'s command (Retry); the tool
            // write-back fallback (the production -WriteBack argv) only when there is no stage
            Assert.Seq(new[] { "backup", "gpu-bake", "stage-or-tool-fallback", "install", "verify" }, Ids(p), "steps");
            Assert.Seq(new[] { "Preflight", "Backup", "Capture", "Derive", "GpuBake", "TagWriter", "Install", "Verify" }, p.Arr("uiSteps").Cast<string>().ToList(), "UI steps");
            var g = Step(p, "gpu-bake");
            Assert.Eq(Path.Combine(C.PwshDir, "pwsh.exe"), g.Str("exe"), "exe = rt pwsh");
            Assert.Eq(r.Work, g.Str("cwd"), "cwd = WORK (run_bake's Set-Location)");
            Assert.Seq(Pw(r, @"engine_v373\gpu_bake.ps1", "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct"),
                       Args(g), "the production argv without -WriteBack (stage only)");
            Assert.Eq(true, g.Obj("params").Bool("shellInstalls"), "the shell installs its stage");
            Assert.Eq(true, g.Obj("capture").Bool("fresh"), "a new capture");
            Assert.Eq(200L, p.Long("worker"), "worker 200");
            Assert.Eq("would be allocated", p.Str("workerNote"), "peeked, not allocated");
            Assert.Eq(Path.Combine(r.Farm(200), "writeback_in_progress.txt"), p.Str("marker"), "marker in the farm");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage"), p.Str("installStage"), "the shell installs OUT\\tagw_stage");
            Assert.Eq(Path.Combine(r.Farm(200), "tag_backup_before_writeback"), p.Str("installBackupDir"), "[4b]'s backup dir");
            var f = Step(p, "stage-or-tool-fallback");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage", "tagw_report.json"), f.Obj("params").Str("report"), "the stage report");
            Assert.Seq(new[] { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SkipCapture", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       f.Obj("params").Arr("fallbackArgs").Cast<string>().ToList(), "fallback = the production -WriteBack argv on this capture");
            var ins = Step(p, "install");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"engine_v373\v043-tagwriter\lmw_install.py"), "install", "--stage", Path.Combine(r.Farm(200), "tagw_stage"), "--target-dir", r.CycDir,
                               "--backup-dir", Path.Combine(r.Farm(200), "tag_backup_before_writeback"), "--marker", Path.Combine(r.Farm(200), "writeback_in_progress.txt"),
                               "--auto-backup-root", Path.Combine(r.Work, "_TAGBAK_auto") }, Args(ins), "lmw_install argv = [4b]'s (argparse: same values)");
            Assert.Eq(Path.Combine(C.PyDir, "py.exe"), ins.Str("exe"), "the bundle py (= gpu_bake's `& py`)");
            Assert.Eq(true, ins.Bool("tagWrite"), "install is a tag write");
            var e = p.Obj("env");
            Assert.Eq(r.Root, e.Str("H2LM_ROOT"), "H2LM_ROOT");
            Assert.Eq(r.Work, e.Str("H2LM_WORK"), "H2LM_WORK (the real path)");
            Assert.Eq(Inst(r), e.Str("H2LM_CODE"), "H2LM_CODE = INST");
            Assert.Eq("1", e.Str("H2LM_NOWINDOW"), "NOWINDOW");
            Assert.True(e.Str("PATH").StartsWith(C.PyDir + ";" + C.PwshDir + ";", StringComparison.OrdinalIgnoreCase), "PATH = py; pwsh; system: " + e.Str("PATH"));
            Assert.Eq(Path.Combine(C.PwshDir, "Modules"), e.Str("PSModulePath"), "PSModulePath isolated");
            Assert.Eq("Bypass", e.Str("PSExecutionPolicyPreference"), "policy");
            Assert.Eq("utf-8", e.Str("PYTHONIOENCODING"), "PYTHONIOENCODING");
            Assert.Eq("1", e.Str("PYTHONDONTWRITEBYTECODE"), "no __pycache__ in INST");
            Assert.Eq("1", e.Str("NO_COLOR"), "NO_COLOR");
            var leaked = e.Keys.Where(k => k.StartsWith("GPULM_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("OSOYOOS_", StringComparison.OrdinalIgnoreCase)
                                           || k.Equals("PYTHONPATH", StringComparison.OrdinalIgnoreCase) || k.Equals("PYTHONHOME", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Eq(0, leaked.Count, "leaked pipeline variables: " + string.Join(", ", leaked));
            Assert.Contains(p.Str("runDir"), Path.Combine(r.Work, @"standalone\runs\"), "run dir under WORK\\standalone\\runs");
            Assert.Eq(true, p.Bool("runDirPredicted"), "predicted, not created");
        }

        private static void LowStageOnly()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--no-install");
            Assert.Seq(new[] { "gpu-bake", "stage-check" }, Ids(p), "steps (R1: the stage-only result is checked)");
            Assert.Seq(Pw(r, @"engine_v373\gpu_bake.ps1", "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "low", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct"),
                       Args(Step(p, "gpu-bake")), "stage-only argv (no -WriteBack)");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage", "tagw_report.json"), Step(p, "stage-check").Obj("params").Str("report"), "stage-check reads OUT\\tagw_stage\\tagw_report.json");
            Assert.Eq(false, p.Bool("install"), "no install");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage"), p.Str("finalStage"), "final stage = OUT\\tagw_stage");
        }

        private static void LightmapAo()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao");
            Assert.Seq(new[] { "backup", "gpu-bake", "stage-ready", "ao-trace", "ao-apply", "ao-stage", "install", "verify" }, Ids(p), "steps");
            Assert.NotContains(string.Join(" ", Args(Step(p, "gpu-bake"))), "-WriteBack ", "the bake is stage-only");
            string drv = Path.Combine(Inst(r), @"_research\ao\gpu_ao.py"), eng = Path.Combine(Inst(r), @"_research\ao\dev_AO"), farm = r.Farm(200);
            var common = new[] { "--farm", farm, "--worker", "200", "--scenario", Cyc, "--bsp", "cyclotron", "--engine", eng };
            Assert.Seq(new[] { drv, "trace" }.Concat(common).Concat(new[] { "--quality", "super", "--plucker-fix", "--scenery-fix" }).ToList(), Args(Step(p, "ao-trace")), "trace argv");
            Assert.Seq(new[] { drv, "apply" }.Concat(common).Concat(new[] { "--tag", "s10e05", "--strength", "1.0", "--exponent", "0.5", "--overwrite" }).ToList(), Args(Step(p, "ao-apply")), "apply argv");
            Assert.Seq(new[] { drv, "stage" }.Concat(common).Concat(new[] { "--tag", "s10e05", "--overwrite" }).ToList(), Args(Step(p, "ao-stage")), "stage argv");
            foreach (var id in new[] { "ao-trace", "ao-apply", "ao-stage" })
            {
                Assert.Eq(Path.Combine(C.PyDir, "py.exe"), Step(p, id).Str("exe"), id + " exe = bundle py");
                Assert.Eq(r.Work, Step(p, id).Str("cwd"), id + " cwd");
            }
            string stage = Path.Combine(farm, @"ao\s10e05\tagw_stage");
            var ins = Step(p, "install");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"engine_v373\v043-tagwriter\lmw_install.py"), "install", "--stage", stage, "--target-dir", r.CycDir,
                               "--backup-dir", Path.Combine(p.Str("runDir"), "tagbak"), "--marker", Path.Combine(farm, "writeback_in_progress.txt"),
                               "--auto-backup-root", Path.Combine(r.Work, "_TAGBAK_auto") }, Args(ins), "lmw_install argv");
            Assert.Eq(true, ins.Bool("tagWrite"), "install is a tag write");
        }

        private static void AoLow()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--ao", "--ao-strength", "0.75", "--ao-exponent", "2");
            var tr = Args(Step(p, "ao-trace"));
            Assert.Eq("low", tr[tr.IndexOf("--quality") + 1], "low traces at low (P6)");
            var ap = Args(Step(p, "ao-apply"));
            Assert.Eq("s075e20", ap[ap.IndexOf("--tag") + 1], "tag");
            Assert.Eq("0.75", ap[ap.IndexOf("--strength") + 1], "strength");
            Assert.Eq("2.0", ap[ap.IndexOf("--exponent") + 1], "exponent");
        }

        private static void StandardNoProbes()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--probe-points", "0");
            Assert.Seq(new[] { "map-query", "backup", "gpu-bake", "stage-or-tool-fallback", "install", "verify" }, Ids(p), "steps (U3: the shell installs)");
            Assert.Eq(false, p.Bool("probePath"), "no probes");
            Assert.Eq(null, p["probeWorker"], "no probe worker");
            Assert.True(!Args(Step(p, "gpu-bake")).Contains("-WriteBack"), "stage-only bake (U3)");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage"), p.Str("installStage"), "the shell installs the direct writer's stage");
            var mq = Step(p, "map-query");
            Assert.Eq(Path.Combine(C.PyDir, "py.exe"), mq.Str("exe"), "map query by the bundle py");
            var ma = Args(mq);
            Assert.Seq(new[] { "-B", "-c" }, ma.Take(2).ToList(), "py -B -c CODE");
            Assert.Seq(new[] { Path.Combine(Inst(r), "v043-tagwriter"), Path.Combine(r.CycDir, "cyclotron.scenario"), "cyclotron", r.CycLtmp }, ma.Skip(3).ToList(), "map query args");
            Assert.Eq(true, p.Bool("probePointsAssumed"), "assumed in a dry run");
        }

        private static void StandardProbes()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--dev-probes");
            Assert.Seq(new[] { "map-query", "backup", "gpu-bake", "stage-ready", "copy-final-stage", "probe-bake", "sh-merge", "validate-final", "install", "verify" }, Ids(p), "steps");
            Assert.Seq(new[] { "Preflight", "Backup", "Capture", "Derive", "GpuBake", "TagWriter", "ProbeCapture", "Probes", "Install", "Verify" },
                       p.Arr("uiSteps").Cast<string>().ToList(), "the UI step list (AO skipped)");
            Assert.Eq(200L, p.Long("worker"), "lightmap worker");
            Assert.Eq(201L, p.Long("probeWorker"), "probe worker (its own capture)");
            Assert.NotContains(string.Join(" ", Args(Step(p, "gpu-bake"))), "-WriteBack ", "lightmap stage-only");
            string run = p.Str("runDir"), ltmp = "triplicate_triplicate_lightmap.scenario_structure_lightmap";
            var pb = Step(p, "probe-bake");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"_research\lightprobes\gpu_lightprobes.py"), "--scenario", Tri, "--bsp", "triplicate", "--worker", "201", "--quality", "super",
                               "--stage", Path.Combine(Inst(r), "engine_v373"), "--ltmp-src", Path.Combine(run, "final_stage", ltmp), "--out", Path.Combine(run, "probes"),
                               "--h2-root", r.Root, "--farm", r.Farm(201), "--pwsh", Path.Combine(C.PwshDir, "pwsh.exe"), "--plucker-fix", "--scenery-fix" }, Args(pb), "gpu_lightprobes argv (P5)");
            Assert.Eq(true, pb.Obj("capture").Bool("skyHi"), "probe capture is SKY_HI");
            Assert.Eq(Path.Combine(run, @"probes\gpu_bake.log"), pb.Str("sideLog"), "the shell tails its gpu_bake.log");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"standalone\sh_stage.py"), "merge", "--stage", Path.Combine(run, "final_stage"), "--sh", Path.Combine(run, "probes", ltmp),
                               "--probes", Path.Combine(run, @"probes\probes.bin"), "--h2-root", r.Root }, Args(Step(p, "sh-merge")), "sh_stage merge argv");
            var cp = Step(p, "copy-final-stage").Obj("params");
            Assert.Eq(Path.Combine(r.Farm(200), "tagw_stage"), cp.Str("from"), "from the bake's stage");
            // review fix (finding 21): the merged stage passes lmw_validate --check-set before the install
            Assert.Seq(new[] { Path.Combine(Inst(r), @"engine_v373\v043-tagwriter\lmw_validate.py"), "--check-set", Path.Combine(run, "final_stage"),
                               "--bsp", Path.Combine(r.TriDir, "triplicate.scenario_structure_bsp"), "--json", Path.Combine(run, "final_stage_validate.json") }, Args(Step(p, "validate-final")), "lmw_validate argv");
            var ins = Args(Step(p, "install"));
            Assert.Eq(Path.Combine(run, "final_stage"), ins[ins.IndexOf("--stage") + 1], "ONE install of RUN\\final_stage");
            Assert.Eq(Path.Combine(r.Farm(200), "writeback_in_progress.txt"), ins[ins.IndexOf("--marker") + 1], "marker in farm N");
        }

        private static void StandardAoProbes()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--ao", "--dev-probes");
            Assert.Seq(new[] { "map-query", "backup", "gpu-bake", "stage-ready", "ao-trace", "ao-apply", "ao-stage", "copy-final-stage", "probe-bake", "sh-merge", "validate-final", "install", "verify" }, Ids(p), "steps");
            Assert.Eq(Path.Combine(r.Farm(200), @"ao\s10e05\tagw_stage"), Step(p, "copy-final-stage").Obj("params").Str("from"), "AO stage is final");
        }

        private static void ProbesOnly()
        {
            var r = C.NewRoot();
            var p = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "probes", "--dev-probes");
            Assert.Seq(new[] { "map-query", "backup", "copy-live-ltmp", "probe-bake", "sh-single", "assemble-check-set", "validate-final", "check-live-ltmp", "install", "verify" }, Ids(p), "steps");
            var asm = Step(p, "assemble-check-set").Obj("params");
            Assert.Eq(Path.Combine(r.TriDir, "triplicate_triplicate_lightmap_truecolor_bitmaps.bitmap"), asm.Str("truecolor"), "the live truecolor is copied beside the probe ltmp");
            Assert.Eq(Path.Combine(p.Str("runDir"), "probe_stage_check"), Args(Step(p, "validate-final"))[2], "check-set on the assembled set");
            Assert.Eq("LtmpOnly", Step(p, "backup").Obj("params").Str("kind"), "ltmp-only backup");
            Assert.Eq(null, p["worker"], "no lightmap worker");
            Assert.Eq(200L, p.Long("probeWorker"), "probe worker");
            string run = p.Str("runDir"), ltmp = "triplicate_triplicate_lightmap.scenario_structure_lightmap";
            var pb = Args(Step(p, "probe-bake"));
            Assert.Eq(Path.Combine(run, "live_ltmp", ltmp), pb[pb.IndexOf("--ltmp-src") + 1], "--ltmp-src = the copy of the live ltmp");
            Assert.Seq(new[] { Path.Combine(Inst(r), @"standalone\sh_stage.py"), "single", "--stage", Path.Combine(run, "probe_stage"), "--src", Path.Combine(run, "live_ltmp", ltmp),
                               "--sh", Path.Combine(run, "probes", ltmp), "--probes", Path.Combine(run, @"probes\probes.bin"), "--h2-root", r.Root, "--name", ltmp }, Args(Step(p, "sh-single")), "single argv");
            var ins = Args(Step(p, "install"));
            Assert.Eq(Path.Combine(run, "probe_stage"), ins[ins.IndexOf("--stage") + 1], "ltmp-only install");
            Assert.Eq(Path.Combine(r.Farm(200), "writeback_in_progress.txt"), ins[ins.IndexOf("--marker") + 1], "marker in the probe farm (recovery scans 200-299)");
        }

        private static void Custom()
        {
            var r = C.NewRoot();
            // E1 (0.6.1): the guard is the only thing that can refuse Custom now. This payload has no engine_custom (the N3 guard):
            // refused with the guard's own message, whether or not the old (now ignored) --dev-custom switch is given
            var off = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--quality", "custom");
            Assert.Eq(2, off.Exit, "refused without the guarded engine: " + off);
            Assert.Contains(off.Json().Str("refused"), "N3 guarded fullgpu", "reason");
            var ung = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom", "--dev-custom");
            Assert.Eq(2, ung.Exit, "--dev-custom is accepted but does not lift the guard: " + ung);
            Assert.Contains(ung.Json().Str("refused"), "N3 guarded fullgpu", "reason");
            // a payload WITH engine_custom offers Custom with no switch at all; the plan runs engine_custom and records guarded=true
            string withGuard = C.BuildPayload(C.Sb.Dir("payload_guarded"), customEngine: true);
            var gl = C.Base("bake"); gl[gl.IndexOf("--payload-dir") + 1] = withGuard;
            gl.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom" });
            var gres = C.Run(gl, 120000);
            Assert.Eq(0, gres.Exit, "Custom needs no dev switch when the payload carries engine_custom: " + gres);
            var gp = gres.Json();
            Assert.Contains(string.Join("|", Args(Step(gp, "gpu-bake"))), Path.Combine(r.Work, @"_runtime\" + Ctx.GuardedInstDir, @"engine_custom\gpu_bake.ps1"), "the guarded Custom engine is the one that runs");
            Assert.Eq(true, gp.Obj("options").Bool("guarded"), "the plan records guarded=true");
            Assert.NotContains(string.Join("|", gp.Arr("warnings").Cast<string>()), "UNGUARDED", "no unguarded warning");
            var ga = Args(Step(gp, "gpu-bake"));
            Assert.Seq(new[] { "-Quality", "custom", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "50000000", "-MCSamples", "16" }, ga.Skip(ga.IndexOf("-Quality")).ToList(), "guarded custom tail (defaults 50M / 16)");
            var gr = new List<string>(gl); gr.AddRange(new[] { "--photons", "100000001" });
            var grr = C.Run(gr, 120000);
            Assert.Eq(2, grr.Exit, "the ranges still hold on the guarded engine: " + grr);
            Assert.Contains(grr.Out + grr.Err, "photons must be", "photons range");
            var p = Plan(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom", "--dev-custom-unguarded");
            var a = Args(Step(p, "gpu-bake"));
            Assert.Seq(new[] { "-Quality", "custom", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "50000000", "-MCSamples", "16" }, a.Skip(a.IndexOf("-Quality")).ToList(), "custom tail (defaults 50M / 16), stage only");
            // U3 (b): the tool write-back fallback argv carries the custom row too (gpu_bake P2 -> osoy_launch --quality-row P MC)
            var fb = Step(p, "stage-or-tool-fallback").Obj("params").Arr("fallbackArgs").Cast<string>().ToList();
            Assert.Seq(new[] { "-Quality", "custom", "-WriteBack", "-SkipCapture", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "50000000", "-MCSamples", "16" },
                       fb.Skip(fb.IndexOf("-Quality")).ToList(), "the fallback gets -Photons / -MCSamples");
            Assert.Contains(Step(p, "gpu-bake").Obj("params").Str("ifDirectWriterCannotRun"), "-Quality custom -WriteBack -SceneryFix -PluckerFix -WriteBackMode direct -Photons 50000000 -MCSamples 16",
                            "the pre-flight fallback argv carries the custom row too");
            Assert.Contains(string.Join("|", p.Arr("warnings").Cast<string>()), "UNGUARDED", "no guarded engine in this payload: warned");
            Assert.Eq(false, p.Obj("options").Bool("guarded"), "the plan records guarded=false (T3 evidence cannot pass for guarded)");
            var q = Plan(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--quality", "custom", "--photons", "100,000,000", "--rays", "64", "--dev-custom-unguarded", "--dev-probes");
            var pb = Args(Step(q, "probe-bake"));
            Assert.Seq(new[] { "--quality", "custom", "--photons", "100000000", "--mc-samples", "64" }, pb.Skip(pb.IndexOf("--quality")).Take(6).ToList(), "probes get P / MC");
            var g = Args(Step(q, "gpu-bake"));
            Assert.Seq(new[] { "-Photons", "100000000", "-MCSamples", "64" }, g.Skip(g.Count - 4).ToList(), "100M / 64");
        }

        private static void Refused(TestRoot r, string what, string needle, params string[] a)
        {
            var res = C.DryJson(r, a);
            Assert.Eq(2, res.Exit, what + ": " + res);
            Assert.Contains(res.Out + res.Err, needle, what);
        }

        private static void Refusals()
        {
            var r = C.NewRoot();
            Refused(r, "AO in probes mode", "Lightprobes", "--scenario", Tri, "--bsp", "triplicate", "--mode", "probes", "--ao");
            Refused(r, "rays 65", "gather rays", "--scenario", Cyc, "--bsp", "cyclotron", "--quality", "custom", "--rays", "65", "--dev-custom-unguarded");
            Refused(r, "photons 999,999", "photons must be", "--scenario", Cyc, "--bsp", "cyclotron", "--quality", "custom", "--photons", "999999", "--dev-custom-unguarded");
            Refused(r, "photons without custom", "only valid with --quality custom", "--scenario", Cyc, "--bsp", "cyclotron", "--photons", "50000000");
            Refused(r, "worker 149", "149", "--scenario", Cyc, "--bsp", "cyclotron", "--worker", "149");
            Refused(r, "worker 60", "200-299", "--scenario", Cyc, "--bsp", "cyclotron", "--worker", "60");
            Refused(r, "bad bsp", "this scenario's folder has: cyclotron", "--scenario", Cyc, "--bsp", "nope");
            Refused(r, "bad scenario", "no scenario tag", "--scenario", @"scenarios\multi\nope\nope", "--bsp", "nope");
            Refused(r, "missing --bsp", "--bsp B is required", "--scenario", Cyc);
            Refused(r, "unknown option", "unknown option --frobnicate", "--scenario", Cyc, "--bsp", "cyclotron", "--frobnicate");
            Refused(r, "AO strength 2.5", "AO strength", "--scenario", Cyc, "--bsp", "cyclotron", "--ao", "--ao-strength", "2.5");
            var l = C.Base("bake"); l.AddRange(new[] { "--h2ek", Path.Combine(C.Short.Root, "no_such_h2ek"), "--scenario", Cyc, "--bsp", "cyclotron", "--dry-run" });
            var nr = C.Run(l);
            Assert.Eq(2, nr.Exit, "missing root refused: " + nr);
            // review fix (finding 14): the light-probe pipeline is gated on T4 -- never a silent downgrade
            Refused(r, "Lightprobes without the T4 flag", "not released yet", "--scenario", Tri, "--bsp", "triplicate", "--mode", "probes", "--probe-points", "11");
            Refused(r, "Standard on a probe BSP without the T4 flag", "use Lightmap Only", "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11");
            Assert.Eq(0, C.DryJson(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--mode", "lightmap").Exit, "Lightmap Only on the probe BSP is allowed");
            Assert.Eq(0, C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--probe-points", "0").Exit, "Standard on a BSP without probe points is allowed");
            // review fix (finding 23): a drive-root H2EK is refused before anything
            var dl = C.Base("bake"); dl.AddRange(new[] { "--h2ek", Path.GetPathRoot(C.Short.Root), "--scenario", Cyc, "--bsp", "cyclotron", "--dry-run" });
            var dr = C.Run(dl);
            Assert.Eq(2, dr.Exit, "drive root refused: " + dr);
            Assert.Contains(dr.Out + dr.Err, "root of a drive", "reason");
            // review fix (finding 15): AO only from a payload that proved its AO engine (features.ao)
            foreach (var feat in new bool?[] { false, null })
            {
                string pd = C.BuildPayload(C.Sb.Dir("payload_ao_" + (feat.HasValue ? "off" : "none")), feat);
                var al = C.Base("bake"); al[al.IndexOf("--payload-dir") + 1] = pd;
                al.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao" });
                var ar = C.Run(al);
                Assert.Eq(2, ar.Exit, "AO refused (features.ao " + (feat.HasValue ? "false" : "absent") + "): " + ar);
                Assert.Contains(ar.Json().Str("refused"), feat.HasValue ? "AO is disabled in this build" : "does not record the AO engine check", "reason");
                al.Remove("--ao");
                Assert.Eq(0, C.Run(al).Exit, "the same payload bakes without AO");
            }
            // V5 (gate T4): light probes from a payload whose T4 proof covers its engine (MANIFEST features.probes) -- no dev flag
            {
                string pd = C.BuildPayload(C.Sb.Dir("payload_probes_on"), true, true);
                Func<string[], CliResult> dry = extra =>
                {
                    var pl = C.Base("bake"); pl[pl.IndexOf("--payload-dir") + 1] = pd;
                    pl.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11" });
                    pl.AddRange(extra);
                    return C.Run(pl);
                };
                var st = dry(new string[0]);
                Assert.Eq(0, st.Exit, "Standard on the probe BSP plans with features.probes enabled, without --dev-probes: " + st);
                Assert.Eq(true, st.Json().Bool("probePath"), "the probe path is planned");
                Assert.Seq(new[] { "map-query", "backup", "gpu-bake", "stage-ready", "copy-final-stage", "probe-bake", "sh-merge", "validate-final", "install", "verify" },
                           Ids(st.Json()), "Standard steps (the same plan as with --dev-probes)");
                var po = dry(new[] { "--mode", "probes" });
                Assert.Eq(0, po.Exit, "Lightprobes only plans with features.probes enabled: " + po);
                Assert.Seq(new[] { "map-query", "backup", "copy-live-ltmp", "probe-bake", "sh-single", "assemble-check-set", "validate-final", "check-live-ltmp", "install", "verify" },
                           Ids(po.Json()), "Lightprobes-only steps");
            }
            foreach (var feat in new bool?[] { false, null })
            {
                string pd = C.BuildPayload(C.Sb.Dir("payload_probes_" + (feat.HasValue ? "off" : "none")), true, feat);
                var pl = C.Base("bake"); pl[pl.IndexOf("--payload-dir") + 1] = pd;
                pl.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11" });
                var pr = C.Run(pl);
                Assert.Eq(2, pr.Exit, "Standard on the probe BSP refused (features.probes " + (feat.HasValue ? "false" : "absent") + "): " + pr);
                Assert.Contains(pr.Json().Str("refused"), "light probes are not released yet in this build", "reason");
                Assert.Contains(pr.Json().Str("refused"), feat.HasValue ? "light probes are disabled in this build" : "does not record the light-probe gate T4", "the build's own reason");
                pl.AddRange(new[] { "--dev-probes" });
                Assert.Eq(0, C.Run(pl).Exit, "the dev flag still overrides");
            }
        }

        /// <summary>Review fixes 18 + 22: the plan prints only the shell's own variables (names of the rest), and the scenario /
        /// BSP in their on-disk case.</summary>
        private static void PrivacyAndCase()
        {
            var r = C.NewRoot();
            var res = C.DryJson(r, "--scenario", @"Scenarios\Multi\Cyclotron\CYCLOTRON", "--bsp", "Cyclotron", "--mode", "lightmap");
            Assert.Eq(0, res.Exit, "mixed-case dry run: " + res);
            Assert.NotContains(res.Out + res.Err, Ctx.SecretValue, "the planted secret's VALUE never appears in the plan");
            var p = res.Json();
            Assert.True(p.Obj("env").Str(Ctx.SecretName) == null, "env{} holds only the shell's own variables");
            Assert.True(p.Arr("envInherited").Cast<string>().Contains(Ctx.SecretName), "inherited variables are listed by NAME");
            Assert.True(p.Obj("env").Keys.All(H2Lm.Proc.ChildEnvironment.IsLoggable), "every printed variable is a loggable one: " + string.Join(",", p.Obj("env").Keys));
            Assert.Eq(Cyc, p.Obj("options").Str("scenario"), "scenario in its on-disk case");
            Assert.Eq("cyclotron", p.Obj("options").Str("bsp"), "BSP in its on-disk case");
            var a = Args(Step(p, "gpu-bake"));
            Assert.Eq(Cyc, a[a.IndexOf("-Scenario") + 1], "gpu_bake gets the on-disk case (the lightmap name / tag reference it writes)");
            Assert.Eq("cyclotron", a[a.IndexOf("-Bsp") + 1], "-Bsp too");
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" });
            var t = C.Run(l);
            Assert.Eq(0, t.Exit, "text dry run");
            Assert.NotContains(t.Out + t.Err, Ctx.SecretValue, "text mode never prints the secret either");
            Assert.Contains(t.Out, "values not logged", "the inherited ones are named only");
        }

        private static List<string> Snapshot(string dir)
        {
            if (!Directory.Exists(dir)) return new List<string> { "(absent)" };
            return Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => x + "|" + (File.Exists(x) ? new FileInfo(x).Length + "|" + File.GetLastWriteTimeUtc(x).Ticks : "dir")).ToList();
        }

        private static void ReadOnlyAndReuse()
        {
            var r = C.NewRoot();
            var before = Snapshot(r.Root);
            var lad0 = Snapshot(C.Lad);
            var res = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--reuse-capture");
            Assert.Eq(2, res.Exit, "reuse without any capture refused: " + res);
            Assert.Contains(res.Json().Str("refused"), "has no capture yet", "reason");
            Directory.CreateDirectory(r.Farm(200));
            File.WriteAllText(Path.Combine(r.Farm(200), "capture_id.json"), "{\"soup_crc32\": \"12345678\"}");
            before = Snapshot(r.Root);
            res = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--reuse-capture");
            Assert.Eq(2, res.Exit, "the registry skips an existing (legacy) farm dir: " + res);
            Assert.Contains(res.Json().Str("refused"), "farm_w201 has no capture yet", "a legacy farm_w200 is never allocated");
            res = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--reuse-capture", "--worker", "200");
            Assert.Eq(2, res.Exit, "a legacy farm without a record is refused: " + res);
            Assert.Contains(res.Json().Str("refused"), "no standalone capture record", "reason");
            foreach (var combo in new[] { new[] { "--mode", "lightmap" }, new[] { "--ao" }, new[] { "--probe-points", "11", "--dev-probes" }, new[] { "--mode", "probes", "--probe-points", "11", "--dev-probes" } })
            {
                var a = new List<string> { "--scenario", combo.Contains("11") ? Tri : Cyc, "--bsp", combo.Contains("11") ? "triplicate" : "cyclotron" };
                a.AddRange(combo);
                var x = C.DryJson(r, a.ToArray());
                Assert.Eq(0, x.Exit, "dry " + string.Join(" ", combo) + ": " + x);
            }
            Assert.Seq(before, Snapshot(r.Root), "the H2EK tree is unchanged by dry runs");
            Assert.Seq(lad0, Snapshot(C.Lad), "LOCALAPPDATA unchanged (no runtime extraction)");
            Assert.True(!Directory.Exists(Path.Combine(r.Work, "standalone")) && !Directory.Exists(Path.Combine(r.Work, "_runtime")), "no standalone\\ or _runtime\\ created");
        }

        private static void TextMode()
        {
            var r = C.NewRoot();
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--ao", "--dev-probes" });
            var res = C.Run(l);
            Assert.Eq(0, res.Exit, "text dry run: " + res);
            foreach (var s in new[] { "DRY RUN -- nothing is started", "ENV (every child process", "H2LM_CODE=", "STEP  1  process map-query", "STEP 13  shell   verify",
                                      "gpu_lightprobes.py", "--ltmp-src", "ASSUMED", "capture farm_w201 NEW (GPULM_SKY_HI)" })
                Assert.Contains(res.Out, s, "text output");
        }

        private static void RealMine()
        {
            if (!Directory.Exists(C.MineWork)) throw new TestSkip("no ProdRoot");
            var top = Directory.GetFileSystemEntries(C.MineWork).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            bool hadStandalone = Directory.Exists(Path.Combine(C.MineWork, "standalone")), hadRuntime = Directory.Exists(Path.Combine(C.MineWork, "_runtime"));
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", C.MineRoot, "--dry-run", "--json", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" });
            var res = C.Run(l);
            Assert.Eq(0, res.Exit, "dry run on ProdRoot: " + res);
            var p = res.Json();
            Assert.Eq(C.MineWork + "\\ (planned)", p.Str("slotBase"), "Direct slot = WORK\\ (no alias on ProdRoot)");
            var w = p.Long("worker").Value;
            Assert.True(w >= 200 && w <= 299 && w != 149, "worker in range: " + w);
            Assert.True(!Directory.Exists(Path.Combine(C.MineWork, "farm_w" + w)), "a worker without a farm dir");
            Assert.Seq(top, Directory.GetFileSystemEntries(C.MineWork).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), "ProdRoot WORK top level unchanged");
            Assert.Eq(hadStandalone, Directory.Exists(Path.Combine(C.MineWork, "standalone")), "no standalone\\ created");
            Assert.Eq(hadRuntime, Directory.Exists(Path.Combine(C.MineWork, "_runtime")), "no _runtime\\ created");
        }
    }
}
