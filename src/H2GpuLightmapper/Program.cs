// "H2 GPU Lightmapper.exe": the WPF window, or the same-exe CLI with --cli (FINAL_DESIGN section 4). One exe; no App.xaml (the
// CLI path must never create a window).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using H2GpuLightmapper.App;

namespace H2GpuLightmapper
{
    public static class Program
    {
        /// <summary>Set as the first statement of Main.</summary>
        public static DateTime MainEnteredUtc;

        /// <summary>GUI dev / test options (same names as the CLI's).</summary>
        public sealed class GuiArgs
        {
            public HostConfig Host = new HostConfig();
            public string Root, Scenario, Bsp;
            /// <summary>--dev-ui-snapshot PNG: render the window off-screen (never shown on the desktop, never activated), save
            /// it, and exit. Used by the UI smoke test.</summary>
            public string Snapshot;
            public int SnapshotDelayMs = 1500;
            /// <summary>--dev-ui-preset "custom,ao,advanced,probes,lightmap,low,stageonly": pre-select options (UI tests only;
            /// a normal launch always starts from the section-4 defaults).</summary>
            public string Preset;
            /// <summary>--dev-ui-autobake (with --dev-ui-snapshot): press Bake once the map is loaded, snapshot when the run ends.</summary>
            public bool AutoBake;
            /// <summary>--dev-skip-preflight / --dev-skip-gpucheck: as the CLI's (sandbox roots in tests).</summary>
            public bool SkipPreflight, SkipGpuCheck;
            public bool HwRender;
            public bool NoStartupWork;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            MainEnteredUtc = DateTime.UtcNow;
            if (args.Contains("--cli")) return Cli.CliMain.Run(args);

            GuiArgs g;
            try { g = ParseGui(args); }
            catch (ArgumentException e)
            {
                MessageBox.Show(e.Message + "\n\nCommand line use: \"H2 GPU Lightmapper.exe\" --cli help", "H2 GPU Lightmapper", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
            // S0.1 finding: WPF SOFTWARE rendering. The shell runs beside its own fullgpu bake; with the GPU saturated by compute,
            // hardware-rendered WPF frames were not presented (a white client area seconds after ContentRendered).
            if (!g.HwRender) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var w = new MainWindow(g);
            return app.Run(w);
        }

        public static GuiArgs ParseGui(string[] args)
        {
            var g = new GuiArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => { if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value"); return args[++i]; };
                switch (a)
                {
                    case "--payload-dir": g.Host.PayloadDir = next(); break;
                    case "--localappdata": g.Host.LocalAppData = next(); break;
                    case "--alias-localappdata": g.Host.AliasLocalAppData = next(); break;
                    case "--alias-volume-root": g.Host.AliasVolumeRoot = next(); break;
                    case "--rt-pwsh": g.Host.RtPwshDir = next(); break;
                    case "--rt-py": g.Host.RtPyDir = next(); break;
                    case "--dev-custom": break;      // accepted and ignored since 0.6.1: Custom needs no switch
                    case "--dev-custom-unguarded": g.Host.DevCustomUnguarded = true; break;
                    case "--dev-probes": g.Host.DevProbes = true; break;
                    case "--h2ek": g.Root = next(); break;
                    case "--scenario": g.Scenario = next(); break;
                    case "--bsp": g.Bsp = next(); break;
                    case "--dev-ui-snapshot": g.Snapshot = next(); break;
                    case "--dev-ui-snapshot-delay-ms": g.SnapshotDelayMs = int.Parse(next(), CultureInfo.InvariantCulture); break;
                    case "--dev-no-startup-work": g.NoStartupWork = true; break;
                    case "--dev-ui-preset": g.Preset = next(); break;
                    case "--dev-ui-autobake": g.AutoBake = true; break;
                    case "--dev-skip-preflight": g.SkipPreflight = true; break;
                    case "--dev-skip-gpucheck": g.SkipGpuCheck = true; break;
                    case "--hw-render": g.HwRender = true; break;
                    default: throw new ArgumentException("unknown option " + a);
                }
            }
            return g;
        }
    }
}
