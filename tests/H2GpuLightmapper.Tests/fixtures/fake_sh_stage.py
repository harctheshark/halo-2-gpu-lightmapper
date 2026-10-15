"""S2.5/S3 test stand-in for INST\\standalone\\sh_stage.py (N1): the merge / single verbs with the real argv, exit codes
(0 ok, 2 refused nothing changed, 3 failed after a change) and last-line contract ('SH_STAGE: ...'). It does not walk tags
(the real gate is S1.5's, tested there): merge replaces the stage ltmp with the SH ltmp and re-stamps tagw_report.json;
single builds a one-ltmp stage whose report records inputs.src.md5. Knob: H2LM_WORK\\fake_control.txt "sh_stage=refuse"."""
import argparse, hashlib, json, os, shutil, sys
sys.dont_write_bytecode = True


def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def out(m, code):
    print('SH_STAGE: ' + m)
    sys.exit(code)


ap = argparse.ArgumentParser()
sp = ap.add_subparsers(dest='verb', required=True)
for v in ('merge', 'single'):
    p = sp.add_parser(v)
    p.add_argument('--sh', required=True); p.add_argument('--probes', default=None); p.add_argument('--points', type=int, default=None)
    p.add_argument('--h2-root', default=None); p.add_argument('--stage', required=True); p.add_argument('--tagio', default=None)
    if v == 'single':
        p.add_argument('--src', required=True); p.add_argument('--name', default=None)
a = ap.parse_args()
W = os.environ.get('H2LM_WORK')
with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
    f.write(json.dumps(dict(tool='sh_stage', argv=sys.argv, cwd=os.getcwd())) + '\n')
ctl = open(os.path.join(W, 'fake_control.txt'), encoding='utf-8').read() if os.path.exists(os.path.join(W, 'fake_control.txt')) else ''
if 'sh_stage=refuse' in ctl:
    out('REFUSED: fake refusal (environment 3 sample_point differs)', 2)
stage = os.path.abspath(a.stage)
if a.h2_root and os.path.normcase(stage).startswith(os.path.normcase(os.path.join(os.path.abspath(a.h2_root), 'tags')) + os.sep):
    out('REFUSED: the stage is under the tags tree', 2)
if any(part.lower().startswith('farm_w') for part in stage.split(os.sep)):
    out('REFUSED: the stage is inside a farm dir', 2)
if a.verb == 'merge':
    rp = os.path.join(stage, 'tagw_report.json')
    rep = json.load(open(rp, encoding='utf-8-sig'))
    if rep.get('status') != 'written':
        out('REFUSED: report status %r' % rep.get('status'), 2)
    ltmp = [n for n in rep['outputs'] if n.endswith('.scenario_structure_lightmap')][0]
    if os.path.getsize(a.sh) != os.path.getsize(os.path.join(stage, ltmp)):
        out('REFUSED: size differs', 2)
    os.makedirs(os.path.join(stage, '_sh_merge_backup'), exist_ok=True)
    shutil.copyfile(os.path.join(stage, ltmp), os.path.join(stage, '_sh_merge_backup', ltmp))
    shutil.copyfile(rp, os.path.join(stage, '_sh_merge_backup', 'tagw_report.json'))
    shutil.copyfile(a.sh, os.path.join(stage, ltmp))
    rep['outputs'][ltmp]['md5'] = md5(os.path.join(stage, ltmp)); rep['outputs'][ltmp]['size'] = os.path.getsize(os.path.join(stage, ltmp))
    rep['sh_merge'] = dict(tool='fake sh_stage')
    json.dump(rep, open(rp, 'w'), indent=1)
    json.dump(dict(verb='merge'), open(os.path.join(stage, 'sh_merge.json'), 'w'))
    out('OK merge %s %s' % (ltmp, rep['outputs'][ltmp]['md5']), 0)
else:
    if os.path.exists(stage) and os.listdir(stage):
        out('REFUSED: %s is not empty' % stage, 2)
    os.makedirs(stage, exist_ok=True)
    name = a.name or os.path.basename(a.src)
    p = os.path.join(stage, name)
    shutil.copyfile(a.sh, p)
    rep = dict(writer='sh_stage v1 (fake) single', status='written', inputs=dict(src=dict(path=a.src, md5=md5(a.src), size=os.path.getsize(a.src))),
               outputs={name: dict(path=p, md5=md5(p), size=os.path.getsize(p), replaced_backup=None)})
    json.dump(rep, open(os.path.join(stage, 'tagw_report.json'), 'w'), indent=1)
    out('OK single %s %s' % (name, md5(p)), 0)
