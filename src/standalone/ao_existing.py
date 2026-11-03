"""ao_existing.py -- AO ONLY: add ambient occlusion to the lightmap that is ALREADY in the tags (0.6.8 RC xc8).

The lighting is NOT baked again. The lightmap set that is installed for the scenario / BSP (ours, the stock tool's or Bungie's) is read, decoded and darkened
by the same occlusion the full bake's AO step uses, and written back as a normal stage (lightmap tag + truecolor bitmap + P8 bitmap, tagw_report.json) that the
shell validates and installs through lmw_install.py (backup first). Nothing is written to the tags here.

  stage    --farm FARM --scenario S --bsp B --engine DEV_AO_ENGINE --h2-root ROOT --tool TOOL_FAST.EXE --strength X --exponent Y --tag TAG
           [--records FILE] [--force] [--overwrite] [--out DIR]
           FARM = a farm with a capture of this BSP and its AO trace (gpu_ao trace). OUT (default FARM\\aoonly\\TAG) receives stage\\, apply\\ and
           aoonly_report.json. Exit 0 stage written, 2 refused in plain words (nothing written), 1 unexpected error.
  selftest the decode / encode round trip and the multiplier arithmetic (no files, no GPU).

How it works (details: docs\\unreleased_0.6.8\\AO_ONLY_DESIGN.md)
  * The occlusion per texel is the full bake's: gpu_ao `apply` runs unchanged on a synthetic farm built from the existing lightmap's own pixels (so the
    seam filter and every AO option are the full bake's); its per-texel result gives the multiplier  M = share_direct * (1 - s*0.5*occ) + (1 - share_direct) * (1 - s*occ)
    with share_direct = 0.5 (a finished lightmap does not store direct and indirect apart: the same "mean of the two fractions" rule gpu_ao uses for the per-vertex and
    scenery rows, whose D+I is all that is stored).
  * AO is applied to the PAGE PIXELS: pixel = encode(decode(pixel) * M). The texel -> pixel map comes from the lightmap's own vertex UVs (the existing layout is kept
    exactly: no chart is moved, mip levels do not change), so a pixel that stands for several texels takes the mean multiplier of the texels it covers.
  * P8 is regenerated from the new truecolor pages with the tag writer's own palette grouping; render info / palette block are updated. UVs, per-vertex directions and
    the geometry buckets are the existing lightmap's (not recomputed).
Refuses (plain words, nothing written): the lightmap is missing / unreadable, does not belong to this BSP (structure checksum, section counts), its page list is not what
the capture's charts give, its pixels do not sit where the capture's charts say, or AO is already applied (the install record says so; --force overrides)."""
import argparse
import hashlib
import json
import os
import struct
import subprocess
import sys
import time
import types
import zlib

import numpy as np

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
F = np.float32
FLAGS = 0x08000000 | 0x00004000           # CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS
DIRECT_SHARE = 0.5                         # unknown split: the mean of the direct (0.5) and indirect (1.0) fractions, as for gpu_ao's per-vertex rows
DIRECT_FRACTION, INDIRECT_FRACTION = 0.5, 1.0
MAP_OK_MIN = 0.98                          # a page whose mapped texels land on covered pixels less often than this is not trusted
MAP_PAGE_SHARE_MIN = 0.985                 # share of the occluded texels that must sit on trusted pages
M_AOV1 = 0x31564F41


class Refused(Exception):
    pass


def say(m):
    print('[ao_existing %s] %s' % (time.strftime('%H:%M:%S'), m), flush=True)


def md5f(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 22), b''):
            h.update(c)
    return h.hexdigest()


def md5b(b):
    return hashlib.md5(b).hexdigest()


def crc32f(p):
    c = 0
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 22), b''):
            c = zlib.crc32(b, c)
    return c & 0xFFFFFFFF


# ------------------------------------------------------------------------------------------------ install record (double-AO protection)
def load_records(path):
    try:
        j = json.load(open(path, encoding='utf-8'))
        return j.get('sets', []) if isinstance(j, dict) else []
    except (OSError, ValueError):
        return []


def find_record(records, md5s):
    """the record whose three installed-file md5s are exactly the md5s of the set that is installed now (None = no AO is known to be in this lightmap)"""
    for r in reversed(records):
        f = r.get('files', {})
        if f and all(md5s.get(k) == v for k, v in f.items()) and len(f) == len(md5s):
            return r
    return None


def record_text(r):
    return 'AO %s, strength %s, exponent %s, %s' % ('only' if r.get('mode') == 'ao-only' else 'with a full bake', r.get('strength'), r.get('exponent'), r.get('utc', 'earlier'))


# ------------------------------------------------------------------------------------------------ pixel decode / encode (the writer's own constants)
def dec_lin(q, LA):
    """uint8 (..., 3) R,G,B  ->  float64 linear light (..., 3) such that encode(dec_lin(q)) == q (the tag writer's encode_output_luxel_rgb in reverse:
    gamma 2.2, then the Schlick luminance curve  q = k t / ((k-1) t + 1),  t = (Y - black) / (white - black))."""
    return dec_lin_enc(np.asarray(q, np.float64) / 255.0, LA)


def dec_lin_enc(e, LA):
    """the same for an ENCODED float (..., 3) in [0,1] (a vertex-lit bucket record keeps the unquantised encode)"""
    e = np.asarray(e, np.float64)
    lin = np.power(e, 1.0 / float(LA.GEXP))
    yn = lin @ np.array([0.212671, 0.71516001, 0.072168998])
    k, wh, bl = float(LA.KSCH), float(LA.WHITE), float(LA.BLACK)
    with np.errstate(all='ignore'):
        t = yn / (k - (k - 1.0) * yn)
        t = np.where(yn >= 1.0, 1.0, np.clip(t, 0.0, 1.0))
        y = bl + t * (wh - bl)
        sc = np.where(yn > 1e-12, y / yn, 0.0)
    return lin * sc[..., None]


def enc_q(lin, LA):
    """float linear (..., 3) -> uint8 R,G,B (the writer's encode + quant)"""
    return LA.quant(LA.encode(np.asarray(lin, F)))


def multiplier(occ, strength, share=DIRECT_SHARE, df=DIRECT_FRACTION, if_=INDIRECT_FRACTION):
    """the AO delta form new = d*md + i*mi with an unknown split: M = share*md + (1-share)*mi (gpu_ao apply's md / mi, vectorised)"""
    occ = np.asarray(occ, np.float64)
    md = np.maximum(0.0, 1.0 - strength * df * occ)
    mi = np.maximum(0.0, 1.0 - strength * if_ * occ)
    return share * md + (1.0 - share) * mi


# ------------------------------------------------------------------------------------------------ modules from the engine's tag writer
class Mods:
    pass


def load_mods(engine):
    tw = os.path.join(os.path.abspath(engine), 'v043-tagwriter')
    if not os.path.isfile(os.path.join(tw, 'lmw_tagio.py')):
        raise Refused('the engine folder %s has no tag writer (v043-tagwriter)' % engine)
    sys.path.insert(0, tw)
    import lmw_tagio, lmw_charts, lmw_atlas, lmw_grid, lmw_vertex, lmw_scenery   # noqa: E402
    m = Mods()
    m.TIO, m.CH, m.LA, m.LG, m.VX, m.SCNW, m.tw = lmw_tagio, lmw_charts, lmw_atlas, lmw_grid, lmw_vertex, lmw_scenery, tw
    return m


def find_build_vertdir(ao_dir, engine):
    for d in (os.path.join(os.path.abspath(engine), '..', '..', '..'), os.path.join(os.path.abspath(ao_dir), '..', '..'), os.path.join(os.path.abspath(ao_dir), '..'), HERE,
              os.path.join(HERE, '..', '..')):
        p = os.path.abspath(os.path.join(d, 'build_vertdir.py'))
        if os.path.isfile(p):
            return p
    raise Refused('build_vertdir.py (the texel -> triangle rasteriser) was not found beside the engine')


def import_file(path, name):
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ------------------------------------------------------------------------------------------------ reading the installed set
def read_pages(data, info, wanted_fmt):
    lo, _ = info['lump']
    out = []
    for p in info['pages']:
        if p['fmt'] != wanted_fmt:
            raise Refused('the bitmap has a page in format %d (expected %d): not a lightmap page set this tool writes' % (p['fmt'], wanted_fmt))
        n = p['w'] * p['h'] * (4 if wanted_fmt == 11 else 1)
        a = np.frombuffer(data, np.uint8, n, lo + p['off']).copy()
        out.append(a.reshape(p['h'], p['w'], 4) if wanted_fmt == 11 else a.reshape(p['h'], p['w']))
    return out


NB8 = [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)]   # lmw_atlas.NB, the order the tool fills a gutter pixel from a neighbour


# ------------------------------------------------------------------------------------------------ chart-space -> page pixel map (from the lightmap's own vertex UVs)
def corner_pixels(M, LM, INST, TR, tch, chart_page, NCP, cl_with, lm_inst, tci):
    """-> (T, 3, 2) float64 page-pixel coordinates of every soup triangle corner (NaN where the lightmap has no vertex for it).
    The same triangle matching the tag writer's VertexStage.assign_uv makes (soup corners vs the section's vertices by position, candidates ranked by normal),
    read in reverse: the vertex's stored page UV (floats 36, 37) times the page size."""
    LG = M.LG
    T = len(TR)
    out = np.full((T, 3, 2), np.nan)
    tp = chart_page[tch]
    order = np.argsort(tp, kind='stable')
    tps = tp[order]
    cuts = np.flatnonzero(np.diff(tps)) + 1
    groups = np.split(order, cuts) if T else []
    n_tri_match = 0
    for g in groups:
        p = int(tp[g[0]])
        if p < 0 or p >= len(tci['pages']):
            continue
        if p < NCP:
            si = cl_with[p]
            V = LM.verts(si)
            Vw = V[:, 0:3].astype(np.float64)
            Nw = V[:, 19:22].astype(np.float64)
            tol = 1e-6
        else:
            i = lm_inst[p - NCP]
            si = LM.ncl + INST[i]['d']
            V = LM.verts(si)
            Mx = INST[i]['M'].astype(np.float64)
            Vw = Mx[0] * (V[:, 0:3].astype(np.float64) @ Mx[1:10].reshape(3, 3)) + Mx[10:13]
            Nw = V[:, 19:22].astype(np.float64) @ Mx[1:10].reshape(3, 3)
            tol = 2e-3
        if not len(V):
            continue
        Pw = TR[g][:, 6:15].reshape(-1, 3)
        NN = TR[g][:, 15:24].reshape(-1, 3, 3)
        Pw3 = Pw.reshape(-1, 3, 3)
        near = LG.Grid(Pw, tol).ball_many(Vw, tol)
        W_, H_ = float(tci['pages'][p]['w']), float(tci['pages'][p]['h'])
        uv = V[:, 36:38].astype(np.float64) * np.array([W_, H_])
        for t in LM.indices(si):
            sets = [set(int(q) // 3 for q in near[j]) for j in t]
            cand = sets[0] & sets[1] & sets[2]
            if not cand:
                continue
            best = None
            for tri in sorted(cand):
                corner = [int(np.argmin(np.linalg.norm(Pw3[tri] - Vw[j], axis=1))) for j in t]
                score = sum(float(np.dot(NN[tri, cj], Nw[j])) for cj, j in zip(corner, t))
                if best is None or score > best[0] + 1e-6:
                    best = (score, tri, corner)
            _, tri, corner = best
            if len(set(corner)) != 3:
                continue
            for cj, j in zip(corner, t):
                if np.isnan(out[g[tri], cj, 0]):
                    out[g[tri], cj] = uv[j]
            n_tri_match += 1
    return out, n_tri_match


# ------------------------------------------------------------------------------------------------ the stage
def build_synth_farm(synth, farm, rows_key, rgb):
    """a farm directory gpu_ao `apply` accepts: atlas_out.bin / atlas_split.bin rows (ord, tix, rgb) of the existing lightmap's own texels, an atlas_dir.bin
    (only checked for existence: the vertdir rebuild is skipped), and the capture's texelgen_soup.bin"""
    os.makedirs(synth, exist_ok=True)
    n = len(rows_key)
    ordl = (rows_key >> np.uint64(32)).astype(np.uint32)
    tix = (rows_key & np.uint64(0xFFFFFFFF)).astype(np.uint32)
    f32 = rgb.astype(np.float32)
    nch = 0
    ha = np.array([0x314C5441, n, 5, nch], np.uint32)
    ra = np.zeros((n, 5), np.uint32)
    ra[:, 0], ra[:, 1] = ordl, tix
    ra[:, 2:5] = f32.view(np.uint32)
    with open(os.path.join(synth, 'atlas_out.bin'), 'wb') as f:
        f.write(ha.tobytes())
        ra.tofile(f)
    hs = np.array([0x31534C41, n, 8, 0], np.uint32)
    rs = np.zeros((n, 8), np.uint32)
    rs[:, 0], rs[:, 1] = ordl, tix
    rs[:, 2:5] = (f32 * np.float32(DIRECT_SHARE)).view(np.uint32)
    rs[:, 5:8] = (f32 * np.float32(1.0 - DIRECT_SHARE)).view(np.uint32)
    with open(os.path.join(synth, 'atlas_split.bin'), 'wb') as f:
        f.write(hs.tobytes())
        rs.tofile(f)
    with open(os.path.join(synth, 'atlas_dir.bin'), 'wb') as f:
        f.write(np.array([0x32524944, 0, 6, 0], np.uint32).tobytes())
    dst = os.path.join(synth, 'texelgen_soup.bin')
    if os.path.exists(dst):
        os.remove(dst)
    try:
        os.link(os.path.join(farm, 'texelgen_soup.bin'), dst)
    except OSError:
        import shutil
        shutil.copyfile(os.path.join(farm, 'texelgen_soup.bin'), dst)


def run_gpu_ao_apply(ao_dir, synth, trace_dir, out_dir, a, engine):
    """gpu_ao.py `apply` unchanged (the full bake's occlusion: seam filter, donor texels, every default) on the synthetic farm. The vertdir rebuild
    (a subprocess of apply, useless here) is stubbed out."""
    sys.path.insert(0, ao_dir)
    import gpu_ao   # noqa: E402
    gpu_ao.run = lambda args, logf=None, cwd=None, env=None: (0, 0.0)
    argv = ['gpu_ao.py', 'apply', '--farm', synth, '--scenario', a.scenario, '--bsp', a.bsp, '--engine', engine, '--tag', out_dir, '--trace-dir', trace_dir,
            '--strength', repr(a.strength), '--exponent', repr(a.exponent), '--overwrite', '--no-vertex', '--no-scenery']
    old = sys.argv
    sys.argv = argv
    rc = 0
    try:
        gpu_ao.main()
    except SystemExit as e:
        rc = e.code or 0
    finally:
        sys.argv = old
    if rc:
        raise RuntimeError('gpu_ao apply failed (exit %s)' % rc)
    rep = json.load(open(os.path.join(out_dir, 'ao_apply_report.json')))
    occ = np.fromfile(os.path.join(out_dir, 'ao_occ.bin'), dtype=np.uint32)
    occ = occ[4:].view(np.float32).astype(np.float64)
    return occ, rep


def vertex_lit_multipliers(M, LM, INST, farm, trace, a, gpu_ao, meta_dist):
    """per vertex-lit (bucket flags 3) instance: the multiplier of every bucket record, from the tag writer's OWN 0x4A923A replay (VertexStage.vertex_lit) fed with
    synthetic query rows whose colour is the vertex multiplier mv = 1 - s * 0.5 (direct + indirect fraction) * occlusion (gpu_ao's rule for rows that store only D+I) and
    whose encode is the identity, so the replay's weighting (own sample, edge midpoints at 0.5, ...) gives the effective multiplier of each record.
    -> {instance: (first record, multipliers (n,))}, plus counts"""
    LA, VX = M.LA, M.VX
    vq_p = os.path.join(farm, 'vertex_q.bin')
    av_p = os.path.join(trace, 'ao_vertex.bin')
    if not (os.path.isfile(vq_p) and os.path.isfile(av_p)):
        return {}, dict(note='no vertex queries / no vertex trace: the vertex-lit records were left as they are')
    vq = np.fromfile(vq_p, dtype=np.uint32)
    n, rec = int(vq[1]), int(vq[2])
    Q = vq[4:4 + n * rec].reshape(n, rec).view(np.float32)
    av = np.fromfile(av_p, dtype=np.uint32)
    if int(av[1]) != n:
        raise RuntimeError('ao_vertex.bin has %d rows, vertex_q.bin %d: run the trace again' % (int(av[1]), n))
    if int(av[3]) != crc32f(vq_p):
        raise RuntimeError('ao_vertex.bin belongs to another vertex_q.bin: run the trace again')
    RAV = av[4:4 + n * int(av[2])].reshape(n, int(av[2]))
    o = gpu_ao.AoOptions(a.strength, meta_dist, a.exponent, 1.0, INDIRECT_FRACTION, DIRECT_FRACTION, 64).validate()
    vocc = gpu_ao.occ_from_raw(RAV[:, 0].view(np.float32), RAV[:, 1].view(np.float32), o)
    mv = np.maximum(0.0, 1.0 - a.strength * 0.5 * (DIRECT_FRACTION + INDIRECT_FRACTION) * vocc).astype(np.float32)
    serve = {}
    for k in range(n):
        key = Q[k, 0:3].tobytes()
        if key not in serve:
            serve[key] = np.array([Q[k, 0], Q[k, 1], Q[k, 2], mv[k], mv[k], mv[k], 0, 0, 0], np.float32)
    VS = VX.VertexStage(bytearray(), LM, INST, None, {}, {}, {}, [], None, serve, {}, 'fallback')
    saved = LA.encode
    LA.encode = lambda c: np.asarray(c, np.float32)           # the replay returns the record colour BEFORE the Schlick / gamma encode: the plain multiplier
    out = {}
    try:
        for i in range(LM.ninst):
            b, st = LM.bref[i]
            if LM.buckets[b]['flags'] != 3:
                continue
            dd = INST[i]['d']
            nw, ok = LM.vlit_count(LM.ncl + dd)
            if not ok:
                continue
            res = VS.vertex_lit(i, LM.verts(LM.ncl + dd))[:nw]
            out[i] = (st, res[:, 0:3].astype(np.float64).mean(1), res[:, 0:3].any(1))
    finally:
        LA.encode = saved
    return out, dict(queries=n, mean_vertex_occ=float(vocc.mean()), mean_vertex_multiplier=float(mv.mean()))


def darken_vertex_lit(M, LM, out_lm, mult):
    """scale the decoded colour of every vertex-lit bucket record by its multiplier and encode it again (the record keeps the unquantised encode, so this is exact)"""
    LA = M.LA
    n_rec = n_changed = 0
    dsum = lsum = 0.0
    for i, (st, m, has) in mult.items():
        b, _ = LM.bref[i]
        bk = LM.buckets[b]
        nw = len(m)
        e0 = bk['rec'].data + st * 24
        recs = np.frombuffer(bytes(out_lm[e0:e0 + nw * 24]), np.float32).reshape(nw, 6).copy()
        use = has & recs[:, 0:3].any(1) & (m < 1.0 - 1e-7)
        n_rec += nw
        if not use.any():
            continue
        lin = dec_lin_enc(recs[use, 0:3], LA)
        new = LA.encode((lin * m[use][:, None]).astype(F))
        lum = lambda x: (np.asarray(x, np.float64) @ np.array([0.2126, 0.7152, 0.0722]))
        dsum += float(lum(lin).sum() - lum(lin * m[use][:, None]).sum())
        lsum += float(lum(lin).sum())
        recs[use, 0:3] = new
        out_lm[e0:e0 + nw * 24] = recs.astype(F).tobytes()
        n_changed += int(use.sum())
    return dict(vertex_lit_records=n_rec, vertex_lit_records_darkened=n_changed, vertex_lit_mean_darkening=(dsum / lsum) if lsum > 0 else 0.0)


class Installed:
    pass


def open_installed(a):
    """gates 0-2 (no capture needed): the installed set exists, AO is not in it already, it parses, it belongs to this BSP -> an Installed with everything the later steps use"""
    ao_dir = os.path.abspath(a.ao_dir or HERE)
    if a.h2_root:
        os.environ['H2LM_ROOT'] = a.h2_root
    farm = os.path.abspath(a.farm) if getattr(a, 'farm', None) else None
    out = (os.path.abspath(a.out) if a.out else os.path.join(farm, 'aoonly', a.tag)) if farm else None
    stg = os.path.join(out, 'stage') if out else None
    M = load_mods(a.engine)
    TIO, CH, LA = M.TIO, M.CH, M.LA
    sys.path.insert(0, ao_dir)
    import gpu_ao   # noqa: E402
    sp = gpu_ao.scen_paths(types.SimpleNamespace(scenario=a.scenario, bsp=a.bsp, engine=a.engine, farm=farm, worker=0))
    pfx = '%s_%s_lightmap' % (sp['leaf'], a.bsp)
    tagdir = sp['tagdir']
    names = {'ltmp': pfx + '.scenario_structure_lightmap', 'tc': pfx + '_truecolor_bitmaps.bitmap', 'p8': pfx + '_bitmaps.bitmap'}
    paths = {k: os.path.join(tagdir, v) for k, v in names.items()}
    # ---- 0. the lightmap that is installed
    miss = [v for k, v in names.items() if not os.path.isfile(paths[k])]
    if miss:
        raise Refused('this scenario / BSP has no complete lightmap in the tags yet (missing: %s). AO only adds occlusion to an existing lightmap: bake the lightmap first '
                      '(Lightmap Only), then run AO only.' % ', '.join(miss))
    if getattr(a, 'tag', None) and os.path.exists(stg) and not a.overwrite:
        raise Refused('%s exists (use --overwrite)' % stg)
    md5s = {names[k]: md5f(paths[k]) for k in names}
    # ---- 1. double AO
    recs = load_records(a.records) if a.records else []
    rec = find_record(recs, md5s)
    if rec is not None:
        if not a.force:
            raise Refused('ambient occlusion has already been applied to the lightmap that is installed now (%s). Applying it again would darken the same places twice. '
                          'To start from the lightmap without AO, restore the backup made before that run (Restore previous lightmap), or bake the lightmap again; '
                          'to darken it a second time anyway, run AO only with --force (the window asks).' % record_text(rec))
        say('NOTE: AO was already applied to this lightmap (%s); --force: applying it again' % record_text(rec))
    D = TIO.defs()
    ok, det = D.pin_check(a.tool) if a.tool else (None, 'no --tool given: the field-definition pin was not checked')
    if ok is False:
        raise Refused('the tool in %s is not the build this program knows how to read and write lightmaps for (%s)' % (a.tool, det))
    lm_b = open(paths['ltmp'], 'rb').read()
    tc_b = open(paths['tc'], 'rb').read()
    p8_b = open(paths['p8'], 'rb').read()
    try:
        LM = TIO.LMView(lm_b, D)
        tci = TIO.bitmap_info(tc_b, D)
        p8i = TIO.bitmap_info(p8_b, D)
        bsp_b = open(sp['bsp'], 'rb').read()
        BSP = TIO.BSPView(bsp_b, D)
    except (TIO.TagIOError, OSError) as e:
        raise Refused('the installed lightmap (or the BSP) could not be read: %s. Nothing was changed.' % e)
    if not all(TIO.crc_ok(x) for x in (lm_b, tc_b, p8_b)):
        raise Refused('the installed lightmap files fail the tag checksum rule: they are damaged or were edited by hand. Nothing was changed.')
    # ---- 2. does it belong to this BSP
    bsp_eff = BSP.checksum if BSP.checksum else (next(iter(BSP.cl_checksums)) if len(BSP.cl_checksums) == 1 else None)
    if not (bsp_eff is not None and LM.checksum == bsp_eff and BSP.cl_checksums <= {bsp_eff}):
        raise Refused('the installed lightmap does not belong to this BSP (its structure checksum is 0x%08X, the BSP has 0x%08X): the BSP was compiled again after the lightmap was made. '
                      'Bake the lightmap again, then run AO only.' % (LM.checksum, bsp_eff or 0))
    if not (LM.ncl == BSP.ncl and LM.ndef == BSP.ndef and LM.ninst == BSP.ninst and LM.nibr == LM.ninst and LM.ncrinfo == LM.ncl):
        raise Refused('the installed lightmap has a different structure than this BSP (clusters %d/%d, definitions %d/%d, instances %d/%d): bake the lightmap again.' % (
            LM.ncl, BSP.ncl, LM.ndef, BSP.ndef, LM.ninst, BSP.ninst))
    bad_fmt = [i for i, p in enumerate(tci['pages']) if not (p['fmt'] == 11 and p['flags'] == 1)]
    if bad_fmt or len(p8i['pages']) != len(tci['pages']):
        raise Refused('the installed lightmap bitmaps are not a lightmap page set (truecolor %d pages, P8 %d pages, %d pages in another format).' % (
            len(tci['pages']), len(p8i['pages']), len(bad_fmt)))
    S = Installed()
    for k, v in dict(ao_dir=ao_dir, farm=farm, out=out, stg=stg, M=M, TIO=TIO, CH=CH, LA=LA, gpu_ao=gpu_ao, sp=sp, names=names, paths=paths, md5s=md5s, lm_b=lm_b, tc_b=tc_b,
                     p8_b=p8_b, LM=LM, tci=tci, p8i=p8i, bsp_b=bsp_b, BSP=BSP).items():
        setattr(S, k, v)
    return S


def precheck(a):
    """the gates that need no capture (run before any GPU time is spent): exit 0 = AO only can go on"""
    S = open_installed(a)
    say('precheck ok: the installed lightmap (%d clusters, %d pages) belongs to this BSP' % (S.LM.ncl, len(S.tci['pages'])))
    return 0


def split_share(path, keyV):
    """dev / validation: the bake's own atlas_split.bin -> the direct share of the light per traced texel (0.5 where the texel has no row or no light)"""
    a = np.fromfile(path, dtype=np.uint32)
    n, rec = int(a[1]), int(a[2])
    R = a[4:4 + n * rec].reshape(n, rec)
    k = (R[:, 0].astype(np.uint64) << np.uint64(32)) | R[:, 1].astype(np.uint64)
    d = R[:, 2:5].view(np.float32).astype(np.float64) @ np.array([0.2126, 0.7152, 0.0722])
    i = R[:, 5:8].view(np.float32).astype(np.float64) @ np.array([0.2126, 0.7152, 0.0722])
    f = np.where(d + i > 1e-9, d / np.maximum(d + i, 1e-9), 0.5)
    o = np.argsort(k, kind='stable')
    k, f = k[o], f[o]
    pos = np.searchsorted(k, keyV)
    pos[pos >= len(k)] = 0
    return np.where(k[pos] == keyV, f[pos], 0.5)


def scenery_multipliers(M, LM, a, farm, trace, sp, gpu_ao, meta_dist):
    """lit scenery (scenery object info / bucket refs): the multiplier of every scenery bucket record. The tag writer's own SceneryStage.apply runs on synthetic query rows whose
    colour is the row multiplier ms = 1 - s * 0.5 (direct + indirect fraction) * occlusion (gpu_ao's rule for rows that store only D+I) with the encode replaced by the
    identity, into a scratch buffer that is NaN everywhere else: the blend of the front and back row, the nearest-valid-vertex repair and the record addressing are the
    writer's own. -> {bucket index: (mask, multipliers)} and counts. Refuses (Refused) when the scenery chain cannot be matched (the writer would refuse too)."""
    SCNW, LA = M.SCNW, M.LA
    qp = os.path.join(farm, 'scenery_q.bin')
    ap_ = os.path.join(trace, 'ao_scenery.bin')
    if not os.path.isfile(qp):
        raise Refused('the capture has no scenery query file (scenery_q.bin) but this lightmap has %d lit scenery object(s): make a new capture.' % LM.nsoi)
    if not os.path.isfile(ap_):
        raise Refused('the AO trace has no scenery rows (ao_scenery.bin) but this lightmap has %d lit scenery object(s): run the trace again.' % LM.nsoi)
    q = open(qp, 'rb').read()
    mg, n, rw, ver = struct.unpack_from('<4I', q, 0)
    if mg != SCNW.Q_MAGIC or rw != 20 or ver != 2 or len(q) != 48 + 80 * n:
        raise Refused('the capture\'s scenery query file is not the format this program reads: make a new capture.')
    Q = np.frombuffer(q, '<u4', n * 20, 48).reshape(n, 20)
    av = np.fromfile(ap_, dtype=np.uint32)
    if int(av[1]) != n or int(av[3]) != (zlib.crc32(q) & 0xFFFFFFFF):
        raise Refused('the AO trace\'s scenery rows belong to another capture: run the trace again.')
    RAS = av[4:4 + n * int(av[2])].reshape(n, int(av[2]))
    o = gpu_ao.AoOptions(a.strength, meta_dist, a.exponent, 1.0, INDIRECT_FRACTION, DIRECT_FRACTION, 64).validate()
    socc = gpu_ao.occ_from_raw(RAS[:, 0].view(np.float32), RAS[:, 1].view(np.float32), o)
    ms = np.maximum(0.0, 1.0 - a.strength * 0.5 * (DIRECT_FRACTION + INDIRECT_FRACTION) * socc).astype(np.float32)
    rgb = np.stack([ms, ms, ms], 1)
    rows = SCNW.Rows(Q, rgb, np.zeros((n, 3), np.float32), 'ao multipliers', dict(q=qp))
    tags_root = os.path.join(os.environ.get('H2LM_ROOT') or '', 'tags')
    scratch = bytearray(b'\xff' * len(LM.d))
    saved = LA.encode
    LA.encode = lambda c: np.asarray(c, np.float32)
    try:
        chain = SCNW.Chain(tags_root, sp['scn'])
        SS = SCNW.SceneryStage(LM, rows, chain, None, False)
        info = SS.apply(scratch, None)
    except Exception as e:                                           # GateError of the writer's own scenery gates, or an unreadable render model chain
        raise Refused('the lit scenery of this map cannot be matched to its tags (%s): the tag writer would refuse this map too. Use a full bake with AO for it.' % e)
    finally:
        LA.encode = saved
    out = {}
    for b, bk in enumerate(LM.buckets):
        if bk['rec'] is None or bk['flags'] != 3:
            continue
        r = np.frombuffer(bytes(scratch[bk['rec'].data:bk['rec'].data + bk['rec'].count * 24]), np.float32).reshape(-1, 6)
        mask = np.isfinite(r[:, 0]) & np.isfinite(r[:, 1]) & np.isfinite(r[:, 2])
        if mask.any():
            out[b] = (mask, r[:, 0:3].astype(np.float64).mean(1))
    return out, dict(scenery_rows=int(n), scenery_records_written=int(info.get('records_written', 0)), mean_scenery_multiplier=float(ms.mean()))


def darken_scenery(M, LM, out_lm, mult):
    """scale the decoded colour of every lit scenery bucket record by its multiplier and encode it again (records of vertex-lit poops that share the bucket are not in the mask)"""
    LA = M.LA
    n_rec = n_changed = 0
    dsum = lsum = 0.0
    for b, (mask, m) in mult.items():
        bk = LM.buckets[b]
        cnt = bk['rec'].count
        e0 = bk['rec'].data
        recs = np.frombuffer(bytes(out_lm[e0:e0 + cnt * 24]), np.float32).reshape(cnt, 6).copy()
        use = mask & recs[:, 0:3].any(1) & (m < 1.0 - 1e-7)
        n_rec += int(mask.sum())
        if not use.any():
            continue
        lin = dec_lin_enc(recs[use, 0:3], LA)
        lum = lambda x: (np.asarray(x, np.float64) @ np.array([0.2126, 0.7152, 0.0722]))
        dsum += float(lum(lin).sum() - lum(lin * m[use][:, None]).sum())
        lsum += float(lum(lin).sum())
        recs[use, 0:3] = LA.encode((lin * m[use][:, None]).astype(F))
        out_lm[e0:e0 + cnt * 24] = recs.astype(F).tobytes()
        n_changed += int(use.sum())
    return dict(scenery_records=n_rec, scenery_records_darkened=n_changed, scenery_mean_darkening=(dsum / lsum) if lsum > 0 else 0.0)


def stage(a):
    t_start = time.time()
    S = open_installed(a)
    ao_dir, farm, out, stg, M, TIO, CH, LA, gpu_ao, sp = S.ao_dir, S.farm, S.out, S.stg, S.M, S.TIO, S.CH, S.LA, S.gpu_ao, S.sp
    names, paths, md5s, lm_b, tc_b, p8_b, LM, tci, p8i, bsp_b, BSP = S.names, S.paths, S.md5s, S.lm_b, S.tc_b, S.p8_b, S.LM, S.tci, S.p8i, S.bsp_b, S.BSP
    # ---- 3. the capture and the trace
    for f in ('texelgen_soup.bin', 'soup_topo.bin', 'capture_id.json'):
        if not os.path.isfile(os.path.join(farm, f)):
            raise Refused('the capture is incomplete (%s missing in %s): make a new capture.' % (f, farm))
    CID = json.load(open(os.path.join(farm, 'capture_id.json')))
    if 'bsp_md5' in CID and CID['bsp_md5'].lower() != md5b(bsp_b):
        raise Refused('the capture was made of another BSP file than the one in the tags now (the BSP changed): make a new capture.')
    soup_p = os.path.join(farm, 'texelgen_soup.bin')
    soup_crc = crc32f(soup_p)
    if CID.get('soup_crc32', '').upper() not in ('', '%08X' % soup_crc):
        raise Refused('the capture record does not match its geometry file: make a new capture.')
    trace = os.path.join(farm, 'ao', 'trace')
    vis_p = os.path.join(trace, 'ao_vis.bin')
    if not os.path.isfile(vis_p):
        raise Refused('there is no AO trace for this capture (%s): run the trace first.' % vis_p)
    hV = np.fromfile(vis_p, dtype=np.uint32, count=4)
    if hV[0] != M_AOV1 or int(hV[3]) != soup_crc:
        raise Refused('the AO trace was made for another capture: run the trace again.')
    cm = os.path.join(os.path.dirname(os.path.abspath(a.engine)), '..', '..', 'cull_marker_check.py')
    cmc = next((p for p in (os.path.join(os.path.abspath(a.engine), 'cull_marker_check.py'), os.path.abspath(cm), os.path.join(ao_dir, '..', '..', 'cull_marker_check.py')) if os.path.isfile(p)), None)
    if cmc:
        r = subprocess.run([sys.executable, cmc, sp['bsp'], '--tagio', M.tw], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=FLAGS)
        if r.returncode == 4:
            raise Refused('this BSP carries lightmap_cull markers (debug cull markers): its unlit clusters have a special palette group that AO only does not rebuild. '
                          'Use a full bake with AO for this map.')
    # ---- 4. layout: charts -> pages, exactly the tag writer's rule, on the installed lightmap
    BV = import_file(find_build_vertdir(ao_dir, a.engine), 'aoonly_build_vertdir')
    hdr_w, tris_w = CH.load_soup(soup_p)
    cont, _ = CH.load_topo_cont(os.path.join(farm, 'soup_topo.bin'))
    INST = BSP.inst
    tii = LM.irinfo()
    lm_inst = [i for i in range(LM.ninst) if tii['bm'][i] >= 0]
    try:
        CP = CH.chart_to_page(hdr_w, tris_w, cont, LM, INST, lm_inst, [])
    except CH.GateError as e:
        raise Refused('the capture\'s charts cannot be matched to the installed lightmap (%s): the lightmap was not made from this geometry. Bake the lightmap again.' % e)
    cl_page, inst_page, NCP, cl_with = CP['cl_page'], CP['inst_page'], CP['NCP'], CP['cl_with']
    cri = LM.crinfo()
    bad_c = [c for c in range(LM.ncl) if int(cri['bm'][c]) != cl_page.get(c, -1)]
    bad_i = [i for i in range(LM.ninst) if int(tii['bm'][i]) != inst_page.get(i, -1)]
    npages = NCP + len(lm_inst)
    if bad_c or bad_i or len(tci['pages']) != npages:
        raise Refused('the installed lightmap\'s page layout is not the one this geometry gives (%d pages in the tag, %d expected; clusters that differ %s, instances that differ %s): '
                      'it was made from different geometry or by a different tool version. Bake the lightmap again.' % (len(tci['pages']), npages, bad_c[:5], bad_i[:5]))
    say('layout ok: %d charts -> %d pages (%d cluster + %d instance)' % (len(hdr_w), npages, NCP, len(lm_inst)))
    white, black, ksch = LA.configure(*LM.root_consts())
    # ---- 5. texels: the trace's own rows; their page pixels
    hdr, TR, tch = BV.parse_soup(soup_p, os.environ.get('GPULM_SEAM_SOUP', '') == '1')
    nch = len(hdr)
    if nch != len(hdr_w):
        raise Refused('the geometry file has %d charts for the writer and %d for the rasteriser: make a new capture.' % (len(hdr_w), nch))
    chart_page = CP['chart_page']
    NT = int(hdr[:, 4].sum())
    start = np.concatenate([[0], np.cumsum(hdr[:, 4])])[:-1]
    RV = np.fromfile(vis_p, dtype=np.uint32)[4:].reshape(int(hV[1]), int(np.fromfile(vis_p, dtype=np.uint32, count=3)[2]))
    keyV = (RV[:, 0].astype(np.uint64) << np.uint64(32)) | RV[:, 1].astype(np.uint64)
    srt = np.argsort(keyV, kind='stable')
    keyV = keyV[srt]
    ordv = (keyV >> np.uint64(32)).astype(np.int64)
    tixv = (keyV & np.uint64(0xFFFFFFFF)).astype(np.int64)
    if (ordv >= nch).any() or (tixv >= hdr[np.minimum(ordv, nch - 1), 4]).any():
        raise Refused('the AO trace has texels outside this capture\'s charts: run the trace again.')
    fl = start[ordv] + tixv
    covered = np.zeros(NT, bool)
    covered[fl] = True
    say('rasterising %d texels of %d charts ...' % (int(covered.sum()), nch))
    tex_tri, tex_b = BV.rasterise(hdr, TR, tch, start, NT)
    BV.clamp(hdr, TR, tch, start, tex_tri, tex_b, covered, NT)
    corner, n_match = corner_pixels(M, LM, INST, TR, tch, chart_page, NCP, cl_with, lm_inst, tci)
    tri = tex_tri[fl]
    okt = tri >= 0
    cpx = np.where(okt[:, None, None], corner[np.maximum(tri, 0)], np.nan)                  # (n, 3, 2)
    pos = (tex_b[fl][:, :, None] * cpx).sum(1)                                            # (n, 2) page pixel coordinates of the texel centre
    okt &= np.isfinite(pos).all(1)
    pg = np.where(okt, chart_page[tch[np.maximum(tri, 0)]], -1)
    pages = read_pages(tc_b, tci, 11)
    dims = np.array([[p['w'], p['h']] for p in tci['pages']], np.int64)
    px = np.floor(np.where(okt[:, None], pos, 0.0)).astype(np.int64)
    inside = okt & (pg >= 0) & (px[:, 0] >= 0) & (px[:, 1] >= 0) & (px[:, 0] < dims[np.maximum(pg, 0), 0]) & (px[:, 1] < dims[np.maximum(pg, 0), 1])
    poff = np.concatenate([[0], np.cumsum(dims[:, 0] * dims[:, 1])])
    flat = np.where(inside, poff[np.maximum(pg, 0)] + px[:, 1] * dims[np.maximum(pg, 0), 0] + px[:, 0], 0)
    alpha = np.concatenate([p[..., 3].reshape(-1) for p in pages])
    onc = inside & (alpha[flat] == 255)
    # per-page trust: mapped texels that land on a covered pixel
    ntex_p = np.bincount(np.where(inside, pg, 0), weights=inside.astype(np.float64), minlength=npages)
    nok_p = np.bincount(np.where(inside, pg, 0), weights=onc.astype(np.float64), minlength=npages)
    ratio = np.where(ntex_p > 0, nok_p / np.maximum(ntex_p, 1), 1.0)
    trusted = ratio >= MAP_OK_MIN
    use = onc & trusted[np.maximum(pg, 0)]
    mapped_share = float(use.sum()) / max(1, len(fl))
    say('texels: %d traced; %d on a vertex-UV-mapped triangle; %d land on a covered pixel; %d of %d pages trusted (%.2f%% of the texels)' % (
        len(fl), int(okt.sum()), int(onc.sum()), int(trusted.sum()), npages, 100 * mapped_share))
    if mapped_share < MAP_PAGE_SHARE_MIN:
        bad = [int(i) for i in np.flatnonzero(~trusted)[:6]]
        raise Refused('the installed lightmap\'s pixels do not sit where this geometry says they should (only %.1f%% of the texels land on lightmap pixels; first pages that disagree: %s): '
                      'the lightmap was made from different geometry, by another layout rule, or edited. Nothing was changed.' % (100 * mapped_share, bad))
    # ---- 6. decode, occlusion, multiplier
    allpix = np.concatenate([p.reshape(-1, 4) for p in pages])
    rgb_t = np.zeros((len(fl), 3))
    ii = np.flatnonzero(use)
    q8 = allpix[flat[ii]][:, [2, 1, 0]]
    rgb_t[ii] = dec_lin(q8, LA)
    synth = os.path.join(out, 'synth')
    build_synth_farm(synth, farm, keyV, rgb_t)
    apply_dir = os.path.join(out, 'apply')
    say('gpu_ao apply (strength %s, exponent %s) on the synthetic farm ...' % (a.strength, a.exponent))
    occ, rep = run_gpu_ao_apply(ao_dir, synth, trace, apply_dir, a, a.engine)
    if len(occ) != len(fl):
        raise RuntimeError('gpu_ao returned %d occlusion values for %d texels' % (len(occ), len(fl)))
    share = DIRECT_SHARE
    if getattr(a, 'split_file', None):
        share = split_share(a.split_file, keyV)
        say('split file: the direct share per texel is the one the bake itself made (%s)' % a.split_file)
    Mt = multiplier(occ, a.strength, share)
    # ---- 7. multiplier per page pixel
    npix = int(poff[-1])
    msum = np.bincount(flat[use], weights=Mt[use], minlength=npix)
    cnt = np.bincount(flat[use], minlength=npix).astype(np.float64)
    hit = cnt > 0
    mpix = np.where(hit, msum / np.maximum(cnt, 1), 1.0)
    # gutter / border pixels (covered, hit by no texel centre): the neighbour the tool copies them from, in its order
    need = (alpha == 255) & ~hit
    for p in range(npages):
        W_, H_ = int(dims[p, 0]), int(dims[p, 1])
        sl = slice(int(poff[p]), int(poff[p + 1]))
        nd = need[sl].reshape(H_, W_)
        if not nd.any():
            continue
        ht = hit[sl].reshape(H_, W_)
        mp = mpix[sl].reshape(H_, W_)
        done = np.zeros((H_, W_), bool)
        for dx, dy in NB8:
            sh_h = np.zeros((H_, W_), bool)
            sh_m = np.ones((H_, W_))
            ys, ye = max(0, -dy), H_ - max(0, dy)
            xs, xe = max(0, -dx), W_ - max(0, dx)
            sh_h[ys:ye, xs:xe] = ht[ys + dy:ye + dy, xs + dx:xe + dx]
            sh_m[ys:ye, xs:xe] = mp[ys + dy:ye + dy, xs + dx:xe + dx]
            take = nd & ~done & sh_h
            mp[take] = sh_m[take]
            done |= take
        mpix[sl] = mp.reshape(-1)
    # ---- 8. apply to the pixels
    sel = np.flatnonzero((alpha == 255) & (mpix < 1.0 - 1e-6))
    q_old = allpix[sel][:, [2, 1, 0]]
    lin = dec_lin(q_old, LA) * mpix[sel][:, None]
    q_new = enc_q(lin, LA)
    allpix2 = allpix.copy()
    allpix2[sel, 2], allpix2[sel, 1], allpix2[sel, 0] = q_new[:, 0], q_new[:, 1], q_new[:, 2]
    changed = np.flatnonzero((allpix2 != allpix).any(1))
    # round trip of the decode itself (how exactly does the writer's encode give back the stored pixel?)
    rsel = np.flatnonzero(alpha == 255)
    rs = rsel[:: max(1, len(rsel) // 400000)]
    rt = enc_q(dec_lin(allpix[rs][:, [2, 1, 0]], LA), LA)
    rt_bad = float((rt != allpix[rs][:, [2, 1, 0]]).any(1).mean()) if len(rs) else 0.0
    lum = lambda q: (np.asarray(q, np.float64) @ np.array([0.2126, 0.7152, 0.0722]))
    l0, l1 = lum(allpix[sel][:, [2, 1, 0]]), lum(q_new)
    nzm = l0 > 0
    stats = dict(pages=npages, covered_pixels=int((alpha == 255).sum()), pixels_with_multiplier=int(len(sel)), pixels_changed=int(len(changed)),
                 share_covered_changed=float(len(changed)) / max(1, int((alpha == 255).sum())),
                 mean_multiplier_over_covered=float(mpix[alpha == 255].mean()) if (alpha == 255).any() else 1.0,
                 mean_darkening_changed_pixels=float(1.0 - (l1[nzm].sum() / max(l0[nzm].sum(), 1e-9))) if nzm.any() else 0.0,
                 mean_encoded_step_changed=float(np.abs(allpix2[changed][:, :3].astype(np.int64) - allpix[changed][:, :3].astype(np.int64)).mean()) if len(changed) else 0.0,
                 texels_traced=int(len(fl)), texels_occluded=int((occ > 0).sum()), mean_occ=float(occ.mean()), texels_mapped_share=mapped_share,
                 pages_not_trusted=int((~trusted).sum()), decode_roundtrip_mismatch_share=rt_bad, direct_share_assumed=DIRECT_SHARE)
    say('pixels: %d of %d covered pixels change (%.2f%%), mean darkening of those %.3f, mean multiplier %.4f; decode round trip mismatch %.4f%%' % (
        stats['pixels_changed'], stats['covered_pixels'], 100 * stats['share_covered_changed'], stats['mean_darkening_changed_pixels'], stats['mean_multiplier_over_covered'], 100 * rt_bad))
    new_pages = [allpix2[int(poff[p]):int(poff[p + 1])].reshape(int(dims[p, 1]), int(dims[p, 0]), 4) for p in range(npages)]
    # ---- 8b. vertex-lit instance records (bucket flags 3): the same occlusion as the full bake gives per vertex row
    vstats = {}
    out_lm = bytearray(lm_b)
    if any(b['flags'] == 3 for b in LM.buckets) and getattr(a, 'vertex', True):
        meta = gpu_ao.read_trace_meta(trace)
        mult, vinfo = vertex_lit_multipliers(M, LM, INST, farm, trace, a, gpu_ao, float(meta['distance_wu']) if meta.get('distance_wu') else gpu_ao.DEFAULT_D)
        if mult:
            vstats = darken_vertex_lit(M, LM, out_lm, mult)
            vstats.update(vinfo)
            say('vertex-lit records: %(vertex_lit_records_darkened)d of %(vertex_lit_records)d darkened (mean %(vertex_lit_mean_darkening).3f)' % vstats)
        else:
            vstats = vinfo
            say('vertex-lit records: ' + vinfo.get('note', 'none'))
    if LM.nsoi and getattr(a, 'vertex', True):
        meta = gpu_ao.read_trace_meta(trace)
        smult, sinfo = scenery_multipliers(M, LM, a, farm, trace, sp, gpu_ao, float(meta['distance_wu']) if meta.get('distance_wu') else gpu_ao.DEFAULT_D)
        sst = darken_scenery(M, LM, out_lm, smult)
        sst.update(sinfo)
        vstats.update(sst)
        say('lit scenery records: %(scenery_records_darkened)d of %(scenery_records)d darkened (mean %(scenery_mean_darkening).3f)' % sst)
    # ---- 9. P8 (the tag writer's own palette grouping) and the lightmap tag
    inst_cl = BSP.inst_clusters()
    p8exe = os.path.join(M.tw, 'bin', 'p8group.exe')
    work = os.path.join(out, 'p8work')
    os.makedirs(work, exist_ok=True)

    def group_p8(pgs, label):
        """the tag writer's own P8 palette grouping (p8group.exe on the pages, the same page records as lm_tagwriter's) -> (palettes, page -> group, P8 pages)"""
        buf = bytearray(struct.pack('<I', len(pgs)))
        for p, pxa in enumerate(pgs):
            if p < NCP:
                cl, lst = CP['cl_with'][p], []
            else:
                cl, lst = -1, inst_cl[lm_inst[p - NCP]]
            buf += struct.pack('<5i', pxa.shape[1], pxa.shape[0], cl, 0, len(lst)) + struct.pack('<%di' % len(lst), *lst) + pxa.tobytes()
        p8in, p8out = os.path.join(work, 'p8_in_%s.bin' % label), os.path.join(work, 'p8_out_%s.bin' % label)
        open(p8in, 'wb').write(buf)
        if os.path.exists(p8out):
            os.remove(p8out)
        r = subprocess.run([p8exe, p8in, p8out, '-t', '4'], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=FLAGS)
        if r.returncode != 0:
            raise RuntimeError('p8group failed (exit %d): %s' % (r.returncode, r.stderr.strip()[-300:]))
        o = open(p8out, 'rb').read()
        ng_ = struct.unpack_from('<I', o, 0)[0]
        off = 4
        pal = np.frombuffer(o, np.uint32, ng_ * 256, off).reshape(ng_, 256).copy()
        off += ng_ * 1024
        grp = np.frombuffer(o, np.int32, len(pgs), off).copy()
        off += 4 * len(pgs)
        out8 = []
        for pxa in pgs:
            n = pxa.shape[0] * pxa.shape[1]
            out8.append(np.frombuffer(o, np.uint8, n, off).reshape(pxa.shape[:2]))
            off += n
        if off != len(o):
            raise RuntimeError('p8group output size %d, parsed %d' % (len(o), off))
        for f in (p8in, p8out):
            os.remove(f)
        return pal, grp, out8

    say('P8 grouping ...')
    PAL, PGRP, p8 = group_p8(new_pages, 'new')
    ng = len(PAL)
    if getattr(a, 'check_p8_baseline', False):
        # validation: does the tag writer's grouping, run on the UNCHANGED pages, give the installed P8 set back? (it does for a set the writer or the tool made)
        old_pages = [allpix[int(poff[p]):int(poff[p + 1])].reshape(int(dims[p, 1]), int(dims[p, 0]), 4) for p in range(npages)]
        pal0, grp0, p80 = group_p8(old_pages, 'base')
        same_pal = pal0.shape == LM.palettes().shape and bool((pal0 == LM.palettes()).all())
        cri_, iri_ = LM.crinfo(), LM.irinfo()
        want_grp = np.full(npages, -9, np.int32)
        for c, pgx in cl_page.items():
            want_grp[pgx] = int(cri_['pal'][c])
        for i, pgx in inst_page.items():
            want_grp[pgx] = int(iri_['pal'][i])
        same_grp = bool((grp0 == want_grp).all())
        same_p8 = TIO.write_bitmap(p80, 18, p8i['base']) == p8_b
        stats['p8_baseline'] = dict(palettes_equal=same_pal, groups_equal=same_grp, p8_bitmap_byte_equal=same_p8)
        say('P8 baseline (the unchanged pages through the same grouping): palettes equal %s, page groups equal %s, P8 bitmap byte-equal to the installed one %s' % (same_pal, same_grp, same_p8))
    for c, pgx in cl_page.items():
        struct.pack_into('<b', out_lm, LM.crinfo_b.data + c * 4 + 2, int(PGRP[pgx]))
    for i, pgx in inst_page.items():
        struct.pack_into('<b', out_lm, LM.irinfo_b.data + i * 4 + 2, int(PGRP[pgx]))
    lm_new = TIO.splice_leaf_block(bytes(out_lm), LM.W, TIO.G0 + '.section palette', PAL.astype('<u4').tobytes())
    lm_new = TIO.set_crc(lm_new)
    tc_new = TIO.write_bitmap(new_pages, 11, tci['base'])
    p8_new = TIO.write_bitmap(p8, 18, p8i['base'])
    # ---- 10. the stage
    os.makedirs(stg, exist_ok=True)
    rep_out = {}
    for key, data in (('ltmp', lm_new), ('tc', tc_new), ('p8', p8_new)):
        dst = os.path.join(stg, names[key])
        with open(dst + '.tmp', 'wb') as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.replace(dst + '.tmp', dst)
        rep_out[names[key]] = dict(path=dst, md5=md5b(data), size=len(data))
    with open(os.path.join(stg, 'AO_NOT_CPU_FAITHFUL.txt'), 'w') as f:
        f.write('This stage was produced by AO only (ambient occlusion added to the lightmap that was installed; an artistic option). It differs from a CPU-faithful '
                'bake by design. Options: strength %s, exponent %s.\n' % (a.strength, a.exponent))
    treport = dict(writer='ao_existing', status='written', started=time.strftime('%Y-%m-%d %H:%M:%S'), outputs=rep_out, stats=dict(pages=dict(count=npages, cluster_pages=NCP, instance_pages=len(lm_inst)),
                   palettes=dict(skeleton=LM.npal, written=ng)), warnings=[], gates={}, source_set=md5s)
    json.dump(treport, open(os.path.join(stg, 'tagw_report.json'), 'w'), indent=1)
    # the same gate the full bake's AO stage passes
    vr = subprocess.run([sys.executable, os.path.join(M.tw, 'lmw_validate.py'), '--check-set', stg, '--bsp', sp['bsp'], '--json', os.path.join(out, 'tagw_validate.json')],
                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=FLAGS)
    vtxt = vr.stdout.decode('mbcs', 'replace')
    open(os.path.join(out, 'tagw_validate.log'), 'w', encoding='utf-8').write(vtxt)
    for l in vtxt.splitlines():
        if 'FAIL' in l or 'checks,' in l:
            print('   ' + l.strip(), flush=True)
    if vr.returncode:
        treport['status'] = 'refused'
        json.dump(treport, open(os.path.join(stg, 'tagw_report.json'), 'w'), indent=1)
        raise RuntimeError('lmw_validate --check-set refused the stage (see %s)' % os.path.join(out, 'tagw_validate.log'))
    stats.update(vstats)
    stats.update(seconds=round(time.time() - t_start, 1), stage=stg, installed_set_md5=md5s, stage_md5={k: v['md5'] for k, v in rep_out.items()}, strength=a.strength, exponent=a.exponent,
                 apply_stats={k: rep['stats'].get(k) for k in ('mean_occ', 'frac_occ_gt_0', 'frac_occ_gt_half', 'seam_filter')})
    json.dump(stats, open(os.path.join(out, 'aoonly_report.json'), 'w'), indent=1)
    say('STAGE %s -> %s  (%.1f s; nothing installed)' % (a.tag, stg, stats['seconds']))
    return 0


def selftest(_a):
    """decode / encode round trip over every grey and a colour sweep, and the multiplier arithmetic"""
    here = os.path.dirname(os.path.abspath(__file__))
    eng = os.environ.get('AOX_ENGINE')
    if not eng:
        print('selftest needs AOX_ENGINE (an engine folder with v043-tagwriter)')
        return 2
    M = load_mods(eng)
    LA = M.LA
    LA.configure(0.0, 0.0, 0.0)
    fails = []

    def chk(name, ok, got=''):
        print('  %-44s %s %s' % (name, 'ok  ' if ok else 'FAIL', got))
        if not ok:
            fails.append(name)
    g = np.arange(256, dtype=np.uint8)
    q = np.stack([g, g, g], 1)
    back = enc_q(dec_lin(q, LA), LA)
    chk('grey ramp round trip', bool((back == q).all()), 'mismatch %d of 256' % int((back != q).any(1).sum()))
    rng = np.random.default_rng(3)
    qs = rng.integers(0, 256, (200000, 3), dtype=np.uint8)
    qb = enc_q(dec_lin(qs, LA), LA)
    bad = float((qb != qs).any(1).mean())
    chk('random colour round trip (<= 0.5% off by one level)', bad < 0.005, 'mismatch %.4f%%' % (100 * bad))
    chk('multiplier: occ 0 -> 1', abs(multiplier(0.0, 1.0) - 1.0) < 1e-12)
    chk('multiplier: occ 1, strength 1 -> 0.25', abs(multiplier(1.0, 1.0) - 0.25) < 1e-12, '%.4f' % multiplier(1.0, 1.0))
    chk('multiplier: strength 2 floors the indirect part at 0', abs(multiplier(1.0, 2.0) - 0.0) < 1e-12 or multiplier(1.0, 2.0) >= 0.0, '%.4f' % multiplier(1.0, 2.0))
    chk('darker multiplier never brightens', bool((enc_q(dec_lin(qs[:1000], LA) * 0.7, LA).astype(int).sum(1) <= qs[:1000].astype(int).sum(1)).all()))
    print('SELFTEST: %s (%d failures)' % ('PASS' if not fails else 'FAIL', len(fails)))
    return 1 if fails else 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('stage')
    p.add_argument('--farm', required=True)
    p.add_argument('--scenario', required=True)
    p.add_argument('--bsp', required=True)
    p.add_argument('--engine', required=True, help='the AO engine folder (holds v043-tagwriter)')
    p.add_argument('--ao-dir', default=None, help='folder of gpu_ao.py (default: beside this script)')
    p.add_argument('--h2-root', default=None)
    p.add_argument('--tool', default=None, help='tool_fast.exe of the H2EK (the field-definition pin)')
    p.add_argument('--strength', type=float, required=True)
    p.add_argument('--exponent', type=float, required=True)
    p.add_argument('--tag', required=True)
    p.add_argument('--records', default=None, help='the AO install records (WORK\\standalone\\ao_installed.json)')
    p.add_argument('--force', action='store_true', help='apply AO even though the install record says this lightmap already has it')
    p.add_argument('--overwrite', action='store_true')
    p.add_argument('--out', default=None)
    p.add_argument('--check-p8-baseline', action='store_true', help='validation: also run the P8 grouping on the UNCHANGED pages and report whether it reproduces the installed P8 set')
    p.add_argument('--split-file', default=None, help='dev / validation: the atlas_split.bin the bake itself made (the exact direct share per texel instead of the assumed 0.5)')
    p.set_defaults(f=stage)
    p = sub.add_parser('precheck', help='the gates that need no capture: the installed lightmap exists, has no AO yet, belongs to this BSP')
    for fl, kw in (('--scenario', dict(required=True)), ('--bsp', dict(required=True)), ('--engine', dict(required=True)), ('--ao-dir', dict(default=None)), ('--h2-root', dict(default=None)),
                   ('--tool', dict(default=None)), ('--records', dict(default=None))):
        p.add_argument(fl, **kw)
    p.add_argument('--force', action='store_true')
    p.set_defaults(f=precheck)
    p = sub.add_parser('selftest')
    p.set_defaults(f=selftest)
    a = ap.parse_args(argv)
    try:
        return a.f(a)
    except Refused as e:
        print('AO only: REFUSED -- ' + str(e).rstrip().rstrip('.') + '. Nothing was written.', flush=True)
        return 2


if __name__ == '__main__':
    sys.exit(main())
