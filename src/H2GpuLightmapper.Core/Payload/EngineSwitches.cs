// Which switches does an engine script declare? Used by the shell to refuse (and grey out) an option whose gpu_bake.ps1 parameter the
// engine it will run does not have: passing an unknown parameter to PowerShell fails the whole bake, so the option is never passed.
// Pure text analysis of the script's FIRST param(...) block (comments and string contents ignored); reads nothing but the payload zip.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace H2GpuLightmapper.Core.Payload
{
    public static class EngineSwitches
    {
        /// <summary>The code text of the script's first top-level param(...) block with comments removed and string contents blanked
        /// (null when the script has none).</summary>
        public static string ParamBlock(string script)
        {
            string t = CodeText(script);
            if (t == null) return null;
            return ParamBlockOfCode(t);
        }

        /// <summary>The script with comments removed and string contents blanked (same length per line; null for an empty script).</summary>
        public static string CodeText(string script)
        {
            if (string.IsNullOrEmpty(script)) return null;
            var code = new StringBuilder(script.Length);
            int n = script.Length;
            // pass 1: strip comments, blank string contents (a "param(" or "[switch]$X" inside a comment / string never counts)
            for (int i = 0; i < n; i++)
            {
                char c = script[i];
                if (c == '<' && i + 1 < n && script[i + 1] == '#')
                {
                    int e = script.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    if (e < 0) break;
                    for (int k = i; k < e + 2; k++) code.Append(script[k] == '\n' ? '\n' : ' ');
                    i = e + 1;
                }
                else if (c == '#')
                {
                    while (i < n && script[i] != '\n') i++;
                    if (i < n) code.Append('\n');
                }
                else if (c == '\'' || c == '"')
                {
                    code.Append(c);
                    int j = i + 1;
                    for (; j < n; j++)
                    {
                        char d = script[j];
                        if (c == '"' && d == '`') { code.Append("  "); j++; continue; }       // backtick escape
                        if (d == c)
                        {
                            if (j + 1 < n && script[j + 1] == c) { code.Append("  "); j++; continue; }   // doubled quote
                            break;
                        }
                        code.Append(d == '\n' ? '\n' : ' ');
                    }
                    if (j < n) code.Append(c);
                    i = j;
                }
                else code.Append(c);
            }
            return code.ToString();
        }

        private static string ParamBlockOfCode(string t)
        {
            var m = Regex.Match(t, @"(?<![\w$\-.])param\s*\(", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            int start = m.Index + m.Length, depth = 1, p = start;
            for (; p < t.Length && depth > 0; p++)
            {
                if (t[p] == '(') depth++;
                else if (t[p] == ')') depth--;
            }
            if (depth != 0) return null;
            return t.Substring(start, p - 1 - start);
        }

        /// <summary>Does the script's param block declare <c>[switch]$name</c>? (case-insensitive; fails closed: false when there is no param block.)</summary>
        public static bool DeclaresSwitch(string script, string name)
        {
            string pb = ParamBlock(script);
            if (pb == null || string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(pb, @"\[\s*(?:System\.Management\.Automation\.)?Switch(?:Parameter)?\s*\]\s*\$" + Regex.Escape(name) + @"(?![\w])", RegexOptions.IgnoreCase);
        }

        /// <summary>Does the script's CODE (comments removed, string contents blanked) read the environment variable <paramref name="name"/>
        /// (<c>$env:NAME</c> / <c>Env:NAME</c>)? This is the marker for an engine feature that is an environment variable, not a parameter:
        /// gpu_bake.ps1 of engine 0.6.4 validates <c>GPULM_COMPAT</c> (0, 1 or unset, anything else throws) and copies fullgpu's
        /// "Compatibility mode" line into the phase summary; an engine that never mentions the variable in code has no such mode and
        /// is never given it. A mention only in a comment or a string does not count. Fails closed (false) for an empty script.</summary>
        public static bool ReadsEnvVar(string script, string name)
        {
            string t = CodeText(script);
            if (t == null || string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(t, @"(?<![\w])\$?env:" + Regex.Escape(name) + @"(?![\w])", RegexOptions.IgnoreCase);
        }

        /// <summary>The text of one file of a payload component (read from its zip; nothing is extracted), or null when the zip / file is absent.</summary>
        public static string ReadComponentText(IPayloadSource src, PayloadComponent c, string relPath)
        {
            if (src == null || c == null || !c.Has(relPath)) return null;
            using (var rs = src.OpenResource(c.Resource))
            {
                if (rs == null) return null;
                using (var zip = new ZipArchive(rs, ZipArchiveMode.Read, false))
                {
                    var e = zip.GetEntry(relPath);
                    if (e == null) return null;
                    using (var s = e.Open())
                    using (var r = new StreamReader(s, new UTF8Encoding(false), true))
                        return r.ReadToEnd();
                }
            }
        }

        private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Does the payload's INST file <paramref name="relPath"/> (e.g. engine_v373/gpu_bake.ps1) declare <c>[switch]$name</c>?
        /// Cached per (zip md5, file md5, switch); the file's md5 is the manifest's, which the shell verifies against the extracted file before every bake.</summary>
        public static bool ComponentDeclares(IPayloadSource src, PayloadComponent c, string relPath, string name)
        {
            var f = c == null ? null : c.Find(relPath);
            if (f == null) return false;
            string key = (c.ZipMd5 ?? c.Name) + "|" + f.Md5 + "|" + relPath + "|" + name;
            lock (Cache)
            {
                bool v;
                if (Cache.TryGetValue(key, out v)) return v;
            }
            bool r;
            try { r = DeclaresSwitch(ReadComponentText(src, c, relPath), name); }
            catch (Exception) { r = false; }
            lock (Cache) Cache[key] = r;
            return r;
        }

        /// <summary>Does the payload's INST file <paramref name="relPath"/> read the environment variable <paramref name="name"/> in its code
        /// (see <see cref="ReadsEnvVar"/>)? Cached like <see cref="ComponentDeclares"/>.</summary>
        public static bool ComponentReadsEnv(IPayloadSource src, PayloadComponent c, string relPath, string name)
        {
            var f = c == null ? null : c.Find(relPath);
            if (f == null) return false;
            string key = (c.ZipMd5 ?? c.Name) + "|" + f.Md5 + "|" + relPath + "|env:" + name;
            lock (Cache)
            {
                bool v;
                if (Cache.TryGetValue(key, out v)) return v;
            }
            bool r;
            try { r = ReadsEnvVar(ReadComponentText(src, c, relPath), name); }
            catch (Exception) { r = false; }
            lock (Cache) Cache[key] = r;
            return r;
        }
    }
}
