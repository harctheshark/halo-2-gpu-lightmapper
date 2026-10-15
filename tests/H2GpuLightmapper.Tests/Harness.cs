using System;
using System.Collections.Generic;
using System.IO;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;

namespace H2GpuLightmapper.Tests
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
        public static void Seq(IList<string> expected, IList<string> actual, string what)
        {
            int n = Math.Max(expected.Count, actual.Count);
            for (int i = 0; i < n; i++)
            {
                string e = i < expected.Count ? expected[i] : "(none)", a = i < actual.Count ? actual[i] : "(none)";
                if (e != a) throw new TestFailure(what + ": item " + i + " expected <" + e + "> got <" + a + ">\n    expected: " + string.Join(" | ", expected) + "\n    actual:   " + string.Join(" | ", actual));
            }
        }
        public static void Contains(string hay, string needle, string what)
        {
            if (hay == null || hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) throw new TestFailure(what + ": <" + needle + "> not in <" + Trim(hay) + ">");
        }
        public static void NotContains(string hay, string needle, string what)
        {
            if (hay != null && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) throw new TestFailure(what + ": <" + needle + "> unexpectedly in <" + Trim(hay) + ">");
        }
        public static string Trim(string s) { return s == null ? "null" : s.Length > 1500 ? s.Substring(0, 700) + "\n...\n" + s.Substring(s.Length - 700) : s; }
    }

    /// <summary>A scratch area: junctions inside are removed with Junction.Remove (the link only) before the tree is cleared with
    /// SafeFs.RemoveTreeNoReparse, which refuses -- deleting nothing -- if a reparse point is still inside. No recursive delete
    /// API is used anywhere.</summary>
    internal sealed class Sandbox : IDisposable
    {
        public string Root { get; private set; }
        public string Parent { get; private set; }
        public bool Keep;
        public List<string> Leftovers { get; } = new List<string>();

        public Sandbox(string parent, string leaf)
        {
            Parent = Path.GetFullPath(parent);
            Directory.CreateDirectory(Parent);
            Root = SafeFs.UniqueChild(Parent, leaf);
            Directory.CreateDirectory(Root);
        }

        public string Dir(string rel) { var p = Path.Combine(Root, rel); Directory.CreateDirectory(p); return p; }

        public void Dispose()
        {
            if (Keep) return;
            try
            {
                foreach (var j in SafeFs.FindReparsePoints(Root, 100))
                {
                    string t;
                    if (Junction.TryGetTarget(j, out t)) Junction.Remove(j);
                    else Leftovers.Add(j + " (a reparse point that is not a junction; left)");
                }
                SafeFs.RemoveTreeNoReparse(Root, Parent);
            }
            catch (Exception e) { Leftovers.Add(Root + " (" + e.GetType().Name + ": " + e.Message + ")"); }
        }
    }
}
