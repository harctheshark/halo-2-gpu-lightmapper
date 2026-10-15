using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace H2GpuLightmapper.Core.Util
{
    /// <summary>
    /// File-system helpers with the project's safety rules built in:
    ///  * NO recursive delete exists in this code base (a unit test scans every source file for one). The only tree removal
    ///    is <see cref="RemoveTreeNoReparse"/>, which refuses any reparse point and is restricted by its callers to two
    ///    folders the shell owns (rt\ and WORK\_runtime\).
    ///  * replacing a file is atomic on one volume (MoveFileEx REPLACE_EXISTING | WRITE_THROUGH) after the new bytes were
    ///    flushed and md5-verified.
    /// </summary>
    public static class SafeFs
    {
        /// <summary>MD5 (S0.1: .NET Framework's MD5 throws under the Windows FIPS policy; say so clearly).</summary>
        public static MD5 NewMd5()
        {
            try { return MD5.Create(); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("MD5 is unavailable (the Windows FIPS policy is on): the payload and tag backups cannot be verified, so the app cannot run", ex);
            }
        }

        public static string Md5Hex(byte[] b)
        {
            using (var m = NewMd5()) return Hex(m.ComputeHash(b));
        }

        public static string Md5File(string path)
        {
            using (var m = NewMd5())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
                return Hex(m.ComputeHash(fs));
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Full path without a trailing separator (drive roots keep theirs).</summary>
        public static string Norm(string path)
        {
            string p = Path.GetFullPath(path);
            if (p.Length > 3) p = p.TrimEnd('\\', '/');
            return p;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> itself or lies inside it (textual, case-insensitive).</summary>
        public static bool IsUnderOrSame(string path, string dir)
        {
            string p = Norm(path), d = Norm(dir);
            if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
            if (!d.EndsWith("\\", StringComparison.Ordinal)) d += "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Writes text (UTF-8, no BOM) via a temp file in the same folder, flushed, then atomically replaces the target.</summary>
        public static void WriteAllTextAtomic(string path, string text)
        {
            WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(text));
        }

        public static void WriteAllBytesAtomic(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            try { MoveReplace(tmp, path); }
            catch { TryDeleteFile(tmp); throw; }
        }

        /// <summary>MoveFileEx(src, dst, REPLACE_EXISTING | WRITE_THROUGH): atomic on one NTFS volume.</summary>
        public static void MoveReplace(string src, string dst)
        {
            if (!MoveFileEx(src, dst, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                throw new IOException("MoveFileEx " + src + " -> " + dst + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        /// <summary>Renames without replacing (fails if <paramref name="dst"/> exists).</summary>
        public static void MoveNoReplace(string src, string dst)
        {
            if (!MoveFileEx(src, dst, MOVEFILE_WRITE_THROUGH))
                throw new IOException("MoveFileEx " + src + " -> " + dst + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        /// <summary>Deletes ONE file if present (never a directory). Returns false on failure instead of throwing.</summary>
        public static bool TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; } catch { return false; }
        }

        /// <summary>
        /// Copies <paramref name="src"/> to a NEW file <paramref name="dst"/> (refuses to overwrite), hashing the source as it
        /// is read, flushes to disk, then re-reads the copy and proves its md5. Returns the md5. The source is opened with
        /// FileShare.ReadWrite, so a tag an editor holds open can still be backed up.
        /// </summary>
        public static string CopyVerified(string src, string dst)
        {
            string srcMd5;
            using (var m = NewMd5())
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = i.Read(buf, 0, buf.Length)) > 0) { m.TransformBlock(buf, 0, n, null, 0); o.Write(buf, 0, n); }
                m.TransformFinalBlock(buf, 0, 0);
                srcMd5 = Hex(m.Hash);
                o.Flush(true);
            }
            try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); } catch { }
            string got = Md5File(dst);
            if (!string.Equals(got, srcMd5, StringComparison.OrdinalIgnoreCase))
                throw new IOException("copy does not verify: " + dst + " md5 " + got + " != source " + srcMd5);
            return srcMd5;
        }

        /// <summary>
        /// Installs <paramref name="src"/> over <paramref name="target"/> atomically: copy to target + ".gpulm_new" in the same
        /// folder, flush, md5-verify against <paramref name="expectedMd5"/>, MoveFileEx(REPLACE_EXISTING), re-read and verify.
        /// </summary>
        public static void ReplaceVerified(string src, string target, string expectedMd5)
        {
            string tmp = target + ".gpulm_new";
            if (File.Exists(tmp)) MoveNoReplace(tmp, tmp + ".stale_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4));
            string got = CopyVerified(src, tmp);
            if (!string.Equals(got, expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new IOException(src + " md5 " + got + " is not the expected " + expectedMd5 + " (nothing replaced)");
            MoveReplace(tmp, target);
            string after = Md5File(target);
            if (!string.Equals(after, expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new IOException(target + " does not verify after the replace (md5 " + after + ")");
        }

        /// <summary>True when nobody else holds the file open (opened GENERIC_READ|GENERIC_WRITE with share mode 0), exactly
        /// lmw_install.py's exclusive_ok. A missing file counts as free.</summary>
        public static bool ExclusiveOk(string path, out string why)
        {
            why = null;
            if (!File.Exists(path)) return true;
            using (var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
            {
                if (!h.IsInvalid) return true;
                int e = Marshal.GetLastWin32Error();
                why = "Win32 error " + e + (e == 32 ? " (sharing violation: open in Sapien / Guerilla / the tool?)" : "");
                return false;
            }
        }

        /// <summary>True when the path itself is a reparse point (junction, symlink, mount point...). Never follows it.</summary>
        public static bool IsReparsePoint(string path)
        {
            uint a = GetFileAttributesW(path);
            return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }

        /// <summary>
        /// Every reparse point at or below <paramref name="dir"/>, found WITHOUT following any (a reparse point is reported
        /// and not entered). Stops after <paramref name="max"/> hits.
        /// </summary>
        public static List<string> FindReparsePoints(string dir, int max = 20)
        {
            var hits = new List<string>();
            if (IsReparsePoint(dir)) { hits.Add(dir); return hits; }
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && hits.Count < max)
            {
                string d = stack.Pop();
                foreach (var e in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { hits.Add(e.FullName); if (hits.Count >= max) break; continue; }
                    if ((e.Attributes & FileAttributes.Directory) != 0) stack.Push(e.FullName);
                }
            }
            return hits;
        }

        /// <summary>
        /// Removes a folder tree that the CALLER has proven it owns, file by file and folder by folder with non-recursive
        /// calls only (File.Delete, Directory.Delete(path, false)). Refuses BEFORE deleting anything when any reparse point
        /// exists at or below <paramref name="dir"/> (a junction could lead into live tags), or when <paramref name="dir"/> is
        /// not a direct child of <paramref name="allowedParent"/>.
        /// </summary>
        public static int RemoveTreeNoReparse(string dir, string allowedParent)
        {
            string d = Norm(dir), parent = Norm(allowedParent);
            if (!string.Equals(Path.GetDirectoryName(d), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("refused: " + d + " is not a direct child of " + parent);
            var rp = FindReparsePoints(d, 1);
            if (rp.Count > 0) throw new InvalidOperationException("refused: " + d + " contains a reparse point (" + rp[0] + "); nothing was deleted");
            var dirs = new List<string>();
            var stack = new Stack<string>();
            stack.Push(d);
            int n = 0;
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                dirs.Add(cur);
                foreach (var e in new DirectoryInfo(cur).EnumerateFileSystemInfos())
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("refused: a reparse point appeared at " + e.FullName);
                    if ((e.Attributes & FileAttributes.Directory) != 0) { stack.Push(e.FullName); continue; }
                    if ((e.Attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(e.FullName, e.Attributes & ~FileAttributes.ReadOnly);
                    File.Delete(e.FullName);
                    n++;
                }
            }
            for (int i = dirs.Count - 1; i >= 0; i--) Directory.Delete(dirs[i], false);
            return n;
        }

        /// <summary>A unique, sortable leaf name "base", or "base_2", "base_3"... when one exists under <paramref name="parent"/>.</summary>
        public static string UniqueChild(string parent, string leaf)
        {
            string p = Path.Combine(parent, leaf);
            for (int i = 2; Directory.Exists(p) || File.Exists(p); i++) p = Path.Combine(parent, leaf + "_" + i);
            return p;
        }

        // ---- interop
        private const uint MOVEFILE_REPLACE_EXISTING = 0x1, MOVEFILE_WRITE_THROUGH = 0x8;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string src, string dst, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
    }
}
