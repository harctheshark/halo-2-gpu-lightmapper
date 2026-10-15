# S0.3: runs INSIDE the bundle interpreter (bundle\py.exe probe_in_bundle.py ...). Read-only: it imports modules, loads DLLs
# with LoadLibrary and prints a JSON report; it writes nothing except the --json file (and sets sys.dont_write_bytecode
# before any --local-dir import, so production folders never get a __pycache__).
#   --chain import_chain.json   import every external module (and every "from X import name") the pipeline scripts use
#   --local-dir D --mods a,b    import these pipeline modules from D (sys.path[0] = D, as the script itself would see)
#   --work-root W               modules a local module pulls from elsewhere under W (lp_* -> $W\v043-tagwriter) are allowed
#   --load-dll P                ctypes.CDLL(P) (engine DLLs: packfast.dll, seamfast.dll)
#   --expect-scipy 0|1          scipy must (1) / must not (0) be importable
#   --json OUT
# exit 0 = every check passed
import sys, os, json, importlib, builtins

R = dict(ok=True, fails=[], notes=[])


def fail(msg):
    R['ok'] = False
    R['fails'].append(msg)


def args():
    a = sys.argv[1:]
    o = dict(chain=None, local_dir=None, work_root=None, mods=[], dlls=[], expect_scipy=None, json=None)
    i = 0
    while i < len(a):
        k = a[i]
        if k == '--chain': o['chain'] = a[i + 1]; i += 2
        elif k == '--local-dir': o['local_dir'] = a[i + 1]; i += 2
        elif k == '--work-root': o['work_root'] = a[i + 1]; i += 2
        elif k == '--mods': o['mods'] = [m for m in a[i + 1].split(',') if m]; i += 2
        elif k == '--load-dll': o['dlls'].append(a[i + 1]); i += 2
        elif k == '--expect-scipy': o['expect_scipy'] = a[i + 1] == '1'; i += 2
        elif k == '--json': o['json'] = a[i + 1]; i += 2
        else: raise SystemExit('probe: unknown arg ' + k)
    return o


def under(p, root):
    try:
        return os.path.commonpath([os.path.normcase(os.path.abspath(p)), os.path.normcase(root)]) == os.path.normcase(root)
    except ValueError:
        return False


def loaded_dlls():
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    psapi = ctypes.WinDLL('psapi', use_last_error=True)
    h = k32.GetCurrentProcess()
    arr = (wintypes.HMODULE * 2048)()
    need = wintypes.DWORD()
    psapi.EnumProcessModules.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.HMODULE), wintypes.DWORD,
                                         ctypes.POINTER(wintypes.DWORD)]
    if not psapi.EnumProcessModules(h, arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return []
    n = need.value // ctypes.sizeof(wintypes.HMODULE)
    k32.GetModuleFileNameW.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    out = []
    for i in range(min(n, 2048)):
        buf = ctypes.create_unicode_buffer(1024)
        k32.GetModuleFileNameW(arr[i], buf, 1024)
        out.append(buf.value)
    return out


def main():
    o = args()
    B = os.path.dirname(os.path.abspath(sys.executable))
    HERE = os.path.dirname(os.path.abspath(__file__))
    allowed = [B, HERE] + [x for x in (o['local_dir'], o['work_root']) if x]
    R['path0_is_probe_dir'] = bool(sys.path) and os.path.normcase(sys.path[0]) == os.path.normcase(HERE)
    R['bundle'] = B
    R['sys'] = dict(executable=sys.executable, prefix=sys.prefix, base_prefix=sys.base_prefix, version=sys.version,
                    path=list(sys.path), isolated=sys.flags.isolated, no_user_site=sys.flags.no_user_site,
                    ignore_environment=sys.flags.ignore_environment, no_site=sys.flags.no_site,
                    dont_write_bytecode=sys.flags.dont_write_bytecode, stdout_encoding=sys.stdout.encoding,
                    exit_builtin=hasattr(builtins, 'exit'), quit_builtin=hasattr(builtins, 'quit'))
    R['env_seen'] = {k: os.environ.get(k) for k in ('PYTHONHOME', 'PYTHONPATH', 'PYTHONSTARTUP', 'PYTHONUSERBASE',
                                                     'PYTHONIOENCODING', 'PYTHONDONTWRITEBYTECODE')}
    try:
        import site
        R['sys']['site_ENABLE_USER_SITE'] = site.ENABLE_USER_SITE
    except Exception as e:
        fail('import site: %r' % e)
    if not under(sys.executable, B) or os.path.normcase(sys.prefix) != os.path.normcase(B):
        fail('interpreter/prefix not the bundle: %s / %s' % (sys.executable, sys.prefix))
    if sys.flags.isolated != 1:
        fail('sys.flags.isolated = %s' % sys.flags.isolated)
    if R['sys'].get('site_ENABLE_USER_SITE'):
        fail('site.ENABLE_USER_SITE is true')
    appdata_site = os.path.join(os.environ.get('APPDATA', '?'), 'Python')
    for p in sys.path:
        if p and not any(under(p, r) for r in allowed):
            fail('sys.path entry outside the bundle: %s' % p)
        if p and under(p, appdata_site):
            fail('user site on sys.path: %s' % p)

    # numpy (+ a BLAS call: exercises numpy.libs openblas + msvcp140) and scipy
    import numpy
    R['numpy'] = dict(version=numpy.__version__, file=numpy.__file__)
    if numpy.__version__ != '2.2.6':
        fail('numpy %s' % numpy.__version__)
    if not under(numpy.__file__, B):
        fail('numpy outside the bundle: %s' % numpy.__file__)
    a = numpy.arange(9, dtype=numpy.float64).reshape(3, 3) + numpy.eye(3)
    R['numpy']['blas_check'] = float(numpy.linalg.det(a))
    R['numpy']['inv_ok'] = bool(numpy.allclose(numpy.linalg.inv(a) @ a, numpy.eye(3)))
    try:
        import numpy.testing                                  # needs numpy/_core/tests/_natype.py (KEEP_TEST_FILES)
        ns = {}
        exec('from numpy import *', ns)                       # what scipy's array_api_compat does
        R['numpy']['testing_and_star_import'] = 'ok'
    except Exception as e:
        fail('numpy.testing / from numpy import *: %r' % e)
    try:
        import scipy
        from scipy.spatial import cKDTree
        pts = numpy.array([[0, 0, 0], [0.5, 0, 0], [3, 0, 0]], dtype=numpy.float64)
        pairs = sorted(map(tuple, cKDTree(pts).query_pairs(1.0, output_type='ndarray').tolist()))
        R['scipy'] = dict(version=scipy.__version__, file=scipy.__file__, ckdtree_pairs=pairs)
        if not under(scipy.__file__, B):
            fail('scipy outside the bundle: %s' % scipy.__file__)
        if o['expect_scipy'] is False:
            fail('scipy importable but this bundle must not have it (%s)' % scipy.__file__)
        if pairs != [(0, 1)]:
            fail('cKDTree pairs %s' % pairs)
    except ImportError as e:
        R['scipy'] = dict(import_error=repr(e))
        if o['expect_scipy']:
            fail('scipy not importable: %r' % e)

    # the pipeline's external imports
    if o['chain']:
        ch = json.load(open(o['chain']))
        res = {}
        for mod, names in ch['external'].items():
            if mod.split('.')[0] == 'scipy' and not o['expect_scipy']:
                res[mod] = 'skipped (no scipy in this bundle; only ao_seamfix needs it)'
                continue
            try:
                m = importlib.import_module(mod)
                miss = [n for n in names if not hasattr(m, n)]
                for n in list(miss):
                    try:
                        importlib.import_module(mod + '.' + n); miss.remove(n)
                    except ImportError:
                        pass
                f = getattr(m, '__file__', None)
                res[mod] = dict(file=f, missing_names=miss)
                if miss:
                    fail('%s lacks %s' % (mod, miss))
                if f and not under(f, B):
                    fail('%s loaded from outside the bundle: %s' % (mod, f))
            except Exception as e:
                res[mod] = 'IMPORT FAILED: %r' % e
                fail('import %s: %r' % (mod, e))
        R['external'] = res

    # pipeline modules from their own folder
    if o['local_dir']:
        sys.dont_write_bytecode = True
        D = os.path.abspath(o['local_dir'])
        sys.path.insert(0, D)
        res = {}
        for mod in o['mods']:
            try:
                m = importlib.import_module(mod)
                f = os.path.abspath(m.__file__)
                res[mod] = dict(file=f)
                if os.path.normcase(os.path.dirname(f)) != os.path.normcase(D):
                    # a module may legitimately come from a sibling dir it inserts itself (lp_* -> $W\v043-tagwriter)
                    res[mod]['note'] = 'resolved outside --local-dir'
            except SystemExit as e:
                res[mod] = 'SystemExit on import: %r' % e
                fail('%s raised SystemExit on import' % mod)
            except Exception as e:
                res[mod] = 'IMPORT FAILED: %r' % e
                fail('import %s from %s: %r' % (mod, D, e))
        R['local'] = dict(dir=D, mods=res)

    for p in o['dlls']:
        import ctypes
        try:
            ctypes.CDLL(p)
            R.setdefault('dll_loads', {})[p] = 'ok'
        except OSError as e:
            R.setdefault('dll_loads', {})[p] = repr(e)
            fail('LoadLibrary %s: %r' % (p, e))

    # every imported module and every loaded DLL must come from the bundle, the local dir, or Windows
    win = os.environ.get('SystemRoot', r'C:\Windows')
    outside = []
    for name, m in list(sys.modules.items()):
        f = getattr(m, '__file__', None)
        if f and not any(under(f, r) for r in allowed):
            outside.append('%s=%s' % (name, f))
    if outside:
        fail('modules outside the bundle: %s' % outside[:10])
    dl = loaded_dlls()
    R['loaded_dlls'] = dict(total=len(dl), bundle=sorted(os.path.relpath(p, B) for p in dl if under(p, B)),
                            other=sorted(p for p in dl if not under(p, B) and not under(p, win)))
    R['vc_runtime_loaded'] = sorted(p for p in dl if os.path.basename(p).lower().startswith(('vcruntime140', 'msvcp140')))
    for p in R['vc_runtime_loaded']:
        if not under(p, B):
            fail('VC runtime loaded from outside the bundle: %s' % p)
    for p in R['loaded_dlls']['other']:
        if not any(under(p, x) for x in o['dlls']) and not any(os.path.normcase(p) == os.path.normcase(os.path.abspath(x)) for x in o['dlls']):
            fail('DLL loaded from outside the bundle and Windows: %s' % p)
    R['modules_loaded'] = len(sys.modules)


try:
    main()
except Exception as e:
    import traceback
    fail('probe crashed: %s' % traceback.format_exc())
o = None
try:
    o = args()
except SystemExit:
    pass
txt = json.dumps(R, indent=1, default=str)
if o and o['json']:
    with open(o['json'], 'w') as f:
        f.write(txt)
print('PROBE %s fails=%d' % ('OK' if R['ok'] else 'FAIL', len(R['fails'])))
for x in R['fails']:
    print('  FAIL: ' + x)
sys.exit(0 if R['ok'] else 1)
