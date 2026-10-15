using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                               "--backup-dir", Path.Combine(p.Str("runDir"), "tag_backup_before_writeback"), "--marker", Path.Combine(farm, "writeback_in_progress.txt"),
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
