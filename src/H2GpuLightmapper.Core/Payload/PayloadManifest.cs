// S2.1. Copied from the S0.1 spike (src\spike_s01_wpf\Payload\PayloadManifest.cs, format 1, unchanged on the wire) into
// the shell core; namespace moved to H2GpuLightmapper.Core.Payload. New: the role constants S1.9 tags files with, and
// helpers to find the runtime / engine components without depending on their names.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace H2GpuLightmapper.Core.Payload
{
    /// <summary>MANIFEST.json embedded as resource "payload/MANIFEST.json" (format 1).</summary>
    [DataContract]
    public sealed class PayloadManifest
    {
        public const string ResourceName = "payload/MANIFEST.json";
        public const string TargetRt = "rt";
        public const string TargetInst = "inst";

        [DataMember(Name = "format", Order = 0)] public int Format { get; set; }
        [DataMember(Name = "appVersion", Order = 1)] public string AppVersion { get; set; }
        [DataMember(Name = "components", Order = 2)] public List<PayloadComponent> Components { get; set; }
        /// <summary>Review fix (finding 15): build-time feature verdicts (S1.9 stage_engine.py). Optional on the wire.</summary>
        [DataMember(Name = "features", Order = 3, IsRequired = false, EmitDefaultValue = false)] public PayloadFeatures Features { get; set; }

        /// <summary>
        /// May this payload bake ambient occlusion? Only when the builder proved the AO trace engine (dev_AO) carries the same
        /// kernel headers as the lightmap engine it traces (features.ao.enabled). Fails CLOSED: a payload without the record
        /// does not bake AO.
        /// </summary>
        public bool AoEnabled(out string why)
        {
            var f = Features == null ? null : Features.Ao;
            if (f == null) { why = "this payload does not record the AO engine check (rebuild it with the current S1.9 builder)"; return false; }
            why = f.Enabled ? null : "AO is disabled in this build: " + (f.Reason ?? "the AO engine check failed");
            return f.Enabled;
        }

        /// <summary>
        /// V5 (gate T4): may this payload bake light probes (Standard on a probe BSP, Lightprobes only)? Only when the builder found
        /// the T4 proof (tests\t4\results\T4_PROOF.json, verdict PASS) naming the md5s of exactly the probe-path engine files it
        /// shipped (features.probes.enabled). Fails CLOSED: a payload without the record does not bake probes.
        /// </summary>
        public bool ProbesEnabled(out string why)
        {
            var f = Features == null ? null : Features.Probes;
            if (f == null) { why = "this payload does not record the light-probe gate T4 (rebuild it with the current S1.9 builder)"; return false; }
            why = f.Enabled ? null : "light probes are disabled in this build: " + (f.Reason ?? "the T4 proof does not cover this engine");
            return f.Enabled;
        }

        /// <summary>
        /// X1: may this payload bake an H2EK that sits at the root of a drive (D:\)? Only when the builder saw that the gpu_bake.ps1 it
        /// ships carries the P1b drive-root handling (features.driveRoot.enabled): an older gpu_bake rejects H2LM_ROOT=D:\ and
        /// cannot be given "D:" (the drive's current folder). Fails CLOSED: a payload without the record refuses a drive root.
        /// </summary>
        public bool DriveRootEnabled(out string why)
        {
            var f = Features == null ? null : Features.DriveRoot;
            if (f == null) { why = "this payload's gpu_bake.ps1 predates drive-root support (no features.driveRoot record)"; return false; }
            why = f.Enabled ? null : "drive roots are disabled in this build: " + (f.Reason ?? "the payload's gpu_bake.ps1 has no drive-root handling");
            return f.Enabled;
        }

        public static PayloadManifest Load(Stream s)
        {
            var ser = new DataContractJsonSerializer(typeof(PayloadManifest),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            var m = (PayloadManifest)ser.ReadObject(s);
            m.Validate();
            return m;
        }

        public static PayloadManifest LoadEmbedded(Assembly asm)
        {
            using (var s = asm.GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidDataException("missing embedded " + ResourceName);
                return Load(s);
            }
        }

        public static PayloadManifest Load(IPayloadSource src)
        {
            using (var s = src.OpenManifest())
            {
                if (s == null) throw new InvalidDataException("payload source " + src.Describe + " has no " + ResourceName);
                return Load(s);
            }
        }

        /// <summary>Structural checks: format 1, unique dir names, safe relative paths, file counts consistent.</summary>
        public void Validate()
        {
            if (Format != 1) throw new InvalidDataException("MANIFEST.json format " + Format + " (this shell reads format 1)");
            if (Components == null || Components.Count == 0) throw new InvalidDataException("MANIFEST.json lists no components");
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Components)
            {
                if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Resource) || string.IsNullOrEmpty(c.DirName))
                    throw new InvalidDataException("MANIFEST.json: a component lacks name / resource / dirName");
                if (c.Target != TargetRt && c.Target != TargetInst) throw new InvalidDataException(c.Name + ": target '" + c.Target + "' is not rt / inst");
                if (c.DirName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || c.DirName.StartsWith(".", StringComparison.Ordinal))
                    throw new InvalidDataException(c.Name + ": bad dirName '" + c.DirName + "'");
                if (!dirs.Add(c.Target + "|" + c.DirName)) throw new InvalidDataException("duplicate dirName " + c.DirName);
                if (c.Files == null || c.Files.Count != c.FileCount) throw new InvalidDataException(c.Name + ": fileCount " + c.FileCount + " != files listed " + (c.Files == null ? 0 : c.Files.Count));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in c.Files)
                {
                    if (string.IsNullOrEmpty(f.Path) || f.Path.StartsWith("/", StringComparison.Ordinal) || f.Path.Contains("\\") || f.Path.Contains(":")
                        || f.Path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                        throw new InvalidDataException(c.Name + ": unsafe path '" + f.Path + "'");
                    if (!seen.Add(f.Path)) throw new InvalidDataException(c.Name + ": duplicate path " + f.Path);
                    if (string.IsNullOrEmpty(f.Md5) || f.Md5.Length != 32) throw new InvalidDataException(c.Name + ": " + f.Path + " has no md5");
                }
            }
        }

        public IEnumerable<PayloadComponent> RtComponents { get { return Components.Where(c => c.Target == TargetRt); } }
        public IEnumerable<PayloadComponent> InstComponents { get { return Components.Where(c => c.Target == TargetInst); } }

        /// <summary>The rt component whose root holds pwsh.exe.</summary>
        public PayloadComponent PwshComponent { get { return RtComponents.FirstOrDefault(c => c.Has("pwsh.exe")); } }
        /// <summary>The rt component whose root holds py.exe (the S0.3 bundle).</summary>
        public PayloadComponent PyComponent { get { return RtComponents.FirstOrDefault(c => c.Has("py.exe")); } }
        /// <summary>The rt component whose root holds mapbake.py: the internal "bake straight from a .map" entry point plus the separate program h2conv it starts
        /// (GPL-3.0-or-later, see NOTICES). Null for a payload that has none (the .map path is then refused in plain words).</summary>
        public PayloadComponent MapbakeComponent { get { return RtComponents.FirstOrDefault(c => c.Has("mapbake.py")); } }
        /// <summary>The engine instance (INST) component: exactly one target=inst component.</summary>
        public PayloadComponent InstComponent
        {
            get
            {
                var l = InstComponents.ToList();
                if (l.Count != 1) throw new InvalidDataException("MANIFEST.json must have exactly one target=inst component, found " + l.Count);
                return l[0];
            }
        }
    }

    [DataContract]
    public sealed class PayloadFeatures
    {
        /// <summary>AO: enabled only when every kernel header / .hlsl present in both _research\ao\dev_AO\v041-fullgpu and
        /// engine_v373\v041-fullgpu has the same md5 (the AO trace walks the lightmap bake's own gate cells).</summary>
        [DataMember(Name = "ao", Order = 0, IsRequired = false, EmitDefaultValue = false)] public PayloadFeature Ao { get; set; }
        /// <summary>V5 light probes: enabled only when gate T4's proof (SH light vs the CPU super cubes on triplicate, the
        /// lightmap stage untouched by the probe worker, sh_stage's refusal, Lightprobes only into a scratch copy) names the md5s
        /// of the probe-path engine files this payload ships (production fullgpu / estimator DLL / gpu_bake, the lightprobe
        /// drivers, sh_stage, the tag walker).</summary>
        [DataMember(Name = "probes", Order = 1, IsRequired = false, EmitDefaultValue = false)] public PayloadFeature Probes { get; set; }
        /// <summary>X1: the shipped gpu_bake.ps1 handles H2LM_ROOT at a drive root (P1b: $RP). Absent = an older engine: drive roots stay refused.</summary>
        [DataMember(Name = "driveRoot", Order = 2, IsRequired = false, EmitDefaultValue = false)] public PayloadFeature DriveRoot { get; set; }
    }

    [DataContract]
    public sealed class PayloadFeature
    {
        [DataMember(Name = "enabled", Order = 0)] public bool Enabled { get; set; }
        [DataMember(Name = "reason", Order = 1, IsRequired = false, EmitDefaultValue = false)] public string Reason { get; set; }
    }

    [DataContract]
    public sealed class PayloadComponent
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        /// <summary>Manifest resource name of the zip, e.g. "payload/rt-pwsh-7.6.6.zip".</summary>
        [DataMember(Name = "resource", Order = 1)] public string Resource { get; set; }
        /// <summary>Versioned, content-hash-named extraction dir leaf, e.g. "pwsh-7.6.6-1a2b3c4d".</summary>
        [DataMember(Name = "dirName", Order = 2)] public string DirName { get; set; }
        /// <summary>"rt" = %LOCALAPPDATA%\H2GpuLightmapper\rt\ ; "inst" = WORK\_runtime\.</summary>
        [DataMember(Name = "target", Order = 3)] public string Target { get; set; }
        [DataMember(Name = "zipSize", Order = 4)] public long ZipSize { get; set; }
        [DataMember(Name = "zipMd5", Order = 5)] public string ZipMd5 { get; set; }
        [DataMember(Name = "fileCount", Order = 6)] public int FileCount { get; set; }
        [DataMember(Name = "totalBytes", Order = 7)] public long TotalBytes { get; set; }
        [DataMember(Name = "maxPathLen", Order = 8)] public int MaxPathLen { get; set; }
        [DataMember(Name = "files", Order = 9)] public List<PayloadFile> Files { get; set; }

        public bool Has(string relPath) { return Files != null && Files.Any(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase)); }
        public PayloadFile Find(string relPath) { return Files == null ? null : Files.FirstOrDefault(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase)); }
        public override string ToString() { return Name + " (" + Target + "\\" + DirName + ", " + FileCount + " files)"; }
    }

    [DataContract]
    public sealed class PayloadFile
    {
        /// <summary>Roles S1.9 may put on a file (free-form; these are the ones the shell acts on).</summary>
        public const string RoleDllSlot = "dllslot";     // gpulm_estimator.dll: path-slot patched per root at extraction
        public const string RoleKey = "key";             // runtime binary md5-checked before every bake
        public const string RoleBoundsDb = "boundsdb";   // h2v_scenery_bounds.db shipped copy (copied into ROOT only if absent)
        public const string RoleGpuCheck = "gpucheck";   // gpucheck.exe (with the Optimus / AMD exports)

        /// <summary>Relative path inside the component, '/'-separated, exactly the zip entry name.</summary>
        [DataMember(Name = "path", Order = 0)] public string Path { get; set; }
        [DataMember(Name = "size", Order = 1)] public long Size { get; set; }
        /// <summary>Lower-case hex md5 of the file as extracted (after any build-time patch). For a "dllslot" file this is
        /// the PRISTINE md5; the per-root slot patch is applied after extraction and verified by re-pristining.</summary>
        [DataMember(Name = "md5", Order = 2)] public string Md5 { get; set; }
        /// <summary>md5 of the production source file before patching (== md5 when unpatched).</summary>
        [DataMember(Name = "sourceMd5", Order = 3, IsRequired = false, EmitDefaultValue = false)] public string SourceMd5 { get; set; }
        /// <summary>Free-form role tag ("engine", "patched:P1", "dllslot", "key", ...).</summary>
        [DataMember(Name = "role", Order = 4, IsRequired = false, EmitDefaultValue = false)] public string Role { get; set; }

        public bool HasRole(string role)
        {
            if (string.IsNullOrEmpty(Role)) return false;
            foreach (var r in Role.Split(',', ';', ' '))
                if (string.Equals(r.Trim(), role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string FileName { get { int i = Path.LastIndexOf('/'); return i < 0 ? Path : Path.Substring(i + 1); } }

        /// <summary>True for the files the shell slot-patches: role "dllslot", or (when S1.9 sets no role) any gpulm_estimator.dll.</summary>
        public bool IsDllSlot
        {
            get { return HasRole(RoleDllSlot) || string.Equals(FileName, "gpulm_estimator.dll", StringComparison.OrdinalIgnoreCase); }
        }

        public override string ToString() { return Path; }
    }
}
