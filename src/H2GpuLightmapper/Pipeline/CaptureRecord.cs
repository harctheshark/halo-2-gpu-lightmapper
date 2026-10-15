// S3.2 capture record and reuse gating (FINAL_DESIGN section 7).
//   After every successful capture the shell writes OUT\standalone_capture.json: quality, P, MC, sky_hi, fixes, the app
//   version, the binary md5s (estimator DLL pristine, fullgpu, gpu_bake, tool_fast, bounds DB), capture_id.json's soup crc,
//   and a stat fingerprint (path, size, mtime) of the whole tags tree minus this BSP's own lightmap outputs, kept beside it in
//   OUT\standalone_capture_tags.tsv.
//   Reuse is offered only when the record exists, its crc matches capture_id.json, quality / P / MC match, fixes and binaries
//   match, and (probes) sky_tex_hi.bin + lp_globals.bin are present. A tags-only difference keeps reuse enabled but needs an
//   explicit confirmation listing the changed tags -- never silent. A farm without a record (legacy / research) is never
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
            };
            var inst = m == null ? null : m.InstComponent;
            Func<string, string> md5Of = rel => inst == null ? null : inst.Find(rel) == null ? null : inst.Find(rel).Md5.ToLowerInvariant();
            id.Binaries["estimatorDll"] = md5Of(InstLayout.EstimatorDll);
            id.Binaries["fullgpu"] = md5Of(gpuBakeRel.Replace("gpu_bake.ps1", "v041-fullgpu/fullgpu.exe"));
            id.Binaries["gpuBake"] = md5Of(gpuBakeRel);
            id.Binaries["toolFast"] = toolMd5 ?? Md5OrNull(Path.Combine(root, "tool_fast.exe"));
            id.Binaries["boundsDb"] = Md5OrNull(Path.Combine(root, "h2v_scenery_bounds.db"));
            return id;
        }

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
        public JObj Record;
        public string RecordPath;
        public bool Unrecorded;
        public string Describe()
        {
            if (!Allowed) return "not reusable: " + Reason;
            var r = Record;
            string what = r == null ? "an unrecorded capture (dev)" : r.Str("quality") + " capture of " + (r.Str("written") ?? "?");
            return "reusable: " + what + (TagsChanged ? " -- " + ChangedTagsTotal + " tag(s) changed since (confirmation needed)" : "");
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
                .Add("appVersion", id.AppVersion).Add("guarded", id.Guarded.HasValue ? (object)id.Guarded.Value : null).Add("binaries", bin)
                .Add("captureId", new JObj().Add("soup_crc32", crc).Add("md5", cidMd5))
                .Add("tags", tags == null ? null : new JObj().Add("file", TagsFileName).Add("files", (long)tags.Count).Add("md5", tags.Md5));
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
                }
                if (i >= 0 && line != null) { if (fp.Lines[i] != line) { fp.Lines[i] = line; changed = true; } }
                else if (i >= 0) { fp.Lines.RemoveAt(i); changed = true; }
                else if (line != null) { fp.Lines.Add(line); changed = true; }
            }
            if (!changed) return false;
            fp.Lines.Sort(StringComparer.OrdinalIgnoreCase);
            fp = TagsFingerprint.FromLinesPublic(fp.Lines);
            fp.Save(tsv);
            j["tags"] = new JObj().Add("file", Path.GetFileName(tsv)).Add("files", (long)fp.Count).Add("md5", fp.Md5);
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
            var bin = j.Obj("binaries") ?? new JObj();
            var diff = new List<string>();
            foreach (var kv in want.Binaries)
                if (!SameText(bin.Str(kv.Key), kv.Value)) diff.Add(kv.Key);
            if (diff.Count > 0) return Refuse(v, "the engine changed since the capture (" + string.Join(", ", diff) + ")");
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
                        v.TagsChanged = true;
                        int total = 0;
                        v.ChangedTags = old == null ? new List<string> { "(the recorded fingerprint file is missing)" } : TagsFingerprint.Diff(old, now, 200, out total);
                        v.ChangedTagsTotal = old == null ? 1 : total;
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

        /// <summary>The top-level tags folder the tool uses as scratch (never a game tag); not fingerprinted.</summary>
        public const string ToolScratchDir = "temp_scenario";

        /// <summary>Is this fingerprint line / tags-relative path inside the tool's scratch folder?</summary>
        public static bool IsToolScratch(string relOrLine)
        {
            return relOrLine != null && relOrLine.StartsWith(ToolScratchDir + "\\", StringComparison.OrdinalIgnoreCase);
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
            return FromLines(lines);
        }

        public static TagsFingerprint FromLinesPublic(List<string> lines) { return FromLines(lines); }

        private static TagsFingerprint FromLines(List<string> lines)
        {
            var fp = new TagsFingerprint { Lines = lines };
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append('\n');
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
            foreach (var l in old.Lines) a[key(l)] = l;
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in now.Lines) b[key(l)] = l;
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
    }
}
