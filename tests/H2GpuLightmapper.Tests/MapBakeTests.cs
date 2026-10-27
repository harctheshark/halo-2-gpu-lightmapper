using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Cli;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.Pipeline;
using static H2GpuLightmapper.Tests.Program;

namespace H2GpuLightmapper.Tests
{
    /// <summary>
    /// Milestone 2 (direct .map baking), headless: the .map plan (dry run through the exe), the sky cube-map dropdown model (listing, preselection, "Leave empty"),
    /// the model never exists for a .scenario, refusal of stock / compressed maps, and a .scenario bake's argv and plan staying exactly what they were.
    /// Milestone 3: AO / light probes (all three modes) and Vista maps (detection, --vista-out, output names, the Vista option only for Vista input) in the plan.
    /// No process of the .map path is started (the dry run starts nothing); the real chain is exercised by the milestone-2 validation runs.
    /// </summary>
    internal static class MapBakeTests
    {
        private const string Cyc = @"scenarios\multi\cyclotron\cyclotron";

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("mapbake: .map dry-run plan = map (read only), output <name>_gpulm.map next to it, the nine steps, cube map policy; starts nothing, writes nothing", MapPlan);
            yield return T("mapbake: stock (compressed / textures.dat) and non-cache maps are refused in plain words; so are reuse / AO with Lightprobes only / output = input / scenario + map / --vista-out on an MCC map", Refusals);
            yield return T("mapbake M3: AO and light probes are accepted on the .map path (Standard / Lightprobes only / Lightmap Only; the plan says what each does), AO strength and exponent are validated", AoAndProbes);
            yield return T("mapbake M3: a Vista map (cache v8) is detected and accepted; --vista-out yes|no decides the output (<name>_gpulm.map = Vista, <name>_gpulm_mcc.map = MCC), only a Vista map has the option", VistaMaps);
            yield return T("mapbake: sky cube-map dropdown model: every kit cube map listed once, the map's own labelled, Leave empty first, automatic match preselected (else Leave empty + the 30% warning)", CubemapListing);
            yield return T("mapbake: --sky-cubemap resolution (none / auto / tag path / sky leaf name) and its plain errors; the CLI option only exists for --map", CubemapCli);
            yield return T("mapbake: a .scenario bake is unchanged: same argv, same plan with and without the mapbake component, no cube-map model, no map keys", ScenarioUnchanged);
        }

        // ------------------------------------------------------------------ helpers

        private static string _mapPayload;
        private static string MapPayload() { return _mapPayload ?? (_mapPayload = C.BuildPayload(C.Sb.Dir("payload_map"), mapbakeComponent: true)); }

        private static List<string> MapBase(string verb, bool withMapbake)
        {
            var l = C.Base(verb);
            if (withMapbake) l[l.IndexOf("--payload-dir") + 1] = MapPayload();
            return l;
        }

        /// <summary>A file with a Halo 2 cache header only: version, flags, external mask, meta address mask.</summary>
        private static string FakeMap(string name, int version, ushort flags, ushort ext, uint mask)
        {
            string p = Path.Combine(C.Sb.Dir("maps"), name);
            var b = new byte[0x400];
            b[0] = (byte)'d'; b[1] = (byte)'a'; b[2] = (byte)'e'; b[3] = (byte)'h';
            BitConverter.GetBytes(version).CopyTo(b, 4);
            BitConverter.GetBytes(flags).CopyTo(b, 0x1C);
            BitConverter.GetBytes(ext).CopyTo(b, 0x1E);
            BitConverter.GetBytes(mask).CopyTo(b, 0x2D0);
            File.WriteAllBytes(p, b);
            return p;
        }

        private static CliResult MapDry(TestRoot r, bool json, params string[] extra)
        {
            var l = MapBase("bake", true);
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run" });
            if (json) l.Add("--json");
            l.AddRange(extra);
            return C.Run(l, 120000);
        }

        private static string Snapshot(string dir)
        {
            return string.Join("\n", Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                .Select(f => f + "|" + (File.Exists(f) ? new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks : "d")));
        }

        // ------------------------------------------------------------------ the .map plan

        private static void MapPlan()
        {
            var r = C.NewRoot();
            string map = FakeMap("headlong.map", 13, 0x10, 0, 0);
            string before = Snapshot(r.Root) + Snapshot(C.Lad);
            long mapLen = new FileInfo(map).Length; var mapTime = File.GetLastWriteTimeUtc(map);
            var t = MapDry(r, false, "--map", map, "--quality", "low", "--sky-cubemap", "headlong=none");
            Assert.Eq(0, t.Exit, "dry run ok\n" + t);
            string outp = Path.Combine(Path.GetDirectoryName(map), "headlong_gpulm.map");
            Assert.Contains(t.Out, "DRY RUN -- nothing is started and nothing is written.", "banner");
            Assert.Contains(t.Out, "source map  " + map + "   (READ ONLY: never modified)", "the .map path");
            Assert.Contains(t.Out, "output map  " + outp, "the default output next to the input, named <name>_gpulm.map");
            Assert.Contains(t.Out, "quality low", "the options");
            Assert.Contains(t.Out, "sky cubemap headlong = none", "the cube map choice given on the command line");
            for (int i = 0; i < MapBakeSession.StepTitles.Length; i++)
                Assert.Contains(t.Out, "STEP " + (i + 1).ToString().PadLeft(2) + "  " + MapBakeSession.StepTitles[i], "step " + (i + 1));
            Assert.Contains(t.Out, "read ONLY the lightmapper's dependency subset of the map", "map-read step");
            Assert.Contains(t.Out, "inject ONLY the lightmap and its bitmap (the lightmap alone for Lightprobes only) into a COPY of the map", "inject step");
            Assert.Contains(t.Out, "--scnr-clusters keep --shift-align 0", "the inject options");
            Assert.Contains(t.Out, "real-copy overlay root", "overlay step");
            Assert.Contains(t.Out, "temporary folder under the work folder", "the temp folder is named");
            Assert.Eq(mapLen, new FileInfo(map).Length, "the input map is untouched (size)");
            Assert.True(File.GetLastWriteTimeUtc(map) == mapTime, "the input map is untouched (time)");
            Assert.True(!File.Exists(outp), "the dry run wrote no output");
            Assert.Eq(before, Snapshot(r.Root) + Snapshot(C.Lad), "dry run: nothing created in the root or LOCALAPPDATA");
            // the policy line when no --sky-cubemap is given
            var d = MapDry(r, false, "--map", map);
            Assert.Eq(0, d.Exit, "dry run (no sky option)\n" + d);
            Assert.Contains(d.Out, "the automatic match", "default policy: the automatic match ...");
            Assert.Contains(d.Out, "LEFT EMPTY with a warning", "... else left empty with a warning");
            // JSON + a chosen output
            string chosen = Path.Combine(C.Sb.Dir("maps"), "mine.map");
            var j = MapDry(r, true, "--map", map, "--map-out", chosen, "--bsp", "headlong", "--sky-cubemap", @"scenarios\skies\multi\headlong\headlong=scenarios\skies\solo\earthcity\bitmaps\cubemap_ec_bridge");
            Assert.Eq(0, j.Exit, "json dry run\n" + j);
            var o = j.Json();
            Assert.Eq("h2lm-mapbake-plan/1", o.Str("schema"), "schema");
            Assert.Eq(map, o.Str("map"), "map"); Assert.Eq(chosen, o.Str("output"), "output"); Assert.Eq("headlong", o.Str("bsp"), "bsp");
            Assert.Eq(MapBakeSession.StepTitles.Length, o.Arr("steps").Count, "steps");
            Assert.Eq(1, o.Arr("skyCubemaps").Count, "one sky choice");
            Assert.Eq(@"scenarios\skies\solo\earthcity\bitmaps\cubemap_ec_bridge", ((JObj)o.Arr("skyCubemaps")[0]).Str("bitmap"), "the choice");
            // an existing output is announced as set aside (not overwritten)
            File.WriteAllBytes(outp, new byte[] { 1 });
            var e = MapDry(r, false, "--map", map);
            Assert.Contains(e.Out, "exists: set aside as .old_<time>", "an existing output is set aside, never overwritten");
            File.Delete(outp);
        }

        // ------------------------------------------------------------------ refusals

        private static void Refusals()
        {
            var r = C.NewRoot();
            string stock = FakeMap("stock.map", 13, 1, 0x7A, 0x3200000);       // compressed, external textures / shared.map
            var s = MapDry(r, false, "--map", stock);
            Assert.Eq(2, s.Exit, "stock map refused\n" + s);
            Assert.Contains(s.Out + s.Err, "stock-style Halo 2 map", "plain words");
            Assert.Contains(s.Out + s.Err, "compressed", "says compressed");
            Assert.Contains(s.Out + s.Err, "textures.dat", "says textures.dat");
            Assert.Contains(s.Out + s.Err, "not supported yet", "says not supported yet");
            // only the external mask / meta mask set: still a stock map
            Assert.Eq(2, MapDry(r, false, "--map", FakeMap("ext.map", 13, 0x10, 0x1E, 0)).Exit, "external store mask -> refused");
            Assert.Eq(2, MapDry(r, false, "--map", FakeMap("mask.map", 13, 0x10, 0, 0x3200000)).Exit, "shared.map address mask -> refused");
            Assert.Eq(null, MapHeaderInfo.Read(FakeMap("vista.map", 8, 0, 0, 0)).Refusal(), "a Vista map (milestone 3) is not refused by the header check");
            Assert.Eq(2, MapDry(r, false, "--map", FakeMap("v9.map", 9, 0, 0, 0)).Exit, "an unknown cache version is refused");
            string junk = Path.Combine(C.Sb.Dir("maps"), "junk.map"); File.WriteAllBytes(junk, new byte[0x400]);
            var j = MapDry(r, false, "--map", junk);
            Assert.Eq(2, j.Exit, "not a cache\n" + j);
            Assert.Contains(j.Out + j.Err, "not a Halo 2 cache file", "plain words");
            Assert.Eq(2, MapDry(r, false, "--map", Path.Combine(C.Sb.Dir("maps"), "missing.map")).Exit, "missing file");
            // a good map, bad options
            string ok = FakeMap("ok.map", 13, 0x10, 0, 0);
            Assert.Eq(0, MapDry(r, false, "--map", ok).Exit, "the good map plans");
            var ao = MapDry(r, false, "--map", ok, "--ao", "--mode", "probes");
            Assert.Eq(2, ao.Exit, "AO with Lightprobes only refused\n" + ao); Assert.Contains(ao.Out + ao.Err, "not available in Lightprobes mode", "plain words");
            var vo = MapDry(r, false, "--map", ok, "--vista-out", "yes");
            Assert.Eq(2, vo.Exit, "--vista-out on an MCC map refused\n" + vo); Assert.Contains(vo.Out + vo.Err, "--vista-out is for a Vista map only", "plain words");
            Assert.Eq(2, MapDry(r, false, "--map", FakeMap("v8b.map", 8, 0, 0, 0), "--vista-out", "maybe").Exit, "--vista-out needs yes or no");
            Assert.Eq(2, MapDry(r, false, "--map", ok, "--reuse-capture").Exit, "reuse refused");
            var same = MapDry(r, false, "--map", ok, "--map-out", ok);
            Assert.Eq(2, same.Exit, "output = input refused\n" + same); Assert.Contains(same.Out + same.Err, "overwrite the input map", "plain words");
            Assert.Eq(2, MapDry(r, false, "--map", ok, "--scenario", Cyc, "--bsp", "cyclotron").Exit, "scenario + map refused");
            Assert.Eq(2, MapDry(r, false, "--map", ok, "--map-out", Path.Combine(C.Sb.Dir("maps"), "nofolder", "x.map")).Exit, "output folder must exist");
            Assert.Eq(2, MapDry(r, false, "--map", ok, "--map-out", Path.Combine(C.Sb.Dir("maps"), "x.txt")).Exit, "output must end in .map");
            // a payload without the mapbake component says so
            var l = C.Base("bake");
            l.AddRange(new[] { "--h2ek", r.Root, "--dry-run", "--map", ok });
            var np = C.Run(l, 120000);
            Assert.Eq(2, np.Exit, "no mapbake component -> refused\n" + np);
            Assert.Contains(np.Out + np.Err, "no .map baking component", "plain words");
            Assert.True(!File.Exists(Path.Combine(Path.GetDirectoryName(ok), "ok_gpulm.map")), "nothing written");
        }


        // ------------------------------------------------------------------ milestone 3: AO and light probes

        private static void AoAndProbes()
        {
            var r = C.NewRoot();
            string map = FakeMap("probes.map", 13, 0x10, 0, 0);
            string before = Snapshot(r.Root) + Snapshot(C.Lad);
            // AO on: accepted; the plan names it with its numbers; JSON carries it
            var a = MapDry(r, false, "--map", map, "--ao", "--ao-strength", "1.0", "--quality", "low");
            Assert.Eq(0, a.Exit, "AO accepted on the .map path\n" + a);
            Assert.Contains(a.Out, "AO on (strength 1, exponent 0.5)", "the options line");
            Assert.Contains(a.Out, "ambient occlusion (strength 1, exponent 0.5", "the layers line");
            Assert.NotContains(a.Out, "not available for a .map", "the milestone-2 refusal text is gone");
            var aj = MapDry(r, true, "--map", map, "--ao", "--ao-strength", "0.75", "--ao-exponent", "1");
            Assert.Eq(0, aj.Exit, "AO json\n" + aj);
            Assert.Eq(true, aj.Json().Bool("ao"), "json: ao"); Assert.Eq("standard", aj.Json().Str("mode"), "json: mode");
            // AO off: nothing about AO
            var n = MapDry(r, false, "--map", map);
            Assert.Eq(0, n.Exit, "AO off\n" + n);
            Assert.Contains(n.Out, "AO off", "AO off in the options"); Assert.NotContains(n.Out, "ambient occlusion (strength", "no AO layer line when off");
            Assert.Contains(n.Out, "light probes when the BSP has probe points", "Standard: probes when the BSP has them (decided by a real run)");
            // strength / exponent are validated like a scenario's
            Assert.Eq(2, MapDry(r, false, "--map", map, "--ao", "--ao-strength", "5").Exit, "AO strength out of range");
            Assert.Eq(2, MapDry(r, false, "--map", map, "--ao", "--ao-exponent", "0.01").Exit, "AO exponent out of range");
            // the three probe modes
            var p = MapDry(r, false, "--map", map, "--mode", "probes");
            Assert.Eq(0, p.Exit, "Lightprobes only accepted\n" + p);
            Assert.Contains(p.Out, "Lightprobes only: no lightmap is baked", "the layers line says the lightmap tag alone is injected");
            Assert.Contains(p.Out, "mode probes", "the options line");
            Assert.Eq("probes", MapDry(r, true, "--map", map, "--mode", "probes").Json().Str("mode"), "json mode probes");
            var l = MapDry(r, false, "--map", map, "--mode", "lightmap");
            Assert.Eq(0, l.Exit, "Lightmap Only accepted\n" + l);
            Assert.Contains(l.Out, "Lightmap Only: a BSP with probe points has them reset to zero", "the plan says Lightmap Only zeroes probes, like the scenario path");
            Assert.Contains(a.Out + n.Out + p.Out, "inject ONLY the lightmap", "the inject step is in every plan");
            Assert.Eq(before, Snapshot(r.Root) + Snapshot(C.Lad), "dry runs: nothing created");
            // the options object: AO + probes mode is invalid, a map bake with AO is valid
            var o = BakeOptions.Defaults(); o.Root = @"C:\H2"; o.MapPath = @"C:\m\a.map"; o.Ao = true;
            Assert.Eq(0, o.Validate(true).Count, "map + AO valid: " + string.Join("; ", o.Validate(true)));
            o.Mode = BakeMode.Probes;
            Assert.True(o.Validate(true).Any(x => x.Contains("Lightprobes mode")), "map + AO + Lightprobes only invalid");
            o.Ao = false;
            Assert.Eq(0, o.Validate(true).Count, "map + Lightprobes only valid");
            Assert.Contains(o.Summary(), "mode probes", "summary names the mode");
            // the map query carries the probe points (a one-bsp map: the sum; several bsps: unknown)
            var q = FakeQuery(); q.ProbePoints.Add(11);
            Assert.Eq(11, q.SingleBspProbePoints.Value, "probe points of the only bsp");
            q.Bsps.Add(@"scenarios\multi\x\second");
            Assert.True(!q.SingleBspProbePoints.HasValue, "unknown with several bsps");
        }

        // ------------------------------------------------------------------ milestone 3: Vista maps

        private static void VistaMaps()
        {
            var r = C.NewRoot();
            string v8 = FakeMap("headlong_mcc.map", 8, 0, 0, 0), v13 = FakeMap("mcc.map", 13, 0x10, 0, 0);
            // detection: only a v8 cache is a Vista map (this is what shows the window's "Convert result back to Vista" checkbox)
            Assert.True(MapHeaderInfo.Read(v8).IsVista, "v8 = Vista");
            Assert.True(!MapHeaderInfo.Read(v13).IsVista, "v13 is not Vista");
            Assert.True(!MapHeaderInfo.Read(Path.Combine(C.Sb.Dir("maps"), "nothing.map")).IsVista, "an unreadable file is not Vista");
            string stockMcc = FakeMap("stock2.map", 13, 1, 0x7A, 0x3200000);
            Assert.True(!MapHeaderInfo.Read(stockMcc).IsVista, "a stock MCC map is not Vista");
            // option defaults: ON for a Vista map; the output name follows the choice
            var o = BakeOptions.Defaults(); o.MapPath = @"C:\m\headlong_mcc.map"; o.MapIsVista = true;
            Assert.True(o.VistaOut, "Convert result back to Vista defaults to ON");
            Assert.Eq(@"C:\m\headlong_mcc_gpulm.map", o.MapOutPath, "Vista result: <name>_gpulm.map");
            o.VistaOut = false;
            Assert.Eq(@"C:\m\headlong_mcc_gpulm_mcc.map", o.MapOutPath, "MCC result: <name>_gpulm_mcc.map");
            o.MapOut = @"C:\m\mine.map";
            Assert.Eq(@"C:\m\mine.map", o.MapOutPath, "an explicit --map-out wins");
            o.MapOut = null; o.MapIsVista = false;
            Assert.Eq(@"C:\m\headlong_mcc_gpulm.map", o.MapOutPath, "an MCC map keeps <name>_gpulm.map whatever VistaOut says");
            // the CLI
            var a = P("--h2ek", @"C:\H2", "--map", v8, "--vista-out", "no");
            var oa = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "parsed: " + string.Join("; ", a.Errors));
            Assert.True(oa.MapIsVista && oa.VistaOutGiven && !oa.VistaOut, "--vista-out no on a Vista map");
            Assert.Eq(Path.Combine(Path.GetDirectoryName(v8), "headlong_mcc_gpulm_mcc.map"), oa.MapOutPath, "MCC intermediate name");
            var ob = CliMain.BakeOptionsFrom(P("--h2ek", @"C:\H2", "--map", v8));
            Assert.True(ob.MapIsVista && ob.VistaOut && !ob.VistaOutGiven, "no flag: yes for a Vista map");
            var oc = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--vista-out", "yes");
            CliMain.BakeOptionsFrom(oc);
            Assert.True(oc.Errors.Any(x => x.Contains("--vista-out is only for --map")), "--vista-out on a scenario bake");
            // the dry-run plan
            string before = Snapshot(r.Root) + Snapshot(C.Lad);
            var y = MapDry(r, false, "--map", v8);
            Assert.Eq(0, y.Exit, "Vista map plans\n" + y);
            string dir = Path.GetDirectoryName(v8);
            Assert.Contains(y.Out, "output map  " + Path.Combine(dir, "headlong_mcc_gpulm.map"), "default: the Vista result");
            Assert.Contains(y.Out, "the input is a Halo 2 Vista map", "the plan says it is a Vista map");
            Assert.Contains(y.Out, "converted back to a Vista map", "default yes");
            Assert.Contains(y.Out, "Vista map: result converted back to Vista", "options line");
            var no = MapDry(r, false, "--map", v8, "--vista-out", "no");
            Assert.Eq(0, no.Exit, "--vista-out no plans\n" + no);
            Assert.Contains(no.Out, "output map  " + Path.Combine(dir, "headlong_mcc_gpulm_mcc.map"), "MCC result name");
            Assert.Contains(no.Out, "not converted back", "says so");
            var j = MapDry(r, true, "--map", v8, "--vista-out", "yes");
            Assert.Eq(0, j.Exit, "json\n" + j);
            Assert.Eq(true, j.Json().Bool("vistaInput"), "json: vistaInput"); Assert.Eq(true, j.Json().Bool("vistaOut"), "json: vistaOut");
            // an MCC map never shows any of it
            var m = MapDry(r, false, "--map", v13);
            Assert.Eq(0, m.Exit, "MCC plans\n" + m);
            Assert.NotContains(m.Out, "the input is a Halo 2 Vista map", "an MCC map's plan has no Vista line");
            Assert.NotContains(m.Out, "Vista map: result", "an MCC map's options have no Vista part");
            Assert.Contains(m.Out, "output map  " + Path.Combine(C.Sb.Dir("maps"), "mcc_gpulm.map"), "MCC default name");
            Assert.Eq(before, Snapshot(r.Root) + Snapshot(C.Lad), "dry runs: nothing created");
        }

        // ------------------------------------------------------------------ the dropdown model

        private static MapQueryResult FakeQuery(bool withAuto = true)
        {
            Func<string, bool, string, JObj> sky = (name, needs, auto) => new JObj().Add("name", name).Add("render_model", name).Add("needs_cubemap", needs)
                .Add("cube_elements", new List<object> { new JObj().Add("power_scale", needs ? 0.3 : 0.0).Add("has_reference", !needs) })
                .Add("auto", new JObj().Add("rule", auto != null ? "A" : "B").Add("cubemap", auto).Add("in_map", false).Add("in_kit", auto != null));
            string ec = @"scenarios\skies\solo\earthcity\bitmaps\cubemap_ec_bridge";
            var j = new JObj().Add("ok", true).Add("kind", "mcc").Add("scenario", @"scenarios\multi\headlong\headlong").Add("internal_name", "headlong").Add("tag_count", 6178L)
                .Add("bsps", new List<object> { @"scenarios\multi\headlong\headlong" })
                .Add("map_cubemaps", new List<object> { @"objects\bitmaps\reflection_maps\dark_gray", @"quag\custom\mapcube" })
                .Add("kit_cubemaps", new List<object> { @"cubemaps\a", @"cubemaps\b", ec, @"objects\bitmaps\reflection_maps\dark_gray" })
                .Add("skies", new List<object>
                {
                    sky(@"scenarios\skies\multi\headlong\headlong", true, withAuto ? ec : null),
                    sky(@"quag\levels\x\second", true, null),
                    sky(@"quag\levels\x\nopower", false, null),
                });
            return MapQueryResult.Parse(Json.Write(j, false));
        }

        private static void CubemapListing()
        {
            var q = FakeQuery();
            Assert.True(q.Ok, "parsed");
            Assert.Eq(3, q.Skies.Count, "three skies parsed");
            var m = CubemapModel.Build(q);
            Assert.Eq(2, m.Count, "only the skies with power scale > 0 and no reference get a dropdown (one per sky)");
            string ec = @"scenarios\skies\solo\earthcity\bitmaps\cubemap_ec_bridge";
            var a = m[0];
            Assert.Eq(CubemapKind.LeaveEmpty, a.Items[0].Kind, "'Leave empty' is the first entry");
            Assert.Eq("none", a.Items[0].Value, "Leave empty passes 'none'");
            Assert.Eq(CubemapKind.Automatic, a.Items[1].Kind, "the automatic match is the second entry");
            Assert.Eq(ec, a.Items[1].Path, "the rule A match (the earthcity bridge cube map)");
            Assert.True(ReferenceEquals(a.Selected, a.AutoItem), "the automatic match is PRESELECTED");
            Assert.Eq(null, a.Warning, "no warning when the match exists");
            // EVERY kit cube map exactly once, the map's own labelled as such
            foreach (var k in q.KitCubemaps) Assert.Eq(1, a.Items.Count(i => i.Path != null && string.Equals(i.Path, k, StringComparison.OrdinalIgnoreCase)), "kit cube map listed once: " + k);
            var own = a.Items.Single(i => i.Path == @"quag\custom\mapcube");
            Assert.Eq(CubemapKind.InMap, own.Kind, "a cube map only the .map carries");
            Assert.Contains(own.Label, "(in this .map)", "labelled as the map's own");
            Assert.Contains(a.Items.Single(i => i.Path == @"objects\bitmaps\reflection_maps\dark_gray").Label, "also in this .map", "a kit cube map the map carries too says so");
            Assert.Eq(1 + 1 + 3 + 1, a.Items.Count, "leave empty + auto + the other kit cube maps + the map-only one");
            // no automatic match: Leave empty is preselected and the plain warning is shown
            var b = m[1];
            Assert.Eq(null, b.AutoItem, "no automatic item");
            Assert.Eq(CubemapKind.LeaveEmpty, b.Selected.Kind, "Leave empty preselected");
            Assert.Contains(b.Warning, "about 30% darker on headlong", "the warning names the cost");
            Assert.Contains(b.Warning, "ambient fill", "the warning names what is lost");
            Assert.Eq(1 + 4 + 1, b.Items.Count, "leave empty + 4 kit + the map-only one (no automatic entry)");
            // a map whose skies need no choice has no dropdown at all
            var none = FakeQuery(); none.Skies.ForEach(s => s.NeedsCubemap = false);
            Assert.Eq(0, CubemapModel.Build(none).Count, "no dropdown when no sky needs a choice");
            Assert.Eq(0, CubemapModel.Build(null).Count, "no query -> nothing");
            // the description lines the plan / log carry
            var d = CubemapModel.Describe(m);
            Assert.Contains(d[0], "(automatic match)", "describe: automatic");
            Assert.Contains(d[1], "LEFT EMPTY", "describe: left empty"); Assert.Contains(d[1], "WARNING", "describe: warning");
        }

        private static void CubemapCli()
        {
            var q = FakeQuery();
            Func<List<SkyChoice>> fresh = () => CubemapModel.Build(q);
            Func<string, string, KeyValuePair<string, string>> kv = (k, v) => new KeyValuePair<string, string>(k, v);
            var m = fresh();
            Assert.Eq(0, CubemapModel.Apply(m, new[] { kv("headlong", "none") }).Count, "none by the sky's leaf name");
            Assert.Eq(CubemapKind.LeaveEmpty, m[0].Selected.Kind, "none = Leave empty");
            m = fresh();
            Assert.Eq(0, CubemapModel.Apply(m, new[] { kv(@"scenarios\skies\multi\headlong\headlong", "AUTO") }).Count, "auto by the full tag name");
            Assert.Eq(CubemapKind.Automatic, m[0].Selected.Kind, "auto");
            m = fresh();
            var e1 = CubemapModel.Apply(m, new[] { kv("second", "auto") });
            Assert.Eq(1, e1.Count, "auto without a match is an error"); Assert.Contains(e1[0], "no automatic cube map match", "plain words");
            m = fresh();
            Assert.Eq(0, CubemapModel.Apply(m, new[] { kv("second", "cubemaps/B.bitmap") }).Count, "a kit cube map by tag path (slashes, case, extension tolerated)");
            Assert.Eq(@"cubemaps\b", m[1].Selected.Path, "resolved to the kit's own spelling");
            Assert.Eq(null, m[1].Warning, "an explicit choice clears the warning");
            Assert.Eq(0, CubemapModel.Apply(m, new[] { kv("second", @"quag\custom\mapcube") }).Count, "the map's own cube map");
            Assert.Eq(CubemapKind.InMap, m[1].Selected.Kind, "in-map kind");
            var e2 = CubemapModel.Apply(fresh(), new[] { kv("second", @"cubemaps\nope") });
            Assert.Eq(1, e2.Count, "an unknown bitmap is refused"); Assert.Contains(e2[0], "not a cube-map bitmap", "plain words");
            var e3 = CubemapModel.Apply(fresh(), new[] { kv("zzz", "none") });
            Assert.Eq(1, e3.Count, "an unknown sky is refused"); Assert.Contains(e3[0], "has no sky 'zzz'", "plain words");
            // ambiguous leaf
            var amb = FakeQuery(); amb.Skies.Add(new MapSkyInfo { Name = @"other\second", NeedsCubemap = true });
            var e4 = CubemapModel.Apply(CubemapModel.Build(amb), new[] { kv("second", "none") });
            Assert.Eq(1, e4.Count, "an ambiguous sky name is refused"); Assert.Contains(e4[0], "matches 2 skies", "plain words");
            // CLI parsing: repeatable, strict, map-only
            var a = P("--h2ek", @"C:\H2", "--map", @"C:\m\headlong.map", "--sky-cubemap", "a=b", "--sky-cubemap", "c=none", "--map-out", @"C:\m\o.map");
            var o = CliMain.BakeOptionsFrom(a);
            Assert.Eq(0, a.Errors.Count, "no errors: " + string.Join("; ", a.Errors));
            Assert.True(o.IsMapBake, "map bake"); Assert.Eq(2, o.SkyCubemaps.Count, "two choices"); Assert.Eq("c", o.SkyCubemaps[1].Key, "order kept"); Assert.Eq("none", o.SkyCubemaps[1].Value, "value");
            Assert.Eq(@"C:\m\o.map", o.MapOutPath, "--map-out");
            var bad = P("--h2ek", @"C:\H2", "--map", @"C:\m\headlong.map", "--sky-cubemap", "noequals");
            CliMain.BakeOptionsFrom(bad);
            Assert.True(bad.Errors.Any(x => x.Contains("--sky-cubemap expects")), "malformed choice");
            var sc = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--sky-cubemap", "a=b");
            CliMain.BakeOptionsFrom(sc);
            Assert.True(sc.Errors.Any(x => x.Contains("only for --map")), "--sky-cubemap on a scenario bake is an error: " + string.Join("; ", sc.Errors));
            var so = P("--h2ek", @"C:\H2", "--scenario", "s", "--bsp", "b", "--map-out", "x.map");
            CliMain.BakeOptionsFrom(so);
            Assert.True(so.Errors.Any(x => x.Contains("--map-out is only for --map")), "--map-out on a scenario bake");
            Assert.Eq(@"C:\m\headlong_gpulm.map", BakeOptions.DefaultMapOut(@"C:\m\headlong.map"), "default output name");
        }

        private static CliArgs P(params string[] a)
        {
            var args = new[] { "--cli", "bake" }.Concat(a).ToArray();
            return CliArgs.Parse(args, 2, new HashSet<string> { "--h2ek", "--scenario", "--bsp", "--map", "--map-out", "--quality", "--photons", "--rays", "--mode", "--worker", "--vista-out", "--ao-strength", "--ao-exponent" },
                                 new HashSet<string> { "--ao", "--reuse-capture", "--no-install", "--yes", "--responsive", "--dev-no-fixes" }, new HashSet<string> { "--sky-cubemap" });
        }

        // ------------------------------------------------------------------ the scenario path is unchanged

        private static void ScenarioUnchanged()
        {
            // 1. the argv builder: the literal production argv, unchanged
            var o = BakeOptions.Defaults(); o.Root = @"C:\H2"; o.ScenarioRel = Cyc; o.Bsp = "cyclotron";
            Assert.Eq(false, o.IsMapBake, "a scenario bake is not a map bake");
            Assert.Seq(new[] { "-Scenario", Cyc, "-Bsp", "cyclotron", "-Worker", "200", "-Quality", "super", "-WriteBack", "-SceneryFix", "-PluckerFix", "-WriteBackMode", "direct" },
                       BakePlanner.GpuBakeArgs(o, 200, true, false), "the production argv");
            Assert.True(o.Summary().IndexOf("map", StringComparison.OrdinalIgnoreCase) < 0 || o.Summary().Contains("capture"), "the scenario summary is the old one: " + o.Summary());
            Assert.Eq(0, o.Validate(true).Count, "valid as before");
            // 2. the dry-run plan JSON is identical whether or not the payload carries the mapbake component (the .map code is not on this path)
            var r = C.NewRoot();
            string strip(string s) { return System.Text.RegularExpressions.Regex.Replace(s, "\"payload\": \"[^\"]*\",?", "").Replace(MapPayload(), "").Replace(C.PayloadDir, ""); }
            foreach (var extra in new[] { new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--quality", "low", "--no-install" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--ao", "--no-install", "--responsive" },
                                          new[] { "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap", "--worker", "281", "--exact-vertex-lighting" } })
            {
                var with = C.Run(MapBase("bake", true).Concat(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--probe-points", "0" }).Concat(extra));
                var without = C.Run(MapBase("bake", false).Concat(new[] { "--h2ek", r.Root, "--dry-run", "--json", "--probe-points", "0" }).Concat(extra));
                Assert.Eq(0, with.Exit, "with component\n" + with); Assert.Eq(0, without.Exit, "without component\n" + without);
                string a = System.Text.RegularExpressions.Regex.Replace(strip(with.Out), @"\d{8}_\d{6}_", "STAMP_"), b = System.Text.RegularExpressions.Regex.Replace(strip(without.Out), @"\d{8}_\d{6}_", "STAMP_");
                Assert.Eq(b, a, "the scenario plan JSON is byte-identical with and without the mapbake component (" + string.Join(" ", extra.Skip(2)) + ")");
                Assert.NotContains(with.Out, "skyCubemaps", "a scenario plan has no cube map choice");
                Assert.NotContains(with.Out, "mapbake", "a scenario plan never mentions the .map path");
                Assert.NotContains(with.Out, "_gpulm", "a scenario plan has no map output");
            }
            // 3. no cube-map model exists for a scenario; the CLI refuses the option
            Assert.Eq(0, CubemapModel.Build(null).Count, "no model without a map query");
            var sc = C.Run(MapBase("bake", true).Concat(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Cyc, "--bsp", "cyclotron", "--sky-cubemap", "a=none" }));
            Assert.Eq(2, sc.Exit, "--sky-cubemap with --scenario refused\n" + sc);
            Assert.Contains(sc.Err + sc.Out, "only for --map", "plain words");
            Assert.Eq(2, C.Run(MapBase("bake", true).Concat(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Cyc, "--bsp", "cyclotron", "--map-out", "x.map" })).Exit, "--map-out with --scenario refused");
            // 4. a scenario dry run is still text-identical in its header (no map line)
            var t = C.Run(MapBase("bake", true).Concat(new[] { "--h2ek", r.Root, "--dry-run", "--scenario", Cyc, "--bsp", "cyclotron", "--mode", "lightmap" }));
            Assert.Eq(0, t.Exit, "text dry run\n" + t);
            Assert.NotContains(t.Out, "source map", "no .map lines in a scenario plan");
        }
    }
}
