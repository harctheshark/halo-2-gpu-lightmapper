// 0.6.8 bounded temp (docs\design\DISK_USAGE_0.6.8.md 5.5): the window's Advanced rows "Temp folder [path] size [Open] [Clean now]",
// "Keep captures for reuse [n]" and "Temp size cap [n] GB", the opt-in "Free space from earlier versions" button, and the start-up sweep
// after the recovery of a picked H2EK. A partial of MainWindow so the large file keeps its shape.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Ui;

namespace H2GpuLightmapper
{
    public partial class MainWindow
    {
        private TempSettings _temp = new TempSettings();
        private bool _tempUiReady;
        private int _tempVersion;

        /// <summary>Called once from the constructor: the two settings from settings.json (keepCaptures / tempCapGB).</summary>
        private void InitTempUi()
        {
            _temp = TempSettings.From(_settings);
            KeepCapturesCombo.ItemsSource = Enumerable.Range(0, TempSettings.MaxKeepCaptures + 1).ToList();
            KeepCapturesCombo.SelectedItem = _temp.KeepCaptures;
            TempCapBox.Text = _temp.CapGB.ToString(CultureInfo.InvariantCulture);
            KeepCapturesCombo.ToolTip = "How many captures stay in the temporary folder for \"Reuse existing capture\" (the newest ones; 0 = none). Default " + TempSettings.DefaultKeepCaptures + ".";
            TempCapBox.ToolTip = "The temporary folder's size cap in GB (" + TempSettings.MinCapGB + "-" + TempSettings.MaxCapGB + "; default " + TempSettings.DefaultCapGB + "): above it the oldest folders are deleted after a bake.";
            _tempUiReady = true;
        }

        private void SaveTempSettings()
        {
            try { _temp.SaveTo(_settings); _settings.Save(); } catch (Exception e) { Log(Palette.Warn, "settings: " + e.Message); }
        }

        private void KeepCaptures_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_tempUiReady || !(KeepCapturesCombo.SelectedItem is int)) return;
            _temp.KeepCaptures = TempSettings.ClampKeep((int)KeepCapturesCombo.SelectedItem);
            SaveTempSettings();
        }

        private void TempCap_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_tempUiReady) return;
            int v;
            if (!int.TryParse((TempCapBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = _temp.CapGB;
            _temp.CapGB = TempSettings.ClampCap(v);
            TempCapBox.Text = _temp.CapGB.ToString(CultureInfo.InvariantCulture);
            SaveTempSettings();
        }

        /// <summary>The Temp row of the picked H2EK: its path, its size (a no-follow walk on a background thread) and whether earlier versions left folders.</summary>
        private void RefreshTempUi()
        {
            string work = _work;
            if (work == null) return;
            int ver = ++_tempVersion;
            TempBox.Text = ShellPaths.TempOf(work);
            TempSizeText.Text = "…";
            Task.Run(() =>
            {
                long b = TempJanitor.TempSize(work);
                int legacy = 0;
                try { legacy = TempJanitor.ListLegacy(work, _host.HasPayload ? _host.Installer : null, _root, false).Count; } catch (Exception) { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ver != _tempVersion || work != _work) return;
                    TempSizeText.Text = JanitorReport.Gb(b);
                    LegacyCleanBtn.Visibility = legacy > 0 ? Visibility.Visible : Visibility.Collapsed;
                    LegacyCleanBtn.Content = "Free space from earlier versions (" + legacy + " folder" + (legacy == 1 ? "" : "s") + ")…";
                }));
            });
        }

        /// <summary>After the recovery of a picked H2EK (background thread): the start-up sweep (orphans, lock-less leftovers, KEEP skeletons, the capture
        /// cache, the cap, the run bundles), then the Temp row. Never while a bake runs (the sweep checks the GPU lock itself).</summary>
        private void TempSweep(int ver, string root)
        {
            if (_g.Snapshot != null || !_host.HasPayload) { Dispatcher.BeginInvoke(new Action(RefreshTempUi)); return; }
            string work = ShellPaths.WorkOf(root);
            JanitorReport rep = null;
            try { if (Directory.Exists(work)) rep = TempJanitor.Sweep(work, _temp, null, null, _host.Manifest.FarmsInTemp); }
            catch (Exception e) { rep = new JanitorReport(); rep.Problems.Add(e.Message); }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ver != _rootVersion) return;
                if (rep != null)
                {
                    if (rep.Skipped) Log(Palette.Dim, "temp: start-up cleanup skipped: " + rep.SkipReason);
                    foreach (var l in rep.Lines) Log(Palette.Dim, "temp: " + l);
                    foreach (var p in rep.Problems) Log(Palette.Warn, "temp: " + p);
                }
                RefreshTempUi();
            }));
        }

        private void OpenTempBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_work == null) return;
            try { OpenFolder(TempJanitor.EnsureTemp(_work)); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Temp folder"); }
        }

        private void CleanTempBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_work == null) return;
            string work = _work;
            if (MessageBox.Show(this, "Delete everything in\n" + ShellPaths.TempOf(work) + "\n(" + TempSizeText.Text + ")?\n\nKept captures go too: the next bake of those maps captures again. Your tags and backups are not in this folder.",
                                "Clean the temporary folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunOp("cleaning the temporary folder", work, null, lease => TempJanitor.CleanNow(work), r => r.ToString(), r => r.Problems.Count == 0, "Temp folder",
                  (r, good) =>
                  {
                      foreach (var l in r.Lines) Log(Palette.Dim, "temp: " + l);
                      MessageBox.Show(this, "Temporary folder: " + r, "Temp folder", MessageBoxButton.OK, good ? MessageBoxImage.Information : MessageBoxImage.Warning);
                      RefreshTempUi();
                  });
        }

        /// <summary>The opt-in removal of the folders 0.6.7 and older left: every item is listed (path and size) before anything is deleted.</summary>
        private void LegacyCleanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_work == null) return;
            string work = _work, root = _root;
            List<LegacyItem> items;
            try { items = TempJanitor.ListLegacy(work, _host.HasPayload ? _host.Installer : null, root); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Earlier versions"); return; }
            if (items.Count == 0) { MessageBox.Show(this, "No folders of earlier versions were found.", "Earlier versions"); RefreshTempUi(); return; }
            var sb = new StringBuilder();
            sb.AppendLine("These folders were left by earlier versions of this app (" + JanitorReport.Gb(items.Sum(i => i.Bytes)) + " in all). Delete exactly these?");
            sb.AppendLine("(Links inside them are removed without following them; your tags and the _TAGBAK_ui backups are not touched.)");
            sb.AppendLine();
            foreach (var it in items.Take(30)) sb.AppendLine(it.ToString());
            if (items.Count > 30) sb.AppendLine("... and " + (items.Count - 30) + " more (--cli cleanup --h2ek \"" + root + "\" --legacy --dry-run lists them all)");
            if (MessageBox.Show(this, sb.ToString(), "Free space from earlier versions", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunOp("removing the earlier versions' folders", work, null, lease => TempJanitor.RemoveLegacy(work, items, _host.HasPayload ? _host.Installer : null, root),
                  r => r.ToString(), r => r.Problems.Count == 0, "Earlier versions",
                  (r, good) =>
                  {
                      foreach (var l in r.Lines) Log(Palette.Dim, "cleanup: " + l);
                      MessageBox.Show(this, "Earlier versions' folders: " + r, "Earlier versions", MessageBoxButton.OK, good ? MessageBoxImage.Information : MessageBoxImage.Warning);
                      RefreshTempUi();
                  });
        }
    }
}
