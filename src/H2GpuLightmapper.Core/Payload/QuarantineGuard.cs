// 0.6.8 RC: antivirus quarantine of the app's own runtime files (Windows Security / Defender removed INST's tool loader -- then inject.exe,
// now H2GpuLightmapper.ToolLoader.exe -- in the middle of a
// bake, 2026-10-29, "Behavior:Win32/DefenseEvasion.A!ml"; the bake then failed late with a misleading tag-writer message).
//   * IsVirusError: the Win32 errors ERROR_VIRUS_INFECTED (225) / ERROR_VIRUS_DELETED (226) behind an open / exec / write failure;
//   * DefenderRemovedException + Message: the one user-facing text ("Windows Security (Defender) removed <file> ...");
//   * KnownSince: the release a shipped binary first appeared in, unchanged (for "unchanged since <version>");
//   * QuarantineRecord: the runtime files that already vanished once (beside the INST dirs, WORK\_runtime\quarantine_<dir>.txt), so a
//     file that vanishes AGAIN is reported before the next bake instead of being re-extracted only to be removed again.
// The app never touches Windows Security itself (no exclusion, no restore): the user decides.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Core.Payload
{
    /// <summary>A runtime file of the app was removed (or blocked) by the antivirus: the bake must not run (or failed because of it).</summary>
    public sealed class DefenderRemovedException : IOException
    {
        public string FilePath { get; private set; }
        public DefenderRemovedException(string filePath, string message) : base(message) { FilePath = filePath; }
    }

    public static class QuarantineGuard
    {
        public const int ErrorVirusInfected = 225, ErrorVirusDeleted = 226;

        /// <summary>md5 of a shipped binary -> the release it first shipped in (unchanged since). Only binaries whose history is known.</summary>
        public static readonly IReadOnlyDictionary<string, string> KnownSince = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "43872d6bd93b3e891bca7f2f8908dd8c", "0.6.8" },     // INST v014-inject-estimator\H2GpuLightmapper.ToolLoader.exe (new in 0.6.8)
            { "6178da9ac4432a42f0be7d4e6e4cebe4", "0.6.9" },     // INST engine_v373/v043-tagwriter/bin/tw_native.exe (new in 0.6.9: the tag writer's native helper)
            { "4be2dfa6a2670f7690b125b2e8b44340", "0.5.0" },     // the old inject.exe (0.5.0 ... 0.6.7; an older INST may still hold it)
        };

        /// <summary>True when the exception (or an inner one) carries ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED.</summary>
        public static bool IsVirusError(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                var w = x as Win32Exception;
                if (w != null && (w.NativeErrorCode == ErrorVirusInfected || w.NativeErrorCode == ErrorVirusDeleted)) return true;
                int hr = x.HResult;
                if (((hr >> 16) & 0x1FFF) == 7 && ((hr & 0xFFFF) == ErrorVirusInfected || (hr & 0xFFFF) == ErrorVirusDeleted)) return true;
            }
            return false;
        }

        /// <summary>The user-facing text. <paramref name="duringBake"/>: the file vanished while the bake ran; <paramref name="nothingWritten"/>:
        /// no tag was changed (always true before a bake).</summary>
        public static string Message(string filePath, string md5, string appVersion, bool duringBake = false, bool nothingWritten = true)
        {
            string since;
            string part = md5 != null && KnownSince.TryGetValue(md5, out since)
                ? "It is part of the H2 GPU Lightmapper and is unchanged since " + since + "."
                : "It is part of the H2 GPU Lightmapper " + (appVersion ?? "") + " (the app checked it against its own copy).";
            return "Windows Security (Defender) removed " + filePath + (duringBake ? " during the bake" : "") + ". " + part
                 + " Restore it from Windows Security > Protection history, or allow it, then run the bake again."
                 + (nothingWritten ? " Nothing was written." : "");
        }

        // ------------------------------------------------------------------ the record of files that vanished once

        public static string RecordPath(string instParent, string instDirName)
        {
            return Path.Combine(instParent, "quarantine_" + instDirName + ".txt");
        }

        /// <summary>Keys ("&lt;component dir&gt;/&lt;rel path&gt;") of files that vanished once; empty when there is no record.</summary>
        public static HashSet<string> LoadRecord(string recordPath)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(recordPath))
                    foreach (var l in File.ReadAllLines(recordPath)) if (l.Trim().Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)) s.Add(l.Trim());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        public static void SaveRecord(string recordPath, IEnumerable<string> keys)
        {
            var k = keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                if (k.Count == 0) { if (File.Exists(recordPath)) File.Delete(recordPath); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(recordPath));
                File.WriteAllLines(recordPath, new[] { "# H2 GPU Lightmapper: runtime files that vanished after extraction (antivirus quarantine); removed when they verify again" }.Concat(k));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static string Key(PayloadComponent c, string rel) { return c.DirName + "/" + rel; }

        /// <summary>0.6.9 (antivirus): the engine copies fullgpu.exe into the worker folder (farm_wNNullgpu.exe) at the start of phase 3 and runs it from there, so the antivirus can remove THAT copy
        /// (Behavior:Win32/DefenseEvasion.A!ml did, 2026-11-02) while the INST copy is still there. Returns the farm copy's path when phase 3 has started in this worker
        /// (gpu_bake.log / gpu_bake_lattice.log exist, so the copy had been made) and the copy is missing or blocked now; null otherwise. Call it BEFORE the after-bake cleanup (which deletes the copy).
        /// 0.6.10: <paramref name="sinceUtc"/> = when THIS bake started. A phase-3 log counts only when it was written since then: a worker folder kept from an
        /// EARLIER bake for "Reuse existing capture" was slimmed by that bake's cleanup (fullgpu.exe removed, the logs left), and its old log made the app blame
        /// the antivirus for the app's own cleanup (seen 2026-11-04, farm_w200 after a failed bake in farm_w232).</summary>
        public static string FarmExeRemoved(string farmDir, DateTime? sinceUtc = null)
        {
            if (string.IsNullOrEmpty(farmDir) || !Directory.Exists(farmDir)) return null;
            if (!PhaseThreeLogs.Any(l => WrittenSince(Path.Combine(farmDir, l), sinceUtc))) return null;
            string exe = Path.Combine(farmDir, "fullgpu.exe");
            return Probe(exe) == null ? null : exe;
        }

        /// <summary>The logs fullgpu.exe writes into the worker folder in phase 3 (the part plan runs right after the copy is made).</summary>
        private static readonly string[] PhaseThreeLogs = { "gpu_bake.log", "gpu_bake_lattice.log", "gpu_bake_partplan.log" };

        private static bool WrittenSince(string path, DateTime? sinceUtc)
        {
            try
            {
                if (!File.Exists(path)) return false;
                return sinceUtc == null || File.GetLastWriteTimeUtc(path) >= sinceUtc.Value.AddSeconds(-2);     // 2 s: file-time granularity
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Can the file be opened for reading? null = yes; "missing" / "blocked" (225/226) / "unreadable" otherwise.</summary>
        public static string Probe(string path)
        {
            if (!File.Exists(path)) return "missing";
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096)) { } return null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (IsVirusError(e)) return "blocked";
                return File.Exists(path) ? null : "missing";      // held open by another program: not our concern here
            }
        }
    }
}
