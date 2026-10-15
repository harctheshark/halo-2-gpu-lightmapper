"""test_ballgrid.py -- synthetic equivalence tests: ao_ballgrid.ball_pair_keys vs the production cKDTree loop (ref_kdtree.py).
CPU only, a few seconds. Usage: python -B test_ballgrid.py <dir holding ao_ballgrid.py> [--json out.json]
Cases are built to hit the edges: exact ties s == r*r on a binary-exact lattice, duplicates, r = 0, radii spanning 4 decades,
float32-derived positions, texel-like sheets that meet at a seam, a single point, a tiny candidate budget (block splitting)."""
import os, sys, json, time
import numpy as np

sys.dont_write_bytecode = True
if os.name == 'nt':                                   # BELOW_NORMAL (the brief); children inherit it
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.abspath(sys.argv[1]))
sys.path.insert(1, HERE)
import ao_ballgrid as BG
from ref_kdtree import kdtree_pair_keys


def case(name, X, r, ok=None, nn=None, budget=None):
    X = np.ascontiguousarray(X, np.float64); r = np.ascontiguousarray(r, np.float64)
    ok = np.arange(len(X), dtype=np.int64) if ok is None else np.asarray(ok, np.int64)
    nn = np.int64(len(X) if nn is None else nn)
    t0 = time.time(); Kr = kdtree_pair_keys(X, r, ok, nn); t1 = time.time()
    Kg = BG.ball_pair_keys(X, r, ok, nn) if budget is None else BG.ball_pair_keys(X, r, ok, nn, budget=budget); t2 = time.time()
    same = Kr.dtype == Kg.dtype and np.array_equal(Kr, Kg)
    # exact ties present? (s == r_i^2 for some candidate pair) -- reported so a case is known to exercise the <= edge
    print('%-44s n %7d  pairs %9d  %s  (kdtree %.2f s, grid %.2f s)' % (name, len(X), len(Kr), 'IDENTICAL' if same else 'DIFFERENT', t1 - t0, t2 - t1), flush=True)
    return dict(name=name, n=int(len(X)), pairs_kdtree=int(len(Kr)), pairs_grid=int(len(Kg)), identical=bool(same),
                only_kdtree=int(len(np.setdiff1d(Kr, Kg))), only_grid=int(len(np.setdiff1d(Kg, Kr))))


def ties_on_lattice():
    """binary-exact lattice (step 0.25): many pair distances are EXACTLY r (s == r*r with no rounding), both sides of the edge"""
    g = np.arange(12) * 0.25
    X = np.stack(np.meshgrid(g, g, g[:4], indexing='ij'), -1).reshape(-1, 3)
    rng = np.random.default_rng(1)
    r = rng.choice([0.25, 0.5, 0.75, 0.3535533905932738, 0.5590169943749475, 1.0], len(X))
    return X, r


def main():
    out = []
    rng = np.random.default_rng(20261014)
    # 1. uniform random cube, radii over 4 decades (forces many grid levels)
    X = rng.uniform(-50, 50, (20000, 3)); r = 10 ** rng.uniform(-3, 0.5, len(X))
    out.append(case('uniform cube, r 1e-3..3 (many levels)', X, r))
    X = rng.uniform(-5, 5, (40000, 3)); r = 10 ** rng.uniform(-3, -0.3, len(X))
    out.append(case('dense cube, r 1e-3..0.5 (many levels)', X, r))
    # 2. exact ties on a binary lattice
    X, r = ties_on_lattice(); out.append(case('binary lattice, exact s == r*r ties', X, r))
    # 3. duplicates + r == 0 (only exact duplicates pair at r = 0)
    X = rng.integers(0, 20, (5000, 3)).astype(np.float64) * 0.5; r = np.where(rng.random(len(X)) < 0.3, 0.0, 0.5)
    out.append(case('duplicates, r = 0 and 0.5', X, r))
    # 4. float32-derived texel sheets meeting at a seam (two charts, different texel sizes), like ao_rep.bin positions
    a = np.stack(np.meshgrid(np.arange(150) * 0.031, np.arange(150) * 0.031, indexing='ij'), -1).reshape(-1, 2)
    b = np.stack(np.meshgrid(4.65 + np.arange(60) * 0.11, np.arange(60) * 0.11, indexing='ij'), -1).reshape(-1, 2)
    S = np.concatenate([a, b]); X = np.c_[S, 0.002 * np.sin(S[:, 0] * 7)].astype(np.float32).astype(np.float64) + np.float64(123.456)
    r = np.minimum(1.25 * np.r_[np.full(len(a), 0.031), np.full(len(b), 0.11)], 0.7)
    out.append(case('float32 texel sheets at a seam (2 charts)', X, r))
    # 5. a dense cluster + far outliers (unbalanced cell counts) with the production ok / nn mapping (sparse rows)
    X = np.r_[rng.normal(0, 0.05, (8000, 3)), rng.uniform(-1000, 1000, (500, 3))]
    r = np.r_[np.full(8000, 0.02), np.full(500, 0.7)]
    ok = np.sort(rng.choice(40000, len(X), replace=False)).astype(np.int64)
    out.append(case('dense cluster + outliers, sparse ok rows', X, r, ok=ok, nn=40000))
    # 6. the same with a tiny budget (block splitting inside a level)
    out.append(case('same, budget 1000 (block splitting)', X, r, ok=ok, nn=40000, budget=1000))
    # 7. a single point; all points identical
    out.append(case('single point', np.array([[1.0, 2.0, 3.0]]), np.array([0.5])))
    out.append(case('all points identical', np.tile([[1.0, 2.0, 3.0]], (50, 1)), np.full(50, 0.1)))
    # 8. collinear points (extent 0 on two axes) and radius larger than the whole extent
    X = np.c_[np.arange(3000) * 0.01, np.zeros(3000), np.zeros(3000)]; r = np.r_[np.full(2999, 0.015), [100.0]]
    out.append(case('collinear, one radius > extent', X, r))
    ok_all = all(c['identical'] for c in out)
    print('UNIT: %s (%d cases)' % ('PASS' if ok_all else 'FAIL', len(out)))
    if '--json' in sys.argv:
        json.dump(dict(pass_=ok_all, cases=out), open(sys.argv[sys.argv.index('--json') + 1], 'w'), indent=1)
    return 0 if ok_all else 1


if __name__ == '__main__':
    sys.exit(main())
