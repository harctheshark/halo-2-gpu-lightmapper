"""Review-fix test stand-in for INST\\engine_v373\\v043-tagwriter\\lmw_validate.py (TEST payload only): the --check-set verb with
the real argv (--check-set DIR --bsp BSP --json F) and exit contract (0 = every check passed, 1 = a check failed). It does not
walk tags (the real gate is production's): it requires the set's three files (ltmp + truecolor + P8) in DIR, writes the JSON
report and logs the call. Knob: H2LM_WORK\\fake_control.txt "lmw_validate=fail"."""
import argparse, json, os, sys
sys.dont_write_bytecode = True

ap = argparse.ArgumentParser()
ap.add_argument('--check-set', required=True)
ap.add_argument('--bsp', required=True)
ap.add_argument('--json')
a = ap.parse_args()
W = os.environ.get('H2LM_WORK')
with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
    f.write(json.dumps(dict(tool='lmw_validate', argv=sys.argv, cwd=os.getcwd())) + '\n')
ctl = open(os.path.join(W, 'fake_control.txt'), encoding='utf-8').read() if os.path.exists(os.path.join(W, 'fake_control.txt')) else ''
names = sorted(os.listdir(a.check_set)) if os.path.isdir(a.check_set) else []
have = dict(ltmp=any(n.endswith('.scenario_structure_lightmap') for n in names),
            truecolor=any(n.endswith('_truecolor_bitmaps.bitmap') for n in names),
            p8=any(n.endswith('_bitmaps.bitmap') and not n.endswith('_truecolor_bitmaps.bitmap') for n in names))
failed = [k for k, v in have.items() if not v]
if 'lmw_validate=fail' in ctl:
    failed.append('set.d.checksum (fake failure)')
if a.json:
    json.dump(dict(checks=have, failed=failed, fake=True), open(a.json, 'w'), indent=1)
print('%d checks, %d failed%s' % (3, len(failed), (': ' + ', '.join(failed)) if failed else ''))
sys.exit(1 if failed else 0)
