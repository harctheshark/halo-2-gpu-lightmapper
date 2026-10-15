// S2.4 _TAGBAK_ui backup (FINAL_DESIGN section 5, run_bake.ps1 step 2): before anything can write a BSP's lightmap, copy
// its lightmap set -- every "<leaf>_<bsp>_lightmap*" file in the scenario folder EXCEPT the radiance bitmaps (never written
// by a bake; their md5s are recorded and re-checked afterwards instead) -- plus the .scenario on a first bake, to
// WORK\_TAGBAK_ui\<leaf>_<bsp>_<yyyyMMdd_HHmmss>\, each copy md5-verified. Writes source.txt (the launcher's format, so its
// Restore button keeps working) and backup.json (file list + md5s + every name that existed, for an exact restore).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    /// <summary>One BSP's lightmap tag set in the live tags folder.</summary>
    public sealed class TagSet
    {
        public string Root, ScenarioRel, Bsp;
        /// <summary>ROOT\tags\&lt;scenario folder&gt;.</summary>
        public string TagDir;
        public string Leaf;
        /// <summary>"&lt;leaf&gt;_&lt;bsp&gt;_lightmap".</summary>
        public string Prefix { get { return Leaf + "_" + Bsp + "_lightmap"; } }
        public string LtmpName { get { return Prefix + ".scenario_structure_lightmap"; } }
        public string ScenarioFile { get { return Path.Combine(TagDir, Leaf + ".scenario"); } }

        public static TagSet For(string root, string scenarioRel, string bsp)
        {
            string s = scenarioRel.Replace('/', '\\').Trim('\\');
            if (s.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
            string tagDir = Path.GetDirectoryName(Path.Combine(root, "tags", s));
            return new TagSet { Root = root, ScenarioRel = s, Bsp = bsp, TagDir = tagDir, Leaf = Path.GetFileName(s) };
        }

        public static bool IsRadiance(string name) { return name.IndexOf("radiance", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Every file of the set present now (radiance included), by name.</summary>
        public List<string> PresentNames()
        {
            if (!Directory.Exists(TagDir)) return new List<string>();
            return Directory.GetFiles(TagDir, Prefix + "*").Select(Path.GetFileName)
                .Where(n => n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && IsSetFile(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A real tag of the set: "&lt;prefix&gt;.scenario_structure_lightmap" or "&lt;prefix&gt;_*.bitmap" -- not the
        /// .gpulm_new / .gpulm_removed_* / .gpulm_replaced_* side files an install or restore leaves.</summary>
        public bool IsSetFile(string name)
        {
            if (name.IndexOf(".gpulm_", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return string.Equals(name, LtmpName, StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith(Prefix + "_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bitmap", StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum BackupKind { LightmapSet, LtmpOnly, BeforeRestore }

    public sealed class BackupResult
    {
        public string Dir;
        public BackupKind Kind;
        public bool FirstBake;
        public List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();   // name -> md5
        public Dictionary<string, string> RadianceMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // full path -> md5
        public List<string> ExistedNames = new List<string>();
        public bool Empty { get { return Files.Count == 0; } }
    }

    public static class TagBackup
    {
        public const string UiFolder = "_TAGBAK_ui";
        public static string UiRoot(string work) { return Path.Combine(work, UiFolder); }

        /// <summary>
        /// Backs up the set before a bake. LightmapSet: every non-radiance set file (+ the .scenario when the BSP has no
        /// lightmap yet = first bake; the install will add the lightmap reference to it). LtmpOnly (Lightprobes mode): the
        /// .scenario_structure_lightmap only. Throws (nothing baked) when any copy does not verify.
        /// </summary>
        public static BackupResult Create(string work, TagSet set, BackupKind kind, DateTime now, IEnumerable<string> explicitNames = null)
        {
            var present = set.PresentNames();
            var res = new BackupResult { Kind = kind, ExistedNames = present };
            res.FirstBake = !present.Any(n => string.Equals(n, set.LtmpName, StringComparison.OrdinalIgnoreCase));
            foreach (var n in present.Where(TagSet.IsRadiance)) res.RadianceMd5[Path.Combine(set.TagDir, n)] = SafeFs.Md5File(Path.Combine(set.TagDir, n));
            var copy = new List<string>();
            if (explicitNames != null) copy.AddRange(explicitNames.Where(n => File.Exists(Path.Combine(set.TagDir, n))).Distinct(StringComparer.OrdinalIgnoreCase));
            else if (kind == BackupKind.LtmpOnly) copy.AddRange(present.Where(n => string.Equals(n, set.LtmpName, StringComparison.OrdinalIgnoreCase)));
            else copy.AddRange(present.Where(n => !TagSet.IsRadiance(n)));
            bool withScenario = explicitNames == null && kind == BackupKind.LightmapSet && res.FirstBake && File.Exists(set.ScenarioFile);
            if (withScenario) copy.Add(Path.GetFileName(set.ScenarioFile));
            string ts = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string leafName = set.Leaf + "_" + set.Bsp + "_" + (kind == BackupKind.BeforeRestore ? "before_restore_" : "") + ts;
            Directory.CreateDirectory(UiRoot(work));
            // review fix (finding 3): the set is built in "<final>.tmp_<rand>" and renamed to its final name only after
            // backup.json is written, so a copy that fails (disk full) or a power cut never leaves a folder that looks like a
            // complete backup (Restore skips ".tmp_" folders and folders without backup.json / source.txt). A failed attempt's
            // tmp folder is left as it is (nothing is ever deleted here).
            string final = SafeFs.UniqueChild(UiRoot(work), leafName);
            string tmp = final + TmpMarker + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(tmp);
            foreach (var n in copy)
            {
                string md5 = SafeFs.CopyVerified(Path.Combine(set.TagDir, n), Path.Combine(tmp, n));
                res.Files.Add(new KeyValuePair<string, string>(n, md5));
            }
            File.WriteAllText(Path.Combine(tmp, "source.txt"), set.TagDir + "\r\n" + set.Prefix);
            var j = new JObj().Add("schema", 1).Add("made", now.ToString("s", CultureInfo.InvariantCulture)).Add("kind", kind.ToString())
                .Add("tagDir", set.TagDir).Add("prefix", set.Prefix).Add("scenario", set.ScenarioRel).Add("bsp", set.Bsp).Add("firstBake", res.FirstBake)
                .Add("files", res.Files.Select(f => (object)new JObj().Add("name", f.Key).Add("md5", f.Value)).ToList())
                .Add("existedNames", res.ExistedNames.Cast<object>().ToList())
                .Add("radiance", res.RadianceMd5.Select(kv => (object)new JObj().Add("name", Path.GetFileName(kv.Key)).Add("md5", kv.Value)).ToList())
                .Add("installedAfter", false);
            SafeFs.WriteAllTextAtomic(Path.Combine(tmp, "backup.json"), Json.Write(j, true));
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(tmp, final); break; }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 10)
                {
                    // a folder of that name appeared meanwhile -> the next unique name; otherwise a transient handle (an
                    // antivirus scan of the new files) -> wait a moment and retry the rename
                    if (Directory.Exists(final)) final = SafeFs.UniqueChild(UiRoot(work), leafName);
                    else System.Threading.Thread.Sleep(200);
                }
            }
            res.Dir = final;
            return res;
        }

        /// <summary>The infix of a backup folder still being written (never a restore candidate).</summary>
        public const string TmpMarker = ".tmp_";

        /// <summary>
        /// Review fix (finding 5): records in backup.json that the bake which made this backup then CHANGED the live set (an
        /// install, or an install that failed after a replace). "Restore previous lightmap" lists it; a backup whose bake never
        /// installed equals the live set and is skipped as a no-op. Best effort: false when backup.json cannot be updated.
        /// </summary>
        public static bool MarkInstalledAfter(string backupDir, DateTime now)
        {
            try
            {
                string p = Path.Combine(backupDir, "backup.json");
                var j = Json.ReadObjFile(p);
                if (j == null) return false;
                j["installedAfter"] = true;
                j["installedAfterAt"] = now.ToString("s", CultureInfo.InvariantCulture);
                SafeFs.WriteAllTextAtomic(p, Json.Write(j, true));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return false; }
        }

        /// <summary>Radiance bitmaps whose md5 changed (or vanished) since the backup: logged as a warning after the bake.</summary>
        public static List<string> ChangedRadiance(BackupResult b)
        {
            return b.RadianceMd5.Where(kv => !File.Exists(kv.Key) || !string.Equals(SafeFs.Md5File(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase))
                                .Select(kv => Path.GetFileName(kv.Key)).ToList();
        }
    }
}
