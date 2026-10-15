using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace H2GpuLightmapper.Core.Util
{
    /// <summary>An insertion-ordered JSON object (keys are case-sensitive, as in JSON).</summary>
    public sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, object> _map = new Dictionary<string, object>(StringComparer.Ordinal);

        public int Count { get { return _keys.Count; } }
        public IEnumerable<string> Keys { get { return _keys; } }

        public object this[string key]
        {
            get { object v; return _map.TryGetValue(key, out v) ? v : null; }
            set { if (!_map.ContainsKey(key)) _keys.Add(key); _map[key] = value; }
        }

        public JObj Add(string key, object value) { this[key] = value; return this; }
        public bool Has(string key) { return _map.ContainsKey(key); }
        public bool Remove(string key) { if (!_map.Remove(key)) return false; _keys.Remove(key); return true; }

        public string Str(string key) { var v = this[key]; return v == null ? null : v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture); }
        public long? Long(string key)
        {
            var v = this[key];
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is string) { long r; if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r; }
            return null;
        }
        public int? Int(string key) { var l = Long(key); return l.HasValue ? (int?)(int)l.Value : null; }
        public bool? Bool(string key) { var v = this[key]; return v is bool ? (bool?)(bool)v : null; }
        public JObj Obj(string key) { return this[key] as JObj; }
        public List<object> Arr(string key) { return this[key] as List<object>; }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var k in _keys) yield return new KeyValuePair<string, object>(k, _map[k]);
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public override string ToString() { return Json.Write(this, true); }
    }

    /// <summary>
    /// Minimal, dependency-free JSON reader / writer. Objects parse to <see cref="JObj"/>, arrays to List&lt;object&gt;,
    /// numbers to long (integral) or double, plus string / bool / null. The writer accepts JObj, IDictionary&lt;string,*&gt;,
    /// IEnumerable, string, bool, integral and floating numbers, DateTime (ISO-8601 round trip) and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            var p = new Parser(text);
            p.Ws();
            var v = p.Value();
            p.Ws();
            if (!p.End) throw p.Err("trailing characters");
            return v;
        }

        public static JObj ParseObj(string text)
        {
            var o = Parse(text) as JObj;
            if (o == null) throw new FormatException("JSON: the top-level value is not an object");
            return o;
        }

        /// <summary>Reads a UTF-8 (BOM tolerated) JSON file into an object; null if the file does not exist.</summary>
        public static JObj ReadObjFile(string path)
        {
            if (!File.Exists(path)) return null;
            return ParseObj(ReadAllTextShared(path));
        }

        /// <summary>Reads a whole text file while other processes may hold it open for writing (FileShare.ReadWrite|Delete).</summary>
        public static string ReadAllTextShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true))
                return sr.ReadToEnd();
        }

        public static string Write(object v, bool indent = true)
        {
            var sb = new StringBuilder();
            W(sb, v, indent, 0);
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder();
            Str(sb, s);
            return sb.ToString();
        }

        private static void W(StringBuilder sb, object v, bool ind, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Str(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is uint || v is ulong || v is short || v is ushort || v is byte || v is sbyte)
            { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (v is DateTime) { Str(sb, ((DateTime)v).ToString("o", CultureInfo.InvariantCulture)); return; }
            if (v is Enum) { Str(sb, v.ToString()); return; }
            IEnumerable<KeyValuePair<string, object>> pairs = null;
            if (v is JObj) pairs = (JObj)v;
            else if (v is IDictionary)
            {
                var l = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry e in (IDictionary)v) l.Add(new KeyValuePair<string, object>(Convert.ToString(e.Key, CultureInfo.InvariantCulture), e.Value));
                pairs = l;
            }
            if (pairs != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in pairs)
                {
                    sb.Append(first ? "" : ",");
                    first = false;
                    if (ind) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                    Str(sb, kv.Key);
                    sb.Append(ind ? ": " : ":");
                    W(sb, kv.Value, ind, depth + 1);
                }
                if (!first && ind) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in (IEnumerable)v)
                {
                    sb.Append(first ? "" : ",");
                    first = false;
                    if (ind) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                    W(sb, x, ind, depth + 1);
                }
                if (!first && ind) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                sb.Append(']');
                return;
            }
            Str(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s; if (_s.Length > 0 && _s[0] == '﻿') _i = 1; }
            public bool End { get { return _i >= _s.Length; } }
            public FormatException Err(string m) { return new FormatException("JSON: " + m + " at offset " + _i); }

            public void Ws() { while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\r' || _s[_i] == '\n')) _i++; }

            public object Value()
            {
                if (End) throw Err("unexpected end");
                char c = _s[_i];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == 't') { Lit("true"); return true; }
                if (c == 'f') { Lit("false"); return false; }
                if (c == 'n') { Lit("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                throw Err("unexpected character '" + c + "'");
            }

            private void Lit(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw Err("expected " + w);
                _i += w.Length;
            }

            private JObj Object()
            {
                var o = new JObj();
                _i++; Ws();
                if (!End && _s[_i] == '}') { _i++; return o; }
                while (true)
                {
                    Ws();
                    if (End || _s[_i] != '"') throw Err("expected a key");
                    string k = String();
                    Ws();
                    if (End || _s[_i] != ':') throw Err("expected ':'");
                    _i++; Ws();
                    o[k] = Value();
                    Ws();
                    if (End) throw Err("unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Err("expected ',' or '}'");
                }
            }

            private List<object> Array()
            {
                var a = new List<object>();
                _i++; Ws();
                if (!End && _s[_i] == ']') { _i++; return a; }
                while (true)
                {
                    Ws();
                    a.Add(Value());
                    Ws();
                    if (End) throw Err("unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Err("expected ',' or ']'");
                }
            }

            private string String()
            {
                _i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw Err("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Err("bad escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Err("bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Err("bad escape \\" + e);
                    }
                }
            }

            private object Number()
            {
                int st = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                bool frac = false;
                if (_i < _s.Length && _s[_i] == '.') { frac = true; _i++; while (_i < _s.Length && char.IsDigit(_s[_i])) _i++; }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    frac = true; _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                string t = _s.Substring(st, _i - st);
                long l;
                if (!frac && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Err("bad number " + t);
            }
        }
    }
}
