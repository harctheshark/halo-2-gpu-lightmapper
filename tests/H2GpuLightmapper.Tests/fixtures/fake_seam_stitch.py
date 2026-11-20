"""S2.5/S3 test stand-in for INST\\standalone\\seam_stitch.py (1.5 Seam stitch only, EXPERIMENTAL). The verbs the shell passes (precheck, stage) with the
refusal the real script makes when the set is missing; the stage is the installed set with the two bitmaps marked (the lightmap tag unchanged, as the real
script keeps it); never a tags write. Every call is appended to H2LM_WORK\\fake_calls.log (one JSON line)."""
import argparse, hashlib, json, os, sys
sys.dont_write_bytecode = True

W = os.environ.get('H2LM_WORK')


def rec(d):
    if W:
        with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
            f.write(json.dumps(d) + '\n')


def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


ap = argparse.ArgumentParser()
sub = ap.add_subparsers(dest='cmd', required=True)
for v in ('precheck', 'stage'):
    p = sub.add_parser(v)
    p.add_argument('--tag-dir', required=True); p.add_argument('--prefix', required=True); p.add_argument('--bsp-file', required=True)
    p.add_argument('--engine', required=True); p.add_argument('--tool')
    if v == 'stage':
        p.add_argument('--out', required=True); p.add_argument('--overwrite', action='store_true')
a = ap.parse_args()
rec(dict(tool='seam_stitch', argv=sys.argv, cwd=os.getcwd()))
names = [a.prefix + '.scenario_structure_lightmap', a.prefix + '_truecolor_bitmaps.bitmap', a.prefix + '_bitmaps.bitmap']
miss = [n for n in names if not os.path.isfile(os.path.join(a.tag_dir, n))]
if miss:
    print('Seam stitch: REFUSED -- this scenario / BSP has no complete lightmap in the tags yet (missing: %s). Nothing was written.' % ', '.join(miss))
    sys.exit(2)
if a.cmd == 'precheck':
    print('[seam_stitch] precheck ok (fake)')
    sys.exit(0)
stage = os.path.join(os.path.abspath(a.out), 'stage')
os.makedirs(stage, exist_ok=True)
outs = {}
for n in names:
    b = open(os.path.join(a.tag_dir, n), 'rb').read() + (b'' if n.endswith('.scenario_structure_lightmap') else b'|STITCHED')
    p = os.path.join(stage, n)
    open(p, 'wb').write(b)
    outs[n] = dict(path=p, md5=md5(p), size=len(b))
json.dump(dict(writer='fake seam_stitch', status='written', outputs=outs), open(os.path.join(stage, 'tagw_report.json'), 'w'), indent=1)
print('[seam_stitch] STAGE -> %s (fake; nothing installed)' % stage)
sys.exit(0)
