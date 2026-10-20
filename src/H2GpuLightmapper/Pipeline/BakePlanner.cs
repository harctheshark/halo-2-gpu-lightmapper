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
//   Standard, no probe points identical to Lightmap Only.
//   Standard, probe points    lightmap stage-only in worker N (+ optional AO) -> copy the final stage to RUN\final_stage -> probe
//                             worker M: gpu_lightprobes (its own GPULM_SKY_HI capture, or a probe-ready reuse) --ltmp-src
//                             RUN\final_stage\<ltmp> -> sh_stage merge -> ONE lmw_install of RUN\final_stage.
//   Lightprobes only          ltmp-only backup -> copy of the live ltmp -> probe worker M: gpu_lightprobes -> sh_stage single ->
//                             live-ltmp-unchanged check -> ltmp-only install.
//   Custom P / MC             gpu_bake -Quality custom -Photons P -MCSamples MC (P2: the osoy_launch 'cuban' row on every custom
//                             tool run, incl. the tool write-back fallback -- U3: every fallback argv the shell builds carries
//                             -Photons / -MCSamples too); gpu_lightprobes --quality custom --photons --mc-samples.
//   Install                   py lmw_install.py install --stage .. --target-dir <tagdir> --backup-dir RUN\tag_backup_before_writeback
//                             --auto-backup-root WORK\_TAGBAK_auto --marker OUT\writeback_in_progress.txt (AO / probes);
//                             Lightmap Only / Standard without probes: --backup-dir OUT\tag_backup_before_writeback, as [4b].
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
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
        public bool RunDirPredicted;
        public int? Worker, ProbeWorker;
        public string WorkerNote, ProbeWorkerNote;
        public int ProbePoints;
        public bool ProbePointsAssumed;
        /// <summary>X1: the adapter limits decided by the session (null = nothing passed: the production argv).</summary>
        public GpuLimits Limits;
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
        public static bool NeedsMapQuery(BakeOptions o) { return o.Mode != BakeMode.Lightmap; }

        /// <summary>INST-relative gpu_bake for the quality: Custom uses engine_custom (N3 guarded fullgpu) when the payload has it.</summary>
        public static string GpuBakeRel(BakeOptions o, PayloadManifest m)
        {
            if (o.Quality == Quality.Custom && m != null && m.InstComponent.Has(InstLayout.GpuBakeCustom)) return InstLayout.GpuBakeCustom;
            return InstLayout.GpuBake;
        }

        public static string FarmDir(string work, int w) { return Path.Combine(work, "farm_w" + w.ToString("D2", CultureInfo.InvariantCulture)); }

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
            if (pi.Limits != null)
                foreach (var ln in pi.Limits.Notes)
                {
                    if (ln.StartsWith("WARNING ", StringComparison.Ordinal)) p.Warnings.Add(ln.Substring(8)); else p.Notes.Add(ln);
                }
            if (!string.IsNullOrEmpty(pi.ProbeWarning)) p.Warnings.Add(pi.ProbeWarning);
            if (!o.Fixes) p.Warnings.Add("the Plücker and Scenery fixes are OFF (dev flag): this is not the validated configuration");
            string runDir = pi.RunDir;
            string work = rt.Work;
            string py = rt.PyExe;

            if (NeedsMapQuery(o)) p.Add(MapQueryStep(rt, rt.Root, o));
            if (pi.ProbePointsAssumed && NeedsMapQuery(o)) p.Notes.Add("the probe-point count is ASSUMED for this dry run; the real run decides it from step " + p.Find(StepIds.MapQuery).Index);

            if (o.Mode == BakeMode.Lightmap && pi.ProbePoints > 0)
                p.Notes.Add("Lightmap Only on a probe BSP: this resets the BSP's " + pi.ProbePoints + " light probes to zero, as `tool lightmaps` does");

            if (p.Install)
            {
                var b = p.Add(new PlanStep { Id = StepIds.Backup, UiStep = StepId.Backup, Kind = PlanStepKind.Shell, Title = "back up the live lightmap set to _TAGBAK_ui (md5-verified)" });
                b.Params.Add("kind", o.Mode == BakeMode.Probes ? "LtmpOnly" : "LightmapSet").Add("tagDir", set.TagDir).Add("prefix", set.Prefix)
                        .Add("into", TagBackup.UiRoot(work) + "\\" + set.Leaf + "_" + o.Bsp + "_<yyyyMMdd_HHmmss>");
            }

            string rest = null;              // where the installed / final stage comes from
            if (o.Mode != BakeMode.Probes)
            {
                int n = pi.Worker ?? throw new InvalidOperationException("no lightmap worker");
                string outN = FarmDir(work, n);
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
                    var ap = p.Add(new PlanStep { Id = StepIds.AoApply, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao apply (strength " + Num(o.AoStrength) + ", exponent " + Num(o.AoExponent) + ")", FileName = py, Cwd = work });
                    ap.Args.Add(drv); ap.Args.Add("apply"); ap.Args.AddRange(common);
                    ap.Args.AddRange(new[] { "--tag", o.AoTag, "--strength", Num(o.AoStrength), "--exponent", Num(o.AoExponent), "--overwrite" });
                    var st = p.Add(new PlanStep { Id = StepIds.AoStage, UiStep = StepId.AO, Kind = PlanStepKind.Process, Title = "gpu_ao stage (lm_tagwriter + lmw_validate -> OUT\\ao\\" + o.AoTag + "\\tagw_stage)", FileName = py, Cwd = work });
                    st.Args.Add(drv); st.Args.Add("stage"); st.Args.AddRange(common); st.Args.AddRange(new[] { "--tag", o.AoTag, "--overwrite" });
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
                p.FinalStage = rest;
                if (p.Install)
                {
                    p.InstallStage = rest;
                    p.Marker = Path.Combine(outN, StartupRecovery.MarkerName);
                    // U3: the default path keeps EXACTLY [4b]'s install command: the baking engine's own lmw_install.py and the
                    // worker's OUT\tag_backup_before_writeback (lmw_install renames an older copy there .older_<ts>, never deletes)
                    p.InstallBackupDir = shellInstallsLightmap ? Path.Combine(outN, "tag_backup_before_writeback") : Path.Combine(runDir, "tag_backup_before_writeback");
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
                    p.Marker = Path.Combine(p.OutProbe, StartupRecovery.MarkerName);
                    p.InstallBackupDir = Path.Combine(runDir, "tag_backup_before_writeback");
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
                    AutoBackupRoot = Path.Combine(work, "_TAGBAK_auto"), Marker = p.Marker,
                }));
                ins.Params.Add("stage", p.InstallStage).Add("targetDir", set.TagDir).Add("backupDir", p.InstallBackupDir).Add("marker", p.Marker)
                          .Add("onExit2", "nothing changed, the stage is kept: Retry after closing Sapien / Guerilla").Add("onExit1", "offer lmw_install restore from the backup dir");
                if (o.Mode != BakeMode.Probes && !o.Ao && !p.ProbePath)
                    ins.Params.Add("same", "the command gpu_bake's [4b] runs (its own $TW\\lmw_install.py, the same arguments): only the Retry is new");
            }
            if (p.Install)
            {
                var v = p.Add(new PlanStep { Id = StepIds.Verify, UiStep = StepId.Verify, Kind = PlanStepKind.Shell, Title = "verify: install marker gone, the installed files equal the stage, radiance bitmaps unchanged" });
                v.Params.Add("marker", p.Marker).Add("tagDir", set.TagDir);
            }
            else p.Notes.Add("stage only: nothing is written to the tags; the final stage is " + p.FinalStage);
            return p;
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
            v.Args.AddRange(new[] { InstLayout.Full(rt.InstDir, InstLayout.LmwValidate), "--check-set", dir, "--bsp", Path.Combine(set.TagDir, o.Bsp + ".scenario_structure_bsp"), "--json", json });
            v.Params.Add("onFail", "nothing is installed; the stage is kept");
        }

        private static void AddProbeBake(BakePlan p, PlanInput pi, TagSet set, string ltmpSrc, string engineDir)
        {
            var o = pi.O;
            var rt = pi.Rt;
            int m = pi.ProbeWorker ?? throw new InvalidOperationException("no probe worker");
            Guards.AssertWorker(m);
            string outM = FarmDir(rt.Work, m);
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
            s.Args.AddRange(new[] { "--stage", engineDir, "--ltmp-src", ltmpSrc, "--out", pr, "--h2-root", rt.Root, "--farm", outM, "--pwsh", rt.PwshExe });
            if (o.Fixes) { s.Args.Add("--plucker-fix"); s.Args.Add("--scenery-fix"); }
            s.Params.Add("adds", "GPULM_PROBES=RUN\\probes\\probes.bin and GPULM_SKY_HI=1 for its gpu_bake only (the lightmap capture never sees SKY_HI)");
        }

        private static ResolvedRuntime WithScript(ResolvedRuntime rt, string script)
        {
            return new ResolvedRuntime
            {
                AppVersion = rt.AppVersion, Root = rt.Root, Work = rt.Work, InstDir = rt.InstDir, SlotBase = rt.SlotBase,
                PwshDir = rt.PwshDir, PwshExe = rt.PwshExe, PyDir = rt.PyDir, PyExe = rt.PyExe, GpuBakePs1 = script,
                LmwInstallPy = rt.LmwInstallPy, BoundsDb = rt.BoundsDb, ScanSceneryPy = rt.ScanSceneryPy, GpuCheckExe = rt.GpuCheckExe,
            };
        }

        public static string Num(double v) { return v.ToString("0.0#####", CultureInfo.InvariantCulture); }
    }
}
