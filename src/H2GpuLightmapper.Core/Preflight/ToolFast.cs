using System;
using System.Collections.Generic;
using System.IO;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Preflight
{
    public enum ToolVerdict { Supported, KnownUnsupported, Unknown, Missing }

    public sealed class ToolIdentity
    {
        public string Path;
        public long Size;
        public string Md5;
        public ToolVerdict Verdict;
        public string Build;
        /// <summary>The UI status line: "tool_fast.exe 2023.06.20 (3a889d37) — supported" or the red reason.</summary>
        public string StatusLine;
    }

    /// <summary>tool_fast.exe identification (FINAL_DESIGN section 8). Every hook address in the DLL, osoy_launch and
    /// H2ToolHooks is for build 2023.06.20.176294.1 only, so any other build is refused.</summary>
    public static class ToolFast
    {
        public const string SupportedMd5 = "3a889d370a7be537af47ff8035acd201";
        public const long SupportedSize = 8804592;
        public const string SupportedBuild = "2023.06.20.176294.1";

        /// <summary>The other H2EK tool_fast md5s the Osoyoos launcher knows (BultinProfiles.json "H2C EK").</summary>
        public static readonly IReadOnlyDictionary<string, string> KnownOther = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "8df7d90caacd84347d2315520a4ea40b", "an older H2EK release" },
            { "0b7ca8fc3742180baa7b5e0a317d942a", "an older H2EK release" },
            { "0d283f6a3c269dfe4bafb6595a41369c", "an older H2EK release" },
        };

        public static ToolIdentity Identify(string root)
        {
            string p = System.IO.Path.Combine(root, "tool_fast.exe");
            var t = new ToolIdentity { Path = p };
            if (!File.Exists(p))
            {
                t.Verdict = ToolVerdict.Missing;
                t.StatusLine = "tool_fast.exe not found in " + root + " -- pick the H2EK folder (the one with tool_fast.exe and tags\\)";
                return t;
            }
            t.Size = new FileInfo(p).Length;
            t.Md5 = SafeFs.Md5File(p);
            return Classify(t);
        }

        public static ToolIdentity Classify(ToolIdentity t)
        {
            string m8 = t.Md5 == null ? "?" : t.Md5.Substring(0, 8);
            if (string.Equals(t.Md5, SupportedMd5, StringComparison.OrdinalIgnoreCase) && t.Size == SupportedSize)
            {
                t.Verdict = ToolVerdict.Supported;
                t.Build = SupportedBuild;
                t.StatusLine = "tool_fast.exe 2023.06.20 (" + m8 + ") — supported";
            }
            else if (t.Md5 != null && KnownOther.ContainsKey(t.Md5))
            {
                t.Verdict = ToolVerdict.KnownUnsupported;
                t.StatusLine = "tool_fast.exe (" + m8 + ") is " + KnownOther[t.Md5] + " -- unsupported H2EK build: the GPU hooks are address-specific to build 2023.06.20 (update the H2EK through Steam)";
            }
            else
            {
                t.Verdict = ToolVerdict.Unknown;
                t.StatusLine = "tool_fast.exe (" + m8 + ", " + t.Size.ToString("N0") + " bytes) is not a stock tool_fast -- only the stock 2023.06.20 build (3a889d37) is supported";
            }
            return t;
        }
    }
}
