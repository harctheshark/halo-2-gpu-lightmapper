// S3.x the bake plan: the ordered steps one option combination runs (FINAL_DESIGN section 5), as plain data. The executor
// (BakeSession) runs it; --dry-run prints it (text, or --json) without starting anything. Process steps carry the EXACT
// file name, argv, working directory and environment the child gets; shell steps are the shell's own actions (backup,
// checks, verified copies, the lmw_install install, verify).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Pipeline
{
    public enum PlanStepKind { Process, Shell }

    /// <summary>Stable step ids (tests and the UI key on them).</summary>
    public static class StepIds
    {
        public const string MapQuery = "map-query";
        public const string Backup = "backup";
        public const string GpuBake = "gpu-bake";
        public const string GpuBakeFallback = "gpu-bake-fallback";
        public const string StageReady = "stage-ready";
        /// <summary>R1 fix (V4 / V6 / V7 finding "false DONE"): a stage-only Lightmap Only run (and Standard on a BSP without
        /// probe points) requires a 'written' tagw_report.json made by THIS run before it can report DONE -- gpu_bake exits 0
        /// when its direct tag writer fails in a stage-only bake (no -WriteBack, so no tool write-back fallback).</summary>
        public const string StageCheck = "stage-check";
        /// <summary>U3 (Retry on the default install path): Lightmap Only / Standard without probes, with install. gpu_bake bakes
        /// stage-only; this step requires the direct writer's 'written' stage made by THIS run (then the shell installs it with
        /// the [4b] lmw_install command, with Retry) -- without one it runs the production tool write-back fallback (gpu_bake
        /// -SkipCapture -WriteBack on the same capture) and the plan ends after its Verify.</summary>
        public const string StageOrToolFallback = "stage-or-tool-fallback";
        public const string AoTrace = "ao-trace";
        public const string AoApply = "ao-apply";
        public const string AoStage = "ao-stage";
        public const string CopyFinalStage = "copy-final-stage";
        public const string CopyLiveLtmp = "copy-live-ltmp";
        public const string ProbeBake = "probe-bake";
        public const string ShMerge = "sh-merge";
        public const string ShSingle = "sh-single";
        public const string CheckLiveLtmp = "check-live-ltmp";
        /// <summary>Review fix (finding 21): the merged / single probe stage goes through lmw_validate --check-set before it can
        /// be installed (gpu_bake gates its own stage the same way).</summary>
        public const string ValidateFinal = "validate-final";
        /// <summary>Lightprobes only: the one-ltmp stage + COPIES of the live bitmaps it will sit beside = the set check-set reads.</summary>
        public const string AssembleCheckSet = "assemble-check-set";
        public const string Install = "install";
        public const string Verify = "verify";
    }

    public sealed class PlanStep
    {
        public int Index;
        public string Id;
        public StepId UiStep;
        public PlanStepKind Kind;
        public string Title;
        // ---- process
        public string FileName;
        public List<string> Args = new List<string>();
        public string Cwd;
        /// <summary>Variables added on top of the plan's base environment (none for every step today; kept for the record).</summary>
        public SortedDictionary<string, string> EnvExtra = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool TagWrite, ProbeWorker;
        public int TimeoutMs = -1;
        /// <summary>A log the child writes that the shell tails into bake.log (gpu_lightprobes' own gpu_bake.log).</summary>
        public string SideLog;
        // ---- capture bookkeeping (S3.2)
        public int? CaptureWorker;
        public string CaptureOut;
        public bool FreshCapture, SkyHi;
        // ---- shell action parameters
        public JObj Params = new JObj();

        public string CommandLine { get { return FileName != null ? JobProcess.BuildCommandLine(FileName, Args) : null; } }

        public ChildEnvironment EnvFor(ChildEnvironment baseEnv)
        {
            if (EnvExtra.Count == 0) return baseEnv;
            var e = ChildEnvironment.Empty();
            foreach (var kv in baseEnv.Vars) e.Set(kv.Key, kv.Value);
            foreach (var kv in EnvExtra) e.Set(kv.Key, kv.Value);
            return e;
        }
    }

    public sealed class BakePlan
    {
        public BakeOptions Options;
        public string AppVersion, PayloadSource;
        public string Root, Work, InstDir, SlotBase, RunDir;
        public bool RunDirPredicted;
        public ResolvedRuntime Rt;
        public string Engine;
        /// <summary>Custom only: true when the engine is engine_custom (the N3 palloc guard), false on the unguarded production
        /// fullgpu (dev --dev-custom-unguarded); null for Low / Super.</summary>
        public bool? CustomGuarded;
        public int? Worker, ProbeWorker;
        public string WorkerNote, ProbeWorkerNote;
        public string Out, OutProbe;
        public int ProbePoints;
        public bool ProbePointsAssumed;
        /// <summary>X1: what gpu_bake gets for this adapter (-MaxCallsPerPart / -BufCapMB); null = not decided (nothing passed).</summary>
        public GpuLimits Limits;
        public bool ProbePath, Install;
        public bool ReuseLightmapCapture, ReuseProbeCapture;
        public string LightmapReuseNote, ProbeReuseNote;
        /// <summary>The stage the SHELL installs (null when stage-only). U3: every install is the shell's (Lightmap Only /
        /// Standard without probes too: OUT\tagw_stage); gpu_bake -WriteBack runs only as the tool write-back fallback.</summary>
        public string InstallStage;
        /// <summary>U3: the lmw_install.py the install step runs. The default path uses the baking engine's own copy
        /// (gpu_bake's `$TW\lmw_install.py`, the script [4b] runs); the AO / probe paths the engine_v373 copy (same bytes).</summary>
        public string LmwInstallPy;
        /// <summary>What the user gets in stage-only mode (the final stage folder).</summary>
        public string FinalStage;
        public string InstallBackupDir, Marker;
        public string LtmpName, TagDir;
        public ChildEnvironment Env;
        public List<PlanStep> Steps = new List<PlanStep>();
        public List<string> Notes = new List<string>();
        public List<string> Warnings = new List<string>();

        public PlanStep Add(PlanStep s) { s.Index = Steps.Count + 1; Steps.Add(s); return s; }
        public PlanStep Find(string id) { return Steps.FirstOrDefault(s => s.Id == id); }
        /// <summary>The UI steps (section 4.9) this plan goes through, in the step-list order (= the StepId order). A gpu_bake
        /// process covers [Capture], Derive, GPU bake, Tag writer (+ Install, Verify with -WriteBack); the probe driver covers
        /// Probe capture and Probes.</summary>
        public List<StepId> UiSteps
        {
            get
            {
                var set = new HashSet<StepId> { StepId.Preflight };
                foreach (var s in Steps)
                {
                    if (s.Id == StepIds.GpuBake)
                    {
                        if (s.FreshCapture) set.Add(StepId.Capture);
                        set.Add(StepId.Derive); set.Add(StepId.GpuBake); set.Add(StepId.TagWriter);
                        if (s.Args.Contains("-WriteBack")) { set.Add(StepId.Install); set.Add(StepId.Verify); }
                    }
                    else if (s.Id == StepIds.ProbeBake) { set.Add(StepId.ProbeCapture); set.Add(StepId.Probes); }
                    else set.Add(s.UiStep);
                }
                return Enum.GetValues(typeof(StepId)).Cast<StepId>().Where(set.Contains).ToList();
            }
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>
        /// Review fix (finding 18, privacy): the plan prints the environment variables the shell SETS or overrides with their
        /// values (ChildEnvironment.IsLoggable: H2LM_*, PATH, PSModulePath, PSExecutionPolicyPreference, NO_COLOR, POWERSHELL_*,
        /// PYTHON*, and the stripped pipeline prefixes so a leak stays visible). Every other variable is inherited unchanged
        /// and listed by NAME only ("envInherited"): it may hold an API key or a token, and bake.log / --dry-run output gets
        /// shared and committed.
        /// </summary>
        public static void SplitEnv(ChildEnvironment env, out List<KeyValuePair<string, string>> shown, out List<string> inherited)
        {
            shown = new List<KeyValuePair<string, string>>();
            inherited = new List<string>();
            if (env == null) return;
            foreach (var kv in env.Vars)
            {
                if (ChildEnvironment.IsLoggable(kv.Key)) shown.Add(kv);
                else inherited.Add(kv.Key);
            }
        }

        public JObj ToJson(bool dryRun)
        {
            var o = Options;
            var env = new JObj();
            List<KeyValuePair<string, string>> shown;
            List<string> inherited;
            SplitEnv(Env, out shown, out inherited);
            foreach (var kv in shown) env.Add(kv.Key, kv.Value);
            var steps = Steps.Select(s =>
            {
                var j = new JObj().Add("n", (long)s.Index).Add("id", s.Id).Add("uiStep", s.UiStep.ToString()).Add("kind", s.Kind == PlanStepKind.Process ? "process" : "shell").Add("title", s.Title);
                if (s.FileName != null)
                {
                    var ex = new JObj();
                    foreach (var kv in s.EnvExtra) ex.Add(kv.Key, kv.Value);
                    j.Add("exe", s.FileName).Add("args", s.Args.Cast<object>().ToList()).Add("cwd", s.Cwd).Add("envExtra", ex)
                     .Add("commandLine", s.CommandLine).Add("tagWrite", s.TagWrite).Add("probeWorker", s.ProbeWorker).Add("sideLog", s.SideLog);
                }
                if (s.CaptureWorker.HasValue)
                    j.Add("capture", new JObj().Add("worker", (long)s.CaptureWorker.Value).Add("out", s.CaptureOut).Add("fresh", s.FreshCapture).Add("skyHi", s.SkyHi));
                j.Add("params", s.Params);
                return (object)j;
            }).ToList();
            return new JObj().Add("schema", "h2lm-bake-plan/1").Add("dryRun", dryRun).Add("appVersion", AppVersion).Add("payload", PayloadSource)
                .Add("options", new JObj().Add("root", o.Root).Add("scenario", o.ScenarioRel).Add("bsp", o.Bsp).Add("quality", o.QualityToken)
                    .Add("photons", o.Quality == Quality.Custom ? (object)o.Photons : null).Add("rays", o.Quality == Quality.Custom ? (object)(long)o.Rays : null)
                    .Add("mode", o.ModeToken).Add("ao", o.Ao).Add("aoStrength", o.AoStrength).Add("aoExponent", o.AoExponent).Add("aoTag", o.Ao ? o.AoTag : null)
                    .Add("reuseCapture", o.ReuseCapture).Add("install", !o.StageOnly).Add("fixes", o.Fixes)
                    .Add("guarded", CustomGuarded.HasValue ? (object)CustomGuarded.Value : null))
                .Add("root", Root).Add("work", Work).Add("inst", InstDir).Add("slotBase", SlotBase).Add("runDir", RunDir).Add("runDirPredicted", RunDirPredicted)
                .Add("pwsh", Rt == null ? null : Rt.PwshExe).Add("py", Rt == null ? null : Rt.PyExe).Add("engine", Engine)
                .Add("worker", Worker.HasValue ? (object)(long)Worker.Value : null).Add("workerNote", WorkerNote)
                .Add("probeWorker", ProbeWorker.HasValue ? (object)(long)ProbeWorker.Value : null).Add("probeWorkerNote", ProbeWorkerNote)
                .Add("out", Out).Add("outProbe", OutProbe)
                .Add("probePoints", (long)ProbePoints).Add("probePointsAssumed", ProbePointsAssumed).Add("probePath", ProbePath)
                .Add("gpuLimits", Limits == null || !Limits.VramBytes.HasValue ? null : new JObj().Add("vramBytes", Limits.VramBytes.Value).Add("source", Limits.Source)
                    .Add("rowsFit", Limits.RowsFit).Add("maxCallsPerPart", Limits.MaxCallsPerPart).Add("bufCapMB", (long)Limits.BufCapMB))
                .Add("reuseLightmapCapture", ReuseLightmapCapture).Add("reuseProbeCapture", ReuseProbeCapture)
                .Add("lightmapReuseNote", LightmapReuseNote).Add("probeReuseNote", ProbeReuseNote)
                .Add("install", Install).Add("installStage", InstallStage).Add("finalStage", FinalStage).Add("installBackupDir", InstallBackupDir).Add("lmwInstallPy", LmwInstallPy).Add("marker", Marker)
                .Add("tagDir", TagDir).Add("ltmp", LtmpName)
                .Add("uiSteps", UiSteps.Select(x => (object)x.ToString()).ToList())
                .Add("env", env).Add("envInherited", inherited.Cast<object>().ToList())
                .Add("steps", steps).Add("notes", Notes.Cast<object>().ToList()).Add("warnings", Warnings.Cast<object>().ToList());
        }

        public string ToText(bool dryRun, bool withEnv = true)
        {
            var sb = new StringBuilder();
            var o = Options;
            if (dryRun) sb.AppendLine("DRY RUN -- nothing is started and nothing is written.");
            sb.AppendLine("H2 GPU Lightmapper " + AppVersion + "   payload " + PayloadSource);
            sb.AppendLine("  root      " + Root);
            sb.AppendLine("  work      " + Work);
            sb.AppendLine("  inst      " + InstDir + (SlotBase != null ? "   (DLL slot base " + SlotBase + ")" : ""));
            if (Rt != null) sb.AppendLine("  runtimes  pwsh " + Rt.PwshExe + "   py " + Rt.PyExe);
            sb.AppendLine("  engine    " + Engine);
            sb.AppendLine("  map       " + o.ScenarioRel + "   bsp " + o.Bsp);
            sb.AppendLine("  options   " + o.Summary());
            sb.AppendLine("  workers   " + (Worker.HasValue ? "farm_w" + Worker.Value.ToString("D2", CultureInfo.InvariantCulture) + (WorkerNote != null ? " (" + WorkerNote + ")" : "") : "-")
                          + "   probe " + (ProbeWorker.HasValue ? "farm_w" + ProbeWorker.Value.ToString("D2", CultureInfo.InvariantCulture) + (ProbeWorkerNote != null ? " (" + ProbeWorkerNote + ")" : "") : "-"));
            sb.AppendLine("  probes    " + ProbePoints + " point(s)" + (ProbePointsAssumed ? " ASSUMED (dry run: pass --probe-points N; a real run queries the scenario)" : "")
                          + (ProbePath ? " -> light probes are baked" : " -> no light probes"));
            if (Limits != null && Limits.VramBytes.HasValue)
                sb.AppendLine("  gpu mem   " + (Limits.VramBytes.Value / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB (" + Limits.Source + ") -> "
                              + (Limits.Changes ? "gpu_bake gets" + (Limits.MaxCallsPerPart > 0 ? " -MaxCallsPerPart " + Limits.MaxCallsPerPart : "") + (Limits.BufCapMB > 0 ? " -BufCapMB " + Limits.BufCapMB : "") : "nothing extra (the production argv)"));
            if (LightmapReuseNote != null) sb.AppendLine("  capture   lightmap: " + LightmapReuseNote);
            if (ProbeReuseNote != null) sb.AppendLine("  capture   probes: " + ProbeReuseNote);
            sb.AppendLine("  run dir   " + RunDir + (RunDirPredicted ? "   (would be created)" : ""));
            if (Install) sb.AppendLine("  install   " + (InstallStage != null ? InstallStage + " (lmw_install, with Retry)" : "by gpu_bake -WriteBack (the production direct writer)") + " -> " + TagDir);
            else sb.AppendLine("  install   NO (stage only) -- the final stage: " + FinalStage);
            foreach (var w in Warnings) sb.AppendLine("  WARNING   " + w);
            foreach (var n in Notes) sb.AppendLine("  note      " + n);
            if (withEnv && Env != null)
            {
                List<KeyValuePair<string, string>> shown;
                List<string> inherited;
                SplitEnv(Env, out shown, out inherited);
                sb.AppendLine("ENV (every child process; built from scratch, sorted; the shell's own variables)");
                foreach (var kv in shown) sb.AppendLine("  " + kv.Key + "=" + kv.Value);
                if (inherited.Count > 0) sb.AppendLine("  + " + inherited.Count + " inherited variable(s) passed through unchanged (values not logged): " + string.Join(", ", inherited));
            }
            foreach (var s in Steps)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "STEP {0,2}  {1,-7} {2,-17} [{3}] {4}", s.Index, s.Kind == PlanStepKind.Process ? "process" : "shell", s.Id, s.UiStep, s.Title));
                if (s.FileName != null)
                {
                    sb.AppendLine("    exe   " + s.FileName);
                    for (int i = 0; i < s.Args.Count; i++) sb.AppendLine("    arg" + (i + 1).ToString(CultureInfo.InvariantCulture).PadRight(3) + JobProcess.QuoteArg(s.Args[i]));
                    sb.AppendLine("    cwd   " + s.Cwd);
                    if (s.EnvExtra.Count > 0) foreach (var kv in s.EnvExtra) sb.AppendLine("    env+  " + kv.Key + "=" + kv.Value);
                    if (s.TagWrite) sb.AppendLine("    TAG WRITE (a cancel waits for it)");
                    if (s.SideLog != null) sb.AppendLine("    tails " + s.SideLog);
                }
                if (s.CaptureWorker.HasValue)
                    sb.AppendLine("    capture farm_w" + s.CaptureWorker.Value.ToString("D2", CultureInfo.InvariantCulture) + (s.FreshCapture ? " NEW" : " REUSED") + (s.SkyHi ? " (GPULM_SKY_HI)" : "") + (s.FreshCapture ? " -> " + CaptureRecord.FileName + " written after it" : ""));
                foreach (var kv in s.Params) sb.AppendLine("    " + kv.Key + ": " + Convert.ToString(kv.Value is JObj ? Json.Write(kv.Value, false) : kv.Value is List<object> ? Json.Write(kv.Value, false) : kv.Value, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
