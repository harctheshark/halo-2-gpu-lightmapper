// S2.1 Shell: payload extraction, verify, repair, INST (FINAL_DESIGN sections 2, 9 and plan step S2.1).
//   rt\  : %LOCALAPPDATA%\H2GpuLightmapper\rt\<dirName>\   shared, immutable (pwsh 7.6.6, py310 + numpy)
//   INST : <ROOT>\GPU Lightmap Work\_runtime\<dirName>\     per root; both gpulm_estimator DLL families slot-patched with
//          SlotAlias.Ensure(WORK).SlotBase (WORK\ itself when it fits in 60 bytes, else an alias junction)
// Start: QuickCheck (size + mtime). Before each bake: VerifyBeforeBake (full md5 of INST, slot DLLs by re-pristining,
// md5 of the key runtime binaries) then Repair when needed (per-file re-extract; a dir whose marker is bad is renamed
// aside -- never deleted -- and extracted again). Old versions go only on request (RemoveOldVersion, reparse-point safe).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Payload
{
    public sealed class ComponentCheck
    {
        public PayloadComponent Component;
        public string Dir;
        public VerifyResult Result;
        public override string ToString()
        {
            return Component.Name + ": " + (Result.Ok ? "ok" : string.Join("; ", Result.Problems.Take(5)) + (Result.Problems.Count > 5 ? " (+" + (Result.Problems.Count - 5) + ")" : ""));
        }
    }

    public sealed class CheckReport
    {
        public List<ComponentCheck> Components = new List<ComponentCheck>();
        public string SlotBase;
        public bool Ok { get { return Components.All(c => c.Result.Ok); } }
        public double Seconds { get { return Components.Sum(c => c.Result.Seconds); } }
        public override string ToString() { return string.Join(" | ", Components.Select(c => c.ToString())); }
    }

    public sealed class InstEnsureResult
    {
        public string Root, Work, InstDir;
        public SlotBaseResult Slot;
        public ExtractResult Extract;
        /// <summary>Slot DLLs re-patched because the slot base changed since extraction (alias moved / recreated).</summary>
        public List<string> Repatched = new List<string>();
    }

    /// <summary>Everything a bake child needs, resolved from the extracted payload.</summary>
    public sealed class ResolvedRuntime
    {
        public string AppVersion;
        public string Root, Work, InstDir, SlotBase;
        public string PwshDir, PwshExe, PyDir, PyExe;
        /// <summary>INST\engine_v373\gpu_bake.ps1 (S0.2: the bake argv is pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File this).</summary>
        public string GpuBakePs1;
        /// <summary>INST\engine_v373\v043-tagwriter\lmw_install.py when present.</summary>
        public string LmwInstallPy;
        /// <summary>The shipped h2v_scenery_bounds.db in INST (null if the payload has none).</summary>
        public string BoundsDb;
        /// <summary>INST gpucheck.exe (null if absent).</summary>
        public string GpuCheckExe;

        public BakeEnvSpec ToEnvSpec()
        {
            return new BakeEnvSpec { PwshDir = PwshDir, PyDir = PyDir, Root = Root, Work = Work, Code = InstDir, NoWindow = true };
        }
    }

    public sealed class OldVersionInfo
    {
        public string Path, Parent, Kind;
        public override string ToString() { return Kind + " " + Path; }
    }

    /// <summary>Verifies slot-patched DLLs: re-pristining the file must give the manifest (pristine) md5, and both slots must
    /// hold the expected base.</summary>
    public sealed class SlotFileVerifier : IFileVerifier
    {
        private readonly string _base;
        private readonly Func<PayloadFile, SlotLayout> _layout;
        public SlotFileVerifier(string slotBase, Func<PayloadFile, SlotLayout> layoutFor) { _base = slotBase; _layout = layoutFor; }
        public bool Handles(PayloadFile f) { return f.IsDllSlot; }
        public string Check(PayloadFile f, string fullPath)
        {
            byte[] b = File.ReadAllBytes(fullPath);
            if (b.LongLength != f.Size) return "size " + b.LongLength + " != " + f.Size;
            var layout = _layout(f);
            byte[] pristine;
            try { pristine = DllSlotPatcher.Repristine(b, layout); }
            catch (SlotPatchException e) { return "not a slot-patched copy of " + f.Md5.Substring(0, 8) + ": " + e.Message; }
            string pm = DllSlotPatcher.Md5Hex(pristine);
            if (!string.Equals(pm, f.Md5, StringComparison.OrdinalIgnoreCase)) return "bytes outside the slots changed (re-pristined md5 " + pm + ")";
            string got;
            try { got = DllSlotPatcher.ReadSlotBase(b, layout); } catch (SlotPatchException e) { return e.Message; }
            if (!string.Equals(got, _base, StringComparison.Ordinal)) return "slots hold '" + got + "', expected '" + _base + "'";
            return null;
        }
    }

    public sealed class PayloadInstaller
    {
        public IPayloadSource Source { get; private set; }
        public ShellPaths Paths { get; private set; }
        public PayloadManifest Manifest { get; private set; }
        private readonly Dictionary<string, SlotLayout> _layoutCache = new Dictionary<string, SlotLayout>(StringComparer.OrdinalIgnoreCase);

        public PayloadInstaller(IPayloadSource src, ShellPaths paths, PayloadManifest manifest = null)
        {
            Source = src ?? throw new ArgumentNullException("src");
            Paths = paths ?? ShellPaths.Default();
            Manifest = manifest ?? PayloadManifest.Load(src);
        }

        public string RtDir(PayloadComponent c) { return Path.Combine(Paths.RtRoot, c.DirName); }
        public string InstDir(string work) { return Path.Combine(ShellPaths.InstParentOf(work), Manifest.InstComponent.DirName); }
        private string DirOf(PayloadComponent c, string work)
        {
            if (c.Target == PayloadManifest.TargetRt) return RtDir(c);
            if (work == null) throw new ArgumentNullException("work", "an inst component needs WORK");
            return Path.Combine(ShellPaths.InstParentOf(work), c.DirName);
        }

        /// <summary>Key runtime binaries (md5-checked before every bake): role "key", every top-level .exe / .dll, python310.zip
        /// and numpy's core extension.</summary>
        public static bool IsKeyFile(PayloadFile f)
        {
            if (f.HasRole(PayloadFile.RoleKey)) return true;
            string p = f.Path.ToLowerInvariant();
            if (p.IndexOf('/') < 0 && (p.EndsWith(".exe") || p.EndsWith(".dll"))) return true;
            if (p == "python310.zip" || p == "python310._pth") return true;
            if (p.EndsWith("/_multiarray_umath.cp310-win_amd64.pyd")) return true;
            return false;
        }

        // ------------------------------------------------------------------ extraction

        /// <summary>Extracts every rt component that is not already present (first run: about 4 s for ~336 MB, S0.1).</summary>
        public List<ExtractResult> EnsureRuntimes(ExtractOptions o = null)
        {
            var r = new List<ExtractResult>();
            foreach (var c in Manifest.RtComponents) r.Add(PayloadExtractor.EnsureExtracted(Source, c, Paths.RtRoot, o));
            return r;
        }

        /// <summary>
        /// INST for one H2EK root: plans / creates the DLL-slot alias (S0.4 SlotAlias.Ensure on the REAL WORK), extracts the
        /// engine component into WORK\_runtime\&lt;dirName&gt; with every slot DLL patched to that base BEFORE the dir is sealed,
        /// and, when INST already exists, re-patches slot DLLs whose base no longer matches (the alias moved).
        /// Throws SlotAliasRefusedException when no slot base fits (preflight: refuse, exit 2).
        /// </summary>
        public InstEnsureResult EnsureInst(string root, ExtractOptions o = null)
        {
            o = o ?? new ExtractOptions();
            string work = ShellPaths.WorkOf(root);
            Directory.CreateDirectory(work);
            var slot = SlotAlias.Ensure(work, Paths.AliasLocalAppData, Paths.AliasVolumeRoot);
            var c = Manifest.InstComponent;
            var res = new InstEnsureResult { Root = SafeFs.Norm(root), Work = work, Slot = slot, InstDir = InstDir(work) };
            var eo = new ExtractOptions
            {
                Progress = o.Progress, Cancel = o.Cancel, MutexWait = o.MutexWait,
                BeforeSeal = tmp => { PatchSlotsInDir(tmp, c, slot.SlotBase); if (o.BeforeSeal != null) o.BeforeSeal(tmp); },
                MarkerExtra = () => SlotMarker(slot.SlotBase, slot),
            };
            res.Extract = PayloadExtractor.EnsureExtracted(Source, c, ShellPaths.InstParentOf(work), eo);
            if (res.Extract.AlreadyPresent) res.Repatched = EnsureSlotBase(res.InstDir, c, slot);
            return res;
        }

        private static JObj SlotMarker(string slotBase, SlotBaseResult slot)
        {
            return new JObj().Add("slotBase", slotBase).Add("slotKind", slot == null ? null : slot.Kind.ToString())
                             .Add("aliasDir", slot == null ? null : slot.AliasDir);
        }

        /// <summary>Re-patches the slot DLLs of an existing INST whose slots do not hold <paramref name="slot"/>'s base.</summary>
        private List<string> EnsureSlotBase(string instDir, PayloadComponent c, SlotBaseResult slot)
        {
            var ver = new SlotFileVerifier(slot.SlotBase, LayoutFor);
            var bad = new List<string>();
            foreach (var f in c.Files.Where(x => x.IsDllSlot))
            {
                string p = Path.Combine(instDir, f.Path.Replace('/', '\\'));
                if (!File.Exists(p) || ver.Check(f, p) != null) bad.Add(f.Path);
            }
            var marker = PayloadExtractor.ReadMarker(instDir);
            bool markerStale = marker == null || !string.Equals(marker.Str("slotBase"), slot.SlotBase, StringComparison.Ordinal);
            if (bad.Count > 0)
            {
                var rr = PayloadExtractor.RepairFiles(Source, c, instDir, bad, (mf, data) => SlotTransform(mf, data, slot.SlotBase));
                if (!rr.Ok) throw new IOException("re-patching the INST slot DLLs failed: " + string.Join("; ", rr.Failed));
            }
            if (bad.Count > 0 || markerStale) PayloadExtractor.WriteMarker(instDir, c, SlotMarker(slot.SlotBase, slot));
            return bad;
        }

        private byte[] SlotTransform(PayloadFile mf, byte[] pristine, string slotBase)
        {
            if (!mf.IsDllSlot) return pristine;
            return DllSlotPatcher.PatchDetailed(pristine, slotBase, LayoutForPristine(mf, pristine)).Bytes;
        }

        private void PatchSlotsInDir(string dir, PayloadComponent c, string slotBase)
        {
            foreach (var f in c.Files.Where(x => x.IsDllSlot))
            {
                string p = Path.Combine(dir, f.Path.Replace('/', '\\'));
                byte[] pristine = File.ReadAllBytes(p);
                var r = DllSlotPatcher.PatchDetailed(pristine, slotBase, LayoutForPristine(f, pristine));
                if (r.IsNoOp) continue;
                string tmp = p + ".slot_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(r.Bytes, 0, r.Bytes.Length);
                    fs.Flush(true);
                    PayloadExtractor.StampMtime(fs.SafeFileHandle);
                }
                SafeFs.MoveReplace(tmp, p);
            }
        }

        private SlotLayout LayoutForPristine(PayloadFile f, byte[] pristine)
        {
            lock (_layoutCache)
            {
                SlotLayout l;
                if (_layoutCache.TryGetValue(f.Md5, out l)) return l;
                l = KnownSlotLayouts.ByPristineMd5(f.Md5) ?? DllSlotPatcher.DiscoverLayout(pristine);
                _layoutCache[f.Md5] = l;
                return l;
            }
        }

        /// <summary>The slot layout of a manifest DLL: a known one by md5, else discovered from the pristine copy in the zip.</summary>
        public SlotLayout LayoutFor(PayloadFile f)
        {
            lock (_layoutCache)
            {
                SlotLayout l;
                if (_layoutCache.TryGetValue(f.Md5, out l)) return l;
                l = KnownSlotLayouts.ByPristineMd5(f.Md5);
                if (l != null) { _layoutCache[f.Md5] = l; return l; }
            }
            var c = Manifest.InstComponent;
            using (var rs = Source.OpenResource(c.Resource))
            using (var zip = new ZipArchive(rs, ZipArchiveMode.Read, false))
            using (var zs = zip.GetEntry(f.Path).Open())
            using (var ms = new MemoryStream())
            {
                zs.CopyTo(ms);
                return LayoutForPristine(f, ms.ToArray());
            }
        }

        // ------------------------------------------------------------------ checks

        /// <summary>Start-up quick check (size + mtime) of every rt component and, with a root, its INST.</summary>
        public CheckReport QuickCheck(string root = null)
        {
            var rep = new CheckReport();
            foreach (var c in Manifest.RtComponents) rep.Components.Add(new ComponentCheck { Component = c, Dir = RtDir(c), Result = PayloadExtractor.QuickCheck(RtDir(c), c) });
            if (root != null)
            {
                string work = ShellPaths.WorkOf(root);
                var c = Manifest.InstComponent;
                rep.Components.Add(new ComponentCheck { Component = c, Dir = DirOf(c, work), Result = PayloadExtractor.QuickCheck(DirOf(c, work), c) });
            }
            return rep;
        }

        /// <summary>
        /// Before every bake: full md5 of INST (slot DLLs re-pristined and their base checked against the CURRENT slot plan,
        /// which SlotAlias.Ensure re-creates when the junction vanished) plus the key runtime binaries (or every runtime file
        /// with <paramref name="allRuntimeFiles"/>, the Repair menu item).
        /// </summary>
        public CheckReport VerifyBeforeBake(string root, CancellationToken ct = default(CancellationToken), bool allRuntimeFiles = false)
        {
            string work = ShellPaths.WorkOf(root);
            Directory.CreateDirectory(work);   // a bake needs it anyway; the slot alias must point at an existing WORK
            var slot = SlotAlias.Ensure(work, Paths.AliasLocalAppData, Paths.AliasVolumeRoot);
            var rep = new CheckReport { SlotBase = slot.SlotBase };
            var c = Manifest.InstComponent;
            string inst = DirOf(c, work);
            rep.Components.Add(new ComponentCheck { Component = c, Dir = inst, Result = PayloadExtractor.FullVerify(inst, c, ct, new SlotFileVerifier(slot.SlotBase, LayoutFor)) });
            foreach (var rc in Manifest.RtComponents)
            {
                Func<PayloadFile, bool> inc = allRuntimeFiles ? (Func<PayloadFile, bool>)null : IsKeyFile;
                rep.Components.Add(new ComponentCheck { Component = rc, Dir = RtDir(rc), Result = PayloadExtractor.FullVerify(RtDir(rc), rc, ct, null, inc) });
            }
            return rep;
        }

        /// <summary>
        /// Repairs what a check found: bad files are re-extracted one by one (slot DLLs re-patched to the current base); a
        /// component whose dir or marker is bad is renamed aside to "&lt;dir&gt;.bad_&lt;ts&gt;" (never deleted) and extracted
        /// again. Returns the re-check of the repaired components.
        /// </summary>
        public CheckReport Repair(string root, CheckReport found, CancellationToken ct = default(CancellationToken))
        {
            string work = root == null ? null : ShellPaths.WorkOf(root);
            SlotBaseResult slot = null;
            var after = new CheckReport();
            foreach (var cc in found.Components.Where(x => !x.Result.Ok))
            {
                var c = cc.Component;
                bool inst = c.Target == PayloadManifest.TargetInst;
                if (inst && slot == null) { Directory.CreateDirectory(work); slot = SlotAlias.Ensure(work, Paths.AliasLocalAppData, Paths.AliasVolumeRoot); }
                string dir = inst ? DirOf(c, work) : RtDir(c);
                if (cc.Result.MarkerBad || !Directory.Exists(dir))
                {
                    if (Directory.Exists(dir))
                        Directory.Move(dir, SafeFs.UniqueChild(Path.GetDirectoryName(dir), Path.GetFileName(dir) + ".bad_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
                    if (inst) EnsureInst(root, new ExtractOptions { Cancel = ct });
                    else PayloadExtractor.EnsureExtracted(Source, c, Paths.RtRoot, new ExtractOptions { Cancel = ct });
                }
                else
                {
                    string sb = inst ? slot.SlotBase : null;
                    var rr = PayloadExtractor.RepairFiles(Source, c, dir, cc.Result.BadFiles, (mf, data) => inst ? SlotTransform(mf, data, sb) : data, ct);
                    if (!rr.Ok) throw new IOException("repair of " + c.Name + " failed: " + string.Join("; ", rr.Failed));
                    if (inst) PayloadExtractor.WriteMarker(dir, c, SlotMarker(slot.SlotBase, slot));
                }
                var res = inst ? PayloadExtractor.FullVerify(dir, c, ct, new SlotFileVerifier(slot.SlotBase, LayoutFor))
                               : PayloadExtractor.FullVerify(dir, c, ct);
                after.Components.Add(new ComponentCheck { Component = c, Dir = dir, Result = res });
            }
            if (slot != null) after.SlotBase = slot.SlotBase;
            return after;
        }

        /// <summary>
        /// App start (FINAL_DESIGN section 9): extract whatever is missing (first run ~4 s), then the quick size + mtime check
        /// of the runtimes (and of the root's INST when a root is given; its slot DLLs are re-patched if the alias moved);
        /// anything the quick check flags is repaired. Returns the final quick check.
        /// </summary>
        public CheckReport StartupEnsure(string root = null, ExtractOptions o = null)
        {
            EnsureRuntimes(o);
            if (root != null) EnsureInst(root, o);
            var q = QuickCheck(root);
            if (q.Ok) return q;
            Repair(root, q, o == null ? default(CancellationToken) : o.Cancel);
            return QuickCheck(root);
        }

        /// <summary>The "Repair" menu item: full md5 of INST and of EVERY runtime file, then repair what is bad.</summary>
        public CheckReport RepairEverything(string root, CancellationToken ct = default(CancellationToken))
        {
            var rep = VerifyBeforeBake(root, ct, true);
            if (rep.Ok) return rep;
            Repair(root, rep, ct);
            return VerifyBeforeBake(root, ct, true);
        }

        /// <summary>VerifyBeforeBake, then Repair if needed; throws when the payload is still bad afterwards.</summary>
        public CheckReport VerifyAndRepairBeforeBake(string root, CancellationToken ct = default(CancellationToken))
        {
            var rep = VerifyBeforeBake(root, ct);
            if (rep.Ok) return rep;
            var after = Repair(root, rep, ct);
            if (!after.Ok) throw new IOException("the payload is damaged and could not be repaired: " + after);
            return VerifyBeforeBake(root, ct);
        }

        // ------------------------------------------------------------------ resolve

        public ResolvedRuntime Resolve(string root)
        {
            string work = ShellPaths.WorkOf(root);
            var pw = Manifest.PwshComponent ?? throw new InvalidDataException("the payload has no pwsh runtime (pwsh.exe)");
            var py = Manifest.PyComponent ?? throw new InvalidDataException("the payload has no Python runtime (py.exe)");
            var ic = Manifest.InstComponent;
            string inst = InstDir(work);
            var marker = PayloadExtractor.ReadMarker(inst);
            Func<string, string> instFile = rel => ic.Has(rel) ? Path.Combine(inst, rel.Replace('/', '\\')) : null;
            var bdb = ic.Files.FirstOrDefault(f => f.HasRole(PayloadFile.RoleBoundsDb)) ?? ic.Files.FirstOrDefault(f => string.Equals(f.FileName, "h2v_scenery_bounds.db", StringComparison.OrdinalIgnoreCase));
            var gc = ic.Files.FirstOrDefault(f => f.HasRole(PayloadFile.RoleGpuCheck)) ?? ic.Files.FirstOrDefault(f => string.Equals(f.FileName, "gpucheck.exe", StringComparison.OrdinalIgnoreCase));
            return new ResolvedRuntime
            {
                AppVersion = Manifest.AppVersion,
                Root = SafeFs.Norm(root), Work = work, InstDir = inst,
                SlotBase = marker == null ? null : marker.Str("slotBase"),
                PwshDir = RtDir(pw), PwshExe = Path.Combine(RtDir(pw), "pwsh.exe"),
                PyDir = RtDir(py), PyExe = Path.Combine(RtDir(py), "py.exe"),
                GpuBakePs1 = instFile("engine_v373/gpu_bake.ps1"),
                LmwInstallPy = instFile("engine_v373/v043-tagwriter/lmw_install.py"),
                BoundsDb = bdb == null ? null : Path.Combine(inst, bdb.Path.Replace('/', '\\')),
                GpuCheckExe = gc == null ? null : Path.Combine(inst, gc.Path.Replace('/', '\\')),
            };
        }

        // ------------------------------------------------------------------ old versions (only on request)

        /// <summary>Folders under rt\ (and, with a root, WORK\_runtime\) that are not this payload's: older versions and
        /// leftover ".tmp_" / ".bad_" dirs.</summary>
        public List<OldVersionInfo> ListOldVersions(string root = null)
        {
            var r = new List<OldVersionInfo>();
            var keepRt = new HashSet<string>(Manifest.RtComponents.Select(c => c.DirName), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Paths.RtRoot))
                foreach (var d in Directory.GetDirectories(Paths.RtRoot))
                    if (!keepRt.Contains(Path.GetFileName(d))) r.Add(new OldVersionInfo { Path = d, Parent = Paths.RtRoot, Kind = "rt" });
            if (root != null)
            {
                string parent = ShellPaths.InstParentOf(ShellPaths.WorkOf(root));
                string keep = Manifest.InstComponent.DirName;
                if (Directory.Exists(parent))
                    foreach (var d in Directory.GetDirectories(parent))
                        if (!string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase)) r.Add(new OldVersionInfo { Path = d, Parent = parent, Kind = "inst" });
            }
            return r;
        }

        /// <summary>Removes one old version folder (only one ListOldVersions returned). Refuses -- deleting nothing -- when the
        /// folder holds any reparse point, or when another shell holds its extraction mutex. Returns the files removed.</summary>
        public int RemoveOldVersion(OldVersionInfo v)
        {
            var fresh = ListOldVersions(v.Kind == "inst" ? Path.GetDirectoryName(Path.GetDirectoryName(v.Parent)) : null);
            if (!fresh.Any(x => string.Equals(SafeFs.Norm(x.Path), SafeFs.Norm(v.Path), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("refused: " + v.Path + " is not an old version folder of this payload");
            string leaf = Path.GetFileName(v.Path);
            int cut = leaf.IndexOf(".tmp_", StringComparison.Ordinal);
            if (cut < 0) cut = leaf.IndexOf(".bad_", StringComparison.Ordinal);
            string dirName = cut > 0 ? leaf.Substring(0, cut) : leaf;
            using (var mtx = new Mutex(false, @"Local\H2GpuLightmapper.extract." + dirName))
            {
                bool owned = false;
                try
                {
                    try { owned = mtx.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new InvalidOperationException("refused: another instance is using " + dirName);
                    return SafeFs.RemoveTreeNoReparse(v.Path, v.Parent);
                }
                finally { if (owned) mtx.ReleaseMutex(); }
            }
        }
    }
}
