// 0.6.8 bounded temp (docs\design\DISK_USAGE_0.6.8.md 5.1-5.8): TempJanitor, TempLease, the link-aware remover, the KEEP markers and
// the DLL-slot alias of WORK\Temp (safe to delete). Everything happens in this run's sandbox (build\s2\t\<id>, %TEMP%\s2\<xx>); no GPU, no
// bake, no tool_fast, no live-tag access.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    internal static class TempJanitorTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("run folder (0.6.8 RC): a scenario / BSP with spaces (and tabs) gives a run label and tag without whitespace; the GPU lock takes it; NameMatches finds the run", SpacedRunLabel);
            yield return T("temp: reparse-safe delete: a junction to a sentinel folder is never followed (refuse / tripwire / unlink-only), the sentinel survives", JunctionSentinel);
            yield return T("temp: a .map run folder whose skeleton links point inside it is removed whole; a long (> 260 chars) tree is removed", MapRunInternalLinksAndLongPath);
            yield return T("temp: after a successful bake the farm is slimmed to its capture (engine product list), the logs go to RUN\\farm_logs; a map farm is deleted whole", AfterBakeSlim);
            yield return T("temp: a failed bake keeps its stage in RUN\\failed; failed-run bundles are kept for the newest 3 runs only; tagbak for the newest 10 per map", FailedRunsKeep3);
            yield return T("temp: the capture cache keeps the newest 3 captures; the cap evicts the oldest first and never a live folder", CaptureCacheAndCap);
            yield return T("temp: start-up sweep: an orphan .lock goes at once, a live one stays; lock-less .map > 24 h / query > 1 h / farm without record > 24 h go; younger stay; a live GPU lock skips the sweep", SweepRules);
            yield return T("temp: Clean now empties Temp but keeps a live folder and the READ ME", CleanNowKeepsLive);
            yield return T("backups: _TAGBAK_ui keeps the original + the newest 5 per set; .tmp_ and marker-named backups are never pruned", UiBackupsKeep);
            yield return T("backups: _TAGBAK_auto: only the folders this app's fallback made are pruned (newest 3 per prefix); older ones are listed by the opt-in cleanup only", AutoBackups);
            yield return T("legacy: the opt-in list names exactly this app's 0.6.7 folders (registry farm with a capture record, _mapbake, its skeletons) and never a research farm; skeleton links are unlinked, never followed", LegacyListAndRemove);
            yield return T("temp: TempLease: .lock held (live), sentinel deleted -> Gone; released -> not live, files removed", LeaseAndGone);
            yield return T("recovery: the KEEP install / tool write-back markers are found after Temp is deleted (and a Temp farm marker too)", KeepMarkers);
            yield return T("slot alias: lives outside Temp and points at it; deleting Temp (even mid-bake) never traverses it, a dangling alias is harmless, the next bake re-creates Temp and reuses it", SlotAliasAndTempDeletion);
            yield return T("temp: the farm product list refuses paths, the capture record and skeleton; CaptureRecordName is the shell's capture record name", ProductsListRules);
            yield return T("paths: 0.6.8 path budget on Steam's default H2EK path (farm in Temp, .map skeleton, .map overlay MAX_PATH figures)", PathBudget068);
            yield return T("paths (RC F1): the tool write-back fallback's backups and the failed-run copy only WARN; the 95-character validation root bakes again (0.6.7 tolerance); FallbackOver names the fallback path", PathBudgetFallbackWarns);
            yield return T("temp (RC F2): the end-of-bake cleanup removes the skeleton overlay of a --worker N that workers.json does not name (links unlinked, never followed); other skeletons stay", SkeletonOfDevWorker);
        }

        private static void PathBudgetFallbackWarns()
        {
            // the 0.6.8 GPU validation's root inside the user's Temp folder (95 characters) was refused on triplicate: farm install backup 251
            string root = @"D:\" + new string('h', 92);        // the same length as that root
            Assert.Eq(95, root.Length, "the validation root length");
            string scen = @"scenarios\multi\triplicate\triplicate", bsp = "triplicate";
            var e = PathBudget.Estimate(root, "0.6.8-12345678", 66, scen, bsp, true);      // the RC INST: maxPathLen 66
            var fb = e.First(x => x.What == "farm install backup");
            Assert.True(fb.FallbackOnly && !fb.Required && fb.Length >= PathBudget.Limit, "the farm install backup is fallback-only and over the limit here: " + fb.Length);
            Assert.True(e.First(x => x.What == "auto backup").FallbackOnly, "_TAGBAK_auto is fallback-only (the shell's install no longer writes it)");
            Assert.True(e.First(x => x.What == "failed-run stage copy").BestEffort, "the failed-run stage copy is best effort");
            var req = PathBudget.LongestRequired(e);
            Assert.True(req.Length < PathBudget.Limit, "every REQUIRED path fits: longest " + req.What + " " + req.Length);
            var over = PathBudget.FallbackOver(root, scen, bsp, true);
            Assert.True(over != null && over.What == "farm install backup" && over.Length == fb.Length, "FallbackOver names the fallback path");
            Assert.True(PathBudget.FallbackOver(@"C:\Program Files (x86)\Steam\steamapps\common\H2EK", scen, bsp, true) == null, "Steam's default path: the fallback fits");
            // preflight: a warning (path.fallback), the budget itself OK
            string sb = C.Short.Dir("rcf1");
            string deepRoot = sb + "\\" + new string('r', Math.Max(1, 95 - sb.Length - 1));
            Directory.CreateDirectory(deepRoot);
            Assert.Eq(95, deepRoot.Length, "a 95-character sandbox root");
            var o = new PreflightOptions { ProbeWrite = false, RunToolSmoke = false, InstDirName = "0.1.0-12345678", InstMaxPathLen = 66, ScenarioRel = scen, Bsp = bsp,
                                           FarmsInTemp = true, AliasLocalAppData = C.Short.Dir("PL"), AliasVolumeRoot = C.Short.Dir("PV") };
            var rep = PreflightChecks.Run(deepRoot, o);
            var b = rep.Get("path.budget");
            Assert.Eq(CheckLevel.Ok, b.Level, "95-character root + triplicate: the budget passes (" + b.Message + ")");
            var w = rep.Get("path.fallback");
            Assert.True(w != null && w.Level == CheckLevel.Warn && w.Message.Contains("writes nothing to your tags"), "the fallback paths are a warning: " + (w == null ? "none" : w.Message));
            // a truly deep root is still refused by the required paths
            var deep = PreflightChecks.Run(C.Sb.Dir(Path.Combine("rcf1deep", new string('d', 60), new string('e', 60))), o).Get("path.budget");
            Assert.Eq(CheckLevel.Fail, deep.Level, "a deep root is still refused: " + deep.Message);
        }

        private static void SpacedRunLabel()
        {
            string scen = @"scenarios\multi\bounce progression test\bounce progression test";
            string label = H2GpuLightmapper.Core.Run.RunFolder.LabelFor(scen, "bounce\tbsp");
            Assert.True(label.IndexOf(' ') < 0 && label.IndexOf('\t') < 0, "label without whitespace: '" + label + "'");
            Assert.Eq("bounce_progression_t", label, "the label (20 characters)");
            string tag = H2GpuLightmapper.Core.Run.RunFolder.TagFor(new DateTime(2026, 10, 31, 12, 14, 9), scen, "bounce\tbsp");
            Assert.Eq("20261031_121409_bounce_progression_t", tag, "the run tag");
            var lk = new GpuLock(Path.Combine(C.Sb.Dir("spacedlock"), GpuLock.FileName), tag, scen);
            Assert.True(lk.Content.Contains(" " + tag + " ") && lk.Content.Contains("bounce_progression_test"), "the lock line: " + lk.Content);
            Assert.True(H2GpuLightmapper.Core.Run.RunFolder.NameMatches(tag, scen, "bounce\tbsp"), "NameMatches finds the run");
            bool threw = false;
            try { new GpuLock(Path.Combine(C.Sb.Dir("spacedlock"), GpuLock.FileName), "a b", scen); } catch (ArgumentException) { threw = true; }
            Assert.True(threw, "the lock still refuses a tag with a space (the label makes sure it never gets one)");
        }

        private static void SkeletonOfDevWorker()
        {
            string work = NewWork("sk2");
            string sent = Sentinel("sk2");
            SafeFs.WriteAllTextAtomic(Path.Combine(ShellPaths.StandaloneOf(work), "workers.json"), "{\"scenarios\\\\multi\\\\a\\\\a|a\": 200}");
            string dev = Path.Combine(work, @"_gpulm_skel\w262"), reg = Path.Combine(work, @"_gpulm_skel\w200"), other = Path.Combine(work, @"_gpulm_skel\w263");
            foreach (var s in new[] { dev, reg, other })
            {
                Junc(Path.Combine(s, @"tags\objects"), sent);
                File.WriteAllText(Path.Combine(s, "skel_key_request.json"), "{}");
            }
            // before the fix: the registry filter kept the dev worker's overlay (and its links into the tags) forever
            TempJanitor.RemoveSkeletons(work, new[] { 262 });
            Assert.True(Directory.Exists(dev), "without thisBake a non-registry worker is never touched (research protection, C7)");
            var rep = TempJanitor.RemoveSkeletons(work, new[] { 262 }, true);
            Assert.True(rep.Problems.Count == 0, "removed: " + string.Join("; ", rep.Problems));
            Assert.True(!Directory.Exists(dev), "this bake's --worker 262 overlay is removed");
            Assert.True(Directory.Exists(reg) && Directory.Exists(other), "the registry worker 200 and another worker 263 (not this bake's) stay");
            SentinelIntact(sent, "after the dev-worker skeleton removal");
            foreach (var s in new[] { reg, other }) SafeFs.UnlinkAll(s);
        }

        // ------------------------------------------------------------------ helpers

        private static string NewWork(string tag)
        {
            string work = C.Sb.Dir(tag + @"\GPU Lightmap Work");
            TempJanitor.EnsureTemp(work);
            return work;
        }

        private static string Temp(string work) { return ShellPaths.TempOf(work); }

        private static string Farm(string work, int w, bool record = true, DateTime? recTime = null, int kb = 4)
        {
            string f = Path.Combine(Temp(work), "farm_w" + w.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(f);
            File.WriteAllBytes(Path.Combine(f, "texelgen_soup.bin"), new byte[kb * 1024]);
            if (record)
            {
                string r = Path.Combine(f, TempJanitor.CaptureRecordName);
                File.WriteAllText(r, "{\"schema\":\"h2lm-standalone-capture/1\"}");
                if (recTime.HasValue) File.SetLastWriteTime(r, recTime.Value);
            }
            return f;
        }

        private static string Sentinel(string tag)
        {
            string s = C.Sb.Dir(tag + @"\SENTINEL_live_tags");
            File.WriteAllText(Path.Combine(s, "do_not_delete.txt"), "sentinel " + tag);
            Directory.CreateDirectory(Path.Combine(s, "sub"));
            File.WriteAllText(Path.Combine(s, "sub", "deep.txt"), "deep");
            return s;
        }

        private static void SentinelIntact(string s, string what)
        {
            Assert.True(File.Exists(Path.Combine(s, "do_not_delete.txt")) && File.Exists(Path.Combine(s, "sub", "deep.txt")), what + ": the sentinel folder is intact");
        }

        private static string Junc(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            Junction.Create(link, target);
            C.Sb.TrackJunction(link);
            return link;
        }

        private static void Final(string runDir)
        {
            new RunStatus { RunDir = runDir, RunTag = Path.GetFileName(runDir), State = RunState.Done }.Save();
        }

        private static string Products()
        {
            string p = Path.Combine(C.Project, @"src\standalone\farm_products.json");
            Assert.True(File.Exists(p), "the shipped product list exists: " + p);
            return p;
        }

        // ------------------------------------------------------------------ tests

        private static void JunctionSentinel()
        {
            string work = NewWork("js");
            string sent = Sentinel("js");
            string farm = Farm(work, 250);
            Junc(Path.Combine(farm, @"tags\scenarios"), sent);
            // 1. the plain remover refuses before deleting anything
            Assert.Throws<InvalidOperationException>(() => SafeFs.RemoveTree(farm, Temp(work), false), "plain remover refuses a link");
            Assert.True(File.Exists(Path.Combine(farm, "texelgen_soup.bin")), "nothing was deleted (plain)");
            // 2. the unlink-inside mode refuses a link that leads OUTSIDE the folder (the tripwire), before unlinking anything
            var e = Assert.Throws<InvalidOperationException>(() => SafeFs.RemoveTree(farm, Temp(work), true), "tripwire");
            Assert.Contains(e.Message, "tripwire", "the refusal says tripwire");
            Assert.True(Junction.IsJunction(Path.Combine(farm, @"tags\scenarios")) && File.Exists(Path.Combine(farm, "texelgen_soup.bin")), "nothing unlinked or deleted (tripwire)");
            // 3. the janitor (sweep / Clean now / cap) goes through the same remover: refused, reported, sentinel intact
            var rep = TempJanitor.CleanNow(work);
            Assert.True(rep.Problems.Count == 1 && Directory.Exists(farm), "Clean now refused that farm: " + string.Join("; ", rep.Problems));
            SentinelIntact(sent, "after the refusals");
            // 4. UnlinkAll removes the link itself only; then the tree goes
            int n = SafeFs.UnlinkAll(farm);
            Assert.Eq(1, n, "one link unlinked");
            SentinelIntact(sent, "after UnlinkAll");
            SafeFs.RemoveTree(farm, Temp(work), false);
            Assert.True(!Directory.Exists(farm), "farm removed");
            SentinelIntact(sent, "after the farm was removed");
            // 5. a junction to the sentinel placed directly in Temp (never made by the app) is not deleted through either
            string tl = Junc(Path.Combine(Temp(work), "farm_w251"), sent);
            var rep2 = TempJanitor.CleanNow(work);
            Assert.True(Junction.IsJunction(tl), "a link at Temp's top level is left alone (RemoveTree refuses a link root)");
            SentinelIntact(sent, "after Clean now with a top-level link");
            Junction.Remove(tl);
        }

        private static void MapRunInternalLinksAndLongPath()
        {
            string work = NewWork("mr");
            string sent = Sentinel("mr");
            string m = Path.Combine(Temp(work), "m3f9a1c");
            string inner = Path.Combine(m, @"r\tags\scenarios\multi");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(inner, "x.scenario"), "overlay copy");
            Junc(Path.Combine(m, @"r\w\_gpulm_skel\w250\tags\scenarios\multi"), inner);
            // a deep tree past MAX_PATH (a .map overlay of the whole kit has those)
            string deep = SafeFs.Long(Path.Combine(m, @"r\tags"));
            while (deep.Length < 300) deep = Path.Combine(deep, "a_long_folder_name_for_the_path_test");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "deep.bitmap"), "x");
            var it = TempJanitor.List(work).Single(i => i.Name == "m3f9a1c");
            Assert.Eq(TempItemKind.MapRun, it.Kind, "classified as a .map run");
            var rep = new JanitorReport();
            Assert.True(TempJanitor.RemoveItem(work, it, rep, "test"), "removed: " + string.Join("; ", rep.Problems));
            Assert.True(!Directory.Exists(m), "the whole m<id> is gone (internal links unlinked, long paths deleted)");
            SentinelIntact(sent, "map run");
            // the same folder with ONE link to the outside is refused whole (tripwire), nothing deleted
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(inner, "x.scenario"), "overlay copy");
            Junc(Path.Combine(m, @"r\w\_gpulm_skel\w250\tags\scenarios\multi"), inner);
            string bad = Junc(Path.Combine(m, @"r\w\_gpulm_skel\w250\tags\objects"), sent);
            var it2 = TempJanitor.List(work).Single(i => i.Name == "m3f9a1c");
            var rep2 = new JanitorReport();
            Assert.True(!TempJanitor.RemoveItem(work, it2, rep2, "test"), "refused");
            Assert.True(File.Exists(Path.Combine(inner, "x.scenario")) && Junction.IsJunction(bad), "nothing was deleted or unlinked");
            SentinelIntact(sent, "tripwire in a map run");
            SafeFs.UnlinkAll(m);
            SafeFs.RemoveTree(m, Temp(work), false);
        }

        private static void AfterBakeSlim()
        {
            string work = NewWork("ab");
            string farm = Farm(work, 252);
            foreach (var n in new[] { "direct_out.bin", "gather_out.bin", "fullgpu_key.bin", "fullgpu.exe", "seam_lists.bin.stale_20261029_010101", "lights.bin", "surf_gpu.bin" })
                File.WriteAllBytes(Path.Combine(farm, n), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(farm, @"parts\p0"));
            File.WriteAllBytes(Path.Combine(farm, @"parts\p0\gather_out.bin"), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(farm, "tagw_stage"));
            File.WriteAllText(Path.Combine(farm, @"tagw_stage\tagw_report.json"), "{}");
            Directory.CreateDirectory(Path.Combine(farm, "skeleton"));
            File.WriteAllBytes(Path.Combine(farm, @"skeleton\skel.bin"), new byte[100]);
            File.WriteAllText(Path.Combine(farm, "gpu_bake.log"), "log line");
            string mapFarm = Farm(work, 253, false);
            string run = C.Sb.Dir(@"ab\GPU Lightmap Work\standalone\runs\20261029_010000_ab_x");
            Final(run);
            var rep = TempJanitor.AfterBake(new AfterBakeRequest
            {
                Work = work, RunDir = run, Success = true, ProductsJson = Products(),
                Farms = { new KeyValuePair<string, bool>(farm, false), new KeyValuePair<string, bool>(mapFarm, true) },
            });
            foreach (var n in new[] { "direct_out.bin", "gather_out.bin", "fullgpu_key.bin", "fullgpu.exe", "seam_lists.bin.stale_20261029_010101" })
                Assert.True(!File.Exists(Path.Combine(farm, n)), n + " slimmed");
            Assert.True(!Directory.Exists(Path.Combine(farm, "parts")) && !Directory.Exists(Path.Combine(farm, "tagw_stage")), "parts\\ and tagw_stage\\ slimmed");
            foreach (var n in new[] { "texelgen_soup.bin", "lights.bin", "surf_gpu.bin", TempJanitor.CaptureRecordName, @"skeleton\skel.bin" })
                Assert.True(File.Exists(Path.Combine(farm, n)), n + " kept (capture / derive / skeleton)");
            Assert.True(File.Exists(Path.Combine(run, @"farm_logs\farm_w252\gpu_bake.log")), "log bundle copied to KEEP");
            Assert.True(!Directory.Exists(mapFarm), "a .map farm is deleted whole");
            // keep 0 = no capture kept
            var rep0 = TempJanitor.AfterBake(new AfterBakeRequest { Work = work, RunDir = run, Success = true, ProductsJson = Products(), Settings = new TempSettings { KeepCaptures = 0 }, Farms = { new KeyValuePair<string, bool>(farm, false) } });
            Assert.True(!Directory.Exists(farm), "Keep captures 0 deletes the farm whole");
            // a payload without the list never keeps a farm
            string f3 = Farm(work, 254);
            TempJanitor.AfterBake(new AfterBakeRequest { Work = work, RunDir = run, Success = true, ProductsJson = null, Farms = { new KeyValuePair<string, bool>(f3, false) } });
            Assert.True(!Directory.Exists(f3), "no product list: the farm is deleted whole");
            // a Temp the user already cleared is tolerated
            var rep4 = TempJanitor.AfterBake(new AfterBakeRequest { Work = work, RunDir = run, Success = false, ProductsJson = Products(), Farms = { new KeyValuePair<string, bool>(Path.Combine(Temp(work), "farm_w255"), false) } });
            Assert.True(rep4.Problems.Count == 0 && rep4.Lines.Any(l => l.Contains("already gone")), "a vanished farm is reported, not an error");
        }

        private static void FailedRunsKeep3()
        {
            string work = NewWork("fr");
            string runs = RunFolder.RunsDir(work);
            var dirs = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string run = Path.Combine(runs, "2026102" + i + "_010000_fr_x");
                Directory.CreateDirectory(run);
                Final(run);
                string farm = Farm(work, 256);
                Directory.CreateDirectory(Path.Combine(farm, "tagw_stage"));
                File.WriteAllText(Path.Combine(farm, @"tagw_stage\fr_x_lightmap.scenario_structure_lightmap"), "stage " + i);
                TempJanitor.AfterBake(new AfterBakeRequest { Work = work, RunDir = run, Success = false, ProductsJson = Products(), Farms = { new KeyValuePair<string, bool>(farm, false) } });
                Assert.True(File.Exists(Path.Combine(run, @"failed\farm_w256_tagw_stage\fr_x_lightmap.scenario_structure_lightmap")), "run " + i + ": its stage is kept in KEEP RUN\\failed");
                dirs.Add(run);
            }
            var with = dirs.Where(d => Directory.Exists(Path.Combine(d, TempJanitor.FailedDir))).ToList();
            Assert.Eq(3, with.Count, "failed bundles are kept for 3 runs");
            Assert.Seq(dirs.Skip(2).ToList(), with, "the newest 3");
            Assert.True(dirs.All(d => File.Exists(Path.Combine(d, "status.json"))), "status.json always stays");
            // tagbak: newest 10 per map label
            for (int i = 0; i < 12; i++)
            {
                string run = Path.Combine(runs, "202611" + i.ToString("D2") + "_010000_tb_y");
                Directory.CreateDirectory(Path.Combine(run, RunFolder.BackupDirName));
                File.WriteAllText(Path.Combine(run, RunFolder.BackupDirName, "a.bitmap"), "b");
                Final(run);
            }
            TempJanitor.PruneRuns(work);
            var tb = Directory.GetDirectories(runs, "*_tb_y").Where(d => Directory.Exists(Path.Combine(d, RunFolder.BackupDirName))).ToList();
            Assert.Eq(10, tb.Count, "tagbak kept for the newest 10 runs of the map");
            Assert.True(!Directory.Exists(Path.Combine(runs, "20261100_010000_tb_y", RunFolder.BackupDirName)), "the oldest lost its tagbak");
        }

        private static void CaptureCacheAndCap()
        {
            string work = NewWork("cc");
            var t0 = DateTime.Now.AddHours(-10);
            for (int i = 0; i < 5; i++) Farm(work, 260 + i, true, t0.AddHours(i), 64);
            TempJanitor.TrimCaptures(work, 3);
            var left = TempJanitor.List(work).Where(x => x.Kind == TempItemKind.Farm).Select(x => x.Worker).OrderBy(x => x).ToList();
            Assert.Seq(new List<string> { "262", "263", "264" }, left.Select(x => x.ToString()).ToList(), "the newest 3 captures stay");
            // cap: 3 x 64 KB + a live farm; cap 130 KB -> the oldest goes first, the live one never
            string live = Farm(work, 265, true, t0.AddHours(-5), 64);           // the OLDEST, but live
            using (var lease = TempLease.Acquire(live, "test"))
            {
                var rep = TempJanitor.EnforceCap(work, 130 * 1024);
                var now = TempJanitor.List(work).Where(x => x.Kind == TempItemKind.Farm).Select(x => x.Worker).OrderBy(x => x).ToList();
                Assert.True(now.Contains(265), "the live folder is never evicted");
                Assert.True(!now.Contains(262), "the oldest non-live capture went first");
                Assert.True(now.Contains(264), "the newest stays: " + string.Join(",", now));
                Assert.True(TempJanitor.TempSize(work) <= 130 * 1024 + 64 * 1024 + 4096, "under the cap except the live folder");
            }
        }

        private static void SweepRules()
        {
            string work = NewWork("sw");
            var now = DateTime.Now;
            Func<string, string> mk = n => { string d = Path.Combine(Temp(work), n); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "x.bin"), "x"); return d; };
            string orphan = mk("farm_w270"); File.WriteAllText(Path.Combine(orphan, TempLease.LockName), "dead bake");
            string live = mk("farm_w271");
            string mOld = mk("m00aa01"), mNew = mk("m00aa02"), qOld = mk("q00aa03"), qNew = mk("q00aa04"), fOld = mk("farm_w272"), fNew = mk("farm_w273");
            string cap = Farm(work, 274);
            Directory.SetLastWriteTime(mOld, now.AddHours(-25)); Directory.SetLastWriteTime(mNew, now.AddHours(-23));
            Directory.SetLastWriteTime(qOld, now.AddHours(-2)); Directory.SetLastWriteTime(qNew, now.AddMinutes(-30));
            Directory.SetLastWriteTime(fOld, now.AddHours(-30)); Directory.SetLastWriteTime(fNew, now.AddHours(-1));
            File.SetLastWriteTime(Path.Combine(cap, TempJanitor.CaptureRecordName), now.AddDays(-30));
            // a live GPU lock (this process, our format) skips the whole sweep
            var lk = new GpuLock(GpuLock.PathFor(work), "sweeptest", "x");
            LockInfo holder;
            Assert.True(lk.TryAcquireNow(out holder), "test GPU lock taken");
            var skipped = TempJanitor.Sweep(work, new TempSettings());
            lk.Release();
            Assert.True(skipped.Skipped && Directory.Exists(orphan) && Directory.Exists(mOld), "a live GPU lock: nothing swept (" + skipped.SkipReason + ")");
            using (TempLease.Acquire(live, "test"))
            {
                var rep = TempJanitor.Sweep(work, new TempSettings(), null, now);
                Assert.True(!Directory.Exists(orphan), "an orphan .lock (owner dead) goes at once");
                Assert.True(Directory.Exists(live), "a live .lock stays");
                Assert.True(!Directory.Exists(mOld) && Directory.Exists(mNew), "lock-less .map folder: > 24 h goes, < 24 h stays");
                Assert.True(!Directory.Exists(qOld) && Directory.Exists(qNew), "query folder: > 1 h goes, < 1 h stays");
                Assert.True(!Directory.Exists(fOld) && Directory.Exists(fNew), "lock-less farm without a capture record: > 24 h goes, < 24 h stays");
                Assert.True(Directory.Exists(cap), "a cached capture is not an orphan (only the cache count / cap remove it)");
                Assert.True(rep.Problems.Count == 0, "no problems: " + string.Join("; ", rep.Problems));
            }
        }

        private static void CleanNowKeepsLive()
        {
            string work = NewWork("cn");
            Farm(work, 280); Farm(work, 281);
            string live = Farm(work, 282);
            Directory.CreateDirectory(Path.Combine(Temp(work), "m00bb01"));
            using (TempLease.Acquire(live, "test"))
            {
                var dry = TempJanitor.CleanNow(work, true);
                Assert.True(Directory.Exists(Path.Combine(Temp(work), "farm_w280")), "a dry run deletes nothing");
                var rep = TempJanitor.CleanNow(work);
                var names = Directory.GetDirectories(Temp(work)).Select(Path.GetFileName).ToList();
                Assert.Seq(new List<string> { "farm_w282" }, names, "only the live folder is left");
                Assert.True(File.Exists(Path.Combine(Temp(work), ShellPaths.TempReadMeName)), "the READ ME stays");
            }
        }

        private static string UiBackup(string work, string leaf, string bsp, string prefix, DateTime t, string extra = "")
        {
            string d = Path.Combine(TagBackup.UiRoot(work), leaf + "_" + bsp + "_" + extra + t.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "backup.json"), Json.Write(new JObj().Add("prefix", prefix).Add("files", new List<object>())));
            return d;
        }

        private static void UiBackupsKeep()
        {
            string work = NewWork("ui");
            var t = new DateTime(2026, 10, 1, 10, 0, 0);
            var a = new List<string>();
            for (int i = 0; i < 8; i++) a.Add(UiBackup(work, "cyc", "cyc", "cyc_cyc_lightmap", t.AddDays(i), i == 5 ? "before_restore_" : ""));
            var other = Enumerable.Range(0, 3).Select(i => UiBackup(work, "tri", "tri", "tri_tri_lightmap", t.AddDays(i))).ToList();
            string tmp = Path.Combine(TagBackup.UiRoot(work), "cyc_cyc_20260901_000000" + TagBackup.TmpMarker + "abcd");
            Directory.CreateDirectory(tmp);
            // a backup an unfinished install marker names is never pruned
            string md = StartupRecovery.MarkersDir(work);
            Directory.CreateDirectory(md);
            File.WriteAllText(StartupRecovery.InstallMarkerPath(work, 290), Json.Write(new JObj().Add("target_dir", @"X:\H2EK\tags\x").Add("backups", new List<object> { a[2] }).Add("files", new JObj())));
            TempJanitor.PruneUiBackups(work);
            var left = a.Where(Directory.Exists).ToList();
            Assert.True(Directory.Exists(a[0]), "the original (oldest) stays");
            Assert.True(a.Skip(3).All(Directory.Exists), "the newest 5 stay (before_restore included)");
            Assert.True(Directory.Exists(a[2]), "the marker-named backup stays");
            Assert.True(!Directory.Exists(a[1]), "a middle one goes");
            Assert.Eq(7, left.Count, "original + newest 5 + the marker-named one");
            Assert.True(other.All(Directory.Exists) && Directory.Exists(tmp), "another set and a .tmp_ folder are untouched");
        }

        private static void AutoBackups()
        {
            string work = NewWork("au");
            string au = Path.Combine(work, "_TAGBAK_auto");
            Directory.CreateDirectory(au);
            string oldOne = Path.Combine(au, "20260101_000000_cyc_cyc_lightmap");
            Directory.CreateDirectory(oldOne);                                                 // made by 0.6.7: never pruned
            var before = TempJanitor.AutoBackupNames(work);
            var mine = new List<string>();
            for (int i = 0; i < 5; i++) { string d = Path.Combine(au, "2026102" + i + "_120000_cyc_cyc_lightmap"); Directory.CreateDirectory(d); mine.Add(d); }
            TempJanitor.RecordAutoBackupsSince(work, before);
            Assert.Eq(5, TempJanitor.AutoOwned(work).Count, "the fallback's 5 folders are recorded as ours");
            TempJanitor.PruneAutoBackups(work);
            Assert.True(Directory.Exists(oldOne), "a pre-existing (0.6.7) folder is never pruned");
            Assert.Seq(mine.Skip(2).ToList(), mine.Where(Directory.Exists).ToList(), "our newest 3 stay");
            var leg = TempJanitor.ListLegacy(work, null, null, false);
            Assert.True(leg.Any(l => l.Path == oldOne) && !leg.Any(l => mine.Contains(l.Path)), "the opt-in list names only the folders this app did not record");
        }

        private static void LegacyListAndRemove()
        {
            string work = NewWork("lg");
            string sent = Sentinel("lg");
            SafeFs.WriteAllTextAtomic(Path.Combine(ShellPaths.StandaloneOf(work), "workers.json"), "{\"scenarios\\\\multi\\\\a\\\\a|a\": 200, \"scenarios\\\\multi\\\\b\\\\b|b\": 201}");
            Func<int, bool, string> farm = (w, rec) =>
            {
                string f = Path.Combine(work, "farm_w" + w);
                Directory.CreateDirectory(f);
                File.WriteAllText(Path.Combine(f, "direct_out.bin"), "x");
                if (rec) File.WriteAllText(Path.Combine(f, TempJanitor.CaptureRecordName), "{}");
                return f;
            };
            string f200 = farm(200, true), f201 = farm(201, true), f262 = farm(262, true), f203 = farm(203, true);
            File.WriteAllText(Path.Combine(f201, StartupRecovery.MarkerName), "started ...");             // an unfinished install: not listed
            string skel = Path.Combine(work, @"_gpulm_skel\w200");
            Junc(Path.Combine(skel, @"tags\objects"), sent);
            string mb = Path.Combine(work, @"_mapbake\m20261028-134231");
            Directory.CreateDirectory(Path.Combine(mb, "logs"));
            var items = TempJanitor.ListLegacy(work);
            var paths = items.Select(i => i.Path).ToList();
            Assert.True(paths.Contains(f200), "registry farm with a capture record is listed");
            Assert.True(!paths.Contains(f201), "a farm with an unfinished install marker is not listed");
            Assert.True(!paths.Contains(f262) && !paths.Contains(f203), "farms outside the registry (research) are never listed");
            Assert.True(paths.Contains(skel) && paths.Contains(mb), "the registry worker's skeleton and the 0.6.7 .map folder are listed");
            var rep = TempJanitor.RemoveLegacy(work, items);
            Assert.True(rep.Problems.Count == 0, "removed: " + string.Join("; ", rep.Problems));
            Assert.True(!Directory.Exists(f200) && !Directory.Exists(skel) && !Directory.Exists(mb) && !Directory.Exists(Path.Combine(work, "_mapbake")), "exactly the listed folders are gone");
            Assert.True(Directory.Exists(f201) && Directory.Exists(f262) && Directory.Exists(f203), "everything else stays");
            SentinelIntact(sent, "legacy skeleton removal");
        }

        private static void LeaseAndGone()
        {
            string work = NewWork("ls");
            string f = Path.Combine(Temp(work), "farm_w283");
            var l = TempLease.Acquire(f, "run1");
            Assert.True(TempJanitor.IsLive(f), "live while held");
            Assert.Throws<SafetyException>(() => TempLease.Acquire(f, "run2"), "a second bake cannot take it");
            Assert.True(!l.Gone, "not gone");
            // the user clears Temp: everything but the held .lock can be deleted
            File.Delete(l.Sentinel);
            Assert.True(l.Gone, "a deleted sentinel = the run root was cleared");
            l.Dispose();
            Assert.True(!TempJanitor.IsLive(f) && !File.Exists(Path.Combine(f, TempLease.LockName)), "released: not live, .lock removed");
        }

        private static void KeepMarkers()
        {
            string work = NewWork("km");
            var set = TagSet.For(@"X:\H2EK", @"scenarios\multi\cyc\cyc", "cyc");
            StartupRecovery.WriteToolWbMarker(work, 284, set, @"X:\ui", "run1", DateTime.Now);
            File.WriteAllText(StartupRecovery.InstallMarkerPath(work, 285), Json.Write(new JObj().Add("target_dir", set.TagDir).Add("prefix", set.Prefix).Add("backups", new List<object> { @"X:\b" }).Add("files", new JObj())));
            string tf = Path.Combine(Temp(work), "farm_w286");
            Directory.CreateDirectory(tf);
            File.WriteAllText(Path.Combine(tf, StartupRecovery.MarkerName), "started 1; previous tags: X:\\b (2 files from X:\\H2EK\\tags\\scenarios\\multi\\cyc\\cyc_cyc_lightmap*). Copy them back.");
            // the user deletes Temp: the KEEP markers are still found
            SafeFs.RemoveTree(Temp(work), work, false);
            var items = StartupRecovery.Scan(work);
            Assert.True(items.Any(i => i.Kind == RecoveryKind.ToolWriteBackMarker && i.Worker == 284 && i.Concerns(set)), "the KEEP tool write-back marker concerns the set");
            Assert.True(items.Any(i => i.Kind == RecoveryKind.InstallMarker && i.Worker == 285 && !i.IsTagRestore && i.Concerns(set)), "the KEEP install marker is an install marker");
            Assert.True(StartupRecovery.FinishToolWbMarker(work, 284, DateTime.Now) && !File.Exists(StartupRecovery.ToolWbMarkerPath(work, 284)), "finished -> set aside .done_");
            // a Temp farm's engine marker is read while it exists
            TempJanitor.EnsureTemp(work);
            Directory.CreateDirectory(tf);
            File.WriteAllText(Path.Combine(tf, StartupRecovery.MarkerName), "started 1; previous tags: X:\\b (2 files from X:\\H2EK\\tags\\scenarios\\multi\\cyc\\cyc_cyc_lightmap*). Copy them back.");
            Assert.True(StartupRecovery.Scan(work).Any(i => i.Worker == 286 && i.Kind == RecoveryKind.ToolWriteBackMarker), "a Temp farm marker is scanned");
        }

        private static void SlotAliasAndTempDeletion()
        {
            string work = NewWork("sa");
            string temp = Temp(work);
            string lad = C.Short.Dir("lad");                       // a short %LOCALAPPDATA% stand-in: the alias goes to <lad>\H2GLM\<k6>
            string vol = C.Short.Dir("v");                         // and the volume-root candidate stays in the sandbox too
            string keepSentinel = Path.Combine(work, "standalone");
            Directory.CreateDirectory(keepSentinel);
            File.WriteAllText(Path.Combine(keepSentinel, "history.json"), "[]");
            Assert.True(temp.Length > 60, "the sandbox Temp path is longer than the 60-character slot: " + temp.Length);
            var slot = SlotAlias.Ensure(temp, lad, vol);
            Assert.True(slot.AliasDir != null, "an alias was needed");
            C.Short.TrackJunction(slot.AliasDir);
            Assert.True(!SafeFs.IsUnderOrSame(slot.AliasDir, temp) && !SafeFs.IsUnderOrSame(slot.AliasDir, work), "the alias is outside Temp and outside WORK: " + slot.AliasDir);
            Assert.True(slot.SlotBase.Length <= 60, "slot base fits: " + slot.SlotBase);
            string t;
            Assert.True(Junction.TryGetTarget(slot.AliasDir, out t) && Junction.SamePathText(t, temp), "the alias points at Temp");
            // a bake is running: its farm lease, files written through the alias land in Temp
            string farm = Path.Combine(temp, "farm_w287");
            var lease = TempLease.Acquire(farm, "bake");
            File.WriteAllText(Path.Combine(slot.AliasDir, "farm_w287", "texelgen_soup.bin"), "through the alias");
            Assert.True(File.Exists(Path.Combine(farm, "texelgen_soup.bin")), "the DLL's writes through the alias land in Temp");
            // the user clears Temp mid-bake (everything Explorer can delete: not the held .lock): the bake sees it, the alias is untouched
            File.Delete(Path.Combine(farm, "texelgen_soup.bin"));
            File.Delete(lease.Sentinel);
            Assert.True(lease.Gone, "the bake detects the cleared run root");
            Assert.True(Junction.IsJunction(slot.AliasDir), "the alias is still a junction (deleting Temp's contents never reaches it)");
            lease.Dispose();
            // now Temp itself is deleted (the folder): the alias dangles; the janitor never walks it (it is not inside Temp) and nothing outside Temp is touched
            SafeFs.RemoveTree(temp, work, false);
            Assert.True(!Directory.Exists(temp), "Temp deleted");
            Assert.True(Junction.PathExists(slot.AliasDir) && Junction.IsJunction(slot.AliasDir), "the alias dangles harmlessly (still a link, no folder behind it)");
            var sw = TempJanitor.Sweep(work, new TempSettings());
            var cn = TempJanitor.CleanNow(work);
            Assert.True(File.Exists(Path.Combine(keepSentinel, "history.json")), "KEEP is untouched");
            Assert.True(Directory.Exists(lad) && Directory.Exists(Path.GetDirectoryName(slot.AliasDir)), "the alias' parent folder is untouched");
            // a dangling alias cannot be used: Ensure refuses until Temp exists again -- the next bake re-creates Temp FIRST (PayloadInstaller.EnsureFarmRoot)
            Assert.Throws<SlotAliasRefusedException>(() => SlotAlias.Ensure(temp, lad, vol), "Ensure refuses a missing Temp");
            TempJanitor.EnsureTemp(work);
            var again = SlotAlias.Ensure(temp, lad, vol);
            Assert.Eq(slot.SlotBase, again.SlotBase, "the same slot base");
            Assert.Eq(AliasState.Reused, again.State, "the existing alias is reused (it resolves to the re-created Temp)");
            Assert.True(File.Exists(Path.Combine(temp, ShellPaths.TempReadMeName)), "Temp re-created with its READ ME");
            // the alias junction itself removed (e.g. a %LOCALAPPDATA% cleaner): the next bake makes it again
            Junction.Remove(slot.AliasDir, temp);
            var third = SlotAlias.Ensure(temp, lad, vol);
            Assert.Eq(AliasState.Created, third.State, "re-created");
            Assert.True(FindLinksUnder(temp).Count == 0, "no link was ever created inside Temp");
        }

        private static List<string> FindLinksUnder(string dir) { return Directory.Exists(dir) ? SafeFs.FindReparsePointsDeep(dir) : new List<string>(); }

        private static void ProductsListRules()
        {
            var p = FarmProducts.Load(Products());
            Assert.True(p != null && p.Files.Contains("direct_out.bin") && p.Dirs.Contains("tagw_stage") && !p.Files.Contains(TempJanitor.CaptureRecordName), "the shipped list loads");
            string dir = C.Sb.Dir("pl");
            Func<string, FarmProducts> load = body => { string f = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(f, body); return FarmProducts.Load(f); };
            Assert.True(load("{\"format\":\"h2lm-farm-products/1\",\"files\":[\"..\\\\x\"]}") == null, "a path is refused");
            Assert.True(load("{\"format\":\"h2lm-farm-products/1\",\"files\":[\"standalone_capture.json\"]}") == null, "the capture record is refused");
            Assert.True(load("{\"format\":\"h2lm-farm-products/1\",\"dirs\":[\"skeleton\"]}") == null, "skeleton is refused");
            Assert.True(load("{\"format\":\"other\"}") == null, "another format is refused");
            Assert.True(FarmProducts.Matches("seam_lists.bin.stale_20261029_010101", "*.stale_*") && !FarmProducts.Matches("seam_lists.bin", "*.stale_*"), "wildcards");
            // the shell's capture record file name (Pipeline.CaptureRecord.FileName) is pinned in the exe's tests; the janitor's constant:
            Assert.Eq("standalone_capture.json", TempJanitor.CaptureRecordName, "capture record name");
        }

        private static void PathBudget068()
        {
            string steam = @"C:\Program Files (x86)\Steam\steamapps\common\H2EK";
            var e = PathBudget.Estimate(steam, "0.6.8-12345678", 120, @"scenarios\multi\a_32_character_scenario_folder_x\a_16_char_leaf_x", "a_16_char_bsp_xx", true);
            var farm = e.First(x => x.What == "farm AO stage");
            Assert.Contains(farm.Example, @"\Temp (safe to delete)\farm_w200\", "the farm is in Temp");
            var all = e.OrderByDescending(x => x.Length).ToList();
            Assert.True(all[0].Length < PathBudget.Limit, "every 0.6.8 scenario-bake path fits " + PathBudget.Limit + " on Steam's default path: longest " + all[0].What + " " + all[0].Length);
            Assert.Eq(farm.Length - 22, PathBudget.Estimate(steam, "0.6.8-12345678", 120, @"scenarios\multi\a_32_character_scenario_folder_x\a_16_char_leaf_x", "a_16_char_bsp_xx", false)
                                         .First(x => x.What == "farm AO stage").Length, "the farm moved into Temp costs exactly 22 characters");
            var sk = PathBudget.MapSkeleton(steam, @"scenarios\multi\a_32_character_scenario_folder_x\a_16_char_leaf_x", "a_16_char_bsp_xx");
            string old067 = steam + @"\GPU Lightmap Work\_mapbake\m20261028-134231\root\w\_gpulm_skel\w200\tags\scenarios\multi\a_32_character_scenario_folder_x\a_16_char_leaf_x_a_16_char_bsp_xx_lightmap_truecolor_bitmaps.bitmap";
            Assert.True(sk.Length < 260 && sk.Length == old067.Length + 1, ".map skeleton " + sk.Length + " (0.6.7: " + old067.Length + "; Temp (safe to delete)/m<6hex>/r is ONE character longer than _mapbake/m<time>/root) stays under MAX_PATH");
            var p99 = PathBudget.MapOverlay(steam, PathBudget.KitRelP99);
            var max = PathBudget.MapOverlay(steam, PathBudget.KitRelMax);
            Assert.Eq(106 + PathBudget.KitRelP99, p99.Length, "the overlay prefix is 106 characters on Steam's default path");
            Assert.Eq(106 + PathBudget.KitRelMax, max.Length, ".map overlay worst case");
            Console.WriteLine("    MAX_PATH figures (Steam default): " + string.Join("; ", all.Select(x => x.What + " " + x.Length)) + "; .map skeleton " + sk.Length + " (0.6.7 " + old067.Length + "); .map overlay p99 " + p99.Length + ", max " + max.Length);
        }
    }
}
