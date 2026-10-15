// The map pieces of the UI / CLI (FINAL_DESIGN section 4.2): the scenarios under ROOT\tags, a scenario's BSPs, and a read-only
// query of the probe-point count, the lightmap's lighting-environment count and the scenario's structure-bsps list.
// The query is a short python program run by the BUNDLE py over the INST tag walker (v043-tagwriter\lmw_tagio, the same one
// lp_probes.py / sh_stage.py use); its probe rule is lp_probes.py's, verbatim: the `structure bsp lighting` element whose
// `bsp` reference's last path component equals the BSP name; one probe per `lighting points` entry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class MapInfo
    {
        public List<string> Bsps = new List<string>();
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
            "out = {'bsps': [], 'points': None, 'pointsError': None, 'ltmpExists': os.path.exists(ltmp), 'envs': None, 'groups': None, 'envsError': None}",
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
                    if (fi is DirectoryInfo sub) { stack.Push(sub); continue; }
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
    }
}
