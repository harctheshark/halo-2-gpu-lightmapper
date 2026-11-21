"""patch_build_vertdir_ssn.py <build_vertdir.py> -- 1.5.1: build_vertdir.py takes the bake's OWN supersample count when --ssn is not given.

The bug (1.5.0, every Low bake since 0.6.11): the driver runs `build_vertdir.py OUT --stamp --quiet` with no --ssn, so --ssn defaulted
to 4 (Super's value). A Low bake runs fullgpu with GPULM_SS_N cleared -> gather.cpp ss_init keeps ssN = raster_globals.bin float[6] = 1
(the CPU capture always runs at low), and vdsamp.bin's header h[3] says 1. Then
  - vertdir_fast.exe gets --ssn 4 and bails "vdsamp.bin header / ssN differs" (exit 3) -> the Python builder runs;
  - the Python's vdsamp_samples() rejects the same file (int(a[3]) != N) as "does not belong to this bake" and runs the OFFLINE
    17-candidate search at N = 4: 16 cells per texel (headlong: ~40 M (texel, cell) rows, many minutes, GBs of RAM) that splats a
    different sample set from the one the engine shaded (16 sub-samples instead of the bake's 1, no 7-ray gate, no seam records) --
    the vertdir.bin is not this bake's construction, not just slow.
The fix: --ssn defaults to None and is resolved before the native call: the vdsamp.bin header(s) of the files vdsamp_samples() would
read (all agreeing); without one, gather.cpp ss_init's own rule: raster_globals.bin float[6] in 1..8, GPULM_SS_N in 1..8 overrides,
else 1. An explicit --ssn still wins. Super is unchanged (header 4 = the old default).
Asserted edits; backs the file up first (.bak_vdssn_<ts>); refuses a file that is not the 43102338 builder (0.6.12 native-patched)."""
import hashlib
import io
import shutil
import sys
import time

BASE_MD5 = '4310233869ceaf768139153f633619ad'
p = sys.argv[1]
raw = open(p, 'rb').read()
if hashlib.md5(raw).hexdigest() != BASE_MD5:
    sys.exit('REFUSED: %s is not the 43102338 build_vertdir.py (md5 %s)' % (p, hashlib.md5(raw).hexdigest()))
s = raw.decode('utf-8')
nl = '\r\n' if '\r\n' in s else '\n'
s = s.replace('\r\n', '\n')


def rep(a, b):
    global s
    assert s.count(a) == 1, (a[:80], s.count(a))
    s = s.replace(a, b)


rep('  runs the Python below unchanged. GPULM_VERTDIR_NATIVE=0 = always the Python.\n"""\n',
    '  runs the Python below unchanged. GPULM_VERTDIR_NATIVE=0 = always the Python.\n'
    '\n'
    'ssN (1.5.1) -- without --ssn the bake\'s own supersample count is used: the vdsamp.bin header(s) h[3]; without one, gather.cpp ss_init\'s\n'
    '  rule (raster_globals.bin float[6] in 1..8, GPULM_SS_N in 1..8 overrides, else 1). Before 1.5.1 --ssn defaulted to 4, so every Low\n'
    '  bake (ssN 1) had its vdsamp.bin refused by both builders and ran the offline N=4 search: very slow and not the bake\'s samples.\n'
    '"""\n')
rep("def run_native(A):\n",
    "def vdsamp_files(run):\n"
    "    \"\"\"The vdsamp.bin file(s) vdsamp_samples() reads: the top-level one when fullgpu_key.bin is there, else parts\\\\p*\\\\vdsamp.bin.\"\"\"\n"
    "    import glob\n"
    "    if os.path.exists(os.path.join(run, 'fullgpu_key.bin')):\n"
    "        return [os.path.join(run, 'vdsamp.bin')]\n"
    "    return sorted(glob.glob(os.path.join(run, 'parts', 'p*', 'vdsamp.bin')))\n"
    "\n"
    "def resolve_ssn(run):\n"
    "    \"\"\"1.5.1: the bake's own ssN -> (ssN, where it came from). The vdsamp.bin header(s) h[3] (what the engine recorded) when every\n"
    "    file has a valid header and they agree; else gather.cpp ss_init: raster_globals.bin float[6] in 1..8, GPULM_SS_N in 1..8\n"
    "    overrides, default 1.\"\"\"\n"
    "    hs = []\n"
    "    for vp in vdsamp_files(run):\n"
    "        h = np.fromfile(vp, np.uint32, 4) if os.path.isfile(vp) else np.zeros(0, np.uint32)\n"
    "        hs.append(int(h[3]) if h.size == 4 and int(h[0]) == 0x31534456 and 1 <= int(h[3]) <= 8 else None)\n"
    "    if hs and None not in hs and len(set(hs)) == 1:\n"
    "        return hs[0], 'vdsamp.bin header'\n"
    "    n, src = 1, 'the engine default'\n"
    "    rg = os.path.join(run, 'raster_globals.bin')\n"
    "    if os.path.isfile(rg):\n"
    "        r = np.fromfile(rg, np.float32, 7)\n"
    "        if r.size == 7 and 1.0 <= float(r[6]) <= 8.0:\n"
    "            n, src = int(float(r[6]) + 0.5), 'raster_globals.bin'\n"
    "    e = os.environ.get('GPULM_SS_N', '')\n"
    "    try:\n"
    "        v = int(e) if e else 0\n"
    "    except ValueError:\n"
    "        v = 0\n"
    "    if 1 <= v <= 8:\n"
    "        n, src = v, 'GPULM_SS_N'\n"
    "    if len(set(h for h in hs if h is not None)) > 1:\n"
    "        src += ' (the vdsamp.bin headers disagree: %s)' % hs\n"
    "    return n, src\n"
    "\n"
    "def run_native(A):\n")
rep("    ap.add_argument('--ssn', type=int, default=4, help='supersamples per axis (SUPER 4; the engine logs ssN)')\n"
    "    A = ap.parse_args(); QUIET = A.quiet\n",
    "    ap.add_argument('--ssn', type=int, default=None,\n"
    "                    help='supersamples per axis (default: the bake\\'s own -- the vdsamp.bin header, else raster_globals.bin / GPULM_SS_N, else 1)')\n"
    "    A = ap.parse_args(); QUIET = A.quiet\n"
    "    if A.ssn is None:\n"
    "        A.ssn, src = resolve_ssn(A.run)\n"
    "        log('ssN %d (%s)' % (A.ssn, src))\n")
shutil.copy2(p, p + '.bak_vdssn_' + time.strftime('%Y%m%d_%H%M%S'))
io.open(p, 'w', encoding='utf-8', newline=nl).write(s)
print('patched', p, hashlib.md5(open(p, 'rb').read()).hexdigest())
