using System;
using System.IO;

namespace H2GpuLightmapper.Core.Payload
{
    /// <summary>
    /// The per-user folders the shell owns, all overridable for tests. Production: %LOCALAPPDATA%\H2GpuLightmapper\
    /// (rt\ runtimes, settings.json). The DLL-slot alias candidates follow FINAL_DESIGN section 2 (%LOCALAPPDATA%\H2GLM\k6\,
    /// then the WORK volume root \H2GLM\k6\).
    /// </summary>
    public sealed class ShellPaths
    {
        public const string AppFolderName = "H2GpuLightmapper";
        public const string WorkFolderName = "GPU Lightmap Work";
        public const string RuntimeFolderName = "_runtime";
        public const string StandaloneFolderName = "standalone";

        /// <summary>%LOCALAPPDATA% (or a test sandbox).</summary>
        public string LocalAppData;
        /// <summary>LocalAppData passed to SlotAlias for the first alias candidate (default: <see cref="LocalAppData"/>).</summary>
        public string AliasLocalAppData;
        /// <summary>Volume root for the second alias candidate; null = WORK's own drive root.</summary>
        public string AliasVolumeRoot;

        public string AppDir { get { return Path.Combine(LocalAppData, AppFolderName); } }
        public string RtRoot { get { return Path.Combine(AppDir, "rt"); } }
        public string SettingsPath { get { return Path.Combine(AppDir, "settings.json"); } }
        public string LogsDir { get { return Path.Combine(AppDir, "logs"); } }

        public static ShellPaths Default()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(lad)) lad = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return new ShellPaths { LocalAppData = lad, AliasLocalAppData = lad };
        }

        public static ShellPaths ForSandbox(string localAppData, string aliasLocalAppData = null, string aliasVolumeRoot = null)
        {
            return new ShellPaths { LocalAppData = localAppData, AliasLocalAppData = aliasLocalAppData ?? localAppData, AliasVolumeRoot = aliasVolumeRoot };
        }

        /// <summary>WORK = ROOT\GPU Lightmap Work (the REAL path; H2LM_WORK; never the DLL-slot alias). A drive root keeps its
        /// backslash ("D:" alone would be the drive's CURRENT folder); such a root is refused anyway (<see cref="IsDriveRoot"/>).</summary>
        public static string WorkOf(string root)
        {
            string full = Path.GetFullPath(root);
            string r = IsDriveRoot(full) ? Path.GetPathRoot(full) : full.TrimEnd('\\');
            return Path.Combine(r, WorkFolderName);
        }

        /// <summary>
        /// Review fix (finding 23): an H2EK at the root of a drive ("D:\") cannot bake -- gpu_bake's P1 needs H2LM_ROOT fully
        /// qualified AND without a trailing backslash, which no spelling of a drive root satisfies, and "D:" alone is relative.
        /// The shell refuses it before anything is written (exit 2).
        /// </summary>
        public static bool IsDriveRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); } catch (Exception) { return false; }
            string pr = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(pr) && string.Equals(pr.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public const string DriveRootMessage = "the H2EK is at the root of a drive; the bake scripts need it inside a folder (they cannot be given a drive root) -- move the Halo 2 Editing Kit into a folder, e.g. D:\\H2EK";
        /// <summary>WORK\_runtime (INST parent).</summary>
        public static string InstParentOf(string work) { return Path.Combine(work, RuntimeFolderName); }
        /// <summary>WORK\standalone (workers.json, runs\, history.json, current_run.json).</summary>
        public static string StandaloneOf(string work) { return Path.Combine(work, StandaloneFolderName); }
    }
}
