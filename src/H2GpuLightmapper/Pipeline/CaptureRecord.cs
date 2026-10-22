// S3.2 capture record and reuse gating (FINAL_DESIGN section 7).
//   After every successful capture the shell writes OUT\standalone_capture.json: quality, P, MC, sky_hi, fixes, the app
//   version, the binary md5s (estimator DLL pristine, fullgpu, gpu_bake, tool_fast, bounds DB), capture_id.json's soup crc,
//   and a stat fingerprint (path, size, mtime) of the whole tags tree minus this BSP's own lightmap outputs, kept beside it in
//   OUT\standalone_capture_tags.tsv.
//   Reuse is offered only when the record exists, its crc matches capture_id.json, quality / P / MC match, fixes and binaries
//   match, and (probes) sky_tex_hi.bin + lp_globals.bin are present. A tags-only difference keeps reuse enabled but needs an
//   explicit confirmation listing the changed tags -- never silent. 0.6.6: only BAKE-RELEVANT changes (TagRelevance) ask; the others
//   are listed as information, and a file whose mtime moved but whose md5 equals the one stored at capture time (4th column of the
//   tags tsv, relevant files <= TagRelevanceCtx.HashCap) is an identical re-save, not a change. A record without hashes keeps the
//   mtime comparison. A farm without a record (legacy / research) is never
//   offered. gpu_bake's own -SkipCapture checks still run underneath.
//   Before a FRESH capture into a farm the old record is renamed aside (never deleted), so a capture that fails half-way can
//   never be vouched for by an older record.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    /// <summary>What a capture made by THIS bake would be (compared field by field with a record).</summary>
    public sealed class CaptureIdentity
    {
        public string Quality;
        public long? Photons;
        public int? Mc;
        public bool SkyHi;
        public bool Plucker, Scenery;
        public string AppVersion;
        /// <summary>Custom only (review fix, finding 16): true = the N3 guarded engine_custom fullgpu, false = the unguarded
        /// production fullgpu (dev). Recorded so T3 evidence made without the guard cannot pass for guarded.</summary>
        public bool? Guarded;
        /// <summary>"Scenery Multi Permutation Fix": the lightmap capture was made WITH -SceneryOwnPermutationOnly (the engine records the tool's per-placement
        /// permutation pick in phase 1, so a capture is reusable only by a bake with the same setting). Never set for the probe worker's own capture
        /// (it is made by gpu_lightprobes, which does not get the switch). A record without the key counts as false.</summary>
        public bool SceneryPermutationFix;
        /// <summary>name -> md5 (null when unknown): estimatorDll, fullgpu, gpuBake, toolFast, boundsDb.</summary>
        public SortedDictionary<string, string> Binaries = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public static CaptureIdentity For(BakeOptions o, PayloadManifest m, string root, string gpuBakeRel, bool skyHi, string toolMd5 = null)
        {
            var id = new CaptureIdentity
            {
                Quality = o.QualityToken, SkyHi = skyHi, Plucker = o.Fixes, Scenery = o.Fixes,
                Photons = o.Quality == App.Quality.Custom ? (long?)o.Photons : null,
                Mc = o.Quality == App.Quality.Custom ? (int?)o.Rays : null,
                AppVersion = m == null ? null : m.AppVersion,
                Guarded = o.Quality == App.Quality.Custom ? (bool?)(gpuBakeRel == InstLayout.GpuBakeCustom) : null,
                SceneryPermutationFix = o.SceneryPermutationFix && !skyHi,
            };
            var inst = m == null ? null : m.InstComponent;
            Func<string, string> md5Of = rel => inst == null ? null : inst.Find(rel) == null ? null : inst.Find(rel).Md5.ToLowerInvariant();
            id.Binaries["estimatorDll"] = md5Of(InstLayout.EstimatorDll);
            id.Binaries["fullgpu"] = md5Of(gpuBakeRel.Replace("gpu_bake.ps1", "v041-fullgpu/fullgpu.exe"));
            id.Binaries["gpuBake"] = md5Of(gpuBakeRel);
            id.Binaries["toolFast"] = toolMd5 ?? Md5OrNull(Path.Combine(root, "tool_fast.exe"));
            id.Binaries["boundsDb"] = Md5OrNull(Path.Combine(root, "h2v_scenery_bounds.db"));
            // SF1: the Scenery Fix Dependencies database under its own name (the legacy one above stays recorded too). A record written
            // before this key existed is compared on boundsDb only (see Evaluate), so old captures are not invalidated for nothing.
            id.Binaries[SceneryFixKey] = Md5OrNull(Path.Combine(root, "SceneryFixDependencies.db"));
            return id;
        }

        /// <summary>The Binaries key of SceneryFixDependencies.db's md5.</summary>
        public const string SceneryFixKey = "sceneryFixDb";

        public static string Md5OrNull(string p) { try { return File.Exists(p) ? SafeFs.Md5File(p) : null; } catch (IOException) { return null; } }

        public string Describe()
        {
            return Quality + (Photons.HasValue ? string.Format(CultureInfo.InvariantCulture, " {0:N0} photons / MC {1}", Photons, Mc) : "") + (SkyHi ? " (probe-ready sky)" : "");
        }
    }

    public sealed class ReuseVerdict
    {
        /// <summary>The capture may be reused (possibly after the changed-tags confirmation).</summary>
        public bool Allowed;
        /// <summary>Why not (shown inline next to the disabled checkbox).</summary>
        public string Reason;
        public bool TagsChanged;
        public List<string> ChangedTags = new List<string>();
        public int ChangedTagsTotal;
        /// <summary>0.6.6: tags that changed since the capture but do not feed the lightmap (information only; never asks).</summary>
        public List<string> InfoTags = new List<string>();
        public int InfoTagsTotal;
        /// <summary>0.6.6: tags re-saved with identical content (md5 equal to the recorded one) -- ignored.</summary>
        public int IdenticalResaves;
        public JObj Record;
        public string RecordPath;
        public bool Unrecorded;
        public string Describe()
        {
            if (!Allowed) return "not reusable: " + Reason;
            var r = Record;
            string what = r == null ? "an unrecorded capture (dev)" : r.Str("quality") + " capture of " + (r.Str("written") ?? "?");
            return "reusable: " + what + (TagsChanged ? " -- " + ChangedTagsTotal + " tag(s) changed since (confirmation needed)" : "")
                + (InfoTagsTotal > 0 ? " (+" + InfoTagsTotal + " unrelated tag change(s), info only)" : "");
        }
    }

    public static class CaptureRecord
    {
        public const string FileName = "standalone_capture.json";
        public const string TagsFileName = "standalone_capture_tags.tsv";
        public const string Schema = "h2lm-standalone-capture/1";

        public static string PathIn(string outDir) { return Path.Combine(outDir, FileName); }

        /// <summary>The soup crc of OUT\capture_id.json (null if absent / unreadable).</summary>
        public static string SoupCrc(string outDir)
        {
            try
            {
                var j = Json.ReadObjFile(Path.Combine(outDir, "capture_id.json"));
                return j == null ? null : j.Str("soup_crc32");
            }
            catch (Exception) { return null; }
        }

        /// <summary>Before a fresh capture: rename an existing record (and its tags file) aside. Never deletes.</summary>
        public static List<string> SupersedeExisting(string outDir, DateTime now)
        {
            var moved = new List<string>();
            string ts = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            foreach (var n in new[] { FileName, TagsFileName })
            {
                string p = Path.Combine(outDir, n);
                if (!File.Exists(p)) continue;
                string dst = SafeFs.UniqueChild(outDir, n + ".superseded_" + ts);
                SafeFs.MoveNoReplace(p, dst);
                moved.Add(dst);
            }
            return moved;
        }

        /// <summary>Writes the record for the capture now in <paramref name="outDir"/>. Returns null on success, else why not.</summary>
        public static string Write(string outDir, CaptureIdentity id, string scenarioRel, string bsp, int worker, string runTag, TagsFingerprint tags, DateTime now)
        {
            string crc = SoupCrc(outDir);
            if (crc == null) return "no capture_id.json soup crc in " + outDir + " -- no record written (this capture will not be offered for reuse)";
            string cidMd5 = CaptureIdentity.Md5OrNull(Path.Combine(outDir, "capture_id.json"));
            if (tags != null) tags.Save(Path.Combine(outDir, TagsFileName));
            var bin = new JObj();
            foreach (var kv in id.Binaries) bin.Add(kv.Key, kv.Value);
            var j = new JObj().Add("schema", Schema).Add("written", now.ToString("s", CultureInfo.InvariantCulture)).Add("runTag", runTag)
                .Add("scenario", scenarioRel).Add("bsp", bsp).Add("worker", (long)worker)
                .Add("quality", id.Quality).Add("photons", id.Photons).Add("mcSamples", id.Mc).Add("skyHi", id.SkyHi)
                .Add("fixes", new JObj().Add("plucker", id.Plucker).Add("scenery", id.Scenery))
                .Add("appVersion", id.AppVersion).Add("guarded", id.Guarded.HasValue ? (object)id.Guarded.Value : null)
                .Add("sceneryPermutationFix", id.SceneryPermutationFix).Add("binaries", bin)
                .Add("captureId", new JObj().Add("soup_crc32", crc).Add("md5", cidMd5))
                .Add("tags", tags == null ? null : new JObj().Add("file", TagsFileName).Add("files", (long)tags.Count).Add("md5", tags.Md5).Add("hashed", (long)tags.HashedCount));
            SafeFs.WriteAllTextAtomic(PathIn(outDir), Json.Write(j, true));
            return null;
        }

        /// <summary>
        /// After the shell's own install wrote tags that the fingerprint covers (only the .scenario, on a first bake: the set
        /// files are excluded anyway), refresh exactly THOSE lines of a record's fingerprint, so our own install never reads as
        /// "tags changed" -- while any other change since the capture (one the user may have confirmed for this bake) keeps
        /// warning on the next reuse. <paramref name="relPaths"/> are relative to ROOT\tags.
        /// </summary>
        public static bool RestampFiles(string outDir, string root, IEnumerable<string> relPaths)
        {
            var j = Read(outDir);
            var t = j == null ? null : j.Obj("tags");
            if (t == null) return false;
            string tsv = Path.Combine(outDir, t.Str("file") ?? TagsFileName);
            var fp = TagsFingerprint.Load(tsv);
            if (fp == null) return false;
            bool changed = false;
            foreach (var rel in relPaths)
            {
                string full = Path.Combine(root, "tags", rel);
                int i = fp.Lines.FindIndex(l => l.StartsWith(rel + "\t", StringComparison.OrdinalIgnoreCase));
                string line = null;
                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    line = rel + "\t" + fi.Length.ToString(CultureInfo.InvariantCulture) + "\t" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    // 0.6.6: a line that carried a content hash keeps one (the file as our install wrote it)
                    if (i >= 0 && fp.Lines[i].Split('\t').Length >= 4 && fi.Length <= TagRelevanceCtx.HashCap)
                    { try { line += "\t" + SafeFs.Md5File(full); } catch (IOException) { } }
                }
                if (i >= 0 && line != null) { if (fp.Lines[i] != line) { fp.Lines[i] = line; changed = true; } }
                else if (i >= 0) { fp.Lines.RemoveAt(i); changed = true; }
                else if (line != null) { fp.Lines.Add(line); changed = true; }
            }
            if (!changed) return false;
            fp.Lines.Sort(StringComparer.OrdinalIgnoreCase);
            fp = TagsFingerprint.FromLinesPublic(fp.Lines);
            fp.Save(tsv);
            j["tags"] = new JObj().Add("file", Path.GetFileName(tsv)).Add("files", (long)fp.Count).Add("md5", fp.Md5).Add("hashed", (long)fp.HashedCount);
            SafeFs.WriteAllTextAtomic(PathIn(outDir), Json.Write(j, true));
            return true;
        }

        public static JObj Read(string outDir)
        {
            try { return Json.ReadObjFile(PathIn(outDir)); } catch (Exception) { return null; }
        }

        /// <summary>
        /// The reuse gate. <paramref name="tagsNow"/> = the current fingerprint (null: compute it here; pass the same object for
        /// several farms). <paramref name="allowUnrecorded"/> (dev, gate T2-B) accepts a farm with a capture but no record.
        /// </summary>
        public static ReuseVerdict Evaluate(string outDir, CaptureIdentity want, string scenarioRel, string bsp, bool forProbes,
                                            Func<TagsFingerprint> tagsNow, bool allowUnrecorded = false)
        {
            var v = new ReuseVerdict { RecordPath = PathIn(outDir) };
            string farm = Path.GetFileName(outDir.TrimEnd('\\'));
            if (!Directory.Exists(outDir)) return Refuse(v, farm + " has no capture yet");
            string crc = SoupCrc(outDir);
            var j = Read(outDir);
            if (j == null)
            {
                if (File.Exists(PathIn(outDir))) return Refuse(v, farm + "\\" + FileName + " is unreadable");
                if (!allowUnrecorded) return Refuse(v, farm + " has no standalone capture record (a legacy or research capture is never reused)");
                if (crc == null) return Refuse(v, farm + " has no capture (capture_id.json)");
                if (forProbes && !ProbeReady(outDir)) return Refuse(v, farm + " is not probe-ready (no sky_tex_hi.bin / lp_globals.bin)");
                v.Allowed = true; v.Unrecorded = true;
                return v;
            }
            v.Record = j;
            if (j.Str("schema") != Schema) return Refuse(v, "unknown record schema " + j.Str("schema"));
            if (crc == null) return Refuse(v, "capture_id.json is missing: the capture is gone or incomplete");
            var rc = j.Obj("captureId");
            if (rc == null || !string.Equals(rc.Str("soup_crc32"), crc, StringComparison.OrdinalIgnoreCase))
                return Refuse(v, "the capture in " + farm + " changed after it was recorded (soup crc " + (rc == null ? "?" : rc.Str("soup_crc32")) + " -> " + crc + ")");
            if (!SameText(j.Str("scenario"), scenarioRel) || !SameText(j.Str("bsp"), bsp))
                return Refuse(v, farm + " holds a capture of " + j.Str("scenario") + " / " + j.Str("bsp"));
            string recQ = j.Str("quality");
            long? recP = j.Long("photons"); int? recMc = j.Int("mcSamples");
            if (!SameText(recQ, want.Quality) || recP != want.Photons || recMc != want.Mc)
                return Refuse(v, "the capture is " + DescribeQ(recQ, recP, recMc) + "; this bake is " + DescribeQ(want.Quality, want.Photons, want.Mc));
            var fx = j.Obj("fixes");
            if (fx == null || fx.Bool("plucker") != want.Plucker || fx.Bool("scenery") != want.Scenery)
                return Refuse(v, "the capture was made with different Osoyoos fixes");
            if ((j.Bool("sceneryPermutationFix") == true) != want.SceneryPermutationFix)
                return Refuse(v, want.SceneryPermutationFix
                    ? "the capture was made without \"" + BakeOptions.SceneryPermutationFixLabel + "\", which needs its own capture (the engine records the tool's shape pick while capturing)"
                    : "the capture was made with \"" + BakeOptions.SceneryPermutationFixLabel + "\"; this bake has it off");
            var bin = j.Obj("binaries") ?? new JObj();
            var diff = new List<string>();
            foreach (var kv in want.Binaries)
            {
                if (kv.Key == CaptureIdentity.SceneryFixKey && !bin.Has(kv.Key)) continue;     // an older record: boundsDb vouches (the scanner keeps the legacy file in step)
                if (!SameText(bin.Str(kv.Key), kv.Value)) diff.Add(kv.Key);
            }
            if (diff.Count > 0)
            {
                bool dbOnly = diff.All(d => d == "boundsDb" || d == CaptureIdentity.SceneryFixKey);
                return Refuse(v, dbOnly ? "the Scenery Fix Dependencies database changed since the capture (" + string.Join(", ", diff) + "): its models were captured with the old one -- bake with a new capture"
                                        : "the engine changed since the capture (" + string.Join(", ", diff) + ")");
            }
            if (forProbes)
            {
                if (j.Bool("skyHi") != true) return Refuse(v, "the capture was not made for light probes (no GPULM_SKY_HI)");
                if (!ProbeReady(outDir)) return Refuse(v, farm + " is not probe-ready (sky_tex_hi.bin / lp_globals.bin missing)");
            }
            // tags: a difference never disables reuse, it needs an explicit confirmation
            var t = j.Obj("tags");
            if (t == null) { v.TagsChanged = true; v.ChangedTags.Add("(the record has no tags fingerprint)"); v.ChangedTagsTotal = 1; }
            else if (tagsNow != null)
            {
                var now = tagsNow();
                if (now != null && !SameText(t.Str("md5"), now.Md5))
                {
                    // V5: Load drops the tool's own scratch lines (tags\temp_scenario\) an older record still lists, so a record
                    // whose only difference is the scratch file another capture created / removed is not "tags changed"
                    var old = TagsFingerprint.Load(Path.Combine(outDir, t.Str("file") ?? TagsFileName));
                    if (old == null || !SameText(old.Md5, now.Md5))
                    {
                        if (old == null)
                        {
                            v.TagsChanged = true;
                            v.ChangedTags = new List<string> { "(the recorded fingerprint file is missing)" };
                            v.ChangedTagsTotal = 1;
                        }
                        else
                        {
                            // 0.6.6: relevance split + content hash for the files whose mtime moved with an equal size
                            var d = TagsFingerprint.Classify(old, now, TagRelevanceCtx.Build(now.Root ?? "", scenarioRel, bsp, now.Lines.Concat(old.Lines).Select(TagsFingerprint.KeyOf).Distinct(StringComparer.OrdinalIgnoreCase)), 200);
                            v.ChangedTags = d.Relevant; v.ChangedTagsTotal = d.RelevantTotal; v.TagsChanged = d.RelevantTotal > 0;
                            v.InfoTags = d.Info; v.InfoTagsTotal = d.InfoTotal; v.IdenticalResaves = d.Identical;
                        }
                    }
                }
            }
            v.Allowed = true;
            return v;
        }

        public static bool ProbeReady(string outDir)
        {
            return File.Exists(Path.Combine(outDir, "sky_tex_hi.bin")) && File.Exists(Path.Combine(outDir, "lp_globals.bin"));
        }

        private static ReuseVerdict Refuse(ReuseVerdict v, string why) { v.Allowed = false; v.Reason = why; return v; }
        private static bool SameText(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }
        private static string DescribeQ(string q, long? p, int? mc)
        {
            return (q ?? "?") + (p.HasValue ? string.Format(CultureInfo.InvariantCulture, " {0:N0} photons / MC {1}", p, mc) : "");
        }
    }

    /// <summary>
    /// A stat fingerprint of ROOT\tags: one line "relpath TAB size TAB mtimeUtcTicks" per file, sorted, minus this BSP's own
    /// lightmap outputs ("&lt;leaf&gt;_&lt;bsp&gt;_lightmap*" in its folder, incl. the install's .gpulm_ side files) and Sapien's
    /// "(auto-backup ...)" files. Reparse points are never followed. About 1 s for 37k files.
    /// V5 (gate T4 finding): tags\temp_scenario\ is excluded too -- it is the tool's own scratch folder: every capture (tool_fast
    /// lightmaps-farm-worker -tags_dir tags) writes / replaces tags\temp_scenario\sapien_temp_scenario_0.scenario, so a Standard
    /// bake's own probe capture made its lightmap capture read as "1 tag changed" and every reuse after another bake needed --yes.
    /// </summary>
    public sealed class TagsFingerprint
    {
        public List<string> Lines = new List<string>();
        public string Md5;
        public int Count { get { return Lines.Count; } }
        /// <summary>The H2EK root this fingerprint was computed from (null for one loaded from a record); lets a comparison hash a file lazily.</summary>
        public string Root;
        /// <summary>0.6.6: lines that carry a content hash (4th column).</summary>
        public int HashedCount { get { int n = 0; foreach (var l in Lines) if (CountTabs(l) >= 3) n++; return n; } }

        private static int CountTabs(string l) { int n = 0; foreach (char c in l) if (c == '\t') n++; return n; }
        /// <summary>The path (first column) of a line.</summary>
        public static string KeyOf(string l) { int i = l.IndexOf('\t'); return i < 0 ? l : l.Substring(0, i); }
        /// <summary>"path TAB size TAB mtime" -- the line without its optional content hash.</summary>
        private static string Stat3(string l)
        {
            int i = l.IndexOf('\t'); if (i < 0) return l;
            i = l.IndexOf('\t', i + 1); if (i < 0) return l;
            i = l.IndexOf('\t', i + 1);
            return i < 0 ? l : l.Substring(0, i);
        }
        private static string HashOf(string l)
        {
            int i = l.IndexOf('\t'); if (i < 0) return null;
            i = l.IndexOf('\t', i + 1); if (i < 0) return null;
            i = l.IndexOf('\t', i + 1);
            return i < 0 ? null : l.Substring(i + 1);
        }
        private static long SizeOf(string l)
        {
            var p = l.Split('\t');
            long n;
            return p.Length >= 3 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        /// <summary>The top-level tags folder the tool uses as scratch (never a game tag); not fingerprinted.</summary>
        public const string ToolScratchDir = "temp_scenario";

        /// <summary>Is this fingerprint line / tags-relative path inside the tool's scratch folder?</summary>
        public static bool IsToolScratch(string relOrLine)
        {
            return relOrLine != null && relOrLine.StartsWith(ToolScratchDir + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The fingerprint a CAPTURE records (0.6.6): the stat lines plus an md5 4th column on every bake-relevant file of at most
        /// <see cref="TagRelevanceCtx.HashCap"/> bytes (scenario folder, this BSP's folder, scenery / shader / light / sky ... tags, the bitmaps and
        /// render_models this map uses). Md5 (the whole-fingerprint check) is over the stat columns only, so it equals a plain Compute of the same state.</summary>
        public static TagsFingerprint ComputeWithHashes(string root, TagSet exclude, string scenarioRel, string bsp)
        {
            var fp = Compute(root, exclude);
            var ctx = TagRelevanceCtx.Build(root, scenarioRel, bsp, fp.Lines.Select(KeyOf));
            string tags = Path.Combine(root, "tags");
            for (int i = 0; i < fp.Lines.Count; i++)
            {
                string l = fp.Lines[i];
                string rel = KeyOf(l);
                long size = SizeOf(l);
                if (size < 0 || size > TagRelevanceCtx.HashCap || !ctx.IsRelevant(rel)) continue;
                try { fp.Lines[i] = l + "\t" + SafeFs.Md5File(Path.Combine(tags, rel)); } catch (Exception) { }     // unreadable: no hash, the mtime comparison stays
            }
            return fp;
        }

        public static TagsFingerprint Compute(string root, TagSet exclude)
        {
            string tags = Path.Combine(root, "tags");
            var lines = new List<string>();
            var stack = new Stack<DirectoryInfo>();
            var top = new DirectoryInfo(tags);
            if (top.Exists) stack.Push(top);
            int baseLen = tags.TrimEnd('\\').Length + 1;
            string exDir = exclude == null ? null : SafeFs.Norm(exclude.TagDir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileSystemInfo[] items;
                try { items = d.GetFileSystemInfos(); } catch (Exception) { continue; }
                bool inExDir = exDir != null && string.Equals(SafeFs.Norm(d.FullName), exDir, StringComparison.OrdinalIgnoreCase);
                foreach (var fi in items)
                {
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (fi is DirectoryInfo sub)
                    {
                        if (ReferenceEquals(d, top) && string.Equals(sub.Name, ToolScratchDir, StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(sub);
                        continue;
                    }
                    string name = fi.Name;
                    if (name.IndexOf("(auto-backup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (inExDir && name.StartsWith(exclude.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var f = (FileInfo)fi;
                    lines.Add(f.FullName.Substring(baseLen) + "\t" + f.Length.ToString(CultureInfo.InvariantCulture) + "\t" + f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                }
            }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return FromLines(lines).WithRoot(root);
        }

        private TagsFingerprint WithRoot(string root) { Root = root; return this; }

        public static TagsFingerprint FromLinesPublic(List<string> lines) { return FromLines(lines); }

        private static TagsFingerprint FromLines(List<string> lines)
        {
            var fp = new TagsFingerprint { Lines = lines };
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(Stat3(l)).Append('\n');     // 0.6.6: the optional content-hash column is not part of the whole-fingerprint md5
            fp.Md5 = SafeFs.Md5Hex(Encoding.UTF8.GetBytes(sb.ToString()));
            return fp;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            foreach (var l in Lines) sb.Append(l).Append('\n');
            SafeFs.WriteAllTextAtomic(path, sb.ToString());
        }

        public static TagsFingerprint Load(string path)
        {
            if (!File.Exists(path)) return null;
            var lines = Json.ReadAllTextShared(path).Split('\n').Where(l => l.Length > 0 && !IsToolScratch(l)).ToList();
            return FromLines(lines);
        }

        /// <summary>Changed / added / removed tag paths (relative to tags\), at most <paramref name="max"/>.</summary>
        public static List<string> Diff(TagsFingerprint old, TagsFingerprint now, int max, out int total)
        {
            Func<string, string> key = l => { int i = l.IndexOf('\t'); return i < 0 ? l : l.Substring(0, i); };
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in old.Lines) a[key(l)] = Stat3(l);
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in now.Lines) b[key(l)] = Stat3(l);
            var r = new List<string>();
            foreach (var kv in b)
            {
                string o;
                if (!a.TryGetValue(kv.Key, out o)) r.Add("added    " + kv.Key);
                else if (o != kv.Value) r.Add("changed  " + kv.Key);
            }
            foreach (var kv in a) if (!b.ContainsKey(kv.Key)) r.Add("removed  " + kv.Key);
            r.Sort(StringComparer.OrdinalIgnoreCase);
            total = r.Count;
            return r.Take(max).ToList();
        }

        /// <summary>
        /// 0.6.6: the changes since a record, split into bake-relevant (these ask) and information only, with identical re-saves dropped.
        /// A file whose stat line moved with an EQUAL size is hashed now (lazily, only those) and compared with the recorded hash when the record has one.
        /// <paramref name="ctx"/> null = every change counts as relevant.
        /// </summary>
        public static TagDiff Classify(TagsFingerprint old, TagsFingerprint now, TagRelevanceCtx ctx, int max)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in old.Lines) a[KeyOf(l)] = l;
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in now.Lines) b[KeyOf(l)] = l;
            var d = new TagDiff();
            var rel = new List<string>(); var inf = new List<string>();
            Action<string, string> add = (k, text) => { if (ctx == null || ctx.IsRelevant(k)) rel.Add(text); else inf.Add(text); };
            foreach (var kv in b)
            {
                string o;
                if (!a.TryGetValue(kv.Key, out o)) { add(kv.Key, "added    " + kv.Key); continue; }
                if (Stat3(o) == Stat3(kv.Value)) continue;
                string h = HashOf(o);
                if (h != null && now.Root != null && SizeOf(o) == SizeOf(kv.Value))
                {
                    try { if (string.Equals(h, SafeFs.Md5File(Path.Combine(now.Root, "tags", kv.Key)), StringComparison.OrdinalIgnoreCase)) { d.Identical++; continue; } }
                    catch (Exception) { }     // unreadable: it counts as changed
                }
                add(kv.Key, "changed  " + kv.Key);
            }
            foreach (var kv in a) if (!b.ContainsKey(kv.Key)) add(kv.Key, "removed  " + kv.Key);
            rel.Sort(StringComparer.OrdinalIgnoreCase); inf.Sort(StringComparer.OrdinalIgnoreCase);
            d.RelevantTotal = rel.Count; d.InfoTotal = inf.Count;
            d.Relevant = rel.Take(max).ToList(); d.Info = inf.Take(max).ToList();
            return d;
        }
    }
}
