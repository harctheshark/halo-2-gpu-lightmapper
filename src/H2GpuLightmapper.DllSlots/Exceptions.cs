using System;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>The DLL bytes are not what the patcher expects (wrong build, already patched, slots not pristine,
    /// bytes outside the slots would change ...). Nothing was written.</summary>
    public sealed class SlotPatchException : Exception
    {
        public SlotPatchException(string message) : base(message) { }
        public SlotPatchException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A slot base string is unusable (too long, not ANSI, not drive-absolute, no trailing backslash ...).
    /// <see cref="Reason"/> is a one-line, user-presentable explanation.</summary>
    public sealed class SlotBaseException : Exception
    {
        public string BaseDir { get; }
        public string Reason { get; }
        public SlotBaseException(string baseDir, string reason)
            : base("slot base '" + baseDir + "' refused: " + reason) { BaseDir = baseDir; Reason = reason; }
    }

    /// <summary>No slot base works for this WORK dir: WORK\ does not fit and no alias candidate is usable.
    /// The bake must be refused (preflight exit 2).</summary>
    public sealed class SlotAliasRefusedException : Exception
    {
        public SlotAliasRefusedException(string message) : base(message) { }
    }

    /// <summary>A junction operation refused to act (target is not a junction, points elsewhere, path exists ...).
    /// Nothing was changed.</summary>
    public sealed class JunctionException : Exception
    {
        public JunctionException(string message) : base(message) { }
        public JunctionException(string message, Exception inner) : base(message, inner) { }
    }
}
