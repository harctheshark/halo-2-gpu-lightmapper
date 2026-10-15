using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace H2GpuLightmapper.Core.Discovery
{
    /// <summary>A Valve KeyValues ("VDF") node: ordered children, keys case-insensitive (Steam's own reader is).</summary>
    public sealed class VdfNode
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

        public object this[string key]
        {
            get
            {
                foreach (var kv in Items) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                return null;
            }
        }

        public string Str(string key) { return this[key] as string; }
        public VdfNode Node(string key) { return this[key] as VdfNode; }

        public IEnumerable<KeyValuePair<string, VdfNode>> Nodes()
        {
            foreach (var kv in Items) if (kv.Value is VdfNode) yield return new KeyValuePair<string, VdfNode>(kv.Key, (VdfNode)kv.Value);
        }
    }

    /// <summary>
    /// Small reader for Steam's text KeyValues files (libraryfolders.vdf, appmanifest_*.acf): quoted or bare tokens,
    /// escapes \\ \" \n \t, nested { } blocks, // comments, and [$CONDITION] suffixes (ignored). Read-only; never writes.
    /// </summary>
    public static class Vdf
    {
        public static VdfNode ParseFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true))
                return Parse(sr.ReadToEnd());
        }

        /// <summary>Parses a document into a root node whose items are the top-level key/value pairs.</summary>
        public static VdfNode Parse(string text)
        {
            int i = 0;
            var root = new VdfNode();
            ParseBlock(text, ref i, root, true);
            return root;
        }

        private static void ParseBlock(string s, ref int i, VdfNode node, bool top)
        {
            while (true)
            {
                bool kq, vq;
                string key = Token(s, ref i, out kq);
                if (key == null)
                {
                    if (top) return;
                    throw new FormatException("VDF: unexpected end of file inside a block");
                }
                if (key == "}" && !kq)
                {
                    if (top) throw new FormatException("VDF: unbalanced '}'");
                    return;
                }
                if (key == "{" && !kq) throw new FormatException("VDF: '{' without a key at offset " + i);
                string val = Token(s, ref i, out vq);
                if (val == null) throw new FormatException("VDF: key '" + key + "' has no value");
                if (val == "{" && !vq)
                {
                    var child = new VdfNode();
                    ParseBlock(s, ref i, child, false);
                    node.Items.Add(new KeyValuePair<string, object>(key, child));
                }
                else
                {
                    node.Items.Add(new KeyValuePair<string, object>(key, val));
                }
                SkipCondition(s, ref i);
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
        }

        private static void SkipCondition(string s, ref int i)
        {
            int j = i;
            while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
            if (j < s.Length && s[j] == '[')
            {
                int e = s.IndexOf(']', j);
                if (e > 0) i = e + 1;
            }
        }

        private static string Token(string s, ref int i, out bool quoted)
        {
            SkipWs(s, ref i);
            quoted = false;
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{' || c == '}') { i++; return c.ToString(); }
            var sb = new StringBuilder();
            if (c == '"')
            {
                quoted = true;
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char e = s[i + 1];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append('\\').Append(e); break;
                        }
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new FormatException("VDF: unterminated string");
                i++;
                return sb.ToString();
            }
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') sb.Append(s[i++]);
            return sb.ToString();
        }
    }
}
