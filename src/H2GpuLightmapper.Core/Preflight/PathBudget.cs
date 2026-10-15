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
            public int Length { get { return Example.Length; } }
            public override string ToString() { return What + ": " + Length + " chars"; }
        }

        public static List<Entry> Estimate(string root, string instDirName, int instMaxPathLen, string scenarioRel = null, string bsp = null)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            string work = root + "\\GPU Lightmap Work";
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
            e.Add(new Entry { What = "farm AO stage", Example = work + "\\farm_w200\\ao\\s200e800\\tagw_stage\\" + truecolor });
            e.Add(new Entry { What = "skeleton overlay", Example = work + "\\_gpulm_skel\\w200\\tags\\" + scenDir + "\\" + truecolor });
            e.Add(new Entry { What = "live tag (install temp)", Example = root + "\\tags\\" + scenDir + "\\" + truecolor + ".gpulm_replaced_" + Ts });
            // lmw_install renames an existing same-name backup to ".older_<ts>" only inside --backup-dir (gpu_bake's per-farm
            // OUT\tag_backup_before_writeback is reused bake after bake); the auto backup and the per-run folder are fresh
            e.Add(new Entry { What = "auto backup", Example = work + "\\_TAGBAK_auto\\" + Ts + "_" + prefix + "\\" + truecolor });
            e.Add(new Entry { What = "farm install backup", Example = work + "\\farm_w200\\tag_backup_before_writeback\\" + truecolor + ".older_" + Ts });
            e.Add(new Entry { What = "UI backup", Example = work + "\\_TAGBAK_ui\\" + leaf + "_" + b + "_before_restore_" + Ts + "\\" + truecolor });
            e.Add(new Entry { What = "run backup", Example = work + "\\standalone\\runs\\" + Ts + "_" + leaf + "_" + b + "\\tag_backup_before_writeback\\" + truecolor });
            return e;
        }

        public static Entry Longest(IEnumerable<Entry> e) { return e.OrderByDescending(x => x.Length).First(); }
    }
}
