"""S2.5/S3 test stand-in for INST\\_research\\ao\\ao_existing.py (0.6.8 xc8 AO only). The verbs the shell passes (precheck, stage) with the same
refusals the real script makes in the cases the shell tests need (no lightmap, the install record says AO is in it already); the stage is the installed set + a marker;
never a GPU, never a tags write. Every call is appended to H2LM_WORK\\fake_calls.log (one JSON line)."""
import argparse, hashlib, json, os, sys
sys.dont_write_bytecode = True

W = os.environ.get('H2LM_WORK')


def rec(d):
    with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
        f.write(json.dumps(d) + '\n')


def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def refuse(m):
    print('AO only: REFUSED -- %s Nothing was written.' % m)
    sys.exit(2)


ap = argparse.ArgumentParser()
sub = ap.add_subparsers(dest='cmd', required=True)
for v in ('precheck', 'stage'):
    p = sub.add_parser(v)
    p.add_argument('--scenario', required=True); p.add_argument('--bsp', required=True); p.add_argument('--engine', required=True)
    p.add_argument('--ao-dir'); p.add_argument('--h2-root'); p.add_argument('--tool'); p.add_argument('--records'); p.add_argument('--force', action='store_true')
    if v == 'stage':
        p.add_argument('--farm', required=True); p.add_argument('--strength', type=float, required=True); p.add_argument('--exponent', type=float, required=True)
        p.add_argument('--tag', required=True); p.add_argument('--overwrite', action='store_true')
a = ap.parse_args()
rec(dict(tool='ao_existing', argv=sys.argv, cwd=os.getcwd(), h2lm_root=os.environ.get('H2LM_ROOT'), gpulm=sorted(k for k in os.environ if k.startswith('GPULM_'))))
rel = a.scenario.replace('/', '\\')
leaf = rel.split('\\')[-1]
tagdir = os.path.join(a.h2_root or os.environ.get('H2LM_ROOT'), 'tags', os.path.dirname(rel))
pfx = '%s_%s_lightmap' % (leaf, a.bsp)
names = [pfx + '.scenario_structure_lightmap', pfx + '_truecolor_bitmaps.bitmap', pfx + '_bitmaps.bitmap']
miss = [n for n in names if not os.path.isfile(os.path.join(tagdir, n))]
if miss:
    refuse('this scenario / BSP has no complete lightmap in the tags yet (missing: %s).' % ', '.join(miss))
sums = {n: md5(os.path.join(tagdir, n)) for n in names}
if a.records and os.path.isfile(a.records) and not a.force:
    for r in json.load(open(a.records, encoding='utf-8')).get('sets', []):
        if r.get('files') == sums:
            refuse('ambient occlusion has already been applied to the lightmap that is installed now (fake).')
if a.cmd == 'precheck':
    print('[ao_existing] precheck ok (fake)')
    sys.exit(0)
farm = os.path.abspath(a.farm)
if not os.path.exists(os.path.join(farm, 'ao', 'trace', 'ao_vis.bin')):
    refuse('there is no AO trace for this capture (fake).')
stage = os.path.join(farm, 'aoonly', a.tag, 'stage')
os.makedirs(stage, exist_ok=True)
outs = {}
for n in names:
    b = open(os.path.join(tagdir, n), 'rb').read() + b'|AO ONLY'
    p = os.path.join(stage, n)
    open(p, 'wb').write(b)
    outs[n] = dict(path=p, md5=md5(p), size=len(b))
json.dump(dict(writer='fake ao_existing', status='written', outputs=outs), open(os.path.join(stage, 'tagw_report.json'), 'w'), indent=1)
open(os.path.join(stage, 'AO_NOT_CPU_FAITHFUL.txt'), 'w').write('fake')
print('[ao_existing] STAGE %s -> %s (fake; nothing installed)' % (a.tag, stage))
sys.exit(0)
