// 0.6.12: the app's work folder is ROOT\GPU Work (was ROOT\GPU Lightmap Work) and its temporary folder WORK\Temp (was WORK\Temp (safe to delete)):
// 26 characters less on every path below them, which is where tool_fast's 259-character limit bites (the .map bake's copy of the kit, the workers).
//
// The first time a root is used, the folders the app made in the old WORK move to the new one:
//   * the old WORK holds only what the app makes (the usual case): the whole folder is renamed (one rename on the same drive: instant, nothing is
//     copied), then its Temp (safe to delete) is renamed to Temp;
//   * it also holds other things (a research tree, files someone put there): only the app's own folders move (_runtime, standalone, _TAGBAK_ui,
//     _TAGBAK_auto and the temporary folder); everything else stays where it is.
// It does NOT move (the old WORK is used as before, and the move is tried again at the next start) while a bake runs (current_run.json names a live
// app, or a live GPU lock), while an unfinished tag write or restore is recorded (standalone\markers: the recovery reads the paths it recorded), or
// when the old WORK is a link. A folder that is open in another program cannot be renamed: then the old WORK is used too.
// Paths the old WORK recorded in run records / markers are mapped to the new WORK when read (Rebase), so Undo and restore keep working.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Payload
{
    public static class WorkMigration
    {
        public const string LegacyWorkFolderName = "GPU Lightmap Work";
        public const string LegacyTempFolderName = "Temp (safe to delete)";
        /// <summary>The folders the app makes in WORK that the migration moves (the temporary folder is handled on its own).</summary>
        public static readonly string[] AppFolders = { ShellPaths.RuntimeFolderName, ShellPaths.StandaloneFolderName, "_TAGBAK_ui", "_TAGBAK_auto" };
        /// <summary>Other entries the app (0.6.7 and later) makes in WORK: when the old WORK holds nothing else, the whole folder is renamed.</summary>
        private static readonly string[] AppOther = { LegacyTempFolderName, "_gpulm_skel", "_mapbake", GpuLock.FileName };

        /// <summary>Tests / support: no migration (WorkOf returns the new WORK). Also set by the environment variable H2LM_NO_WORK_MIGRATION=1.</summary>
        public static bool Disabled = Environment.GetEnvironmentVariable("H2LM_NO_WORK_MIGRATION") == "1";

        /// <summary>A --dry-run (it writes nothing): WorkOf only LOOKS -- the new WORK when it exists or no old one does, else the old one; nothing is moved.</summary>
        public static bool ReadOnly;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Pending = new List<string>();

        /// <summary>The messages of migrations done (or deferred) since the last call, for the log.</summary>
        public static List<string> TakeMessages()
        {
            lock (Gate) { var r = new List<string>(Pending); Pending.Clear(); return r; }
        }

        private static void Say(string m) { lock (Gate) Pending.Add(m); }

        /// <summary>Test hook: forget the decisions of this process (the next WorkOf decides again).</summary>
        public static void ResetForTests() { lock (Gate) { Resolved.Clear(); Pending.Clear(); } }

        /// <summary>Is <paramref name="work"/> an old-name WORK (ROOT\GPU Lightmap Work)? Its temporary folder keeps the old name too.</summary>
        public static bool IsLegacyWork(string work)
        {
            return work != null && string.Equals(Path.GetFileName(work.TrimEnd('\\')), LegacyWorkFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The WORK to use for the root folder <paramref name="rootDir"/> (already normalized): decided once per process.</summary>
        public static string Resolve(string rootDir)
        {
            string neu = Path.Combine(rootDir, ShellPaths.WorkFolderName);
            if (Disabled) return neu;
            lock (Gate)
            {
                string got;
                if (Resolved.TryGetValue(neu, out got)) return got;
                string old = Path.Combine(rootDir, LegacyWorkFolderName);
                if (ReadOnly) return Directory.Exists(neu) || !Directory.Exists(old) ? neu : old;   // not cached: nothing moved, a later real run still migrates
                try { got = Decide(neu, old); }
                catch (Exception e) { got = Directory.Exists(neu) || !Directory.Exists(old) ? neu : old; Pending.Add("work folder: " + e.Message + " -- using " + got); }
                Resolved[neu] = got;
                return got;
            }
        }

        private static string Decide(string neu, string old)
        {
            if (!Directory.Exists(old)) return neu;
            string why = WhyNotNow(old);
            if (why != null)
            {
                if (Directory.Exists(neu)) { Pending.Add("work folder: " + old + " was not moved into " + neu + " (" + why + "); it is moved at a later start"); return neu; }
                Pending.Add("work folder: still using " + old + " (" + why + "); it is renamed to " + neu + " at a later start");
                return old;
            }
            var names = SafeList(old);
            var known = new HashSet<string>(AppFolders.Concat(AppOther), StringComparer.OrdinalIgnoreCase);
            bool onlyApp = names.All(n => known.Contains(n) || n.StartsWith("farm_w", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (!Directory.Exists(neu) && onlyApp)
            {
                try
                {
                    Directory.Move(old, neu);
                    Pending.Add("work folder: renamed " + old + " to " + neu + " (shorter paths)");
                    RenameTemp(neu);
                    return neu;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Pending.Add("work folder: " + old + " could not be renamed (" + e.Message + "); moving its folders one by one");
                }
            }
            // move the app's own folders one by one; the rest stays in the old folder
            Directory.CreateDirectory(neu);
            int moved = 0;
            var left = new List<string>();
            foreach (var n in AppFolders)
            {
                string s = Path.Combine(old, n), d = Path.Combine(neu, n);
                if (!Directory.Exists(s)) continue;
                if (Directory.Exists(d)) { left.Add(n + " (already in the new folder)"); continue; }
                if (SafeFs.IsReparsePoint(s)) { left.Add(n + " (a link)"); continue; }
                try { Directory.Move(s, d); moved++; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { left.Add(n + " (" + e.Message + ")"); }
            }
            string ot = Path.Combine(old, LegacyTempFolderName), nt = Path.Combine(neu, ShellPaths.TempFolderName);
            if (Directory.Exists(ot) && !Directory.Exists(nt) && !SafeFs.IsReparsePoint(ot))
            {
                try { Directory.Move(ot, nt); moved++; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { left.Add(LegacyTempFolderName + " (" + e.Message + ")"); }
            }
            Pending.Add("work folder: moved " + moved + " of the app's folders from " + old + " to " + neu + (left.Count > 0 ? "; not moved: " + string.Join(", ", left) : "")
                        + (onlyApp ? "" : "; the other contents of " + old + " stay where they are"));
            return neu;
        }

        private static void RenameTemp(string work)
        {
            string ot = Path.Combine(work, LegacyTempFolderName), nt = Path.Combine(work, ShellPaths.TempFolderName);
            if (!Directory.Exists(ot) || Directory.Exists(nt)) return;
            try { Directory.Move(ot, nt); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Pending.Add("work folder: " + ot + " could not be renamed to " + nt + " (" + e.Message + "); it is only temporary files: delete it when no bake runs");
            }
        }

        private static List<string> SafeList(string dir)
        {
            try { return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList(); }
            catch (Exception) { return new List<string> { "?" }; }
        }

        /// <summary>Why the old WORK cannot move now (null = it can).</summary>
        private static string WhyNotNow(string old)
        {
            if (SafeFs.IsReparsePoint(old)) return "it is a link";
            CurrentRun cur;
            var k = CurrentRun.Inspect(old, out cur);
            if (k == AttachKind.LiveOtherShell || k == AttachKind.LiveThisShell) return "a bake is running in it";
            var li = GpuLock.Inspect(GpuLock.PathFor(old));
            if (li.Exists && !GpuLock.IsStale(li, SystemProcessProbe.Instance, DateTime.UtcNow)) return "its GPU lock is held";
            string md = Path.Combine(ShellPaths.StandaloneOf(old), StartupRecovery.MarkersFolder);
            if (Directory.Exists(md) && Directory.EnumerateFiles(md).Any(f => Path.GetFileName(f).IndexOf(".done", StringComparison.OrdinalIgnoreCase) < 0))
                return "an unfinished tag write or restore is recorded in it";
            return null;
        }

        /// <summary>A path recorded under an old WORK (ROOT\GPU Lightmap Work\..., its Temp (safe to delete)\...) that no longer exists, mapped to the
        /// same place in the new WORK when that exists; otherwise the path unchanged.</summary>
        public static string Rebase(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) return path;
                string seg = "\\" + LegacyWorkFolderName + "\\";
                int i = path.IndexOf(seg, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return path;
                string rest = path.Substring(i + seg.Length);
                if (rest.StartsWith(LegacyTempFolderName + "\\", StringComparison.OrdinalIgnoreCase))
                    rest = ShellPaths.TempFolderName + rest.Substring(LegacyTempFolderName.Length);
                string cand = path.Substring(0, i) + "\\" + ShellPaths.WorkFolderName + "\\" + rest;
                return File.Exists(cand) || Directory.Exists(cand) ? cand : path;
            }
            catch (Exception) { return path; }
        }
    }
}
