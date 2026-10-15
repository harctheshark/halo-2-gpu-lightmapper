// S2.5 the shell's host context, shared by the GUI and the CLI: where the payload comes from (the exe's embedded resources;
// dev: a folder), the per-user folders (%LOCALAPPDATA%\H2GpuLightmapper; dev: a sandbox), dev runtime overrides, the
// settings file, and the feature flags. Nothing here starts a process or extracts anything on its own.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Pipeline;

namespace H2GpuLightmapper.App
{
    /// <summary>Dev / test overrides (CLI flags; the GUI takes the same ones on its command line).</summary>
    public sealed class HostConfig
    {
        /// <summary>--payload-dir: a folder with MANIFEST.json + the zips instead of the embedded payload.</summary>
        public string PayloadDir;
        /// <summary>--localappdata: the per-user root (rt\, settings.json) -- a test sandbox.</summary>
        public string LocalAppData;
        /// <summary>--alias-localappdata / --alias-volume-root: the DLL-slot alias candidates (tests).</summary>
        public string AliasLocalAppData, AliasVolumeRoot;
        /// <summary>--rt-pwsh / --rt-py: use these runtime folders instead of the extracted rt components (dev / tests).</summary>
        public string RtPwshDir, RtPyDir;
        /// <summary>--dev-custom-unguarded: also allow Custom when the payload has no engine_custom (the N3 palloc guard is then
        /// NOT there: T3 evidence from such a run is marked guarded=false). Since 0.6.1 Custom itself needs no flag (it is
        /// shown as "Experimental" whenever the payload carries the guarded engine); the old --dev-custom switch is still
        /// accepted by the command lines and ignored.</summary>
        public bool DevCustomUnguarded;
        /// <summary>--dev-probes: allow the light-probe pipeline (Standard on a probe BSP, Lightprobes) before gate T4.</summary>
        public bool DevProbes;
    }

    public sealed class AppHost
    {
        public HostConfig Config { get; private set; }
        public ShellPaths Paths { get; private set; }
        public IPayloadSource Source { get; private set; }
        /// <summary>Null when the exe carries no payload and no --payload-dir was given (see <see cref="PayloadError"/>).</summary>
        public PayloadManifest Manifest { get; private set; }
        public string PayloadError { get; private set; }
        private PayloadInstaller _installer;

        public static string ExeVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "?" : v.ToString(3);
            }
        }

        public static AppHost Create(HostConfig c)
        {
            c = c ?? new HostConfig();
            var h = new AppHost { Config = c };
            h.Paths = c.LocalAppData != null
                ? ShellPaths.ForSandbox(Path.GetFullPath(c.LocalAppData), c.AliasLocalAppData != null ? Path.GetFullPath(c.AliasLocalAppData) : null,
                                        c.AliasVolumeRoot != null ? Path.GetFullPath(c.AliasVolumeRoot) : null)
                : ShellPaths.Default();
            if (c.LocalAppData == null && (c.AliasLocalAppData != null || c.AliasVolumeRoot != null))
            {
                if (c.AliasLocalAppData != null) h.Paths.AliasLocalAppData = Path.GetFullPath(c.AliasLocalAppData);
                if (c.AliasVolumeRoot != null) h.Paths.AliasVolumeRoot = Path.GetFullPath(c.AliasVolumeRoot);
            }
            try
            {
                if (c.PayloadDir != null)
                {
                    if (!File.Exists(Path.Combine(c.PayloadDir, "MANIFEST.json"))) throw new InvalidDataException("no MANIFEST.json in " + c.PayloadDir);
                    h.Source = new DirectoryPayloadSource(c.PayloadDir);
                }
                else
                {
                    var asm = Assembly.GetExecutingAssembly();
                    if (!asm.GetManifestResourceNames().Contains(PayloadManifest.ResourceName))
                        throw new InvalidDataException("this build of H2 GPU Lightmapper carries no payload (a development build): pass --payload-dir <folder>");
                    h.Source = new AssemblyPayloadSource(asm);
                }
                h.Manifest = PayloadManifest.Load(h.Source);
                var inst = h.Manifest.InstComponent;     // exactly one, or throws
                if (h.Manifest.PwshComponent == null) throw new InvalidDataException("the payload has no pwsh runtime (pwsh.exe)");
                if (h.Manifest.PyComponent == null) throw new InvalidDataException("the payload has no Python runtime (py.exe)");
                if (!inst.Has(InstLayout.GpuBake)) throw new InvalidDataException("the payload's engine has no " + InstLayout.GpuBake);
            }
            catch (Exception e)
            {
                h.Manifest = null;
                h.PayloadError = e.Message;
            }
            return h;
        }

        public bool HasPayload { get { return Manifest != null; } }

        public string AppVersion { get { return Manifest != null && !string.IsNullOrEmpty(Manifest.AppVersion) ? Manifest.AppVersion : ExeVersion; } }

        public PayloadInstaller Installer
        {
            get
            {
                if (Manifest == null) throw new InvalidOperationException(PayloadError ?? "no payload");
                return _installer ?? (_installer = new PayloadInstaller(Source, Paths, Manifest));
            }
        }

        /// <summary>Paths of everything a bake needs, from the MANIFEST only (no extraction, nothing written): usable by --dry-run.</summary>
        public ResolvedRuntime Resolve(string root)
        {
            var rt = Installer.Resolve(root);
            if (Config.RtPwshDir != null) { rt.PwshDir = Path.GetFullPath(Config.RtPwshDir).TrimEnd('\\'); rt.PwshExe = Path.Combine(rt.PwshDir, "pwsh.exe"); }
            if (Config.RtPyDir != null) { rt.PyDir = Path.GetFullPath(Config.RtPyDir).TrimEnd('\\'); rt.PyExe = Path.Combine(rt.PyDir, "py.exe"); }
            return rt;
        }

        public AppSettings LoadSettings() { return AppSettings.Load(Paths.SettingsPath); }

        /// <summary>Review fix (finding 16): Custom on the UNGUARDED production fullgpu (the payload has no engine_custom):
        /// --dev-custom-unguarded or settings.json "devCustomUnguarded": true.</summary>
        public bool CustomUnguardedAllowed { get { return Config.DevCustomUnguarded || SettingFlag("devCustomUnguarded"); } }

        /// <summary>Does this payload carry the N3 guarded Custom engine (engine_custom)?</summary>
        public bool HasGuardedCustomEngine { get { return Manifest != null && Manifest.InstComponent.Has(InstLayout.GpuBakeCustom); } }

        /// <summary>E1 (0.6.1): Custom needs no flag. It is offered (window: "Custom (Experimental)"; command line: --quality custom)
        /// when the payload carries the N3 guarded engine, or when the explicit unguarded dev flag is given. A payload without
        /// engine_custom (the builder drops it after an engine promotion until gate T3a is re-run) offers no Custom at all.</summary>
        public bool CustomAvailable { get { return HasGuardedCustomEngine || CustomUnguardedAllowed; } }

        /// <summary>Review fix (finding 14): the light-probe pipeline (Standard on a probe BSP, Lightprobes only) is release-gated
        /// on T4. V5 (gate T4): on when the payload carries the T4 proof for its own engine files (MANIFEST features.probes,
        /// written by the S1.9 builder only when tests\t4\results\T4_PROOF.json names the md5s it ships); dev overrides
        /// --dev-probes / settings.json "devProbes": true. Off: Lightprobes is refused, Standard on a BSP with probe points is
        /// refused (Lightmap Only, which resets the probes like `tool lightmaps`, stays available).</summary>
        public bool ProbesEnabled { get { string w; return Config.DevProbes || SettingFlag("devProbes") || (Manifest != null && Manifest.ProbesEnabled(out w)); } }

        /// <summary>Why light probes are off in this build (null when they are on).</summary>
        public string ProbesWhy
        {
            get
            {
                if (ProbesEnabled) return null;
                if (Manifest == null) return PayloadError ?? "no payload";
                string w;
                Manifest.ProbesEnabled(out w);
                return w;
            }
        }

        /// <summary>Review fix (finding 15): AO only when the payload proved the AO engine is in sync (MANIFEST features.ao).</summary>
        public bool AoEnabled(out string why)
        {
            if (Manifest == null) { why = PayloadError ?? "no payload"; return false; }
            return Manifest.AoEnabled(out why);
        }

        private bool SettingFlag(string name)
        {
            try { return LoadSettings()[name] is bool b && b; } catch (Exception) { return false; }
        }
    }
}
