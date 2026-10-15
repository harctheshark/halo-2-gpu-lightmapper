using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>
    /// NTFS directory junctions (mount-point reparse points): create with CreateDirectory + FSCTL_SET_REPARSE_POINT
    /// (no admin, no SeCreateSymbolicLinkPrivilege), read the target, and remove with RemoveDirectory ONLY -- which
    /// deletes the link itself and never touches anything in the target. There is no recursive delete anywhere here.
    /// </summary>
    public static class Junction
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_READ_ATTRIBUTES = 0x80;
        private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const int MAX_REPARSE = 16 * 1024;
        private const string NtPrefix = @"\??\";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr sa);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        /// <summary>Full path with no trailing backslash (except a bare drive root "X:\"). Refuses UNC and \\?\ forms.</summary>
        public static string NormalizeDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("empty path");
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("UNC / device paths are not supported: " + path);
            string f = Path.GetFullPath(path);
            if (f.Length < 3 || f[1] != ':' || f[2] != '\\') throw new ArgumentException("not a drive-absolute path: " + path);
            while (f.Length > 3 && f.EndsWith(@"\", StringComparison.Ordinal)) f = f.Substring(0, f.Length - 1);
            return f;
        }

        public static bool SamePathText(string a, string b) =>
            string.Equals(NormalizeDir(a), NormalizeDir(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the path exists and is a directory carrying a mount-point (junction) reparse tag.</summary>
        public static bool IsJunction(string path) => TryGetTarget(path, out _);

        /// <summary>Exists at all (file, directory, junction, even a dangling one).</summary>
        public static bool PathExists(string path) => GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;

        /// <summary>Creates <paramref name="linkDir"/> as a junction to the existing directory <paramref name="targetDir"/>.
        /// Refuses if anything already exists at <paramref name="linkDir"/>; the parent must exist. On failure the empty
        /// directory it created is removed (RemoveDirectory) and the error is thrown.</summary>
        public static void Create(string linkDir, string targetDir)
        {
            string link = NormalizeDir(linkDir), target = NormalizeDir(targetDir);
            if (link.Length <= 3) throw new JunctionException("refusing to turn a drive root into a junction: " + link);
            if (!Directory.Exists(target)) throw new JunctionException("junction target does not exist or is not a directory: " + target);
            if (PathExists(link)) throw new JunctionException("something already exists at " + link);
            if (link.StartsWith(target + @"\", StringComparison.OrdinalIgnoreCase)) throw new JunctionException("a junction inside its own target would be a cycle: " + link);
            if (!CreateDirectoryW(link, IntPtr.Zero)) throw new JunctionException("CreateDirectory(" + link + ") failed", new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                byte[] subst = Encoding.Unicode.GetBytes(NtPrefix + target);
                byte[] print = Encoding.Unicode.GetBytes(target);
                int pathBuf = subst.Length + 2 + print.Length + 2;
                int dataLen = 8 + pathBuf;
                if (8 + dataLen > MAX_REPARSE) throw new JunctionException("junction target path too long: " + target);
                var buf = new byte[8 + dataLen];
                BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buf, 0);
                BitConverter.GetBytes((ushort)dataLen).CopyTo(buf, 4);
                // +6 Reserved = 0
                BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                       // SubstituteNameOffset
                BitConverter.GetBytes((ushort)subst.Length).CopyTo(buf, 10);           // SubstituteNameLength
                BitConverter.GetBytes((ushort)(subst.Length + 2)).CopyTo(buf, 12);     // PrintNameOffset
                BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);           // PrintNameLength
                Buffer.BlockCopy(subst, 0, buf, 16, subst.Length);
                Buffer.BlockCopy(print, 0, buf, 16 + subst.Length + 2, print.Length);
                using (var h = CreateFileW(link, GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                                           FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
                {
                    if (h.IsInvalid) throw new JunctionException("cannot open " + link + " to set the reparse point", new Win32Exception(Marshal.GetLastWin32Error()));
                    if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, buf.Length, null, 0, out _, IntPtr.Zero))
                        throw new JunctionException("FSCTL_SET_REPARSE_POINT on " + link + " failed (is the volume NTFS?)", new Win32Exception(Marshal.GetLastWin32Error()));
                }
                if (!TryGetTarget(link, out var got) || !string.Equals(got, target, StringComparison.OrdinalIgnoreCase))
                    throw new JunctionException("junction " + link + " reads back as '" + got + "', expected '" + target + "'");
            }
            catch
            {
                // Undo: RemoveDirectory only. If the reparse point was set this removes the link, never the target's content.
                RemoveDirectoryW(link);
                throw;
            }
        }

        /// <summary>Reads a junction's target (normalized, no \??\ prefix). False for anything that is not a directory
        /// junction to a drive path (plain dirs, files, symlinks, volume mount points, missing paths).</summary>
        public static bool TryGetTarget(string linkDir, out string target)
        {
            target = null;
            uint a = GetFileAttributesW(linkDir);
            if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_DIRECTORY) == 0 || (a & FILE_ATTRIBUTE_REPARSE_POINT) == 0) return false;
            using (var h = CreateFileW(linkDir, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                                       FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                var buf = new byte[MAX_REPARSE];
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, null, 0, buf, buf.Length, out int got, IntPtr.Zero) || got < 16) return false;
                if (BitConverter.ToUInt32(buf, 0) != IO_REPARSE_TAG_MOUNT_POINT) return false;
                int so = BitConverter.ToUInt16(buf, 8), sl = BitConverter.ToUInt16(buf, 10);
                if (16 + so + sl > got) return false;
                string s = Encoding.Unicode.GetString(buf, 16 + so, sl);
                if (!s.StartsWith(NtPrefix, StringComparison.Ordinal)) return false;
                s = s.Substring(NtPrefix.Length);
                if (s.Length < 3 || s[1] != ':' || s[2] != '\\') return false;   // \??\Volume{guid}\ = a volume mount point, not ours
                while (s.Length > 3 && s.EndsWith(@"\", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
                target = s;
                return true;
            }
        }

        /// <summary>
        /// Removes a junction with RemoveDirectory ONLY (the link goes; the target and everything in it stay). Refuses --
        /// changing nothing -- when the path is not a directory junction, or when <paramref name="expectedTarget"/> is given
        /// and the junction points elsewhere. Never recursive, never follows the link.
        /// </summary>
        public static void Remove(string linkDir, string expectedTarget = null)
        {
            string link = NormalizeDir(linkDir);
            if (!PathExists(link)) throw new JunctionException("nothing at " + link);
            if (!TryGetTarget(link, out var t)) throw new JunctionException("refusing: " + link + " is not a directory junction (plain directories are never removed here)");
            if (expectedTarget != null && !SamePathText(t, expectedTarget))
                throw new JunctionException("refusing: " + link + " points at '" + t + "', not '" + NormalizeDir(expectedTarget) + "'");
            if (!RemoveDirectoryW(link)) throw new JunctionException("RemoveDirectory(" + link + ") failed", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        /// <summary>True when both paths open (following reparse points) to the same directory object: same volume serial
        /// and same file index. Read-only; writes nothing.</summary>
        public static bool SameDirectory(string a, string b)
        {
            if (!TryFileId(a, out var va, out var ia) || !TryFileId(b, out var vb, out var ib)) return false;
            return va == vb && ia == ib;
        }

        private static bool TryFileId(string path, out uint volume, out ulong index)
        {
            volume = 0; index = 0;
            using (var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return false;
                if ((info.Attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) return false;
                volume = info.VolumeSerialNumber;
                index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return true;
            }
        }
    }
}
