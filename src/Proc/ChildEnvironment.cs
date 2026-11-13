// S0.2 spike -> reusable by S2.3. The child environment block (FINAL_DESIGN.md section 5), built explicitly, never inherited
// implicitly: a copy of the shell's environment minus the pipeline's own variables, plus the H2LM_* contract and the
// runtime isolation variables.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace H2Lm.Proc
{
    /// <summary>What the bake child needs to know about the installed runtimes and the H2EK root (all absolute paths).</summary>
    public sealed class BakeEnvSpec
    {
        public string PyDir;     // %LOCALAPPDATA%\H2GpuLightmapper\rt\py310-<h8>        (contains py.exe); may be null in spikes
        public string Root;      // H2LM_ROOT  = the H2EK root (tool_fast.exe, tags\)
        public string Work;      // H2LM_WORK  = <ROOT>\GPU Lightmap Work   (the REAL path, never the DLL-slot alias)
        public string Code;      // H2LM_CODE  = <WORK>\_runtime\<appver>-<h8>  (INST)
        public string Farms;     // H2LM_FARMS = <WORK>\Temp (safe to delete)  (0.6.8, gpu_bake P13; null = not set, the farms are in WORK)
        public bool NoWindow = true;   // H2LM_NOWINDOW=1
    }

    public sealed class ChildEnvironment
    {
        /// <summary>Removed from the inherited environment (case-insensitive prefixes): design section 5, plus (review fix)
        /// fullgpu's research knobs DEVP_* (gather.cpp: DEVP_SB_PAD changes products, DEVP_PROF_EXIT=1 exits 0 right after the
        /// gather...) and gpu_bake's own temporaries V370_*. 0.6.8: also the Windows py launcher's PY_PYTHON / PY_PYTHON3 / PY_PYTHON2 (they
        /// pick the launcher's interpreter, e.g. a Python 3.13 without numpy): `py` resolves to the bundle's py.exe (a python.exe, which
        /// ignores both these and a script's shebang), but if anything ever reached C:\Windows\py.exe they would steer it.</summary>
        public static readonly string[] StrippedPrefixes = { "GPULM_", "OSOYOOS_", "H2LM_", "PYTHON", "DEVP_", "V370_", "PY_PYTHON" };

        /// <summary>
        /// Variables a log / a dry-run plan may print WITH their value (review fix, privacy): the ones the shell sets or
        /// overrides (H2LM_*, PATH, NO_COLOR, PYTHON*) and the pipeline
        /// prefixes that must never be present at all (shown so a leak stays visible). Every other inherited variable is
        /// passed to the child unchanged but logged by NAME only: it may hold an API key or a token.
        /// </summary>
        public static bool IsLoggable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var p in new[] { "H2LM_", "GPULM_", "OSOYOOS_", "DEVP_", "V370_", "PYTHON", "PY_PYTHON" })
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var n in new[] { "PATH", "NO_COLOR" })
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        /// <summary>Removed by exact name (case-insensitive) before the bake values are added. 1.0: the payload ships no shell runtime; these
        /// inherited console-shell variables are still removed, so the child environment does not depend on the console the app was started from.</summary>
        public static readonly string[] StrippedNames = { "PSModulePath", "PSExecutionPolicyPreference", "NO_COLOR",
                                                          "POWERSHELL_TELEMETRY_OPTOUT", "POWERSHELL_UPDATECHECK" };

        private readonly SortedDictionary<string, string> _vars =
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static ChildEnvironment Empty() { return new ChildEnvironment(); }

        public static ChildEnvironment FromCurrentProcess()
        {
            var e = new ChildEnvironment();
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                var k = (string)kv.Key;
                if (string.IsNullOrEmpty(k) || k[0] == '=') continue;     // drive-cwd pseudo variables (=C:=...)
                e._vars[k] = (string)kv.Value ?? "";
            }
            return e;
        }

        /// <summary>The bake child environment of FINAL_DESIGN.md section 5.</summary>
        public static ChildEnvironment ForBake(BakeEnvSpec s)
        {
            if (s == null) throw new ArgumentNullException("s");
            var e = FromCurrentProcess();
            foreach (var p in StrippedPrefixes) e.RemovePrefix(p);
            foreach (var n in StrippedNames) e.Remove(n);
            if (!string.IsNullOrEmpty(s.Root)) e.Set("H2LM_ROOT", s.Root);
            if (!string.IsNullOrEmpty(s.Work)) e.Set("H2LM_WORK", s.Work);
            if (!string.IsNullOrEmpty(s.Code)) e.Set("H2LM_CODE", s.Code);
            if (!string.IsNullOrEmpty(s.Farms)) e.Set("H2LM_FARMS", s.Farms);
            if (s.NoWindow) e.Set("H2LM_NOWINDOW", "1");
            string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var path = new List<string>();
            if (!string.IsNullOrEmpty(s.PyDir)) path.Add(s.PyDir);      // bundle py.exe BEFORE C:\Windows\py.exe
            path.Add(Path.Combine(sysRoot, "System32"));
            path.Add(sysRoot);
            path.Add(Path.Combine(sysRoot, @"System32\Wbem"));
            e.Set("PATH", string.Join(";", path));
            e.Set("NO_COLOR", "1");
            e.Set("PYTHONIOENCODING", "utf-8");
            return e;
        }

        public ChildEnvironment Set(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('=') >= 0 || name.IndexOf('\0') >= 0) throw new ArgumentException("bad env name: " + name);
            if (value == null) throw new ArgumentNullException("value", "use Remove(name) to delete a variable");
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("NUL in env value of " + name);
            _vars[name] = value;
            return this;
        }

        public ChildEnvironment Remove(string name) { _vars.Remove(name); return this; }

        public ChildEnvironment RemovePrefix(string prefix)
        {
            var dead = new List<string>();
            foreach (var k in _vars.Keys) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) dead.Add(k);
            foreach (var k in dead) _vars.Remove(k);
            return this;
        }

        public string Get(string name) { string v; return _vars.TryGetValue(name, out v) ? v : null; }

        public IEnumerable<KeyValuePair<string, string>> Vars { get { return _vars; } }

        /// <summary>CREATE_UNICODE_ENVIRONMENT block body: "k=v\0k=v\0" (the marshaller adds the final terminating NUL).</summary>
        public string ToBlock()
        {
            var sb = new StringBuilder();
            foreach (var kv in _vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            if (sb.Length == 0) sb.Append('\0');
            return sb.ToString();
        }
    }
}
