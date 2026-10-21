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
            yield return T("unit 0.6.4: cross-folder BSP picker: every structure_bsps entry by tag path, other folders labelled 'name  (from folder)', old engine keeps the beside-the-scenario list, ambiguity / missing tag notes, options (BspRel validation, summary, tag file)", CrossFolderPicker);
            yield return T("unit 0.6.4: cross-folder BSP engine gate (gpu_bake.ps1 declares -WriterOnly and -ToolOverlay): new engine supported, older engine / Custom engine / no payload refused with the plain sentence", CrossFolderGate);
            yield return T("unit 0.6.4: write-back fallback detector: the engine's three direct->tool lines (and a tool->direct one) are recognised, ordinary lines and the shell's own warning are not; warning + summary texts", FallbackDetect);
            yield return T("unit: review fixes: on-disk case, install-without-layer flags, the undo time rule, --probes-fallback", ReviewFixes);
            yield return T("unit: V6 --install-retries: parsing (ranges, needs an install) + the Retry answers (N yes, then no; a cancel ends the wait)", InstallRetries);
            yield return T("unit: X2 Keep PC responsive: unticked argv byte-identical, ticked = + -Responsive last on every gpu_bake argv (36 combinations), --responsive, labels", X2Responsive);
            yield return T("unit: W2 2004 Exact Vertex Lighting?: unticked argv byte-identical, ticked = + -ExactVertexLighting last on every gpu_bake argv (72 combinations, with and without -Responsive), --exact-vertex-lighting, labels", W2ExactVertex);
            yield return T("unit: Scenery Multi Permutation Fix: unticked argv byte-identical, ticked = + -SceneryOwnPermutationOnly last on every gpu_bake argv (144 combinations), --scenery-permutation-fix, labels, summary", SceneryPermArgv);
            yield return T("unit: Scenery Multi Permutation Fix engine gate: param-block parser (comments / strings / types), the real engines' param blocks, the payload gate (Super, Custom), disabled-when-unsupported", SceneryPermEngineGate);
            yield return T("unit: Scenery Multi Permutation Fix capture identity: a capture is reusable only by a bake with the same setting (older records count as off; the probe capture never carries it)", SceneryPermCaptureIdentity);
            yield return T("unit: W2 run folder label (<= 20 chars), tagbak, and Undo / the Logs button keep working for OLD-style run folders (<ts>_<leaf>_<bsp>, tag_backup_before_writeback)", W2RunFolders);
            yield return T("unit: X2 live VRAM sizing: a normal desktop (<= 3 GiB held) = today's argv on every card; a game on top shrinks the parts with a note; gpucheck live figures", X2LiveVram);
            yield return T("unit: X2 TDR message: the three device-loss signatures -> the plain explanation (parser, log file, composition)", X2TdrMessage);
            yield return T("unit: U3 the direct writer's pre-flight refusal is recognised only before any phase (not a later failure, a cancel, an exit 0)", DirectWriterCannotRun);
        }

        // ------------------------------------------------------------------ X2: Keep PC responsive / live VRAM / TDR message

        private static void X2Responsive()
        {
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            Assert.Eq(false, o.Responsive, "default: unticked");
            Assert.True(!o.Summary().Contains("responsive"), "unticked: the summary says nothing");
            var six = GpuLimits.Compute(6L << 30, "t", 0);                 // -MaxCallsPerPart and -BufCapMB present
            int combos = 0;
            foreach (var lim in new GpuLimits[] { null, GpuLimits.Compute(25310527488L, "t", 0), six })
                foreach (bool wb in new[] { true, false })
                    foreach (bool sc in new[] { true, false })
                        foreach (var q in new[] { Quality.Low, Quality.Super, Quality.Custom })
                        {
                            o.Quality = q; o.Photons = 20000000; o.Rays = 8;
                            o.Responsive = false;
                            var off = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                            Assert.True(!off.Contains("-Responsive"), "unticked: no -Responsive");
                            o.Responsive = true;
                            var on = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                            Assert.Seq(off.Concat(new[] { "-Responsive" }).ToList(), on, "ticked = the unticked argv + -Responsive LAST (wb " + wb + ", skip " + sc + ", " + q + ")");
                            combos++;
                        }
            Assert.Eq(36, combos, "36 argv combinations compared");
            // the unticked argv is the literal production one (byte-for-byte what 0.6.2 builds)
            o.Responsive = false; o.Quality = Quality.Super;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 200, true, false), "unticked production argv");
            o.Responsive = true;
            Assert.Contains(o.Summary(), "responsive on", "the summary (plan + log) says 'responsive on'");
            // CLI
            var a = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--responsive");
            var co = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "no errors: " + string.Join("; ", a.Errors));
            Assert.Eq(true, co.Responsive, "--responsive");
            Assert.Eq(false, CliMain.BakeOptionsFrom(P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b")).Responsive, "absent: off");
            // the labels the window shows
            Assert.Eq("Keep PC responsive - Slower Bakes!", BakeOptions.ResponsiveLabel, "label");
            Assert.Eq("Splits the GPU work into short pieces so your desktop, videos and games stay smooth while it bakes. The lightmap is identical; the bake takes longer.", BakeOptions.ResponsiveTip, "tooltip");
            Assert.Eq("2004 Exact Vertex Lighting?", BakeOptions.ExactVertexLightingLabel, "placeholder label");
            Assert.Eq("You likely don't need this, this is just to shut Harcs brain up", BakeOptions.ExactVertexLightingTip, "placeholder tooltip");
        }

        // ------------------------------------------------------------------ Scenery Multi Permutation Fix (Experimental Improvement)

        private const string ScenerySwitch = "-SceneryOwnPermutationOnly";

        private static void SceneryPermArgv()
        {
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            Assert.Eq(false, o.SceneryPermutationFix, "default: unticked");
            Assert.True(!o.Summary().Contains("permutation"), "unticked: the summary says nothing");
            var six = GpuLimits.Compute(6L << 30, "t", 0);
            int combos = 0;
            foreach (var lim in new GpuLimits[] { null, GpuLimits.Compute(25310527488L, "t", 0), six })
                foreach (bool wb in new[] { true, false })
                    foreach (bool sc in new[] { true, false })
                        foreach (var q in new[] { Quality.Low, Quality.Super, Quality.Custom })
                            foreach (bool resp in new[] { false, true })
                                foreach (bool evl in new[] { false, true })
                                {
                                    o.Quality = q; o.Photons = 20000000; o.Rays = 8; o.Responsive = resp; o.ExactVertexLighting = evl;
                                    o.SceneryPermutationFix = false;
                                    var off = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                                    Assert.True(!off.Contains(ScenerySwitch), "unticked: no " + ScenerySwitch);
                                    o.SceneryPermutationFix = true;
                                    var on = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                                    Assert.Seq(off.Concat(new[] { ScenerySwitch }).ToList(), on, "ticked = the unticked argv + " + ScenerySwitch + " LAST (wb " + wb + ", skip " + sc + ", " + q + ", resp " + resp + ", evl " + evl + ")");
                                    combos++;
                                }
            Assert.Eq(144, combos, "144 argv combinations compared");
            o.SceneryPermutationFix = false; o.ExactVertexLighting = false; o.Responsive = false; o.Quality = Quality.Super;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 200, true, false), "unticked production argv (byte-identical to 0.6.3)");
            o.SceneryPermutationFix = true; o.Responsive = true; o.ExactVertexLighting = true;
            var all = BakePlanner.GpuBakeArgs(o, 200, true, false);
            Assert.Seq(new[] { "-Responsive", "-ExactVertexLighting", ScenerySwitch }, all.Skip(all.Count - 3).ToList(), "after the other two Advanced options");
            Assert.Contains(o.Summary(), "scenery multi permutation fix on", "the summary (plan + log header) says so");
            // CLI
            var a = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--scenery-permutation-fix");
            var co = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "no errors: " + string.Join("; ", a.Errors));
            Assert.Eq(true, co.SceneryPermutationFix, "--scenery-permutation-fix");
            Assert.True(!co.Responsive && !co.ExactVertexLighting, "independent of the other two");
            Assert.Eq(false, CliMain.BakeOptionsFrom(P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b")).SceneryPermutationFix, "absent: off");
            Assert.True(CliMain.Usage.Contains("--scenery-permutation-fix") && CliMain.Usage.Contains("Needs engine 0.6.4 or newer"), "the usage text lists the flag and the engine requirement");
            // the window's texts, exactly
            Assert.Eq("Scenery Multi Permutation Fix (Experimental Improvement)", BakeOptions.SceneryPermutationFixLabel, "label");
            Assert.Eq("Needs engine 0.6.4 or newer.", BakeOptions.SceneryPermutationFixNeedsEngine, "suffix of the disabled tooltip");
            Assert.Eq("SceneryOwnPermutationOnly", BakeOptions.SceneryPermutationFixSwitch, "the engine-side switch name");
            Assert.Eq("Fallback for scenery that draws more than one shape from one tag, e.g. a rock whose shape changes when you move it, or a lifepod placed with a named variant like 'crashed'. "
                      + "Only the shape the game actually draws casts shadows, ground hidden inside scenery is filled from its surroundings, and a variant's own pieces get lit instead of rendering black. "
                      + "Differs from the 2004 tool on purpose. Most maps never need this: giving each shape its own scenery tag avoids the problem.", BakeOptions.SceneryPermutationFixTip, "tooltip");
        }

        private static string Script(params string[] lines) { return string.Join("\r\n", lines); }

        private static void SceneryPermEngineGate()
        {
            Func<string, bool> decl = s => Core.Payload.EngineSwitches.DeclaresSwitch(s, "SceneryOwnPermutationOnly");
            // declared, in the shapes real scripts use
            Assert.True(decl(Script("param(", "  [string]$Scenario = \"\",", "  [switch]$SceneryOwnPermutationOnly,   # opt-in", "  [switch]$Responsive", ")", "Write-Host hi")), "declared");
            Assert.True(decl(Script("[CmdletBinding()]", "param([switch] $sceneryownpermutationonly)")), "case-insensitive, space before the name, [CmdletBinding()] first");
            Assert.True(decl(Script("PARAM (", "  [Parameter(Mandatory=$false)][System.Management.Automation.SwitchParameter]$SceneryOwnPermutationOnly", ")")), "attribute + full SwitchParameter type");
            Assert.True(decl(Script("<# header mentioning param( and [switch]$Other #>", "param(", "  [switch]$SceneryOwnPermutationOnly", ")")), "a block comment above that mentions param( is skipped");
            Assert.True(decl(Script("param(", "  [ValidateSet(\"low\",\"super\")][string]$Quality = \"super\",   # (nested parens) )", "  [switch]$SceneryOwnPermutationOnly", ")")), "nested parentheses and a ')' in a comment do not end the block early");
            // NOT declared
            Assert.True(!decl(Script("param(", "  [switch]$Responsive,", "  [switch]$ExactVertexLighting", ")")), "an older engine");
            Assert.True(!decl(Script("param(", "  [switch]$Responsive   # SceneryOwnPermutationOnly arrives in 0.6.4: [switch]$SceneryOwnPermutationOnly", ")")), "named only in a comment");
            Assert.True(!decl(Script("param(", "  [string]$Note = \"[switch]$SceneryOwnPermutationOnly\"", ")")), "named only inside a string");
            Assert.True(!decl(Script("param(", "  [string]$SceneryOwnPermutationOnly", ")")), "declared, but not as a switch");
            Assert.True(!decl(Script("param(", "  [switch]$SceneryOwnPermutationOnlyX", ")")), "a longer name is not the name");
            Assert.True(!decl(Script("param(", "  [switch]$Responsive", ")", "function F { param([switch]$SceneryOwnPermutationOnly) }", "if ($SceneryOwnPermutationOnly) { }")), "a function's own param block / a later use does not count");
            Assert.True(!decl(Script("$x = 1", "if ($SceneryOwnPermutationOnly) { 1 }")), "no param block at all");
            Assert.True(!decl(""), "empty script");
            Assert.True(!decl(null), "null script");
            Assert.True(!decl(Script("param(", "  [switch]$SceneryOwnPermutationOnly")), "an unterminated param block fails closed");
            // the real engines (read-only): production 0.6.3 has Responsive + ExactVertexLighting but not the new switch; the dev engine has it
            string prod = Path.Combine(C.MineWork, @"engine_v373\gpu_bake.ps1");
            if (File.Exists(prod))
            {
                string t = File.ReadAllText(prod);
                Assert.True(Core.Payload.EngineSwitches.DeclaresSwitch(t, "Responsive") && Core.Payload.EngineSwitches.DeclaresSwitch(t, "ExactVertexLighting"), "production engine_v373 declares -Responsive and -ExactVertexLighting");
                Assert.True(decl(t), "production engine_v373 (promoted to 0.6.4 on 2026-10-21) declares -SceneryOwnPermutationOnly");
            }
            string old063 = Path.Combine(C.MineWork, @"engine_v373\_backup_20261021_145623_pre_064\gpu_bake.ps1");
            if (File.Exists(old063))
            {
                string t = File.ReadAllText(old063);
                Assert.True(Core.Payload.EngineSwitches.DeclaresSwitch(t, "Responsive") && Core.Payload.EngineSwitches.DeclaresSwitch(t, "ExactVertexLighting"), "the 0.6.3 engine (pre-promotion backup) declares -Responsive and -ExactVertexLighting");
                Assert.True(!decl(t), "the 0.6.3 engine (pre-promotion backup) does NOT declare -SceneryOwnPermutationOnly");
            }
            string dev = Path.Combine(C.MineWork, @"_research\permself\dev_CO\gpu_bake.ps1");
            if (File.Exists(dev)) Assert.True(decl(File.ReadAllText(dev)), "the dev_CO engine declares -SceneryOwnPermutationOnly");
            else if (!File.Exists(prod)) throw new TestSkip("neither the production nor the dev_CO engine is on this PC");
            // the payload gate (in-process: AppHost over the test payloads; nothing extracted, nothing started)
            var good = AppHost.Create(new HostConfig { PayloadDir = C.PayloadDir });
            Assert.True(good.HasPayload, "test payload loads: " + good.PayloadError);
            var o = BakeOptions.Defaults();
            Assert.Eq(null, good.SceneryPermutationFixWhy(o), "a payload whose engine_v373 declares it: supported");
            var old = AppHost.Create(new HostConfig { PayloadDir = C.BuildPayload(C.Sb.Dir("payload_nosp"), sceneryEngine: false) });
            string why = old.SceneryPermutationFixWhy(o);
            Assert.True(why != null, "an older engine: not supported");
            Assert.Contains(why, "Needs engine 0.6.4 or newer.", "the plain reason ends with the engine requirement");
            Assert.Contains(why, "engine_v373/gpu_bake.ps1", "and names the engine file");
            Assert.Contains(why, "-SceneryOwnPermutationOnly", "and the parameter");
            // Custom runs engine_custom: that engine decides for Custom
            var mixed = AppHost.Create(new HostConfig { PayloadDir = C.BuildPayload(C.Sb.Dir("payload_nosp_custom"), customEngine: true, customSceneryEngine: false) });
            var oc = BakeOptions.Defaults(); oc.Quality = Quality.Custom;
            Assert.Eq(null, mixed.SceneryPermutationFixWhy(BakeOptions.Defaults()), "Super: engine_v373 declares it");
            string cwhy = mixed.SceneryPermutationFixWhy(oc);
            Assert.True(cwhy != null && cwhy.Contains("engine_custom/gpu_bake.ps1") && cwhy.Contains("Needs engine 0.6.4 or newer."), "Custom: engine_custom lacks it -> refused, naming engine_custom: " + cwhy);
            var both = AppHost.Create(new HostConfig { PayloadDir = C.BuildPayload(C.Sb.Dir("payload_sp_custom"), customEngine: true) });
            Assert.Eq(null, both.SceneryPermutationFixWhy(oc), "Custom with a declaring engine_custom: supported");
            // no payload at all: never supported
            var none = AppHost.Create(new HostConfig { PayloadDir = Path.Combine(C.Sb.Dir("payload_empty")) });
            Assert.True(!none.HasPayload && none.SceneryPermutationFixWhy(o) != null, "no payload: not supported");
        }

        private static void SceneryPermCaptureIdentity()
        {
            var sb = C.Sb.Dir("unit_reuse_scn");
            string root = Path.Combine(sb, "H2");
            Directory.CreateDirectory(Path.Combine(root, @"tags\scenarios\m\m"));
            File.WriteAllText(Path.Combine(root, @"tags\scenarios\m\m\m.scenario"), "s");
            var set = TagSet.For(root, @"scenarios\m\m\m", "m");
            string farm = Path.Combine(sb, "farm_w200");
            Directory.CreateDirectory(farm);
            File.WriteAllText(Path.Combine(farm, "capture_id.json"), "{\"soup_crc32\": \"ABCD1234\"}");
            Func<TagsFingerprint> tags = () => TagsFingerprint.Compute(root, set);
            Func<bool, CaptureIdentity> id = scn => { var i = Id(); i.SceneryPermutationFix = scn; return i; };
            Func<CaptureIdentity, ReuseVerdict> eval = want => CaptureRecord.Evaluate(farm, want, @"scenarios\m\m\m", "m", false, tags);
            Assert.Eq(null, CaptureRecord.Write(farm, id(false), @"scenarios\m\m\m", "m", 200, "run1", tags(), DateTime.Now), "capture made without the option");
            Assert.True(eval(id(false)).Allowed, "off -> off: reusable");
            var r = eval(id(true));
            Assert.True(!r.Allowed && r.Reason.Contains("made without") && r.Reason.Contains("Scenery Multi Permutation Fix"), "off -> on: refused, says why: " + r.Reason);
            Assert.Eq(null, CaptureRecord.Write(farm, id(true), @"scenarios\m\m\m", "m", 200, "run2", tags(), DateTime.Now), "capture made WITH the option");
            Assert.True(eval(id(true)).Allowed, "on -> on: reusable");
            r = eval(id(false));
            Assert.True(!r.Allowed && r.Reason.Contains("this bake has it off"), "on -> off: refused, says why: " + r.Reason);
            Assert.Eq(true, CaptureRecord.Read(farm).Bool("sceneryPermutationFix"), "the record carries sceneryPermutationFix");
            // an OLDER record (before this option existed) has no key: it counts as off
            var j = CaptureRecord.Read(farm);
            string text = File.ReadAllText(CaptureRecord.PathIn(farm));
            File.WriteAllText(CaptureRecord.PathIn(farm), System.Text.RegularExpressions.Regex.Replace(text, "\"sceneryPermutationFix\"\\s*:\\s*(true|false),?", ""));
            Assert.True(CaptureRecord.Read(farm) != null && !CaptureRecord.Read(farm).Has("sceneryPermutationFix"), "key removed from the record");
            Assert.True(eval(id(false)).Allowed, "older record + off: reusable (old captures are not invalidated)");
            Assert.True(!eval(id(true)).Allowed, "older record + on: refused");
            // the identity a bake builds: the lightmap capture carries the option, the probe worker's own SKY_HI capture never does
            var o = BakeOptions.Defaults(); o.SceneryPermutationFix = true;
            Assert.Eq(true, CaptureIdentity.For(o, null, root, InstLayout.GpuBake, false).SceneryPermutationFix, "lightmap capture: on");
            Assert.Eq(false, CaptureIdentity.For(o, null, root, InstLayout.GpuBake, true).SceneryPermutationFix, "probe capture (SKY_HI): never");
            o.SceneryPermutationFix = false;
            Assert.Eq(false, CaptureIdentity.For(o, null, root, InstLayout.GpuBake, false).SceneryPermutationFix, "off: off");
        }

        private static void W2ExactVertex()
        {
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            Assert.Eq(false, o.ExactVertexLighting, "default: unticked");
            Assert.True(!o.Summary().Contains("exact vertex"), "unticked: the summary says nothing");
            var six = GpuLimits.Compute(6L << 30, "t", 0);
            int combos = 0;
            foreach (var lim in new GpuLimits[] { null, GpuLimits.Compute(25310527488L, "t", 0), six })
                foreach (bool wb in new[] { true, false })
                    foreach (bool sc in new[] { true, false })
                        foreach (var q in new[] { Quality.Low, Quality.Super, Quality.Custom })
                            foreach (bool resp in new[] { false, true })
                            {
                                o.Quality = q; o.Photons = 20000000; o.Rays = 8; o.Responsive = resp;
                                o.ExactVertexLighting = false;
                                var off = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                                Assert.True(!off.Contains("-ExactVertexLighting"), "unticked: no -ExactVertexLighting");
                                o.ExactVertexLighting = true;
                                var on = BakePlanner.GpuBakeArgs(o, 200, wb, sc, lim);
                                Assert.Seq(off.Concat(new[] { "-ExactVertexLighting" }).ToList(), on, "ticked = the unticked argv + -ExactVertexLighting LAST (wb " + wb + ", skip " + sc + ", " + q + ", responsive " + resp + ")");
                                combos++;
                            }
            Assert.Eq(72, combos, "72 argv combinations compared");
            o.ExactVertexLighting = false; o.Responsive = false; o.Quality = Quality.Super;
            Assert.Seq(new[] { "-Scenario", @"scenarios\multi\cyclotron\cyclotron", "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 200, true, false), "unticked production argv (byte-identical to 0.6.2)");
            o.ExactVertexLighting = true;
            Assert.Contains(o.Summary(), "exact vertex lighting on", "the summary says so");
            var a = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--exact-vertex-lighting");
            var co = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "no errors: " + string.Join("; ", a.Errors));
            Assert.Eq(true, co.ExactVertexLighting, "--exact-vertex-lighting");
            Assert.Eq(false, co.Responsive, "independent of --responsive");
            Assert.Eq(false, CliMain.BakeOptionsFrom(P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b")).ExactVertexLighting, "absent: off");
            Assert.Eq("exact vertex lighting on (slower tag writer)", BakeOptions.ExactVertexLightingNote, "plan note text");
            Assert.Eq("2004 Exact Vertex Lighting?", BakeOptions.ExactVertexLightingLabel, "label");
            Assert.Eq("You likely don't need this, this is just to shut Harcs brain up", BakeOptions.ExactVertexLightingTip, "tooltip (no '(coming in a later build)')");
        }

        private static void W2RunFolders()
        {
            Assert.Eq("cyclotron_cyclotron", RunFolder.Label("cyclotron", "cyclotron"), "19 chars: whole");
            Assert.Eq("triplicate_triplicat", RunFolder.Label("triplicate", "triplicate"), "cut to 20");
            Assert.Eq(20, RunFolder.Label(new string('a', 40), "b").Length, "never more than 20");
            Assert.Eq("a_b", RunFolder.Label("a", "b"), "short");
            Assert.Eq("a_b_c", RunFolder.Label("a:b", "c"), "sanitized (the invalid ':')");
            Assert.Eq(new string('x', 19), RunFolder.Label(new string('x', 19) + ".", "q"), "no trailing dot after the cut");
            Assert.Eq("_", RunFolder.Label("", ""), "empty names give '_' (never an empty folder name)");
            Assert.Eq("20261020_123456_cyclotron_cyclotron", RunFolder.TagFor(new DateTime(2026, 10, 20, 12, 34, 56), @"scenarios\multi\cyclotron\cyclotron", "cyclotron"), "TagFor");
            Assert.Eq("tagbak", RunFolder.BackupDirName, "new backup subfolder");
            Assert.Eq("tag_backup_before_writeback", RunFolder.LegacyBackupDirName, "old backup subfolder");
            // NameMatches: both styles
            Assert.True(RunFolder.NameMatches("20261020_123456_cyclotron_cyclotron", @"scenarios\multi\cyclotron\cyclotron", "cyclotron"), "new style");
            Assert.True(RunFolder.NameMatches("20261015_101010_triplicate_triplicate", @"scenarios\multi\triplicate\triplicate", "triplicate"), "old style (whole leaf_bsp)");
            Assert.True(RunFolder.NameMatches("20261020_123456_triplicate_triplicat", @"scenarios\multi\triplicate\triplicate", "triplicate"), "new style (cut)");
            Assert.True(!RunFolder.NameMatches("20261020_123456_cyclotron_cyclotron", @"scenarios\multi\triplicate\triplicate", "triplicate"), "another map");
            // Undo / history: old-style and new-style run folders side by side in one work folder (the test sandbox is cleaned by the harness)
            const string Cyc = @"scenarios\multi\cyclotron\cyclotron";
            string tmp = C.Sb.Dir("w2");                // short on purpose: MAX_PATH
            {
                string work = Path.Combine(tmp, "GPU Lightmap Work");
                string oldRun = Path.Combine(RunFolder.RunsDir(work), "20261015_101010_cyclotron_cyclotron");
                string newRun = Path.Combine(RunFolder.RunsDir(work), "20261020_101010_cyclotron_cyclotron");
                Func<string, string, DateTime, string> mk = (run, sub, started) =>
                {
                    string bak = Path.Combine(run, sub);
                    Directory.CreateDirectory(bak);
                    File.WriteAllText(Path.Combine(bak, "_install_manifest.json"), "{\"made\": \"" + started.AddSeconds(2).ToString("yyyyMMdd_HHmmss") + "\"}");
                    File.WriteAllText(Path.Combine(bak, "t.bitmap"), "x");
                    var st = new RunStatus { RunDir = run, RunTag = Path.GetFileName(run), Scenario = Cyc, Bsp = "cyclotron", State = RunState.Done, InstallBackupDir = bak, StartedUtc = started.ToUniversalTime() };
                    st.Save();
                    return bak;
                };
                string oldBak = mk(oldRun, "tag_backup_before_writeback", new DateTime(2026, 10, 15, 10, 10, 10, DateTimeKind.Local));
                var only = CliMain.LastInstall(work, Cyc, "cyclotron");
                Assert.True(only != null && only.InstallBackupDir == oldBak, "an OLD-style run folder (0.6.2) still offers its install backup for Undo: " + (only == null ? "null" : only.InstallBackupDir));
                string newBak = mk(newRun, "tagbak", new DateTime(2026, 10, 20, 10, 10, 10, DateTimeKind.Local));
                var both = CliMain.LastInstall(work, Cyc, "cyclotron");
                Assert.True(both != null && both.InstallBackupDir == newBak, "the newer run (tagbak) wins: " + (both == null ? "null" : both.InstallBackupDir));
                foreach (var f in Directory.GetFiles(newBak)) File.Delete(f);
                Directory.Delete(newBak);
                var back = CliMain.LastInstall(work, Cyc, "cyclotron");
                Assert.True(back != null && back.InstallBackupDir == oldBak, "with the new backup gone, the old-style one is the last install again");
            }
        }

        private static void X2LiveVram()
        {
            const long D = 25310527488L;                 // the RTX 4090's dedicated_video_memory
            const long GiB = 1L << 30;
            var o = BakeOptions.Defaults(); o.ScenarioRel = @"scenarios\multi\cyclotron\cyclotron"; o.Bsp = "cyclotron";
            var plain = GpuLimits.Compute(D, "t", 0);
            // ---- 'normal' (other programs <= 3 GiB): exactly today's limits, notes and argv, on every card size
            foreach (long other in new long[] { 0, 1 * GiB, 2 * GiB, 3 * GiB, 3 * GiB - 1 })
            {
                var g = GpuLimits.Compute(D, "t", 0, other);
                Assert.Eq(plain.MaxCallsPerPart, g.MaxCallsPerPart, "4090, other use " + other + ": same -MaxCallsPerPart");
                Assert.Eq(plain.BufCapMB, g.BufCapMB, "same -BufCapMB");
                Assert.Seq(plain.Notes, g.Notes, "same notes (nothing about other programs)");
                Assert.True(!g.EffectiveVramBytes.HasValue, "no effective-VRAM override");
                foreach (bool wb in new[] { true, false })
                    Assert.Seq(BakePlanner.GpuBakeArgs(o, 200, wb, false), BakePlanner.GpuBakeArgs(o, 200, wb, false, g), "argv unchanged (wb " + wb + ")");
            }
            foreach (long card in new long[] { 8000000000L, 12000000000L, 16000000000L, 6L << 30 })
            {
                var base1 = GpuLimits.Compute(card, "t", 0);
                var idle = GpuLimits.Compute(card, "t", 0, 3 * GiB);
                Assert.Eq(base1.MaxCallsPerPart, idle.MaxCallsPerPart, "card " + card + ": a normal desktop changes nothing");
                Assert.Seq(base1.Notes, idle.Notes, "card " + card + ": same notes");
            }
            // this PC at the time of the build: ~3.2 GiB used system-wide, budget 24,505,221,120 -> still the production argv
            long? now = GpuLimits.ComputeOtherUse(D, 24505221120L, 0, 3438923776L);
            Assert.Eq((long?)3438923776L, now, "system counter beats the budget-implied 0.75 GiB");
            Assert.Eq(0L, GpuLimits.Compute(D, "t", 0, now).MaxCallsPerPart, "4090 with a 3.2 GiB desktop: argv unchanged");
            // ---- another program holds a lot: the parts shrink, with the plain note
            var game = GpuLimits.Compute(D, "t", 0, 12 * GiB);
            Assert.True(game.MaxCallsPerPart > 0 && game.MaxCallsPerPart < GpuLimits.PlannerCapRows, "4090 + 12 GiB held: -MaxCallsPerPart " + game.MaxCallsPerPart);
            Assert.Eq((long?)(D - 9 * GiB), game.EffectiveVramBytes, "sized for the VRAM left after the 3 GiB a desktop is allowed");
            Assert.True(game.Notes.Any(n => n.StartsWith("12,288 MiB of VRAM is in use by other programs; parts sized for 14,", StringComparison.Ordinal)), "note: " + string.Join(" | ", game.Notes));
            var ag = BakePlanner.GpuBakeArgs(o, 200, false, false, game);
            Assert.Seq(BakePlanner.GpuBakeArgs(o, 200, false, false), ag.Take(ag.IndexOf("-MaxCallsPerPart")).ToList(), "the production argv is the prefix");
            Assert.Eq(game.MaxCallsPerPart.ToString(), ag[ag.IndexOf("-MaxCallsPerPart") + 1], "value");
            Assert.Eq(0, game.BufCapMB, "D3D11's per-resource limit belongs to the adapter, not to what others hold");
            // monotone: the more others hold, the smaller the parts; never below the 1 M floor; a nearly full card warns
            long prev = long.MaxValue;
            foreach (int g2 in new[] { 6, 9, 12, 16, 20 })
            {
                var x = GpuLimits.Compute(D, "t", 0, (long)g2 * GiB);
                long m = x.MaxCallsPerPart > 0 ? x.MaxCallsPerPart : GpuLimits.PlannerCapRows;
                Assert.True(m <= prev, "monotone at " + g2 + " GiB held");
                prev = m;
            }
            var nearly = GpuLimits.Compute(D, "t", 0, 23 * GiB);
            Assert.True(nearly.MaxCallsPerPart > 1000000L && nearly.MaxCallsPerPart < GpuLimits.TinyRows && nearly.Notes.Any(n => n.StartsWith("WARNING ")), "a card nearly full: tiny parts with a warning: " + nearly.MaxCallsPerPart);
            var full = GpuLimits.Compute(D, "t", 0, 24 * GiB);
            Assert.True(full.MaxCallsPerPart == 1000000L && full.Notes.Any(n => n.StartsWith("WARNING ")), "a card full: the 1 M floor with a warning");
            // a 16 GB card with a game on top is cut further than the same card idle
            Assert.True(GpuLimits.Compute(16000000000L, "t", 0, 9 * GiB).MaxCallsPerPart < GpuLimits.Compute(16000000000L, "t", 0).MaxCallsPerPart, "16 GB + game < 16 GB idle");
            // unknown live figure = today
            Assert.Seq(plain.Notes, GpuLimits.Compute(D, "t", 0, null).Notes, "not measured: unchanged");
            // ---- ComputeOtherUse
            Assert.Eq((long?)null, GpuLimits.ComputeOtherUse(null, null, null, null), "nothing known -> null");
            Assert.Eq((long?)5000L, GpuLimits.ComputeOtherUse(null, null, null, 5000L), "system counter only");
            Assert.Eq((long?)(2 * GiB), GpuLimits.ComputeOtherUse(24 * GiB, 22 * GiB, 0, null), "budget only: dedicated - budget");
            Assert.Eq((long?)(2 * GiB), GpuLimits.ComputeOtherUse(24 * GiB, 22 * GiB + 100, 100, null), "the process's own use is not 'other'");
            Assert.Eq((long?)(10 * GiB), GpuLimits.ComputeOtherUse(24 * GiB, 22 * GiB, 0, 10 * GiB), "the larger of the two");
            Assert.Eq((long?)0L, GpuLimits.ComputeOtherUse(24 * GiB, 30 * GiB, 0, null), "a budget above the dedicated size: 0, never negative");
            // ---- gpucheck JSON: the live figures of the default adapter
            string js = "{\"adapters\":[{\"luid\":\"0:2\",\"software\":false,\"dedicated_video_memory\":" + D
                        + ",\"video_memory\":{\"queried\":true,\"budget\":24505221120,\"current_usage\":4096,\"available_for_reservation\":12386828288,\"current_reservation\":0,\"system_dedicated_usage\":3412492288}}],"
                        + "\"default_adapter\":{\"luid\":\"0:2\",\"name\":\"X\",\"feature_level\":\"12_1\"},\"verdict\":{\"ok\":true}}";
            var gc = Core.Preflight.GpuCheck.Parse(js, 0);
            Assert.Eq((long?)24505221120L, gc.LiveBudgetBytes, "budget");
            Assert.Eq((long?)4096L, gc.LiveProcessUsageBytes, "process usage");
            Assert.Eq((long?)3412492288L, gc.SystemDedicatedUsageBytes, "system dedicated usage");
            var old = Core.Preflight.GpuCheck.Parse(js.Replace("\"video_memory\"", "\"video_memory_x\""), 0);
            Assert.True(!old.LiveBudgetBytes.HasValue && !old.SystemDedicatedUsageBytes.HasValue && old.DedicatedVideoMemoryBytes == D, "an older gpucheck (no live figures): dedicated only");
            // the REAL gpucheck.exe 1.1.0 output recorded on the dev PC (build\gpucheck\gpucheck_<host>.json): the parser reads its live figures
            foreach (var real in System.IO.Directory.GetFiles(System.IO.Path.Combine(C.Project, @"build\gpucheck"), "gpucheck_*.json").Where(x => !x.Contains("_plain_")))
            {
                string txt = System.IO.File.ReadAllText(real);
                if (!txt.Contains("\"video_memory\"")) continue;
                var rg = Core.Preflight.GpuCheck.Parse(txt, 0);
                Assert.True(rg.DedicatedVideoMemoryBytes > 0 && rg.LiveBudgetBytes > 0 && rg.LiveBudgetBytes <= rg.DedicatedVideoMemoryBytes, "real JSON: dedicated " + rg.DedicatedVideoMemoryBytes + ", budget " + rg.LiveBudgetBytes);
                Assert.True(rg.SystemDedicatedUsageBytes.HasValue, "real JSON: the system-wide counter is there");
                var other = GpuLimits.ComputeOtherUse(rg.DedicatedVideoMemoryBytes, rg.LiveBudgetBytes, rg.LiveProcessUsageBytes, rg.SystemDedicatedUsageBytes);
                Assert.True(other.HasValue && other.Value >= 0 && other.Value < rg.DedicatedVideoMemoryBytes, "real JSON: other use " + other);
            }
            var failed = Core.Preflight.GpuCheck.Parse(js.Replace("\"queried\":true", "\"queried\":false").Replace("\"system_dedicated_usage\":3412492288", "\"system_dedicated_usage\":null"), 0);
            Assert.True(!failed.LiveBudgetBytes.HasValue && !failed.SystemDedicatedUsageBytes.HasValue, "query failed / counter null: no figures");
        }

        private static void X2TdrMessage()
        {
            const string expect = "Windows reset the GPU because one GPU step ran too long (a TDR). Try \"Keep PC responsive - Slower Bakes!\", close other GPU programs, or report your GPU.";
            Assert.Eq(expect, DeviceLoss.Explanation, "the exact sentence");
            Assert.True(DeviceLoss.IsDeviceLossLine("DIRECT: *** GPU CAST DEVICE REMOVED (0x887A0006)"), "banner at the start");
            Assert.True(DeviceLoss.IsDeviceLossLine("   DIRECT: *** GPU CAST DEVICE REMOVED"), "leading blanks");
            Assert.True(DeviceLoss.IsDeviceLossLine("the GPU device was RESET by Windows (TDR)"), "TDR sentence anywhere");
            Assert.True(DeviceLoss.IsDeviceLossLine("fullgpu: DXGI_ERROR_DEVICE_HUNG during Dispatch"), "DEVICE_HUNG anywhere");
            Assert.True(!DeviceLoss.IsDeviceLossLine("x DIRECT: *** GPU CAST DEVICE REMOVED"), "the banner only counts at the start of the line");
            Assert.True(!DeviceLoss.IsDeviceLossLine("DIRECT: *** GPU CAST started") && !DeviceLoss.IsDeviceLossLine("") && !DeviceLoss.IsDeviceLossLine(null) && !DeviceLoss.IsDeviceLossLine("device removed"), "ordinary lines");
            Assert.Eq("plain", DeviceLoss.Compose("plain", false), "no device loss: the text is unchanged");
            var c = DeviceLoss.Compose("the bake stopped (exit 1): x", true);
            Assert.True(c.StartsWith(expect, StringComparison.Ordinal) && c.EndsWith("the bake stopped (exit 1): x", StringComparison.Ordinal), "explanation first, the technical reason kept: " + c);
            // the parser collects them from either stream
            var p = new ProgressParser();
            p.Feed("[3] GPU BAKE", false);
            Assert.True(!p.DeviceLost, "none yet");
            p.Feed("DIRECT: *** GPU CAST DEVICE REMOVED (hung)", false);
            p.Feed("pwsh: DEVICE_HUNG", true);
            Assert.Eq(2, p.DeviceLossLines.Count, "both streams");
            Assert.True(p.DeviceLost, "flag");
            Assert.True(DeviceLoss.AnyDeviceLoss(new[] { "a", "b was RESET by Windows (TDR)" }) && !DeviceLoss.AnyDeviceLoss(new[] { "a", "b" }) && !DeviceLoss.AnyDeviceLoss(null), "AnyDeviceLoss");
            // a log file (the AO driver's own log)
            string f = System.IO.Path.Combine(C.Sb.Dir("x2_tdr"), "drv.log");
            System.IO.File.WriteAllText(f, "tracing\nDIRECT: *** GPU CAST DEVICE REMOVED\nexit 3\n");
            Assert.True(DeviceLoss.FileHasDeviceLoss(f), "log file with the banner");
            System.IO.File.WriteAllText(f, "tracing\nexit 3\n");
            Assert.True(!DeviceLoss.FileHasDeviceLoss(f) && !DeviceLoss.FileHasDeviceLoss(f + ".missing") && !DeviceLoss.FileHasDeviceLoss(null), "clean / missing / null");
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
                                  new HashSet<string> { "--ao", "--reuse-capture", "--no-install", "--yes", "--responsive", "--exact-vertex-lighting", "--scenery-permutation-fix", "--dev-no-fixes", "--reuse-unrecorded" });
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

        private const string Tut = @"scenarios\solo\01a_tutorial", Spc = @"scenarios\solo\01b_spacestation";

        private static void CrossFolderPicker()
        {
            string root = C.Sb.Dir("unit_xbsp");
            Action<string> mk = rel => { string p = Path.Combine(root, "tags", rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "x"); };
            string scen = Spc + @"\01b_spacestation";
            mk(scen + ".scenario");
            mk(Spc + @"\01_bsp_2.scenario_structure_bsp");
            mk(Spc + @"\01outro_bsp.scenario_structure_bsp");
            mk(Tut + @"\01_bsp_0.scenario_structure_bsp");
            mk(Tut + @"\01_bsp_1.scenario_structure_bsp");
            var refs = new[] { Tut + @"\01_bsp_0", Tut + @"\01_bsp_1", Spc + @"\01_bsp_2", Spc + @"\01outro_bsp", Tut + @"\missing_bsp" };
            List<string> notes;
            // an engine that resolves the BSP from the scenario: every referenced BSP, other folders labelled
            var c = MapQuery.BspChoices(root, scen, refs, null, out notes);
            Assert.Seq(new[] { "01_bsp_0  (from " + Tut + ")", "01_bsp_1  (from " + Tut + ")", "01_bsp_2", "01outro_bsp" }, c.Select(x => x.Label).ToList(), "labels, in the scenario's order");
            Assert.Seq(new[] { "01_bsp_0", "01_bsp_1", "01_bsp_2", "01outro_bsp" }, c.Select(x => x.Name).ToList(), "the plain name is what gpu_bake -Bsp gets");
            Assert.Eq(Tut + @"\01_bsp_1", c[1].Rel, "the tag path of an other-folder BSP");
            Assert.True(c[0].CrossFolder && c[1].CrossFolder && !c[2].CrossFolder && !c[3].CrossFolder && c[2].Rel == null, "beside-the-scenario entries carry no path (every pre-0.6.4 bake)");
            Assert.Eq("01_bsp_1  (from scenarios\\solo\\01a_tutorial)", c[1].ToString(), "the exact label the user sees");
            Assert.Eq(1, notes.Count, "one note: " + string.Join(" | ", notes));
            Assert.Contains(notes[0], "missing_bsp", "the missing tag is named");
            Assert.Contains(notes[0], "does not exist", "and said to be missing");
            // an older engine: the pre-0.6.4 list (only a BSP file beside the scenario) + a plain note naming the others
            var o = MapQuery.BspChoices(root, scen, refs, BakeOptions.CrossFolderBspNeedsEngine, out notes);
            Assert.Seq(new[] { "01_bsp_2", "01outro_bsp" }, o.Select(x => x.Label).ToList(), "old engine: only the BSPs beside the scenario");
            Assert.True(o.All(x => !x.CrossFolder), "no other-folder entry");
            Assert.Eq(1, notes.Count, "one plain message");
            Assert.Contains(notes[0], "3 BSP(s) in other folders", "counts the ones it cannot offer");
            Assert.Contains(notes[0], "01_bsp_1 (" + Tut + ")", "names them with their folder");
            Assert.Contains(notes[0], "needs engine 0.6.4 or newer", "and says what is needed");
            // two entries with one name in different folders: the engine refuses them, so the picker does not offer them
            mk(@"scenarios\solo\other\01_bsp_1.scenario_structure_bsp");
            var amb = MapQuery.BspChoices(root, scen, refs.Concat(new[] { @"scenarios\solo\other\01_bsp_1" }), null, out notes);
            Assert.True(!amb.Any(x => x.Name == "01_bsp_1"), "ambiguous name not offered");
            Assert.True(notes.Any(n => n.Contains("2 structure_bsps entries named 01_bsp_1")), "and explained: " + string.Join(" | ", notes));
            // no usable reference: the folder listing, as before
            var folderList = MapQuery.Bsps(root, scen).ToArray();       // the folder listing's own order (ordinal, ignoring case)
            Assert.Seq(new[] { "01_bsp_2", "01outro_bsp" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), folderList, "the folder listing");
            Assert.Seq(folderList, MapQuery.BspChoices(root, scen, null, null, out notes).Select(x => x.Label).ToList(), "no references: the folder listing");
            Assert.Seq(folderList, MapQuery.BspChoices(root, scen, new[] { Tut + @"\missing_bsp" }, null, out notes).Select(x => x.Label).ToList(), "only a missing reference: the folder listing");
            // a referenced BSP that is ALSO copied beside the scenario: the referenced one (other folder) is what is offered
            mk(Spc + @"\01_bsp_1.scenario_structure_bsp");
            var dup = MapQuery.BspChoices(root, scen, refs, null, out notes);
            Assert.True(dup.Single(x => x.Name == "01_bsp_1").CrossFolder, "the copy beside the scenario does not hide the referenced BSP");

            // the map query's JSON carries the references
            var mi = MapQuery.Parse(new[] { "MAPQUERY {\"bsps\": [\"01_bsp_0\"], \"bspRefs\": [\"scenarios\\\\solo\\\\01a_tutorial\\\\01_bsp_0\", \"scenarios\\\\solo\\\\01a_tutorial\\\\01_bsp_1\"], \"points\": 0, \"pointsError\": null, \"ltmpExists\": false, \"envs\": null, \"groups\": null, \"envsError\": null}" }, 0, "");
            Assert.True(mi.Ok, "ok");
            Assert.Seq(new[] { Tut + @"\01_bsp_0", Tut + @"\01_bsp_1" }, mi.BspRefs, "bspRefs parsed, full paths");
            Assert.Seq(new[] { "01_bsp_0" }, mi.Bsps, "bsps unchanged (leaf names)");
            Assert.Eq(0, MapQuery.Parse(new[] { "MAPQUERY {\"bsps\": [], \"points\": 0, \"pointsError\": null, \"ltmpExists\": false}" }, 0, "").BspRefs.Count, "an old-format result has no refs");
            Assert.Contains(MapQuery.Code, "out['bspRefs'].append", "the snippet collects the full tag paths");

            // options
            var bo = BakeOptions.Defaults(); bo.Root = root; bo.ScenarioRel = scen; bo.Bsp = "01_bsp_1";
            Assert.Eq(0, bo.Validate(true).Count, "no BspRel: valid");
            Assert.Eq(null, bo.BspRel, "default: beside the scenario");
            Assert.Eq(Path.Combine(root, "tags", Spc, "01_bsp_1.scenario_structure_bsp"), bo.BspTagFile(Path.Combine(root, "tags", Spc)), "no BspRel: the file beside the scenario");
            bo.BspRel = Tut + @"\01_bsp_1";
            Assert.Eq(0, bo.Validate(true).Count, "an other-folder BSP path: valid");
            Assert.Eq(Path.Combine(root, "tags", Tut, "01_bsp_1.scenario_structure_bsp"), bo.BspTagFile(Path.Combine(root, "tags", Spc)), "BspRel: that tag");
            Assert.Contains(bo.Summary(), "BSP from " + Tut, "the options line says where the BSP is");
            Assert.True(!BakeOptions.Defaults().Summary().Contains("BSP from"), "default: nothing said");
            bo.BspRel = @"..\x\01_bsp_1"; Assert.True(bo.Validate(true).Any(e => e.Contains("not a plain tags-relative path")), "leaving the tags folder is refused");
            bo.BspRel = Tut + @"\other"; Assert.True(bo.Validate(true).Any(e => e.Contains("does not end in the BSP name")), "the path must end in the BSP name");
            bo.BspRel = @"C:\x\01_bsp_1"; Assert.True(bo.Validate(true).Count > 0, "a rooted path is refused");
            Assert.Eq(Tut, BakeOptions.BspFolder(Tut + @"\01_bsp_1"), "BspFolder");
            Assert.Eq("", BakeOptions.BspFolder("01_bsp_1"), "BspFolder of a bare name");
        }

        private static void CrossFolderGate()
        {
            var good = AppHost.Create(new HostConfig { PayloadDir = C.BuildPayload(C.Sb.Dir("payload_xbsp_gate"), crossBspEngine: true) });
            Assert.True(good.HasPayload, "payload loads: " + good.PayloadError);
            var o = BakeOptions.Defaults();
            Assert.Eq(null, good.CrossFolderBspWhy(o), "an engine whose gpu_bake.ps1 declares -WriterOnly and -ToolOverlay: supported");
            var old = AppHost.Create(new HostConfig { PayloadDir = C.PayloadDir });
            string why = old.CrossFolderBspWhy(o);
            Assert.True(why != null, "the default (0.6.3-shaped) test engine: not supported");
            Assert.Contains(why, BakeOptions.CrossFolderBspNeedsEngine, "ends with the plain requirement");
            Assert.Contains(why, "Baking a BSP from another folder needs engine 0.6.4 or newer.", "the exact sentence");
            Assert.Contains(why, "engine_v373/gpu_bake.ps1", "names the engine file");
            Assert.Contains(why, "-WriterOnly", "and the missing parameter");
            // Custom runs engine_custom: that engine decides for Custom
            var cust = AppHost.Create(new HostConfig { PayloadDir = C.BuildPayload(C.Sb.Dir("payload_xbsp_gate_c"), customEngine: true, crossBspEngine: true) });
            var oc = BakeOptions.Defaults(); oc.Quality = Quality.Custom;
            Assert.Eq(null, cust.CrossFolderBspWhy(oc), "Custom with a declaring engine_custom: supported");
            var none = AppHost.Create(new HostConfig { PayloadDir = Path.Combine(C.Sb.Dir("payload_xbsp_gate_none")) });
            Assert.True(!none.HasPayload && none.CrossFolderBspWhy(o) != null, "no payload: not supported");
            // the BSP selection is the only thing it gates: Compatibility mode and the Scenery fix gates are independent
            Assert.Eq(null, good.SceneryPermutationFixWhy(o), "independent of the scenery gate");
            Assert.True(good.CompatEngineWhy(o) != null, "independent of the compat gate (this payload has no compat engine)");
        }

        private static void FallbackDetect()
        {
            // the exact texts of engine 0.6.4 gpu_bake.ps1 (all printed by its Note function as "      *** <text>")
            var a = WriteBackFallback.Detect("      *** direct tag writer FAILED -- falling back to the tool write-back: PHASE 4a: the skeleton tool run failed (exit 1)");
            Assert.True(a != null && a.Direction == FallbackDirection.DirectToTool && !a.HeadsUp, "FAILED -- falling back: direct -> tool, an actual fallback");
            Assert.Eq("PHASE 4a: the skeleton tool run failed (exit 1)", a.Why, "the engine's reason");
            var b = WriteBackFallback.Detect(@"      *** -WriteBackMode direct cannot run: C:\x\v043-tagwriter\lm_tagwriter.py; a py with numpy -- using -WriteBackMode tool");
            Assert.True(b != null && b.Direction == FallbackDirection.DirectToTool && !b.HeadsUp, "cannot run -- using tool: direct -> tool");
            Assert.Eq(@"C:\x\v043-tagwriter\lm_tagwriter.py; a py with numpy", b.Why, "the missing pieces");
            var h = WriteBackFallback.Detect("      *** tag writer: the skeleton run did not start (boom) -- phase 4a will fall back");
            Assert.True(h != null && h.HeadsUp && h.Direction == FallbackDirection.DirectToTool && h.Why == "boom", "the heads-up before the fallback");
            Assert.True(h.Key != a.Key, "a heads-up and the fallback are different warnings");
            var r = WriteBackFallback.Detect("      *** falling back to the direct tag writer: tool write-back refused");
            Assert.True(r != null && r.Direction == FallbackDirection.ToolToDirect, "the opposite direction is recognised too");
            // not fallbacks
            Assert.True(WriteBackFallback.Detect("      *** tag writer FAILED: fake skeleton refused") == null, "a stage-only writer failure has no fallback");
            Assert.True(WriteBackFallback.Detect("[4] WRITE-BACK - inject the GPU lighting into the tool and let it save the tag") == null, "a phase banner");
            Assert.True(WriteBackFallback.Detect("      *** GPULM_COMPAT=1: the FP32-only double-single (df64) kernel set is FORCED") == null, "another note");
            Assert.True(WriteBackFallback.Detect("") == null && WriteBackFallback.Detect(null) == null && WriteBackFallback.Detect("   ") == null, "empty");
            // the shell's own warning is never detected again (its reason text can contain the engine's phrase)
            string w = WriteBackFallback.WarningText(a);
            Assert.True(w.StartsWith("WARNING: the lightmap write-back fell back from the DIRECT tag writer to the older TOOL write-back", StringComparison.Ordinal), "warning text: " + w);
            Assert.Contains(w, "PHASE 4a: the skeleton tool run failed (exit 1)", "carries the engine's reason");
            Assert.Contains(w, "ambient occlusion and light probes are not applied by this path", "says what it means");
            Assert.True(WriteBackFallback.Detect("*** " + w) == null && WriteBackFallback.Detect(w) == null, "own warning ignored");
            var self = new FallbackInfo { Direction = FallbackDirection.DirectToTool, Why = "direct tag writer FAILED -- falling back to the tool write-back: x" };
            Assert.True(WriteBackFallback.Detect("*** " + WriteBackFallback.WarningText(self)) == null, "even when the reason contains the phrase");
            Assert.Contains(WriteBackFallback.WarningText(h), "is about to fall back", "heads-up wording");
            Assert.Contains(WriteBackFallback.WarningText(r), "fell back from the TOOL write-back to the DIRECT tag writer", "reverse wording");
            Assert.Eq("WARNING: the write-back fell back from the DIRECT tag writer to the TOOL write-back (PHASE 4a: the skeleton tool run failed (exit 1))", WriteBackFallback.SummaryText(a), "summary sentence");
            Assert.Eq("WARNING: the write-back fell back from the TOOL write-back to the DIRECT tag writer (tool write-back refused)", WriteBackFallback.SummaryText(r), "reverse summary");
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
