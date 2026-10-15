"""ab_seamfix.py -- S1.8 / N4 gate: the scipy-free seam filter vs production, CPU only, read-only on GPU Lightmap Work.

  prep    build scratch\patched_ao (the production ao_seamfix.py, md5-checked, + the .patch applied strictly + add\ao_ballgrid.py)
          and scratch\<farm>\ (COPIES of the apply-time inputs and the AO trace, md5-checked against the recorded reports)
  pairs   the canonical pair set K: the production cKDTree loop vs ao_ballgrid, plus the full seam_pairs / seam_filter_raw
          outputs of the pristine module (scipy) vs the patched one; boundary diagnostics (exact ties, +-1e-9 radius shell)
  apply   production gpu_ao.py apply twice per tag: A = pristine seam filter (scipy), B = patched (scipy blocked);
          md5 of every run product, A vs B vs the farm's own run dir and the recorded report
  stage   lm_tagwriter (dev_AO, the engine gpu_ao stage uses) on each B run into a scratch stage; tag md5s vs the farm's stage
  selftest  gpu_ao selftest (the seam filter's synthetic tests) on the A and the B path
  verdict   PASS/FAIL from the results above -> results\\S1.8_verdict.json
  all     every step in order

usage: python -B ab_seamfix.py <step> [--farms w71,w151]
Nothing under GPU Lightmap Work is written: sources are only read (copied out); every output goes to scratch\\ or results\\."""
import os, sys, re, json, time, hashlib, shutil, subprocess, importlib.util
import numpy as np

sys.dont_write_bytecode = True
if os.name == 'nt':                                   # BELOW_NORMAL (the brief); children inherit it
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)

GLW = os.environ.get('H2LM_TEST_GLW', '')   # <H2EK_ROOT>\GPU Lightmap Work (production, read only)
PROD_AO = os.path.join(GLW, '_research', 'ao')
TW = os.path.join(PROD_AO, 'dev_AO', 'v043-tagwriter')         # the tag writer gpu_ao stage uses by default (ENGINE_DEFAULT)
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, '..', '..'))
NPATCH = os.path.join(PROJ, 'patches', 'N4_ao_seamfix')
SCR = os.path.join(HERE, 'scratch')
PATCHED = os.path.join(SCR, 'patched_ao')
RES = os.path.join(HERE, 'results')
PY = sys.executable
FLAGS = 0x08000000 | 0x00004000                                  # CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS

PRISTINE_MD5 = '7905b9bf8446462b8d623fc68272da44'                # production _research\ao\ao_seamfix.py (2026-10-13 18:17)
PATCHED_MD5 = 'a1444d81478ee1efb53d54b3fc73d9eb'                 # pristine + ao_seamfix.py.patch

FARMS = {
    # The farm roots get re-baked by other work (the 2026-10-14 tie promotion re-baked farm_w71 at 05:47 and farm_w151 at 07:02, AFTER
    # the scratch copies of this A/B were taken at 06:56). The apply-time atlas_out / vertex_out / scenery_out are then found in a
    # `_pre_*` snapshot dir: prep picks the dir whose atlas_out.bin has the reports' atlas_out_md5_original (root first).
    'w71': dict(src=os.path.join(GLW, 'farm_w71'), tags=['s075f', 's125e05']),       # cyclotron
    'w151': dict(src=os.path.join(GLW, 'farm_w151'), tags=['s075f', 's125e05']),     # headlong
}
AO_MODIFIED = ('atlas_out.bin', 'vertex_out.bin', 'scenery_out.bin')        # apply rewrites these: never take them from a run dir
INPUTS = ('atlas_out.bin', 'atlas_split.bin', 'atlas_dir.bin', 'texelgen_soup.bin', 'vertex_q.bin', 'vertex_out.bin', 'scenery_out.bin',
          'scenery_q.bin', 'capture_id.json', 'soup_topo.bin')
TRACE = ('ao_vis.bin', 'ao_rep.bin', 'ao_vertex.bin', 'ao_scenery.bin', 'ao_trace.txt')
RUN_PRODUCTS = ('atlas_out.bin', 'vertex_out.bin', 'vertdir.bin', 'scenery_out.bin', 'atlas_dir.bin', 'atlas_split.bin', 'capture_id.json',
                'texelgen_soup.bin', 'soup_topo.bin', 'vertex_q.bin', 'scenery_q.bin')


def log(m): print('[ab %s] %s' % (time.strftime('%H:%M:%S'), m), flush=True)


def md5f(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 22), b''): h.update(c)
    return h.hexdigest()


def under(p, root):
    p = os.path.normcase(os.path.abspath(p)); root = os.path.normcase(os.path.abspath(root))
    return p.startswith(root + os.sep)


def wjson(name, obj):
    os.makedirs(RES, exist_ok=True)
    p = os.path.join(RES, name)
    with open(p + '.tmp', 'w') as f: json.dump(obj, f, indent=1)
    os.replace(p + '.tmp', p)
    return p


def rjson(name):
    p = os.path.join(RES, name)
    return json.load(open(p)) if os.path.exists(p) else None


# ------------------------------------------------------------------------------------------------ strict patch (no fuzz, no offset)
def apply_strict(src, patch):
    """unified diff -> bytes. Every hunk must match at its stated line, context and removed lines byte for byte (CR kept)."""
    S = src.split(b'\n'); P = patch.split(b'\n'); out = []; si = 0; i = 0; hunks = 0
    while i < len(P):
        m = re.match(rb'@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@', P[i])
        if not m:
            i += 1; continue
        hunks += 1
        old_start, old_len = int(m.group(1)), int(m.group(2) if m.group(2) is not None else 1)
        tgt = old_start - 1 if old_len else old_start
        if tgt < si: raise ValueError('hunk %d overlaps the previous one' % hunks)
        out.extend(S[si:tgt]); si = tgt; i += 1; used = 0
        while i < len(P) and P[i][:1] in (b' ', b'-', b'+', b'\\'):
            t, body = P[i][:1], P[i][1:]
            if t in (b' ', b'-'):
                if si >= len(S) or S[si] != body: raise ValueError('hunk %d: line %d does not match (no fuzz allowed)' % (hunks, si + 1))
                if t == b' ': out.append(body)
                si += 1; used += 1
            elif t == b'+':
                out.append(body)
            i += 1
        if used != old_len: raise ValueError('hunk %d: %d old lines, header says %d' % (hunks, used, old_len))
    if not hunks: raise ValueError('no hunks')
    out.extend(S[si:])
    return b'\n'.join(out)


# ------------------------------------------------------------------------------------------------ prep
def build_patched():
    src = os.path.join(PROD_AO, 'ao_seamfix.py')
    b = open(src, 'rb').read()
    if hashlib.md5(b).hexdigest() != PRISTINE_MD5:
        raise SystemExit('production ao_seamfix.py is %s, not the pristine %s: re-derive the patch' % (hashlib.md5(b).hexdigest(), PRISTINE_MD5))
    out = apply_strict(b, open(os.path.join(NPATCH, 'ao_seamfix.py.patch'), 'rb').read())
    if hashlib.md5(out).hexdigest() != PATCHED_MD5:
        raise SystemExit('patched ao_seamfix.py md5 %s != %s' % (hashlib.md5(out).hexdigest(), PATCHED_MD5))
    os.makedirs(PATCHED, exist_ok=True)
    open(os.path.join(PATCHED, 'ao_seamfix.py'), 'wb').write(out)
    shutil.copyfile(os.path.join(NPATCH, 'add', 'ao_ballgrid.py'), os.path.join(PATCHED, 'ao_ballgrid.py'))
    return dict(pristine_md5=PRISTINE_MD5, patched_md5=PATCHED_MD5, ao_ballgrid_md5=md5f(os.path.join(PATCHED, 'ao_ballgrid.py')))


def copy_checked(src, dst):
    if os.path.exists(dst) and os.path.getsize(dst) == os.path.getsize(src) and md5f(dst) == md5f(src):
        return md5f(dst)
    shutil.copyfile(src, dst + '.tmp'); os.replace(dst + '.tmp', dst)
    m1, m2 = md5f(src), md5f(dst)
    if m1 != m2: raise SystemExit('copy mismatch %s' % src)
    return m2


def prep(farms):
    info = dict(patched=build_patched(), farms={})
    log('patched ao_seamfix built: %s' % info['patched'])
    for name in farms:
        F = FARMS[name]; src = F['src']; dst = os.path.join(SCR, name)
        os.makedirs(os.path.join(dst, 'ao', 'trace'), exist_ok=True)
        reps = {t: json.load(open(os.path.join(src, 'ao', t, 'ao_apply_report.json'))) for t in F['tags']}
        tw_inputs = {}
        for t in F['tags']:
            p = os.path.join(src, 'ao', t, 'tagw_stage', 'tagw_report.json')
            if os.path.exists(p): tw_inputs[t] = json.load(open(p)).get('inputs', {})
        orig = {reps[t]['atlas_out_md5_original'] for t in F['tags']}
        if len(orig) != 1: raise SystemExit('%s: the tags were applied over different atlases %s' % (name, orig))
        snap = None                                              # where the apply-time AO-modified inputs are now
        for d in [src] + sorted(os.path.join(src, e) for e in os.listdir(src) if e.startswith('_pre_') and os.path.isdir(os.path.join(src, e))):
            if os.path.exists(os.path.join(d, 'atlas_out.bin')) and md5f(os.path.join(d, 'atlas_out.bin')) in orig:
                snap = d; break
        if snap is None: raise SystemExit('%s: no dir holds the apply-time atlas_out.bin %s' % (name, orig))
        fi = {}
        for f in INPUTS:
            cands = []
            if f in AO_MODIFIED: cands.append(os.path.join(snap, f))
            else: cands.append(os.path.join(src, 'ao', F['tags'][0], 'run', f))                     # the hardlinks apply made
            have = [c for c in cands if os.path.exists(c)]
            if not have: raise SystemExit('%s: no source for %s' % (name, f))
            m = copy_checked(have[0], os.path.join(dst, f))
            fi[f] = dict(src=have[0], md5=m)
            log('%s  %-18s %s  <- %s' % (name, f, m, have[0]))
        if orig != {fi['atlas_out.bin']['md5']}:
            raise SystemExit('%s: atlas_out.bin %s is not the apply-time original %s' % (name, fi['atlas_out.bin']['md5'], orig))
        for t, inp in tw_inputs.items():                        # the tag writer recorded md5s of some run inputs: cross-check
            for k, f in (('texelgen_soup.bin', 'texelgen_soup.bin'), ('soup_topo.bin', 'soup_topo.bin'), ('capture_id', 'capture_id.json')):
                if k in inp and inp[k]['md5'] != fi[f]['md5']:
                    raise SystemExit('%s: %s md5 %s != the %s stage record %s' % (name, f, fi[f]['md5'], t, inp[k]['md5']))
        tr = {}
        for f in TRACE:
            p = os.path.join(src, 'ao', 'trace', f)
            if os.path.exists(p): tr[f] = copy_checked(p, os.path.join(dst, 'ao', 'trace', f))
        info['farms'][name] = dict(src=src, inputs=fi, trace=tr, atlas_out_md5_original=sorted(orig)[0])
    wjson('prep.json', info)
    return info


# ------------------------------------------------------------------------------------------------ module loading
def load_module(name, path, extra_path=None):
    if extra_path and extra_path not in sys.path: sys.path.insert(0, extra_path)
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec); sys.modules[name] = m; spec.loader.exec_module(m)
    return m


def load_inputs(G, SF, farm):
    """the arrays cmd_apply hands to seam_filter_raw (same code, same order)"""
    tr = os.path.join(farm, 'ao', 'trace')
    hA, RA = G.load_rows(os.path.join(farm, 'atlas_out.bin'), G.M_ATL1, 5)
    hV, RV = G.load_rows(os.path.join(tr, 'ao_vis.bin'), G.M_AOV1, 4)
    keyA = (RA[:, 0].astype(np.uint64) << np.uint64(32)) | RA[:, 1].astype(np.uint64)
    keyV = (RV[:, 0].astype(np.uint64) << np.uint64(32)) | RV[:, 1].astype(np.uint64)
    pos = np.searchsorted(keyV, keyA); pos[pos >= len(keyV)] = 0
    found = keyV[pos] == keyA
    ov = RV[pos, 2:4].view(np.float32).astype(np.float64)
    occ_raw = np.where(found, ov[:, 0], 0.0); rays = np.where(found, ov[:, 1], 0.0)
    rep = SF.load_rep(os.path.join(tr, 'ao_rep.bin'), G.M_AOR1, keyA)
    if rep is None: raise SystemExit('%s: ao_rep.bin missing / not aligned' % farm)
    Wc, Hc = SF.load_soup_dims(os.path.join(farm, 'texelgen_soup.bin'))
    return dict(ord_=RA[:, 0].astype(np.int64), tix=RA[:, 1].astype(np.int64), W=Wc, H=Hc, P=rep[0], nrm=rep[1], has=rays > 0,
                occ_raw=occ_raw, rays=rays)


def bytes_eq(u, v):
    u = np.ascontiguousarray(u); v = np.ascontiguousarray(v)
    return u.dtype == v.dtype and u.shape == v.shape and u.tobytes() == v.tobytes()


# ------------------------------------------------------------------------------------------------ pairs
def pairs(farms):
    G = load_module('gpu_ao_prod', os.path.join(PROD_AO, 'gpu_ao.py'))
    SFp = load_module('ao_seamfix_prod', os.path.join(PROD_AO, 'ao_seamfix.py'))
    BG = load_module('ao_ballgrid', os.path.join(PATCHED, 'ao_ballgrid.py'), PATCHED)
    SFn = load_module('ao_seamfix_n4', os.path.join(PATCHED, 'ao_seamfix.py'), PATCHED)
    if md5f(os.path.join(PATCHED, 'ao_seamfix.py')) != PATCHED_MD5: raise SystemExit('patched_ao is stale: run prep')
    sys.path.insert(0, HERE)
    from ref_kdtree import kdtree_pair_keys
    allres = {}
    for name in farms:
        farm = os.path.join(SCR, name)
        D = load_inputs(G, SFp, farm)
        prm = SFp.Params(radius=1.0, directed=False, same_chart=False)          # the reports' seam params (gpu_ao apply defaults)
        prmn = SFn.Params(radius=1.0, directed=False, same_chart=False)
        if prm.as_dict() != prmn.as_dict(): raise SystemExit('Params differ between the modules')
        nb, x, y = SFp.neighbour_index(D['ord_'], D['tix'], D['W'], D['H'])
        ts = SFp.chart_texel_size(D['ord_'], D['P'], D['has'], nb, len(D['W']))
        ok = np.flatnonzero(D['has']); X = D['P'][ok]
        r = np.minimum(prm.radius * prm.w_zero_rd * ts[D['ord_'][ok]], prm.rcap); nn = np.int64(len(D['P']))
        res = dict(texels=int(len(D['P'])), points=int(len(ok)), r_min=float(r.min()), r_max=float(r.max()),
                   nonfinite_positions=int((~np.isfinite(X)).any(1).sum()))
        log('%s: %d texels, %d with a sample, r %.5f .. %.5f' % (name, res['texels'], res['points'], res['r_min'], res['r_max']))
        t0 = time.time(); Kr = kdtree_pair_keys(X, r, ok, nn); t1 = time.time()
        Kg = BG.ball_pair_keys(X, r, ok, nn); t2 = time.time()
        res.update(pairs_kdtree=int(len(Kr)), pairs_grid=int(len(Kg)), K_identical=bool(bytes_eq(Kr, Kg)),
                   only_kdtree=int(len(np.setdiff1d(Kr, Kg))), only_grid=int(len(np.setdiff1d(Kg, Kr))),
                   seconds_kdtree=round(t1 - t0, 2), seconds_grid=round(t2 - t1, 2),
                   K_md5=hashlib.md5(Kr.tobytes()).hexdigest())
        log('%s: K kdtree %d (%.1f s)  grid %d (%.1f s)  -> %s' % (name, len(Kr), t1 - t0, len(Kg), t2 - t1, 'IDENTICAL' if res['K_identical'] else 'DIFFERENT'))
        # boundary diagnostics: how close does any candidate pair come to the inclusion edge s <= max(r_i, r_j)^2 ?
        rr = np.zeros(len(D['P'])); rr[ok] = r
        a = Kr // nn; b = Kr % nn
        d = D['P'][b] - D['P'][a]; s = (d[:, 0] * d[:, 0] + d[:, 1] * d[:, 1]) + d[:, 2] * d[:, 2]
        R2 = np.maximum(rr[a] * rr[a], rr[b] * rr[b])
        res['exact_ties_in_K'] = int((s == R2).sum())
        pos_ = R2 > 0                                          # R2 == 0: r = 0 on both sides, only exact duplicates pair
        res['pairs_with_zero_radius_both_sides'] = int((~pos_).sum())
        res['min_rel_margin_inside'] = float(((R2[pos_] - s[pos_]) / R2[pos_]).min()) if pos_.any() else None
        Kw = BG.ball_pair_keys(X, r * (1 + 1e-9), ok, nn); Kn = BG.ball_pair_keys(X, r * (1 - 1e-9), ok, nn)
        res['pairs_r_plus_1e-9'] = int(len(Kw)); res['pairs_r_minus_1e-9'] = int(len(Kn))
        res['pairs_within_1e-9_of_edge'] = int(len(Kw) - len(Kn))
        out_ = np.setdiff1d(Kw, Kr)
        if len(out_):
            a2 = out_ // nn; b2 = out_ % nn; d2 = D['P'][b2] - D['P'][a2]
            s2 = (d2[:, 0] * d2[:, 0] + d2[:, 1] * d2[:, 1]) + d2[:, 2] * d2[:, 2]
            R22 = np.maximum(rr[a2] * rr[a2], rr[b2] * rr[b2]); res['min_rel_margin_outside'] = float(((s2 - R22) / R22).min())
        else:
            res['min_rel_margin_outside'] = None
        del Kw, Kn, d, s, R2, a, b
        # the full filter: pristine module (scipy) vs patched module (grid), every output byte for byte
        t0 = time.time()
        o1, r1, i1 = SFp.seam_filter_raw(D['ord_'], D['tix'], D['W'], D['H'], D['P'], D['nrm'], D['has'], D['occ_raw'], D['rays'], prm)
        t1 = time.time()
        o2, r2, i2 = SFn.seam_filter_raw(D['ord_'], D['tix'], D['W'], D['H'], D['P'], D['nrm'], D['has'], D['occ_raw'], D['rays'], prmn)
        t2 = time.time()
        res['filter'] = dict(seconds_prod=round(t1 - t0, 2), seconds_n4=round(t2 - t1, 2),
                             coupled_pairs_prod=i1['pairs'], coupled_pairs_n4=i2['pairs'], coupled_texels_prod=i1['coupled'], coupled_texels_n4=i2['coupled'],
                             zero_weight_pairs_in_K=int(len(Kr) - i1['pairs']),
                             a_identical=bytes_eq(i1['a'], i2['a']), b_identical=bytes_eq(i1['b'], i2['b']), w_identical=bytes_eq(i1['w'], i2['w']),
                             ts_identical=bytes_eq(i1['ts'], i2['ts']), occ_raw_identical=bytes_eq(o1, o2), rays_identical=bytes_eq(r1, r2))
        f = res['filter']
        res['pass'] = bool(res['K_identical'] and f['a_identical'] and f['b_identical'] and f['w_identical'] and f['ts_identical']
                           and f['occ_raw_identical'] and f['rays_identical'] and res['nonfinite_positions'] == 0)
        log('%s: filter prod %.1f s, n4 %.1f s; %d coupled pairs (%d zero-weight pairs in K); outputs %s' %
            (name, t1 - t0, t2 - t1, i1['pairs'], f['zero_weight_pairs_in_K'], 'IDENTICAL' if res['pass'] else 'DIFFERENT'))
        log('%s: boundary: %d exact ties, %d pairs within +-1e-9 radius of the edge, margins in %.3g / out %s' %
            (name, res['exact_ties_in_K'], res['pairs_within_1e-9_of_edge'], res['min_rel_margin_inside'], res['min_rel_margin_outside']))
        allres[name] = res
        wjson('pairs_%s.json' % name, res)
        del D, Kr, Kg, o1, r1, i1, o2, r2, i2
    return allres


# ------------------------------------------------------------------------------------------------ apply
def run_logged(args, logf, cwd=None):
    t0 = time.time()
    with open(logf, 'wb') as f:
        rc = subprocess.call(args, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=FLAGS if os.name == 'nt' else 0, cwd=cwd)
    return rc, time.time() - t0


def _is_reparse(q):
    """True for ANY reparse point (junction, symlink, mount point) -- read with lstat, never followed. Review fix (finding 13):
    os.path.isjunction exists only from Python 3.12 and os.path.islink is False for an NTFS junction, so on the 3.10.5 that
    runs this harness the old guard never fired."""
    try:
        return bool(getattr(os.lstat(q), 'st_file_attributes', 0) & 0x400) or os.path.islink(q)
    except OSError:
        return True      # cannot tell: treat as a reparse point (refuse)


def clear_scratch_dir(p):
    """removes a scratch run dir of OUR OWN (regular files only, under scratch\\, never a reparse point). Pass 1 walks the
    tree WITHOUT descending into any reparse point and refuses -- removing nothing -- if it finds one (os.walk on 3.10 would
    otherwise walk INTO a junction, and os.remove would then delete files in its target, possibly live tags); pass 2 removes."""
    if not os.path.isdir(p): return
    if not under(p, SCR): raise SystemExit('refusing to clear %s (not under scratch)' % p)
    if _is_reparse(p): raise SystemExit('reparse point in scratch: %s' % p)
    for root, dirs, files in os.walk(p, topdown=True):
        for n in dirs + files:
            q = os.path.join(root, n)
            if _is_reparse(q): raise SystemExit('reparse point in scratch (nothing was removed): %s' % q)
    for root, dirs, files in os.walk(p, topdown=False):
        for d in dirs:
            if _is_reparse(os.path.join(root, d)): raise SystemExit('reparse point appeared in scratch: %s' % os.path.join(root, d))
        for f in files: os.remove(os.path.join(root, f))
        for d in dirs: os.rmdir(os.path.join(root, d))
    os.rmdir(p)


def apply(farms):
    if md5f(os.path.join(PATCHED, 'ao_seamfix.py')) != PATCHED_MD5: raise SystemExit('patched_ao is stale: run prep')
    allres = {}
    for name in farms:
        F = FARMS[name]; farm = os.path.join(SCR, name)
        for tag in F['tags']:
            rep0 = json.load(open(os.path.join(F['src'], 'ao', tag, 'ao_apply_report.json')))
            o = rep0['options']; sp = rep0['stats']['seam_filter']['params']
            args = ['apply', '--farm', farm, '--strength', repr(o['strength']), '--exponent', repr(o['exponent']), '--fof', repr(o['fully_occluded_fraction']),
                    '--indirect', repr(o['indirect_fraction']), '--direct', repr(o['direct_fraction']), '--seam-radius', repr(sp['radius']), '--overwrite']
            if sp.get('directed'): args.append('--seam-directed')
            if sp.get('same_chart'): args.append('--seam-inchart')
            farm_run = os.path.join(F['src'], 'ao', tag, 'run')
            res = dict(options=o, seam_params=sp, recorded_atlas_out_md5_ao=rep0['atlas_out_md5_ao'],
                       farm_run={f: md5f(os.path.join(farm_run, f)) for f in RUN_PRODUCTS if os.path.exists(os.path.join(farm_run, f))},
                       farm_ao_occ=md5f(os.path.join(F['src'], 'ao', tag, 'ao_occ.bin')) if os.path.exists(os.path.join(F['src'], 'ao', tag, 'ao_occ.bin')) else None)
            for mode in ('A', 'B'):
                t = '%s_%s' % (tag, mode); out = os.path.join(farm, 'ao', t)
                clear_scratch_dir(out)
                os.makedirs(os.path.join(farm, 'ao'), exist_ok=True)
                logf = os.path.join(farm, 'ao', t + '.log')
                rc, dt = run_logged([PY, '-B', os.path.join(HERE, 'run_gpu_ao.py'), mode, PROD_AO, PATCHED, '--'] + args + ['--tag', t], logf)
                txt = open(logf, errors='replace').read()
                ln = [l for l in txt.splitlines() if l.startswith('RUN_GPU_AO ')]
                info = json.loads(ln[-1][11:]) if ln else {}
                rd = os.path.join(out, 'run')
                prods = {f: md5f(os.path.join(rd, f)) for f in RUN_PRODUCTS if os.path.exists(os.path.join(rd, f))}
                rj = json.load(open(os.path.join(out, 'ao_apply_report.json'))) if os.path.exists(os.path.join(out, 'ao_apply_report.json')) else {}
                res[mode] = dict(rc=rc, seconds=round(dt, 1), driver=info, run=prods,
                                 ao_occ=md5f(os.path.join(out, 'ao_occ.bin')) if os.path.exists(os.path.join(out, 'ao_occ.bin')) else None,
                                 seam_filter=rj.get('stats', {}).get('seam_filter'), stats={k: v for k, v in rj.get('stats', {}).items() if k != 'seconds'})
                log('%s %s %s: rc %d, %.1f s, atlas_out %s, ao_seamfix %s, scipy %s' % (name, tag, mode, rc, dt, prods.get('atlas_out.bin'),
                                                                                       info.get('ao_seamfix'), info.get('scipy_loaded')))
            A, B = res['A'], res['B']
            pa = os.path.normcase(os.path.join(PROD_AO, 'ao_seamfix.py')); pb = os.path.normcase(os.path.join(PATCHED, 'ao_seamfix.py'))
            res['checks'] = dict(
                rc_zero=A['rc'] == 0 and B['rc'] == 0,
                A_used_pristine_with_scipy=os.path.normcase(A['driver'].get('ao_seamfix') or '') == pa and bool(A['driver'].get('scipy_loaded')),
                B_used_patched_without_scipy=os.path.normcase(B['driver'].get('ao_seamfix') or '') == pb and B['driver'].get('scipy_loaded') == [],
                atlas_out_A_eq_B=A['run'].get('atlas_out.bin') == B['run'].get('atlas_out.bin') is not None,
                atlas_out_eq_recorded=B['run'].get('atlas_out.bin') == rep0['atlas_out_md5_ao'] == A['run'].get('atlas_out.bin'),
                run_products_A_eq_B=A['run'] == B['run'],
                run_products_eq_farm=all(B['run'].get(f) == m for f, m in res['farm_run'].items()) and set(B['run']) == set(res['farm_run']),
                ao_occ_A_eq_B_eq_farm=A['ao_occ'] == B['ao_occ'] and (res['farm_ao_occ'] is None or B['ao_occ'] == res['farm_ao_occ']),
                stats_A_eq_B=A['stats'] == B['stats'],
                seam_report_eq_farm=B['seam_filter'] == rep0['stats']['seam_filter'])
            res['pass'] = all(res['checks'].values())
            log('%s %s: %s %s' % (name, tag, 'PASS' if res['pass'] else 'FAIL', {k: v for k, v in res['checks'].items() if not v}))
            allres['%s_%s' % (name, tag)] = res
    wjson('apply.json', allres)
    return allres


# ------------------------------------------------------------------------------------------------ stage (tag writer on the B runs)
def stage(farms):
    allres = {}
    for name in farms:
        F = FARMS[name]; farm = os.path.join(SCR, name)
        for tag in F['tags']:
            twr = json.load(open(os.path.join(F['src'], 'ao', tag, 'tagw_stage', 'tagw_report.json')))
            ar = twr['args']; inp = twr['inputs']
            live = dict(bsp=md5f(ar['bsp']) == inp['bsp']['md5'], scenario=md5f(ar['scenario']) == inp['scenario']['md5'],
                        skel_manifest=md5f(inp['skel_manifest']['path']) == inp['skel_manifest']['md5'])
            out = os.path.join(farm, 'ao', tag + '_B'); stg = os.path.join(out, 'tagw_stage')
            clear_scratch_dir(stg)
            args = [PY, '-B', os.path.join(TW, 'lm_tagwriter.py'), '--run-dir', os.path.join(out, 'run'), '--skeleton', ar['skeleton'], '--stage', stg,
                    '--bsp', ar['bsp'], '--scenario', ar['scenario'], '--threads', str(ar['threads']), '--lightmap-name', ar['lightmap_name'],
                    '--norow', ar['norow']]
            rc, dt = run_logged(args, os.path.join(out, 'tagwriter.log'))
            got = {}
            if os.path.isdir(stg):
                for f in sorted(os.listdir(stg)):
                    if f.endswith(('.scenario_structure_lightmap', '.bitmap', '.scenario')): got[f] = md5f(os.path.join(stg, f))
            want = {f: v['md5'] for f, v in twr['outputs'].items()}
            disk = {f: md5f(os.path.join(F['src'], 'ao', tag, 'tagw_stage', f)) for f in want}
            res = dict(rc=rc, seconds=round(dt, 1), live_inputs_unchanged=live, staged=got, farm_stage_recorded=want, farm_stage_on_disk=disk,
                       identical=(rc == 0 and got == want == disk))
            log('%s %s B stage: rc %d, %.1f s -> %s (live inputs unchanged: %s)' % (name, tag, rc, dt, 'IDENTICAL to the farm stage' if res['identical'] else 'DIFFERENT', live))
            allres['%s_%s' % (name, tag)] = res
    wjson('stage.json', allres)
    return allres


# ------------------------------------------------------------------------------------------------ selftest
def selftest():
    res = {}
    for mode in ('A', 'B'):
        logf = os.path.join(SCR, 'selftest_%s.log' % mode)
        rc, dt = run_logged([PY, '-B', os.path.join(HERE, 'run_gpu_ao.py'), mode, PROD_AO, PATCHED, '--', 'selftest'], logf)
        txt = open(logf, errors='replace').read()
        seam = [l.strip() for l in txt.splitlines() if 'seam:' in l]
        last = [l for l in txt.splitlines() if l.startswith('SELFTEST')]
        drv = [l for l in txt.splitlines() if l.startswith('RUN_GPU_AO ')]
        res[mode] = dict(rc=rc, summary=last[-1] if last else None, seam_lines=seam, driver=json.loads(drv[-1][11:]) if drv else None)
        log('selftest %s: rc %d %s' % (mode, rc, res[mode]['summary']))
    res['pass'] = res['A']['rc'] == 0 and res['B']['rc'] == 0 and res['B']['driver'] and res['B']['driver']['scipy_loaded'] == []
    wjson('selftest.json', res)
    return res


# ------------------------------------------------------------------------------------------------ verdict
def verdict(farms):
    pr = {n: rjson('pairs_%s.json' % n) for n in farms}
    ap = rjson('apply.json') or {}; st = rjson('stage.json') or {}; se = rjson('selftest.json') or {}; un = rjson('unit_ballgrid.json') or {}
    gate_pairs = all(pr[n] and pr[n]['pass'] for n in farms)
    gate_apply = all(ap.get('%s_%s' % (n, t), {}).get('pass') for n in farms for t in FARMS[n]['tags'])
    gate_stage = all(st.get('%s_%s' % (n, t), {}).get('identical') for n in farms for t in FARMS[n]['tags'])
    v = dict(step='S1.8 / N4', date=time.strftime('%Y-%m-%d %H:%M'), farms=farms,
             gate_canonical_pairs_identical=gate_pairs, gate_applied_atlas_out_identical=gate_apply,
             staged_tags_identical=gate_stage, selftest_pass=bool(se.get('pass')), unit_pass=bool(un.get('pass_')),
             pairs={n: {k: pr[n][k] for k in ('points', 'pairs_kdtree', 'pairs_grid', 'K_identical', 'exact_ties_in_K', 'pairs_within_1e-9_of_edge',
                                                'seconds_kdtree', 'seconds_grid')} | {'zero_weight_pairs_in_K': pr[n]['filter']['zero_weight_pairs_in_K'],
                                                                                     'coupled_pairs': pr[n]['filter']['coupled_pairs_prod']} for n in farms if pr[n]},
             atlas_out={k: dict(A=v_['A']['run'].get('atlas_out.bin'), B=v_['B']['run'].get('atlas_out.bin'), recorded=v_['recorded_atlas_out_md5_ao'])
                        for k, v_ in ap.items()})
    v['verdict'] = 'PASS' if (gate_pairs and gate_apply and v['selftest_pass'] and v['unit_pass']) else 'FAIL'
    v['decision'] = ('v1 ships WITHOUT scipy: ao_seamfix.py patched with ao_ballgrid (N4)' if v['verdict'] == 'PASS'
                     else 'v1 bundles scipy 1.15.3 (N4 failed its gate)')
    wjson('S1.8_verdict.json', v)
    log('VERDICT %s -- %s' % (v['verdict'], v['decision']))
    write_manifest(v)
    return v


def write_manifest(v):
    """patches\\N4_ao_seamfix\\N4_MANIFEST.json: what the payload builder (S1.9) applies, and whether it may"""
    add = os.path.join(NPATCH, 'add', 'ao_ballgrid.py')
    m = dict(id='N4', step='S1.8', base='GPU Lightmap Work',
             target=dict(path='_research/ao/ao_seamfix.py', pristine_md5=PRISTINE_MD5, patch='ao_seamfix.py.patch',
                         patch_md5=md5f(os.path.join(NPATCH, 'ao_seamfix.py.patch')), patched_md5=PATCHED_MD5,
                         apply='strict: exact context at the stated lines, no fuzz, no offset; CRLF kept'),
             adds=[dict(path='_research/ao/ao_ballgrid.py', src='add/ao_ballgrid.py', md5=md5f(add), size=os.path.getsize(add))],
             gate=dict(verdict=v['verdict'], date=v['date'], results='tests/S1.8_ao_seamfix/results/S1.8_verdict.json',
                       canonical_pairs_identical=v['gate_canonical_pairs_identical'], applied_atlas_out_identical=v['gate_applied_atlas_out_identical'],
                       staged_tags_identical=v['staged_tags_identical']),
             ship=v['verdict'] == 'PASS', bundle_scipy=v['verdict'] != 'PASS', decision=v['decision'])
    p = os.path.join(NPATCH, 'N4_MANIFEST.json')
    with open(p + '.tmp', 'w', newline='\n') as f: json.dump(m, f, indent=1); f.write('\n')
    os.replace(p + '.tmp', p)
    log('wrote %s' % p)


def main():
    step = sys.argv[1] if len(sys.argv) > 1 else 'all'
    farms = sys.argv[sys.argv.index('--farms') + 1].split(',') if '--farms' in sys.argv else list(FARMS)
    if step in ('prep', 'all'): prep(farms)
    if step in ('pairs', 'all'): pairs(farms)
    if step in ('apply', 'all'): apply(farms)
    if step in ('stage', 'all'): stage(farms)
    if step in ('selftest', 'all'): selftest()
    if step in ('verdict', 'all'): verdict(farms)


if __name__ == '__main__':
    main()
