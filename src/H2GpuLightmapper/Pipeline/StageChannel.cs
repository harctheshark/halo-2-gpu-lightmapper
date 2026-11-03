using System;
using System.Collections.Generic;
using H2Lm.Proc;

namespace H2GpuLightmapper.Pipeline
{
    public sealed class StageChannel : IDisposable
    {
        public static bool Off = true;
        public static StageChannel Current { get { return null; } }
        public string Home;
        public static StageChannel Start() { return new StageChannel(); }
        public void SetDirs(IEnumerable<string> dirs) { }
        public void SetToolDirs(IEnumerable<string> dirs) { }
        public static ChildEnvironment Apply(ChildEnvironment env) { return env; }
        public void Dispose() { }
    }
}
