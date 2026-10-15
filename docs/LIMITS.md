# Limits of the Halo 2 GPU Lightmapper

> Source references such as `gather.cpp:6020` or `gpulm_estimator.cpp:5160` point into the GPU engine's source tree. That source is not part of this repository (see [BUILDING.md](BUILDING.md)).

This page lists how big a map the GPU lightmapper can bake, and how many photons and rays it can use. For each limit
it gives the number, its kind, what happens past it, and whether Halo 2 or `tool_fast` stops you first.

**How to read it**

- **HARD**: a fixed limit set by a data type, an array, a file or tag format, or the Direct3D 11 API. Past it, the
  bake refuses, splits the work, or (where marked **silent**) gives a wrong result without an error.
- **SOFT**: a limit that comes from VRAM, RAM, time or the Windows GPU watchdog. It moves with your hardware.
- **Moot**: Halo 2's tag format or `tool_fast` stops you earlier, so the lightmapper's own limit never matters.
- Every count is **per BSP** unless it says "per scenario". The lightmapper bakes one BSP at a time, and nothing adds up
  across the BSPs of a scenario (a scenario holds at most 16 BSPs).
- A **covered texel** is a lightmap texel that some triangle actually covers. A **shading call** (or call row) is one
  supersample of one covered texel: Low takes 1 per texel, Super and Custom take 16, of which 10.2-15.6 survive on the
  tested maps (a texel on a chart edge is only partly covered). The part planner counts a conservative **coverage bound**
  instead of the real calls: 16.0-16.6 bound rows per covered texel at Super.
- Measured figures come from one PC: an RTX 4090 (24 GB, 24,564 MiB), about 95 GiB (102 GB) of RAM, Windows' default
  2-second GPU timeout, production engine v373. Anything about other GPUs, smaller cards or less RAM is **extrapolated, not
  tested**.

---

## Summary: what can it handle?

| Question | Short answer | Kind |
|---|---|---|
| How much geometry in a BSP? | Whatever `tool_fast` can import: 512 clusters; 32,767 vertices and 65,535 triangles per cluster or instance-definition section; the whole BSP's collision geometry at most 32,767 surfaces, 65,535 edges and 65,535 vertices; 512 instance definitions; 1,024 instances. The lightmapper's own triangle limit is 16,777,216, far above. Largest tested: 98,314 traced triangles and 620 instances (earthcity_4). | HARD (Halo 2's) |
| How wide can a BSP be? | Keep the solid geometry under about **510 × R world units on every axis**, where R is the map's photon search radius at the bake's quality. Super and Custom use half of Low's radius, so they have the tighter limit: R = 0.5-5 wu on the tested maps at Super, so at least 255 wu (about 777 m). Past it, light leaks through walls in the photon pass **silently**. The closest tested map uses 92% of it (mainmenu at Super). | HARD, silent |
| How many lightmap texels? | **No fixed limit.** The work is split automatically into passes ("parts") of at most 52,195,783 coverage-bound call rows, about 3.1-3.3 M covered texels at Super. Largest tested: 10.9 M covered texels (mainmenu, 168.7 M calls, 4 parts, 11.4 min at Super). The next walls are estimates, not tested: the seam list at about 21.5 M seam records (about 8 M covered texels on a map like earthcity_4), the 32-bit `tool_fast` at about 70 M covered texels, and Halo 2's 1 GiB bitmap tag at 268 M page texels. | SOFT, with HARD outer walls |
| How big can one chart be? | At Super, about 1.73 M covered texels or 2.94 M grid texels (about 1,713 × 1,713). Bigger charts are refused with a clear message. The exception is a chart more than 4,096 grid texels wide on one axis or with more than 200,000 triangles: the capture skips it **silently**. A lightmap page is at most 4,096 × 4,096. | HARD |
| How many photons? | Low 5 M, Super 20 M, Custom 1-100 M per BSP. The hard ceilings are 4,294,967,295 emission candidates and 127,822,507 stored photons. Depending on the map, they are reached at a budget of 124 M to 3.7 G photons, so the 100 M cap of the window is safe on every tested map. | HARD |
| How many gather rays? | Per shading call: Low 72, Super 128, Custom 2·MC² up to 8,192 (MC 1-64). Per texel that is 72, 2,048 and up to 131,072. The Direct3D ceiling is 65,535 rays per call (MC 181). | HARD |
| Other rays | Sun: at most 5,000 shadow rays per texel per sun (the count is the sun's angular radius / π × 5,000, so a real sun gets far fewer). Emissive surfaces: 2-256 samples per texel and light. AO: 1-4,096 rays per texel (the app uses 1,024). Every long ray (gather, sun, photon, probe) stops at 2,048 world units. All of these except AO match `tool_fast`; AO is GPU-only. | HARD (Halo 2's) |
| How many lights? | Halo 2 allows 500 lights, 500 light fixtures and 32 skies per scenario. Emissive (surface) lights: up to 32,767 per BSP on the fast path, and any number on the slower path. | HARD |
| Light probes | About **280 probe points per BSP** at the default buffer cap; more are refused cleanly. Halo 2 allows 1,024. Tested: 11. | HARD |
| VRAM | 24 GB held every tested map at Super, and the measured Custom runs (cyclotron up to 100 M / 64; mainmenu 50 M / 16 peaked at 21,756 of 24,564 MiB). These are total `nvidia-smi` figures: they INCLUDE the 2.8-3.4 GiB the desktop already used on the test PC (cyclotron at Super peaked at about 11,000 MiB in total, so about 7-8 GiB of it was the bake's own). Custom was not measured on the other maps. 12 GB holds parts up to about 26 M calls, 8 GB about 15 M. The bake does **not** adapt to VRAM. On cards under about 7 GB, Direct3D 11's documented per-resource limit (a quarter of the VRAM) is below the 1,792 MiB buffer cap. | SOFT, not tested below 24 GB |
| Bake time | Super: 22 s to 17.4 min on the tested maps (RTX 4090). Custom 100 M / 64: 1.4 h measured (cyclotron), up to about 4.2 h predicted (earthcity_1). | SOFT |
| GPU timeout (TDR) | The slowest single GPU step (the photon lattice) takes 1.47-1.78 s on three tested maps against Windows' 2 s limit. A GPU about 1.1-1.4× slower per core can time out there. | SOFT, not tested on other GPUs |

**The limits a real map is most likely to hit first**, in order:

1. A GPU with less than 24 GB of VRAM on a big map (more than about 15-26 M shading calls in a part). Nothing re-plans for
   VRAM.
2. A GPU noticeably slower than an RTX 4090 on a photon-dense map: the Windows 2 s timeout.
3. A very wide BSP: the 510 × R blocker-grid clamp (silent light leak). Super and Custom halve R, so they reach it at
   half the width that Low does.
4. More than about 280 light-probe points in one BSP.
5. Custom quality with very many rays: time, not a hard limit. 8 hours is reached only on a GPU about 2× slower than a
   4090 at the 100 M / 64 maximum.
6. A big map with many small charts: the map-wide seam list passes Direct3D 11's documented 2 GiB at about 8 M covered
   texels on a map like earthcity_4 (untested; what happens then depends on the driver).

---

## 1. BSP geometry

### 1.1 Halo 2's own limits (these bind first)

All of these are enforced by `tool_fast` when it imports the BSP, or by the tag format when the tag loads. A BSP that
breaks them never reaches the lightmapper. The maxima were read out of `tool_fast.exe` 2023.06.20 itself.

| Limit | Value | Kind | Scope | Source | Past it |
|---|---|---|---|---|---|
| Clusters | 512 | HARD (tag) | per BSP | `structure_bsp_cluster_block` max | the tool cannot add the 513th; a tag with more fails to load |
| Vertices per geometry section (each cluster, each instance definition) | 32,767 | HARD (import) | per section | tool_fast `sub_479ED4`; tag max 32,767 | import fails: "section ... has too many vertices" |
| Triangles per section | 65,535 | HARD (import) | per section | `sub_479ED4`; tag max 65,535 | import fails: "too many triangles" |
| Strip indices per section | 65,535 | HARD (import) | per section | `sub_479ED4` | import fails |
| Parts / subclusters / shared edges per section | 255 / 32,768 / 16,384 | HARD (import) | per section | `sub_479ED4` | import fails |
| Collision surfaces / edges / vertices | 32,767 / 65,535 / 65,535 | HARD (import) | per collision geometry (the BSP, and each instance definition) | tool_fast `sub_470542` | "collision geometry is too large ... reduce the amount of geometry" |
| Collision BSP planes / bsp2d nodes | < 32,768 / ≤ 32,767 | HARD (import) | per collision geometry | `sub_470542` | import fails |
| Instance definitions | 512 | HARD (tag) | per BSP | tag max | cannot add / load fails |
| Instances (placements) | 1,024, shared by all definitions | HARD (tag) | per BSP | tag max | cannot add / load fails |
| Materials | 1,024 | HARD (tag) | per BSP | tag max | cannot add / load fails |
| Scenery placements | 2,000 | HARD (tag) | per scenario | `scenario_scenery_block` max | cannot add / load fails |
| Structure BSPs | 16 | HARD (tag) | per scenario | tag max | each one is baked separately |

The collision limits apply to the whole BSP (one collision geometry), so for a BSP as a whole they are the tightest
geometry limit. Inside one cluster, which of the vertex, triangle and strip-index limits binds first depends on the mesh;
this was not measured.

Halo 2's runtime has its own budgets that a bake cannot change. For example, the stock MCC Halo 2 draws at most 850 render
sections per camera pass, so very large BSPs flicker in game whatever the lightmap.

### 1.2 The lightmapper's own geometry limits

| Limit | Value | Kind | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|
| Traced triangles (BSP + instance definitions + scenery) | 16,777,216 (2^24): instance triangle ranges are stored as float32 | HARD, **silent** | `gpulm_estimator.cpp:5160`, `gather.hlsl:338` | odd ranges round, so instances trace the wrong triangles; only scenery override rows are checked (`scn_host.h:186`, exit 9) | **moot**: the 32-bit tool and the import limits stop far earlier; tested max 98,314 |
| Instances | no count limit; time grows about linearly (each ray loops over the instances with box culls, no hierarchy) | SOFT (time) | `gather.hlsl:334-351` | slower rays only. A synthetic 64× instance count was about 44× slower. | Halo 2 caps 1,024 instances (5.4× cyclotron's 188) |
| Meshes the capture can track | 4,096 slots. Each non-scenery slot also commits two 512 KiB bitmaps (1 MiB) in the 32-bit tool | HARD | `gpulm_estimator.cpp:426, :435, :2013, :2059` | new meshes skipped; instances then fail the "NO placement link" check (`gpu_bake.ps1:877`) | **probably moot**: at most 512 + 512 non-scenery meshes (about 1 GiB of bitmaps) plus compact scenery slots for at most 2,000 scenery objects; tested max 302 |
| Per-vertex lighting queries (vertex-lit geometry) | 1,048,576 | HARD | `gpulm_estimator.cpp:517` (kVQMax) | capture marked TRUNCATED; the pipeline guard refuses the bake | **not moot**: Halo 2 allows about 33.5 M vertex-lit vertices (1,024 buckets × 32,767). Tested max 33,842. |
| Occluder triangles held in the 32-bit capture | no count limit; 264 B each | SOFT (32-bit address space) | `gpulm_estimator.cpp:1910-1930` | out of memory, so the tool crashes (inferred) | 98K triangles = 26 MB, so far away |
| BSP width for the photon visibility grid | box of the solid (opaque and alpha-tested) triangles < about **510 × R** wu on every axis (512 cells of size R, plus one R of padding each side) | HARD, **silent** | `gather.cpp:6020-6022` (clamp to 512), `:6027-6039` (insert), `re_estimate.hlsl:288` | the grid clamps once the box reaches 510 × R. Blocker triangles whose box starts 511 × R or more past the minimum (cell 512 or later) are then never inserted, so photons leak through those walls in the radiance lattice. The only sign: a `RE: blocker grid` line in `gpu_bake_lattice.log` with a 512 in it. | not a Halo 2 limit (GPU only) |
| BSP width for the photon grid | photon box < about 1,022 × R wu per axis (1,024 cells) | SOFT (time) | `gather.cpp:5533-5536` | the compact surface pass stays exact (edge cells absorb the rest) but gets slower; the per-slice fallback drops surface light past it (maintainer note 4) | GPU only; **moot** in practice, because the 510 × R blocker grid limit is reached first |
| Ray length | 2,048 wu | HARD | `gather.hlsl:651`; tool_fast `0x4B26C1` = 2048.0f | geometry farther away is invisible; a sun ray that finds no sky within 2,048 wu leaves the texel dark | **same as tool_fast**, so moot |
| Float32 precision | the 0.001 wu offsets drop to about 1 float step at 8,192 wu from the origin | SOFT | `gather.hlsl:801` | gradual self-shadowing acne far from the origin | **same as tool_fast**; the tested maps reach 1,537 wu from the origin (high_10), where the offset is still about 8 float steps |

**R, the photon search radius**, is the largest photon-manager radius of the map scaled by the quality's gather-quality
factor: × 1 at Low, × 0.5 at Super and Custom (the tool's gather quality 4.0). So Super and Custom have half of Low's
width limit. Measured at Super: 0.5 wu (cyclotron, needle, grotto, lighttest2), 1 (mainmenu), 2 (headlong, triplicate),
3 (chronopolis), 4 (earthcity_1), 5 (high_10, earthcity_4). cyclotron at Low uses R = 1.0 (blocker grid 129 × 126 × 102,
cell 1.0) against 0.5 at Super (255 × 250 × 201, cell 0.5). The 510 × R limit at Super is therefore 255 wu on a
0.5-radius map and 2,550 wu on a 5-radius map. The closest tested maps at Super are mainmenu (472 of 512 cells, 92%),
needle (419 of 512, 82%) and high_10 (389 of 512, 76%).

---

## 2. Lightmap, texels, charts and pages

| Limit | Value | Kind | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|
| Shading calls per pass ("part") | 52,195,783 call rows (= 1,792 MiB / 36 B), counted on the coverage bound (16.0-16.6 rows per covered texel at Super), so about 3.1-3.3 M covered texels; absolute 59,652,323 (2^32 / 72) | HARD (policy) | `gather.cpp:592-603, :608` | **automatic**: the map is split into the fewest balanced parts, baked one after another and merged. mainmenu Super ran in 4 parts. headlong (47.2 M real calls, 52.7 M bound) ran in 2. | no |
| Total shading calls per BSP | no fixed limit | SOFT (time; per-part memory) | `DIRECT_SEGMENTATION.md` | more parts, more time | see the outer walls below |
| Rows per GPU work segment (inside a part) | 27,633,061 call rows (GQ, 68 B); 46,976,204 grid slots (40 B). The per-vertex and scenery rows (the "tail") are reserved in every segment | HARD (policy) | `seg_plan.h:160-182` | **automatic**: more segments | no |
| One chart (charts are never split) | Super: ≤ 2,936,012 grid texels (about 1,713²) and ≤ about 1,727,000 covered texels | HARD (policy) | `seg_plan.h:171-173`; `gather.cpp:614` | clean refusal: `BUFCAP: FATAL chart N alone needs ...` or `PART: *** FATAL chart N alone needs ...`, exit 10. `-BufCapMB 4095` raises it to about 6.7 M grid / 3.9 M covered texels. | the stock tool already complains about pages over 1,024², so charts this big are unlikely |
| Chart grid in the capture | ≤ 4,096 cells per axis, ≤ 200,000 triangles per chart; ≤ 1,000,000 charts per container | HARD | `gpulm_estimator.cpp:8872, :8884` | the chart is **skipped silently**; a container with more than 1,000,000 charts is skipped whole | rare: a chart over 4,096 cells on one axis that still passes the 2.94 M grid-texel limit is under 717 cells on the other |
| Supersamples per texel | ssN 1-8 per axis (Low 1, Super 4) | HARD (range) | `gather.cpp:245-273` | out-of-range values are ignored | the tool's presets stop at 4 |
| Lightmap page size | ≤ 4,096 × 4,096, power of two | HARD (writer policy) | `lm_tagwriter.py:56, :411-414` | **automatic**: the largest charts are halved, up to 3 times; then `*** PAGE OVER 4096x4096 ... Lower the lightmap resolution`, exit 6 | the stock tool caps pages at 1,024 and downscales. Whether MCC draws 4,096 pages correctly is **not verified** (2,048 is the known-safe size). |
| Pages per BSP | about one per lit cluster and per lightmapped instance, so ≤ 1,536 | HARD (via Halo 2's cluster and instance maxima) | tag maxima; `p8group.c:384` (≤ 100,000) | — | Halo 2 binds; tested max 585 (earthcity_4) |
| Page pixels per BSP | ≤ 1 GiB of truecolor pixel data = 268,435,456 page texels (16 full 4,096² pages) | HARD (tag) | bitmap `processed pixel data` max 0x40000000 (tool_fast data definition `bitmap_pixel_data` at `0xBDE1E0`; load check `sub_623B6D`) | the writer has **no guard**: it would save a tag that `tool_fast` refuses to load | this is Halo 2's limit; tested max 22.85 M texels (mainmenu, 174 pages), 8.5% of it |
| P8 palettes | ≤ 32 per lightmap | HARD (the tool's rule) | `p8group.c:354` | never exceeded by construction | tag max 128 |
| Lightmap groups | exactly 1 per lightmap tag (writer) | HARD (writer policy) | `lmw_tagio.py:577` | `TagIOError`, clean | the tool always writes 1 |
| Seam list (map-wide, loaded in every part) | 100 B per seam record; documented D3D11 limit 2 GiB = 21,474,836 records; wraps at 4 GiB = 42,949,672 records | SOFT up to 2 GiB, then HARD, **silent** at 4 GiB | `gather.cpp:451-476, :1302, :1132-1139` | 2-4 GiB: depends on the driver (NVIDIA accepted 2.3 GB elsewhere). ≥ 4 GiB: the buffer wraps short, its view fails unchecked, and border texels lose their seam expansion without an error. | not a Halo 2 limit. Tested max 3.92 M records (mainmenu, 18% of 2 GiB). Records per covered texel range from 0.13 (headlong) to 2.74 (earthcity_4), so 2 GiB is about 8 M covered texels on an earthcity_4-like map and about 60 M on a mainmenu-like map (0.36). |
| The 32-bit `tool_fast` (capture and skeleton runs) | 4 GiB address space (large-address-aware); 12 B per chart-grid texel for its own luxel buffers | SOFT | PE header; `alloc_luxel_buffer 0x4BDEA9` | allocation failure, then a clean refusal or a tool crash | **estimate, untested**: about 170 M grid texels / about 70 M covered texels. mainmenu uses 296 MB. |
| Tool write-back fallback (only maps that cannot use the direct writer) | about 25-30 M covered texels | SOFT (32-bit) | `_research/sever_toolfast/INVENTORY.md` | the fallback cannot inject that much data | estimate |

**Largest tested**: mainmenu at Super has 13,684 charts, 10.9 M covered texels, 414 M grid slots and 168.7 M shading
calls. It ran in 4 parts on the RTX 4090 in 11.4 minutes. Nothing larger has been baked.

---

## 3. Lights

| Limit | Value | Kind | Scope | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|---|
| Scenario lights / light fixtures / skies | 500 / 500 / 32 | HARD (tag) | per scenario | tag maxima | cannot add / load fails | this **is** Halo 2's limit |
| Lights per photon manager | 16,383 | HARD (CPU format) | per manager | tool_fast `0x4B1233`, `0x4B7F37` (14-bit light id) | the tool opens a new manager | GPU keys hold 16 bits, so moot |
| Emissive surface lights, fast path | ≤ 32,767 per BSP; ≤ 1,024 candidate lights and ≤ 512 passing pairs per texel query | HARD (packing) | `gather.cpp:6796, :6974, :6976`; `surface.hlsl:971` | **automatic** fallback to the per-slice path: same result, much slower (headlong, 810 lights: 30 min instead of 5.1 min) | no Halo 2 limit. Tested max 810 (headlong). |
| Emissive samples per texel and light | 2-256 | HARD | `gather.cpp:3830`; `surface.hlsl:867` | clamped | 256 is `tool_fast`'s own ceiling |
| Sun shadow rays | `clamp(floor(angular radius/π × 5000), 1, 5000)` per texel per sun (radius in radians), so ≤ 5,000; a sun with a 0.5° angular radius gets 13 | HARD (CPU formula) | `direct.hlsl:20, :486` | — | same as `tool_fast` |
| Suns and analytic lights | no count limit; each one is a full direct pass | SOFT (time) | `gather.cpp:3516-3706`, `:2871` | time grows linearly. At 1,024 suns or more the sun cell cull switches off (still correct, slower). | tested max 17 suns (high_10), 41 analytic lights (earthcity_1) |
| Sky cubes for per-hit sky | ≤ 64 | HARD | `gather.cpp:770` | `SKY: *** FATAL ... refusing`, exit 9 | moot: Halo 2 allows 32 skies per scenario |
| Directional-sky emitters | ≤ 1,000,000 records; each emitter grid 1-30 × 1-30 | HARD | `sun_exact.h:126-176` | `sky_gpu.bin REFUSED` (sky emitters off, loud) | the 30 × 30 grid is `tool_fast`'s own clamp |
| Exact sky-chain lights | no fixed limit | SOFT (time) | `sky_chain.h` | time | tested max 101 (grotto, cyclotron) |

---

## 4. Photons

| Limit | Value | Kind | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|
| Photon budget | Low 5 M, Super 20 M (the tool's presets); Custom 1,000,000-100,000,000 | HARD (UI range) | `BakeOptions.cs:22`; the Custom engine's `gpu_bake.ps1` (`-Photons` ValidateRange); presets from tool_fast's table at `0xA30950` | refused | Custom is Experimental in 0.6.1 (shown in the window, no switch; only Super's own 20 M / 8 is validated) |
| Emission candidates (all lights together) | ≤ 4,294,967,295 (uint32 offsets) | HARD | `gather.cpp:4090-4113`; `photoncast.hlsl:503-509` | Custom (guarded build): `FATAL PALLOC OVERFLOW (N3 guard) ... Lower the photon budget`, exit 9. Low/Super (production build): **silent wrap**, so photons go to the wrong light, but unreachable at 5 M / 20 M. | no |
| Stored photons (deposits) | ≤ 127,822,507 (2^27 slots, 2 GiB per buffer) | HARD | `gather.cpp:4536-4539` | first 16,000,000 deposits: one cast; more: **automatic** exact regrow and a second full cast (the cast time doubles). Past the cap: `GPU CAST needs N photon slots, past the D3D11 buffer limit`, exit 4. | no |
| Photon cloud after the sky chain | 16 B per photon per buffer; documented 2 GiB at 134 M photons, wrap at 268 M | SOFT, then HARD | `gather.cpp:5547-5607` | 2-4 GiB: depends on the driver (a refused buffer is `buf fail ... FATAL`, exit 1). Wrap: the cull switches off, no lattice is written, then `GATHER: FATAL no valid GPU r.e. lattice`, exit 7 | the wrap is out of reach (deposit cap 127.8 M plus the sky chain's photons, +18% on cyclotron); the 2 GiB mark could be crossed just below the deposit cap |
| Bounces per photon path | 64 iterations (1-256 via `GPULM_CAST_BOUNCES`) | SOFT (fidelity cap) | `gather.cpp:4281-4303` | the path is cut and counted; warns if more than 1% of paths hit it. At most 0.95^64 = 3.8% of the worst-case path energy is affected. | `tool_fast` has no cap |

How the budget turns into candidates and deposits depends on the map, so the ceilings are reached at different budgets:

| Map | Candidates per budget photon | Budget where candidates pass 2^32 | Deposits per budget photon | Budget where deposits pass the cap |
|---|---|---|---|---|
| chronopolis | 34.6 | **124 M** | 0.254 | 503 M |
| headlong | 23.5 | 183 M | 0.372 | 344 M |
| cyclotron | 15.2 | 282 M | 0.383 | **333 M** |
| triplicate | 14.5 | 296 M | 0.084 | 1,518 M |
| needle | 10.7 | 401 M | 0.132 | 966 M |
| earthcity_4 | 4.3 | 1,001 M | 0.177 | 722 M |
| high_10 | 3.2 | 1,328 M | 0.057 | 2,252 M |
| mainmenu | 2.5 | 1,696 M | 0.011 | 11,281 M |
| earthcity_1 | 2.0 | 2,188 M | 0.048 | 2,636 M |
| grotto | 1.15 | 3,726 M | 0.001 | — |

The window's 100 M cap is below every row. chronopolis has the least headroom (1.24×). A map with more than about 43
candidates per budget photon would reach the candidate ceiling at 100 M; Custom then refuses cleanly. Maps with many
surface or sky lights have the highest ratios.

Measured at Custom 100 M / 64 on cyclotron: 1,522,337,889 candidates, 38,393,481 deposits (one regrow), 45,466,268
photons including the sky chain.

---

## 5. Gather rays (Low, Super, Custom) and other rays

| Limit | Value | Kind | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|
| Gather rays per shading call | Low 72 (the tool's 6 × 3, doubled per axis on the GPU); Super 128 (16 × 8, the tool's); Custom (2·MC) × MC, MC 1-64, so ≤ 8,192 | HARD (UI range) | `gather.cpp:337-352`; `gpu_bake.ps1:1470`; `BakeOptions.cs:23`; the Custom engine's `gpu_bake.ps1` (`-MCSamples` ValidateRange); `FINAL_DESIGN.md` | MC outside 1-64 is refused | — |
| Gather rays per texel | Low 72; Super 2,048; Custom MC 16: 8,192; MC 64: 131,072 | follows from the above | ssN² × rays per call | — | — |
| Rays per call, API ceiling | ≤ 65,535 (MC ≤ 181) | HARD (D3D11: 65,535 groups per Dispatch) | `gather.cpp:1705-1722` | `FATAL: ray dispatch needs N groups > 65535 ... Aborting rather than returning silently-zero gather results`, exit 7 | moot: the UI stops at MC 64 |
| Rays per dispatch | Super: fixed 4,096 calls = 524,288 rays; Custom: `clamp(floor(524,288 / 2·MC²), 64, 4,096)` calls, so ≤ 524,288 rays (exactly 524,288 at power-of-two MC ≥ 8); Low: adaptive, 400 ms target; scratch ≤ 256 MiB | SOFT (TDR) | `gather.cpp:1703-1731, :1865-1869`; `gpu_bake.ps1:1470`; `BakeOptions.cs:70` | — | 3.4-28.6 ms per dispatch on a 4090, about 70× under the 2 s limit |
| Transparent layers per ray | 33 passes (32 pass-through layers) | HARD | `gather.hlsl:631` | the ray contributes 0 | same as `tool_fast` |
| Enclosure gate | 7 probe rays per call | HARD | `gather.hlsl:776` | — | same as `tool_fast` |
| Gate candidate walk | ≤ 64 rounds (the tool has no cap) | SOFT (fidelity) | `gather.cpp:1280-1284` | the call stays rejected and is logged; rarely binds | GPU-only |
| AO rays per texel | 1-4,096 (the app uses 1,024 since 0.6.0; 0.5.0 used 64) | HARD (range) | `gpu_ao.py:140` | refused | AO is GPU-only |
| Probe rays | 6 × 128² = 98,304 cube texels per probe, 18 gather rays each | HARD (the tool's cube builder) | `gather.cpp:429-442` | — | same as `tool_fast` |

Gather throughput on the RTX 4090 is 18-155 M rays/s depending on the map (530 M on a tiny test map), and it does not
depend on MC. Measured totals: cyclotron Super 2.16 G rays in 71 s; mainmenu Super 21.6 G rays in 163 s; cyclotron
100 M / 64 138.3 G rays in 4,319 s.

---

## 6. Ambient occlusion

| Limit | Value | Kind | Source | Past it |
|---|---|---|---|---|
| Rays per texel | 1-4,096. The app passes no ray count, so the shipped `gpu_ao.py`'s default applies: **1,024** (the approved recipe, A1, 2026-10-15; the 0.5.0 window used 64) | HARD (range) | `gpu_ao.py:140`; `BakePlanner.cs:185-187` | refused |
| Seam filter | texels on the two sides of a lightmap seam are blended by world position, and **never with a surface stacked under them** (`gpu_ao apply --seam-stack-eps`, default 0.004 wu; the app passes no override) | design (A1) | `gpu_ao.py` apply; `ao_seamfix.py` | — |
| Distance | above 0 and below 64 wu (default 0.656 wu = 2 m) | HARD (range) | `gpu_ao.py:139`; the AO engine's `gpu_bake.ps1:313` | refused |
| Ray counters | 64-bit | HARD | measured: 16,051,191,808 rays printed correctly | — |
| Time | chronopolis (3.93 M texels): 0.25 G rays in 0.58 s at 64 rays; 16.05 G rays in 97.6 s at 4,096 rays; the app's 1,024 rays are about 4 G rays, about 10 s of GPU kernel time (+6 s wall time over 64 rays, A1) | SOFT | `farm_w191/ao` | time only. Throughput is about 390-430 M rays/s up to 1,024 rays and about 165 M rays/s at 4,096; the largest single submit was 79 ms. |
| Writer | AO needs the direct tag writer | HARD (design) | README | a map that falls back to the tool writer bakes without AO, or stops |

AO VRAM was not measured.

---

## 7. Light probes

| Limit | Value | Kind | Source | Past it | Halo 2 lower? |
|---|---|---|---|---|---|
| Probe points the GPU pass can hold | ⌊(27,633,061 − the largest chart's call rows) / 98,304⌋, so **about 270-281 per BSP** at the default cap; about 640 with `-BufCapMB 4095` | HARD (policy) | `gather.cpp:429-442`; `seg_plan.h:163-165` | clean refusal: `BUFCAP: FATAL the N appended rows alone need X MB of GQ` or `chart N alone needs ...`, exit 10 | **not moot**: Halo 2 allows 1,024 |
| Probe file | ≤ 4,096 probes | HARD | `gather.cpp:434` | `PROBE: *** bad probes file`, exit 9 | moot |
| Lighting environments in the lightmap tag | 1,024 per BSP | HARD (tag) | `structure_lightmap_lighting_environment_block` max | a scenario with more probe points in a BSP should crash the tool's skeleton run (read from the decompile, not reproduced) | this is Halo 2's limit |
| Scenario probe points | 32,768 per BSP lighting element, 16 elements per scenario | HARD (tag) | tag maxima | — | the 1,024 above binds first |

Tested: triplicate, 11 probe points (1,081,344 probe rows). Each probe point adds 98,304 shading rows, about 21 MiB of
VRAM at the fitted rate.

---

## 8. Memory and time scaling

### 8.1 Formulas (per part, per BSP)

| Resource | Model | Source |
|---|---|---|
| VRAM, main pass | about 1.39 GiB + 228 B per call row + 52 B per grid slot + 42 B per stored photon (fit on 8 measured peaks, ±1.3 GB) | `_research/limits/scaling/predict_limits.py` |
| VRAM, photon lattice pass | grows by about 40 B per budget photon on average (65 B from 20 to 50 M, 25 B from 50 to 100 M): cyclotron 6.3 / 8.1 / 9.3 GiB at 20 / 50 / 100 M; mainmenu 13.6 GiB at 50 M | nvidia-smi logs, T3 (`scaling/vram_segments.json`) |
| VRAM, photon cast | 48 B per slot: 16 M slots minimum (768 MB), 6 GiB at the 2^27 cap | `gather.cpp:4182-4219` |
| Host RAM | planner: 300 B per call + 40 B per slot, kept under 40% of physical RAM. Measured working set 310-890 B per call; commit (pagefile) about 2× the working set. | `gather.cpp:595-601`; `HOSTMEM` log lines |
| Host RAM, photons | about 150-200 B per stored photon | `gather.cpp:4654-4762` |
| Gather time | call rows × 2·MC² / R, R = 18-155 M rays/s on a 4090 | farm logs |
| Lattice time | a + b × budget: b = 4.3 s per M photons (cyclotron) to 17.5 s per M (mainmenu, sky-chain heavy); doubled cast once deposits pass 16 M | farm logs |
| Direct time | lights × calls × shadow rays (up to 5,000 per sun) | `direct.hlsl` |
| Parts | run one after another, so total time grows linearly with the map | `gpu_bake.ps1:1500-1530` |

The part planner sizes parts by the 1,792 MiB per-buffer cap and by host RAM only. **It never looks at VRAM.**

### 8.2 By GPU memory (extrapolated from the fit; only 24 GB was tested)

Assumes about 1 GiB of VRAM used by the desktop, 2.5 grid slots per call and about 5 M stored photons, and counts a card's
"GB" as 10^9 bytes, which leaves a small extra margin.

| Card | Call rows per part that fit (about this much VRAM free) | ≈ covered texels per part at Super | Tested maps whose largest Super part fits |
|---|---|---|---|
| 24 GB | about 59 M (21.4 GiB), so the planner's 52.2 M cap binds first | 3.1-3.3 M | all (worst: mainmenu, 18.2 GiB predicted; mainmenu 50 M / 16 peaked at 21,756 of 24,564 MiB including the desktop) |
| 16 GB | about 37 M (13.9 GiB) | about 2.3 M | all except mainmenu (18.2 GiB) and earthcity_1 (14.1 GiB, borderline) |
| 12 GB | about 26 M (10.2 GiB) | about 1.6 M | all except earthcity_1, mainmenu and chronopolis (10.5 GiB, borderline) |
| 8 GB | about 15 M (6.5 GiB) | about 0.9 M | lighttest2, grotto, needle, earthcity_4 only; cyclotron (7.0), high_10 (7.5) and triplicate (8.3 GiB) are just over |

The covered-texel column divides by the planner's coverage bound (about 16.3 rows per covered texel), because
`-MaxCallsPerPart` limits bound rows.

Direct3D 11 documents a per-resource limit of min(max(128 MB, a quarter of the dedicated VRAM), 2,048 MB); the engine's
own exact-sun and sky-chain code applies this formula (`sun_exact.h:368-375`), but the main pass's per-resource cap is a
fixed 1,792 MiB (`seg_plan.h:49`). On a card with less than about 7 GB the documented limit is below 1,792 MiB, so
`-BufCapMB` would have to be lowered (not tested).

The photon lattice runs as its own process before the parts and must fit too: 6.3 GiB on cyclotron at Super, 13.6 GiB on
mainmenu at 50 M photons.

What happens on a card that is too small is **not tested**. Expected: Windows pages GPU memory into system RAM (much
slower, and closer to the 2 s timeout), or a buffer fails to create (`buf fail ... FATAL`, exit 1; or
`FULLGPU: query buffer fail -- FATAL`, exit 9). The workaround is smaller parts with `gpu_bake.ps1 -MaxCallsPerPart`;
the window does not offer it.

### 8.3 By host RAM (extrapolated; only about 95 GiB was tested)

| RAM | Call rows per part allowed by the planner | Expected memory per part (at mainmenu's measured 0.38 KB working set and 0.89 KB commit per call) |
|---|---|---|
| 16 GB | about 14-19 M | 5-7 GiB working set, 12-16 GiB commit |
| 32 GB | about 29-38 M | 10-14 GiB working set, 24-32 GiB commit: the pagefile must be large |
| 64 GB or more | the GPU cap (52.2 M) binds first | — |

The range depends on grid slots per call (1.44-4.53 measured). The planner budgets 300 B per call + 40 B per slot against
40% of RAM, but the measured working set is 310-890 B per call (more on photon-heavy Custom bakes) and the commit is about
2× that, so on a low-RAM PC a planned part can still page. `-PartHostMB` / `-MaxCallsPerPart` lower it.

### 8.4 Measured maps (Super, RTX 4090)

| Map | Covered texels | Shading calls | Parts | Traced triangles | Instances | Total time | Largest part, VRAM (fit, over desktop) | Custom 100 M / 64 (predicted) |
|---|---|---|---|---|---|---|---|---|
| lighttest2 | 0.15 M | 2.3 M | 1 | 3.4 K | 19 | 22 s | 2.2 GiB | 1.5 min |
| grotto | 0.10 M | 1.35 M | 1 | 15.8 K | 16 | 5.4 min | 2.1 GiB | 38 min |
| needle | 0.47 M | 4.8 M | 1 | 29.6 K | 138 | 3.7 min | 3.6 GiB | 43 min |
| earthcity_4 | 0.86 M | 9.7 M | 1 | 98.3 K | 620 | 17.4 min | 5.3 GiB | 1.6 h |
| cyclotron | 1.25 M | 16.9 M | 1 | 14.7 K | 188 | 4.1 min | 7.0 GiB | 1.36 h (**measured**) |
| high_10 | 1.37 M | 18.2 M | 1 | 13.7 K | 116 | 3.7 min | 7.5 GiB | 1.0 h |
| triplicate | 1.61 M | 20.9 M | 1 | 23.3 K | 97 | 6.0 min | 8.3 GiB | 2.5 h |
| earthcity_1 | 2.97 M | 36.8 M | 1 | 66.1 K | 508 | 9.3 min | 14.1 GiB | 4.2 h |
| headlong | 3.29 M | 47.2 M | 2 | 20.1 K | 141 | 5.5 min | 9.1 GiB | 2.2 h |
| chronopolis | 3.93 M | 52.7 M | 2 | 35.2 K | 152 | 7.7 min | 10.5 GiB | 3.2 h |
| mainmenu | 10.9 M | 168.7 M | 4 | 42.4 K | 109 | 11.4 min | 18.2 GiB | 3.4 h |

Total time includes capture and tag writing. earthcity_4 spends 386 s of its 17.4 min in the stock tool's skeleton run,
which is cached afterwards. The Custom predictions were checked on two measured runs and ran about 8% high (cyclotron
100 M / 64: 5,307 s predicted, 4,893 s measured; mainmenu 50 M / 16: 1,728 s predicted, 1,604 s measured).

For comparison, the CPU `tool_fast` on a 28-worker farm took about 85 minutes for chronopolis at Super; the GPU took 7.7.

---

## 9. Windows, the GPU timeout, long bakes and paths

| Limit | Value | Kind | Source | Past it |
|---|---|---|---|---|
| GPU timeout (TDR) | 2 s per GPU submission (Windows default `TdrDelay`) | SOFT | Windows | `DEVICE REMOVED (TDR)` and exit 4 or 8, or `FATAL: the GPU device was REMOVED`; the bake stops (it never continues on a dead device). Live tags are not touched. |
| Photon lattice tile | the slowest tile is 1.47 s (cyclotron), 1.59 s (headlong), 1.78 s (earthcity_4) on a 4090; tiles never go below 1,024 samples | SOFT, **the tightest margin** | `gather.cpp:6297-6366` | a GPU only 1.12-1.36× slower per core (clock or memory latency, not core count) can time out on these maps |
| Other steps (4090) | gate pre-pass ≤ 860 ms (2.3×); sky chain ≤ 709 ms (2.8×, adaptive, 400 ms target); sun tile ≤ 399 ms; surface cull ≤ 373 ms; gather ≤ 29 ms | SOFT | `_research/limits/scaling/tdr_table.md` | adaptive steps shrink themselves; only their first pass matters |
| Capture | 50 min | SOFT | `gpu_bake.ps1:838` | killed, bake fails. Tested max 50 s in the measured runs (an earlier mainmenu capture with the FP64 Plücker fix took 479 s). |
| Skeleton tool run (stock CPU tool) | 30 min | SOFT | `gpu_bake.ps1:651` | killed, bake fails. Tested max 596 s (earthcity_1), 33% of the limit. |
| CPU photon-cast helper (multi-sun maps) | 30 min | SOFT | `gpu_bake.ps1:851` | killed; the bake continues with the GPU's statistical suns |
| Long bakes | no checkpoint or resume | SOFT | — | a bake cut by power-off, sleep or reboot is lost and must be re-run; the live tags are untouched because install comes last |
| Working path length | the longest path a bake creates (the H2EK folder plus the scenario, BSP and lightmap tag names) < 240 characters | HARD | exe preflight (`PathBudget.cs`, `PreflightChecks.cs:237-248`); `gather.cpp:329-358` (char[300] buffers + 260-character `MAX_PATH`) | refused by the exe with the path named (a warning only before a scenario is chosen) |
| Size of one input file read with `loadf` | ≤ 2,147,483,647 bytes (32-bit `ftell`) | HARD | `gather.cpp:284` | crash (expected, untested); **moot**: the largest such file is 33 MB (mainmenu `gather_samp.bin`) |
| GPU features | Direct3D 11 feature level 11_0 **with FP64 and extended FP64** (ddiv / dfma / drcp) shader support | HARD | `gather.cpp:1052-1075`; `gpucheck.cpp:364-376` | refused by the exe's GPU check |
| Number of GPUs | one: the default adapter | HARD (design) | `gather.cpp:1053` (`D3D11CreateDevice(nullptr, ...)`); `gpucheck.cpp` | a second GPU is not used; the exe only advises which adapter will run |

**8 hours.** At Super no tested map comes close: the slowest (earthcity_4, 17.4 min) would need a GPU about 28× slower
than a 4090. At Custom 100 M / 64, the slowest predicted map (earthcity_1, 4.2 h) reaches 8 hours on a GPU about 1.9×
slower than a 4090 (mainmenu 2.3×, chronopolis 2.5×). As a rule of thumb, 8 hours of MC 64 gathering on a 4090 is
66-545 M shading calls (about 4-40 M covered texels), depending on the map's ray speed; at MC 16 it is 16× more, at Super
64× more. The developer's PC powers off after 8 hours, which is why this mark is tracked.

The window's Custom time estimate scales the whole GPU step by the ray count, so it overestimates (about 13,900 s shown
against 4,900 s measured for cyclotron 100 M / 64).

---

## 10. Which limits are really Halo 2's own

| Area | Halo 2 / `tool_fast` limit | Lightmapper limit | Which binds |
|---|---|---|---|
| Geometry | 512 clusters; 32,767 vertices / 65,535 triangles per section; collision 32,767 surfaces | 2^24 traced triangles | **Halo 2** |
| Instances | 512 definitions, 1,024 placements per BSP | none (time only) | **Halo 2** |
| Scenery | 2,000 per scenario | 4,096 capture slots, 8,192 owners | **Halo 2** |
| Ray length, layers, gate rays, sample search | 2,048 wu, 33 passes, 7 rays, 17 candidates | identical | equal (CPU-faithful) |
| Photon light ids | 16,383 lights per manager (14 bits) | 16 bits | **Halo 2** |
| Skies | 32 per scenario | 64 sky cubes | **Halo 2** |
| Sky emitter grid | 30 × 30 | 30 × 30 | equal |
| Surface samples, sun rays | 256, 5,000 | 256, 5,000 | equal |
| Photon budget | int32 in the tool | 2^32 candidates / 127.8 M deposits | the window's 100 M cap |
| Lightmap page | 1,024² stock (downscaled) | 4,096² | the lightmapper allows **more** than the stock tool |
| Page pixels | 1 GiB per bitmap tag | no own limit | **Halo 2** |
| Light probes | 1,024 per BSP | about 280 per BSP | **the lightmapper** (clean refusal) |
| Vertex-lit vertices | about 33.5 M per BSP | 1,048,576 captured queries | **the lightmapper** (clean refusal) |
| BSP width | none | about 510 × R wu (blocker grid; R halves at Super and Custom) | **the lightmapper** (silent) |
| Lightmap texels | the 32-bit tool's address space (estimate: about 70 M covered) | seam list 2 GiB / 4 GiB; VRAM per part | depends on the map; untested past 10.9 M |
| Time / VRAM / TDR | — | your GPU | **your hardware** |

---

## Maintainer notes (not for end users)

**Known gaps that fail silently or ungracefully** (all checked in source):

1. **Blocker grid clamp** (`gather.cpp:6022`): the r.e. visibility grid is clamped to 512 cells per axis without growing
   the cell. Dimension = trunc(E/R + 2) + 1, so the clamp starts at E = 510 × R. Triangles whose box starts at cell 512 or
   later (511 × R or more past the blocker box's minimum) are never inserted (the insert loop's start is not clamped), so
   the radiance lattice leaks light there. R is the quality-scaled radius (Super and Custom: half of Low's). The fine-grid
   path refuses grids over 512 per axis or 2^25 cells, so it cannot rescue this. mainmenu at Super is at 92%
   (449×472×341, R = 1). Fix: grow the cell to `extent/512` like the compact SURFCULL grid does.
2. **`srvRaw` never checks `CreateShaderResourceView`** (`gather.cpp:1132-1139`). Any `srvRaw` buffer of 4 GiB or more
   wraps short and binds a null view. The map-wide seam list (100 B per record) is the first one in reach.
3. **Production fullgpu (Low/Super) has no palloc guard**: a candidate total of 2^32 or more wraps silently. Unreachable
   at 5 M / 20 M (≥ 6× margin on every tested map); only the Custom (N3) build refuses.
4. **Per-slice surface fallback** (`surface.hlsl:658-660`): the query cell is not clamped against the 1,024-per-axis photon
   grid, so once the photon box passes about 1,022 × R it drops surface light silently for texels 1,024 × R or more past
   the box's minimum. Reached only when the compact pass is refused. The host's
   `qcnt[q] > 512` refusal (`gather.cpp:6976`) predates `SC_MAXC = 1024` and sends 513-1,024-pair queries to this path.
5. **Float-encoded triangle ranges** (2^24): silent, but moot under Halo 2's limits.
6. **VRAM is not planned**: parts are sized by the per-buffer cap and host RAM only.
7. **Capture chart filter** (`gpulm_estimator.cpp:8872`): charts over 4,096 grid cells on one axis are skipped, and the
   writer's chart-count gate cannot see it.
8. **No 1 GiB guard in the writer** for truecolor pixel data: it would write a tag `tool_fast` refuses to load.
9. **More than 1,024 probe points in a BSP**: the tool's `build_lightprobes 0x4AF061` ignores the failed block add and
   writes through a null pointer (decompile; not reproduced). No preflight checks the point count.
10. **TDR**: the r.e. tile cannot shrink below 1,024 samples by design (its time is the slowest wave), so on slower GPUs
    only the fine blocker grid (per-sample cost) or a higher `TdrDelay` helps.
11. **Capture surface-index cap** (`gpulm_estimator.cpp:435, :2003`): a surface index of 4,194,304 or more in one mesh slot
    is dropped and only counted (`idxOverflow=` in the `final_dump` line of `gpulm.log`); no guard reads it. Moot under
    Halo 2's 65,535 triangles per section, unless object recursion offsets grow that far.
12. **The per-resource cap ignores the card**: 1,792 MiB is fixed (`seg_plan.h:49`), while D3D11 documents a quarter of
    the dedicated VRAM on cards under 8 GB. The exact-sun and sky-chain code already uses that formula (`sun_exact.h:368`).

**Corrections made while compiling** (the sweep reports disagreed; source settles each):

- The blocker-grid clamp is **not** exact past 512 cells: triangles starting past cell 511 are dropped (see 1).
- R depends on the quality (corrected 2026-10-15; an earlier draft said it did not): the largest photon-manager radius
  × √(1 / gather quality), so × 1 at Low and × 0.5 at Super and Custom. cyclotron: R = 1.0 at Low (`farm_w212`), 0.5 at
  Super (`farm_w71`).
- The sky chain is no longer pinned at 1.5-1.65 s: since the 2026-10-08 TDR fix its floor is 1 draw, its first pass 8
  draws and its target 400 ms (`sky_chain.h:710-722`); measured passes are 437-709 ms.
- Low casts 72 gather rays per call (the default `GPULM_RAY_MULT 2` is not cleared at Low), not the CPU's 18.
- The probe buffer's 32-bit byte size cannot wrap: the GQ reservation (68 B per row) always refuses first, even at
  `-BufCapMB 4095`.
- The highest candidate ratio is chronopolis (34.6 per budget photon, overflow at 124 M), not headlong (183 M).
- The 11.7 GB VRAM figure is cyclotron at 50 M / 16 (8.3 GB over a 3.4 GB desktop), not Super.

**Adversarial verification, 2026-10-15** (every HARD number re-opened in its source; SOFT numbers re-derived from
`scaling/`): fixed here were the 509 → 510 × R and 1,021 → 1,022 × R grid bounds (truncation), R's quality dependence,
the per-part texel count (the planner caps coverage-bound rows, not real calls), the calls-per-texel range (10.2-15.6, not
13.5-15.5), the seam ratio range (headlong 0.13), the lattice VRAM slope (about 40 B per budget photon, not 87), the 16 GB
and 12 GB fits, the sun ray formula (angular radius, not area), the AO distance range (below 64 wu), the GPU check
(extended doubles), the tested RAM (95 GiB, not 99.5 GB) and the largest coordinate (1,537 wu, high_10). Added: the
whole-BSP collision limit in the summary, D3D11's per-resource limit on small cards, one GPU only, the capture's per-slot
bitmaps and surface-index cap. Tool addresses `0x4B26C1` (2048.0f), `0x4B1233` and `0x4B7F37` (0x3FFF) were re-read with
idalib on a private copy; the bitmap `processed pixel data` maximum 0x40000000 was read from `tool_fast.exe` at `0xBDE1E0`.

**Exit codes of fullgpu**: 1 device, shader or buffer creation failed; 3 map failure; 4 TDR, device removed or a FATAL in
the GPU cast; 7 gather FATAL (including a missing lattice); 8 direct pass failed; 9 refusals; 10 buffer-cap or part-plan
FATAL. The tag writer exits 6 for `PAGE OVER 4096`.

**Sources**: `gather.cpp`, `seg_plan.h`, `sky_chain.h`, `sun_exact.h`, `scn_host.h` and `*.hlsl` are in
`engine_v373/v041-fullgpu/` and `engine_v373/v015-direct/`; `gpulm_estimator.cpp` in `engine_v373/v014-inject-estimator/`;
`lm_tagwriter.py`, `lmw_tagio.py`, `p8group.c` in `engine_v373/v043-tagwriter/`. Tag maxima were read from `tool_fast.exe`
2023.06.20 (md5 `3a889d37…`) into `_research/limits/block_maxcounts.tsv`; tool addresses were read with idalib on a private
copy (`_research/limits/captag_ida_evidence/`). Measurements are in `_research/limits/scaling/` (from the `farm_w*` logs
and the T3/T4 nvidia-smi logs). `FINAL_DESIGN.md` and `BakeOptions.cs` are in the H2 GPU Lightmapper exe project.
