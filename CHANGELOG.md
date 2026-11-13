# Changelog

## 1.0.0 (2026-11-12)

Exe SHA-256 `9bd5833071236017ee8a9bcfd0d4e3b65767fe9974a30b7af5099ca7a8d41cfe`.

- **The exe is about 26 MB instead of 167.6 MB.** It is 26,428,928 bytes (0.6.12: 167,629,824 bytes). There are two reasons: the files embedded in the exe are stored more compactly, and the program no longer contains a PowerShell runtime. The embedded payload is 25,621,466 bytes (0.6.12: 166,838,324 bytes); the PowerShell runtime alone was a 105,675,041-byte part of it.
- **No PowerShell anywhere.**
  - Until 0.6.12 the bake was driven by a PowerShell script (`gpu_bake.ps1`) that ran on a bundled PowerShell 7 runtime. It is now driven by a small native program, `h2lm-bake.exe`, in the engine folder (the AO engine folder has its own copy). It takes the same steps in the same order, with the same arguments.
  - The app no longer starts, needs or checks any PowerShell, including the one that comes with Windows. The "PowerShell policy" pre-flight check is gone, and so is the developer option `--rt-pwsh`.
  - Results are unchanged. On the release's test maps the baked lightmap tags are byte-identical to 0.6.12's: Standard, Custom, AO, light probes and the `.map` path.
  - The GPU programs are the same files as in 0.6.12 (`fullgpu.exe` md5 `f04032ae`, the AO engine's `d1ac121c`).
- **Fixed: Vista `.map` bakes could lose sounds.** When a Vista map was baked and converted back to a Vista map, a small number of sounds could be missing from the result on a PC that had never run the standalone map converter. The bundled runtime lacked a resampling library that the conversion needed for those sounds, and the bake still reported success. The conversion now resamples those sounds itself, with the same samples as before, and a result that lost any sound is refused instead of being handed over.
- **Fixed: a level with an empty instanced-geometry object could not be finished by the direct tag writer.** An object whose name starts with `%` is imported as instanced geometry; when it holds only `+portal` faces (or another non-rendered material) the importer keeps an instance with no geometry. On a level that also has lit scenery the tag writer took that instance for a per-vertex-lit one and refused the level ("vertex_out.bin absent ... 1 vertex-lit instance bucket refs"), so ambient occlusion and light probes could not run. Such an instance now takes no lightmap records, exactly as in the tool's own save: on the test level the direct writer's lightmap pixels, palettes and scenery vertex colours equal the tool's own, byte for byte. The tag writer files `lm_tagwriter.py`, `lmw_vertex.py` and `lmw_validate.py` changed in both engines; the light-probe gate was run again for them.
- **Fixed: the tool write-back fallback never started (0.6.8 to 0.6.12).** When the direct tag writer refuses a level, the bake falls back to letting the tool save the lightmap. Since temporary files moved to their own folder (0.6.8) that step waited for the tool's log in the wrong folder and stopped after 90 seconds with "the early-injected DLL never logged '[GPULM v373M] init complete ...'", on every PC. It works again. The direct writer handles nearly every level, so the fallback was rarely reached.
- **New warning: an instanced-geometry object with no visible geometry.** The window shows it in yellow under the BSP picker and the bake log repeats it with the object's name, for example `"%portals" has no visible geometry`. The usual cause is portal faces placed in a `%` object: they are dropped at import and do not work as portals. The level still bakes. To make the portals work, move those faces into the level mesh (an object without the `%` prefix) and import the level again. A scenario bake in Lightmap Only mode does not run the map query and so does not log the warning; the window still shows it.
- **Known and not fixed:** (1) a level with an instanced-geometry instance placed exactly at the world origin (scale 1, position 0) makes the object-grid rebuild refuse ("build_objgrids.py REFUSED ... root element count"); the bake continues with the older instanced model for the sky lights and lists it under "NOT THE VALIDATED CONFIGURATION" (accuracy only). (2) On a graphics card with about 4 GB the part planner can stop with "a single row band of chart 0 ... past the per-part limit (video memory)" and advise adding portals or splitting the BSP; there the advice is wrong, because the fixed memory the bake needs is larger than what the planner allows itself on that card (the card minus a 1.5 GiB reserve). Cards under 6 GB remain untested.
- **First start of a new version.** The embedded files are unpacked once, which takes a few seconds longer than before because they are stored more compactly. Later starts are not affected. An install of 0.6.12 is reused where the files are the same (the Python runtime).
- **Going back to 0.6.12.** On a PC where 1.0.0 was the first version to unpack the Python runtime, 0.6.12 refuses that folder ("incomplete or foreign dir in the way"), although the files are the same. Close the app, delete `%LOCALAPPDATA%\H2GpuLightmapper\rt\py310-np226-d55e6aae` and start 0.6.12 again: it unpacks the folder itself, and 1.0.0 accepts it afterwards. A PC that ran 0.6.12 before 1.0.0 is not affected.
- **The old PowerShell runtime folder can be removed.** Earlier versions left a PowerShell runtime folder (about 257 MB) in the program's `rt` folder under `%LOCALAPPDATA%\H2GpuLightmapper\`. It is no longer used. The program's own clean-up offers to remove it: **Advanced settings > Free space from earlier versions** in the window, or `--cli cleanup --h2ek DIR --legacy` (add `--dry-run` to list only).
- **Still bundled:** the Python 3.10.5 runtime with numpy 2.2.6. The map converter and several bake steps use it.
- **Third-party notices.** The PowerShell notices are gone. NOTICES.txt now lists five components: CPython, NumPy, the Microsoft Visual C++ runtime DLLs, the Osoyoos launcher (MIT) and h2conv (GPL-3.0-or-later; it was section 6 and is section 5 now), and says that the engine, including `h2lm-bake.exe`, and the shell are the project's own code.
- Nothing else changed: the same options, quality levels, command line (except `--rt-pwsh`), work folders and GPU programs.
- **Not tested:** everything listed as not tested under 0.6.12 is still not tested (real 8 GB or 6 GB cards, GPUs other than one RTX 4090, extra direct light samples together with a bake that is cut into parts).

## 0.6.12 (2026-11-10)

Exe SHA-256 `7f92279896eadb739b7236003e557daabc4c334b36ad72533aa7240840f87588`.

- **Maps with thousands of surface lights and a large search distance bake in minutes instead of hours.** A lightmap tag with a large search distance (lower / upper bound, for example 4 / 10) on a map with thousands of surface lights sent 0.6.11 down its slowest surface-light path. Test map (3,215 surface lights, 39 sun and sky lights, search distance 4 / 10, Super, RTX 4090): a whole bake through the app with AO was cancelled after about 2 hours on 0.6.11 and takes about 9 minutes now (6.0 minutes of it in the engine). Per step, 0.6.11 -> 0.6.12: sun and sky lights 434 s -> about 45 s; surface lights hours -> about 35 s; bounce light 117 s -> about 7 s; per-vertex light direction 31 s -> 4 s.
  - Surface lights: the list of candidate lights per texel has no upper limit any more (it was 512, then the slow path). The lists are built in windows and give exactly the same pairs in the same order.
  - Sun, sky and surface lights: shadow rays search each object's triangles through a tree instead of scanning them in blocks, and each sun or sky light only dispatches the texels it can reach.
  - Ray tracing on large maps: on a map whose sample table is larger than 32 MiB the ray-tracing path read part of it wrong, failed its own self-check and fell back to the slower tracer. Fixed; the self-check passes on that map now.
  - Per-vertex light direction: the builder is a native program now. Its output file is byte-identical to the Python one, which remains as the fallback.
- **New: extra direct light samples (opt-in, Custom quality).** Grain in direct light from wide sky lights and from surface lights comes from the number of random rays the original tool uses per texel, which this app copies. With **Advanced settings > Extra direct light samples** ticked, a box **Direct light samples ×** (1 to 64, default 4) appears under Custom quality; command line `--direct-samples K` with `--quality custom`.
  - Every sun, sky-light and surface-light pass runs K times with different random numbers; the average is stored. Grain falls with the square root of K (×4 halves it, ×16 quarters it). On the test map above ×4 took the direct-light grain from 6.3 % to 3.2 % and the engine time from 309 s to 479 s; only the direct-light passes repeat. The brightness does not change (measured mean difference 0.005 %).
  - Off (the default) and ×1 run exactly the previous program: the same tags, bit for bit.
  - It is not the original tool's noise pattern, which is why it is opt-in. It does not affect bounce light; for grain in bounce light raise the Custom gather rays.
- **`.map` bakes of Halo 2 Vista maps set up in seconds.** The setup step copies only the tags the map uses. That already worked for maps built with the MCC editing kit; a Vista map was not recognised and got a copy of the whole kit. Test map: 28,355 files / 9.8 GB -> 9,172 files / 0.76 GB, 156 s -> 5 to 7 s. Very long tag paths that the map does not use no longer stop the bake.
- A sky whose cube map reference is empty in your editing kit stays empty in a `.map` bake (the app used to fill in a guess from a sky with the same model).
- **Live progress and time left.** The window shows the running step, a progress bar that moves during the long GPU steps (sky lights, bounce light, suns, surface lights, compose) and a time-left estimate per step. The estimate uses the times of earlier bakes of the same map and settings on this PC; a first bake gets a rough estimate.
- **Shorter work folder names.** `GPU Lightmap Work` is now `GPU Work`, and `Temp (safe to delete)` is now `Temp`. An existing folder is renamed on the first start (not while a bake is running; a dry run never moves anything). Shorter paths leave more room under the Windows path limit. A Windows Security exclusion or a script that names the old folder has to be updated.
- **Lightmaps against 0.6.11, with the new option off.** Cyclotron and Headlong: lightmap tag and bitmaps identical. Triplicate: 3 of 1,614,296 texels differ, by at most 0.0000025; the bitmaps are identical. Main menu: the truecolor lightmap differs in 3 of 92 million channel values, each by one level of 255; the paletted lightmap gets a freshly trained palette, as after any change of input, and on lit texels differs by 0.11 of a level on average (at most 13), the size of the palette's own rounding (0.07 to 0.09 on average). The causes are last-digit rounding in the engine's numbers and, on Triplicate, the new shadow-ray search no longer accepting a rare stray hit that the old block scan let through.
- Checked: Custom quality (identical to the normal engine on three maps), light probes, AO and `.map` bakes through the app. A pretend 6 GB and 8 GB card, 47 forced parts and a forced cut on Cyclotron equal the plain bake; the many-surface-light test map on a pretend 8 GB card is cut into 2 parts and equals the single-part bake. Extra direct light samples through the app: ×4 runs with 4 and changes the result, ×1 equals the plain Custom bake.
- **Not tested:** real 8 GB or 6 GB cards; GPUs other than one RTX 4090; extra direct light samples together with a bake that is cut into parts.

## 0.6.11 (2026-11-06)

Exe SHA-256 `10fc5ae48fb53c1ff41e4803500ea51710c5f0c48832b005992585232f0a32b9`.

- **The light direction stored per vertex is much closer to the original tool.** This is the direction that drives bump mapping and the shading of lit objects. Average agreement with the original CPU tool's direction for the same vertex (cosine, 1.0 = identical), 0.6.10 -> 0.6.11: High Charity lightmapped objects 0.90 -> 0.96, scenery 0.69 -> 0.81, level geometry (clusters) 0.83 -> 0.90; Cairo Station lightmapped objects 0.93 -> 0.97, clusters 0.89 -> 0.90; Cyclotron lightmapped objects 0.93 -> 0.98. The lightmap colours are unchanged: the lightmap bitmaps and the accuracy score are identical to 0.6.10 on every map tested.
  - Surface lights (glowing panels, lit decorations) now give the direction of the brightest single light, as the original tool does (this was the cause of the poor High Charity scenery direction).
  - A lightmapped object's vertex is matched only to a direction sample on its own surface, as the original tool does. The previous fallback to any surface at the same point mixed in perpendicular faces.
  - The per-vertex direction is built from the samples the bake itself used (after its occlusion checks), including the samples on chart borders; the engine records them in a new per-sample file and the builder reads them. The result no longer depends on the order the GPU wrote them in, so repeated bakes give the same file.
- **Maps with many sky lights bake much faster.** The sky pass now plans its work in smaller steps. High Charity at Super on an RTX 4090: 25 minutes -> about 6 minutes (about 4 to 5 times faster), with the same result. Other maps are unchanged or slightly quicker.
- The first (gate) pass submits its work to the GPU in smaller pieces (at most about 250 ms each). The result does not change.
- The engine counts the extra video memory of the new surface-light pass (up to 256 MiB on big maps) when it decides how to cut the main pass into parts. A pretend 8 GB card on High Charity (two parts) gives exactly the same lightmap as the single-part bake.
- Custom quality (identical to the normal engine on three maps), light probes (probe data identical to earlier releases), AO and `.map` bakes were run again and work as before.
- The new per-sample file is removed with the other bake products after every bake, so a kept capture does not grow.
- **Not tested on real hardware other than one RTX 4090**, including real 8 GB and 6 GB cards.

## 0.6.10 (2026-11-04)

Exe SHA-256 `c5bb627f6678e8445a2ee5c2cf861a4773e1d216c0151e5b76583a09c1a2e9a2`.

- **Bakes now fit graphics cards with 8 GB (and 6 GB) of video memory.** The photon-lattice pass, the first step of a bake, used to need 6-14 GB on its own. It now runs on a small slice of the map and needs about 2-3.5 GiB at Super, with exactly the same result.
- **The main pass is cut into more parts when the card needs it.** The part sizing now counts all of a part's memory, not just its shading rows. More, smaller parts give the same lightmap; the bake only takes a little longer.
- **1.5 GiB is kept free** for Windows, your desktop and a browser (it was 1 GiB). On a 24 GB card nothing changes: the lightmap is byte-identical to 0.6.9.
- **Measured with a pretend card** (the engine pretended to have 8 GB or 6 GB on an RTX 4090 and refused any allocation past the card minus 1.5 GiB; Super quality; every lightmap identical to the 24 GB bake). Most video memory used by the whole bake:

  | Map | 8 GB card (6.5 GiB usable) | 6 GB card (4.5 GiB usable) |
  |---|---|---|
  | cyclotron | 4.7 GiB | 3.6 GiB |
  | headlong | 5.2 GiB | 3.8 GiB |
  | mainmenu | 5.7 GiB | 3.9 GiB |
  | earthcity_1 | 5.0 GiB | 3.3 GiB |

- **Custom quality:** on the 8 GB pretend card, up to the maximum of 100 million photons fits on cyclotron and headlong. A Custom photon count the card cannot hold is now refused before anything starts, with the number of photons that fits. Photons are never lowered silently.
- **Fixed:** after a bake that failed for another reason, the app could wrongly say that Windows Security had removed `fullgpu.exe` from a worker folder. That folder was left from an earlier bake for "Reuse existing capture", and the app's own cleanup had removed the file. Only the worker folders the bake actually ran in are checked now.
- **Not tested on a real small card.** Everything above was measured on a 24 GB card pretending to be smaller. A real 8 GB or 6 GB card also has driver overhead and other programs, which is what the 1.5 GiB reserve is for. Still needing more memory: a very large lightmap chart that cannot be split, and Custom quality above what the card holds. Cards under 6 GB are untested.

## 0.6.9 (2026-11-03)

Exe SHA-256 `8fb0a33217bcee8aaafcbbab4b239dc7de81f6c12f2f997f6c311d6fef260af3`.

This is everything since 0.6.6, the last published release. 0.6.7 and 0.6.8 were never published on their own; their entries are below.

- **Ray-tracing mode, automatic.** On GPUs with RT cores the final-gather rays run on them. Every bake checks the GPU and times a short self-check, and uses RT only when it is at least 1.5 times faster and agrees with the normal tracer; otherwise it bakes the old way, so a bake never fails because RT is missing. Scores against the CPU tool moved by 0.0001 points or less (cyclotron 99.9567 with RT, 99.9566 without).
- **Faster:** whole bakes, Super, fresh capture, RTX 4090: cyclotron 230 s to 134 s, earthcity_1 591 s to 302-343 s. The skeleton step (22 s instead of 61 s on cyclotron) and the tag writer (7 s instead of 33 s) are faster with the same output.
- **Repeatable bakes.** Re-baking an unchanged map gives the same lightmap. Every map's lightmap changes once with this update, by about the size of the old run-to-run noise.
- **Cards with less video memory are sized automatically** (parts and largest buffers). Nothing changes on a 24 GB card.
- **More than 281 light probes works** (up to 1,024, Halo 2's limit), in batches.
- **A giant lightmap chart is split exactly instead of refused**; maps with no portals no longer stop with "BUFCAP: FATAL". The result is identical to the unsplit one wherever both can run.
- **`.map` bakes copy only the tags the map needs** (about 4 GB of free disk space instead of about 14 GB for headlong; the window says about 8 GB).
- **Windows Security (Defender):** if it removes one of the program's engine files, the bake stops before writing anything and names the file; restore it from Protection history. See the README.
- Updated bundled map converter (h2conv).
- Known limits: RT tested only on an RTX 4090; the photon lattice pass is not sized to video memory; small-VRAM cards and maps that really need the chart split were tested only by simulation.

## 0.6.8 (never published on its own; part of 0.6.9)

- **AO only mode** (`--mode ao`): adds ambient occlusion to the lightmap that is already in the tags, without baking the lighting again. It refuses, writing nothing, when the lightmap does not match the geometry or already has AO.
- **Bounded disk use:** temporary files live in `GPU Lightmap Work\Temp (safe to delete)\`, which can be deleted at any time; old captures and backups are trimmed automatically; `--cli cleanup`; Advanced settings has Open, Clean now and "Free space from earlier versions".
- Multi-sun and surface-light maps use the GPU's exact photon cast (no CPU cast needed).
- Optional re-bake speedup (`--rebake-hint`).
- `.map` bakes on a very long H2EK path stop with a clear message instead of finishing with missing textures.
- The tool loader is now `H2GpuLightmapper.ToolLoader.exe` with file details.

## 0.6.7 (2026-10-27): never published on its own; part of 0.6.9

Exe SHA-256 `38f7d49c3c852f14c0804e3d7cfa1cb39240775155a488580542a7f57f147ea2`.

- **Bake straight from a compiled `.map`.** Window: Map: .map file; CLI `--map FILE.map [--map-out] [--bsp] [--vista-out yes|no] [--sky-cubemap SKY=BITMAP|none|auto]`. MCC maps built with the Editing Kit and Halo 2 Vista maps; AO and light probes work; a dropdown per sky whose cube-map reference the compile lost. The input is never modified; the result is a copy with only the lightmap and its bitmap replaced. Stock compressed maps are refused. The app reads the map, bakes, and writes a lightmapped copy; temporary work files are cleaned up after the run.
- **Bundled converter (h2conv)** that reads and writes the map: GPL-3.0-or-later, a separate program, https://github.com/harctheshark/Halo2-Map-Converter; shipped as unmodified source in the runtime, not in this repository. NOTICES section 6.
- **Sapien reload signal** after install, restore and undo.
- **Engine:** multi-sun maps get their sun photons from the CPU's own cast (box level 98.07% -> 98.31%); `lightmap_cull` / `lightmap_cull_pvs` marker maps partly ported (see README); `{` Ignored-by-Lightmapper fix in exact vertex lighting.

## 0.6.6 (2026-10-22): pre-release

Exe SHA-256 `60c33ed73398d62c422cb00f72d35b93667f1434f848957c6b8826d1c8a12e9d`.

- **Sapien re-saves no longer stop a bake.** The capture guard now compares only what the lightmap depends on (the bitmaps and render models the BSP uses), and `--reuse-capture` is content-aware.
- **Triplicate corner texels are filled** from covered neighbours with a warning, instead of the `set.d.uv_on_alpha255` refusal (a large count is still refused).
- **Safety fix:** a refused stage can no longer be marked done or installed. In 0.6.4 / 0.6.5 a Lightmap Only run with a failed final check still ended DONE, and install could run without probes. Re-bake such runs.
- Checked on an RTX 4090, stage only: light-probe gate re-run (SH 99.533%), triplicate Standard and Lightmap Only on fresh captures, a small test map with AO + exact vertex lighting. Not re-run: AO neutrality and the Custom guard gate (their pinned engine files did not change).

## 0.6.5 (2026-10-22): pre-release

Exe SHA-256 `ecaa6e5236b9de6d5ab056f45a752b46bca331dc8c912c3530fe9c6bde3c1ef1`.

- **The GPU timeout (TDR) is much harder to hit.**
  - A tester's Surface laptop (Snapdragon X2 Elite, Adreno X2-85, shared memory, Windows' 2 s timeout) lost the GPU in the sky-chain step. The
    smallest piece of work the engine could submit was one draw of every "walker" of a batch, and a walker's draw is a whole photon, so a pass
    took as long as the slowest single photon.
  - Photons of the sky chain, the exact sun and the photon cast can now pause and resume (the smallest unit is one trace). Every step of the
    default path starts with a tiny first submission, is timed with a high-resolution clock, grows only while it is far under its target, and
    shrinks at once after an overshoot. The one-time shader compile is taken by a warm-up dispatch.
  - Integrated / shared-memory GPUs: the exact-sun step sized its batches from the dedicated video memory only (tiny on such GPUs); it now uses a
    quarter of the local memory budget when dedicated memory is under 1 GiB.
  - Scheduling only: the results on the RTX 4090 are unchanged (guard, AO and probe gates re-run; identical (matched by name) bakes), and the 1.5 s
    warning never appeared. **Not yet tried on the Surface / Snapdragon / Adreno PC or any slow GPU.**
- **Maps with more than one cubemap sky are accepted** (for example `07a_highcharity` `high_0`): each sky triangle samples its own sky's cube.
  The bake ran through the exe; it was not compared with a CPU bake.
- **Ambient occlusion and light probes now work for a BSP in another folder** (`01b_spacestation` / `01_bsp_1`). The AO driver finds the BSP
  file the way the bake does; the probe pipeline never needed it. Checked through the exe (Standard + AO + 2 probe points, stage only).
- Known: two of three Standard bakes of triplicate in the tests stopped at the known final check `set.d.uv_on_alpha255` (a fresh capture passed).

## 0.6.4 (2026-10-21): pre-release

Exe SHA-256 `34f0e1a144efc157784cc86290696bb0b3b701d4a9304bbe6a606e74923d0a1b`.

- **Compatibility mode (DF64) for GPUs without FP64.**
  - Intel integrated / Arc and Qualcomm Adreno GPUs have no double-precision (FP64) support, and 0.6.3 refused them. The engine now
    has a second kernel set that uses only FP32 ("double-single" maths) and picks it **automatically** on such a GPU. There is
    nothing to switch on; NVIDIA and AMD cards keep full FP64.
  - The window has a new **Graphics card** group (under Quality) that shows the GPU and the precision: `Precision: Full FP64`, or
    `Precision: Compatibility mode (DF64) ...`. The same line is in the log, `--dry-run` and the run summary.
  - **Experimental.** On an RTX 4090 with the mode forced on, real maps came out identical to full precision (a small test map,
    earthcity_1, an AO trace of cyclotron). It has not run on a real Intel or Qualcomm GPU or on Windows on ARM.
  - `--force-compat` (command line, for testing) runs those kernels on a GPU that has FP64.
- **Scenery Multi Permutation Fix (Experimental Improvement)** (Advanced settings, off by default, `--scenery-permutation-fix`).
  - For scenery that draws more than one shape from one tag (a rock whose shape changes when you move it, a lifepod placed with
    a named variant like "crashed"). Only the shape the game actually draws casts shadows, ground buried inside scenery is
    filled from its surroundings, and a variant's own pieces get lit instead of rendering black.
  - It differs from the 2004 tool on purpose. Most maps never need it: giving each shape its own scenery tag avoids the problem.
  - Off is byte-identical to 0.6.3. AO and light probes do not take it (the plan says so).
- **GPU timeout (TDR) risk reduced again: the photon-lattice step is resumable.**
  - The one GPU step that had a unit it could not split (about 1.7-2 s per submission on a small test map even on an RTX 4090, against
    Windows' 2 s limit) now stops after a work budget sized from the measured time and resumes. Bit-identical results.
  - It is also much faster: on the RTX 4090 the small test map's lattice went from 16.8 s to about 4 s and cyclotron's from 32.6 s to
    about 6-7 s.
- **Keep PC responsive is v2.** About 10 ms GPU pieces, each waited for, with an idle gap after each so the GPU is busy about 60%
  of the time, plus lowest GPU and below-normal CPU priority (0.6.3: 40 ms pieces back to back). The lightmap is identical; bakes
  take longer (about 1.5-1.8x on the tested maps).
- **A BSP from another folder can be baked.** `01b_spacestation` uses `01_bsp_1` from `scenarios\solo\01a_tutorial`; the BSP list
  now shows every BSP the scenario references, labelled with its folder. The lightmap is written beside the scenario. Lightmap
  only for now (AO and light probes are refused for such a BSP).
- **A fallback to the tool write-back is no longer silent:** the log and the run summary show a yellow warning with the reason.
- **Scan and Build Scenery Fix Dependencies** lists the added models by name and skips sky models.
- **Sapien's scratch files** (`temp_scenario`, auto-backups) are no longer listed as maps or read as the bake touching your tags.
- The 0.6.3 AO stage failure with "2004 Exact Vertex Lighting?" on is fixed (checked end to end on a small test map).

## 0.6.3 (2026-10-20): pre-release

Exe SHA-256 `81264a3c166c3aea392bf57d65db2643993bbc8c18df4c8c4d2687fce3fddcd5`.

- **Keep PC responsive - Slower Bakes!** (Advanced settings, off by default, `--responsive`).
  - Every GPU submission is kept to about 40 ms, so the desktop, videos and games stay smooth during a bake.
  - The lightmap is identical (identical (matched by name) on the tested maps).
  - Bakes take about 15% longer.
- **2004 Exact Vertex Lighting?** (Advanced settings, off by default, `--exact-vertex-lighting`).
  - The tag writer re-runs `tool_fast`'s per-vertex visibility test (7 probes per sample) and drops the samples `tool_fast`
    drops.
  - On the stock Steam cyclotron, the vertex-lit records go from 84.6% to 98.1% (colour) and from 90.2% to 95.5%
    (direction), bit-exact against `tool_fast`'s own write-back.
  - Lightmap pixels are unchanged. The tag-writing step takes about 2 minutes longer.
  - With the box off, the output is byte-identical to 0.6.2.
- **GPU timeout (TDR) risk reduced again.**
  - The photon cast and the light-probe trace are now paced by measured time. Every piece is timed, and a slow GPU gets
    smaller pieces while a fast GPU keeps its plan.
  - The cast was the riskiest step found by a per-stage survey: up to 0.85-1.47 s per submission on an RTX 4090.
  - Gather pacing grows by at most 1.25x per step.
  - Results are identical on every tested map.
  - A per-dispatch survey (`GPULM_DISPATCH_SURVEY=1`) is built in.
  - **Not fixed:** one unit of the photon lattice is not yet splittable (about 1.7-2 s on a small test map on an RTX 4090). The log
    warns above 1.5 s.
- **Clearer GPU-reset messages.** The engine names the reason for a device loss in plain words and says which step and chunk
  died. The app explains a Windows GPU reset and suggests the responsive option.
- **Video memory in use by other programs** (a game, a browser) is taken into account when the work is sized. On an idle
  24 GB card nothing changes.
- **Shorter run-folder paths:** `<time>_<label up to 20 chars>\tagbak`. A long scenario name that was refused at 243
  characters now fits. Older run folders still work with Undo and Restore.
- **GPUs without FP64 are refused in plain words** and are not supported. This covers Intel integrated and Arc graphics and
  Qualcomm Adreno.
- **Fixed:** with AO and exact vertex lighting both on, the AO layer's final tag stage did not pass the option through.
- **Checked on this build:**
  - AO neutrality (identical (matched by name) to production).
  - Custom on cyclotron (identical (matched by name)).
  - Light probes on triplicate (99.533%, unchanged).
  - An exe bake with both new options on.
  - All test suites.

## 0.6.2 (2026-10-20): pre-release

Exe SHA-256 `efc7dc4fa5ccdbb174adcdc2aed14e5f6bda78db8fe27bb4bb8384b9c54b4920`. It fixes the issues listed in 0.6.1's README.

- **Scenery fix: custom models and correct sizes.**
  - The scenery fix hook (v5) looks up each scenery model's compression bounds by its **tag name**, in the new
    `SceneryFixDependencies.db` in your H2EK folder.
    - Before every bake, the app scans the scenery the scenario places and adds any model that is missing. The log names each
      one: "... wasn't in the Scenery Fix Dependencies database -- added automatically, continuing on!".
    - **Advanced > Scan and Build Scenery Fix Dependencies** scans the whole tags folder on demand.
    - The old file is kept up to date for older launchers.
  - **Fixed:** scenery whose data sat above 1 GB in `tool_fast`'s memory was silently left compressed. Its shadows and its
    per-vertex lighting were wrong (found on a custom lifepod model). The vertex lighting of such scenery now matches the CPU
    exactly.
  - **Fixed:** about 40 stock scenery models shared a lookup entry with a different model and were decompressed at the wrong size.
  - **Fixed:** 3-vertex marker sections that are not compressed were "decompressed" with an unrelated model's bounds.
- **GPU timeout (TDR) risk reduced.**
  - The photon-lattice step is split into smaller GPU submissions with identical results. On cyclotron (RTX 4090) the worst one
    went from 1.45 s to 0.67 s.
  - The sky pass now starts small.
  - Not eliminated: one step cannot be split further.
- **Wide BSPs:** no more silent light leaks past about 510 x the photon radius. The occlusion grid grows its cells instead.
- **VRAM-aware work parts:** the app sizes its parts from the card's memory. Nothing changes on 24 GB; smaller cards are untested.
- **Light probes:** more than 281 probe points per BSP is refused up front instead of after the whole bake. Maps with lit
  scenery no longer fail the probe step.
- **Maps lit only by emissive surfaces** (no sun, no sky) capture correctly. The capture DLL is injected before the tool starts.
- **H2EK at a drive root** (for example `D:\`) is accepted. No real bake has been run from one yet.
- **Checked on this build:**
  - Production identity on cyclotron.
  - AO neutrality and the exe-vs-hand bake.
  - Light probes on triplicate (99.533%, unchanged).
  - Custom on cyclotron.
  - An end-to-end exe bake of a map with lit custom scenery.
  - A post-release-build bake (lightmap 99.82% vs the CPU, vertex lighting points identical).

## 0.6.1 (2026-10-15): pre-release

First public release. Changes since the internal 0.6.0 build:

- **Custom quality is shown in the window, labelled Experimental.** Pick your own photon count (1,000,000-100,000,000, default
  50,000,000) and gather rays (1-64, default 16). A note explains that only Super's own values (20,000,000 photons, 8 rays) are
  validated and that large values mean very long bakes. The ETA turns amber above one hour and red at eight hours or more.
  Custom runs a guarded copy of the GPU host that refuses a photon count its 32-bit arithmetic cannot hold. No development
  switch is needed any more (`--dev-custom` is still accepted and does nothing).
- **No personal build paths in the exe.** The compiler path map keeps the build machine's folders out of the exe, and the
  Python bundle records the user site-packages path generically. The build is still reproducible byte for byte.
- **Documentation** corrected: known issue 5 (stock-cyclotron per-vertex records) says what was measured and that the cause is
  unknown; the limits, VRAM and light-probe statements agree everywhere.
- **Checked on this exe:** the production-capture identity run on cyclotron (equal to the validated bake), Custom at Super's
  values (equal to Super), light probes on triplicate (99.533% against a 99.53% target), cancel during the GPU bake (exit 4,
  nothing left running), and Retry after a held lightmap file (installed, then undone).

## 0.6.0 (2026-10-15): internal

- The direct tag writer reads the decorator data of a stock Steam H2EK's original scenarios, so all 73 scenario files of that
  H2EK work (0.5.0 hit 16 scenarios).
- Every install, including Lightmap Only and Standard, goes through the app's own installer with **Retry** when Sapien or
  Guerilla holds a file.
- Ambient occlusion shoots 1,024 rays per texel and the seam filter never blends texels with a surface stacked underneath
  (no more blotches on ramps). The seam filter runs on numpy alone, so scipy is no longer bundled.
- The launcher patches of the engine were folded into the production engine scripts.
- README: new Limits and Known issues sections.

## 0.5.0 (2026-10-14): first internal build

- One exe (WPF window plus the same-exe `--cli`) that bundles PowerShell 7.6.6, Python 3.10.5 with numpy 2.2.6 and the GPU
  engine.
- Quality Low and Super; modes Lightmap Standard, Lightmap Lightprobes and Lightmap Only; ambient occlusion with strength and
  exponent; capture reuse; backups, Restore and Undo; crash and power-loss recovery.
