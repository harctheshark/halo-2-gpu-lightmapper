// SF1: the result of "Scan and Build Scenery Fix Dependencies": how many models were scanned, which were added, which conflict
// (and why that matters) and where SceneryFixDependencies.db lives. Built in code (no XAML) with the window's dark theme, like
// the restore picker; the text is selectable so it can be copied into a bug report.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace H2GpuLightmapper.Ui
{
    public sealed class ScanResultWindow : Window
    {
        public ScanResultWindow(Window owner, string title, IEnumerable<string> lines, string dbPath)
        {
            Owner = owner;
            Title = title;
            Width = 820; Height = 520; MinWidth = 520; MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            try { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Ui/DarkTheme.xaml", UriKind.Relative) }); } catch (Exception) { }
            Background = Palette.Clear; Foreground = Palette.Fg;
            var root = new DockPanel { Margin = new Thickness(10) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var show = new Button { Content = "Show the file", MinWidth = 100, IsEnabled = dbPath != null && File.Exists(dbPath) };
            var close = new Button { Content = "Close", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), IsDefault = true, IsCancel = true };
            buttons.Children.Add(show); buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var box = new TextBox
            {
                Text = string.Join(Environment.NewLine, lines), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = Palette.Field, Foreground = Palette.Fg, BorderBrush = Palette.Edge,
            };
            root.Children.Add(box);
            Content = root;
            show.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dbPath + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, title); }
            };
            close.Click += (s, e) => Close();
        }
    }
}
