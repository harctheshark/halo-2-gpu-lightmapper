// S3.1-S3.4 option -> pipeline mapping (FINAL_DESIGN section 5). Pure: builds the step list from the options and the resolved
// context; starts nothing, writes nothing. The same plan is printed by --dry-run and executed by BakeSession.
//
//   Lightmap Only, AO off     gpu_bake -Scenario S -Bsp B -Worker N -Quality Q [-SkipCapture] -SceneryFix -PluckerFix
//                             -WriteBackMode direct   = the production run_bake.ps1 argv WITHOUT -WriteBack (stage only).
//                             Stage-only (R1): a 'written' tagw_report.json made by this run is required (stage-check), else
//                             the run FAILS. Install (U3, was -WriteBack = gpu_bake's own [4b] install, which had no Retry):
//                             stage-or-tool-fallback = that same 'written' stage -> the SHELL installs OUT\tagw_stage with the
//                             command [4b] runs (the engine's own lmw_install.py, --backup-dir OUT\tag_backup_before_writeback,
//                             --auto-backup-root WORK\_TAGBAK_auto, --marker OUT\writeback_in_progress.txt), with Retry; no
//                             stage -> the production tool write-back fallback: the -WriteBack argv with -SkipCapture on this
//                             capture ([4a] again, then the tool's [4], as production falls back). A gpu_bake that refuses
//                             the stage-only bake because the direct writer cannot run at all -> the full -WriteBack argv.
//   Lightmap Only, AO on      gpu_bake stage-only -> OUT\skeleton + a 'written' tagw_report.json (else: the map fell back to the
//                             tool writer, AO needs the direct writer) -> gpu_ao trace (quality low->low, super/custom->super) ->
//                             apply --strength X --exponent Y --overwrite -> stage -> the shell installs OUT\ao\sXeY\tagw_stage.
//   AO only (0.6.8 xc8)       no lighting is baked: the lightmap that is already installed gets ambient occlusion added.
//                             [gpu_bake -CaptureOnly when the capture is new: phase 1 only] -> gpu_ao trace (the same AO trace) -> ao_existing.py stage
//                             (decodes the installed set, applies the full bake's occlusion to its pixels, regenerates P8, validates) -> OUToonly\sXeY\stage
//                             -> copy-final-stage -> install (backup first) -> verify. Refuses when the set is missing / does not match the BSP / already has AO.
//   Standard, no probe points identical to Lightmap Only.
//   Standard, probe points    lightmap stage-only in worker N (+ optional AO) -> copy the final stage to RUN\final_stage -> probe
//                             worker M: gpu_lightprobes (its own GPULM_SKY_HI capture, or a probe-ready reuse) --ltmp-src
//                             RUN\final_stage\<ltmp> -> sh_stage merge -> ONE lmw_install of RUN\final_stage.
//   Lightprobes only          ltmp-only backup -> copy of the live ltmp -> probe worker M: gpu_lightprobes -> sh_stage single ->
//                             live-ltmp-unchanged check -> ltmp-only install.
//   Custom P / MC             gpu_bake -Quality custom -Photons P -MCSamples MC (P2: the osoy_launch 'cuban' row on every custom
//                             tool run, incl. the tool write-back fallback -- U3: every fallback argv the shell builds carries
//                             -Photons / -MCSamples too); gpu_lightprobes --quality custom --photons --mc-samples.
//   Install                   py lmw_install.py install --stage RUN\final_stage --target-dir <tagdir> --backup-dir RUN\tagbak
//                             --marker WORK\standalone\markers\install_wNN.json (0.6.8 bounded temp: every mode installs from the KEEP copy of
//                             the final stage, the backup and the marker are in KEEP, never in the farm, which lives in WORK\Temp (safe to delete)
//                             and may be deleted at any moment; _TAGBAK_auto is retired in the shell's install -- no --auto-backup-root).
//   Farms                     0.6.8: OUT = <farm root>\farm_wNN, the farm root = WORK\Temp (safe to delete) when the payload's engine takes
//                             H2LM_FARMS (MANIFEST features.farms), else WORK (the 0.6.7 layout).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2Lm.Proc;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class PlanInput
    {
        public BakeOptions O;
        public string AppVersion, PayloadSource;
        public PayloadManifest Manifest;
        public ResolvedRuntime Rt;
        public ChildEnvironment Env;
        public string RunDir;
        /// <summary>Milestone 3 (.map bake): the overlay work folder (H2LM_WORK). gpu_lightprobes.py refuses a --farm that is not directly under H2LM_WORK, so its --farm is the
        /// overlay's farm_wNN junction (to the same real folder); every path the shell reads stays the real one. Null for every scenario bake.</summary>
        public string OverlayWork;
        public bool RunDirPredicted;
        public int? Worker, ProbeWorker;
        public string WorkerNote, ProbeWorkerNote;
        public int ProbePoints;
        public bool ProbePointsAssumed;
        /// <summary>X1: the adapter limits decided by the session (null = nothing passed: the production argv).</summary>
        public GpuLimits Limits;
        /// <summary>Compatibility mode (DF64): the kernel set the session decided (Unknown = the GPU is not known: dry run / GPU check skipped) and whether
        /// every engine the bake runs has the mode.</summary>
        public PrecisionMode Precision;
        public bool EngineHasCompat;
        /// <summary>X1: the probe-count preflight found the BSP close to the GPU probe pass's limit.</summary>
        public string ProbeWarning;
        public bool ReuseLightmap, ReuseProbe;
        public string LightmapReuseNote, ProbeReuseNote;
    }

    public static class BakePlanner
    {
        public const int MapQueryTimeoutMs = 120000;
        public const int ShStageTimeoutMs = 10 * 60 * 1000;

        /// <summary>Does this option set bake light probes? (Probes mode always; Standard when the BSP has probe points.)</summary>
        public static bool NeedsProbes(BakeOptions o, int points) { return o.Mode == BakeMode.Probes || (o.Mode == BakeMode.Standard && points > 0); }
        /// <summary>Does it use the lightmap worker N? (everything but Probes mode)</summary>
        public static bool NeedsLightmapWorker(BakeOptions o) { return o.Mode != BakeMode.Probes; }
        /// <summary>Does it need the map query (probe points / environments)? (Standard and Probes)</summary>
        public static bool NeedsMapQuery(BakeOptions o) { return o.Mode != BakeMode.Lightmap && o.Mode != BakeMode.AoOnly; }

        /// <summary>INST-relative gpu_bake for the quality: Custom uses engine_custom (N3 guarded fullgpu) when the payload has it.</summary>
        public static string GpuBakeRel(BakeOptions o, PayloadManifest m)
        {
            if (o.Quality == Quality.Custom && m != null && m.InstComponent.Has(InstLayout.GpuBakeCustom)) return InstLayout.GpuBakeCustom;
            return InstLayout.GpuBake;
        }

        /// <param name="farmRoot">the folder that holds the workers: ResolvedRuntime.Farms (WORK\Temp (safe to delete) or WORK).</param>
        public static string FarmDir(string farmRoot, int w) { return Path.Combine(farmRoot, "farm_w" + w.ToString("D2", CultureInfo.InvariantCulture)); }

        /// <summary>gpu_bake's own arguments in run_bake.ps1's order (+ the P2 custom parameters last).</summary>
        public static List<string> GpuBakeArgs(BakeOptions o, int worker, bool writeBack, bool skipCapture, GpuLimits limits = null)
        {
            var a = new List<string> { "-Scenario", o.ScenarioRel, "-Bsp", o.Bsp, "-Worker", worker.ToString(CultureInfo.InvariantCulture), "-Quality", o.QualityToken };
            if (writeBack) a.Add("-WriteBack");
            if (skipCapture) a.Add("-SkipCapture");
            if (o.Fixes) { a.Add("-SceneryFix"); a.Add("-PluckerFix"); }
            a.Add("-WriteBackMode"); a.Add("direct");
            if (o.Quality == Quality.Custom)
            {
                a.Add("-Photons"); a.Add(o.Photons.ToString(CultureInfo.InvariantCulture));
                a.Add("-MCSamples"); a.Add(o.Rays.ToString(CultureInfo.InvariantCulture));
            }
            // X1: sized for the adapter's VRAM. Both are 0 on a 24 GB card (the planner's own cap binds first), so that argv is unchanged.
            if (limits != null && limits.MaxCallsPerPart > 0) { a.Add("-MaxCallsPerPart"); a.Add(limits.MaxCallsPerPart.ToString(CultureInfo.InvariantCulture)); }
            if (limits != null && limits.BufCapMB > 0) { a.Add("-BufCapMB"); a.Add(limits.BufCapMB.ToString(CultureInfo.InvariantCulture)); }
            // X2: "Keep PC responsive - Slower Bakes!": the engine's own switch (it sets GPULM_RESPONSIVE=1 for its fullgpu runs). Last, and only
            // when ticked, so an unticked bake's argv is exactly what it was before this option.
            if (o.Responsive) a.Add("-Responsive");
            // W2 (0.6.3): "2004 Exact Vertex Lighting?": the engine's opt-in switch (lm_tagwriter --exact-vertex-lighting). Every gpu_bake the plan
            // builds writes the direct-writer stage, so every one gets it; unticked = no byte of the argv changes.
            if (o.ExactVertexLighting) a.Add("-ExactVertexLighting");
            // "Scenery Multi Permutation Fix (Experimental Improvement)": the engine's opt-in switch. Only ever reached for an engine that declares it
            // (BakeSession.Prepare refuses otherwise; AppHost.SceneryPermutationFixWhy); the capture and the write-back both need it, so every
            // gpu_bake of the bake (incl. the tool write-back fallback argv) gets it. Unticked = no byte of the argv changes.
            if (o.SceneryPermutationFix) a.Add("-" + BakeOptions.SceneryPermutationFixSwitch);
            return a;
        }

        /// <summary>The read-only map query (probe points by lp_probes.py's rule, lighting environments, the BSP list).</summary>
        public static PlanStep MapQueryStep(ResolvedRuntime rt, string root, BakeOptions o)
        {
            var set = TagSet.For(root, o.ScenarioRel, o.Bsp);
            var s = new PlanStep
            {
                Id = StepIds.MapQuery, UiStep = StepId.Preflight, Kind = PlanStepKind.Process, Title = "map query (read-only): probe points, lighting environments, BSPs",
                FileName = rt.PyExe, Cwd = rt.Work, TimeoutMs = MapQueryTimeoutMs,
            };
            s.Args.AddRange(MapQuery.Args(Path.Combine(rt.InstDir, InstLayout.TagIoDir), set.ScenarioFile, o.Bsp, Path.Combine(set.TagDir, set.LtmpName)));
            s.Params.Add("when", "before planning: decides whether light probes are baked");
            return s;
        }

        public static BakePlan Build(PlanInput pi)
        {
            var o = pi.O;
            var rt = pi.Rt;
            var set = TagSet.For(rt.Root, o.ScenarioRel, o.Bsp);
            var p = new BakePlan
            {
                Options = o, AppVersion = pi.AppVersion, PayloadSource = pi.PayloadSource, Rt = rt, Env = pi.Env,
                Root = rt.Root, Work = rt.Work, InstDir = rt.InstDir, SlotBase = rt.SlotBase, RunDir = pi.RunDir, RunDirPredicted = pi.RunDirPredicted,
                Worker = pi.Worker, ProbeWorker = pi.ProbeWorker, WorkerNote = pi.WorkerNote, ProbeWorkerNote = pi.ProbeWorkerNote,
                ProbePoints = pi.ProbePoints, ProbePointsAssumed = pi.ProbePointsAssumed,
                ProbePath = NeedsProbes(o, pi.ProbePoints), Install = !o.StageOnly,
                ReuseLightmapCapture = pi.ReuseLightmap, ReuseProbeCapture = pi.ReuseProbe,
                LightmapReuseNote = pi.LightmapReuseNote, ProbeReuseNote = pi.ProbeReuseNote,
                LtmpName = set.LtmpName, TagDir = set.TagDir,
            };
            string engineRel = GpuBakeRel(o, pi.Manifest);
            p.Engine = InstLayout.Full(rt.InstDir, engineRel);
            string engineDir = Path.GetDirectoryName(p.Engine);
            if (o.Quality == Quality.Custom) p.CustomGuarded = engineRel == InstLayout.GpuBakeCustom;
            if (o.Quality == Quality.Custom && engineRel == InstLayout.GpuBake)
                p.Warnings.Add("UNGUARDED Custom (dev --dev-custom-unguarded): this payload has no engine_custom with the N3 palloc guard, so Custom runs engine_v373's fullgpu -- a uint32 photon-count wrap would NOT be caught; evidence from this run is marked guarded=false");
            p.Limits = pi.Limits;
            p.Precision = pi.Precision; p.EngineHasCompat = pi.EngineHasCompat;
            if (pi.Precision == PrecisionMode.Compat)
                p.Notes.Add("compatibility mode (DF64): this GPU has no FP64, so fullgpu runs its FP32-only double-single kernels (chosen by the engine itself; nothing is passed). Experimental: the lightmap matched full precision in testing; the bake may be slower");
            else if (pi.Precision == PrecisionMode.CompatForced)
                p.Notes.Add(BakeOptions.ForceCompatNote + "; the lightmap matched full precision in testing; the bake may be slower");
            if (pi.Precision == PrecisionMode.Unknown && pi.EngineHasCompat)
                p.Notes.Add("precision is decided by the GPU when the bake starts: gpucheck reports it and fullgpu picks the kernels; a GPU without FP64 runs in Compatibility mode (DF64)");
            if (pi.Limits != null)
                foreach (var ln in pi.Limits.Notes)
                {
                    if (ln.StartsWith("WARNING ", StringComparison.Ordinal)) p.Warnings.Add(ln.Substring(8)); else p.Notes.Add(ln);
                }
            if (!string.IsNullOrEmpty(pi.ProbeWarning)) p.Warnings.Add(pi.ProbeWarning);
            if (!o.Fixes) p.Warnings.Add("the Plücker and Scenery fixes are OFF (dev flag): this is not the validated configuration");
            if (o.Responsive)
                p.Notes.Add("responsive on (\"" + BakeOptions.ResponsiveLabel + "\"): every gpu_bake gets -Responsive; the GPU work is split into short pieces so the desktop stays smooth, the lightmap is identical, the bake takes longer"
                            + (o.Ao || NeedsProbes(o, pi.ProbePoints) ? "; the AO trace and the light probe bake are run by driver scripts that cannot take the switch, so they get GPULM_RESPONSIVE=1 in their environment instead" : ""));
            if (o.ExactVertexLighting)
                p.Notes.Add(BakeOptions.ExactVertexLightingNote + " (\"" + BakeOptions.ExactVertexLightingLabel + "\"): every gpu_bake gets -ExactVertexLighting; the tag writer re-runs the CPU's per-vertex visibility gate (about 2 minutes longer on cyclotron); it changes only the vertex-lit records, the lightmap bitmaps are unaffected"
                            + (o.Ao ? "; the AO layer's final stage is written by 'gpu_ao stage', which gets --exact-vertex-lighting too" : ""));
            if (o.SceneryPermutationFix)
            {
                p.Notes.Add(BakeOptions.SceneryPermutationFixNote + " (\"" + BakeOptions.SceneryPermutationFixLabel + "\"): every gpu_bake gets -" + BakeOptions.SceneryPermutationFixSwitch
                            + "; the capture records the shape the tool picked for each scenery placement, so a capture is only reusable by a bake with the same setting");
                if (o.Mode == BakeMode.Probes)
                    p.Warnings.Add(BakeOptions.SceneryPermutationFixLabel + " has no effect in Lightprobes mode: no lightmap is baked, so no gpu_bake gets the switch");
                else if (o.Ao || NeedsProbes(o, pi.ProbePoints))
                    p.Warnings.Add(BakeOptions.SceneryPermutationFixLabel + " is not passed to the " + (o.Ao ? "AO trace" : "") + (o.Ao && NeedsProbes(o, pi.ProbePoints) ? " or the " : "")
                                   + (NeedsProbes(o, pi.ProbePoints) ? "light probe bake" : "") + " (driver scripts that run their own engine copy and cannot take the switch): "
                                   + "that part still sees every shape of a multi permutation scenery tag, and the combination is untested");
            }
            string runDir = pi.RunDir;
            string work = rt.Work;
            string py = rt.PyExe;

            if (NeedsMapQuery(o)) p.Add(MapQueryStep(rt, rt.Root, o));
            if (pi.ProbePointsAssumed && NeedsMapQuery(o)) p.Notes.Add("the probe-point count is ASSUMED for this dry run; the real run decides it from step " + p.Find(StepIds.MapQuery).Index);

            if (o.Mode == BakeMode.Lightmap && pi.ProbePoints > 0)
                p.Notes.Add("Lightmap Only on a probe BSP: this resets the BSP's " + pi.ProbePoints + " light probes to zero, as `tool lightmaps` does");

            if (o.Mode == BakeMode.AoOnly) AddAoPrecheck(p, pi);
            if (p.Install)
            {
                var b = p.Add(new PlanStep { Id = StepIds.Backup, UiStep = StepId.Backup, Kind = PlanStepKind.Shell, Title = "back up the live lightmap set to _TAGBAK_ui (md5-verified)" });
                b.Params.Add("kind", o.Mode == BakeMode.Probes ? "LtmpOnly" : "LightmapSet").Add("tagDir", set.TagDir).Add("prefix", set.Prefix)
                        .Add("into", TagBackup.UiRoot(work) + "\\" + set.Leaf + "_" + o.Bsp + "_<yyyyMMdd_HHmmss>");
            }

            string rest = null;              // where the installed / final stage comes from
            if (o.Mode == BakeMode.AoOnly)
            {
                rest = AddAoOnly(p, pi, set, engineDir);
                p.FinalStage = rest;
                if (p.Install)
                {
                    p.InstallStage = rest;
                    p.Marker = StartupRecovery.InstallMarkerPath(work, pi.Worker.Value);
                    p.InstallBackupDir = Path.Combine(runDir, RunFolder.BackupDirName);
                    p.LmwInstallPy = rt.LmwInstallPy;
                }
            }
            else if (o.Mode != BakeMode.Probes)
            {
                int n = pi.Worker ?? throw new InvalidOperationException("no lightmap worker");
                string outN = FarmDir(rt.Farms, n);
                p.Out = outN;
                // U3: gpu_bake always bakes stage-only. With install and no AO / probe layer the SHELL installs the direct writer's
                // stage (the [4b] command, with Retry); gpu_bake -WriteBack (the production argv) runs only as the tool write-back
                // fallback. Before U3 that plan ran gpu_bake -WriteBack, whose [4b] lmw_install failed the whole run on a held file.
                bool shellInstallsLightmap = p.Install && !o.Ao && !p.ProbePath;
                var args = GpuBakeArgs(o, n, false, pi.ReuseLightmap, pi.Limits);
                var spec = PwshInvocation.GpuBake(WithScript(rt, p.Engine), pi.Env, args);   // refuses -?, -Cleanup, a worker outside 200-299 / 149
                var g = p.Add(new PlanStep
                {
                    Id = StepIds.GpuBake, UiStep = pi.ReuseLightmap ? StepId.Derive : StepId.Capture, Kind = PlanStepKind.Process,
                    Title = "gpu_bake.ps1 farm_w" + n.ToString("D2", CultureInfo.InvariantCulture) + (shellInstallsLightmap ? " (stage only; the shell installs its stage)" : " (stage only)"),
                    FileName = spec.FileName, Cwd = spec.Cwd, CaptureWorker = n, CaptureOut = outN, FreshCapture = !pi.ReuseLightmap,
                });
                g.Args.AddRange(spec.Args);
                if (shellInstallsLightmap)
                    g.Params.Add("shellInstalls", true)
                            .Add("ifDirectWriterCannotRun", "gpu_bake refuses a stage-only bake up front when the direct writer cannot run at all; the production "
                                 + "-WriteBack argv switches to the tool write-back there, so the shell then runs it: " + string.Join(" ", GpuBakeArgs(o, n, true, pi.ReuseLightmap, pi.Limits)));
                rest = Path.Combine(outN, "tagw_stage");

                if (!p.Install && !o.Ao && !p.ProbePath)
                {
                    // R1 fix (false DONE): without -WriteBack gpu_bake has no fallback and exits 0 even when [4a] failed, so the
                    // stage-only result is checked here (AO / probe plans check it in stage-ready)
                    var c = p.Add(new PlanStep { Id = StepIds.StageCheck, UiStep = StepId.TagWriter, Kind = PlanStepKind.Shell, Title = "require the direct writer's stage (a 'written' tagw_report.json made by this run)" });
                    c.Params.Add("report", Path.Combine(outN, "tagw_stage", "tagw_report.json"))
                            .Add("else", "the direct tag writer did not produce a stage: the run FAILS (nothing was written to the tags)");
                }
                if (shellInstallsLightmap)
                {
                    // U3: the stage gpu_bake's [4b] would have installed -> the shell's install (Retry); none -> the production fallback
                    var f = p.Add(new PlanStep
                    {
                        Id = StepIds.StageOrToolFallback, UiStep = StepId.TagWriter, Kind = PlanStepKind.Shell,
                        Title = "require the direct writer's stage (a 'written' tagw_report.json made by this run) -> the shell installs it; none -> the production tool write-back fallback",
                    });
                    f.Params.Add("report", Path.Combine(outN, "tagw_stage", "tagw_report.json"))
                            .Add("else", "the direct tag writer made no stage: gpu_bake -WriteBack (the production argv) with -SkipCapture on this capture runs [4a] again and "
                                 + "then the tool write-back [4], as production falls back; it installs itself (no Retry there) and the run ends after its Verify")
                            .Add("fallbackArgs", GpuBakeArgs(o, n, true, true, pi.Limits).Cast<object>().ToList());
                }
                if (o.Ao || p.ProbePath)
                {
                    var r = p.Add(new PlanStep { Id = StepIds.StageReady, UiStep = StepId.TagWriter, Kind = PlanStepKind.Shell, Title = "require the direct writer's stage (OUT\\skeleton + a 'written' tagw_report.json)" });
                    r.Params.Add("skeleton", Path.Combine(outN, "skeleton")).Add("report", Path.Combine(outN, "tagw_stage", "tagw_report.json"))
                            .Add("else", "the map fell back to the tool writer: " + (o.Ao ? "AO" : "light probes") + " need(s) the direct writer -- offer to install without " + (o.Ao ? "AO" : "probes")
                                 + " (production argv, -SkipCapture -WriteBack)");
                }
                if (o.Ao)
                {
                    string drv = InstLayout.Full(rt.InstDir, InstLayout.AoDriver);
                    string aoEng = InstLayout.Full(rt.InstDir, InstLayout.AoEngineDir);
                    var common = new List<string> { "--farm", outN, "--worker", n.ToString(CultureInfo.InvariantCulture), "--scenario", o.ScenarioRel, "--bsp", o.Bsp, "--engine", aoEng };
                    var tr = p.Add(new PlanStep { Id = StepIds.AoTrace, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao trace (dev_AO gpu_bake -AOOnly on the capture, quality " + o.AoTraceQuality + ")", FileName = py, Cwd = work });
                    tr.Args.Add(drv); tr.Args.Add("trace"); tr.Args.AddRange(common); tr.Args.Add("--quality"); tr.Args.Add(o.AoTraceQuality);
                    if (o.Fixes) { tr.Args.Add("--plucker-fix"); tr.Args.Add("--scenery-fix"); }
                    if (o.Responsive) tr.EnvExtra["GPULM_RESPONSIVE"] = "1";    // X2: the gpu_ao driver runs gpu_bake -AOOnly itself and has no -Responsive argument
                    var ap = p.Add(new PlanStep { Id = StepIds.AoApply, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao apply (strength " + Num(o.AoStrength) + ", exponent " + Num(o.AoExponent) + ")", FileName = py, Cwd = work });
                    ap.Args.Add(drv); ap.Args.Add("apply"); ap.Args.AddRange(common);
                    ap.Args.AddRange(new[] { "--tag", o.AoTag, "--strength", Num(o.AoStrength), "--exponent", Num(o.AoExponent), "--overwrite" });
                    var st = p.Add(new PlanStep { Id = StepIds.AoStage, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao stage (lm_tagwriter + lmw_validate -> OUT\\ao\\" + o.AoTag + "\\tagw_stage)", FileName = py, Cwd = work });
                    st.Args.Add(drv); st.Args.Add("stage"); st.Args.AddRange(common); st.Args.AddRange(new[] { "--tag", o.AoTag, "--overwrite" });
                    if (o.ExactVertexLighting) st.Args.Add("--exact-vertex-lighting");        // W2 (0.6.3): the AO layer final stage is written by gpu_ao stage (P6b), not by gpu_bake
                    rest = Path.Combine(outN, "ao", o.AoTag, "tagw_stage");
                    p.Notes.Add("ambient occlusion is an artistic layer, not CPU-faithful");
                }
                if (p.ProbePath)
                {
                    string fin = Path.Combine(runDir, "final_stage");
                    var cp = p.Add(new PlanStep { Id = StepIds.CopyFinalStage, UiStep = StepId.ProbeCapture, Kind = PlanStepKind.Shell, Title = "copy the final stage to RUN\\final_stage (the files tagw_report.json lists, md5-verified)" });
                    cp.Params.Add("from", rest).Add("to", fin);
                    AddProbeBake(p, pi, set, Path.Combine(fin, set.LtmpName), engineDir);
                    string pr = Path.Combine(runDir, "probes");
                    var m = p.Add(new PlanStep { Id = StepIds.ShMerge, UiStep = StepId.Probes, Kind = PlanStepKind.Process, Title = "sh_stage merge (only the SH fields may differ)", FileName = py, Cwd = work, TimeoutMs = ShStageTimeoutMs });
                    m.Args.AddRange(new[] { InstLayout.Full(rt.InstDir, InstLayout.ShStage), "merge", "--stage", fin, "--sh", Path.Combine(pr, set.LtmpName), "--probes", Path.Combine(pr, "probes.bin"), "--h2-root", rt.Root });
                    AddValidate(p, rt, set, o, fin, Path.Combine(runDir, "final_stage_validate.json"), "the merged final stage");
                    rest = fin;
                }
                if (!p.ProbePath)
                {
                    // 0.6.8 bounded temp: the final stage leaves the farm (which may be deleted at any moment) for KEEP RUN\final_stage before
                    // anything reads it again: the install's source, the stage-only result and a .map bake's injection source are that copy
                    string fin = Path.Combine(runDir, "final_stage");
                    var cp = p.Add(new PlanStep { Id = StepIds.CopyFinalStage, UiStep = StepId.TagWriter, Kind = PlanStepKind.Shell, Title = "copy the final stage to RUN\\final_stage (the files tagw_report.json lists, md5-verified; the farm is temporary)" });
                    cp.Params.Add("from", rest).Add("to", fin);
                    rest = fin;
                }
                p.FinalStage = rest;
                if (p.Install)
                {
                    p.InstallStage = rest;
                    p.Marker = StartupRecovery.InstallMarkerPath(work, n);
                    // 0.6.8: the backup goes to the run's own RUN\tagbak (KEEP) in every mode; Lightmap Only / Standard without a layer keep the
                    // baking engine's own lmw_install.py (the one gpu_bake's [4b] runs), the layers use the payload's
                    p.InstallBackupDir = Path.Combine(runDir, RunFolder.BackupDirName);
                    p.LmwInstallPy = shellInstallsLightmap ? Path.Combine(engineDir, "v043-tagwriter", "lmw_install.py") : rt.LmwInstallPy;
                }
            }
            else
            {
                string live = Path.Combine(runDir, "live_ltmp", set.LtmpName);
                var cl = p.Add(new PlanStep { Id = StepIds.CopyLiveLtmp, UiStep = StepId.Backup, Kind = PlanStepKind.Shell, Title = "copy the live lightmap tag to RUN\\live_ltmp (md5-verified)" });
                cl.Params.Add("from", Path.Combine(set.TagDir, set.LtmpName)).Add("to", live);
                AddProbeBake(p, pi, set, live, engineDir);
                string pr = Path.Combine(runDir, "probes");
                string ps = Path.Combine(runDir, "probe_stage");
                var sg = p.Add(new PlanStep { Id = StepIds.ShSingle, UiStep = StepId.Probes, Kind = PlanStepKind.Process, Title = "sh_stage single (a one-ltmp stage; only the SH fields may differ)", FileName = py, Cwd = work, TimeoutMs = ShStageTimeoutMs });
                sg.Args.AddRange(new[] { InstLayout.Full(rt.InstDir, InstLayout.ShStage), "single", "--stage", ps, "--src", live, "--sh", Path.Combine(pr, set.LtmpName),
                                         "--probes", Path.Combine(pr, "probes.bin"), "--h2-root", rt.Root, "--name", set.LtmpName });
                // check-set reads a whole set: the one-ltmp stage + COPIES of the live bitmaps it will be installed beside
                string chk = Path.Combine(runDir, "probe_stage_check");
                var asm = p.Add(new PlanStep { Id = StepIds.AssembleCheckSet, UiStep = StepId.Probes, Kind = PlanStepKind.Shell, Title = "the set lmw_validate checks: the probe stage's ltmp + copies of the live bitmaps (md5-verified)" });
                asm.Params.Add("ltmp", Path.Combine(ps, set.LtmpName)).Add("truecolor", Path.Combine(set.TagDir, set.Prefix + "_truecolor_bitmaps.bitmap"))
                          .Add("p8", Path.Combine(set.TagDir, set.Prefix + "_bitmaps.bitmap")).Add("to", chk);
                AddValidate(p, rt, set, o, chk, Path.Combine(runDir, "probe_stage_validate.json"), "the probe stage (with the live bitmaps)");
                p.FinalStage = ps;
                if (p.Install)
                {
                    var ck = p.Add(new PlanStep { Id = StepIds.CheckLiveLtmp, UiStep = StepId.Install, Kind = PlanStepKind.Shell, Title = "the live lightmap tag is still the one the probes were written into" });
                    ck.Params.Add("live", Path.Combine(set.TagDir, set.LtmpName)).Add("report", Path.Combine(ps, "tagw_report.json")).Add("copy", live);
                    p.InstallStage = ps;
                    p.Marker = StartupRecovery.InstallMarkerPath(work, pi.ProbeWorker.Value);
                    p.InstallBackupDir = Path.Combine(runDir, RunFolder.BackupDirName);
                    p.LmwInstallPy = rt.LmwInstallPy;
                }
            }

            if (p.InstallStage != null)
            {
                var ins = p.Add(new PlanStep { Id = StepIds.Install, UiStep = StepId.Install, Kind = PlanStepKind.Shell, Title = "lmw_install.py install (TAG WRITE: a cancel waits for it)", TagWrite = true });
                ins.FileName = py;
                ins.Cwd = work;
                ins.Args.AddRange(TagInstaller.InstallArgs(new InstallRequest
                {
                    LmwInstallPy = p.LmwInstallPy ?? rt.LmwInstallPy, Stage = p.InstallStage, TargetDir = set.TagDir, BackupDir = p.InstallBackupDir,
                    AutoBackupRoot = null, Marker = p.Marker,          // 0.6.8: _TAGBAK_auto retired in the shell's install (the run's tagbak + _TAGBAK_ui are the backups)
                }));
                ins.Params.Add("stage", p.InstallStage).Add("targetDir", set.TagDir).Add("backupDir", p.InstallBackupDir).Add("marker", p.Marker)
                          .Add("onExit2", "nothing changed, the stage is kept: Retry after closing Sapien / Guerilla").Add("onExit1", "offer lmw_install restore from the backup dir");
                if (o.Mode != BakeMode.Probes && !o.Ao && !p.ProbePath)
                    ins.Params.Add("same", "gpu_bake's [4b] lmw_install.py (its own $TW copy) on the KEEP copy of its stage, backup RUN\\tagbak, marker in KEEP; with Retry");
            }
            if (p.Install)
            {
                var v = p.Add(new PlanStep { Id = StepIds.Verify, UiStep = StepId.Verify, Kind = PlanStepKind.Shell, Title = "verify: install marker gone, the installed files equal the stage, radiance bitmaps unchanged" });
                v.Params.Add("marker", p.Marker).Add("tagDir", set.TagDir);
            }
            else p.Notes.Add("stage only: nothing is written to the tags; the final stage is " + p.FinalStage);
            // Compatibility mode (DF64): --force-compat gives every step that runs the GPU engine GPULM_COMPAT=1 (BakeSession refused it up front on an
            // engine that does not validate the variable, so it is only ever reached for an engine that does). Without it nothing is added: the engine
            // picks the kernel set by the GPU itself.
            if (o.ForceCompat)
            {
                foreach (var s in p.Steps) ApplyCompatEnv(o, s);
                var fb = p.Find(StepIds.StageOrToolFallback);
                if (fb != null) fb.Params.Add("fallbackEnv", GpuCheck.CompatEnvVar + "=1");
            }
            ApplyRebakeHint(o, p, pi.ReuseLightmap);        // 0.6.8 Advanced "Re-bake speedup" (off: nothing added)
            ApplyDirectSamples(o, p);                        // 0.6.12 Advanced "Extra direct light samples" + the Custom box (off / x1: nothing added)
            return p;
        }

        /// <summary>0.6.12 "Extra direct light samples" (the Advanced opt-in + Custom's "Direct light samples ×" box / --direct-samples N): GPULM_DIRECT_SAMPLE_MUL=N on
        /// the gpu_bake steps of a CUSTOM-quality bake. The engine then runs each sun / sky-light pass and the surface-light class N times with different seeds and
        /// averages them. Off, N = 1, or another quality: nothing is set (the argv and the environment are exactly as before). A tick without Custom quality is
        /// reported as a warning, never silently dropped.</summary>
        public static void ApplyDirectSamples(BakeOptions o, BakePlan p)
        {
            if (!o.DirectSamplesOptIn) return;
            if (o.Quality != Quality.Custom) { p.Warnings.Add(BakeOptions.DirectSamplesLabel + " has no effect: it applies to Custom quality only"); return; }
            int k = o.DirectSamplesEffective;
            if (k <= 1) return;
            int n = 0;
            foreach (var s in p.Steps)
                if (s.Kind == PlanStepKind.Process && s.Id == StepIds.GpuBake) { s.EnvExtra[BakeOptions.DirectSamplesEnvVar] = k.ToString(System.Globalization.CultureInfo.InvariantCulture); n++; }
            if (n > 0) p.Notes.Add(o.DirectSamplesNote);
        }

        /// <summary>0.6.8 "Re-bake speedup" (--rebake-hint, default off): GPULM_SKY_HINT=1 on the gpu_bake steps of a bake that reuses its lightmap
        /// capture (the farm's skychain_hint.txt is the previous bake's chain of THIS capture; the engine refuses a hint with another S_cast).
        /// Off, or a fresh capture: nothing is set (the argv and environment are exactly as before).</summary>
        public static void ApplyRebakeHint(BakeOptions o, BakePlan p, bool reuseLightmap)
        {
            if (!o.RebakeHint) return;
            if (!reuseLightmap) { p.Warnings.Add(BakeOptions.RebakeHintLabel + " has no effect: this bake makes a new capture (it only speeds up 'Reuse existing capture')"); return; }
            int n = 0;
            foreach (var s in p.Steps)
                if (s.Kind == PlanStepKind.Process && s.Id == StepIds.GpuBake) { s.EnvExtra[BakeOptions.RebakeHintEnvVar] = "1"; n++; }
            if (n > 0) p.Notes.Add(BakeOptions.RebakeHintNote);
        }

        /// <summary>--force-compat: GPULM_COMPAT=1 on a step that runs the GPU engine (gpu_bake, its tool write-back fallback, the AO trace and the light
        /// probe driver, which run gpu_bake / fullgpu themselves). Other steps (tag writer, installs, sh_stage) never get it.</summary>
        public static void ApplyCompatEnv(BakeOptions o, PlanStep s)
        {
            if (!o.ForceCompat || s == null || s.Kind != PlanStepKind.Process) return;
            if (s.Id == StepIds.GpuBake || s.Id == StepIds.GpuBakeFallback || s.Id == StepIds.AoTrace || s.Id == StepIds.ProbeBake)
                s.EnvExtra[GpuCheck.CompatEnvVar] = "1";
        }

        /// <summary>AO only, first step: the gates that need no capture (ao_existing.py precheck). A refusal here costs no backup and no GPU time.</summary>
        private static void AddAoPrecheck(BakePlan p, PlanInput pi)
        {
            var o = pi.O;
            var rt = pi.Rt;
            string drv = InstLayout.Full(rt.InstDir, InstLayout.AoDriver);
            var s = p.Add(new PlanStep
            {
                Id = StepIds.AoPrecheck, UiStep = StepId.Preflight, Kind = PlanStepKind.Process, FileName = rt.PyExe, Cwd = rt.Work,
                Title = "ao_existing precheck (read-only): the installed lightmap exists, has no AO yet, and belongs to this BSP",
            });
            s.Args.Add(InstLayout.Full(rt.InstDir, InstLayout.AoExisting)); s.Args.Add("precheck");
            s.Args.AddRange(new[] { "--scenario", o.ScenarioRel, "--bsp", o.Bsp, "--engine", InstLayout.Full(rt.InstDir, InstLayout.AoEngineDir), "--ao-dir", Path.GetDirectoryName(drv),
                                    "--h2-root", rt.Root, "--tool", Path.Combine(rt.Root, "tool_fast.exe"), "--records", AoRecords.PathFor(rt.Work) });
            if (o.AoForce) s.Args.Add("--force");
        }

        /// <summary>The AO-only steps (0.6.8 xc8). Returns the stage folder the shell copies and installs. The capture (geometry only) is new unless the plan reuses one; the
        /// AO trace is the full bake's own; ao_existing.py then adds the occlusion to the lightmap that is installed now. The install record check is ao_existing's too
        /// (it gets the record file and --force); the session checks it up front as well, so a refusal costs no GPU time.</summary>
        private static string AddAoOnly(BakePlan p, PlanInput pi, TagSet set, string engineDir)
        {
            var o = pi.O;
            var rt = pi.Rt;
            int n = pi.Worker ?? throw new InvalidOperationException("no lightmap worker");
            string outN = FarmDir(rt.Farms, n);
            p.Out = outN;
            if (!pi.ReuseLightmap)
            {
                var args = GpuBakeArgs(o, n, false, false, pi.Limits);
                args.Remove("-ExactVertexLighting");               // no tag writer runs in AO only
                args.Add("-CaptureOnly");
                var spec = PwshInvocation.GpuBake(WithScript(rt, p.Engine), pi.Env, args);
                var g = p.Add(new PlanStep
                {
                    Id = StepIds.GpuBake, UiStep = StepId.Capture, Kind = PlanStepKind.Process,
                    Title = "gpu_bake.ps1 farm_w" + n.ToString("D2", CultureInfo.InvariantCulture) + " (capture only: AO only needs the geometry; no lighting is baked)",
                    FileName = spec.FileName, Cwd = spec.Cwd, CaptureWorker = n, CaptureOut = outN, FreshCapture = true,
                });
                g.Args.AddRange(spec.Args);
            }
            string drv = InstLayout.Full(rt.InstDir, InstLayout.AoDriver);
            string aoEng = InstLayout.Full(rt.InstDir, InstLayout.AoEngineDir);
            string py = rt.PyExe, work = rt.Work;
            var common = new List<string> { "--farm", outN, "--worker", n.ToString(CultureInfo.InvariantCulture), "--scenario", o.ScenarioRel, "--bsp", o.Bsp, "--engine", aoEng };
            var tr = p.Add(new PlanStep { Id = StepIds.AoTrace, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao trace (dev_AO gpu_bake -AOOnly on the capture, quality " + o.AoTraceQuality + ")", FileName = py, Cwd = work });
            tr.Args.Add(drv); tr.Args.Add("trace"); tr.Args.AddRange(common); tr.Args.Add("--quality"); tr.Args.Add(o.AoTraceQuality);
            if (o.Fixes) { tr.Args.Add("--plucker-fix"); tr.Args.Add("--scenery-fix"); }
            if (o.Responsive) tr.EnvExtra["GPULM_RESPONSIVE"] = "1";
            string ax = InstLayout.Full(rt.InstDir, InstLayout.AoExisting);
            var ex = p.Add(new PlanStep
            {
                Id = StepIds.AoExisting, UiStep = StepId.AO, Kind = PlanStepKind.Process, FileName = py, Cwd = work,
                Title = "ao_existing stage (strength " + Num(o.AoStrength) + ", exponent " + Num(o.AoExponent) + "): add the AO to the installed lightmap -> OUT\\aoonly\\" + o.AoTag + "\\stage (nothing installed)",
            });
            ex.Args.Add(ax); ex.Args.Add("stage");
            ex.Args.AddRange(new[] { "--farm", outN, "--scenario", o.ScenarioRel, "--bsp", o.Bsp, "--engine", aoEng, "--ao-dir", Path.GetDirectoryName(drv), "--h2-root", rt.Root,
                                     "--tool", Path.Combine(rt.Root, "tool_fast.exe"), "--strength", Num(o.AoStrength), "--exponent", Num(o.AoExponent), "--tag", o.AoTag, "--overwrite",
                                     "--records", AoRecords.PathFor(rt.Work) });
            if (o.AoForce) ex.Args.Add("--force");
            p.Notes.Add("AO only: no lighting is baked -- the lightmap installed now (ours, the stock tool's or Bungie's) keeps its light; ambient occlusion is an artistic layer, not CPU-faithful, and it is added once (an install record stops a second AO on the same lightmap)");
            p.Notes.Add("AO only changes the lightmap bitmaps (truecolor + P8) and the vertex-lit instance records; the geometry, UVs and per-vertex directions of the installed lightmap are kept");
            var cp = p.Add(new PlanStep { Id = StepIds.CopyFinalStage, UiStep = StepId.TagWriter, Kind = PlanStepKind.Shell, Title = "copy the final stage to RUN\\final_stage (the files tagw_report.json lists, md5-verified; the farm is temporary)" });
            string fin = Path.Combine(p.RunDir, "final_stage");
            cp.Params.Add("from", Path.Combine(outN, "aoonly", o.AoTag, "stage")).Add("to", fin);
            return fin;
        }

        /// <summary>Review fix (finding 21): py INST\engine_v373\v043-tagwriter\lmw_validate.py --check-set DIR --bsp &lt;live bsp&gt;
        /// --json F -- the same gate gpu_bake's Invoke-TagWriterStage runs on its own stage; nothing is installed unless it exits 0.</summary>
        private static void AddValidate(BakePlan p, ResolvedRuntime rt, TagSet set, BakeOptions o, string dir, string json, string what)
        {
            var v = p.Add(new PlanStep
            {
                Id = StepIds.ValidateFinal, UiStep = StepId.Probes, Kind = PlanStepKind.Process, Title = "lmw_validate --check-set on " + what + " (the install gate)",
                FileName = rt.PyExe, Cwd = rt.Work, TimeoutMs = ShStageTimeoutMs,
            });
            v.Args.AddRange(new[] { InstLayout.Full(rt.InstDir, InstLayout.LmwValidate), "--check-set", dir, "--bsp", o.BspTagFile(set.TagDir), "--json", json });
            v.Params.Add("onFail", "nothing is installed; the stage is kept");
        }

        private static void AddProbeBake(BakePlan p, PlanInput pi, TagSet set, string ltmpSrc, string engineDir)
        {
            var o = pi.O;
            var rt = pi.Rt;
            int m = pi.ProbeWorker ?? throw new InvalidOperationException("no probe worker");
            Guards.AssertWorker(m);
            string outM = FarmDir(rt.Farms, m);
            p.OutProbe = outM;
            string pr = Path.Combine(p.RunDir, "probes");
            var s = p.Add(new PlanStep
            {
                Id = StepIds.ProbeBake, UiStep = StepId.ProbeCapture, Kind = PlanStepKind.Process, ProbeWorker = true,
                Title = "gpu_lightprobes.py probe worker farm_w" + m.ToString("D2", CultureInfo.InvariantCulture) + (pi.ReuseProbe ? " (probe-ready capture reused)" : " (its own GPULM_SKY_HI capture)"),
                FileName = rt.PyExe, Cwd = rt.Work, SideLog = Path.Combine(pr, "gpu_bake.log"),
                CaptureWorker = m, CaptureOut = outM, FreshCapture = !pi.ReuseProbe, SkyHi = true,
            });
            s.Args.AddRange(new[] { InstLayout.Full(rt.InstDir, InstLayout.ProbeDriver), "--scenario", o.ScenarioRel, "--bsp", o.Bsp,
                                    "--worker", m.ToString(CultureInfo.InvariantCulture), "--quality", o.QualityToken });
            if (o.Quality == Quality.Custom)
                s.Args.AddRange(new[] { "--photons", o.Photons.ToString(CultureInfo.InvariantCulture), "--mc-samples", o.Rays.ToString(CultureInfo.InvariantCulture) });
            if (pi.ReuseProbe) s.Args.Add("--skip-capture");
            // 0.6.8: with the farms in Temp the probe driver's --farm is the real farm (P5c checks it against H2LM_FARMS); only the 0.6.7 layout of a
            // .map bake still names the overlay work folder's farm junction (the driver then checks it against H2LM_WORK)
            s.Args.AddRange(new[] { "--stage", engineDir, "--ltmp-src", ltmpSrc, "--out", pr, "--h2-root", rt.Root, "--farm", pi.OverlayWork != null && !rt.FarmsInTemp ? FarmDir(pi.OverlayWork, m) : outM, "--pwsh", rt.PwshExe });
            if (o.Fixes) { s.Args.Add("--plucker-fix"); s.Args.Add("--scenery-fix"); }
            if (o.Responsive) s.EnvExtra["GPULM_RESPONSIVE"] = "1";             // X2: gpu_lightprobes.py runs gpu_bake itself and has no -Responsive argument
            s.Params.Add("adds", "GPULM_PROBES=RUN\\probes\\probes.bin and GPULM_SKY_HI=1 for its gpu_bake only (the lightmap capture never sees SKY_HI)");
        }

        private static ResolvedRuntime WithScript(ResolvedRuntime rt, string script)
        {
            return new ResolvedRuntime
            {
                AppVersion = rt.AppVersion, Root = rt.Root, Work = rt.Work, InstDir = rt.InstDir, SlotBase = rt.SlotBase,
                PwshDir = rt.PwshDir, PwshExe = rt.PwshExe, PyDir = rt.PyDir, PyExe = rt.PyExe, MapbakeDir = rt.MapbakeDir, MapbakePy = rt.MapbakePy, GpuBakePs1 = script,
                LmwInstallPy = rt.LmwInstallPy, BoundsDb = rt.BoundsDb, ScanSceneryPy = rt.ScanSceneryPy, GpuCheckExe = rt.GpuCheckExe, FarmRoot = rt.FarmRoot,
            };
        }

        public static string Num(double v) { return v.ToString("0.0#####", CultureInfo.InvariantCulture); }
    }
}
