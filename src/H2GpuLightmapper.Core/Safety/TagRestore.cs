// S2.4 "Restore previous lightmap..." (FINAL_DESIGN section 9): a WORK\_TAGBAK_ui set of this BSP that is not a
// "_before_restore_" folder; the current files are saved aside first (md5-verified); every file is put back atomically
// (copy to .gpulm_new, flush, md5, MoveFileEx REPLACE_EXISTING, re-verify); a set file a later FIRST bake created (absent
// from the backup's existedNames) is renamed aside to .gpulm_removed_<ts>, never deleted. Refuses before changing
// anything when a target is held open (Sapien / Guerilla) or a backup copy no longer matches its recorded md5.
// Review fixes:
//   * finding 3: a folder without backup.json AND without source.txt (a backup that never finished) or a ".tmp_" folder is
//     never a candidate; a legacy source.txt-only backup (no md5s) is marked Verified = false so the UI warns first;
//   * finding 5: Newest() skips a backup that equals the live set (a bake that backed up and then never installed) -- the
//     restore would be a no-op; Restore() of such a backup reports NothingToRestore instead of "restored N file(s)";
//     Candidates() carry InstalledAfter (backup.json) for the picker (GUI) / --from (CLI);
//   * finding 7: before the first replace a marker (lmw_install's JSON shape: backups = [the saved-aside folder, which also
//     gets an _install_manifest.json], target_dir, per-file state) is written to WORK\standalone\markers\; it is renamed
//     .done_<ts> when the restore completes. A crash in between leaves it for StartupRecovery, which offers lmw_install
//     restore = back to the files as they were before this restore;
//   * finding 6: after a complete restore, the standalone farms' markers of exactly this set (the tool write-back's text
//     marker, an lmw_install marker) are renamed .restored_<ts>, so the bake is no longer refused.
// The caller holds a TagWriteLease (TagWriteGate) outside a bake. "Undo last install" = TagInstaller.Restore with the run's
// tag_backup_before_writeback (lmw_install restore).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    public sealed class RestoreCandidate
    {
        public string Dir, Name;
        public DateTime? Stamp;
        public JObj Manifest;
        /// <summary>name -> md5 recorded at backup time (null md5 for a legacy backup without backup.json).</summary>
        public List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();
        /// <summary>backup.json with an md5 for every file (false: a legacy source.txt-only backup, restored unverified).</summary>
        public bool Verified { get { return Manifest != null && Files.All(f => f.Value != null); } }
        /// <summary>The bake that made this backup then changed the live set (backup.json installedAfter).</summary>
        public bool InstalledAfter { get { return Manifest != null && Manifest.Bool("installedAfter") == true; } }
        public string Describe()
        {
            return (Stamp.HasValue ? Stamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?") + "   " + Name + "   " + Files.Count + " file(s)"
                   + (InstalledAfter ? "   (a bake installed after it)" : "") + (Verified ? "" : "   NOT VERIFIED (legacy backup without md5s)");
        }
        public override string ToString() { return Name; }
    }

    public enum RestoreOutcome { Restored, Refused, Failed, NothingToRestore }

    public sealed class RestoreResult
    {
        public RestoreOutcome Outcome;
        public string Message, FromDir, SavedAsideDir, MarkerPath;
        public List<string> Restored = new List<string>();
        public List<string> RenamedAside = new List<string>();
        /// <summary>Markers of this set that the completed restore made obsolete (renamed .restored_&lt;ts&gt;).</summary>
        public List<string> ClearedMarkers = new List<string>();
    }

    public static class TagRestore
    {
        private static readonly string[] Order = { "_truecolor_bitmaps.bitmap", "_bitmaps.bitmap", ".scenario_structure_lightmap", ".scenario" };
        private static int OrderKey(string n)
        {
            for (int i = 0; i < Order.Length; i++) if (n.EndsWith(Order[i], StringComparison.OrdinalIgnoreCase)) return i;
            return Order.Length;
        }

        /// <summary>This BSP's _TAGBAK_ui backups, newest first ("_before_restore_", ".tmp_" and unfinished folders excluded).</summary>
        public static List<RestoreCandidate> Candidates(string work, TagSet set)
        {
            var r = new List<RestoreCandidate>();
            string ui = TagBackup.UiRoot(work);
            if (!Directory.Exists(ui)) return r;
            string head = set.Leaf + "_" + set.Bsp + "_";
            foreach (var d in Directory.GetDirectories(ui))
            {
                string name = Path.GetFileName(d);
                if (!name.StartsWith(head, StringComparison.OrdinalIgnoreCase) || name.IndexOf("_before_restore_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf(TagBackup.TmpMarker, StringComparison.OrdinalIgnoreCase) >= 0) continue;          // still being written / failed
                if (SafeFs.IsReparsePoint(d)) continue;
                bool hasJson = File.Exists(Path.Combine(d, "backup.json")), hasSource = File.Exists(Path.Combine(d, "source.txt"));
                if (!hasJson && !hasSource) continue;                                                               // never finished
                var c = new RestoreCandidate { Dir = d, Name = name };
                DateTime ts;
                string rest = name.Substring(head.Length);
                if (rest.Length >= 15 && DateTime.TryParseExact(rest.Substring(0, 15), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts)) c.Stamp = ts;
                try { c.Manifest = hasJson ? Json.ReadObjFile(Path.Combine(d, "backup.json")) : null; } catch (Exception) { c.Manifest = null; }
                if (hasJson && c.Manifest == null) continue;                                                        // a damaged backup.json: not trusted
                string prefix = c.Manifest != null ? c.Manifest.Str("prefix") : ReadSourcePrefix(d);
                if (prefix != null && !string.Equals(prefix, set.Prefix, StringComparison.OrdinalIgnoreCase)) continue;   // another BSP with a colliding name
                if (c.Manifest != null && c.Manifest.Arr("files") != null)
                {
                    foreach (var f in c.Manifest.Arr("files").OfType<JObj>()) c.Files.Add(new KeyValuePair<string, string>(f.Str("name"), f.Str("md5")));
                }
                else
                {
                    foreach (var f in Directory.GetFiles(d).Select(Path.GetFileName).Where(set.IsSetFile))
                        c.Files.Add(new KeyValuePair<string, string>(f, null));
                }
                if (c.Files.Count == 0) continue;
                r.Add(c);
            }
            return r.OrderByDescending(c => c.Stamp ?? Directory.GetCreationTime(c.Dir)).ThenByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The newest candidate whose restore would change something (a backup equal to the live set is skipped).</summary>
        public static RestoreCandidate Newest(string work, TagSet set)
        {
            var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return Candidates(work, set).FirstOrDefault(c => !IsNoOp(set, c, live));
        }

        public static RestoreCandidate ByName(string work, TagSet set, string name)
        {
            return Candidates(work, set).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when restoring <paramref name="c"/> would change nothing: every backed-up file equals the live one and
        /// no later set file would be renamed aside. <paramref name="liveMd5"/> caches live md5s across candidates.</summary>
        public static bool IsNoOp(TagSet set, RestoreCandidate c, Dictionary<string, string> liveMd5 = null)
        {
            if (c == null || c.Files.Count == 0) return true;
            liveMd5 = liveMd5 ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in c.Files)
            {
                string live = Path.Combine(set.TagDir, f.Key);
                if (!File.Exists(live)) return false;
                string lm, bm = f.Value;
                try
                {
                    if (!liveMd5.TryGetValue(f.Key, out lm)) { lm = SafeFs.Md5File(live); liveMd5[f.Key] = lm; }
                    if (bm == null)
                    {
                        string src = Path.Combine(c.Dir, f.Key);
                        if (!File.Exists(src)) return false;
                        bm = SafeFs.Md5File(src);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }   // held open: the restore itself reports it
                if (!string.Equals(lm, bm, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return AsideNames(set, c).Count == 0;
        }

        /// <summary>Set files present now that the backup's bake did not have (a later first bake created them).</summary>
        private static List<string> AsideNames(TagSet set, RestoreCandidate c)
        {
            var aside = new List<string>();
            var existed = c.Manifest == null || c.Manifest.Arr("existedNames") == null ? null
                        : new HashSet<string>(c.Manifest.Arr("existedNames").OfType<string>(), StringComparer.OrdinalIgnoreCase);
            if (existed != null)
                foreach (var n in set.PresentNames().Where(n => !TagSet.IsRadiance(n)))
                    if (!existed.Contains(n) && !c.Files.Any(f => string.Equals(f.Key, n, StringComparison.OrdinalIgnoreCase))) aside.Add(n);
            return aside;
        }

        private static string ReadSourcePrefix(string dir)
        {
            string p = Path.Combine(dir, "source.txt");
            if (!File.Exists(p)) return null;
            var lines = File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count >= 2 ? lines[1] : null;
        }

        /// <summary>WORK\standalone\markers\restore_&lt;leaf&gt;_&lt;bsp&gt;.in_progress.json (StartupRecovery scans it).</summary>
        public static string MarkerPathFor(string work, TagSet set)
        {
            return Path.Combine(StartupRecovery.MarkersDir(work), "restore_" + set.Leaf + "_" + set.Bsp + StartupRecovery.RestoreMarkerSuffix);
        }

        /// <param name="ownLockContent">The GPU lock content of the caller's TagWriteLease (so the marker cleanup does not take
        /// the caller's own lock for a running bake).</param>
        public static RestoreResult Restore(string work, TagSet set, RestoreCandidate c, DateTime now, string ownLockContent = null)
        {
            var res = new RestoreResult { FromDir = c == null ? null : c.Dir };
            if (c == null || c.Files.Count == 0) { res.Outcome = RestoreOutcome.NothingToRestore; res.Message = "no backup of " + set.Leaf + " / " + set.Bsp + " exists yet"; return res; }
            if (!Directory.Exists(set.TagDir)) { res.Outcome = RestoreOutcome.Failed; res.Message = "the tag folder is missing: " + set.TagDir; return res; }
            Guards.AssertTargetUnderTags(set.TagDir, set.Root);
            string ts = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            // 1. what will change: the backed-up files, plus set files a later first bake created
            var files = c.Files.OrderBy(f => OrderKey(f.Key)).ToList();
            var aside = AsideNames(set, c);

            // 2. refuse before any change: a backup copy that no longer verifies, a restore that changes nothing, a target held
            //    open, an earlier restore of this set that never finished
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string src = Path.Combine(c.Dir, f.Key);
                if (!File.Exists(src)) { res.Outcome = RestoreOutcome.Failed; res.Message = "the backup file is missing: " + src + " -- nothing was changed"; return res; }
                string m = SafeFs.Md5File(src);
                if (f.Value != null && !string.Equals(m, f.Value, StringComparison.OrdinalIgnoreCase)) { res.Outcome = RestoreOutcome.Failed; res.Message = "the backup " + src + " no longer matches its recorded md5 -- nothing was changed"; return res; }
                md5[f.Key] = m;
            }
            string markerPath = MarkerPathFor(work, set);
            if (File.Exists(markerPath))
            {
                res.Outcome = RestoreOutcome.Refused;
                res.Message = "an earlier restore of this lightmap did not finish (" + markerPath + "): recover it first (the start-up recovery, or --cli restore --recover)";
                return res;
            }
            var busy = new List<string>();
            foreach (var n in files.Select(f => f.Key).Concat(aside))
            {
                string why;
                if (!SafeFs.ExclusiveOk(Path.Combine(set.TagDir, n), out why)) busy.Add(n + ": " + why);
            }
            if (busy.Count > 0) { res.Outcome = RestoreOutcome.Refused; res.Message = "the lightmap files are open in another program (close Sapien / Guerilla and retry): " + string.Join("; ", busy); return res; }
            var liveNow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string t = Path.Combine(set.TagDir, f.Key);
                if (File.Exists(t)) liveNow[f.Key] = SafeFs.Md5File(t);
            }
            if (aside.Count == 0 && files.All(f => liveNow.ContainsKey(f.Key) && string.Equals(liveNow[f.Key], md5[f.Key], StringComparison.OrdinalIgnoreCase)))
            {
                res.Outcome = RestoreOutcome.NothingToRestore;
                res.Message = "the live lightmap of " + set.Leaf + " / " + set.Bsp + " already equals the backup " + c.Name + " -- nothing to restore (pick an older backup)";
                return res;
            }

            // 3. save the current files aside (md5-verified) + the rollback manifest lmw_install restore reads
            var cur = files.Select(f => f.Key).Concat(aside).Where(n => File.Exists(Path.Combine(set.TagDir, n))).ToList();
            var saved = TagBackup.Create(work, set, BackupKind.BeforeRestore, now, cur);
            res.SavedAsideDir = saved.Dir;
            var existed = new JObj();
            foreach (var kv in saved.Files) existed[kv.Key] = kv.Value;
            var absent = files.Select(f => f.Key).Where(n => !File.Exists(Path.Combine(set.TagDir, n))).Cast<object>().ToList();
            var staged = new JObj();
            foreach (var f in files) staged[f.Key] = md5[f.Key];
            SafeFs.WriteAllTextAtomic(Path.Combine(saved.Dir, "_install_manifest.json"), Json.Write(new JObj().Add("made", ts).Add("target_dir", set.TagDir)
                .Add("existed", existed).Add("absent_before", absent).Add("staged", staged).Add("kind", "tag_restore").Add("from", c.Dir), true));

            // 4. the marker (before the first replace)
            var mfiles = new JObj();
            foreach (var f in files) mfiles[f.Key] = new JObj().Add("restore_md5", md5[f.Key]).Add("previous_md5", liveNow.ContainsKey(f.Key) ? liveNow[f.Key] : null).Add("state", "pending");
            foreach (var n in aside) mfiles[n] = new JObj().Add("rename_aside", true).Add("state", "pending");
            var marker = new JObj().Add("kind", "tag_restore").Add("started", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Add("from", c.Dir)
                .Add("target_dir", set.TagDir).Add("backups", new List<object> { saved.Dir }).Add("prefix", set.Prefix).Add("scenario", set.ScenarioRel).Add("bsp", set.Bsp)
                .Add("note", "a 'Restore previous lightmap' was interrupted -- put back the files as they were before it with lmw_install.py restore --backup-dir \"" + saved.Dir + "\" --target-dir \"" + set.TagDir + "\"")
                .Add("files", mfiles);
            SafeFs.WriteAllTextAtomic(markerPath, Json.Write(marker, true));
            res.MarkerPath = markerPath;

            // 5. put the backup back, file by file, atomically
            try
            {
                foreach (var f in files)
                {
                    SafeFs.ReplaceVerified(Path.Combine(c.Dir, f.Key), Path.Combine(set.TagDir, f.Key), md5[f.Key]);
                    res.Restored.Add(f.Key);
                    ((JObj)mfiles[f.Key])["state"] = "installed";
                    SafeFs.WriteAllTextAtomic(markerPath, Json.Write(marker, true));
                }
                foreach (var n in aside)
                {
                    string t = Path.Combine(set.TagDir, n);
                    if (File.Exists(t))
                    {
                        SafeFs.MoveNoReplace(t, t + ".gpulm_removed_" + ts);
                        res.RenamedAside.Add(n);
                    }
                    ((JObj)mfiles[n])["state"] = "installed";
                    SafeFs.WriteAllTextAtomic(markerPath, Json.Write(marker, true));
                }
            }
            catch (Exception e)
            {
                res.Outcome = RestoreOutcome.Failed;
                if (res.Restored.Count == 0 && res.RenamedAside.Count == 0)
                {
                    try { SafeFs.MoveNoReplace(markerPath, markerPath + ".aborted_" + ts); } catch (IOException) { }
                    res.Message = "restore FAILED before any file was replaced: " + e.Message + " -- nothing was changed";
                }
                else
                    res.Message = "restore FAILED after " + res.Restored.Count + " file(s): " + e.Message + " -- the files as they were before the restore are in "
                                  + res.SavedAsideDir + "; the marker " + markerPath + " stays, so the next start (or --cli restore --recover) puts them back";
                return res;
            }
            try { SafeFs.MoveNoReplace(markerPath, markerPath + ".done_" + ts); } catch (IOException) { }
            try { res.ClearedMarkers = StartupRecovery.ClearMarkersFor(work, set, now, null, ownLockContent); } catch (Exception) { }
            res.Outcome = RestoreOutcome.Restored;
            res.Message = "restored " + res.Restored.Count + " file(s) from " + c.Dir + (res.RenamedAside.Count > 0 ? "; renamed aside " + string.Join(", ", res.RenamedAside) + " (created after that backup)" : "")
                          + (c.Verified ? "" : " (a legacy backup without md5s: restored as found)")
                          + (res.ClearedMarkers.Count > 0 ? "; cleared the unfinished write-back marker(s) of this lightmap: " + string.Join(", ", res.ClearedMarkers) : "")
                          + ". Reload the map in Sapien.";
            return res;
        }
    }
}
