"""S2.5/S3 test stand-in for INST\\_research\\lightprobes\\gpu_lightprobes.py (P5). Same options and the same child shape: it runs
<stage>\\gpu_bake.ps1 (the FAKE gpu_bake in the test payload) through --pwsh with GPULM_PROBES + GPULM_SKY_HI=1 added to the
environment it was given, its output in OUT\\gpu_bake.log (the shell tails it), then requires the probe-ready sky files and
writes OUT\\probes.bin + OUT\\<ltmp> (= --ltmp-src with its last 8 bytes replaced by 'SHSHSHSH': same size, like lp_write_sh).
Knob: H2LM_WORK\\fake_control.txt "probes=ok|fail|points=N"."""
import argparse, json, os, struct, subprocess, sys, time
sys.dont_write_bytecode = True

ap = argparse.ArgumentParser()
ap.add_argument('--scenario', required=True); ap.add_argument('--bsp', required=True); ap.add_argument('--quality', default='low')
ap.add_argument('--worker', type=int, required=True); ap.add_argument('--stage', required=True)
ap.add_argument('--skip-capture', action='store_true'); ap.add_argument('--out', required=True)
ap.add_argument('--sever', default=None); ap.add_argument('--ltmp-src', required=True)
ap.add_argument('--h2-root', required=True); ap.add_argument('--farm', required=True); ap.add_argument('--pwsh', required=True)
ap.add_argument('--plucker-fix', action='store_true'); ap.add_argument('--scenery-fix', action='store_true')
ap.add_argument('--photons', type=int, default=None); ap.add_argument('--mc-samples', type=int, default=None)
a = ap.parse_args()
W = os.environ.get('H2LM_WORK')
ctl = {}
p = os.path.join(W, 'fake_control.txt')
if os.path.exists(p):
    for l in open(p, encoding='utf-8'):
        if '=' in l:
            k, v = l.strip().split('=', 1); ctl[k] = v
with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
    f.write(json.dumps(dict(tool='gpu_lightprobes', argv=sys.argv, cwd=os.getcwd(), h2lm_root=os.environ.get('H2LM_ROOT'),
                            gpulm=sorted(k for k in os.environ if k.startswith('GPULM_')), pythonpath=os.environ.get('PYTHONPATH'))) + '\n')
if os.path.basename(os.path.abspath(a.farm)).lower() != 'farm_w%02d' % a.worker:
    sys.exit('REFUSED: --farm is not farm_w%02d' % a.worker)
if os.environ.get('H2LM_WORK') and os.path.normcase(os.path.dirname(os.path.abspath(a.farm))) != os.path.normcase(os.path.abspath(W)):
    sys.exit('REFUSED: --farm is not under H2LM_WORK')
if (a.photons is not None or a.mc_samples is not None) and a.quality != 'custom':
    sys.exit('REFUSED: --photons / --mc-samples need --quality custom')
if os.path.exists(a.out):
    sys.exit('REFUSED: %s exists' % a.out)
os.makedirs(a.out)
n = int(ctl.get('points', '11'))
with open(os.path.join(a.out, 'probes.bin'), 'wb') as f:
    f.write(struct.pack('<I', n))
    for i in range(n):
        f.write(struct.pack('<3f', float(i), 0.0, 1.0))
env = dict(os.environ); env['GPULM_PROBES'] = os.path.join(a.out, 'probes.bin'); env['GPULM_SKY_HI'] = '1'; env['H2LM_ROOT'] = a.h2_root
bake = [a.pwsh, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(a.stage, 'gpu_bake.ps1'), '-Scenario', a.scenario,
        '-Bsp', a.bsp, '-Worker', str(a.worker), '-Quality', a.quality] + (['-SkipCapture'] if a.skip_capture else [])
bake += (['-PluckerFix'] if a.plucker_fix else []) + (['-SceneryFix'] if a.scenery_fix else []) + (['-Photons', str(a.photons)] if a.photons is not None else []) + (
         ['-MCSamples', str(a.mc_samples)] if a.mc_samples is not None else [])
t0 = time.time()
with open(os.path.join(a.out, 'gpu_bake.log'), 'wb') as f:
    rc = subprocess.call(bake, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env)
if ctl.get('probes') == 'fail':
    sys.exit('step 2 failed: fake failure (rc %d)' % rc)
for fn in ('sky_tex_hi.bin', 'lp_globals.bin'):
    if not os.path.exists(os.path.join(a.farm, fn)):
        sys.exit('step 2: the capture has no %s (rc %d)' % (fn, rc))
if rc:
    sys.exit('step 2 failed: gpu_bake rc %d (see %s)' % (rc, os.path.join(a.out, 'gpu_bake.log')))
name = os.path.basename(a.ltmp_src)
b = open(a.ltmp_src, 'rb').read()
open(os.path.join(a.out, name), 'wb').write(b[:-8] + b'SHSHSHSH')
print('DONE in %.1f s -> %s' % (time.time() - t0, a.out))
print('  candidate lightmap tag: %s (NOT installed)' % os.path.join(a.out, name))
