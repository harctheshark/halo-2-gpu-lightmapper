using System;
using System.Collections.Generic;
using System.IO;

namespace H2GpuLightmapper.DllSlots.Tests
{
    internal sealed class TestFailure : Exception { public TestFailure(string m) : base(m) { } }
    internal sealed class TestSkip : Exception { public TestSkip(string m) : base(m) { } }

    internal static class Assert
    {
        public static void True(bool cond, string what) { if (!cond) throw new TestFailure(what); }
        public static void Eq<TV>(TV expected, TV actual, string what)
        {
            if (!EqualityComparer<TV>.Default.Equals(expected, actual))
                throw new TestFailure(what + ": expected <" + expected + "> got <" + actual + ">");
        }
        public static TE Throws<TE>(Action a, string what) where TE : Exception
        {
            try { a(); }
            catch (TE e) { return e; }
            catch (Exception e) { throw new TestFailure(what + ": expected " + typeof(TE).Name + ", got " + e.GetType().Name + ": " + e.Message); }
            throw new TestFailure(what + ": expected " + typeof(TE).Name + ", nothing was thrown");
        }
    }

    /// <summary>
    /// Records every file / directory / junction a test creates and undoes them in reverse order with NON-recursive
    /// operations only: File.Delete, Directory.Delete(recursive:false), Junction.Remove (RemoveDirectory). Anything it
    /// cannot remove is reported and left in place.
    /// </summary>
    internal sealed class Sandbox : IDisposable
    {
        private readonly Stack<KeyValuePair<char, string>> _made = new Stack<KeyValuePair<char, string>>();
        public string Root { get; }
        public List<string> Leftovers { get; } = new List<string>();

        public Sandbox(string root) { Root = root; }

        public string Dir(string path) { var p = Path.Combine(Root, path); MkDirChain(p); return p; }
        public string File(string path, string text)
        {
            var p = Path.Combine(Root, path);
            MkDirChain(Path.GetDirectoryName(p));
            System.IO.File.WriteAllText(p, text);
            _made.Push(new KeyValuePair<char, string>('f', p));
            return p;
        }
        public void TrackFile(string p) => _made.Push(new KeyValuePair<char, string>('f', p));
        public void TrackDir(string p) => _made.Push(new KeyValuePair<char, string>('d', p));
        public void TrackJunction(string p) => _made.Push(new KeyValuePair<char, string>('j', p));

        /// <summary>Creates each missing directory of <paramref name="p"/> one level at a time and tracks it.</summary>
        public void MkDirChain(string p)
        {
            var missing = new Stack<string>();
            for (var d = Path.GetFullPath(p); d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
            while (missing.Count > 0) { var d = missing.Pop(); Directory.CreateDirectory(d); TrackDir(d); }
        }

        public void Dispose()
        {
            while (_made.Count > 0)
            {
                var e = _made.Pop();
                try
                {
                    switch (e.Key)
                    {
                        case 'f': if (System.IO.File.Exists(e.Value)) System.IO.File.Delete(e.Value); break;
                        case 'j': if (Junction.PathExists(e.Value)) Junction.Remove(e.Value); break;
                        case 'd':
                            if (Junction.IsJunction(e.Value)) { Leftovers.Add(e.Value + " (became a junction; left)"); break; }
                            if (Directory.Exists(e.Value)) Directory.Delete(e.Value, false);
                            break;
                    }
                }
                catch (Exception ex) { Leftovers.Add(e.Value + " (" + ex.GetType().Name + ": " + ex.Message + ")"); }
            }
        }
    }
}
