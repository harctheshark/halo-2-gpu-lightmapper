// 0.6.8 bounded temp (docs\design\DISK_USAGE_0.6.8.md, sections 5.1-5.8). The app's temporary working root is
// WORK\Temp (safe to delete)\:
//     READ ME - safe to delete.txt
//     farm_wNN\      the workers: this bake's products + (when kept) its capture; .lock + .h2lm_run.json while a bake uses it
//     m<6hex>\       one .map bake (.lock while it runs): tags_x\, r\ (the overlay root), h2work\, logs\ (copied to KEEP at the end)
//     q<6hex>\       a Vista map query's scratch
// Rules this class enforces:
//   * the user may delete Temp at ANY moment, even mid-bake: nothing tag safety depends on lives there (install markers, install
//     sources and every tag backup are in KEEP = WORK\standalone, WORK\_TAGBAK_ui); a bake whose run root vanished stops with
//     RootGoneMessage and writes nothing;
//   * no junction or symbolic link is ever created inside Temp; every delete here is SafeFs.RemoveTree, which refuses on a link
//     (or, for a .map run folder, unlinks only links that point inside that same folder and refuses on any other: a tripwire);
//   * a folder whose .lock cannot be opened belongs to a live bake and is never touched; while a live holder has WORK\.gpu_bake.lock
//     the start-up sweep touches nothing at all;
//   * bounded: after every bake the farm is slimmed to its capture (or deleted), at most KeepCaptures captures stay (newest first),
//     Temp is capped (CapGB, oldest evicted first), failed-run bundles are kept for the newest 3 runs only, tag backups are bounded;
//   * old 0.6.7 folders (WORK\farm_wNN, WORK\_mapbake, WORK\_gpulm_skel, WORK\_TAGBAK_auto, old runtimes) are removed only by the opt-in
//     ListLegacy / RemoveLegacy (a button / --cli cleanup --legacy that lists exactly what it deletes) -- never by a sweep: research
//     farms can sit in the product's 200-299 range.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    /// <summary>The two user settings of the bounded temp (Advanced: "Keep captures for reuse", "Temp size cap").</summary>
    public sealed class TempSettings
    {
        public const int DefaultKeepCaptures = 3, MaxKeepCaptures = 10;
        public const int DefaultCapGB = 40, MinCapGB = 10, MaxCapGB = 250;
        public const string KeepCapturesKey = "keepCaptures", CapGBKey = "tempCapGB";

        public int KeepCaptures = DefaultKeepCaptures;
        public int CapGB = DefaultCapGB;
        public long CapBytes { get { return (long)CapGB << 30; } }

        public static TempSettings From(AppSettings s)
        {
            var t = new TempSettings();
            if (s == null) return t;
            t.KeepCaptures = ClampKeep(AsInt(s[KeepCapturesKey], DefaultKeepCaptures));
            t.CapGB = ClampCap(AsInt(s[CapGBKey], DefaultCapGB));
            return t;
        }

        public void SaveTo(AppSettings s)
        {
            s[KeepCapturesKey] = (long)ClampKeep(KeepCaptures);
            s[CapGBKey] = (long)ClampCap(CapGB);
        }

        public static int ClampKeep(int v) { return Math.Max(0, Math.Min(MaxKeepCaptures, v)); }
        public static int ClampCap(int v) { return Math.Max(MinCapGB, Math.Min(MaxCapGB, v)); }

        private static int AsInt(object o, int dflt)
        {
            if (o is long) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)o));
            if (o is double) return (int)(double)o;
            int v;
            if (o is string && int.TryParse((string)o, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return dflt;
        }
    }

    /// <summary>
    /// A bake's hold on one Temp folder (a farm_wNN or an m&lt;id&gt;): &lt;dir&gt;\.lock opened with FileShare.None for the whole run (a sweep,
    /// Clean now or another window can then prove the folder is in use), plus the sentinel &lt;dir&gt;\.h2lm_run.json, which Explorer CAN
    /// delete: when it is gone the folder was wiped under the bake (<see cref="Gone"/>). Dispose releases the lock and removes both files.
    /// </summary>
    public sealed class TempLease : IDisposable
    {
        public const string LockName = ".lock", SentinelName = ".h2lm_run.json";
        private FileStream _fs;
        public string Dir { get; private set; }
        public string Sentinel { get { return Path.Combine(Dir, SentinelName); } }

        private TempLease() { }

        /// <summary>Creates <paramref name="dir"/> when missing and takes its lock; throws <see cref="SafetyException"/> when another live
        /// process holds it.</summary>
        public static TempLease Acquire(string dir, string runTag)
        {
            Directory.CreateDirectory(dir);
            FileStream fs;
            try { fs = new FileStream(Path.Combine(dir, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { throw new SafetyException(dir + " is in use by another bake (" + e.Message + ")"); }
            var l = new TempLease { _fs = fs, Dir = dir };
            try
            {
                var b = Encoding.UTF8.GetBytes("H2LM TEMP " + (runTag ?? "?") + " pid " + ProcessInfo.CurrentPid + "\r\n");
                fs.SetLength(0); fs.Write(b, 0, b.Length); fs.Flush(true);
                SafeFs.WriteAllTextAtomic(l.Sentinel, Json.Write(new JObj().Add("schema", "h2lm-temp-run/1").Add("run", runTag).Add("pid", (long)ProcessInfo.CurrentPid)
                                                                     .Add("started", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)), true));
            }
            catch { l.Dispose(); throw; }
            return l;
        }

        /// <summary>The folder (or its sentinel) was deleted while the bake held it: the user cleared Temp mid-bake.</summary>
        public bool Gone
        {
            get
            {
                try { return !Directory.Exists(Dir) || !File.Exists(Sentinel); }
                catch (Exception) { return true; }
            }
        }

        public void Dispose()
        {
            var fs = _fs; _fs = null;
            if (fs == null) return;
            try { fs.Dispose(); } catch (Exception) { }
            SafeFs.TryDeleteFile(Path.Combine(Dir, LockName));
            SafeFs.TryDeleteFile(Sentinel);
        }
    }

    public sealed class JanitorReport
    {
        public List<string> Lines = new List<string>();
        public List<string> Problems = new List<string>();
        public long FreedBytes;
        public int Removed;
        public bool Skipped;
        public string SkipReason;
        public void Add(string l) { Lines.Add(l); }
        public override string ToString()
        {
            if (Skipped) return "skipped: " + SkipReason;
            return Removed + " folder(s) removed, " + Gb(FreedBytes) + " freed" + (Problems.Count > 0 ? "; " + Problems.Count + " problem(s): " + string.Join("; ", Problems.Take(3)) : "");
        }
        public static string Gb(long b) { return (b / 1073741824.0).ToString("0.0#", CultureInfo.InvariantCulture) + " GB"; }
    }

    public enum TempItemKind { Farm, MapRun, Query, Other }

    public sealed class TempItem
    {
        public string Path, Name;
        public TempItemKind Kind;
        public int Worker;
        /// <summary>A live bake holds its .lock.</summary>
        public bool Live;
        /// <summary>A .lock file exists that CAN be opened: its bake died (crash, kill, power-off).</summary>
        public bool OrphanLock;
        /// <summary>A farm with a capture record (standalone_capture.json) and no lock: a cached capture for "Reuse existing capture".</summary>
        public bool CachedCapture;
        public DateTime LastUse;
        public long Bytes = -1;
        public override string ToString() { return Name + (Live ? " (in use)" : CachedCapture ? " (capture kept for reuse)" : OrphanLock ? " (left by a crashed bake)" : ""); }
    }

    public sealed class LegacyItem
    {
        public string Path, Parent, Kind, Why;
        public long Bytes;
        /// <summary>The folder may hold links (a skeleton overlay or a 0.6.7 .map folder): they are unlinked (never followed) before the delete.</summary>
        public bool UnlinkFirst;
        public OldVersionInfo OldVersion;
        public override string ToString() { return Kind + ": " + Path + " (" + JanitorReport.Gb(Bytes) + ")" + (Why != null ? " -- " + Why : ""); }
    }

    /// <summary>What a bake hands the janitor when it ends.</summary>
    public sealed class AfterBakeRequest
    {
        public string Work, RunDir;
        public bool Success;
        public TempSettings Settings = new TempSettings();
        /// <summary>The farms this bake used: (folder, isMapBake).</summary>
        public List<KeyValuePair<string, bool>> Farms = new List<KeyValuePair<string, bool>>();
        /// <summary>INST\standalone\farm_products.json (the engine-owned list of phase-3/4 products); null or missing = no slim: a farm is
        /// either kept whole (never: a capture is only kept slimmed) or deleted whole.</summary>
        public string ProductsJson;
        /// <summary>The farms are in Temp (the 0.6.8 layout). In the 0.6.7 layout (a payload without features.farms) the janitor leaves the
        /// farms alone, as 0.6.7 did; the KEEP-side bounds (run bundles) still apply.</summary>
        public bool FarmsInTemp = true;
        public Action<string> Log;
    }

    public static class TempJanitor
    {
        public const int FailedBundlesKeep = 3, RunTagbakKeep = 10, UiKeepNewest = 5, AutoKeepNewest = 3;
        public const double OrphanMapHours = 24, OrphanQueryHours = 1, LeftoverFarmHours = 24;
        public const long MinFreeBytes = 8L << 30, DefaultFarmNeedBytes = 3L << 30;
        public const long LogFileCap = 5L << 20, LogTotalCap = 20L << 20;
        /// <summary>= CaptureRecord.FileName (the shell's capture record in a farm; a test pins the two together).</summary>
        public const string CaptureRecordName = "standalone_capture.json";
        public const string FarmLogsDir = "farm_logs", FailedDir = "failed";
        public const string AutoOwnedFile = "tagbak_auto_owned.json";
        public const string FarmSizesFile = "farm_sizes.json";

        public const string RootGoneMessage = "The temporary folder was deleted during the bake. Nothing was written to your tags. Bake again.";
        public const string RootGoneAfterWriteMessage = "The temporary folder was deleted during the bake, after the lightmap was written and verified. Your tags are fine; the run's temporary files are gone.";

        public static readonly string ReadMeText =
            "This folder holds the H2 GPU Lightmapper's temporary working files: the workers (farm_wNN), the folders of\r\n" +
            "bakes from a .map (m...) and map queries (q...).\r\n\r\n" +
            "It is SAFE TO DELETE at any time, even while a bake runs:\r\n" +
            "  * your tags are never written from here; the install source, the install marker and every tag backup live\r\n" +
            "    elsewhere (GPU Lightmap Work\\standalone and GPU Lightmap Work\\_TAGBAK_ui);\r\n" +
            "  * a bake whose folder disappears stops and tells you that nothing was written to your tags; just bake again;\r\n" +
            "  * the only thing lost is a kept capture: the next bake of that map captures again (\"Reuse existing capture\" is unavailable).\r\n\r\n" +
            "The app also keeps it small by itself: after every bake only a few captures are kept for reuse (Advanced settings:\r\n" +
            "\"Keep captures for reuse\", default 3) and the folder is capped (\"Temp size cap\", default 40 GB; the oldest go first).\r\n" +
            "Advanced > Temp folder > Clean now empties it from the app.\r\n";

        private static readonly Regex FarmRx = new Regex(@"^farm_w(\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapRx = new Regex(@"^m[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QueryRx = new Regex(@"^q[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StampRx = new Regex(@"(\d{8}_\d{6})(_\d+)?$", RegexOptions.Compiled);

        // ================================================================== the Temp root

        /// <summary>Creates WORK\Temp (safe to delete) and its READ ME when missing; returns the folder.</summary>
        public static string EnsureTemp(string work)
        {
            string t = ShellPaths.TempOf(work);
            Directory.CreateDirectory(t);
            string rm = Path.Combine(t, ShellPaths.TempReadMeName);
            if (!File.Exists(rm)) { try { File.WriteAllText(rm, ReadMeText, new UTF8Encoding(false)); } catch (Exception) { } }
            return t;
        }

        /// <summary>A new, unused child name of Temp: &lt;prefix&gt;&lt;6 hex&gt; (m = a .map bake, q = a Vista query).</summary>
        public static string NewRunDir(string work, char prefix)
        {
            string t = EnsureTemp(work);
            for (int i = 0; i < 100; i++)
            {
                string d = Path.Combine(t, prefix + Guid.NewGuid().ToString("N").Substring(0, 6));
                if (!Directory.Exists(d) && !File.Exists(d)) return d;
            }
            throw new IOException("no free temporary folder name under " + t);
        }

        /// <summary>A live bake holds this folder's .lock (it cannot be opened). A missing .lock is not live.</summary>
        public static bool IsLive(string dir)
        {
            string lk = Path.Combine(dir, TempLease.LockName);
            if (!File.Exists(lk)) return false;
            try { using (new FileStream(lk, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>Every child of Temp, classified (no size: <see cref="Measure"/>).</summary>
        public static List<TempItem> List(string work)
        {
            var r = new List<TempItem>();
            string t = ShellPaths.TempOf(work);
            if (!Directory.Exists(t)) return r;
            foreach (var d in Directory.GetDirectories(t))
            {
                string n = Path.GetFileName(d);
                var it = new TempItem { Path = d, Name = n, Kind = TempItemKind.Other };
                var fm = FarmRx.Match(n);
                if (fm.Success) { it.Kind = TempItemKind.Farm; it.Worker = int.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture); }
                else if (MapRx.IsMatch(n)) it.Kind = TempItemKind.MapRun;
                else if (QueryRx.IsMatch(n)) it.Kind = TempItemKind.Query;
                bool hasLock = File.Exists(Path.Combine(d, TempLease.LockName));
                it.Live = hasLock && IsLive(d);
                it.OrphanLock = hasLock && !it.Live;
                string rec = Path.Combine(d, CaptureRecordName);
                it.CachedCapture = it.Kind == TempItemKind.Farm && !hasLock && File.Exists(rec);
                try { it.LastUse = it.CachedCapture ? File.GetLastWriteTime(rec) : Directory.GetLastWriteTime(d); } catch (Exception) { it.LastUse = DateTime.MinValue; }
                r.Add(it);
            }
            return r;
        }

        public static void Measure(IEnumerable<TempItem> items) { foreach (var it in items) if (it.Bytes < 0) it.Bytes = SafeFs.DirSize(it.Path); }

        /// <summary>Total size of Temp (no link followed).</summary>
        public static long TempSize(string work) { return SafeFs.DirSize(ShellPaths.TempOf(work)); }

        /// <summary>Deletes one Temp child (never a live one): a .map run folder with its internal links unlinked first (the tripwire refuses any
        /// other link), everything else with the plain no-link remover.</summary>
        public static bool RemoveItem(string work, TempItem it, JanitorReport rep, string why)
        {
            if (it.Live || IsLive(it.Path)) { rep.Add("kept " + it.Name + ": in use by a running bake"); return false; }
            try
            {
                long b = it.Bytes >= 0 ? it.Bytes : SafeFs.DirSize(it.Path);
                var log = new List<string>();
                SafeFs.RemoveTree(it.Path, ShellPaths.TempOf(work), it.Kind == TempItemKind.MapRun, log);
                rep.FreedBytes += b; rep.Removed++;
                rep.Add("removed " + it.Name + " (" + JanitorReport.Gb(b) + ")" + (why != null ? ": " + why : "") + (log.Count > 0 ? " [" + log.Count + " internal link(s) unlinked]" : ""));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                rep.Problems.Add(it.Name + ": " + e.Message);
                rep.Add("could not remove " + it.Name + ": " + e.Message);
                return false;
            }
        }

        // ================================================================== after a bake (5.3)

        /// <summary>
        /// When a bake ends (any outcome): for each of its farms, copy the log bundle into KEEP RUN\farm_logs\farm_wNN (tail 5 MB per file,
        /// 20 MB in all); on failure also the farm's tagw_stage into RUN\failed\ (kept for the newest 3 runs); then a .map farm is deleted
        /// whole, a scenario farm is slimmed to its capture (the engine-owned product list) when captures are kept, else deleted. Then the
        /// capture cache is trimmed to KeepCaptures and Temp to its cap. Tolerates a Temp the user already deleted.
        /// </summary>
        public static JanitorReport AfterBake(AfterBakeRequest q)
        {
            var rep = new JanitorReport();
            Action<string> say = l => { rep.Add(l); var h = q.Log; if (h != null) { try { h(l); } catch (Exception) { } } };
            var products = FarmProducts.Load(q.ProductsJson);
            foreach (var kv in q.Farms)
            {
                string farm = kv.Key;
                bool map = kv.Value;
                try
                {
                    if (!Directory.Exists(farm)) { say("temp: " + Path.GetFileName(farm) + " is already gone (the temporary folder was cleared)"); continue; }
                    if (q.RunDir != null && Directory.Exists(q.RunDir))
                    {
                        int nl = CopyLogBundle(farm, Path.Combine(q.RunDir, FarmLogsDir, Path.GetFileName(farm)));
                        say("temp: " + nl + " log file(s) of " + Path.GetFileName(farm) + " copied to " + Path.Combine(q.RunDir, FarmLogsDir));
                        if (!q.Success)
                        {
                            string st = Path.Combine(farm, "tagw_stage");
                            if (Directory.Exists(st) && !map)
                            {
                                string to = Path.Combine(q.RunDir, FailedDir, Path.GetFileName(farm) + "_tagw_stage");
                                int nf = CopyFlat(st, to);
                                say("temp: the failed run's stage (" + nf + " file(s)) kept in " + to);
                            }
                        }
                    }
                    if (!q.FarmsInTemp) continue;
                    var it = new TempItem { Path = farm, Name = Path.GetFileName(farm), Kind = TempItemKind.Farm, Live = false };
                    long before = SafeFs.DirSize(farm);
                    RecordFarmSize(q.Work, it.Name, before);
                    bool keep = !map && q.Settings.KeepCaptures > 0 && products != null && File.Exists(Path.Combine(farm, CaptureRecordName));
                    if (keep)
                    {
                        long freed = products.Slim(farm, say);
                        rep.FreedBytes += freed;
                        say("temp: " + it.Name + " slimmed to its capture (" + JanitorReport.Gb(freed) + " of phase-3/4 products removed; kept for \"Reuse existing capture\")");
                    }
                    else
                    {
                        it.Bytes = before;
                        RemoveItem(q.Work, it, rep, map ? "a .map bake's farm is never kept" : q.Settings.KeepCaptures == 0 ? "\"Keep captures for reuse\" is 0"
                                                              : products == null ? "this payload has no farm product list" : "no capture record");
                    }
                }
                catch (Exception e) { rep.Problems.Add(Path.GetFileName(farm) + ": " + e.Message); say("temp: " + Path.GetFileName(farm) + ": " + e.Message); }
            }
            if (q.FarmsInTemp)
            {
                var r2 = TrimCaptures(q.Work, q.Settings.KeepCaptures);
                var r3 = EnforceCap(q.Work, q.Settings.CapBytes);
                foreach (var r in new[] { r2, r3 }) { foreach (var l in r.Lines) say(l); rep.Problems.AddRange(r.Problems); rep.FreedBytes += r.FreedBytes; rep.Removed += r.Removed; }
            }
            var r4 = PruneRuns(q.Work);
            foreach (var l in r4.Lines) say(l);
            rep.Problems.AddRange(r4.Problems);
            return rep;
        }

        /// <summary>Copies a farm's *.log / *.txt / *.json (top folder and one level down, links never followed) into <paramref name="to"/>:
        /// the tail 5 MB of each, 20 MB in all. Returns the count.</summary>
        public static int CopyLogBundle(string farm, string to)
        {
            int n = 0;
            long total = 0;
            var files = new List<string>();
            foreach (var d in new[] { farm }.Concat(SafeDirs(farm)))
                foreach (var f in SafeFiles(d))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".log" || ext == ".txt" || ext == ".json") files.Add(f);
                }
            foreach (var f in files.OrderByDescending(f => SafeTime(f)))
            {
                if (total >= LogTotalCap) break;
                string rel = f.Substring(farm.Length).TrimStart('\\');
                string dst = Path.Combine(to, rel.Replace('\\', '_'));
                try
                {
                    Directory.CreateDirectory(to);
                    using (var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long take = Math.Min(Math.Min(i.Length, LogFileCap), LogTotalCap - total);
                        i.Seek(i.Length - take, SeekOrigin.Begin);
                        using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buf = new byte[1 << 16];
                            long left = take;
                            int r;
                            while (left > 0 && (r = i.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0) { o.Write(buf, 0, r); left -= r; }
                        }
                        total += take;
                    }
                    n++;
                }
                catch (Exception) { }
            }
            return n;
        }

        private static int CopyFlat(string from, string to)
        {
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (var f in SafeFiles(from))
            {
                try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); n++; } catch (Exception) { }
            }
            return n;
        }

        private static IEnumerable<string> SafeDirs(string d)
        {
            try { return new DirectoryInfo(d).GetDirectories().Where(x => (x.Attributes & FileAttributes.ReparsePoint) == 0).Select(x => x.FullName).ToList(); }
            catch (Exception) { return new string[0]; }
        }

        private static IEnumerable<string> SafeFiles(string d)
        {
            try { return new DirectoryInfo(d).GetFiles().Where(x => (x.Attributes & FileAttributes.ReparsePoint) == 0).Select(x => x.FullName).ToList(); }
            catch (Exception) { return new string[0]; }
        }

        private static DateTime SafeTime(string f) { try { return File.GetLastWriteTimeUtc(f); } catch (Exception) { return DateTime.MinValue; } }

        /// <summary>Keeps the newest <paramref name="keep"/> cached captures (by their capture record's time); the older ones are deleted.</summary>
        public static JanitorReport TrimCaptures(string work, int keep)
        {
            var rep = new JanitorReport();
            var caps = List(work).Where(i => i.CachedCapture && !i.Live).OrderByDescending(i => i.LastUse).ToList();
            foreach (var it in caps.Skip(Math.Max(0, keep))) RemoveItem(work, it, rep, "older than the newest " + keep + " capture(s) kept for reuse");
            return rep;
        }

        /// <summary>Temp above <paramref name="capBytes"/>: delete the oldest non-live folders first until it fits.</summary>
        public static JanitorReport EnforceCap(string work, long capBytes, ICollection<string> spare = null)
        {
            var rep = new JanitorReport();
            var items = List(work);
            Measure(items);
            long size = items.Sum(i => Math.Max(0, i.Bytes));
            if (size <= capBytes) return rep;
            rep.Add("temp: " + JanitorReport.Gb(size) + " is over the " + JanitorReport.Gb(capBytes) + " cap: the oldest folders go first");
            foreach (var it in items.Where(i => !i.Live && (spare == null || !spare.Contains(i.Path, StringComparer.OrdinalIgnoreCase))).OrderBy(i => i.LastUse))
            {
                if (size <= capBytes) break;
                if (RemoveItem(work, it, rep, "Temp size cap")) size -= Math.Max(0, it.Bytes);
            }
            return rep;
        }

        // ================================================================== start-up sweep (5.4)

        /// <summary>
        /// At app start (and before a CLI bake / doctor): never while a live holder has the GPU lock. Orphans (a .lock that opens = its bake
        /// died) go at once; lock-less .map folders older than 24 h, query folders older than 1 h and lock-less farms without a capture record
        /// older than 24 h go too; KEEP skeleton overlays of this app's workers are unlinked (never followed) and removed; then the capture
        /// cache and the cap are applied.
        /// </summary>
        public static JanitorReport Sweep(string work, TempSettings settings, IProcessProbe probe = null, DateTime? now = null, bool farmsInTemp = true)
        {
            var rep = new JanitorReport();
            probe = probe ?? SystemProcessProbe.Instance;
            var t = now ?? DateTime.Now;
            if (!Directory.Exists(work)) return rep;
            var li = GpuLock.Inspect(GpuLock.PathFor(work));
            if (li.Exists && !GpuLock.IsStale(li, probe, DateTime.UtcNow)) { rep.Skipped = true; rep.SkipReason = "a bake holds the GPU lock (" + li.Content + ")"; return rep; }
            CurrentRun cur;
            var ak = CurrentRun.Inspect(work, out cur, probe);
            if (ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell) { rep.Skipped = true; rep.SkipReason = "a bake is running (" + (cur == null ? "?" : cur.RunTag) + ")"; return rep; }
            foreach (var it in List(work))
            {
                if (it.Live) continue;
                double age = (t - it.LastUse).TotalHours;
                if (it.OrphanLock) RemoveItem(work, it, rep, "left by a bake that did not finish (crash, kill or power-off)");
                else if (it.Kind == TempItemKind.MapRun && age >= OrphanMapHours) RemoveItem(work, it, rep, "a .map bake folder older than 24 h");
                else if (it.Kind == TempItemKind.Query && age >= OrphanQueryHours) RemoveItem(work, it, rep, "a map query folder older than 1 h");
                else if (it.Kind == TempItemKind.Farm && !it.CachedCapture && age >= LeftoverFarmHours) RemoveItem(work, it, rep, "a worker folder with no capture record, older than 24 h");
            }
            foreach (var l in RemoveSkeletons(work).Lines) rep.Add(l);
            if (farmsInTemp)
            {
                var a = TrimCaptures(work, settings.KeepCaptures);
                var b = EnforceCap(work, settings.CapBytes);
                foreach (var r in new[] { a, b }) { rep.Lines.AddRange(r.Lines); rep.Problems.AddRange(r.Problems); rep.FreedBytes += r.FreedBytes; rep.Removed += r.Removed; }
            }
            var c = PruneRuns(work);
            rep.Lines.AddRange(c.Lines); rep.Problems.AddRange(c.Problems);
            var d = PruneAutoBackups(work);
            rep.Lines.AddRange(d.Lines); rep.Problems.AddRange(d.Problems);
            return rep;
        }

        /// <summary>The KEEP skeleton overlays WORK\_gpulm_skel\wNN of this app's workers (workers.json names NN): their junctions point into
        /// the LIVE tags, so they are unlinked first (RemoveDirectory on each link, never followed), then the plain folders are removed. A
        /// skeleton of a worker this app does not own (research) is never touched. The caller proved no bake is running.
        /// 0.6.8 RC (F2): <paramref name="thisBake"/> = the workers THIS bake just used (its end-of-bake cleanup): they are removed even when
        /// workers.json does not name them (the dev flag --worker N), because this bake's own gpu_bake just (re)built that overlay.</summary>
        public static JanitorReport RemoveSkeletons(string work, IEnumerable<int> only = null, bool thisBake = false)
        {
            var rep = new JanitorReport();
            string sk = Path.Combine(work, "_gpulm_skel");
            if (!Directory.Exists(sk)) return rep;
            HashSet<int> owned;
            if (thisBake && only != null) owned = new HashSet<int>(only);
            else
            {
                try { owned = new HashSet<int>(new WorkerRegistry(work).ReadAll().Values); }
                catch (Exception e) { rep.Problems.Add("workers.json: " + e.Message); return rep; }
                if (only != null) owned.IntersectWith(only);
            }
            foreach (int w in owned)
            {
                string d = Path.Combine(sk, "w" + w.ToString("D2", CultureInfo.InvariantCulture));
                if (!Directory.Exists(d)) continue;
                try
                {
                    var log = new List<string>();
                    int n = SafeFs.UnlinkAll(d, log);
                    int f = SafeFs.RemoveTree(d, sk, false);
                    rep.Removed++;
                    rep.Add("removed the skeleton overlay " + d + " (" + n + " link(s) into the tags unlinked, never followed; " + f + " copied file(s) deleted)");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    rep.Problems.Add(d + ": " + e.Message);
                    rep.Add("could not remove the skeleton overlay " + d + ": " + e.Message);
                }
            }
            return rep;
        }

        // ================================================================== Clean now (5.5)

        /// <summary>Deletes everything in Temp except folders a live bake holds and the READ ME. The caller holds the TagWriteGate.</summary>
        public static JanitorReport CleanNow(string work, bool dryRun = false)
        {
            var rep = new JanitorReport();
            var items = List(work);
            Measure(items);
            foreach (var it in items)
            {
                if (it.Live) { rep.Add("kept " + it.Name + ": in use by a running bake"); continue; }
                if (dryRun) { rep.Add("would remove " + it.Name + " (" + JanitorReport.Gb(it.Bytes) + ")"); rep.FreedBytes += it.Bytes; rep.Removed++; continue; }
                RemoveItem(work, it, rep, "Clean now");
            }
            string t = ShellPaths.TempOf(work);
            if (Directory.Exists(t))
                foreach (var f in Directory.GetFiles(t).Where(f => !string.Equals(Path.GetFileName(f), ShellPaths.TempReadMeName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (dryRun) { rep.Add("would remove the file " + Path.GetFileName(f)); continue; }
                    if (!SafeFs.IsReparsePoint(f) && SafeFs.TryDeleteFile(f)) rep.Add("removed the file " + Path.GetFileName(f));
                }
            return rep;
        }

        // ================================================================== before a bake: free space (5.4 step 6)

        /// <summary>The space a bake of this worker needs: its last measured farm size, else 3 GB.</summary>
        public static long NeedFor(string work, int worker)
        {
            try
            {
                var j = Json.ReadObjFile(Path.Combine(ShellPaths.StandaloneOf(work), FarmSizesFile));
                var v = j == null ? null : j.Long("farm_w" + worker.ToString("D2", CultureInfo.InvariantCulture));
                if (v.HasValue && v.Value > 0) return Math.Max(DefaultFarmNeedBytes, v.Value);
            }
            catch (Exception) { }
            return DefaultFarmNeedBytes;
        }

        private static void RecordFarmSize(string work, string farmName, long bytes)
        {
            try
            {
                string p = Path.Combine(ShellPaths.StandaloneOf(work), FarmSizesFile);
                var j = Json.ReadObjFile(p) ?? new JObj();
                j[farmName] = bytes;
                SafeFs.WriteAllTextAtomic(p, Json.Write(j, true));
            }
            catch (Exception) { }
        }

        /// <summary>When the drive would keep less than 8 GB free after the bake, evict cached captures / leftovers (oldest first, never the
        /// folders in <paramref name="spare"/> or a live one); returns null when there is room, else the refusal text.</summary>
        public static string EnsureSpace(string work, long needBytes, ICollection<string> spare, JanitorReport rep, Func<string, long> freeBytes = null)
        {
            freeBytes = freeBytes ?? (p => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(p))).AvailableFreeSpace);
            string t = EnsureTemp(work);
            long free = freeBytes(t);
            if (free - needBytes >= MinFreeBytes) return null;
            var items = List(work).Where(i => !i.Live && (spare == null || !spare.Contains(i.Path, StringComparer.OrdinalIgnoreCase))).OrderBy(i => i.LastUse).ToList();
            foreach (var it in items)
            {
                RemoveItem(work, it, rep, "to make room for this bake");
                free = freeBytes(t);
                if (free - needBytes >= MinFreeBytes) return null;
            }
            return "not enough free disk space for this bake on the drive of " + work + ": " + JanitorReport.Gb(free) + " free, the bake needs about "
                   + JanitorReport.Gb(needBytes) + " and the drive must keep " + JanitorReport.Gb(MinFreeBytes) + " free -- free some space and bake again (nothing was written)";
        }

        // ================================================================== KEEP-side bounds: run bundles (5.3 / 5.6)

        /// <summary>
        /// The run folders (WORK\standalone\runs\&lt;ts&gt;_&lt;label&gt;): the install backup (tagbak) and the heavy staging folders are kept for the
        /// newest 10 runs of each map label; failed-run bundles (failed\) for the newest 3 runs that have one. bake.log / status.json always
        /// stay. A run that is not final, or whose backup an unfinished install marker names, is never touched.
        /// </summary>
        public static JanitorReport PruneRuns(string work)
        {
            var rep = new JanitorReport();
            string runs = RunFolder.RunsDir(work);
            if (!Directory.Exists(runs)) return rep;
            var spare = MarkerBackups(work);
            var dirs = Directory.GetDirectories(runs).Where(d => !SafeFs.IsReparsePoint(d)).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
            Func<string, bool> final = d =>
            {
                try { var st = RunStatus.Load(d); return st == null || st.IsFinal; } catch (Exception) { return false; }
            };
            var heavy = new[] { RunFolder.BackupDirName, RunFolder.LegacyBackupDirName, "final_stage", "probes", "probe_stage", "probe_stage_check", "live_ltmp", FarmLogsDir };
            foreach (var g in dirs.GroupBy(d => LabelOf(Path.GetFileName(d)), StringComparer.OrdinalIgnoreCase))
                foreach (var d in g.Skip(RunTagbakKeep))
                {
                    if (!final(d)) continue;
                    foreach (var h in heavy)
                    {
                        string p = Path.Combine(d, h);
                        if (!Directory.Exists(p) || spare.Contains(SafeFs.Norm(p))) continue;
                        RemoveKeepSub(p, d, rep, "older than the newest " + RunTagbakKeep + " runs of " + g.Key);
                    }
                }
            foreach (var d in dirs.Where(d => Directory.Exists(Path.Combine(d, FailedDir))).Skip(FailedBundlesKeep))
                if (final(d)) RemoveKeepSub(Path.Combine(d, FailedDir), d, rep, "failed-run bundles are kept for the newest " + FailedBundlesKeep + " runs");
            return rep;
        }

        private static string LabelOf(string runName) { return runName.Length > 16 && runName[15] == '_' ? runName.Substring(16) : runName; }

        private static void RemoveKeepSub(string p, string parent, JanitorReport rep, string why)
        {
            try
            {
                long b = SafeFs.DirSize(p);
                SafeFs.RemoveTree(p, parent, false);
                rep.FreedBytes += b; rep.Removed++;
                rep.Add("removed " + p + " (" + JanitorReport.Gb(b) + "): " + why);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                rep.Problems.Add(p + ": " + e.Message);
            }
        }

        /// <summary>The backup folders an unfinished install / restore marker names (normalized): they are never pruned.</summary>
        private static HashSet<string> MarkerBackups(string work)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var it in StartupRecovery.Scan(work))
                    if (it.BackupDir != null) { try { s.Add(SafeFs.Norm(it.BackupDir)); } catch (Exception) { } }
            }
            catch (Exception) { }
            return s;
        }

        // ================================================================== tag backups (5.6)

        /// <summary>
        /// WORK\_TAGBAK_ui: per lightmap set (the prefix its backup.json / source.txt names) keep the OLDEST backup (the original from before the
        /// first GPU bake) and the newest 5 (before_restore copies included); delete the rest. Unfinished (.tmp_) folders, folders an unfinished
        /// marker names and folders whose set cannot be read are never touched. <paramref name="prefix"/> limits it to one set.
        /// </summary>
        public static JanitorReport PruneUiBackups(string work, string prefix = null)
        {
            var rep = new JanitorReport();
            string ui = TagBackup.UiRoot(work);
            if (!Directory.Exists(ui)) return rep;
            var spare = MarkerBackups(work);
            var all = new List<KeyValuePair<string, KeyValuePair<string, DateTime>>>();
            foreach (var d in Directory.GetDirectories(ui))
            {
                string name = Path.GetFileName(d);
                if (name.IndexOf(TagBackup.TmpMarker, StringComparison.OrdinalIgnoreCase) >= 0 || SafeFs.IsReparsePoint(d)) continue;
                string p = BackupPrefix(d);
                if (p == null) continue;
                if (prefix != null && !string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)) continue;
                all.Add(new KeyValuePair<string, KeyValuePair<string, DateTime>>(d, new KeyValuePair<string, DateTime>(p, StampOf(d))));
            }
            foreach (var g in all.GroupBy(x => x.Value.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = g.OrderBy(x => x.Value.Value).ThenBy(x => Path.GetFileName(x.Key), StringComparer.Ordinal).Select(x => x.Key).ToList();
                if (ordered.Count <= 1 + UiKeepNewest) continue;
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ordered[0] };
                foreach (var k in ordered.Skip(ordered.Count - UiKeepNewest)) keep.Add(k);
                foreach (var d in ordered.Where(x => !keep.Contains(x)))
                {
                    if (spare.Contains(SafeFs.Norm(d))) { rep.Add("kept " + d + ": an unfinished install / restore names it"); continue; }
                    RemoveKeepSub(d, ui, rep, "backups of " + g.Key + ": the original + the newest " + UiKeepNewest + " are kept");
                }
            }
            return rep;
        }

        private static string BackupPrefix(string d)
        {
            try
            {
                string bj = Path.Combine(d, "backup.json");
                if (File.Exists(bj)) { var j = Json.ReadObjFile(bj); if (j != null && j.Str("prefix") != null) return j.Str("prefix"); }
                string st = Path.Combine(d, "source.txt");
                if (File.Exists(st))
                {
                    var lines = File.ReadAllLines(st).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    if (lines.Count >= 2) return lines[1];
                }
            }
            catch (Exception) { }
            return null;
        }

        private static DateTime StampOf(string d)
        {
            var m = StampRx.Match(Path.GetFileName(d));
            DateTime t;
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            try { return Directory.GetCreationTime(d); } catch (Exception) { return DateTime.MinValue; }
        }

        /// <summary>WORK\_TAGBAK_auto folders that exist now (names), for <see cref="RecordAutoBackupsSince"/>.</summary>
        public static HashSet<string> AutoBackupNames(string work)
        {
            string a = Path.Combine(work, "_TAGBAK_auto");
            if (!Directory.Exists(a)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(Directory.GetDirectories(a).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>_TAGBAK_auto is retired in the shell's own install (0.6.8, user decision); only gpu_bake's tool write-back fallback still
        /// writes it. The folders that fallback made during THIS run (new names since <paramref name="before"/>) are recorded as this app's in
        /// KEEP standalone\tagbak_auto_owned.json -- only those are ever pruned; older _TAGBAK_auto folders are listed by the opt-in cleanup.</summary>
        public static void RecordAutoBackupsSince(string work, HashSet<string> before)
        {
            var now = AutoBackupNames(work);
            var added = now.Where(n => !before.Contains(n)).ToList();
            if (added.Count == 0) return;
            var owned = AutoOwned(work);
            foreach (var n in added) owned.Add(n);
            SaveAutoOwned(work, owned);
        }

        public static HashSet<string> AutoOwned(string work)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var j = Json.ReadObjFile(Path.Combine(ShellPaths.StandaloneOf(work), AutoOwnedFile));
                var arr = j == null ? null : j.Arr("owned");
                if (arr != null) foreach (var o in arr.OfType<string>()) s.Add(o);
            }
            catch (Exception) { }
            return s;
        }

        private static void SaveAutoOwned(string work, HashSet<string> owned)
        {
            SafeFs.WriteAllTextAtomic(Path.Combine(ShellPaths.StandaloneOf(work), AutoOwnedFile),
                Json.Write(new JObj().Add("schema", "h2lm-tagbak-auto-owned/1").Add("owned", owned.OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList()), true));
        }

        /// <summary>Of the _TAGBAK_auto folders this app's fallback made (the owned list), keep the newest 3 per lightmap prefix.</summary>
        public static JanitorReport PruneAutoBackups(string work)
        {
            var rep = new JanitorReport();
            string a = Path.Combine(work, "_TAGBAK_auto");
            var owned = AutoOwned(work);
            if (owned.Count == 0 || !Directory.Exists(a)) return rep;
            var spare = MarkerBackups(work);
            var present = owned.Where(n => Directory.Exists(Path.Combine(a, n))).ToList();
            var rx = new Regex(@"^(\d{8}_\d{6})_(.+)$");
            foreach (var g in present.Where(n => rx.IsMatch(n)).GroupBy(n => rx.Match(n).Groups[2].Value, StringComparer.OrdinalIgnoreCase))
                foreach (var n in g.OrderByDescending(n => rx.Match(n).Groups[1].Value, StringComparer.Ordinal).Skip(AutoKeepNewest))
                {
                    string d = Path.Combine(a, n);
                    if (spare.Contains(SafeFs.Norm(d))) continue;
                    RemoveKeepSub(d, a, rep, "the tool write-back's auto backups of " + g.Key + ": the newest " + AutoKeepNewest + " are kept");
                }
            var left = AutoOwned(work);
            left.RemoveWhere(n => !Directory.Exists(Path.Combine(a, n)));
            try { SaveAutoOwned(work, left); } catch (Exception) { }
            return rep;
        }

        // ================================================================== the opt-in cleanup of 0.6.7 folders (5.4 "legacy")

        /// <summary>
        /// Exactly what the opt-in "Free space from earlier versions" would delete -- nothing is deleted here:
        ///   * WORK\farm_wNN that workers.json names AND that carry this app's capture record (a research farm is never listed);
        ///   * every folder in WORK\_mapbake (0.6.7 .map bakes; a crashed one holds a whole copy of the kit tags);
        ///   * WORK\_gpulm_skel\wNN of those same workers (their links into the tags are unlinked first, never followed);
        ///   * WORK\_TAGBAK_auto folders this app did not record as its own (the 0.6.7 install's second backup; _TAGBAK_ui keeps the backups);
        ///   * old runtime versions (WORK\_runtime\&lt;old&gt;, %LOCALAPPDATA%\H2GpuLightmapper\rt\&lt;old&gt;) when an installer is given.
        /// A farm whose install marker is still there (an unfinished install) is not listed.
        /// </summary>
        public static List<LegacyItem> ListLegacy(string work, PayloadInstaller installer = null, string root = null, bool measure = true)
        {
            var r = new List<LegacyItem>();
            if (!Directory.Exists(work)) return r;
            Dictionary<string, int> reg;
            try { reg = new WorkerRegistry(work).ReadAll(); } catch (Exception) { reg = new Dictionary<string, int>(); }
            var regW = new HashSet<int>(reg.Values);
            foreach (int w in regW.OrderBy(x => x))
            {
                string f = Path.Combine(work, "farm_w" + w.ToString("D2", CultureInfo.InvariantCulture));
                if (!Directory.Exists(f) || SafeFs.IsReparsePoint(f)) continue;
                if (!File.Exists(Path.Combine(f, CaptureRecordName))) continue;
                if (File.Exists(Path.Combine(f, StartupRecovery.MarkerName))) continue;
                r.Add(new LegacyItem { Path = f, Parent = work, Kind = "0.6.7 worker", Why = "registry worker " + w + " with this app's capture record" });
            }
            string mb = Path.Combine(work, "_mapbake");
            if (Directory.Exists(mb) && !SafeFs.IsReparsePoint(mb))
                foreach (var d in Directory.GetDirectories(mb))
                    if (!IsLive(d)) r.Add(new LegacyItem { Path = d, Parent = mb, Kind = "0.6.7 .map bake folder", UnlinkFirst = true });
            string sk = Path.Combine(work, "_gpulm_skel");
            if (Directory.Exists(sk))
                foreach (int w in regW.OrderBy(x => x))
                {
                    string d = Path.Combine(sk, "w" + w.ToString("D2", CultureInfo.InvariantCulture));
                    if (Directory.Exists(d)) r.Add(new LegacyItem { Path = d, Parent = sk, Kind = "skeleton overlay", UnlinkFirst = true, Why = "links into the tags are unlinked, never followed" });
                }
            string au = Path.Combine(work, "_TAGBAK_auto");
            if (Directory.Exists(au))
            {
                var owned = AutoOwned(work);
                foreach (var d in Directory.GetDirectories(au))
                    if (!owned.Contains(Path.GetFileName(d)) && !SafeFs.IsReparsePoint(d))
                        r.Add(new LegacyItem { Path = d, Parent = au, Kind = "_TAGBAK_auto backup", Why = "the retired second install backup (the _TAGBAK_ui backups stay)" });
            }
            if (installer != null && root != null)
            {
                try
                {
                    foreach (var v in installer.ListOldVersions(root))
                        r.Add(new LegacyItem { Path = v.Path, Parent = v.Parent, Kind = "old runtime (" + v.Kind + ")", OldVersion = v });
                }
                catch (Exception) { }
            }
            if (measure) foreach (var it in r) it.Bytes = SafeFs.DirSize(it.Path);
            return r;
        }

        /// <summary>Deletes the items <see cref="ListLegacy"/> returned (each re-checked against a fresh listing first). The caller holds the
        /// TagWriteGate (no bake runs).</summary>
        public static JanitorReport RemoveLegacy(string work, IEnumerable<LegacyItem> items, PayloadInstaller installer = null, string root = null)
        {
            var rep = new JanitorReport();
            var fresh = new HashSet<string>(ListLegacy(work, installer, root, false).Select(x => SafeFs.Norm(x.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (!fresh.Contains(SafeFs.Norm(it.Path))) { rep.Add("skipped " + it.Path + ": no longer an earlier-version folder this app owns"); continue; }
                try
                {
                    long b = it.Bytes > 0 ? it.Bytes : SafeFs.DirSize(it.Path);
                    if (it.OldVersion != null && installer != null) installer.RemoveOldVersion(it.OldVersion);
                    else
                    {
                        int n = it.UnlinkFirst ? SafeFs.UnlinkAll(it.Path) : 0;
                        SafeFs.RemoveTree(it.Path, it.Parent, false);
                        if (n > 0) rep.Add("unlinked " + n + " link(s) in " + it.Path + " (never followed)");
                    }
                    rep.FreedBytes += b; rep.Removed++;
                    rep.Add("removed " + it.Path + " (" + JanitorReport.Gb(b) + ")");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    rep.Problems.Add(it.Path + ": " + e.Message);
                    rep.Add("could not remove " + it.Path + ": " + e.Message);
                }
            }
            string mb = Path.Combine(work, "_mapbake");
            try { if (Directory.Exists(mb) && !Directory.EnumerateFileSystemEntries(mb).Any()) Directory.Delete(mb, false); } catch (Exception) { }
            return rep;
        }
    }

    /// <summary>
    /// The engine-owned list of a farm's phase-3/4 products (INST\standalone\farm_products.json, made from pipeline_guard.py's BAKE_PRODUCTS
    /// and the files gpu_bake.ps1 deletes / regenerates before its phase 3): exactly what a -SkipCapture re-bake rebuilds. Slim deletes those
    /// and keeps the capture (phase-1 outputs, the derive tables, skeleton\, the capture record).
    /// </summary>
    public sealed class FarmProducts
    {
        public List<string> Files = new List<string>();
        public List<string> Dirs = new List<string>();
        public List<string> Patterns = new List<string>();

        public static FarmProducts Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var j = Json.ReadObjFile(path);
                if (j == null || j.Str("format") != "h2lm-farm-products/1") return null;
                var p = new FarmProducts();
                foreach (var o in (j.Arr("files") ?? new List<object>()).OfType<string>()) p.Files.Add(o);
                foreach (var o in (j.Arr("dirs") ?? new List<object>()).OfType<string>()) p.Dirs.Add(o);
                foreach (var o in (j.Arr("patterns") ?? new List<object>()).OfType<string>()) p.Patterns.Add(o);
                foreach (var n in p.Files.Concat(p.Dirs).Concat(p.Patterns))
                    if (n.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || n.Contains("..") || n.Equals(TempJanitor.CaptureRecordName, StringComparison.OrdinalIgnoreCase)
                        || n.Equals("skeleton", StringComparison.OrdinalIgnoreCase)) return null;      // a list that names a path or the capture itself is not trusted
                return p;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Deletes the listed products from the farm's top folder (links never followed). Returns the bytes freed.</summary>
        public long Slim(string farm, Action<string> say)
        {
            long freed = 0;
            var names = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in new DirectoryInfo(farm).GetFiles())
            {
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool hit = names.Contains(f.Name) || Patterns.Any(p => Matches(f.Name, p));
                if (!hit) continue;
                try { long b = f.Length; if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly; f.Delete(); freed += b; }
                catch (Exception e) { if (say != null) say("temp: could not delete " + f.FullName + ": " + e.Message); }
            }
            foreach (var dn in Dirs)
            {
                string d = Path.Combine(farm, dn);
                if (!Directory.Exists(d)) continue;
                try { long b = SafeFs.DirSize(d); SafeFs.RemoveTree(d, farm, false); freed += b; }
                catch (Exception e) { if (say != null) say("temp: could not delete " + d + ": " + e.Message); }
            }
            return freed;
        }

        /// <summary>A '*' wildcard match (case-insensitive), e.g. "*.stale_*".</summary>
        public static bool Matches(string name, string pattern)
        {
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
        }
    }
}
