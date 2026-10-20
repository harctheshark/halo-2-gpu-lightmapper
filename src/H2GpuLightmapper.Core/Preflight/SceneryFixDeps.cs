// "Scenery Fix Dependencies": the database the Osoyoos scenery fix (H2ToolHooks.dll) reads to decompress a scenery model's
// occluder vertices with the right position bound. A render_model that is not in it falls back to part[0]'s box, which is the
// WRONG scale for a model-wide-compressed model, so its shadow geometry is distorted in the CPU and the GPU bake alike.
//   SceneryFixDependencies.db   in the H2EK root (the name the user can find): one line per (render_model tag name, section key), so
//                               two models that share a lookup key but differ in size no longer clash. The legacy
//                               h2v_scenery_bounds.db is kept in step by the scanner (the Osoyoos launcher's older DLL reads that).
//   scan_scenery_bounds.py      the scanner shipped in the payload (INST\standalone\); this class only RUNS it as a child
//                               process (hidden, BELOW_NORMAL, in a job) and reads its --json report. It never edits a DB itself:
//                               the scanner makes the timestamped backup, appends, and creates SceneryFixDependencies.db on its
//                               first run (a full scan of the tags folder; the scenario then only decides what is reported).
//   Two uses: the "Scan and Build Scenery Fix Dependencies" action (full scan of ROOT\tags) and the automatic check before every
//   bake that uses the fix (only the models the bake's scenario places are reported; models the database did not know are added,
//   a log line each, and the bake goes on). Neither ever blocks a bake: a scanner failure is a warning.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Preflight
{
    /// <summary>A model with sections that cannot be told apart even by name (two sections of ONE model share a key with different bounds).</summary>
    public sealed class SceneryConflict
    {
        public string Model;
        public int Sections, Conflicts;
    }

    public sealed class SceneryScanResult
    {
        /// <summary>The scanner ran and its report was read (exit 0, 1 or 3). False: exit 2 / crash / timeout / no report.</summary>
        public bool Ran;
        public int ExitCode = -1;
        public bool TimedOut;
        /// <summary>Why it did not run / the report is unusable (null when Ran).</summary>
        public string Error;
        public string Db, LegacyDb;
        public string Scenario;
        public int Models, LinesToAdd, Unparsed, NoBound, KeyCollisions, OtherAdded;
        public bool Applied;
        /// <summary>The database had no per-model lines yet: the scanner built it from every render_model under tags\.</summary>
        public bool Seeding;
        /// <summary>tags-relative render_model paths the database did not know at all (in the scan's scope), now added.</summary>
        public List<string> Added = new List<string>();
        /// <summary>Models whose tag bound changed since their line was written; a new line was added.</summary>
        public List<string> Updated = new List<string>();
        public List<SceneryConflict> Conflicts = new List<SceneryConflict>();
        public double Seconds;
        public string JsonPath;
        public List<string> Output = new List<string>();
    }

    public static class SceneryFixDeps
    {
        public const string DbName = "SceneryFixDependencies.db";
        public const string LegacyDbName = "h2v_scenery_bounds.db";
        /// <summary>A full scan walks every render_model under ROOT\tags (1.7 s on the stock H2EK); generous for a slow disk.</summary>
        public const int FullScanTimeoutMs = 15 * 60 * 1000;
        /// <summary>A scenario scan follows scenario -> .scenery -> .model -> .render_model by path strings; the very first one also
        /// builds the whole database.</summary>
        public const int ScenarioScanTimeoutMs = 5 * 60 * 1000;

        public static string DbPath(string root) { return Path.Combine(root, DbName); }
        public static string LegacyPath(string root) { return Path.Combine(root, LegacyDbName); }

        /// <summary>The log line for a model the automatic check added (the wording the user asked for).</summary>
        public static string AddedLine(string model)
        {
            return model + " wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!";
        }

        public static string UpdatedLine(string model)
        {
            return model + " had a different size bound in the Scenery Fix Dependencies database than its tag has now -- updated automatically, continuing on!";
        }

        public static string ConflictLine(SceneryConflict c)
        {
            return "WARNING: " + c.Model + ": " + c.Conflicts + " of " + c.Sections + " compressed section(s) share a lookup key (vertex count, part count, strip total) with a different size bound"
                   + " inside this ONE model, so the database cannot tell them apart by name; those sections may be scaled wrong. Added anyway. Continuing on.";
        }

        /// <summary>The scanner's argv (after the interpreter): -B scan.py ROOT [--scenario S] --apply --json OUT. No --db: the scanner's
        /// own default (ROOT\SceneryFixDependencies.db, the legacy file kept in step) is the contract.</summary>
        public static List<string> Args(string scannerPy, string root, string scenarioOrNull, string jsonOut)
        {
            var a = new List<string> { "-B", scannerPy, root };
            if (!string.IsNullOrEmpty(scenarioOrNull)) { a.Add("--scenario"); a.Add(scenarioOrNull); }
            a.Add("--apply");
            a.Add("--json"); a.Add(jsonOut);
            return a;
        }

        /// <summary>
        /// Runs the scanner with --apply (hidden, BELOW_NORMAL). Never throws for a scanner problem: the result says why it did not
        /// run. <paramref name="onLine"/> receives the scanner's own output lines (progress text for the window).
        /// </summary>
        public static SceneryScanResult Run(string pyExe, string scannerPy, string root, string scenarioOrNull, string cwd,
                                           ChildEnvironment env, int timeoutMs, string jsonOut, Action<string> onLine = null)
        {
            var res = new SceneryScanResult { Scenario = scenarioOrNull, JsonPath = jsonOut, Db = DbPath(root), LegacyDb = LegacyPath(root) };
            if (scannerPy == null || !File.Exists(scannerPy)) { res.Error = "this build's payload has no scan_scenery_bounds.py"; return res; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(jsonOut));
                if (File.Exists(jsonOut)) File.Delete(jsonOut);      // a stale report must never be read as this scan's
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { res.Error = "cannot prepare the scan report folder: " + e.Message; return res; }
            ProcessRunner.Result r;
            try
            {
                r = ProcessRunner.RunCapture(pyExe, Args(scannerPy, root, scenarioOrNull, jsonOut), cwd, env, timeoutMs,
                                             (l, err) => { if (onLine != null) { try { onLine(l); } catch (Exception) { } } });
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                res.Error = "the scanner could not be started: " + e.Message;
                return res;
            }
            res.ExitCode = r.ExitCode; res.TimedOut = r.TimedOut; res.Seconds = r.Seconds;
            lock (r.StdoutLines) res.Output.AddRange(r.StdoutLines);
            lock (r.StderrLines) res.Output.AddRange(r.StderrLines.Select(l => "ERR| " + l));
            if (r.TimedOut) { res.Error = "the scanner timed out after " + (timeoutMs / 1000) + " s"; return res; }
            if (r.ExitCode == 2) { res.Error = "the scanner refused (exit 2): " + Tail(res.Output); return res; }
            if (r.ExitCode != 0 && r.ExitCode != 1 && r.ExitCode != 3) { res.Error = "the scanner failed (exit " + ProcessRunner.FormatExit(r.ExitCode) + "): " + Tail(res.Output); return res; }
            string json;
            try { json = File.Exists(jsonOut) ? File.ReadAllText(jsonOut) : null; }
            catch (IOException e) { res.Error = "cannot read the scan report: " + e.Message; return res; }
            if (json == null) { res.Error = "the scanner wrote no report (exit " + r.ExitCode + "): " + Tail(res.Output); return res; }
            string perr = ParseReport(json, res);
            if (perr != null) { res.Error = perr; return res; }
            res.Ran = true;
            return res;
        }

        private static string Tail(List<string> lines)
        {
            var t = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Reverse().Take(3).Reverse().ToList();
            return t.Count == 0 ? "(no output)" : string.Join(" | ", t);
        }

        private static void AddModels(List<object> src, List<string> into)
        {
            if (src == null) return;
            foreach (var o in src)
            {
                string m = o as string ?? (o is JObj ? ((JObj)o).Str("model") : null);
                if (!string.IsNullOrEmpty(m) && !into.Contains(m, StringComparer.OrdinalIgnoreCase)) into.Add(m);
            }
        }

        /// <summary>
        /// Reads the scanner's --json report into <paramref name="res"/>. Added models = added_models[] (strings, or objects with
        /// "model"); a report without that field (an older scanner) that says applied: the "missing" entries. Returns null, or why
        /// the report is unusable.
        /// </summary>
        public static string ParseReport(string json, SceneryScanResult res)
        {
            JObj j;
            try { j = Json.ParseObj(json); }
            catch (FormatException e) { return "the scan report is not valid JSON: " + e.Message; }
            res.Models = j.Int("models") ?? 0;
            res.LinesToAdd = j.Int("lines_to_add") ?? 0;
            res.Applied = j.Bool("applied") == true;
            res.Seeding = j.Bool("seeding") == true;
            res.KeyCollisions = j.Int("key_collisions") ?? 0;
            string db = j.Str("db"); if (!string.IsNullOrEmpty(db)) res.Db = db;
            string ldb = j.Str("legacy_db"); if (!string.IsNullOrEmpty(ldb)) res.LegacyDb = ldb;
            var nobound = j.Arr("nobound"); res.NoBound = nobound == null ? 0 : nobound.Count;
            var nosec = j.Arr("nosec"); res.Unparsed = nosec == null ? 0 : nosec.Count;
            res.Added.Clear(); res.Updated.Clear(); res.Conflicts.Clear();
            if (j.Has("added_models")) AddModels(j.Arr("added_models"), res.Added);
            else if (res.Applied) AddModels(j.Arr("missing"), res.Added);
            AddModels(j.Arr("updated_models"), res.Updated);
            var other = j.Arr("also_added_models");
            res.OtherAdded = other == null ? 0 : other.Count;
            var conf = j.Arr("conflict");
            if (conf != null)
                foreach (var o in conf)
                {
                    var e = o as JObj;
                    if (e == null) continue;
                    res.Conflicts.Add(new SceneryConflict { Model = e.Str("model"), Sections = e.Int("sections") ?? 0, Conflicts = e.Int("conflict") ?? 0 });
                }
            return null;
        }

        /// <summary>The result as lines for the window's result dialog and the CLI.</summary>
        public static List<string> Summarize(SceneryScanResult r, int maxList = 60)
        {
            var l = new List<string>();
            if (!r.Ran) { l.Add("The scan did not complete: " + r.Error); l.Add("The existing database (if any) is used as it is."); return l; }
            l.Add("Scanned " + r.Models.ToString("N0", CultureInfo.InvariantCulture) + " render_model(s)" + (string.IsNullOrEmpty(r.Scenario) ? " (all of the tags folder)" : " (the whole tags folder, reported for " + r.Scenario + ")")
                  + " in " + r.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
            if (r.Seeding) l.Add("SceneryFixDependencies.db did not have per-model entries yet: it was built from every render_model in the tags folder.");
            l.Add(r.Added.Count == 0 ? "Added: none -- every scenery model with a compressed section was already in the database."
                                     : "Added " + r.Added.Count + " model(s) the database did not know:");
            foreach (var m in r.Added.Take(maxList)) l.Add("    " + m);
            if (r.Added.Count > maxList) l.Add("    ... and " + (r.Added.Count - maxList) + " more");
            if (r.OtherAdded > 0) l.Add("(" + r.OtherAdded.ToString("N0", CultureInfo.InvariantCulture) + " more model(s) elsewhere in the tags folder were not in the database either and were added too.)");
            if (r.Updated.Count > 0)
            {
                l.Add("Updated " + r.Updated.Count + " model(s) whose tag now has a different size bound than their database entry:");
                foreach (var m in r.Updated.Take(maxList)) l.Add("    " + m);
                if (r.Updated.Count > maxList) l.Add("    ... and " + (r.Updated.Count - maxList) + " more");
            }
            if (r.Conflicts.Count > 0)
            {
                l.Add("Conflicts: " + r.Conflicts.Count + " model(s) with sections the database cannot tell apart. Entries are per model name, so models that share a lookup key");
                l.Add("(vertex count, part count, strip total) no longer clash with each other; this is the rare case of two sections of ONE model sharing a key with different");
                l.Add("size bounds. They were added anyway, but those sections may be decompressed at the wrong scale (a distorted shadow):");
                foreach (var c in r.Conflicts.Take(maxList)) l.Add("    " + c.Model + "   (" + c.Conflicts + " of " + c.Sections + " compressed section(s))");
                if (r.Conflicts.Count > maxList) l.Add("    ... and " + (r.Conflicts.Count - maxList) + " more");
            }
            else l.Add("Conflicts: none." + (r.KeyCollisions > 0 ? " (" + r.KeyCollisions + " lookup key(s) are shared by models of different sizes -- each is told apart by its tag name.)" : ""));
            if (r.NoBound > 0) l.Add("Note: " + r.NoBound + " model(s) have compressed sections whose bound could not be read from the tag (the database cannot fix those).");
            l.Add("Database: " + r.Db);
            if (!string.IsNullOrEmpty(r.LegacyDb)) l.Add("Legacy database kept in step for the older Osoyoos DLL: " + r.LegacyDb);
            return l;
        }
    }
}
