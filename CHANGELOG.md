# Changelog

## 1.5.1 (2026-11-21)

Exe SHA-256 `fd84dd810341603c477793e30bdff74f303762e6c6b491dd04d03ba56cdf9db7` (27,151,872 bytes).

- **Fix.** **Low quality bakes no longer stall at the vertex-direction step.** In 1.5.0 a Low bake gave the vertex-direction builder Super's supersample count (4) instead of its own (1): the fast builder refused the bake's samples, and the slow fallback ran an offline search that could take a very long time (the log looked stuck on the Python builder) and did not use the bake's own samples. The builder now reads the count from the bake itself, so a Low bake writes its vertex directions from its own samples in seconds. Super and Custom bakes are unchanged.
- **Fix.** **Big Custom photon counts no longer cast the photons twice.** The GPU photon cast started with room for 16,000,000 photon deposits, sized for Super. A bake keeps about 0.37-0.42 deposits per photon of its budget, so every Custom bake above about 40 million photons filled it ("GPU CAST OVERFLOWED ... RE-CASTING") and cast every photon a second time. The first cast is now sized from the budget (0.45 x the photon count), so it runs once. The photons and the lightmap are exactly the same as before: on headlong with Custom 50,000,000 photons / MC 8 the staged lightmap is identical to 1.5.0's. On the test PC the second cast only cost seconds, so do not expect a big speed-up: 1.5.1 took 222 s and 256 s in two runs, 1.5.0 252 s (run-to-run spread is that large); the overflow warning and the repeated cast are gone. Bakes up to about 35 million photons, every preset included, are unchanged.
- **Fix.** **A leftover work folder no longer stops a bake.** Every bake builds a temporary overlay of your tags for the tag writer (in `GPU Work\_gpulm_skel\w<NN>`) and removes it at the end. When one was left behind (a bake that ended early, or one run on a worker number the app does not own) and the next bake on that number was for a DIFFERENT map, the bake stopped with "skeleton overlay ... must be a REAL directory" (exit 3) and wrote nothing. The bake program now removes a leftover overlay first: its links into your tags are unlinked without ever being followed, and only its own copied files are deleted.
- **Checks.** Through the app on the test maps (stage only): a Low bake of headlong wrote its vertex directions from its own samples; Custom 50 M on headlong cast once and staged the same lightmap as 1.5.0 (which re-cast); Super on headlong staged the same lightmap as 1.5.0 (the lightmap tag byte for byte, the bitmaps' pixels equal); the Custom, ambient occlusion and light probe stages equal 1.5.0's; the MCC `.map` bake gives 1.5.0's output map byte for byte on the same input; a Low bake on a worker whose leftover overlay was made for another map finished normally, removed it, and left the tags its links pointed to unchanged. Live tags were unchanged by every test. The CPU test suite passed (core 119, shell 139, S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the UI tests ran on this exe too, 3/3).
- **First start.** 1.5.1 unpacks its own engine folder (`GPU Work\_runtime\1.5.1-e210f10e`) once, about 165 MB per H2EK; the 1.5.0 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Known and not fixed: a surface made of very thin triangles that each have their own tiny patch (under a texel wide) can still show soft streaks after seam stitching. A real card under 6 GB has not run a bake.

## 1.5.0 (2026-11-20)

Exe SHA-256 `8de4c3eb105efb83ff8f313f42f24b5338d3e9db1b4ad30c24d261e4a49dcbf9` (27,152,896 bytes).

- **New (experimental): Stitch lightmap seams.** Advanced settings, off by default; command line `--stitch-seams`. Where a lightmap is coarse, two lightmap patches that meet on a flat surface can disagree at their shared edge: the game blends texels smoothly inside a patch but never across from one patch to the next, so the difference shows as a hard straight line (often along long, thin triangles). A higher lightmap resolution hides it, but also makes every shadow sharper. With this option the tag writer changes the texels next to each seam between flat or gently curved faces (within 20 degrees), as little as it can, so that both sides agree; corners and folds are left alone, and shadows elsewhere keep their sharpness. It stitches every lightmap the bake writes, the ambient occlusion layer's too. On cyclotron (Super) it stitched 2,869 seams: the seams that stepped by 6/255 or more went from 87 to 0, the largest step from 37/255 to 5/255, and 34,216 texels changed. It is not the original tool's output. Off, every lightmap is byte-identical to 1.4.2's.
- **New (experimental): Seam stitch only.** Mode "Seam stitch only"; command line `--mode stitch`. Stitches the seams of the lightmap that is already in the tags (made by this program, the stock tool or Bungie) without baking anything: no capture and no GPU, about 15 seconds on the test maps. Only the two lightmap bitmaps change; the lightmap tag, its layout and its palettes are kept. The current lightmap is backed up first, so "Restore previous lightmap" undoes it. It stops, writing nothing, when the BSP has no lightmap yet or the lightmap does not belong to the BSP; it is not available for a `.map` bake (use the option there).
- **Checks.** With the option off, the Custom, ambient occlusion and light probe stages through the app are equal to 1.4.1's (and 1.4.2's) on the test maps, and the MCC `.map` bake gives 1.4.2's output map byte for byte on the same input. With it on, on one capture, only the two lightmap bitmaps differ from the bake without it. "Seam stitch only" was checked end to end on a test H2EK: it installs only the two bitmaps, the lightmap tag stays byte-equal, and "Restore previous lightmap" gives the previous set back exactly. Live tags were unchanged by every test. The CPU test suite passed (core 119, shell 139 (incl. the seam stitch tests), S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the UI tests ran on this exe too, 3/3). This exe carries the gated candidate's payload file for file apart from the version-stamped bake program, and on it the MCC `.map` bake gives the candidate's output map.
- **First start.** 1.5.0 unpacks its own engine folder (`GPU Work\_runtime\1.5.0-6074f34c`) once, about 165 MB per H2EK; the 1.4.2 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Known and not fixed: a surface made of very thin triangles that each have their own tiny patch (under a texel wide) can still show soft streaks after stitching: the edges agree, but such a triangle cannot hold the light across its width. A real card under 6 GB has not run a bake.

## 1.4.2 (2026-11-19)

Exe SHA-256 `c846b57e178e7cf13632151d3498880fb4b53c365ee7cd9aad7b3e1c872ec654` (27,116,032 bytes).

- **Fixed: ambient occlusion and light probes on a scenario whose lightmap reference points elsewhere.** A scenario copied from another map (or a lightmap made under an older name) can reference a lightmap in another folder than the one the bake writes. Up to 1.4.1 the direct tag writer refused such a scenario, the whole bake fell back to the original tool's write-back, and ambient occlusion and light probes were not applied, with only a line in `bake.log` to say so. The writer now repoints the reference to the new lightmap, as the tool's own write-back does; the old lightmap tag is left as it is, and the window and the log name both. The `.scenario` is then part of the bake's backup, so "Restore previous lightmap" puts the old reference back too.
- **New: `.map` bakes whose BSP or lightmap tag lives in another folder.** Some maps keep a BSP's tags under another scenario's folder (newmombasa: two of its three BSPs and its lightmap are under `03a_oldmombasa`). The MCC `.map` bake now keeps the BSP where the map has it and writes the lightmap the map names for that BSP, instead of assuming `<scenario folder>\<scenario>_<bsp>_lightmap`; the window says which tags it uses. Not yet baked end to end on such a map.
- **Closer to the original tool: photons that hit scenery.** The original tool traces a scenery object through three nested grids (the scene, the object, one grid per mesh section), and a cell of the outer grid limits how far the inner grids look. 1.4.1 traced scenery like any other object, so it could accept a triangle the tool only reaches in a later cell. On earthcity_1 that was the 3 / 5 photons 1.4.1 still listed as different, all knock-on effects of one such hit. The ray tracer now follows the tool's scenery grids. Compared photon by photon with the tool's own cast, earthcity_1 goes from 3 / 5 of 650,519 photons only one side has to none (and none in a full log of 9,325,494 cast photons); headlong, mainmenu and cyclotron stay identical. The bake changes only where the new rule acts: on the same capture, 1.4.2 gives 1.4.1's lightmap file for file on headlong, cyclotron, mainmenu and triplicate; earthcity_1 changes in a few texels. Accuracy against the CPU farm reference (Super, floored / un-floored, 1.4.1 -> 1.4.2): earthcity_1 99.562886% / 98.292238% -> 99.562884% / 98.292236%; headlong 99.72913% / 98.45848%, cyclotron 99.71962% / 98.44898% and mainmenu 97.14666% / 95.87601% unchanged. Speed is unchanged: the cast (1.4.1 / 1.4.2, same capture) earthcity_1 12.2 s / 12.7 s, headlong 10.0 s / 10.1 s, cyclotron 10.2 s / 10.2 s, mainmenu 8.3 s / 8.2 s, triplicate 4.7 s / 4.6 s; the GPU part of the bake (single runs) earthcity_1 249 s / 237 s, headlong 152 s / 153 s, cyclotron 81 s / 82 s, mainmenu 298 s / 306 s, triplicate 100 s / 101 s.
- **Checks.** Engine (`engine_v390`): the Custom engine reproduces the main engine on cyclotron, headlong and mainmenu; the AO engine reproduces the main engine; the light probes of triplicate hold 1.4.1's SH light (100.000% at the worst probe). The cast was compared with the original tool's own cast on four maps (above). With the 1.4.1 rule switched back on, the new engine gives 1.4.1's lightmap file for file on earthcity_1, headlong, cyclotron, mainmenu and triplicate. Through the app, on the release candidate: Custom and ambient occlusion (cyclotron) and light probes (triplicate) give stages equal to 1.4.1's, and the MCC `.map` bake (headlong) gives 1.4.1's output map; a Vista map built with the Halo 2 Editing Kit and the stock Vista triplicate both end with exit 0; inputs and live tags unchanged. The lightmap-reference repoint was checked end to end on a copied scenario: ambient occlusion applied, reference repointed, the scenario in the backup, and restore puts the old reference back. This exe carries the candidate's payload file for file apart from the version-stamped bake program; on it the MCC `.map` bake gives the candidate's output map and the CPU test suite passed (core 119, shell 135, S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the UI tests ran on this exe too, 3/3).
- **First start.** 1.4.2 unpacks its own engine folder (`GPU Work\_runtime\1.4.2-862678f0`) once, about 165 MB per H2EK; the 1.4.1 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Known and not fixed: a real card under 6 GB has not run a bake. Of the two items listed under 1.0.1, the one about 4 GB cards was addressed in 1.4.1; the other still applies.

## 1.4.1 (2026-11-18)

Exe SHA-256 `9fea9b31f393092727571cf19fdad3928bd55e6d1329bcefbe7e4685857f0ad5` (27,028,480 bytes).

- **Faster: the photon cast of sky and surface lights.** Every bake casts the photons of sky lights and surface lights the way the original tool does, light by light. That step is much faster: its scheduler was reworked (small lights share batches, each light's trial run is sized to the light, work slots are pooled), its ray tracer now walks a tree per object instead of testing every block of triangles, and its GPU programs use single-precision arithmetic only. A map with thousands of lights gains the most: on a custom Vista map with 6,485 sky and surface lights the cast went from 1,288 s to 82 s and the whole `.map` bake from 1,637 s to 440 s (RTX 4090, Super). The cast on the test maps, 1.4.0 against 1.4.1 on the same capture: earthcity_1 58.6 s -> 12.6 s, headlong 42.2 s -> 10.0 s, cyclotron 25.2 s -> 10.8 s, mainmenu 27.9 s -> 8.3 s, triplicate 20.4 s -> 4.6 s; the GPU part of the bake: earthcity_1 271 s -> 228 s, headlong 185 s -> 161 s, cyclotron 104 s -> 90 s, mainmenu 314 s -> 298 s, triplicate 125 s -> 108 s. In one measurement the first bake after a restart of the PC took about 13 s longer, once (as far as we can tell the graphics driver preparing the new, larger GPU programs).
- **Closer to the original tool: which triangle a photon hits.** Compared photon by photon with the original tool's own cast, 1.4.0 differed in a few photons where a ray grazes the edge of a triangle or where two objects have triangles in exactly the same place. The new ray tracer follows the tool's rules there: its cluster and cell lists, and its order when two hits are at the same distance (the object the tool made later wins). Photons only one side has (tool / GPU), with 1.4.0's rule and with 1.4.1: headlong 15 / 11 of 1,790,910 -> none; mainmenu 1 / 1 of 88,308 -> none; cyclotron none -> none; earthcity_1 228 / 219 of 650,519 -> 3 / 5 (the rest there starts at an object placed with no rotation or scaling; not fixed). So lightmaps change in a small number of texels on maps that have such rays: of the test maps earthcity_1, headlong and triplicate differ from 1.4.0's, cyclotron and mainmenu come out file for file the same. Accuracy against the CPU farm reference barely moves (Super, same capture, floored / un-floored, 1.4.0 -> 1.4.1): earthcity_1 99.56259% / 98.29194% -> 99.56289% / 98.29224%; headlong 99.72908% / 98.45843% -> 99.72913% / 98.45848%; cyclotron 99.71962% / 98.44898% and mainmenu 97.14666% / 95.87601% unchanged.
- **New: graphics cards with about 4 GB.** Up to 1.4.0 a card with about 4 GB of video memory could not bake at all: the part planner kept a flat 1.5 GiB free for Windows, what was left was smaller than the bake's fixed need, and it stopped with "a single row band of chart 0 ... past the per-part limit (video memory)" and the wrong advice to add portals. On such a card the engine now takes its reserve from what Windows really leaves free and cuts the bake into more, smaller parts; the window and the log say so ("small graphics card ... the bake runs in many small parts"). When even that does not fit, the app says before the bake that the card has too little free video memory and that portals or a BSP split do not help. Cards with 6 GB or more are planned exactly as before. Checked with a pretend 3,935 MiB card on an RTX 4090: headlong at Super bakes in 24 parts in 240 s and gives the single-pass lightmap. No real 4 GB card has run it.
- **Fixed: one ray tracer for the whole bake.** In a bake cut into parts, a part with fewer than a million rows was traced by the D3D11 tracer while the other parts used the ray-tracing cores. The two tracers agree to about one part in a million, and that is what made one small part's per-vertex rows differ from the others' (the merge stop 1.4.0 worked around). The tracer is now chosen once per bake, so a bake in many parts gives the single-pass result.
- **Less disk use: run folders.** Each bake's run folder (`GPU Work\standalone\runs\...`) holds copies of the baked lightmap: its install backup `tagbak\` and its staging folders, about 260 MB per run on a large map. They were kept for the newest 10 runs of each BSP and are now kept for the newest 3. The small log bundle of a run is still kept for the newest 10 runs, `bake.log` and `status.json` for every run. "Restore previous lightmap" is not affected: it uses `_TAGBAK_ui` (the original and the newest 5). The older copies are removed the next time the app starts a bake.
- **Checks.** Engine (`engine_v389`): the Custom engine reproduces the main engine on cyclotron, headlong and mainmenu; the AO engine reproduces the main engine; the light probes of triplicate hold the same SH light as 1.3.0's (100.000% at the worst probe; the probe values themselves move with the few cast photons). The cast was compared with the original tool's own cast on four maps (above). With the old hit rule switched back on, the new engine gives 1.4.0's lightmap file for file on headlong, cyclotron, mainmenu and triplicate; on earthcity_1 it differs from 1.4.0 in 26 of 1.64 million cast photons (rays that lie in the plane of a triangle: the old tracer could accept such a triangle far outside its edges, the tree walk never visits it). Through the app, on the release candidate: Custom and ambient occlusion (cyclotron) give stages equal to 1.2.0's; light probes (triplicate) and the MCC `.map` bake (headlong) differ from 1.2.0's by the new hit rule only, shown by that control; a Vista map built with the Halo 2 Editing Kit and the stock Vista triplicate both end with exit 0; inputs and live tags unchanged. This exe carries the candidate's payload file for file apart from the version-stamped bake program; on it the MCC `.map` bake gives the candidate's output map and the CPU test suite passed (core 119, shell 135, S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the UI tests ran on this exe too, 3/3).
- **First start.** 1.4.1 unpacks its own engine folder (`GPU Work\_runtime\1.4.1-8a912fc5`) once, about 165 MB per H2EK; the 1.4.0 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Known and not fixed: earthcity_1 still differs from the original tool's cast in a few photons (above). A real card under 6 GB has not run a bake. Of the two items listed under 1.0.1, the one about 4 GB cards is what this version addresses; the other still applies.

## 1.4.0 (2026-11-16)

Exe SHA-256 `299e671bc752b3103350d6b8364bc43645f160fa79a39fb4e691b82a0a44179b` (26,777,088 bytes).

- **Faster: light probes.** The light-probe pass used to make its own capture and then repeat the photon cast and the radiance lattice the lightmap bake had just made (the two are bit-identical). It now runs on the lightmap bake's own capture and reuses both, and the slow search loop of the step that composes the probe cubes is native code. The probe values are byte-identical to 1.3.0's. Measured on triplicate (RTX 4090): the probe pass at Custom 50 M photons / 32 rays went from 121 s to 25 s (whole bake 336 s to 211 s); on an Old Mombasa cinematic BSP the probe pass takes 28 s against 230 s the old way. Through the app at Super, the Standard + probes bake of triplicate: 173 s (1.2.0: 249 s). "Reuse existing capture" works with the single capture; a capture made by an older version still bakes its probes the old way, and so does a bake with the Scenery Multi Permutation Fix.
- **New option: fill wide dark holes.** Advanced settings > "Fill wide dark holes (goes beyond the original tool)", command line `--fill-wide-holes`. Where every sample of a lightmap texel looks like it is inside geometry, the texel gets no light of its own; the original tool fills such texels from their lit neighbours in two passes and leaves a wider patch black, and so does this app by default. With the option the fill goes on inward (up to 16 more passes, each texel gets the average of its filled neighbours). On triplicate at Super the two passes leave 9,749 of 33,784 such texels black; the option fills 6,787 of them and changes no texel that was lit. What is left lies on surfaces with no lit texel at all and stays black. It is not what the original tool does, so it is off by default; with it off the lightmap is exactly 1.3.0's.
- **Fixed: light probes on maps with several skies.** The probe bake of a map with more than one sky stopped with an error in every earlier version (one file was read from the wrong offset). Such maps now bake their probes; the probe colours there have not been compared with the original tool's.
- **Fixed: objects that mix foliage and other materials.** An instanced object lit per vertex whose materials mix foliage and non-foliage (for example a tree whose trunk and leaves are one object) made the app's own tag writer refuse the whole map: the bake fell back to the slower original-tool write-back and could not get ambient occlusion or light probes. The writer now blends per vertex by the vertex's own material, as the original tool does. Checked against the original tool's own output of the same bake (01a_tutorial, 01_bsp_0): the leaf vertices match it bit for bit as often as every other per-vertex object of the map does (83.21% against 82.3%; 3.8% without the fix), and the lightmap bitmaps are the tool's.
- **Fixed: a bake in many parts could stop at the merge.** A bake that is cut into many parts (little video memory) could stop at the end with "vertex_out.bin DIFFERS between parts". One small part's per-vertex rows could come out a float rounding step away from the other parts' (colours by a few parts per million). The merge now keeps the version the other parts agree on, which is byte for byte the single-pass result, and still refuses any real difference.
- **Clearer messages.** A graphics card too small for the engine (about 4 GB) is told so before the bake starts, instead of being advised later to add portals; and a Lightmap Only bake now also warns about instanced objects that hold no visible geometry (for example an object made only of portal faces).
- **Checks.** Engine: with the new option off the v387 engine gives 1.3.0's lightmap bit for bit (D3D11 and RT); the AO engine reproduces the main engine; the Custom engine reproduces the main engine on cyclotron, headlong and mainmenu; the probe cubes of the new flow are md5-identical to 1.3.0's. Through the app, on the release candidate: Custom, ambient occlusion and light probes (cyclotron / triplicate) give stages equal to 1.2.0's, the MCC `.map` bake gives 1.2.0's output map md5 for md5, and a Vista map built with the Halo 2 Editing Kit and the stock Vista triplicate both end with exit 0, inputs and live tags unchanged. This exe carries the candidate's payload file for file apart from the version-stamped bake program; on it the MCC `.map` bake gives the candidate's output map and the CPU test suite passed (core 119, shell 135, S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the UI tests ran on this exe too, 3/3).
- **First start.** 1.4.0 unpacks its own engine folder (`GPU Work\_runtime\1.4.0-c74769be`) once, about 150 MB per H2EK; the 1.3.0 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Known and not fixed: the two items listed under 1.0.1 still apply.

## 1.3.0 (2026-11-15)

Exe SHA-256 `0726156a77d0a70ebee3cfb6b973a11cd5acbaa2f3614f2439cda6eae091ca07` (26,727,936 bytes).

- **New: an app icon.** The app has its own icon (Explorer, the taskbar, Alt-Tab and the window): the Master Chief helmet with Halo 2's lens flares in its visor. It uses assets from Halo 2 under Microsoft's Game Content Usage Rules; the notice is in NOTICES.txt and in the README.
- **Checks.** The lightmapping engine is 1.2.0's (`engine_v386`): the payload equals the 1.2.0 release's file for file, apart from the version-stamped bake program and NOTICES.txt, so 1.2.0's engine and through-the-app checks carry over. On this exact exe the CPU test suite passed (core 119, shell 131, S1.9 12 incl. determinism, T0/T1 12, DLL slots 31, mapbake / AO / wording checks; the two window tests the suite skips ran on this exe too, 6/6 with the other UI tests), including the window loading with the icon.
- **First start.** 1.3.0 unpacks its own engine folder (`GPU Work\_runtime\1.3.0-8e1359d1`) once, about 150 MB per H2EK; the 1.2.0 folder next to it can be deleted. The Python runtime folder of 1.0.0 is reused.
- Nothing else changed: the same engine, lightmaps, options, quality levels, command line and work folders as 1.2.0.

## 1.2.0 (2026-11-15)

Exe SHA-256 `8def26e4d42b022a36d7910674c33a0f8dc378d0d206b09b1453e4e1d5354c5f` (26,457,088 bytes).

- **Fixed: dark splotches along creases and edges.** Where every sample of a texel looks like it is inside geometry (typically where two surfaces meet), the texel gets no light of its own. The original tool fills such a texel from its lit neighbours, in two passes over the 3x3 texels around it. The GPU lightmapper copied the way the tool's 28-process farm does this fill, where each process only sees every 28th texel, so almost none were filled and they stayed black: dark splotches along creases and edges. The fill now runs over the whole chart, as a single tool process does, which is what Bungie's own lightmaps show. On triplicate: 20,057 such texels; 19,631 stayed black before, 5,334 now (clusters too wide for a two-pass fill; the original tool leaves those dark too).
- **Changed: double precision for the distance test and the sample position.** The ray-to-surface distance test and each texel's sample position are now computed in double precision inside the GPU programs (the original Halo 2 Vista tool computes them in double; the MCC tool in single). The per-texel noise lands differently; the brightness does not change. Cost on triplicate: about 1 second with RT, about 7 seconds without.
- **New: the H2EK folder is easier to see.** The window shows the H2EK folder it bakes in much more clearly: its name in large type in the Halo 2 Editing Kit box, the full path right above the Bake button ("This bake writes to ..."), in the window title and in the confirm dialog.
- **Accuracy.** Lightmaps change where these texels are. The CPU reference the accuracy numbers are measured against is a 28-process farm bake, which leaves those texels black, so filling them lowers the score against it by about a quarter of a point; the double-precision change alone moves it by less than 0.01 of a point. Accuracy against the CPU farm reference, Super, one fresh capture per map: headlong 99.73% floored / 98.46% un-floored (1.0.2 on the same capture: 100.00% / 98.73%; 1.2.0 with the farm fill: 99.99% / 98.72%), cyclotron 99.72% / 98.45% (1.0.2: 99.96% / 98.69%; farm fill: 99.96% / 98.69%).
- **Checks.** Through the app, on the release candidate (whose payload this exe carries file for file, apart from the version-stamped bake program): Custom, ambient occlusion and light probes (cyclotron / triplicate), the MCC `.map` bake, a Vista map built with the Halo 2 Editing Kit and the stock Vista map triplicate all end with exit 0, with the input maps and the live tags unchanged and the composer using the single-process fill. Engine gates: the AO engine reproduces the main engine (bake products keyed-identical), the Custom engine reproduces the main engine on cyclotron, headlong and mainmenu (D3D11 and RT), and the light-probe light equals 1.0.2's (spherical-harmonics recon 100.000%; 1.0.2's probes are the ones that matched the CPU at 99.53%). On this exact exe: the MCC `.map` bake gives the same output map as on that candidate (md5-identical), and the CPU test suite passed.
- **First start.** 1.2.0 unpacks its own engine folder (`GPU Work\_runtime\1.2.0-05ae31da`) once; the Python runtime folder of 1.0.0 is reused.
- **Known and not fixed:** the two items listed under 1.0.1 still apply.
- Nothing else changed: the same options, quality levels, command line and work folders.

## 1.0.2 (2026-11-14)

Exe SHA-256 `71898cfd27fc2f9895a4168d810ade53ea5df75db485a28bd1f5a81f41127e41` (26,436,096 bytes).

- **Fixed: a `.map` bake of an original Halo 2 Vista map was refused at the end.** A `.map` bake of an original Halo 2 Vista map ran to the end and was then refused: "REFUSED: the injection touched more than the lightmap and its bitmap (replaced 2, added 1 ...)", exit 3, and no map was written. A map made with the original Halo 2 Vista tools has no true-colour lightmap bitmap; the maps that came with the game are such maps. The baked bitmap is therefore added to the map, not put in the place of an old one, and the last check of the `.map` path counted every added tag as an error. Every version since 0.6.7 did this. The check now accepts exactly that one addition, the lightmap's own true-colour bitmap next to the structure BSP and the lightmap that are replaced, and still refuses any other added or replaced tag. Tested on the stock Vista map triplicate (Standard, Super): the bake ends with exit 0, the result passes the Vista file check, and the map loads and looks right in Halo 2 Vista (the map loaded in the game was baked by a development build with the same fix; the release build gives the same file, byte for byte, except for the time of writing in the file header). The result is larger than the input (39,948,800 to 56,076,800 bytes).
- **Other maps and scenario bakes are unchanged.** A map that already holds the bitmap (a map built with the Halo 2 Editing Kit, in MCC or Vista form) takes the same path as before, and a scenario bake does not use this check. Of the 1,166 files inside the exe, four differ from 1.0.1: the map-bake script and the three copies of the bake program `h2lm-bake.exe`, which carry the version number (their source did not change). The GPU programs are the same files.
- **Checks.** The check is covered by a new unit test (23 cases on the changed-tag lists of two real maps), an original Vista map is now one of the release gates, and the CPU test suite passed on this build. Through the app, on this exact exe: the MCC `.map` gate gives the same output map as 1.0.1 (md5-identical); a Vista map built with the Halo 2 Editing Kit bakes and comes back as a Vista map as before; the stock Vista map triplicate bakes with exit 0 and the log names the added bitmap (byte for byte the map that was checked in the game, except for the time of writing in the file header). The three scenario gates (Custom, AO, light probes) were not repeated: no file they use changed (see the file-by-file comparison above). One scenario bake of a custom map (Super with ambient occlusion, stage only) was run through this exe and through the 1.0.1 exe: the staged tags are the same under the comparison the release gates use, which sets aside the few bytes that differ between any two runs (the lightmap tag itself is byte-identical).
- **First start.** 1.0.2 unpacks its own engine folder (`GPU Work\_runtime\1.0.2-6d167265`) and its map-bake scripts once; the Python runtime folder of 1.0.0 is reused.
- **Known and not fixed:** the two items listed under 1.0.1 still apply.
- Nothing else changed: the same options, quality levels, command line and work folders.

## 1.0.1 (2026-11-13)

Exe SHA-256 `bfb4397d5ea6c2b6afac3495222d71d1dbe2ee16a893d7bcdefe77e9c0c50fa7` (26,436,096 bytes).

- **Fixed: instanced geometry placed exactly at the world origin.** An instanced-geometry instance at position 0, 0, 0 with no rotation and scale 1 was not recognised as an instance. This is common in levels made in Blender, where a `%` object is often left at the origin. Three things followed: the object-grid rebuild refused the level ("build_objgrids.py REFUSED ... root element count"), the bake continued with the older instanced model for the sky lights and listed that under "NOT THE VALIDATED CONFIGURATION", and the instance's triangles were traced as level geometry and not through the instance's own grid. The engine now tells an instance by its place in the instance table and not by its matrix, as the original tool does, so such a level takes the validated path like every other level. On the test level the rebuilt root grid equals the tool's own, element for element (24 of 24), and the sky lights run on the full chain. Compared with 1.0.0 on that level (Super with AO), both lightmap bitmaps and every vertex colour are identical; fewer than a hundred per-vertex light directions differ, in the last float digit (at most 2.4e-7). The fix removes the fallback; on this level it does not change the picture.
- **Levels without such an instance are unchanged.** On the release's test maps the baked lightmap tags are byte-identical to 1.0.0's: Custom, AO, light probes and the `.map` path.
- **The GPU programs were rebuilt for the fix.** Only the host program around the GPU kernels changed; no kernel changed. `fullgpu.exe` is md5 `0b7fefb0` (1.0.0: `f04032ae`) and the AO engine's is `ebf4f31a` (1.0.0: `d1ac121c`). Every engine gate was run again for them: the AO neutrality gates, the light-probe gate and the Custom-quality guard's identity gate (cyclotron, headlong, mainmenu).
- **First start.** 1.0.1 unpacks its own engine folder once (`GPU Work\_runtime\1.0.1-2e35a0e4`); the Python runtime folder of 1.0.0 is reused.
- **Known and not fixed:** On a graphics card with about 4 GB the part planner can stop with "a single row band of chart 0 ... past the per-part limit (video memory)" and advise adding portals or splitting the BSP. In that case the advice is wrong: the fixed memory the bake needs is larger than what the planner allows itself on that card (the card minus a 1.5 GiB reserve), and no change to the level helps. Cards under 6 GB remain untested. A scenario bake in Lightmap Only mode still does not log the empty-instanced-geometry warning (the window shows it).
- Nothing else changed: the same options, quality levels, command line and work folders.

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
