using System;
using System.Collections.Generic;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Preflight
{
    /// <summary>The S1.6 gpucheck.exe verdict (schema 1; exit 0 ok, 1 not ok, 2 internal error).</summary>
    public sealed class GpuCheckResult
    {
        public bool Ran;
        public int ExitCode = -1;
        public bool Ok;
        public List<string> Reasons = new List<string>();
        public List<string> Advice = new List<string>();
        public string DefaultAdapter;
        public string FeatureLevel;
        public bool Doubles, ExtendedDoubles, Hybrid;
        /// <summary>gpucheck 1.2.0 (verdict.notes): facts that are not failures, e.g. "the GPU has no double-precision (FP64) shader support".
        /// An older gpucheck put those in <see cref="Reasons"/> (and exited 1); the app moves them here when it accepts the GPU.</summary>
        public List<string> Notes = new List<string>();
        /// <summary>gpucheck 1.2.0 default_adapter.compat_mode: the engine's own rule says "use the FP32-only DF64 kernels" (null on an older gpucheck).</summary>
        public bool? CompatModeReported;
        /// <summary>Set by the preflight when the GPU has no FP64 and the engine this bake runs has Compatibility mode (DF64): the GPU is accepted
        /// (<see cref="Ok"/> is then true) and the bake runs the FP32-only kernels.</summary>
        public bool CompatAccepted;
        public double? EffectiveTdrSeconds;
        /// <summary>X1: dedicated video memory (bytes) of the default adapter (the adapters[] entry with its luid); null when gpucheck did not say.</summary>
        public long? DedicatedVideoMemoryBytes;
        /// <summary>X2: IDXGIAdapter3::QueryVideoMemoryInfo(LOCAL) of that adapter at check time: the OS budget for this process, and this
        /// process's own use (about 0 in gpucheck). Null when gpucheck predates the figure or the query failed.</summary>
        public long? LiveBudgetBytes, LiveProcessUsageBytes;
        /// <summary>X2: the system-wide "GPU Adapter Memory / Dedicated Usage" counter of that adapter (every program, as Task Manager shows it); null when unavailable.</summary>
        public long? SystemDedicatedUsageBytes;
        public string Error;
        public JObj Raw;

        /// <summary>The Advanced-panel GPU line.</summary>
        public string Line
        {
            get
            {
                if (!Ran) return "GPU: not checked" + (Error != null ? " (" + Error + ")" : "");
                if (Raw == null) return "GPU: gpucheck failed (exit " + ExitCode + (Error != null ? ", " + Error : "") + ")";
                return "GPU: " + (DefaultAdapter ?? "?") + ", FL " + (FeatureLevel ?? "?") + ", FP64 " + (Doubles && ExtendedDoubles ? "yes" : "NO")
                       + (CompatAccepted ? " (Compatibility mode)" : "")
                       + (Hybrid ? ", hybrid" : "") + (Ok ? "" : " -- " + (GpuCheck.LacksFp64(this) ? GpuCheck.NoFp64Plain + " (Technical detail: " + GpuCheck.Fp64Detail(this) + ")" : string.Join("; ", Reasons)));
            }
        }
    }

    /// <summary>Which kernel set a bake runs: unknown (GPU not checked / dry run), the native FP64 kernels, the FP32-only DF64 kernels chosen
    /// automatically because the GPU has no FP64, or the DF64 kernels forced for testing (<c>--force-compat</c>, GPULM_COMPAT=1).</summary>
    public enum PrecisionMode { Unknown, Full, Compat, CompatForced }

    public static class GpuCheck
    {
        /// <summary>The user's decision (0.6.3): GPUs without FP64 are NOT supported, and the refusal says so in plain words.</summary>
        public const string NoFp64Plain = "This GPU can't run the GPU lightmapper: it has no double-precision (FP64) support (Intel integrated/Arc and Qualcomm Adreno GPUs lack it). Use a PC with an NVIDIA or AMD GPU, or bake with the CPU tool.";

        // ---- Compatibility mode (DF64): engine 0.6.4. A GPU without FP64 runs the engine's FP32-only double-single kernels, picked by fullgpu itself.
        // The wording below is what the main window, the bake log header, the dry-run plan and the run summary show.
        public const string PrecisionFull = "Precision: Full FP64";
        public const string PrecisionCompat = "Precision: Compatibility mode (DF64) - your GPU has no FP64, using FP32 double-single maths instead. Experimental; same results in testing, may be slower.";
        public const string PrecisionCompatForced = "Precision: Compatibility mode (DF64) - forced for testing (--force-compat); this GPU's FP64 is not used. Experimental; same results in testing, may be slower.";
        /// <summary>The GPU is not known yet (not checked, or a dry run) and the engine HAS Compatibility mode.</summary>
        public const string PrecisionAutomatic = "Precision: automatic - Full FP64 on a GPU with FP64; a GPU without it runs in Compatibility mode (DF64), which this engine supports.";
        /// <summary>The GPU is not known yet and the engine has NO Compatibility mode.</summary>
        public const string PrecisionNeedsFp64 = "Precision: Full FP64 - this engine needs a GPU with FP64 (it has no Compatibility mode).";
        /// <summary>The environment variable the engine validates (gpu_bake.ps1 of engine 0.6.4); 1 forces the DF64 kernels, 0 the native ones, unset = automatic.</summary>
        public const string CompatEnvVar = "GPULM_COMPAT";

        public static string PrecisionText(PrecisionMode m, bool engineHasCompat)
        {
            switch (m)
            {
                case PrecisionMode.Full: return PrecisionFull;
                case PrecisionMode.Compat: return PrecisionCompat;
                case PrecisionMode.CompatForced: return PrecisionCompatForced;
                default: return engineHasCompat ? PrecisionAutomatic : PrecisionNeedsFp64;
            }
        }

        private static bool HasOtherReason(GpuCheckResult g)
        {
            return g.Reasons.Any(r => r.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("no hardware", StringComparison.OrdinalIgnoreCase) >= 0
                                      || r.IndexOf("feature level", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Does the default hardware adapter lack FP64 (double-precision shader ops or the extended double instructions) and nothing else
        /// is wrong with it? True for gpucheck 1.2.0 (verdict ok, <c>compat_mode</c>) and for an older gpucheck (verdict not ok, FP64 reasons only):
        /// the answer comes from the adapter's fields, not from the verdict. Such a GPU runs in Compatibility mode (DF64) on an engine that has it
        /// and is refused (<see cref="NoFp64Plain"/>) on one that does not.</summary>
        public static bool NoFp64(GpuCheckResult g)
        {
            return g != null && g.Ran && g.Raw != null && g.DefaultAdapter != null && (!g.Doubles || !g.ExtendedDoubles) && !HasOtherReason(g);
        }

        /// <summary>Applies the engine's capability to a GPU check: a GPU without FP64 is ACCEPTED (<see cref="GpuCheckResult.Ok"/> true,
        /// <see cref="GpuCheckResult.CompatAccepted"/> true, returns <see cref="PrecisionMode.Compat"/>) when the engine has Compatibility mode (DF64)
        /// and REFUSED (Ok false, returns Unknown; <see cref="RefusalText"/> is the plain sentence) when it does not; a GPU with FP64 returns
        /// <see cref="PrecisionMode.Full"/> (when its verdict is ok). Unknown for a check that did not run or a GPU that fails for another reason.
        /// Safe to call again with a different engine flag (the window does, when the quality changes the engine).</summary>
        public static PrecisionMode ApplyEngine(GpuCheckResult g, bool compatEngine)
        {
            if (g == null || !g.Ran || g.Raw == null) return PrecisionMode.Unknown;
            if (NoFp64(g))
            {
                if (!compatEngine) { g.Ok = false; g.CompatAccepted = false; return PrecisionMode.Unknown; }
                if (!g.CompatAccepted) { g.Notes.AddRange(g.Reasons); g.Reasons.Clear(); }
                g.Ok = true; g.CompatAccepted = true;
                return PrecisionMode.Compat;
            }
            return g.Ok ? PrecisionMode.Full : PrecisionMode.Unknown;
        }

        /// <summary>Is the failure the lack of FP64 (double-precision / extended-double shader instructions) on a real hardware adapter?</summary>
        public static bool LacksFp64(GpuCheckResult g)
        {
            if (g == null || g.Ok) return false;
            bool fp64Reason = g.Reasons.Any(r => r.IndexOf("double-precision", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("extended double", StringComparison.OrdinalIgnoreCase) >= 0);
            bool fieldsSay = g.DefaultAdapter != null && (!g.Doubles || !g.ExtendedDoubles);
            return (fp64Reason || fieldsSay) && !HasOtherReason(g);
        }

        /// <summary>The preflight's refusal text for a failed GPU check: the plain FP64 sentence + the technical detail, or the generic text.</summary>
        public static string RefusalText(GpuCheckResult g)
        {
            if (NoFp64(g) || LacksFp64(g))
                return NoFp64Plain + " (Technical detail: " + (g.DefaultAdapter != null ? g.DefaultAdapter + ": " : "") + Fp64Detail(g) + ")";
            return "This GPU cannot run the lightmapper: " + string.Join("; ", g.Reasons);
        }

        /// <summary>The engine-side / gpucheck wording of why the adapter has no FP64 (reasons + notes; built from the adapter's fields when gpucheck gave none).</summary>
        public static string Fp64Detail(GpuCheckResult g)
        {
            var parts = new List<string>(g.Reasons);
            parts.AddRange(g.Notes);
            if (parts.Count == 0)
            {
                if (!g.Doubles) parts.Add("the GPU has no double-precision (FP64) shader support");
                if (!g.ExtendedDoubles) parts.Add("the GPU lacks extended double-precision shader instructions (ddiv/dfma/drcp)");
            }
            return string.Join("; ", parts);
        }

        /// <summary>Runs gpucheck.exe hidden (CREATE_NO_WINDOW, job, BELOW_NORMAL) and parses its stdout JSON. Brief device
        /// creation only; it writes nothing.</summary>
        public static GpuCheckResult Run(string exe, int timeoutMs = 30000)
        {
            var r = new ProcessRunner.Result();
            try { r = ProcessRunner.RunCapture(exe, new string[0], System.IO.Path.GetDirectoryName(exe), null, timeoutMs); }
            catch (Exception e) { return new GpuCheckResult { Ran = false, Error = e.Message }; }
            if (r.TimedOut) return new GpuCheckResult { Ran = true, ExitCode = -1, Error = "timed out" };
            return Parse(r.Stdout, r.ExitCode);
        }

        public static GpuCheckResult Parse(string json, int exitCode)
        {
            var g = new GpuCheckResult { Ran = true, ExitCode = exitCode };
            JObj j;
            try { j = Json.ParseObj(json); }
            catch (Exception e) { g.Error = "unreadable output: " + e.Message; return g; }
            g.Raw = j;
            var v = j.Obj("verdict");
            g.Ok = v != null && v.Bool("ok") == true && exitCode == 0;
            if (v != null)
            {
                if (v.Arr("reasons") != null) g.Reasons.AddRange(v.Arr("reasons").Select(x => Convert.ToString(x)));
                if (v.Arr("advice") != null) g.Advice.AddRange(v.Arr("advice").Select(x => Convert.ToString(x)));
                if (v.Arr("notes") != null) g.Notes.AddRange(v.Arr("notes").Select(x => Convert.ToString(x)));
            }
            var d = j.Obj("default_adapter");
            if (d != null)
            {
                g.DefaultAdapter = d.Str("name");
                g.FeatureLevel = d.Str("feature_level");
                g.Doubles = d.Bool("double_precision_float_shader_ops") == true;
                g.ExtendedDoubles = d.Bool("extended_doubles_shader_instructions") == true;
                g.CompatModeReported = d.Bool("compat_mode");
            }
            g.Hybrid = j.Bool("hybrid") == true;
            // X1: the dedicated VRAM of the adapter fullgpu will use = the adapters[] entry with the default adapter's luid
            // (a software adapter, e.g. the Microsoft Basic Render Driver, is never taken)
            var adapters = j.Arr("adapters");
            if (adapters != null)
            {
                string luid = d == null ? null : d.Str("luid");
                JObj pick = null;
                foreach (var o in adapters)
                {
                    var ad = o as JObj;
                    if (ad == null || ad.Bool("software") == true) continue;
                    if (luid != null && string.Equals(ad.Str("luid"), luid, StringComparison.OrdinalIgnoreCase)) { pick = ad; break; }
                    if (pick == null && luid == null) pick = ad;
                }
                if (pick != null)
                {
                    long? vram = pick.Long("dedicated_video_memory");
                    if (vram.HasValue && vram.Value > 0) g.DedicatedVideoMemoryBytes = vram;
                    var vm = pick.Obj("video_memory");
                    if (vm != null && vm.Bool("queried") == true)
                    {
                        long? bud = vm.Long("budget"), cu = vm.Long("current_usage");
                        if (bud.HasValue && bud.Value > 0) { g.LiveBudgetBytes = bud; g.LiveProcessUsageBytes = cu; }
                    }
                    if (vm != null) { long? sys = vm.Long("system_dedicated_usage"); if (sys.HasValue && sys.Value >= 0) g.SystemDedicatedUsageBytes = sys; }
                }
            }
            var tdr = j.Obj("tdr");
            if (tdr != null && tdr["effective_delay_s"] != null) g.EffectiveTdrSeconds = Convert.ToDouble(tdr["effective_delay_s"]);
            if (!g.Ok && g.Reasons.Count == 0)
                g.Reasons.Add(exitCode == 2 ? "gpucheck could not run its checks" : "the default GPU lacks Direct3D 11 at feature level 11_0 -- the lightmapper's kernels require it");
            return g;
        }
    }
}
