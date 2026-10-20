// X2: the engine's device-loss lines -> one plain sentence for the user. A bake whose GPU step ran longer than Windows allows (a TDR)
// ends with the engine's own lines ("DIRECT: *** GPU CAST DEVICE REMOVED ...", "... was RESET by Windows (TDR)", DXGI's DEVICE_HUNG);
// the raw failure reason of such a run is a pwsh stack line the user cannot act on.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Core.Run
{
    public static class DeviceLoss
    {
        /// <summary>What the user is told. Names the window's own setting exactly as it is labelled.</summary>
        public const string Explanation = "Windows reset the GPU because one GPU step ran too long (a TDR). Try \"Keep PC responsive - Slower Bakes!\", close other GPU programs, or report your GPU.";

        /// <summary>The three signatures: a line that STARTS with the engine's device-removed banner, or CONTAINS the TDR sentence or DEVICE_HUNG.</summary>
        public static bool IsDeviceLossLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            return t.StartsWith("DIRECT: *** GPU CAST DEVICE REMOVED", StringComparison.Ordinal)
                   || t.IndexOf("was RESET by Windows (TDR)", StringComparison.Ordinal) >= 0
                   || t.IndexOf("DEVICE_HUNG", StringComparison.Ordinal) >= 0;
        }

        public static bool AnyDeviceLoss(IEnumerable<string> lines) { return lines != null && lines.Any(IsDeviceLossLine); }

        /// <summary>The tail (default 2 MB) of a log file, searched for the signatures (a driver script's own log that never reached our pipe).</summary>
        public static bool FileHasDeviceLoss(string path, int tailBytes = 2 * 1024 * 1024)
        {
            try
            {
                if (path == null || !File.Exists(path)) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - tailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null) if (IsDeviceLossLine(line)) return true;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        /// <summary>The failure text the result shows: the plain explanation first, the technical reason after it. Unchanged when no device loss was seen.</summary>
        public static string Compose(string technical, bool deviceLost)
        {
            return deviceLost ? Explanation + "\nDetails: " + technical : technical;
        }
    }
}
