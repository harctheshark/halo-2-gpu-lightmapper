// Sapien reload signal (2026-10-26 RE of the H2EK build 2023.06.20 sapien.exe, tag_files\file_change_watch_pc.cpp +
// tag_files_sync_pc.cpp + tag_group_dependencies.cpp):
//   * Sapien keeps ONE recursive ReadDirectoryChangesW watch on "tags\" (sub_7F36A0, dwNotifyFilter = 0x14 =
//     FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_LAST_WRITE; NOT FILE_NAME). Each idle frame sub_6D1D80 polls it; changed
//     paths are debounced until the directory is quiet, then sub_7D8310 maps "<tags>\<name>.<ext>" to a LOADED tag, reads the
//     file's tag-header checksum (+0x28) from disk and compares it (and the read-only flag) with the checksum of the tag in
//     memory. A difference marks the tag (and, through the parent/child dependency graph, its dependents) and
//     sub_6D3370 reloads them ("reloaded %s.%s...").
//   * tool_fast writes a tag IN PLACE (open + write + close): MODIFIED events for the real name -> Sapien reloads.
//   * lmw_install.py / TagRestore write <name>.gpulm_new and os.replace / MoveFileEx it over the tag: with Sapien's filter that
//     produces only MODIFIED events for the *.gpulm_new name (ignored: not a tag extension) -- NO event for the real tag name
//     (verified with a ReadDirectoryChangesW filter 0x14 probe) -- so an open Sapien never learns that the lightmap changed.
//   * The fix: after the files are in place, bump their LastWriteTime (SetFileTime, metadata only: not one data byte is
//     written). That raises FILE_NOTIFY_CHANGE_LAST_WRITE for the real name; Sapien then sees the new on-disk checksum
//     (lmw_tagio.set_crc writes it) and reloads. Harmless when Sapien is not open.
// Nothing here throws: a file that is missing, locked or read-only is only reported.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace H2GpuLightmapper.Core.Safety
{
    public sealed class SapienPingResult
    {
        public List<string> Touched = new List<string>();
        public List<string> Skipped = new List<string>();
        /// <summary>One line for the log / message; null when nothing was asked.</summary>
        public string Note
        {
            get
            {
                if (Touched.Count == 0 && Skipped.Count == 0) return null;
                return "Sapien reload signal: " + (Touched.Count == 0 ? "no file could be signalled" : Touched.Count + " tag file(s) signalled (an open Sapien reloads them on its next idle frame)")
                       + (Skipped.Count > 0 ? "; skipped " + string.Join(", ", Skipped) : "");
            }
        }
    }

    public static class SapienReloadPing
    {
        /// <summary>Tag extensions a lightmap install / restore replaces (+ the BSP the probe write-back changes).</summary>
        private static readonly string[] Exts = { ".scenario_structure_lightmap", ".scenario_structure_bsp", ".bitmap", ".scenario" };
        // dependency order, like lmw_install.ORDER: bitmaps first, then the lightmap, then the rest
        private static readonly string[] Order = { "_truecolor_bitmaps.bitmap", "_bitmaps.bitmap", ".scenario_structure_lightmap", ".scenario_structure_bsp", ".scenario" };

        public static bool IsTagFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] == '_') return false;
            return Exts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        private static int OrderKey(string n)
        {
            for (int i = 0; i < Order.Length; i++) if (n.EndsWith(Order[i], StringComparison.OrdinalIgnoreCase)) return i;
            return Order.Length;
        }

        /// <summary>The tag-file names (no folder) directly inside <paramref name="folder"/> (a stage / backup folder).</summary>
        public static List<string> TagNamesIn(string folder)
        {
            var l = new List<string>();
            try { if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) l.AddRange(Directory.GetFiles(folder).Select(Path.GetFileName).Where(IsTagFileName)); }
            catch (Exception) { }
            return l;
        }

        /// <summary>Bumps LastWriteTime of each named file in <paramref name="tagDir"/> (names that are absent are ignored).</summary>
        public static SapienPingResult Touch(string tagDir, IEnumerable<string> names)
        {
            var r = new SapienPingResult();
            if (string.IsNullOrEmpty(tagDir) || names == null) return r;
            foreach (var n in names.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(OrderKey))
                TouchFile(Path.Combine(tagDir, n), n, r);
            return r;
        }

        /// <summary>Same, for full paths (the standalone helper).</summary>
        public static SapienPingResult TouchPaths(IEnumerable<string> paths)
        {
            var r = new SapienPingResult();
            if (paths == null) return r;
            foreach (var p in paths.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => OrderKey(Path.GetFileName(x))))
                TouchFile(p, Path.GetFileName(p), r);
            return r;
        }

        private static void TouchFile(string path, string label, SapienPingResult r)
        {
            try
            {
                if (!File.Exists(path)) return;                    // absent: nothing for Sapien to reload
                DateTime cur = File.GetLastWriteTimeUtc(path), now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(path, now > cur ? now : cur.AddSeconds(1));      // always a real change, so a notification is raised
                r.Touched.Add(label);
            }
            catch (Exception e) { r.Skipped.Add(label + " (" + e.GetType().Name + ")"); }
        }
    }
}
