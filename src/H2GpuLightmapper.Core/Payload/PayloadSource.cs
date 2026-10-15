using System;
using System.IO;
using System.Reflection;

namespace H2GpuLightmapper.Core.Payload
{
    /// <summary>Where the payload zips and MANIFEST.json come from: the exe's embedded resources in production, a folder in
    /// tests and dev runs (--payload-dir).</summary>
    public interface IPayloadSource
    {
        string Describe { get; }
        /// <summary>"payload/MANIFEST.json", or null if absent.</summary>
        Stream OpenManifest();
        /// <summary>A seekable read stream over the resource "payload/&lt;x&gt;.zip", or null if absent.</summary>
        Stream OpenResource(string resourceName);
    }

    /// <summary>Embedded resources (S0.1: GetManifestResourceStream is an UnmanagedMemoryStream over the mapped exe image;
    /// nothing is copied into managed memory).</summary>
    public sealed class AssemblyPayloadSource : IPayloadSource
    {
        private readonly Assembly _asm;
        public AssemblyPayloadSource(Assembly asm) { _asm = asm ?? throw new ArgumentNullException("asm"); }
        public string Describe { get { return "embedded:" + _asm.GetName().Name; } }
        public Stream OpenManifest() { return _asm.GetManifestResourceStream(PayloadManifest.ResourceName); }
        public Stream OpenResource(string resourceName) { return _asm.GetManifestResourceStream(resourceName); }
    }

    /// <summary>A folder holding MANIFEST.json and the zips: resource "payload/x.zip" maps to &lt;dir&gt;\x.zip.</summary>
    public sealed class DirectoryPayloadSource : IPayloadSource
    {
        private readonly string _dir;
        public DirectoryPayloadSource(string dir) { _dir = Path.GetFullPath(dir); }
        public string Describe { get { return "dir:" + _dir; } }
        public Stream OpenManifest() { return Open("MANIFEST.json"); }

        public Stream OpenResource(string resourceName)
        {
            const string pfx = "payload/";
            string leaf = resourceName.StartsWith(pfx, StringComparison.Ordinal) ? resourceName.Substring(pfx.Length) : resourceName;
            if (leaf.Length == 0 || leaf.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || leaf.StartsWith(".", StringComparison.Ordinal))
                throw new InvalidDataException("bad payload resource name " + resourceName);
            return Open(leaf);
        }

        private Stream Open(string leaf)
        {
            string p = Path.Combine(_dir, leaf);
            return File.Exists(p) ? new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16) : null;
        }
    }
}
