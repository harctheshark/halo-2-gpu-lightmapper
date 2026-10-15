// WORK\standalone\history.json (FINAL_DESIGN section 2): one entry per finished run (map, options, state, per-step seconds),
// capped. It feeds the info line ("last bake ...") and the Custom ETA, scaled from this map's last Super run:
//   capture ~ photons (P / 20M), the GPU bake ~ rays per gather call (2 MC^2 / 128), everything else unchanged.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class HistoryEntry
    {
        public string RunTag, Scenario, Bsp, Quality, Mode, State;
        public long? Photons;
        public int? Mc;
        public bool Ao, Reuse, Install;
        public DateTime StartedUtc;
        public double Seconds;
        public Dictionary<string, double> Steps = new Dictionary<string, double>(StringComparer.Ordinal);

        public JObj ToJObj()
        {
            var st = new JObj();
            foreach (var kv in Steps) st.Add(kv.Key, Math.Round(kv.Value, 1));
            return new JObj().Add("runTag", RunTag).Add("scenario", Scenario).Add("bsp", Bsp).Add("quality", Quality).Add("photons", Photons).Add("mcSamples", Mc)
                .Add("mode", Mode).Add("ao", Ao).Add("reuse", Reuse).Add("install", Install).Add("state", State).Add("startedUtc", StartedUtc)
                .Add("seconds", Math.Round(Seconds, 1)).Add("steps", st);
        }

        public static HistoryEntry From(JObj j)
        {
            var e = new HistoryEntry
            {
                RunTag = j.Str("runTag"), Scenario = j.Str("scenario"), Bsp = j.Str("bsp"), Quality = j.Str("quality"), Mode = j.Str("mode"), State = j.Str("state"),
                Photons = j.Long("photons"), Mc = j.Int("mcSamples"), Ao = j.Bool("ao") == true, Reuse = j.Bool("reuse") == true, Install = j.Bool("install") == true,
            };
            DateTime d;
            if (DateTime.TryParse(j.Str("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) e.StartedUtc = d;
            e.Seconds = ToD(j["seconds"]);
            var st = j.Obj("steps");
            if (st != null) foreach (var kv in st) e.Steps[kv.Key] = ToD(kv.Value);
            return e;
        }

        private static double ToD(object o) { try { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; } }
    }

    public static class History
    {
        public const int Max = 300;
        public static string PathFor(string work) { return Path.Combine(ShellPaths.StandaloneOf(work), "history.json"); }

        public static List<HistoryEntry> Load(string work)
        {
            try
            {
                var j = Json.ReadObjFile(PathFor(work));
                if (j == null || j.Arr("runs") == null) return new List<HistoryEntry>();
                return j.Arr("runs").OfType<JObj>().Select(HistoryEntry.From).ToList();
            }
            catch (Exception) { return new List<HistoryEntry>(); }
        }

        public static void Append(string work, HistoryEntry e)
        {
            var all = Load(work);
            all.Add(e);
            if (all.Count > Max) all = all.Skip(all.Count - Max).ToList();
            Directory.CreateDirectory(ShellPaths.StandaloneOf(work));
            SafeFs.WriteAllTextAtomic(PathFor(work), Json.Write(new JObj().Add("schema", 1).Add("runs", all.Select(x => (object)x.ToJObj()).ToList()), true));
        }

        private static bool Same(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }

        public static HistoryEntry LastDone(List<HistoryEntry> h, string scenario, string bsp, string quality = null)
        {
            return h.Where(e => Same(e.Scenario, scenario) && Same(e.Bsp, bsp) && e.State == "DONE" && (quality == null || Same(e.Quality, quality)))
                    .OrderByDescending(e => e.StartedUtc).FirstOrDefault();
        }

        public static HistoryEntry Last(List<HistoryEntry> h, string scenario, string bsp)
        {
            return h.Where(e => Same(e.Scenario, scenario) && Same(e.Bsp, bsp)).OrderByDescending(e => e.StartedUtc).FirstOrDefault();
        }

        /// <summary>The Custom ETA in seconds from this map's last Super run with a fresh capture (null: no such run yet).</summary>
        public static double? CustomEta(List<HistoryEntry> h, string scenario, string bsp, long photons, int mc, out string basis)
        {
            basis = null;
            var s = h.Where(e => Same(e.Scenario, scenario) && Same(e.Bsp, bsp) && e.State == "DONE" && Same(e.Quality, "super") && !e.Reuse)
                     .OrderByDescending(e => e.StartedUtc).FirstOrDefault();
            if (s == null) return null;
            double cap = Get(s, StepId.Capture), bake = Get(s, StepId.GpuBake);
            double rest = Math.Max(0, s.Seconds - cap - bake);
            double eta = cap * (photons / (double)BakeOptions.SuperPhotons) + bake * (BakeOptions.RaysPerCall(mc) / (double)BakeOptions.RaysPerCall(BakeOptions.SuperMc)) + rest;
            basis = string.Format(CultureInfo.InvariantCulture, "scaled from the Super run of {0:yyyy-MM-dd HH:mm} ({1})", s.StartedUtc.ToLocalTime(), Dur(s.Seconds));
            return eta;
        }

        /// <summary>The ETA of a Low / Super run: this map's last finished run of that quality.</summary>
        public static double? PlainEta(List<HistoryEntry> h, string scenario, string bsp, string quality)
        {
            var s = LastDone(h, scenario, bsp, quality);
            return s == null ? (double?)null : s.Seconds;
        }

        private static double Get(HistoryEntry e, StepId s) { double v; return e.Steps.TryGetValue(s.ToString(), out v) ? v : 0; }

        public static string Dur(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalHours >= 1) return string.Format(CultureInfo.InvariantCulture, "{0} h {1:00} min", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format(CultureInfo.InvariantCulture, "{0} min {1:00} s", (int)t.TotalMinutes, t.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} s", t.TotalSeconds);
        }
    }

    /// <summary>Worker numbers for a plan. The real run allocates through WorkerRegistry (persisted); --dry-run only PEEKS:
    /// the registered number, or the one the registry would hand out next (nothing written).</summary>
    public static class WorkerPeek
    {
        public static int? Registered(string work, string scenario, string bsp, bool probes)
        {
            try { return new Core.Safety.WorkerRegistry(work).Find(scenario, bsp, probes); } catch (Exception) { return null; }
        }

        public static int Peek(string work, string scenario, string bsp, bool probes, IEnumerable<int> alsoTaken, out string note)
        {
            var reg = new Core.Safety.WorkerRegistry(work);
            var all = reg.ReadAll();
            int w;
            if (all.TryGetValue(Core.Safety.WorkerRegistry.Key(scenario, bsp, probes), out w)) { note = "registered"; return w; }
            var used = new HashSet<int>(all.Values);
            if (alsoTaken != null) foreach (var t in alsoTaken) used.Add(t);
            for (int n = reg.First; n <= reg.Last; n++)
            {
                if (!Core.Safety.Guards.WorkerAllowed(n) || used.Contains(n)) continue;
                string f = Path.Combine(work, "farm_w" + n.ToString("D2", CultureInfo.InvariantCulture));
                string s = Path.Combine(work, "_gpulm_skel", "w" + n.ToString("D2", CultureInfo.InvariantCulture));
                if (Directory.Exists(f) || File.Exists(f) || Directory.Exists(s)) continue;
                note = "would be allocated";
                return n;
            }
            throw new Core.Safety.SafetyException("all standalone workers " + reg.First + "-" + reg.Last + " are taken");
        }
    }
}
