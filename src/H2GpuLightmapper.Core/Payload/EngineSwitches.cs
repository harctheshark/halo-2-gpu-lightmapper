// Which parameters does an engine's bake program take? Used by the shell to refuse (and grey out) an option whose parameter the
// engine it will run does not have: the bake program (like the script before it) silently IGNORES an unknown parameter, so the option would
// do nothing while the bake looked normal; it is never passed.
// 1.0: read from the engine folder's parameter list (bake_params.txt beside h2lm-bake.exe, written by the payload builder from
// `h2lm-bake.exe --list-params`); reads nothing but the payload zip.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace H2GpuLightmapper.Core.Payload
{
    public static class EngineSwitches
    {
        /// <summary>The bake program of an engine folder.</summary>
        public const string BakeExeName = "h2lm-bake.exe";
        /// <summary>The parameter list of an engine folder, beside its bake program.</summary>
        public const string ParamsFileName = "bake_params.txt";
        /// <summary>The "type" of a parameter-list line that names an environment variable the engine validates (not a parameter).</summary>
        public const string EnvType = "env";

        /// <summary>The parameter list that belongs to the bake program <paramref name="exeRel"/> (e.g. engine_v373/h2lm-bake.exe ->
        /// engine_v373/bake_params.txt; '/' separators).</summary>
        public static string ParamsFileOf(string exeRel)
        {
            if (string.IsNullOrEmpty(exeRel)) return null;
            int i = exeRel.LastIndexOf('/');
            return (i < 0 ? "" : exeRel.Substring(0, i + 1)) + ParamsFileName;
        }

        /// <summary>The parameter list as name -> type (switch, string, int, long, double; <see cref="EnvType"/> for an environment
        /// variable the engine validates). One "Name&lt;TAB&gt;type" per line; blank lines and lines starting with '#' are ignored; names are
        /// case-insensitive (the first entry of a name wins). Null text -> an empty list.</summary>
        public static Dictionary<string, string> ParseParams(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                string name = parts[0].TrimStart('-');
                if (name.Length == 0 || d.ContainsKey(name)) continue;
                d[name] = parts[1].ToLowerInvariant();
            }
            return d;
        }

        /// <summary>Does the parameter list declare the switch <paramref name="name"/>? (case-insensitive; fails closed: false for an empty list.)</summary>
        public static bool DeclaresSwitch(string paramsText, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string t;
            return ParseParams(paramsText).TryGetValue(name, out t) && t == "switch";
        }

        /// <summary>Does the parameter list name the environment variable <paramref name="name"/> as one the engine validates? This is the marker
        /// for an engine feature that is an environment variable, not a parameter: engine 0.6.4 and newer validate <c>GPULM_COMPAT</c> (0, 1 or
        /// unset, anything else fails the bake) and report fullgpu's "Compatibility mode" line; an engine whose list does not name it has no such
        /// mode and is never given it. Fails closed (false) for an empty list.</summary>
        public static bool ReadsEnvVar(string paramsText, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string t;
            return ParseParams(paramsText).TryGetValue(name, out t) && t == EnvType;
        }

        /// <summary>Does a PYTHON driver script carry the marker line <c>NAME = 1</c> (at column 0, optionally followed by a comment)? This is the marker for a driver
        /// feature that is neither a bake parameter nor an environment variable: gpu_ao.py / gpu_lightprobes.py of 0.6.5 declare <c>SPLIT_BSP_SUPPORT = 1</c> when they read
        /// the BSP from the file the scenario's structure_bsps names (another folder than the scenario). An older driver never mentions it, so it is never trusted with such a
        /// BSP. Fails closed (false) for an empty script.</summary>
        public static bool HasPyMarker(string script, string name)
        {
            if (string.IsNullOrEmpty(script) || string.IsNullOrEmpty(name)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(script, @"^" + System.Text.RegularExpressions.Regex.Escape(name) + @"[ \t]*=[ \t]*1(?![\w.])",
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        /// <summary>The text of one file of a payload component (read from its payload resource: a zip entry, or a file inside the
        /// component's pack; nothing is extracted; size and md5 checked against the manifest), or null when the file is absent.</summary>
        public static string ReadComponentText(IPayloadSource src, PayloadComponent c, string relPath)
        {
            if (src == null || c == null) return null;
            var f = c.Find(relPath);
            if (f == null) return null;
            var data = PayloadExtractor.ReadFileBytes(src, c, f);
            using (var r = new StreamReader(new MemoryStream(data), new UTF8Encoding(false), true))
                return r.ReadToEnd();
        }

        private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool Cached(IPayloadSource src, PayloadComponent c, string relPath, string kind, Func<string, bool> test)
        {
            var f = c == null ? null : c.Find(relPath);
            if (f == null) return false;
            string key = (c.ZipMd5 ?? c.Name) + "|" + f.Md5 + "|" + relPath + "|" + kind;
            lock (Cache)
            {
                bool v;
                if (Cache.TryGetValue(key, out v)) return v;
            }
            bool r;
            try { r = test(ReadComponentText(src, c, relPath)); }
            catch (Exception) { r = false; }
            lock (Cache) Cache[key] = r;
            return r;
        }

        /// <summary>Does the payload's INST parameter list <paramref name="paramsRel"/> (e.g. engine_v373/bake_params.txt) declare the switch
        /// <paramref name="name"/>? Cached per (zip md5, file md5, switch); the file's md5 is the manifest's, which the shell verifies against the
        /// extracted file before every bake.</summary>
        public static bool ComponentDeclares(IPayloadSource src, PayloadComponent c, string paramsRel, string name)
        {
            return Cached(src, c, paramsRel, "sw:" + name, t => DeclaresSwitch(t, name));
        }

        /// <summary>Does the payload's INST python file <paramref name="relPath"/> carry the marker line <c>name = 1</c> (see <see cref="HasPyMarker"/>)? Cached like
        /// <see cref="ComponentDeclares"/>.</summary>
        public static bool ComponentHasPyMarker(IPayloadSource src, PayloadComponent c, string relPath, string name)
        {
            return Cached(src, c, relPath, "py:" + name, t => HasPyMarker(t, name));
        }

        /// <summary>Does the payload's INST parameter list <paramref name="paramsRel"/> name the environment variable <paramref name="name"/>
        /// (see <see cref="ReadsEnvVar"/>)? Cached like <see cref="ComponentDeclares"/>.</summary>
        public static bool ComponentReadsEnv(IPayloadSource src, PayloadComponent c, string paramsRel, string name)
        {
            return Cached(src, c, paramsRel, "env:" + name, t => ReadsEnvVar(t, name));
        }
    }
}
