// A small, strict "--name value" / "--switch" parser for the same-exe CLI. Unknown options are an error (exit 2), so a typo
// can never silently fall back to a default.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace H2GpuLightmapper.Cli
{
    public sealed class CliArgs
    {
        private readonly Dictionary<string, string> _vals = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _switches = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
        public string Verb { get; private set; }
        public List<string> Errors { get; } = new List<string>();

        /// <param name="valueOptions">options that take a value</param>
        /// <param name="switchOptions">options without a value</param>
        public static CliArgs Parse(IList<string> args, int start, ISet<string> valueOptions, ISet<string> switchOptions)
        {
            var a = new CliArgs();
            int i = start;
            if (i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal)) a.Verb = args[i++];
            for (; i < args.Count; i++)
            {
                string k = args[i];
                if (!k.StartsWith("--", StringComparison.Ordinal)) { a.Errors.Add("unexpected argument '" + k + "'"); continue; }
                int eq = k.IndexOf('=');
                string inline = null;
                if (eq > 0) { inline = k.Substring(eq + 1); k = k.Substring(0, eq); }
                if (switchOptions.Contains(k))
                {
                    if (inline != null) a.Errors.Add(k + " takes no value");
                    a._switches.Add(k);
                }
                else if (valueOptions.Contains(k))
                {
                    string v = inline;
                    if (v == null)
                    {
                        if (i + 1 >= args.Count) { a.Errors.Add(k + " needs a value"); continue; }
                        v = args[++i];
                    }
                    if (a._vals.ContainsKey(k)) a.Errors.Add(k + " given twice");
                    a._vals[k] = v;
                }
                else a.Errors.Add("unknown option " + k);
            }
            return a;
        }

        public bool Has(string k) { _used.Add(k); return _switches.Contains(k); }
        public string Get(string k) { _used.Add(k); string v; return _vals.TryGetValue(k, out v) ? v : null; }

        public long? GetLong(string k)
        {
            string v = Get(k);
            if (v == null) return null;
            long r;
            string t = v.Replace(",", "").Replace("_", "").Trim();
            if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) { Errors.Add(k + ": '" + v + "' is not a whole number"); return null; }
            return r;
        }

        public int? GetInt(string k)
        {
            var l = GetLong(k);
            if (l == null) return null;
            if (l < int.MinValue || l > int.MaxValue) { Errors.Add(k + ": out of range"); return null; }
            return (int)l.Value;
        }

        public double? GetDouble(string k)
        {
            string v = Get(k);
            if (v == null) return null;
            double r;
            if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r) || double.IsNaN(r) || double.IsInfinity(r)) { Errors.Add(k + ": '" + v + "' is not a number"); return null; }
            return r;
        }

        public string Choice(string k, string def, params string[] allowed)
        {
            string v = Get(k);
            if (v == null) return def;
            string low = v.Trim().ToLowerInvariant();
            if (!allowed.Contains(low)) { Errors.Add(k + " must be one of " + string.Join(" | ", allowed) + " (got '" + v + "')"); return def; }
            return low;
        }

        /// <summary>Options that were given but that this verb never reads.</summary>
        public IEnumerable<string> Unused { get { return _vals.Keys.Concat(_switches).Where(k => !_used.Contains(k)); } }
    }
}
