// Milestone 2 / 3 (direct .map baking): the pure parts -- the map header check (stock / compressed maps are refused in plain words; a Vista map is accepted since milestone 3), the read-only map query result
// (what the separate program h2conv reports about a map), and the sky cube-map dropdown model (listing, preselection, "Leave empty", the CLI --sky-cubemap resolution).
// Nothing here starts a process or touches the disk except reading a map's first bytes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Pipeline
{
    /// <summary>The container facts of a .map, read from its first 0x300 bytes only.</summary>
    public sealed class MapHeaderInfo
    {
        public const string StockRefusal = "This is a stock-style Halo 2 map: it is compressed and its bitmaps live in the shared textures.dat (and its shared tags in shared.map). "
            + "Baking a lightmap straight from such a map is not supported yet. Use a self-contained map built with the Halo 2 Editing Kit "
            + "(a map you compiled yourself, or a custom / workshop map), or bake the scenario from the editing kit tags.";

        public int Version;
        public int Flags, ExternalMask;
        public uint MetaAddressMask;
        public bool Compressed { get { return (Flags & 1) != 0; } }
        /// <summary>A Halo 2 Vista map (cache v8): read through its MCC form (h2conv's reverse converter), baked like an MCC map, optionally converted back to Vista.</summary>
        public bool IsVista { get { return Error == null && Version == 8; } }
        public bool SelfContained { get { return Version == 13 && !Compressed && ExternalMask == 0 && MetaAddressMask == 0; } }
        public string Error;

        public static MapHeaderInfo Read(string path)
        {
            var h = new MapHeaderInfo();
            byte[] b = new byte[0x2D4];
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n = 0, r;
                    while (n < b.Length && (r = fs.Read(b, n, b.Length - n)) > 0) n += r;
                    if (n < b.Length) { h.Error = path + " is too short to be a Halo 2 cache file"; return h; }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { h.Error = "cannot read " + path + ": " + e.Message; return h; }
            if (b[0] != (byte)'d' || b[1] != (byte)'a' || b[2] != (byte)'e' || b[3] != (byte)'h') { h.Error = path + " is not a Halo 2 cache file (no \"head\" signature)"; return h; }
            h.Version = BitConverter.ToInt32(b, 4);
            if (h.Version == 13)
            {
                h.Flags = BitConverter.ToUInt16(b, 0x1C);
                h.ExternalMask = BitConverter.ToUInt16(b, 0x1E);
                h.MetaAddressMask = BitConverter.ToUInt32(b, 0x2D0);
            }
            return h;
        }

        /// <summary>The plain-words refusal for a map that cannot be baked from, else null (a Vista map is accepted).</summary>
        public string Refusal()
        {
            if (Error != null) return Error;
            if (Version == 8) return null;
            if (Version != 13) return "unsupported Halo 2 cache version " + Version + " (an MCC map is version 13)";
            if (!SelfContained) return StockRefusal;
            return null;
        }
    }

    public enum CubemapKind { LeaveEmpty, Automatic, InMap, Kit }

    /// <summary>One entry of a sky's cube-map dropdown.</summary>
    public sealed class CubemapItem
    {
        public CubemapKind Kind;
        /// <summary>The bitmap tag path (backslashes, no extension); null for "Leave empty".</summary>
        public string Path;
        public string Label;
        /// <summary>The value given to <c>mapbake prepare --sky-cubemap SKY=VALUE</c>: the tag path, or "none".</summary>
        public string Value { get { return Kind == CubemapKind.LeaveEmpty ? "none" : Path; } }
        public override string ToString() { return Label; }
    }

    /// <summary>The dropdown of ONE sky that needs a cube-map choice.</summary>
    public sealed class SkyChoice
    {
        public string Sky, RenderModel;
        public List<CubemapItem> Items = new List<CubemapItem>();
        public CubemapItem Selected;
        /// <summary>Shown under the dropdown when the preselection is "Leave empty" because no automatic match exists; null otherwise.</summary>
        public string Warning;
        public CubemapItem AutoItem { get { return Items.FirstOrDefault(i => i.Kind == CubemapKind.Automatic); } }
        public CubemapItem EmptyItem { get { return Items.First(i => i.Kind == CubemapKind.LeaveEmpty); } }
        public string SkyLeaf { get { int i = Sky.LastIndexOf('\\'); return i < 0 ? Sky : Sky.Substring(i + 1); } }
    }

    public sealed class MapSkyInfo
    {
        public string Name, RenderModel;
        public bool NeedsCubemap;
        public double? Power;
        public string AutoCubemap, AutoRule, AutoNote;
        public bool AutoInMap, AutoInKit;
    }

    /// <summary>What <c>mapbake query</c> (h2conv.mapbake_query) says about a map.</summary>
    public sealed class MapQueryResult
    {
        public bool Ok;
        public string Error, Refusal, Kind, Scenario, InternalName, ScenarioType;
        public int TagCount;
        public List<string> Bsps = new List<string>(), MapCubemaps = new List<string>(), KitCubemaps = new List<string>();
        public List<MapSkyInfo> Skies = new List<MapSkyInfo>();
        /// <summary>Light probe points per element of the scenario's "Structure BSP Lighting" block (the cache keeps no bsp name for an element; a one-bsp map has one element).</summary>
        public List<int> ProbePoints = new List<int>();

        /// <summary>The probe point count of the map's only structure bsp (null when unknown: several bsps).</summary>
        public int? SingleBspProbePoints { get { return Bsps.Count == 1 ? (int?)ProbePoints.Sum() : null; } }
        public bool IsVista { get { return string.Equals(Kind, "vista", StringComparison.OrdinalIgnoreCase); } }

        public static MapQueryResult Parse(string json)
        {
            var r = new MapQueryResult();
            JObj j;
            try { j = Json.ParseObj(json); }
            catch (Exception e) when (e is FormatException || e is InvalidCastException) { r.Error = "the map query answered something unreadable: " + e.Message; return r; }
            if (j == null) { r.Error = "the map query answered nothing"; return r; }
            r.Ok = j.Bool("ok") == true;
            r.Error = j.Str("error");
            r.Refusal = j.Str("refusal");
            r.Kind = j.Str("kind");
            r.Scenario = j.Str("scenario");
            r.InternalName = j.Str("internal_name");
            r.ScenarioType = j.Str("scenario_type");
            r.TagCount = j.Int("tag_count") ?? 0;
            Action<string, List<string>> strs = (k, l) => { var a = j.Arr(k); if (a != null) foreach (var o in a) if (o is string s) l.Add(s); };
            var pp = j.Arr("probe_points"); if (pp != null) foreach (var o in pp) if (o != null) r.ProbePoints.Add(Convert.ToInt32(o, CultureInfo.InvariantCulture));
            strs("bsps", r.Bsps); strs("map_cubemaps", r.MapCubemaps); strs("kit_cubemaps", r.KitCubemaps);
            var skies = j.Arr("skies");
            if (skies != null)
                foreach (var o in skies.OfType<JObj>())
                {
                    var s = new MapSkyInfo { Name = o.Str("name"), RenderModel = o.Str("render_model"), NeedsCubemap = o.Bool("needs_cubemap") == true };
                    var els = o.Arr("cube_elements");
                    if (els != null)
                        foreach (var e in els.OfType<JObj>())
                        {
                            object p = e["power_scale"];
                            if (p != null) { double d = Convert.ToDouble(p, CultureInfo.InvariantCulture); s.Power = s.Power.HasValue ? Math.Max(s.Power.Value, d) : d; }
                        }
                    var au = o.Obj("auto");
                    if (au != null)
                    {
                        s.AutoCubemap = au.Str("cubemap"); s.AutoRule = au.Str("rule"); s.AutoNote = au.Str("note");
                        s.AutoInMap = au.Bool("in_map") == true; s.AutoInKit = au.Bool("in_kit") == true;
                    }
                    r.Skies.Add(s);
                }
            return r;
        }
    }

    /// <summary>The sky cube-map dropdown model: listing, preselection, "Leave empty", and the CLI resolution. Only ever built for a .map; a .scenario bake has no such thing.</summary>
    public static class CubemapModel
    {
        public const string LeaveEmptyLabel = "Leave empty (no cube map)";
        public const string NoAutoWarning = "No automatic match for this sky, so it is left empty: the bake will lack the sky's ambient fill (about 30% darker on headlong). "
            + "If you know the cube map this map was made with, pick it from the list.";

        public static string Norm(string tagPath)
        {
            if (tagPath == null) return null;
            string s = tagPath.Trim().Trim('"').Replace('/', '\\').Trim('\\');
            if (s.EndsWith(".bitmap", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 7);
            return s;
        }

        /// <summary>The skies that need a choice (a Cube Map element with power scale &gt; 0 and no reference), each with its dropdown. Empty for a map whose skies need none.</summary>
        public static List<SkyChoice> Build(MapQueryResult q)
        {
            var res = new List<SkyChoice>();
            if (q == null) return res;
            var kit = new HashSet<string>(q.KitCubemaps.Select(Norm), StringComparer.OrdinalIgnoreCase);
            var inMap = new HashSet<string>(q.MapCubemaps.Select(Norm), StringComparer.OrdinalIgnoreCase);
            foreach (var sky in q.Skies.Where(s => s.NeedsCubemap))
            {
                var c = new SkyChoice { Sky = sky.Name, RenderModel = sky.RenderModel };
                c.Items.Add(new CubemapItem { Kind = CubemapKind.LeaveEmpty, Label = LeaveEmptyLabel });
                string auto = Norm(sky.AutoCubemap);
                if (!string.IsNullOrEmpty(auto))
                    c.Items.Add(new CubemapItem
                    {
                        Kind = CubemapKind.Automatic, Path = auto,
                        Label = auto + "   (automatic match: the kit's sky with the same sky model uses it" + (inMap.Contains(auto) && !kit.Contains(auto) ? "; it is in this .map" : "") + ")",
                    });
                foreach (var k in kit.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(k, auto, StringComparison.OrdinalIgnoreCase)) continue;
                    c.Items.Add(new CubemapItem { Kind = CubemapKind.Kit, Path = KitCase(q, k), Label = KitCase(q, k) + (inMap.Contains(k) ? "   (kit; also in this .map)" : "") });
                }
                foreach (var m in inMap.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (kit.Contains(m) || string.Equals(m, auto, StringComparison.OrdinalIgnoreCase)) continue;
                    c.Items.Add(new CubemapItem { Kind = CubemapKind.InMap, Path = MapCase(q, m), Label = MapCase(q, m) + "   (in this .map)" });
                }
                c.Selected = c.AutoItem ?? c.EmptyItem;
                if (c.AutoItem == null) c.Warning = NoAutoWarning;
                res.Add(c);
            }
            return res;
        }

        private static string KitCase(MapQueryResult q, string k) { return q.KitCubemaps.Select(Norm).FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) ?? k; }
        private static string MapCase(MapQueryResult q, string k) { return q.MapCubemaps.Select(Norm).FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) ?? k; }

        /// <summary>The skies of the map that match a CLI/GUI sky name: equal to the tag name, or equal to its last part(s).</summary>
        public static List<SkyChoice> MatchSky(IEnumerable<SkyChoice> all, string key)
        {
            string k = Norm(key);
            if (string.IsNullOrEmpty(k)) return new List<SkyChoice>();
            return all.Where(s => string.Equals(s.Sky, k, StringComparison.OrdinalIgnoreCase) || s.Sky.EndsWith("\\" + k, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Applies the requested <c>--sky-cubemap SKY=BITMAP|none|auto</c> choices to the model (the rest keep their preselection). Errors are plain sentences and
        /// refuse the bake before anything is written.</summary>
        public static List<string> Apply(List<SkyChoice> model, IEnumerable<KeyValuePair<string, string>> requested)
        {
            var errs = new List<string>();
            foreach (var kv in requested)
            {
                var hit = MatchSky(model, kv.Key);
                if (hit.Count == 0)
                {
                    errs.Add("--sky-cubemap: this map has no sky '" + kv.Key + "' that needs a cube map" + (model.Count > 0 ? " (it has: " + string.Join(", ", model.Select(m => m.Sky)) + ")" : " (none of its skies needs a choice)"));
                    continue;
                }
                if (hit.Count > 1) { errs.Add("--sky-cubemap: '" + kv.Key + "' matches " + hit.Count + " skies (" + string.Join(", ", hit.Select(m => m.Sky)) + "): give more of the tag name"); continue; }
                var c = hit[0];
                string v = (kv.Value ?? "").Trim();
                if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) { c.Selected = c.EmptyItem; c.Warning = null; continue; }
                if (v.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    if (c.AutoItem == null) errs.Add("--sky-cubemap: the sky '" + c.Sky + "' has no automatic cube map match (use a bitmap tag path or none)");
                    else { c.Selected = c.AutoItem; c.Warning = null; }
                    continue;
                }
                string n = Norm(v);
                var item = c.Items.FirstOrDefault(i => i.Path != null && string.Equals(i.Path, n, StringComparison.OrdinalIgnoreCase));
                if (item == null) { errs.Add("--sky-cubemap: '" + v + "' is not a cube-map bitmap of the editing kit tags or of this map (bitmap type = cube map)"); continue; }
                c.Selected = item; c.Warning = null;
            }
            return errs;
        }

        /// <summary>One line per sky for the plan / log: "sky X -> cube map Y" or "-> left empty (warning)".</summary>
        public static List<string> Describe(IEnumerable<SkyChoice> model)
        {
            var l = new List<string>();
            foreach (var c in model)
            {
                if (c.Selected.Kind == CubemapKind.LeaveEmpty)
                    l.Add("sky cube map: " + c.Sky + " -> LEFT EMPTY" + (c.Warning != null ? "  -- WARNING: " + c.Warning : " (your choice)"));
                else
                    l.Add("sky cube map: " + c.Sky + " -> " + c.Selected.Path + (c.Selected.Kind == CubemapKind.Automatic ? " (automatic match)" : c.Selected.Kind == CubemapKind.InMap ? " (from the map)" : " (your choice)"));
            }
            return l;
        }
    }
}
