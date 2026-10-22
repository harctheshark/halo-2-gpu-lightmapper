# Halo 2 GPU Lightmapper

A GPU re-implementation of the Halo 2 (MCC H2EK) lightmapper. It reproduces the CPU `tool_fast` bake, with the Osoyoos
Plücker and scenery fixes, on the GPU in minutes instead of hours, and it is packaged as one standalone Windows exe: no
Python, PowerShell or other runtime to install.

> **Unofficial fan tool.** Halo, Halo 2 and the Halo 2 Editing Kit belong to Microsoft / 343 Industries / Bungie. This project
> is not affiliated with or endorsed by them. See [Credits and thanks](#credits-and-thanks) and
> [Transparency](#transparency-this-project-was-vibecoded).

**Release status: pre-release (0.6.5).** It is validated on one PC (an RTX 4090 with 24 GB), on the author's own H2EK and on
the unmodified tags of a Steam H2EK. It is not code-signed and has not been run on a clean Windows install. Read
[Known issues](#known-issues-fix-in-progress) and [Limits](#limits) before you bake a big map. What changed in each version is in
[CHANGELOG.md](CHANGELOG.md).

## Contents

- [Highlights](#highlights)
- [Accuracy against the CPU](#accuracy-against-the-cpu)
- [Speed](#speed)
- [Requirements](#requirements)
- [Download and first start](#download-and-first-start)
- [How to use](#how-to-use)
- [Command line](#command-line)
- [Safety: backups, restore, nothing touches your tags until install](#safety)
- [Limits](#limits)
- [Known issues (fix in progress)](#known-issues-fix-in-progress)
- [FAQ](#faq)
- [Building from this repository](#building-from-this-repository)
- [Credits and thanks](#credits-and-thanks)
- [Transparency: this project was vibecoded](#transparency-this-project-was-vibecoded)
- [License](#license)

## Highlights

- **Matches the CPU bake.** Same photon mapping, final gather, sun, sky and emissive lighting as `tool_fast`, run on the GPU.
  On four of the seven tested maps the result is at, or within, the CPU's own run-to-run noise, and on the others it is within
  about two percentage points (see the table below).
- **Minutes, not hours.** chronopolis: 7.7 minutes on the GPU against 92 minutes on the CPU.
- **One exe.** The window and a command line are the same file. It carries PowerShell 7.6.6, Python 3.10.5 with numpy and the
  GPU engine inside itself and unpacks them on first start.
- **Quality:** Low, Super (the stock CPU quality levels) and Custom (Experimental: your own photon and gather-ray counts).
- **Modes:** Lightmap Standard (lightmaps, plus light probes on a BSP that has probe points), Lightmap Lightprobes (probes
  only), Lightmap Only.
- **Any GPU with Direct3D 11.** NVIDIA and AMD cards bake at full FP64 precision; a GPU without FP64 (Intel, Qualcomm Adreno)
  switches to Compatibility mode (DF64) by itself (new in 0.6.4, experimental).
- **Hard to time out.** The default path asks the GPU for small, measured pieces of work (photons that can pause and resume, tiny
  first submissions, an immediate shrink after an overshoot), so Windows' 2-second GPU timeout is much harder to hit (new in
  0.6.5; written after a Snapdragon / Adreno laptop lost the GPU, not yet tried on one).
- **Ambient occlusion** (optional, off by default): a GPU AO pass for an extra artistic look. It is *not* CPU-faithful.
- **Capture reuse.** Skip the slow `tool_fast` capture step when you re-bake the same map.
- **Custom scenery just works.** The scenery fix needs each scenery model's compression bounds. Before every bake the app scans
  the scenery your scenario places and adds any model that is missing to `SceneryFixDependencies.db` in your H2EK folder (the
  log says so); **Advanced > Scan and Build Scenery Fix Dependencies** scans your whole tags folder on demand.
- **Safe by design.** Every install is backed up and md5-verified first; Restore and Undo are built in; a crash or power loss
  is recovered at the next start.

## Accuracy against the CPU

Each score compares the GPU lightmap with a lightmap baked by the CPU `tool_fast` (Super quality, Plücker and scenery fixes
on) for the same map. 100% would be identical.

Two numbers are given for every map:

- **Floored.** The CPU lightmapper is itself noisy: two CPU bakes of the same map differ from each other, because photon
  mapping is stochastic. The *floored* score takes that noise into account. A value **above 100%** means the GPU differs
  from the CPU reference by less than the CPU differs from itself, that is, the GPU is within the CPU's own noise.
- **Un-floored.** The raw agreement with one CPU reference bake, with no allowance for the CPU's noise. This is the strict
  number, and it cannot reach 100% because the reference itself contains noise.

| Map | Floored | Un-floored | Notes |
|---|---|---|---|
| chronopolis | 100.28% | 99.01% | Pearson 0.9997 |
| headlong | 99.99% | 98.72% | |
| earthcity_1 | 100.11% | 98.84% | |
| cyclotron | 99.96% | 98.69% | |
| needle | 98.25% | 96.98% | |
| high_10 | 98.26% | 96.99% | |
| mainmenu | 98.39% | 97.11% | |

Super quality, on the author's PC (RTX 4090). The lower rows (needle, high_10, mainmenu) are the maps with the largest
remaining gap to the CPU. Light probes: the spherical-harmonics light of the triplicate probes matches the CPU super bake at
99.53%.

Ambient occlusion is **not** part of these numbers. It is an artistic layer the CPU lightmapper does not have, so a lightmap
baked with AO no longer matches the CPU result. With AO off the app reproduces the validated lightmap. A fresh capture is not
bit-for-bit reproducible, in the CPU tool either, so two GPU bakes of the same map differ very slightly.

## Speed

| Map | GPU | CPU | Hardware |
|---|---|---|---|
| chronopolis (Super) | 7.7 min | 92 min | GPU: RTX 4090. CPU: `tool_fast` farm of 28 workers |
| headlong (Super) | about 5-6 min | 49.7 min | GPU: RTX 4090. CPU: `tool_fast` farm of 28 workers |

Other examples on the same GPU (Super): cyclotron about 4.5 minutes, mainmenu about 10 minutes. These are measurements from
one PC. Another GPU will be slower or faster; a card much slower than an RTX 4090 has not been tried (see
[Known issues](#known-issues-fix-in-progress)).

## Requirements

- **Windows 10 version 1903 or later, or Windows 11, 64-bit.** .NET Framework 4.8 is part of Windows from 1903 on. Nothing else
  has to be installed.
- **A GPU with Direct3D 11 feature level 11_0.** The app checks the GPU before every bake and shows the result in the
  **Graphics card** group:
  - **NVIDIA and AMD GPUs** have double-precision (FP64) support and bake at **full precision**.
  - **GPUs without FP64** (Intel integrated and Intel Arc graphics, Qualcomm Adreno) run in **Compatibility mode (DF64)**
    automatically: the engine uses FP32 "double-single" maths instead. It is **experimental**: it matched full precision in
    testing (on an RTX 4090 with the mode forced on), it may be slower, and it has not been run on a real Intel or Qualcomm GPU
    or on Windows on ARM.
  - On a laptop with two GPUs, the bake must run on the NVIDIA/AMD one (the app tells you how). Only an RTX 4090 with 24 GB has
    been tested; the app sizes its work to the card's video memory but this is untested below 24 GB.
- **The stock MCC Halo 2 Editing Kit (H2EK)** from Steam, with `tool_fast.exe` build 2023.06.20 (md5
  `3a889d370a7be537af47ff8035acd201`). Other builds are refused, because the GPU hooks are address-specific.
- **The H2EK's own prerequisites** (the app checks them and names any that is missing; it never installs anything): a `data`
  folder, `libfbxsdk.dll`, the x86 Visual C++ 2015-2022 runtime and the DirectX End-User Runtime (June 2010).
- An **NTFS** drive with at least 8 GB free, and about 3 GB per map for the work folders. The H2EK folder may be at the root of a
  drive (new in 0.6.2, not yet tested with a real bake there), must use plain ANSI characters, and the working paths must stay
  under 240 characters.

## Download and first start

1. Download `H2 GPU Lightmapper.exe` from the **Releases** page of this repository. **That one file is all you need**: it
   carries the engine, PowerShell, Python and the licence texts inside itself. The other release files are optional
   (`.exe.config` only names .NET Framework 4.8, which Windows 10 1903+ and Windows 11 already have; `SHA256SUMS.txt`,
   `NOTICES.txt` and `build_record.json` are for checking and reference). Check the download with
   `certutil -hashfile "H2 GPU Lightmapper.exe" SHA256` and compare it with the SHA-256 in the release notes.
2. **SmartScreen.** The exe is **not code-signed**, so Windows SmartScreen will probably show "Windows protected your PC".
   Click **More info**, then **Run anyway**. Do this only for a file whose SHA-256 matches the one published with the release.
   If Smart App Control is on, Windows may block the exe outright (not tested).
3. On the first start the app unpacks its runtimes (about 300 MB) to `%LOCALAPPDATA%\H2GpuLightmapper\rt\`. That takes a few
   seconds. Before every bake it re-checks every unpacked file and repairs any that changed.

## How to use

Start the exe. The H2EK folder and the map are remembered between launches; the bake options go back to their defaults.

1. **Halo 2 Editing Kit.** Pick a detected H2EK or **Browse...**. The status line shows whether its `tool_fast.exe` is
   supported and any missing prerequisite.
2. **Map.** Pick the **Scenario**, then the **BSP**. The BSP list shows every BSP the scenario references; one that lives in
   another folder is labelled with it, for example `01_bsp_1  (from scenarios\solo\01a_tutorial)`. Its lightmap is written beside
   the scenario. Ambient occlusion and light probes work for such a BSP too (0.6.5).
3. **Quality**
   - **Low:** quicker, lower quality.
   - **Super** (default): the high quality, the one that is validated against the CPU.
   - **Custom (Experimental):** your own photon count (1,000,000 to 100,000,000, default 50,000,000) and gather rays
     (1 to 64, default 16). **Only Super's own values (20,000,000 photons, 8 rays) are validated.** Anything else is untested,
     there is no CPU reference for it, and large values mean very long bakes (100,000,000 photons with 64 rays took about
     1.4 hours on cyclotron on an RTX 4090). The window shows an ETA and a note.

   **Graphics card** (under Quality) shows your GPU and the precision it will bake at: `Precision: Full FP64`, or
   `Precision: Compatibility mode (DF64)` on a GPU without FP64. There is nothing to choose: the app picks it.
4. **Mode**
   - **Lightmap Standard** (default): lightmaps, plus light probes if the BSP has probe points.
   - **Lightmap Lightprobes:** probes only.
   - **Lightmap Only:** lightmaps only. On a BSP with probe points this resets its probes to zero, exactly as `tool lightmaps`
     does.
5. **Ambient Occlusion** (off by default). **Strength** (default 1.0) and **Exponent** (default 0.5). This is an artistic
   layer and is *not* CPU-faithful. The author's favourite look is strength 1.25, exponent 0.5.
6. **Reuse existing capture** (off by default = make a new capture). Offered only when the map's last capture matches the
   same quality and binaries. If your tags changed since the capture, the app lists them and asks first.
7. **Advanced:** Stage only (bake without touching your tags), the work folder, **Undo last install**, **Repair
   runtime**, **Scan and Build Scenery Fix Dependencies** (adds every render model of your tags folder to
   `SceneryFixDependencies.db` and lists the models it added; sky models are skipped; bakes already do this automatically for
   the scenery of their own scenario).
   **Advanced settings** (all off by default):
   - **Keep PC responsive - Slower Bakes!** Splits the GPU work into short pieces (about 10 ms) and leaves the GPU idle between
     them (busy about 60% of the time), at low priority, so your desktop, videos and games stay smooth while it bakes. The
     lightmap is identical; the bake takes longer (about 1.5-1.8x on the tested maps). Command line: `--responsive`.
   - **2004 Exact Vertex Lighting?** Makes the per-vertex lit geometry (instanced objects lit per vertex) match `tool_fast`'s own
     write-back more closely by re-running the CPU's per-vertex visibility test while the tags are written. On the stock
     Steam cyclotron this moves the vertex-lit records from about 85% to about 98% bit-exact. The lightmap pixels do not
     change; the tag-writing step takes about 2 minutes longer. You likely don't need it. Command line:
     `--exact-vertex-lighting`.
   - **Scenery Multi Permutation Fix (Experimental Improvement).** A fallback for scenery that draws more than one shape from
     one tag, for example a rock whose shape changes when you move it, or a lifepod placed with a named variant like "crashed".
     Only the shape the game actually draws casts shadows, ground hidden inside scenery is filled from its surroundings, and a
     variant's own pieces get lit instead of rendering black. It differs from the 2004 tool on purpose. Most maps never need
     it: giving each shape its own scenery tag avoids the problem. Ambient occlusion and light probes do not take it. Command
     line: `--scenery-permutation-fix`.
8. **Bake**, **Cancel**, **Restore previous lightmap...**, **Open logs**.

**Before you bake:** close the map in Sapien and Guerilla. A bake that will install is refused while one of its lightmap files is
open in another program; the window then offers **Retry**.

The progress row shows Preflight, Back up, Capture, Derive, GPU bake, Tag writer, AO, Probe capture, Probes, Install and Verify,
with a live log. The logs of every run are in `<H2EK>\GPU Lightmap Work\standalone\runs\`.

## Command line

The same exe works from a terminal, and no window opens.

```
"H2 GPU Lightmapper.exe" --cli bake --h2ek DIR --scenario S --bsp B
      [--quality low|super|custom] [--photons N --rays M] [--mode standard|probes|lightmap]
      [--ao [--ao-strength X] [--ao-exponent Y]] [--reuse-capture] [--yes] [--no-install]
"H2 GPU Lightmapper.exe" --cli restore --h2ek DIR --scenario S --bsp B [--list | --from BACKUP | --undo-last-install]
"H2 GPU Lightmapper.exe" --cli doctor [--h2ek DIR]
"H2 GPU Lightmapper.exe" --cli help
```

Example: a Super lightmap of cyclotron with AO, written to the tags after the usual backup:

```
"H2 GPU Lightmapper.exe" --cli bake --h2ek "D:\Games\H2EK" --scenario scenarios\multi\cyclotron\cyclotron --bsp cyclotron ^
      --quality super --mode lightmap --ao --ao-strength 1.25 --ao-exponent 0.5
```

Add `--dry-run` to print every step without starting or writing anything, and `--no-install` to bake into the work folder
only. The Advanced settings are `--responsive`, `--exact-vertex-lighting` and `--scenery-permutation-fix`; `--force-compat`
runs the Compatibility mode (DF64) kernels on a GPU that has FP64 (for testing). Exit codes: 0 ok, 2 refused before anything
was written, 3 failed, 4 cancelled.

<a id="safety"></a>
## Safety

- **Nothing touches your tags until the install step.** The GPU bake works in its own work folder
  (`<H2EK>\GPU Lightmap Work\`), and only the final install writes your lightmap tags. **Stage only** never installs.
- **Backups.** Before every bake that installs, the BSP's lightmap files are copied to
  `<H2EK>\GPU Lightmap Work\_TAGBAK_ui\<scenario>_<bsp>_<time>\` and md5-verified. The install keeps a second copy and a
  progress marker.
- **Restore previous lightmap...** (window) or `--cli restore` puts the newest differing backup back. **Undo last install**
  puts back exactly what the last install replaced. Reload the map in Sapien afterwards.
- **Sapien or Guerilla holding a file:** the install is refused with nothing changed, and the window asks you to **Retry** once
  you have closed the map (CLI: `--install-retries N`). The bake is not lost.
- **Crash or power loss:** the next start finds an unfinished install and restores the files from the install's own backup.
- **Cancel** stops the bake at once, except during a tag write, which is finished and verified first.
- The app never deletes your files, never changes Windows settings, and never writes the radiance bitmaps.

**Never delete `GPU Lightmap Work`, a `farm_w...` folder or `_gpulm_skel` with a recursive delete tool** (`rmdir /s`, Windows
PowerShell 5.1 `Remove-Item -Recurse`, disk cleaners). The skeleton folders contain links (junctions) into your live `tags`
folder and some tools follow them. The app has no cleanup feature for this reason.

## Limits

How big a map can it bake? This is a summary; the full study (with sources and the numbers behind each row) is in
[docs/LIMITS.md](docs/LIMITS.md). Every count is per BSP. **HARD** is a fixed limit, **SOFT** moves with your hardware.
Measurements come from one RTX 4090 PC; anything about other hardware is extrapolated, not tested.

| Question | Short answer | Kind |
|---|---|---|
| Geometry in a BSP | Whatever `tool_fast` can import (512 clusters, 32,767 vertices and 65,535 triangles per section, 1,024 instances...). Largest tested: 98,314 traced triangles, 620 instances. | HARD (Halo 2's) |
| BSP width | No fixed limit since 0.6.2: past about 510 x R world units per axis (R = the photon search radius) the occlusion grid grows its cells instead of dropping walls, and the log says so. No map that wide has been baked yet. | SOFT |
| Lightmap texels | No fixed limit; the work is split into parts automatically. Largest tested: 10.9 M covered texels (mainmenu, 11.4 min). | SOFT |
| One chart | At Super about 1.73 M covered texels (about 1,713 x 1,713 grid); bigger is refused with a message. | HARD |
| Photons | Low 5 M, Super 20 M, Custom 1-100 M. | HARD |
| Gather rays | Per shading call: Low 72, Super 128, Custom 2 x MC^2 (MC 1-64). | HARD |
| Lights | Halo 2 allows 500 lights, 500 fixtures, 32 skies per scenario. | HARD |
| Light probes | 281 probe points per BSP at the default buffer cap. A BSP above that is refused before anything runs, with a message to use Lightmap Only. | HARD |
| Video memory | 24 GB held every tested map. The app sizes its work parts from your card's VRAM and, since 0.6.3, from what other programs are using; on an idle 24 GB card nothing changes. | SOFT, not tested below 24 GB |
| Bake time | Super: 22 s to 17.4 min on the tested maps (RTX 4090). | SOFT |
| GPU timeout (TDR) | Every long GPU step is paced by measured time and starts with a tiny submission. Since 0.6.5 the photons of the sky chain, the exact sun and the photon cast are resumable (the smallest unit is one trace, not one photon), the photon lattice is resumable (0.6.4), and the exact-sun batch size no longer reads only the dedicated memory of an integrated GPU. None of the tested bakes on the RTX 4090 printed the 1.5 s warning. A GPU much slower than an RTX 4090, or one with shared memory, has not been tried. | SOFT |

## Known issues (fix in progress)

These are open problems in this build.

1. **GPU-timeout risk (TDR), reduced much further in 0.6.5, not removed, and not tried on the PC that triggered it.** The photon cast,
   the light-probe trace, the sky chain and the gather are paced by measured time, the photon lattice is resumable (0.6.4), and 0.6.5
   makes the photons of the sky chain, the exact sun and the photon cast resumable and starts every step with a tiny submission. It was
   written after a Surface laptop (Snapdragon X2 Elite, Adreno X2-85, 2 s timeout) lost the GPU in the sky chain. **This build has not
   been run on that PC or on any GPU other than an RTX 4090**, where nothing changes in the output and the 1.5 s warning never appeared.
   A GPU much slower than a 4090, or a map much denser than the tested ones, has not been tried. If a bake fails with a GPU reset, the
   app says so in plain words; "Keep PC responsive" helps.
2. **VRAM sizing is untested below 24 GB.** The app sizes its work parts from your card's memory (and what other programs
   use), but this has only been checked by calculation, not on a 12 or 16 GB card, and the photon-lattice step is not sized by it.
   On a 4 GB card a big map may not fit.
3. **Compatibility mode (DF64) is new and experimental.** GPUs without FP64 now run the engine's FP32 kernels instead of being
   refused. They matched full precision in testing, but only on an RTX 4090 with the mode forced on: **no real Intel or
   Qualcomm GPU and no Windows on ARM PC has run it**, and it may be slower. Please report what you see.
4. **Light probes need 281 points or fewer per BSP.** A BSP with more is refused up front (use Lightmap Only).
5. **Drive-root H2EK folders** (for example `D:\`) are accepted but no real bake has been run from one yet.
6. **Per-vertex lighting differs a little from `tool_fast`'s own write-back by default.** About 14-15% of the vertex-lit
   records include samples that `tool_fast` drops (its visibility test is not reproduced by default). The lightmap bitmaps are
   identical. **2004 Exact Vertex Lighting?** (Advanced settings) closes most of the gap; a faster fix inside the GPU bake is
   planned.
7. **A rare final-check refusal.** Standard bakes of triplicate sometimes stop at the last check because a lightmap UV (1 or 2 of
   35,572) lands exactly on a texel corner that the bake did not cover (`set.d.uv_on_alpha255`). Nothing is installed. It depends on the
   capture: in 0.6.5's tests two of three Standard runs hit it and one fresh capture passed; bake again with a new capture if you hit it.

Maps with several cubemap skies (for example 07a_highcharity high_0) are accepted since 0.6.5; that bake was not compared with a CPU bake.

Other limits: Custom quality and the Scenery Multi Permutation Fix are Experimental; ambient occlusion is not CPU-faithful; the
app has not been tested on a clean Windows install, with Smart App Control, or on a second PC or GPU.

## FAQ

**Does it change my tags?** Only when you bake with install on (the default) and the install step succeeds. Before that, the
app backs up the BSP's lightmap files, and **Restore previous lightmap...** puts them back. With **Stage only** (or
`--no-install`) your tags are never written.

**How close to the CPU is it?** On the tested maps the lightmap agrees with the CPU `tool_fast` bake at 97.0-99.0% un-floored,
which is 98.3-100.3% once the CPU's own run-to-run noise is allowed for (see [Accuracy](#accuracy-against-the-cpu)). On
chronopolis the per-texel correlation is 0.9997.

**Does AO change accuracy?** Yes, on purpose. AO darkens occluded corners using a GPU ray trace, something the CPU lightmapper
never does, so an AO lightmap no longer matches the CPU result. Leave AO off if you want the CPU-faithful lightmap.

**Why does Windows warn me?** The exe is unsigned. See [Download and first start](#download-and-first-start).

**Does it work with Halo 2 Vista?** No. It targets the MCC Halo 2 H2EK `tool_fast` build named above.

## Building from this repository

This repository holds the source of the app (the WPF shell and command line), the build tooling and the tests. It does **not**
hold the GPU engine: the engine binaries, kernels and capture DLL are shipped inside the release exe, and their source is not
part of this snapshot. See [docs/BUILDING.md](docs/BUILDING.md) for what is needed and what is missing, and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit.

## Credits and thanks

- **Bungie, 343 Industries and Microsoft** for Halo 2 and the Halo 2 Editing Kit. Halo is a trademark of Microsoft. This is an
  unofficial fan tool and is not affiliated with them.
- **The Osoyoos Launcher project and its authors** (num0005 and the Osoyoos Launcher contributors) for the Plücker and
  scenery fixes and the `H2ToolHooks` hook DLL, which this tool ships under their MIT license (and the scenery-bounds database
  that goes with it). Their license is reproduced in [THIRD_PARTY_NOTICES](docs/THIRD_PARTY_NOTICES.txt).
- **Unreal Engine Lightmass** (Epic Games) for the ambient-occlusion formula that the AO layer follows.
- **PowerShell** (Microsoft, MIT), **CPython** (Python Software Foundation) and **numpy** (the NumPy developers), which are
  bundled inside the exe under their own licenses.

## Transparency: this project was vibecoded

This project was built with heavy AI assistance: Anthropic's Claude, used through Claude Code, including AI agents for
research, reverse engineering and testing. The author directed the work, tested it in game, judged the results, and made the
decisions about what to accept. It has been in the works since April 2026. The code and the numbers in this README were
checked against real bakes, but treat it as what it is: a fast-moving hobby tool with the known issues listed above.

## License

The code in this repository is released under the **MIT license** (see [LICENSE](LICENSE)), Copyright (c) 2026 Harc
(harctheshark). Third-party components (PowerShell, CPython, numpy, the Osoyoos hook DLL and database, and others) keep their
own licenses; see [THIRD_PARTY_NOTICES](docs/THIRD_PARTY_NOTICES.txt).
