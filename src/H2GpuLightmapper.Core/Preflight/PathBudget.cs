using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Core.Preflight
{
    /// <summary>
    /// The longest full paths a bake of one root (and, when known, one scenario/BSP) will create. FINAL_DESIGN section 8:
    /// every one must stay under 240 characters (net48 + tool_fast + the DLL are MAX_PATH-bound; LongPathsEnabled is off by
    /// default). Without a scenario a nominal one is used (scenario folder 32, leaf 16, BSP 16 characters) and an overrun is
    /// only a warning; with the real scenario / BSP it is a refusal.
    /// </summary>
    public static class PathBudget
    {
        public const int Limit = 240;
        public const int NominalScenarioDir = 32, NominalLeaf = 16, NominalBsp = 16;
        private const string Ts = "20261014_120000";

        public sealed class Entry
        {
            public string What;
            public string Example;
            /// <summary>0.6.8 RC (F1): a path only the rare tool write-back FALLBACK creates (gpu_bake -WriteBack: its farm backup, its
            /// _TAGBAK_auto copy). Over the limit it is a preflight WARNING, not a refusal; if a bake then needs the fallback, the shell stops
            /// it before the fallback starts (nothing written; FallbackOver).</summary>
            public bool FallbackOnly;
            /// <summary>0.6.8 RC (F1): a best-effort copy (a failed run's stage kept in KEEP): a failed copy is skipped, so over the limit it
            /// is a warning, not a refusal.</summary>
            public bool BestEffort;
            /// <summary>Counts for the refusal (everything a normal bake must create).</summary>
            public bool Required { get { return !FallbackOnly && !BestEffort; } }
            public int Length { get { return Example.Length; } }
            public override string ToString() { return What + ": " + Length + " chars"; }
        }

        /// <summary>0.6.8: a .map bake's run folder name under Temp ("m" + 6 hex) and its overlay root ("r").</summary>
        public const string MapRunExample = "m3f9a1c", MapOverlayRoot = "r";
        /// <summary>0.6.8: the 99th-percentile and the longest tag path (relative to tags\) of the dev PC's 38k-tag kit (DISK_USAGE_0.6.8.md, C2).</summary>
        public const int KitRelP99 = 138, KitRelMax = 191;

        /// <param name="farmsInTemp">0.6.8: the workers are in WORK\Temp (safe to delete) (MANIFEST features.farms); adds the .map overlay entries.</param>
        public static List<Entry> Estimate(string root, string instDirName, int instMaxPathLen, string scenarioRel = null, string bsp = null, bool farmsInTemp = false)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            string work = Payload.ShellPaths.WorkOf(root + "\\");                      // 0.6.12: ROOT\GPU Work (an old-name folder while its move is deferred)
            string farms = farmsInTemp ? Payload.ShellPaths.TempOf(work) : work;
            string scenDir, leaf;
            if (!string.IsNullOrEmpty(scenarioRel))
            {
                string s = scenarioRel.Replace('/', '\\').Trim('\\');
                if (s.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
                scenDir = Path.GetDirectoryName(s) ?? "";
                leaf = Path.GetFileName(s);
            }
            else
            {
                scenDir = new string('s', NominalScenarioDir);
                leaf = new string('l', NominalLeaf);
            }
            string b = string.IsNullOrEmpty(bsp) ? new string('b', NominalBsp) : bsp;
            string prefix = leaf + "_" + b + "_lightmap";
            string truecolor = prefix + "_truecolor_bitmaps.bitmap";
            var e = new List<Entry>();
            if (!string.IsNullOrEmpty(instDirName))
                e.Add(new Entry { What = "INST extraction", Example = work + "\\_runtime\\" + instDirName + ".tmp_00000000\\" + new string('x', Math.Max(1, instMaxPathLen)) });
            e.Add(new Entry { What = "farm AO stage", Example = farms + "\\farm_w200\\ao\\s200e800\\tagw_stage\\" + truecolor });
            e.Add(new Entry { What = "skeleton overlay", Example = work + "\\_gpulm_skel\\w200\\tags\\" + scenDir + "\\" + truecolor });
            e.Add(new Entry { What = "live tag (install temp)", Example = root + "\\tags\\" + scenDir + "\\" + truecolor + ".gpulm_replaced_" + Ts });
            // lmw_install renames an existing same-name backup to ".older_<ts>" only inside --backup-dir (gpu_bake's per-farm
            // OUT\tag_backup_before_writeback is reused bake after bake); the auto backup and the per-run folder are fresh
            // 0.6.8: the shell's own install backs up into RUN\tagbak in every mode and no longer writes _TAGBAK_auto; these two paths
            // are made only by gpu_bake -WriteBack, i.e. the rare tool write-back fallback (F1: a warning, refused at bake time if needed)
            e.Add(new Entry { What = "auto backup", Example = work + "\\_TAGBAK_auto\\" + Ts + "_" + prefix + "\\" + truecolor, FallbackOnly = true });
            e.Add(new Entry { What = "farm install backup", Example = farms + "\\farm_w200\\tag_backup_before_writeback\\" + truecolor + ".older_" + Ts, FallbackOnly = true });
            if (farmsInTemp)
            {
                // 0.6.8: a failed run's copy of the stage goes to the KEEP run folder (best effort: TempJanitor skips a copy that fails)
                e.Add(new Entry { What = "failed-run stage copy", Example = work + "\\standalone\\runs\\" + Ts + "_" + Run.RunFolder.Label(leaf, b) + "\\failed\\farm_w200_tagw_stage\\" + truecolor, BestEffort = true });
            }
            e.Add(new Entry { What = "UI backup", Example = work + "\\_TAGBAK_ui\\" + leaf + "_" + b + "_before_restore_" + Ts + "\\" + truecolor });
            // W2 (0.6.3): the run folder is <ts>_<label (<= 20 chars)> and its install backup subfolder is "tagbak" (0.6.2: <ts>_<leaf>_<bsp> and
            // tag_backup_before_writeback, which reached 243 characters for a long map name)
            e.Add(new Entry { What = "run backup", Example = work + "\\standalone\\runs\\" + Ts + "_" + Run.RunFolder.Label(leaf, b) + "\\" + Run.RunFolder.BackupDirName + "\\" + truecolor });
            return e;
        }

        public static Entry Longest(IEnumerable<Entry> e) { return e.OrderByDescending(x => x.Length).First(); }

        /// <summary>The longest path a normal bake must create (the refusal budget): fallback-only and best-effort paths excluded.</summary>
        public static Entry LongestRequired(IEnumerable<Entry> e) { return Longest(e.Where(x => x.Required)); }

        /// <summary>0.6.8 RC (F1): the longest tool write-back FALLBACK path of this bake when it reaches the limit, else null. The shell
        /// calls it right before the fallback would start: non-null = the bake stops there, nothing written to the tags.</summary>
        public static Entry FallbackOver(string root, string scenarioRel, string bsp, bool farmsInTemp)
        {
            var over = Estimate(root, null, 0, scenarioRel, bsp, farmsInTemp).Where(x => x.FallbackOnly && x.Length >= Limit).ToList();
            return over.Count == 0 ? null : Longest(over);
        }

        /// <summary>0.6.8: the longest path of a .map bake's overlay copy of the kit = WORK\Temp (safe to delete)\m&lt;6hex&gt;\r\tags\ + the kit's longest
        /// relative tag path. Reported (and a warning when it passes MAX_PATH = 260); the copy itself is made with \\?\ paths, but tool_fast cannot
        /// open a tag past MAX_PATH, so a map that USES such a tag cannot be baked from a .map on that H2EK path.</summary>
        /// <summary>0.6.8: a .map bake's skeleton overlay path (WORK\Temp (safe to delete)\m&lt;6hex&gt;\r\w\_gpulm_skel\w200\tags\&lt;scenario folder&gt;\&lt;truecolor&gt;).
        /// Reported only (not part of a scenario bake's refusal budget); it is ONE character longer than 0.6.7's (_mapbake\m&lt;yyyyMMdd-HHmmss&gt;\root, 31 characters, vs Temp (safe to delete)\m&lt;6hex&gt;\r, 32).</summary>
        public static Entry MapSkeleton(string root, string scenarioRel, string bsp)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            string s = scenarioRel.Replace('/', '\\').Trim('\\');
            string scenDir = Path.GetDirectoryName(s) ?? "", leaf = Path.GetFileName(s);
            string ov = Payload.ShellPaths.TempOf(Payload.ShellPaths.WorkOf(root + "\\")) + "\\" + MapRunExample + "\\" + MapOverlayRoot;
            return new Entry { What = ".map skeleton overlay", Example = ov + "\\w\\_gpulm_skel\\w200\\tags\\" + scenDir + "\\" + leaf + "_" + bsp + "_lightmap_truecolor_bitmaps.bitmap" };
        }

        public static Entry MapOverlay(string root, int kitRel)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            string pre = Payload.ShellPaths.TempOf(Payload.ShellPaths.WorkOf(root + "\\")) + "\\" + MapRunExample + "\\" + MapOverlayRoot + "\\tags\\";
            return new Entry { What = ".map overlay tag (kit relative path " + kitRel + ")", Example = pre + new string('t', Math.Max(1, kitRel)) };
        }
    }
}
