using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace FakeBakeEngine
{
    /// <summary>Stand-in for the engine's bake program. Runs `py -I -B "&lt;its own folder&gt;\fake_gpu_bake.py" args...` (py = the first py.exe on
    /// the PATH it inherited, the bundle's), pumping py's stdout / stderr line by line (UTF-8, flushed per line) and returning py's exit code.
    /// Every argument, `--list-params` included, goes to the python fake, which defines the parameters of each test suite.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string script = Path.Combine(here, "fake_gpu_bake.py");
            string py = FindOnPath("py");
            var err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            if (py == null) { err.WriteLine("fake h2lm-bake: no py.exe on the PATH"); return 9; }
            if (!File.Exists(script)) { err.WriteLine("fake h2lm-bake: no fake_gpu_bake.py next to " + here); return 9; }
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

            var sb = new StringBuilder("-I -B ").Append(Quote(script));
            foreach (var a in args) sb.Append(' ').Append(Quote(a));
            var psi = new ProcessStartInfo(py, sb.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(psi))
            {
                var t1 = new Thread(() => Pump(p.StandardOutput, o)); var t2 = new Thread(() => Pump(p.StandardError, err));
                t1.Start(); t2.Start();
                p.WaitForExit(); t1.Join(); t2.Join();
                return p.ExitCode;
            }
        }

        private static void Pump(StreamReader r, StreamWriter w)
        {
            string line;
            while ((line = r.ReadLine()) != null) lock (w) { w.WriteLine(line); w.Flush(); }
        }

        private static string FindOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string p = Path.Combine(d.Trim().Trim('"'), name + ".exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>CommandLineToArgvW-compatible quoting.</summary>
        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(c); bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }
    }
}
