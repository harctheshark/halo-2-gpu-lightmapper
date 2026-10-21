// The map pieces of the UI / CLI (FINAL_DESIGN section 4.2): the scenarios under ROOT\tags, a scenario's BSPs, and a read-only
// query of the probe-point count, the lightmap's lighting-environment count and the scenario's structure-bsps list.
// The query is a short python program run by the BUNDLE py over the INST tag walker (v043-tagwriter\lmw_tagio, the same one
// lp_probes.py / sh_stage.py use); its probe rule is lp_probes.py's, verbatim: the `structure bsp lighting` element whose
// `bsp` reference's last path component equals the BSP name; one probe per `lighting points` entry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Pipeline
{
    /// <summary>One entry of the window's BSP picker. <see cref="Name"/> is the plain BSP name every bake argument uses; <see cref="Rel"/> is the BSP's
    /// tags-relative tag path (no extension) when it lives in ANOTHER folder than the scenario, else null.</summary>
    public sealed class BspChoice
    {
        public string Name, Rel, Label;
        public bool CrossFolder { get { return Rel != null; } }
        public static BspChoice Plain(string name) { return new BspChoice { Name = name, Label = name }; }
        public static BspChoice Other(string name, string rel)
        {
            return new BspChoice { Name = name, Rel = rel, Label = name + "  (from " + BakeOptions.BspFolder(rel) + ")" };
        }
        public override string ToString() { return Label; }
    }

    public sealed class MapInfo
    {
        public List<string> Bsps = new List<string>();
        /// <summary>0.6.4: the tag path (backslashes, no extension, as stored in the scenario) of every structure_bsps entry, in order, duplicates kept --
        /// so a BSP that lives in another folder than the scenario can be found.</summary>
        public List<string> BspRefs = new List<string>();
        public int? Points;
        public bool LtmpExists;
        public int? Envs, Groups;
        public string PointsError, EnvsError, Error;
        public string Raw;
        public bool Ok { get { return Error == null && PointsError == null; } }
    }

    public static class MapQuery
    {
        // Single quotes only (the argv is quoted by JobProcess.QuoteArg; no backslash escapes needed in C#).
        public static readonly string Code = string.Join("\n", new[]
        {
            "import sys, os, re, json",
            "sys.dont_write_bytecode = True",
            "tagio, scen, bsp, ltmp = sys.argv[1:5]",
            "sys.path.insert(0, tagio)",
            "out = {'bsps': [], 'bspRefs': [], 'points': None, 'pointsError': None, 'ltmpExists': os.path.exists(ltmp), 'envs': None, 'groups': None, 'envsError': None}",
            "try:",
            "    import lmw_tagio as T",
            "except Exception as e:",
            "    out['pointsError'] = 'lmw_tagio: %s: %s' % (type(e).__name__, e); print('MAPQUERY ' + json.dumps(out)); sys.exit(0)",
            "try:",
            "    d = open(scen, 'rb').read(); W = T.Walk(d, 'scnr', keep_fields=True)",
            "    for r in W.tagrefs:",
            "        if re.match(r'root\\[0\\]\\.structure bsps\\[\\d+\\]\\.structure bsp\\^$', r.path) and r.name:",
            "            n = r.name.replace('/', chr(92)).split(chr(92))[-1]",
            "            if n not in out['bsps']: out['bsps'].append(n)",
            "            out['bspRefs'].append(r.name.replace('/', chr(92)))",
            "    el = None",
            "    for r in W.tagrefs:",
            "        m = re.match(r'root\\[0\\]\\.structure bsp lighting\\[(\\d+)\\]\\.bsp\\*\\^$', r.path)",
            "        if m and r.name and r.name.replace('/', chr(92)).split(chr(92))[-1].lower() == bsp.lower(): el = int(m.group(1)); break",
            "    if el is None: out['points'] = 0",
            "    else:",
            "        pat = re.compile(r'root\\[0\\]\\.structure bsp lighting\\[%d\\]\\.lighting points\\[(\\d+)\\]\\.position$' % el)",
            "        out['points'] = sum(1 for p, o, t, s in W.fields if pat.match(p))",
            "except Exception as e:",
            "    out['pointsError'] = '%s: %s' % (type(e).__name__, e)",
            "if out['ltmpExists']:",
            "    try:",
            "        L = T.Walk(open(ltmp, 'rb').read(), 'ltmp', keep_fields=True)",
            "        ENV = 'root[0].lightmap groups[0].lighting environments'",
            "        out['groups'] = L.count('root[0].lightmap groups')",
            "        out['envs'] = L.count(ENV) if any(T.npath(p) == T.npath(ENV) for p in L.cnt_addr) else 0",
            "    except Exception as e:",
            "        out['envsError'] = '%s: %s' % (type(e).__name__, e)",
            "print('MAPQUERY ' + json.dumps(out))",
        });

        public static List<string> Args(string tagIoDir, string scenarioFile, string bsp, string ltmpFile)
        {
            return new List<string> { "-B", "-c", Code, tagIoDir, scenarioFile, bsp ?? "", ltmpFile };
        }

        public static MapInfo Parse(IEnumerable<string> stdoutLines, int exitCode, string stderr)
        {
            var mi = new MapInfo();
            string line = stdoutLines.LastOrDefault(l => l.StartsWith("MAPQUERY ", StringComparison.Ordinal));
            if (line == null) { mi.Error = "the map query printed no result (exit " + exitCode + "): " + (stderr ?? "").Trim(); return mi; }
            mi.Raw = line.Substring(9);
            JObj j;
            try { j = Json.ParseObj(mi.Raw); }
            catch (FormatException e) { mi.Error = "the map query result is not JSON: " + e.Message; return mi; }
            var b = j.Arr("bsps");
            if (b != null) mi.Bsps = b.OfType<string>().ToList();
            var br = j.Arr("bspRefs");
            if (br != null) mi.BspRefs = br.OfType<string>().ToList();
            mi.Points = j.Int("points");
            mi.PointsError = j.Str("pointsError");
            mi.LtmpExists = j.Bool("ltmpExists") == true;
            mi.Envs = j.Int("envs");
            mi.Groups = j.Int("groups");
            mi.EnvsError = j.Str("envsError");
            return mi;
        }

        /// <summary>Runs the query (hidden, BELOW_NORMAL, in a job). With a BakeRun it is logged as a run step.</summary>
        public static MapInfo Run(string pyExe, IList<string> args, string cwd, ChildEnvironment env, BakeRun run = null)
        {
            if (run != null)
            {
                var lines = new List<string>();
                Action<ParsedLine> h = l => { if (l.Kind != LineKind.Shell) lock (lines) lines.Add(l.Text); };
                run.Line += h;
                try
                {
                    var oc = run.RunProcess(new ProcSpec { Name = "map query", FileName = pyExe, Args = args, Cwd = cwd, Env = env, TimeoutMs = BakePlanner.MapQueryTimeoutMs });
                    if (oc.Cancelled) throw new OperationCanceledException("the run was cancelled");
                    List<string> copy;
                    lock (lines) copy = lines.ToList();
                    return Parse(copy, oc.ExitCode, oc.FailureReason);
                }
                finally { run.Line -= h; }
            }
            var r = ProcessRunner.RunCapture(pyExe, args, cwd, env, BakePlanner.MapQueryTimeoutMs);
            if (r.TimedOut) return new MapInfo { Error = "the map query timed out" };
            return Parse(r.StdoutLines, r.ExitCode, r.Stderr);
        }

        /// <summary>Every scenario under ROOT\tags as "scenarios\multi\x\x" (no extension), sorted; Sapien's "(auto-backup ...)"
        /// copies are left out. Reparse points are not followed.</summary>
        public static List<string> Scenarios(string root)
        {
            string tags = Path.Combine(root, "tags");
            var r = new List<string>();
            if (!Directory.Exists(tags)) return r;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(tags));
            int baseLen = tags.TrimEnd('\\').Length + 1;
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileSystemInfo[] items;
                try { items = d.GetFileSystemInfos(); } catch (Exception) { continue; }
                foreach (var fi in items)
                {
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (fi is DirectoryInfo sub)
                    {
                        // the tool's / Sapien's scratch folder (tags\temp_scenario\sapien_temp_scenario_0.scenario) is not a map
                        if (sub.Name.Equals(TagsFingerprint.ToolScratchDir, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(d.FullName.TrimEnd('\\'), tags.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(sub); continue;
                    }
                    if (!fi.Name.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) continue;
                    if (fi.Name.IndexOf("(auto-backup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string rel = fi.FullName.Substring(baseLen);
                    r.Add(rel.Substring(0, rel.Length - 9));
                }
            }
            r.Sort(StringComparer.OrdinalIgnoreCase);
            return r;
        }

        /// <summary>The BSPs a scenario can bake: gpu_bake reads &lt;scenario folder&gt;\&lt;bsp&gt;.scenario_structure_bsp, so these
        /// are the structure-bsps names (when known) that exist there, else the folder listing.</summary>
        public static List<string> Bsps(string root, string scenarioRel, IEnumerable<string> fromScenario = null)
        {
            string dir = Path.GetDirectoryName(Path.Combine(root, "tags", scenarioRel));
            var r = new List<string>();
            if (dir == null || !Directory.Exists(dir)) return r;
            if (fromScenario != null)
                foreach (var b in fromScenario)
                    if (File.Exists(Path.Combine(dir, b + ".scenario_structure_bsp")) && !r.Contains(b, StringComparer.OrdinalIgnoreCase)) r.Add(b);
            if (r.Count > 0) return r;
            foreach (var f in Directory.GetFiles(dir, "*.scenario_structure_bsp").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                r.Add(Path.GetFileNameWithoutExtension(f));
            return r;
        }

        /// <summary>
        /// 0.6.4 cross-folder BSP: the picker's list. Every BSP the scenario's structure_bsps block references, by its tag path wherever it lives.
        /// A BSP in the scenario's own folder is listed by its plain name (exactly as before); one in ANOTHER folder is labelled
        /// "01_bsp_1  (from scenarios\solo\01a_tutorial)" and carries its tag path. <paramref name="crossFolderWhy"/> is the engine gate
        /// (<see cref="H2GpuLightmapper.App.AppHost.CrossFolderBspWhy"/>): null = the engine resolves a BSP from the scenario, so other-folder BSPs are
        /// offered; otherwise the old behaviour is kept (only a BSP file beside the scenario is listed) and <paramref name="notes"/> says in plain words
        /// which BSPs the scenario references elsewhere and why they are not offered. With no usable reference the folder listing is the fallback.
        /// </summary>
        public static List<BspChoice> BspChoices(string root, string scenarioRel, IEnumerable<string> refs, string crossFolderWhy, out List<string> notes)
        {
            notes = new List<string>();
            var res = new List<BspChoice>();
            string scenDir = Path.GetDirectoryName(Path.Combine(root, "tags", scenarioRel));
            string scenFolder = Path.GetDirectoryName(scenarioRel) ?? "";
            var leafOrder = new List<string>();
            var byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (refs != null)
                foreach (var raw in refs)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string p = raw.Replace('/', '\\').Trim('\\');
                    string leaf = p.Substring(p.LastIndexOf('\\') + 1);
                    if (leaf.Length == 0) continue;
                    List<string> l;
                    if (!byLeaf.TryGetValue(leaf, out l)) { byLeaf[leaf] = l = new List<string>(); leafOrder.Add(leaf); }
                    if (!l.Contains(p, StringComparer.OrdinalIgnoreCase)) l.Add(p);
                }
            var elsewhere = new List<string>();
            foreach (var leaf in leafOrder)
            {
                var paths = byLeaf[leaf];
                string beside = scenDir == null ? null : Path.Combine(scenDir, leaf + ".scenario_structure_bsp");
                bool besideExists = beside != null && File.Exists(beside);
                if (crossFolderWhy != null)
                {
                    // the pre-0.6.4 rule: gpu_bake reads <scenario folder>\<bsp>.scenario_structure_bsp
                    if (besideExists) res.Add(BspChoice.Plain(leaf));
                    else elsewhere.Add(leaf + " (" + BakeOptions.BspFolder(paths[0]) + ")");
                    continue;
                }
                if (paths.Count > 1)
                {
                    notes.Add("The scenario has " + paths.Count + " structure_bsps entries named " + leaf + " (" + string.Join(", ", paths.Select(x => BakeOptions.BspFolder(x))) + "): the engine cannot tell them apart, so " + leaf + " is not offered.");
                    continue;
                }
                string rel = paths[0];
                string folder = BakeOptions.BspFolder(rel);
                string file = Path.Combine(root, "tags", rel + ".scenario_structure_bsp");
                if (string.Equals(folder, scenFolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(file)) res.Add(BspChoice.Plain(leaf));
                }
                else if (File.Exists(file)) res.Add(BspChoice.Other(leaf, rel));
                else if (besideExists) res.Add(BspChoice.Plain(leaf));       // the engine falls back to the file beside the scenario
                else notes.Add("The scenario references " + leaf + " in " + folder + ", but that tag does not exist.");
            }
            if (elsewhere.Count > 0)
                notes.Add("This scenario also references " + elsewhere.Count + " BSP(s) in other folders (" + string.Join(", ", elsewhere) + ") that cannot be baked here: " + crossFolderWhy);
            if (res.Count > 0 || scenDir == null || !Directory.Exists(scenDir)) return res;
            foreach (var n in Bsps(root, scenarioRel)) res.Add(BspChoice.Plain(n));
            return res;
        }
    }
}
