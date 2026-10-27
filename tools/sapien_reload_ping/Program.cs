// sapien_reload_ping.exe -- tell an open Sapien that tag files changed on disk (see SapienReloadPing.cs for the RE).
//   sapien_reload_ping.exe [--set] <tag file> [<tag file> ...]
//   --set : for each *.scenario_structure_lightmap also signal its <base>_bitmaps.bitmap / <base>_truecolor_bitmaps.bitmap
// Only bumps each file's LastWriteTime (no data byte is written). Harmless when Sapien is not running (exit 0). No window; BELOW_NORMAL.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using H2GpuLightmapper.Core.Safety;

internal static class SapienReloadPingExe
{
    private static int Main(string[] args)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        bool set = false;
        var paths = new List<string>();
        foreach (var a in args) { if (a == "--set") set = true; else paths.Add(a); }
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: sapien_reload_ping.exe [--set] <tag file> [...]   (bumps LastWriteTime so an open Sapien reloads the tag)");
            return 2;
        }
        if (set)
            foreach (var p in paths.ToArray())
            {
                const string L = ".scenario_structure_lightmap";
                if (!p.EndsWith(L, StringComparison.OrdinalIgnoreCase)) continue;
                string b = p.Substring(0, p.Length - L.Length);
                paths.Add(b + "_bitmaps.bitmap");
                paths.Add(b + "_truecolor_bitmaps.bitmap");
            }
        var r = SapienReloadPing.TouchPaths(paths);
        foreach (var t in r.Touched) Console.WriteLine("signalled " + t);
        foreach (var s in r.Skipped) Console.WriteLine("skipped   " + s);
        if (r.Touched.Count == 0 && r.Skipped.Count == 0) Console.WriteLine("nothing to signal (no such file)");
        return 0;
    }
}
