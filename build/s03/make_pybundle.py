# S0.3 (H2 GPU Lightmapper standalone): build the isolated, relocatable CPython 3.10.5 + numpy 2.2.6 [+ scipy 1.15.3] runtime
# folder ("rt\py310-<h8>") from the VALIDATED local installs -- no download. Derived from
# tools\pkgres_from_research\make_pybundle.py (research proof); additions are marked [S0.3].
#
#   usage: "C:\Program Files\Python310\python.exe" -I make_pybundle.py <outdir> [--scipy] [--zip <out.zip>]
#                                                   [--stdlib both|pyc|src]
#
# Layout written (outdir must not exist; built in <outdir>.tmp_<pid> and renamed at the end):
#   python.exe pythonw.exe py.exe(= python.exe)  python310.dll python3.dll vcruntime140.dll vcruntime140_1.dll LICENSE.txt
#   *.pyd + libffi-7.dll libssl-1_1.dll libcrypto-1_1.dll sqlite3.dll   (Python310\DLLs minus Tk and _test*)
#   python310.zip      stdlib (minus tests/Tk/idle/ensurepip/lib2to3/venv/turtledemo), [S0.3] unchecked-hash .pyc next to
#                      each .py (zipimport loads the .pyc, tracebacks still get source lines), the patched site.py and the
#                      bundle's sitecustomize.py
#   site-packages\     numpy (+ numpy.libs, dist-info) [+ scipy, scipy.libs, dist-info], minus tests/.pyi/.pxd/C headers/
#                      static libs, [S0.3] every .py precompiled to __pycache__ (unchecked-hash) so a run never writes
#   python310._pth     "python310.zip / . / site-packages / import site"  -> isolated: no registry, no PYTHONPATH/HOME
#   H2LM_PYBUNDLE.json [S0.3] versions, source md5s, patches, bundle_id (h8) and every other file's size + md5
#
# [S0.3] Two verified 3.10.5 ._pth traps are fixed here (see PROGRESS.md):
#   1. "import site" under a ._pth still adds %APPDATA%\Python\Python310\site-packages (isolated=1 but no_user_site=0)
#      -> site.py patch: check_enableusersite() returns False when sys.flags.isolated.
#   2. isolated mode drops sys.path[0] (the script's dir) -> sitecustomize.py re-adds exactly what CPython would.
import os, sys, shutil, zipfile, hashlib, json, ctypes, importlib.util, marshal, time

try:
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL
except Exception:
    pass

PY = r'C:\Program Files\Python310'
SP = os.path.join(os.environ['APPDATA'], 'Python', 'Python310', 'site-packages')
HERE = os.path.dirname(os.path.abspath(__file__))
WANT_PY = (3, 10, 5)
WANT_NUMPY = '2.2.6'
WANT_SCIPY = '1.15.3'
SITE_PY_MD5 = '23cf5b302f557f7461555a35a0dc8c15'          # C:\Program Files\Python310\Lib\site.py (3.10.5) as patched
SITE_OLD = ('    if sys.flags.no_user_site:\n'
            '        return False\n'
            '\n'
            '    if hasattr(os, "getuid") and hasattr(os, "geteuid"):\n')
SITE_NEW = ('    if sys.flags.no_user_site:\n'
            '        return False\n'
            '    if sys.flags.isolated:\n'
            '        # H2LM bundle patch (S0.3): a ._pth file makes 3.10 isolated but leaves no_user_site 0, so "import site"\n'
            '        # would still add %APPDATA%\\Python\\Python310\\site-packages. Isolated means no user site, as with -I.\n'
            '        return False\n'
            '\n'
            '    if hasattr(os, "getuid") and hasattr(os, "geteuid"):\n')
ROOT_FILES = ('python.exe', 'pythonw.exe', 'python310.dll', 'python3.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
              'LICENSE.txt')
STDLIB_SKIP_DIRS = {'site-packages', 'test', 'tests', 'idlelib', 'tkinter', 'ensurepip', 'lib2to3', 'turtledemo',
                    '__pycache__', 'venv'}
STDLIB_SKIP_FILES = {'turtle.py'}                          # tkinter-only
PTH = 'python310.zip\n.\nsite-packages\nimport site\n'
# [S0.3] files inside a stripped tests/ dir that RUNTIME code imports (found by tests\import_all.py): numpy 2.2.6's
# numpy/testing/_private/utils.py does `from numpy._core.tests._natype import pd_NA` at import, and scipy's
# array_api_compat does `from numpy import *`, which pulls numpy.testing -> without this file scipy.spatial cannot import.
KEEP_TEST_FILES = {'numpy': ['_core/tests/_natype.py']}
ZIP_DT = (1980, 1, 1, 0, 0, 0)


def die(msg):
    sys.exit('make_pybundle: ' + msg)


def md5f(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def dist_version(name):
    import glob
    hits = glob.glob(os.path.join(SP, name + '-*.dist-info'))
    if len(hits) != 1:
        die('expected one %s dist-info in %s, found %s' % (name, SP, hits))
    for line in open(os.path.join(hits[0], 'METADATA'), encoding='utf-8'):
        if line.startswith('Version:'):
            return line.split(':', 1)[1].strip(), hits[0]
    die('no Version in ' + hits[0])


# [S0.3] each .pyc is compiled in a FRESH interpreter (-S -s -B, PYTHONHASHSEED=0). marshal sets FLAG_REF on an object only
# when its refcount is > 1, so an interned name kept alive by some earlier compile in the same process flips bytes
# (random.pyc 'register_at_fork' differed between two otherwise identical builds). A fresh process per file makes the
# refcounts -- and so the bytes -- a function of the source alone.
_COMPILER = ('import sys, importlib.util, importlib._bootstrap_external as be\n'
             'src = sys.stdin.buffer.read()\n'
             'try:\n'
             '    code = compile(src, sys.argv[1], "exec", dont_inherit=True, optimize=0)\n'
             'except SyntaxError as e:\n'
             '    sys.stderr.write("SyntaxError: %s" % e); sys.exit(3)\n'
             'sys.stdout.buffer.write(be._code_to_hash_pyc(code, importlib.util.source_hash(src), checked=False))\n')
_CENV = None


def pyc_bytes(source, dfile):
    """unchecked-hash .pyc (PEP 552): deterministic (no mtime), never revalidated, never rewritten."""
    global _CENV
    import subprocess
    if _CENV is None:
        _CENV = {k: os.environ[k] for k in ('SYSTEMROOT', 'TEMP', 'TMP') if k in os.environ}
        _CENV['PYTHONHASHSEED'] = '0'
    r = subprocess.run([sys.executable, '-S', '-s', '-B', '-c', _COMPILER, dfile], input=source, capture_output=True,
                       env=_CENV, creationflags=0x08000000 | 0x00004000)
    if r.returncode == 3:
        raise SyntaxError(r.stderr.decode('utf-8', 'replace'))
    if r.returncode != 0 or len(r.stdout) < 17:
        die('compiler child failed on %s (rc %d): %s' % (dfile, r.returncode, r.stderr.decode('utf-8', 'replace')[-400:]))
    data = r.stdout
    # self-check: the child's code object equals an in-process compile (code equality ignores marshal ref flags)
    if marshal.loads(data[16:]) != compile(source, dfile, 'exec', dont_inherit=True, optimize=0):
        die('compiled code mismatch for ' + dfile)
    return data


def build_stdlib_zip(out, mode, site_src, sc_src):
    n_py = n_pyc = 0
    entries = []
    root = os.path.join(PY, 'Lib')
    for dp, dns, fns in os.walk(root):
        dns[:] = sorted(d for d in dns if d not in STDLIB_SKIP_DIRS)
        for f in sorted(fns):
            if not f.endswith('.py') or f in STDLIB_SKIP_FILES:
                continue
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, root)
            src = site_src if rel == 'site.py' else open(p, 'rb').read()
            entries.append((rel, src))
    entries.append(('sitecustomize.py', sc_src))
    entries.sort(key=lambda e: e[0].replace('\\', '/'))
    bad = []
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for rel, src in entries:
            arc = rel.replace('\\', '/')
            if mode in ('both', 'src'):
                zi = zipfile.ZipInfo(arc, ZIP_DT); zi.compress_type = zipfile.ZIP_DEFLATED; zi.external_attr = 0o644 << 16
                z.writestr(zi, src); n_py += 1
            if mode in ('both', 'pyc'):
                try:
                    data = pyc_bytes(src, rel)
                except SyntaxError as e:
                    bad.append('%s: %s' % (rel, e)); continue
                zi = zipfile.ZipInfo(arc[:-3] + '.pyc', ZIP_DT); zi.compress_type = zipfile.ZIP_DEFLATED
                zi.external_attr = 0o644 << 16
                z.writestr(zi, data); n_pyc += 1
    return n_py, n_pyc, bad


def copy_pkg(src, dst, extra_skip_rel=()):
    def ign(d, names):
        rel = os.path.relpath(d, src)
        out = []
        for n in names:
            r = os.path.normpath(os.path.join(rel, n))
            if n in ('tests', '__pycache__') or n.endswith(('.pyi', '.pxd')) or r in extra_skip_rel:
                out.append(n)
            elif n.endswith(('.lib', '.a', '.h')) and os.path.isfile(os.path.join(d, n)):
                out.append(n)
        return out
    shutil.copytree(src, dst, ignore=ign)


def precompile_tree(root):
    """every .py -> __pycache__/<name>.cpython-310.pyc (unchecked-hash), so importing never writes into the runtime."""
    n, bad = 0, []
    for dp, dns, fns in os.walk(root):
        dns.sort()
        for f in sorted(fns):
            if not f.endswith('.py'):
                continue
            p = os.path.join(dp, f)
            src = open(p, 'rb').read()
            try:
                # relative co_filename keeps the build deterministic; the loader re-points it (_fix_co_filename) at import
                data = pyc_bytes(src, os.path.relpath(p, root))
            except SyntaxError as e:
                bad.append('%s: %s' % (os.path.relpath(p, root), e)); continue
            c = importlib.util.cache_from_source(p)
            os.makedirs(os.path.dirname(c), exist_ok=True)
            with open(c, 'wb') as fh:
                fh.write(data)
            n += 1
    return n, bad


def det_zip(src_dir, out_zip):
    """deterministic zip: sorted forward-slash names, fixed 1980 timestamps, fixed attributes, DEFLATE level 9."""
    names = []
    for dp, dns, fns in os.walk(src_dir):
        for f in fns:
            p = os.path.join(dp, f)
            names.append((os.path.relpath(p, src_dir).replace('\\', '/'), p))
    names.sort()
    with zipfile.ZipFile(out_zip, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for arc, p in names:
            zi = zipfile.ZipInfo(arc, ZIP_DT); zi.compress_type = zipfile.ZIP_DEFLATED; zi.external_attr = 0o644 << 16
            zi.create_system = 0
            with open(p, 'rb') as fh:
                z.writestr(zi, fh.read(), compresslevel=9)
    return os.path.getsize(out_zip)


def main(argv):
    if not argv or argv[0].startswith('-'):
        die('usage: make_pybundle.py <outdir> [--scipy] [--zip <out.zip>] [--stdlib both|pyc|src]')
    out = os.path.abspath(argv[0])
    want_scipy = '--scipy' in argv
    zip_out = argv[argv.index('--zip') + 1] if '--zip' in argv else None
    mode = argv[argv.index('--stdlib') + 1] if '--stdlib' in argv else 'both'
    if mode not in ('both', 'pyc', 'src'):
        die('--stdlib must be both|pyc|src')
    if tuple(sys.version_info[:3]) != WANT_PY or os.path.normcase(sys.base_prefix) != os.path.normcase(PY):
        die('run me with %s\\python.exe %s (the .pyc magic must match the bundled interpreter); this is %s at %s'
            % (PY, '.'.join(map(str, WANT_PY)), sys.version.split()[0], sys.base_prefix))
    if os.path.exists(out):
        die('outdir exists, refusing: ' + out)
    if zip_out and os.path.exists(zip_out):
        die('zip exists, refusing: ' + zip_out)
    npv, np_dist = dist_version('numpy')
    if npv != WANT_NUMPY:
        die('numpy %s found, want %s' % (npv, WANT_NUMPY))
    if want_scipy:
        spv, sp_dist = dist_version('scipy')
        if spv != WANT_SCIPY:
            die('scipy %s found, want %s' % (spv, WANT_SCIPY))
    site_path = os.path.join(PY, 'Lib', 'site.py')
    if md5f(site_path) != SITE_PY_MD5:
        die('site.py md5 %s != %s: re-review the user-site patch before building' % (md5f(site_path), SITE_PY_MD5))
    site_src = open(site_path, 'rb').read().decode('utf-8').replace('\r\n', '\n')
    if site_src.count(SITE_OLD) != 1:
        die('site.py patch anchor not found exactly once')
    site_new = site_src.replace(SITE_OLD, SITE_NEW).encode('utf-8')
    # LF-normalised so a git checkout with core.autocrlf (CRLF on disk) builds the same bytes / bundle_id
    sc_src = open(os.path.join(HERE, 'bundle_src', 'sitecustomize.py'), 'rb').read().replace(b'\r\n', b'\n')

    t0 = time.time()
    tmp = out + '.tmp_%d' % os.getpid()
    os.makedirs(tmp)
    for f in ROOT_FILES:
        shutil.copy2(os.path.join(PY, f), tmp)
    shutil.copy2(os.path.join(PY, 'python.exe'), os.path.join(tmp, 'py.exe'))   # [S0.3] gpu_bake calls `py`
    dll_skipped = []
    for f in sorted(os.listdir(os.path.join(PY, 'DLLs'))):
        fl = f.lower()
        if fl.startswith(('_tkinter', 'tcl', 'tk', '_test', '_ctypes_test')) or fl.endswith(('.ico', '.cat')):
            dll_skipped.append(f); continue
        shutil.copy2(os.path.join(PY, 'DLLs', f), tmp)
    n_py, n_pyc, bad_std = build_stdlib_zip(os.path.join(tmp, 'python310.zip'), mode, site_new, sc_src)
    spo = os.path.join(tmp, 'site-packages')
    os.makedirs(spo)
    copy_pkg(os.path.join(SP, 'numpy'), os.path.join(spo, 'numpy'),
             extra_skip_rel=(os.path.normpath('_core/include'), os.path.normpath('_core/lib'), os.path.normpath('random/lib')))
    for pkg, rels in KEEP_TEST_FILES.items():
        if pkg == 'scipy' and not want_scipy:
            continue
        for rel in rels:
            s = os.path.join(SP, pkg, os.path.normpath(rel))
            if not os.path.isfile(s):
                die('KEEP_TEST_FILES entry missing in the source: ' + s)
            d = os.path.join(spo, pkg, os.path.normpath(rel))
            os.makedirs(os.path.dirname(d), exist_ok=True)
            shutil.copy2(s, d)
    shutil.copytree(os.path.join(SP, 'numpy.libs'), os.path.join(spo, 'numpy.libs'))
    shutil.copytree(np_dist, os.path.join(spo, os.path.basename(np_dist)))
    if want_scipy:
        copy_pkg(os.path.join(SP, 'scipy'), os.path.join(spo, 'scipy'))
        shutil.copytree(os.path.join(SP, 'scipy.libs'), os.path.join(spo, 'scipy.libs'))
        shutil.copytree(sp_dist, os.path.join(spo, os.path.basename(sp_dist)))
    n_sp, bad_sp = precompile_tree(spo)
    with open(os.path.join(tmp, 'python310._pth'), 'w', newline='\n') as f:
        f.write(PTH)
    files = []
    for dp, dns, fns in os.walk(tmp):
        for f in fns:
            p = os.path.join(dp, f)
            files.append(dict(path=os.path.relpath(p, tmp).replace('\\', '/'), size=os.path.getsize(p), md5=md5f(p)))
    files.sort(key=lambda r: r['path'])
    bid = hashlib.md5(''.join('%s %s\n' % (r['path'], r['md5']) for r in files).encode()).hexdigest()
    man = dict(
        schema='h2lm-pybundle/1', bundle_id=bid, h8=bid[:8],
        python='%d.%d.%d' % WANT_PY, numpy=npv, scipy=(spv if want_scipy else None),
        stdlib_mode=mode, built_utc=None,   # deliberately no timestamp: the manifest is part of a deterministic payload
        # E1 (0.6.1): the generic spelling, not the build machine's C:\Users\<name>\AppData\Roaming path (tools\payload\zip_payloads.py
        # scrubs bundles built before this change the same way)
        source=dict(python_dir=PY, user_site='%APPDATA%\\Python\\Python310\\site-packages', python_exe_md5=md5f(os.path.join(PY, 'python.exe')),
                    python310_dll_md5=md5f(os.path.join(PY, 'python310.dll')), site_py_md5=SITE_PY_MD5),
        patches=['Lib/site.py: check_enableusersite() returns False when sys.flags.isolated (3.10 ._pth leaves no_user_site 0)',
                 'sitecustomize.py (in python310.zip): re-adds CPython 3.10 sys.path[0] that isolated mode drops',
                 'py.exe = copy of python.exe'],
        pth=PTH.splitlines(), dlls_skipped=dll_skipped,
        compile_skipped=dict(stdlib=bad_std, site_packages=bad_sp),
        counts=dict(stdlib_py=n_py, stdlib_pyc=n_pyc, site_packages_pyc=n_sp),
        files=files)
    with open(os.path.join(tmp, 'H2LM_PYBUNDLE.json'), 'w', newline='\n') as f:
        json.dump(man, f, indent=1, sort_keys=True)
    os.rename(tmp, out)
    raw = sum(r['size'] for r in files)
    print('bundle %s: %d files, %.2f MB raw, id %s, %.1fs' % (out, len(files) + 1, raw / 1048576, bid[:8], time.time() - t0))
    print('stdlib: %d .py + %d .pyc (mode %s); site-packages: %d .pyc; compile skipped: stdlib %d, site-packages %d'
          % (n_py, n_pyc, mode, n_sp, len(bad_std), len(bad_sp)))
    for b in bad_std + bad_sp:
        print('  compile skipped: ' + b)
    if zip_out:
        zs = det_zip(out, zip_out)
        print('zip %s: %.2f MB (%d bytes), md5 %s' % (zip_out, zs / 1048576, zs, md5f(zip_out)))
    return 0


if __name__ == '__main__':
    if sys.flags.hash_randomization:
        # [S0.3] deterministic .pyc: frozenset constants (`x in {'a', 'b'}`) are marshalled in str-hash order, which
        # changes per process under hash randomization (23 stdlib .pyc differed between two builds). PYTHONHASHSEED is
        # ignored under -I/-E, so re-run once with a scratch environment: -s (no user site), -B, PYTHONHASHSEED=0.
        import subprocess
        env = {k: os.environ[k] for k in ('SYSTEMROOT', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP', 'USERPROFILE', 'PATH')
               if k in os.environ}
        env['PYTHONHASHSEED'] = '0'
        sys.exit(subprocess.call([sys.executable, '-s', '-B', os.path.abspath(__file__)] + sys.argv[1:], env=env))
    sys.exit(main(sys.argv[1:]))
