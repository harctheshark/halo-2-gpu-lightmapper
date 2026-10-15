"""ref_kdtree.py -- the production pair loop of ao_seamfix.seam_pairs (md5 7905b9bf), VERBATIM apart from being a function.
The reference side of the S1.8 A/B. Needs scipy (the dev interpreter's user site has 1.15.3)."""
import numpy as np
from scipy.spatial import cKDTree


def kdtree_pair_keys(X, r, ok, nn_):
    tree = cKDTree(X)
    K = []
    for c0 in range(0, len(ok), 100000):
        sl = slice(c0, min(c0 + 100000, len(ok)))
        lists = tree.query_ball_point(X[sl], r[sl], workers=1)
        cnt = np.fromiter((len(l) for l in lists), np.int64, count=len(lists))
        i = ok[np.repeat(np.arange(sl.start, sl.stop), cnt)]
        j = ok[np.fromiter((q for l in lists for q in l), np.int64, count=int(cnt.sum()))]
        k = i != j; i = i[k]; j = j[k]
        K.append(np.minimum(i, j) * nn_ + np.maximum(i, j))
    K = np.unique(np.concatenate(K))                       # sorted unique unordered pairs: the deterministic order
    return K
