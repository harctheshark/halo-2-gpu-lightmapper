// S2.4 startup recovery (FINAL_DESIGN section 9): after a power loss, the PC's 8 h auto-shutdown or a crash, look for
//   * an interrupted run: WORK\standalone\current_run.json naming a shell that no longer exists -> mark it INTERRUPTED;
//   * a half-finished tag install: OUT\writeback_in_progress.txt in a standalone farm (200-299). lmw_install's marker is JSON
//     (backups[0] + target_dir) -> offer "lmw_install restore"; the tool write-back's marker is a text line naming the tag
//     folder and prefix -> offer "Restore previous lightmap" from _TAGBAK_ui (which renames that marker .restored_<ts>);
//   * a half-finished "Restore previous lightmap" (review fix, finding 7): WORK\standalone\markers\*.in_progress.json, the
//     same JSON shape as lmw_install's (backups[0] = the files as they were before that restore) -> offer lmw_install restore;
//   * a GPU lock left by a dead standalone shell -> release it (only a lock in OUR format whose shell is gone).
// Review fix (finding 1): a marker that belongs to a bake which is STILL RUNNING is never offered for a restore. Markers in
// the farms of the live run (current_run.json names a live shell: its worker + probe worker), and every marker while a live
// holder has WORK\.gpu_bake.lock (another window, the PowerShell launcher, a research bake, a restore), are reported with
// InUse = true; callers must not act on them.
// farm_w149 is never looked at (the scan covers the standalone range only). Nothing here deletes anything: markers are
// handled by lmw_install / TagRestore (renamed to .restored_<ts>), a stale lock is the one small file we created.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    public enum RecoveryKind { InterruptedRun, InstallMarker, ToolWriteBackMarker, StaleOwnLock }

    public sealed class RecoveryItem
    {
        public RecoveryKind Kind;
        public string Path, Message;
        public int Worker;
        public string RunDir;
        /// <summary>InstallMarker: lmw_install restore --backup-dir BackupDir --target-dir TargetDir --marker Path.
        /// ToolWriteBackMarker: TargetDir = the tag folder the text names.</summary>
        public string BackupDir, TargetDir;
        /// <summary>"&lt;leaf&gt;_&lt;bsp&gt;_lightmap" of the set the marker concerns, when known.</summary>
        public string Prefix;
        /// <summary>The marker of an interrupted "Restore previous lightmap" (WORK\standalone\markers).</summary>
        public bool IsTagRestore;
        /// <summary>The marker belongs to a bake that is still running (or a live holder has the GPU lock): never act on it.</summary>
        public bool InUse;
        public string InUseWhy;
        /// <summary>The marker text / JSON could not be parsed: the set it concerns is unknown (treat as concerning every set).</summary>
        public bool Unparsed;
        /// <summary>ToolWriteBackMarker: the number of files gpu_bake copied into BackupDir before the tool ran ("(N files from").</summary>
        public int? BackupFileCount;

        /// <summary>Does this marker concern <paramref name="set"/>? (an unparsed marker concerns every set: fail closed)</summary>
        public bool Concerns(TagSet set)
        {
            if (Kind != RecoveryKind.InstallMarker && Kind != RecoveryKind.ToolWriteBackMarker) return false;
            if (Unparsed || TargetDir == null) return true;
            bool dir;
            try { dir = string.Equals(SafeFs.Norm(TargetDir), SafeFs.Norm(set.TagDir), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return true; }
            return dir && (Prefix == null || string.Equals(Prefix, set.Prefix, StringComparison.OrdinalIgnoreCase));
        }

        public override string ToString() { return Kind + (InUse ? " (IN USE: " + InUseWhy + ")" : "") + ": " + Message; }
    }

    public static class StartupRecovery
    {
        public const string MarkerName = "writeback_in_progress.txt";
        /// <summary>WORK\standalone\markers: the markers of "Restore previous lightmap" runs (TagRestore).</summary>
        public const string MarkersFolder = "markers";
        public const string RestoreMarkerSuffix = ".in_progress.json";

        private static readonly Regex ToolMarkerRx = new Regex(@"previous tags: (?<bak>.*) \((?<n>\d+) files from (?<dir>.*)\\(?<pfx>[^\\]*)\*\)", RegexOptions.Compiled);

        public static string MarkersDir(string work) { return System.IO.Path.Combine(ShellPaths.StandaloneOf(work), MarkersFolder); }

        /// <param name="ownLockContent">The content of a GPU lock THIS caller holds (a TagWriteLease): it does not make the
        /// markers "in use".</param>
        public static List<RecoveryItem> Scan(string work, IProcessProbe probe = null, string ownLockContent = null)
        {
            probe = probe ?? SystemProcessProbe.Instance;
            var items = new List<RecoveryItem>();

            CurrentRun cur;
            var ak = CurrentRun.Inspect(work, out cur, probe);
            if (ak == AttachKind.Stale && cur != null)
            {
                var st = cur.RunDir != null && Directory.Exists(cur.RunDir) ? RunStatus.Load(cur.RunDir) : null;
                items.Add(new RecoveryItem
                {
                    Kind = RecoveryKind.InterruptedRun, Path = CurrentRun.PathFor(work), RunDir = cur.RunDir,
                    Message = "the bake " + cur.RunTag + " was interrupted (the app closed or the PC shut down" + (st != null && st.Phase != null ? ", during phase [" + st.Phase + "] " + st.PhaseTitle : "") + ")",
                });
            }

            // ---- which markers belong to something that is still running
            var busyFarms = new Dictionary<int, string>();
            string allBusy = null;
            if ((ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell) && cur != null)
            {
                string why = "the bake " + cur.RunTag + " is still running";
                RunStatus st = null;
                try { st = cur.RunDir != null && Directory.Exists(cur.RunDir) ? RunStatus.Load(cur.RunDir) : null; } catch (Exception) { st = null; }
                if (st == null) allBusy = why;
                else
                {
                    if (st.Worker > 0) busyFarms[st.Worker] = why;
                    var plan = st.Extra == null ? null : st.Extra.Obj("plan");
                    var pw = plan == null ? null : plan.Long("probeWorker");
                    if (pw.HasValue) busyFarms[(int)pw.Value] = why;
                    if (st.Worker <= 0 && !pw.HasValue) allBusy = why;
                }
            }
            var li = GpuLock.Inspect(GpuLock.PathFor(work));
            if (li.Exists && li.Content != ownLockContent && !GpuLock.IsStale(li, probe, DateTime.UtcNow))
                allBusy = allBusy ?? "a bake holds the GPU lock (" + li.Content + ")";
            Action<RecoveryItem> busy = it =>
            {
                string w;
                if (allBusy != null) { it.InUse = true; it.InUseWhy = allBusy; }
                else if (it.Worker > 0 && busyFarms.TryGetValue(it.Worker, out w)) { it.InUse = true; it.InUseWhy = w; }
            };

            for (int w = Guards.FirstWorker; w <= Guards.LastWorker; w++)
            {
                if (!Guards.WorkerAllowed(w)) continue;
                string farm = System.IO.Path.Combine(work, "farm_w" + w.ToString("D2", CultureInfo.InvariantCulture));
                string marker = System.IO.Path.Combine(farm, MarkerName);
                if (!File.Exists(marker)) continue;
                var it = Classify(marker, w, false);
                busy(it);
                items.Add(it);
            }
            string md = MarkersDir(work);
            if (Directory.Exists(md))
            {
                foreach (var f in Directory.GetFiles(md).Where(f => f.EndsWith(RestoreMarkerSuffix, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var it = Classify(f, 0, true);
                    busy(it);
                    items.Add(it);
                }
            }

            if (li.Exists && li.IsStandalone && li.Pid > 0 && !probe.IsAlive(li.Pid, li.StartTicks))
                items.Add(new RecoveryItem { Kind = RecoveryKind.StaleOwnLock, Path = GpuLock.PathFor(work), Message = "the GPU lock is held by a standalone run whose app is gone (" + li.Content + ")" });
            return items;
        }

        private static RecoveryItem Classify(string marker, int w, bool restoreMarker)
        {
            string text;
            try { text = Json.ReadAllTextShared(marker); }
            catch (Exception e)
            {
                return new RecoveryItem { Kind = restoreMarker ? RecoveryKind.InstallMarker : RecoveryKind.ToolWriteBackMarker, Path = marker, Worker = w, Unparsed = true, IsTagRestore = restoreMarker,
                                          Message = "unreadable marker " + marker + ": " + e.Message };
            }
            JObj j = null;
            try { j = Json.ParseObj(text); } catch (FormatException) { }
            if (j != null && j.Arr("backups") != null && j.Str("target_dir") != null)
            {
                var files = j.Obj("files");
                var pending = files == null ? new List<string>() : files.Where(kv => kv.Value is JObj && ((JObj)kv.Value).Str("state") != "installed").Select(kv => kv.Key).ToList();
                string prefix = j.Str("prefix");
                if (prefix == null && files != null)
                {
                    var lt = files.Select(kv => kv.Key).FirstOrDefault(n => n.EndsWith(".scenario_structure_lightmap", StringComparison.OrdinalIgnoreCase));
                    if (lt != null) prefix = lt.Substring(0, lt.Length - ".scenario_structure_lightmap".Length);
                }
                bool tr = restoreMarker || j.Str("kind") == "tag_restore";
                return new RecoveryItem
                {
                    Kind = RecoveryKind.InstallMarker, Path = marker, Worker = w, IsTagRestore = tr, Prefix = prefix,
                    BackupDir = j.Arr("backups").OfType<string>().FirstOrDefault(), TargetDir = j.Str("target_dir"),
                    Message = (tr ? "a \"Restore previous lightmap\" into " : "a lightmap install into ") + j.Str("target_dir") + " (started " + j.Str("started") + ") did not finish"
                              + (pending.Count > 0 ? "; not " + (tr ? "restored" : "installed") + ": " + string.Join(", ", pending) : "")
                              + (tr ? " -- put back the files as they were before that restore" : " -- restore the previous lightmap from its backup"),
                };
            }
            if (restoreMarker)
                return new RecoveryItem { Kind = RecoveryKind.InstallMarker, Path = marker, IsTagRestore = true, Unparsed = true, Message = "a restore marker that cannot be read: " + marker };
            var it = new RecoveryItem { Kind = RecoveryKind.ToolWriteBackMarker, Path = marker, Worker = w };
            var m = ToolMarkerRx.Match(text);
            if (m.Success)
            {
                it.TargetDir = m.Groups["dir"].Value.Trim(); it.Prefix = m.Groups["pfx"].Value.Trim(); it.BackupDir = m.Groups["bak"].Value.Trim();
                int nf;
                if (int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nf)) it.BackupFileCount = nf;
            }
            else it.Unparsed = true;
            it.Message = "a tool write-back (farm_w" + w + ") did not finish: " + text.Trim() + " -- use 'Restore previous lightmap'"
                         + (it.Prefix != null ? " for " + it.Prefix : "") + " (a full restore of that set clears this marker)";
            return it;
        }

        /// <summary>
        /// The lightmap set a marker concerns (from its tag folder + "&lt;leaf&gt;_&lt;bsp&gt;_lightmap" prefix): the scenario is
        /// the .scenario in that folder whose name starts the prefix (the longest one). Null when unknown or outside ROOT\tags.
        /// </summary>
        public static TagSet SetFor(RecoveryItem it, string root)
        {
            if (it == null || it.TargetDir == null || it.Prefix == null || !it.Prefix.EndsWith("_lightmap", StringComparison.OrdinalIgnoreCase)) return null;
            string tags = System.IO.Path.Combine(root, "tags");
            string dir;
            try { dir = SafeFs.Norm(it.TargetDir); } catch (Exception) { return null; }
            if (!SafeFs.IsUnderOrSame(dir, tags) || string.Equals(dir, SafeFs.Norm(tags), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) return null;
            string core = it.Prefix.Substring(0, it.Prefix.Length - "_lightmap".Length);
            string leaf = Directory.GetFiles(dir, "*.scenario").Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                .Where(l => core.StartsWith(l + "_", StringComparison.OrdinalIgnoreCase) && core.Length > l.Length + 1)
                .OrderByDescending(l => l.Length).FirstOrDefault();
            if (leaf == null) return null;
            string bsp = core.Substring(leaf.Length + 1);
            string rel = dir.Substring(SafeFs.Norm(tags).Length).Trim('\\') + "\\" + leaf;
            return TagSet.For(root, rel, bsp);
        }

        /// <summary>
        /// Review fix (finding 6): after a FULL restore of <paramref name="set"/> (TagRestore, every set file md5-verified) the
        /// standalone farms' markers that concern exactly that set -- the tool write-back's text marker, an lmw_install marker
        /// -- are renamed to "&lt;marker&gt;.restored_&lt;ts&gt;" (never deleted), so the next bake is no longer refused. Markers that
        /// are in use, unparsed, or about another set are left alone. Returns the renamed paths.
        /// </summary>
        public static List<string> ClearMarkersFor(string work, TagSet set, DateTime now, IProcessProbe probe = null, string ownLockContent = null)
        {
            var done = new List<string>();
            string ts = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            foreach (var it in Scan(work, probe, ownLockContent))
            {
                if (it.InUse || it.Unparsed || it.IsTagRestore) continue;
                if (it.Kind != RecoveryKind.ToolWriteBackMarker && it.Kind != RecoveryKind.InstallMarker) continue;
                if (it.TargetDir == null || it.Prefix == null || !it.Concerns(set)) continue;
                string to = it.Path + ".restored_" + ts;
                try { SafeFs.MoveNoReplace(it.Path, to); done.Add(it.Path); }
                catch (IOException) { }
            }
            return done;
        }

        public sealed class UnchangedResult
        {
            /// <summary>The live set was proven unchanged and the marker was renamed aside.</summary>
            public bool Cleared;
            /// <summary>Why it was (or was not) cleared.</summary>
            public string Message;
            public string MovedTo;
            public TagSet Set;
        }

        /// <summary>
        /// R1 fix (V6 finding K2): after a crash / kill during the TOOL write-back [4] BEFORE the tool saved, the live set still
        /// equals what it was before that write-back -- there is nothing to restore (and TagRestore.Newest would skip the equal
        /// backup and pick an OLDER one), yet the text marker refused every later bake of the BSP. This proves "unchanged" and
        /// only then renames the marker to "&lt;marker&gt;.verified_&lt;ts&gt;" (never deleted):
        ///   1. gpu_bake's own copy (the marker's BackupDir, made right before the tool ran) holds exactly the N files the marker
        ///      names, and every lightmap-set file in it (radiance included) is md5-equal to the live one;
        ///   2. the live set has no set file that copy lacks (the tool created nothing); with N = 0 (a first bake) the live set
        ///      must still be empty;
        ///   3. when this app's newest _TAGBAK_ui backup of the set was taken before the marker, the live set equals it too
        ///      (its md5s, incl. the .scenario on a first bake; TagRestore.IsNoOp).
        /// Anything else (in use, unparsed, a differing or missing file, a newer backup) leaves the marker alone: Cleared = false.
        /// The caller holds a TagWriteLease (or is the start-up recovery, where Scan already marks live markers InUse).
        /// </summary>
        public static UnchangedResult ClearToolMarkerIfUnchanged(string work, string root, RecoveryItem it, DateTime now)
        {
            var r = new UnchangedResult();
            if (it == null || it.Kind != RecoveryKind.ToolWriteBackMarker) { r.Message = "not a tool write-back marker"; return r; }
            if (it.InUse) { r.Message = "it belongs to a bake that is still running (" + it.InUseWhy + ")"; return r; }
            if (it.Unparsed || it.BackupDir == null || !it.BackupFileCount.HasValue) { r.Message = "the marker cannot be read"; return r; }
            var set = SetFor(it, root);
            r.Set = set;
            if (set == null) { r.Message = "the marker does not name a lightmap set this app can find"; return r; }
            if (!File.Exists(it.Path)) { r.Message = "the marker is gone"; return r; }
            DateTime markerLocal = File.GetLastWriteTime(it.Path);
            int n = it.BackupFileCount.Value;
            var present = set.PresentNames();
            var bakNames = new List<string>();
            try
            {
                if (n > 0)
                {
                    string bak = it.BackupDir;
                    if (!Directory.Exists(bak) || SafeFs.IsReparsePoint(bak)) { r.Message = "the write-back's own backup folder is missing: " + bak; return r; }
                    var all = Directory.GetFiles(bak).Select(System.IO.Path.GetFileName).ToList();
                    if (all.Count != n) { r.Message = "the write-back's backup folder holds " + all.Count + " file(s), the marker says " + n + ": it is not that write-back's copy"; return r; }
                    bakNames = all.Where(set.IsSetFile).ToList();
                    if (bakNames.Count == 0) { r.Message = "the write-back's backup holds no file of " + set.Prefix; return r; }
                    foreach (var nm in bakNames)
                    {
                        string live = System.IO.Path.Combine(set.TagDir, nm);
                        if (!File.Exists(live)) { r.Message = nm + " is missing from the live set"; return r; }
                        if (!string.Equals(SafeFs.Md5File(live), SafeFs.Md5File(System.IO.Path.Combine(bak, nm)), StringComparison.OrdinalIgnoreCase))
                        { r.Message = nm + " differs from the copy the write-back saved before it ran (the tool saved: restore it)"; return r; }
                    }
                }
                var created = present.Where(p => !bakNames.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                if (created.Count > 0) { r.Message = "the live set has file(s) the write-back's backup does not: " + string.Join(", ", created) + " (the tool saved: restore it)"; return r; }
                var c = TagRestore.Candidates(work, set).FirstOrDefault();
                string cNote = "";
                if (c != null)
                {
                    DateTime cTime = c.Stamp ?? Directory.GetCreationTime(c.Dir);
                    if (cTime > markerLocal) { r.Message = "a backup newer than the marker exists (" + c.Name + "): restore by hand (--list / --from)"; return r; }
                    if (!TagRestore.IsNoOp(set, c)) { r.Message = "the live set differs from the backup " + c.Name + " taken before that bake"; return r; }
                    cNote = " and to this app's backup " + c.Name;
                }
                string to = it.Path + ".verified_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                SafeFs.MoveNoReplace(it.Path, to);
                r.Cleared = true;
                r.MovedTo = to;
                r.Message = "the live " + set.Prefix + " set is unchanged -- md5-equal to the copy the tool write-back saved before it ran (" + bakNames.Count + " set file(s))" + cNote
                            + ": the tool never saved, there is nothing to restore; the marker was set aside as " + to;
                return r;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                r.Message = "could not compare the live set (" + e.Message + ") -- close Sapien / Guerilla and retry";
                return r;
            }
        }

        /// <summary>Marks the interrupted run's status.json INTERRUPTED and clears current_run.json.</summary>
        public static void MarkInterrupted(string work, RecoveryItem it)
        {
            if (it.Kind != RecoveryKind.InterruptedRun) throw new ArgumentException("not an interrupted run");
            if (it.RunDir != null && Directory.Exists(it.RunDir))
            {
                var st = RunStatus.Load(it.RunDir) ?? new RunStatus { RunDir = it.RunDir, RunTag = System.IO.Path.GetFileName(it.RunDir) };
                if (!st.IsFinal)
                {
                    st.State = RunState.Interrupted;
                    st.Reason = "the app or the PC stopped during the run" + (st.Phase != null ? " (phase [" + st.Phase + "])" : "");
                    st.EndedUtc = DateTime.UtcNow;
                    st.Save();
                }
            }
            CurrentRun.Clear(work, it.RunDir);
        }

        /// <summary>Releases a GPU lock left by a dead standalone shell (re-checked right before; anything else is left alone).</summary>
        public static bool ReleaseStaleOwnLock(string work, IProcessProbe probe = null)
        {
            probe = probe ?? SystemProcessProbe.Instance;
            string p = GpuLock.PathFor(work);
            var li = GpuLock.Inspect(p);
            if (!li.Exists || !li.IsStandalone || li.Pid <= 0 || probe.IsAlive(li.Pid, li.StartTicks)) return false;
            var again = GpuLock.Inspect(p);
            if (again.Content != li.Content || again.LastWriteUtc != li.LastWriteUtc) return false;
            try { File.Delete(p); return true; } catch (IOException) { return false; }
        }

        /// <summary>For an InstallMarker item: the lmw_install restore request (the caller adds PyExe / LmwInstallPy / Env).
        /// Refuses an item that is in use or unparsed.</summary>
        public static TagRestoreRequest RestoreRequestFor(RecoveryItem it, string root, string work)
        {
            if (it.Kind != RecoveryKind.InstallMarker) throw new ArgumentException("not an install marker");
            if (it.InUse) throw new SafetyException("refused: " + it.Path + " belongs to a bake that is still running (" + it.InUseWhy + ")");
            if (it.Unparsed || it.BackupDir == null || it.TargetDir == null) throw new SafetyException("refused: the marker " + it.Path + " cannot be read; restore by hand");
            return new TagRestoreRequest { Root = root, Work = work, BackupDir = it.BackupDir, TargetDir = it.TargetDir, Marker = it.Path };
        }
    }

    /// <summary>What a cancelled / failed run leaves to offer (FINAL_DESIGN section 9 "Cancel"): an lmw_install restore when
    /// the install marker exists.</summary>
    public static class CancelAftermath
    {
        public sealed class Offer
        {
            public bool MarkerExists;
            public bool IsInstallMarker;
            public string Marker, BackupDir, TargetDir, Text;
        }

        public static Offer Evaluate(string marker)
        {
            var o = new Offer { Marker = marker, MarkerExists = marker != null && File.Exists(marker) };
            if (!o.MarkerExists) return o;
            o.Text = Json.ReadAllTextShared(marker);
            try
            {
                var j = Json.ParseObj(o.Text);
                o.BackupDir = j.Arr("backups") == null ? null : j.Arr("backups").OfType<string>().FirstOrDefault();
                o.TargetDir = j.Str("target_dir");
                o.IsInstallMarker = o.BackupDir != null && o.TargetDir != null;
            }
            catch (FormatException) { }
            return o;
        }
    }
}
