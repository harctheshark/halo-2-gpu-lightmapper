using System;
using System.Collections.Generic;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>One PE section header (raw = file offsets, Va = RVA).</summary>
    public sealed class PeSection
    {
        public string Name { get; internal set; }
        public uint VirtualSize { get; internal set; }
        public uint VirtualAddress { get; internal set; }
        public uint SizeOfRawData { get; internal set; }
        public uint PointerToRawData { get; internal set; }
        public uint Characteristics { get; internal set; }
        public bool IsWritable => (Characteristics & 0x80000000u) != 0;          // IMAGE_SCN_MEM_WRITE
        public bool IsInitializedData => (Characteristics & 0x00000040u) != 0;   // IMAGE_SCN_CNT_INITIALIZED_DATA
        public bool IsCode => (Characteristics & 0x00000020u) != 0;              // IMAGE_SCN_CNT_CODE
        public bool ContainsFileOffset(long off) => off >= PointerToRawData && off < (long)PointerToRawData + SizeOfRawData;
    }

    /// <summary>
    /// Minimal read-only PE parser: headers, sections, and the absolute-pointer relocation sites. Used to classify the
    /// path slots (.rdata const vs .data buffer) and to prove that no relocated pointer in the image points INTO a slot
    /// (only at its start) -- i.e. no code or data reads a fixed sub-string of the base path.
    /// </summary>
    public sealed class PeImage
    {
        private readonly byte[] _b;
        public ushort Machine { get; }
        public bool Is64 { get; }
        public ulong ImageBase { get; }
        public uint CheckSum { get; }
        public IReadOnlyList<PeSection> Sections { get; }
        public uint RelocRva { get; }
        public uint RelocSize { get; }

        public PeImage(byte[] bytes)
        {
            _b = bytes ?? throw new ArgumentNullException(nameof(bytes));
            if (_b.Length < 0x40 || _b[0] != (byte)'M' || _b[1] != (byte)'Z') throw new SlotPatchException("not a PE image (no MZ header)");
            int pe = I32(0x3C);
            if (pe <= 0 || pe > _b.Length - 24 || _b[pe] != (byte)'P' || _b[pe + 1] != (byte)'E' || _b[pe + 2] != 0 || _b[pe + 3] != 0)
                throw new SlotPatchException("not a PE image (bad PE signature)");
            Machine = U16(pe + 4);
            int nsec = U16(pe + 6);
            int optSize = U16(pe + 20);
            int opt = pe + 24;
            Need(opt, optSize);
            ushort magic = U16(opt);
            if (magic == 0x10B) Is64 = false;
            else if (magic == 0x20B) Is64 = true;
            else throw new SlotPatchException("not a PE image (optional header magic 0x" + magic.ToString("X") + ")");
            ImageBase = Is64 ? U64(opt + 24) : U32(opt + 28);
            CheckSum = U32(opt + 64);
            uint nRva = U32(opt + (Is64 ? 108 : 92));
            int dd = opt + (Is64 ? 112 : 96);
            if (nRva > 5 && dd + 6 * 8 <= opt + optSize) { RelocRva = U32(dd + 5 * 8); RelocSize = U32(dd + 5 * 8 + 4); }
            var secs = new List<PeSection>();
            int so = opt + optSize;
            Need(so, nsec * 40);
            for (int i = 0; i < nsec; i++)
            {
                int s = so + 40 * i;
                int n = 0; while (n < 8 && _b[s + n] != 0) n++;
                secs.Add(new PeSection
                {
                    Name = System.Text.Encoding.ASCII.GetString(_b, s, n),
                    VirtualSize = U32(s + 8),
                    VirtualAddress = U32(s + 12),
                    SizeOfRawData = U32(s + 16),
                    PointerToRawData = U32(s + 20),
                    Characteristics = U32(s + 36),
                });
            }
            Sections = secs;
        }

        public PeSection SectionForFileOffset(long off)
        {
            foreach (var s in Sections) if (s.ContainsFileOffset(off)) return s;
            return null;
        }

        /// <summary>File offset -> RVA, or -1 when the offset is not inside any section's raw data.</summary>
        public long FileOffsetToRva(long off)
        {
            var s = SectionForFileOffset(off);
            return s == null ? -1 : s.VirtualAddress + (off - s.PointerToRawData);
        }

        /// <summary>RVA -> file offset, or -1 when the RVA is not backed by raw data.</summary>
        public long RvaToFileOffset(ulong rva)
        {
            foreach (var s in Sections)
            {
                if (rva >= s.VirtualAddress && rva - s.VirtualAddress < s.SizeOfRawData)
                    return (long)(s.PointerToRawData + (rva - s.VirtualAddress));
            }
            return -1;
        }

        /// <summary>The RVA every absolute pointer in the image points at (one entry per HIGHLOW / DIR64 relocation).</summary>
        public List<ulong> RelocatedPointerTargets()
        {
            var t = new List<ulong>();
            foreach (var s in RelocatedPointerSites()) t.Add(s.TargetRva);
            return t;
        }

        /// <summary>One absolute pointer: where it is stored (file offset, 4 or 8 bytes) and the RVA it points at.</summary>
        public struct PointerSite
        {
            public long FileOffset;
            public int Width;
            public ulong TargetRva;
        }

        /// <summary>Every HIGHLOW / DIR64 relocation site backed by raw data, with the RVA its pointer targets.</summary>
        public List<PointerSite> RelocatedPointerSites()
        {
            var t = new List<PointerSite>();
            if (RelocRva == 0 || RelocSize == 0) return t;
            long p = RvaToFileOffset(RelocRva);
            if (p < 0) throw new SlotPatchException("relocation directory is not backed by raw data");
            long end = p + RelocSize;
            if (end > _b.Length) throw new SlotPatchException("relocation directory runs past the end of the file");
            while (p + 8 <= end)
            {
                uint page = U32((int)p), size = U32((int)p + 4);
                if (size < 8 || p + size > end) break;
                for (long e = p + 8; e + 2 <= p + size; e += 2)
                {
                    ushort ent = U16((int)e);
                    int type = ent >> 12, off = ent & 0xFFF;
                    if (type != 3 && type != 10) continue;   // HIGHLOW / DIR64; 0 = padding
                    long site = RvaToFileOffset(page + (uint)off);
                    if (site < 0) continue;
                    ulong va = type == 3 ? U32((int)site) : U64((int)site);
                    if (va >= ImageBase) t.Add(new PointerSite { FileOffset = site, Width = type == 3 ? 4 : 8, TargetRva = va - ImageBase });
                }
                p += size;
            }
            return t;
        }

        private void Need(int off, int len)
        {
            if (off < 0 || len < 0 || (long)off + len > _b.Length) throw new SlotPatchException("truncated PE header");
        }
        private ushort U16(int o) { Need(o, 2); return BitConverter.ToUInt16(_b, o); }
        private uint U32(int o) { Need(o, 4); return BitConverter.ToUInt32(_b, o); }
        private int I32(int o) { Need(o, 4); return BitConverter.ToInt32(_b, o); }
        private ulong U64(int o) { Need(o, 8); return BitConverter.ToUInt64(_b, o); }
    }
}
