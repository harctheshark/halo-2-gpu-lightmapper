// 0.6.4 item 2: the engine falls back from the direct tag writer to the tool write-back (or the other way round) with ONE yellow "Note" line in its
// output and nothing else; the bake still ends DONE, so the user never learned that the lightmap was written by the other path. This class recognises
// the engine's own fallback lines so the shell can raise a visible warning in the log and put it in the run summary.
//
// The exact texts (engine 0.6.4, gpu_bake.ps1 -- all printed by its Note function as "      *** <text>"):
//   direct tag writer FAILED -- falling back to the tool write-back: <why>                         line ~2060  (phase 4a failed, -WriteBack, no -NoAutoFallback)
//   -WriteBackMode direct cannot run: <missing pieces> -- using -WriteBackMode tool                 line ~933   (pre-flight: a writer file / numpy is missing)
//   tag writer: the skeleton run did not start (<why>) -- phase 4a will fall back                  line ~1480  (the heads-up that precedes the first one)
// There is no automatic tool -> direct fallback in the engine today; "falling back to the direct ..." is recognised too so that a future engine's
// opposite fallback is reported the same way instead of passing silently.
using System;
using System.Text.RegularExpressions;

namespace H2GpuLightmapper.Core.Run
{
    public enum FallbackDirection { DirectToTool, ToolToDirect }

    public sealed class FallbackInfo
    {
        public FallbackDirection Direction;
        /// <summary>True for the engine's heads-up ("... phase 4a will fall back"): the fallback has not happened yet.</summary>
        public bool HeadsUp;
        /// <summary>The engine's own reason text (may be empty).</summary>
        public string Why;
        /// <summary>The distinct key: a repeated line of the same kind raises one warning.</summary>
        public string Key { get { return Direction + (HeadsUp ? ":headsup" : ""); } }
    }

    public static class WriteBackFallback
    {
        /// <summary>Starts every warning line the shell writes; the detector ignores its own lines.</summary>
        public const string WarnPrefix = "WARNING: the lightmap write-back";

        private static readonly Regex Failed = new Regex(@"direct tag writer FAILED\s*--\s*falling back to the tool write-back\s*:?\s*(?<why>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CannotRun = new Regex(@"-WriteBackMode\s+(?<m>direct|both)\s+cannot run\s*:\s*(?<why>.*?)\s*--\s*using\s+-WriteBackMode\s+tool\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HeadsUp = new Regex(@"tag writer\s*:\s*the skeleton run did not start\s*\((?<why>.*)\)\s*--\s*phase 4a will fall back", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ToolToDirect = new Regex(@"fall(?:ing|s)?\s+back\s+to\s+the\s+direct\b[^:]*:?\s*(?<why>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Is this engine output line one of the engine's write-back fallback messages? Null when it is not.</summary>
        public static FallbackInfo Detect(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string t = line.Trim();
            if (t.StartsWith(WarnPrefix, StringComparison.Ordinal) || t.StartsWith("*** " + WarnPrefix, StringComparison.Ordinal)) return null;     // the shell's own warning
            Match m;
            if ((m = Failed.Match(t)).Success) return new FallbackInfo { Direction = FallbackDirection.DirectToTool, Why = m.Groups["why"].Value.Trim() };
            if ((m = CannotRun.Match(t)).Success) return new FallbackInfo { Direction = FallbackDirection.DirectToTool, Why = m.Groups["why"].Value.Trim() };
            if ((m = HeadsUp.Match(t)).Success) return new FallbackInfo { Direction = FallbackDirection.DirectToTool, HeadsUp = true, Why = m.Groups["why"].Value.Trim() };
            if ((m = ToolToDirect.Match(t)).Success) return new FallbackInfo { Direction = FallbackDirection.ToolToDirect, Why = m.Groups["why"].Value.Trim() };
            return null;
        }

        /// <summary>The visible warning for the log (one line).</summary>
        public static string WarningText(FallbackInfo f, string who = "the engine")
        {
            string why = string.IsNullOrEmpty(f.Why) ? "" : " Reason: " + f.Why;
            if (f.Direction == FallbackDirection.ToolToDirect)
                return WarnPrefix + " fell back from the TOOL write-back to the DIRECT tag writer (" + who + ")." + why;
            if (f.HeadsUp)
                return WarnPrefix + " is about to fall back from the DIRECT tag writer to the older TOOL write-back (" + who + "): the direct writer's skeleton run did not start." + why;
            return WarnPrefix + " fell back from the DIRECT tag writer to the older TOOL write-back (" + who + "). The lightmap is still written, but by the tool, not by the "
                   + "direct writer: ambient occlusion and light probes are not applied by this path." + why;
        }

        /// <summary>The short form for the run summary / the end-of-bake message.</summary>
        public static string SummaryText(FallbackInfo f)
        {
            string why = string.IsNullOrEmpty(f.Why) ? "" : " (" + f.Why + ")";
            return f.Direction == FallbackDirection.ToolToDirect
                ? "WARNING: the write-back fell back from the TOOL write-back to the DIRECT tag writer" + why
                : "WARNING: the write-back fell back from the DIRECT tag writer to the TOOL write-back" + why;
        }
    }
}
