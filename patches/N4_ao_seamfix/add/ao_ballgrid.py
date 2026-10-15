"""ao_ballgrid.py -- exact variable-radius ball pairs in pure numpy for ao_seamfix.seam_pairs (H2 GPU Lightmapper standalone,
design N4 / plan S1.8, 2026-10-14).

Replaces the seam filter's only scipy use (the production loop in ao_seamfix.seam_pairs):

    tree = cKDTree(X)
    for each chunk of 100000 queries:  lists = tree.query_ball_point(X[sl], r[sl], workers=1)
        i = ok[query row]; j = ok[list entry]; drop i == j; append min(i, j) * nn + max(i, j)
    K = np.unique(np.concatenate(chunks))

so the standalone Python bundle can ship without scipy. The rule is the one lmw_grid.py (the tag writer's 2026-09-29 cKDTree
replacement) reproduces: cKDTree's leaf test for p = 2, eps = 0 is

    s = ((dx*dx + dy*dy) + dz*dz) <= r*r        float64, d = data point - query point
                                                (scipy's sqeuclidean_distance_double order for 3 dims)

and s is symmetric in (i, j) bit for bit (x - y == -(y - x) in IEEE arithmetic), so a pair is in K iff the test passes with the
radius of i OR with the radius of j. That is what the union over all queries plus np.unique produces, and what this module
produces.

Search. Every query gets the smallest level L whose cell h_L = h0 * 2**L is >= |r| * (1 + 1e-6). A level grids ALL points with
cell h_L and exact integer cell keys (no hashing). For a query in that level, every point that can pass the test lies in the
query's own cell or one of its 26 neighbours:
  - s <= fl(r*r) gives |d_k| <= |r| * (1 + 3e-16) on every axis;
  - the cell coordinate u = fl(fl(x - o) / h) carries an error below 2.5e-10 cells, because the cells per axis are capped at
    2**19 + 2 (h >= extent / 2**19);
  - so |u_j - u_i| < (1 + 3e-16) / (1 + 1e-6) + 5e-10 < 1, and the floors differ by at most 1.
A floor of h >= 1e-150 keeps that true when d*d underflows to 0. The 27 cells are candidates only; the exact test above decides.
Candidates are generated per query cell, tested, mapped through `ok`, and canonicalised exactly as the production loop does
(min * nn + max, i == j dropped, np.unique). The result is the same sorted int64 array.

Not covered: non-finite positions or radii (cKDTree has no defined result for them) raise ValueError. An empty X raises the
same ValueError the production loop raises (np.concatenate of no chunks).

A1 (2026-10-15) adds the second entry point, for ao_denoise.bilateral_raw (the AO-noise workflow's apply options):

    tree = cKDTree(X)
    for each chunk of receivers sl:  lists = tree.query_ball_point(X[sl], rad[sl], workers=W, return_sorted=True)

ball_blocks yields the same DIRECTED pairs (receiver i, source j, the receiver's own row included, exactly the cKDTree test
((dx*dx + dy*dy) + dz*dz) <= r_i*r_i with d = X[j] - X[i]) in blocks, one radius level at a time (so a level's grid is built once and
freed, not once per receiver chunk). Block order and the order inside a block are unspecified; every receiver is in exactly one block
with ALL its sources, and the only consumer sorts each receiver's sources by its own unique key (bilateral_raw's lexsort), which is
what makes the result independent of both. ball_pair_keys is unchanged.
"""
import numpy as np

MARGIN = 1.0 + 1e-6               # cell >= |r| * MARGIN: the +-1 neighbourhood is a superset of the ball (see the docstring)
MAX_CELLS_LOG2 = 19               # at most 2**19 + 2 cells per axis: the cell-coordinate rounding stays below 2.5e-10 cells
H_MIN = 1e-150                    # cell floor: a d*d that underflows to 0 still lies inside +-1 cell
BUDGET = 1 << 21                  # candidates per vectorised block (about 100 MB of temporaries)
_OFF = np.array([(a, b, c) for a in (-1, 0, 1) for b in (-1, 0, 1) for c in (-1, 0, 1)], np.int64)


def _levels(ra, ext):
    """-> h0, L (per query): the smallest level whose cell h0 * 2**L covers |r| * MARGIN"""
    need = ra * MARGIN
    hfloor = max(ext / float(1 << MAX_CELLS_LOG2), H_MIN)
    h0 = max(float(need.min()), hfloor)
    with np.errstate(divide='ignore', invalid='ignore'):
        L = np.ceil(np.log2(np.maximum(need, h0) / h0))
    L = np.maximum(L, 0).astype(np.int64)
    while True:                                   # log2 rounding: make h_L >= need exact (ldexp is exact)
        short = np.ldexp(h0, L) < need
        if not short.any(): break
        L = L + short
    return h0, L


def ball_pair_keys(X, r, ok, nn, budget=BUDGET):
    """X (n, 3) float64 query = data points, r (n,) radii, ok (n,) the atlas row of each point, nn the key base (len(P)).
    -> sorted unique int64 keys min(ok[i], ok[j]) * nn + max(ok[i], ok[j]) over every i != j with
       ((dx*dx + dy*dy) + dz*dz) <= r_i * r_i  or  <= r_j * r_j      (== the cKDTree loop of ao_seamfix.seam_pairs)"""
    X = np.ascontiguousarray(X, dtype=np.float64)
    r = np.ascontiguousarray(r, dtype=np.float64).reshape(-1)
    ok = np.ascontiguousarray(ok, dtype=np.int64).reshape(-1)
    nn = np.int64(nn)
    if X.ndim != 2 or X.shape[1] != 3 or len(r) != len(X) or len(ok) != len(X):
        raise ValueError('ball_pair_keys: X must be (n, 3), r and ok of length n')
    n = len(X)
    if n == 0:
        raise ValueError('need at least one array to concatenate')      # what the production loop raises on an empty set
    if not (np.isfinite(X).all() and np.isfinite(r).all()):
        raise ValueError('ball_pair_keys: non-finite position or radius (cKDTree has no defined result for it)')
    r2 = r * r                                    # cKDTree: upper_bound = r * r for p = 2, eps = 0
    lo = X.min(0)
    ext = float((X.max(0) - lo).max())
    h0, L = _levels(np.abs(r), ext)
    out = []
    for lv in np.unique(L):
        Q = np.flatnonzero(L == lv)
        h = float(np.ldexp(h0, int(lv)))
        o = lo - h                                # every cell coordinate >= 1, so the -1 neighbour stays >= 0
        C = np.floor((X - o) / h).astype(np.int64)
        N = C.max(0) + 2                          # the +1 neighbour stays <= N - 1: exact, collision-free keys
        key = (C[:, 0] * N[1] + C[:, 1]) * N[2] + C[:, 2]
        order = np.argsort(key, kind='stable')
        Xs = X[order]
        uk, ust, ucn = np.unique(key[order], return_index=True, return_counts=True)
        off = (_OFF[:, 0] * N[1] + _OFF[:, 1]) * N[2] + _OFF[:, 2]
        qc, qinv = np.unique(key[Q], return_inverse=True)            # the query cells of this level
        qinv = qinv.reshape(-1)
        nk = qc[:, None] + off[None, :]
        pos = np.minimum(np.searchsorted(uk, nk), len(uk) - 1)
        hit = uk[pos] == nk
        NS = np.where(hit, ust[pos], 0)                               # per query cell x 27 neighbours: start / count in the
        NC = np.where(hit, ucn[pos], 0)                               # cell-sorted points
        qo = np.argsort(qinv, kind='stable')                          # queries in cell order (memory locality)
        Q = Q[qo]; qinv = qinv[qo]
        cs = np.cumsum(NC.sum(1)[qinv])                               # running candidate count
        a0 = 0
        while a0 < len(Q):
            base = int(cs[a0 - 1]) if a0 else 0
            a1 = max(int(np.searchsorted(cs, base + budget, 'right')), a0 + 1)
            cell = qinv[a0:a1]
            c = NC[cell].reshape(-1); s0 = NS[cell].reshape(-1)
            qq = np.repeat(Q[a0:a1], 27)
            m = c > 0
            c = c[m]; s0 = s0[m]; qq = qq[m]
            tot = int(c.sum())
            if tot:
                qi = np.repeat(qq, c)
                ps = np.arange(tot, dtype=np.int64) + np.repeat(s0 - (np.cumsum(c) - c), c)
                d = Xs[ps, 0] - X[qi, 0]; s = d * d                  # d = data - query, summed x, y, z in that order
                d = Xs[ps, 1] - X[qi, 1]; s += d * d
                d = Xs[ps, 2] - X[qi, 2]; s += d * d
                keep = s <= r2[qi]
                gi = ok[qi[keep]]; gj = ok[order[ps[keep]]]
                k = gi != gj
                gi = gi[k]; gj = gj[k]
                out.append(np.minimum(gi, gj) * nn + np.maximum(gi, gj))
            a0 = a1
    return np.unique(np.concatenate(out))


def ball_blocks(X, r, block=100000, budget=BUDGET):
    """X (n, 3) float64 = the data points AND the queries, r (n,) the radius of each query (a receiver), block = the most queries per
    yielded block. Generator of (qi, pj, nq): int64 arrays qi (query row) and pj (data row) of EVERY directed pair with
        ((dx*dx + dy*dy) + dz*dz) <= r[qi] * r[qi]       d = X[pj] - X[qi], float64 (== cKDTree(X).query_ball_point(X, r))
    the query's own row included (cKDTree returns it too), and nq = the number of queries this block completes. Each query appears in
    exactly one block together with ALL its data rows; block and in-block order are unspecified. Empty blocks are not yielded."""
    X = np.ascontiguousarray(X, dtype=np.float64)
    r = np.ascontiguousarray(r, dtype=np.float64).reshape(-1)
    if X.ndim != 2 or X.shape[1] != 3 or len(r) != len(X):
        raise ValueError('ball_blocks: X must be (n, 3), r of length n')
    n = len(X)
    if n == 0:
        return
    if not (np.isfinite(X).all() and np.isfinite(r).all()):
        raise ValueError('ball_blocks: non-finite position or radius (cKDTree has no defined result for it)')
    if (r < 0).any():
        raise ValueError('ball_blocks: negative radius (cKDTree returns no neighbours for it; the radii here are >= 0)')
    r2 = r * r                                   # cKDTree: upper_bound = r * r for p = 2, eps = 0
    lo = X.min(0)
    ext = float((X.max(0) - lo).max())
    h0, L = _levels(np.abs(r), ext)
    for lv in np.unique(L):
        Q = np.flatnonzero(L == lv)
        h = float(np.ldexp(h0, int(lv)))
        o = lo - h
        C = np.floor((X - o) / h).astype(np.int64)
        N = C.max(0) + 2
        key = (C[:, 0] * N[1] + C[:, 1]) * N[2] + C[:, 2]
        order = np.argsort(key, kind='stable')
        Xs = X[order]
        uk, ust, ucn = np.unique(key[order], return_index=True, return_counts=True)
        off = (_OFF[:, 0] * N[1] + _OFF[:, 1]) * N[2] + _OFF[:, 2]
        qc, qinv = np.unique(key[Q], return_inverse=True)
        qinv = qinv.reshape(-1)
        nk = qc[:, None] + off[None, :]
        pos = np.minimum(np.searchsorted(uk, nk), len(uk) - 1)
        hit = uk[pos] == nk
        NS = np.where(hit, ust[pos], 0)
        NC = np.where(hit, ucn[pos], 0)
        qo = np.argsort(qinv, kind='stable')
        Q = Q[qo]; qinv = qinv[qo]
        cs = np.cumsum(NC.sum(1)[qinv])
        a0 = 0
        while a0 < len(Q):
            base = int(cs[a0 - 1]) if a0 else 0
            a1 = max(int(np.searchsorted(cs, base + budget, 'right')), a0 + 1)
            a1 = min(a1, a0 + max(int(block), 1))
            cell = qinv[a0:a1]
            c = NC[cell].reshape(-1); s0 = NS[cell].reshape(-1)
            qq = np.repeat(Q[a0:a1], 27)
            m = c > 0
            c = c[m]; s0 = s0[m]; qq = qq[m]
            tot = int(c.sum())
            if tot:
                qi = np.repeat(qq, c)
                ps = np.arange(tot, dtype=np.int64) + np.repeat(s0 - (np.cumsum(c) - c), c)
                d = Xs[ps, 0] - X[qi, 0]; s = d * d                  # d = data - query, summed x, y, z in that order
                d = Xs[ps, 1] - X[qi, 1]; s += d * d
                d = Xs[ps, 2] - X[qi, 2]; s += d * d
                keep = s <= r2[qi]
                if keep.any():
                    yield qi[keep], order[ps[keep]], a1 - a0
            a0 = a1
