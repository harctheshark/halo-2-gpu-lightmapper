// S3.x one bake, start to finish (FINAL_DESIGN sections 5, 7, 8, 9), shared by the GUI and the CLI.
//   Prepare (refusals here = exit 2, before anything is written to the tags or a farm):
//     options + feature flag -> root / scenario / BSP exist -> path text -> preflight (tool_fast, prerequisites, NTFS, ANSI,
//     slot, budget, free space, write probe, tool smoke) -> payload extract + full verify + repair -> gpucheck + pwsh policy
//     -> bounds DB copied if absent -> startup recovery (interrupted run marked, own stale lock released; a half-finished
//     install REFUSES the bake) -> map query (probe points, environments) -> workers -> capture-reuse gate
//   Run (BakeRun: RUN\bake.log, status.json, current_run.json): run header -> the shared GPU lock (WAITING while held) ->
//     the plan's steps -> verify -> history. Cancel: BakeRun.RequestCancel (deferred during a tag write); RUN\cancel.request
//     (written by another window or `--cli cancel`) requests it too. Exit codes 0 done, 2 refused, 3 failed, 4 cancelled.
//   --dry-run: Prepare without starting a process or writing a file (no preflight, no extraction, workers peeked, the probe
//     count from --probe-points), then the plan is printed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
    public enum PromptKind { ChangedTags, InstallWithoutLayer, InstallRetry, RestoreAfterFailedInstall, RestoreAfterCancel }

    public sealed class Prompt
    {
        public PromptKind Kind;
        public string Title, Message;
        public List<string> Details = new List<string>();
        /// <summary>InstallWithoutLayer: which layers the production write-back would drop (AO; the BSP's light probes, reset
        /// to zero like `tool lightmaps`).</summary>
        public bool DropsAo, DropsProbes;
        public int ProbePoints;
    }

    public sealed class SessionOptions
    {
        public bool DryRun;
        /// <summary>Dev / tests: skip preflight (a sandbox root has no real tool_fast).</summary>
        public bool SkipPreflight;
        /// <summary>Dev / tests: skip gpucheck.</summary>
        public bool SkipGpuCheck;
        /// <summary>--dry-run only: the probe-point count to assume (a real run queries the scenario).</summary>
        public int? AssumeProbePoints;
        /// <summary>--dry-run only (X1): the adapter's dedicated memory in MiB to size the parts for (a real run asks gpucheck).</summary>
        public int? AssumeVramMB;
        /// <summary>--dry-run only (X2): video memory in MiB that other programs are assumed to hold (a real run measures it with gpucheck).</summary>
        public int? AssumeOtherVramUseMB;
        /// <summary>--dry-run only (Compatibility mode): plan as if the GPU had no FP64 (a real run asks gpucheck). On an engine without Compatibility mode
        /// that is the plain no-FP64 refusal, exactly as a real run on such a GPU.</summary>
        public bool AssumeNoFp64;
        public TimeSpan LockWait = TimeSpan.FromMinutes(240);
        public TimeSpan LockPoll = TimeSpan.FromSeconds(20);
        /// <summary>Dev: another file whose appearance requests a cancel (besides RUN\cancel.request).</summary>
        public string CancelFile;
        public DateTime? Now;
    }

    public sealed class RefusedException : Exception { public RefusedException(string m) : base(m) { } }
    public sealed class StepFailedException : Exception { public StepFailedException(string m) : base(m) { } }

    public sealed class BakeSession
    {
        public const string CancelRequestFile = "cancel.request";

        private readonly AppHost _host;
        private readonly BakeOptions _o;
        private readonly SessionOptions _so;
        private readonly object _lock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<string> _pre = new List<string>();
        private bool _cancel;
        private Timer _watch;

        // prepared context
        private string _root, _work;
        private ResolvedRuntime _rt;
        private ChildEnvironment _env;
        private PreflightReport _pf;
        private string _toolMd5;
        private int? _n, _m;
        private string _nNote, _mNote;
        private int _points;
        private bool _driveRootOk;
        private GpuLimits _limits;
        private string _probeWarning;
        private bool _pointsAssumed;
        private MapInfo _map;
        private bool _reuseN, _reuseM;
        private string _reuseNNote, _reuseMNote;
        private Lazy<TagsFingerprint> _tagsNow;
        private TagSet _set;
        private BackupResult _backup;
        private string _liveCopyMd5;
        private readonly Dictionary<string, double> _stepSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        private string _curStep;
        private DateTime _curStepStart;
        private bool _installedWithoutLayer;
        /// <summary>U3: the production -WriteBack argv installed (the tool write-back fallback, or an install-without-layer):
        /// Verify compares the live set with OUT\tagw_stage only when this run's direct writer wrote one.</summary>
        private bool _productionWriteBack;
        private string _productionWriteBackWhy;
        /// <summary>0.6.4 cross-folder BSP: the BSP's tag path is still to be taken from the scenario's structure_bsps (non-dry: right after the map query).</summary>
        private bool _bspFromScenario;
        /// <summary>0.6.4: the engine's own write-back fallbacks seen in this run (see <see cref="WriteBackFallback"/>), one entry per distinct warning.</summary>
        private readonly List<FallbackInfo> _fallbackSeen = new List<FallbackInfo>();

        /// <summary>The run-summary sentences for the write-back fallbacks this run saw ("WARNING: the write-back fell back from the DIRECT tag writer to
        /// the TOOL write-back (reason)"): the fallbacks that happened; a heads-up only when its fallback line never followed.</summary>
        public IList<string> WriteBackFallbacks
        {
            get
            {
                lock (_lock)
                {
                    var r = new List<string>();
                    foreach (var f in _fallbackSeen.Where(x => !x.HeadsUp)) r.Add(WriteBackFallback.SummaryText(f));
                    foreach (var f in _fallbackSeen.Where(x => x.HeadsUp))
                        if (!_fallbackSeen.Any(x => !x.HeadsUp && x.Direction == f.Direction)) r.Add(WriteBackFallback.SummaryText(f));
                    return r;
                }
            }
        }

        /// <summary>0.6.4: an engine output line (or the shell's own fallback) that is a write-back fallback becomes a visible warning in the log -- once per
        /// kind -- and is remembered for the run summary.</summary>
        private void NoteWriteBackFallback(BakeRun run, string text)
        {
            var f = WriteBackFallback.Detect(text);
            if (f == null) return;
            lock (_lock)
            {
                if (_fallbackSeen.Any(x => x.Key == f.Key)) return;
                _fallbackSeen.Add(f);
            }
            try { run.Warn(WriteBackFallback.WarningText(f)); } catch (Exception) { }
        }
        /// <summary>0.6.6: tolerated stage deviations seen in this run (lmw_validate WARN / the tag writer filled uncovered texels under lit UVs; see
        /// <see cref="StageWarning"/>): the stage is installed anyway, with a yellow warning in the log and a sentence in the run summary.</summary>
        private readonly List<StageWarningInfo> _stageWarnSeen = new List<StageWarningInfo>();

        public IList<string> StageWarnings
        {
            get { lock (_lock) return _stageWarnSeen.Select(StageWarning.SummaryText).ToList(); }
        }

        private void NoteStageWarning(BakeRun run, string text)
        {
            var w = StageWarning.Detect(text);
            if (w == null) return;
            lock (_lock)
            {
                if (_stageWarnSeen.Any(x => x.Key == w.Key)) return;
                _stageWarnSeen.Add(w);
            }
            try { run.Warn(StageWarning.WarningText(w)); } catch (Exception) { }
        }
        /// <summary>The live tags were written by this run (the shell's install, or gpu_bake -WriteBack's [4b] / [4]).</summary>
        private bool _installed;
        private readonly List<RecoveryItem> _markers = new List<RecoveryItem>();
        private PreflightItem _hybridFail;
        /// <summary>Compatibility mode (DF64): every engine this bake runs has it (gpu_bake.ps1 validates GPULM_COMPAT); the kernel set the bake runs
        /// (Unknown = decided by the GPU at run time); and fullgpu's own "Compatibility mode" line as the engine reported it.</summary>
        private bool _engineHasCompat;
        private PrecisionMode _precision;
        private string _engineCompatLine;

        public BakeSession(AppHost host, BakeOptions o, SessionOptions so = null)
        {
            _host = host; _o = o.Clone(); _so = so ?? new SessionOptions();
        }

        public BakeOptions Options { get { return _o; } }
        public BakePlan Plan { get; private set; }
        public BakeRun Run { get; private set; }
        public int ExitCode { get; private set; } = -1;
        public string Reason { get; private set; }
        /// <summary>The run summary's precision line (after the run): "Precision: Full FP64" / the Compatibility-mode sentence, + the engine's own line when it printed one.</summary>
        public string PrecisionSummary { get; private set; }
        public PreflightReport Preflight { get { return _pf; } }

        /// <summary>Messages before the run folder exists (and replayed into bake.log once it does).</summary>
        public event Action<string> Info;
        public event Action<ParsedLine> Line;
        public event Action<RunStatus> StatusChanged;
        public event Action<BakePlan> Planned;
        /// <summary>Confirmations (changed tags, install without AO / probes, retry, restore). The CLI answers from its flags;
        /// the GUI shows a dialog. Default: no.</summary>
        public Func<Prompt, bool> Ask = p => false;

        private void Say(string m)
        {
            lock (_lock) _pre.Add(m);
            var h = Info; if (h != null) { try { h(m); } catch { } }
        }

        public void RequestCancel()
        {
            BakeRun r;
            lock (_lock) { _cancel = true; r = Run; }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            if (r != null) r.RequestCancel();
        }

        private void CheckCancel() { lock (_lock) if (_cancel) throw new OperationCanceledException("cancelled before the run started"); }

        // ================================================================== entry

        public int Execute()
        {
            try
            {
                Prepare();
                if (_so.DryRun)
                {
                    Plan = BuildPlan(PredictRunDir(), true);
                    var h = Planned; if (h != null) h(Plan);
                    return ExitCode = 0;
                }
                return ExitCode = RunPlan();
            }
            catch (RefusedException e) { Reason = e.Message; Say("REFUSED: " + e.Message); return ExitCode = 2; }
            catch (SafetyException e) when (Run == null) { Reason = e.Message; Say("REFUSED: " + e.Message); return ExitCode = 2; }
            catch (OperationCanceledException e) when (Run == null) { Reason = e.Message; Say("CANCELLED: " + e.Message); return ExitCode = 4; }
            catch (Exception e) when (Run == null) { Reason = e.GetType().Name + ": " + e.Message; Say("FAILED before the run started: " + Reason); Say("  (internal) " + e); return ExitCode = 3; }
        }

        // ================================================================== prepare (no tag / farm write)

        private void Refuse(string m) { throw new RefusedException(m); }

        /// <summary>0.6.4 cross-folder BSP: the lightmap is what engine 0.6.4 verifies for a BSP in another folder (its skeleton, direct writer and tool
        /// write-back overlay). 0.6.5: the AO driver (gpu_ao stage) and the light-probe driver read the BSP from the file the scenario's structure_bsps names -- but only a payload whose
        /// drivers carry the marker (AppHost.CrossFolderAoProbesWhy) may be given such a BSP with AO / probes; an older payload's drivers look for it beside the scenario, so
        /// for them the layer is refused (the 0.6.4 sentence).</summary>
        private void RefuseCrossFolderLayers() { RefuseCrossFolderLayersFor(_o.BspRel != null); }

        /// <param name="crossFolder">the BSP is in another folder than the scenario: known (BspRel) or, in a dry run, implied (the BSP is not beside the scenario and is looked up by the real run).</param>
        private void RefuseCrossFolderLayersFor(bool crossFolder)
        {
            if (!crossFolder) return;
            bool probes = _o.Mode == BakeMode.Probes || BakePlanner.NeedsProbes(_o, _points);
            if (!_o.Ao && !probes) return;
            string why = _host.CrossFolderAoProbesWhy(_o.Ao, probes);
            if (why != null) Refuse(why);
        }

        /// <summary>0.6.4: the map query listed the scenario's structure_bsps. The BSP named <c>_o.Bsp</c> is the entry whose tag path ends in that name -- in
        /// the scenario's folder or in ANOTHER one (<c>_o.BspRel</c> is then set, and gpu_bake is given the same plain name: engine 0.6.4 resolves the file the
        /// same way, lmw_skeleton.py resolve-bsp). Two entries with that name are refused (the engine refuses them too). No entry: a BSP file beside the
        /// scenario is the old behaviour, else nothing to bake.</summary>
        private void ResolveBspFromScenario(MapInfo m)
        {
            string scenFolder = BakeOptions.BspFolder(_o.ScenarioRel);
            var hits = new List<string>();
            foreach (var raw in m.BspRefs)
            {
                string p = (raw ?? "").Replace('/', '\\').Trim('\\');
                if (p.Length == 0 || !string.Equals(p.Substring(p.LastIndexOf('\\') + 1), _o.Bsp, StringComparison.OrdinalIgnoreCase)) continue;
                if (!hits.Contains(p, StringComparer.OrdinalIgnoreCase)) hits.Add(p);
            }
            string beside = Path.Combine(_set.TagDir, _o.Bsp + ".scenario_structure_bsp");
            if (hits.Count > 1) Refuse("the scenario has " + hits.Count + " structure_bsps entries named " + _o.Bsp + " (" + string.Join(", ", hits.Select(BakeOptions.BspFolder)) + "): the engine cannot tell them apart");
            if (hits.Count == 1)
            {
                string rel = hits[0], file = Path.Combine(_root, "tags", rel + ".scenario_structure_bsp");
                if (File.Exists(file))
                {
                    string leaf = BakeOptions.CanonicalBsp(Path.GetDirectoryName(file), _o.Bsp) ?? _o.Bsp;
                    if (leaf != _o.Bsp) { Say("  BSP name as on disk: " + leaf + " (given: " + _o.Bsp + ")"); _o.Bsp = leaf; _set = TagSet.For(_root, _o.ScenarioRel, _o.Bsp); }
                    if (string.Equals(BakeOptions.BspFolder(rel), scenFolder, StringComparison.OrdinalIgnoreCase)) { _o.BspRel = null; return; }
                    _o.BspRel = BakeOptions.BspFolder(rel) + "\\" + leaf;
                    Say("  BSP tag (from the scenario's structure_bsps): " + _o.BspRel + " -- in another folder than the scenario; the lightmap tags are named and written beside the scenario");
                    if (File.Exists(beside)) Say("  *** a file " + _o.Bsp + ".scenario_structure_bsp is ALSO beside the scenario; the scenario does not reference it, so it is ignored (the engine bakes the referenced BSP)");
                    RefuseCrossFolderLayers();
                    return;
                }
                if (!File.Exists(beside)) Refuse("the scenario references the BSP " + rel + " but that tag does not exist");
                Say("  *** the scenario references " + rel + " which does not exist; using the file beside the scenario (as the engine does)");
            }
            if (!File.Exists(beside)) Refuse("no BSP tag " + beside + ": the scenario's structure_bsps block has no BSP named " + _o.Bsp + (m.Bsps.Count > 0 ? " (it lists: " + string.Join(", ", m.Bsps) + ")" : ""));
        }

        private void Prepare()
        {
            bool dry = _so.DryRun;
            if (!_host.HasPayload) Refuse(_host.PayloadError ?? "no payload");
            var errs = _o.Validate(_host.CustomAvailable);
            if (errs.Count > 0) Refuse(string.Join("; ", errs));
            // review fix (finding 14): the light-probe pipeline is release-gated on T4 (V5: the payload's features.probes)
            if (_o.Mode == BakeMode.Probes && !_host.ProbesEnabled)
                Refuse("Lightmap Lightprobes is not released yet in this build (light probes are gated on T4, the engine_v373 re-validation: " + _host.ProbesWhy + "); dev: --dev-probes");
            // review fix (finding 16): Custom only on the N3 guarded fullgpu unless explicitly allowed unguarded
            if (_o.Quality == Quality.Custom && BakePlanner.GpuBakeRel(_o, _host.Manifest) == InstLayout.GpuBake && !_host.CustomUnguardedAllowed)
                Refuse("Custom quality needs the N3 guarded fullgpu (engine_custom), which this payload does not carry -- without it a photon count past the uint32 limit wraps silently; dev: --dev-custom-unguarded");
            // "Scenery Multi Permutation Fix": only an engine whose gpu_bake.ps1 declares -SceneryOwnPermutationOnly may be given it -- PowerShell fails a
            // bake that is passed an unknown parameter, so this is refused up front (dry runs too), never passed on
            if (_o.SceneryPermutationFix)
            {
                string sw = _host.SceneryPermutationFixWhy(_o);
                if (sw != null) Refuse(sw);
            }
            // Compatibility mode (DF64): the engines this bake runs either all validate GPULM_COMPAT or the GPU must have FP64. --force-compat is
            // refused on an engine without it (dry runs too): GPULM_COMPAT is never passed to an engine that does not validate it.
            string compatWhy = _host.CompatEngineWhy(_o);
            _engineHasCompat = compatWhy == null;
            if (_o.ForceCompat && compatWhy != null) Refuse("--force-compat is not available: " + compatWhy);
            // review fix (finding 15): AO only when the payload proved the AO trace engine matches the lightmap engine
            string aoWhy;
            if (_o.Ao && !_host.AoEnabled(out aoWhy)) Refuse("ambient occlusion is not available: " + aoWhy);

            // ---- root / scenario / BSP
            string full = null;
            try { full = Path.GetFullPath(_o.Root.Trim().Trim('"')); } catch (Exception e) { Refuse("not a usable folder: " + _o.Root + " (" + e.Message + ")"); }
            if (ShellPaths.IsDriveRoot(full))
            {
                // X1 (was review fix, finding 23): an H2EK at "D:\" bakes only with a payload whose gpu_bake.ps1 has the P1b handling
                string dwhy;
                if (!_host.DriveRootEnabled(out dwhy)) Refuse(ShellPaths.DriveRootMessage + " (" + full + "): " + dwhy);
                _driveRootOk = true;
            }
            _root = ShellPaths.NormalizeRoot(full);
            if (!Directory.Exists(_root)) Refuse("the H2EK folder does not exist: " + _root);
            if (!Directory.Exists(Path.Combine(_root, "tags"))) Refuse(_root + " has no tags\\ folder -- not an H2EK folder");
            _o.Root = _root;
            string rel = BakeOptions.NormalizeScenario(_root, _o.ScenarioRel);
            if (rel == null) Refuse("the scenario '" + _o.ScenarioRel + "' is not a path inside " + Path.Combine(_root, "tags"));
            // review fix (finding 22): the on-disk case, as the production launcher passes it
            string canon = BakeOptions.CanonicalScenario(_root, rel);
            if (canon != null && canon != rel) { Say("  scenario path as on disk: " + canon + " (given: " + rel + ")"); rel = canon; }
            _o.ScenarioRel = rel;
            _set = TagSet.For(_root, rel, _o.Bsp);
            if (!File.Exists(_set.ScenarioFile)) Refuse("no scenario tag " + _set.ScenarioFile);
            // 0.6.4 cross-folder BSP: the BSP's tag may live in ANOTHER folder than the scenario (01b_spacestation -> 01a_tutorial\01_bsp_1). Only an engine whose
            // gpu_bake.ps1 resolves it from the scenario's structure_bsps (AppHost.CrossFolderBspWhy) may be given one; an older engine keeps the beside-the-scenario rule.
            string crossWhy = _host.CrossFolderBspWhy(_o);
            if (_o.BspRel != null)
            {
                if (crossWhy != null) Refuse("the BSP " + _o.Bsp + " is the tag " + _o.BspRel + " (another folder than the scenario): " + crossWhy);
                string otherFile = _o.BspTagFile(_set.TagDir);
                if (!File.Exists(otherFile)) Refuse("no BSP tag " + otherFile);
                string otherCanon = BakeOptions.CanonicalBsp(Path.GetDirectoryName(otherFile), _o.Bsp);
                if (otherCanon != null && otherCanon != _o.Bsp)
                {
                    Say("  BSP name as on disk: " + otherCanon + " (given: " + _o.Bsp + ")");
                    _o.BspRel = BakeOptions.BspFolder(_o.BspRel) + "\\" + otherCanon; _o.Bsp = otherCanon; _set = TagSet.For(_root, rel, _o.Bsp);
                }
                Say("  BSP tag: " + _o.BspRel + " -- in another folder than the scenario; the lightmap tags are named and written beside the scenario");
                RefuseCrossFolderLayers();
            }
            else
            {
                string bspFile = Path.Combine(_set.TagDir, _o.Bsp + ".scenario_structure_bsp");
                if (!File.Exists(bspFile))
                {
                    if (crossWhy == null)
                    {
                        _bspFromScenario = true;      // not beside the scenario: its tag path comes from the scenario's structure_bsps block (the map query, below)
                        Say("  BSP " + _o.Bsp + " is not beside the scenario: its tag is looked up in the scenario's structure_bsps block");
                    }
                    else
                    {
                        var have = MapQuery.Bsps(_root, rel);
                        Refuse("no BSP tag " + bspFile + (have.Count > 0 ? " (this scenario's folder has: " + string.Join(", ", have) + ")" : "") + ". If the scenario's BSP lives in another folder: " + crossWhy);
                    }
                }
                else
                {
                    string bspCanon = BakeOptions.CanonicalBsp(_set.TagDir, _o.Bsp);
                    if (bspCanon != null && bspCanon != _o.Bsp) { Say("  BSP name as on disk: " + bspCanon + " (given: " + _o.Bsp + ")"); _o.Bsp = bspCanon; _set = TagSet.For(_root, rel, _o.Bsp); }
                    _bspFromScenario = crossWhy == null;     // a copy beside the scenario must be the BSP the scenario references (checked after the map query)
                }
            }
            _work = ShellPaths.WorkOf(_root);
            string bad = PathTextProblem(_root);
            if (bad != null) Refuse(bad);
            Say("H2 GPU Lightmapper " + _host.AppVersion + (dry ? "   DRY RUN" : "") + "   " + _o.Summary());
            Say("  map " + rel + " / " + _o.Bsp + "   root " + _root);
            CheckCancel();

            // ---- preflight (section 8)
            var inst = _host.Manifest.InstComponent;
            bool dgpuExports = _o.Quality == Quality.Custom && BakePlanner.GpuBakeRel(_o, _host.Manifest) == InstLayout.GpuBakeCustom;
            if (dry) Say("  preflight: not run in a dry run (see --cli doctor)");
            else if (_so.SkipPreflight)
            {
                // review fix (finding 24): the tool_fast identity stays a HARD refusal even with the dev flag -- it is the only
                // effective defs pin (gpu_bake runs lm_tagwriter without --tool, whose own pin falls back to the dev PC's path)
                var tool = ToolFast.Identify(_root);
                if (tool.Verdict != ToolVerdict.Supported) Refuse("preflight (tool.build, kept with --dev-skip-preflight): " + tool.StatusLine);
                _toolMd5 = tool.Md5;
                Say("  preflight: SKIPPED (dev flag) except tool.build: " + tool.StatusLine);
            }
            else
            {
                Say("  preflight ...");
                _pf = PreflightChecks.Run(_root, new PreflightOptions
                {
                    ProbeWrite = true, RunToolSmoke = true, Custom = _o.Quality == Quality.Custom, CustomPhotons = _o.Photons,
                    InstDirName = inst.DirName, InstMaxPathLen = inst.MaxPathLen, ScenarioRel = rel, Bsp = _o.Bsp,
                    AliasLocalAppData = _host.Paths.AliasLocalAppData, AliasVolumeRoot = _host.Paths.AliasVolumeRoot,
                    FullgpuSelectsDgpu = dgpuExports, AllowDriveRoot = _driveRootOk,
                });
                foreach (var it in _pf.Items.Where(i => i.Level != CheckLevel.Ok)) Say("    " + it);
                _toolMd5 = _pf.Tool == null ? null : _pf.Tool.Md5;
                if (!_pf.Ok) Refuse("preflight: " + string.Join(" | ", _pf.Failures.Select(f => f.Message)));
                Say("  preflight ok: " + (_pf.Tool == null ? "" : _pf.Tool.StatusLine));
            }
            CheckCancel();

            // ---- payload: extract what is missing, full verify + repair (section 9)
            if (dry)
            {
                _rt = _host.Resolve(_root);
                if (_rt.SlotBase == null)
                {
                    try { _rt.SlotBase = SlotAliasPlanner.Plan(_work, _host.Paths.AliasLocalAppData, _host.Paths.AliasVolumeRoot).SlotBase + " (planned)"; }
                    catch (SlotAliasRefusedException e) { Refuse("no DLL slot base fits this H2EK path: " + e.Message); }
                }
            }
            else
            {
                Say("  runtime: extract / verify ...");
                try
                {
                    var q = _host.Installer.StartupEnsure(_root, new ExtractOptions { Cancel = _cts.Token });
                    if (!q.Ok) Refuse("the runtime could not be prepared: " + q);
                    var v = _host.Installer.VerifyAndRepairBeforeBake(_root, _cts.Token);
                    Say(string.Format(CultureInfo.InvariantCulture, "  runtime ok ({0:0.0} s md5): slot base {1}", v.Seconds, v.SlotBase));
                }
                catch (SlotAliasRefusedException e) { Refuse("no DLL slot base fits this H2EK path: " + e.Message); }
                catch (OperationCanceledException) { throw; }
                catch (RefusedException) { throw; }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is PathTooLongException || e is TimeoutException)
                { Refuse("the runtime payload could not be extracted / verified: " + e.Message); }
                _rt = _host.Resolve(_root);
            }
            _env = BakeEnv.Build(_rt);
            CheckCancel();

            // ---- GPU + pwsh policy (needs the extracted runtime)
            GpuCheckResult gpuRes = null;
            if (!dry && !_so.SkipPreflight)
            {
                bool gpu = !_so.SkipGpuCheck && _rt.GpuCheckExe != null;
                if (!_so.SkipGpuCheck && _rt.GpuCheckExe == null) Say("  gpucheck: the payload has no gpucheck.exe -- the GPU is not checked");
                var pf2 = PreflightChecks.Run(_root, new PreflightOptions
                {
                    ProbeWrite = false, RunToolSmoke = false, Custom = _o.Quality == Quality.Custom, CustomPhotons = _o.Photons,
                    InstDirName = inst.DirName, InstMaxPathLen = inst.MaxPathLen, ScenarioRel = rel, Bsp = _o.Bsp,
                    AliasLocalAppData = _host.Paths.AliasLocalAppData, AliasVolumeRoot = _host.Paths.AliasVolumeRoot, AllowDriveRoot = _driveRootOk,
                    GpuCheckExe = gpu ? _rt.GpuCheckExe : null,
                    GpuCheckPlainExe = gpu ? InstLayout.FindByName(inst, _rt.InstDir, InstLayout.GpuCheckPlain) : null,
                    PwshExe = _rt.PwshExe, PwshEnv = _env, FullgpuSelectsDgpu = dgpuExports, CompatEngine = _engineHasCompat,
                });
                gpuRes = pf2.Gpu;
                if (pf2.Precision != PrecisionMode.Unknown) _precision = pf2.Precision;
                foreach (var id in new[] { "gpu", "gpu.precision", "gpu.advice", "gpu.hybrid", "pwsh.policy" })
                {
                    var it = pf2.Get(id);
                    if (it != null) Say("    " + it);
                    // gpu.hybrid (review fix, finding 17) is decided once the workers are known: it passes when Windows' per-app
                    // preference already gives every worker folder's fullgpu.exe the high-performance GPU
                    if (it != null && it.Level == CheckLevel.Fail && id == "gpu.hybrid") { _hybridFail = it; continue; }
                    if (it != null && it.Level == CheckLevel.Fail) Refuse(it.Message);
                }
                if (_rt.BoundsDb != null)
                {
                    var b = PreflightChecks.EnsureBoundsDb(_root, _rt.BoundsDb);
                    Say("  bounds DB: " + b.Message);
                }
            }
            CheckCancel();

            // ---- Compatibility mode (DF64): which kernel set this bake runs. The engine chooses by the GPU itself; this is only what the log and the plan say.
            if (dry && _so.AssumeNoFp64)
            {
                if (!_engineHasCompat) Refuse(GpuCheck.NoFp64Plain + " (dry run with --assume-no-fp64; " + compatWhy + ")");
                _precision = PrecisionMode.Compat;
            }
            if (_o.ForceCompat) _precision = PrecisionMode.CompatForced;
            Say("  " + GpuCheck.PrecisionText(_precision, _engineHasCompat));

            // ---- GPU limits (X1 item 2): part sizing from the adapter's dedicated VRAM; nothing is passed on a 24 GB card
            {
                long? vram = null; string vsrc = null;
                if (gpuRes != null && gpuRes.DedicatedVideoMemoryBytes.HasValue) { vram = gpuRes.DedicatedVideoMemoryBytes; vsrc = "gpucheck: " + (gpuRes.DefaultAdapter ?? "default adapter"); }
                else if (_so.AssumeVramMB.HasValue && _so.AssumeVramMB.Value > 0) { vram = (long)_so.AssumeVramMB.Value * 1048576L; vsrc = dry ? "--assume-vram-mb (dry run)" : "--assume-vram-mb"; }
                // X2: what other programs hold right now (gpucheck: the system-wide counter + the OS budget); a dry run takes --assume-other-vram-mb
                long? other = null;
                if (gpuRes != null && gpuRes.DedicatedVideoMemoryBytes.HasValue)
                    other = GpuLimits.ComputeOtherUse(gpuRes.DedicatedVideoMemoryBytes, gpuRes.LiveBudgetBytes, gpuRes.LiveProcessUsageBytes, gpuRes.SystemDedicatedUsageBytes);
                else if (vsrc != null && _so.AssumeOtherVramUseMB.HasValue) other = (long)_so.AssumeOtherVramUseMB.Value * 1048576L;
                _limits = vsrc == null ? (gpuRes != null ? GpuLimits.Compute(null, null, 0) : GpuLimits.Unknown(null))
                                       : GpuLimits.Compute(vram, vsrc, _o.Quality == Quality.Custom ? _o.Photons : 0, other);
                if (vsrc != null)
                {
                    Say("  gpu memory: " + (vram.Value / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB (" + vsrc + ")"
                        + (other.HasValue ? "; in use by other programs now: " + (other.Value / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB" : ""));
                    foreach (var ln in _limits.Notes) Say("    " + ln);
                }
            }
            CheckCancel();

            // ---- startup recovery (section 9)
            CurrentRun curRun;
            var ak = CurrentRun.Inspect(_work, out curRun);
            if (ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell)
                Refuse("another bake is running on this H2EK (run " + (curRun == null ? "?" : curRun.RunTag) + "); wait for it or cancel it first");
            foreach (var it in StartupRecovery.Scan(_work))
            {
                switch (it.Kind)
                {
                    case RecoveryKind.InterruptedRun:
                        if (dry) Say("  recovery (would mark): " + it.Message);
                        else { StartupRecovery.MarkInterrupted(_work, it); Say("  recovery: " + it.Message + " -- marked INTERRUPTED"); }
                        break;
                    case RecoveryKind.StaleOwnLock:
                        if (dry) Say("  recovery (would release): " + it.Message);
                        else Say("  recovery: " + it.Message + (StartupRecovery.ReleaseStaleOwnLock(_work) ? " -- released" : " -- left (it changed)"));
                        break;
                    default:
                        _markers.Add(it);     // decided once the workers are known (review fix, finding 6: scoped to this BSP / its farms)
                        break;
                }
            }
            CheckCancel();

            // ---- map query: probe points / environments (Standard, Lightprobes)
            // (0.6.4: also run, in any mode, to read the scenario's structure_bsps when the BSP's tag may be in another folder)
            bool needQuery = BakePlanner.NeedsMapQuery(_o);
            if (needQuery || (_bspFromScenario && !dry))
            {
                if (dry)
                {
                    _points = _so.AssumeProbePoints ?? 0;
                    _pointsAssumed = true;
                }
                else
                {
                    var q = BakePlanner.MapQueryStep(_rt, _root, _o);
                    Say("  map query: " + q.CommandLine.Substring(0, Math.Min(160, q.CommandLine.Length)) + " ...");
                    _map = MapQuery.Run(q.FileName, q.Args, q.Cwd, _env);
                    if (!_map.Ok) Refuse("the map query failed: " + (_map.Error ?? _map.PointsError));
                    if (_bspFromScenario) ResolveBspFromScenario(_map);
                    if (!needQuery) _map = null;        // Lightmap Only: the query only located the BSP; the probe / lightmap facts stay unread as before
                }
                if (needQuery && !dry)
                {
                    _points = _map.Points ?? 0;
                    Say("  map: " + _points + " probe point(s); lightmap " + (_map.LtmpExists ? "present, " + (_map.Envs.HasValue ? _map.Envs + " lighting environment(s)" : "environments unknown (" + _map.EnvsError + ")") : "absent"));
                    if (_o.Mode == BakeMode.Probes)
                    {
                        if (_points == 0) Refuse("this BSP has no light probe points (lp_probes would exit 3): nothing to bake in Lightprobes mode");
                        if (!_map.LtmpExists) Refuse("this BSP has no lightmap yet: bake it first (Standard or Lightmap Only)");
                        if (_map.Envs != _points) Refuse("the lightmap has " + (_map.Envs.HasValue ? _map.Envs.ToString() : "?") + " lighting environment(s) but the BSP has " + _points
                                                         + " probe point(s): rebake the lightmap (Standard) first");
                    }
                }
            }
            if (_bspFromScenario && dry && !File.Exists(Path.Combine(_set.TagDir, _o.Bsp + ".scenario_structure_bsp")))
            {
                Say("  (dry run: a BSP tag that is not beside the scenario is looked up in the scenario's structure_bsps by the real run)");
                RefuseCrossFolderLayersFor(true);       // it is in another folder: AO / probes need the 0.6.5 drivers (the real run decides with the real probe count)
            }
            bool probes = BakePlanner.NeedsProbes(_o, _points);
            RefuseCrossFolderLayers();
            if (probes && !_host.ProbesEnabled)
                Refuse("this BSP has " + _points + " light probe point(s) and light probes are not released yet in this build (gated on T4, the engine_v373 re-validation: "
                       + _host.ProbesWhy + "): use Lightmap Only, which resets this BSP's " + _points + " probes to zero like `tool lightmaps` (CLI: --mode lightmap); dev: --dev-probes");
            if (probes)
            {
                // X1 item 4: the GPU probe pass holds ~281 points per BSP at its buffer cap; refuse HERE, before any bake, not at the end
                var pv = GpuLimits.CheckProbes(_points);
                if (pv.Refuse) Refuse(pv.Message);
                if (pv.Marginal) { _probeWarning = pv.Message; Say("  " + pv.Message); }
            }
            CheckCancel();

            // ---- workers (200-299; farm_w149 never)
            if (dry)
            {
                var taken = new List<int>();
                if (BakePlanner.NeedsLightmapWorker(_o))
                {
                    if (_o.Worker.HasValue) { _n = _o.Worker; _nNote = "--worker"; }
                    else { _n = WorkerPeek.Peek(_work, rel, _o.Bsp, false, null, out _nNote); }
                    taken.Add(_n.Value);
                }
                if (probes)
                {
                    if (_o.ProbeWorker.HasValue) { _m = _o.ProbeWorker; _mNote = "--probe-worker"; }
                    else { _m = WorkerPeek.Peek(_work, rel, _o.Bsp, true, taken, out _mNote); }
                }
            }
            else
            {
                var reg = new WorkerRegistry(_work);
                try
                {
                    if (BakePlanner.NeedsLightmapWorker(_o)) { _n = _o.Worker ?? reg.GetOrAllocate(rel, _o.Bsp, false); _nNote = _o.Worker.HasValue ? "--worker" : "registry"; }
                    if (probes) { _m = _o.ProbeWorker ?? reg.GetOrAllocate(rel, _o.Bsp, true); _mNote = _o.ProbeWorker.HasValue ? "--probe-worker" : "registry"; }
                }
                catch (SafetyException e) { Refuse(e.Message); }
            }
            if (_n.HasValue) Guards.AssertWorker(_n.Value);
            if (_m.HasValue) Guards.AssertWorker(_m.Value);
            if (_n.HasValue && _m.HasValue && _n.Value == _m.Value) Refuse("the probe worker must differ from the lightmap worker");

            // ---- unfinished installs / restores (review fixes, findings 1 + 6): a marker of THIS lightmap, or in a farm this
            // bake uses, refuses the bake (until Restore / --recover handles it, or the running bake that owns it ends); a
            // marker of another BSP only gets a note (its farm and its tags are not touched by this bake)
            foreach (var it in _markers)
            {
                bool mine = it.Concerns(_set) || (it.Worker > 0 && ((_n.HasValue && it.Worker == _n.Value) || (_m.HasValue && it.Worker == _m.Value)));
                if (mine && it.InUse) Refuse("an install of this lightmap is in progress in another bake (" + it.InUseWhy + "): wait for it to end");
                if (mine)
                    Refuse("an earlier lightmap install did not finish: " + it.Message + " -- undo it first (Restore previous lightmap, or --cli restore --h2ek \"" + _root + "\" --recover)");
                Say("  note: " + (it.InUse ? "(in use by a running bake) " : "") + it.Message + " -- another lightmap; this bake goes ahead");
            }

            // ---- R1 fix (V6 findings H1 / H2): a bake that will install refuses up front while a file it replaces is held open
            // (Sapien / Guerilla) -- the same exclusive-open test lmw_install makes before it changes anything. Before, a held
            // file failed the backup with a raw IOException, or the run baked for minutes and then failed at the install.
            if (!_o.StageOnly && !dry)
            {
                var names = _o.Mode == BakeMode.Probes ? new List<string> { _set.LtmpName } : _set.PresentNames().Where(n => !TagSet.IsRadiance(n)).ToList();
                var held = new List<string>();
                foreach (var nm in names)
                {
                    string why;
                    if (!SafeFs.ExclusiveOk(Path.Combine(_set.TagDir, nm), out why)) held.Add(nm + ": " + why);
                }
                if (held.Count > 0)
                    Refuse("the lightmap files are open in another program -- close Sapien / Guerilla, then bake again (nothing was changed): " + string.Join("; ", held));
            }

            // ---- hybrid GPU (review fix, finding 17): the per-app preference of exactly the fullgpu.exe copies this bake runs
            if (_hybridFail != null)
            {
                var exes = new[] { _n, _m }.Where(w => w.HasValue).Select(w => Path.Combine(BakePlanner.FarmDir(_work, w.Value), "fullgpu.exe")).ToList();
                var missing = exes.Where(e => !PreflightChecks.HighPerformancePreferenceSet(e)).ToList();
                if (missing.Count > 0)
                {
                    // Windows' Graphics settings can only add an exe that exists: put the exact file gpu_bake will copy there
                    // (its own Copy-Item -Force replaces it with the same bytes), so the user can register it
                    string src = InstLayout.Full(_rt.InstDir, BakePlanner.GpuBakeRel(_o, _host.Manifest).Replace("gpu_bake.ps1", "v041-fullgpu/fullgpu.exe"));
                    var made = new List<string>();
                    foreach (var e in missing.Where(e => !File.Exists(e)))
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(e)); SafeFs.CopyVerified(src, e); made.Add(e); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                    Refuse(_hybridFail.Message + " This bake runs: " + string.Join(" and ", missing) + " -- add " + (missing.Count == 1 ? "it" : "them")
                           + " there with 'High performance', then bake again" + (made.Count > 0 ? " (placed there now so Windows can find " + (made.Count == 1 ? "it" : "them") + ")" : "") + ".");
                }
                Say("  gpu.hybrid: Windows gives " + string.Join(" and ", exes) + " the high-performance GPU (per-app preference) -- ok");
            }

            // ---- Scenery Fix Dependencies (SF1): BEFORE the capture and the reuse gate -- gpu_bake pins the database's md5 into the
            // capture record and refuses a write-back if it changed since, so whatever this check adds must already be in the file
            // when the capture identity below is computed. Never blocks the bake.
            SceneryFixCheck(dry, rel);
            CheckCancel();

            // ---- capture reuse gate (section 7)
            _tagsNow = new Lazy<TagsFingerprint>(() => TagsFingerprint.Compute(_root, _set));
            var changed = new List<string>();
            int changedTotal = 0;
            var infoChanged = new List<string>();     // 0.6.6: changed since the capture but not bake-relevant (information only)
            int infoTotal = 0, identicalResaves = 0;
            string engineRel = BakePlanner.GpuBakeRel(_o, _host.Manifest);
            if (_o.ReuseCapture)
            {
                if (_toolMd5 == null) _toolMd5 = CaptureIdentity.Md5OrNull(Path.Combine(_root, "tool_fast.exe"));
                if (_n.HasValue)
                {
                    var v = CaptureRecord.Evaluate(BakePlanner.FarmDir(_work, _n.Value), CaptureIdentity.For(_o, _host.Manifest, _root, engineRel, false, _toolMd5),
                                                   rel, _o.Bsp, false, () => _tagsNow.Value, _o.ReuseUnrecorded);
                    if (!v.Allowed) Refuse("Reuse existing capture: " + v.Reason);
                    _reuseN = true; _reuseNNote = v.Describe();
                    if (v.TagsChanged) { changed.AddRange(v.ChangedTags); changedTotal = Math.Max(changedTotal, v.ChangedTagsTotal); }
                    infoChanged.AddRange(v.InfoTags); infoTotal = Math.Max(infoTotal, v.InfoTagsTotal); identicalResaves = Math.Max(identicalResaves, v.IdenticalResaves);
                }
                if (_m.HasValue)
                {
                    var v = CaptureRecord.Evaluate(BakePlanner.FarmDir(_work, _m.Value), CaptureIdentity.For(_o, _host.Manifest, _root, engineRel, true, _toolMd5),
                                                   rel, _o.Bsp, true, () => _tagsNow.Value, _o.ReuseUnrecorded);
                    if (v.Allowed)
                    {
                        _reuseM = true; _reuseMNote = v.Describe();
                        if (v.TagsChanged) { foreach (var c in v.ChangedTags) if (!changed.Contains(c)) changed.Add(c); changedTotal = Math.Max(changedTotal, v.ChangedTagsTotal); }
                        foreach (var c in v.InfoTags) if (!infoChanged.Contains(c)) infoChanged.Add(c);
                        infoTotal = Math.Max(infoTotal, v.InfoTagsTotal); identicalResaves = Math.Max(identicalResaves, v.IdenticalResaves);
                    }
                    else if (_o.Mode == BakeMode.Probes) Refuse("Reuse existing capture (probe worker): " + v.Reason);
                    else _reuseMNote = "a new probe capture (" + v.Reason + ")";
                }
                // 0.6.6: tags that changed but do not feed the lightmap, and re-saves with identical content, never ask -- they are said once
                if (identicalResaves > 0) Say("  " + identicalResaves + " tag(s) were re-saved since the capture with identical content (Sapien / Guerilla / Tool) -- not a change");
                if (infoTotal > 0)
                    Say("  " + infoTotal + " tag(s) changed since the capture but do not feed the lightmap (AI / scripts / sounds / bitmaps and models this map does not use ...) -- information only"
                        + " (first: " + string.Join("; ", infoChanged.Take(5)) + ")");
                if (changed.Count > 0)
                {
                    var pr = new Prompt
                    {
                        Kind = PromptKind.ChangedTags, Title = "Tags changed since the capture",
                        Message = changedTotal + " tag file(s) changed since the capture was made. Reusing it bakes the OLD geometry / materials for those. Reuse anyway?",
                        Details = changed,
                    };
                    if (dry) Say("  WARNING: reuse would ask for confirmation: " + changedTotal + " tag(s) changed since the capture (first: " + string.Join("; ", changed.Take(5)) + ")");
                    else if (!_o.AcceptChangedTags && !Ask(pr))
                        Refuse("reuse declined: " + changedTotal + " tag(s) changed since the capture (" + string.Join("; ", changed.Take(10)) + (changedTotal > 10 ? "; ..." : "") + ") -- bake with a new capture, or confirm (CLI: --yes)");
                    else Say("  reuse confirmed although " + changedTotal + " tag(s) changed since the capture");
                }
            }
            CheckCancel();
        }

        /// <summary>
        /// SF1: before a bake that uses the scenery fix, scan the models this scenario's scenery places and add the ones the Scenery
        /// Fix Dependencies database does not know yet (scan_scenery_bounds.py --scenario --apply --json). One log line per added
        /// model; conflicts are a warning; a scanner failure is a warning and the bake continues with the database as it is.
        /// A dry run starts nothing and writes nothing, so it only says that a real run scans.
        /// </summary>
        private void SceneryFixCheck(bool dry, string rel)
        {
            if (!_o.Fixes) return;
            if (dry) { Say("  scenery fix dependencies: not scanned in a dry run (a real run adds this scenario's missing scenery models to " + SceneryFixDeps.DbName + ")"); return; }
            if (_rt == null || _rt.ScanSceneryPy == null || !File.Exists(_rt.ScanSceneryPy))
            {
                Say("  scenery fix dependencies: this payload has no scanner -- the existing database is used as it is");
                return;
            }
            string json = Path.Combine(ShellPaths.StandaloneOf(_work), "scenery_fix_last_scan.json");
            Say("  scenery fix dependencies: scanning the scenery of " + rel + " ...");
            SceneryScanResult r;
            try { r = SceneryFixDeps.Run(_rt.PyExe, _rt.ScanSceneryPy, _root, rel, _rt.Work, _env, SceneryFixDeps.ScenarioScanTimeoutMs, json); }
            catch (Exception e) when (!(e is OperationCanceledException)) { Say("  WARNING: scenery fix dependencies: the scan failed (" + e.GetType().Name + ": " + e.Message + ") -- continuing with the existing database"); return; }
            if (!r.Ran) { Say("  WARNING: scenery fix dependencies: " + r.Error + " -- continuing with the existing database"); return; }
            if (r.Seeding)
                Say("  scenery fix dependencies: " + SceneryFixDeps.DbName + " had no per-model entries yet -- built it from the " + r.Models.ToString("N0", CultureInfo.InvariantCulture)
                    + " render_models of the tags folder" + (r.OtherAdded > 0 ? " (" + r.OtherAdded.ToString("N0", CultureInfo.InvariantCulture) + " models outside this scenario were added too)" : "") + ", continuing on!");
            foreach (var m in r.Added) Say(SceneryFixDeps.AddedLine(m));
            foreach (var m in r.Updated) Say(SceneryFixDeps.UpdatedLine(m));
            foreach (var c in r.Conflicts) Say(SceneryFixDeps.ConflictLine(c));
            if (r.LinesToAdd > 0 && !r.Applied)
                Say("  WARNING: scenery fix dependencies: " + r.LinesToAdd + " line(s) were missing but the scanner did not write them -- continuing with the existing database");
            string md5 = CaptureIdentity.Md5OrNull(SceneryFixDeps.DbPath(_root));
            Say(string.Format(CultureInfo.InvariantCulture, "  scenery fix dependencies: {0} model(s) scanned, {1} of this scenario's added, {2} updated, {3} conflict(s); {4} {5}",
                              r.Models, r.Added.Count, r.Updated.Count, r.Conflicts.Count, SceneryFixDeps.DbName, md5 == null ? "(not present)" : "md5 " + md5.Substring(0, 8)));
        }

        /// <summary>Non-ASCII / '{' '}' '[' ']' in the H2EK path (the same rules preflight applies; checked even in a dry run).</summary>
        public static string PathTextProblem(string root)
        {
            string work = ShellPaths.WorkOf(root);
            foreach (var p in new[] { root, work })
            {
                if (p.Any(c => c > 0x7E || c < 0x20)) return "the H2EK path has non-ASCII characters: the bake scripts would garble it -- move the H2EK to a plain-ASCII folder (" + p + ")";
                var f = p.Where(c => PreflightChecks.ForbiddenPathChars.Contains(c)).Distinct().ToArray();
                if (f.Length > 0) return "the H2EK path contains " + string.Join(" ", f.Select(c => "'" + c + "'")) + ", which the bake script cannot handle -- rename the folder (" + p + ")";
            }
            return null;
        }

        private string PredictRunDir()
        {
            var now = _so.Now ?? DateTime.Now;
            return Path.Combine(RunFolder.RunsDir(_work), RunFolder.TagFor(now, _o.ScenarioRel, _o.Bsp));      // W2: the same name RunFolder.Create makes
        }

        private BakePlan BuildPlan(string runDir, bool predicted)
        {
            return BakePlanner.Build(new PlanInput
            {
                O = _o, AppVersion = _host.AppVersion, PayloadSource = _host.Source.Describe, Manifest = _host.Manifest, Rt = _rt, Env = _env,
                RunDir = runDir, RunDirPredicted = predicted, Worker = _n, ProbeWorker = _m, WorkerNote = _nNote, ProbeWorkerNote = _mNote,
                ProbePoints = _points, ProbePointsAssumed = _pointsAssumed, ReuseLightmap = _reuseN, ReuseProbe = _reuseM,
                Limits = _limits, ProbeWarning = _probeWarning, Precision = _precision, EngineHasCompat = _engineHasCompat,
                LightmapReuseNote = _o.ReuseCapture ? _reuseNNote : null, ProbeReuseNote = _reuseMNote,
            });
        }

        // ================================================================== run

        /// <summary>
        /// Review fix (finding 11): EVERYTHING after BakeRun.Start (which writes current_run.json naming this shell) runs inside
        /// the try, so run.Finish always runs -- an exception while planning can no longer leave status.json PREPARING and
        /// current_run.json naming this live shell (which refused every later bake in this window).
        /// </summary>
        private int RunPlan()
        {
            var run = BakeRun.Start(new RunSpec { Work = _work, ScenarioRel = _o.ScenarioRel, Bsp = _o.Bsp, Worker = _n ?? _m ?? 0, Now = _so.Now, AppVersion = _host.AppVersion });
            lock (_lock) Run = run;
            BakePlan plan = null;
            var started = DateTime.UtcNow;
            RunState final = RunState.Failed;
            string reason = null;
            try
            {
                run.Line += l => { var h = Line; if (h != null) h(l); };
                // Compatibility mode (DF64): fullgpu's own line ("Compatibility mode (no FP64): ..." / "Compatibility mode: off (native FP64 kernels; ...)"), which
                // gpu_bake copies into every phase summary -- what the engine really ran is what the run summary reports
                run.Line += l => { if (l != null && l.Kind != LineKind.Shell && l.Text != null && l.Text.IndexOf("Compatibility mode", StringComparison.Ordinal) >= 0) _engineCompatLine = l.Text.Trim(); };
                // 0.6.4: the engine's one-line write-back fallbacks (direct tag writer -> tool write-back) get a visible warning and a run-summary sentence
                run.Line += l => { if (l != null && l.Kind != LineKind.Shell && l.Text != null) NoteWriteBackFallback(run, l.Text); };
                // 0.6.6: a tolerated stage deviation (lmw_validate WARN, texels filled under lit UVs) is a visible warning too -- the stage installs
                run.Line += l => { if (l != null && l.Kind != LineKind.Shell && l.Text != null) NoteStageWarning(run, l.Text); };
                run.StatusChanged += OnStatus;
                bool cancelledEarly;
                lock (_lock) cancelledEarly = _cancel;
                string[] pre;
                lock (_lock) pre = _pre.ToArray();
                foreach (var l in pre) run.Shell("pre| " + l);
                plan = BuildPlan(run.RunDir, false);
                Plan = plan;
                var hp = Planned; if (hp != null) { try { hp(plan); } catch { } }
                foreach (var l in plan.ToText(false, true).Split('\n')) run.Shell("plan| " + l.TrimEnd('\r'));
                run.Status.Extra["options"] = new JObj().Add("quality", _o.QualityToken).Add("bspRel", _o.BspRel).Add("photons", _o.Quality == Quality.Custom ? (object)_o.Photons : null)
                    .Add("rays", _o.Quality == Quality.Custom ? (object)(long)_o.Rays : null).Add("mode", _o.ModeToken).Add("ao", _o.Ao)
                    .Add("aoStrength", _o.AoStrength).Add("aoExponent", _o.AoExponent).Add("reuseCapture", _o.ReuseCapture).Add("install", !_o.StageOnly)
                    .Add("guarded", plan.CustomGuarded.HasValue ? (object)plan.CustomGuarded.Value : null);
                run.Status.Extra["plan"] = new JObj().Add("probePath", plan.ProbePath).Add("probeWorker", plan.ProbeWorker.HasValue ? (object)(long)plan.ProbeWorker.Value : null)
                    .Add("finalStage", plan.FinalStage).Add("steps", plan.Steps.Select(s => (object)s.Id).ToList());
                run.SetStep(StepId.Preflight);
                if (cancelledEarly) run.RequestCancel();
                StartCancelWatch(run);
                try
                {
                    var hdr = RunHeader.Collect(_rt.PwshExe, _rt.PyExe, _env, _work);
                    foreach (var l in hdr.Lines()) run.Shell(l);
                    CheckRuntimeHeader(run, hdr);
                    run.ThrowIfCancelled();
                    var lk = new GpuLock(GpuLock.PathFor(_work), run.RunTag, _o.ScenarioRel);
                    var acq = run.AcquireGpuLock(lk, _so.LockWait, _so.LockPoll, _cts.Token);
                    if (acq == LockAcquire.Cancelled) throw new OperationCanceledException("cancelled while waiting for the GPU lock");
                    if (acq == LockAcquire.TimedOut) throw new StepFailedException("another bake held the GPU lock for more than " + _so.LockWait.TotalMinutes + " min -- gave up");
                    foreach (var s in plan.Steps)
                    {
                        if (s.Id == StepIds.MapQuery) { run.Shell("step " + s.Index + " " + s.Id + ": done before planning (" + _points + " probe point(s))"); continue; }
                        // review fix (finding 10): once the live tags were written only Verify follows -- it runs even when a
                        // cancel arrived during the (deferred) tag write, so the written set is always verified
                        if (_installed && s.Id == StepIds.Verify)
                        { if (run.CancelRequested) run.Shell("the cancel arrived during the tag write: the install completed, so it is verified before the run ends"); }
                        else run.ThrowIfCancelled();
                        run.SetStep(s.UiStep);
                        run.Shell("---- step " + s.Index + "/" + plan.Steps.Count + " " + s.Id + ": " + s.Title);
                        if (!ExecuteStep(plan, s)) break;      // false = the plan ended early (installed without the layer)
                    }
                    final = RunState.Done;
                    if (_installedWithoutLayer) reason = "installed WITHOUT " + LayerText() + " (the map fell back to the tool writer)";
                    else if (_productionWriteBack) reason = "installed by " + _productionWriteBackWhy;
                    else if (!plan.Install) reason = "stage only -- nothing was written to the tags; the final stage is " + plan.FinalStage;
                    if (_installed && run.CancelRequested) reason = (reason != null ? reason + "; " : "") + "installed and verified -- the cancel arrived during the tag write and was applied after it";
                }
                catch (OperationCanceledException e) { final = RunState.Cancelled; reason = e.Message + (_installed ? " (AFTER the install: the new lightmap is in the tags)" : ""); Aftermath(plan, true); }
                catch (StepFailedException e) { final = RunState.Failed; reason = e.Message; Aftermath(plan, false); }
                catch (SafetyException e) { final = RunState.Failed; reason = "refused by a safety rule: " + e.Message; Aftermath(plan, false); }
                catch (Exception e) { final = RunState.Failed; reason = e.GetType().Name + ": " + e.Message; run.Shell(e.ToString()); Aftermath(plan, false); }
                finally { StopCancelWatch(); }
            }
            catch (Exception e)
            {
                // the run's own setup failed (planning, logging): still a final state, a released lock, a cleared current_run.json
                final = RunState.Failed;
                reason = "the run could not be set up: " + e.GetType().Name + ": " + e.Message;
                try { run.Shell(e.ToString()); } catch { }
                StopCancelWatch();
            }
            CloseStep();
            // 0.6.4: a write-back fallback is part of the run summary (the bake still ends DONE: the lightmap is written, by the other path)
            try
            {
                var fbs = WriteBackFallbacks;
                if (fbs.Count > 0)
                {
                    reason = (reason != null ? reason + "; " : "") + string.Join("; ", fbs);
                    lock (run.Status) run.Status.Extra["writeBackFallback"] = fbs.Cast<object>().ToList();
                }
                var sws = StageWarnings;
                if (sws.Count > 0)
                {
                    reason = (reason != null ? reason + "; " : "") + string.Join("; ", sws);
                    lock (run.Status) run.Status.Extra["stageWarnings"] = sws.Cast<object>().ToList();
                }
            }
            catch { }
            Reason = reason;
            try { lock (run.Status) run.Status.Extra["installed"] = _installed; } catch { }
            // run summary: the kernel set this bake ran (the plan's decision, plus the engine's own line when it printed one)
            try
            {
                if (_precision == PrecisionMode.Unknown && _engineCompatLine != null)
                    _precision = _engineCompatLine.IndexOf("(no FP64)", StringComparison.Ordinal) >= 0 ? PrecisionMode.Compat
                               : _engineCompatLine.IndexOf(": off", StringComparison.Ordinal) >= 0 ? PrecisionMode.Full : PrecisionMode.Unknown;
                PrecisionSummary = GpuCheck.PrecisionText(_precision, _engineHasCompat) + (_engineCompatLine != null ? "   [engine: " + _engineCompatLine + "]" : "");
                run.Shell("precision| " + PrecisionSummary);
                lock (run.Status) run.Status.Extra["precision"] = new JObj().Add("mode", plan == null ? null : plan.PrecisionModeToken).Add("text", GpuCheck.PrecisionText(_precision, _engineHasCompat))
                    .Add("forced", _o.ForceCompat).Add("engineHasCompatMode", _engineHasCompat).Add("engineLine", _engineCompatLine);
            }
            catch { }
            try
            {
                History.Append(_work, new HistoryEntry
                {
                    RunTag = run.RunTag, Scenario = _o.ScenarioRel, Bsp = _o.Bsp, Quality = _o.QualityToken, Mode = _o.ModeToken,
                    Photons = _o.Quality == Quality.Custom ? (long?)_o.Photons : null, Mc = _o.Quality == Quality.Custom ? (int?)_o.Rays : null,
                    Ao = _o.Ao, Reuse = _o.ReuseCapture, Install = plan != null && plan.Install, State = final.ToString().ToUpperInvariant(), StartedUtc = started,
                    Seconds = (DateTime.UtcNow - started).TotalSeconds, Steps = new Dictionary<string, double>(_stepSeconds),
                });
            }
            catch (Exception e) { run.Shell("history.json write failed: " + e.Message); }
            return run.Finish(final, reason);
        }

        /// <summary>
        /// Review fix (finding 20): the children must report the pinned runtime -- pwsh 7.6.6, `& py` = the bundle's py.exe,
        /// sys.executable inside the bundle, numpy 2.2.6 -- or the run stops (exit 3) BEFORE the GPU lock. Only the dev runtime
        /// overrides (--rt-pwsh / --rt-py, which also skip the runtime md5 check) may go on, loudly.
        /// </summary>
        private void CheckRuntimeHeader(BakeRun run, RunHeaderInfo hdr)
        {
            var mism = hdr.Mismatches(_rt.PyExe, _rt.PyDir);
            bool dev = _host.Config.RtPwshDir != null || _host.Config.RtPyDir != null;
            if (dev) run.Shell("*** DEV RUNTIME OVERRIDE (--rt-pwsh / --rt-py): these runtimes were NOT md5-verified" + (mism.Count > 0 ? "; NOT THE PINNED RUNTIME: " + string.Join("; ", mism) : " (versions match the pin)"));
            else if (mism.Count > 0) throw new StepFailedException("the bake would not run on the pinned runtime: " + string.Join("; ", mism) + " -- Repair runtime, then bake again");
            else run.Shell("    runtime = the pinned bundle (pwsh " + RunHeader.PinnedPwsh + ", bundle py, numpy " + RunHeader.PinnedNumpy + ")");
        }

        private string LayerText()
        {
            bool probes = BakePlanner.NeedsProbes(_o, _points);
            return _o.Ao && probes ? "AO and light probes" : _o.Ao ? "AO" : "light probes";
        }

        /// <summary>The live tags were written by this run: recorded in status.json; the run's _TAGBAK_ui backup is marked
        /// installedAfter (Restore previous lightmap lists it as the state before this install).</summary>
        private void MarkInstalled()
        {
            if (_installed) return;
            _installed = true;
            lock (Run.Status) Run.Status.Extra["installed"] = true;
            if (_backup != null && TagBackup.MarkInstalledAfter(_backup.Dir, DateTime.Now)) Run.Shell("backup " + _backup.Dir + " marked installedAfter");
        }

        private void OnStatus(RunStatus st)
        {
            lock (_lock)
            {
                if (st.Step != _curStep)
                {
                    CloseStepLocked();
                    _curStep = st.Step;
                    _curStepStart = DateTime.UtcNow;
                }
            }
            var h = StatusChanged; if (h != null) { try { h(st); } catch { } }
        }

        private void CloseStep() { lock (_lock) CloseStepLocked(); }
        private void CloseStepLocked()
        {
            if (_curStep == null) return;
            double s = (DateTime.UtcNow - _curStepStart).TotalSeconds;
            double v;
            _stepSeconds[_curStep] = (_stepSeconds.TryGetValue(_curStep, out v) ? v : 0) + s;
            _curStep = null;
        }

        private void StartCancelWatch(BakeRun run)
        {
            string req = Path.Combine(run.RunDir, CancelRequestFile);
            string extra = _so.CancelFile;
            int fired = 0;
            _watch = new Timer(_ =>
            {
                try
                {
                    if (Volatile.Read(ref fired) != 0) return;
                    if (File.Exists(req) || (extra != null && File.Exists(extra)))
                    {
                        if (Interlocked.Exchange(ref fired, 1) != 0) return;
                        run.Shell("a cancel was requested from outside this window (" + (File.Exists(req) ? req : extra) + ")");
                        RequestCancel();
                    }
                }
                catch { }
            }, null, 500, 1000);
        }

        private void StopCancelWatch() { var w = _watch; _watch = null; if (w != null) w.Dispose(); }

        // ================================================================== steps

        private ProcOutcome RunStepProcess(PlanStep s, Action<string> onLine = null)
        {
            Action<ParsedLine> h = null;
            if (onLine != null) { h = l => { if (l.Kind != LineKind.Shell) onLine(l.Text); }; Run.Line += h; }
            LogTail tail = null;
            Timer tt = null;
            var sideLines = new List<string>();
            try
            {
                if (s.SideLog != null)
                {
                    tail = new LogTail(s.SideLog);
                    int busy = 0;
                    tt = new Timer(_ =>
                    {
                        if (Interlocked.Exchange(ref busy, 1) != 0) return;
                        try { foreach (var l in tail.ReadNewLines()) { lock (sideLines) sideLines.Add(l); Run.Shell("    side| " + l); } }
                        catch { }
                        finally { Volatile.Write(ref busy, 0); }
                    }, null, 1000, 1000);
                }
                var oc = Run.RunProcess(new ProcSpec
                {
                    Name = s.Id, FileName = s.FileName, Args = s.Args, Cwd = s.Cwd, Env = s.EnvFor(_env), TagWrite = s.TagWrite,
                    ProbeWorker = s.ProbeWorker, TimeoutMs = s.TimeoutMs,
                });
                if (tt != null) { tt.Dispose(); tt = null; try { foreach (var l in tail.ReadNewLines()) { lock (sideLines) sideLines.Add(l); Run.Shell("    side| " + l); } } catch { } }
                if (s.SideLog != null) lock (sideLines) { _sideSawPhase2 = sideLines.Any(l => l.StartsWith("[2]", StringComparison.Ordinal) || l.StartsWith("[3]", StringComparison.Ordinal)); _sideDeviceLoss = DeviceLoss.AnyDeviceLoss(sideLines); }
                return oc;
            }
            finally
            {
                if (tt != null) tt.Dispose();
                if (h != null) Run.Line -= h;
            }
        }

        private bool _sideSawPhase2;

        private bool ExecuteStep(BakePlan plan, PlanStep s)
        {
            switch (s.Id)
            {
                case StepIds.Backup: DoBackup(s); return true;
                case StepIds.GpuBake: return DoGpuBake(plan, s);
                case StepIds.StageReady: return DoStageReady(plan, s);
                case StepIds.StageCheck: DoStageCheck(s); return true;
                case StepIds.StageOrToolFallback: return DoStageOrToolFallback(plan, s);
                case StepIds.AoTrace: case StepIds.AoApply: case StepIds.AoStage: DoSimple(s, "gpu_ao " + s.Args[1]); return true;
                case StepIds.CopyFinalStage: DoCopyFinalStage(s); return true;
                case StepIds.CopyLiveLtmp: DoCopyLiveLtmp(s); return true;
                case StepIds.ProbeBake: DoProbeBake(s); return true;
                case StepIds.ShMerge: case StepIds.ShSingle: DoShStage(s); return true;
                case StepIds.CheckLiveLtmp: DoCheckLiveLtmp(s); return true;
                case StepIds.AssembleCheckSet: DoAssembleCheckSet(s); return true;
                case StepIds.ValidateFinal: DoSimple(s, "lmw_validate --check-set (" + s.Args[2] + "; nothing is installed, the stage is kept)"); return true;
                case StepIds.Install: DoInstall(plan, plan.InstallStage); return true;
                case StepIds.Verify: DoVerify(plan); return true;
                default: throw new StepFailedException("unknown plan step " + s.Id);
            }
        }

        private void DoBackup(PlanStep s)
        {
            var kind = s.Params.Str("kind") == "LtmpOnly" ? BackupKind.LtmpOnly : BackupKind.LightmapSet;
            try { _backup = TagBackup.Create(_work, _set, kind, _so.Now ?? DateTime.Now); }
            catch (IOException e) when (IsSharingViolation(e))
            {
                // R1 fix (V6 finding H1): a set file opened by another program between the preflight check and the backup gave a
                // raw IOException; say what it means (nothing was changed: the backup reads only)
                throw new StepFailedException("the lightmap files are open in another program (close Sapien / Guerilla, then bake again; nothing was changed): " + e.Message);
            }
            Run.Status.BackupDir = _backup.Dir;
            Run.SetStep(StepId.Backup);
            Run.Shell("backed up " + _backup.Files.Count + " file(s) to " + _backup.Dir + (_backup.FirstBake ? " (first bake of this BSP: the .scenario is included)" : "")
                      + (_backup.RadianceMd5.Count > 0 ? "; " + _backup.RadianceMd5.Count + " radiance bitmap md5(s) recorded" : ""));
        }

        /// <summary>The planned gpu_bake: always stage-only since U3 (the shell installs). false = the plan ended here (the
        /// production -WriteBack argv ran instead and was verified: see DirectWriterCannotRun).</summary>
        private bool DoGpuBake(BakePlan plan, PlanStep s)
        {
            if (s.FreshCapture)
                foreach (var m in CaptureRecord.SupersedeExisting(s.CaptureOut, DateTime.Now)) Run.Shell("the earlier capture record was renamed aside: " + m);
            var oc = RunStepProcess(s);
            if (oc.Cancelled) throw new OperationCanceledException("cancelled during gpu_bake");
            bool captured = s.FreshCapture && (oc.Ok || oc.Parser.PhasesSeen.Any(ph => ph != "1"));
            if (captured) WriteCaptureRecord(s, false);
            if (!oc.Ok)
            {
                if (plan.Install && s.Params.Has("shellInstalls") && DirectWriterCannotRun(oc))
                {
                    // U3: production's -WriteBack argv would have switched to the tool write-back at this pre-flight (gpu_bake
                    // "$WriteBack -and -not $NoAutoFallback -> -WriteBackMode tool"); stage-only it throws instead. Nothing ran yet.
                    Run.Shell("*** gpu_bake refused the stage-only bake before any phase -- the direct tag writer cannot run (" + (oc.FailureReason ?? "?")
                              + "); with -WriteBack production switches to the tool write-back there, so the production argv runs now");
                    RunProductionWriteBack(plan, !s.FreshCapture, s, "the direct tag writer cannot run: the production -WriteBack argv",
                                           "the production -WriteBack argv (the direct tag writer cannot run: " + (oc.FailureReason ?? "?") + ")", false);
                    return false;
                }
                throw FailedStep("the bake stopped (exit " + ProcessRunner.FormatExit(oc.ExitCode) + "): " + (oc.FailureReason ?? "?"), oc);
            }
            return true;
        }

        /// <summary>U3: gpu_bake.ps1's own pre-flight refusal "-WriteBackMode direct cannot run: ..." (a writer file, the skeleton
        /// DLL or numpy missing), thrown before any phase banner -- with -WriteBack production falls back to tool mode there.</summary>
        public static bool DirectWriterCannotRun(ProcOutcome oc)
        {
            const string sig = "-WriteBackMode direct cannot run";
            if (oc == null || oc.Ok || oc.Cancelled || oc.TimedOut || oc.Parser == null) return false;
            if (oc.Parser.PhasesSeen.Count > 0) return false;          // a phase started: not the pre-flight
            return (oc.FailureReason ?? "").Contains(sig) || oc.Parser.ErrorLines.Any(l => l.Contains(sig));
        }

        /// <summary>
        /// U3: Lightmap Only / Standard without probes, with install. The stage-only bake's 'written' stage (this run's) is the stage
        /// gpu_bake's [4b] would have installed: the shell installs it next (the same lmw_install command, with Retry). Without one,
        /// the production tool write-back fallback runs: gpu_bake -WriteBack with -SkipCapture on this capture ([4a] again, then the
        /// tool's [4], exactly as gpu_bake -WriteBack falls back after a [4a] failure). Returns false then (the plan ends after its
        /// Verify).
        /// </summary>
        private bool DoStageOrToolFallback(BakePlan plan, PlanStep s)
        {
            string rp = s.Params.Str("report");
            string why = StageProblem(rp, null, Run.Status.StartedUtc) ?? ValidateProblem(rp, Run.Status.StartedUtc);
            if (why == null) { Run.Shell("the direct writer's stage is ready: " + Path.GetDirectoryName(rp) + " -- the shell installs it (the lmw_install command of gpu_bake's [4b], with Retry)"); return true; }
            Run.Shell("*** " + why + " -- the direct tag writer made no stage: the production tool write-back fallback runs (gpu_bake -WriteBack -SkipCapture on this capture)");
            // 0.6.4: the same fallback, as a visible warning (log + run summary), whoever noticed it
            NoteWriteBackFallback(Run, "direct tag writer FAILED -- falling back to the tool write-back: " + why);
            RunProductionWriteBack(plan, true, null, "the production tool write-back fallback", "the production tool write-back fallback (the direct tag writer made no stage: " + why + ")", false);
            return false;
        }

        /// <summary>
        /// The production -WriteBack argv of this bake on worker N (gpu_bake installs itself: [4b] the direct writer, or the tool's
        /// write-back [4] when [4a] fails), then Verify. Used when the direct writer made no stage (U3, the AO / probe "install
        /// without the layer" offer) or cannot run at all. Custom: GpuBakeArgs carries -Quality custom -Photons -MCSamples, so the
        /// tool write-back goes through osoy_launch --quality-row P MC (gpu_bake P2, Start-OsoyTool "writeback").
        /// </summary>
        /// <param name="resultWhy">the run's result text ("installed by ..."); null when the layer text says it (withoutLayer)</param>
        private void RunProductionWriteBack(BakePlan plan, bool skipCapture, PlanStep captureFrom, string headline, string resultWhy, bool withoutLayer)
        {
            var args = BakePlanner.GpuBakeArgs(_o, plan.Worker.Value, true, skipCapture, plan.Limits);
            var spec = PwshInvocation.GpuBake(WithEngine(plan.Engine), _env, args, StepIds.GpuBakeFallback);
            var fs = new PlanStep { Id = StepIds.GpuBakeFallback, UiStep = StepId.Install, Kind = PlanStepKind.Process, FileName = spec.FileName, Cwd = spec.Cwd };
            fs.Args.AddRange(spec.Args);
            BakePlanner.ApplyCompatEnv(_o, fs);      // --force-compat: the tool write-back fallback re-runs gpu_bake (a fresh capture), so it gets the variable too
            bool fresh =!skipCapture && captureFrom != null && captureFrom.CaptureWorker.HasValue;
            if (fresh) { fs.CaptureWorker = captureFrom.CaptureWorker; fs.CaptureOut = captureFrom.CaptureOut; fs.FreshCapture = true; }
            string marker = Path.Combine(plan.Out, StartupRecovery.MarkerName), bak = Path.Combine(plan.Out, "tag_backup_before_writeback");
            plan.Marker = marker;      // for the aftermath; status.json records it once the write-back starts (finding 4)
            _productionWriteBack = true;
            _productionWriteBackWhy = resultWhy ?? headline;
            Run.SetStep(fresh ? StepId.Capture : StepId.Install);
            Run.Shell("---- " + headline + ": " + fs.CommandLine);
            if (fresh)
                foreach (var m in CaptureRecord.SupersedeExisting(fs.CaptureOut, DateTime.Now)) Run.Shell("the earlier capture record was renamed aside: " + m);
            var oc = RunWriteBackProcess(fs, marker, bak);
            if (InstallRan(oc)) MarkInstalled();
            if (fresh && (oc.Ok || oc.Parser.PhasesSeen.Any(ph => ph != "1"))) WriteCaptureRecord(fs, false);
            if (oc.Cancelled && !_installed) throw new OperationCanceledException("cancelled during the install");
            if (!oc.Ok && !(oc.Cancelled && _installed)) throw FailedStep("the production write-back stopped (exit " + ProcessRunner.FormatExit(oc.ExitCode) + "): " + (oc.FailureReason ?? "?"), oc);
            if (oc.Cancelled) Run.Shell("the cancel was applied after the tag write: the lightmap was installed; verifying it before the run ends");
            if (withoutLayer) _installedWithoutLayer = true;
            Run.SetStep(StepId.Verify);
            DoVerify(plan);
        }

        /// <summary>
        /// Review fix (finding 4): a gpu_bake -WriteBack run records its install backup / marker in status.json only once an
        /// install actually STARTS -- the [4b] (direct writer) or [4] (tool write-back) banner -- never at the start of the bake:
        /// OUT\tag_backup_before_writeback is shared by every bake of the worker, so recording it early let "Undo last install"
        /// pick a run that never installed and restore an older state.
        /// </summary>
        private ProcOutcome RunWriteBackProcess(PlanStep s, string marker, string backupDir)
        {
            Action<ParsedLine> h = l =>
            {
                if (l.Kind != LineKind.Phase || !ProgressParser.IsTagWritePhase(l.PhaseId)) return;
                lock (Run.Status) { Run.Status.Marker = marker; Run.Status.InstallBackupDir = backupDir; }
            };
            Run.Line += h;
            try { return RunStepProcess(s); }
            finally { Run.Line -= h; }
        }

        /// <summary>The tag write of a gpu_bake -WriteBack process completed: its [4b] / [4] phase ran and either the process
        /// ended normally, or a cancel deferred through the write was applied at the NEXT banner (so the write had ended).
        /// Nothing after [4b] / [4] writes the tags.</summary>
        private static bool InstallRan(ProcOutcome oc)
        {
            var ph = oc.Parser.PhasesSeen;
            int i = ph.FindLastIndex(p => ProgressParser.IsTagWritePhase(p));
            if (i < 0 || oc.TimedOut) return false;
            if (oc.ExitCode == 0 && !oc.Cancelled) return true;
            return oc.Cancelled && i < ph.Count - 1;
        }

        private void WriteCaptureRecord(PlanStep s, bool skyHi)
        {
            try
            {
                var id = CaptureIdentity.For(_o, _host.Manifest, _root, BakePlanner.GpuBakeRel(_o, _host.Manifest), skyHi, _toolMd5 ?? CaptureIdentity.Md5OrNull(Path.Combine(_root, "tool_fast.exe")));
                var fp = TagsFingerprint.ComputeWithHashes(_root, _set, _o.ScenarioRel, _o.Bsp);     // 0.6.6: + an md5 on the bake-relevant files, so an identical re-save is not "changed"
                string why = CaptureRecord.Write(s.CaptureOut, id, _o.ScenarioRel, _o.Bsp, s.CaptureWorker.Value, Run.RunTag, fp, DateTime.Now);
                Run.Shell(why ?? "capture record written: " + CaptureRecord.PathIn(s.CaptureOut) + " (" + id.Describe() + ", " + fp.Count + " tag files fingerprinted, " + fp.HashedCount + " with a content hash)");
            }
            catch (Exception e) { Run.Shell("capture record NOT written (" + e.Message + ") -- this capture will not be offered for reuse"); }
        }

        private void DoSimple(PlanStep s, string what)
        {
            var oc = RunStepProcess(s);
            if (oc.Cancelled) throw new OperationCanceledException("cancelled during " + what);
            if (!oc.Ok) throw FailedStep(what + " failed (exit " + ProcessRunner.FormatExit(oc.ExitCode) + "): " + (oc.FailureReason ?? "?"), oc,
                                         s.Id == StepIds.AoTrace && s.Args.IndexOf("--farm") >= 0 && s.Args.IndexOf("--farm") + 1 < s.Args.Count
                                             ? Path.Combine(s.Args[s.Args.IndexOf("--farm") + 1], "ao", "gpu_ao_trace_driver.log") : null);
        }

        /// <summary>X2: a failed step's exception. When the engine reported a lost GPU device (a TDR: a line on either stream, in the step's
        /// side log, or in a driver script's own log) the plain explanation leads the text; otherwise the text is exactly what it was.</summary>
        private StepFailedException FailedStep(string technical, ProcOutcome oc, string extraLog = null)
        {
            bool lost = (oc != null && oc.Parser != null && oc.Parser.DeviceLost) || _sideDeviceLoss || DeviceLoss.FileHasDeviceLoss(extraLog);
            if (lost) Run.Shell("*** the engine reported a lost GPU device (TDR): " + DeviceLoss.Explanation);
            return new StepFailedException(DeviceLoss.Compose(technical, lost));
        }

        private bool _sideDeviceLoss;

        /// <summary>OUT\skeleton + a 'written' tagw_report.json; otherwise the map fell back to the tool writer.</summary>
        private bool DoStageReady(BakePlan plan, PlanStep s)
        {
            string why = StageProblem(s.Params.Str("report"), s.Params.Str("skeleton"), Run.Status.StartedUtc);
            if (why == null) { Run.Shell("the direct writer's stage is ready"); return true; }
            // review fix (finding 9): name EVERY layer the fallback drops -- on a probe BSP the production write-back also resets
            // the BSP's light probes to zero (as `tool lightmaps` does), not just AO
            bool dropsProbes = plan.ProbePath, dropsAo = _o.Ao;
            string layer = LayerText();
            Run.Shell("*** " + why + " -- this map fell back to the tool writer; " + layer + " need(s) the direct writer");
            if (!plan.Install) throw new StepFailedException(why + ": " + layer + " need(s) the direct writer's stage (this map fell back to the tool writer); nothing was written");
            string probeText = dropsProbes ? " and RESETS this BSP's " + _points + " light probe(s) to zero, as `tool lightmaps` does" : "";
            var pr = new Prompt
            {
                Kind = PromptKind.InstallWithoutLayer, Title = "Install without " + layer + "?", DropsAo = dropsAo, DropsProbes = dropsProbes, ProbePoints = _points,
                Message = "This map fell back to the tool writer (" + why + "). " + layer + " need(s) the direct writer's stage. Install the lightmap without " + layer
                          + " (the production bake with this capture)? This installs the lightmap only" + probeText + ".",
            };
            if (!(FlagsAllowInstallWithoutLayer(_o, pr) || Ask(pr)))
                throw new StepFailedException(why + ": " + layer + " need(s) the direct writer's stage; the lightmap was NOT installed (CLI: " + FallbackFlagsText(pr) + ")");
            // the exact production path with this capture: gpu_bake -SkipCapture -WriteBack (direct writer, tool fallback)
            RunProductionWriteBack(plan, true, null, "installing without " + layer + probeText, null, true);
            return false;
        }

        /// <summary>The CLI flags that answer "install without the layer" yes: --ao-fallback install for AO and (separately)
        /// --probes-fallback install for the probe reset; each dropped layer needs its own.</summary>
        public static bool FlagsAllowInstallWithoutLayer(BakeOptions o, Prompt p)
        {
            if (p.Kind != PromptKind.InstallWithoutLayer || (!p.DropsAo && !p.DropsProbes)) return false;
            return (!p.DropsAo || o.AoFallback == AoFallback.InstallWithoutAo) && (!p.DropsProbes || o.ProbesFallback == AoFallback.InstallWithoutAo);
        }

        public static string FallbackFlagsText(Prompt p)
        {
            var l = new List<string>();
            if (p.DropsAo) l.Add("--ao-fallback install");
            if (p.DropsProbes) l.Add("--probes-fallback install");
            return string.Join(" + ", l);
        }

        private ResolvedRuntime WithEngine(string script)
        {
            return new ResolvedRuntime
            {
                AppVersion = _rt.AppVersion, Root = _rt.Root, Work = _rt.Work, InstDir = _rt.InstDir, SlotBase = _rt.SlotBase, PwshDir = _rt.PwshDir, PwshExe = _rt.PwshExe,
                PyDir = _rt.PyDir, PyExe = _rt.PyExe, GpuBakePs1 = script, LmwInstallPy = _rt.LmwInstallPy, BoundsDb = _rt.BoundsDb, ScanSceneryPy = _rt.ScanSceneryPy, GpuCheckExe = _rt.GpuCheckExe,
            };
        }

        /// <param name="notBeforeUtc">R1: when given, a report last written before this time (minus 2 s for file-time
        /// granularity) is a stale one from an earlier bake of this worker, not this run's.</param>
        public static string StageProblem(string reportPath, string skeletonDir, DateTime? notBeforeUtc = null)
        {
            if (skeletonDir != null && !Directory.Exists(skeletonDir)) return "no " + skeletonDir;
            if (!File.Exists(reportPath)) return "no " + reportPath;
            if (notBeforeUtc.HasValue)
            {
                var t = File.GetLastWriteTimeUtc(reportPath);
                if (t < notBeforeUtc.Value.AddSeconds(-2))
                    return reportPath + " was not written by this run (last written " + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ", before the run started)";
            }
            try
            {
                var j = Json.ReadObjFile(reportPath);
                if (j == null || j.Str("status") != "written")
                {
                    string err = j == null ? null : LastLine(j.Str("error"));
                    return reportPath + " status is '" + (j == null ? "?" : j.Str("status")) + "', not 'written'" + (err != null ? " (" + err + ")" : "");
                }
            }
            catch (Exception e) { return reportPath + " is unreadable: " + e.Message; }
            return null;
        }

        /// <summary>0.6.6: the stage-only gpu_bake runs lmw_validate on its own stage but exits 0 even when it refuses it (it only Notes "tag writer FAILED"),
        /// so a stage whose report says 'written' could still be one the format gate refused -- and the shell would install it (Lightmap Only / Standard
        /// without probes have no later gate). Reads OUT\tagw_validate.json (the gate's --json, next to the stage folder): failed checks -> the reason text;
        /// a missing / stale file or a clean result -> null. WARN-only results (tolerated, see <see cref="StageWarning"/>) are not a problem.</summary>
        public static string ValidateProblem(string reportPath, DateTime? notBeforeUtc = null)
        {
            try
            {
                string stageDir = Path.GetDirectoryName(reportPath);
                string vj = stageDir == null ? null : Path.Combine(Path.GetDirectoryName(stageDir) ?? "", "tagw_validate.json");
                if (vj == null || !File.Exists(vj)) return null;
                if (notBeforeUtc.HasValue && File.GetLastWriteTimeUtc(vj) < notBeforeUtc.Value.AddSeconds(-2)) return null;
                var j = Json.ReadObjFile(vj);
                string why = j == null ? null : StageWarning.RefusalFromFailed(j.Arr("failed"));
                return why == null ? null : why + " (" + vj + ")";
            }
            catch (Exception) { return null; }
        }

        /// <summary>The last non-empty line of a traceback / message (the exception text), at most 300 characters; null if none.</summary>
        public static string LastLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string l = text.Replace("\r", "").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
            if (l == null) return null;
            return l.Length > 300 ? l.Substring(0, 300) + " ..." : l;
        }

        /// <summary>R1 fix (false DONE): the stage-only Lightmap Only result must be a 'written' stage made by this run.</summary>
        private void DoStageCheck(PlanStep s)
        {
            string rp = s.Params.Str("report");
            string why = StageProblem(rp, null, Run.Status.StartedUtc) ?? ValidateProblem(rp, Run.Status.StartedUtc);
            if (why == null) { Run.Shell("the direct writer's stage is ready: " + Path.GetDirectoryName(rp)); return; }
            Run.Shell("*** " + why + " -- the direct tag writer did not produce a stage");
            throw new StepFailedException("the direct tag writer did not produce a stage: " + why + ". Nothing was written to the tags. (On a stock H2EK the original "
                                          + "multiplayer scenarios hit a known tag-reader limit -- see the README, Known limits.)");
        }

        private static bool IsSharingViolation(IOException e)
        {
            int code = e.HResult & 0xFFFF;
            return code == 32 || code == 33;     // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        }

        /// <summary>Copies the files the stage's tagw_report.json lists (+ the report, + the AO note) into a fresh folder,
        /// md5-verified against the report.</summary>
        private void DoCopyFinalStage(PlanStep s)
        {
            string from = s.Params.Str("from"), to = s.Params.Str("to");
            string rp = Path.Combine(from, "tagw_report.json");
            string why = StageProblem(rp, null);
            if (why != null) throw new StepFailedException("the final stage is not usable: " + why);
            Guards.AssertStageNotUnderTags(to, _root);
            if (Directory.Exists(to)) throw new StepFailedException(to + " already exists");
            Directory.CreateDirectory(to);
            var rep = Json.ReadObjFile(rp);
            var outs = rep.Obj("outputs") ?? new JObj();
            int n = 0;
            foreach (var kv in outs)
            {
                var o = kv.Value as JObj;
                string md5 = SafeFs.CopyVerified(Path.Combine(from, kv.Key), Path.Combine(to, kv.Key));
                if (o != null && o.Str("md5") != null && !string.Equals(o.Str("md5"), md5, StringComparison.OrdinalIgnoreCase))
                    throw new StepFailedException("the staged " + kv.Key + " is not the file its writer reported (md5 " + md5 + " != " + o.Str("md5") + ")");
                n++;
            }
            if (!outs.Any(kv => kv.Key.EndsWith(".scenario_structure_lightmap", StringComparison.OrdinalIgnoreCase))) throw new StepFailedException("the final stage lists no lightmap tag");
            SafeFs.CopyVerified(rp, Path.Combine(to, "tagw_report.json"));
            string aoNote = Path.Combine(from, "AO_NOT_CPU_FAITHFUL.txt");
            if (File.Exists(aoNote)) SafeFs.CopyVerified(aoNote, Path.Combine(to, "AO_NOT_CPU_FAITHFUL.txt"));
            Run.Shell("copied " + n + " staged tag(s) + tagw_report.json to " + to + " (md5-verified)");
        }

        private void DoCopyLiveLtmp(PlanStep s)
        {
            string from = s.Params.Str("from"), to = s.Params.Str("to");
            Guards.AssertStageNotUnderTags(to, _root);
            if (!File.Exists(from)) throw new StepFailedException("the live lightmap tag is gone: " + from);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            _liveCopyMd5 = SafeFs.CopyVerified(from, to);
            Run.Shell("copied the live lightmap tag (md5 " + _liveCopyMd5 + ") to " + to);
        }

        private void DoProbeBake(PlanStep s)
        {
            if (s.FreshCapture)
                foreach (var m in CaptureRecord.SupersedeExisting(s.CaptureOut, DateTime.Now)) Run.Shell("the earlier probe capture record was renamed aside: " + m);
            _sideSawPhase2 = false;
            var oc = RunStepProcess(s);
            if (oc.Cancelled) throw new OperationCanceledException("cancelled during the light probe bake");
            if (s.FreshCapture && (oc.Ok || _sideSawPhase2)) WriteCaptureRecord(s, true);
            if (!oc.Ok) throw FailedStep("the light probe bake failed (exit " + ProcessRunner.FormatExit(oc.ExitCode) + "): " + (oc.FailureReason ?? "?"), oc);
            Run.SetStep(StepId.Probes);
        }

        private void DoShStage(PlanStep s)
        {
            string last = null;
            var oc = RunStepProcess(s, l => { if (l.TrimStart().StartsWith("SH_STAGE:", StringComparison.Ordinal)) last = l.Trim(); });
            if (oc.Cancelled) throw new OperationCanceledException("cancelled during sh_stage");
            if (oc.ExitCode == 0) { Run.Shell(last ?? "sh_stage ok"); return; }
            if (oc.ExitCode == 2) throw new StepFailedException("sh_stage REFUSED (nothing changed): " + (last ?? oc.FailureReason));
            if (oc.ExitCode == 3) throw new StepFailedException("sh_stage FAILED after a change (rolled back from _sh_merge_backup): " + (last ?? oc.FailureReason));
            throw new StepFailedException("sh_stage exit " + ProcessRunner.FormatExit(oc.ExitCode) + ": " + (last ?? oc.FailureReason));
        }

        private void DoCheckLiveLtmp(PlanStep s)
        {
            string live = s.Params.Str("live"), rp = s.Params.Str("report");
            string now = File.Exists(live) ? SafeFs.Md5File(live) : null;
            string src = null;
            try { var j = Json.ReadObjFile(rp); src = j == null || j.Obj("inputs") == null || j.Obj("inputs").Obj("src") == null ? null : j.Obj("inputs").Obj("src").Str("md5"); }
            catch (Exception) { }
            if (now == null) throw new StepFailedException("the live lightmap tag is gone: " + live);
            if (!string.Equals(now, _liveCopyMd5, StringComparison.OrdinalIgnoreCase) || (src != null && !string.Equals(src, _liveCopyMd5, StringComparison.OrdinalIgnoreCase)))
                throw new StepFailedException("the live lightmap tag changed while the probes baked (md5 " + now + ", the probes were written into " + (src ?? _liveCopyMd5)
                                              + ") -- nothing was installed; the probe stage is kept in " + Path.GetDirectoryName(rp));
            Run.Shell("the live lightmap tag is unchanged (md5 " + now + ")");
        }

        /// <summary>Lightprobes only (finding 21): the set lmw_validate --check-set reads = the probe stage's ltmp + md5-verified
        /// COPIES of the live truecolor / P8 bitmaps it will be installed beside (the live tags are only read).</summary>
        private void DoAssembleCheckSet(PlanStep s)
        {
            string to = s.Params.Str("to");
            Guards.AssertStageNotUnderTags(to, _root);
            if (Directory.Exists(to)) throw new StepFailedException(to + " already exists");
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (var k in new[] { "ltmp", "truecolor", "p8" })
            {
                string from = s.Params.Str(k);
                if (!File.Exists(from)) throw new StepFailedException("the check set cannot be assembled: " + from + " is missing -- nothing was installed");
                SafeFs.CopyVerified(from, Path.Combine(to, Path.GetFileName(from)));
                n++;
            }
            Run.Shell("assembled the check set (" + n + " files, md5-verified copies) in " + to);
        }

        private void DoInstall(BakePlan plan, string stage)
        {
            Run.SetStep(StepId.Install);
            for (int attempt = 1; ; attempt++)
            {
                var r = TagInstaller.Install(new InstallRequest
                {
                    PyExe = _rt.PyExe, LmwInstallPy = plan.LmwInstallPy ?? _rt.LmwInstallPy, Root = _root, Work = _work, Stage = stage, TargetDir = _set.TagDir,
                    BackupDir = plan.InstallBackupDir, AutoBackupRoot = Path.Combine(_work, "_TAGBAK_auto"), Marker = plan.Marker, Env = _env,
                }, Run);
                Run.Shell("install: " + r.Message);
                // review fix (finding 4): status.json names the install backup only when the install changed the live set
                if (r.Outcome == InstallOutcome.Installed || r.Outcome == InstallOutcome.FailedAfterChange)
                {
                    lock (Run.Status) { Run.Status.Marker = plan.Marker; Run.Status.InstallBackupDir = plan.InstallBackupDir; }
                    MarkInstalled();
                }
                switch (r.Outcome)
                {
                    case InstallOutcome.Installed:
                        return;
                    case InstallOutcome.Cancelled:
                        throw new OperationCanceledException("cancelled during the install");
                    case InstallOutcome.Refused:
                        if (Ask(new Prompt { Kind = PromptKind.InstallRetry, Title = "The lightmap files are in use", Message = r.Message + "\n\nRetry the install?" }))
                        { Run.ThrowIfCancelled(); continue; }
                        throw new StepFailedException("the install was refused before any change (" + r.Message + "); the stage is kept: " + stage);
                    case InstallOutcome.FailedAfterChange:
                        {
                            bool restore = Ask(new Prompt { Kind = PromptKind.RestoreAfterFailedInstall, Title = "The install failed", Message = r.Message + "\n\nRestore the previous lightmap from " + plan.InstallBackupDir + " now?" });
                            string res = restore ? RestoreFrom(plan.InstallBackupDir, plan.Marker) : "not restored -- run: --cli restore --h2ek \"" + _root + "\" --scenario \"" + _o.ScenarioRel + "\" --bsp " + _o.Bsp + " --undo-last-install";
                            throw new StepFailedException("the install FAILED after a change: " + r.Message + " | " + res);
                        }
                    default:
                        throw new StepFailedException("the install did not run: " + r.Message);
                }
            }
        }

        private string RestoreFrom(string backupDir, string marker)
        {
            try
            {
                var rr = TagInstaller.Restore(new TagRestoreRequest
                {
                    PyExe = _rt.PyExe, LmwInstallPy = _rt.LmwInstallPy, Root = _root, Work = _work, BackupDir = backupDir, TargetDir = _set.TagDir,
                    Marker = marker != null && File.Exists(marker) ? marker : null, Env = _env,
                }, null);
                return "restore: " + rr.Message;
            }
            catch (Exception e) { return "restore could not run: " + e.Message; }
        }

        private void DoVerify(BakePlan plan)
        {
            Run.SetStep(StepId.Verify);
            if (plan.Marker != null && File.Exists(plan.Marker))
                throw new StepFailedException("the install marker is still there (" + plan.Marker + "): the install did not finish -- restore the previous lightmap");
            // the installed files equal the stage the shell installed. U3: after the production -WriteBack argv (the tool write-back
            // fallback, or an install without the layer) only a 'written' OUT\tagw_stage of THIS run (its [4b] installed it) is
            // compared, and a difference is a note: the tool's [4] writes its own bytes
            string stage;
            bool strict;
            if (_productionWriteBack)
            {
                strict = false;
                string tw = Path.Combine(plan.Out ?? "", "tagw_stage");
                stage = !_installedWithoutLayer && StageProblem(Path.Combine(tw, "tagw_report.json"), null, Run.Status.StartedUtc) == null ? tw : null;
                if (stage == null) Run.Shell("the production write-back wrote the lightmap set (the tool's [4]: no direct-writer stage of this run to compare it with)");
            }
            else
            {
                strict = plan.InstallStage != null;
                stage = plan.InstallStage ?? Path.Combine(plan.Out ?? "", "tagw_stage");
            }
            int same = 0;
            var diff = new List<string>();
            var written = new List<string>();
            if (stage != null && File.Exists(Path.Combine(stage, "tagw_report.json")))
            {
                var rep = Json.ReadObjFile(Path.Combine(stage, "tagw_report.json"));
                foreach (var kv in rep.Obj("outputs") ?? new JObj())
                {
                    written.Add(kv.Key);
                    string live = Path.Combine(_set.TagDir, kv.Key);
                    string want = (kv.Value as JObj) == null ? null : ((JObj)kv.Value).Str("md5");
                    string got = File.Exists(live) ? SafeFs.Md5File(live) : null;
                    if (want != null && string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) same++;
                    else diff.Add(kv.Key);
                }
            }
            if (strict && diff.Count > 0) throw new StepFailedException("the installed files differ from the stage: " + string.Join(", ", diff));
            if (!strict && diff.Count > 0)
                Run.Shell("note: the live set differs from OUT\\tagw_stage (" + string.Join(", ", diff) + ") -- expected when gpu_bake fell back to the tool write-back");
            if (same > 0) Run.Shell("verified: " + same + " installed file(s) md5-equal to the stage");
            if (_backup != null)
            {
                var rad = TagBackup.ChangedRadiance(_backup);
                if (rad.Count > 0) Run.Shell("*** WARNING: radiance bitmap(s) changed during the bake (they are never written by it): " + string.Join(", ", rad));
                else if (_backup.RadianceMd5.Count > 0) Run.Shell("radiance bitmaps unchanged (" + _backup.RadianceMd5.Count + ")");
            }
            // our own install wrote the .scenario on a first bake (the set files are outside the fingerprint): refresh exactly that
            // line of the capture records, so the next reuse does not read our own install as "tags changed"
            try
            {
                string dirRel = Path.GetDirectoryName(_set.ScenarioRel) ?? "";
                var rels = written.Where(n => !n.StartsWith(_set.Prefix, StringComparison.OrdinalIgnoreCase)).Select(n => Path.Combine(dirRel, n)).ToList();
                if (_backup != null && _backup.FirstBake && !rels.Any()) rels.Add(_set.ScenarioRel + ".scenario");
                if (rels.Count > 0)
                    foreach (var w in new[] { plan.Worker, plan.ProbeWorker }.Where(x => x.HasValue))
                        if (CaptureRecord.RestampFiles(BakePlanner.FarmDir(_work, w.Value), _root, rels))
                            Run.Shell("capture record of farm_w" + w.Value.ToString("D2", CultureInfo.InvariantCulture) + ": fingerprint of " + string.Join(", ", rels) + " refreshed after our install");
            }
            catch (Exception e) { Run.Shell("capture record refresh failed: " + e.Message); }
        }

        /// <summary>After a cancel / failure: if an install marker is left, offer lmw_install restore (section 9).</summary>
        private void Aftermath(BakePlan plan, bool cancelled)
        {
            try
            {
                string marker = Run.Status.Marker ?? plan.Marker;
                if (marker == null) return;
                var off = CancelAftermath.Evaluate(marker);
                if (!off.MarkerExists) return;
                Run.Shell("*** the install marker " + marker + " exists: the lightmap set may be half-written");
                if (!off.IsInstallMarker) { Run.Shell("*** use Restore previous lightmap (the _TAGBAK_ui backup " + (Run.Status.BackupDir ?? "?") + ")"); return; }
                var pr = new Prompt
                {
                    Kind = PromptKind.RestoreAfterCancel, Title = cancelled ? "Cancelled during the install" : "The run failed during the install",
                    Message = "The lightmap install did not finish. Restore the previous lightmap from " + off.BackupDir + " now?",
                };
                if (Ask(pr))
                {
                    var rr = TagInstaller.Restore(new TagRestoreRequest
                    {
                        PyExe = _rt.PyExe, LmwInstallPy = _rt.LmwInstallPy, Root = _root, Work = _work, BackupDir = off.BackupDir, TargetDir = off.TargetDir, Marker = marker, Env = _env,
                    }, null);
                    Run.Shell("restore: " + rr.Message);
                }
                else Run.Shell("not restored -- run: --cli restore --h2ek \"" + _root + "\" --recover");
            }
            catch (Exception e) { Run.Shell("aftermath check failed: " + e.Message); }
        }
    }
}
