using System;
using System.IO;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    public sealed class SafetyException : InvalidOperationException
    {
        public SafetyException(string m) : base(m) { }
    }

    /// <summary>
    /// Hard rules enforced in code (FINAL_DESIGN section 9 "Never", the agent brief): the standalone's workers are 200-299
    /// and farm_w149 is denylisted outright; no stage under ROOT\tags (lm_tagwriter's own LIVE_TAGS guard is keyed to the dev
    /// PC, so the shell enforces it); live-tag writes only into ROOT\tags via lmw_install / Restore.
    /// </summary>
    public static class Guards
    {
        public const int FirstWorker = 200, LastWorker = 299;
        /// <summary>Workers the standalone never uses, whatever range is configured (farm_w149 belongs to research).</summary>
        public static readonly int[] DeniedWorkers = { 149 };

        public static bool WorkerAllowed(int w)
        {
            if (Array.IndexOf(DeniedWorkers, w) >= 0) return false;
            return w >= FirstWorker && w <= LastWorker;
        }

        public static void AssertWorker(int w)
        {
            if (Array.IndexOf(DeniedWorkers, w) >= 0) throw new SafetyException("refused: farm_w" + w + " is denylisted (never touched by the standalone)");
            if (w < FirstWorker || w > LastWorker) throw new SafetyException("refused: worker " + w + " is outside the standalone's range " + FirstWorker + "-" + LastWorker);
        }

        /// <summary>A path the shell writes for a farm must not be inside a denylisted farm dir.</summary>
        public static void AssertNotDeniedFarm(string path, string work)
        {
            foreach (int w in DeniedWorkers)
            {
                string farm = Path.Combine(work, "farm_w" + w.ToString("D2"));
                string skel = Path.Combine(work, "_gpulm_skel", "w" + w.ToString("D2"));
                if (SafeFs.IsUnderOrSame(path, farm) || SafeFs.IsUnderOrSame(path, skel)) throw new SafetyException("refused: " + path + " is inside the denylisted farm_w" + w);
            }
        }

        /// <summary>A stage (tagw_stage, final_stage, an AO stage...) must never lie under ROOT\tags.</summary>
        public static void AssertStageNotUnderTags(string stage, string root)
        {
            string tags = Path.Combine(root, "tags");
            if (SafeFs.IsUnderOrSame(stage, tags)) throw new SafetyException("refused: the stage " + stage + " is under the live tags folder " + tags);
        }

        /// <summary>A tag install / restore target must be a folder inside ROOT\tags.</summary>
        public static void AssertTargetUnderTags(string targetDir, string root)
        {
            string tags = Path.Combine(root, "tags");
            if (!SafeFs.IsUnderOrSame(targetDir, tags) || string.Equals(SafeFs.Norm(targetDir), SafeFs.Norm(tags), StringComparison.OrdinalIgnoreCase))
                throw new SafetyException("refused: the install target " + targetDir + " is not a folder inside " + tags);
        }
    }
}
