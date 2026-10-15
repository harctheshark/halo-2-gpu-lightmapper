# N4: scipy-free AO apply path (plan S1.8, re-made and re-gated by A1 on 2026-10-15)

The AO apply step (`_research\ao\gpu_ao.py apply`) imports two modules that use scipy, and N4 makes both run on numpy alone, so the
standalone Python bundle can ship without scipy 1.15.3:

| Production file | scipy use | N4 replacement |
|---|---|---|
| `ao_seamfix.py` (seam filter, on by default) | `cKDTree.query_ball_point` inside `seam_pairs` | `ao_ballgrid.ball_pair_keys` |
| `ao_denoise.py` (A1: new with the AO-noise workflow; `--ao-smooth`, `--ao-shrink`, the noise / detail metrics, selftest) | `cKDTree.query_ball_point` inside `bilateral_raw` | `ao_ballgrid.ball_blocks` (directed lists, blocks per radius level) |

`ao_ballgrid` is an exact numpy uniform-grid radius search (one grid level per power-of-two radius band, the same float64 leaf test as
cKDTree: `((dx*dx + dy*dy) + dz*dz) <= r*r`, d = data - query). Its docstring has the argument for why the +-1 cell neighbourhood is a
superset of the ball.

## Files
| File | What |
|---|---|
| `ao_seamfix.py.patch` | strict patch against production `GPU Lightmap Work\_research\ao\ao_seamfix.py` (pristine md5 `93c2fe8c`, CRLF), 2 hunks. The stacked-layer rule (`--seam-stack-eps`, default 0.004) is production's own and stays. |
| `ao_denoise.py.patch` | strict patch against production `_research\ao\ao_denoise.py` (pristine md5 `5da70c2f`, CRLF), 3 hunks. |
| `add\ao_ballgrid.py` | a new module, shipped **beside** the two patched files at INST`\_research\ao\ao_ballgrid.py`. `ball_pair_keys` is the S1.8 function untouched; `ball_blocks` is new (A1). |
| `N4_MANIFEST.json` | `targets` (pristine / patch / patched md5 per file, `base_file` = the project's pinned copy under `patches\base\_research\ao\`), `adds`, and the gate verdict. `ship` is true only after `tests\a1_ao_fix\ab_n4.py verdict` wrote PASS. The S1.8 record is kept under `previous`. |
| `.gitattributes` | `* -text`, so git never rewrites line endings and the md5s stay true. |

Re-make after production moves: `py -I -B tests\a1_ao_fix\remake_n4.py` (exact-count edits on the new production files; it asserts the
pinned md5s, so a further edit of either file refuses until the script is reviewed), then `py -B tests\a1_ao_fix\ab_n4.py all`.

## How the payload builder applies it (`tools\payload\stage_engine.stage_n4`)
1. Refuse unless the manifest says `ship` + gate PASS.
2. For each target: copy production, assert md5 == `pristine_md5` (production moved -> stop and re-make + re-gate), assert the pinned
   base copy has the same md5, apply the patch strictly (no fuzz, no offset), assert the result md5 == `patched_md5`.
3. Copy `add\ao_ballgrid.py` next to them and assert its md5.

## What else in the pipeline uses scipy (checked 2026-10-15)
After N4 the shipped path is scipy-free: gpu_ao `trace` / `apply` / `stage` (with or without the denoise options), the v043 tag writer
(`lmw_grid.py`), engine_v373 and the lightprobe driver. The only remaining `import scipy` lines are research code the shell never calls:
`_research\ao\ao_seam_diag.py` (gpu_ao's `seams` metric verb, via ao_seam_report) and `_research\lightprobes\mainmenu\scn_analysis\an8.py` /
`an16.py`.

## Behaviour
Identical to production: the same pair set K, the same weights, the same `atlas_out.bin`, the same `ao_occ.bin`, the same
`bilateral_raw` outputs (gate results in `tests\a1_ao_fix\results\`). There is no env switch. `ball_pair_keys` / `ball_blocks` raise
ValueError on non-finite positions or radii (cKDTree has no defined result for them).
