// S2.1. Based on the S0.1 spike's extractor (src\spike_s01_wpf\Payload\PayloadExtractor.cs: streaming md5-checked
// extraction into <dir>.tmp_<8hex>, ".complete" marker, atomic rename, size+mtime quick check, full md5 verify, named
// mutex, MAX_PATH refusal before any write, no recursive delete). Extended for the shell:
//   * any IPayloadSource (embedded resources or a folder);
//   * ExtractOptions.BeforeSeal: a hook that runs on the finished tmp dir BEFORE the marker + rename (the INST DLL slot
//     patch), and MarkerExtra fields recorded in ".complete";
//   * IFileVerifier: files a caller verifies its own way (slot-patched DLLs are checked by re-pristining, not by md5);
//   * RepairFiles: re-extracts single files from the zip (antivirus quarantine, tamper) with an atomic per-file replace.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using H2GpuLightmapper.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace H2GpuLightmapper.Core.Payload
{
    public sealed class ExtractProgress
    {
        public string Component;
        public int FilesDone, FilesTotal;
        public long BytesDone, BytesTotal;
    }

    public sealed class ExtractOptions
    {
        public IProgress<ExtractProgress> Progress;
        public CancellationToken Cancel;
        /// <summary>Runs on the complete, md5-verified tmp dir before the ".complete" marker is written and the dir renamed.
        /// Whatever it changes must be stamped with <see cref="PayloadExtractor.StampUtc"/> to keep the quick check valid.</summary>
        public Action<string> BeforeSeal;
        /// <summary>Extra fields for the ".complete" marker (evaluated after BeforeSeal).</summary>
        public Func<JObj> MarkerExtra;
        public TimeSpan MutexWait = TimeSpan.FromMinutes(10);
    }

    public sealed class ExtractResult
    {
        public string Component;
        public string FinalDir;
        public bool AlreadyPresent;     // a complete, matching dir already existed (no work done)
        public int Files;
        public long Bytes;
        public double Seconds;
        public string ResourceStreamType;
    }

    public sealed class VerifyResult
    {
        public bool Ok;
        public int FilesChecked;
        public long BytesChecked;
        public double Seconds;
        public List<string> Problems = new List<string>();
        /// <summary>Relative ('/') paths of the files that failed (input for <see cref="PayloadExtractor.RepairFiles"/>).</summary>
        public List<string> BadFiles = new List<string>();
        /// <summary>True when the ".complete" marker is missing or names another zip (repair cannot fix that: re-extract).</summary>
        public bool MarkerBad;
        public void Bad(string relPath, string problem) { Problems.Add(problem); if (!BadFiles.Contains(relPath)) BadFiles.Add(relPath); }
    }

    public sealed class RepairResult
    {
        public List<string> Repaired = new List<string>();
        public List<string> Failed = new List<string>();
        public bool Ok { get { return Failed.Count == 0; } }
    }

    /// <summary>A file a caller verifies itself instead of by md5 (e.g. a slot-patched DLL).</summary>
    public interface IFileVerifier
    {
        bool Handles(PayloadFile f);
        /// <summary>null when the file is good, else the problem text.</summary>
        string Check(PayloadFile f, string fullPath);
    }

    public static class PayloadExtractor
    {
        public const string CompleteMarker = ".complete";
        public static readonly DateTime StampUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int Buf = 1 << 20;
        /// <summary>".tmp_" + 8 hex.</summary>
        public const int TmpSuffixLen = 13;
        /// <summary>Win32 MAX_PATH without the NUL. net48 + LongPathsEnabled=0 (the Windows default) cannot go past it.</summary>
        public const int MaxPath = 259;

        /// <summary>Longest full path extraction will create under parentDir (tmp dir included). Must be &lt;= MaxPath.</summary>
        public static int LongestPath(string parentDir, PayloadComponent c)
        {
            return Path.GetFullPath(parentDir).TrimEnd('\\').Length + 1 + c.DirName.Length + TmpSuffixLen + 1 + c.MaxPathLen;
        }

        public static string MutexName(PayloadComponent c) { return @"Local\H2GpuLightmapper.extract." + c.DirName; }

        public static ExtractResult EnsureExtracted(IPayloadSource src, PayloadComponent c, string parentDir, ExtractOptions o = null)
        {
            o = o ?? new ExtractOptions();
            var sw = Stopwatch.StartNew();
            string final = Path.Combine(parentDir, c.DirName);
            var res = new ExtractResult { Component = c.Name, FinalDir = final };
            int longest = LongestPath(parentDir, c);
            if (longest > MaxPath)
                throw new PathTooLongException(string.Format("{0}: extraction would need {1}-char paths (limit {2}) under {3}",
                    c.Name, longest, MaxPath, parentDir));
            Directory.CreateDirectory(parentDir);

            using (var mtx = new Mutex(false, MutexName(c)))
            {
                bool owned = false;
                try
                {
                    try { owned = mtx.WaitOne(o.MutexWait); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("another instance is extracting " + c.DirName);

                    if (IsMarkedComplete(final, c)) { res.AlreadyPresent = true; res.Seconds = sw.Elapsed.TotalSeconds; return res; }
                    if (Directory.Exists(final))
                        throw new IOException("incomplete or foreign dir in the way (not deleted, by rule): " + final);

                    string tmp = final + ".tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    Directory.CreateDirectory(tmp);
                    string tmpFull = Path.GetFullPath(tmp).TrimEnd('\\') + "\\";

                    var want = new Dictionary<string, PayloadFile>(StringComparer.Ordinal);
                    foreach (var f in c.Files) want[f.Path] = f;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var buf = new byte[Buf];
                    var p = new ExtractProgress { Component = c.Name, FilesTotal = c.FileCount, BytesTotal = c.TotalBytes };
                    long lastReport = 0;

                    using (var rs = src.OpenResource(c.Resource))
                    {
                        if (rs == null) throw new InvalidDataException("missing payload resource " + c.Resource + " in " + src.Describe);
                        res.ResourceStreamType = rs.GetType().FullName;
                        if (rs.Length != c.ZipSize) throw new InvalidDataException(c.Resource + ": size " + rs.Length + " != manifest " + c.ZipSize);
                        using (var zip = new ZipArchive(rs, ZipArchiveMode.Read, false))
                        using (var md5 = SafeFs.NewMd5())
                        {
                            foreach (var e in zip.Entries)
                            {
                                o.Cancel.ThrowIfCancellationRequested();
                                if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue; // dir entry
                                PayloadFile mf;
                                if (!want.TryGetValue(e.FullName, out mf)) throw new InvalidDataException("zip entry not in manifest: " + e.FullName);
                                if (!seen.Add(e.FullName)) throw new InvalidDataException("duplicate zip entry: " + e.FullName);
                                string dst = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', '\\')));
                                if (!dst.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("zip-slip entry: " + e.FullName);
                                Directory.CreateDirectory(Path.GetDirectoryName(dst));

                                md5.Initialize();
                                long n = 0;
                                using (var zs = e.Open())
                                using (var fs = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                                {
                                    if (mf.Size > 0) fs.SetLength(mf.Size);
                                    int r;
                                    while ((r = zs.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        md5.TransformBlock(buf, 0, r, null, 0);
                                        fs.Write(buf, 0, r);
                                        n += r;
                                        p.BytesDone += r;
                                        if (o.Progress != null && p.BytesDone - lastReport >= (8 << 20)) { lastReport = p.BytesDone; o.Progress.Report(p); }
                                    }
                                    if (n != mf.Size) throw new InvalidDataException(e.FullName + ": size " + n + " != manifest " + mf.Size);
                                    fs.Flush();
                                    StampMtime(fs.SafeFileHandle);
                                }
                                md5.TransformFinalBlock(buf, 0, 0);
                                string h = SafeFs.Hex(md5.Hash);
                                if (!string.Equals(h, mf.Md5, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException(e.FullName + ": md5 " + h + " != manifest " + mf.Md5);
                                p.FilesDone++;
                                res.Bytes += n;
                            }
                        }
                    }
                    if (seen.Count != c.Files.Count) throw new InvalidDataException(c.Name + ": " + seen.Count + " files extracted, manifest has " + c.Files.Count);
                    if (o.BeforeSeal != null) o.BeforeSeal(tmp);
                    WriteMarker(tmp, c, o.MarkerExtra == null ? null : o.MarkerExtra());
                    Directory.Move(tmp, final); // atomic rename on the same volume
                    res.Files = seen.Count;
                    if (o.Progress != null) o.Progress.Report(p);
                    res.Seconds = sw.Elapsed.TotalSeconds;
                    return res;
                }
                finally { if (owned) mtx.ReleaseMutex(); }
            }
        }

        public static void WriteMarker(string dir, PayloadComponent c, JObj extra)
        {
            var m = new JObj().Add("name", c.Name).Add("zipMd5", c.ZipMd5).Add("fileCount", c.FileCount)
                              .Add("totalBytes", c.TotalBytes).Add("extractedUtc", DateTime.UtcNow);
            if (extra != null) foreach (var kv in extra) m[kv.Key] = kv.Value;
            SafeFs.WriteAllTextAtomic(Path.Combine(dir, CompleteMarker), Json.Write(m, true));
        }

        public static JObj ReadMarker(string dir)
        {
            string p = Path.Combine(dir, CompleteMarker);
            if (!File.Exists(p)) return null;
            try { return Json.ParseObj(Json.ReadAllTextShared(p)); } catch (FormatException) { return null; }
        }

        public static bool IsMarkedComplete(string dir, PayloadComponent c)
        {
            var m = ReadMarker(dir);
            return m != null && string.Equals(m.Str("zipMd5"), c.ZipMd5, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Start-up check: marker matches, every manifest file exists with its size and the extraction mtime stamp.</summary>
        public static VerifyResult QuickCheck(string dir, PayloadComponent c)
        {
            var sw = Stopwatch.StartNew();
            var v = new VerifyResult();
            if (!IsMarkedComplete(dir, c)) { v.MarkerBad = true; v.Problems.Add("no matching .complete marker in " + dir); }
            foreach (var f in c.Files)
            {
                var fi = new FileInfo(Path.Combine(dir, f.Path.Replace('/', '\\')));
                v.FilesChecked++;
                if (!fi.Exists) { v.Bad(f.Path, "missing " + f.Path); continue; }
                if (fi.Length != f.Size) v.Bad(f.Path, "size " + f.Path);
                else if (fi.LastWriteTimeUtc != StampUtc) v.Bad(f.Path, "mtime " + f.Path);
                v.BytesChecked += fi.Length;
            }
            v.Ok = v.Problems.Count == 0;
            v.Seconds = sw.Elapsed.TotalSeconds;
            return v;
        }

        /// <summary>Pre-bake check: full md5 of every (included) file against the manifest; files the verifier handles are
        /// checked its way instead.</summary>
        public static VerifyResult FullVerify(string dir, PayloadComponent c, CancellationToken ct = default(CancellationToken),
            IFileVerifier custom = null, Func<PayloadFile, bool> include = null)
        {
            var sw = Stopwatch.StartNew();
            var v = new VerifyResult();
            if (!IsMarkedComplete(dir, c)) { v.MarkerBad = true; v.Problems.Add("no matching .complete marker in " + dir); }
            var buf = new byte[Buf];
            using (var md5 = SafeFs.NewMd5())
            {
                foreach (var f in c.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (include != null && !include(f)) continue;
                    string path = Path.Combine(dir, f.Path.Replace('/', '\\'));
                    v.FilesChecked++;
                    if (!File.Exists(path)) { v.Bad(f.Path, "missing " + f.Path); continue; }
                    if (custom != null && custom.Handles(f))
                    {
                        string why;
                        try { why = custom.Check(f, path); } catch (Exception e) { why = e.Message; }
                        if (why != null) v.Bad(f.Path, f.Path + ": " + why);
                        v.BytesChecked += f.Size;
                        continue;
                    }
                    md5.Initialize();
                    try
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                        {
                            int r;
                            while ((r = fs.Read(buf, 0, buf.Length)) > 0) { md5.TransformBlock(buf, 0, r, null, 0); v.BytesChecked += r; }
                        }
                    }
                    catch (IOException e) { v.Bad(f.Path, "unreadable " + f.Path + ": " + e.Message); continue; }
                    catch (UnauthorizedAccessException e) { v.Bad(f.Path, "unreadable " + f.Path + ": " + e.Message); continue; }
                    md5.TransformFinalBlock(buf, 0, 0);
                    if (!string.Equals(SafeFs.Hex(md5.Hash), f.Md5, StringComparison.OrdinalIgnoreCase)) v.Bad(f.Path, "md5 " + f.Path);
                }
            }
            v.Ok = v.Problems.Count == 0;
            v.Seconds = sw.Elapsed.TotalSeconds;
            return v;
        }

        /// <summary>
        /// Re-extracts the given files of a component (already extracted and marked complete) from the zip: each one is
        /// written to "&lt;file&gt;.repair_&lt;8hex&gt;" in its own folder, md5-checked against the manifest, optionally transformed
        /// (the slot patch), flushed, mtime-stamped, then atomically moved over the bad file. Serialised with extraction by
        /// the component's named mutex.
        /// </summary>
        public static RepairResult RepairFiles(IPayloadSource src, PayloadComponent c, string dir, IEnumerable<string> relPaths,
            Func<PayloadFile, byte[], byte[]> transform = null, CancellationToken ct = default(CancellationToken))
        {
            var res = new RepairResult();
            var todo = new HashSet<string>(relPaths, StringComparer.OrdinalIgnoreCase);
            if (todo.Count == 0) return res;
            using (var mtx = new Mutex(false, MutexName(c)))
            {
                bool owned = false;
                try
                {
                    try { owned = mtx.WaitOne(TimeSpan.FromMinutes(10)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("another instance is extracting " + c.DirName);
                    string dirFull = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                    using (var rs = src.OpenResource(c.Resource))
                    {
                        if (rs == null) throw new InvalidDataException("missing payload resource " + c.Resource);
                        using (var zip = new ZipArchive(rs, ZipArchiveMode.Read, false))
                        {
                            foreach (var rel in todo)
                            {
                                ct.ThrowIfCancellationRequested();
                                var mf = c.Find(rel);
                                try
                                {
                                    if (mf == null) throw new InvalidDataException("not in the manifest");
                                    var e = zip.GetEntry(mf.Path);
                                    if (e == null) throw new InvalidDataException("not in the zip");
                                    byte[] data;
                                    using (var zs = e.Open())
                                    using (var ms = new MemoryStream((int)Math.Max(0, Math.Min(int.MaxValue, mf.Size))))
                                    {
                                        zs.CopyTo(ms);
                                        data = ms.ToArray();
                                    }
                                    if (data.LongLength != mf.Size || !string.Equals(SafeFs.Md5Hex(data), mf.Md5, StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidDataException("the zip copy does not match the manifest");
                                    if (transform != null) data = transform(mf, data);
                                    string dst = Path.GetFullPath(Path.Combine(dir, mf.Path.Replace('/', '\\')));
                                    if (!dst.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("path escapes the component dir");
                                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                    string tmp = dst + ".repair_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                                    using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                    {
                                        fs.Write(data, 0, data.Length);
                                        fs.Flush(true);
                                        StampMtime(fs.SafeFileHandle);
                                    }
                                    try
                                    {
                                        if (File.Exists(dst) && (File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0)
                                            File.SetAttributes(dst, File.GetAttributes(dst) & ~FileAttributes.ReadOnly);
                                        SafeFs.MoveReplace(tmp, dst);
                                    }
                                    catch { SafeFs.TryDeleteFile(tmp); throw; }
                                    res.Repaired.Add(mf.Path);
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException))
                                {
                                    res.Failed.Add(rel + ": " + ex.Message);
                                }
                            }
                        }
                    }
                }
                finally { if (owned) mtx.ReleaseMutex(); }
            }
            return res;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileTime(SafeFileHandle h, IntPtr creation, IntPtr lastAccess, ref long lastWrite);

        public static void StampMtime(SafeFileHandle h)
        {
            long ft = StampUtc.ToFileTimeUtc();
            if (!SetFileTime(h, IntPtr.Zero, IntPtr.Zero, ref ft))
                throw new IOException("SetFileTime failed: " + Marshal.GetLastWin32Error());
        }

        public static void StampMtime(string path)
        {
            File.SetLastWriteTimeUtc(path, StampUtc);
        }
    }
}
