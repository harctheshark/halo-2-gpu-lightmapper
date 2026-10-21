using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// End-to-end runs of the exe's --cli bake / restore / doctor / cancel / status on SANDBOX H2EK roots, with the test payload:
    /// fake gpu_bake / gpu_ao / gpu_lightprobes / sh_stage (the real interfaces, no GPU, no tool), the REAL portable pwsh 7.6.6,
    /// the REAL Python bundle (map query over the real lmw_tagio) and the REAL lmw_install.py installing into the sandbox tags.
    /// </summary>
    internal static class E2eTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("e2e 0.6.4: cross-folder BSP: a scenario whose BSP lives in another folder bakes with an engine that resolves it (plain -Bsp name, lmw_validate gets the referenced tag, the lightmap lands beside the scenario), AO / probes refused, an older engine refused before gpu_bake starts, a copy beside the scenario is ignored", CrossFolderBsp);
            yield return T("e2e 0.6.4: the engine's silent write-back fallback becomes a visible warning in the log and the run summary (pre-flight switch to tool, writer failed + shell fallback), once per run; a clean bake says nothing", FallbackWarning);
            yield return T("e2e: Lightmap Only install = backup + stage-only gpu_bake + the shell's install (real lmw_install, [4b]'s command) + verify + capture record + history", LightmapInstall);
            yield return T("e2e: capture reuse: record -> -SkipCapture; changed tags need --yes; other quality refused", Reuse);
            yield return T("e2e: Lightmap Only + AO: trace / apply / stage, the shell's install; Undo last install restores", AoInstall);
            yield return T("e2e: AO on a map that fell back to the tool writer: stops (exit 3) or installs without AO on request", AoFallback);
            yield return T("e2e: Standard on triplicate: real map query (11 points), probe worker SKY_HI capture, merge, one install", StandardProbes);
            yield return T("e2e: Lightprobes only on triplicate: ltmp-only backup + install; refused on cyclotron (no points)", ProbesOnly);
            yield return T("e2e: cancel from another process during phase 3 -> exit 4, lock released, nothing installed", CancelPhase3);
            yield return T("e2e: cancel during the fallback's [4b] install is deferred: the install completes, is VERIFIED, DONE with the cancel noted", CancelDeferred);
            yield return T("e2e: a failing bake -> exit 3 with gpu_bake's reason; the completed capture is still recorded", FailBake);
            yield return T("e2e: W2 --responsive + --exact-vertex-lighting both reach gpu_bake (bound, in the log), the stage is written, the run folder has the short name; unticked binds neither", W2BothOptions);
            yield return T("e2e: --scenery-permutation-fix reaches gpu_bake (-SceneryOwnPermutationOnly bound, in the log header); an engine without the parameter refuses the bake before gpu_bake is ever started; captures are reusable only with the same setting", SceneryPermBake);
            yield return T("e2e: DF64 Compatibility mode: nothing is passed by default; --force-compat puts GPULM_COMPAT=1 in gpu_bake's (and the AO trace's) environment only; the log header, the engine's own line and the run summary state the precision; an engine without the mode refuses --force-compat before gpu_bake starts", Df64Bake);
            yield return T("e2e: X2 --responsive reaches gpu_bake (-Responsive bound; log says 'responsive on'); a device-loss line on a failed bake -> the plain TDR explanation", X2ResponsiveAndTdr);
            yield return T("e2e: Restore previous lightmap; an unfinished install refuses bakes until --recover undoes it", RestoreRecover);
            yield return T("e2e: another bake holds the GPU lock -> WAITING, then gives up (exit 3); the foreign lock untouched", LockWait);
            yield return T("e2e: doctor / status / cancel verbs; real preflight refuses a root without tool_fast (exit 2)", DoctorStatus);
            yield return T("fix e2e: probes gated on T4: Standard on triplicate refused (nothing written), Lightmap Only bakes there", ProbesGated);
            yield return T("fix e2e: restore / undo / --recover refused while a bake is live or the GPU lock is held", RestoreGuarded);
            yield return T("fix e2e: undo + restore after a FAILED bake: the failed run is skipped, the pre-install state comes back", UndoSkipsFailed);
            yield return T("fix e2e: a tool write-back marker refuses only its own BSP; --recover restores that set and clears it", ToolMarkerScoped);
            yield return T("fix e2e: install without the layers on a probe BSP needs --probes-fallback (probes reset named)", ProbesFallback);
            yield return T("fix e2e: --dev-skip-preflight still refuses an unsupported tool_fast; cleanup-alias verb", SkipPreflightTool);
            yield return T("V6 e2e: the ltmp held (share 0) at the shell's install -> exit 2 -> no retry without the flag (exit 3, nothing changed); --install-retries -> retried and installed", AoInstallHeld);
            yield return T("R1 e2e: stage-only Lightmap Only whose direct writer made no stage (or only an older one) -> FAILED (exit 3), never DONE", StageOnlyNoStage);
            yield return T("R1 e2e: a lightmap file held open at bake start -> refused (exit 2) with the Sapien / Guerilla message, nothing written", HeldAtStart);
            yield return T("R1 e2e: a tool write-back killed before the tool saved: --recover proves the set unchanged, sets the marker aside, restores nothing", ToolMarkerUnchanged);
            yield return T("U3 e2e: Lightmap Only: the ltmp held at the shell's install -> exit 2 -> no retry without the flag (exit 3, nothing changed, stage kept); --install-retries -> retried and installed", LightmapInstallHeld);
            yield return T("U3 e2e: Lightmap Only + Custom whose direct writer made no stage -> the production tool write-back fallback (-SkipCapture -WriteBack + the custom row) installs; Verify notes it", ToolFallbackCustom);
            yield return T("U3 e2e: Standard (no probes): the shell installs too; a fallback whose own direct writer works installs by its [4b], verified against that stage", StandardFallbackDirect);
            yield return T("U3 e2e: the direct writer cannot run at all (gpu_bake's pre-flight) -> the full production -WriteBack argv (tool write-back), capture recorded; stage-only -> FAILED", NoDirectWriter);
        }

        /// <summary>U3 (a): V6 finding H2 -- a lightmap file opened DURING the bake used to end the default path FAILED after the
        /// whole bake ([4b] inside gpu_bake has no retry). Now the shell installs, so the same Retry as the AO path applies.</summary>
        private static void LightmapInstallHeld()
        {
            foreach (bool withRetry in new[] { false, true })
            {
                var r = C.NewRoot();
                var orig = SetMd5(r.CycDir, CycPrefix);
                r.Control("gpu_bake_wait\n");
                var args = new List<string> { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" };
                if (withRetry) args.AddRange(new[] { "--install-retries", "1", "--install-retry-wait-s", "3" });
                var t = BakeAsync(r, args.ToArray());
                var sw = Stopwatch.StartNew();
                string waiting = Path.Combine(r.Work, "fake_gpu_bake_waiting.flag");
                while (!File.Exists(waiting) && !t.IsCompleted && sw.ElapsedMilliseconds < 120000) Thread.Sleep(100);
                if (!File.Exists(waiting)) throw new TestFailure("the stage-only bake never paused: " + (t.IsCompleted ? t.Result.ToString() : "timeout"));
                string run = CurrentRun.Read(r.Work).RunDir;
                using (var hold = new FileStream(r.CycLtmp, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    File.WriteAllText(Path.Combine(r.Work, "fake_gpu_bake_go.flag"), "go");
                    if (withRetry)
                    {
                        sw.Restart();
                        while (!t.IsCompleted && sw.ElapsedMilliseconds < 120000 &&
                               !(File.Exists(Path.Combine(run, "bake.log")) && Json.ReadAllTextShared(Path.Combine(run, "bake.log")).Contains("INSTALL: REFUSED"))) Thread.Sleep(100);
                        Assert.True(!t.IsCompleted, "the run waits for the retry while the file is held");
                    }
                    else { t.Wait(180000); }
                }
                var res = t.Result;
                string log = File.ReadAllText(Path.Combine(run, "bake.log"));
                string stageLtmp = Path.Combine(r.Farm(200), "tagw_stage", CycPrefix + ".scenario_structure_lightmap");
                Assert.Contains(log, "INSTALL: REFUSED: target file(s) in use", "lmw_install refused the held file (exit 2)");
                Assert.True(!File.Exists(Path.Combine(r.Farm(200), "writeback_in_progress.txt")), "no marker left");
                Assert.True(!r.Calls("gpu_bake").Single().Arr("argv").Cast<string>().Contains("-WriteBack"), "ONE stage-only gpu_bake (no [4b] inside it)");
                if (!withRetry)
                {
                    Assert.Eq(3, res.Exit, "no retry without the flag: " + res);
                    Assert.Contains(res.Out, "=== answer: no (the CLI retries only with --install-retries N)", "the CLI said no");
                    Assert.Contains(res.Out, "the install was refused before any change", "reason");
                    foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
                    Assert.True(File.Exists(stageLtmp), "the stage is kept");
                    Assert.Eq(null, RunStatus.Load(run).InstallBackupDir, "no install backup recorded (nothing changed)");
                }
                else
                {
                    Assert.Eq(0, res.Exit, "retried and installed: " + res);
                    Assert.Contains(res.Out, "retry 1 of 1 in 3 s", "the CLI retried once");
                    Assert.Contains(log, "installed and verified", "the retry installed");
                    foreach (var n in new[] { CycPrefix + ".scenario_structure_lightmap", CycPrefix + "_bitmaps.bitmap", CycPrefix + "_truecolor_bitmaps.bitmap" })
                        Assert.Eq(Md5(Path.Combine(r.Farm(200), "tagw_stage", n)), Md5(Path.Combine(r.CycDir, n)), "the direct writer's stage is installed: " + n);
                    Assert.Eq(Path.Combine(r.Farm(200), "tag_backup_before_writeback"), RunStatus.Load(run).InstallBackupDir, "[4b]'s backup dir recorded");
                    // Undo last install restores the pre-bake set from that backup
                    var l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--undo-last-install" });
                    var u = C.Run(l);
                    Assert.Eq(0, u.Exit, "undo: " + u);
                    foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "undone " + kv.Key);
                }
            }
        }

        /// <summary>U3 (a) + (b): no 'written' stage in the stage-only bake -> the production tool write-back fallback on the same
        /// capture, with the Custom parameters (gpu_bake P2 hands them to osoy_launch --quality-row P MC in the write-back).</summary>
        private static void ToolFallbackCustom()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=nostage\n");
            var res = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "custom", "--photons", "20,000,000", "--rays", "8",
                                "--dev-custom-unguarded"), "custom install through the tool fallback");
            string run = RunDirOf(res);
            var calls = r.Calls("gpu_bake");
            Assert.Eq(2, calls.Count, "the stage-only bake + the production write-back");
            Func<JObj, List<string>> tail = c => { var a = c.Arr("argv").Cast<string>().ToList(); return a.Skip(a.FindIndex(x => x.EndsWith("gpu_bake.ps1", StringComparison.OrdinalIgnoreCase)) + 1).ToList(); };
            Assert.Seq(new[] { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "custom", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "20000000", "-MCSamples", "8" },
                       tail(calls[0]), "1: stage-only, fresh capture");
            Assert.Seq(new[] { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "custom", "-WriteBack", "-SkipCapture", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct", "-Photons", "20000000", "-MCSamples", "8" },
                       tail(calls[1]), "2: the production -WriteBack argv on this capture, WITH the custom row");
            Assert.Contains(Bytes(r.CycLtmp), "TOOL WRITE-BACK " + CycPrefix + ".scenario_structure_lightmap custom quality-row 20000000 8", "the (fake) tool write-back got the custom row");
            string log = File.ReadAllText(Path.Combine(run, "bake.log"));
            Assert.Contains(log, "the direct tag writer made no stage: the production tool write-back fallback runs", "the fallback is announced");
            Assert.Contains(log, "no direct-writer stage of this run to compare it with", "Verify says what it could not compare");
            Assert.Contains(res.Out, "installed by the production tool write-back fallback", "the result says how it was installed");
            Assert.NotContains(log, "start lmw_install install", "the shell's own install did not run (gpu_bake installed)");
            var st = RunStatus.Load(run);
            Assert.True(st.State == RunState.Done && st.Extra.Bool("installed") == true, "DONE, installed");
            Assert.Eq(Path.Combine(r.Farm(200), "tag_backup_before_writeback"), st.InstallBackupDir, "the write-back's backup recorded once [4] started");
            Assert.True(File.Exists(Path.Combine(r.Farm(200), "standalone_capture.json")), "the stage-only bake's capture was recorded");
            Assert.Eq(orig[CycPrefix + "_radiance_bitmaps_0.bitmap"], Md5(Path.Combine(r.CycDir, CycPrefix + "_radiance_bitmaps_0.bitmap")), "radiance untouched");
        }

        /// <summary>U3: Standard on a BSP without probe points takes the same path; when the stage-only bake made no stage but the
        /// fallback's own [4a] works, its [4b] installs and Verify compares the live set with THAT (this run's) stage.</summary>
        private static void StandardFallbackDirect()
        {
            var r = C.NewRoot();
            r.Control("gpu_bake=nostage_once\n");
            var res = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron"), "standard: fallback installs by its [4b]");
            string log = File.ReadAllText(Path.Combine(RunDirOf(res), "bake.log"));
            Assert.Contains(log, "map: 0 probe point(s)", "the map query ran (Standard)");
            Assert.Eq(2, r.Calls("gpu_bake").Count, "stage-only + the fallback");
            Assert.Contains(log, "[4b] INSTALL", "the fallback's [4b] installed");
            Assert.Contains(log, "verified: 3 installed file(s) md5-equal to the stage", "verified against this run's stage");
            Assert.Eq(Md5(Path.Combine(r.Farm(200), "tagw_stage", CycPrefix + ".scenario_structure_lightmap")), Md5(r.CycLtmp), "the fallback's stage is in");
            // and the ordinary Standard install (stage ready) is the shell's
            r.Control("gpu_bake=ok\n");
            var ok = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron"), "standard: the shell installs");
            string log2 = File.ReadAllText(Path.Combine(RunDirOf(ok), "bake.log"));
            Assert.Contains(log2, "the shell installs it", "stage ready -> the shell's install");
            Assert.NotContains(log2, "[4b] INSTALL", "no [4b] inside gpu_bake");
            Assert.True(!r.Calls("gpu_bake").Last().Arr("argv").Cast<string>().Contains("-WriteBack"), "stage-only");
        }

        /// <summary>U3: gpu_bake.ps1 refuses a stage-only bake up front when the direct writer cannot run at all ("-WriteBackMode
        /// direct cannot run", no phase started); with -WriteBack production switches to the tool write-back there.</summary>
        private static void NoDirectWriter()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=nodirect\n");
            var so = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Assert.Eq(3, so.Exit, "stage only: FAILED (nothing to fall back to): " + so);
            Assert.Contains(so.Out, "-WriteBackMode direct cannot run", "gpu_bake's reason");
            Assert.Eq(1, r.Calls("gpu_bake").Count, "no fallback in stage-only mode");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
            var res = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "install: the production argv");
            var calls = r.Calls("gpu_bake");
            Assert.Eq(3, calls.Count, "the refused stage-only bake + the production argv");
            var last = calls.Last().Arr("argv").Cast<string>().ToList();
            Assert.True(last.Contains("-WriteBack") && !last.Contains("-SkipCapture"), "the FULL production argv (nothing was captured yet): " + string.Join(" ", last));
            Assert.Contains(Bytes(r.CycLtmp), "TOOL WRITE-BACK", "the (fake) tool write-back wrote it");
            Assert.Contains(res.Out, "installed by the production -WriteBack argv (the direct tag writer cannot run", "reason");
            var rec = Json.ReadObjFile(Path.Combine(r.Farm(200), "standalone_capture.json"));
            Assert.Eq(Json.ReadObjFile(Path.Combine(r.Farm(200), "capture_id.json")).Str("soup_crc32"), rec.Obj("captureId").Str("soup_crc32"), "the fallback's capture was recorded");
        }

        /// <summary>R1 fix (V4 / V6 / V7 "false DONE"): gpu_bake exits 0 in a stage-only bake whose [4a] tag writer failed.</summary>
        private static void StageOnlyNoStage()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=nostage\n");
            var x = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Assert.Eq(3, x.Exit, "no stage: FAILED, not DONE: " + x);
            Assert.Contains(x.Out, "the direct tag writer did not produce a stage", "reason");
            Assert.NotContains(x.Out, "RESULT DONE", "never DONE");
            // an OLDER 'written' report in the same farm is not this run's stage
            r.Control("gpu_bake=ok\n");
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install"), "a good stage-only bake");
            string rep = Path.Combine(r.Farm(200), "tagw_stage", "tagw_report.json");
            File.SetLastWriteTimeUtc(rep, DateTime.UtcNow.AddMinutes(-5));
            r.Control("gpu_bake=nostage\n");
            var y = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Assert.Eq(3, y.Exit, "a stale report is not a stage: " + y);
            Assert.Contains(y.Out, "was not written by this run", "stale reason");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
        }

        /// <summary>R1 fix (V6 findings H1 / H2): a file the install replaces is held open (FileShare.None, like Sapien) when the
        /// bake starts -> refused before anything runs, with the plain message; stage-only is not refused for it.</summary>
        private static void HeldAtStart()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            CliResult x, dry;
            using (new FileStream(r.CycLtmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                x = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
                dry = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            }
            Assert.Eq(2, x.Exit, "refused before any write: " + x);
            Assert.Contains(x.Out + x.Err, "open in another program -- close Sapien / Guerilla", "the plain message");
            Assert.Contains(x.Out + x.Err, CycPrefix + ".scenario_structure_lightmap", "names the file");
            Assert.True(!Directory.Exists(Path.Combine(r.Work, "_TAGBAK_ui")), "no backup made, nothing ran");
            Assert.Eq(0, dry.Exit, "a stage-only plan is not refused for a held file: " + dry);
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "released: the bake installs");
        }

        /// <summary>R1 fix (V6 finding K2): gpu_bake's [4] made its copy + text marker, then the run was killed before the tool
        /// saved. The live set is unchanged; --recover must set the marker aside (not restore the OLDER backup Newest would pick).</summary>
        private static void ToolMarkerUnchanged()
        {
            var r = C.NewRoot();
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "install #1 (S0 -> S1)");
            var s1 = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=fail_bake\n");
            var f = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(3, f.Exit, "bake #2 backs up S1 then stops: " + f);
            r.Control("gpu_bake=ok\n");
            Assert.Eq(2, Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_ui")).Length, "two backups: S0 (bake #1) and S1 (bake #2)");
            // bake #2's tool write-back [4], as gpu_bake does it: copy <prefix>*, then the marker; killed before the tool saved
            string bak = Path.Combine(r.Farm(200), "tag_backup_before_writeback_k2");
            Directory.CreateDirectory(bak);
            var files = Directory.GetFiles(r.CycDir, CycPrefix + "*");
            foreach (var x in files) File.Copy(x, Path.Combine(bak, Path.GetFileName(x)));
            string marker = Path.Combine(r.Farm(200), "writeback_in_progress.txt");
            File.WriteAllText(marker, "started 10/14/2026 02:00:00; previous tags: " + bak + " (" + files.Length + " files from " + r.CycDir + "\\" + CycPrefix + "*). Copy them back if the new tag is broken.");
            Assert.Eq(2, C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap").Exit, "the marker refuses this BSP");
            var l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--recover" });
            var rec = C.Run(l);
            Assert.Eq(0, rec.Exit, "--recover: " + rec);
            Assert.Contains(rec.Out, "verified unchanged", "proved unchanged");
            Assert.NotContains(rec.Out, "  restoring ", "nothing restored");
            Assert.True(!File.Exists(marker) && Directory.GetFiles(r.Farm(200), "writeback_in_progress.txt.verified_*").Length == 1, "marker set aside as .verified_<ts>");
            foreach (var kv in s1) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), kv.Key + " is still S1 (the older S0 was NOT restored)");
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "the bake is allowed again");
        }

        /// <summary>V6 (gate T6): a stand-in for Sapien holding the lightmap tag (FileShare.None) between the shell's backup and its
        /// install. The fake gpu_ao 'stage' verb waits for the test's go flag (knob ao_stage_wait).</summary>
        private static void AoInstallHeld()
        {
            foreach (bool withRetry in new[] { false, true })
            {
                var r = C.NewRoot();
                var orig = SetMd5(r.CycDir, CycPrefix);
                r.Control("ao_stage_wait\n");
                var args = new List<string> { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao" };
                if (withRetry) args.AddRange(new[] { "--install-retries", "1", "--install-retry-wait-s", "3" });
                var t = BakeAsync(r, args.ToArray());
                var sw = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(r.Work, "fake_ao_stage_waiting.flag")) && !t.IsCompleted && sw.ElapsedMilliseconds < 120000) Thread.Sleep(100);
                if (!File.Exists(Path.Combine(r.Work, "fake_ao_stage_waiting.flag"))) throw new TestFailure("the AO stage never ran: " + (t.IsCompleted ? t.Result.ToString() : "timeout"));
                CliResult res;
                string run = CurrentRun.Read(r.Work).RunDir;
                using (var hold = new FileStream(r.CycLtmp, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    File.WriteAllText(Path.Combine(r.Work, "fake_ao_stage_go.flag"), "go");
                    if (withRetry)
                    {
                        // hold until the first install attempt was refused, then let the retry find the file free
                        sw.Restart();
                        while (!t.IsCompleted && sw.ElapsedMilliseconds < 120000 &&
                               !(File.Exists(Path.Combine(run, "bake.log")) && Json.ReadAllTextShared(Path.Combine(run, "bake.log")).Contains("INSTALL: REFUSED"))) Thread.Sleep(100);
                        Assert.True(!t.IsCompleted, "the run waits for the retry while the file is held");
                    }
                    else { t.Wait(180000); }
                }
                res = t.Result;
                string log = File.ReadAllText(Path.Combine(run, "bake.log"));
                string stageLtmp = Path.Combine(r.Farm(200), @"ao\s10e05\tagw_stage", CycPrefix + ".scenario_structure_lightmap");
                Assert.Contains(log, "INSTALL: REFUSED: target file(s) in use", "lmw_install refused the held file (exit 2)");
                Assert.True(!File.Exists(Path.Combine(r.Farm(200), "writeback_in_progress.txt")), "no marker left");
                if (!withRetry)
                {
                    Assert.Eq(3, res.Exit, "no retry without the flag: " + res);
                    Assert.Contains(res.Out, "=== answer: no (the CLI retries only with --install-retries N)", "the CLI said no");
                    Assert.Contains(res.Out, "the install was refused before any change", "reason");
                    foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
                    Assert.True(File.Exists(stageLtmp), "the stage is kept");
                }
                else
                {
                    Assert.Eq(0, res.Exit, "retried and installed: " + res);
                    Assert.Contains(res.Out, "retry 1 of 1 in 3 s", "the CLI retried once");
                    Assert.Contains(log, "installed and verified", "the retry installed");
                    Assert.Eq(Md5(stageLtmp), Md5(r.CycLtmp), "the AO stage is installed");
                }
            }
        }

        private static void ProbesGated()
        {
            var r = C.NewRoot();
            string before = Md5(r.TriLtmp);
            var no = C.Bake(r, "--scenario", Tri, "--bsp", "triplicate");
            Assert.Eq(2, no.Exit, "Standard on a probe BSP refused without --dev-probes: " + no);
            Assert.Contains(no.Out, "11 light probe point(s) and light probes are not released yet", "names the point count");
            Assert.Contains(no.Out, "use Lightmap Only", "and the way forward");
            Assert.True(!Directory.Exists(Path.Combine(r.Work, "_TAGBAK_ui")) && !Directory.Exists(Path.Combine(r.Work, @"standalone\runs")), "refused before any write (no backup, no run)");
            var p = C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--mode", "probes");
            Assert.Eq(2, p.Exit, "Lightprobes refused: " + p);
            Ok(C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--mode", "lightmap"), "Lightmap Only on the probe BSP");
            Assert.True(Md5(r.TriLtmp) != before, "baked");
        }

        private static void RestoreGuarded()
        {
            var r = C.NewRoot();
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "install");
            var live = SetMd5(r.CycDir, CycPrefix);
            // a bake "running" in another window: current_run.json names a LIVE shell (this test process)
            string runDir = Path.Combine(r.Work, @"standalone\runs\20990101_000000_cyclotron_cyclotron");
            Directory.CreateDirectory(runDir);
            new RunStatus { RunDir = runDir, RunTag = "20990101_000000_cyclotron_cyclotron", Worker = 200, State = RunState.Running }.Save();
            CurrentRun.Write(r.Work, runDir, "20990101_000000_cyclotron_cyclotron");
            File.Copy(Directory.GetFiles(r.Farm(200), "writeback_in_progress.txt.done_*").First(), Path.Combine(r.Farm(200), "writeback_in_progress.txt"));   // its [4b] in progress
            foreach (var extra in new[] { new string[0], new[] { "--undo-last-install" } })
            {
                var l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron" }); l.AddRange(extra);
                var x = C.Run(l);
                Assert.Eq(2, x.Exit, "refused during a live bake: " + x);
                Assert.Contains(x.Err, "a bake is running", "reason");
            }
            var lr = C.Base("restore"); lr.AddRange(new[] { "--h2ek", r.Root, "--recover" });
            var rec = C.Run(lr);
            Assert.Eq(2, rec.Exit, "--recover refused BEFORE it looks at the live bake's marker: " + rec);
            Assert.True(File.Exists(Path.Combine(r.Farm(200), "writeback_in_progress.txt")), "the live bake's marker is untouched");
            var scan = Core.Safety.StartupRecovery.Scan(r.Work);
            Assert.True(scan.Single(i => i.Worker == 200).InUse, "startup recovery reports the live bake's marker as in use (never offered): " + string.Join(" | ", scan));
            CurrentRun.Clear(r.Work, runDir);
            File.Move(Path.Combine(r.Farm(200), "writeback_in_progress.txt"), Path.Combine(r.Farm(200), "writeback_in_progress.txt.test_aside"));
            // a research bake holds the GPU lock
            File.WriteAllText(Path.Combine(r.Work, ".gpu_bake.lock"), "RESEARCH bake by another workflow");
            var l2 = C.Base("restore"); l2.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron" });
            var y = C.Run(l2);
            Assert.Eq(2, y.Exit, "refused while the GPU lock is held: " + y);
            Assert.Contains(y.Err, "holds the GPU lock", "reason");
            Assert.Eq("RESEARCH bake by another workflow", File.ReadAllText(Path.Combine(r.Work, ".gpu_bake.lock")), "the foreign lock untouched");
            foreach (var kv in live) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "nothing restored: " + kv.Key);
            File.Delete(Path.Combine(r.Work, ".gpu_bake.lock"));
            var z = C.Run(l2);
            Assert.Eq(0, z.Exit, "free again: the restore runs: " + z);
            Assert.True(!File.Exists(Path.Combine(r.Work, ".gpu_bake.lock")), "the restore released its lock");
        }

        private static void UndoSkipsFailed()
        {
            var r = C.NewRoot();
            var s0 = SetMd5(r.CycDir, CycPrefix);
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "run A installs S1");
            var s1 = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=fail_bake\n");
            Assert.Eq(3, C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap").Exit, "run B fails in phase 3 (never installs)");
            r.Control("gpu_bake=ok\n");
            foreach (var kv in s1) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "B changed nothing");
            var runs = Directory.GetDirectories(Path.Combine(r.Work, @"standalone\runs")).Select(RunStatus.Load).OrderBy(x => x.StartedUtc).ToList();
            Assert.Eq(null, runs.Last().InstallBackupDir, "the failed run records no install backup (finding 4)");
            var ls = C.Base("restore", false); ls.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--list" });
            var list = C.Run(ls);
            Assert.Contains(list.Out, "(= the live set: nothing to restore)", "B's backup equals the live set");
            Assert.Contains(list.Out, "(a bake installed after it)   <- the default", "A's backup is the default (installedAfter)");
            var u = C.Base("restore"); u.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--undo-last-install" });
            var undo = C.Run(u);
            Assert.Eq(0, undo.Exit, "undo: " + undo);
            Assert.Contains(undo.Out, "undo the install of run " + runs.First().RunTag, "undoes A, not the failed B");
            foreach (var kv in s0) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "back to S0: " + kv.Key);
            // the default restore is a no-op now (every backup equals S0 or is older): said so, exit 2
            var rs = C.Base("restore"); rs.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--from", Path.GetFileName(Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_ui")).Where(d => !d.Contains("before_restore")).OrderBy(d => d).Last()) });
            var again = C.Run(rs);
            Assert.Contains(again.Out, "RESULT", "--from runs: " + again);
        }

        private static void ToolMarkerScoped()
        {
            var r = C.NewRoot();
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "an install (makes the _TAGBAK_ui backup)");
            var s0 = Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_ui")).Single();
            // power cut during a tool write-back [4] of cyclotron: the text marker stays, the set is half-written
            File.WriteAllText(Path.Combine(r.Farm(200), "writeback_in_progress.txt"),
                "started 10/14/2026 02:00:00; previous tags: " + Path.Combine(r.Farm(200), "tag_backup_before_writeback") + " (3 files from " + r.CycDir + "\\" + CycPrefix + "*). Copy them back if the new tag is broken.");
            File.WriteAllText(r.CycLtmp, "HALF WRITTEN by the tool");
            var other = C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--mode", "lightmap", "--no-install");
            Assert.Eq(0, other.Exit, "another BSP still bakes (the marker is not about it): " + other);
            Assert.Contains(other.Out, "another lightmap; this bake goes ahead", "with a note");
            var mine = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(2, mine.Exit, "this BSP is refused: " + mine);
            var l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--recover" });
            var rec = C.Run(l);
            Assert.Eq(0, rec.Exit, "--recover restores the set the marker names: " + rec);
            Assert.True(!File.Exists(Path.Combine(r.Farm(200), "writeback_in_progress.txt")) && Directory.GetFiles(r.Farm(200), "writeback_in_progress.txt.restored_*").Length == 1,
                        "the text marker was renamed .restored_ (finding 6)");
            Assert.Eq(Md5(Path.Combine(s0, CycPrefix + ".scenario_structure_lightmap")), Md5(r.CycLtmp), "the ltmp came back from the backup");
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "the bake is allowed again");
        }

        private static void ProbesFallback()
        {
            var r = C.NewRoot();
            r.Control("gpu_bake=nostage\n");
            var orig = SetMd5(r.TriDir, "triplicate_triplicate_lightmap");
            var ao = C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--dev-probes", "--ao-fallback", "install");
            Assert.Eq(3, ao.Exit, "--ao-fallback alone never drops the probes: " + ao);
            Assert.Contains(ao.Out, "RESETS this BSP's 11 light probe(s) to zero", "the prompt names the probe reset (finding 9)");
            Assert.Contains(ao.Out, "--probes-fallback install", "and the flag that allows it");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.TriDir, kv.Key)), "untouched " + kv.Key);
            var go = Ok(C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--dev-probes", "--probes-fallback", "install"), "install without the probes");
            Assert.Contains(go.Out, "installed WITHOUT light probes", "reason");
        }

        private static void SkipPreflightTool()
        {
            var r = C.NewRoot(withTool: false);
            var x = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(2, x.Exit, "no tool_fast: refused even with --dev-skip-preflight: " + x);
            Assert.Contains(x.Out, "kept with --dev-skip-preflight", "reason (finding 24)");
            Assert.True(!Directory.Exists(Path.Combine(r.Work, "_TAGBAK_ui")), "nothing written");
            var l = C.Base("cleanup-alias", false); l.AddRange(new[] { "--h2ek", r.Root, "--dry-run" });
            var ca = C.Run(l);
            Assert.Eq(0, ca.Exit, "cleanup-alias: " + ca);
            Assert.Contains(ca.Out, "no alias of", "a short sandbox WORK has no alias");
        }

        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
        private const string Tri = @"scenarios\multi\triplicate\triplicate";
        private const string CycPrefix = "cyclotron_cyclotron_lightmap";

        private static string Md5(string p) { return SafeFs.Md5File(p); }

        private static CliResult Ok(CliResult r, string what)
        {
            if (r.Exit != 0) throw new TestFailure(what + " failed: " + r);
            Assert.Contains(r.Out, "RESULT DONE", what);
            return r;
        }

        private static string RunDirOf(CliResult r)
        {
            var l = r.Lines.LastOrDefault(x => x.StartsWith("run folder: ", StringComparison.Ordinal));
            if (l == null) throw new TestFailure("no run folder line: " + r);
            return l.Substring(12).Trim();
        }

        private static Dictionary<string, string> SetMd5(string dir, string prefix)
        {
            return Directory.GetFiles(dir, prefix + "*").Where(f => f.IndexOf(".gpulm_", StringComparison.OrdinalIgnoreCase) < 0)
                            .ToDictionary(f => Path.GetFileName(f), Md5, StringComparer.OrdinalIgnoreCase);
        }

        private static string Bytes(string p) { return File.ReadAllText(p); }

        private static void LightmapInstall()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            var res = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "lightmap install");
            string run = RunDirOf(res);
            string farm = r.Farm(200);
            // installed = the direct writer's stage, by the REAL lmw_install -- U3: the shell's install (the command [4b] runs)
            foreach (var n in new[] { CycPrefix + ".scenario_structure_lightmap", CycPrefix + "_bitmaps.bitmap", CycPrefix + "_truecolor_bitmaps.bitmap" })
                Assert.Eq(Md5(Path.Combine(farm, "tagw_stage", n)), Md5(Path.Combine(r.CycDir, n)), "installed " + n);
            Assert.Contains(Bytes(r.CycLtmp), "|BAKED super 200", "the baked ltmp");
            Assert.Eq(orig[CycPrefix + "_radiance_bitmaps_0.bitmap"], Md5(Path.Combine(r.CycDir, CycPrefix + "_radiance_bitmaps_0.bitmap")), "radiance untouched");
            Assert.True(!File.Exists(Path.Combine(farm, "writeback_in_progress.txt")), "marker gone");
            // _TAGBAK_ui backup of the original set (radiance excluded)
            var bak = Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_ui")).Single();
            foreach (var kv in orig.Where(k => k.Key.IndexOf("radiance", StringComparison.OrdinalIgnoreCase) < 0))
                Assert.Eq(kv.Value, Md5(Path.Combine(bak, kv.Key)), "backup of " + kv.Key);
            Assert.True(!File.Exists(Path.Combine(bak, CycPrefix + "_radiance_bitmaps_0.bitmap")), "radiance not copied");
            Assert.True(File.Exists(Path.Combine(bak, "backup.json")) && File.Exists(Path.Combine(bak, "source.txt")), "backup.json + source.txt");
            // capture record (S3.2)
            var rec = Json.ReadObjFile(Path.Combine(farm, "standalone_capture.json"));
            Assert.Eq("super", rec.Str("quality"), "record quality");
            Assert.Eq(Json.ReadObjFile(Path.Combine(farm, "capture_id.json")).Str("soup_crc32"), rec.Obj("captureId").Str("soup_crc32"), "record crc");
            Assert.Eq(true, rec.Obj("fixes").Bool("plucker"), "fixes recorded");
            Assert.True(File.Exists(Path.Combine(farm, "standalone_capture_tags.tsv")), "tags fingerprint file");
            Assert.True(rec.Obj("binaries").Str("estimatorDll") == Ctx.ProdDllMd5, "pristine DLL md5 recorded");
            // run records, lock, workers, history
            var st = RunStatus.Load(run);
            Assert.Eq(RunState.Done, st.State, "status DONE");
            Assert.Eq(bak, st.BackupDir, "status.backupDir");
            Assert.True(!File.Exists(Path.Combine(r.Work, @"standalone\current_run.json")), "current_run.json cleared");
            Assert.True(!File.Exists(Path.Combine(r.Work, ".gpu_bake.lock")), "lock released");
            Assert.Eq(200L, Json.ReadObjFile(Path.Combine(r.Work, @"standalone\workers.json")).Long(@"scenarios\multi\cyclotron\cyclotron|cyclotron"), "workers.json");
            var h = Json.ReadObjFile(Path.Combine(r.Work, @"standalone\history.json")).Arr("runs").OfType<JObj>().ToList();
            Assert.Eq("DONE", h.Last().Str("state"), "history entry");
            string log = File.ReadAllText(Path.Combine(run, "bake.log"));
            foreach (var s in new[] { "=== plan|", "[1] CAPTURE", "the shell installs it", "start lmw_install install", "installed and verified", "verified: 3 installed file(s) md5-equal to the stage",
                                      "GPU lock acquired", "capture record written", "pwsh 7.6.6", "numpy 2.2.6" })
                Assert.Contains(log, s, "bake.log");
            Assert.NotContains(log, "[4b] INSTALL", "no install inside gpu_bake (U3)");
            var startIns = log.Split('\n').FirstOrDefault(x => x.Contains("start lmw_install install"));
            Assert.True(startIns != null && startIns.Contains(Path.Combine(farm, "tag_backup_before_writeback")) && startIns.Contains(Path.Combine(r.Work, "_TAGBAK_auto"))
                        && startIns.Contains(Path.Combine(farm, "writeback_in_progress.txt")) && startIns.Contains(@"engine_v373\v043-tagwriter\lmw_install.py"),
                        "[4b]'s lmw_install arguments: " + startIns);
            // the child really ran with the shell's environment (from scratch)
            var call = r.Calls("gpu_bake").Single();
            Assert.Eq(0, call.Arr("gpulm").Count, "no GPULM_* leaked into gpu_bake: " + string.Join(",", call.Arr("gpulm")));
            Assert.Eq(0, call.Arr("devp").Count, "no DEVP_* / V370_* leaked into gpu_bake (finding 19): " + string.Join(",", call.Arr("devp")));
            Assert.Eq(true, call.Bool("secret_inherited"), "other inherited variables still reach the child unchanged");
            Assert.NotContains(log, Ctx.SecretValue, "bake.log never holds an inherited variable's value (finding 18)");
            Assert.Eq(true, st.Extra.Bool("installed"), "status.json: installed");
            Assert.Eq(Path.Combine(farm, "tag_backup_before_writeback"), st.InstallBackupDir, "the install backup is recorded (the shell's install ran, [4b]'s backup dir)");
            Assert.True(File.Exists(Path.Combine(farm, @"tag_backup_before_writeback\_install_manifest.json")), "lmw_install's manifest in [4b]'s backup dir");
            Assert.Eq(1, Directory.GetFiles(farm, "writeback_in_progress.txt.done_*").Length, "the marker went through OUT and was set aside .done_ (as [4b])");
            Assert.Eq(1, Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_auto")).Length, "the auto backup (WORK\\_TAGBAK_auto, as [4b])");
            Assert.Contains(log, "DEV RUNTIME OVERRIDE (--rt-pwsh / --rt-py): these runtimes were NOT md5-verified (versions match the pin)", "the run header was checked against the pin, loudly under the dev override (finding 20)");
            Assert.Eq(null, call.Str("pythonpath"), "PYTHONPATH stripped");
            Assert.Eq(Path.Combine(C.PyDir, "py.exe"), call.Str("py"), "& py = the bundle");
            Assert.Eq(Path.Combine(r.Work, @"_runtime\0.5.0-s25cccc"), call.Str("h2lm_code"), "H2LM_CODE");
            Assert.Eq(r.Work, call.Str("cwd"), "cwd WORK");
            var argv = call.Arr("argv").Cast<string>().ToList();
            Assert.Seq(new[] { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       argv.Skip(argv.FindIndex(a => a.EndsWith("gpu_bake.ps1", StringComparison.OrdinalIgnoreCase)) + 1).ToList(), "the argv gpu_bake received (stage only)");
            Assert.Contains(string.Join(" ", argv.Take(6)), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File", "pwsh flags");
            // INST: extracted, sealed, slot DLLs patched for this WORK
            var mk = Json.ReadObjFile(Path.Combine(r.Work, @"_runtime\0.5.0-s25cccc\.complete"));
            Assert.True(mk != null && mk.Str("slotBase") != null, "INST marker with the slot base");
        }

        private static void Reuse()
        {
            var r = C.NewRoot();
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install"), "first (stage-only) bake");
            string farm = r.Farm(200);
            string crc = Json.ReadObjFile(Path.Combine(farm, "capture_id.json")).Str("soup_crc32");
            var dry = C.DryJson(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--reuse-capture", "--no-install");
            Assert.Eq(0, dry.Exit, "reuse dry run: " + dry);
            var g = dry.Json().Arr("steps").OfType<JObj>().Single(s => s.Str("id") == "gpu-bake");
            Assert.True(g.Arr("args").Cast<string>().Contains("-SkipCapture"), "-SkipCapture planned");
            Assert.Eq(false, g.Obj("capture").Bool("fresh"), "reused");
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--reuse-capture", "--no-install"), "reuse bake");
            Assert.True(r.Calls("gpu_bake").Last().Arr("argv").Cast<string>().Contains("-SkipCapture"), "gpu_bake got -SkipCapture");
            Assert.Eq(crc, Json.ReadObjFile(Path.Combine(farm, "capture_id.json")).Str("soup_crc32"), "the capture was not remade");
            Assert.True(File.Exists(Path.Combine(farm, "standalone_capture.json")), "the record stays");
            // a tag changed since the capture: confirmation needed (CLI: --yes), the change is named
            File.WriteAllText(Path.Combine(r.Tags, @"objects\thing\thing.shader"), "a changed shader");
            var refused = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--reuse-capture", "--no-install");
            Assert.Eq(2, refused.Exit, "changed tags refused without --yes: " + refused);
            Assert.Contains(refused.Out, @"objects\thing\thing.shader", "the changed tag is listed");
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--reuse-capture", "--no-install", "--yes"), "reuse with --yes");
            var q = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--reuse-capture", "--no-install");
            Assert.Eq(2, q.Exit, "low reuse of a super capture refused: " + q);
            Assert.Contains(q.Out, "this bake is low", "reason");
            // a fresh capture renames the old record aside (never deleted)
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install"), "fresh capture again");
            Assert.True(Directory.GetFiles(farm, "standalone_capture.json.superseded_*").Length >= 1, "the older record was renamed aside");
        }

        private static void AoInstall()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            var res = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao"), "AO install");
            string run = RunDirOf(res);
            string stage = Path.Combine(r.Farm(200), @"ao\s10e05\tagw_stage");
            Assert.Eq(Md5(Path.Combine(stage, CycPrefix + ".scenario_structure_lightmap")), Md5(r.CycLtmp), "the AO stage was installed");
            Assert.Contains(Bytes(r.CycLtmp), "|AO s10e05", "AO layer");
            var ao = r.Calls("gpu_ao");
            Assert.Seq(new[] { "trace", "apply", "stage" }, ao.Select(c => c.Arr("argv").Cast<string>().ElementAt(1)).ToList(), "verbs in order");
            Assert.True(ao[0].Arr("argv").Cast<string>().SequenceEqual(new[] { ao[0].Arr("argv").Cast<string>().First(), "trace", "--farm", r.Farm(200), "--worker", "200", "--scenario", Cyc, "--bsp", "cyclotron",
                        "--engine", Path.Combine(r.Work, @"_runtime\0.5.0-s25cccc\_research\ao\dev_AO"), "--quality", "super", "--plucker-fix", "--scenery-fix" }), "trace argv: " + string.Join(" ", ao[0].Arr("argv")));
            Assert.True(string.Equals(Path.Combine(C.PwshDir, "pwsh.exe"), ao[0].Str("pwsh_on_path"), StringComparison.OrdinalIgnoreCase), "gpu_ao's `pwsh` = the bundled one (PATH): " + ao[0].Str("pwsh_on_path"));
            Assert.Eq(r.Root, ao[0].Str("h2lm_root"), "H2LM_ROOT for P6");
            Assert.True(!r.Calls("gpu_bake").Single().Arr("argv").Cast<string>().Contains("-WriteBack"), "the bake itself was stage-only");
            var st = RunStatus.Load(run);
            Assert.Eq(Path.Combine(run, "tagbak"), st.InstallBackupDir, "install backup recorded (W2: RUN\\tagbak)");
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(run), @"^\d{8}_\d{6}_cyclotron_cyclotron$"), "the run folder uses the short label: " + Path.GetFileName(run));
            // Undo last install (lmw_install restore from RUN\tagbak)
            var l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--undo-last-install" });
            var u = C.Run(l);
            Assert.Eq(0, u.Exit, "undo: " + u);
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "undone " + kv.Key);
        }

        private static void AoFallback()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=nostage\n");
            var stop = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao");
            Assert.Eq(3, stop.Exit, "stops: " + stop);
            Assert.Contains(stop.Out, "fell back to the tool writer", "reason");
            Assert.Contains(stop.Out, "--ao-fallback install", "how to proceed");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
            Assert.Eq(0, r.Calls("gpu_ao").Count, "no AO step ran");
            var go = Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao", "--ao-fallback", "install"), "install without AO");
            Assert.Contains(go.Out, "installed WITHOUT AO", "reason");
            var last = r.Calls("gpu_bake").Last().Arr("argv").Cast<string>().ToList();
            Assert.True(last.Contains("-SkipCapture") && last.Contains("-WriteBack"), "the production write-back on this capture: " + string.Join(" ", last));
            Assert.Contains(Bytes(r.CycLtmp), "TOOL WRITE-BACK", "the (fake) tool write-back wrote it");
        }

        private static void StandardProbes()
        {
            var r = C.NewRoot();
            var res = Ok(C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--dev-probes"), "standard on triplicate");
            string run = RunDirOf(res);
            string ltmp = "triplicate_triplicate_lightmap.scenario_structure_lightmap";
            var val = r.Calls("lmw_validate").Single();
            Assert.Eq(Path.Combine(run, "final_stage"), val.Arr("argv").Cast<string>().ElementAt(2), "the merged final stage passed lmw_validate --check-set before the install (finding 21)");
            Assert.Contains(res.Out, "11 probe point(s); lightmap present, 11 lighting environment(s)", "the real map query (lmw_tagio)");
            Assert.Eq(Md5(Path.Combine(run, "final_stage", ltmp)), Md5(r.TriLtmp), "RUN\\final_stage installed");
            byte[] b = File.ReadAllBytes(r.TriLtmp);
            Assert.Eq("SHSHSHSH", System.Text.Encoding.ASCII.GetString(b, b.Length - 8, 8), "the SH ltmp was merged");
            var bakes = r.Calls("gpu_bake");
            Assert.Eq(2, bakes.Count, "lightmap bake + probe bake");
            Assert.Eq(0, bakes[0].Arr("gpulm").Count, "the lightmap capture never sees SKY_HI");
            Assert.Contains(string.Join(" ", bakes[1].Arr("gpulm")), "GPULM_SKY_HI=1", "the probe capture is SKY_HI");
            Assert.Contains(string.Join(" ", bakes[1].Arr("bound")), "Worker=201", "probe worker 201");
            var pr = Json.ReadObjFile(Path.Combine(r.Farm(201), "standalone_capture.json"));
            Assert.Eq(true, pr.Bool("skyHi"), "probe capture record is probe-ready");
            var lp = r.Calls("gpu_lightprobes").Single();
            Assert.Eq(0, lp.Arr("gpulm").Count, "gpu_lightprobes got the clean env (it forwards os.environ)");
            Assert.Eq(1, r.Calls("sh_stage").Count, "one merge");
            string log = File.ReadAllText(Path.Combine(run, "bake.log"));
            Assert.Contains(log, "side| [1] CAPTURE", "the probe bake's own log is tailed");
            Assert.Contains(log, "side|       PROBE: traced", "probe pass");
            Assert.Contains(log, "SH_STAGE: OK merge", "merge result");
            Assert.True(Directory.GetFiles(Path.Combine(run, "final_stage")).Length >= 3, "final stage copied");
            // reuse of both captures: the probe capture is probe-ready
            var dry = C.DryJson(r, "--scenario", Tri, "--bsp", "triplicate", "--probe-points", "11", "--reuse-capture", "--yes", "--dev-probes");
            Assert.Eq(0, dry.Exit, "reuse dry: " + dry);
            var pj = dry.Json();
            Assert.Eq(true, pj.Bool("reuseLightmapCapture"), "lightmap capture reusable");
            Assert.Eq(true, pj.Bool("reuseProbeCapture"), "probe capture reusable");
            Assert.True(pj.Arr("steps").OfType<JObj>().Single(s => s.Str("id") == "probe-bake").Arr("args").Cast<string>().Contains("--skip-capture"), "--skip-capture for probes");
        }

        private static void ProbesOnly()
        {
            var r = C.NewRoot();
            string bmp = Path.Combine(r.TriDir, "triplicate_triplicate_lightmap_bitmaps.bitmap");
            string bmpMd5 = Md5(bmp), ltmpMd5 = Md5(r.TriLtmp);
            var res = Ok(C.Bake(r, "--scenario", Tri, "--bsp", "triplicate", "--mode", "probes", "--dev-probes"), "probes only");
            string run = RunDirOf(res);
            Assert.Eq(Path.Combine(run, "probe_stage_check"), r.Calls("lmw_validate").Single().Arr("argv").Cast<string>().ElementAt(2), "the probe ltmp + the live bitmaps passed check-set (finding 21)");
            Assert.Eq(3, Directory.GetFiles(Path.Combine(run, "probe_stage_check")).Length, "the check set = ltmp + truecolor + P8 copies");
            Assert.Eq(bmpMd5, Md5(bmp), "bitmaps untouched (ltmp-only install)");
            Assert.True(Md5(r.TriLtmp) != ltmpMd5, "ltmp changed");
            Assert.Eq(Md5(Path.Combine(run, "probe_stage", Path.GetFileName(r.TriLtmp))), Md5(r.TriLtmp), "the single stage was installed");
            var bak = Directory.GetDirectories(Path.Combine(r.Work, "_TAGBAK_ui")).Single();
            Assert.Seq(new[] { Path.GetFileName(r.TriLtmp) }, Directory.GetFiles(bak).Select(Path.GetFileName).Where(n => n.EndsWith(".scenario_structure_lightmap")).ToList(), "ltmp-only backup");
            Assert.Eq(ltmpMd5, Md5(Path.Combine(bak, Path.GetFileName(r.TriLtmp))), "backup = the original");
            Assert.Eq(0, r.Calls("gpu_bake").Count(c => !string.Join(" ", c.Arr("gpulm")).Contains("SKY_HI")), "no lightmap bake in probes mode");
            var no = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "probes", "--dev-probes");
            Assert.Eq(2, no.Exit, "probes on cyclotron: " + no);
            Assert.Contains(no.Out, "no light probe points", "reason");
        }

        private static Task<CliResult> BakeAsync(TestRoot r, params string[] a) { return Task.Run(() => C.Bake(r, a)); }

        /// <summary>Waits (bounded) until the run in progress on <paramref name="r"/> reports phase <paramref name="phase"/>.</summary>
        private static string WaitPhase(TestRoot r, string phase, Task<CliResult> t, int ms = 90000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !t.IsCompleted)
            {
                var cur = CurrentRun.Read(r.Work);
                if (cur != null && cur.RunDir != null)
                {
                    var st = RunStatus.Load(cur.RunDir);
                    if (st != null && st.Phase == phase) return cur.RunDir;
                }
                Thread.Sleep(200);
            }
            throw new TestFailure("phase " + phase + " never came" + (t.IsCompleted ? ": " + t.Result : ""));
        }

        private static CliResult CancelVerb(TestRoot r)
        {
            var l = C.Base("cancel", false); l.AddRange(new[] { "--h2ek", r.Root });
            return C.Run(l);
        }

        private static void CancelPhase3()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=slow3\n");
            var t = BakeAsync(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            WaitPhase(r, "3", t);
            var sw = Stopwatch.StartNew();
            var c = CancelVerb(r);
            Assert.Eq(0, c.Exit, "cancel verb: " + c);
            var res = t.Result;
            Assert.Eq(4, res.Exit, "cancelled: " + res);
            Assert.Contains(res.Out, "RESULT CANCELLED", "result");
            Assert.True(sw.ElapsedMilliseconds < 20000, "cancelled promptly (" + sw.ElapsedMilliseconds + " ms)");
            Assert.True(!File.Exists(Path.Combine(r.Work, ".gpu_bake.lock")), "lock released");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
            Assert.Eq(RunState.Cancelled, RunStatus.Load(RunDirOf(res)).State, "status CANCELLED");
            Assert.NotContains(File.ReadAllText(Path.Combine(RunDirOf(res), "bake.log")), "SHOULD NOT PRINT", "the bake was really stopped");
        }

        private static void CancelDeferred()
        {
            var r = C.NewRoot();
            // U3: the default path's install is the shell's (its deferral: core safety.install); gpu_bake's own [4b] still runs
            // in the tool write-back fallback when the fallback's direct writer works -- that tag write is deferred the same way
            r.Control("gpu_bake=nostage_once_slow4b\n");
            var t = BakeAsync(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            string run = WaitPhase(r, "4b", t);
            Assert.Eq(0, CancelVerb(r).Exit, "cancel verb");
            var res = t.Result;
            // review fix (finding 10): the deferred cancel landed after the install -> the run verifies it and ends DONE, saying so
            Assert.Eq(0, res.Exit, "installed + verified, the cancel applied after the write: " + res);
            Assert.Contains(res.Out, "the cancel arrived during the tag write and was applied after it", "the reason says so");
            string log = File.ReadAllText(Path.Combine(run, "bake.log"));
            Assert.Contains(log, "deferred until the write ends", "deferred");
            Assert.Contains(log, "installed and verified", "the install completed");
            Assert.Contains(log, "verified: 3 installed file(s) md5-equal to the stage", "Verify ran after the cancel");
            var st = RunStatus.Load(run);
            Assert.True(st.State == RunState.Done && st.Extra.Bool("installed") == true, "status DONE, installed: " + st.State);
            Assert.Eq(Md5(Path.Combine(r.Farm(200), "tagw_stage", CycPrefix + ".scenario_structure_lightmap")), Md5(r.CycLtmp), "never half-written: the full set is in");
            Assert.True(!File.Exists(Path.Combine(r.Farm(200), "writeback_in_progress.txt")), "no marker left");
        }

        // W2 e2e: both Advanced settings reach the real gpu_bake invocation (the fake records its bound parameters)
        private static void W2BothOptions()
        {
            var r = C.NewRoot();
            var off = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Ok(off, "unticked bake");
            var bOff = r.Calls("gpu_bake").Single();
            Assert.True(!bOff.Arr("bound").Cast<string>().Any(x => x.StartsWith("ExactVertexLighting=") || x.StartsWith("Responsive=")), "unticked: neither switch bound");
            Assert.True(!off.Out.Contains("exact vertex lighting"), "unticked: the log does not mention exact vertex lighting");
            var r2 = C.NewRoot();
            var on = C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--responsive", "--exact-vertex-lighting");
            Ok(on, "both options bake");
            var bOn = r2.Calls("gpu_bake").Single();
            var argv = bOn.Arr("argv").Cast<string>().ToList();
            Assert.True(argv.Contains("-Responsive") && argv.Contains("-ExactVertexLighting"), "gpu_bake got both: " + string.Join(" ", argv));
            Assert.True(bOn.Arr("bound").Cast<string>().Contains("ExactVertexLighting=True") && bOn.Arr("bound").Cast<string>().Contains("Responsive=True"), "both switches bound");
            Assert.Contains(on.Out, "exact vertex lighting on (slower tag writer)", "the plan / log says 'exact vertex lighting on (slower tag writer)'");
            Assert.Contains(on.Out, "responsive on", "and 'responsive on'");
            // AO on: the AO layer's final stage is written by gpu_ao stage -> it gets --exact-vertex-lighting too (P6b); the AO trace / apply do not
            var r3 = C.NewRoot();
            var ao = C.Bake(r3, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--ao", "--exact-vertex-lighting");
            Ok(ao, "AO + exact vertex lighting bake");
            var calls = r3.Calls("gpu_ao");
            Assert.Seq(new[] { "trace", "apply", "stage" }, calls.Select(c => c.Arr("argv").Cast<string>().ElementAt(1)).ToList(), "verbs in order");
            Assert.True(calls[2].Arr("argv").Cast<string>().Contains("--exact-vertex-lighting"), "gpu_ao stage got --exact-vertex-lighting: " + string.Join(" ", calls[2].Arr("argv")));
            Assert.True(!calls[0].Arr("argv").Cast<string>().Contains("--exact-vertex-lighting") && !calls[1].Arr("argv").Cast<string>().Contains("--exact-vertex-lighting"), "trace / apply do not");
            Assert.True(r3.Calls("gpu_bake").Single().Arr("argv").Cast<string>().Contains("-ExactVertexLighting"), "and the lightmap gpu_bake still gets -ExactVertexLighting");
        }

        // Scenery Multi Permutation Fix e2e: the fake gpu_bake records its bound parameters. The old-engine payload's fake has no such parameter, so passing it
        // would make PowerShell fail the bake -- the shell must refuse before starting gpu_bake at all.
        private static void SceneryPermBake()
        {
            var r = C.NewRoot();
            var off = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Ok(off, "unticked bake");
            var bOff = r.Calls("gpu_bake").Single();
            Assert.True(!bOff.Arr("argv").Cast<string>().Contains("-SceneryOwnPermutationOnly"), "unticked: gpu_bake got no switch");
            Assert.True(!bOff.Arr("bound").Cast<string>().Any(x => x.StartsWith("SceneryOwnPermutationOnly=")), "unticked: nothing bound");
            Assert.True(!off.Out.Contains("permutation fix"), "unticked: the log does not mention it");
            // ticked: reaches gpu_bake, bound, listed in the log header (the Summary line) and the plan notes
            var r2 = C.NewRoot();
            var on = C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--scenery-permutation-fix");
            Ok(on, "ticked bake");
            var bOn = r2.Calls("gpu_bake").Single();
            Assert.True(bOn.Arr("argv").Cast<string>().Contains("-SceneryOwnPermutationOnly"), "gpu_bake got the switch: " + string.Join(" ", bOn.Arr("argv")));
            Assert.True(bOn.Arr("bound").Cast<string>().Contains("SceneryOwnPermutationOnly=True"), "bound");
            Assert.Contains(on.Out, "scenery multi permutation fix on", "the bake log header says so");
            // the capture it made is recorded as made WITH the option: an unticked reuse is refused, a ticked one is accepted
            Assert.Eq(true, Json.ReadObjFile(Path.Combine(r2.Farm(200), "standalone_capture.json")).Bool("sceneryPermutationFix"), "record: sceneryPermutationFix true");
            var bad = C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--reuse-capture");
            Assert.Eq(2, bad.Exit, "reuse without the option refused: " + bad);
            Assert.Contains(bad.Out, "was made with \"Scenery Multi Permutation Fix (Experimental Improvement)\"", "and says why");
            Ok(C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--reuse-capture", "--scenery-permutation-fix"), "reuse with the same setting");
            var last = r2.Calls("gpu_bake").Last().Arr("argv").Cast<string>().ToList();
            Assert.True(last.Contains("-SkipCapture") && last.Contains("-SceneryOwnPermutationOnly"), "the reuse bake: -SkipCapture and the switch: " + string.Join(" ", last));
            // and the other way round: an unticked capture cannot be reused by a ticked bake (the engine needs the pick recorded while capturing)
            var bad2 = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--reuse-capture", "--scenery-permutation-fix");
            Assert.Eq(2, bad2.Exit, "reuse of an unticked capture refused: " + bad2);
            Assert.Contains(bad2.Out, "made without \"Scenery Multi Permutation Fix (Experimental Improvement)\"", "and says why");
            // an engine without the parameter: refused up front, gpu_bake never started, nothing written
            var r3 = C.NewRoot();
            string oldPayload = C.BuildPayload(C.Sb.Dir("payload_e2e_nosp"), sceneryEngine: false);
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = oldPayload;
            l.AddRange(new[] { "--h2ek", r3.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--scenery-permutation-fix" });
            var refused = C.Run(l);
            Assert.Eq(2, refused.Exit, "refused (exit 2): " + refused);
            Assert.Contains(refused.Out, "RESULT REFUSED", "plain result word");
            Assert.Contains(refused.Out, "Needs engine 0.6.4 or newer.", "plain reason");
            Assert.Eq(0, r3.Calls("gpu_bake").Count, "gpu_bake was never started");
            Assert.True(!Directory.Exists(r3.Farm(200)), "no farm folder made");
            // the same payload, option off: bakes as always
            var l2 = C.Base("bake"); l2[l2.IndexOf("--payload-dir") + 1] = oldPayload;
            l2.AddRange(new[] { "--h2ek", r3.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" });
            Ok(C.Run(l2), "old engine, option off");
            Assert.True(!r3.Calls("gpu_bake").Single().Arr("argv").Cast<string>().Contains("-SceneryOwnPermutationOnly"), "no switch passed to the old engine");
        }

        // Compatibility mode (DF64) e2e. The fake gpu_bake of a "0.6.4" test payload validates GPULM_COMPAT and prints fullgpu's own "Compatibility mode" line (off, or
        // "(no FP64)" when GPULM_COMPAT=1 or the control file says gpu_bake=nofp64); an older payload's fake never mentions the variable.
        // ------------------------------------------------------------------ 0.6.4: cross-folder BSP

        private const string Cyc2 = @"scenarios\multi\cyc2\cyc2";

        /// <summary>A sandbox root plus a second scenario, scenarios\multi\cyc2\cyc2, that is a copy of the real cyclotron scenario: its structure_bsps entry names
        /// scenarios\multi\cyclotron\cyclotron, a BSP in ANOTHER folder (like 01b_spacestation -> 01a_tutorial\01_bsp_1). No BSP file beside cyc2 unless asked.</summary>
        private static TestRoot XRoot(bool copyBeside)
        {
            var r = C.NewRoot(false);
            string d = Path.Combine(r.Tags, @"scenarios\multi\cyc2");
            Directory.CreateDirectory(d);
            File.Copy(Path.Combine(r.CycDir, "cyclotron.scenario"), Path.Combine(d, "cyc2.scenario"));
            if (copyBeside) File.WriteAllText(Path.Combine(d, "cyclotron.scenario_structure_bsp"), "a COPY of the bsp beside cyc2");
            return r;
        }

        private static List<string> TailArgs(JObj call)
        {
            var a = call.Arr("argv").Cast<string>().ToList();
            return a.Skip(a.FindIndex(x => x.EndsWith("gpu_bake.ps1", StringComparison.OrdinalIgnoreCase)) + 1).ToList();
        }

        /// <summary>The BSP tag path the run resolved (status.json extra.options.bspRel): null = the BSP beside the scenario.</summary>
        private static string BspRelOf(CliResult res)
        {
            return RunStatus.Load(RunDirOf(res)).Extra.Obj("options").Str("bspRel");
        }

        private static void CrossFolderBsp()
        {
            string newP = C.BuildPayload(C.Sb.Dir("payload_e2e_xbsp"), crossBspEngine: true), oldP = C.PayloadDir;
            string refTag = Path.Combine(@"scenarios\multi\cyclotron", "cyclotron");
            // 1. an engine that resolves the BSP from the scenario: gpu_bake gets the plain name, the shell finds the real tag for the install gate
            var r1 = XRoot(false);
            var a = Ok(DfBake(newP, r1, "--scenario", Cyc2, "--bsp", "cyclotron", "--mode", "lightmap"), "cross-folder BSP, Lightmap Only");
            string log = File.ReadAllText(Path.Combine(RunDirOf(a), "bake.log"));
            Assert.Contains(log, "BSP tag (from the scenario's structure_bsps): " + refTag + " -- in another folder than the scenario", "the shell found the BSP's tag path");
            Assert.Seq(new[] { "-Scenario", Cyc2, "-Bsp", "cyclotron" }, TailArgs(r1.Calls("gpu_bake").First()).Take(4).ToList(), "gpu_bake gets the scenario and the PLAIN BSP name (the engine resolves the file)");
            Assert.Eq(refTag, BspRelOf(a), "the run recorded the BSP tag where it really is (status.json options.bspRel)");
            string made = Path.Combine(r1.Tags, @"scenarios\multi\cyc2\cyc2_cyclotron_lightmap.scenario_structure_lightmap");
            Assert.True(File.Exists(made), "the lightmap is named and written beside the SCENARIO");
            Assert.True(!File.Exists(Path.Combine(r1.CycDir, "cyc2_cyclotron_lightmap.scenario_structure_lightmap")), "nothing was written into the BSP's folder");
            Assert.Eq("fake bsp cyclotron", File.ReadAllText(Path.Combine(r1.Tags, refTag + ".scenario_structure_bsp")), "the BSP tag itself is untouched");
            Assert.Contains(a.Out, "BSP from scenarios\\multi\\cyclotron", "the options line names the BSP's folder");

            // 2. a BSP in the scenario's own folder under the same engine: nothing changes (no other-folder line, the BSP beside is validated)
            var r2 = C.NewRoot();
            var b = Ok(DfBake(newP, r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "same-folder BSP, new engine");
            Assert.NotContains(File.ReadAllText(Path.Combine(RunDirOf(b), "bake.log")), "BSP tag (from the scenario's structure_bsps)", "no other-folder line");
            Assert.Eq(null, BspRelOf(b), "the BSP beside the scenario: no other-folder path");

            // 3. a COPY beside the scenario that the scenario does not reference is ignored: the engine bakes the referenced tag, so does the install gate
            var r3 = XRoot(true);
            var c = Ok(DfBake(newP, r3, "--scenario", Cyc2, "--bsp", "cyclotron", "--mode", "lightmap"), "a copy beside the scenario");
            Assert.Contains(File.ReadAllText(Path.Combine(RunDirOf(c), "bake.log")), "is ALSO beside the scenario", "the copy is called out");
            Assert.Eq(refTag, BspRelOf(c), "still the referenced tag, not the copy");

            // 4. AO / light probes are not supported for such a BSP yet: refused before gpu_bake starts
            var r4 = XRoot(false);
            var d = DfBake(newP, r4, "--scenario", Cyc2, "--bsp", "cyclotron", "--ao");
            Assert.Eq(2, d.Exit, "refused: " + d);
            Assert.Contains(d.Out, "lives in another folder than the scenario; ambient occlusion and light probes are not supported for it yet", "plain reason");
            Assert.Eq(0, r4.Calls("gpu_bake").Count, "gpu_bake never started");
            Assert.True(!Directory.Exists(r4.Farm(200)), "no farm folder made");

            // 5. an engine without the resolution: refused up front with the plain sentence; nothing started, nothing written
            var r5 = XRoot(false);
            var e = DfBake(oldP, r5, "--scenario", Cyc2, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(2, e.Exit, "refused: " + e);
            Assert.Contains(e.Out, "RESULT REFUSED", "plain result word");
            Assert.Contains(e.Out, "Baking a BSP from another folder needs engine 0.6.4 or newer.", "and the requirement");
            Assert.Eq(0, r5.Calls("gpu_bake").Count, "gpu_bake never started");
            Assert.True(!File.Exists(Path.Combine(r5.Tags, @"scenarios\multi\cyc2\cyc2_cyclotron_lightmap.scenario_structure_lightmap")), "nothing written");

            // 6. dry runs: the new engine plans it (the BSP is looked up by the real run); the old engine refuses it
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = newP;
            l.AddRange(new[] { "--h2ek", r5.Root, "--dry-run", "--scenario", Cyc2, "--bsp", "cyclotron", "--mode", "lightmap" });
            var dry = C.Run(l);
            Assert.Eq(0, dry.Exit, "dry run, new engine: " + dry);
            Assert.Contains(dry.Out, "is looked up in the scenario's structure_bsps", "the dry run says so");
            var l2 = C.Base("bake");
            l2.AddRange(new[] { "--h2ek", r5.Root, "--dry-run", "--scenario", Cyc2, "--bsp", "cyclotron", "--mode", "lightmap" });
            var dryOld = C.Run(l2);
            Assert.Eq(2, dryOld.Exit, "dry run, old engine: refused: " + dryOld);
            Assert.Contains(dryOld.Out, "needs engine 0.6.4 or newer", "plain");
        }

        // ------------------------------------------------------------------ 0.6.4: the silent write-back fallback

        private static int Occurrences(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static void FallbackWarning()
        {
            const string Warn = "WARNING: the lightmap write-back fell back from the DIRECT tag writer to the older TOOL write-back";
            const string Summary = "WARNING: the write-back fell back from the DIRECT tag writer to the TOOL write-back";
            // 1. gpu_bake's own pre-flight: "-WriteBackMode direct cannot run: ... -- using -WriteBackMode tool" (one yellow Note line in the engine's output)
            var r1 = C.NewRoot();
            r1.Control("gpu_bake=nodirect\n");
            var a = Ok(C.Bake(r1, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "pre-flight fallback");
            string log1 = File.ReadAllText(Path.Combine(RunDirOf(a), "bake.log"));
            Assert.Eq(1, Occurrences(log1, "*** " + Warn), "one visible warning in bake.log");
            Assert.Contains(log1, "Reason: fake: lm_tagwriter.py is missing", "with the engine's reason");
            Assert.Contains(a.Out, Warn, "the live output shows it");
            Assert.Contains(a.Out, Summary + " (fake: lm_tagwriter.py is missing)", "and the run summary (the RESULT line) names it");
            var sa = RunStatus.Load(RunDirOf(a));
            Assert.Eq(RunState.Done, sa.State, "the bake itself is still DONE");
            Assert.True(sa.Notes.Any(n => n.StartsWith(Warn, StringComparison.Ordinal)), "status.json notes keep it");
            Assert.Eq(1, sa.Extra.Arr("writeBackFallback").Count, "status.json extra.writeBackFallback");
            Assert.True(sa.Reason != null && sa.Reason.Contains(Summary), "status.json reason carries the summary: " + sa.Reason);
            // 2. the direct writer made no stage -> the shell runs the production tool write-back; the engine ALSO prints its own fallback line: one warning, not two
            var r2 = C.NewRoot();
            r2.Control("gpu_bake=nostage\n");
            var b = Ok(C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "writer failed, tool write-back");
            string log2 = File.ReadAllText(Path.Combine(RunDirOf(b), "bake.log"));
            Assert.Eq(1, Occurrences(log2, "*** " + Warn), "shell + engine report the same fallback once");
            Assert.Contains(b.Out, Summary, "the run summary names it");
            Assert.Contains(b.Out, "installed by the production tool write-back fallback", "and the old sentence stays");
            Assert.Eq(1, RunStatus.Load(RunDirOf(b)).Extra.Arr("writeBackFallback").Count, "recorded once");
            // 3. a clean bake says nothing
            var r3 = C.NewRoot();
            var c = Ok(C.Bake(r3, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "clean");
            Assert.NotContains(c.Out, "WARNING: the lightmap write-back", "no warning");
            Assert.NotContains(c.Out, "fell back", "no summary sentence");
            var sc = RunStatus.Load(RunDirOf(c));
            Assert.True(!sc.Extra.Has("writeBackFallback") && !sc.Notes.Any(n => n.Contains("write-back fell back")), "status.json is clean");
        }

        private static CliResult DfBake(string payload, TestRoot r, params string[] extra)
        {
            var l = C.Base("bake"); l[l.IndexOf("--payload-dir") + 1] = payload;
            l.AddRange(new[] { "--h2ek", r.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--lock-wait-min", "1", "--lock-poll-s", "0.5" }); l.AddRange(extra);
            return C.Run(l);
        }

        private static void Df64Bake()
        {
            const string Var = "GPULM_COMPAT";
            string newP = C.BuildPayload(C.Sb.Dir("payload_e2e_df64"), compatEngine: true), oldP = C.PayloadDir;
            var args = new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install" };
            Func<string, TestRoot, string[], CliResult> go = (p, r, extra) => DfBake(p, r, args.Concat(extra).ToArray());
            // 1. a GPU with FP64 (the engine says 'off'): nothing passed, the header says 'automatic' (the GPU check is skipped here), the summary 'Full FP64'
            var r1 = C.NewRoot();
            var a = Ok(go(newP, r1, new string[0]), "automatic, FP64 GPU");
            Assert.Eq(0, r1.Calls("gpu_bake").Single().Arr("gpulm").Count, "nothing in gpu_bake's GPULM_*: the variable is never passed by default");
            Assert.Contains(a.Out, "precision " + GpuCheck.PrecisionAutomatic, "the plan in the log header: precision line");
            Assert.Contains(a.Out, "Precision: automatic", "the pre-run line");
            Assert.True(a.Lines.Any(x => x.StartsWith("Precision: Full FP64   [engine: Compatibility mode: off", StringComparison.Ordinal)), "the run summary (last lines): " + string.Join(" | ", a.Lines.Skip(Math.Max(0, a.Lines.Count - 4))));
            var sa = RunStatus.Load(RunDirOf(a)).Extra.Obj("precision");
            Assert.Eq("Precision: Full FP64", sa.Str("text"), "status.json precision.text");
            Assert.Eq(true, sa.Bool("engineHasCompatMode"), "status.json: the engine has the mode");
            Assert.Contains(File.ReadAllText(Path.Combine(RunDirOf(a), "bake.log")), "precision| Precision: Full FP64", "bake.log carries the summary too");
            // 2. a GPU without FP64 (the engine chose the DF64 kernels itself): still nothing passed; the summary says Compatibility mode, from the engine's own line
            var r2 = C.NewRoot(); r2.Control("gpu_bake=nofp64");
            var b = Ok(go(newP, r2, new string[0]), "automatic, GPU without FP64");
            Assert.Eq(0, r2.Calls("gpu_bake").Single().Arr("gpulm").Count, "still nothing passed");
            Assert.True(b.Lines.Any(x => x.StartsWith(GpuCheck.PrecisionCompat + "   [engine: Compatibility mode (no FP64): fake", StringComparison.Ordinal)), "summary: " + string.Join(" | ", b.Lines.Skip(Math.Max(0, b.Lines.Count - 4))));
            Assert.Eq("Precision: Compatibility mode (DF64) - your GPU has no FP64, using FP32 double-single maths instead. Experimental; same results in testing, may be slower.",
                      RunStatus.Load(RunDirOf(b)).Extra.Obj("precision").Str("text"), "status.json: the exact sentence");
            // 3. --force-compat: GPULM_COMPAT=1 reaches gpu_bake (bound by the fake's validation), the options line + plan + summary say FORCED
            var r3 = C.NewRoot();
            var c = Ok(go(newP, r3, new[] { "--force-compat" }), "forced");
            Assert.Seq(new[] { Var + "=1" }, r3.Calls("gpu_bake").Single().Arr("gpulm").Cast<string>().ToList(), "gpu_bake's environment: exactly " + Var + "=1");
            Assert.Contains(c.Out, "compatibility mode (DF64) FORCED", "the options line / log header");
            Assert.Contains(c.Out, "    env+  GPULM_COMPAT=1", "the plan lists the step's env");
            Assert.Contains(c.Out, "*** GPULM_COMPAT=1: the FP32-only double-single (df64) kernel set is FORCED", "the engine accepted it (its own validation note)");
            Assert.True(c.Lines.Any(x => x.StartsWith(GpuCheck.PrecisionCompatForced + "   [engine: Compatibility mode (no FP64): fake", StringComparison.Ordinal)), "summary: forced, with the engine's line");
            Assert.Eq(true, RunStatus.Load(RunDirOf(c)).Extra.Obj("precision").Bool("forced"), "status.json: forced");
            // with AO the trace (which runs gpu_bake -AOOnly itself) gets it; apply / stage do not
            var r4 = C.NewRoot();
            Ok(go(newP, r4, new[] { "--force-compat", "--ao" }), "forced + AO");
            var ao = r4.Calls("gpu_ao");
            Assert.Seq(new[] { "trace", "apply", "stage" }, ao.Select(x => x.Arr("argv").Cast<string>().ElementAt(1)).ToList(), "verbs");
            Assert.True(ao[0].Arr("gpulm").Cast<string>().Contains(Var) && !ao[1].Arr("gpulm").Cast<string>().Contains(Var) && !ao[2].Arr("gpulm").Cast<string>().Contains(Var), "only the AO trace carries " + Var);
            Assert.Seq(new[] { Var + "=1" }, r4.Calls("gpu_bake").Single().Arr("gpulm").Cast<string>().ToList(), "and the lightmap gpu_bake");
            // 4. an engine without the mode: --force-compat is refused before anything starts; the unforced bake passes nothing and states what it needs
            var r5 = C.NewRoot();
            var bad = go(oldP, r5, new[] { "--force-compat" });
            Assert.Eq(2, bad.Exit, "refused: " + bad);
            Assert.Contains(bad.Out, "RESULT REFUSED", "plain result word");
            Assert.Contains(bad.Out, "--force-compat is not available", "plain reason");
            Assert.Contains(bad.Out, "Compatibility mode needs engine 0.6.4 or newer.", "and the requirement");
            Assert.Eq(0, r5.Calls("gpu_bake").Count, "gpu_bake was never started");
            Assert.True(!Directory.Exists(r5.Farm(200)), "no farm folder made");
            var fine = Ok(go(oldP, r5, new string[0]), "old engine, unforced");
            Assert.Eq(0, r5.Calls("gpu_bake").Single().Arr("gpulm").Count, "nothing passed to the old engine");
            Assert.True(!fine.Out.Contains(Var) && !fine.Out.Contains("[engine:"), "and its log never mentions " + Var);
            Assert.True(fine.Lines.Any(x => x == GpuCheck.PrecisionNeedsFp64), "its summary says it needs a GPU with FP64: " + string.Join(" | ", fine.Lines.Skip(Math.Max(0, fine.Lines.Count - 4))));
        }

        // X2 e2e: --responsive reaches the real gpu_bake invocation (the fake records its bound parameters); an engine device-loss line on a failed bake
        // becomes the plain TDR explanation in the result (and an ordinary failure does not)
        private static void X2ResponsiveAndTdr()
        {
            var r = C.NewRoot();
            var off = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install");
            Ok(off, "unticked bake");
            var bOff = r.Calls("gpu_bake").Single();
            Assert.True(!bOff.Arr("argv").Cast<string>().Contains("-Responsive"), "unticked: gpu_bake got no -Responsive");
            Assert.True(!bOff.Arr("bound").Cast<string>().Any(x => x.StartsWith("Responsive=")), "unticked: nothing bound");
            Assert.True(!off.Out.Contains("responsive on"), "unticked: the log does not say 'responsive on'");
            var r2 = C.NewRoot();
            var on = C.Bake(r2, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--no-install", "--responsive");
            Ok(on, "responsive bake");
            var bOn = r2.Calls("gpu_bake").Single();
            Assert.True(bOn.Arr("argv").Cast<string>().Contains("-Responsive"), "ticked: gpu_bake got -Responsive: " + string.Join(" ", bOn.Arr("argv").Cast<string>()));
            Assert.True(bOn.Arr("bound").Cast<string>().Contains("Responsive=True"), "ticked: the switch bound");
            Assert.Contains(on.Out, "responsive on", "the plan / log shows 'responsive on'");
            // the device-loss lines -> the plain explanation
            var r3 = C.NewRoot();
            r3.Control("gpu_bake=fail_tdr\n");
            var tdr = C.Bake(r3, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(3, tdr.Exit, "failed: " + tdr);
            var resultLine = tdr.Lines.First(x => x.StartsWith("RESULT FAILED", StringComparison.Ordinal));
            Assert.Contains(tdr.Out, H2GpuLightmapper.Core.Run.DeviceLoss.Explanation, "the plain TDR explanation is shown");
            Assert.Contains(tdr.Out, "Details: the bake stopped", "the technical reason is kept after it");
            Assert.True(tdr.Out.IndexOf(H2GpuLightmapper.Core.Run.DeviceLoss.Explanation, StringComparison.Ordinal) >= 0 && resultLine.Contains("Windows reset the GPU"), "in the RESULT line: " + resultLine);
            var r4 = C.NewRoot();
            r4.Control("gpu_bake=fail_bake\n");
            var plain = C.Bake(r4, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(3, plain.Exit, "failed: " + plain);
            Assert.True(!plain.Out.Contains("Windows reset the GPU"), "an ordinary failure gets no TDR text");
            Assert.Contains(plain.Out, "PHASE 3: the fake bake failed on purpose", "its own reason");
        }

        private static void FailBake()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            r.Control("gpu_bake=fail_bake\n");
            var res = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(3, res.Exit, "failed: " + res);
            Assert.Contains(res.Out, "PHASE 3: the fake bake failed on purpose", "gpu_bake's own reason");
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "untouched " + kv.Key);
            Assert.True(File.Exists(Path.Combine(r.Farm(200), "standalone_capture.json")), "the capture finished before the failure: recorded");
            Assert.True(!File.Exists(Path.Combine(r.Work, ".gpu_bake.lock")), "lock released");
        }

        private static void RestoreRecover()
        {
            var r = C.NewRoot();
            var orig = SetMd5(r.CycDir, CycPrefix);
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "install");
            var l = C.Base("restore", false); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron", "--list" });
            var ls = C.Run(l);
            Assert.Eq(0, ls.Exit, "list: " + ls);
            Assert.Contains(ls.Out, "cyclotron_cyclotron_", "the backup is listed");
            l = C.Base("restore", false); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron" });
            var rs = C.Run(l);
            Assert.Eq(0, rs.Exit, "restore: " + rs);
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "restored " + kv.Key);
            // an install that "did not finish": put the last install's marker back
            Ok(C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap"), "install again");
            string farm = r.Farm(200);
            var done = Directory.GetFiles(farm, "writeback_in_progress.txt.done_*").OrderBy(x => x).Last();
            File.Copy(done, Path.Combine(farm, "writeback_in_progress.txt"));
            var refused = C.Bake(r, "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap");
            Assert.Eq(2, refused.Exit, "bake refused while an install is unfinished: " + refused);
            Assert.Contains(refused.Out, "did not finish", "reason");
            l = C.Base("restore"); l.AddRange(new[] { "--h2ek", r.Root, "--recover" });
            var rec = C.Run(l);
            Assert.Eq(0, rec.Exit, "recover: " + rec);
            foreach (var kv in orig) Assert.Eq(kv.Value, Md5(Path.Combine(r.CycDir, kv.Key)), "recovered to the pre-install " + kv.Key);
            Assert.True(!File.Exists(Path.Combine(farm, "writeback_in_progress.txt")), "marker handled (renamed .restored_)");
        }

        private static void LockWait()
        {
            var r = C.NewRoot();
            Directory.CreateDirectory(r.Work);
            string lk = Path.Combine(r.Work, ".gpu_bake.lock");
            File.WriteAllText(lk, "RESEARCH bake by another workflow");
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dev-skip-preflight", "--dev-skip-gpucheck", "--lock-wait-min", "0.05", "--lock-poll-s", "0.5",
                               "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" });
            var res = C.Run(l);
            Assert.Eq(3, res.Exit, "gave up: " + res);
            Assert.Contains(res.Out, "held the GPU lock", "reason");
            Assert.Contains(File.ReadAllText(Path.Combine(RunDirOf(res), "bake.log")), "waiting for the GPU lock held by: RESEARCH bake by another workflow", "WAITING named the holder");
            Assert.Eq("RESEARCH bake by another workflow", File.ReadAllText(lk), "the foreign lock is untouched");
            Assert.Eq(0, r.Calls("gpu_bake").Count, "nothing ran");
        }

        private static void DoctorStatus()
        {
            var r = C.NewRoot(withTool: false);
            var l = C.Base("doctor"); l.AddRange(new[] { "--h2ek", r.Root, "--no-gpu", "--json" });
            var d = C.Run(l);
            Assert.Eq(2, d.Exit, "doctor finds problems (no tool_fast): " + d);
            var j = d.Json();
            Assert.Eq(true, j.Bool("runtimeOk"), "runtime extracted + quick-checked");
            var items = j.Obj("preflight").Arr("items").OfType<JObj>().ToList();
            Assert.Eq("fail", items.Single(i => i.Str("id") == "tool.build").Str("level"), "tool_fast missing reported");
            l = C.Base("doctor"); l.Add("--json");
            var d2 = C.Run(l);
            Assert.Eq(0, d2.Exit, "doctor without a root lists the H2EK folders: " + d2);
            Assert.True(d2.Json().Arr("roots") != null, "roots listed");
            l = C.Base("status", false); l.AddRange(new[] { "--h2ek", r.Root });
            var s = C.Run(l);
            Assert.Eq(0, s.Exit, "status");
            Assert.Contains(s.Out, "current run: None", "no run");
            var c = CancelVerb(r);
            Assert.Eq(2, c.Exit, "cancel without a run: " + c);
            // the real preflight (no dev flag) refuses before anything is written
            var before = Directory.GetFileSystemEntries(r.Tags, "*", SearchOption.AllDirectories).Select(f => f + "|" + (File.Exists(f) ? Md5(f) : "d")).ToList();
            l = C.Base("bake"); l.AddRange(new[] { "--h2ek", r.Root, "--scenario", Cyc, "--bsp", "cyclotron" });
            var p = C.Run(l);
            Assert.Eq(2, p.Exit, "preflight refuses: " + p);
            Assert.Contains(p.Out, "preflight:", "reason");
            Assert.Contains(p.Out, "tool_fast", "tool_fast named");
            Assert.Seq(before, Directory.GetFileSystemEntries(r.Tags, "*", SearchOption.AllDirectories).Select(f => f + "|" + (File.Exists(f) ? Md5(f) : "d")).ToList(), "tags unchanged");
            Assert.True(!Directory.Exists(Path.Combine(r.Work, @"standalone\runs")) && !Directory.Exists(r.Farm(200)), "no run folder, no farm");
            var help = C.Run(new[] { "--cli", "help" });
            Assert.Eq(0, help.Exit, "help");
            Assert.Contains(help.Out, "Exit codes: 0 ok, 2 refused before any write, 3 failed, 4 cancelled", "usage");
            Assert.Eq(2, C.Run(new[] { "--cli", "frob" }).Exit, "unknown verb");
        }
    }
}
