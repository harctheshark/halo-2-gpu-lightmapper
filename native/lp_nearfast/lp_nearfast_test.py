"""lp_nearfast_test.py LIGHTPROBES_DIR RUN_DIR GLOBALS.json [--out JSON] -- is lpnearfast.dll the numpy loop, bit for bit?
LIGHTPROBES_DIR = the folder with lp_compose.py / lp_nearpoint.py / lpnearfast.dll under test; RUN_DIR = a worker folder that holds a probe
pass's outputs (probe_hits.bin, vertex_out.bin, surf_gpu.bin, bitmap_idx.bin, bitmaps.bin); GLOBALS.json = that probe pass's globals.json
(its _probes_bin must exist). Reads only.
 1. the search: surface_terms() with the numpy loop (LP_NEARFAST=0) and with the DLL, every _shade() call recorded -- the lights that are
    seen, in order, and per light the rows, the uv and the normal at the hit as BIT PATTERNS, and the summed result array, must be identical;
 2. the whole step: lp_compose.py run twice into two temporary folders (numpy / DLL): every probe_N.float md5-equal; the two wall times.
Exit 0 = identical."""
import hashlib
import importlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

import numpy as np


def main():
    av = [a for a in sys.argv[1:] if not a.startswith('--')]
    lp, run, glp = av[0], av[1], av[2]
    outj = sys.argv[sys.argv.index('--out') + 1] if '--out' in sys.argv else None
    gl = json.load(open(glp))
    sys.path.insert(0, lp)
    f32 = np.float32
    # the rows lp_compose.py hands to surface_terms (its own statements)
    h = np.fromfile(os.path.join(run, 'probe_hits.bin'), np.uint32, 4)
    nP, N = int(h[1]), int(h[2]); T = 6 * N * N
    PRB = np.fromfile(os.path.join(run, 'probe_hits.bin'), np.float32, offset=16).reshape(nP * T, 16)
    sf = PRB[:, 0].astype(np.int32) == 2
    pp = np.fromfile(gl['_probes_bin'], np.float32, offset=4).reshape(-1, 3)
    pidx = np.nonzero(sf)[0] // T
    O = pp[pidx].astype(f32); Hs = PRB[sf, 3:6].astype(f32)
    R = {'rows': int(sf.sum()), 'probes': nP}
    rec = {}
    for mode in ('numpy', 'native'):
        os.environ['LP_NEARFAST'] = '0' if mode == 'numpy' else '1'
        if 'lp_nearpoint' in sys.modules:
            del sys.modules['lp_nearpoint']
        M = importlib.import_module('lp_nearpoint')
        calls = []
        orig = M._shade

        def spy(p, idx, Uh, Nh, d, dirN, BM, PSL, _o=orig, _c=calls):
            _c.append((p.tobytes(), idx.astype(np.int64).tobytes(), np.ascontiguousarray(Uh, f32).tobytes(), np.ascontiguousarray(Nh, f32).tobytes()))
            return _o(p, idx, Uh, Nh, d, dirN, BM, PSL)
        M._shade = spy
        rep = {}
        t0 = time.time()
        em = M.surface_terms(O, Hs, gl, run, rep)
        rec[mode] = (calls, em.tobytes(), rep, round(time.time() - t0, 2))
    a, b = rec['numpy'], rec['native']
    R['search'] = {'numpy_s': a[3], 'native_s': b[3], 'numpy_path': a[2].get('emitter_search'), 'native_path': b[2].get('emitter_search'),
                   'lights_seen': len(a[0]), 'hits': a[2].get('emitter_hits'),
                   'lights_equal': len(a[0]) == len(b[0]) and all(x[0] == y[0] for x, y in zip(a[0], b[0])),
                   'rows_equal': len(a[0]) == len(b[0]) and all(x[1] == y[1] for x, y in zip(a[0], b[0])),
                   'uv_bits_equal': len(a[0]) == len(b[0]) and all(x[2] == y[2] for x, y in zip(a[0], b[0])),
                   'normal_bits_equal': len(a[0]) == len(b[0]) and all(x[3] == y[3] for x, y in zip(a[0], b[0])),
                   'result_equal': a[1] == b[1], 'hits_equal': a[2].get('emitter_hits') == b[2].get('emitter_hits')}
    S = R['search']
    ok = S['native_path'] == 'native' and S['numpy_path'] == 'numpy' and all(S[k] for k in ('lights_equal', 'rows_equal', 'uv_bits_equal', 'normal_bits_equal', 'result_equal', 'hits_equal'))
    # the whole compose step, twice
    tmp = tempfile.mkdtemp(prefix='lpnf_')
    try:
        md = {}
        for mode in ('numpy', 'native'):
            o = os.path.join(tmp, mode)
            env = dict(os.environ, LP_NEARFAST='0' if mode == 'numpy' else '1')
            t0 = time.time()
            r = subprocess.run([sys.executable, '-B', os.path.join(lp, 'lp_compose.py'), run, o, glp], capture_output=True, text=True, env=env, creationflags=0x08000000)
            dt = round(time.time() - t0, 2)
            fl = sorted(f for f in os.listdir(o) if f.endswith('.float')) if os.path.isdir(o) else []
            md[mode] = {'exit': r.returncode, 'seconds': dt, 'floats': {f: hashlib.md5(open(os.path.join(o, f), 'rb').read()).hexdigest() for f in fl},
                        'tail': r.stdout.strip().splitlines()[-3:] if r.stdout else r.stderr[-300:]}
        R['compose'] = {'numpy_s': md['numpy']['seconds'], 'native_s': md['native']['seconds'], 'floats': len(md['numpy']['floats']),
                        'exits': [md['numpy']['exit'], md['native']['exit']],
                        'floats_equal': bool(md['numpy']['floats']) and md['numpy']['floats'] == md['native']['floats'], 'md5': md['native']['floats']}
        ok = ok and R['compose']['floats_equal'] and R['compose']['exits'] == [0, 0]
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    R['ok'] = ok
    if outj:
        json.dump(R, open(outj, 'w'), indent=1)
    print(json.dumps({k: v for k, v in R.items() if k != 'compose'} | {'compose': {k: v for k, v in R['compose'].items() if k != 'md5'}}, indent=1))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
