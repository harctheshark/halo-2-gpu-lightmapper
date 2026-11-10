// 0.6.12: the live progress of a bake for the window: a bar that moves through every step (not only at step changes), and the time left in the
// current step and in the whole bake.
//
// Stages: (.map bakes) Read map, Copy tags, then the bake's own steps (Preflight ... Verify, the planned ones), then (.map) Write map. The GPU bake is
// split into its parts: photon lattice (sky chain rounds "lights a..N"), gather (gate + one gather), sun light ("DIRECT: SUN k progress P%", N suns),
// surface lights ("SURFCULL: segment i/n"), compose; a bake in several chart parts ("--- v372 SEG PART k/n") runs gather .. compose once per part.
//
// Time left in a stage = from its own progress (elapsed * (1 - f) / f) and from the same stage of this map's last bake (stage_times.json), blended by f;
// neither known = "estimating". The bar weights every stage by its expected seconds (last bake, else a default) and moves inside a stage by its
// progress, or by time when the stage reports none (never past 95 % of the stage until it ends). The bar never moves back.
// Thread-safe: lines arrive on the child-output threads, the window reads Snapshot() on its timer.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    public enum StageState { Pending, Active, Done, Skipped, Failed }

    public sealed class BakeProgress
    {
        public const string MapRead = "MapRead", MapCopy = "MapCopy", MapWrite = "MapWrite";
        public static readonly string[] GpuParts = { "lattice", "gather", "suns", "surface", "compose" };
        private static readonly Dictionary<string, string> GpuPartNames = new Dictionary<string, string>
        {
            { "lattice", "photon lattice" }, { "gather", "gather" }, { "suns", "sun light" }, { "surface", "surface lights" }, { "compose", "compose" },
        };

        /// <summary>Seconds a stage takes when this map has no earlier bake (bar weights and the time-based creep only; never shown as a time).</summary>
        private static readonly Dictionary<string, double> Default = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { MapRead, 40 }, { MapCopy, 30 }, { "Preflight", 5 }, { "Backup", 2 }, { "Capture", 60 }, { "Derive", 10 }, { "GpuBake", 600 },
            { "GpuBake.lattice", 150 }, { "GpuBake.gather", 150 }, { "GpuBake.suns", 240 }, { "GpuBake.surface", 50 }, { "GpuBake.compose", 10 },
            { "TagWriter", 40 }, { "AO", 120 }, { "ProbeCapture", 60 }, { "Probes", 60 }, { "Install", 3 }, { "Verify", 2 }, { MapWrite, 90 },
        };

        /// <summary>0.6.12b: seconds per MILLION lattice work slots ("[lattice] BUFCAP ... % of N slots)", printed at the start of the GPU bake) of
        /// the stages after it, for a map with no earlier bake: the ROUGH estimate (this PC's learned rates, StageTimes "_rates", else these;
        /// from a many-light test map / cyclotron / headlong on an RTX 4090, super; maps differ about 3x per slot, hence "rough").</summary>
        private static readonly Dictionary<string, double> DefaultRate = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "GpuBake.lattice", 2.2 }, { "GpuBake.gather", 2.8 }, { "GpuBake.suns", 2.0 }, { "GpuBake.surface", 0.6 }, { "GpuBake.compose", 0.4 },
            { "GpuBake", 8.5 }, { "TagWriter", 0.6 }, { "AO", 4.9 }, { "ProbeCapture", 0.4 }, { "Probes", 0.4 }, { "Install", 0.03 }, { "Verify", 0.03 }, { MapWrite, 0.8 },
        };

        public sealed class Stage
        {
            public string Key, Name;
            public StageState State;
            public DateTime? Start, End;
            public double? Hist;           // this map's last bake (seconds)
            public double Frac = -1;       // own progress 0..1, -1 = none reported
            public string Sub;             // "12 of 39" etc.
            public double LastF;           // the bar fraction last shown (never moves back)
            public double AnchorF = -1, AnchorEl;   // where the bar stood when the stage's own estimate appeared
        }

        private readonly object _lock = new object();
        private readonly List<Stage> _stages = new List<Stage>();
        private readonly Dictionary<string, double> _hist;
        private readonly Dictionary<string, double> _rates;   // this PC's learned seconds per M slots (StageTimes "_rates"); empty = DefaultRate
        private double _mslots;                                // the map's lattice work slots in millions (0 = not seen yet)
        private readonly Dictionary<string, Stage> _gpu = new Dictionary<string, Stage>(StringComparer.Ordinal);
        private readonly bool _map;
        private DateTime _t0;
        private double _barMax;
        private bool _ended;
        // GPU bake parsing state
        private int _chainN, _sunN, _surfN, _partK, _partN;
        private bool _latticeEnded;
        // sun passes: seconds of each finished sun ("DIRECT: SUN k tiles: ... N ms"), the current sun, its progress and start
        private readonly List<double> _sunSec = new List<double>();
        private int _sunCur = -1;
        private double _sunCurP;
        private DateTime? _sunCurStart, _sunNextStart;

        public bool HasHistory { get { return _hist.Count > 0; } }

        /// <param name="map">a bake from a .map (adds Read map / Copy tags / Write map)</param>
        /// <param name="hist">this map's stage seconds from its last bake (StageTimes); empty = first bake</param>
        /// <param name="rates">this PC's seconds per million lattice slots per stage (StageTimes.LoadRates); null = the built-in defaults</param>
        public BakeProgress(bool map, Dictionary<string, double> hist, DateTime utcNow, Dictionary<string, double> rates = null)
        {
            _map = map;
            _hist = hist ?? new Dictionary<string, double>();
            _rates = rates ?? new Dictionary<string, double>();
            _t0 = utcNow;
            if (map) { Add(MapRead, "Read map"); Add(MapCopy, "Copy tags"); }
            foreach (var kv in Ui.StepChip.Names) Add(kv.Key.ToString(), kv.Value);
            if (map) Add(MapWrite, "Write map");
            foreach (var p in GpuParts) _gpu[p] = new Stage { Key = "GpuBake." + p, Name = GpuPartNames[p], State = StageState.Pending, Hist = H("GpuBake." + p) };
            _stages[0].State = StageState.Active;
            _stages[0].Start = utcNow;
        }

        private void Add(string key, string name) { _stages.Add(new Stage { Key = key, Name = name, State = StageState.Pending, Hist = H(key) }); }
        private double? H(string key) { double v; return _hist.TryGetValue(key, out v) && v > 0 ? v : (double?)null; }
        private Stage Find(string key) { return _stages.FirstOrDefault(s => s.Key == key); }
        public IReadOnlyList<Stage> Stages { get { return _stages; } }
        /// <summary>The map's lattice work slots in millions once the GPU bake printed them (0 before; StageTimes.SaveRates).</summary>
        public double MSlots { get { lock (_lock) return _mslots; } }

        /// <summary>The rough seconds of a stage with no earlier bake of this map: rate x the map's slots (null before the slots are known).</summary>
        private double? Rough(Stage s)
        {
            if (s.Hist.HasValue || _mslots <= 0) return null;
            double r;
            if (!_rates.TryGetValue(s.Key, out r) || r <= 0) { if (!DefaultRate.TryGetValue(s.Key, out r)) return null; }
            return Math.Max(1, r * _mslots);
        }
        /// <summary>This map's last time of the stage, else its rough estimate, else null.</summary>
        private double? HistOrRough(Stage s) { return s.Hist ?? Rough(s); }

        // ============================================================ inputs

        /// <summary>The bake's plan: steps outside it are skipped.</summary>
        public void OnPlan(IEnumerable<StepId> steps)
        {
            var set = new HashSet<string>(steps.Select(s => s.ToString()), StringComparer.Ordinal);
            lock (_lock)
                foreach (var s in _stages)
                    if (s.Key != MapRead && s.Key != MapCopy && s.Key != MapWrite && !set.Contains(s.Key) && s.State == StageState.Pending) s.State = StageState.Skipped;
        }

        /// <summary>The bake's current step (RunStatus.Step).</summary>
        public void OnStep(StepId step, DateTime utc) { lock (_lock) Enter(step.ToString(), utc); }

        /// <summary>A .map session phase line ("Reading the map ...", "Baking ...", "Injecting ...").</summary>
        public void OnMapPhase(string text, DateTime utc)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (text.StartsWith("Reading the map's dependency subset", StringComparison.Ordinal)) Enter(MapCopy, utc);
                else if (text.StartsWith("Reading the map", StringComparison.Ordinal)) Enter(MapRead, utc);
                else if (text.StartsWith("Baking", StringComparison.Ordinal)) Enter("Preflight", utc);
                else if (text.StartsWith("Injecting", StringComparison.Ordinal)) Enter(MapWrite, utc);
            }
        }

        private static readonly Regex RxCopy = new Regex(@"overlay: (\d+) of (\d+) kit tags copied", RegexOptions.Compiled);
        private static readonly Regex RxChainPlan = new Regex(@"SKYX: plan: .*?, (\d+) lights in cast order", RegexOptions.Compiled);
        private static readonly Regex RxChainRound = new Regex(@"SKYX: round \d+: \d+ walkers \(lights (\d+)\.\.", RegexOptions.Compiled);
        private static readonly Regex RxLatticeEnd = new Regex(@"^\s*lattice pre-pass [0-9.]+s", RegexOptions.Compiled);
        private static readonly Regex RxSunN = new Regex(@"DIRECT: lights\.bin (\d+) SUN", RegexOptions.Compiled);
        private static readonly Regex RxSunProg = new Regex(@"DIRECT: SUN (\d+) progress (\d+)%", RegexOptions.Compiled);
        private static readonly Regex RxSunDone = new Regex(@"DIRECT: SUN (\d+) tiles:", RegexOptions.Compiled);
        private static readonly Regex RxSurfSeg = new Regex(@"SURFCULL:\s+segment (\d+)/(\d+)", RegexOptions.Compiled);
        private static readonly Regex RxPart = new Regex(@"--- v372 SEG PART (\d+)/(\d+)", RegexOptions.Compiled);
        private static readonly Regex RxSlots = new Regex(@"BUFCAP: v372 cap .*?% of (\d+) slots\)", RegexOptions.Compiled);

        /// <summary>Every output line of the bake (the bake's own lines, PROGRESS| lines, the .map helper's lines).</summary>
        public void OnLine(string raw, DateTime utc)
        {
            if (string.IsNullOrEmpty(raw)) return;
            string t = raw.Trim();
            if (t.StartsWith("PROGRESS| ", StringComparison.Ordinal)) t = t.Substring(10);
            lock (_lock)
            {
                if (_ended) return;
                Match m;
                if ((m = RxCopy.Match(t)).Success) { var s = Find(MapCopy); if (s != null && s.State == StageState.Active) SetFrac(s, D(m, 1) / Math.Max(1, D(m, 2)), I(m, 1).ToString("N0", CultureInfo.InvariantCulture) + " of " + I(m, 2).ToString("N0", CultureInfo.InvariantCulture) + " files"); return; }
                var gb = Find("GpuBake");
                if (gb == null || gb.State != StageState.Active) return;
                if (_mslots <= 0 && (m = RxSlots.Match(t)).Success) { _mslots = D(m, 1) / 1e6; }   // the first one: the main bake's lattice (not the AO run's); the line goes on to the [lattice] handling
                if ((m = RxPart.Match(t)).Success)
                {
                    _partK = I(m, 1); _partN = I(m, 2);
                    foreach (var p in new[] { "gather", "suns", "surface", "compose" }) { var g = _gpu[p]; if (_partK > 1) { g.State = StageState.Pending; g.Frac = -1; g.Sub = null; g.LastF = 0; g.AnchorF = -1; g.Start = null; g.End = null; } }
                    _sunSec.Clear(); _sunCur = -1; _sunNextStart = null;
                    GpuEnter("gather", utc);
                    return;
                }
                if (t.Contains("[lattice]"))
                {
                    GpuEnter("lattice", utc);
                    if ((m = RxChainPlan.Match(t)).Success) { _chainN = I(m, 1); return; }
                    if ((m = RxChainRound.Match(t)).Success && _chainN > 0) { int a = I(m, 1); SetFrac(_gpu["lattice"], 0.9 * a / _chainN, "sky chain " + a.ToString("N0", CultureInfo.InvariantCulture) + " of " + _chainN.ToString("N0", CultureInfo.InvariantCulture) + " lights"); return; }
                    if (t.Contains("SKYX: all")) { SetFrac(_gpu["lattice"], 0.9, "sky chain done"); return; }
                    if (t.Contains("RE lattice")) { SetFrac(_gpu["lattice"], 0.97, null); return; }
                    return;
                }
                if (RxLatticeEnd.IsMatch(raw)) { _latticeEnded = true; GpuEnd("lattice", utc); return; }
                if (_latticeEnded && _gpu["gather"].State == StageState.Pending && (t.Contains("GATEFIRST:") || t.Contains("GATEWALK:") || t.Contains("BUFCAP: texelgen used")))
                { GpuEnter("gather", utc); return; }
                if (t.Contains("dispatch done:")) { GpuEnd("gather", utc); return; }
                if ((m = RxSunN.Match(t)).Success)
                {
                    _sunN = I(m, 1);
                    if (_sunN > 0) GpuEnter("suns", utc);
                    return;
                }
                if ((m = RxSunProg.Match(t)).Success && _sunN > 0)
                {
                    GpuEnter("suns", utc);
                    int k = I(m, 1), pc = I(m, 2);
                    if (k != _sunCur) { _sunCur = k; _sunCurStart = _sunNextStart ?? _gpu["suns"].Start ?? utc; }
                    _sunCurP = pc / 100.0;
                    SetFrac(_gpu["suns"], (k + pc / 100.0) / _sunN, "sun " + (k + 1) + " of " + _sunN);
                    return;
                }
                if ((m = RxSunDone.Match(t)).Success && _sunN > 0)
                {
                    GpuEnter("suns", utc);
                    int k = I(m, 1);
                    var ms = Regex.Match(t, @"(\d+) ms\s*$");
                    double sec = ms.Success ? double.Parse(ms.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0 : (_sunCurStart.HasValue ? (utc - _sunCurStart.Value).TotalSeconds : 0);
                    _sunSec.Add(sec);
                    _sunNextStart = utc; _sunCur = k; _sunCurP = 1;
                    SetFrac(_gpu["suns"], (k + 1.0) / _sunN, "sun " + Math.Min(_sunN, k + 2) + " of " + _sunN);
                    return;
                }
                if (t.Contains("SURFCULL: v372H per-light") || t.Contains("DIRECT: SURFACE ") && !t.Contains("SURFACE pass"))
                { GpuEnter("surface", utc); return; }
                if ((m = RxSurfSeg.Match(t)).Success) { GpuEnter("surface", utc); _surfN = I(m, 2); SetFrac(_gpu["surface"], D(m, 1) / Math.Max(1, _surfN), "segment " + I(m, 1) + " of " + _surfN); return; }
                if (t.Contains("SURFACE pass")) { GpuEnter("compose", utc); return; }
                if (t.Contains("COMPOSE: wrote atlas_out")) { var c = _gpu["compose"]; if (c.State == StageState.Active) SetFrac(c, 0.9, null); return; }
            }
        }

        public void Finish(bool ok, DateTime utc)
        {
            lock (_lock)
            {
                _ended = true;
                foreach (var s in _stages)
                {
                    if (s.State == StageState.Active) { s.State = ok ? StageState.Done : StageState.Failed; s.End = utc; }
                    else if (ok && s.State == StageState.Pending) s.State = StageState.Skipped;
                }
                foreach (var g in _gpu.Values) if (g.State == StageState.Active) { g.State = ok ? StageState.Done : StageState.Failed; g.End = utc; }
                if (ok) _barMax = 1;
            }
        }

        private static int I(Match m, int g) { return int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture); }
        private static double D(Match m, int g) { return double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture); }
        private static void SetFrac(Stage s, double f, string sub) { s.Frac = Math.Max(s.Frac, Math.Min(1, Math.Max(0, f))); if (sub != null) s.Sub = sub; }

        /// <summary>Stage <paramref name="key"/> starts: every stage before it that is still open ends now (a fast one may never have been seen).</summary>
        private void Enter(string key, DateTime utc)
        {
            int i = _stages.FindIndex(s => s.Key == key);
            if (i < 0 || _stages[i].State == StageState.Active || _stages[i].State == StageState.Done) return;
            for (int j = 0; j < i; j++)
            {
                var s = _stages[j];
                if (s.State == StageState.Active) { s.State = StageState.Done; s.End = utc; if (s.Key == "GpuBake") CloseGpu(utc); }
                else if (s.State == StageState.Pending) { s.State = StageState.Skipped; }
            }
            _stages[i].State = StageState.Active;
            _stages[i].Start = utc;
        }

        private void GpuEnter(string part, DateTime utc)
        {
            var g = _gpu[part];
            if (g.State == StageState.Active || g.State == StageState.Done) return;
            int i = Array.IndexOf(GpuParts, part);
            for (int j = 0; j < i; j++)
            {
                var p = _gpu[GpuParts[j]];
                if (p.State == StageState.Active) { p.State = StageState.Done; p.End = utc; }
                else if (p.State == StageState.Pending && (_partK <= 1 || j > 0)) p.State = StageState.Skipped;
            }
            g.State = StageState.Active;
            if (g.Start == null) g.Start = utc;
        }

        private void GpuEnd(string part, DateTime utc) { var g = _gpu[part]; if (g.State == StageState.Active) { g.State = StageState.Done; g.End = utc; g.Frac = 1; } }
        private void CloseGpu(DateTime utc) { foreach (var g in _gpu.Values) if (g.State == StageState.Active) { g.State = StageState.Done; g.End = utc; } }

        // ============================================================ outputs

        public sealed class View
        {
            public double Bar;                    // 0..1
            public string Line;                   // "GPU bake > sun light: sun 12 of 39  .  about 3 min left in this step  .  about 5 min left in total"
            public string Elapsed;                // "12 min 05 s"
            public double? TotalLeft, StageLeft, PartLeft;   // seconds (null = not known yet)
            public Dictionary<string, string> Detail = new Dictionary<string, string>(StringComparer.Ordinal);   // chip key -> "39 s" / "~3 min left" / "~2 min"
            public Dictionary<string, StageState> States = new Dictionary<string, StageState>(StringComparer.Ordinal);
        }

        private double Expected(Stage s) { return HistOrRough(s) ?? (Default.ContainsKey(s.Key) ? Default[s.Key] : 10); }

        /// <summary>Elapsed seconds of a stage (its end, or now).</summary>
        private static double El(Stage s, DateTime now) { return s.Start == null ? 0 : Math.Max(0, ((s.End ?? now) - s.Start.Value).TotalSeconds); }

        /// <summary>Time left of an ACTIVE stage (null = unknown) and its fraction for the bar. <paramref name="own"/>: a better estimate of the
        /// stage's own (the sun passes), used in place of elapsed * (1 - f) / f.</summary>
        private void Left(Stage s, DateTime now, out double? left, out double frac, double? own = null, bool ownOnly = false)
        {
            double el = El(s, now);
            double? fromFrac = own, fromHist = null;
            if (!fromFrac.HasValue && !ownOnly && s.Frac >= 0.03 && el >= 8) fromFrac = el * (1 - s.Frac) / s.Frac;
            if (s.Hist.HasValue) fromHist = Math.Max(0, s.Hist.Value - el);
            else { var r = Rough(s); if (r.HasValue && el < r.Value) fromHist = r.Value - el; }   // rough: past it = unknown (never "0 s left")
            double? h = HistOrRough(s);
            double fw = Math.Max(0, s.Frac);
            if (fromFrac.HasValue && fromHist.HasValue) left = fw * fromFrac.Value + (1 - fw) * fromHist.Value;
            else left = fromFrac ?? fromHist;
            if (own.HasValue && ownOnly)
            {
                // continue from where the time-moved bar stands when the stage's own estimate first appears (no jump back, no hold)
                if (s.AnchorF < 0) { s.AnchorF = s.LastF; s.AnchorEl = el; }
                double rest = el - s.AnchorEl;
                frac = s.AnchorF + (1 - s.AnchorF) * (rest + own.Value > 0 ? rest / (rest + own.Value) : 0);
            }
            else if (own.HasValue && el + own.Value > 0) frac = Math.Max(Math.Max(0, s.Frac), el / (el + own.Value));
            else if (ownOnly && !h.HasValue) frac = Math.Max(Math.Max(0, s.Frac), 0.95 * (1 - Math.Exp(-el / Math.Max(1, Expected(s)))));   // no estimate yet: time moves it
            else if (ownOnly) frac = Math.Max(Math.Max(0, s.Frac), Math.Min(0.95, el / Math.Max(1, h.Value)));                                // ... against the last bake's (or the rough) time
            else if (s.Frac >= 0) frac = s.Frac;
            else
            {
                double e = Expected(s);
                frac = h.HasValue ? Math.Min(0.95, el / Math.Max(1, e)) : 0.95 * (1 - Math.Exp(-el / Math.Max(1, e)));
            }
            frac = Math.Max(frac, Math.Max(0, s.LastF));
            s.LastF = frac;
        }

        /// <summary>The sun passes' time left from the suns already done: the current sun's remainder (its own %) + the suns still to come x the
        /// mean of the finished ones (the first sun, usually far heavier, is left out once another has finished). Null until that is known.</summary>
        private double? SunsLeft(DateTime now)
        {
            if (_sunN <= 0 || _sunCur < 0) return null;
            bool curDone = _sunCurP >= 1;
            int after = _sunN - _sunCur - 1;
            double? cur = 0;
            if (!curDone)
            {
                double elc = _sunCurStart.HasValue ? Math.Max(0, (now - _sunCurStart.Value).TotalSeconds) : 0;
                cur = _sunCurP >= 0.05 && elc >= 2 ? elc * (1 - _sunCurP) / _sunCurP : (double?)null;
            }
            if (after <= 0) return cur;
            var pool = _sunSec.Count >= 2 ? _sunSec.Skip(1).ToList() : null;
            if (pool == null || pool.Count == 0 || !cur.HasValue) return null;
            return cur.Value + after * pool.Average();
        }

        public View Snapshot(DateTime now)
        {
            lock (_lock)
            {
                var v = new View { Elapsed = Dur((now - _t0).TotalSeconds) };
                double total = 0, done = 0, later = 0;
                double? stageLeft = null, partLeft = null;
                bool afterCur = false, laterUnknown = false;
                Stage cur = null;
                string curSub = null, curPart = null;
                foreach (var s in _stages)
                {
                    v.States[s.Key] = s.State;
                    if (s.State == StageState.Skipped) continue;
                    double w = Expected(s);
                    total += w;
                    if (s.State == StageState.Done || s.State == StageState.Failed) { done += w; v.Detail[s.Key] = Dur(El(s, now)); continue; }
                    if (s.State == StageState.Active)
                    {
                        cur = s;
                        double? left; double f;
                        if (s.Key == "GpuBake") GpuLeft(now, out left, out partLeft, out f, out curPart, out curSub);
                        else { Left(s, now, out left, out f); curSub = s.Sub; }
                        done += w * f;
                        stageLeft = left;
                        v.Detail[s.Key] = left.HasValue ? "~" + Short(left.Value) + " left"
                                        : partLeft.HasValue ? "~" + Short(partLeft.Value) + " in " + curPart
                                        : s.Frac >= 0 ? ((int)(100 * Math.Max(0, s.Frac))).ToString(CultureInfo.InvariantCulture) + "%" : "…";
                        afterCur = true;
                        continue;
                    }
                    // pending
                    var ph = HistOrRough(s);
                    if (ph.HasValue) { v.Detail[s.Key] = "~" + Short(ph.Value); if (afterCur) later += ph.Value; }
                    else if (afterCur && Expected(s) > 15) laterUnknown = true;          // a long step with no earlier bake: the total is not known
                    else if (afterCur) later += Expected(s);                              // a short step: its default is close enough
                }
                double bar = total > 0 ? done / total : 0;
                _barMax = Math.Max(_barMax, Math.Min(0.995, bar));
                v.Bar = _barMax;
                v.StageLeft = stageLeft;
                v.PartLeft = partLeft;
                v.TotalLeft = stageLeft.HasValue && !laterUnknown ? stageLeft + later : null;
                if (cur != null)
                {
                    string name = cur.Name + (curPart != null ? " › " + curPart : "") + (curSub != null ? ": " + curSub : "")
                                + (_partN > 1 && cur.Key == "GpuBake" ? " (chart part " + _partK + " of " + _partN + ")" : "");
                    string a = stageLeft.HasValue ? "about " + Dur(stageLeft.Value) + " left in this step"
                             : partLeft.HasValue ? "about " + Dur(partLeft.Value) + " left in " + curPart + " (the rest of this step: estimating)"
                             : "estimating the time left in this step";
                    string b = v.TotalLeft.HasValue ? "about " + Dur(v.TotalLeft.Value) + " left in total" + (HasHistory ? "" : " (a rough estimate: this map's first bake)")
                             : HasHistory ? "total: estimating" : "total: estimating (this map's first bake with this version; the next bake shows the time left from the start)";
                    v.Line = name + "  ·  " + a + "  ·  " + b;
                }
                return v;
            }
        }

        /// <summary>The GPU bake's time left (the current part + the later parts' times from the last bake; null when a later part has no time
        /// yet), the current part's own time left, and the step's fraction for the bar.</summary>
        private void GpuLeft(DateTime now, out double? left, out double? partLeft, out double frac, out string partName, out string sub)
        {
            var gb = Find("GpuBake");
            double el = El(gb, now);
            partName = null; sub = null; partLeft = null;
            double wsum = 0, wdone = 0, lft = 0; bool unknown = false, after = false;
            foreach (var p in GpuParts)
            {
                var g = _gpu[p];
                if (g.State == StageState.Skipped) continue;
                double w = Expected(g);
                wsum += w;
                if (g.State == StageState.Done) { wdone += w; continue; }
                if (g.State == StageState.Active)
                {
                    double? l; double f;
                    // the sun passes: never elapsed / (suns done / suns): the first sun is often ten times the others (a test map: 106 s vs ~10 s)
                    Left(g, now, out l, out f, p == "suns" ? SunsLeft(now) : null, p == "suns");
                    wdone += w * f;
                    partName = g.Name; sub = g.Sub; partLeft = l;
                    if (l.HasValue) lft += l.Value; else unknown = true;
                    after = true;
                    continue;
                }
                if (after || g.State == StageState.Pending) { var gh = HistOrRough(g); if (gh.HasValue) lft += gh.Value; else unknown = true; }
            }
            double fParts = wsum > 0 ? wdone / wsum : 0;
            if (_partN > 1) fParts = ((_partK - 1) + fParts) / _partN;
            if (!after)
            {
                // between the parts (the skeleton start, the guards before the lattice): time against the whole step's last bake
                double? l0; double f0;
                Left(gb, now, out l0, out f0);
                left = l0; frac = Math.Max(fParts, f0 * 0.05);
                return;
            }
            frac = Math.Min(0.99, fParts);
            var gbh = HistOrRough(gb);
            left = unknown ? (gbh.HasValue && partLeft.HasValue ? Math.Max(partLeft.Value, gbh.Value - el) : (double?)null) : lft;
            if (_partN > 1 && left.HasValue && _partK < _partN) left = left.Value + (_partN - _partK) * (left.Value + el) / Math.Max(1, _partK);
        }

        /// <summary>Seconds of every stage (and GPU-bake part) that ran to its end, for stage_times.json.</summary>
        public Dictionary<string, double> Durations()
        {
            lock (_lock)
            {
                var d = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var s in _stages) if (s.State == StageState.Done && s.Start.HasValue && s.End.HasValue) d[s.Key] = Math.Round((s.End.Value - s.Start.Value).TotalSeconds, 1);
                if (_partN <= 1)
                    foreach (var g in _gpu.Values) if (g.State == StageState.Done && g.Start.HasValue && g.End.HasValue) d[g.Key] = Math.Round((g.End.Value - g.Start.Value).TotalSeconds, 1);
                return d;
            }
        }

        public static string Dur(double seconds) { return History.Dur(seconds); }

        /// <summary>"3 min" / "45 s" / "1 h 05 min" for the chips.</summary>
        public static string Short(double seconds)
        {
            if (seconds >= 3600) return ((int)(seconds / 3600)).ToString(CultureInfo.InvariantCulture) + " h " + ((int)(seconds % 3600 / 60)).ToString("00", CultureInfo.InvariantCulture) + " min";
            if (seconds >= 90) return ((int)Math.Round(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)Math.Round(Math.Max(1, seconds))).ToString(CultureInfo.InvariantCulture) + " s";
        }
    }

    /// <summary>WORK\standalone\stage_times.json: the seconds of every stage of each map's last bake (per map, quality, mode, AO, source), for the
    /// progress model's time left. Written by the window when a bake ends; a stage that did not run to its end keeps its earlier time.</summary>
    public static class StageTimes
    {
        public const int MaxMaps = 200;
        public static string PathFor(string work) { return Path.Combine(ShellPaths.StandaloneOf(work), "stage_times.json"); }

        public static string Key(bool map, string mapOrScenario, string bsp, string quality, string mode, bool ao)
        {
            return string.Join("|", map ? "map" : "scenario", (mapOrScenario ?? "").ToLowerInvariant(), (bsp ?? "").ToLowerInvariant(), quality ?? "", mode ?? "", ao ? "ao" : "noao");
        }

        public static Dictionary<string, double> Load(string work, string key)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                var j = Json.ReadObjFile(PathFor(work));
                var m = j == null ? null : j.Obj("maps");
                var e = m == null ? null : m.Obj(key);
                if (e == null) return d;
                foreach (var kv in e)
                {
                    if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                    try { d[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return d;
        }

        /// <summary>0.6.12b: this PC's seconds per million lattice slots of each stage (any map) for the quality/mode key <paramref name="rkey"/>
        /// ("_rates" in stage_times.json): a first bake of a map gets a rough time left from them. Empty = none learned yet.</summary>
        public static Dictionary<string, double> LoadRates(string work, string rkey)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                var j = Json.ReadObjFile(PathFor(work));
                var r = j == null ? null : j.Obj("_rates");
                var e = r == null ? null : r.Obj(rkey);
                if (e == null) return d;
                foreach (var kv in e)
                {
                    if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                    try { double v = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture); if (v > 0) d[kv.Key] = v; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return d;
        }

        /// <summary>Learns the rates from a bake that printed its slots: seconds / (slots / 1e6), averaged with the rate already stored (so one
        /// unusual map moves it only half way).</summary>
        public static void SaveRates(string work, string rkey, Dictionary<string, double> stages, double mslots, DateTime utc)
        {
            if (stages == null || stages.Count == 0 || !(mslots > 0.5)) return;
            try
            {
                var j = Json.ReadObjFile(PathFor(work)) ?? new JObj();
                var r = j.Obj("_rates") ?? new JObj();
                var e = r.Obj(rkey) ?? new JObj();
                var old = LoadRates(work, rkey);
                foreach (var kv in stages)
                {
                    if (kv.Key == "Preflight" || kv.Key == "Backup" || kv.Key == "Capture" || kv.Key == "Derive" || kv.Key == BakeProgress.MapRead || kv.Key == BakeProgress.MapCopy) continue;
                    double v = kv.Value / mslots, o;
                    e[kv.Key] = Math.Round(old.TryGetValue(kv.Key, out o) ? 0.5 * (o + v) : v, 4);
                }
                e["_utc"] = utc.ToString("o", CultureInfo.InvariantCulture);
                r[rkey] = e;
                j["_rates"] = r;
                j["schema"] = 1;
                Directory.CreateDirectory(ShellPaths.StandaloneOf(work));
                SafeFs.WriteAllTextAtomic(PathFor(work), Json.Write(j, true));
            }
            catch (Exception) { }
        }

        public static void Save(string work, string key, Dictionary<string, double> stages, DateTime utc)
        {
            if (stages == null || stages.Count == 0) return;
            try
            {
                var j = Json.ReadObjFile(PathFor(work)) ?? new JObj();
                var maps = j.Obj("maps") ?? new JObj();
                var e = maps.Obj(key) ?? new JObj();
                foreach (var kv in stages) e[kv.Key] = kv.Value;
                e["_utc"] = utc.ToString("o", CultureInfo.InvariantCulture);
                maps[key] = e;
                if (maps.Count > MaxMaps)
                {
                    var keep = maps.Select(kv => new { kv.Key, U = (kv.Value as JObj) == null ? "" : ((JObj)kv.Value).Str("_utc") ?? "" })
                                   .OrderByDescending(x => x.U, StringComparer.Ordinal).Take(MaxMaps).Select(x => x.Key).ToList();
                    var nm = new JObj();
                    foreach (var k in keep) nm[k] = maps[k];
                    maps = nm;
                }
                j["maps"] = maps;
                j["schema"] = 1;
                Directory.CreateDirectory(ShellPaths.StandaloneOf(work));
                SafeFs.WriteAllTextAtomic(PathFor(work), Json.Write(j, true));
            }
            catch (Exception) { }
        }
    }
}
