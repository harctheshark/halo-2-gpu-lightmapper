# Building

This page says what is needed to build the app, and says plainly what this repository does **not** contain.

## What is in this repository

- the **app source** (`src/`): the WPF window, the command line, and the shell core library, all in C# for .NET Framework 4.8;
- the **payload builder** (`tools/payload/`) that stages the engine files, zips the runtimes and builds the exe that embeds them;
- **patches** (`patches/`) that adapt the engine scripts to the exe, with their series files;
- **native sources** (`native/`): `gpucheck` (the GPU capability check), the `osoy_launch` patch and its build script, and the
  build scripts for the guarded GPU host (`fullgpu_guard`);
- **test sources** (`tests/`) and the Python bundle scripts (`build/s03/`, `build/fetch_pwsh.py`).

## What is NOT in this repository

**The GPU engine.** The engine binaries (`fullgpu.exe`, the estimator/capture DLLs, `inject.exe`, `seamfast.dll`, ...), the
HLSL kernels, the capture-DLL source, the engine's PowerShell and Python pipeline (`gpu_bake.ps1` and the numeric scripts) and
the tag-writer scripts are shipped **inside the release exe**, and their source is not part of this snapshot. The patches in
`patches/` apply to those files and are pinned by md5 to the engine version they were written for, so they cannot be applied
without it.

Also not here: the Halo 2 Editing Kit (it comes from the user's own Steam install), the portable PowerShell zip and the
CPython install (downloaded or taken from a local install at build time), prebuilt binaries of any kind, and the gate proof
records (`T4_PROOF.json`, `AO_ENGINE_PROOF.json`) that the payload builder consults before it enables light probes, AO and Custom.

Consequence: **you can build and run the app shell from this repository; you cannot rebuild the release exe from it.** To get a
working bake engine, use the release exe.

## Building the app shell

Needs:

- Windows 10/11, 64-bit;
- the **.NET SDK** (a recent one; the projects target `net48`) and the **.NET Framework 4.8 targeting pack** (it ships with
  Visual Studio 2022 or the .NET Framework 4.8 Developer Pack). The projects restore offline: each `NuGet.Config` clears every
  package source.

```
cd src\H2GpuLightmapper
dotnet build -c Release
```

Clone or unpack the repository into a **short folder path** (for example `C:\src\h2glm`). From a path of about 200 characters
or more, the build can fail with `MSB3030` because of the Windows 260-character path limit.

This produces `H2 GPU Lightmapper.exe` **without** a payload: the window opens, but it cannot extract an engine. You can point
it at an unpacked payload folder with the development option listed by `--cli help`. The compiler path map in the project file
(`PathMap`) keeps build paths out of the exe, and the build is deterministic.

## Building the native helpers

Need **Visual Studio 2022 with the MSVC 14.44 toolset** (the build scripts call `vcvarsall.bat`) and Python 3.10.

- `python native\gpucheck\build_gpucheck.py` builds `gpucheck.exe` (x64, static runtime, reproducible).
- `pwsh native\osoy_launch\build_p4.ps1` builds the patched Osoyoos `osoy_launch.exe` (x86) from `osoy_launch.prod_f223095a.c`
  plus `patches\P4_osoy_launch.patch`.
- `python native\fullgpu_guard\build_fullgpu_guard.py --engine-dir <engine_v373>` builds the guarded GPU host. It needs the
  engine source tree, which is not in this repository.

## The payload builder (reference)

`tools/payload/build_payload.py` is the script that produced the release exe. It expects:

- a **production engine tree**: a folder `<H2EK_ROOT>\GPU Lightmap Work\` with `engine_v373` (and the AO and probe research
  drivers) at the md5s the patch series pins. Pass it with `--glw` (and `--h2root` for the H2EK root). The roots in
  `payload_spec.json` are the placeholders `<H2EK_ROOT>`; replace them or use the command-line overrides;
- the **PowerShell 7.6.6** zip (`python build\fetch_pwsh.py fetch --allow-download` downloads the official release and
  verifies its SHA-256);
- a **CPython 3.10.5 x64 install with numpy 2.2.6** on the build PC, from which `build\s03\make_pybundle.py` makes the isolated
  runtime;
- the **Osoyoos** `H2ToolHooks.dll` and `h2v_scenery_bounds.db` (MIT), and the built `gpucheck.exe` and `osoy_launch.exe`;
- the gate proof files mentioned above.

Because the engine inputs are not public, treat these scripts as documentation of how the release was made rather than as a
reproducible recipe.

## Tests

The test programs under `tests/` are source for reading and for the maintainer's machine. They need a production-like H2EK
and engine tree, and they read their locations from environment variables instead of built-in paths:

| Variable | Meaning |
|---|---|
| `H2LM_TEST_MINE_ROOT` (or `--mine DIR`) | a stock-tool H2EK root whose `GPU Lightmap Work` holds `engine_v373` (read only) |
| `H2LM_TEST_GLW` | that `GPU Lightmap Work` folder (the Python tests) |
| `H2LM_TEST_ENGINE` | that `engine_v373` folder (the DLL-slot tests) |
| `H2LM_TEST_ROOT` | the H2EK root (the launcher self-test) |
| `H2LM_TEST_STEAM_LIB`, `H2LM_TEST_LONGPATH_ROOT` | optional: a Steam library with the stock H2EK, and an H2EK whose work path is 70 characters; those tests skip when unset |

Some tests also expect the unpacked PowerShell and Python bundle at `cache\` and `build\s03\out\` (see `build/fetch_pwsh.py`
and `build/s03/make_pybundle.py`). Production files are only read; none of the tests writes live tags or starts a GPU bake.

## Public-copy notes

A few strings that look like machine paths are functional and stay on purpose:

- the 60-character work-folder string in `DllSlotPatcher.cs` and `h2lm_payload_lib.py` is the exact text stored inside the
  engine's capture DLL, which the app overwrites at run time;
- `src/standalone/sh_stage.py` refuses to stage into the author's development H2EK root (a safety guard);
- the diff context lines in `patches/` quote the original text of the files they patch, byte for byte.
