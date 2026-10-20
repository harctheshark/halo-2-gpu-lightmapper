// X1 (Monday 2026-10-19 robustness list, items 2 and 4): what the bake may ask of THIS adapter, decided before anything runs.
//   (b) VRAM-aware part sizing: the part planner of fullgpu sizes parts by the 1,792 MiB per-buffer cap and the host RAM only; it never
//       looks at VRAM (LIMITS.md 8.2). From the adapter's dedicated memory this class derives gpu_bake's -MaxCallsPerPart (and, on a
//       card under ~7 GiB, -BufCapMB). On a 24 GB card both come out as 0 = "not passed": the production argv is unchanged.
//   (a) light-probe preflight: the GPU probe pass holds 98,304 rows per probe point inside one 68-byte-per-row GQ buffer; past the cap
//       it refuses (BUFCAP exit 10) AFTER the lightmap bake of a Standard run. The count is known beforehand, so the shell refuses first.
// Pure: no process, no file, no GPU. All constants are the engine's (seg_plan.h / gather.cpp) or LIMITS.md's fitted figures.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class GpuLimits
    {
        // ---- engine constants (seg_plan.h:40-50, gather.cpp:429-442) ---------------------------------------------------------
        /// <summary>fullgpu's default per-resource buffer cap (kDefaultCapMB): 1.75 GiB, 256 MB under D3D11's 2048 MB ceiling.</summary>
        public const int DefaultBufCapMB = 1792;
        /// <summary>gpu_bake -BufCapMB accepts 64..4095 (kMinCapMB / kMaxCapMB).</summary>
        public const int MinBufCapMB = 64;
        /// <summary>The planner's own rows-per-part ceiling at the default cap: 1,792 MiB / 36 B (T9 record) = 52,195,783 call rows.</summary>
        public const long PlannerCapRows = 52195783L;
        /// <summary>GQ record: the largest per-call record of the main pass (kGQB).</summary>
        public const int GqBytes = 68;
        /// <summary>One probe point = 6 cube faces x 128 x 128 texels = 98,304 shading rows (hdr_cubemap_builder; GPULM_PROBE_N 128).</summary>
        public const int ProbeRowsPerPoint = 6 * 128 * 128;
        /// <summary>Halo 2's own ceiling (structure_lightmap_lighting_environment_block): 1,024 per BSP.</summary>
        public const int HaloMaxProbePoints = 1024;

        // ---- LIMITS.md 8.1 / 8.2: VRAM of the main pass = 1.39 GiB + 228 B per call row + 52 B per grid slot + 42 B per stored photon
        // (fit on 8 measured peaks, +-1.3 GB), 2.5 grid slots per call, about 5 M stored photons, about 1 GiB used by the desktop.
        public const double BaseBytes = 1.39 * 1073741824.0;
        public const double BytesPerCall = 228.0 + 2.5 * 52.0;            // 358
        public const double BytesPerPhoton = 42.0;
        public const long DefaultStoredPhotons = 5000000L;
        public const double DesktopReserveBytes = 1073741824.0;
        /// <summary>Safety margin on the rows that fit (the fit is +-1.3 GB and the grid slots per call measured 1.44-4.53).</summary>
        public const double RowsMargin = 0.90;
        /// <summary>An adapter reporting less dedicated memory than this (an integrated GPU: its memory is shared system RAM) is not sized.</summary>
        public const long MinSizedVramBytes = 2L << 30;
        /// <summary>Under this many rows a part cannot even hold one ordinary chart: sized, but with a warning.</summary>
        public const long TinyRows = 3000000L;

        /// <summary>Dedicated VRAM of the adapter fullgpu will use, in bytes (null = unknown: nothing is changed).</summary>
        public long? VramBytes;
        /// <summary>Where the figure came from ("gpucheck: NVIDIA GeForce ...", "--assume-vram-mb").</summary>
        public string Source;
        /// <summary>gpu_bake -MaxCallsPerPart; 0 = not passed (the planner's own cap binds first, or the VRAM is unknown).</summary>
        public long MaxCallsPerPart;
        /// <summary>gpu_bake -BufCapMB; 0 = not passed (the default 1,792 MiB cap is below D3D11's documented per-resource limit).</summary>
        public int BufCapMB;
        /// <summary>Call rows the VRAM holds in one part before the margin (information).</summary>
        public long RowsFit;
        public readonly List<string> Notes = new List<string>();

        /// <summary>True when gpu_bake gets an extra argument for this adapter.</summary>
        public bool Changes { get { return MaxCallsPerPart > 0 || BufCapMB > 0; } }

        public static GpuLimits Unknown(string why)
        {
            var g = new GpuLimits();
            if (why != null) g.Notes.Add(why);
            return g;
        }

        /// <summary>D3D11's documented per-resource limit for an adapter with this much dedicated memory:
        /// min(max(128 MB, a quarter of the VRAM), 2048 MB) -- the formula sun_exact.h:368-375 already uses.</summary>
        public static int D3d11ResourceLimitMB(long vramBytes)
        {
            double mib = vramBytes / 1048576.0;
            return (int)Math.Min(Math.Max(128.0, Math.Floor(mib / 4.0)), 2048.0);
        }

        /// <param name="photons">Custom only: the photon budget (an upper bound of the stored photons); 0 = the Low / Super assumption.</param>
        public static GpuLimits Compute(long? vramBytes, string source, long photons)
        {
            var g = new GpuLimits { VramBytes = vramBytes, Source = source };
            if (!vramBytes.HasValue || vramBytes.Value <= 0)
            {
                g.Notes.Add("the adapter's dedicated memory is unknown: parts are sized by the engine alone (the production behaviour)");
                return g;
            }
            long v = vramBytes.Value;
            string mib = (v / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
            if (v < MinSizedVramBytes)
            {
                g.Notes.Add("the adapter reports only " + mib + " of dedicated memory (an integrated GPU shares system RAM): parts are not sized from it");
                return g;
            }
            // rows that fit: (VRAM - desktop - fixed base - stored photons) / bytes per call row, with the margin
            long stored = Math.Max(DefaultStoredPhotons, photons);
            double free = v - DesktopReserveBytes - BaseBytes - stored * BytesPerPhoton;
            long fit = free <= 0 ? 0L : (long)(free / BytesPerCall);
            g.RowsFit = fit;
            long rows = (long)(fit * RowsMargin);
            // rows >= the planner's own 52.2 M cap: that cap binds first and nothing is passed (24 GB: 62.9 M fit, production argv unchanged)
            if (rows < PlannerCapRows)
            {
                g.MaxCallsPerPart = Math.Max(rows, 1000000L);
                g.Notes.Add("parts sized for " + mib + " of VRAM: -MaxCallsPerPart " + g.MaxCallsPerPart.ToString("N0", CultureInfo.InvariantCulture)
                            + " call rows (" + fit.ToString("N0", CultureInfo.InvariantCulture) + " fit, " + (int)Math.Round((1.0 - RowsMargin) * 100) + "% margin; the planner's own cap is "
                            + PlannerCapRows.ToString("N0", CultureInfo.InvariantCulture) + ")");
                if (rows < TinyRows)
                    g.Notes.Add("WARNING this adapter has very little free memory for the main pass: parts of " + g.MaxCallsPerPart.ToString("N0", CultureInfo.InvariantCulture)
                                + " rows may still be too small for the biggest lightmap charts, and the photon lattice pass (which -MaxCallsPerPart does not size) may not fit");
            }
            int d3d = D3d11ResourceLimitMB(v);
            if (d3d < DefaultBufCapMB)
            {
                g.BufCapMB = Math.Max(d3d, MinBufCapMB);
                g.Notes.Add("D3D11's documented per-resource limit on " + mib + " is " + d3d + " MiB, below fullgpu's default " + DefaultBufCapMB + " MiB cap: -BufCapMB " + g.BufCapMB);
            }
            if (v < (14L << 30))
                g.Notes.Add("the photon lattice pass (6-14 GiB on the big maps) is its own process and is not sized by the part knob: a map the lattice does not fit is not made to fit");
            return g;
        }

        // ---------------------------------------------------------------- light probes (item 4)

        public sealed class ProbeVerdict
        {
            public int Points, CapMB, MaxPoints;
            /// <summary>Probe points the GQ buffer can hold if the largest lightmap chart is tiny (the hard upper bound).</summary>
            public bool Refuse;
            /// <summary>Within <see cref="MarginPoints"/> of the bound: the largest chart's rows are taken from the same buffer.</summary>
            public bool Marginal;
            public string Message;
        }

        /// <summary>The largest chart's call rows also live in the GQ buffer (capC = capG - tail, seg_plan.h:163-165); LIMITS.md measured
        /// 270-281 per BSP at the default cap, i.e. up to 11 points' worth (1.08 M rows).</summary>
        public const int MarginPoints = 11;

        /// <summary>GQ rows one buffer of capMB holds.</summary>
        public static long CapRows(int capMB) { return (long)capMB * 1048576L / GqBytes; }

        /// <summary>Most probe points one BSP can have before the tail alone fills the GQ buffer: the largest N with
        /// N x 98,304 &lt; floor(capMB MiB / 68) (the engine's check is tail &gt;= capG). 281 at the default 1,792 MiB.</summary>
        public static int MaxProbePoints(int capMB)
        {
            long cap = CapRows(capMB);
            if (cap <= 0) return 0;
            return (int)((cap - 1) / ProbeRowsPerPoint);
        }

        /// <summary>The probe pass runs with the default cap: gpu_lightprobes.py cannot forward -BufCapMB (the payload driver has no such
        /// argument), and a cap above D3D11's 2,048 MB ceiling is untested for the probe buffers, so the cap is not raised.</summary>
        public static ProbeVerdict CheckProbes(int points, int capMB = DefaultBufCapMB)
        {
            var v = new ProbeVerdict { Points = points, CapMB = capMB, MaxPoints = MaxProbePoints(capMB) };
            if (points > v.MaxPoints)
            {
                v.Refuse = true;
                v.Message = "this BSP has " + points + " light probe points; the GPU probe pass holds at most " + v.MaxPoints + " per BSP at its " + capMB
                            + " MiB buffer cap (" + ProbeRowsPerPoint.ToString("N0", CultureInfo.InvariantCulture) + " rows per point x " + GqBytes
                            + " B per row must fit one buffer; Halo 2 itself allows " + HaloMaxProbePoints.ToString("N0", CultureInfo.InvariantCulture)
                            + "). Nothing was started. Bake with Lightmap Only (CLI: --mode lightmap), which resets this BSP's probes to zero like `tool lightmaps` does";
            }
            else if (points > v.MaxPoints - MarginPoints)
            {
                v.Marginal = true;
                v.Message = "this BSP has " + points + " light probe points, close to the " + v.MaxPoints + " the GPU probe pass can hold at its " + capMB
                            + " MiB buffer cap: the rows of the largest lightmap chart share that buffer, so the probe pass may still refuse (BUFCAP exit 10) after the lightmap bake";
            }
            return v;
        }
    }
}
