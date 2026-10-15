// S2.2 H2EK discovery (FINAL_DESIGN section 4.1, ship-fast design section 6): remembered roots, Steam (SteamPath ->
// libraryfolders.vdf -> appmanifest_1613450.acf -> installdir), Osoyoos Settings.JSON H2 MCC profiles, and Browse.
// Read-only: nothing here writes anywhere except AppSettings.Save (the shell's own settings.json).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Util;
using Microsoft.Win32;

namespace H2GpuLightmapper.Core.Discovery
{
    public enum RootSource { Remembered, Steam, Osoyoos, Browse }

    public sealed class RootCandidate
    {
        public string Root;
        public RootSource Source;
        /// <summary>"Steam library D:\Games (build 11518694)", "Osoyoos profile 'My H2EK'", ...</summary>
        public string Label;
        /// <summary>True when ROOT\tool_fast.exe exists (a quick filter; preflight does the real check).</summary>
        public bool HasToolFast;
        public override string ToString() { return Source + " " + Root + " (" + Label + ")"; }
    }

    public sealed class DiscoveryOptions
    {
        public IEnumerable<string> Remembered;
        /// <summary>null = read HKCU\Software\Valve\Steam\SteamPath, then HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath.</summary>
        public string SteamPath;
        public bool SkipSteam;
        /// <summary>null = %APPDATA%\Osoyoos\Settings.JSON.</summary>
        public string OsoyoosSettings;
        public bool SkipOsoyoos;
    }

    public static class H2ekDiscovery
    {
        public const string H2ekAppId = "1613450";

        public static List<RootCandidate> Discover(DiscoveryOptions o = null)
        {
            o = o ?? new DiscoveryOptions();
            var all = new List<RootCandidate>();
            if (o.Remembered != null)
                foreach (var r in o.Remembered.Where(x => !string.IsNullOrWhiteSpace(x)))
                    all.Add(new RootCandidate { Root = r, Source = RootSource.Remembered, Label = "used before" });
            if (!o.SkipSteam)
            {
                try { all.AddRange(FromSteam(o.SteamPath ?? SteamPathFromRegistry())); } catch (Exception) { }
            }
            if (!o.SkipOsoyoos)
            {
                try { all.AddRange(FromOsoyoos(o.OsoyoosSettings ?? DefaultOsoyoosSettings())); } catch (Exception) { }
            }
            return Dedupe(all);
        }

        /// <summary>First occurrence wins (order: remembered, Steam, Osoyoos); paths compared case-insensitively, normalized.</summary>
        public static List<RootCandidate> Dedupe(IEnumerable<RootCandidate> cands)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r = new List<RootCandidate>();
            foreach (var c in cands)
            {
                string n;
                try { n = NormalizeRoot(c.Root); } catch (Exception) { continue; }
                if (!seen.Add(n)) continue;
                c.Root = n;
                c.HasToolFast = File.Exists(Path.Combine(n, "tool_fast.exe"));
                r.Add(c);
            }
            return r;
        }

        public static string NormalizeRoot(string root)
        {
            string p = Path.GetFullPath(root.Trim().Trim('"').Replace('/', '\\'));
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }

        public static string DefaultOsoyoosSettings()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Osoyoos", "Settings.JSON");
        }

        /// <summary>HKCU\Software\Valve\Steam\SteamPath (forward slashes, lower-case on this PC), else the HKLM InstallPath.</summary>
        public static string SteamPathFromRegistry()
        {
            foreach (var probe in new[] { Tuple.Create(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                                          Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                                          Tuple.Create(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(probe.Item1, RegistryView.Registry64))
                    using (var k = hk.OpenSubKey(probe.Item2))
                    {
                        var v = k == null ? null : k.GetValue(probe.Item3) as string;
                        if (!string.IsNullOrWhiteSpace(v)) return Path.GetFullPath(v.Replace('/', '\\'));
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Every Steam library folder listed in &lt;steam&gt;\steamapps\libraryfolders.vdf (plus the Steam folder itself).</summary>
        public static List<string> SteamLibraries(string steamPath)
        {
            var libs = new List<string>();
            if (string.IsNullOrEmpty(steamPath)) return libs;
            libs.Add(NormalizeRoot(steamPath));
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return libs;
            var root = Vdf.ParseFile(vdf);
            var lf = root.Node("libraryfolders") ?? root.Node("LibraryFolders");
            if (lf == null) return libs;
            foreach (var kv in lf.Items)
            {
                string p = null;
                if (kv.Value is VdfNode) p = ((VdfNode)kv.Value).Str("path");          // new format: "N" { "path" "..." }
                else if (kv.Value is string && char.IsDigit(kv.Key[0])) p = (string)kv.Value;   // old format: "N" "path"
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    string n = NormalizeRoot(p);
                    if (!libs.Contains(n, StringComparer.OrdinalIgnoreCase)) libs.Add(n);
                }
                catch (Exception) { }
            }
            return libs;
        }

        /// <summary>H2EK installs (Steam app 1613450 "Halo 2 Mod Tools - MCC") across all Steam libraries.</summary>
        public static List<RootCandidate> FromSteam(string steamPath)
        {
            var r = new List<RootCandidate>();
            foreach (var lib in SteamLibraries(steamPath))
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + H2ekAppId + ".acf");
                if (!File.Exists(acf)) continue;
                VdfNode st;
                try { st = Vdf.ParseFile(acf).Node("AppState"); } catch (Exception) { continue; }
                if (st == null) continue;
                string dir = st.Str("installdir");
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string root = Path.Combine(lib, "steamapps", "common", dir);
                if (!Directory.Exists(root)) continue;
                r.Add(new RootCandidate
                {
                    Root = root, Source = RootSource.Steam,
                    Label = "Steam " + (st.Str("name") ?? "app " + H2ekAppId) + " in " + lib + (st.Str("buildid") != null ? " (build " + st.Str("buildid") + ")" : ""),
                });
            }
            return r;
        }

        /// <summary>Halo 2 MCC profiles of the Osoyoos launcher (%APPDATA%\Osoyoos\Settings.JSON: a JSON array of profiles with
        /// generation "halo2" and build_type "release_mcc"); ROOT = the folder of tool_fast_path (else tool_path, else tag_path's parent).</summary>
        public static List<RootCandidate> FromOsoyoos(string settingsPath)
        {
            var r = new List<RootCandidate>();
            if (string.IsNullOrEmpty(settingsPath) || !File.Exists(settingsPath)) return r;
            var doc = Json.Parse(Json.ReadAllTextShared(settingsPath));
            var arr = doc as List<object>;
            if (arr == null && doc is JObj) arr = ((JObj)doc).Arr("profiles");
            if (arr == null) return r;
            foreach (var o in arr.OfType<JObj>())
            {
                if (!string.Equals(o.Str("generation"), "halo2", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(o.Str("build_type"), "release_mcc", StringComparison.OrdinalIgnoreCase)) continue;
                string root = null;
                foreach (var key in new[] { "tool_fast_path", "tool_path" })
                {
                    string t = o.Str(key);
                    if (!string.IsNullOrWhiteSpace(t)) { root = Path.GetDirectoryName(t); break; }
                }
                if (root == null && !string.IsNullOrWhiteSpace(o.Str("tag_path"))) root = Path.GetDirectoryName(o.Str("tag_path").TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(root)) continue;
                r.Add(new RootCandidate { Root = root, Source = RootSource.Osoyoos, Label = "Osoyoos profile '" + (o.Str("profile_name") ?? "?") + "'" });
            }
            return r;
        }

        /// <summary>Browse: accepts the H2EK folder itself, or tool_fast.exe / any file inside it, or its tags folder.</summary>
        public static RootCandidate FromBrowse(string picked)
        {
            string p = NormalizeRoot(picked);
            if (File.Exists(p)) p = Path.GetDirectoryName(p);
            if (string.Equals(Path.GetFileName(p), "tags", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(p, "tool_fast.exe")))
                p = Path.GetDirectoryName(p);
            return Dedupe(new[] { new RootCandidate { Root = p, Source = RootSource.Browse, Label = "chosen" } }).FirstOrDefault();
        }
    }
}
