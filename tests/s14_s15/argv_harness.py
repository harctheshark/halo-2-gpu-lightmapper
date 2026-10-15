"""argv_harness.py SPEC.json -- run a research driver (gpu_lightprobes.py / gpu_ao.py) IN-PROCESS with every child process faked,
and record what it WOULD have launched. Nothing is ever spawned: subprocess.call is replaced by a recorder, Popen / os.system
raise. Used by test_p5_p6.py to prove that the P5/P6 copies launch exactly what the pristine copies launch.

SPEC = {"target": path, "argv": [...], "out": result.json, "mode": "main" | "globals", "effects": true|false}
  main     runpy the script as __main__ with sys.argv = [target] + argv; SystemExit is caught (code/message recorded)
  globals  runpy it under another name (main() does not run) and record the module-level H2 / GLW / HERE
effects: the fake emulates the files a real child would leave (probes.bin, globals.json, the AO stage dir, ao_vis.bin) so the
driver walks on to its next step. RESULT = {"calls": [{"args", "cwd", "env_added", "env_changed", "env_removed"}], "exit": ..., "globals": ...}
"""
import sys, os, json, runpy, subprocess, io, contextlib, traceback

sys.dont_write_bytecode = True
spec = json.load(open(sys.argv[1]))
BASE_ENV = dict(os.environ)
CALLS = []


def _env_delta(env):
    if env is None:
        return None, None, None
    added = {k: v for k, v in env.items() if k not in BASE_ENV}
    changed = {k: v for k, v in env.items() if k in BASE_ENV and BASE_ENV[k] != v}
    removed = sorted(k for k in BASE_ENV if k not in env)
    return added, changed, removed


def _effects(args):
    a = [str(x) for x in args]
    j = ' '.join(a)
    if 'lp_probes.py' in j:                         # lp_probes SCEN BSP OUT.bin
        open(a[-1], 'wb').write(b'\x00\x00\x00\x00')
    elif 'lp_globals.py' in j:                      # lp_globals FARM OUT.json
        open(a[-1], 'w').write('{}')
    elif 'lm_tagwriter.py' in j:                    # gpu_ao stage: the writer makes the stage dir
        os.makedirs(a[a.index('--stage') + 1], exist_ok=True)
    elif 'gpu_bake.ps1' in j and '-AOOnly' in a:    # gpu_ao trace: the default trace dir gets ao_vis.bin
        farm = spec.get('farm_for_effects')
        if farm:
            os.makedirs(os.path.join(farm, 'ao', 'trace'), exist_ok=True)
            open(os.path.join(farm, 'ao', 'trace', 'ao_vis.bin'), 'wb').write(b'')


def fake_call(args, *pa, **kw):
    added, changed, removed = _env_delta(kw.get('env'))
    env = kw.get('env')
    CALLS.append(dict(args=[str(x) for x in args], cwd=kw.get('cwd'), env_added=added, env_changed=changed, env_removed=removed,
                      env_h2lm_gpulm=None if env is None else {k: v for k, v in env.items() if k.upper().startswith(('H2LM_', 'GPULM_'))},
                      creationflags=kw.get('creationflags')))
    if spec.get('effects'):
        _effects(args)
    return 0


def forbidden(*a, **k):
    raise RuntimeError('argv_harness: a real child process was requested: %r' % (a[:1],))


subprocess.call = fake_call
subprocess.Popen = forbidden
subprocess.run = forbidden
subprocess.check_call = forbidden
subprocess.check_output = forbidden
os.system = forbidden

res = dict(calls=CALLS, exit=None, stdout=None, globals=None, error=None)
buf = io.StringIO()
target = spec['target']
try:
    with contextlib.redirect_stdout(buf):
        if spec.get('mode', 'main') == 'globals':
            g = runpy.run_path(target, run_name='argv_harness_probe')
            res['globals'] = {k: g.get(k) for k in ('H2', 'GLW', 'HERE')}
        else:
            sys.argv = [target] + list(spec['argv'])
            runpy.run_path(target, run_name='__main__')
except SystemExit as e:
    res['exit'] = e.code if (e.code is None or isinstance(e.code, int)) else str(e.code)
except BaseException:
    res['error'] = traceback.format_exc()
res['stdout'] = buf.getvalue()
json.dump(res, open(spec['out'], 'w'), indent=1)
