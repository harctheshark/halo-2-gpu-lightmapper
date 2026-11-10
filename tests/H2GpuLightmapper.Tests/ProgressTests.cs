// 0.6.12: the live progress model (Pipeline/BakeProgress.cs) on synthetic bake output: the bar moves inside a step and never back, the GPU bake's
// parts are found from fullgpu's lines (sky chain rounds, PROGRESS| per-sun %, the surface segments), the time left comes from the stage's own progress
// and from this map's last bake (stage_times.json), a .map bake has its Read map / Copy tags / Write map stages. No process, no file outside the sandbox.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Pipeline;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    internal static class ProgressTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("progress 0.6.12: the bar moves inside every step and never back; GPU-bake parts from fullgpu's lines; first bake = time left of the part only", FirstBake);
            yield return T("progress 0.6.12: with this map's last stage times every step and the total have a time left that falls as the bake runs; stage_times.json round trip", SecondBake);
            yield return T("progress 0.6.12: a .map bake has Read map / Copy tags (its file count) / the bake / Write map; PROGRESS| lines are kept out of the log (LineKind.Progress)", MapBake);
            yield return T("progress 0.6.12b: a map's FIRST bake gets a rough time left once the lattice slots are printed (this PC's learned rates, else defaults); _rates round trip", FirstBakeRough);
        }

        private static void FirstBakeRough()
        {
            var p = new BakeProgress(false, null, T0);
            p.OnPlan(new[] { StepId.Preflight, StepId.Capture, StepId.Derive, StepId.GpuBake, StepId.TagWriter, StepId.Install, StepId.Verify });
            p.OnStep(StepId.Capture, T0.AddSeconds(5));
            p.OnStep(StepId.GpuBake, T0.AddSeconds(60));
            var v = p.Snapshot(T0.AddSeconds(61));
            Assert.True(!v.TotalLeft.HasValue, "before the slots line the first bake has no total");
            p.OnLine("      [lattice] BUFCAP: v372 cap 1792 MB per resource; coverage bound 1 of 2 grid texels (x16 cells = 22368848 call rows, 42.1% of 53122976 slots) in 47 ms; 2 work segments", T0.AddSeconds(62));
            Assert.True(Math.Abs(p.MSlots - 53.122976) < 1e-6, "slots read: " + p.MSlots);
            p.OnLine("      [lattice] SKYX: plan: S_cast 1 (captured: lcg_cast.bin), 20 lights in cast order (5 directional-sky)", T0.AddSeconds(63));
            v = p.Snapshot(T0.AddSeconds(70));
            Assert.True(v.TotalLeft.HasValue && v.StageLeft.HasValue, "after the slots line: a step and a total time left (" + v.Line + ")");
            Assert.Contains(v.Line, "rough", "the first bake's total is labelled rough");
            double t1 = v.TotalLeft.Value;
            // defaults: lattice 2.2 + gather 2.8 + suns 2.0 + surface 0.6 + compose 0.4 = 8.0 s per M slots of GPU parts (+ tag writer / install / verify)
            Assert.True(t1 > 8.0 * 53.1 - 60 && t1 < 9.5 * 53.1, "rough total from the defaults: " + t1);
            Assert.True(v.Detail.ContainsKey("TagWriter") && v.Detail["TagWriter"].StartsWith("~"), "a pending step shows its rough time");
            // learned rates (twice the defaults) give twice the GPU time
            var rates = new Dictionary<string, double> { { "GpuBake.lattice", 4.4 }, { "GpuBake.gather", 5.6 }, { "GpuBake.suns", 4.0 }, { "GpuBake.surface", 1.2 }, { "GpuBake.compose", 0.8 }, { "GpuBake", 17 }, { "TagWriter", 1.2 }, { "Install", 0.06 }, { "Verify", 0.06 } };
            var q = new BakeProgress(false, null, T0, rates);
            q.OnPlan(new[] { StepId.Preflight, StepId.Capture, StepId.Derive, StepId.GpuBake, StepId.TagWriter, StepId.Install, StepId.Verify });
            q.OnStep(StepId.GpuBake, T0.AddSeconds(60));
            q.OnLine("      [lattice] BUFCAP: v372 cap 1792 MB per resource; coverage bound 1 of 2 grid texels (x16 cells = 22368848 call rows, 42.1% of 53122976 slots) in 47 ms", T0.AddSeconds(62));
            q.OnLine("      [lattice] SKYX: plan: S_cast 1 (captured: lcg_cast.bin), 20 lights in cast order (5 directional-sky)", T0.AddSeconds(63));
            var w = q.Snapshot(T0.AddSeconds(70));
            Assert.True(w.TotalLeft.HasValue && Math.Abs(w.TotalLeft.Value - 2 * t1) < 0.05 * t1 + 10, "learned rates scale the estimate: " + w.TotalLeft + " vs 2 x " + t1);
            // a rough part that runs past its estimate says 'estimating', never '0 s left'
            var late = q.Snapshot(T0.AddSeconds(63 + 4.4 * 53.2 + 30));
            Assert.True(!late.Line.Contains("about 0 s"), "past the rough time: no '0 s left' (" + late.Line + ")");
            // _rates round trip (averaged with the stored rate)
            string work = C != null && C.Sb != null ? C.Sb.Dir("progress_rates") : Path.Combine(Path.GetTempPath(), "h2lm_rates_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            StageTimes.SaveRates(work, "super|Standard|noao", new Dictionary<string, double> { { "GpuBake.gather", 106.2 }, { "Capture", 30 } }, 53.1, DateTime.UtcNow);
            var r1 = StageTimes.LoadRates(work, "super|Standard|noao");
            Assert.True(Math.Abs(r1["GpuBake.gather"] - 2.0) < 1e-3 && !r1.ContainsKey("Capture"), "rate = seconds / M slots; Capture is not per slot");
            StageTimes.SaveRates(work, "super|Standard|noao", new Dictionary<string, double> { { "GpuBake.gather", 212.4 } }, 53.1, DateTime.UtcNow);
            Assert.True(Math.Abs(StageTimes.LoadRates(work, "super|Standard|noao")["GpuBake.gather"] - 3.0) < 1e-3, "the second bake moves the rate half way");
            StageTimes.Save(work, "k", new Dictionary<string, double> { { "Capture", 30 } }, DateTime.UtcNow);
            Assert.True(StageTimes.LoadRates(work, "super|Standard|noao").Count == 1 && StageTimes.Load(work, "k").Count == 1, "maps and _rates live side by side");
        }

        private static readonly DateTime T0 = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A short synthetic bake: (seconds after the start, the line or a step change "@Step").</summary>
        private static List<KeyValuePair<double, string>> Script()
        {
            var s = new List<KeyValuePair<double, string>>();
            Action<double, string> a = (t, l) => s.Add(new KeyValuePair<double, string>(t, l));
            a(0, "@Preflight"); a(5, "@Capture"); a(45, "@Derive"); a(55, "@GpuBake");
            a(60, "      [lattice] SKYX: plan: S_cast 1 (captured: lcg_cast.bin), 20 lights in cast order (5 directional-sky)");
            for (int r = 0; r < 4; r++) a(60 + 20 * r, "      [lattice] SKYX: round " + r + ": 1000 walkers (lights " + (5 * r) + "..19, 0 with 4 phases), GPU+readback 1 ms");
            a(140, "      [lattice] SKYX: all 20 chain lights exact in 4 rounds");
            a(150, "      lattice pre-pass 95.0s");
            a(151, "      GATEWALK: round 1: 10 rejected calls");
            a(200, "      PROGRESS| dispatch done: GPU 49000 ms | wall 49000 ms");
            a(201, "      PROGRESS| DIRECT: lights.bin 4 SUN + 0 ANALYTIC, farLen=1000.0");
            double t = 201;
            for (int k = 0; k < 4; k++)
            {
                double len = k == 0 ? 60 : 10;
                for (int pc = 20; pc <= 100; pc += 20) a(t + len * pc / 100.0, "      PROGRESS| DIRECT: SUN " + k + " progress " + pc + "% (1 of 2) " + (len * pc * 10) + " ms");
                t += len;
                a(t, "      PROGRESS| DIRECT: SUN " + k + " tiles: 9 dispatches, size 1..2 (floor 1, cap 2), slowest 1 ms, " + (len * 1000) + " ms");
            }
            a(t + 1, "      SURFCULL: v372H per-light first-bounce photons");
            a(t + 6, "      SURFCULL:   segment 1/2: queries [0..1)");
            a(t + 11, "      SURFCULL:   segment 2/2: queries [1..2)");
            a(t + 12, "      DIRECT: SURFACE pass 11000 ms (3 lights)");
            a(t + 13, "      COMPOSE: wrote atlas_out.bin  1 texels");
            a(t + 15, "@TagWriter"); a(t + 45, "@Install"); a(t + 47, "@Verify"); a(t + 48, "#end");
            return s;
        }

        private static BakeProgress Run(Dictionary<string, double> hist, Action<double, BakeProgress.View, string> each, out DateTime end)
        {
            var p = new BakeProgress(false, hist, T0);
            p.OnPlan(new[] { StepId.Preflight, StepId.Capture, StepId.Derive, StepId.GpuBake, StepId.TagWriter, StepId.Install, StepId.Verify });
            end = T0;
            foreach (var kv in Script())
            {
                var at = T0.AddSeconds(kv.Key);
                end = at;
                if (kv.Value == "#end") { p.Finish(true, at); break; }
                StepId id;
                if (kv.Value.StartsWith("@", StringComparison.Ordinal) && Enum.TryParse(kv.Value.Substring(1), out id)) p.OnStep(id, at);
                else p.OnLine(kv.Value, at);
                // the window's 1 s timer, in between
                each(kv.Key, p.Snapshot(at), kv.Value);
                each(kv.Key + 0.5, p.Snapshot(at.AddSeconds(0.5)), null);
            }
            return p;
        }

        private static void FirstBake()
        {
            double last = -1;
            var seen = new HashSet<string>();
            bool partLeftSeen = false, stageLeftDuringGpu = false;
            DateTime end;
            var p = Run(null, (t, v, l) =>
            {
                Assert.True(v.Bar >= last - 1e-9, "the bar never moves back (" + last + " -> " + v.Bar + " at " + t + " s)");
                last = v.Bar;
                if (v.Line != null && v.Line.Contains("›")) seen.Add(v.Line.Split('›')[1].Split(':')[0].Split('·')[0].Trim());
                if (v.Line != null && v.Line.StartsWith("GPU bake", StringComparison.Ordinal) && v.PartLeft.HasValue) partLeftSeen = true;
                if (v.Line != null && v.Line.StartsWith("GPU bake", StringComparison.Ordinal) && v.StageLeft.HasValue) stageLeftDuringGpu = true;
            }, out end);
            Assert.True(seen.SetEquals(new[] { "photon lattice", "gather", "sun light", "surface lights", "compose" }), "the GPU bake's parts were all named: " + string.Join(", ", seen));
            Assert.True(partLeftSeen, "first bake: the running part's time left is shown");
            Assert.True(!stageLeftDuringGpu, "first bake: no whole-step time left for the GPU bake (the later parts have no time yet)");
            var d = p.Durations();
            foreach (var k in new[] { "Preflight", "Capture", "Derive", "GpuBake", "TagWriter", "Install", "GpuBake.lattice", "GpuBake.gather", "GpuBake.suns", "GpuBake.surface", "GpuBake.compose" })
                Assert.True(d.ContainsKey(k), "duration recorded: " + k);
            Assert.True(Math.Abs(d["Capture"] - 40) < 0.01 && Math.Abs(d["GpuBake.suns"] - 91) < 1.5, "durations: Capture " + d["Capture"] + ", suns " + d["GpuBake.suns"]);
            Assert.Eq(1.0, p.Snapshot(end).Bar, "the bar is full at the end");
        }

        private static void SecondBake()
        {
            DateTime end;
            var first = Run(null, (t, v, l) => { }, out end);
            string work = C != null && C.Sb != null ? C.Sb.Dir("progress_work") : Path.Combine(Path.GetTempPath(), "h2lm_progress_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string key = StageTimes.Key(false, @"scenarios\multi\x\x", "x", "super", "Standard", false);
            StageTimes.Save(work, key, first.Durations(), DateTime.UtcNow);
            var hist = StageTimes.Load(work, key);
            Assert.Eq(first.Durations().Count, hist.Count, "stage_times.json round trip");
            double total = (end - T0).TotalSeconds;
            double? firstLeft = null;
            int checks = 0;
            Run(hist, (t, v, l) =>
            {
                if (t >= total - 1) return;
                Assert.True(v.TotalLeft.HasValue, "with the last bake's times the total time left is known (" + t + " s: " + v.Line + ")");
                if (firstLeft == null) firstLeft = v.TotalLeft;
                double truth = total - t;
                // the replay is the same bake: the estimate is within 20 % (+5 s) of the truth everywhere
                Assert.True(Math.Abs(v.TotalLeft.Value - truth) <= 0.2 * truth + 5, "time left " + v.TotalLeft.Value.ToString("0") + " s vs the truth " + truth.ToString("0") + " s at " + t + " s (" + v.Line + ")");
                checks++;
            }, out end);
            Assert.True(checks > 20 && firstLeft.HasValue && Math.Abs(firstLeft.Value - total) < 0.2 * total, "checked " + checks + " points; the first estimate " + firstLeft + " s vs " + total + " s");
        }

        private static void MapBake()
        {
            var p = new BakeProgress(true, null, T0);
            Assert.Eq("MapRead", p.Stages[0].Key, "a .map bake starts with Read map");
            Assert.True(p.Stages.Select(s => s.Key).SequenceEqual(new[] { "MapRead", "MapCopy" }.Concat(Ui.StepChip.Names.Select(n => n.Key.ToString())).Concat(new[] { "MapWrite" })), "Read map, Copy tags, the steps, Write map");
            p.OnMapPhase("Reading the map (a Vista map is converted to its MCC form first: about half a minute) ...", T0);
            p.OnMapPhase("Reading the map's dependency subset and building the overlay ...", T0.AddSeconds(30));
            p.OnLine("  mapbake|   overlay: 4000 of 8000 kit tags copied (3 s)", T0.AddSeconds(40));
            var v = p.Snapshot(T0.AddSeconds(41));
            Assert.Eq(StageState.Done, v.States["MapRead"], "Read map done");
            Assert.Eq(StageState.Active, v.States["MapCopy"], "Copy tags running");
            Assert.Contains(v.Line, "4,000 of 8,000 files", "the copy's file count is shown");
            p.OnMapPhase("Baking (the ordinary GPU bake of the prepared tree, stage only) ...", T0.AddSeconds(45));
            p.OnPlan(new[] { StepId.Preflight, StepId.Capture, StepId.Derive, StepId.GpuBake, StepId.TagWriter });
            p.OnStep(StepId.Capture, T0.AddSeconds(50));
            p.OnMapPhase("Injecting the lightmap into a copy of the map and validating it ...", T0.AddSeconds(900));
            v = p.Snapshot(T0.AddSeconds(901));
            Assert.Eq(StageState.Active, v.States["MapWrite"], "Write map running");
            Assert.Eq(StageState.Done, v.States["Capture"], "the bake's steps are done");
            Assert.Eq(StageState.Skipped, v.States["Install"], "steps outside the plan are skipped");
            var parser = new ProgressParser();
            Assert.Eq(LineKind.Progress, parser.Feed("      PROGRESS| DIRECT: SUN 0 progress 5% (1 of 2) 10 ms", false).Kind, "a PROGRESS| line is LineKind.Progress");
            Assert.Eq(LineKind.Plain, parser.Feed("      SUNCULL: SUN 0 key 0", false).Kind, "an ordinary line is not");
        }
    }
}
