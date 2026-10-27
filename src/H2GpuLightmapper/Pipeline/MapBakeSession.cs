// Milestone 2: bake a lightmap straight from a compiled .map (an MCC Halo 2 cache built with the Halo 2 Editing Kit, self-contained).
// Milestone 3: ambient occlusion and light probes (the ordinary pipeline's final stage is injected: lightmap + bitmap, or the lightmap alone for a Lightprobes-only
// bake), and Vista maps (cache v8: read through h2conv's reverse conversion to MCC form, baked like an MCC map, optionally converted back to Vista).
//
//   1. query     the separate program h2conv (via the bundled mapbake.py) reads the map: container, scenario, bsps, skies, cube maps. Stock (compressed / textures.dat)
//                maps are refused in plain words; a Vista map is read through its MCC form (reverse conversion into the run folder).
//   2. read      ONLY the lightmapper's dependency subset of the map into a temporary per-run folder under the work folder (removed after the run).
//   3. overlay   a REAL-COPY overlay root: the kit's tags copied (never hard links or junctions into the live tags) + the tags read from the map on top, tool_fast.exe and the
//                scenery databases; the live tags are snapshotted before, and after every tool / bake step (any change aborts the run).
//   4. bake      the normal GPU bake (stage only) through the ordinary BakeSession: only the root it reads (H2LM_ROOT / H2LM_WORK) is the overlay; the runtime, the worker's farm
//                folder, the GPU lock and the run folder are the real ones. The bake's first phase is tool_fast loading that tree: a failure there is reported as such.
//   5. inject    ONLY the lightmap and its bitmap go into a COPY of the map (h2conv.inject --scnr-clusters keep --shift-align 0, with the map's bsp), validated, published as
//                <name>_gpulm.map next to the input (or the chosen path; never over the input). A Vista map: the MCC result is converted back to Vista (--vista-out yes).
//   6. cleanup   the temporary folder (the map's files + overlay) is deleted, junctions unlinked first; the logs stay.
//
// A scenario bake never gets here: BakeOptions.IsMapBake is false and BakeSession runs exactly as before.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using H2Lm.Proc;
using Json = H2GpuLightmapper.Core.Util.Json;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class MapBakeSession
    {
        public const string TempFolder = "_mapbake";
        public const int QueryTimeoutMs = 15 * 60 * 1000, PrepareTimeoutMs = 90 * 60 * 1000, InjectTimeoutMs = 45 * 60 * 1000, SnapTimeoutMs = 20 * 60 * 1000, CleanupTimeoutMs = 60 * 60 * 1000;

        /// <summary>The steps of the .map path in order (plan text, log header, tests).</summary>
        public static readonly string[] StepTitles =
        {
            "read the map (h2conv, a separate program): container, scenario, bsps, skies, cube maps; stock / compressed maps are refused; a Vista map is first converted to its MCC form",
            "snapshot the live tags (the guard: any change during the run aborts it)",
            "read ONLY the lightmapper's dependency subset of the map into a temporary folder under the work folder (removed after the run)",
            "build the real-copy overlay root: kit tags copied (never linked) + the tags read from the map on top, tool_fast.exe, the scenery databases",
            "load check: the bake's first phase is tool_fast loading that tree (a failure there refuses the map in plain words)",
            "the normal GPU bake of the overlay (stage only; production engine; the worker's farm folder is the real one)",
            "inject ONLY the lightmap and its bitmap (the lightmap alone for Lightprobes only) into a COPY of the map (h2conv.inject --scnr-clusters keep --shift-align 0); a Vista map with the conversion back to Vista chosen is converted back (h2conv.inject --to-vista)",
            "validate the copy (h2conv validators; the Vista validators for a Vista result), then publish it as the output map (the input is never touched)",
            "check the live tags again, then delete the temp folder (junctions unlinked first; the logs stay)",
        };

        private readonly AppHost _host;
        private readonly BakeOptions _o;
        private readonly SessionOptions _so;
        private readonly object _lock = new object();
        private bool _cancel;
        private JobProcess _cur;
        private bool _ignoreCancel;

        // prepared context
        private string _root, _realWork, _scenarioRel, _bsp, _bspRel, _outPath, _mapPath, _runDir;
        private ResolvedRuntime _rt;
        private ChildEnvironment _env;
        private MapQueryResult _query;
        private List<SkyChoice> _skies = new List<SkyChoice>();
        private int _worker, _pworker;
        private bool _vista;
        private string _h2work;
        private StreamWriter _sessionLog;
        private readonly List<string> _pre = new List<string>();

        public MapBakeSession(AppHost host, BakeOptions o, SessionOptions so = null)
        {
            _host = host; _o = o.Clone(); _so = so ?? new SessionOptions();
        }

        public BakeOptions Options { get { return _o; } }
        public BakeSession Inner { get; private set; }
        public BakePlan Plan { get; private set; }
        public BakeRun Run { get { var i = Inner; return i == null ? null : i.Run; } }
        public int ExitCode { get; private set; } = -1;
        public string Reason { get; private set; }
        public string PrecisionSummary { get { var i = Inner; return i == null ? null : i.PrecisionSummary; } }
        public string OutputPath { get { return _outPath; } }
        /// <summary>The skies that need a cube-map choice, with the choice this run uses (after the map query).</summary>
        public IList<SkyChoice> Skies { get { return _skies; } }
        /// <summary>The dry-run plan (text / JSON), set by Execute in a dry run.</summary>
        public string DryText { get; private set; }
        public JObj DryJson { get; private set; }

        public event Action<string> Info;
        public event Action<ParsedLine> Line;
        public event Action<RunStatus> StatusChanged;
        public event Action<BakePlan> Planned;
        /// <summary>The coarse phase the .map path is in ("Reading the map", "Preparing ...", "Injecting ..."): for the window's status line.</summary>
        public event Action<string> Phase;
        public Func<Prompt, bool> Ask = p => false;

        private void Say(string m)
        {
            lock (_lock) _pre.Add(m);
            try { if (_sessionLog != null) lock (_sessionLog) { _sessionLog.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + m); _sessionLog.Flush(); } } catch (IOException) { }
            var h = Info; if (h != null) { try { h(m); } catch (Exception) { } }
        }

        private void Phased(string p) { Say("== " + p); var h = Phase; if (h != null) { try { h(p); } catch (Exception) { } } }

        public void RequestCancel()
        {
            JobProcess c;
            BakeSession inner;
            lock (_lock) { _cancel = true; c = _cur; inner = Inner; }
            try { if (c != null) c.Terminate(); } catch (Exception) { }
            if (inner != null) inner.RequestCancel();
        }

        private void CheckCancel() { lock (_lock) if (_cancel && !_ignoreCancel) throw new OperationCanceledException("cancelled"); }

        private void Refuse(string m) { throw new RefusedException(m); }

        // ================================================================== entry

        public int Execute()
        {
            try
            {
                Prepare();
                if (_so.DryRun)
                {
                    DryText = PlanText(true);
                    DryJson = PlanJson(true);
                    return ExitCode = 0;
                }
                return ExitCode = RunAll();
            }
            catch (RefusedException e) { Reason = e.Message; Say("REFUSED: " + e.Message); return ExitCode = 2; }
            catch (SafetyException e) { Reason = e.Message; Say("REFUSED: " + e.Message); return ExitCode = 2; }
            catch (OperationCanceledException e) { Reason = e.Message; Say("CANCELLED: " + e.Message); return ExitCode = 4; }
            catch (Exception e) { Reason = e.GetType().Name + ": " + e.Message; Say("FAILED: " + Reason); Say("  (internal) " + e); return ExitCode = 3; }
        }

        // ================================================================== prepare (static checks; a dry run stops here)

        private void Prepare()
        {
            bool dry = _so.DryRun;
            if (!_host.HasPayload) Refuse(_host.PayloadError ?? "no payload");
            if (_host.Manifest.MapbakeComponent == null)
                Refuse("this build has no .map baking component (its payload lacks the mapbake runtime): bake the scenario from the editing kit tags, or use a build that has it");
            var errs = _o.Validate(_host.CustomAvailable);
            if (errs.Count > 0) Refuse(string.Join("; ", errs));
            if (_o.Quality == Quality.Custom && BakePlanner.GpuBakeRel(_o, _host.Manifest) == InstLayout.GpuBake && !_host.CustomUnguardedAllowed)
                Refuse("Custom quality needs the N3 guarded fullgpu (engine_custom), which this payload does not carry; dev: --dev-custom-unguarded");
            if (_o.SceneryPermutationFix) { string sw = _host.SceneryPermutationFixWhy(_o); if (sw != null) Refuse(sw); }
            string compatWhy = _host.CompatEngineWhy(_o);
            if (_o.ForceCompat && compatWhy != null) Refuse("--force-compat is not available: " + compatWhy);

            string full;
            try { full = Path.GetFullPath(_o.Root.Trim().Trim('"')); } catch (Exception e) { Refuse("not a usable folder: " + _o.Root + " (" + e.Message + ")"); return; }
            if (ShellPaths.IsDriveRoot(full)) Refuse(ShellPaths.DriveRootMessage + " (" + full + ")");
            _root = ShellPaths.NormalizeRoot(full);
            if (!Directory.Exists(_root)) Refuse("the H2EK folder does not exist: " + _root);
            if (!Directory.Exists(Path.Combine(_root, "tags"))) Refuse(_root + " has no tags\\ folder -- not an H2EK folder");
            _o.Root = _root;
            _realWork = ShellPaths.WorkOf(_root);
            string bad = BakeSession.PathTextProblem(_root);
            if (bad != null) Refuse(bad);

            _mapPath = Path.GetFullPath(_o.MapPath.Trim().Trim('"'));
            if (!File.Exists(_mapPath)) Refuse("the map file does not exist: " + _mapPath);
            var hdr = MapHeaderInfo.Read(_mapPath);
            string why = hdr.Refusal();
            if (why != null) Refuse(why);
            _vista = hdr.IsVista;
            _o.MapIsVista = _vista;            // this reading is the one that counts (the default output name depends on it)
            if (_o.VistaOutGiven && !_vista) Refuse(BakeOptions.VistaOutNotVista);
            _outPath = _o.MapOutPath;
            if (string.Equals(_mapPath, _outPath, StringComparison.OrdinalIgnoreCase)) Refuse("the output map would overwrite the input map: choose another output file");
            if (_outPath.StartsWith(Path.Combine(_root, "tags") + "\\", StringComparison.OrdinalIgnoreCase)) Refuse("the output map must not be inside the editing kit's tags folder");
            if (_outPath.StartsWith(Path.Combine(_realWork, TempFolder) + "\\", StringComparison.OrdinalIgnoreCase)) Refuse("the output map must not be inside the temporary folder (" + Path.Combine(_realWork, TempFolder) + "): its contents are deleted");
            string outDir = Path.GetDirectoryName(_outPath);
            if (!Directory.Exists(outDir)) Refuse("the output folder does not exist: " + outDir);
            Say("H2 GPU Lightmapper " + _host.AppVersion + (dry ? "   DRY RUN" : "") + "   " + _o.Summary());
            Say("  source map " + _mapPath + "   (read only: it is never modified)" + (_vista ? "   [Halo 2 Vista map: converted to its MCC form with h2conv, baked like an MCC map, " + (_o.VistaOut ? "then the result is converted back to Vista" : "the result stays an MCC map") + "]" : ""));
            Say("  output map " + _outPath + (File.Exists(_outPath) ? "   (exists: it is set aside as .old_<time> first)" : ""));
            Say("  root " + _root);
            if (dry) Say("  (dry run: the map is not queried; a real run reads its skies, cube maps and bsps with h2conv first)");
        }

        // ================================================================== run

        private ProcessRunner.Result RunMapbake(string verb, IEnumerable<string> args, int timeoutMs)
        {
            CheckCancel();
            var a = MapBakeProcs.Args(_rt, _host.Config.DevH2convHome, verb, args);
            var r = ProcessRunner.RunCapture(_rt.PyExe, a, _realWork, _env, timeoutMs, (l, isErr) => Say("  " + l), jp => { lock (_lock) _cur = jp; });
            lock (_lock) _cur = null;
            if (r.TimedOut) throw new StepFailedException("the mapbake step '" + verb + "' ran longer than " + (timeoutMs / 60000) + " minutes and was stopped");
            CheckCancel();
            return r;
        }

        private static string LastMapbakeLine(ProcessRunner.Result r)
        {
            string last = null;
            lock (r.StdoutLines) foreach (var l in r.StdoutLines) if (l.StartsWith("mapbake| ", StringComparison.Ordinal)) last = l.Substring(9);
            return last;
        }

        private string RefusedLine(ProcessRunner.Result r)
        {
            lock (r.StdoutLines)
                foreach (var l in r.StdoutLines)
                    if (l.StartsWith("mapbake| REFUSED: ", StringComparison.Ordinal)) return l.Substring("mapbake| REFUSED: ".Length);
            return null;
        }

        private void GuardLive(string snapA, string label)
        {
            string snapN = Path.Combine(_runDir, "logs", "live_" + label + ".json");
            var s = RunMapbake("snap", new[] { "--tags", Path.Combine(_root, "tags"), "--out", snapN }, SnapTimeoutMs);
            if (s.ExitCode != 0) throw new StepFailedException("the live-tag snapshot failed (" + label + "): " + (LastMapbakeLine(s) ?? "exit " + s.ExitCode));
            var d = RunMapbake("snapdiff", new[] { snapA, snapN, "--out", Path.Combine(_runDir, "logs", "livediff_" + label + ".json") }, SnapTimeoutMs);
            if (d.ExitCode != 0)
            {
                string diffs = string.Join("; ", d.StdoutLines.Where(l => l.StartsWith("mapbake| LIVE TAG ", StringComparison.Ordinal)).Select(l => l.Substring(18)).Take(5));
                throw new StepFailedException("the live tags changed during the run (" + label + "): " + diffs + " -- the run was aborted and nothing was written to the output. "
                                              + "This tool never writes the live tags; if another program (Sapien, Guerilla) saved a tag meanwhile, close it and run again");
            }
        }

        private int RunAll()
        {
            // ---- the runtime (extracted / verified by the ordinary bake as well; this one is needed first for the map query)
            Say("  runtime: extract / verify ...");
            try
            {
                var q = _host.Installer.StartupEnsure(_root);
                if (!q.Ok) Refuse("the runtime could not be prepared: " + q);
            }
            catch (SlotAliasRefusedException e) { Refuse("no DLL slot base fits this H2EK path: " + e.Message); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is PathTooLongException || e is TimeoutException)
            { Refuse("the runtime payload could not be extracted / verified: " + e.Message); }
            _rt = _host.Resolve(_root);
            if (_rt.MapbakePy == null || !File.Exists(_rt.MapbakePy)) Refuse("the mapbake runtime is missing from this payload (" + _rt.MapbakePy + ")");
            _env = BakeEnv.Build(_rt);
            Directory.CreateDirectory(_realWork);

            // ---- another bake on this H2EK? (the ordinary bake checks this too; here it is before anything is written)
            CurrentRun curRun;
            var ak = CurrentRun.Inspect(_realWork, out curRun);
            if (ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell)
                Refuse("another bake is running on this H2EK (run " + (curRun == null ? "?" : curRun.RunTag) + "); wait for it or cancel it first");

            // ---- the temporary per-run folder
            string id = "m" + (_so.Now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string parent = Path.Combine(_realWork, TempFolder);
            _runDir = Path.Combine(parent, id);
            Directory.CreateDirectory(Path.Combine(_runDir, "logs"));
            try { File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.Hidden); File.SetAttributes(_runDir, File.GetAttributes(_runDir) | FileAttributes.Hidden); } catch (Exception) { }
            _sessionLog = new StreamWriter(Path.Combine(_runDir, "session.log"), true, new UTF8Encoding(false));
            foreach (var l in _pre) _sessionLog.WriteLine(l);
            _sessionLog.Flush();
            Say("  temp folder " + _runDir + " (deleted at the end; the logs stay)");
            int code = 3;
            string reason = null;
            bool cleaned = false;
            try
            {
                code = RunSteps(out reason);
            }
            catch (RefusedException e) { code = 2; reason = e.Message; Say("REFUSED: " + e.Message); }
            catch (OperationCanceledException) { code = 4; reason = "cancelled"; Say("CANCELLED"); }
            catch (StepFailedException e) { code = 3; reason = e.Message; Say("FAILED: " + e.Message); }
            catch (SafetyException e) { code = 2; reason = "refused by a safety rule: " + e.Message; Say("REFUSED: " + reason); }
            catch (Exception e) { code = 3; reason = e.GetType().Name + ": " + e.Message; Say("FAILED: " + reason); Say("  (internal) " + e); }
            finally
            {
                if (_so.KeepMapTemp) Say("temp folder KEPT (dev flag --dev-keep-mapbake-temp): " + _runDir);
                else cleaned = CleanupTemp();
                try { if (_sessionLog != null) { _sessionLog.Dispose(); _sessionLog = null; } } catch (Exception) { }
            }
            Reason = reason;
            if (!cleaned && !_so.KeepMapTemp) Reason = (Reason != null ? Reason + "; " : "") + "WARNING: the temporary folder could not be deleted: " + _runDir;
            return code;
        }

        private bool CleanupTemp()
        {
            _ignoreCancel = true;
            try
            {
                Phased("Deleting the temporary work files and overlay ...");
                var r = RunMapbake("cleanup", new[] { "--run", _runDir, "--keep", "logs", "--keep", "session.log" }, CleanupTimeoutMs);
                if (r.ExitCode != 0) { Say("WARNING: cleanup failed (exit " + r.ExitCode + "): " + (LastMapbakeLine(r) ?? "")); return false; }
                bool gone = !Directory.Exists(Path.Combine(_runDir, "root")) && !Directory.Exists(Path.Combine(_runDir, "tags_x"));
                if (!gone) Say("WARNING: the temporary folders are still there: " + _runDir);
                return gone;
            }
            catch (Exception e) { Say("WARNING: cleanup failed: " + e.Message); return false; }
            finally { _ignoreCancel = false; }
        }

        private int RunSteps(out string reason)
        {
            reason = null;
            string tagsDir = Path.Combine(_root, "tags");
            string logs = Path.Combine(_runDir, "logs");

            // ---- 1. read the map
            Phased("Reading the map ...");
            string qjson = Path.Combine(logs, "query.json");
            _h2work = Path.Combine(_runDir, "h2work");
            var qa = new List<string> { "--map", _mapPath, "--kit", tagsDir, "--cache", Path.Combine(ShellPaths.StandaloneOf(_realWork), "mapbake", "kit_cubemaps.json"), "--out", qjson };
            if (_vista) { qa.Add("--work-dir"); qa.Add(_h2work); Phased("Reading the map (a Vista map is converted to its MCC form first: about half a minute) ..."); }
            var qr = RunMapbake("query", qa, QueryTimeoutMs);
            if (!File.Exists(qjson)) throw new StepFailedException("the map reader gave no answer: " + (LastMapbakeLine(qr) ?? "exit " + qr.ExitCode));
            _query = MapQueryResult.Parse(File.ReadAllText(qjson));
            if (_query.Refusal != null) Refuse(_query.Refusal);
            if (!_query.Ok) Refuse(_query.Error ?? "the map could not be read");
            Say("  map: " + (_query.InternalName ?? "?") + (_vista ? "   (Vista map, read through its MCC form)" : "") + "   scenario " + _query.Scenario + "   " + _query.Bsps.Count + " structure bsp(s)   " + _query.TagCount.ToString("N0", CultureInfo.InvariantCulture) + " tags");
            DecideBsp();
            DecideCubemaps();
            foreach (var l in CubemapModel.Describe(_skies)) Say("  " + l);
            ValidateScenarioFolder();

            // ---- the worker (200-299; the registry of the REAL work folder, under its own key so a scenario bake of the same name keeps its worker and capture)
            bool lmWorker = BakePlanner.NeedsLightmapWorker(_o);
            if (lmWorker)
            {
                if (_o.Worker.HasValue) _worker = _o.Worker.Value;
                else
                {
                    try { _worker = new WorkerRegistry(_realWork).GetOrAllocate("mapbake\\" + _scenarioRel, _bsp, false); }
                    catch (SafetyException e) { Refuse(e.Message); }
                }
                Guards.AssertWorker(_worker);
                Say("  worker farm_w" + _worker.ToString("D2", CultureInfo.InvariantCulture) + (_o.Worker.HasValue ? " (--worker)" : " (registry key mapbake\\" + _scenarioRel + ")"));
            }

            // ---- 2. the live-tag guard, then read the map + overlay
            string snapA = Path.Combine(logs, "live_before.json");
            Phased("Snapshotting the live tags ...");
            var sa = RunMapbake("snap", new[] { "--tags", tagsDir, "--out", snapA }, SnapTimeoutMs);
            if (sa.ExitCode != 0) throw new StepFailedException("the live-tag snapshot failed: " + (LastMapbakeLine(sa) ?? "exit " + sa.ExitCode));
            Phased("Reading the map's dependency subset and building the overlay (this copies the kit tags: about a minute) ...");
            var pa = new List<string> { "--map", _mapPath, "--kit", tagsDir, "--tool-root", _root, "--run", _runDir, "--report", Path.Combine(logs, "prepare_report.json") };
            foreach (var c in _skies) { pa.Add("--sky-cubemap"); pa.Add(c.Sky + "=" + c.Selected.Value); }
            var pr = RunMapbake("prepare", pa, PrepareTimeoutMs);
            if (pr.ExitCode != 0)
            {
                string rl = RefusedLine(pr);
                if (rl != null) Refuse(rl);
                throw new StepFailedException("reading the map failed (exit " + pr.ExitCode + "): " + (LastMapbakeLine(pr) ?? ""));
            }
            GuardLive(snapA, "after_prepare");
            var prep = Json.ReadObjFile(Path.Combine(logs, "prepare_report.json"));
            if (prep == null || prep.Str("scenario") == null) throw new StepFailedException("the map-read report is missing");
            _scenarioRel = prep.Str("scenario");
            string overlayRoot = Path.Combine(_runDir, "root");
            string overlayWork = Path.Combine(overlayRoot, "w");
            Directory.CreateDirectory(overlayWork);
            string farm = null;
            if (lmWorker)
            {
                farm = BakePlanner.FarmDir(_realWork, _worker);
                Directory.CreateDirectory(farm);
                var jr = RunMapbake("junction", new[] { "--link", Path.Combine(overlayWork, Path.GetFileName(farm)), "--target", farm }, SnapTimeoutMs);
                if (jr.ExitCode != 0) throw new StepFailedException("could not link the worker's farm folder into the overlay: " + (LastMapbakeLine(jr) ?? ""));
            }

            // ---- light probes: the scenario's probe points (read from the map's scenario); Standard bakes them when there are any, Lightprobes only needs them,
            // Lightmap Only resets them to zero (as `tool lightmaps` does) -- the same three behaviours as for a scenario
            int points = 0;
            var mq = MapQuery.Run(_rt.PyExe, MapQuery.Args(Path.Combine(_rt.InstDir, InstLayout.TagIoDir), Path.Combine(overlayRoot, "tags", _scenarioRel + ".scenario"), _bsp,
                                                        Path.Combine(overlayRoot, "tags", BakeOptions.BspFolder(_scenarioRel), Path.GetFileName(_scenarioRel) + "_" + _bsp + "_lightmap.scenario_structure_lightmap")), _realWork, _env);
            if (mq.Ok) points = mq.Points ?? 0;
            else Say("  note: the probe-point query of the map's scenario failed (" + (mq.Error ?? mq.PointsError) + "); the bake's own query decides");
            bool probeWorker = BakePlanner.NeedsProbes(_o, points);
            Say("  light probes: the BSP has " + points + " probe point(s)" + (_o.Mode == BakeMode.Lightmap && points > 0 ? " -- Lightmap Only RESETS them to zero in the output map (as `tool lightmaps` does)" :
                points > 0 && probeWorker ? " -- baked as for a scenario" : ""));
            if (probeWorker)
            {
                if (_o.ProbeWorker.HasValue) _pworker = _o.ProbeWorker.Value;
                else
                {
                    try { _pworker = new WorkerRegistry(_realWork).GetOrAllocate("mapbake\\" + _scenarioRel, _bsp, true); }
                    catch (SafetyException e) { Refuse(e.Message); }
                }
                Guards.AssertWorker(_pworker);
                if (lmWorker && _pworker == _worker) Refuse("the probe worker must differ from the lightmap worker");
                Say("  probe worker farm_w" + _pworker.ToString("D2", CultureInfo.InvariantCulture) + (_o.ProbeWorker.HasValue ? " (--probe-worker)" : " (registry key mapbake\\" + _scenarioRel + ", probes)"));
                string pfarm = BakePlanner.FarmDir(_realWork, _pworker);
                Directory.CreateDirectory(pfarm);
                var jp = RunMapbake("junction", new[] { "--link", Path.Combine(overlayWork, Path.GetFileName(pfarm)), "--target", pfarm }, SnapTimeoutMs);
                if (jp.ExitCode != 0) throw new StepFailedException("could not link the probe worker's farm folder into the overlay: " + (LastMapbakeLine(jp) ?? ""));
            }

            // ---- 3. the ordinary GPU bake of the overlay (stage only)
            var no = _o.Clone();
            no.MapPath = null; no.MapOut = null; no.SkyCubemaps = new List<KeyValuePair<string, string>>();
            no.Root = overlayRoot; no.ScenarioRel = _scenarioRel; no.Bsp = _bsp; no.BspRel = _bspRel;
            no.StageOnly = true; no.ReuseCapture = false; no.Worker = lmWorker ? (int?)_worker : null; no.ProbeWorker = probeWorker ? (int?)_pworker : null;      // mode / AO / quality as chosen
            var ctx = new OverlayContext { RealRoot = _root, RealWork = _realWork, OverlayRoot = overlayRoot, OverlayWork = overlayWork };
            ctx.PreLines.Add("  source map " + _mapPath + "   (read only)");
            ctx.PreLines.Add("  output map " + _outPath + "   (a COPY of the map with only the lightmap and its bitmap replaced" + (_o.Mode == BakeMode.Probes ? " -- Lightprobes only: the lightmap tag alone" : "") + "; set aside as .old_<time> if it exists)");
            if (_vista) ctx.PreLines.Add("  Vista map: converted to its MCC form with h2conv; " + (_o.VistaOut ? "the injected MCC result is converted back to a Vista map (h2conv.inject --to-vista)" : "the result stays an MCC map (not converted back)"));
            ctx.PreLines.Add("  .map path: read the dependency subset -> real-copy overlay root -> this stage-only bake -> inject the lightmap -> validate -> delete the temp folder " + _runDir);
            foreach (var l in CubemapModel.Describe(_skies)) ctx.PreLines.Add("  " + l);
            ctx.PreLines.Add("  overlay root " + overlayRoot + " (H2LM_ROOT); its work folder " + overlayWork + " (H2LM_WORK) holds " + (lmWorker ? "farm_w" + _worker.ToString("D2", CultureInfo.InvariantCulture) : "") + (lmWorker && probeWorker ? " and " : "")
                           + (probeWorker ? "farm_w" + _pworker.ToString("D2", CultureInfo.InvariantCulture) : "") + " as junction(s) to the real farm folder(s)");
            ctx.PlanNotes.Add(".map bake of " + _mapPath + " -> " + _outPath + ": this is the bake step of the .map path (stage only); the shell then injects ONLY the lightmap and its bitmap into a copy of the map");
            ctx.PlanNotes.AddRange(CubemapModel.Describe(_skies));
            var nso = new SessionOptions
            {
                DryRun = false, SkipPreflight = _so.SkipPreflight, SkipGpuCheck = _so.SkipGpuCheck, LockWait = _so.LockWait, LockPoll = _so.LockPoll,
                CancelFile = _so.CancelFile, Now = _so.Now, Overlay = ctx,
            };
            var inner = new BakeSession(_host, no, nso);
            inner.Info += m => Say(m);
            inner.Line += l => { var h = Line; if (h != null) h(l); };
            inner.StatusChanged += st => { var h = StatusChanged; if (h != null) h(st); };
            inner.Planned += p => { Plan = p; var h = Planned; if (h != null) h(p); };
            inner.Ask = p => Ask(p);
            lock (_lock) { Inner = inner; if (_cancel) inner.RequestCancel(); }
            Phased("Baking (the ordinary GPU bake of the prepared tree, stage only) ...");
            int ic = inner.Execute();
            if (ic != 0)
            {
                string step = inner.Run == null ? null : inner.Run.Status.Step;
                string ir = inner.Reason ?? "exit " + ic;
                if (ic == 4) throw new OperationCanceledException("cancelled");
                if (ic == 2) Refuse(ir);
                if (step == null || step == StepId.Preflight.ToString() || step == StepId.Capture.ToString())
                    throw new StepFailedException("tool_fast could not load / capture the tags read from this map (the bake stopped in its " + (step ?? "first") + " step), so nothing was injected: " + ir);
                throw new StepFailedException("the bake failed in its " + step + " step: " + ir);
            }
            GuardLive(snapA, "after_bake");

            // ---- 4. inject only the lightmap + its bitmap into a copy of the map, validate, publish
            // the final stage the ordinary plan made: the lightmap stage, the AO layer's stage, the merged probe stage, or the Lightprobes-only stage (one lightmap tag)
            string stage = Plan != null && !string.IsNullOrEmpty(Plan.FinalStage) ? Plan.FinalStage : (farm != null ? Path.Combine(farm, "tagw_stage") : null);
            if (stage == null || !Directory.Exists(stage)) throw new StepFailedException("the bake made no final stage to inject (" + (stage ?? "none") + ")");
            Say("  final stage " + stage);
            Phased(_vista && _o.VistaOut ? "Injecting the lightmap, validating, and converting the result back to Vista (a few minutes) ..." : "Injecting the lightmap into a copy of the map and validating it ...");
            var ia = new List<string> { "--map", _mapPath, "--run", _runDir, "--stage", stage, "--scenario", _scenarioRel, "--bsp", _bsp, "--out", _outPath, "--report", Path.Combine(logs, "inject_result.json") };
            if (_bspRel != null) { ia.Add("--bsp-rel"); ia.Add(_bspRel); }
            if (_o.Mode == BakeMode.Probes) ia.Add("--ltmp-only");
            if (_vista) { ia.Add("--vista-out"); ia.Add(_o.VistaOut ? "yes" : "no"); }
            var ir2 = RunMapbake("inject", ia, InjectTimeoutMs);
            if (ir2.ExitCode != 0)
            {
                string rl = RefusedLine(ir2);
                throw new StepFailedException("injecting the lightmap into the map failed" + (rl != null ? ": " + rl : " (exit " + ir2.ExitCode + ")") + "; the baked stage is kept in " + stage);
            }
            GuardLive(snapA, "after_inject");
            reason = "baked from the map: " + _outPath;
            Say("RESULT map baked: " + _outPath);
            return 0;
        }

        private void DecideBsp()
        {
            var leaves = _query.Bsps.Select(b => b.Substring(b.LastIndexOf('\\') + 1)).ToList();
            string want = _o.Bsp;
            if (string.IsNullOrWhiteSpace(want))
            {
                if (_query.Bsps.Count == 1) want = leaves[0];
                else Refuse("this map's scenario has " + _query.Bsps.Count + " structure bsps (" + string.Join(", ", leaves) + "): choose one (CLI: --bsp NAME)");
            }
            var hits = _query.Bsps.Where(b => string.Equals(b.Substring(b.LastIndexOf('\\') + 1), want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) Refuse("the map has no structure bsp named '" + want + "' (it has: " + string.Join(", ", leaves) + ")");
            if (hits.Count > 1) Refuse("the map has " + hits.Count + " structure bsps named '" + want + "'");
            _bspRel = hits[0];
            _bsp = _bspRel.Substring(_bspRel.LastIndexOf('\\') + 1);
            string scen = _query.Scenario ?? "";
            if (!string.Equals(BakeOptions.BspFolder(_bspRel), BakeOptions.BspFolder(scen), StringComparison.OrdinalIgnoreCase))
                Refuse("this map's BSP '" + _bsp + "' lives in another folder than its scenario (" + BakeOptions.BspFolder(_bspRel) + "); that layout is not supported for a .map bake yet");
            _bspRel = null;      // beside the scenario: the plain name is all gpu_bake needs
        }

        private void DecideCubemaps()
        {
            _skies = CubemapModel.Build(_query);
            var errs = CubemapModel.Apply(_skies, _o.SkyCubemaps);
            if (errs.Count > 0) Refuse(string.Join("; ", errs));
        }

        private void ValidateScenarioFolder()
        {
            string s = _query.Scenario ?? "";
            if (s.Length == 0 || !BakeOptions.IsPlainTagRel(s)) Refuse("the map's scenario name is not a plain tag path: '" + s + "'");
            _scenarioRel = s;
        }

        // ================================================================== plan text

        public string PlanText(bool dry)
        {
            var sb = new StringBuilder();
            if (dry) sb.AppendLine("DRY RUN -- nothing is started and nothing is written.");
            sb.AppendLine("H2 GPU Lightmapper " + _host.AppVersion + "   payload " + _host.Source.Describe + "   (bake from a .map)");
            sb.AppendLine("  root        " + _root);
            sb.AppendLine("  work        " + _realWork);
            sb.AppendLine("  source map  " + _mapPath + "   (READ ONLY: never modified)");
            sb.AppendLine("  output map  " + _outPath + "   (a copy of the map with ONLY the lightmap and its bitmap replaced)" + (File.Exists(_outPath) ? "   [exists: set aside as .old_<time>]" : ""));
            sb.AppendLine("  bsp         " + (string.IsNullOrWhiteSpace(_o.Bsp) ? "(decided from the map: the only bsp, or --bsp NAME)" : _o.Bsp));
            sb.AppendLine("  options     " + _o.Summary());
            sb.AppendLine("  layers      " + (_o.Mode == BakeMode.Probes ? "Lightprobes only: no lightmap is baked; the probes are written into the map's own lightmap tag (the only tag besides the structure bsp that is injected)"
                                                  : "lightmap" + (_o.Mode == BakeMode.Standard ? " + light probes when the BSP has probe points (decided from the map's scenario by a real run)" : " only (Lightmap Only: a BSP with probe points has them reset to zero, as tool lightmaps does)")
                                                    + (_o.Ao ? " + ambient occlusion (strength " + _o.AoStrength.ToString(CultureInfo.InvariantCulture) + ", exponent " + _o.AoExponent.ToString(CultureInfo.InvariantCulture) + "; run by the same gpu_ao steps as for a scenario)" : "")));
            if (_vista)
                sb.AppendLine("  vista       the input is a Halo 2 Vista map: h2conv converts it to its MCC form (kept in the temp folder), the bake and the injection run on that, and "
                              + (_o.VistaOut ? "the MCC result is converted back to a Vista map (h2conv.inject --to-vista) = the output" : "the MCC result is the output (not converted back)"));
            if (_o.SkyCubemaps.Count > 0)
                foreach (var kv in _o.SkyCubemaps) sb.AppendLine("  sky cubemap " + kv.Key + " = " + kv.Value + "   (checked against the map's skies and the kit's cube-map bitmaps by a real run)");
            else
                sb.AppendLine("  sky cubemap for every sky with cube map power scale > 0 and no cube map reference (compiled maps lose it): the automatic match (the kit's sky with the same sky model), "
                              + "else LEFT EMPTY with a warning (the bake then lacks the sky's ambient fill, about 30% darker on headlong); override with --sky-cubemap <sky>=<bitmap tag path | none>");
            sb.AppendLine("  temp folder " + Path.Combine(_realWork, TempFolder) + "\\m<time>" + "   (the map's files + overlay; deleted at the end, the logs stay)");
            sb.AppendLine("  engine      the normal bake: stage only, nothing is written to the editing kit's tags; the dry run does not query the map, so the bake's own argv is printed by a real run's log header");
            for (int i = 0; i < StepTitles.Length; i++) sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "STEP {0,2}  {1}", i + 1, StepTitles[i]));
            return sb.ToString();
        }

        public JObj PlanJson(bool dry)
        {
            return new JObj().Add("schema", "h2lm-mapbake-plan/1").Add("dryRun", dry).Add("appVersion", _host.AppVersion).Add("payload", _host.Source.Describe)
                .Add("root", _root).Add("work", _realWork).Add("map", _mapPath).Add("output", _outPath).Add("outputExists", File.Exists(_outPath))
                .Add("bsp", string.IsNullOrWhiteSpace(_o.Bsp) ? null : _o.Bsp).Add("quality", _o.QualityToken)
                .Add("options", _o.Summary()).Add("mode", _o.ModeToken).Add("ao", _o.Ao).Add("vistaInput", _vista).Add("vistaOut", _vista ? (object)_o.VistaOut : null)
                .Add("skyCubemaps", _o.SkyCubemaps.Select(kv => (object)new JObj().Add("sky", kv.Key).Add("bitmap", kv.Value)).ToList())
                .Add("steps", StepTitles.Select((t, i) => (object)new JObj().Add("n", (long)(i + 1)).Add("title", t)).ToList());
        }
    }

    /// <summary>The argv of the internal mapbake entry point (the bundled Python + mapbake.py [--h2conv-home DIR] verb ...) and the read-only map query the window and the session share.</summary>
    public static class MapBakeProcs
    {
        /// <summary>Deletes the throw-away folder of a Vista query (its files, then its folders deepest first; no recursive delete, no links followed: it only ever holds the temporary MCC map).</summary>
        internal static void DeleteThrowAway(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { try { File.Delete(f); } catch (Exception) { } }
                foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length)) { try { Directory.Delete(d); } catch (Exception) { } }
                Directory.Delete(dir);
            }
            catch (Exception) { }
        }

        public static List<string> Args(ResolvedRuntime rt, string devH2convHome, string verb, IEnumerable<string> rest)
        {
            var a = new List<string> { rt.MapbakePy };
            if (!string.IsNullOrEmpty(devH2convHome)) { a.Add("--h2conv-home"); a.Add(Path.GetFullPath(devH2convHome)); }
            a.Add(verb);
            a.AddRange(rest);
            return a;
        }

        /// <summary>mapbake query: the map's container, scenario, bsps, skies and cube maps (names only; nothing of the map's content). The JSON goes to <paramref name="outJson"/> (default: a temp file, deleted).</summary>
        public static MapQueryResult Query(ResolvedRuntime rt, ChildEnvironment env, string devH2convHome, string map, string kitTags, string cache, string outJson = null)
        {
            if (rt == null || rt.MapbakePy == null) return new MapQueryResult { Error = "this build has no .map baking component" };
            bool temp = outJson == null;
            string o = outJson ?? Path.Combine(Path.GetTempPath(), "h2lm_mapq_" + Guid.NewGuid().ToString("N") + ".json");
            // a Vista map is read through its MCC form: the reverse conversion (about half a minute) goes into a throw-away folder under the work folder
            string vwork = null;
            var qargs = new List<string> { "--map", map, "--kit", kitTags, "--cache", cache, "--out", o };
            try
            {
                var hi = MapHeaderInfo.Read(map);
                if (hi.IsVista) { vwork = Path.Combine(rt.Work, MapBakeSession.TempFolder, "q" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(vwork); qargs.Add("--work-dir"); qargs.Add(vwork); }
            }
            catch (Exception) { vwork = null; }
            try
            {
                var r = ProcessRunner.RunCapture(rt.PyExe, Args(rt, devH2convHome, "query", qargs), rt.Work, env, MapBakeSession.QueryTimeoutMs);
                if (r.TimedOut) return new MapQueryResult { Error = "reading the map took too long" };
                if (!File.Exists(o)) return new MapQueryResult { Error = "the map reader gave no answer (exit " + r.ExitCode + ")" };
                return MapQueryResult.Parse(File.ReadAllText(o));
            }
            finally
            {
                if (temp) { try { File.Delete(o); } catch (IOException) { } }
                if (vwork != null) DeleteThrowAway(vwork);
            }
        }
    }
}
