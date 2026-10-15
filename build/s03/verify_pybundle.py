# S0.3: verify an isolated Python bundle built by make_pybundle.py. Read-only towards production (every child that touches a
# production folder runs with PYTHONDONTWRITEBYTECODE=1 and imports only; --help runs only argparse-first scripts).
# Children run one at a time (the multiprocessing check uses 2 workers), BELOW_NORMAL, CREATE_NO_WINDOW.
#
#   usage: "C:\Program Files\Python310\python.exe" -I verify_pybundle.py <bundle_dir> --chain import_chain.json
#          [--expect-scipy] [--zip <payload.zip>] [--work <GPU Lightmap Work>] [--json report.json]
# exit 0 = every MUST check passed (INFO checks never fail the run)
import os, sys, json, subprocess, hashlib, time, zipfile, shutil

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pe_deps  # noqa: E402
import make_pybundle  # noqa: E402  (KEEP_TEST_FILES; importing it builds nothing)

FLAGS = 0x08000000 | 0x00004000          # CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS
SYSPY = r'C:\Program Files\Python310\python.exe'
WIN = os.environ.get('SystemRoot', r'C:\Windows')
REPORT = dict(checks=[], ok=True)


def check(name, ok, detail=None, must=True):
    REPORT['checks'].append(dict(name=name, ok=bool(ok), must=must, detail=detail))
    if must and not ok:
        REPORT['ok'] = False
    print('%-5s %s%s' % ('PASS' if ok else ('FAIL' if must else 'INFO'), name,
                         '' if detail is None else ' -- ' + (detail if isinstance(detail, str) else json.dumps(detail, default=str)[:600])))


def md5f(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def scratch_env(bundle, extra=None, bogus=True):
    """the child env the shell will build (design section 5): from scratch, bundle first on PATH."""
    env = {k: os.environ[k] for k in ('SYSTEMROOT', 'WINDIR', 'TEMP', 'TMP', 'APPDATA', 'LOCALAPPDATA', 'USERPROFILE',
                                       'COMSPEC', 'PATHEXT', 'SYSTEMDRIVE', 'PROGRAMDATA', 'PROGRAMFILES', 'USERNAME',
                                       'COMPUTERNAME', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE', 'OS')
           if k in os.environ}
    env['PATH'] = ';'.join([bundle, os.path.join(WIN, 'System32'), WIN, os.path.join(WIN, 'System32', 'Wbem')])
    if bogus:
        user_site = os.path.join(os.environ['APPDATA'], 'Python', 'Python310', 'site-packages')
        env['PYTHONHOME'] = r'C:\bogus_pythonhome'
        env['PYTHONPATH'] = r'C:\bogus_pythonpath;' + user_site      # the user site HAS scipy: must stay invisible
        env['PYTHONUSERBASE'] = os.path.join(os.environ['APPDATA'], 'Python')
        env['PYTHONSTARTUP'] = r'C:\bogus_startup.py'
    if extra:
        env.update(extra)
    return env


def run(args, env, cwd=None, stdin=None, timeout=300):
    t = time.time()
    p = subprocess.run(args, env=env, cwd=cwd, input=stdin, capture_output=True, text=True, creationflags=FLAGS,
                       timeout=timeout)
    return p.returncode, p.stdout, p.stderr, time.time() - t


def snapshot(root):
    s = {}
    for dp, dns, fns in os.walk(root):
        for d in dns:
            s[os.path.relpath(os.path.join(dp, d), root) + os.sep] = 0
        for f in fns:
            p = os.path.join(dp, f)
            st = os.stat(p)
            s[os.path.relpath(p, root)] = (st.st_size, st.st_mtime_ns)
    return s


def main(argv):
    B = os.path.abspath(argv[0])
    chain = os.path.abspath(argv[argv.index('--chain') + 1])
    expect_scipy = '--expect-scipy' in argv
    zpath = argv[argv.index('--zip') + 1] if '--zip' in argv else None
    W = argv[argv.index('--work') + 1] if '--work' in argv else os.environ.get('H2LM_TEST_GLW', '')
    out_json = argv[argv.index('--json') + 1] if '--json' in argv else None
    PYB = os.path.join(B, 'py.exe')
    REPORT['bundle'] = B
    man = json.load(open(os.path.join(B, 'H2LM_PYBUNDLE.json')))
    REPORT['bundle_id'] = man['bundle_id']
    tests = os.path.join(HERE, 'tests')
    before = snapshot(B)

    # ---- A. layout -------------------------------------------------------------------------------------------------
    need = ['python.exe', 'py.exe', 'pythonw.exe', 'python310.dll', 'python3.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
            'python310.zip', 'python310._pth', 'LICENSE.txt', 'site-packages/numpy/__init__.py',
            'site-packages/numpy-2.2.6.dist-info/LICENSE.txt']
    miss = [n for n in need if not os.path.isfile(os.path.join(B, n))]
    check('A1 required files present (incl. vcruntime140.dll + vcruntime140_1.dll in the bundle root)', not miss, miss or None)
    check('A2 py.exe is a byte copy of python.exe', md5f(PYB) == md5f(os.path.join(B, 'python.exe')), md5f(PYB))
    pth = open(os.path.join(B, 'python310._pth')).read()
    check('A3 python310._pth', pth == 'python310.zip\n.\nsite-packages\nimport site\n', pth.splitlines())
    libs = os.listdir(os.path.join(B, 'site-packages', 'numpy.libs'))
    check('A4 numpy.libs carries its own (mangled) msvcp140', any(n.lower().startswith('msvcp140-') for n in libs), libs)
    if expect_scipy:
        sl = os.listdir(os.path.join(B, 'site-packages', 'scipy.libs'))
        check('A5 scipy.libs present (scipy 1.15.3 .pyd import no msvcp140 at all: kernel32 + python310 + its openblas)', bool(sl), sl)
    def kept(path):   # a KEEP_TEST_FILES source or its __pycache__ .pyc
        for k, v in make_pybundle.KEEP_TEST_FILES.items():
            for f in v:
                d, stem = 'site-packages/%s/%s/' % (k, f.rsplit('/', 1)[0]), f.rsplit('/', 1)[1][:-3]
                if path.startswith(d) and os.path.basename(path).startswith(stem + '.'):
                    return True
        return False
    bad = [r['path'] for r in man['files']
           if ('tests' in r['path'].lower().split('/') and not kept(r['path']))
           or r['path'].lower().startswith(('_tkinter', 'tcl86', 'tk86', '_test', '_ctypes_test'))]
    with zipfile.ZipFile(os.path.join(B, 'python310.zip')) as z:
        zn = z.namelist()
        bad += [n for n in zn if n.split('/')[0] in ('tkinter', 'idlelib', 'test', 'ensurepip', 'lib2to3', 'turtledemo', 'venv')]
        site_src = z.read('site.py').decode()
        sc_present = 'sitecustomize.py' in zn and 'sitecustomize.pyc' in zn
    check('A6 tests / Tk / idle stripped', not bad, bad[:10] or None)
    check('A7 python310.zip carries the user-site patch in site.py and sitecustomize(.pyc)',
          'H2LM bundle patch (S0.3)' in site_src and 'if sys.flags.isolated:' in site_src and sc_present)

    # ---- B. manifest -----------------------------------------------------------------------------------------------
    listed = {r['path']: r for r in man['files']}
    on_disk = {}
    for dp, dns, fns in os.walk(B):
        for f in fns:
            rel = os.path.relpath(os.path.join(dp, f), B).replace('\\', '/')
            if rel != 'H2LM_PYBUNDLE.json':
                on_disk[rel] = os.path.join(dp, f)
    mism = [p for p, r in listed.items() if p not in on_disk or os.path.getsize(on_disk[p]) != r['size'] or md5f(on_disk[p]) != r['md5']]
    extra = sorted(set(on_disk) - set(listed))
    check('B1 H2LM_PYBUNDLE.json matches every file (size + md5), no extras', not mism and not extra,
          dict(mismatch=mism[:5], extra=extra[:5], files=len(listed), bundle_id=man['h8']))

    # ---- C. static PE audit ----------------------------------------------------------------------------------------
    pa = pe_deps.audit(B)
    check('C1 PE imports resolve on a clean PC (VC runtime bundled, rest in-box)', not pa['problems'],
          dict(pe_files=pa['pe_files'], machines=pa['machines'], vc_in_bundle=pa['vc_runtime_files_in_bundle'],
               problems=pa['problems'][:5], system32=sorted(pa['system32_dlls_used'])))

    # ---- D. isolation (bogus PYTHONHOME/PYTHONPATH -> user site with scipy, C:\Windows\py.exe present) ------------------
    check('D0 C:\\Windows\\py.exe present (the launcher the bundle must beat)', os.path.isfile(os.path.join(WIN, 'py.exe')),
          must=False)
    env = scratch_env(B)
    env.pop('PYTHONDONTWRITEBYTECODE', None)
    rc, so, se, dt = run([PYB, '-c', 'import numpy, sys, json; print(json.dumps([sys.executable, sys.prefix, sys.flags.isolated, '
                          'numpy.__version__, numpy.__file__, sys.path]))'], env, cwd=tests)
    try:
        exe, pref, iso, npv, npf, sp = json.loads(so)
        ok = (os.path.normcase(exe) == os.path.normcase(PYB) and os.path.normcase(pref) == os.path.normcase(B) and iso == 1
              and npv == '2.2.6' and os.path.normcase(npf).startswith(os.path.normcase(B)))
    except Exception:
        ok, sp = False, None
    check("D1 py -c 'import numpy,sys' under bogus PYTHONHOME/PYTHONPATH reports the bundle, isolated=1, numpy 2.2.6",
          rc == 0 and ok, dict(rc=rc, out=so.strip()[:400], err=se.strip()[-300:]))
    pj = os.path.join(os.path.dirname(B), 'probe_%s.json' % os.path.basename(B))
    rc, so, se, dt = run([PYB, os.path.join(HERE, 'probe_in_bundle.py'), '--chain', chain, '--expect-scipy',
                          '1' if expect_scipy else '0', '--json', pj], env, cwd=tests)
    pr = json.load(open(pj)) if os.path.isfile(pj) else {}
    REPORT['probe'] = pr
    check('D2 probe: every external pipeline import loads from the bundle; user site absent; VC runtime + all DLLs from the '
          'bundle/Windows; scipy %s' % ('present' if expect_scipy else 'ABSENT despite PYTHONPATH -> user-site scipy'),
          rc == 0 and pr.get('ok'), dict(rc=rc, fails=pr.get('fails'), vc=pr.get('vc_runtime_loaded'),
                                         scipy=pr.get('scipy'), err=se.strip()[-300:]))

    # ---- E. pwsh resolution of `py` -------------------------------------------------------------------------------
    pw_portable = os.path.join(os.path.dirname(os.path.dirname(HERE)), 'cache', 'pwsh-7.6.6-win-x64', 'pwsh.exe')
    pw = pw_portable if os.path.isfile(pw_portable) else shutil.which('pwsh')
    if pw:
        e2 = scratch_env(B)
        e2['PSModulePath'] = os.path.join(os.path.dirname(pw), 'Modules')
        e2['POWERSHELL_TELEMETRY_OPTOUT'] = '1'; e2['POWERSHELL_UPDATECHECK'] = 'Off'; e2['NO_COLOR'] = '1'
        rc, so, se, dt = run([pw, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                              os.path.join(tests, 'pwsh_py_resolve.ps1')], e2, cwd=tests)
        try:
            r = json.loads(so.strip().splitlines()[-1])
            says = json.loads(r['py_says'])
            ok = (os.path.normcase(r['resolved']) == os.path.normcase(PYB) and os.path.normcase(says[0]) == os.path.normcase(PYB)
                  and says[1] == 1)
        except Exception:
            r, ok = so[-400:], False
        check('E1 pwsh `& py` resolves to the bundle py.exe (PATH order), not C:\\Windows\\py.exe', rc == 0 and ok,
              dict(pwsh=pw, result=r, err=se.strip()[-300:]))
    else:
        check('E1 pwsh `& py` resolution', False, 'no pwsh found', must=False)

    # ---- F. CreateProcess search order (information for the shell) ------------------------------------------------
    rc, so, se, dt = run([SYSPY, '-I', '-c', 'import subprocess,sys; r=subprocess.run(["py","-c","import sys;print(sys.executable)"],'
                          'capture_output=True,text=True,creationflags=0x08000000); print(r.stdout.strip() or r.stderr.strip()[-200:])'],
                         scratch_env(B, bogus=False), cwd=tests)
    check('F1 CreateProcess("py") from a process OUTSIDE the bundle dir ignores PATH order (Windows dir is searched first)',
          os.path.normcase(so.strip()) != os.path.normcase(PYB), dict(final_interpreter=so.strip(), via='the Windows py launcher, not the bundle', note='launch the bundle by ABSOLUTE path from the shell / any non-pwsh caller'),
          must=False)

    # ---- G. sys.path[0] parity with a normal (non-isolated) CPython 3.10.5 -----------------------------------------
    s03 = HERE
    cases = [
        ('abs script', [os.path.join(tests, 'p0_main.py')], os.path.dirname(HERE), None),
        ('rel script backslash', ['tests\\p0_main.py'], s03, None),
        ('rel script slash', ['tests/p0_main.py'], s03, None),
        ('bare script', ['p0_main.py'], tests, None),
        ('-c', ['-c', 'import sys,json;print(json.dumps(dict(path0=sys.path[0])))'], tests, None),
        ('-m', ['-m', 'p0_main'], tests, None),
        ('stdin -', ['-'], tests, open(os.path.join(tests, 'p0_main.py')).read()),
        ('explicit -I', ['-I', 'p0_main.py'], tests, None),
        ('explicit -IB cluster', ['-IB', 'p0_main.py'], tests, None),
        ('-W with I in value', ['-W', 'ignore::ImportWarning', 'p0_main.py'], tests, None),
    ]
    rows, allsame = [], True
    for name, args, cwd, stdin in cases:
        rb = run([PYB] + args, scratch_env(B), cwd=cwd, stdin=stdin)
        rs = run([SYSPY, '-s'] + args if args[0] != '-I' and not args[0].startswith('-I') else [SYSPY] + args,
                 scratch_env(B, bogus=False), cwd=cwd, stdin=stdin)
        def norm(txt, prefix):
            # an explicit -I leaves the interpreter's own python310.zip at sys.path[0]: compare it as <prefix>\...
            try:
                d = json.loads(txt.strip())
                if d.get('path0') and os.path.normcase(d['path0']).startswith(os.path.normcase(prefix) + os.sep):
                    d['path0'] = '<prefix>' + d['path0'][len(prefix):]
                return json.dumps(d, sort_keys=True)
            except Exception:
                return txt.strip()
        same = (rb[0] == rs[0] and norm(rb[1], B) == norm(rs[1], os.path.dirname(SYSPY)))
        allsame &= same
        rows.append(dict(case=name, same=same, bundle=rb[1].strip() or rb[2].strip()[-160:], system=rs[1].strip() or rs[2].strip()[-160:]))
    check('G1 sys.path[0] and own-dir sibling imports identical to the system 3.10.5 (script/rel/bare/-c/-m/stdin/-I)',
          allsame, rows)

    # ---- H. the pipeline modules themselves, from their production folders (import only) ---------------------------
    ch = json.load(open(chain))
    groups = {}
    for s in ch['scripts']:
        v = s['import_safe_violations']
        if all(x.split(': ', 1)[1].startswith('assert ') for x in v):
            groups.setdefault(os.path.dirname(s['path']), []).append(os.path.splitext(os.path.basename(s['path']))[0])
    dlls = [os.path.join(W, 'engine_v373', 'seamfast.dll'), os.path.join(W, 'engine_v373', 'v043-tagwriter', 'bin', 'packfast.dll')]
    hres, hok = {}, True
    for i, (d, mods) in enumerate(sorted(groups.items())):
        ej = os.path.join(os.path.dirname(B), 'probe_local_%s_%d.json' % (os.path.basename(B), i))
        a = [PYB, os.path.join(HERE, 'probe_in_bundle.py'), '--local-dir', d, '--work-root', W, '--mods', ','.join(sorted(set(mods))),
             '--expect-scipy', '1' if expect_scipy else '0', '--json', ej]
        if i == 0:
            for dl in dlls:
                a += ['--load-dll', dl]
        rc, so, se, dt = run(a, scratch_env(B, {'PYTHONDONTWRITEBYTECODE': '1'}), cwd=d)
        r = json.load(open(ej)) if os.path.isfile(ej) else {}
        loc = (r.get('local') or {}).get('mods', {})
        failed = {m: v for m, v in loc.items() if isinstance(v, str)}
        # ao_seamfix needs scipy: on the numpy-only bundle its failure is the EXPECTED N4 gate, not an isolation bug
        exp_fail = {m for m, v in failed.items() if m == 'ao_seamfix' and not expect_scipy and 'scipy' in v}
        fails = [f for f in r.get('fails', []) if not (exp_fail and 'ao_seamfix' in f)]
        good = rc in (0, 1) and not fails and set(failed) <= exp_fail
        hok &= good
        hres[os.path.relpath(d, W)] = dict(ok=good, imported=sorted(m for m in loc if m not in failed), failed=failed,
                                           expected_failures=sorted(exp_fail), dll_loads=r.get('dll_loads'), fails=fails)
    check('H1 every import-safe pipeline module (tag writer lmw_*, engine, $W helpers, AO, lightprobes) imports under the '
          'bundle from its own folder; packfast.dll + seamfast.dll load', hok, hres)

    # ---- I. real production entry points start under the bundle (argparse --help only) -----------------------------
    helps = [os.path.join(W, 'engine_v373', 'v043-tagwriter', n) for n in ('lm_tagwriter.py', 'lmw_install.py', 'lmw_skeleton.py',
                                                                          'lmw_validate.py')]
    helps += [os.path.join(W, 'engine_v373', 'derive_owner_table.py'), os.path.join(W, '_research', 'ao', 'gpu_ao.py'),
              os.path.join(W, '_research', 'lightprobes', 'gpu_lightprobes.py')]
    ires, iok = {}, True
    for h in helps:
        rc, so, se, dt = run([PYB, h, '--help'], scratch_env(B, {'PYTHONDONTWRITEBYTECODE': '1'}), cwd=os.path.dirname(h))
        rs = run([SYSPY, h, '--help'], scratch_env(B, {'PYTHONDONTWRITEBYTECODE': '1'}, bogus=False), cwd=os.path.dirname(h))
        same = rc == rs[0] and so == rs[1]
        iok &= (rc == 0 and same)
        ires[os.path.relpath(h, W)] = dict(rc=rc, same_as_system=same, first=so.strip().splitlines()[0][:100] if so.strip() else se.strip()[-200:])
    check('I1 production scripts start (imports + argparse) under the bundle, --help byte-identical to the system 3.10.5',
          iok, ires)

    # ---- J. encodings + multiprocessing ----------------------------------------------------------------------------
    enc = {}
    for label, extra in (('PYTHONIOENCODING=utf-8', {'PYTHONIOENCODING': 'utf-8'}), ('unset', {})):
        rb = run([PYB, '-c', 'import sys;print(sys.stdout.encoding, sys.getfilesystemencoding(), sys.flags.utf8_mode)'],
                 scratch_env(B, extra), cwd=tests)
        rs = run([SYSPY, '-s', '-c', 'import sys;print(sys.stdout.encoding, sys.getfilesystemencoding(), sys.flags.utf8_mode)'],
                 scratch_env(B, extra, bogus=False), cwd=tests)
        enc[label] = dict(bundle=rb[1].strip(), system=rs[1].strip())
    check('J1 PYTHONIOENCODING still honoured (gpu_bake sets utf-8 for phase 2) and default pipe encoding equals the system',
          all(v['bundle'] == v['system'] for v in enc.values()) and enc['PYTHONIOENCODING=utf-8']['bundle'].startswith('utf-8'), enc)
    rc, so, se, dt = run([PYB, os.path.join(tests, 'mp_t.py')], scratch_env(B), cwd=tests)
    try:
        r = json.loads(so.strip().splitlines()[-1])
        ok = all(os.path.normcase(c[0]) == os.path.normcase(PYB) and c[1] == 1 and os.path.normcase(c[3]).startswith(os.path.normcase(B))
                 for c in r['children'])
    except Exception:
        r, ok = (so + se)[-400:], False
    check('J2 multiprocessing spawn children run the bundle interpreter (isolated, bundle numpy)', rc == 0 and ok, r)

    # ---- M. strip safety: every non-test numpy (/scipy) module that imports on the system imports in the bundle -------
    mres, mok = {}, True
    for pkg in (['numpy', 'scipy'] if expect_scipy else ['numpy']):
        jb = os.path.join(os.path.dirname(B), 'importall_%s_%s.json' % (os.path.basename(B), pkg))
        js = os.path.join(os.path.dirname(B), 'importall_system_%s.json' % pkg)
        rb = run([PYB, os.path.join(tests, 'import_all.py'), pkg, jb], scratch_env(B), cwd=tests, timeout=900)
        if not os.path.isfile(js):
            run([SYSPY, '-B', os.path.join(tests, 'import_all.py'), pkg, js], scratch_env(B, bogus=False), cwd=tests, timeout=900)
        b, s = json.load(open(jb)), json.load(open(js))
        sys_ok = set(s['ok'])
        regress, third = {}, {}
        for m, err in b['failed'].items():
            if m not in sys_ok:
                continue                                   # fails on the system too
            missing = err.split("No module named '")[1].split("'")[0] if "No module named '" in err else None
            if missing and missing.split('.')[0] not in ('numpy', 'scipy'):
                third[m] = err                             # needs a third-party package the pipeline never bundles
            else:
                regress[m] = err
        absent = sorted(set(s['ok']) - set(b['ok']) - set(b['failed']))
        mok &= not regress and not absent
        mres[pkg] = dict(bundle_ok=len(b['ok']), system_ok=len(s['ok']), regressions=regress, absent_modules=absent[:20],
                         third_party_only=third)
    check('M1 strip safety: every non-test %s module that imports on the system also imports in the bundle'
          % ('numpy/scipy' if expect_scipy else 'numpy'), mok, mres)

    # ---- N. relocation to a non-ANSI path with spaces (rt\ lives under %LOCALAPPDATA%, which can hold any user name) ---
    if '--reloc-test' in argv:
        reloc_root = os.path.join(os.path.dirname(B), '_reloc_ü测试 ж_%d' % os.getpid())
        R2 = os.path.join(reloc_root, 'py310-' + man['h8'])
        shutil.copytree(B, R2)
        e3 = scratch_env(R2)
        snippet = ('import sys, json, numpy; r = [sys.executable, sys.flags.isolated, numpy.__version__, numpy.__file__, '
                   'float(numpy.linalg.det(numpy.eye(3) * 2))]\n'
                   'try:\n from scipy.spatial import cKDTree; r.append(len(cKDTree(numpy.zeros((4, 3))).query_pairs(0.1)))\n'
                   'except ImportError: r.append(None)\n'
                   'print(json.dumps(r))')
        rc, so, se, dt = run([os.path.join(R2, 'py.exe'), '-c', snippet], dict(e3, PYTHONIOENCODING='utf-8'), cwd=tests)
        try:
            r = json.loads(so)
            ok = (rc == 0 and os.path.normcase(r[0]) == os.path.normcase(os.path.join(R2, 'py.exe')) and r[1] == 1
                  and r[2] == '2.2.6' and os.path.normcase(r[3]).startswith(os.path.normcase(R2)) and abs(r[4] - 8.0) < 1e-9
                  and (r[5] == 6 if expect_scipy else r[5] is None))
        except Exception:
            r, ok = (so + se)[-400:], False
        check('N1 relocated bundle works from a non-ANSI path with spaces (numpy BLAS%s)' % (' + scipy cKDTree' if expect_scipy else ''),
              ok, dict(path=R2, result=r))
        # remove only the copy made just now (a plain copytree of the bundle: assert no reparse point first)
        rp = [dp for dp, dns, fns in os.walk(reloc_root) for d in dns if os.path.islink(os.path.join(dp, d))
              or (os.stat(os.path.join(dp, d), follow_symlinks=False).st_file_attributes & 0x400)]
        if not rp and os.path.basename(reloc_root).startswith('_reloc_'):
            shutil.rmtree(reloc_root)

    # ---- K. immutability: nothing written into the bundle by any run above (bytecode writing was ON) ----------------
    after = snapshot(B)
    diff = sorted(set(after) ^ set(before)) + sorted(k for k in before if k in after and before[k] != after[k])
    check('K1 the bundle is immutable at runtime (no __pycache__ / no file changed, PYTHONDONTWRITEBYTECODE unset)', not diff,
          diff[:10] or None)

    # ---- L. timing + size ------------------------------------------------------------------------------------------
    def med(args, env):
        ts = []
        for _ in range(3):
            ts.append(run(args, env, cwd=tests)[3])
        return round(sorted(ts)[1], 3)
    tb = med([PYB, '-c', 'import numpy'], scratch_env(B))
    tsys = med([SYSPY, '-c', 'import numpy'], scratch_env(B, bogus=False))
    check('L1 py -c "import numpy" wall time, median of 3 (bundle vs system 3.10.5 + user-site numpy)', True,
          dict(bundle_s=tb, system_s=tsys), must=False)
    comp = {}
    raw = {}
    for r in man['files']:
        p = r['path']
        k = ('numpy' if p.startswith('site-packages/numpy/') else 'numpy.libs' if p.startswith('site-packages/numpy.libs/')
             else 'scipy' if p.startswith('site-packages/scipy/') else 'scipy.libs' if p.startswith('site-packages/scipy.libs/')
             else 'dist-info' if '.dist-info/' in p else 'python310.zip (stdlib)' if p == 'python310.zip'
             else 'core (exe, dll, pyd, vcruntime)')
        raw[k] = raw.get(k, 0) + r['size']
    sizes = dict(files=len(man['files']) + 1, raw_mb=round(sum(r['size'] for r in man['files']) / 1048576, 2),
                 raw_by_component_mb={k: round(v / 1048576, 2) for k, v in sorted(raw.items())})
    if zpath and os.path.isfile(zpath):
        with zipfile.ZipFile(zpath) as z:
            for i in z.infolist():
                p = i.filename
                k = ('numpy' if p.startswith('site-packages/numpy/') else 'numpy.libs' if p.startswith('site-packages/numpy.libs/')
                     else 'scipy' if p.startswith('site-packages/scipy/') else 'scipy.libs' if p.startswith('site-packages/scipy.libs/')
                     else 'dist-info' if '.dist-info/' in p else 'python310.zip (stdlib)' if p == 'python310.zip'
                     else 'core (exe, dll, pyd, vcruntime)')
                comp[k] = comp.get(k, 0) + i.compress_size
        sizes.update(zip=zpath, zip_bytes=os.path.getsize(zpath), zip_mb=round(os.path.getsize(zpath) / 1048576, 2),
                     zip_md5=md5f(zpath), compressed_by_component_mb={k: round(v / 1048576, 2) for k, v in sorted(comp.items())})
    REPORT['sizes'] = sizes
    check('L2 sizes', True, sizes, must=False)
    if out_json:
        with open(out_json, 'w') as f:
            json.dump(REPORT, f, indent=1, default=str)
    print('VERIFY %s: %s (%d checks)' % (os.path.basename(B), 'GREEN' if REPORT['ok'] else 'RED', len(REPORT['checks'])))
    return 0 if REPORT['ok'] else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
