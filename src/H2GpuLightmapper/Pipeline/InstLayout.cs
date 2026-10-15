// The engine instance (INST = WORK\_runtime\<dirName>) layout the shell relies on: the payload mirrors the GPU Lightmap Work
// code layout (FINAL_DESIGN section 2; S1.9 builds it). Paths are manifest-relative ('/' separators).
using System.IO;
using H2GpuLightmapper.Core.Payload;

namespace H2GpuLightmapper.Pipeline
{
    public static class InstLayout
    {
        public const string GpuBake = "engine_v373/gpu_bake.ps1";
        /// <summary>Optional: a Custom engine copy carrying the N3 guarded fullgpu (S1.9 ships it only after gate T3a). When
        /// absent, Custom runs engine_v373 (the feature flag keeps Custom hidden until then).</summary>
        public const string GpuBakeCustom = "engine_custom/gpu_bake.ps1";
        public const string EngineDir = "engine_v373";
        public const string EngineCustomDir = "engine_custom";
        public const string LmwInstall = "engine_v373/v043-tagwriter/lmw_install.py";
        /// <summary>The format / invariant gate gpu_bake runs on its own stage (--check-set); the shell runs it on probe stages.</summary>
        public const string LmwValidate = "engine_v373/v043-tagwriter/lmw_validate.py";
        public const string EstimatorDll = "engine_v373/v014-inject-estimator/gpulm_estimator.dll";
        public const string FullGpu = "engine_v373/v041-fullgpu/fullgpu.exe";
        public const string AoDriver = "_research/ao/gpu_ao.py";
        public const string AoEngineDir = "_research/ao/dev_AO";
        public const string AoGpuBake = "_research/ao/dev_AO/gpu_bake.ps1";
        public const string ProbeDriver = "_research/lightprobes/gpu_lightprobes.py";
        public const string ShStage = "standalone/sh_stage.py";
        /// <summary>The GLW-root tag writer copy (lmw_tagio + lmw_defs.json) the lp_* scripts and the map query import.</summary>
        public const string TagIoDir = "v043-tagwriter";
        public const string TagIo = "v043-tagwriter/lmw_tagio.py";
        public const string GpuCheckPlain = "gpucheck_plain.exe";

        public static string Full(string instDir, string rel) { return Path.Combine(instDir, rel.Replace('/', '\\')); }

        /// <summary>A manifest file found by its file name anywhere in INST (e.g. gpucheck_plain.exe), or null.</summary>
        public static string FindByName(PayloadComponent inst, string instDir, string fileName)
        {
            foreach (var f in inst.Files)
                if (string.Equals(f.FileName, fileName, System.StringComparison.OrdinalIgnoreCase)) return Full(instDir, f.Path);
            return null;
        }
    }
}
