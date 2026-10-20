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
                       + (Hybrid ? ", hybrid" : "") + (Ok ? "" : " -- " + string.Join("; ", Reasons));
            }
        }
    }

    public static class GpuCheck
    {
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
