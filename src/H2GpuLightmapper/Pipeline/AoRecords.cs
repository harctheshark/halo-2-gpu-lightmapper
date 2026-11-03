// 0.6.8 xc8 -- AO install records (the double-AO protection of "AO only").
//
// WORK\standalone\ao_installed.json: one entry per lightmap set this program installed WITH ambient occlusion (a full bake with AO, or AO only):
// the scenario / BSP, the md5 of the three files that were installed (lightmap tag, truecolor bitmap, P8 bitmap), the options and the run.
// AO only refuses (and ao_existing.py refuses too) when the set that is installed NOW has exactly the md5s of such an entry: the AO is in it already, and a
// second pass would darken the same places twice. Any change of the files (a new bake, a restore of the backup, an edit in Guerilla) changes the md5s, so the
// record stops matching -- restoring the lightmap from before the AO therefore lifts the protection by itself. --force / the window's question overrides it.
//
// What it cannot know: AO that was baked into a lightmap by another program or copied in from elsewhere (no record). Those sets are allowed; the window and
// the command line say so before AO only starts.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class AoRecord
    {
        public string Scenario, Bsp, Mode, RunTag, Utc;
        public double Strength, Exponent;
        public SortedDictionary<string, string> Files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public JObj ToJObj()
        {
            var f = new JObj();
            foreach (var kv in Files) f.Add(kv.Key, kv.Value);
            return new JObj().Add("scenario", Scenario).Add("bsp", Bsp).Add("mode", Mode).Add("strength", Strength).Add("exponent", Exponent).Add("runTag", RunTag).Add("utc", Utc).Add("files", f);
        }

        public static AoRecord From(JObj j)
        {
            var r = new AoRecord { Scenario = j.Str("scenario"), Bsp = j.Str("bsp"), Mode = j.Str("mode"), RunTag = j.Str("runTag"), Utc = j.Str("utc") };
            try { r.Strength = Convert.ToDouble(j["strength"], CultureInfo.InvariantCulture); r.Exponent = Convert.ToDouble(j["exponent"], CultureInfo.InvariantCulture); } catch (Exception) { }
            var f = j.Obj("files");
            if (f != null) foreach (var kv in f) r.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return r;
        }

        /// <summary>"AO only, strength 1, exponent 0.5, 2026-11-01 20:41" -- the words the refusal uses.</summary>
        public string Describe()
        {
            string when = Utc;
            DateTime d;
            if (DateTime.TryParse(Utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) when = d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return "AO " + (Mode == "ao-only" ? "only" : "with a full bake") + ", strength " + BakePlanner.Num(Strength) + ", exponent " + BakePlanner.Num(Exponent) + (when != null ? ", " + when : "");
        }
    }

    public static class AoRecords
    {
        public const int Max = 200;
        public static string PathFor(string work) { return Path.Combine(ShellPaths.StandaloneOf(work), "ao_installed.json"); }

        public static List<AoRecord> Load(string work)
        {
            try
            {
                var j = Json.ReadObjFile(PathFor(work));
                if (j == null || j.Arr("sets") == null) return new List<AoRecord>();
                return j.Arr("sets").OfType<JObj>().Select(AoRecord.From).ToList();
            }
            catch (Exception) { return new List<AoRecord>(); }
        }

        /// <summary>The three files of the set that is installed now, by name -> md5 (null when one is missing).</summary>
        public static SortedDictionary<string, string> SetMd5s(TagSet set)
        {
            var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in new[] { set.LtmpName, set.Prefix + "_truecolor_bitmaps.bitmap", set.Prefix + "_bitmaps.bitmap" })
            {
                string p = Path.Combine(set.TagDir, n);
                if (!File.Exists(p)) return null;
                d[n] = SafeFs.Md5File(p);
            }
            return d;
        }

        /// <summary>The record whose md5s are exactly the installed set's (the newest), or null.</summary>
        public static AoRecord Find(List<AoRecord> all, IDictionary<string, string> md5s)
        {
            if (md5s == null) return null;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var f = all[i].Files;
                if (f.Count == md5s.Count && f.All(kv => md5s.ContainsKey(kv.Key) && string.Equals(md5s[kv.Key], kv.Value, StringComparison.OrdinalIgnoreCase))) return all[i];
            }
            return null;
        }

        public static void Append(string work, AoRecord r)
        {
            var all = Load(work);
            all.Add(r);
            if (all.Count > Max) all = all.Skip(all.Count - Max).ToList();
            Directory.CreateDirectory(ShellPaths.StandaloneOf(work));
            SafeFs.WriteAllTextAtomic(PathFor(work), Json.Write(new JObj().Add("schema", 1).Add("sets", all.Select(x => (object)x.ToJObj()).ToList()), true));
        }

        /// <summary>The refusal sentence of AO only (shown by the window, the command line and the log) when the installed set already carries AO.</summary>
        public static string AlreadyText(AoRecord r)
        {
            return "Ambient occlusion has already been added to the lightmap that is installed now (" + r.Describe() + "). Adding it again would darken the same places twice. "
                 + "To add AO to the lightmap as it was before, restore the backup made before that run (Restore previous lightmap) or bake the lightmap again; "
                 + "to darken it a second time anyway, run AO only again with --force (the window asks).";
        }
    }
}
