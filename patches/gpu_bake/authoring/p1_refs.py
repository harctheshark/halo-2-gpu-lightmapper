"""Authoring helper (not shipped): the 15 P1 code references $W -> $WC, each with an exact expected count.
usage: py -I p1_refs.py <file>   (edits the file in place; refuses on any count mismatch)"""
import sys

REPL = [
    (b'$INJ  = "$W\\v014-inject-estimator\\inject.exe"', b'$INJ  = "$WC\\v014-inject-estimator\\inject.exe"', 1),
    (b'$OSOY        = "$W\\osoyoos"', b'$OSOY        = "$WC\\osoyoos"', 1),
    (b'--cullcheck "$W\\cull_marker_check.py"', b'--cullcheck "$WC\\cull_marker_check.py"', 2),
    (b'& py "$W\\cull_marker_check.py"', b'& py "$WC\\cull_marker_check.py"', 1),
    (b'& py "$W\\sky_gate.py"', b'& py "$WC\\sky_gate.py"', 1),
    (b'& py "$W\\build_surf_gpu.py"', b'& py "$WC\\build_surf_gpu.py"', 1),
    (b'& py "$W\\build_sky_gpu.py"', b'& py "$WC\\build_sky_gpu.py"', 1),
    (b'$oracle = "$W\\v041-fullgpu\\re_owner.bin"', b'$oracle = "$WC\\v041-fullgpu\\re_owner.bin"', 1),
    (b'& py "$W\\promote_owner_table.py"', b'& py "$WC\\promote_owner_table.py"', 2),
    (b'else { "$W\\build_seam_lists.py" }', b'else { "$WC\\build_seam_lists.py" }', 1),
    (b'Get-ChildItem "$W\\v043-suncull\\soup_flags_store"', b'Get-ChildItem "$WC\\v043-suncull\\soup_flags_store"', 1),
    (b'& py "$W\\build_vertdir.py"', b'& py "$WC\\build_vertdir.py"', 1),
    (b"& (Join-Path $W 'cleanup_farm.ps1')", b"& (Join-Path $WC 'cleanup_farm.ps1')", 1),
]

p = sys.argv[1]
d = open(p, 'rb').read()
total = 0
for old, new, n in REPL:
    c = d.count(old)
    if c != n:
        sys.exit('REFUSED: %r occurs %d times, expected %d' % (old, c, n))
    d = d.replace(old, new)
    total += n
if total != 15:
    sys.exit('REFUSED: %d replacements, expected 15' % total)
open(p, 'wb').write(d)
print('P1: %d code references moved to $WC in %s' % (total, p))
