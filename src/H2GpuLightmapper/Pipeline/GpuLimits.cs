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
        // 0.6.11: the engine's v377 +256 MiB (vram_plan.h kEnvConstB: the surface-direction PDIR buffer) belongs to the engine's separate per-part ENVELOPE check
        // (part_fits_vram: 245 B/row + 40 B/slot + 2440+256 MiB), which cuts a part that does not fit into more parts and cannot change the output. This class mirrors
        // part_rows_limit (BaseBytes / BytesPerCall below), which v377 left unchanged, so these constants stay equal to the engine's (13,265,749 rows at 8192 MiB).
        public const double BaseBytes = 1.39 * 1073741824.0;
        public const double BytesPerCall = 228.0 + 2.5 * 52.0;            // 358
        public const double BytesPerPhoton = 42.0;
        public const long DefaultStoredPhotons = 5000000L;
        /// <summary>0.6.10: 1.5 GiB for Windows, the desktop and a browser -- the same reserve the engine uses (vram_plan.h kDesktopBytes / kHeadroomBytes; 0.6.9: 1 GiB).</summary>
        public const double DesktopReserveBytes = 1.5 * 1073741824.0;
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
        /// <summary>0.6.10 (Custom quality): the video memory of the photon pass (cast + lattice, ledger peak) = <see cref="CustomPassBaseMiB"/> + <see cref="CustomPassMiBPerMillion"/> x millions of photons;
        /// fitted on cyclotron at 20 / 50 / 100 M photons (2.75 / 4.24 / 6.65 GiB). A card that cannot hold even this with <see cref="CustomFloorMiB"/> to spare is refused before anything starts.</summary>
        public const double CustomPassBaseMiB = 1850.0, CustomPassMiBPerMillion = 48.0, CustomFloorMiB = 512.0;
        public static double CustomPassMiB(long photons) { return CustomPassBaseMiB + CustomPassMiBPerMillion * (photons / 1.0e6); }
        /// <summary>Most photons whose photon pass fits in the card minus the 1.5 GiB desktop reserve (0 when even the base does not fit).</summary>
        public static long CustomPhotonsFit(long vramBytes)
        {
            double room = vramBytes / 1048576.0 - DesktopReserveBytes / 1048576.0 - CustomPassBaseMiB;
            return room <= 0 ? 0L : (long)(room / CustomPassMiBPerMillion * 1.0e6);
        }
        /// <summary>Custom only: the photon count that fits this card (information; 0 = not computed).</summary>
        public long CustomPhotonsFitCount;
        /// <summary>Custom only: non-null when the photon count cannot fit this card at all: the bake is refused with this text before any work.</summary>
        public string CustomRefusal;
        /// <summary>X2: video memory other programs held when the plan was made (null = not measured).</summary>
        public long? OtherUseBytes;
        /// <summary>X2: the VRAM the parts were sized for when other programs held more than a normal desktop (null = the dedicated figure, i.e. today).</summary>
        public long? EffectiveVramBytes;
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

        /// <summary>X2: how much video memory other programs may hold before the part sizing reacts: the 1 GiB the model already assumes for
        /// the desktop plus 2 GiB of slack (a browser, a chat client and a video player are normal). Up to this much, the argv is exactly
        /// today's; every byte beyond it comes off the VRAM the parts are sized for.</summary>
        public const double NormalOtherUseBytes = 3.0 * 1073741824.0;   // 0.6.10: fixed at 3 GiB (a normal desktop's 1 GiB + 2 GiB of slack); the part sizing reserve above is 1.5 GiB

        /// <summary>X2: the live video memory held by programs other than the bake, in bytes (null = unknown: nothing changes). From
        /// gpucheck: the system-wide GPU Adapter Memory "Dedicated Usage" counter, and what the OS budget (QueryVideoMemoryInfo, LOCAL)
        /// implies (dedicated - budget + this process's own usage); the larger of the two.</summary>
        public static long? ComputeOtherUse(long? dedicatedBytes, long? budgetBytes, long? processUsageBytes, long? systemDedicatedUsageBytes)
        {
            long? best = null;
            if (systemDedicatedUsageBytes.HasValue && systemDedicatedUsageBytes.Value >= 0) best = systemDedicatedUsageBytes.Value;
            if (dedicatedBytes.HasValue && dedicatedBytes.Value > 0 && budgetBytes.HasValue && budgetBytes.Value > 0)
            {
                long implied = Math.Max(0L, dedicatedBytes.Value - (budgetBytes.Value - Math.Max(0L, processUsageBytes ?? 0L)));
                if (!best.HasValue || implied > best.Value) best = implied;
            }
            return best;
        }

        /// <param name="photons">Custom only: the photon budget (an upper bound of the stored photons); 0 = the Low / Super assumption.</param>
        /// <param name="otherUseBytes">X2: video memory other programs hold right now (see <see cref="ComputeOtherUse"/>); null = not measured.
        /// Beyond <see cref="NormalOtherUseBytes"/> the parts are sized for the VRAM that is left; below it nothing changes.</param>
        public static GpuLimits Compute(long? vramBytes, string source, long photons, long? otherUseBytes = null)
        {
            var g = new GpuLimits { VramBytes = vramBytes, Source = source };
            if (!vramBytes.HasValue || vramBytes.Value <= 0)
            {
                g.Notes.Add("the adapter's dedicated memory is unknown: parts are sized by the engine alone (the production behaviour)");
                return g;
            }
            long dedicated = vramBytes.Value;
            long v = dedicated;
            string mib = (v / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
            if (v < MinSizedVramBytes)
            {
                g.Notes.Add("the adapter reports only " + mib + " of dedicated memory (an integrated GPU shares system RAM): parts are not sized from it");
                return g;
            }
            // X2: another program holds more than a normal desktop does -> size the parts for what is left (the 24 GB / idle-desktop path
            // never gets here: other use <= NormalOtherUseBytes leaves v == dedicated and every figure below is today's)
            if (otherUseBytes.HasValue)
            {
                g.OtherUseBytes = otherUseBytes.Value;
                double excess = otherUseBytes.Value - NormalOtherUseBytes;
                if (excess > 0)
                {
                    v = Math.Max(0L, dedicated - (long)excess);
                    g.EffectiveVramBytes = v;
                    string other = (otherUseBytes.Value / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
                    string eff = (v / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
                    g.Notes.Add(other + " of VRAM is in use by other programs; parts sized for " + eff + " (close them to get the full size back; a normal desktop's "
                                + ((long)(NormalOtherUseBytes / 1048576.0)).ToString("N0", CultureInfo.InvariantCulture) + " MiB is already allowed for)");
                    mib = eff;
                }
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
            int d3d = D3d11ResourceLimitMB(dedicated);   // X2: a property of the adapter, not of what other programs hold
            if (d3d < DefaultBufCapMB)
            {
                g.BufCapMB = Math.Max(d3d, MinBufCapMB);
                g.Notes.Add("D3D11's documented per-resource limit on " + (dedicated / 1048576L).ToString("N0", CultureInfo.InvariantCulture) + " MiB is " + d3d + " MiB, below fullgpu's default " + DefaultBufCapMB + " MiB cap: -BufCapMB " + g.BufCapMB);
            }
            // 0.6.10 (measured on the stock maps at Super with an 8 GB and a 6 GB card limit): the photon lattice pass no longer allocates the main pass's buffers; it
            // runs on a small slice of the map and needs about 1.5-3.5 GiB of video memory (the main-pass parts are the larger figure). Both are kept inside the card.
            // 0.6.10: Custom quality with a very large photon count on a small card. The photon pass (cast + lattice) grows with the photon budget;
            // measured on cyclotron with an 8 GB card limit and the hard limit on: 20 M photons 2.7 GiB, 50 M 4.1 GiB, 100 M 6.5 GiB (ledger peak of that pass).
            if (photons > 0)
            {
                g.CustomPhotonsFitCount = CustomPhotonsFit(v);
                double needMiB = CustomPassMiB(photons);
                double cardMiB = v / 1048576.0;
                string fitTxt = g.CustomPhotonsFitCount.ToString("N0", CultureInfo.InvariantCulture);
                if (needMiB > cardMiB - CustomFloorMiB)
                    g.CustomRefusal = "Custom quality with " + photons.ToString("N0", CultureInfo.InvariantCulture) + " photons needs about " + (needMiB / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)
                        + " GiB of video memory for the photon pass; this graphics card has " + (cardMiB / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
                        + " (about " + (Math.Max(0.0, cardMiB - DesktopReserveBytes / 1048576.0) / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB usable after Windows and your other programs). "
                        + "The most this card handles is about " + fitTxt + " photons. Lower the photons to " + fitTxt + " or fewer, or bake with Super quality. Nothing was started.";
                else if (needMiB > cardMiB - DesktopReserveBytes / 1048576.0)
                    g.Notes.Add("WARNING Custom quality with " + photons.ToString("N0", CultureInfo.InvariantCulture) + " photons needs about " + (needMiB / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)
                                + " GiB of video memory for the photon pass, more than the " + ((cardMiB - DesktopReserveBytes / 1048576.0) / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)
                                + " GiB this card has to spare; it may run slower or stop. About " + fitTxt + " photons fit comfortably.");
            }
            if (v < (14L << 30))
                g.Notes.Add("the photon lattice pass runs on a small slice of the map and needs about 1.5-3.5 GiB of video memory at Super (measured on the stock maps); the main pass is cut into parts that fit the card");
            return g;
        }

        // ---------------------------------------------------------------- light probes (item 4)

        public sealed class ProbeVerdict
        {
            public int Points, CapMB, MaxPoints;
            /// <summary>Batches the probe pass runs in (1 = one pass; 0.6.9 rc069_a2: the engine runs more than <see cref="MaxPoints"/> points as exact batches).</summary>
            public int Batches = 1;
            /// <summary>More points than Halo 2 itself allows per BSP (<see cref="HaloMaxProbePoints"/>): refused before anything starts.</summary>
            public bool Refuse;
            /// <summary>More points than one GPU pass holds (<see cref="MaxPoints"/>): the engine bakes them in <see cref="Batches"/> exact batches (same result as one pass, takes longer).</summary>
            public bool Batched;
            /// <summary>Within <see cref="MarginPoints"/> of one pass's bound: the largest chart's rows are taken from the same buffer.</summary>
            public bool Marginal;
            public string Message;
        }

        /// <summary>The largest chart's call rows also live in the GQ buffer (capC = capG - tail, seg_plan.h:163-165); LIMITS.md measured
        /// 270-281 per BSP at the default cap, i.e. up to 11 points' worth (1.08 M rows).</summary>
        public const int MarginPoints = 11;

        /// <summary>GQ rows one buffer of capMB holds.</summary>
        public static long CapRows(int capMB) { return (long)capMB * 1048576L / GqBytes; }

        /// <summary>Most probe points ONE GPU probe pass can hold before the tail alone fills the GQ buffer: the largest N with
        /// N x 98,304 &lt; floor(capMB MiB / 68) (the engine's check is tail &gt;= capG). 281 at the default 1,792 MiB. Since rc069_a2 the engine
        /// runs a BSP with more points as several exact batches of at most this many, so this is a batch size, no longer a limit.</summary>
        public static int MaxProbePoints(int capMB)
        {
            long cap = CapRows(capMB);
            if (cap <= 0) return 0;
            return (int)((cap - 1) / ProbeRowsPerPoint);
        }

        /// <summary>The probe pass runs with the default cap: gpu_lightprobes.py cannot forward -BufCapMB (the payload driver has no such
        /// argument), and a cap above D3D11's 2,048 MB ceiling is untested for the probe buffers, so the cap is not raised.
        /// 0.6.9 rc069_a2: only Halo 2's own ceiling (1,024 points per BSP) refuses; a BSP with more points than one pass holds (281 at the
        /// default cap) is baked by the engine in exact batches (engine_v375: the batched result is byte-identical to a single run).</summary>
        public static ProbeVerdict CheckProbes(int points, int capMB = DefaultBufCapMB)
        {
            var v = new ProbeVerdict { Points = points, CapMB = capMB, MaxPoints = MaxProbePoints(capMB) };
            if (points > HaloMaxProbePoints)
            {
                v.Refuse = true;
                v.Message = "this BSP has " + points.ToString("N0", CultureInfo.InvariantCulture) + " light probe points; Halo 2 itself allows at most "
                            + HaloMaxProbePoints.ToString("N0", CultureInfo.InvariantCulture) + " per BSP, so the lightmap tag could not hold them. Nothing was started. "
                            + "Remove some light probe points from the BSP, or bake with Lightmap Only (CLI: --mode lightmap), which resets this BSP's probes to zero like `tool lightmaps` does";
            }
            else if (v.MaxPoints > 0 && points > v.MaxPoints)
            {
                v.Batched = true;
                v.Batches = (points + v.MaxPoints - 1) / v.MaxPoints;
                v.Message = "this BSP has " + points + " light probe points; one GPU probe pass holds " + v.MaxPoints + ", so they are baked in " + v.Batches
                            + " batches (exact: the result is the same as one pass, it only takes about " + v.Batches + " times as long)";
            }
            else if (points > v.MaxPoints - MarginPoints)
            {
                v.Marginal = true;
                v.Message = "this BSP has " + points + " light probe points, close to the " + v.MaxPoints + " one GPU probe pass can hold at its " + capMB
                            + " MiB buffer cap: the rows of the largest lightmap chart share that buffer, so the probe pass may still refuse (BUFCAP exit 10) after the lightmap bake";
            }
            return v;
        }
    }
}
