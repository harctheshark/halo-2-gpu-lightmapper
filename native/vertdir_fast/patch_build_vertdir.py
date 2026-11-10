"""patch_build_vertdir.py <build_vertdir.py> -- 0.6.12: build_vertdir.py runs vertdir_fast.exe (beside it) first for its default construction
and keeps the exe's result (exit 0 written / 2 bad input); any other outcome (exe missing, exit 3 = a path the native build does not carry, a
crash) runs the Python builder exactly as before. GPULM_VERTDIR_NATIVE=0 = always the Python. Asserted edits; backs the file up first
(.bak_vdnative_<ts>); refuses a file that is not the ddbf4bd4 builder or is already patched."""
import hashlib
import io
import shutil
import sys
import time

BASE_MD5 = 'ddbf4bd4095403d9b7115788bb9eb1a4'
p = sys.argv[1]
raw = open(p, 'rb').read()
if hashlib.md5(raw).hexdigest() != BASE_MD5:
    sys.exit('REFUSED: %s is not the ddbf4bd4 build_vertdir.py (md5 %s)' % (p, hashlib.md5(raw).hexdigest()))
s = raw.decode('utf-8')
nl = '\r\n' if '\r\n' in s else '\n'
s = s.replace('\r\n', '\n')


def rep(a, b):
    global s
    assert s.count(a) == 1, (a[:80], s.count(a))
    s = s.replace(a, b)


rep('import numpy as np, struct, sys, os, time, zlib, argparse\n',
    'import numpy as np, struct, sys, os, time, zlib, argparse, subprocess\n')
rep('"""\nimport numpy as np',
    '\nnative (0.6.12) -- vertdir_fast.exe beside this script builds the DEFAULT construction (exact keys, the CPU splat weights, the supersample\n'
    '  splat from vdsamp.bin) natively, byte-identical (native/vertdir_fast; a test map 31 s -> 4 s, a 4-part bake 188 s -> 22 s). It is run first; its\n'
    '  exit 0 (written) / 2 (bad input) is final, anything else (no exe, exit 3 = the offline search or an input it does not carry, a crash)\n'
    '  runs the Python below unchanged. GPULM_VERTDIR_NATIVE=0 = always the Python.\n'
    '"""\nimport numpy as np')
rep("if __name__ == '__main__':\n",
    "def run_native(A):\n"
    "    \"\"\"vertdir_fast.exe for the default construction -> its exit code (0 / 2), or None = run the Python builder.\"\"\"\n"
    "    if A.legacy or A.centre_splat or A.ssn <= 0 or os.environ.get('GPULM_VERTDIR_NATIVE', '') == '0' or os.environ.get('VD_OWN_SEARCH'):\n"
    "        return None\n"
    "    exe = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'vertdir_fast.exe')\n"
    "    if not os.path.isfile(exe):\n"
    "        return None\n"
    "    cmd = [exe, A.run, '--ssn', str(A.ssn)]\n"
    "    for k, v in (('--atlas', A.atlas), ('--dir', A.dir), ('--soup', A.soup), ('--out', A.out)):\n"
    "        if v:\n"
    "            cmd += [k, v]\n"
    "    cmd += (['--stamp'] if A.stamp else []) + (['--quiet'] if A.quiet else [])\n"
    "    try:\n"
    "        r = subprocess.run(cmd, capture_output=True, creationflags=0x08000000)\n"
    "    except OSError as e:\n"
    "        print('VERTDIR-BUILD: native builder could not start (%s) -- the Python builder runs' % e, flush=True)\n"
    "        return None\n"
    "    txt = r.stdout.decode('utf-8', 'replace') + r.stderr.decode('utf-8', 'replace')\n"
    "    if r.returncode in (0, 2):\n"
    "        for line in txt.splitlines():\n"
    "            print(line, flush=True)\n"
    "        return r.returncode\n"
    "    if not A.quiet:\n"
    "        for line in txt.splitlines():\n"
    "            print(line, flush=True)\n"
    "    print('VERTDIR-BUILD: native builder exit %d -- the Python builder runs' % r.returncode, flush=True)\n"
    "    return None\n"
    "\n"
    "if __name__ == '__main__':\n")
rep("    A = ap.parse_args(); QUIET = A.quiet\n",
    "    A = ap.parse_args(); QUIET = A.quiet\n"
    "    rn = run_native(A)\n"
    "    if rn is not None:\n"
    "        sys.exit(rn)\n")
shutil.copy2(p, p + '.bak_vdnative_' + time.strftime('%Y%m%d_%H%M%S'))
io.open(p, 'w', encoding='utf-8', newline=nl).write(s)
print('patched', p, hashlib.md5(open(p, 'rb').read()).hexdigest())
