// Review fix (finding 5): "Restore previous lightmap..." lets the user pick WHICH _TAGBAK_ui backup to restore. The default
// selection is TagRestore.Newest (the newest backup that differs from the live set); every backup is listed with its time,
// whether a bake installed after it, whether it equals the live set (nothing to restore) and whether it is verified (a legacy
// backup without md5s is restored as found). Built in code (no XAML) with the window's dark theme.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using H2GpuLightmapper.Core.Safety;

namespace H2GpuLightmapper.Ui
{
    public sealed class RestorePicker : Window
    {
        private sealed class Row
        {
            public RestoreCandidate C;
            public bool NoOp;
            public override string ToString()
            {
                return C.Describe() + (NoOp ? "   (= the live set: nothing to restore)" : "") + "\n      " + string.Join(", ", C.Files.Select(f => f.Key));
            }
        }

        private readonly ListBox _list;
        public RestoreCandidate Chosen { get; private set; }

        public RestorePicker(Window owner, string title, List<RestoreCandidate> cands, Func<RestoreCandidate, bool> isNoOp, RestoreCandidate preselect)
        {
            Owner = owner;
            Title = title;
            Width = 720; Height = 380; MinWidth = 520; MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            try { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Ui/DarkTheme.xaml", UriKind.Relative) }); } catch (Exception) { }
            Background = Palette.Clear; Foreground = Palette.Fg;
            var root = new DockPanel { Margin = new Thickness(10) };
            var head = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = Palette.Fg,
                Text = "Pick the backup to put back. The current files are saved aside first (md5-verified); close the map in Sapien / Guerilla first.",
            };
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "Restore", MinWidth = 90, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            _list = new ListBox { Background = Palette.Field, Foreground = Palette.Fg, BorderBrush = Palette.Edge };
            var rows = cands.Select(c => new Row { C = c, NoOp = isNoOp(c) }).ToList();
            foreach (var r in rows) _list.Items.Add(r);
            _list.SelectedItem = rows.FirstOrDefault(r => preselect != null && r.C.Dir == preselect.Dir) ?? rows.FirstOrDefault(r => !r.NoOp) ?? rows.FirstOrDefault();
            root.Children.Add(_list);
            Content = root;
            ok.Click += (s, e) =>
            {
                var r = _list.SelectedItem as Row;
                if (r == null) return;
                if (r.NoOp) { MessageBox.Show(this, "That backup equals the live lightmap: restoring it changes nothing. Pick an older one.", title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (!r.C.Verified && MessageBox.Show(this, r.C.Name + " is a legacy backup without recorded md5s: it cannot be verified and is restored as found. Restore it anyway?",
                                                     title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Chosen = r.C;
                DialogResult = true;
            };
        }
    }
}
