using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Discovery
{
    /// <summary>
    /// %LOCALAPPDATA%\H2GpuLightmapper\settings.json: what is remembered across launches (FINAL_DESIGN section 4: root and
    /// map; the bake options are NOT remembered, they reset every launch). Unknown keys written by a newer shell are kept.
    /// </summary>
    public sealed class AppSettings
    {
        public const int MaxRoots = 12;
        private JObj _doc = new JObj();
        public string Path { get; private set; }

        public List<string> Roots = new List<string>();
        public string LastRoot, LastScenario, LastBsp;

        public static AppSettings Load(string path)
        {
            var s = new AppSettings { Path = path };
            try
            {
                var j = Json.ReadObjFile(path);
                if (j == null) return s;
                s._doc = j;
                var arr = j.Arr("roots");
                if (arr != null) s.Roots = arr.OfType<string>().ToList();
                s.LastRoot = j.Str("lastRoot");
                s.LastScenario = j.Str("lastScenario");
                s.LastBsp = j.Str("lastBsp");
            }
            catch (Exception) { }   // a damaged settings file is replaced on the next save, never fatal
            return s;
        }

        /// <summary>Moves <paramref name="root"/> to the front of the remembered list (case-insensitive dedupe, capped).</summary>
        public void Remember(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            string n = H2ekDiscovery.NormalizeRoot(root);
            Roots.RemoveAll(r => string.Equals(r, n, StringComparison.OrdinalIgnoreCase));
            Roots.Insert(0, n);
            if (Roots.Count > MaxRoots) Roots.RemoveRange(MaxRoots, Roots.Count - MaxRoots);
            LastRoot = n;
        }

        public void Save()
        {
            _doc["schema"] = 1L;
            _doc["roots"] = Roots.Cast<object>().ToList();
            _doc["lastRoot"] = LastRoot;
            _doc["lastScenario"] = LastScenario;
            _doc["lastBsp"] = LastBsp;
            SafeFs.WriteAllTextAtomic(Path, Json.Write(_doc, true));
        }

        /// <summary>Other keys (S2.5 UI state, history...) stored alongside, preserved across saves.</summary>
        public object this[string key] { get { return _doc[key]; } set { _doc[key] = value; } }
    }
}
