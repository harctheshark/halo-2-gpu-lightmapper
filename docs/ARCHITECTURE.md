# Architecture

A concise overview of how the Halo 2 GPU Lightmapper is put together. Part of the system (the GPU engine itself) is not in
this repository; see [BUILDING.md](BUILDING.md) for what is and is not here.

## The pieces

```
H2 GPU Lightmapper.exe   (one file, WPF on .NET Framework 4.8, window and --cli in the same exe)
 |
 |  embedded payload (zip resources, listed in MANIFEST.json, every file md5-pinned)
 |    - CPython 3.10.5 + numpy 2.2.6   (an isolated copy; no scipy)
 |    - the engine package         (bake program, Python scripts, GPU kernels host, capture DLLs, tag writer)
 |
 |  first start unpacks to
 |    %LOCALAPPDATA%\H2GpuLightmapper\rt\          runtimes, shared and immutable (md5-checked before every bake)
 |    <H2EK>\GPU Work\_runtime\<ver>\     the engine, per H2EK
 |
 |  a bake is a chain of child processes (a Windows job object: cancelling kills the whole tree)
 |    shell -> h2lm-bake.exe -> osoy_launch -> tool_fast (+ hook DLLs) ; Python (bundled) ; fullgpu.exe
```

**Shell (C#).** `src/H2GpuLightmapper` is the window and the command line; `src/H2GpuLightmapper.Core` holds everything that is
not UI: H2EK discovery, preflight checks, payload extraction, the process runner, progress parsing, and the safety code
(backups, installer, restore, recovery, the GPU lock). `src/H2GpuLightmapper.DllSlots` patches the work-folder path inside
the capture DLL; `src/Proc` runs child processes inside a job object. All of it compiles into the single exe.

**Bake program (C#).** `h2lm-bake.exe` sits in each engine folder (the production engine and the AO engine) and drives one bake. Its
source is part of the engine and is not in this repository. Until 0.6.12 the driver was a PowerShell script, `gpu_bake.ps1`, run on a bundled
PowerShell 7. The program takes the same steps in the same order and takes the same arguments, and the app uses no PowerShell. Its
phases are:

1. **Pre-flight.** Clear the engine's environment variables, check the root, the tool, the hook DLLs and the files of the direct tag
   writer.
2. **Capture.** Start `tool_fast` through `osoy_launch` (a held launch, so the hook DLL is injected early), wait for the DLL's ready
   message and for the tool to exit, then run the checks on what it recorded. A bake that reuses a capture skips this phase and checks
   the capture's records instead.
3. **Derive.** Python helpers build the sky, surface-light, owner, seam, sun-cull and object-grid tables from the capture.
4. **GPU bake.** Plan the parts (`fullgpu.exe` writes the plan), run the photon lattice, then the main pass in one run or in parts
   that are merged afterwards, and check the results.
5. **Tag writer.** The direct tag writer makes the stage and validates it. Only when it fails does the tool's own write-back run, as
   the fallback.

Every child process is started through one place in the program. The AO engine's copy, started with `-AOOnly`, takes the place of
that engine's own fork of the script.

**Bundled Python.** The numeric pipeline is Python with numpy. The exe carries a portable copy, so the user installs nothing. The
shell builds the child environment from scratch (every `GPULM_*`, `H2LM_*` and `PYTHON*` variable removed, then its own set) so a
developer's shell settings cannot leak into a bake.

**Engine payload.** The validated GPU lightmapper pipeline, packaged under `engine_v373`: the capture and estimator DLLs,
the `fullgpu.exe` compute host and its HLSL kernels, the Python pipeline, and the direct tag writer. The Python scripts are
run as they are; small, strictly checked patches (`patches/`) adapt them to the exe (configurable paths, no console window, a
Custom quality row, AO and light-probe drivers), and each patch is pinned by the md5 of the file it applies to.

## The bake, step by step

1. **Preflight.** The H2EK's `tool_fast.exe` must be the supported build (md5 `3a889d37...`), the prerequisites must exist,
   the paths must fit (the capture DLL holds a work path of at most 60 characters, so a short junction is made when needed),
   and a GPU check (`native/gpucheck`) confirms Direct3D 11 feature level 11_0 with FP64 and extended doubles.
2. **Back up** the BSP's lightmap files to `_TAGBAK_ui`, md5-verified.
3. **Capture.** `tool_fast` is started through `osoy_launch` (a patched build of the Osoyoos launcher) and a DLL is injected
   (`inject.exe`) that hooks the CPU lightmapper. Instead of computing light, the hooks record what the CPU lightmapper would
   have asked: the geometry, the lightmap charts, the texel sample points, the lights and the photon-manager data. This is
   the "estimator DLL" capture. The Osoyoos `H2ToolHooks.dll` supplies the Plucker (ray/triangle) and scenery fixes the CPU
   baseline also runs with.
4. **Derive** the helper structures from the capture (owner tables, object grids, seam lists, ...).
5. **GPU bake.** `fullgpu.exe` is a Direct3D 11 compute host. It runs the photon emission and the radiance lattice, the sun,
   sky and emissive direct light, and the final gather with closest-hit rules matched to the CPU's (including its tie rule on
   coplanar duplicates), then merges parts when a big map is split into several passes.
6. **Direct tag writer.** Python code converts the bake products into the lightmap tag set (bitmaps, per-vertex lighting,
   palettes) and writes them to a *stage* folder, validated against the tag format. If a map cannot use the direct writer, a
   fallback runs the tool's own write-back with the same capture.
7. **AO (optional).** A second GPU pass traces 1,024 short rays per lightmap texel, blends texels across lightmap seams by
   their world position (never with a surface stacked underneath), and darkens the lightmap. The formula follows Unreal
   Engine Lightmass' ambient occlusion. It is an artistic layer, not CPU-faithful. The seam filter is a numpy-only radius
   search so that scipy need not be bundled.
8. **Light probes (optional).** A separate probe capture bakes the BSP's spherical-harmonics light probes on the GPU. The
   lightmap's own capture stays identical to the plain bake; the probe values are merged into the final tag, and the merge
   refuses any change outside the probe fields.
9. **Install and verify.** Only now are the live tags written, by the installer (`lmw_install`): it keeps its own backup,
   writes a progress marker before the first replace, replaces file by file, and verifies by md5 that the installed files
   equal the stage. A file held open by Sapien or Guerilla refuses the install with nothing changed; **Retry** installs the
   kept stage.

## Quality and modes

- **Low / Super** run the production argument sets. **Custom** writes the photon count and ray count straight into the tool's
  quality row (through `osoy_launch --quality-row`) and runs a guarded copy of the GPU host that refuses a photon count its
  32-bit emission arithmetic cannot hold.
- **Standard / Lightprobes / Lightmap Only** select which of the steps above run.
- **Capture reuse** is gated by a capture record (quality, binaries, bounds database, capture id) plus a stat fingerprint of
  the tags tree; changed tags need an explicit confirmation.

## Safety design

- The live tags are written in exactly one place (the installer), never by the GPU stages.
- Everything that replaces a file first copies it (`_TAGBAK_ui`, the installer's own backup, `_TAGBAK_auto`).
- Unfinished work is recorded in marker files so a crash or power loss is recovered at the next start.
- A GPU lock keeps two bakes from sharing the GPU; worker folders are allocated from a reserved range per scenario and BSP.
- The shell refuses to run a recursive delete anywhere near a work folder (the skeleton folders hold junctions into the live
  tags), and its tests enforce that rule on the source.
- Cancel kills the process tree, except during a tag write, which is finished and verified first.

## Repository map

| Path | What |
|---|---|
| `src/` | the app: shell, core library, DLL slot patcher, process runner, `sh_stage.py` (probe tag merge) |
| `patches/` | the strict, md5-pinned patches that adapt the engine scripts to the exe, with their series files (the `gpu_bake` series is for 0.6.12 and older, whose driver was a script) |
| `tools/payload/` | the payload builder: stages the engine, zips the payload, builds the exe that embeds it |
| `native/` | C/C++ sources and build scripts: `gpucheck`, the `osoy_launch` patch and build, the `fullgpu_guard` build |
| `tests/` | unit and end-to-end test sources (C# test programs, Python tests) |
| `docs/` | this documentation |
