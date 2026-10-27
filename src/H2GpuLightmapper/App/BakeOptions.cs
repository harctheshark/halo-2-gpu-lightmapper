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
        /// <summary>X2 Advanced settings "Keep PC responsive - Slower Bakes!" / CLI --responsive: every gpu_bake the plan builds gets
        /// -Responsive (the engine splits its GPU work into short pieces; the lightmap is identical, the bake takes longer). Default off:
        /// the argv is then byte-identical to before this option existed. Not remembered between launches (like every bake option).</summary>
        public bool Responsive;
        /// <summary>W2 (0.6.3) Advanced settings "2004 Exact Vertex Lighting?" / CLI --exact-vertex-lighting: every gpu_bake that writes the tags gets
        /// -ExactVertexLighting (the direct writer re-runs the CPU's 7-probe per-vertex visibility gate, so vertex-lit records match the 2004 tool
        /// more closely; the tag writer is about 2 minutes slower on cyclotron). Default off: the argv is then byte-identical to before.</summary>
        public bool ExactVertexLighting;
        public const string ExactVertexLightingNote = "exact vertex lighting on (slower tag writer)";
        public const string ExactVertexLightingLabel = "2004 Exact Vertex Lighting?";
        public const string ExactVertexLightingTip = "You likely don't need this, this is just to shut Harcs brain up";
        /// <summary>Advanced settings "Scenery Multi Permutation Fix (Experimental Improvement)" / CLI --scenery-permutation-fix: every gpu_bake the plan builds
        /// gets -SceneryOwnPermutationOnly (scenery that draws more than one shape from one tag: only the shape the game draws casts shadows, ground hidden
        /// inside scenery is filled from its surroundings, a variant's own pieces get lit). Differs from the 2004 tool on purpose. Default off: the argv is then
        /// byte-identical to before. ENGINE-GATED: only an engine whose gpu_bake.ps1 declares the parameter may be given it (see AppHost.SceneryPermutationFixWhy):
        /// the window greys the checkbox, the command line and the session refuse -- an unknown parameter is never passed to PowerShell.
        /// The capture is made WITH the option (the engine records the tool's permutation pick in phase 1), so it is part of the capture identity.</summary>
        public bool SceneryPermutationFix;
        public const string SceneryPermutationFixSwitch = "SceneryOwnPermutationOnly";
        public const string SceneryPermutationFixNote = "scenery multi permutation fix on (experimental; differs from the 2004 tool on purpose)";
        public const string SceneryPermutationFixLabel = "Scenery Multi Permutation Fix (Experimental Improvement)";
        public const string SceneryPermutationFixNeedsEngine = "Needs engine 0.6.4 or newer.";
        public const string SceneryPermutationFixTip = "Fallback for scenery that draws more than one shape from one tag, e.g. a rock whose shape changes when you move it, or a lifepod placed with a named variant like 'crashed'. "
            + "Only the shape the game actually draws casts shadows, ground hidden inside scenery is filled from its surroundings, and a variant's own pieces get lit instead of rendering black. "
            + "Differs from the 2004 tool on purpose. Most maps never need this: giving each shape its own scenery tag avoids the problem.";
        /// <summary>CLI-ONLY testing override <c>--force-compat</c> (not in the window, not in Advanced settings): gives every gpu_bake / AO trace / light probe step
        /// <c>GPULM_COMPAT=1</c> so a GPU WITH FP64 runs the FP32-only Compatibility-mode (DF64) kernels. ENGINE-GATED: only an engine whose gpu_bake.ps1 validates
        /// GPULM_COMPAT (engine 0.6.4 or newer) may be given it (see AppHost.CompatEngineWhy); otherwise the bake is refused up front, dry runs too, and the
        /// variable is never passed. Compatibility mode itself is automatic (the engine picks it for a GPU without FP64) and has no option.</summary>
        public bool ForceCompat;
        public const string ForceCompatNote = "Compatibility mode (DF64) FORCED for testing (--force-compat: GPULM_COMPAT=1 on every GPU step)";
        public const string ForceCompatNeedsEngine = "Compatibility mode needs engine 0.6.4 or newer.";
        /// <summary>0.6.4 cross-folder BSP: the tags-relative path (no extension, backslashes) of the BSP tag when it lives in ANOTHER folder than the scenario
        /// (01b_spacestation -> scenarios\solo\01a_tutorial\01_bsp_1). Null = the BSP is beside the scenario (every pre-0.6.4 bake). <see cref="Bsp"/> stays the plain
        /// leaf name: that is what gpu_bake -Bsp takes (engine 0.6.4 resolves the real file from the scenario's structure_bsps block; the lightmap tags are
        /// still named and written in the SCENARIO's folder). Set by the window's BSP picker; the session also resolves it from the scenario when a command-line
        /// bake names a BSP that is not beside the scenario. ENGINE-GATED (see AppHost.CrossFolderBspWhy).</summary>
        public string BspRel;
        /// <summary>gpu_bake.ps1 parameters that only an engine with the cross-folder BSP resolution (dev_S, merged into engine 0.6.4) declares: -WriterOnly and
        /// -ToolOverlay come from the same patch as lmw_skeleton.py resolve-bsp, so an engine that has them resolves the BSP from the scenario.</summary>
        public static readonly string[] CrossFolderBspSwitches = { "WriterOnly", "ToolOverlay" };
        public const string CrossFolderBspNeedsEngine = "Baking a BSP from another folder needs engine 0.6.4 or newer.";
        /// <summary>0.6.5: the marker line (<c>SPLIT_BSP_SUPPORT = 1</c>) a payload's gpu_ao.py / gpu_lightprobes.py carries when it reads the BSP from the file the scenario's
        /// structure_bsps names. Without it (a 0.6.4 payload) AO and light probes stay refused for a BSP from another folder.</summary>
        public const string SplitBspMarker = "SPLIT_BSP_SUPPORT";
        public const string CrossFolderBspNoAoProbes = "this BSP lives in another folder than the scenario; ambient occlusion and light probes are not supported for it yet "
            + "(only the lightmap is): untick Ambient occlusion and use Lightmap Only (CLI: --mode lightmap)";
        public const string ResponsiveLabel ="Keep PC responsive - Slower Bakes!";
        public const string ResponsiveTip = "Splits the GPU work into short pieces so your desktop, videos and games stay smooth while it bakes. The lightmap is identical; the bake takes longer.";

        // ------------------------------------------------------------------------------------------------------------------ direct .map baking (milestone 2)
        /// <summary>The compiled .map to bake from (MCC Halo 2 cache built with the Halo 2 Editing Kit). Null = an ordinary scenario bake: every pre-existing bake,
        /// whose argv and plan must not change by even a byte. Non-null = <see cref="Pipeline.MapBakeSession"/>: the dependency subset of the map is read into a temporary
        /// work folder, baked there in a real-copy overlay root, and ONLY the lightmap and its bitmap are injected into a COPY of the map.</summary>
        public string MapPath;
        /// <summary>The output map (null = <c>&lt;input folder&gt;\&lt;name&gt;_gpulm.map</c>). Never the input.</summary>
        public string MapOut;
        /// <summary>Per sky: sky tag name (or its last part) -> a cube-map bitmap tag path, or "none" (leave the Cube Map Reference empty), or "auto" (the automatic
        /// match). A sky the map has that is not listed gets the preselected default (the automatic match, else none). Only meaningful with <see cref="MapPath"/>.</summary>
        public List<KeyValuePair<string, string>> SkyCubemaps = new List<KeyValuePair<string, string>>();
        public bool IsMapBake { get { return MapPath != null; } }
        public const string MapOutSuffix = "_gpulm", MapOutMccSuffix = "_gpulm_mcc";
        /// <summary>Milestone 3: the input map is a Vista map (cache v8). Set from the map's header by the window, the command line and the session (the session's reading is the one that counts).
        /// A Vista map is converted to MCC form, baked like an MCC map and, by <see cref="VistaOut"/>, converted back.</summary>
        public bool MapIsVista;
        /// <summary>Vista input only: convert the baked MCC result back to a Vista map (default yes = the window's checkbox "Convert result back to Vista", on). No = the MCC map is the result.
        /// <see cref="VistaOutGiven"/> is true when the user (CLI --vista-out) chose; giving it for a non-Vista map is refused.</summary>
        public bool VistaOut = true;
        public bool VistaOutGiven;
        public const string VistaOutNotVista = "--vista-out is for a Vista map only (this map is an MCC map, which is baked and written as an MCC map)";

        /// <summary>"&lt;input folder&gt;\&lt;name&gt;_gpulm.map" next to the input; <paramref name="mccIntermediate"/> (a Vista map whose result stays an MCC map): "&lt;name&gt;_gpulm_mcc.map".</summary>
        public static string DefaultMapOut(string mapPath, bool mccIntermediate = false)
        {
            string full = Path.GetFullPath(mapPath.Trim().Trim('"'));
            return Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + (mccIntermediate ? MapOutMccSuffix : MapOutSuffix) + ".map");
        }

        /// <summary>True when the result is an MCC map made from a Vista map (the user chose not to convert back).</summary>
        public bool ResultIsMccFromVista { get { return MapIsVista && !VistaOut; } }

        /// <summary>The output path this bake writes (the user's, else the default next to the input).</summary>
        public string MapOutPath { get { return string.IsNullOrWhiteSpace(MapOut) ? DefaultMapOut(MapPath, ResultIsMccFromVista) : Path.GetFullPath(MapOut.Trim().Trim('"')); } }

        public static BakeOptions Defaults() { return new BakeOptions(); }

        public BakeOptions Clone() { var c = (BakeOptions)MemberwiseClone(); c.SkyCubemaps = new List<KeyValuePair<string, string>>(SkyCubemaps); return c; }


        /// <summary>The folder part of a tags-relative tag path ("scenarios\solo\01a_tutorial" for "scenarios\solo\01a_tutorial\01_bsp_1").</summary>
        public static string BspFolder(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            int i = rel.LastIndexOf('\\');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        /// <summary>The BSP tag file this bake lights: the file named by <see cref="BspRel"/> (another folder), else "&lt;bsp&gt;.scenario_structure_bsp" in
        /// <paramref name="scenarioTagDir"/> (beside the scenario, every pre-0.6.4 bake).</summary>
        public string BspTagFile(string scenarioTagDir)
        {
            if (BspRel != null && Root != null) return Path.Combine(Root, "tags", BspRel + ".scenario_structure_bsp");
            return Path.Combine(scenarioTagDir, Bsp + ".scenario_structure_bsp");
        }

        /// <summary>A tags-relative path that cannot leave the tags folder (no rooted path, no ".." / "." / empty parts).</summary>
        public static bool IsPlainTagRel(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '/' }) >= 0 || rel.Trim() != rel) return false;
            return !rel.Split('\\').Any(p => p == ".." || p == "." || p.Length == 0);
        }

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
                string tags = Path.Combine(H2GpuLightmapper.Core.Payload.ShellPaths.NormalizeRoot(root), "tags") + "\\";   // X1: a drive root stays "D:\"
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
            if (IsMapBake) return ValidateMapBake(customAvailable);
            if (string.IsNullOrWhiteSpace(Root)) e.Add("no H2EK folder given");
            if (string.IsNullOrWhiteSpace(ScenarioRel)) e.Add("no scenario given");
            if (string.IsNullOrWhiteSpace(Bsp)) e.Add("no BSP given");
            else if (Bsp.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || Bsp.Trim() != Bsp) e.Add("the BSP name '" + Bsp + "' is not a plain tag name");
            if (BspRel != null)
            {
                if (!IsPlainTagRel(BspRel)) e.Add("the BSP tag path '" + BspRel + "' is not a plain tags-relative path");
                else if (!string.IsNullOrWhiteSpace(Bsp) && !string.Equals(BspRel.Substring(BspRel.LastIndexOf('\\') + 1), Bsp, StringComparison.OrdinalIgnoreCase))
                    e.Add("the BSP tag path '" + BspRel + "' does not end in the BSP name '" + Bsp + "'");
            }
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

        /// <summary>Validation of a .map bake (<see cref="MapPath"/> set). The scenario / BSP are decided from the map; AO and light probes are refused.</summary>
        private List<string> ValidateMapBake(bool customAvailable)
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(Root)) e.Add("no H2EK folder given");
            if (!string.IsNullOrWhiteSpace(ScenarioRel)) e.Add("give either a scenario or a .map, not both");
            if (string.IsNullOrWhiteSpace(MapPath) || !MapPath.Trim().Trim('"').EndsWith(".map", StringComparison.OrdinalIgnoreCase)) e.Add("the map file must be a .map");
            else
            {
                try
                {
                    string inp = Path.GetFullPath(MapPath.Trim().Trim('"')), outp = MapOutPath;
                    if (string.Equals(inp, outp, StringComparison.OrdinalIgnoreCase)) e.Add("the output map would overwrite the input map: choose another output file");
                    if (!outp.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) e.Add("the output file must end in .map");
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { e.Add("not a usable map or output path: " + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(Bsp) && (Bsp.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || Bsp.Trim() != Bsp)) e.Add("the BSP name '" + Bsp + "' is not a plain tag name");
            if (Ao && Mode == BakeMode.Probes) e.Add("ambient occlusion is a lightmap layer: it is not available in Lightprobes mode");
            if (Ao)
            {
                if (!(AoStrength >= AoStrengthMin && AoStrength <= AoStrengthMax)) e.Add(string.Format(CultureInfo.InvariantCulture, "AO strength must be {0} - {1} (got {2})", AoStrengthMin, AoStrengthMax, AoStrength));
                if (!(AoExponent >= AoExponentMin && AoExponent <= AoExponentMax)) e.Add(string.Format(CultureInfo.InvariantCulture, "AO exponent must be {0} - {1} (got {2})", AoExponentMin, AoExponentMax, AoExponent));
            }
            if (VistaOutGiven && !MapIsVista) e.Add(VistaOutNotVista);
            if (ReuseCapture) e.Add("a capture cannot be reused when baking from a .map (every .map bake makes a new capture in its own temporary copy)");
            if (Quality == Quality.Custom)
            {
                if (!customAvailable) e.Add("Custom quality needs the N3 guarded fullgpu (engine_custom), which this payload does not carry");
                if (Photons < PhotonsMin || Photons > PhotonsMax) e.Add(string.Format(CultureInfo.InvariantCulture, "photons must be {0:N0} - {1:N0} (got {2:N0})", PhotonsMin, PhotonsMax, Photons));
                if (Rays < RaysMin || Rays > RaysMax) e.Add("gather rays (MC samples) must be " + RaysMin + " - " + RaysMax + " (got " + Rays + ")");
            }
            foreach (var kv in SkyCubemaps)
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) e.Add("--sky-cubemap needs <sky>=<bitmap tag path | none>");
            foreach (var w in new[] { Worker, ProbeWorker })
                if (w.HasValue && !Core.Safety.Guards.WorkerAllowed(w.Value)) e.Add("worker " + w.Value + " is not allowed (the standalone uses 200-299; farm_w149 never)");
            return e;
        }

        public string Summary()
        {
            if (IsMapBake)
            {
                var m = "from map " + Path.GetFileName(MapPath) + "   quality " + QualityToken;
                if (Quality == Quality.Custom) m += string.Format(CultureInfo.InvariantCulture, " (Experimental: {0:N0} photons, MC {1})", Photons, Rays);
                m += "   mode " + ModeToken + "   AO " + (Ao ? string.Format(CultureInfo.InvariantCulture, "on (strength {0}, exponent {1})", AoStrength, AoExponent) : "off");
                if (MapIsVista) m += "   Vista map: " + (VistaOut ? "result converted back to Vista" : "result stays an MCC map");
                m += "   output " + Path.GetFileName(MapOutPath);
                if (Responsive) m += "   responsive on";
                if (ExactVertexLighting) m += "   exact vertex lighting on";
                if (SceneryPermutationFix) m += "   scenery multi permutation fix on";
                if (ForceCompat) m += "   compatibility mode (DF64) FORCED";
                if (!Fixes) m += "   FIXES OFF (dev)";
                return m;
            }
            var s = "quality " + QualityToken;
            if (Quality == Quality.Custom) s += string.Format(CultureInfo.InvariantCulture, " (Experimental: {0:N0} photons, MC {1} = {2:N0} rays per gather call)", Photons, Rays, RaysPerCall(Rays));
            s += "   mode " + ModeToken;
            s += "   AO " + (Ao ? string.Format(CultureInfo.InvariantCulture, "on (strength {0}, exponent {1})", AoStrength, AoExponent) : "off");
            s += "   capture " + (ReuseCapture ? "reuse" : "new");
            s += "   " + (StageOnly ? "stage only (no tags written)" : "install");
            if (Responsive) s += "   responsive on";
            if (ExactVertexLighting) s += "   exact vertex lighting on";
            if (SceneryPermutationFix) s += "   scenery multi permutation fix on";
            if (BspRel != null) s += "   BSP from " + BspFolder(BspRel);
            if (ForceCompat) s += "   compatibility mode (DF64) FORCED";
            if (!Fixes) s += "   FIXES OFF (dev)";
            return s;
        }
    }
}
