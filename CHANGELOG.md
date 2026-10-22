# Changelog

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
  - Scheduling only: the results on the RTX 4090 are unchanged (guard, AO and probe gates re-run; keyed-identical bakes), and the 1.5 s
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
  - **Experimental.** On an RTX 4090 with the mode forced on, real maps came out identical to full precision (vuthatest,
    earthcity_1, an AO trace of cyclotron). It has not run on a real Intel or Qualcomm GPU or on Windows on ARM.
  - `--force-compat` (command line, for testing) runs those kernels on a GPU that has FP64.
- **Scenery Multi Permutation Fix (Experimental Improvement)** (Advanced settings, off by default, `--scenery-permutation-fix`).
  - For scenery that draws more than one shape from one tag (a rock whose shape changes when you move it, a lifepod placed with
    a named variant like "crashed"). Only the shape the game actually draws casts shadows, ground hidden inside scenery is
    filled from its surroundings, and a variant's own pieces get lit instead of rendering black.
  - It differs from the 2004 tool on purpose. Most maps never need it: giving each shape its own scenery tag avoids the problem.
  - Off is byte-identical to 0.6.3. AO and light probes do not take it (the plan says so).
- **GPU timeout (TDR) risk reduced again: the photon-lattice step is resumable.**
  - The one GPU step that had a unit it could not split (about 1.7-2 s per submission on vuthatest even on an RTX 4090, against
    Windows' 2 s limit) now stops after a work budget sized from the measured time and resumes. Bit-identical results.
  - It is also much faster: on the RTX 4090 vuthatest's lattice went from 16.8 s to about 4 s and cyclotron's from 32.6 s to
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
- The 0.6.3 AO stage failure with "2004 Exact Vertex Lighting?" on is fixed (checked end to end on vuthatest).

## 0.6.3 (2026-10-20): pre-release

Exe SHA-256 `81264a3c166c3aea392bf57d65db2643993bbc8c18df4c8c4d2687fce3fddcd5`.

- **Keep PC responsive - Slower Bakes!** (Advanced settings, off by default, `--responsive`).
  - Every GPU submission is kept to about 40 ms, so the desktop, videos and games stay smooth during a bake.
  - The lightmap is identical (keyed-identical on the tested maps).
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
  - **Not fixed:** one unit of the photon lattice is not yet splittable (about 1.7-2 s on vuthatest on an RTX 4090). The log
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
  - AO neutrality (keyed-identical to production).
  - Custom on cyclotron (keyed-identical).
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
  - **Fixed:** about 40 stock scenery models shared a lookup key with a different model and were decompressed at the wrong size.
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
