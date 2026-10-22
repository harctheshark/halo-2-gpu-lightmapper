// S2.5 the shell's host context, shared by the GUI and the CLI: where the payload comes from (the exe's embedded resources;
// dev: a folder), the per-user folders (%LOCALAPPDATA%\H2GpuLightmapper; dev: a sandbox), dev runtime overrides, the
// settings file, and the feature flags. Nothing here starts a process or extracts anything on its own.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
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

        /// <summary>X1: an H2EK at a drive root bakes only with a payload whose gpu_bake.ps1 has the P1b handling (MANIFEST features.driveRoot);
        /// dev override settings.json "devDriveRoot": true is NOT offered -- the refusal is what protects an older engine.</summary>
        public bool DriveRootEnabled(out string why)
        {
            if (Manifest == null) { why = PayloadError ?? "no payload"; return false; }
            return Manifest.DriveRootEnabled(out why);
        }

        /// <summary>Review fix (finding 15): AO only when the payload proved the AO engine is in sync (MANIFEST features.ao).</summary>
        public bool AoEnabled(out string why)
        {
            if (Manifest == null) { why = PayloadError ?? "no payload"; return false; }
            return Manifest.AoEnabled(out why);
        }

        /// <summary>Does the gpu_bake.ps1 this bake would run (engine_v373, or engine_custom for Custom when the payload has it) declare
        /// <c>[switch]$name</c>? Read from the payload's own copy of the script (the file whose md5 the shell verifies before every bake);
        /// nothing is extracted. False when there is no payload.</summary>
        public bool EngineDeclares(BakeOptions o, string name)
        {
            if (Manifest == null) return false;
            return EngineSwitches.ComponentDeclares(Source, Manifest.InstComponent, BakePlanner.GpuBakeRel(o, Manifest), name);
        }

        /// <summary>"Scenery Multi Permutation Fix (Experimental Improvement)" needs an engine whose gpu_bake.ps1 declares
        /// <c>-SceneryOwnPermutationOnly</c> (engine 0.6.4 or newer). Null = this payload's engine for these options takes it;
        /// else the plain reason. Quality matters (Custom runs engine_custom); the other options do not.</summary>
        public string SceneryPermutationFixWhy(BakeOptions o)
        {
            if (Manifest == null) return PayloadError ?? "no payload";
            if (EngineDeclares(o, BakeOptions.SceneryPermutationFixSwitch)) return null;
            return BakeOptions.SceneryPermutationFixLabel + " is not available: the engine this bake would run (" + BakePlanner.GpuBakeRel(o, Manifest)
                   + ") has no -" + BakeOptions.SceneryPermutationFixSwitch + " parameter. " + BakeOptions.SceneryPermutationFixNeedsEngine;
        }

        /// <summary>The gpu_bake.ps1 files the bake <paramref name="o"/> would run: the lightmap engine (engine_v373, or engine_custom for Custom when the
        /// payload has it) and, with ambient occlusion, the AO engine copy (dev_AO). The light probe driver runs the lightmap engine's script.</summary>
        public System.Collections.Generic.List<string> EngineScripts(BakeOptions o)
        {
            var l = new System.Collections.Generic.List<string>();
            if (Manifest == null) return l;
            l.Add(BakePlanner.GpuBakeRel(o, Manifest));
            if (o.Ao && Manifest.InstComponent.Has(InstLayout.AoGpuBake)) l.Add(InstLayout.AoGpuBake);
            return l;
        }

        /// <summary>Compatibility mode (DF64) needs an engine whose gpu_bake.ps1 validates <c>GPULM_COMPAT</c> (engine 0.6.4 or newer; the marker is the
        /// variable read in the script's code, see EngineSwitches.ReadsEnvVar). Null = every engine this bake would run has it: a GPU without FP64 is
        /// accepted and runs the FP32-only kernels automatically, and <c>--force-compat</c> may be used. Else the plain reason: a GPU without FP64 is
        /// then refused (the 0.6.3 sentence) and GPULM_COMPAT is never passed to that engine.</summary>
        public string CompatEngineWhy(BakeOptions o)
        {
            if (Manifest == null) return PayloadError ?? "no payload";
            foreach (var rel in EngineScripts(o))
                if (!EngineSwitches.ComponentReadsEnv(Source, Manifest.InstComponent, rel, GpuCheck.CompatEnvVar))
                    return "the engine this bake would run (" + rel + ") has no Compatibility mode (its gpu_bake.ps1 does not validate " + GpuCheck.CompatEnvVar + "). " + BakeOptions.ForceCompatNeedsEngine;
            return null;
        }

        /// <summary>Cross-folder BSP (engine 0.6.4): a BSP whose tag lives in ANOTHER folder than the scenario (01b_spacestation -> 01a_tutorial\01_bsp_1) is
        /// baked only by an engine whose gpu_bake.ps1 resolves it from the scenario's structure_bsps block. Marker: the gpu_bake.ps1 the bake would run
        /// declares <c>-WriterOnly</c> and <c>-ToolOverlay</c> (<see cref="BakeOptions.CrossFolderBspSwitches"/>; same patch as lmw_skeleton.py resolve-bsp).
        /// Null = supported. Else the plain reason: such BSPs are then not offered in the picker, and a bake naming one is refused (an older engine would look
        /// for the file beside the scenario, fail its skeleton / tag writer, and fall back to the tool write-back on LIVE tags).</summary>
        public string CrossFolderBspWhy(BakeOptions o)
        {
            if (Manifest == null) return PayloadError ?? "no payload";
            string rel = BakePlanner.GpuBakeRel(o, Manifest);
            foreach (var sw in BakeOptions.CrossFolderBspSwitches)
                if (!EngineDeclaresIn(rel, sw))
                    return "the engine this bake would run (" + rel + ") cannot bake a BSP from another folder (its gpu_bake.ps1 has no -" + sw + " parameter). " + BakeOptions.CrossFolderBspNeedsEngine;
            return null;
        }

        /// <summary>Ambient occlusion / light probes for a BSP in ANOTHER folder than the scenario (0.6.5). <paramref name="ao"/>: the AO trace + stage run (gpu_ao.py with the AO
        /// engine copy dev_AO); <paramref name="probes"/>: the light probe driver runs (gpu_lightprobes.py). Each is supported only when the payload's driver script carries the
        /// marker line (<see cref="BakeOptions.SplitBspMarker"/>) and, for AO, the AO engine copy's gpu_bake.ps1 resolves the BSP from the scenario (declares the same switches as
        /// <see cref="CrossFolderBspWhy"/>). Null = supported. Else the plain reason (the 0.6.4 sentence): the layer stays refused, the lightmap alone is still baked.</summary>
        public string CrossFolderAoProbesWhy(bool ao, bool probes)
        {
            if (Manifest == null) return PayloadError ?? "no payload";
            if (ao)
            {
                if (!EngineSwitches.ComponentHasPyMarker(Source, Manifest.InstComponent, InstLayout.AoDriver, BakeOptions.SplitBspMarker)) return BakeOptions.CrossFolderBspNoAoProbes;
                foreach (var sw in BakeOptions.CrossFolderBspSwitches)
                    if (!EngineDeclaresIn(InstLayout.AoGpuBake, sw)) return BakeOptions.CrossFolderBspNoAoProbes;
            }
            if (probes && !EngineSwitches.ComponentHasPyMarker(Source, Manifest.InstComponent, InstLayout.ProbeDriver, BakeOptions.SplitBspMarker)) return BakeOptions.CrossFolderBspNoAoProbes;
            return null;
        }

        private bool EngineDeclaresIn(string rel, string name)
        {
            return Manifest != null && EngineSwitches.ComponentDeclares(Source, Manifest.InstComponent, rel, name);
        }

        /// <summary>Does every engine this bake would run have Compatibility mode (DF64)?</summary>
        public bool EngineHasCompat(BakeOptions o) { return CompatEngineWhy(o) == null; }

        private bool SettingFlag(string name)
        {
            try { return LoadSettings()[name] is bool b && b; } catch (Exception) { return false; }
        }
    }
}
