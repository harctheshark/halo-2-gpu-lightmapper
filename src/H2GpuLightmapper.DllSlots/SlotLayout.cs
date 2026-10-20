using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>One path slot in a gpulm_estimator.dll: a NUL-terminated ANSI string at a fixed file offset.</summary>
    public sealed class SlotSpec
    {
        /// <summary>"kBaseDir" (the .rdata const char[61]) or "g_dirBuf" (the .data char[300], kDir's default).</summary>
        public string Name { get; }
        /// <summary>File offset of the first byte.</summary>
        public int Offset { get; }
        /// <summary>Bytes owned by the slot (61 for kBaseDir = 60 chars + NUL; 300 for g_dirBuf).</summary>
        public int Length { get; }
        /// <summary>PE section the slot lives in (".rdata" / ".data"), informational.</summary>
        public string Section { get; }

        public SlotSpec(string name, int offset, int length, string section)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length < DllSlotPatcher.MaxBaseBytes + 1) throw new ArgumentOutOfRangeException(nameof(length), "a slot must hold at least 60 bytes + NUL");
            Name = name; Offset = offset; Length = length; Section = section;
        }

        public bool Contains(long fileOffset) => fileOffset >= Offset && fileOffset < (long)Offset + Length;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0}@0x{1:X}+{2}({3})", Name, Offset, Length, Section);
    }

    public enum SlotLayoutSource
    {
        /// <summary>A layout verified by hand and pinned to the pristine DLL's md5 (production aaf10a0f / skeleton f0fb73ac).</summary>
        Known,
        /// <summary>Found by <see cref="DllSlotPatcher.DiscoverLayout"/> in a DLL build this library has never seen.</summary>
        Discovered,
    }

    /// <summary>Where the base-path slots of ONE pristine DLL build are. Immutable.</summary>
    public sealed class SlotLayout
    {
        public string Name { get; }
        /// <summary>md5 (lowercase hex) of the PRISTINE dll this layout belongs to.</summary>
        public string PristineMd5 { get; }
        public long FileSize { get; }
        public IReadOnlyList<SlotSpec> Slots { get; }
        public SlotLayoutSource Source { get; }

        public SlotLayout(string name, string pristineMd5, long fileSize, IEnumerable<SlotSpec> slots, SlotLayoutSource source)
        {
            Name = name;
            PristineMd5 = pristineMd5?.ToLowerInvariant();
            FileSize = fileSize;
            Slots = (slots ?? throw new ArgumentNullException(nameof(slots))).OrderBy(s => s.Offset).ToList();
            Source = source;
            if (Slots.Count == 0) throw new ArgumentException("a layout needs at least one slot");
            for (int i = 1; i < Slots.Count; i++)
                if ((long)Slots[i - 1].Offset + Slots[i - 1].Length > Slots[i].Offset) throw new ArgumentException("slots overlap");
            if (fileSize > 0 && (long)Slots[Slots.Count - 1].Offset + Slots[Slots.Count - 1].Length > fileSize)
                throw new ArgumentException("a slot runs past the end of the file");
        }

        public bool InAnySlot(long fileOffset)
        {
            foreach (var s in Slots) if (s.Contains(fileOffset)) return true;
            return false;
        }

        /// <summary>Same offsets and lengths (names / md5 / source ignored).</summary>
        public bool SameSlotsAs(SlotLayout other) =>
            other != null && other.Slots.Count == Slots.Count &&
            Slots.Zip(other.Slots, (a, b) => a.Offset == b.Offset && a.Length == b.Length).All(x => x);

        /// <summary>Compact JSON (for MANIFEST.json / the CLI): {"name":..,"pristine_md5":..,"size":..,"source":..,"slots":[{"name":..,"offset":..,"length":..,"section":..}]}</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(Json.Str(Name))
              .Append(",\"pristine_md5\":").Append(Json.Str(PristineMd5))
              .Append(",\"size\":").Append(FileSize.ToString(CultureInfo.InvariantCulture))
              .Append(",\"source\":").Append(Json.Str(Source.ToString().ToLowerInvariant()))
              .Append(",\"slots\":[");
            for (int i = 0; i < Slots.Count; i++)
            {
                var s = Slots[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(s.Name))
                  .Append(",\"offset\":").Append(s.Offset.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"length\":").Append(s.Length.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"section\":").Append(Json.Str(s.Section)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public override string ToString() => Name + " [" + PristineMd5 + ", " + FileSize + " B] " + string.Join(", ", Slots);
    }

    /// <summary>The layouts verified during judging (FINAL_DESIGN section 1) and re-verified by S0.4 (scan + relocation analysis).</summary>
    public static class KnownSlotLayouts
    {
        /// <summary>engine_v373\v014-inject-estimator\gpulm_estimator.dll (also dev_AO's copy).</summary>
        public static readonly SlotLayout Production = new SlotLayout(
            "gpulm_estimator (production)", "aaf10a0fbc1b78ddefebe8248acddcca", 773120,
            new[] { new SlotSpec("kBaseDir", 0x6CBE0, 61, ".rdata"), new SlotSpec("g_dirBuf", 0xB6608, 300, ".data") },
            SlotLayoutSource.Known);

        /// <summary>engine_v373\v043-tagwriter\dll\gpulm_estimator.dll, the SKELETON-mode build (also dev_AO's copy).</summary>
        public static readonly SlotLayout Skeleton = new SlotLayout(
            "gpulm_estimator (skeleton)", "f0fb73ac9cd42f8f716f26b64584759b", 687616,
            new[] { new SlotSpec("kBaseDir", 0x66F94, 61, ".rdata"), new SlotSpec("g_dirBuf", 0xA2400, 300, ".data") },
            SlotLayoutSource.Known);

        /// <summary>
        /// W1 2026-10-20: engine_v373\v014-inject-estimator\gpulm_estimator.dll after the 0.6.2 promotion (b2370477, also the AO engine's copy).
        /// The offsets are the ones <see cref="DllSlotPatcher.DiscoverLayout"/> finds (two NUL-terminated copies of the pristine
        /// G:\...\GPU Lightmap Work\ path, no relocated pointer into a slot interior: tests/H2GpuLightmapper.DllSlots.Tests, test
        /// "promoted-production layout"); before this entry the shell reached them by discovery on every extraction.
        /// </summary>
        public static readonly SlotLayout ProductionV062 = new SlotLayout(
            "gpulm_estimator (production, 0.6.2)", "b2370477f36db8c74e6749d5389f2a57", 775168,
            new[] { new SlotSpec("kBaseDir", 446432, 61, ".rdata"), new SlotSpec("g_dirBuf", 748552, 300, ".data") },
            SlotLayoutSource.Known);

        public static IReadOnlyList<SlotLayout> All { get; } = new[] { Production, ProductionV062, Skeleton };

        public static SlotLayout ByPristineMd5(string md5)
        {
            if (md5 == null) return null;
            foreach (var l in All) if (string.Equals(l.PristineMd5, md5, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }
    }

    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
        public static string Bool(bool b) => b ? "true" : "false";
    }
}
