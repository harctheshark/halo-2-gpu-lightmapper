"""SF1 test stand-in for INST\\standalone\\scan_scenery_bounds.py: the real CLI (root, --scenario, --apply, --json, --db ...) and
exit codes (0 ok / applied, 1 missing not applied, 2 usage, 3 conflicts), and the JSON fields the shell reads. It scans nothing.
Knob (H2LM_WORK\\fake_control.txt, one line): "scan=add:<model>;<model>" appends a line per model to ROOT\\SceneryFixDependencies.db
(creating it) and reports them in added_models; "scan=conflict" reports one conflicting model (exit 3, a model is also added);
"scan=refuse" exits 2; "scan=crash" raises; "scan=nojson" exits 0 without a report. No knob: nothing is missing (exit 0)."""
import argparse, json, os, sys
sys.dont_write_bytecode = True

ap = argparse.ArgumentParser()
ap.add_argument('root'); ap.add_argument('subdirs', nargs='*')
ap.add_argument('--db'); ap.add_argument('--json'); ap.add_argument('--apply', action='store_true')
ap.add_argument('--allow-conflicts', action='store_true'); ap.add_argument('--scenery-only', action='store_true')
ap.add_argument('--scenario')
a = ap.parse_args()
W = os.environ.get('H2LM_WORK')
with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8') as f:
    f.write(json.dumps(dict(tool='scan_scenery', argv=sys.argv, cwd=os.getcwd())) + '\n')
cp = os.path.join(W, 'fake_control.txt')
ctl = open(cp, encoding='utf-8').read() if os.path.exists(cp) else ''
line = [l for l in ctl.splitlines() if l.startswith('scan=')]
knob = line[0][5:] if line else ''
if knob == 'refuse':
    print('scenario not found: fake')
    sys.exit(2)
if knob == 'crash':
    raise RuntimeError('fake scanner crash')
db = a.db or os.path.join(a.root, 'SceneryFixDependencies.db')
added, conflict, updated, also, seeding, rc = [], [], [], [], False, 0
if knob.startswith('add:'):
    added = [m for m in knob[4:].split(';') if m]
if knob.startswith('update:'):
    updated = [m for m in knob[7:].split(';') if m]
if knob.startswith('seed:'):
    seeding = True
    added = [m for m in knob[5:].split(';') if m]
    also = ['other' + str(i) for i in range(3)]
if knob == 'conflict':
    added = ['conflict_ok' + chr(92) + 'fine.render_model']
    conflict = [dict(model='trees' + chr(92) + 'redalder' + chr(92) + 'redalder.render_model', sections=2, missing=0, conflict=2, nobound=0)]
    rc = 3
if (added or updated) and a.apply:
    with open(db, 'a', encoding='ascii', newline='') as f:
        for m in added + updated:
            f.write('fake-line ' + m + '\r\n')
rep = dict(root=a.root, db=db, models=len(added) + 5, ok=[], missing=[dict(model=m, sections=1, missing=1, conflict=0, nobound=0) for m in added],
           conflict=conflict, nobound=[], nosec=[], lines_to_add=len(added) + len(updated), added_models=added if a.apply else [], applied=bool((added or updated) and a.apply),
           updated_models=updated if a.apply else [], also_added_models=also, seeding=seeding, key_collisions=0,
           legacy_db=os.path.join(a.root, 'h2v_scenery_bounds.db'), scan_seconds=0.0)
if a.scenario:
    rep.update(scenario=a.scenario, scenery_tags=[], render_models=[], unresolved_scenery=[])
print('fake scan: %d model(s), %d added' % (rep['models'], len(added)))
if a.json and knob != 'nojson':
    with open(a.json, 'w') as f:
        json.dump(rep, f)
sys.exit(rc)
