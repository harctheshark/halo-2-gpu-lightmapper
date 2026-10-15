using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>Outcome of a successful <see cref="DllSlotPatcher.PatchDetailed"/>.</summary>
    public sealed class PatchResult
    {
        public byte[] Bytes { get; internal set; }
        public SlotLayout Layout { get; internal set; }
        public string BaseDir { get; internal set; }
        public int CodePage { get; internal set; }
        public string PristineMd5 { get; internal set; }
        public string PatchedMd5 { get; internal set; }
        /// <summary>Number of bytes that differ from the pristine input (all inside the slots, proven).</summary>
        public int ChangedBytes { get; internal set; }
        /// <summary>True when the base equals <see cref="DllSlotPatcher.PristineBase"/> (H2EKMine): output == input.</summary>
        public bool IsNoOp => ChangedBytes == 0;

        public string ToJson() =>
            "{\"layout\":" + Json.Str(Layout.Name) + ",\"layout_source\":" + Json.Str(Layout.Source.ToString().ToLowerInvariant()) +
            ",\"base\":" + Json.Str(BaseDir) + ",\"code_page\":" + CodePage.ToString(CultureInfo.InvariantCulture) +
            ",\"pristine_md5\":" + Json.Str(PristineMd5) + ",\"patched_md5\":" + Json.Str(PatchedMd5) +
            ",\"changed_bytes\":" + ChangedBytes.ToString(CultureInfo.InvariantCulture) + ",\"noop\":" + Json.Bool(IsNoOp) + "}";
    }

    /// <summary>What <see cref="DllSlotPatcher.Identify"/> found in a DLL (pristine or patched).</summary>
    public sealed class SlotIdentity
    {
        public SlotLayout Layout { get; internal set; }
        /// <summary>The base string both slots currently hold.</summary>
        public string Base { get; internal set; }
        public bool IsPristine { get; internal set; }
        public string Md5 { get; internal set; }
    }

    /// <summary>
    /// S0.4: re-points gpulm_estimator.dll's two hard-coded copies of
    /// <c>G:\SteamLibrary\steamapps\common\H2EKMine\GPU Lightmap Work\</c> (kBaseDir, the farm-worker base, and g_dirBuf,
    /// kDir's single-process default) at another directory, in place, NUL-padded, with no rebuild.
    /// <para>Safety argument (gpulm_estimator.cpp:58-60, 7437, 7449-7456): kBaseDir is only ever a "%s" argument and
    /// g_dirBuf/kDir is only ever a "%s" argument or an snprintf destination; nothing compares, indexes or measures them,
    /// and no relocated pointer in either image points INTO a slot (verified by <see cref="VerifyLayoutAgainstImage"/>).
    /// A shorter NUL-padded string is therefore exactly equivalent to a rebuild with that literal.</para>
    /// Every function here is pure (byte[] in, new byte[] out) except the explicit *File helpers.
    /// </summary>
    public static class DllSlotPatcher
    {
        /// <summary>The literal compiled into both production DLL builds (60 chars, ASCII).</summary>
        public const string PristineBase = @"G:\SteamLibrary\steamapps\common\H2EKMine\GPU Lightmap Work\";
        /// <summary>The largest base the 61-byte kBaseDir slot can hold (60 bytes + NUL), in ANSI BYTES.</summary>
        public const int MaxBaseBytes = 60;
        /// <summary>g_dirBuf's size in the source (char g_dirBuf[300]).</summary>
        public const int DirBufBytes = 300;

        private static readonly byte[] PristineBaseBytes = Encoding.ASCII.GetBytes(PristineBase);
        private const string ForbiddenChars = "<>\"|?*/";

        // ------------------------------------------------------------------ hashing

        public static string Md5Hex(byte[] bytes)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(bytes);
                var sb = new StringBuilder(32);
                foreach (var x in h) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ base validation

        /// <summary>
        /// Checks a slot base: drive-absolute (X:\...), ends with '\', no empty / "." / ".." segments, no segment ending in
        /// ' ' or '.', none of &lt;&gt;"|?*/ or control chars, ':' only after the drive letter, round-trips through
        /// <paramref name="codePage"/> with no best-fit, and is at most <see cref="MaxBaseBytes"/> BYTES in that code page.
        /// </summary>
        public static bool TryValidateBase(string baseDir, int codePage, out byte[] ansi, out string reason)
        {
            ansi = null; reason = null;
            if (string.IsNullOrEmpty(baseDir)) { reason = "empty"; return false; }
            if (baseDir.Length < 4 || !IsDriveLetter(baseDir[0]) || baseDir[1] != ':' || baseDir[2] != '\\')
            { reason = @"must be a drive-absolute directory like X:\dir\ (no UNC, no \\?\ prefix, no relative path)"; return false; }
            if (baseDir[baseDir.Length - 1] != '\\') { reason = "must end with a backslash"; return false; }
            for (int i = 0; i < baseDir.Length; i++)
            {
                char c = baseDir[i];
                if (c < 0x20) { reason = "contains control character U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture); return false; }
                if (ForbiddenChars.IndexOf(c) >= 0) { reason = "contains '" + c + "'"; return false; }
                if (c == ':' && i != 1) { reason = "':' is only allowed after the drive letter"; return false; }
            }
            foreach (var seg in baseDir.Substring(3, baseDir.Length - 4).Split('\\'))
            {
                if (seg.Length == 0) { reason = "empty path segment (doubled backslash)"; return false; }
                if (seg == "." || seg == "..") { reason = "relative segment '" + seg + "'"; return false; }
                if (seg.EndsWith(" ", StringComparison.Ordinal) || seg.EndsWith(".", StringComparison.Ordinal))
                { reason = "segment '" + seg + "' ends with a space or dot (Windows strips it, so the path would not round-trip)"; return false; }
            }
            if (!AnsiCodePage.TryEncode(baseDir, codePage, out var b))
            { reason = "not representable in the ANSI code page " + codePage.ToString(CultureInfo.InvariantCulture) + " (the DLL and tool_fast use the A APIs)"; return false; }
            if (Array.IndexOf(b, (byte)0) >= 0) { reason = "encodes to a NUL byte"; return false; }
            if (b.Length > MaxBaseBytes)
            {
                reason = string.Format(CultureInfo.InvariantCulture, "{0} bytes in code page {1}; the DLL slot holds at most {2}", b.Length, codePage, MaxBaseBytes);
                return false;
            }
            ansi = b;
            return true;
        }

        public static bool TryValidateBase(string baseDir, out string reason) => TryValidateBase(baseDir, AnsiCodePage.System, out _, out reason);

        /// <summary>Throws <see cref="SlotBaseException"/> with the reason; returns the ANSI bytes.</summary>
        public static byte[] ValidateBase(string baseDir, int codePage)
        {
            if (!TryValidateBase(baseDir, codePage, out var b, out var why)) throw new SlotBaseException(baseDir, why);
            return b;
        }

        private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        // ------------------------------------------------------------------ layouts

        /// <summary>The known layout for this pristine DLL (by md5), else a <see cref="DiscoverLayout"/> result.</summary>
        public static SlotLayout ResolveLayout(byte[] pristineDll)
        {
            if (pristineDll == null) throw new ArgumentNullException(nameof(pristineDll));
            var known = KnownSlotLayouts.ByPristineMd5(Md5Hex(pristineDll));
            if (known != null) return known;
            foreach (var l in KnownSlotLayouts.All)
            {
                if (l.FileSize != pristineDll.Length) continue;
                byte[] cur;
                try { Repristine(pristineDll, l); cur = ReadSlotBytes(pristineDll, l); }
                catch (SlotPatchException) { continue; }
                throw new SlotPatchException("this is " + l.Name + " ALREADY slot-patched (slots hold '" + Encoding.Default.GetString(cur) +
                                             "') -- patch the pristine payload copy instead");
            }
            return DiscoverLayout(pristineDll);
        }

        /// <summary>
        /// Finds the slots in a PRISTINE DLL build this library has not seen: exactly two NUL-terminated copies of
        /// <see cref="PristineBase"/> and no other occurrence of the path (ASCII or UTF-16); an x86 PE; one copy in a
        /// read-only data section (kBaseDir, 61 bytes) and one in a writable data section (g_dirBuf, 300 bytes or up to
        /// the next relocated pointer target, whichever is smaller; the bytes after the string must be zero); and no
        /// relocated pointer anywhere in the image pointing strictly inside either slot. Throws <see cref="SlotPatchException"/>.
        /// </summary>
        public static SlotLayout DiscoverLayout(byte[] pristineDll)
        {
            if (pristineDll == null) throw new ArgumentNullException(nameof(pristineDll));
            var pe = new PeImage(pristineDll);
            if (pe.Machine != 0x14C) throw new SlotPatchException("not an x86 DLL (machine 0x" + pe.Machine.ToString("X", CultureInfo.InvariantCulture) + "); tool_fast is 32-bit");

            var withNul = new byte[PristineBaseBytes.Length + 1];
            Buffer.BlockCopy(PristineBaseBytes, 0, withNul, 0, PristineBaseBytes.Length);
            var hits = FindAll(pristineDll, withNul);
            int bare = FindAll(pristineDll, PristineBaseBytes).Count;
            int tail = FindAll(pristineDll, Encoding.ASCII.GetBytes(@"H2EKMine\GPU Lightmap Work")).Count;
            int wide = FindAll(pristineDll, Encoding.Unicode.GetBytes(@"H2EKMine\GPU Lightmap Work")).Count;
            if (hits.Count != 2)
                throw new SlotPatchException("expected exactly 2 NUL-terminated copies of the base path, found " + hits.Count + " -- not a pristine gpulm_estimator build");
            if (bare != 2 || tail != 2 || wide != 0)
                throw new SlotPatchException(string.Format(CultureInfo.InvariantCulture,
                    "the base path also occurs outside the two slots (prefix copies {0}, 'H2EKMine\\GPU Lightmap Work' {1}, UTF-16 {2}) -- a slot patch would leave a stale copy", bare, tail, wide));

            var targets = pe.RelocatedPointerTargets();
            var slots = new List<SlotSpec>();
            foreach (int off in hits)
            {
                var sec = pe.SectionForFileOffset(off) ?? throw new SlotPatchException("base path copy at 0x" + off.ToString("X") + " is outside every section");
                if (sec.IsCode || !sec.IsInitializedData) throw new SlotPatchException("base path copy at 0x" + off.ToString("X") + " is in " + sec.Name + ", not an initialized-data section");
                long rva = pe.FileOffsetToRva(off);
                long rawEnd = (long)sec.PointerToRawData + sec.SizeOfRawData;
                int len;
                string name;
                if (!sec.IsWritable)
                {
                    name = "kBaseDir"; len = MaxBaseBytes + 1;
                }
                else
                {
                    name = "g_dirBuf";
                    long next = long.MaxValue;
                    foreach (var t in targets) if ((long)t > rva && (long)t < next) next = (long)t;
                    long lim = Math.Min(DirBufBytes, Math.Min(next - rva, rawEnd - off));
                    len = (int)lim;
                }
                if (len < MaxBaseBytes + 1 || off + (long)len > rawEnd) throw new SlotPatchException("slot " + name + " at 0x" + off.ToString("X") + " has no room for 60 bytes + NUL");
                for (int i = PristineBaseBytes.Length; i < len; i++)
                    if (pristineDll[off + i] != 0) throw new SlotPatchException("slot " + name + " at 0x" + off.ToString("X") + " is not NUL-padded at +" + i);
                slots.Add(new SlotSpec(name, off, len, sec.Name));
            }
            if (slots.Count(s => s.Name == "kBaseDir") != 1 || slots.Count(s => s.Name == "g_dirBuf") != 1)
                throw new SlotPatchException("expected one read-only (kBaseDir) and one writable (g_dirBuf) copy, found " + string.Join(", ", slots));
            var layout = new SlotLayout("gpulm_estimator (discovered)", Md5Hex(pristineDll), pristineDll.Length, slots, SlotLayoutSource.Discovered);
            VerifyLayoutAgainstImage(pristineDll, layout);
            return layout;
        }

        /// <summary>
        /// Proves a layout is safe for this image: every slot lies in initialized non-code data, and no relocated pointer
        /// points strictly inside a slot (so no code reads a fixed suffix of the path). Returns the number of pointers to
        /// each slot's start. Throws <see cref="SlotPatchException"/>.
        /// </summary>
        public static int[] VerifyLayoutAgainstImage(byte[] dll, SlotLayout layout)
        {
            var pe = new PeImage(dll);
            var targets = pe.RelocatedPointerTargets();
            var starts = new int[layout.Slots.Count];
            for (int k = 0; k < layout.Slots.Count; k++)
            {
                var s = layout.Slots[k];
                var sec = pe.SectionForFileOffset(s.Offset);
                if (sec == null || sec.IsCode || !sec.IsInitializedData || !sec.ContainsFileOffset(s.Offset + (long)s.Length - 1))
                    throw new SlotPatchException("slot " + s + " is not wholly inside an initialized data section");
                long rva = pe.FileOffsetToRva(s.Offset);
                foreach (var t in targets)
                {
                    if ((long)t == rva) starts[k]++;
                    else if ((long)t > rva && (long)t < rva + s.Length)
                        throw new SlotPatchException("a relocated pointer targets slot " + s + " at +" + ((long)t - rva) + " -- something reads a fixed part of the path");
                }
            }
            return starts;
        }

        /// <summary>Throws unless every slot holds exactly <see cref="PristineBase"/> followed by NUL bytes to its end.</summary>
        public static void AssertPristineSlots(byte[] dll, SlotLayout layout)
        {
            if (layout.FileSize > 0 && dll.Length != layout.FileSize)
                throw new SlotPatchException("size " + dll.Length + " != " + layout.FileSize + " for layout " + layout.Name);
            foreach (var s in layout.Slots)
            {
                if ((long)s.Offset + s.Length > dll.Length) throw new SlotPatchException("slot " + s + " is past the end of the file");
                for (int i = 0; i < s.Length; i++)
                {
                    byte want = i < PristineBaseBytes.Length ? PristineBaseBytes[i] : (byte)0;
                    if (dll[s.Offset + i] != want)
                        throw new SlotPatchException("slot " + s + " does not hold the pristine 60-char G: path (byte +" + i + ") -- patch only a pristine payload copy");
                }
            }
        }

        // ------------------------------------------------------------------ patch

        /// <summary>The design's pure function patch(dllBytes, base): system ACP, layout resolved from the bytes.</summary>
        public static byte[] Patch(byte[] pristineDll, string baseDir) => PatchDetailed(pristineDll, baseDir).Bytes;

        /// <summary>
        /// patch(dllBytes, base): (1) the input must be pristine for the layout (md5 when the layout pins one, and both
        /// slots hold the 60-char G: path); (2) base passes <see cref="TryValidateBase"/>; (3) base is written NUL-padded
        /// over each whole slot; (4) every changed byte is proven to lie inside a slot, both slots read back as base, and
        /// writing the pristine base back reproduces the input byte for byte. The input array is never modified.
        /// </summary>
        public static PatchResult PatchDetailed(byte[] pristineDll, string baseDir, SlotLayout layout = null, int? codePage = null)
        {
            if (pristineDll == null) throw new ArgumentNullException(nameof(pristineDll));
            int cp = codePage ?? AnsiCodePage.System;
            layout = layout ?? ResolveLayout(pristineDll);
            string md5 = Md5Hex(pristineDll);
            if (layout.PristineMd5 != null && !string.Equals(md5, layout.PristineMd5, StringComparison.OrdinalIgnoreCase))
                throw new SlotPatchException("input md5 " + md5 + " is not the pristine " + layout.PristineMd5 + " of layout " + layout.Name + " -- patch only a pristine payload copy");
            AssertPristineSlots(pristineDll, layout);
            byte[] ansi = ValidateBase(baseDir, cp);

            var outb = (byte[])pristineDll.Clone();
            foreach (var s in layout.Slots)
            {
                Array.Clear(outb, s.Offset, s.Length);
                Buffer.BlockCopy(ansi, 0, outb, s.Offset, ansi.Length);
            }

            int changed = 0;
            for (int i = 0; i < outb.Length; i++)
            {
                if (outb[i] == pristineDll[i]) continue;
                if (!layout.InAnySlot(i)) throw new SlotPatchException("internal: byte 0x" + i.ToString("X") + " outside every slot changed");
                changed++;
            }
            string back = ReadSlotBase(outb, layout, cp);
            if (!string.Equals(back, baseDir, StringComparison.Ordinal)) throw new SlotPatchException("internal: slots read back '" + back + "'");
            if (!Repristine(outb, layout).SequenceEqual(pristineDll)) throw new SlotPatchException("internal: re-pristining the patched bytes does not reproduce the input");

            return new PatchResult
            {
                Bytes = outb, Layout = layout, BaseDir = baseDir, CodePage = cp,
                PristineMd5 = md5, PatchedMd5 = changed == 0 ? md5 : Md5Hex(outb), ChangedBytes = changed,
            };
        }

        // ------------------------------------------------------------------ read back / identify / undo

        /// <summary>The base both slots hold. Each slot must be a NUL-terminated string of at most 60 bytes, zero to the
        /// slot's end, decodable in <paramref name="codePage"/> (default system ACP), and both slots must agree.</summary>
        public static string ReadSlotBase(byte[] dll, SlotLayout layout, int? codePage = null)
        {
            var raw = ReadSlotBytes(dll, layout);
            int cp = codePage ?? AnsiCodePage.System;
            try { return AnsiCodePage.Strict(cp).GetString(raw); }
            catch (DecoderFallbackException e) { throw new SlotPatchException("the slot string does not decode in ANSI code page " + cp, e); }
        }

        /// <summary>The raw bytes (without NUL) both slots hold. Byte-level structure check only: each slot is a
        /// NUL-terminated string of 1..60 bytes, zero to the slot's end, and both slots are identical.</summary>
        public static byte[] ReadSlotBytes(byte[] dll, SlotLayout layout)
        {
            if (dll == null) throw new ArgumentNullException(nameof(dll));
            byte[] first = null;
            foreach (var s in layout.Slots)
            {
                if ((long)s.Offset + s.Length > dll.Length) throw new SlotPatchException("slot " + s + " is past the end of the file");
                int n = 0;
                while (n < s.Length && dll[s.Offset + n] != 0) n++;
                if (n == 0 || n > MaxBaseBytes) throw new SlotPatchException("slot " + s + " holds " + (n == 0 ? "an empty string" : "a string longer than 60 bytes"));
                for (int i = n; i < s.Length; i++)
                    if (dll[s.Offset + i] != 0) throw new SlotPatchException("slot " + s + " is not NUL-padded after its string (+" + i + ")");
                var v = new byte[n];
                Buffer.BlockCopy(dll, s.Offset, v, 0, n);
                if (first == null) first = v;
                else if (!first.SequenceEqual(v)) throw new SlotPatchException("the slots disagree (" + layout.Slots[0] + " vs " + s + ")");
            }
            return first;
        }

        /// <summary>Writes <see cref="PristineBase"/> back into a (patched) DLL's slots and, when the layout pins a pristine
        /// md5, proves the result has it. Pure. Throws <see cref="SlotPatchException"/>.</summary>
        public static byte[] Repristine(byte[] patchedDll, SlotLayout layout)
        {
            ReadSlotBytes(patchedDll, layout);
            var outb = (byte[])patchedDll.Clone();
            foreach (var s in layout.Slots)
            {
                Array.Clear(outb, s.Offset, s.Length);
                Buffer.BlockCopy(PristineBaseBytes, 0, outb, s.Offset, PristineBaseBytes.Length);
            }
            if (layout.PristineMd5 != null)
            {
                string m = Md5Hex(outb);
                if (!string.Equals(m, layout.PristineMd5, StringComparison.OrdinalIgnoreCase))
                    throw new SlotPatchException("re-pristined md5 " + m + " != " + layout.PristineMd5 + " -- bytes outside the slots differ from layout " + layout.Name);
            }
            return outb;
        }

        /// <summary>
        /// Identifies a pristine OR patched DLL against <paramref name="layouts"/> (default: the known ones; a pristine
        /// unknown build is also tried through <see cref="DiscoverLayout"/>). A patched DLL matches a layout only when its
        /// slots are well-formed and re-pristining it reproduces the layout's pristine md5, i.e. it differs from the
        /// pristine build ONLY inside the slots. Returns null when nothing matches.
        /// </summary>
        public static SlotIdentity Identify(byte[] dll, IEnumerable<SlotLayout> layouts = null, int? codePage = null)
        {
            if (dll == null) throw new ArgumentNullException(nameof(dll));
            string md5 = Md5Hex(dll);
            foreach (var l in layouts ?? KnownSlotLayouts.All)
            {
                if (l.FileSize > 0 && dll.Length != l.FileSize) continue;
                try
                {
                    string b = ReadSlotBase(dll, l, codePage);
                    Repristine(dll, l);
                    return new SlotIdentity { Layout = l, Base = b, IsPristine = string.Equals(b, PristineBase, StringComparison.Ordinal), Md5 = md5 };
                }
                catch (SlotPatchException) { }
            }
            if (layouts == null)
            {
                try
                {
                    var d = DiscoverLayout(dll);
                    return new SlotIdentity { Layout = d, Base = PristineBase, IsPristine = true, Md5 = md5 };
                }
                catch (SlotPatchException) { }
            }
            return null;
        }

        // ------------------------------------------------------------------ file helpers (the only I/O in this class)

        /// <summary>
        /// Reads <paramref name="pristinePath"/>, patches it, and writes <paramref name="destPath"/> via a temp file in
        /// the same folder (then File.Replace / File.Move), then re-reads and md5-checks the destination. Refuses when
        /// source and destination are the same file, or the destination exists and <paramref name="overwrite"/> is false.
        /// The source file is never opened for writing.
        /// </summary>
        public static PatchResult PatchFile(string pristinePath, string destPath, string baseDir, SlotLayout layout = null, bool overwrite = false, int? codePage = null)
        {
            string src = Path.GetFullPath(pristinePath), dst = Path.GetFullPath(destPath);
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) throw new SlotPatchException("refusing to patch in place: " + src + " (patch a COPY)");
            if (File.Exists(dst) && !overwrite) throw new SlotPatchException("destination exists: " + dst);
            var r = PatchDetailed(File.ReadAllBytes(src), baseDir, layout, codePage);
            string dir = Path.GetDirectoryName(dst);
            string tmp = Path.Combine(dir, Path.GetFileName(dst) + ".tmp_" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(tmp, r.Bytes);
            try
            {
                if (File.Exists(dst)) File.Replace(tmp, dst, null);
                else File.Move(tmp, dst);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            string got = Md5Hex(File.ReadAllBytes(dst));
            if (!string.Equals(got, r.PatchedMd5, StringComparison.Ordinal)) throw new SlotPatchException("written file md5 " + got + " != " + r.PatchedMd5 + ": " + dst);
            return r;
        }

        /// <summary>True when the DLL at <paramref name="path"/> is a known build (pristine or slot-patched) whose slots hold
        /// exactly <paramref name="expectedBase"/>. For the before-each-bake INST check.</summary>
        public static bool VerifyFile(string path, string expectedBase, out string reason, IEnumerable<SlotLayout> layouts = null, int? codePage = null)
        {
            reason = null;
            byte[] b;
            try { b = File.ReadAllBytes(path); }
            catch (Exception e) { reason = "cannot read " + path + ": " + e.Message; return false; }
            var id = Identify(b, layouts, codePage);
            if (id == null) { reason = path + " is not a known gpulm_estimator build (md5 " + Md5Hex(b) + ") or bytes outside its slots changed"; return false; }
            if (!string.Equals(id.Base, expectedBase, StringComparison.Ordinal)) { reason = path + " slots hold '" + id.Base + "', expected '" + expectedBase + "'"; return false; }
            return true;
        }

        // ------------------------------------------------------------------ util

        private static List<int> FindAll(byte[] hay, byte[] needle)
        {
            var r = new List<int>();
            if (needle.Length == 0) return r;
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                if (hay[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) r.Add(i);
            }
            return r;
        }
    }
}
