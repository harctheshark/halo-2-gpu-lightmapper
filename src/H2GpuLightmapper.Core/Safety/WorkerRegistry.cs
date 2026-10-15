// S2.4 worker allocation (FINAL_DESIGN section 5): WORK\standalone\workers.json maps "scenario|bsp" (lightmap worker) and
// "scenario|bsp|probes" (probe worker) to a farm number in 200-299, so a capture is never mixed across maps. A new key gets
// the lowest number that is free in the registry AND has no farm_wNNN / _gpulm_skel\wNNN folder on disk (a research or
// legacy farm is never reused). farm_w149 is denylisted in code. The file is a flat JSON object (like the launcher's
// workers.json) and is updated under a named mutex + atomic replace.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using Json = H2GpuLightmapper.Core.Util.Json;

namespace H2GpuLightmapper.Core.Safety
{
    public sealed class WorkerRegistry
    {
        public string Work { get; private set; }
        public string FilePath { get { return Path.Combine(ShellPaths.StandaloneOf(Work), "workers.json"); } }
        public int First { get; private set; }
        public int Last { get; private set; }

        public WorkerRegistry(string work, int first = Guards.FirstWorker, int last = Guards.LastWorker)
        {
            if (first < Guards.FirstWorker || last > Guards.LastWorker || first > last) throw new ArgumentException("the worker range must lie within " + Guards.FirstWorker + "-" + Guards.LastWorker);
            Work = work; First = first; Last = last;
        }

        /// <summary>"scenarios\multi\cyclotron\cyclotron|cyclotron" (lower-case, backslashes, no extension) [+ "|probes"].</summary>
        public static string Key(string scenarioRel, string bsp, bool probes = false)
        {
            if (string.IsNullOrWhiteSpace(scenarioRel) || string.IsNullOrWhiteSpace(bsp)) throw new ArgumentException("scenario and bsp are required");
            string s = scenarioRel.Trim().Replace('/', '\\').Trim('\\');
            if (s.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
            string k = (s + "|" + bsp.Trim()).ToLowerInvariant();
            if (k.Contains("|probes")) throw new ArgumentException("bsp name collides with the probe key suffix");
            return probes ? k + "|probes" : k;
        }

        public Dictionary<string, int> ReadAll()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var j = Json.ReadObjFile(FilePath);
            if (j == null) return d;
            foreach (var kv in j)
            {
                if (!(kv.Value is long)) throw new InvalidDataException(FilePath + ": '" + kv.Key + "' is not a worker number");
                int w = (int)(long)kv.Value;
                Guards.AssertWorker(w);          // a hand-edited registry can never hand out 149 or a research number
                d[kv.Key] = w;
            }
            return d;
        }

        /// <summary>The worker already assigned to this key, or null.</summary>
        public int? Find(string scenarioRel, string bsp, bool probes = false)
        {
            int w;
            return ReadAll().TryGetValue(Key(scenarioRel, bsp, probes), out w) ? (int?)w : null;
        }

        /// <summary>The key's worker, allocating (and persisting) the lowest free one in range when it has none.</summary>
        public int GetOrAllocate(string scenarioRel, string bsp, bool probes = false)
        {
            string key = Key(scenarioRel, bsp, probes);
            using (var m = new Mutex(false, @"Local\H2GpuLightmapper.workers." + SlotAliasPlanner.Key6(Work)))
            {
                bool owned = false;
                try
                {
                    try { owned = m.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("another instance holds the worker registry");
                    var all = ReadAll();
                    int w;
                    if (all.TryGetValue(key, out w)) return w;
                    var used = new HashSet<int>(all.Values);
                    for (int n = First; n <= Last; n++)
                    {
                        if (!Guards.WorkerAllowed(n) || used.Contains(n)) continue;
                        if (FarmExists(n)) continue;
                        all[key] = n;
                        Save(all);
                        return n;
                    }
                    throw new SafetyException("all standalone workers " + First + "-" + Last + " are taken (registry or existing farm folders)");
                }
                finally { if (owned) m.ReleaseMutex(); }
            }
        }

        public string FarmDir(int w) { Guards.AssertWorker(w); return Path.Combine(Work, "farm_w" + w.ToString("D2", CultureInfo.InvariantCulture)); }

        private bool FarmExists(int n)
        {
            string f = Path.Combine(Work, "farm_w" + n.ToString("D2", CultureInfo.InvariantCulture));
            string s = Path.Combine(Work, "_gpulm_skel", "w" + n.ToString("D2", CultureInfo.InvariantCulture));
            return Directory.Exists(f) || File.Exists(f) || Directory.Exists(s);
        }

        private void Save(Dictionary<string, int> all)
        {
            var j = new JObj();
            foreach (var kv in all.OrderBy(x => x.Value)) j[kv.Key] = (long)kv.Value;
            SafeFs.WriteAllTextAtomic(FilePath, Json.Write(j, true));
        }
    }
}
