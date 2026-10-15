// S2.5 the window (FINAL_DESIGN section 4). Everything slow (discovery, scenario listing, payload extraction, the map query,
// the reuse gate's tags fingerprint, gpucheck, the bake itself) runs off the UI thread; results come back through the
// dispatcher. The bake options start at the defaults every launch; root and map are remembered in settings.json.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Cli;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Pipeline;
using H2GpuLightmapper.Ui;
using H2Lm.Proc;

namespace H2GpuLightmapper
{
    public partial class MainWindow : Window
    {
        private readonly Program.GuiArgs _g;
        private readonly AppHost _host;
        private readonly AppSettings _settings;
        private readonly ObservableCollection<LogItem> _log = new ObservableCollection<LogItem>();
        private readonly ConcurrentQueue<LogItem> _logQ = new ConcurrentQueue<LogItem>();
        private readonly ObservableCollection<StepChip> _chips = new ObservableCollection<StepChip>();
        private readonly DispatcherTimer _flush;
        private readonly List<string> _scenarios = new List<string>();
        private ICollectionView _scenarioView;
        private string _scenarioFilter = "";
        private bool _suppress;

        private string _root, _work;
        private ResolvedRuntime _rt;
        private ChildEnvironment _env;
        private bool _runtimeReady;
        private MapInfo _mapInfo;
        private int _rootVersion, _mapVersion, _reuseVersion;

        private BakeSession _session;
        private DateTime _bakeStarted;
        private double? _eta;
        private List<StepId> _planSteps;

        private string _attachRunDir;
        private LogTail _attachTail;
        private DispatcherTimer _attachTimer;
        private bool _closeAfterBake;
        private bool _aoAvailable;
        private string _aoWhy;
        /// <summary>A tag-writing / runtime-rewriting action outside a bake is running (restore, undo, recovery restore, repair):
        /// its name. Review fix (finding 7): the window cannot close and no other such action (or a bake) starts meanwhile.</summary>
        private string _op;
        private bool _closeAfterOp;

        public MainWindow() : this(new Program.GuiArgs()) { }

        public MainWindow(Program.GuiArgs g)
        {
            _g = g ?? new Program.GuiArgs();
            InitializeComponent();
            DarkTitleBar.Attach(this);
            _host = AppHost.Create(_g.Host);
            _settings = _host.LoadSettings();
            Title = "H2 GPU Lightmapper " + _host.AppVersion;
            LogList.ItemsSource = _log;
            foreach (var kv in StepChip.Names) _chips.Add(new StepChip { Step = kv.Key, Name = kv.Value });
            StepList.ItemsSource = _chips;
            _scenarioView = CollectionViewSource.GetDefaultView(_scenarios);
            _scenarioView.Filter = o => _scenarioFilter.Length == 0 || ((string)o).IndexOf(_scenarioFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            ScenarioCombo.ItemsSource = _scenarioView;
            ScenarioCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ScenarioText_Changed));
            // review fixes: Custom only with the guarded engine (or the explicit unguarded dev flag); Lightprobes only with the
            // T4 probe flag; AO only when the payload proved its AO engine (findings 16, 14, 15)
            QCustom.Visibility = _host.CustomAvailable ? Visibility.Visible : Visibility.Collapsed;
            MProbes.Visibility = _host.ProbesEnabled ? Visibility.Visible : Visibility.Collapsed;
            _aoAvailable = _host.AoEnabled(out _aoWhy);
            if (!_aoAvailable && AoCheck != null) { AoCheck.IsChecked = false; }
            if (Application.Current != null) Application.Current.SessionEnding += OnSessionEnding;
            _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _flush.Tick += (s, e) => FlushLog();
            _flush.Start();
            if (_g.Snapshot != null)
            {
                // the UI smoke test: laid out and rendered off-screen, never activated (no focus steal)
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000; Top = -32000; ShowActivated = false; ShowInTaskbar = false;
            }
            Loaded += OnLoaded;
            Closing += OnClosing;
            // the options scroll when the window is short, so the progress and at least ~90 px of log stay visible
            SizeChanged += (s, e) => OptionsScroll.MaxHeight = Math.Max(160, ActualHeight * 0.62);
            UpdateOptionUi();
        }

        // ================================================================== start-up

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log(Palette.Dim, "H2 GPU Lightmapper " + _host.AppVersion + " (exe " + AppHost.ExeVersion + ")   payload: " + (_host.HasPayload ? _host.Source.Describe : "NONE"));
            if (!_host.HasPayload)
            {
                SetStatus(Palette.Bad, "This build cannot bake: " + _host.PayloadError);
                BakeBtn.IsEnabled = false;
            }
            if (_g.Preset != null)
            {
                foreach (var p in _g.Preset.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()))
                {
                    switch (p)
                    {
                        case "custom": if (QCustom.Visibility == Visibility.Visible) QCustom.IsChecked = true; break;
                        case "low": QLow.IsChecked = true; break;
                        case "ao": AoCheck.IsChecked = true; break;
                        case "advanced": AdvancedExpander.IsExpanded = true; break;
                        case "probes": if (MProbes.Visibility == Visibility.Visible) MProbes.IsChecked = true; break;
                        case "lightmap": MLightmap.IsChecked = true; break;
                        case "stageonly": StageOnlyCheck.IsChecked = true; break;
                    }
                }
                UpdateOptionUi();
            }
            if (_g.Snapshot != null)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, _g.SnapshotDelayMs)) };
                t.Tick += (s, a) =>
                {
                    t.Stop();
                    if (_g.AutoBake && !_busy)
                    {
                        // UI test: press Bake (stage-only presets skip the install confirmation); snapshot when the run ends
                        BakeBtn_Click(BakeBtn, null);
                        var guard = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
                        guard.Tick += (s2, a2) => { guard.Stop(); SaveSnapshot(_g.Snapshot + ".timeout.png"); if (_session != null) _session.RequestCancel(); };
                        guard.Start();
                        if (_session == null) { SaveSnapshot(_g.Snapshot); Close(); }
                        return;
                    }
                    SaveSnapshot(_g.Snapshot); Close();
                };
                t.Start();
            }
            if (!_g.NoStartupWork) StartDiscovery();
        }

        private void SaveSnapshot(string path)
        {
            try
            {
                var root = (FrameworkElement)Content;
                var dpi = VisualTreeHelper.GetDpi(this);
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                                                 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }
                bmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) enc.Save(fs);
            }
            catch (Exception ex) { try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { } }
        }

        private void StartDiscovery()
        {
            Task.Run(() => H2ekDiscovery.Discover(new DiscoveryOptions { Remembered = _settings.Roots })).ContinueWith(t =>
            {
                var list = t.Status == TaskStatus.RanToCompletion ? t.Result : new List<RootCandidate>();
                if (_g.Root != null)
                {
                    var b = H2ekDiscovery.FromBrowse(_g.Root);
                    if (b != null && !list.Any(c => SamePath(c.Root, b.Root))) list.Insert(0, b);
                }
                Dispatcher.BeginInvoke(new Action(() => FillRoots(list, _g.Root ?? _settings.LastRoot)));
            });
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        private void FillRoots(List<RootCandidate> list, string prefer)
        {
            _suppress = true;
            var items = list.Select(c => new RootItem { Root = c.Root, Display = c.Root + "   — " + c.Label + (c.HasToolFast ? "" : " (no tool_fast.exe)") }).ToList();
            RootCombo.ItemsSource = items;
            var pick = items.FirstOrDefault(i => prefer != null && SamePath(i.Root, prefer))
                    ?? items.FirstOrDefault(i => list.First(c => c.Root == i.Root).HasToolFast) ?? items.FirstOrDefault();
            _suppress = false;
            if (pick == null) { ToolStatus.Text = "No H2EK found: use Browse… to pick the folder with tool_fast.exe."; ToolStatus.Foreground = Palette.Warn; return; }
            RootCombo.SelectedItem = pick;
        }

        private void RootCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var it = RootCombo.SelectedItem as RootItem;
            if (it != null) SetRoot(it.Root);
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick tool_fast.exe in the Halo 2 Editing Kit folder", Filter = "tool_fast.exe|tool_fast.exe|All files|*.*",
                CheckFileExists = true, InitialDirectory = _root != null && Directory.Exists(_root) ? _root : null,
            };
            if (dlg.ShowDialog(this) != true) return;
            var c = H2ekDiscovery.FromBrowse(dlg.FileName);
            if (c == null || !Directory.Exists(Path.Combine(c.Root, "tags"))) { MessageBox.Show(this, "That is not an H2EK folder (it needs tags\\ next to tool_fast.exe).", "H2 GPU Lightmapper", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var items = (RootCombo.ItemsSource as IEnumerable<RootItem> ?? new RootItem[0]).ToList();
            var ex = items.FirstOrDefault(i => SamePath(i.Root, c.Root));
            if (ex == null) { ex = new RootItem { Root = c.Root, Display = c.Root + "   — picked" }; items.Insert(0, ex); RootCombo.ItemsSource = items; }
            RootCombo.SelectedItem = ex;
        }

        private void SetRoot(string root)
        {
            int ver = ++_rootVersion;
            DetachView();
            _root = root;
            _work = ShellPaths.WorkOf(root);
            _rt = null; _env = null; _runtimeReady = false; _mapInfo = null;
            WorkBox.Text = _work;
            ToolStatus.Text = "Checking " + root + " …";
            ToolStatus.Foreground = Palette.Dim;
            _scenarios.Clear(); _scenarioView.Refresh();
            BspCombo.ItemsSource = null;
            MapInfo.Text = "";
            var inst = _host.HasPayload ? _host.Manifest.InstComponent : null;
            Task.Run(() =>
            {
                var tool = ToolFast.Identify(root);
                string pathBad = BakeSession.PathTextProblem(root);
                var pf = PreflightChecks.Run(root, new PreflightOptions
                {
                    ProbeWrite = false, RunToolSmoke = false, InstDirName = inst == null ? null : inst.DirName, InstMaxPathLen = inst == null ? 0 : inst.MaxPathLen,
                    AliasLocalAppData = _host.Paths.AliasLocalAppData, AliasVolumeRoot = _host.Paths.AliasVolumeRoot,
                });
                var scen = MapQuery.Scenarios(root);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ver != _rootVersion) return;
                    var fails = pf.Failures.Select(f => f.Message).ToList();
                    if (pathBad != null && !fails.Contains(pathBad)) fails.Insert(0, pathBad);
                    ToolStatus.Text = tool.StatusLine + (fails.Count > 0 ? "\n" + string.Join("\n", fails.Where(f => f != tool.StatusLine)) : "");
                    ToolStatus.Foreground = tool.Verdict == ToolVerdict.Supported && fails.Count == 0 ? Palette.Good : Palette.Bad;
                    _scenarios.Clear(); _scenarios.AddRange(scen); _scenarioFilter = ""; _scenarioView.Refresh();
                    string want = _g.Scenario ?? (SamePath(_settings.LastRoot ?? "", root) ? _settings.LastScenario : null);
                    string norm = BakeOptions.NormalizeScenario(root, want);
                    var hit = norm == null ? null : _scenarios.FirstOrDefault(s => string.Equals(s, norm, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) ScenarioCombo.SelectedItem = hit;
                    Log(Palette.Dim, "H2EK " + root + ": " + tool.StatusLine + "; " + scen.Count + " scenario(s)");
                }));
                if (!_host.HasPayload) return;
                Dispatcher.BeginInvoke(new Action(() => { if (ver == _rootVersion) SetStatus(Palette.Dim, "Preparing the runtime (the first start takes a few seconds)…"); }));
                try
                {
                    var q = _host.Installer.StartupEnsure(root);
                    var rt = _host.Resolve(root);
                    var env = BakeEnv.Build(rt);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ver != _rootVersion) return;
                        _rt = rt; _env = env; _runtimeReady = q.Ok;
                        SetStatus(q.Ok ? Palette.Dim : Palette.Bad, q.Ok ? "Ready." : "The runtime is damaged: " + q + " (Advanced > Repair runtime)");
                        RefreshMap();
                    }));
                    GpuProbe(ver, rt, inst);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() => { if (ver == _rootVersion) SetStatus(Palette.Bad, "The runtime could not be prepared on this H2EK: " + ex.Message); }));
                }
                Recovery(ver, root);
            });
        }

        private void GpuProbe(int ver, ResolvedRuntime rt, PayloadComponent inst)
        {
            if (rt.GpuCheckExe == null) { Dispatcher.BeginInvoke(new Action(() => GpuLine.Text = "GPU: the payload has no gpucheck.exe")); return; }
            var r = GpuCheck.Run(rt.GpuCheckExe);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ver != _rootVersion) return;
                GpuLine.Text = r.Line + (r.Advice != null && r.Advice.Count > 0 ? "\n" + string.Join("\n", r.Advice) : "");
                GpuLine.Foreground = r.Ok ? Palette.Dim : Palette.Bad;
            }));
        }

        /// <summary>Startup recovery (section 9): interrupted runs are marked, an own stale lock is released, a half-finished
        /// install is offered for restore; a run alive in another window is attached read-only.</summary>
        private void Recovery(int ver, string root)
        {
            string work = ShellPaths.WorkOf(root);
            if (!Directory.Exists(work)) return;
            List<RecoveryItem> items;
            try { items = StartupRecovery.Scan(work); } catch (Exception) { return; }
            CurrentRun cur;
            var ak = CurrentRun.Inspect(work, out cur);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ver != _rootVersion) return;
                if (_g.Snapshot != null) { foreach (var it in items) Log(Palette.Warn, "recovery (snapshot mode, not acted on): " + it); return; }
                // review fix (finding 1): a marker of a bake that is still running (another window, the launcher, a research bake)
                // is never offered for a restore -- restoring over a set that is being written would mix two lightmaps
                bool liveElsewhere = ak == AttachKind.LiveOtherShell;
                foreach (var it in items)
                {
                    if (it.InUse || (liveElsewhere && (it.Kind == RecoveryKind.InstallMarker || it.Kind == RecoveryKind.ToolWriteBackMarker)))
                    {
                        Log(Palette.Dim, "recovery: " + it.Message + " -- in use by a running bake (" + (it.InUseWhy ?? "another window") + "); left alone");
                        continue;
                    }
                    switch (it.Kind)
                    {
                        case RecoveryKind.InterruptedRun:
                            StartupRecovery.MarkInterrupted(work, it);
                            Log(Palette.Warn, "recovery: " + it.Message + " -- marked INTERRUPTED");
                            break;
                        case RecoveryKind.StaleOwnLock:
                            Log(Palette.Warn, "recovery: " + it.Message + (StartupRecovery.ReleaseStaleOwnLock(work) ? " -- released" : " -- left"));
                            break;
                        case RecoveryKind.InstallMarker:
                            if (_op == null && MessageBox.Show(this, it.Message + "\n\nRestore the previous lightmap from its backup now?", it.IsTagRestore ? "A restore did not finish" : "An install did not finish",
                                                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                                RestoreMarker(root, work, it);
                            else Log(Palette.Warn, "recovery: " + it.Message + " (not restored; Bake stays refused until it is)");
                            break;
                        default:
                            {
                                // R1 fix (V6 finding K2): a write-back killed before the tool saved left the set unchanged -- prove it
                                // (md5 against the copy gpu_bake made before [4]) and set the marker aside instead of restoring
                                // an OLDER backup or refusing every bake of this BSP forever
                                if (_op == null)
                                {
                                    StartupRecovery.UnchangedResult un = null;
                                    try { using (TagWriteGate.Enter(work, "the recovery check")) un = StartupRecovery.ClearToolMarkerIfUnchanged(work, root, it, DateTime.Now); }
                                    catch (SafetyException e) { Log(Palette.Warn, "recovery check refused: " + e.Message); }
                                    if (un != null && un.Cleared) { Log(Palette.Good, "recovery: " + it.Message + " -- " + un.Message); break; }
                                    if (un != null) Log(Palette.Dim, "recovery check: " + un.Message);
                                }
                                // review fix (finding 6): the tool write-back's marker names the set: offer its newest differing backup
                                var set = StartupRecovery.SetFor(it, root);
                                var c = set == null ? null : TagRestore.Newest(work, set);
                                if (c != null && _op == null && MessageBox.Show(this, it.Message + "\n\nRestore " + set.Prefix + " from the backup " + c.Name + " now?" + (c.Verified ? "" : "\n(a legacy backup without md5s: restored as found)"),
                                                                 "A write-back did not finish", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                                    RunTagRestore(work, set, c, "recovery restore");
                                else
                                {
                                    if (c == null) MessageBox.Show(this, it.Message + "\n\n(No backup of this lightmap was found to restore it from automatically.)", "A write-back did not finish", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    Log(Palette.Warn, "recovery: " + it.Message);
                                }
                                break;
                            }
                    }
                }
                if (ak == AttachKind.LiveOtherShell && cur != null) AttachView(cur);
            }));
        }

        private void RestoreMarker(string root, string work, RecoveryItem it)
        {
            if (_rt == null) { MessageBox.Show(this, "The runtime is not ready yet; try again in a moment (or use --cli restore --recover).", "H2 GPU Lightmapper"); return; }
            TagRestoreRequest req;
            try { req = StartupRecovery.RestoreRequestFor(it, root, work); }
            catch (SafetyException e) { MessageBox.Show(this, e.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            req.PyExe = _rt.PyExe; req.LmwInstallPy = _rt.LmwInstallPy; req.Env = _env;
            RunOp("recovery restore", work, null, lease => TagInstaller.Restore(req), r => r.Outcome + ": " + r.Message, r => r.Outcome == InstallOutcome.Installed, "Restore");
        }

        // ================================================================== tag-writing actions outside a bake (review fixes 2 + 7)

        /// <summary>
        /// Runs a tag-writing (or runtime-rewriting) action off the UI thread, gated: refused while a bake runs on this H2EK or a
        /// live holder has the GPU lock (TagWriteGate); the GPU lock is held for its duration; the window cannot close, the
        /// restore / undo / repair buttons and Bake are disabled until it ends; Windows is asked to hold a shutdown meanwhile.
        /// </summary>
        private void RunOp<T>(string name, string work, string scenario, Func<TagWriteLease, T> body, Func<T, string> describe, Func<T, bool> ok, string title)
        {
            if (_op != null) { MessageBox.Show(this, "\"" + _op + "\" is still running.", title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (_session != null) { MessageBox.Show(this, "A bake is running in this window.", title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string why = TagWriteGate.Blocked(work, name);
            if (why != null) { MessageBox.Show(this, why, title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SetOp(name);
            Task.Run(() =>
            {
                using (var lease = TagWriteGate.Enter(work, name, scenario)) return body(lease);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                SetOp(null);
                bool good = t.Status == TaskStatus.RanToCompletion && ok(t.Result);
                string m = t.Status == TaskStatus.RanToCompletion ? describe(t.Result) : name + " refused / failed: " + t.Exception.GetBaseException().Message;
                Log(good ? Palette.Good : Palette.Bad, name + ": " + m);
                if (_closeAfterOp) { Close(); return; }
                MessageBox.Show(this, m, title, MessageBoxButton.OK, good ? MessageBoxImage.Information : MessageBoxImage.Warning);
                RefreshMap();
            })));
        }

        private void RunTagRestore(string work, TagSet set, RestoreCandidate c, string name)
        {
            RunOp(name, work, set.ScenarioRel, lease => TagRestore.Restore(work, set, c, DateTime.Now, lease.LockContent), r => r.Message, r => r.Outcome == RestoreOutcome.Restored, "Restore previous lightmap");
        }

        private void SetOp(string name)
        {
            _op = name;
            if (name != null) { ShutdownBlock.Create(this, "H2 GPU Lightmapper is writing lightmap tags (" + name + ")"); SetStatus(Palette.Fg, name + " …"); }
            else ShutdownBlock.Destroy(this);
            UpdateActionButtons();
        }

        /// <summary>Restore / Undo / Repair / Bake / the options are usable only when nothing else runs or is attached.</summary>
        private void UpdateActionButtons()
        {
            bool free = !_busy && _op == null && _attachRunDir == null;
            RestoreBtn.IsEnabled = free;
            OptionsPanel.IsEnabled = free;
            BakeBtn.IsEnabled = free && _host.HasPayload;
            CancelBtn.IsEnabled = _busy || _attachRunDir != null;
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            bool tagWrite = _op != null || (_session != null && _session.Run != null && _session.Run.Status.InTagWrite);
            if (tagWrite) e.Cancel = true;      // Windows shows the ShutdownBlock reason; a forced shutdown still ends the process
        }

        // ================================================================== map

        private void ScenarioText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            string t = ScenarioCombo.Text ?? "";
            if (ScenarioCombo.SelectedItem is string sel && string.Equals(sel, t, StringComparison.OrdinalIgnoreCase)) return;
            _scenarioFilter = t.Trim();
            _scenarioView.Refresh();
            if (ScenarioCombo.IsKeyboardFocusWithin && !ScenarioCombo.IsDropDownOpen && _scenarioFilter.Length > 0) ScenarioCombo.IsDropDownOpen = true;
        }

        private void ScenarioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var s = ScenarioCombo.SelectedItem as string;
            if (s == null || _root == null) return;
            _scenarioFilter = "";
            var bsps = MapQuery.Bsps(_root, s);
            _suppress = true;
            BspCombo.ItemsSource = bsps;
            _suppress = false;
            string want = _g.Bsp ?? (string.Equals(s, _settings.LastScenario, StringComparison.OrdinalIgnoreCase) ? _settings.LastBsp : null);
            BspCombo.SelectedItem = bsps.FirstOrDefault(b => string.Equals(b, want, StringComparison.OrdinalIgnoreCase)) ?? bsps.FirstOrDefault();
            if (bsps.Count == 0) MapInfo.Text = "No .scenario_structure_bsp in this scenario's folder.";
        }

        private void BspCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            RefreshMap();
        }

        private string CurScenario { get { return ScenarioCombo.SelectedItem as string; } }
        private string CurBsp { get { return BspCombo.SelectedItem as string; } }

        /// <summary>Info line: probe points, lightmap + environments, the last capture, the last bake. Then the reuse gate.</summary>
        private void RefreshMap()
        {
            string root = _root, scen = CurScenario, bsp = CurBsp;
            if (root == null || scen == null || bsp == null) return;
            int ver = ++_mapVersion;
            _mapInfo = null;
            MapInfo.Text = "Reading the map…";
            var rt = _rt; var env = _env;
            Task.Run(() =>
            {
                MapInfo mi = null;
                if (rt != null && _runtimeReady)
                {
                    var set = TagSet.For(root, scen, bsp);
                    var args = MapQuery.Args(Path.Combine(rt.InstDir, InstLayout.TagIoDir), set.ScenarioFile, bsp, Path.Combine(set.TagDir, set.LtmpName));
                    try { mi = MapQuery.Run(rt.PyExe, args, rt.Work, env); } catch (Exception ex) { mi = new MapInfo { Error = ex.Message }; }
                }
                var hist = History.Load(ShellPaths.WorkOf(root));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ver != _mapVersion) return;
                    _mapInfo = mi;
                    if (mi != null && mi.Bsps.Count > 0)
                    {
                        var ordered = MapQuery.Bsps(root, scen, mi.Bsps);
                        var cur = BspCombo.ItemsSource as List<string>;
                        if (cur == null || !ordered.SequenceEqual(cur, StringComparer.OrdinalIgnoreCase))
                        {
                            _suppress = true; BspCombo.ItemsSource = ordered; BspCombo.SelectedItem = ordered.FirstOrDefault(b => string.Equals(b, bsp, StringComparison.OrdinalIgnoreCase)) ?? ordered.FirstOrDefault(); _suppress = false;
                        }
                    }
                    MapInfo.Text = MapInfoText(mi, hist, scen, bsp);
                    UpdateOptionUi();
                    RefreshReuse();
                }));
            });
        }

        private static string MapInfoText(MapInfo mi, List<HistoryEntry> hist, string scen, string bsp)
        {
            var parts = new List<string>();
            if (mi == null) parts.Add("probe points: (runtime not ready)");
            else if (!mi.Ok) parts.Add("map query failed: " + (mi.Error ?? mi.PointsError));
            else
            {
                parts.Add(mi.Points == 0 ? "no light probe points" : mi.Points + " light probe point(s)");
                parts.Add(mi.LtmpExists ? "lightmap present" + (mi.Envs.HasValue ? " (" + mi.Envs + " lighting environment(s))" : "") : "no lightmap yet");
            }
            var last = History.Last(hist, scen, bsp);
            if (last != null) parts.Add("last bake " + last.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + last.Quality + " " + last.State.ToLowerInvariant() + " (" + History.Dur(last.Seconds) + ")");
            return string.Join("  ·  ", parts);
        }

        /// <summary>The reuse checkbox: enabled only when the capture record matches (section 7), otherwise disabled with the reason.</summary>
        private void RefreshReuse()
        {
            string root = _root, scen = CurScenario, bsp = CurBsp;
            if (root == null || scen == null || bsp == null || !_host.HasPayload) return;
            var o = ReadOptions(out _);
            int ver = ++_reuseVersion;
            ReuseReason.Text = "Checking the capture…";
            int points = _mapInfo != null && _mapInfo.Points.HasValue ? _mapInfo.Points.Value : 0;
            Task.Run(() =>
            {
                string work = ShellPaths.WorkOf(root);
                var set = TagSet.For(root, scen, bsp);
                var tags = new Lazy<TagsFingerprint>(() => TagsFingerprint.Compute(root, set));
                bool probes = o.Mode == BakeMode.Probes;
                int? w = WorkerPeek.Registered(work, scen, bsp, probes);
                string engineRel = BakePlanner.GpuBakeRel(o, _host.Manifest);
                ReuseVerdict v = null;
                string extra = null;
                if (w.HasValue)
                {
                    v = CaptureRecord.Evaluate(BakePlanner.FarmDir(work, w.Value), CaptureIdentity.For(o, _host.Manifest, root, engineRel, probes), scen, bsp, probes, () => tags.Value);
                    if (o.Mode == BakeMode.Standard && points > 0)
                    {
                        int? m = WorkerPeek.Registered(work, scen, bsp, true);
                        var vm = m.HasValue ? CaptureRecord.Evaluate(BakePlanner.FarmDir(work, m.Value), CaptureIdentity.For(o, _host.Manifest, root, engineRel, true), scen, bsp, true, () => tags.Value) : null;
                        extra = vm != null && vm.Allowed ? "the probe capture too" : "the probe pass makes a new probe capture";
                    }
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ver != _reuseVersion) return;
                    bool ok = v != null && v.Allowed;
                    _suppress = true;
                    ReuseCheck.IsEnabled = ok && !_busy;
                    if (!ok) ReuseCheck.IsChecked = false;
                    _suppress = false;
                    ReuseReason.Text = v == null ? "No capture of this map yet (a new one is made)." : ok ? v.Describe().Replace("reusable: ", "Reusable: ") + (extra != null ? "; " + extra : "") : "Not reusable: " + v.Reason;
                    ReuseReason.Foreground = ok && v.TagsChanged ? Palette.Warn : Palette.Dim;
                }));
            });
        }

        // ================================================================== options

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            UpdateOptionUi();
            if (!_suppress && (sender == QLow || sender == QSuper || sender == QCustom || sender == MStandard || sender == MProbes || sender == MLightmap)) RefreshReuse();
        }

        private void Number_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsInitialized) return;
            UpdateOptionUi();
        }

        private void PhotonsBox_LostFocus(object sender, RoutedEventArgs e)
        {
            long p;
            if (long.TryParse((PhotonsBox.Text ?? "").Replace(",", "").Replace("_", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p))
                PhotonsBox.Text = p.ToString("N0", CultureInfo.InvariantCulture);
        }

        private void ResetCustom_Click(object sender, RoutedEventArgs e)
        {
            PhotonsBox.Text = BakeOptions.PhotonsDefault.ToString("N0", CultureInfo.InvariantCulture);
            RaysBox.Text = BakeOptions.RaysDefault.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The options as the UI shows them (parse errors listed; range errors come from BakeOptions.Validate).</summary>
        private BakeOptions ReadOptions(out List<string> errors)
        {
            errors = new List<string>();
            var o = BakeOptions.Defaults();
            o.Root = _root; o.ScenarioRel = CurScenario; o.Bsp = CurBsp;
            o.Quality = QLow.IsChecked == true ? Quality.Low : QCustom.IsChecked == true && QCustom.Visibility == Visibility.Visible ? Quality.Custom : Quality.Super;
            long p; int r; double d;
            if (long.TryParse((PhotonsBox.Text ?? "").Replace(",", "").Replace("_", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) o.Photons = p;
            else if (o.Quality == Quality.Custom) { errors.Add("photons: not a whole number"); o.Photons = -1; }
            if (int.TryParse((RaysBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) o.Rays = r;
            else if (o.Quality == Quality.Custom) { errors.Add("gather rays: not a whole number"); o.Rays = -1; }
            o.Mode = MProbes.IsChecked == true ? BakeMode.Probes : MLightmap.IsChecked == true ? BakeMode.Lightmap : BakeMode.Standard;
            o.Ao = AoCheck.IsChecked == true && o.Mode != BakeMode.Probes;
            if (double.TryParse((AoStrengthBox.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) o.AoStrength = d;
            else if (o.Ao) { errors.Add("AO strength: not a number"); o.AoStrength = double.NaN; }
            if (double.TryParse((AoExponentBox.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) o.AoExponent = d;
            else if (o.Ao) { errors.Add("AO exponent: not a number"); o.AoExponent = double.NaN; }
            o.ReuseCapture = ReuseCheck.IsChecked == true && ReuseCheck.IsEnabled;
            o.StageOnly = StageOnlyCheck.IsChecked == true;
            return o;
        }

        private bool _busy;

        private void UpdateOptionUi()
        {
            if (!IsInitialized || CustomPanel == null) return;
            bool custom = QCustom.IsChecked == true && QCustom.Visibility == Visibility.Visible;
            CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            bool probesMode = MProbes.IsChecked == true;
            AoCheck.IsEnabled = !probesMode && !_busy && _aoAvailable;
            AoPanel.IsEnabled = AoCheck.IsChecked == true && !probesMode && !_busy && _aoAvailable;
            AoNote.Text = !_aoAvailable ? "Not available in this build (its AO engine is out of date for the lightmap engine)." : probesMode ? "Not available in Lightprobes mode." : "An artistic layer, not CPU-faithful.";
            AoNote.ToolTip = !_aoAvailable ? _aoWhy : null;
            // the AO / custom field checks
            List<string> perr;
            var o = ReadOptions(out perr);
            MarkBox(PhotonsBox, custom && (o.Photons < BakeOptions.PhotonsMin || o.Photons > BakeOptions.PhotonsMax));
            MarkBox(RaysBox, custom && (o.Rays < BakeOptions.RaysMin || o.Rays > BakeOptions.RaysMax));
            MarkBox(AoStrengthBox, o.Ao && !(o.AoStrength >= BakeOptions.AoStrengthMin && o.AoStrength <= BakeOptions.AoStrengthMax));
            MarkBox(AoExponentBox, o.Ao && !(o.AoExponent >= BakeOptions.AoExponentMin && o.AoExponent <= BakeOptions.AoExponentMax));
            if (custom)
            {
                if (o.Rays >= BakeOptions.RaysMin && o.Rays <= BakeOptions.RaysMax)
                {
                    long rpc = BakeOptions.RaysPerCall(o.Rays);
                    double x = rpc / (double)BakeOptions.RaysPerCall(BakeOptions.SuperMc);
                    RaysLine.Text = string.Format(CultureInfo.InvariantCulture, "MC {0} → {1:N0} rays per gather call ({2}× Super's 128)", o.Rays, rpc, x >= 1 ? x.ToString("0.##", CultureInfo.InvariantCulture) : x.ToString("0.###", CultureInfo.InvariantCulture));
                }
                else RaysLine.Text = "Gather rays must be 1 – 64.";
                UpdateCost(o);
            }
            // Lightmap Only on a probe BSP
            int pts = _mapInfo != null && _mapInfo.Points.HasValue ? _mapInfo.Points.Value : 0;
            if (MLightmap.IsChecked == true && pts > 0) { ModeNotice.Text = "Resets this BSP's " + pts + " light probes to zero, as `tool lightmaps` does."; ModeNotice.Visibility = Visibility.Visible; }
            else if (MProbes.IsChecked == true && _mapInfo != null && _mapInfo.Ok && pts == 0) { ModeNotice.Text = "This BSP has no light probe points."; ModeNotice.Visibility = Visibility.Visible; }
            else if (MStandard.IsChecked == true && pts > 0 && !_host.ProbesEnabled)
            {
                // review fix (finding 14): never a silent downgrade -- Standard on a probe BSP is refused unless this build's payload
                // passed gate T4 (V5: MANIFEST features.probes)
                ModeNotice.Text = "Light probes are not released in this build (gate T4): on this BSP (" + pts + " probe points) Standard is refused. Use Lightmap Only, which resets its " + pts + " probes to zero, as `tool lightmaps` does.";
                ModeNotice.Visibility = Visibility.Visible;
            }
            else ModeNotice.Visibility = Visibility.Collapsed;
            if (!_busy) BakeBtn.IsEnabled = _host.HasPayload && _attachRunDir == null && _op == null;
        }

        private static void MarkBox(TextBox b, bool bad) { b.BorderBrush = bad ? Palette.Bad : Palette.Edge; }

        private void UpdateCost(BakeOptions o)
        {
            if (_root == null || CurScenario == null || CurBsp == null || o.Photons < BakeOptions.PhotonsMin || o.Photons > BakeOptions.PhotonsMax || o.Rays < 1 || o.Rays > 64)
            { CostLine.Text = ""; return; }
            var h = History.Load(ShellPaths.WorkOf(_root));
            string basis;
            var eta = History.CustomEta(h, CurScenario, CurBsp, o.Photons, o.Rays, out basis);
            if (eta == null) { CostLine.Text = "ETA: unknown until this map has a Super bake with a new capture."; CostLine.Foreground = Palette.Dim; return; }
            bool long1 = eta.Value > 3600, long8 = eta.Value >= 8 * 3600;
            string warn = long8 ? ".  That is 8 hours or more: a PC that shuts itself down after 8 hours would stop this bake, and it would have to start again."
                        : long1 ? ".  Over an hour: if your PC shuts itself down after a set time (some do after 8 hours), start long bakes early."
                        : "";
            CostLine.Text = "ETA about " + History.Dur(eta.Value) + " (" + basis + ")" + warn;
            CostLine.Foreground = long8 ? Palette.Bad : long1 ? Palette.Warn : Palette.Dim;
        }

        // ================================================================== bake

        private void BakeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_op != null || _session != null) return;
            List<string> perr;
            var o = ReadOptions(out perr);
            var errs = perr.Concat(o.Validate(_host.CustomAvailable)).Distinct().ToList();
            if (errs.Count > 0)
            {
                if (_g.Snapshot != null) { Log(Palette.Bad, "cannot bake yet: " + string.Join("; ", errs)); return; }   // UI test: never a dialog
                MessageBox.Show(this, string.Join("\n", errs), "Cannot bake yet", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!o.StageOnly && _g.Snapshot == null && MessageBox.Show(this, "Bake " + o.ScenarioRel + " / " + o.Bsp + "?\n\n" + o.Summary() + "\n\nThe current lightmap is backed up first; Restore previous lightmap puts it back.",
                                                 "H2 GPU Lightmapper", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try
            {
                _settings.Remember(o.Root);
                _settings.LastScenario = o.ScenarioRel; _settings.LastBsp = o.Bsp;
                _settings.Save();
            }
            catch (Exception ex) { Log(Palette.Warn, "settings.json not saved: " + ex.Message); }
            var s = new BakeSession(_host, o, new SessionOptions { SkipPreflight = _g.SkipPreflight, SkipGpuCheck = _g.SkipGpuCheck });
            s.Info += m => Enqueue(new LogItem { Text = m, Brush = m.StartsWith("REFUSED", StringComparison.Ordinal) ? Palette.Bad : Palette.Dim });
            s.Line += l => Enqueue(new LogItem { Text = l.IsErr ? "ERR| " + l.Text : l.Text, Brush = Palette.ForLine(l) });
            s.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => OnRunStatus(st)));
            s.Planned += p => Dispatcher.BeginInvoke(new Action(() => OnPlanned(p)));
            s.Ask = AskOnUi;
            _session = s;
            _bakeStarted = DateTime.UtcNow;
            var hist = History.Load(_work);
            string basis;
            _eta = o.Quality == Quality.Custom ? History.CustomEta(hist, o.ScenarioRel, o.Bsp, o.Photons, o.Rays, out basis) : History.PlainEta(hist, o.ScenarioRel, o.Bsp, o.QualityToken);
            _planSteps = null;
            foreach (var c in _chips) c.State = ChipState.Pending;
            _chips[0].State = ChipState.Active;
            Bar.Value = 0;
            SetBusy(true);
            SetStatus(Palette.Fg, "Preparing: preflight, runtime check, map query…");
            Task.Run(() => s.Execute()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => OnBakeDone(s, t.Status == TaskStatus.RanToCompletion ? t.Result : 3, t.Exception))));
        }

        private bool AskOnUi(Prompt p)
        {
            if (_g.Snapshot != null) { Enqueue(new LogItem { Text = "question (UI test: answered no): " + p.Title, Brush = Palette.Warn }); return false; }
            return (bool)Dispatcher.Invoke(new Func<bool>(() =>
            {
                string det = p.Details.Count > 0 ? "\n\n" + string.Join("\n", p.Details.Take(25)) + (p.Details.Count > 25 ? "\n…" : "") : "";
                return MessageBox.Show(this, p.Message + det, p.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }));
        }

        private void OnPlanned(BakePlan p)
        {
            _planSteps = p.UiSteps;
            foreach (var c in _chips) if (!_planSteps.Contains(c.Step)) c.State = ChipState.Skipped;
        }

        private void OnRunStatus(RunStatus st)
        {
            StepId cur;
            if (st.Step != null && Enum.TryParse(st.Step, out cur))
            {
                // the step list follows the StepId order: plan steps before the current one are done (a fast phase may never
                // have been observed as current), later ones pending, steps outside the plan skipped
                foreach (var c in _chips)
                {
                    if (_planSteps != null && !_planSteps.Contains(c.Step) && c.Step != cur) c.State = ChipState.Skipped;
                    else if (c.Step < cur) c.State = ChipState.Done;
                    else if (c.Step == cur) c.State = st.IsFinal && st.State != RunState.Done ? ChipState.Failed : ChipState.Active;
                    else c.State = ChipState.Pending;
                }
                var order = (_planSteps ?? _chips.Select(c => c.Step).ToList());
                double total = order.Sum(StepChip.Weight), before = 0;
                foreach (var s in order) { if (s == cur) break; before += StepChip.Weight(s); }
                Bar.Value = total > 0 ? 1000 * before / total : 0;
            }
            if (st.State == RunState.Waiting) SetStatus(Palette.Warn, "Waiting for " + (st.WaitingFor ?? "the GPU lock") + " …");
            else if (!st.IsFinal) SetStatus(Palette.Fg, (st.CancelRequested ? (st.CancelDeferred ? "Cancelling after the tag write finishes… " : "Cancelling… ") : "")
                                              + (st.Step ?? "") + (st.Phase != null ? "  [" + st.Phase + "] " + st.PhaseTitle : ""));
            double el = (DateTime.UtcNow - _bakeStarted).TotalSeconds;
            EtaText.Text = History.Dur(el) + (_eta.HasValue ? " / ~" + History.Dur(_eta.Value) : "");
        }

        private void OnBakeDone(BakeSession s, int code, Exception ex)
        {
            SetBusy(false);
            _session = null;
            string word = CliMain.ResultWord(code);
            string msg = word + (s.Reason != null ? ": " + s.Reason : "") + (ex != null ? " (" + ex.GetBaseException().Message + ")" : "");
            SetStatus(code == 0 ? Palette.Good : code == 4 ? Palette.Warn : Palette.Bad, msg);
            foreach (var c in _chips)
            {
                if (c.State == ChipState.Active) c.State = code == 0 ? ChipState.Done : ChipState.Failed;
                else if (code == 0 && c.State == ChipState.Pending) c.State = ChipState.Done;
            }
            if (code == 0) Bar.Value = 1000;
            Log(code == 0 ? Palette.Good : Palette.Bad, "RESULT " + msg + (s.Run != null ? "   (run folder " + s.Run.RunDir + ")" : ""));
            if (_g.AutoBake && _g.Snapshot != null)
            {
                FlushLog();
                Dispatcher.BeginInvoke(new Action(() => { SaveSnapshot(_g.Snapshot); Close(); }), DispatcherPriority.ApplicationIdle);
                return;
            }
            if (_closeAfterBake) { Close(); return; }
            RefreshMap();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateActionButtons();
            UpdateOptionUi();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_session != null) { _session.RequestCancel(); SetStatus(Palette.Warn, "Cancelling… (a tag write in progress finishes first)"); return; }
            if (_attachRunDir != null)
            {
                try
                {
                    Core.Util.SafeFs.WriteAllTextAtomic(Path.Combine(_attachRunDir, BakeSession.CancelRequestFile), "cancel requested from another window " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\r\n");
                    SetStatus(Palette.Warn, "Cancel requested; the window that runs the bake stops it.");
                }
                catch (Exception ex) { SetStatus(Palette.Bad, "Could not request the cancel: " + ex.Message); }
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_op != null)
            {
                // review fix (finding 7): a restore / undo / repair is writing; closing now would leave a half-written set
                _closeAfterOp = true;
                e.Cancel = true;
                SetStatus(Palette.Warn, "\"" + _op + "\" is writing the lightmap tags -- the window closes as soon as it has finished.");
                return;
            }
            if (_session == null) { DetachView(); return; }
            if (MessageBox.Show(this, "A bake is running. Cancel it and close the window when it has stopped?\n(A tag write in progress always finishes first.)", "H2 GPU Lightmapper",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _closeAfterBake = true;
                _session.RequestCancel();
            }
            e.Cancel = true;
        }

        // ================================================================== restore / undo / repair / folders

        private bool MapPicked(out string root, out string scen, out string bsp)
        {
            root = _root; scen = CurScenario; bsp = CurBsp;
            if (root != null && scen != null && bsp != null) return true;
            MessageBox.Show(this, "Pick the H2EK, the scenario and the BSP first.", "H2 GPU Lightmapper", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        private void RestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            string root, scen, bsp;
            if (!MapPicked(out root, out scen, out bsp)) return;
            string work = ShellPaths.WorkOf(root);
            var set = TagSet.For(root, scen, bsp);
            // review fix (finding 2): refused while a bake runs (another window, the launcher, research) -- checked again under the lock
            string why = TagWriteGate.Blocked(work, "restore previous lightmap");
            if (why != null) { MessageBox.Show(this, why, "Restore previous lightmap", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var cands = TagRestore.Candidates(work, set);
            if (cands.Count == 0) { MessageBox.Show(this, "There is no backup of " + set.Prefix + " yet (backups are made before every bake that installs).", "Restore", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            // review fix (finding 5): pick the backup; the default is the newest one that differs from the live set
            var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var picker = new RestorePicker(this, "Restore previous lightmap — " + scen + " / " + bsp, cands, c => TagRestore.IsNoOp(set, c, live), TagRestore.Newest(work, set));
            if (picker.ShowDialog() != true || picker.Chosen == null) return;
            RunTagRestore(work, set, picker.Chosen, "restore previous lightmap");
        }

        private void UndoInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            string root, scen, bsp;
            if (!MapPicked(out root, out scen, out bsp)) return;
            if (_rt == null) { MessageBox.Show(this, "The runtime is not ready yet.", "Undo last install"); return; }
            string work = ShellPaths.WorkOf(root);
            var last = CliMain.LastInstall(work, scen, bsp);
            if (last == null) { MessageBox.Show(this, "This app has not installed a lightmap for " + scen + " / " + bsp + " (or its install backup is gone).", "Undo last install"); return; }
            var set = TagSet.For(root, scen, bsp);
            if (MessageBox.Show(this, "Put back the files the install of run " + last.RunTag + " replaced?\n\nfrom " + last.InstallBackupDir, "Undo last install", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var req = new TagRestoreRequest
            {
                PyExe = _rt.PyExe, LmwInstallPy = _rt.LmwInstallPy, Root = root, Work = work, BackupDir = last.InstallBackupDir, TargetDir = set.TagDir,
                Marker = last.Marker != null && File.Exists(last.Marker) ? last.Marker : null, Env = _env,
            };
            RunOp("undo last install", work, scen, lease => TagInstaller.Restore(req), r => r.Outcome + ": " + r.Message, r => r.Outcome == InstallOutcome.Installed, "Undo last install");
        }

        private void RepairBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null || !_host.HasPayload) return;
            string root = _root;
            RunOp("repair runtime", ShellPaths.WorkOf(root), null, lease => _host.Installer.RepairEverything(root),
                  r => r.Ok ? "The runtime is intact (or was repaired)." : "Still damaged: " + r, r =>
                  {
                      if (r.Ok) _runtimeReady = true;
                      return r.Ok;
                  }, "Repair runtime");
        }

        private void LogsBtn_Click(object sender, RoutedEventArgs e)
        {
            string dir = _session != null && _session.Run != null ? _session.Run.RunDir : _attachRunDir;
            if (dir == null && _work != null && CurScenario != null && CurBsp != null)
            {
                string runs = RunFolder.RunsDir(_work);
                string tail = "_" + RunFolder.ScenarioLeaf(CurScenario) + "_" + CurBsp;
                if (Directory.Exists(runs)) dir = Directory.GetDirectories(runs).Where(d => d.EndsWith(tail, StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            }
            if (dir == null && _work != null) dir = RunFolder.RunsDir(_work);
            OpenFolder(dir);
        }

        private void OpenWorkBtn_Click(object sender, RoutedEventArgs e) { OpenFolder(_work); }

        private void OpenFolder(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) { MessageBox.Show(this, "The folder does not exist yet: " + (dir ?? "(none)"), "H2 GPU Lightmapper"); return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "H2 GPU Lightmapper"); }
        }

        // ================================================================== re-attach (a bake running in another window)

        private void AttachView(CurrentRun cur)
        {
            _attachRunDir = cur.RunDir;
            _attachTail = new LogTail(Path.Combine(cur.RunDir, "bake.log"));
            Log(Palette.Warn, "another window is baking on this H2EK (run " + cur.RunTag + "): showing it read-only");
            // review fix (finding 2): no Bake, Restore, Undo or Repair while the other window's bake may be writing these tags
            UpdateActionButtons();
            _attachTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _attachTimer.Tick += (s, e) => PollAttach();
            _attachTimer.Start();
            PollAttach();
        }

        private void PollAttach()
        {
            if (_attachRunDir == null) return;
            try
            {
                foreach (var l in _attachTail.ReadNewLines()) Enqueue(new LogItem { Text = l, Brush = l.StartsWith("=== ", StringComparison.Ordinal) ? Palette.Dim : l.StartsWith("ERR| ", StringComparison.Ordinal) ? Palette.ErrLine : Palette.Plain });
                var st = RunStatus.Load(_attachRunDir);
                if (st != null)
                {
                    OnRunStatus(st);
                    if (st.IsFinal)
                    {
                        SetStatus(st.State == RunState.Done ? Palette.Good : Palette.Warn, "The other window's bake ended: " + st.State + (st.Reason != null ? ": " + st.Reason : ""));
                        DetachView();
                        UpdateOptionUi();
                        RefreshMap();
                    }
                }
            }
            catch (Exception) { }
        }

        private void DetachView()
        {
            if (_attachTimer != null) { _attachTimer.Stop(); _attachTimer = null; }
            _attachRunDir = null;
            _attachTail = null;
            UpdateActionButtons();
        }

        // ================================================================== log + status

        private void Enqueue(LogItem it) { _logQ.Enqueue(it); }
        private void Log(Brush b, string text) { _logQ.Enqueue(new LogItem { Text = text, Brush = b }); }

        private void FlushLog()
        {
            if (_logQ.IsEmpty) return;
            var sv = FindScroll(LogList);
            bool atEnd = sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 1;
            int n = 0;
            LogItem it;
            while (n < 3000 && _logQ.TryDequeue(out it)) { _log.Add(it); n++; }
            while (_log.Count > 5000) _log.RemoveAt(0);
            if (atEnd && _log.Count > 0) LogList.ScrollIntoView(_log[_log.Count - 1]);
        }

        private static ScrollViewer FindScroll(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var r = FindScroll(VisualTreeHelper.GetChild(d, i));
                if (r != null) return r;
            }
            return null;
        }

        private void SetStatus(Brush b, string text)
        {
            StatusLine.Text = text;
            StatusLine.Foreground = b;
        }
    }
}
