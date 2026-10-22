// 0.6.6: which tags feed THIS bake's lightmap, and what a content hash of them says about a "changed" mtime.
//   The capture-reuse gate used to prompt for ANY tag whose size / mtime moved since the capture (~35k files). Two refinements:
//   (1) relevance: only a change to a tag that can change the baked result prompts / refuses -- the scenario folder (minus AI / script / sound
//       resources), this BSP's folder (bsp + lighting info), scenery / crate / device / light / sky / model / decorator / shader tags anywhere,
//       and the bitmaps / render_models this map USES (a tag-reference walk, the same one the engine's skeleton guard runs: lmw_usedset.py).
//       Every other change is listed as information only.
//   (2) content: a file whose mtime moved but whose size is equal is compared by md5 when the record stored one (records written by 0.6.6+ carry a
//       4th column on the relevant files <= HashCap); an identical re-save (Sapien / Guerilla / Tool) is not a change. Older records have no
//       hash column and keep the mtime behaviour (the relevance split still applies).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    /// <summary>Relevance of tags (tags-relative paths) to one bake; built from the file list of a fingerprint.</summary>
    public sealed class TagRelevanceCtx
    {
        /// <summary>Largest file whose md5 is stored / compared.</summary>
        public const long HashCap = 32L << 20;

        private static readonly HashSet<string> TierA = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "scenery", "crate", "device_machine", "device_control", "device_light_fixture", "light", "light_volume", "lens_flare", "sky", "model",
            "decorator_set", "scenario_structure_lighting_info", "shader", "shader_halogram", "shader_template", "shader_pass", "shader_light_response",
            "shader_screen", "shader_transparent_chicago", "shader_transparent_chicago_extended", "shader_transparent_generic", "shader_transparent_glass",
            "shader_transparent_meter", "shader_transparent_plasma", "shader_transparent_water", "shader_foliage", "shader_custom", "material_effects", "global_lighting",
        };
        private static readonly Regex ScenarioNon = new Regex(@"^(scenario_(ai|bipeds|cinematics|comments|creature|equipment|hs_source_file|sound_scenery|trigger_volumes|vehicles|weapons)_resource|hsc|txt|log)$",
                                                              RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PathRx = new Regex(@"([A-Za-z0-9_ .()\-]+(?:\\[A-Za-z0-9_ .()\-]+)+)\x00", RegexOptions.CultureInvariant);

        private string _scenDir;                               // "scenarios\m\m\" (lower)
        private readonly HashSet<string> _bspDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // folders (with trailing \) of the BSP tag(s) of this bake
        /// <summary>Lower-case relative paths of the bitmaps / render_models this map uses; null = the walk failed (every bitmap / render_model counts).</summary>
        public HashSet<string> Used;
        public string Root;

        public static string Ext(string rel)
        {
            int d = rel.LastIndexOf('.');
            int s = rel.LastIndexOf('\\');
            return d > s && d >= 0 ? rel.Substring(d + 1) : "";
        }

        private static string DirOf(string rel) { int s = rel.LastIndexOf('\\'); return s < 0 ? "" : rel.Substring(0, s + 1); }

        /// <summary>Builds the context: the tag-reference walk from the scenario + this BSP over <paramref name="relFiles"/> (every tags-relative file, any case).</summary>
        public static TagRelevanceCtx Build(string root, string scenarioRel, string bsp, IEnumerable<string> relFiles)
        {
            var c = new TagRelevanceCtx { Root = root };
            string scen = (scenarioRel ?? "").Replace('/', '\\').Trim('\\');
            if (scen.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) scen = scen.Substring(0, scen.Length - 9);
            c._scenDir = DirOf(scen).ToLowerInvariant();
            c._bspDirs.Add(c._scenDir);
            try { c.Used = Walk(c, root, scen, bsp, relFiles); } catch (Exception) { c.Used = null; }
            return c;
        }

        private static HashSet<string> Walk(TagRelevanceCtx c, string root, string scen, string bsp, IEnumerable<string> relFiles)
        {
            var idx = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);   // lower path w/o ext -> (ext, rel)
            foreach (var rel in relFiles)
            {
                string e = Ext(rel);
                if (e.Length == 0) continue;
                if (!TierA.Contains(e) && !string.Equals(e, "bitmap", StringComparison.OrdinalIgnoreCase) && !string.Equals(e, "render_model", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e, "scenario_structure_bsp", StringComparison.OrdinalIgnoreCase)) continue;
                string key = rel.Substring(0, rel.Length - e.Length - 1);
                List<KeyValuePair<string, string>> l;
                if (!idx.TryGetValue(key, out l)) idx[key] = l = new List<KeyValuePair<string, string>>();
                l.Add(new KeyValuePair<string, string>(e.ToLowerInvariant(), rel));
            }
            string scenFile = scen + ".scenario";
            string tagsDir = Path.Combine(root, "tags");
            var roots = new List<string> { scenFile };
            // this bake's BSP: the scenario's reference named <bsp> (it may sit in another folder), else <scenario folder>\<bsp>
            string sd;
            try { sd = Latin1(File.ReadAllBytes(Path.Combine(tagsDir, scenFile))); } catch (IOException) { return null; }
            bool foundBsp = false;
            foreach (Match m in PathRx.Matches(sd))
                foreach (var hit in Lookup(idx, m.Groups[1].Value))
                    if (hit.Key == "scenario_structure_bsp" && string.Equals(Path.GetFileName(hit.Value.Substring(0, hit.Value.Length - hit.Key.Length - 1)), bsp, StringComparison.OrdinalIgnoreCase))
                    { roots.Add(hit.Value); c._bspDirs.Add(DirOf(hit.Value).ToLowerInvariant()); foundBsp = true; }
            if (!foundBsp)
            {
                string guess = c._scenDir + bsp + ".scenario_structure_bsp";
                if (File.Exists(Path.Combine(tagsDir, guess))) roots.Add(guess);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var todo = new Stack<string>(roots);
            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (todo.Count > 0)
            {
                string rel = todo.Pop();
                if (!seen.Add(rel)) continue;
                string text;
                try { text = Latin1(File.ReadAllBytes(Path.Combine(tagsDir, rel))); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                foreach (Match m in PathRx.Matches(text))
                    foreach (var hit in Lookup(idx, m.Groups[1].Value))
                    {
                        if (hit.Key == "bitmap" || hit.Key == "render_model") leaves.Add(hit.Value.ToLowerInvariant());
                        if ((TierA.Contains(hit.Key) || hit.Key == "render_model") && !seen.Contains(hit.Value)) todo.Push(hit.Value);
                    }
            }
            return leaves;
        }

        private static string Latin1(byte[] b) { return Encoding.GetEncoding(28591).GetString(b); }

        private static readonly KeyValuePair<string, string>[] NoHit = new KeyValuePair<string, string>[0];

        /// <summary>A reference's path text is glued to the field-name text before it ("base_map" + path): when the whole string names no tag,
        /// drop leading characters of the FIRST segment until a suffix does (the longest hit wins).</summary>
        private static IEnumerable<KeyValuePair<string, string>> Lookup(Dictionary<string, List<KeyValuePair<string, string>>> idx, string s)
        {
            List<KeyValuePair<string, string>> l;
            if (idx.TryGetValue(s, out l)) return l;
            int cut = s.IndexOf('\\');
            for (int i = 1; i < cut; i++) if (idx.TryGetValue(s.Substring(i), out l)) return l;
            return NoHit;
        }

        /// <summary>Does a change of this tag (tags-relative path) change what this bake would produce?</summary>
        public bool IsRelevant(string rel)
        {
            string e = Ext(rel);
            string dir = DirOf(rel);
            if (_scenDir.Length > 0 && dir.StartsWith(_scenDir, StringComparison.OrdinalIgnoreCase))
                return !ScenarioNon.IsMatch(e);     // the scenario folder, recursive (resources ...)
            if (_bspDirs.Contains(dir) && (string.Equals(e, "scenario_structure_bsp", StringComparison.OrdinalIgnoreCase) || e.StartsWith("scenario_structure_lighting", StringComparison.OrdinalIgnoreCase))) return true;
            if (TierA.Contains(e)) return true;
            if (string.Equals(e, "bitmap", StringComparison.OrdinalIgnoreCase) || string.Equals(e, "render_model", StringComparison.OrdinalIgnoreCase))
                return Used == null || Used.Contains(rel.ToLowerInvariant());
            return false;
        }
    }

    /// <summary>Result of comparing a record's fingerprint with the current one.</summary>
    public sealed class TagDiff
    {
        public List<string> Relevant = new List<string>();
        public int RelevantTotal;
        public List<string> Info = new List<string>();
        public int InfoTotal;
        /// <summary>Files whose mtime moved but whose content hash equals the recorded one (a re-save with identical bytes).</summary>
        public int Identical;
    }
}
