// 0.6.6: the engine's tolerated stage deviations. A stage the format gate (lmw_validate) accepts WITH a warning, or in which the tag writer filled a few
// uncovered texels under lit vertex UVs, is installed; the user must still SEE it (a yellow warning in the log + the run summary), as for a write-back fallback.
//
// The exact texts (engine 0.6.6):
//   lmw_validate.py output   "   WARN  set.d.uv_on_alpha255   WARNING: 1 of 44171 lit UVs are not on an alpha-255 texel (tolerated: <= 16); ..."
//   gpu_bake.ps1 Note        "      *** lmw_validate: set.d.uv_on_alpha255: WARNING: 1 of 44171 lit UVs ..."
//   lm_tagwriter.py          "[tagwriter   10.8s] *** WARNING: filled 2 uncovered texel(s) under lit vertex UVs from their covered neighbours (page, x, y: [...])"
//   gpu_bake.ps1 Note        "      *** tag writer: filled 2 uncovered texel(s) under lit vertex UVs ..."
using System;
using System.Text.RegularExpressions;

namespace H2GpuLightmapper.Core.Run
{
    public sealed class StageWarningInfo
    {
        /// <summary>"validate" (a lmw_validate WARN) or "fill" (the writer filled texels).</summary>
        public string Kind;
        /// <summary>The engine's own detail text.</summary>
        public string Detail;
        public string Key { get { return Kind; } }
    }

    public static class StageWarning
    {
        public const string WarnPrefix = "WARNING: the lightmap was written with a tolerated deviation";

        private static readonly Regex Validate = new Regex(@"(?:^|\s)WARN\s+(?<chk>set\.d\.\S+)\s+(?<d>.*)$|lmw_validate:\s*(?<chk2>set\.d\.\S+?):\s*(?<d2>.*)$", RegexOptions.Compiled);
        private static readonly Regex Fill = new Regex(@"filled\s+(?<n>\d+)\s+uncovered\s+texel\(s\)\s+under\s+lit\s+vertex\s+UVs(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Is this engine output line one of the tolerated-deviation messages? Null when it is not (and for the shell's own warning).</summary>
        public static StageWarningInfo Detect(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string t = line.Trim();
            if (t.StartsWith(WarnPrefix, StringComparison.Ordinal) || t.StartsWith("*** " + WarnPrefix, StringComparison.Ordinal)) return null;
            Match m;
            if ((m = Validate.Match(t)).Success)
            {
                string chk = m.Groups["chk"].Success ? m.Groups["chk"].Value : m.Groups["chk2"].Value;
                string d = (m.Groups["d"].Success ? m.Groups["d"].Value : m.Groups["d2"].Value).Trim();
                if (d.StartsWith("WARNING:", StringComparison.Ordinal)) d = d.Substring(8).Trim();
                return new StageWarningInfo { Kind = "validate", Detail = chk + ": " + d };
            }
            if ((m = Fill.Match(t)).Success)
                return new StageWarningInfo { Kind = "fill", Detail = "the tag writer filled " + m.Groups["n"].Value + " uncovered texel(s) under lit vertex UVs from their covered neighbours" + m.Groups["rest"].Value.TrimEnd() };
            return null;
        }

        public static string WarningText(StageWarningInfo w)
        {
            return WarnPrefix + " (installed anyway): " + w.Detail + ". At most a few vertices sample a slightly different lightmap colour than the CPU bake; see the bake log.";
        }

        public static string SummaryText(StageWarningInfo w)
        {
            return "WARNING: installed with a tolerated deviation (" + w.Detail + ")";
        }

        /// <summary>The refusal text for a stage whose lmw_validate result (tagw_validate.json) lists failed checks; null when none.</summary>
        public static string RefusalFromFailed(System.Collections.Generic.IList<object> failed)
        {
            if (failed == null || failed.Count == 0) return null;
            var names = new System.Collections.Generic.List<string>();
            foreach (var f in failed) names.Add(Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture));
            return "lmw_validate refused the stage: " + string.Join(", ", names);
        }
    }
}
