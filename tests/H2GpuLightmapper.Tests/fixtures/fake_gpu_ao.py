"""S2.5/S3 test stand-in for INST\\_research\\ao\\gpu_ao.py (P6). Same verbs / options the shell passes (trace, apply, stage);
writes the farm files the next verb and the shell read; never a GPU, never a tags tree. Every call is appended to
H2LM_WORK\\fake_calls.log (one JSON line)."""
import argparse, hashlib, json, os, shutil, sys
sys.dont_write_bytecode = True

W = os.environ.get('H2LM_WORK')


def rec(d):
    with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
        f.write(json.dumps(d) + '\n')


def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def die(m):
    print('gpu_ao: ' + m, file=sys.stderr)
    sys.exit(2)


ap = argparse.ArgumentParser()
sub = ap.add_subparsers(dest='cmd', required=True)
for v in ('trace', 'apply', 'stage'):
    p = sub.add_parser(v)
    p.add_argument('--worker', type=int, required=True); p.add_argument('--farm', required=True)
    p.add_argument('--scenario', required=True); p.add_argument('--bsp', required=True); p.add_argument('--engine', required=True)
    if v == 'trace':
        p.add_argument('--quality', choices=('low', 'super'), default=None)
        p.add_argument('--plucker-fix', action='store_true'); p.add_argument('--scenery-fix', action='store_true')
    else:
        p.add_argument('--tag', required=True); p.add_argument('--overwrite', action='store_true')
    if v == 'apply':
        p.add_argument('--strength', type=float, required=True); p.add_argument('--exponent', type=float, required=True)
a = ap.parse_args()
if not W or not os.path.isdir(W):
    die('H2LM_WORK is not set')
rec(dict(tool='gpu_ao', argv=sys.argv, cwd=os.getcwd(), h2lm_root=os.environ.get('H2LM_ROOT'), pwsh_on_path=shutil.which('pwsh'),
         gpulm=sorted(k for k in os.environ if k.startswith('GPULM_')), pythonpath=os.environ.get('PYTHONPATH')))
farm = os.path.abspath(a.farm)
if os.path.basename(farm).lower() != 'farm_w%02d' % a.worker:
    die('--farm is not farm_w%02d' % a.worker)
if not os.path.exists(os.path.join(a.engine, 'gpu_bake.ps1')):
    die('no gpu_bake.ps1 in %s' % a.engine)
if a.cmd == 'trace':
    if os.path.exists(os.path.join(farm, '.gpu_bake.lock')):
        die('a farm lock exists')
    if not os.path.exists(os.path.join(farm, 'capture_id.json')):
        die('no capture in %s' % farm)
    d = os.path.join(farm, 'ao', 'trace'); os.makedirs(d, exist_ok=True)
    open(os.path.join(d, 'ao_vis.bin'), 'wb').write(b'AOVIS ' + (a.quality or 'super').encode())
    print('   AO TRACE fake: 64 rays, quality %s' % (a.quality or '(default)'))
    print('trace done in 0.1 s -> %s' % d)
elif a.cmd == 'apply':
    if not os.path.exists(os.path.join(farm, 'ao', 'trace', 'ao_vis.bin')):
        die('no trace -- run `trace` first')
    run = os.path.join(farm, 'ao', a.tag, 'run')
    if os.path.exists(run) and not a.overwrite:
        die('%s exists' % run)
    os.makedirs(run, exist_ok=True)
    open(os.path.join(run, 'vertdir.bin'), 'wb').write(('AO s=%r e=%r' % (a.strength, a.exponent)).encode())
    print('APPLY %s: strength %r exponent %r' % (a.tag, a.strength, a.exponent))
else:
    out = os.path.join(farm, 'ao', a.tag)
    if not os.path.exists(os.path.join(out, 'run', 'vertdir.bin')):
        die('no applied run')
    if not os.path.isdir(os.path.join(farm, 'skeleton')):
        die('no skeleton')
    src = os.path.join(farm, 'tagw_stage')
    rep = json.load(open(os.path.join(src, 'tagw_report.json'), encoding='utf-8-sig'))
    stage = os.path.join(out, 'tagw_stage'); os.makedirs(stage, exist_ok=True)
    outs = {}
    for n in rep['outputs']:
        b = open(os.path.join(src, n), 'rb').read() + ('|AO %s' % a.tag).encode()
        p = os.path.join(stage, n); open(p, 'wb').write(b)
        outs[n] = dict(path=p, md5=md5(p), size=len(b))
    json.dump(dict(writer='fake ao', status='written', outputs=outs), open(os.path.join(stage, 'tagw_report.json'), 'w'), indent=1)
    open(os.path.join(stage, 'AO_NOT_CPU_FAITHFUL.txt'), 'w').write('fake')
    print('STAGE %s -> %s (NOT installed)' % (a.tag, stage))
    # V6 knob (H2LM_WORK\fake_control.txt "ao_stage_wait"): announce the stage, then wait (<= 60 s) for the test's go flag, so the
    # test can take a file lock (a stand-in for Sapien) after the shell's backup step and before the shell's install
    ctl = os.path.join(W, 'fake_control.txt')
    if os.path.exists(ctl) and 'ao_stage_wait' in open(ctl, encoding='utf-8').read():
        import time
        open(os.path.join(W, 'fake_ao_stage_waiting.flag'), 'w').write('waiting')
        t0 = time.time()
        while not os.path.exists(os.path.join(W, 'fake_ao_stage_go.flag')) and time.time() - t0 < 60:
            time.sleep(0.1)
        print('STAGE wait over (%s)' % ('go' if os.path.exists(os.path.join(W, 'fake_ao_stage_go.flag')) else 'timed out'))
sys.exit(0)
