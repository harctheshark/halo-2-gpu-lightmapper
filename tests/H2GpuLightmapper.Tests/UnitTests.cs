using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Cli;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.Pipeline;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>In-process unit tests of the exe's own classes (options, CLI parsing, planner argv, capture record + reuse gate,
    /// tags fingerprint, history / ETA, map-query parsing).</summary>
    internal static class UnitTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("unit: option defaults reset every launch (Super / Standard / AO off 1.0 0.5 / new capture / install)", Defaults);
            yield return T("unit: option validation (ranges, Custom flag, AO in Probes, workers 149 / 60)", Validation);
            yield return T("unit: scenario normalisation + AO tag + custom chunk", Helpers);
            yield return T("unit: CLI parsing (strict: unknown option, missing value, --photons without custom)", CliParsing);
            yield return T("unit: gpu_bake argv = run_bake.ps1's production order (+ P2 custom last)", GpuBakeArgs);
            yield return T("unit: X1 GPU limits (VRAM part sizing, 24 GB = production argv, D3D11 cap on small cards) + probe-count limit + gpucheck VRAM + NormalizeRoot", X1Limits);
            yield return T("unit: capture record + reuse gate rules (crc, quality, P / MC, fixes, binaries, probes, tags)", ReuseGate);
            yield return T("unit: tags fingerprint excludes this BSP's outputs, Sapien auto-backups and the tool's temp_scenario scratch; diff lists changes", Fingerprint);
            yield return T("unit: after our install only the .scenario line is restamped; other changes keep warning", Restamp);
            yield return T("unit: history + Custom ETA scaling", HistoryEta);
            yield return T("unit: map-query result parsing", MapQueryParse);
            yield return T("unit: review fixes: on-disk case, install-without-layer flags, the undo time rule, --probes-fallback", ReviewFixes);
            yield return T("unit: V6 --install-retries: parsing (ranges, needs an install) + the Retry answers (N yes, then no; a cancel ends the wait)", InstallRetries);
            yield return T("unit: U3 the direct writer's pre-flight refusal is recognised only before any phase (not a later failure, a cancel, an exit 0)", DirectWriterCannotRun);
        }

        private static void DirectWriterCannotRun()
        {
            Func<int, bool, string[], string[], ProcOutcome> oc = (exit, cancelled, outLines, errLines) =>
            {
                var p = new ProgressParser();
                foreach (var l in outLines) p.Feed(l, false);
                foreach (var l in errLines) p.Feed(l, true);
                var o = new ProcOutcome { ExitCode = exit, Cancelled = cancelled, Parser = p };
                if (!o.Ok && !o.Cancelled) o.FailureReason = p.FailureReason();
                return o;
            };
            // pwsh's error block for gpu_bake's `throw $m` (run_bake's reason rule takes the "| message" line)
            var err = new[] { "Exception: C:\\x\\gpu_bake.ps1:702", "Line |", " 702 |          else { throw $m } } }", "     |                 ~~~~~~~~",
                              "     | -WriteBackMode direct cannot run: C:\\x\\v043-tagwriter\\lm_tagwriter.py" };
            Assert.True(BakeSession.DirectWriterCannotRun(oc(1, false, new[] { "fake gpu_bake: s / b" }, err)), "the pre-flight refusal before any phase");
            // a long message wrapped over two lines: the signature is in an earlier error line
            var wrapped = new[] { "Exception: x", "     | -WriteBackMode direct cannot run: a very long list of missing", "     | files and more" };
            Assert.True(BakeSession.DirectWriterCannotRun(oc(1, false, new string[0], wrapped)), "found in any error line");
            Assert.True(!BakeSession.DirectWriterCannotRun(oc(1, false, new[] { "[1] CAPTURE - x" }, err)), "a phase ran: not the pre-flight");
            Assert.True(!BakeSession.DirectWriterCannotRun(oc(0, false, new string[0], err)), "exit 0");
            Assert.True(!BakeSession.DirectWriterCannotRun(oc(1, true, new string[0], err)), "cancelled");
            Assert.True(!BakeSession.DirectWriterCannotRun(oc(1, false, new string[0], new[] { "Exception: x", "     | PHASE 3: something else" })), "another failure");
            Assert.True(!BakeSession.DirectWriterCannotRun(null), "no outcome");
        }

        private static void InstallRetries()
        {
            Func<string[], CliArgs> p = a => CliArgs.Parse(new[] { "--cli", "bake" }.Concat(a).ToArray(), 2,
                new HashSet<string> { "--h2ek", "--scenario", "--bsp", "--install-retries", "--install-retry-wait-s" }, new HashSet<string> { "--no-install", "--ao" });
            var a1 = p(new[] { "--h2ek", "x", "--scenario", "s", "--bsp", "b", "--ao", "--install-retries", "2", "--install-retry-wait-s", "5" });
            var o1 = CliMain.BakeOptionsFrom(a1);
            var r1 = CliMain.InstallRetryFrom(a1, o1);
            Assert.Eq(0, a1.Errors.Count, "valid: " + string.Join("; ", a1.Errors));
            Assert.Eq(2, r1.Retries, "retries");
            Assert.Eq(5, r1.WaitSeconds, "wait");
            var a0 = p(new[] { "--h2ek", "x", "--scenario", "s", "--bsp", "b" });
            var r0 = CliMain.InstallRetryFrom(a0, CliMain.BakeOptionsFrom(a0));
            Assert.Eq(0, r0.Retries, "default: no retry");
            Assert.Eq(CliMain.DefaultInstallRetryWaitS, r0.WaitSeconds, "default wait");
            foreach (var bad in new[]
            {
                new[] { "--install-retries", "21" }, new[] { "--install-retries", "-1" }, new[] { "--install-retries", "1", "--install-retry-wait-s", "0" },
                new[] { "--install-retry-wait-s", "5" }, new[] { "--install-retries", "1", "--no-install" },
            })
            {
                var ab = p(new[] { "--h2ek", "x", "--scenario", "s", "--bsp", "b" }.Concat(bad).ToArray());
                CliMain.InstallRetryFrom(ab, CliMain.BakeOptionsFrom(ab));
                Assert.True(ab.Errors.Count > 0, "refused: " + string.Join(" ", bad));
            }
            var pr = new Prompt { Kind = PromptKind.InstallRetry, Title = "The lightmap files are in use", Message = "INSTALL: REFUSED ...\n\nRetry the install?" };
            int slept = 0;
            var pol = new CliMain.InstallRetryPolicy { Retries = 2, WaitSeconds = 1, Sleep = t => { slept++; System.Threading.Thread.Sleep(5); } };
            Assert.True(pol.Answer(pr, () => false), "retry 1: yes");
            Assert.True(slept > 0, "it waited before retry 1");
            Assert.True(pol.Answer(pr, () => false), "retry 2: yes");
            Assert.True(!pol.Answer(pr, () => false), "retries used up: no");
            Assert.Eq(2, pol.Used, "used");
            var none = new CliMain.InstallRetryPolicy();
            Assert.True(!none.Answer(pr, () => false), "no flag: no");
            var pc = new CliMain.InstallRetryPolicy { Retries = 1, WaitSeconds = 3600, Sleep = t => { throw new InvalidOperationException("must not wait after a cancel"); } };
            Assert.True(pc.Answer(pr, () => true), "a cancel ends the wait at once (yes: BakeSession's ThrowIfCancelled then stops the run as CANCELLED)");
        }

        private static void ReviewFixes()
        {
            // finding 22: canonical case from the folder listing
            var sb = C.Sb.Dir("unit_case");
            string root = Path.Combine(sb, "H2");
            Directory.CreateDirectory(Path.Combine(root, @"tags\scenarios\Multi\cyclotron"));
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\Multi\cyclotron\Cyclotron.scenario"), "s");
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\Multi\cyclotron\cyclotron.scenario_structure_bsp"), "b");
            Assert.Eq(@"scenarios\Multi\cyclotron\Cyclotron", BakeOptions.CanonicalScenario(root, @"SCENARIOS\multi\CYCLOTRON\cyclotron"), "every segment in its on-disk case");
            Assert.Eq(null, BakeOptions.CanonicalScenario(root, @"scenarios\multi\nope\nope"), "missing -> null");
            Assert.Eq("cyclotron", BakeOptions.CanonicalBsp(Path.Combine(root, @"tags\scenarios\Multi\cyclotron"), "CYCLOTRON"), "BSP from the listing");
            // finding 9: each dropped layer needs its own flag
            var o = BakeOptions.Defaults();
            var both = new Prompt { Kind = PromptKind.InstallWithoutLayer, DropsAo = true, DropsProbes = true };
            Assert.True(!BakeSession.FlagsAllowInstallWithoutLayer(o, both), "no flags: no");
            o.AoFallback = AoFallback.InstallWithoutAo;
            Assert.True(!BakeSession.FlagsAllowInstallWithoutLayer(o, both), "--ao-fallback alone never drops the probes");
            Assert.True(BakeSession.FlagsAllowInstallWithoutLayer(o, new Prompt { Kind = PromptKind.InstallWithoutLayer, DropsAo = true }), "AO only: --ao-fallback is enough");
            o.ProbesFallback = AoFallback.InstallWithoutAo;
            Assert.True(BakeSession.FlagsAllowInstallWithoutLayer(o, both), "both flags: yes");
            Assert.Eq("--ao-fallback install + --probes-fallback install", BakeSession.FallbackFlagsText(both), "named");
            var a = P("--h2ek", "x", "--scenario", "s", "--bsp", "b", "--probes-fallback", "install");
            Assert.Eq(AoFallback.InstallWithoutAo, CliMain.BakeOptionsFrom(a).ProbesFallback, "--probes-fallback parsed");
            // finding 4: an install backup counts for a run only when its install happened at / after the run's start
            string bak = C.Sb.Dir("unit_undo_bak");
            var start = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(bak, "_install_manifest.json"), "{\"made\": \"" + start.ToLocalTime().AddMinutes(-30).ToString("yyyyMMdd_HHmmss") + "\"}");
            Assert.True(!CliMain.InstalledSince(bak, start), "an older install's backup (the shared OUT folder) does not count");
            File.WriteAllText(Path.Combine(bak, "_install_manifest.json"), "{\"made\": \"" + start.ToLocalTime().AddMinutes(5).ToString("yyyyMMdd_HHmmss") + "\"}");
            Assert.True(CliMain.InstalledSince(bak, start), "an install during the run counts");
        }

        private static void Defaults()
        {
            var o = BakeOptions.Defaults();
            Assert.Eq(Quality.Super, o.Quality, "quality");
            Assert.Eq(BakeMode.Standard, o.Mode, "mode");
            Assert.Eq(false, o.Ao, "ao");
            Assert.Eq(1.0, o.AoStrength, "strength");
            Assert.Eq(0.5, o.AoExponent, "exponent");
            Assert.Eq(50000000L, o.Photons, "photons");
            Assert.Eq(16, o.Rays, "rays");
            Assert.Eq(false, o.ReuseCapture, "reuse");
            Assert.Eq(false, o.StageOnly, "stage only");
            Assert.Eq(true, o.Fixes, "fixes always on");
            Assert.Eq("s10e05", o.AoTag, "default AO tag");
            Assert.Eq(512L, BakeOptions.RaysPerCall(16), "16 -> 512 rays per call");
            Assert.Eq(8192L, BakeOptions.RaysPerCall(64), "64 -> 8192");
            Assert.Eq(128L, BakeOptions.RaysPerCall(8), "super 128");
        }

        private static BakeOptions Ok() { var o = BakeOptions.Defaults(); o.Root = @"C:\x"; o.ScenarioRel = @"scenarios\multi\a\a"; o.Bsp = "a"; return o; }

        private static void Validation()
        {
            Assert.Eq(0, Ok().Validate(false).Count, "defaults valid");
            var c = Ok(); c.Quality = Quality.Custom;
            Assert.Contains(string.Join("|", c.Validate(false)), "N3 guarded fullgpu", "custom refused when the payload has no guarded engine");
            Assert.Eq(0, c.Validate(true).Count, "custom 50M/16 valid with the guarded engine");
            c.Photons = 999999; Assert.Contains(string.Join("|", c.Validate(true)), "photons", "photons low");
            c.Photons = 100000001; Assert.Contains(string.Join("|", c.Validate(true)), "photons", "photons high");
            c.Photons = 100000000; c.Rays = 65; Assert.Contains(string.Join("|", c.Validate(true)), "gather rays", "rays high");
            c.Rays = 0; Assert.Contains(string.Join("|", c.Validate(true)), "gather rays", "rays low");
            c.Rays = 64; Assert.Eq(0, c.Validate(true).Count, "100M / 64 valid");
            c.Photons = 1000000; c.Rays = 1; Assert.Eq(0, c.Validate(true).Count, "1M / 1 valid");
            var a = Ok(); a.Ao = true; a.AoStrength = 0; Assert.Contains(string.Join("|", a.Validate(false)), "strength", "strength 0");
            a.AoStrength = 2.0; a.AoExponent = 8.5; Assert.Contains(string.Join("|", a.Validate(false)), "exponent", "exponent 8.5");
            a.AoExponent = 0.1; Assert.Eq(0, a.Validate(false).Count, "2.0 / 0.1 valid");
            a.Mode = BakeMode.Probes; Assert.Contains(string.Join("|", a.Validate(false)), "Lightprobes", "AO refused in probes mode");
            var w = Ok(); w.Worker = 149; Assert.Contains(string.Join("|", w.Validate(false)), "149", "149 refused");
            w.Worker = 60; Assert.Contains(string.Join("|", w.Validate(false)), "200-299", "research worker refused");
            w.Worker = 210; w.ProbeWorker = 210; Assert.Contains(string.Join("|", w.Validate(false)), "differ", "same probe worker refused");
            var b = Ok(); b.Bsp = @"..\x"; Assert.True(b.Validate(false).Count > 0, "bsp with a path refused");
        }

        private static void Helpers()
        {
            Assert.Eq(@"scenarios\multi\cyclotron\cyclotron", BakeOptions.NormalizeScenario(@"C:\H2", "scenarios/multi/cyclotron/cyclotron.scenario"), "slashes + ext");
            Assert.Eq(@"scenarios\multi\cyclotron\cyclotron", BakeOptions.NormalizeScenario(@"C:\H2", @"C:\H2\tags\scenarios\multi\cyclotron\cyclotron.scenario"), "full path");
            Assert.Eq(null, BakeOptions.NormalizeScenario(@"C:\H2", @"C:\Other\tags\x\y.scenario"), "outside tags");
            Assert.Eq(null, BakeOptions.NormalizeScenario(@"C:\H2", @"scenarios\..\..\x"), "dot-dot");
            var o = BakeOptions.Defaults(); o.AoStrength = 0.75; o.AoExponent = 1.0;
            Assert.Eq("s075e10", o.AoTag, "0.75 / 1.0");
            o.AoStrength = 1.25; o.AoExponent = 0.5;
            Assert.Eq("s125e05", o.AoTag, "1.25 / 0.5 (the research tag)");
            Assert.Eq(4096, BakeOptions.CustomChunk(8), "MC 8 -> 4096 (= super's 16 8 4096)");
            Assert.Eq(1024, BakeOptions.CustomChunk(16), "MC 16 -> 1024");
            Assert.Eq(64, BakeOptions.CustomChunk(64), "MC 64 -> 64");
            Assert.Eq("super", Mk(Quality.Custom).AoTraceQuality, "custom traces at super");
            Assert.Eq("low", Mk(Quality.Low).AoTraceQuality, "low traces at low");
            Assert.True(BakeSession.PathTextProblem(@"C:\H2 {x}") != null, "braces refused");
            Assert.True(BakeSession.PathTextProblem(@"C:\H2 [x]") != null, "brackets refused");
            Assert.True(BakeSession.PathTextProblem("C:\\H2\u00e9") != null, "non-ASCII refused");
            Assert.Eq(null, BakeSession.PathTextProblem(@"C:\Steam Library\H2EK"), "plain path ok");
        }

        private static BakeOptions Mk(Quality q) { var o = BakeOptions.Defaults(); o.Quality = q; return o; }

        private static CliArgs P(params string[] a)
        {
            var args = new[] { "--cli", "bake" }.Concat(a).ToArray();
            var x = CliArgs.Parse(args, 2, new HashSet<string> { "--h2ek", "--scenario", "--bsp", "--quality", "--photons", "--rays", "--mode", "--ao-strength", "--ao-exponent", "--ao-fallback", "--probes-fallback", "--worker", "--probe-worker" },
                                  new HashSet<string> { "--ao", "--reuse-capture", "--no-install", "--yes", "--dev-no-fixes", "--reuse-unrecorded" });
            return x;
        }

        private static void CliParsing()
        {
            var a = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--quality", "custom", "--photons", "75,000,000", "--rays", "32", "--mode", "probes", "--no-install");
            var o = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "no errors: " + string.Join("; ", a.Errors));
            Assert.Eq(Quality.Custom, o.Quality, "custom");
            Assert.Eq(75000000L, o.Photons, "photons with separators");
            Assert.Eq(32, o.Rays, "rays");
            Assert.Eq(BakeMode.Probes, o.Mode, "probes");
            Assert.Eq(true, o.StageOnly, "--no-install");
            var b = P("--h2ek", "x", "--scenario", "s", "--bsp", "b", "--photons", "5");
            CliMain.BakeOptionsFrom(b);
            Assert.Contains(string.Join("|", b.Errors), "only valid with --quality custom", "--photons without custom");
            var c = P("--h2ek", "x", "--scenario", "s", "--bsp", "b", "--bogus");
            Assert.Contains(string.Join("|", c.Errors), "unknown option --bogus", "unknown option");
            var d = P("--h2ek");
            Assert.Contains(string.Join("|", d.Errors), "needs a value", "missing value");
            var e = P("--h2ek", "x", "--scenario", "s", "--bsp", "b", "--quality", "ultra");
            CliMain.BakeOptionsFrom(e);
            Assert.Contains(string.Join("|", e.Errors), "must be one of", "bad choice");
            var f = P("--h2ek", "x", "--scenario", "s", "--bsp", "b", "--ao-strength", "1.5");
            CliMain.BakeOptionsFrom(f);
            Assert.Contains(string.Join("|", f.Errors), "need --ao", "AO numbers without --ao");
            var g = P("--h2ek=x", "--scenario=s", "--bsp=b", "--ao", "--ao-strength=1.25", "--ao-exponent", "2");
            var go = CliMain.BakeOptionsFrom(g);
            Assert.Eq(0, g.Errors.Count, "--name=value form");
            Assert.Eq(1.25, go.AoStrength, "strength");
        }

        private static void GpuBakeArgs()
        {
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 200, true, false), "production install argv");
            o.Quality = Quality.Low;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "201", "-Quality", "low", "-SkipCapture", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 201, false, true), "low stage-only reuse argv");
            o.Quality = Quality.Custom; o.Photons = 20000000; o.Rays = 8;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "202", "-Quality", "custom", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "20000000", "-MCSamples", "8" },
                       BakePlanner.GpuBakeArgs(o, 202, true, false), "custom 20M/8 argv");
            o.Fixes = false; o.Quality = Quality.Super;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "203", "-Quality", "super", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 203, false, false), "dev no-fixes argv");
        }

        private static void X1Limits()
        {
            const long GB24 = 25310527488L;             // the RTX 4090's gpucheck figure (dedicated_video_memory)
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            // ---- 24 GB: nothing is passed, the argv is byte-for-byte the production one
            var big = GpuLimits.Compute(GB24, "t", 0);
            Assert.Eq(0L, big.MaxCallsPerPart, "24 GB: no -MaxCallsPerPart");
            Assert.Eq(0, big.BufCapMB, "24 GB: no -BufCapMB");
            Assert.True(!big.Changes, "24 GB changes nothing");
            foreach (bool wb in new[] { true, false })
                foreach (bool sc in new[] { true, false })
                    Assert.Seq(BakePlanner.GpuBakeArgs(o, 200, wb, sc), BakePlanner.GpuBakeArgs(o, 200, wb, sc, big), "24 GB argv == production (wb " + wb + ", skip " + sc + ")");
            Assert.True(GpuLimits.Compute(24L << 30, "t", 0).MaxCallsPerPart == 0, "a 24 GiB card (25.77 GB) too");
            Assert.True(GpuLimits.Compute(20L << 30, "t", 0).MaxCallsPerPart == 0 || GpuLimits.Compute(20L << 30, "t", 0).MaxCallsPerPart > 40000000L, "20 GiB: at most a mild cap");
            // ---- smaller cards: the rows fit LIMITS.md 8.2 (16 GB ~37 M, 12 GB ~26 M, 8 GB ~15 M) less the 10% margin
            long r16 = GpuLimits.Compute(16000000000L, "t", 0).MaxCallsPerPart, r12 = GpuLimits.Compute(12000000000L, "t", 0).MaxCallsPerPart, r8 = GpuLimits.Compute(8000000000L, "t", 0).MaxCallsPerPart;
            Assert.True(r16 > 30000000L && r16 < 37000000L, "16 GB rows " + r16);
            Assert.True(r12 > 21000000L && r12 < 26000000L, "12 GB rows " + r12);
            Assert.True(r8 > 11000000L && r8 < 15000000L, "8 GB rows " + r8);
            Assert.True(r16 > r12 && r12 > r8, "monotone");
            Assert.Eq(0, GpuLimits.Compute(8L << 30, "t", 0).BufCapMB, "an 8 GiB card: D3D11 allows 2048 MB > 1792, no -BufCapMB");
            // Custom: the photon budget is an upper bound of the stored photons -> fewer rows
            Assert.True(GpuLimits.Compute(16000000000L, "t", 100000000L).MaxCallsPerPart < r16, "Custom 100 M photons leaves fewer rows");
            // ---- under ~7 GiB: D3D11's documented per-resource limit (a quarter of the VRAM) is below 1792 MiB
            Assert.Eq(1536, GpuLimits.Compute(6L << 30, "t", 0).BufCapMB, "6 GiB: -BufCapMB 1536");
            Assert.Eq(1024, GpuLimits.Compute(4L << 30, "t", 0).BufCapMB, "4 GiB: -BufCapMB 1024");
            Assert.Eq(1792, GpuLimits.D3d11ResourceLimitMB(7L << 30), "7 GiB is exactly the default cap");
            Assert.Eq(2048, GpuLimits.D3d11ResourceLimitMB(24L << 30), "capped at 2048");
            var six = GpuLimits.Compute(6L << 30, "t", 0);
            var a6 = BakePlanner.GpuBakeArgs(o, 200, false, false, six);
            Assert.True(a6.IndexOf("-MaxCallsPerPart") > 0 && a6.IndexOf("-BufCapMB") > a6.IndexOf("-MaxCallsPerPart"), "both passed on a 6 GiB card: " + string.Join(" ", a6));
            Assert.Seq(BakePlanner.GpuBakeArgs(o, 200, false, false), a6.Take(a6.IndexOf("-MaxCallsPerPart")).ToList(), "the production argv is the prefix");
            // unknown / tiny / integrated: nothing is passed
            Assert.True(!GpuLimits.Compute(null, null, 0).Changes && !GpuLimits.Compute(512L << 20, "t", 0).Changes && !GpuLimits.Compute(0, "t", 0).Changes, "unknown / integrated: unchanged");
            // ---- light probes: tail rows 98,304 per point against the GQ buffer (68 B per row)
            Assert.Eq(27633061L, GpuLimits.CapRows(1792), "1,792 MiB / 68 B");
            Assert.Eq(281, GpuLimits.MaxProbePoints(1792), "281 points at the default cap (LIMITS.md: ~270-281)");
            Assert.Eq(642, GpuLimits.MaxProbePoints(4095), "~640 at -BufCapMB 4095");
            Assert.True(!GpuLimits.CheckProbes(11).Refuse && !GpuLimits.CheckProbes(11).Marginal, "11 points (triplicate) fine");
            Assert.True(!GpuLimits.CheckProbes(281).Refuse && GpuLimits.CheckProbes(281).Marginal, "281: allowed but marginal");
            Assert.True(GpuLimits.CheckProbes(282).Refuse && GpuLimits.CheckProbes(1024).Refuse, "282 / 1,024 refused");
            Assert.True(!GpuLimits.CheckProbes(270).Marginal && GpuLimits.CheckProbes(271).Marginal && !GpuLimits.CheckProbes(271).Refuse, "marginal band = the last 11 points (271-281)");
            Assert.Contains(GpuLimits.CheckProbes(300).Message, "at most 281", "message names the limit");
            // ---- gpucheck JSON: the default adapter's dedicated memory (never the software adapter)
            string js = "{\"adapters\":[{\"luid\":\"0:1\",\"software\":true,\"dedicated_video_memory\":0},{\"luid\":\"0:2\",\"software\":false,\"dedicated_video_memory\":" + GB24
                        + "},{\"luid\":\"0:3\",\"software\":false,\"dedicated_video_memory\":134217728}],\"default_adapter\":{\"luid\":\"0:2\",\"name\":\"X\",\"feature_level\":\"12_1\"},\"verdict\":{\"ok\":true}}";
            var gc = Core.Preflight.GpuCheck.Parse(js, 0);
            Assert.Eq((long?)GB24, gc.DedicatedVideoMemoryBytes, "the default adapter's VRAM");
            Assert.Eq((long?)134217728L, Core.Preflight.GpuCheck.Parse(js.Replace("\"luid\":\"0:2\",\"name\"", "\"luid\":\"0:3\",\"name\""), 0).DedicatedVideoMemoryBytes, "matched by luid, not by position");
            Assert.Eq((long?)null, Core.Preflight.GpuCheck.Parse("{\"verdict\":{\"ok\":true}}", 0).DedicatedVideoMemoryBytes, "absent -> null");
            // ---- roots: a drive root keeps its backslash, every other root loses it
            Assert.Eq(@"D:\", Core.Payload.ShellPaths.NormalizeRoot(@"D:\"), "D:\\ stays");
            Assert.Eq(@"D:\", Core.Payload.ShellPaths.NormalizeRoot("D:/"), "D:/ -> D:\\");
            Assert.Eq(@"D:\H2EK", Core.Payload.ShellPaths.NormalizeRoot(@"D:\H2EK\"), "a folder loses its trailing backslash");
            Assert.Eq(@"D:\GPU Lightmap Work", Core.Payload.ShellPaths.WorkOf(@"D:\"), "WORK of a drive root is the drive root plus GPU Lightmap Work");
            Assert.Eq(@"D:\H2EK\GPU Lightmap Work", Core.Payload.ShellPaths.WorkOf(@"D:\H2EK\"), "WORK of a folder");
            Assert.Eq(@"D:\tags\scenarios\x", Path.Combine(Core.Payload.ShellPaths.NormalizeRoot(@"D:\"), "tags", @"scenarios\x"), "joins never give 'D:tags'");
            // the scenario normaliser strips the tags prefix at a drive root too
            Assert.Eq(@"scenarios\multi\cyclotron\cyclotron", BakeOptions.NormalizeScenario(@"D:\", @"D:\tags\scenarios\multi\cyclotron\cyclotron.scenario"), "NormalizeScenario at a drive root");
        }

        private static CaptureIdentity Id(string q = "super", long? p = null, int? mc = null, bool sky = false)
        {
            var id = new CaptureIdentity { Quality = q, Photons = p, Mc = mc, SkyHi = sky, Plucker = true, Scenery = true, AppVersion = "t" };
            id.Binaries["estimatorDll"] = "aa"; id.Binaries["fullgpu"] = "bb"; id.Binaries["gpuBake"] = "cc"; id.Binaries["toolFast"] = "dd"; id.Binaries["boundsDb"] = "ee";
            return id;
        }

        private static void ReuseGate()
        {
            var sb = C.Sb.Dir("unit_reuse");
            string root = Path.Combine(sb, "H2");
            Directory.CreateDirectory(Path.Combine(root, @"tags\scenarios\m\m"));
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\m\m\m.scenario"), "s");
            File.WriteAllText(Path.Combine(root, @"tags\other.tag"), "o");
            var set = TagSet.For(root, @"scenarios\m\m\m", "m");
            string farm = Path.Combine(sb, "farm_w200");
            Func<TagsFingerprint> tags = () => TagsFingerprint.Compute(root, set);
            var v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.Contains(v.Reason, "no capture yet", "no farm");
            Directory.CreateDirectory(farm);
            File.WriteAllText(Path.Combine(farm, "capture_id.json"), "{\"soup_crc32\": \"ABCD1234\"}");
            v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.Contains(v.Reason, "no standalone capture record", "legacy farm never offered");
            v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags, true);
            Assert.True(v.Allowed && v.Unrecorded, "dev --reuse-unrecorded");
            Assert.Eq(null, CaptureRecord.Write(farm, Id(), @"scenarios\m\m\m", "m", 200, "run1", tags(), DateTime.Now), "record written");
            v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.True(v.Allowed && !v.TagsChanged, "matching record: " + v.Reason);
            // V5 (gate T4 finding): an older record whose fingerprint still lists the tool's scratch file is not "tags changed"
            var oldStyle = TagsFingerprint.FromLinesPublic(tags().Lines.Concat(new[] { "temp_scenario\\sapien_temp_scenario_0.scenario\t5\t1" })
                                                                      .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList());
            Assert.Eq(null, CaptureRecord.Write(farm, Id(), @"scenarios\m\m\m", "m", 200, "run1b", oldStyle, DateTime.Now), "an older-style record");
            v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.True(v.Allowed && !v.TagsChanged, "only the tool's scratch line differs -> no confirmation: " + string.Join("|", v.ChangedTags));
            Assert.Contains(CaptureRecord.Evaluate(farm, Id("low"), @"scenarios\m\m\m", "m", false, tags).Reason, "this bake is low", "quality mismatch");
            Assert.Contains(CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "x", false, tags).Reason, "holds a capture of", "other bsp");
            var other = Id(); other.Binaries["fullgpu"] = "zz";
            Assert.Contains(CaptureRecord.Evaluate(farm, other, @"scenarios\m\m\m", "m", false, tags).Reason, "fullgpu", "binary mismatch named");
            var nofix = Id(); nofix.Plucker = false;
            Assert.Contains(CaptureRecord.Evaluate(farm, nofix, @"scenarios\m\m\m", "m", false, tags).Reason, "fixes", "fixes mismatch");
            Assert.Contains(CaptureRecord.Evaluate(farm, Id(sky: true), @"scenarios\m\m\m", "m", true, tags).Reason, "not made for light probes", "not probe-ready");
            // custom P / MC
            Assert.Eq(null, CaptureRecord.Write(farm, Id("custom", 50000000, 16), @"scenarios\m\m\m", "m", 200, "run2", tags(), DateTime.Now), "custom record");
            Assert.True(CaptureRecord.Evaluate(farm, Id("custom", 50000000, 16), @"scenarios\m\m\m", "m", false, tags).Allowed, "custom same P/MC");
            Assert.Contains(CaptureRecord.Evaluate(farm, Id("custom", 50000000, 32), @"scenarios\m\m\m", "m", false, tags).Reason, "MC 32", "custom other MC");
            Assert.Contains(CaptureRecord.Evaluate(farm, Id("custom", 60000000, 16), @"scenarios\m\m\m", "m", false, tags).Reason, "60,000,000", "custom other P");
            // crc changed underneath
            File.WriteAllText(Path.Combine(farm, "capture_id.json"), "{\"soup_crc32\": \"FFFF0000\"}");
            Assert.Contains(CaptureRecord.Evaluate(farm, Id("custom", 50000000, 16), @"scenarios\m\m\m", "m", false, tags).Reason, "changed after it was recorded", "crc mismatch");
            // probes record + tags change -> allowed but needs confirmation
            File.WriteAllText(Path.Combine(farm, "sky_tex_hi.bin"), "s"); File.WriteAllText(Path.Combine(farm, "lp_globals.bin"), "g");
            Assert.Eq(null, CaptureRecord.Write(farm, Id(sky: true), @"scenarios\m\m\m", "m", 200, "run3", tags(), DateTime.Now), "probe record");
            Assert.True(CaptureRecord.Evaluate(farm, Id(sky: true), @"scenarios\m\m\m", "m", true, tags).Allowed, "probe-ready");
            File.WriteAllText(Path.Combine(root, @"tags\other.tag"), "o changed");
            File.WriteAllText(Path.Combine(root, @"tags\new.tag"), "n");
            v = CaptureRecord.Evaluate(farm, Id(sky: true), @"scenarios\m\m\m", "m", true, tags);
            Assert.True(v.Allowed && v.TagsChanged, "tags changed: still allowed, needs confirmation");
            Assert.Eq(2, v.ChangedTagsTotal, "2 changed");
            Assert.Contains(string.Join("|", v.ChangedTags), "changed  other.tag", "changed listed");
            Assert.Contains(string.Join("|", v.ChangedTags), "added    new.tag", "added listed");
            // superseded aside, never deleted
            var moved = CaptureRecord.SupersedeExisting(farm, DateTime.Now);
            Assert.Eq(2, moved.Count, "record + tsv renamed aside");
            Assert.True(!File.Exists(CaptureRecord.PathIn(farm)) && moved.All(File.Exists), "renamed, not deleted");
        }

        private static void Fingerprint()
        {
            var sb = C.Sb.Dir("unit_fp");
            string root = Path.Combine(sb, "H2");
            string d = Path.Combine(root, @"tags\scenarios\m\m");
            Directory.CreateDirectory(d);
            foreach (var n in new[] { "m.scenario", "m_m_lightmap.scenario_structure_lightmap", "m_m_lightmap_bitmaps.bitmap", "m_m_lightmap.scenario_structure_lightmap.gpulm_replaced_1",
                                      "m_x_lightmap.scenario_structure_lightmap", "m (auto-backup thu oct 8).scenario" })
                File.WriteAllText(Path.Combine(d, n), n);
            var set = TagSet.For(root, @"scenarios\m\m\m", "m");
            var fp = TagsFingerprint.Compute(root, set);
            var names = fp.Lines.Select(l => l.Split('\t')[0]).ToList();
            Assert.Seq(new[] { @"scenarios\m\m\m.scenario", @"scenarios\m\m\m_x_lightmap.scenario_structure_lightmap" }, names, "only the scenario + the OTHER bsp's lightmap");
            File.WriteAllText(Path.Combine(d, "m_m_lightmap_bitmaps.bitmap"), "rebaked");
            Assert.Eq(fp.Md5, TagsFingerprint.Compute(root, set).Md5, "this BSP's own outputs never change the fingerprint");
            File.WriteAllText(Path.Combine(d, "m.scenario"), "edited");
            Assert.True(fp.Md5 != TagsFingerprint.Compute(root, set).Md5, "a scenario edit does");
            // V5 (gate T4 finding): tags\temp_scenario (the tool's scratch; every capture writes sapien_temp_scenario_0.scenario
            // there) is never fingerprinted -- only that TOP-level folder
            string t0 = TagsFingerprint.Compute(root, set).Md5;
            Directory.CreateDirectory(Path.Combine(root, @"tags\temp_scenario\scenarios"));
            File.WriteAllText(Path.Combine(root, @"tags\temp_scenario\sapien_temp_scenario_0.scenario"), "scratch");
            File.WriteAllText(Path.Combine(root, @"tags\temp_scenario\scenarios\x.scenario"), "scratch 2");
            Assert.Eq(t0, TagsFingerprint.Compute(root, set).Md5, "tags\\temp_scenario is not fingerprinted");
            Directory.CreateDirectory(Path.Combine(root, @"tags\scenarios\temp_scenario"));
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\temp_scenario\x.tag"), "a real folder of the same name deeper down");
            Assert.True(t0 != TagsFingerprint.Compute(root, set).Md5, "only the top-level temp_scenario is skipped");
            Assert.True(TagsFingerprint.IsToolScratch("temp_scenario\\a.scenario\t1\t2") && !TagsFingerprint.IsToolScratch("scenarios\\temp_scenario\\x.tag"), "IsToolScratch");
        }

        private static void Restamp()
        {
            var sb = C.Sb.Dir("unit_restamp");
            string root = Path.Combine(sb, "H2");
            string d = Path.Combine(root, @"tags\scenarios\m\m");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "m.scenario"), "s");
            File.WriteAllText(Path.Combine(root, @"tags\other.tag"), "o");
            var set = TagSet.For(root, @"scenarios\m\m\m", "m");
            string farm = Path.Combine(sb, "farm_w200");
            Directory.CreateDirectory(farm);
            File.WriteAllText(Path.Combine(farm, "capture_id.json"), "{\"soup_crc32\": \"ABCD1234\"}");
            Func<TagsFingerprint> tags = () => TagsFingerprint.Compute(root, set);
            Assert.Eq(null, CaptureRecord.Write(farm, Id(), @"scenarios\m\m\m", "m", 200, "r", tags(), DateTime.Now), "record");
            File.WriteAllText(Path.Combine(d, "m.scenario"), "s + the lightmap reference our install added");
            File.SetLastWriteTimeUtc(Path.Combine(d, "m.scenario"), DateTime.UtcNow.AddMinutes(1));
            Assert.True(CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags).TagsChanged, "the scenario edit shows");
            Assert.True(CaptureRecord.RestampFiles(farm, root, new[] { @"scenarios\m\m\m.scenario" }), "restamped");
            var v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.True(v.Allowed && !v.TagsChanged, "our own install no longer reads as a change: " + string.Join("; ", v.ChangedTags));
            Assert.Eq(tags().Md5, CaptureRecord.Read(farm).Obj("tags").Str("md5"), "restamped md5 == a fresh fingerprint");
            File.WriteAllText(Path.Combine(root, @"tags\other.tag"), "o changed by the user");
            Assert.True(!CaptureRecord.RestampFiles(farm, root, new[] { @"scenarios\m\m\m.scenario" }), "nothing to restamp for the scenario");
            v = CaptureRecord.Evaluate(farm, Id(), @"scenarios\m\m\m", "m", false, tags);
            Assert.True(v.TagsChanged && v.ChangedTags.Single().Contains("other.tag"), "a user change keeps warning: " + string.Join("; ", v.ChangedTags));
        }

        private static void HistoryEta()
        {
            var h = new List<HistoryEntry>
            {
                new HistoryEntry { Scenario = "s", Bsp = "b", Quality = "super", State = "DONE", StartedUtc = DateTime.UtcNow.AddDays(-1), Seconds = 1000,
                                   Steps = new Dictionary<string, double> { { "Capture", 100 }, { "GpuBake", 600 } } },
                new HistoryEntry { Scenario = "s", Bsp = "b", Quality = "super", State = "DONE", Reuse = true, StartedUtc = DateTime.UtcNow, Seconds = 50 },
            };
            string basis;
            var eta = History.CustomEta(h, "s", "b", 40000000, 16, out basis);
            // capture 100 * 2 + bake 600 * 4 + rest 300 = 2900
            Assert.True(eta.HasValue && Math.Abs(eta.Value - 2900) < 1e-6, "ETA " + eta);
            Assert.Contains(basis, "scaled from the Super run", "basis");
            Assert.True(History.CustomEta(h, "s", "other", 1, 1, out basis) == null, "no super run -> unknown");
            Assert.Eq(50.0, History.PlainEta(h, "s", "b", "super").Value, "plain ETA = last finished");
        }

        private static void MapQueryParse()
        {
            var mi = MapQuery.Parse(new[] { "noise", "MAPQUERY {\"bsps\": [\"a\", \"b\"], \"points\": 11, \"pointsError\": null, \"ltmpExists\": true, \"envs\": 11, \"groups\": 1, \"envsError\": null}" }, 0, "");
            Assert.True(mi.Ok, "ok");
            Assert.Eq(11, mi.Points.Value, "points");
            Assert.Eq(11, mi.Envs.Value, "envs");
            Assert.Seq(new[] { "a", "b" }, mi.Bsps, "bsps");
            var bad = MapQuery.Parse(new[] { "Traceback..." }, 1, "boom");
            Assert.True(!bad.Ok && bad.Error.Contains("boom"), "no result line");
            Assert.True(MapQuery.Code.IndexOf('"') < 0, "the snippet has no double quote (argv-safe)");
        }
    }
}
