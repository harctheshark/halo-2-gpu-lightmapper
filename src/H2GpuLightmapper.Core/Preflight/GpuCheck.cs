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
                       + (Hybrid ? ", hybrid" : "") + (Ok ? "" : " -- " + (GpuCheck.LacksFp64(this) ? GpuCheck.NoFp64Plain + " (Technical detail: " + string.Join("; ", Reasons) + ")" : string.Join("; ", Reasons)));
            }
        }
    }

    public static class GpuCheck
    {
        /// <summary>The user's decision (0.6.3): GPUs without FP64 are NOT supported, and the refusal says so in plain words.</summary>
        public const string NoFp64Plain = "This GPU can't run the GPU lightmapper: it has no double-precision (FP64) support (Intel integrated/Arc and Qualcomm Adreno GPUs lack it). Use a PC with an NVIDIA or AMD GPU, or bake with the CPU tool.";

        /// <summary>Is the failure the lack of FP64 (double-precision / extended-double shader instructions) on a real hardware adapter?</summary>
        public static bool LacksFp64(GpuCheckResult g)
        {
            if (g == null || g.Ok) return false;
            bool fp64Reason = g.Reasons.Any(r => r.IndexOf("double-precision", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("extended double", StringComparison.OrdinalIgnoreCase) >= 0);
            bool other = g.Reasons.Any(r => r.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("no hardware", StringComparison.OrdinalIgnoreCase) >= 0
                                            || r.IndexOf("feature level", StringComparison.OrdinalIgnoreCase) >= 0);
            bool fieldsSay = g.DefaultAdapter != null && (!g.Doubles || !g.ExtendedDoubles);
            return (fp64Reason || fieldsSay) && !other;
        }

        /// <summary>The preflight's refusal text for a failed GPU check: the plain FP64 sentence + the technical detail, or the generic text.</summary>
        public static string RefusalText(GpuCheckResult g)
        {
            string detail = string.Join("; ", g.Reasons);
            if (LacksFp64(g)) return NoFp64Plain + " (Technical detail: " + (g.DefaultAdapter != null ? g.DefaultAdapter + ": " : "") + detail + ")";
            return "This GPU cannot run the lightmapper: " + detail;
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
            }
            var d = j.Obj("default_adapter");
            if (d != null)
            {
                g.DefaultAdapter = d.Str("name");
                g.FeatureLevel = d.Str("feature_level");
                g.Doubles = d.Bool("double_precision_float_shader_ops") == true;
                g.ExtendedDoubles = d.Bool("extended_doubles_shader_instructions") == true;
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
                g.Reasons.Add(exitCode == 2 ? "gpucheck could not run its checks" : "the default GPU lacks Direct3D 11 FL 11_0 with FP64 (double) shader support -- the lightmapper's kernels require it");
            return g;
        }
    }
}
