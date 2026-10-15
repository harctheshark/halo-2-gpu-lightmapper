using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace H2GpuLightmapper.DllSlots
{
    public enum SlotBaseKind
    {
        /// <summary>WORK\ itself fits the slot (a short H2EK folder, Steam's ...\H2EK, ...). No junction.</summary>
        Direct = 0,
        /// <summary>%LOCALAPPDATA%\H2GLM\&lt;k6&gt;\ -> junction to WORK.</summary>
        AliasLocalAppData = 1,
        /// <summary>&lt;WORK's volume&gt;:\H2GLM\&lt;k6&gt;\ -> junction to WORK.</summary>
        AliasVolumeRoot = 2,
    }

    /// <summary>One usable slot base. For aliases, <see cref="AliasDir"/> is the junction directory (no trailing '\').</summary>
    public sealed class SlotBaseCandidate
    {
        public SlotBaseKind Kind { get; }
        /// <summary>The exact string to write into the DLL slots (ends with '\', &lt;= 60 ANSI bytes).</summary>
        public string SlotBase { get; }
        public string AliasDir { get; }
        public SlotBaseCandidate(SlotBaseKind kind, string slotBase, string aliasDir) { Kind = kind; SlotBase = slotBase; AliasDir = aliasDir; }
        public override string ToString() => Kind + " " + SlotBase;
    }

    /// <summary>Pure planning result: which slot base WORK gets. <see cref="SlotBase"/> / <see cref="Kind"/> are the first
    /// usable candidate; <see cref="Candidates"/> lists every usable one in preference order (Ensure falls through them).</summary>
    public sealed class SlotBasePlan
    {
        /// <summary>The REAL work dir (normalized, no trailing '\'). H2LM_WORK always stays this path.</summary>
        public string WorkDir { get; internal set; }
        public string Key6 { get; internal set; }
        public int CodePage { get; internal set; }
        public bool WorkDirIsAnsi { get; internal set; }
        public IReadOnlyList<SlotBaseCandidate> Candidates { get; internal set; }
        /// <summary>Why each rejected candidate (including WORK\ itself) was not usable.</summary>
        public IReadOnlyList<string> Rejected { get; internal set; }
        public SlotBaseKind Kind => Candidates[0].Kind;
        public string SlotBase => Candidates[0].SlotBase;
        public string AliasDir => Candidates[0].AliasDir;
        public bool IsAlias => Kind != SlotBaseKind.Direct;
    }

    public enum AliasState { NotNeeded, Created, Reused }

    /// <summary>What <see cref="SlotAlias.Ensure"/> settled on, after creating or reusing the junction.</summary>
    public sealed class SlotBaseResult
    {
        public string WorkDir { get; internal set; }
        public SlotBaseKind Kind { get; internal set; }
        /// <summary>Patch the DLL slots with exactly this string.</summary>
        public string SlotBase { get; internal set; }
        public string AliasDir { get; internal set; }
        public AliasState State { get; internal set; }
        public string Key6 { get; internal set; }
        public IReadOnlyList<string> Notes { get; internal set; }

        public string ToJson() =>
            "{\"work\":" + Json.Str(WorkDir) + ",\"kind\":" + Json.Str(Kind.ToString()) + ",\"slot_base\":" + Json.Str(SlotBase) +
            ",\"alias_dir\":" + Json.Str(AliasDir) + ",\"state\":" + Json.Str(State.ToString()) + ",\"key6\":" + Json.Str(Key6) +
            ",\"notes\":[" + string.Join(",", Notes.Select(Json.Str)) + "]}";
    }

    /// <summary>
    /// FINAL_DESIGN section 2: "DLL slot = WORK\ when it fits in 60 characters. Otherwise the slot gets an alias junction,
    /// tried in this order: 1. %LOCALAPPDATA%\H2GLM\&lt;k6&gt;\ if it is ANSI and &lt;= 60; 2. else &lt;ROOT volume&gt;:\H2GLM\&lt;k6&gt;\;
    /// 3. else refuse." Pure: touches no file system (only Path.GetFullPath).
    /// </summary>
    public static class SlotAliasPlanner
    {
        public const string AliasFolderName = "H2GLM";

        /// <summary>First 6 lowercase hex chars of SHA-256(UTF-8(upper-invariant normalized WORK)). Stable per directory,
        /// case-insensitive like NTFS.</summary>
        public static string Key6(string workDir)
        {
            string n = Junction.NormalizeDir(workDir).ToUpperInvariant();
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(n));
                return string.Concat(h.Take(3).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>Plans with the real %LOCALAPPDATA%, WORK's own volume root and the system ANSI code page.</summary>
        public static SlotBasePlan Plan(string workDir) =>
            Plan(workDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null, null);

        /// <param name="workDir">The real WORK dir (ROOT\GPU Lightmap Work).</param>
        /// <param name="localAppData">Candidate 1's parent (null/empty = skip candidate 1).</param>
        /// <param name="volumeRoot">Candidate 2's parent; default = WORK's drive root ("G:\").</param>
        /// <param name="codePage">Default = <see cref="AnsiCodePage.System"/>.</param>
        /// <exception cref="SlotAliasRefusedException">No candidate fits (step 3: refuse).</exception>
        public static SlotBasePlan Plan(string workDir, string localAppData, string volumeRoot = null, int? codePage = null)
        {
            int cp = codePage ?? AnsiCodePage.System;
            string work = Junction.NormalizeDir(workDir);
            string k6 = Key6(work);
            var ok = new List<SlotBaseCandidate>();
            var rejected = new List<string>();

            string direct = work.Length == 3 ? work : work + @"\";
            if (DllSlotPatcher.TryValidateBase(direct, cp, out _, out var why)) ok.Add(new SlotBaseCandidate(SlotBaseKind.Direct, direct, null));
            else rejected.Add("WORK\\ '" + direct + "': " + why);

            if (ok.Count == 0)
            {
                if (!string.IsNullOrEmpty(localAppData)) Consider(SlotBaseKind.AliasLocalAppData, localAppData, k6, work, cp, ok, rejected);
                else rejected.Add("%LOCALAPPDATA% candidate: not available");
                string vr = string.IsNullOrEmpty(volumeRoot) ? Path.GetPathRoot(work) : volumeRoot;
                Consider(SlotBaseKind.AliasVolumeRoot, vr, k6, work, cp, ok, rejected);
            }
            if (ok.Count == 0)
                throw new SlotAliasRefusedException("no DLL slot base fits for WORK '" + work + "' (max 60 ANSI bytes): " + string.Join("; ", rejected));

            return new SlotBasePlan
            {
                WorkDir = work, Key6 = k6, CodePage = cp, WorkDirIsAnsi = AnsiCodePage.RoundTrips(work, cp),
                Candidates = ok, Rejected = rejected,
            };
        }

        private static void Consider(SlotBaseKind kind, string parent, string k6, string work, int cp, List<SlotBaseCandidate> ok, List<string> rejected)
        {
            string label = kind == SlotBaseKind.AliasLocalAppData ? "%LOCALAPPDATA% alias" : "volume-root alias";
            string p;
            try { p = Junction.NormalizeDir(parent); }
            catch (ArgumentException e) { rejected.Add(label + " under '" + parent + "': " + e.Message); return; }
            string aliasDir = (p.Length == 3 ? p : p + @"\") + AliasFolderName + @"\" + k6;
            string slot = aliasDir + @"\";
            if (aliasDir.StartsWith(work + @"\", StringComparison.OrdinalIgnoreCase) || string.Equals(aliasDir, work, StringComparison.OrdinalIgnoreCase))
            { rejected.Add(label + " '" + slot + "': inside WORK (a junction cycle)"); return; }
            if (DllSlotPatcher.TryValidateBase(slot, cp, out _, out var why)) ok.Add(new SlotBaseCandidate(kind, slot, aliasDir));
            else rejected.Add(label + " '" + slot + "': " + why);
        }
    }

    /// <summary>The file-system half: makes the planned slot base real.</summary>
    public static class SlotAlias
    {
        /// <summary>Plans with the real %LOCALAPPDATA% / volume root / system ACP and ensures the junction.</summary>
        public static SlotBaseResult Ensure(string workDir) =>
            Ensure(workDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null, null);

        /// <summary>
        /// Direct: checks WORK exists. Alias: for each usable candidate in order -- reuse an existing junction that points
        /// at WORK and resolves to the same directory object, else create the H2GLM parent (a plain folder) and the
        /// junction, then prove it resolves to WORK (volume serial + file index, nothing is written into WORK). A path that
        /// exists but is not our junction (a plain folder, a junction to elsewhere = k6 collision) is never touched; the
        /// next candidate is tried. Throws <see cref="SlotAliasRefusedException"/> when none works.
        /// </summary>
        public static SlotBaseResult Ensure(string workDir, string localAppData, string volumeRoot = null, int? codePage = null)
        {
            var plan = SlotAliasPlanner.Plan(workDir, localAppData, volumeRoot, codePage);
            if (!Directory.Exists(plan.WorkDir)) throw new SlotAliasRefusedException("WORK does not exist: " + plan.WorkDir);
            var notes = new List<string>(plan.Rejected);
            if (!plan.IsAlias)
                return new SlotBaseResult { WorkDir = plan.WorkDir, Kind = SlotBaseKind.Direct, SlotBase = plan.SlotBase, State = AliasState.NotNeeded, Key6 = plan.Key6, Notes = notes };

            foreach (var c in plan.Candidates)
            {
                try
                {
                    var st = EnsureOne(c.AliasDir, plan.WorkDir);
                    return new SlotBaseResult { WorkDir = plan.WorkDir, Kind = c.Kind, SlotBase = c.SlotBase, AliasDir = c.AliasDir, State = st, Key6 = plan.Key6, Notes = notes };
                }
                catch (Exception e) when (e is JunctionException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                {
                    notes.Add(c + ": " + e.Message);
                }
            }
            throw new SlotAliasRefusedException("no alias for WORK '" + plan.WorkDir + "' could be made: " + string.Join("; ", notes));
        }

        private static AliasState EnsureOne(string aliasDir, string work)
        {
            if (Junction.PathExists(aliasDir))
            {
                if (!Junction.TryGetTarget(aliasDir, out var t)) throw new JunctionException(aliasDir + " exists and is not a junction -- left untouched");
                if (!Junction.SamePathText(t, work)) throw new JunctionException(aliasDir + " is a junction to '" + t + "' (key collision) -- left untouched");
                if (!Junction.SameDirectory(aliasDir, work)) throw new JunctionException(aliasDir + " does not resolve to WORK -- left untouched");
                return AliasState.Reused;
            }
            string parent = Path.GetDirectoryName(aliasDir);
            if (Junction.IsJunction(parent)) throw new JunctionException(parent + " is itself a junction -- refusing to nest");
            Directory.CreateDirectory(parent);
            Junction.Create(aliasDir, work);
            if (!Junction.SameDirectory(aliasDir, work))
            {
                Junction.Remove(aliasDir, work);
                throw new JunctionException("new junction " + aliasDir + " does not resolve to WORK; removed it");
            }
            return AliasState.Created;
        }

        /// <summary>Removes an alias made by <see cref="Ensure(string)"/> (RemoveDirectory on the junction only, and only
        /// when it points at <paramref name="workDir"/>). The H2GLM parent folder is left in place.</summary>
        public static void RemoveAlias(string aliasDir, string workDir) => Junction.Remove(aliasDir, workDir);
    }
}
