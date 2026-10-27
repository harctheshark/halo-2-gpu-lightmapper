// Milestone 2: the window's ".map file" source (direct .map baking). A partial of MainWindow so the large file keeps its shape: the source selector, the map
// picker + output, the BSP list from the map, one cube-map dropdown per sky that needs a choice, and the bake itself through MapBakeSession.
// The .scenario source is untouched: with "Scenario" ticked nothing here runs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Cli;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Pipeline;
using H2GpuLightmapper.Ui;
using H2Lm.Proc;

namespace H2GpuLightmapper
{
    public partial class MainWindow
    {
        private MapBakeSession _mapSession;
        private MapQueryResult _mapQuery;
        private string _mapPicked, _mapOutChosen;
        private bool _mapIsVista;
        private List<SkyChoice> _skyModel = new List<SkyChoice>();
        private readonly List<ComboBox> _skyBoxes = new List<ComboBox>();
        private int _mapFileVersion;

        private bool IsMapSource { get { return SrcMap != null && SrcMap.IsChecked == true; } }
        private bool BakeRunning { get { return _session != null || _mapSession != null; } }
        private BakeRun CurRun { get { return _session != null ? _session.Run : _mapSession != null ? _mapSession.Run : null; } }

        private void CancelBake()
        {
            if (_session != null) _session.RequestCancel();
            else if (_mapSession != null) _mapSession.RequestCancel();
        }

        // ================================================================== source selector

        private void Source_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || SrcMap == null || MapPanel == null) return;
            bool map = IsMapSource;
            MapPanel.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
            ScenarioLabel.Visibility = ScenarioCombo.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
            if (map)
            {
                _suppress = true; BspCombo.ItemsSource = null; _suppress = false;
                MapInfo.Text = _mapPicked == null ? "Pick a .map file." : "";
                if (_mapPicked != null) RefreshMapFile();
            }
            else
            {
                _suppress = true; BspCombo.ItemsSource = null; _suppress = false;
                MapInfo.Text = "";
                var s = CurScenario;
                if (s != null) ScenarioCombo_SelectionChanged(ScenarioCombo, null);
            }
            UpdateOptionUi();
        }

        /// <summary>Called at the end of UpdateOptionUi. Milestone 3: a .map bake offers the same mode / AO choices as a scenario; only "Reuse existing capture" and "Stage only"
        /// do not exist for it (every .map bake makes its own capture and always ends with the output map).</summary>
        private void ApplyMapModeUi()
        {
            if (SrcMap == null || MStandard == null) return;
            bool map = IsMapSource;
            if (map)
            {
                if (StageOnlyCheck.IsChecked == true) StageOnlyCheck.IsChecked = false;
                _suppress = true; ReuseCheck.IsChecked = false; _suppress = false;
            }
            bool free = !_busy;
            MStandard.IsEnabled = MProbes.IsEnabled = MLightmap.IsEnabled = free;
            if (map) { ReuseCheck.IsEnabled = false; ReuseReason.Text = "Not used when baking from a .map (every .map bake makes its own capture)."; }
            StageOnlyCheck.IsEnabled = !map && free;
            VistaOutCheck.IsEnabled = free;
            MapBrowseBtn.IsEnabled = MapOutBrowseBtn.IsEnabled = free;
            SkyPanel.IsEnabled = free;
            if (map) BakeBtn.IsEnabled = !_busy && _host.HasPayload && _attachRunDir == null && _op == null && _mapQuery != null && _mapQuery.Ok && _mapPicked != null && CurBsp != null;
        }

        // ================================================================== pick the map / the output

        private void MapBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick the .map to bake from", Filter = "Halo 2 map (*.map)|*.map|All files (*.*)|*.*", CheckFileExists = true };
            if (_mapPicked != null) { try { dlg.InitialDirectory = Path.GetDirectoryName(_mapPicked); } catch (Exception) { } }
            if (dlg.ShowDialog(this) != true) return;
            SetMapFile(dlg.FileName);
        }

        private void MapOutBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (_mapPicked == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the baked copy of the map as", Filter = "Halo 2 map (*.map)|*.map", DefaultExt = ".map", AddExtension = true,
                FileName = Path.GetFileName(BakeOptions.DefaultMapOut(_mapPicked, VistaToMccOnly)), InitialDirectory = Path.GetDirectoryName(_mapPicked), OverwritePrompt = false,
            };
            if (dlg.ShowDialog(this) != true) return;
            _mapOutChosen = dlg.FileName;
            ShowMapOut();
            UpdateOptionUi();
        }

        private bool VistaToMccOnly { get { return _mapIsVista && VistaOutCheck != null && VistaOutCheck.IsChecked != true; } }

        private void VistaOut_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || MapOutBox == null) return;
            ShowMapOut();
            UpdateOptionUi();
        }

        private void ShowMapOut()
        {
            if (_mapPicked == null) { MapOutBox.Text = ""; return; }
            string o = _mapOutChosen ?? BakeOptions.DefaultMapOut(_mapPicked, VistaToMccOnly);
            MapOutBox.Text = o;
            MapOutBox.Foreground = string.Equals(Path.GetFullPath(o), Path.GetFullPath(_mapPicked), StringComparison.OrdinalIgnoreCase) ? Palette.Bad : Palette.Fg;
        }

        /// <summary>A picked map: remember it, check its header at once (stock maps are refused in plain words; a Vista map shows the "Convert result back to Vista" box), then query it off the UI thread.</summary>
        public void SetMapFile(string path)
        {
            _mapPicked = path; _mapOutChosen = null; _mapQuery = null;
            MapPathBox.Text = path;
            ShowMapOut();
            RefreshMapFile();
        }

        private void RefreshMapFile()
        {
            ClearSkyPanel();
            if (_mapPicked == null) return;
            var hdr = MapHeaderInfo.Read(_mapPicked);
            _mapIsVista = hdr.IsVista;
            VistaOutCheck.Visibility = _mapIsVista ? Visibility.Visible : Visibility.Collapsed;      // shown for a Vista map only
            ShowMapOut();
            string why = hdr.Refusal();
            if (why != null)
            {
                _mapQuery = null;
                _suppress = true; BspCombo.ItemsSource = null; _suppress = false;
                MapInfo.Text = why; MapInfo.Foreground = Palette.Bad;
                UpdateOptionUi();
                return;
            }
            if (_rt == null || !_runtimeReady || _rt.MapbakePy == null || _root == null)
            {
                MapInfo.Text = _rt != null && _rt.MapbakePy == null ? "This build has no .map baking component." : "Waiting for the runtime …"; MapInfo.Foreground = Palette.Dim;
                UpdateOptionUi();
                return;
            }
            int ver = ++_mapFileVersion;
            MapInfo.Text = _mapIsVista ? "Reading the Vista map (converting it to its MCC form first: about half a minute) …" : "Reading the map …"; MapInfo.Foreground = Palette.Dim;
            var rt = _rt; var env = _env; string root = _root, map = _mapPicked, work = ShellPaths.WorkOf(_root);
            Task.Run(() =>
            {
                MapQueryResult q;
                try { q = MapBakeProcs.Query(rt, env, _host.Config.DevH2convHome, map, Path.Combine(root, "tags"), Path.Combine(ShellPaths.StandaloneOf(work), "mapbake", "kit_cubemaps.json")); }
                catch (Exception ex) { q = new MapQueryResult { Error = "the map could not be read: " + ex.Message }; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ver != _mapFileVersion) return;
                    _mapQuery = q;
                    if (q.Refusal != null || !q.Ok)
                    {
                        _suppress = true; BspCombo.ItemsSource = null; _suppress = false;
                        MapInfo.Text = q.Refusal ?? q.Error ?? "the map could not be read"; MapInfo.Foreground = Palette.Bad;
                        UpdateOptionUi();
                        return;
                    }
                    var bsps = q.Bsps.Select(b => BspChoice.Plain(b.Substring(b.LastIndexOf('\\') + 1))).ToList();
                    _suppress = true;
                    BspCombo.ItemsSource = bsps;
                    BspCombo.SelectedItem = bsps.FirstOrDefault();
                    _suppress = false;
                    _skyModel = CubemapModel.Build(q);
                    BuildSkyPanel();
                    MapInfo.Text = (q.InternalName ?? "map") + (q.IsVista ? "  ·  Vista map" : "") + "  ·  scenario " + q.Scenario + "  ·  " + q.Bsps.Count + " BSP(s)" + (q.Bsps.Count > 1 ? " — pick the one to bake" : "")
                                   + (q.SingleBspProbePoints.HasValue && q.SingleBspProbePoints.Value > 0 ? "  ·  " + q.SingleBspProbePoints.Value + " light probe point(s)" : "")
                                   + (_skyModel.Count > 0 ? "  ·  " + _skyModel.Count + " sky(ies) need a cube map choice" : "");
                    MapInfo.Foreground = Palette.Dim;
                    UpdateOptionUi();
                }));
            });
        }

        private void ClearSkyPanel()
        {
            SkyPanel.Children.Clear(); _skyBoxes.Clear(); _skyModel = new List<SkyChoice>();
        }

        /// <summary>One dropdown per sky that needs a choice: "Leave empty" + the automatic match + every cube-map bitmap of the kit + the map's own; the automatic match is preselected,
        /// else "Leave empty" with the plain warning underneath.</summary>
        private void BuildSkyPanel()
        {
            SkyPanel.Children.Clear(); _skyBoxes.Clear();
            foreach (var c in _skyModel)
            {
                var head = new TextBlock { Text = "Sky cube map — " + c.Sky, Margin = new Thickness(0, 4, 0, 2), TextWrapping = TextWrapping.Wrap };
                head.SetResourceReference(TextBlock.StyleProperty, "FieldLabel");
                var box = new ComboBox { ItemsSource = c.Items, SelectedItem = c.Selected, MaxDropDownHeight = 320, Tag = c };
                box.ToolTip = "Compiled maps lose the sky's cube map reference. Pick the cube map the map was made with, or leave it empty (the bake then lacks the sky's ambient fill).";
                var warn = new TextBlock { Text = c.Warning ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Palette.Warn, Margin = new Thickness(0, 2, 0, 0) };
                warn.Visibility = c.Warning == null ? Visibility.Collapsed : Visibility.Visible;
                box.SelectionChanged += (s, e) =>
                {
                    var it = box.SelectedItem as CubemapItem;
                    if (it == null) return;
                    c.Selected = it;
                    bool empty = it.Kind == CubemapKind.LeaveEmpty;
                    warn.Text = empty ? (c.AutoItem == null ? CubemapModel.NoAutoWarning : "Left empty on purpose: the bake will lack the sky's ambient fill (about 30% darker on headlong).") : "";
                    warn.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                };
                _skyBoxes.Add(box);
                SkyPanel.Children.Add(head); SkyPanel.Children.Add(box); SkyPanel.Children.Add(warn);
            }
        }

        // ================================================================== options + bake

        /// <summary>Called by ReadOptions for the .map source: the scenario fields are replaced by the map, the output, the Vista choice and the sky choices; mode and AO are the window's.</summary>
        private void ApplyMapOptions(BakeOptions o)
        {
            o.ScenarioRel = null; o.BspRel = null;
            o.MapPath = _mapPicked ?? "";
            o.MapOut = _mapOutChosen;
            o.MapIsVista = _mapIsVista; o.VistaOut = VistaOutCheck == null || VistaOutCheck.IsChecked == true; o.VistaOutGiven = false;
            o.ReuseCapture = false; o.StageOnly = false;
            o.SkyCubemaps = _skyModel.Select(c => new KeyValuePair<string, string>(c.Sky, c.Selected == null ? "none" : c.Selected.Value)).ToList();
        }

        private void StartMapBake(BakeOptions o)
        {
            if (_g.Snapshot == null)
            {
                var skyLines = CubemapModel.Describe(_skyModel);
                if (MessageBox.Show(this, "Bake " + Path.GetFileName(o.MapPath) + "?\n\n" + o.Summary() + "\n\nOutput: " + o.MapOutPath
                                    + "\nThe map you picked is not modified; the result is a copy with only the lightmap" + (o.Mode == BakeMode.Probes ? " (with its light probes)" : "") + " replaced."
                                    + (o.MapIsVista ? (o.VistaOut ? "\nThis is a Vista map: it is converted to MCC form for the bake and the result is converted back to Vista (a few extra minutes)."
                                                                  : "\nThis is a Vista map: it is converted to MCC form for the bake; the result stays an MCC map (not converted back).") : "")
                                    + (File.Exists(o.MapOutPath) ? "\nThe existing output is set aside as .old_<time> first." : "")
                                    + (skyLines.Count > 0 ? "\n\n" + string.Join("\n", skyLines) : "")
                                    + "\n\nTemporary work files are written while it runs (about 35 GB of free disk space are needed) and deleted afterwards.",
                                    "H2 GPU Lightmapper", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            }
            var s = new MapBakeSession(_host, o, new SessionOptions { SkipPreflight = _g.SkipPreflight, SkipGpuCheck = _g.SkipGpuCheck });
            s.Info += m => Enqueue(new LogItem { Text = m, Brush = m.StartsWith("REFUSED", StringComparison.Ordinal) ? Palette.Bad : m.StartsWith("==", StringComparison.Ordinal) ? Palette.Accent : Palette.Dim });
            s.Line += l => Enqueue(new LogItem { Text = l.IsErr ? "ERR| " + l.Text : l.Text, Brush = Palette.ForLine(l) });
            s.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => OnRunStatus(st)));
            s.Planned += p => Dispatcher.BeginInvoke(new Action(() => OnPlanned(p)));
            s.Phase += p => Dispatcher.BeginInvoke(new Action(() => SetStatus(Palette.Fg, p)));
            s.Ask = AskOnUi;
            _mapSession = s;
            _bakeStarted = DateTime.UtcNow;
            _eta = null;
            _planSteps = null;
            foreach (var c in _chips) c.State = ChipState.Pending;
            _chips[0].State = ChipState.Active;
            Bar.Value = 0;
            SetBusy(true);
            SetStatus(Palette.Fg, "Preparing: reading the map …");
            Task.Run(() => s.Execute()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => OnMapBakeDone(s, t.Status == TaskStatus.RanToCompletion ? t.Result : 3, t.Exception))));
        }

        private void OnMapBakeDone(MapBakeSession s, int code, Exception ex)
        {
            SetBusy(false);
            _mapSession = null;
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
            if (s.PrecisionSummary != null) Log(Palette.Dim, s.PrecisionSummary);
            if (_closeAfterBake) { Close(); return; }
        }
    }
}
