// S2.5 the bake options (FINAL_DESIGN section 4). The SAME object backs the GUI and the CLI, and it is never saved: every
// launch starts from these defaults (Super, Lightmap Standard, AO off at 1.0 / 0.5, create a new capture, install).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.App
{
    public enum Quality { Low, Super, Custom }

    /// <summary>Standard = lightmaps + light probes when the BSP has probe points (the default); Probes = light probes only;
    /// Lightmap = lightmaps only (on a probe BSP its N probes are reset to zero, as `tool lightmaps` does).</summary>
    public enum BakeMode { Standard, Probes, Lightmap }

    /// <summary>What to do when AO was asked for but the bake fell back to the tool writer (no stage to apply AO to).</summary>
    public enum AoFallback { Stop, InstallWithoutAo }

    public sealed class BakeOptions
    {
        public const long PhotonsMin = 1000000, PhotonsMax = 100000000, PhotonsDefault = 50000000;
        public const int RaysMin = 1, RaysMax = 64, RaysDefault = 16;
        public const double AoStrengthMin = 0.01, AoStrengthMax = 2.0, AoStrengthDefault = 1.0;
        public const double AoExponentMin = 0.1, AoExponentMax = 8.0, AoExponentDefault = 0.5;
        /// <summary>Super's photon budget and secondary MC (the tool's super row: {4, 8, 0, 20000000, 0, 4.0, 0}).</summary>
        public const long SuperPhotons = 20000000;
        public const int SuperMc = 8;

        public string Root, ScenarioRel, Bsp;
        public Quality Quality = Quality.Super;
        public long Photons = PhotonsDefault;
        /// <summary>The tool's secondary Monte-Carlo samples (gpu_bake -MCSamples): rays per gather call = 2 * MC^2.</summary>
        public int Rays = RaysDefault;
        public BakeMode Mode = BakeMode.Standard;
        public bool Ao;
        public double AoStrength = AoStrengthDefault, AoExponent = AoExponentDefault;
        public bool ReuseCapture;
        /// <summary>Advanced "Stage only (don't write tags)" / CLI --no-install.</summary>
        public bool StageOnly;
        /// <summary>Dev: a fixed worker (200-299) instead of the registry's.</summary>
        public int? Worker, ProbeWorker;
        /// <summary>Plücker + Scenery fixes: always on and not shown (dev CLI flag --dev-no-fixes turns them off).</summary>
        public bool Fixes = true;
        /// <summary>Reuse even when the tags fingerprint changed (the GUI asks; the CLI needs --yes).</summary>
        public bool AcceptChangedTags;
        /// <summary>Dev: reuse a capture that has no standalone record (a production / research capture, gate T2-B). gpu_bake's
        /// own -SkipCapture checks still run.</summary>
        public bool ReuseUnrecorded;
        public AoFallback AoFallback = AoFallback.Stop;
        /// <summary>Review fix (finding 9): installing without the light probes on a probe BSP (the map fell back to the tool
        /// writer) resets the BSP's probes to zero; the CLI needs its own --probes-fallback install for that.</summary>
        public AoFallback ProbesFallback = AoFallback.Stop;

        public static BakeOptions Defaults() { return new BakeOptions(); }

        public BakeOptions Clone() { return (BakeOptions)MemberwiseClone(); }

        public string QualityToken { get { return Quality.ToString().ToLowerInvariant(); } }
        public string ModeToken { get { return Mode == BakeMode.Probes ? "probes" : Mode == BakeMode.Lightmap ? "lightmap" : "standard"; } }

        /// <summary>The trace quality P6's `gpu_ao trace --quality` takes: low -> low, super / custom -> super (ssN 4 = super's cells).</summary>
        public string AoTraceQuality { get { return Quality == Quality.Low ? "low" : "super"; } }

        /// <summary>Rays per gather call = 2 * MC^2 (Super's MC 8 gives 128).</summary>
        public static long RaysPerCall(int mc) { return 2L * mc * mc; }
        public long RaysPerCallNow { get { return Quality == Quality.Custom ? RaysPerCall(Rays) : Quality == Quality.Super ? RaysPerCall(SuperMc) : 0; } }

        /// <summary>fullgpu's third argument for custom (gpu_bake P2): clamp(floor(524288 / (2 MC^2)), 64, 4096).</summary>
        public static int CustomChunk(int mc) { return (int)Math.Min(4096, Math.Max(64, Math.Floor(524288.0 / (2.0 * mc * mc)))); }

        /// <summary>AO tag "s&lt;strength&gt;e&lt;exponent&gt;" with the decimal point dropped: 1.0 / 0.5 -> s10e05, 0.75 / 1.0 -> s075e10.</summary>
        public string AoTag { get { return "s" + AoNum(AoStrength) + "e" + AoNum(AoExponent); } }

        public static string AoNum(double v)
        {
            return v.ToString("0.0#####", CultureInfo.InvariantCulture).Replace(".", "");
        }

        /// <summary>"scenarios\multi\cyclotron\cyclotron" from any of: that, with ".scenario", forward slashes, or a full path
        /// inside ROOT\tags. Null when it cannot be made relative.</summary>
        public static string NormalizeScenario(string root, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Trim('"').Replace('/', '\\');
            if (s.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
            if (Path.IsPathRooted(s))
            {
                if (root == null) return null;
                string tags = Path.Combine(Path.GetFullPath(root).TrimEnd('\\'), "tags") + "\\";
                string full = Path.GetFullPath(s);
                if (!full.StartsWith(tags, StringComparison.OrdinalIgnoreCase)) return null;
                s = full.Substring(tags.Length);
            }
            s = s.Trim('\\');
            if (s.Length == 0 || s.Split('\\').Any(p => p == ".." || p == "." || p.Length == 0)) return null;
            return s;
        }

        /// <summary>
        /// Review fix (finding 22): the scenario path in its ON-DISK case ("scenarios\multi\cyclotron\cyclotron" even when the
        /// user typed "Scenarios\Multi\Cyclotron\Cyclotron"): gpu_bake builds the lightmap name, the tag reference lm_tagwriter
        /// writes into the .scenario on a first bake and the installed file names from it, exactly like the production launcher
        /// (which passes the listing's case). Null when a segment does not exist.
        /// </summary>
        public static string CanonicalScenario(string root, string rel)
        {
            if (root == null || rel == null) return null;
            string cur = Path.Combine(root, "tags");
            var parts = rel.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var outp = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                string want = last ? parts[i] + ".scenario" : parts[i];
                if (!Directory.Exists(cur)) return null;
                var hit = new DirectoryInfo(cur).EnumerateFileSystemInfos(want).FirstOrDefault(e => string.Equals(e.Name, want, StringComparison.OrdinalIgnoreCase)
                                                                                                && (last ? (e.Attributes & FileAttributes.Directory) == 0 : (e.Attributes & FileAttributes.Directory) != 0));
                if (hit == null) return null;
                outp.Add(last ? hit.Name.Substring(0, hit.Name.Length - ".scenario".Length) : hit.Name);
                cur = hit.FullName;
            }
            return outp.Count == 0 ? null : string.Join("\\", outp);
        }

        /// <summary>The BSP name in its on-disk case (the "&lt;bsp&gt;.scenario_structure_bsp" file in <paramref name="tagDir"/>),
        /// or null when there is none.</summary>
        public static string CanonicalBsp(string tagDir, string bsp)
        {
            if (tagDir == null || bsp == null || !Directory.Exists(tagDir)) return null;
            string want = bsp + ".scenario_structure_bsp";
            var hit = new DirectoryInfo(tagDir).EnumerateFiles(want).FirstOrDefault(e => string.Equals(e.Name, want, StringComparison.OrdinalIgnoreCase));
            return hit == null ? null : hit.Name.Substring(0, hit.Name.Length - ".scenario_structure_bsp".Length);
        }

        /// <summary>Errors that refuse the bake before anything is written (CLI exit 2). <paramref name="customAvailable"/> is
        /// <see cref="AppHost.CustomAvailable"/>: the payload carries the guarded Custom engine (or the dev --dev-custom-unguarded
        /// flag was given). Since 0.6.1 there is no other switch: Custom is Experimental, not hidden.</summary>
        public List<string> Validate(bool customAvailable)
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(Root)) e.Add("no H2EK folder given");
            if (string.IsNullOrWhiteSpace(ScenarioRel)) e.Add("no scenario given");
            if (string.IsNullOrWhiteSpace(Bsp)) e.Add("no BSP given");
            else if (Bsp.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || Bsp.Trim() != Bsp) e.Add("the BSP name '" + Bsp + "' is not a plain tag name");
            if (Quality == Quality.Custom)
            {
                if (!customAvailable) e.Add("Custom quality needs the N3 guarded fullgpu (engine_custom), which this payload does not carry -- without it a photon count past the uint32 limit wraps silently; dev: --dev-custom-unguarded");
                if (Photons < PhotonsMin || Photons > PhotonsMax) e.Add(string.Format(CultureInfo.InvariantCulture, "photons must be {0:N0} - {1:N0} (got {2:N0})", PhotonsMin, PhotonsMax, Photons));
                if (Rays < RaysMin || Rays > RaysMax) e.Add("gather rays (MC samples) must be " + RaysMin + " - " + RaysMax + " (got " + Rays + ")");
            }
            if (Ao)
            {
                if (Mode == BakeMode.Probes) e.Add("ambient occlusion is a lightmap layer: it is not available in Lightprobes mode");
                if (!(AoStrength >= AoStrengthMin && AoStrength <= AoStrengthMax)) e.Add(string.Format(CultureInfo.InvariantCulture, "AO strength must be {0} - {1} (got {2})", AoStrengthMin, AoStrengthMax, AoStrength));
                if (!(AoExponent >= AoExponentMin && AoExponent <= AoExponentMax)) e.Add(string.Format(CultureInfo.InvariantCulture, "AO exponent must be {0} - {1} (got {2})", AoExponentMin, AoExponentMax, AoExponent));
            }
            foreach (var w in new[] { Worker, ProbeWorker })
                if (w.HasValue && !Core.Safety.Guards.WorkerAllowed(w.Value)) e.Add("worker " + w.Value + " is not allowed (the standalone uses 200-299; farm_w149 never)");
            if (Worker.HasValue && ProbeWorker.HasValue && Worker.Value == ProbeWorker.Value) e.Add("the probe worker must differ from the lightmap worker");
            return e;
        }

        public string Summary()
        {
            var s = "quality " + QualityToken;
            if (Quality == Quality.Custom) s += string.Format(CultureInfo.InvariantCulture, " (Experimental: {0:N0} photons, MC {1} = {2:N0} rays per gather call)", Photons, Rays, RaysPerCall(Rays));
            s += "   mode " + ModeToken;
            s += "   AO " + (Ao ? string.Format(CultureInfo.InvariantCulture, "on (strength {0}, exponent {1})", AoStrength, AoExponent) : "off");
            s += "   capture " + (ReuseCapture ? "reuse" : "new");
            s += "   " + (StageOnly ? "stage only (no tags written)" : "install");
            if (!Fixes) s += "   FIXES OFF (dev)";
            return s;
        }
    }
}
