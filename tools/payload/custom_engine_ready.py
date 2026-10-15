"""custom_engine_ready.py -- may the payload ship engine_custom/ (the N3 guarded fullgpu) right now? Read-only. Stdlib only.

  py -I -B tools\\payload\\custom_engine_ready.py [--json]
Exit 0 = yes, 1 = no (the reason is printed). The rule is stage_engine.py's --with-custom-engine gate, checked up front so the
build scripts can pass --with-custom-engine exactly when it would be accepted:
  - the T3a record (payload_spec inst.custom_engine.latest = build\\fullgpu_guard\\LATEST.json) says ok and its
    gpu_identity_proof starts with PASS;
  - it was proven against the production fullgpu.exe on disk NOW (an engine promotion turns the custom engine off again until
    native\\fullgpu_guard is rebuilt and T3a re-run);
  - the guard exe it names exists with the tested md5."""
import hashlib
import json
import os
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import h2lm_payload_lib as L   # noqa: E402


def md5(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 22), b''):
            h.update(b)
    return h.hexdigest()


def verdict():
    spec = L.load_spec()
    roots = spec['roots']
    latest = L.resolve(spec['inst']['custom_engine']['latest'], roots)
    try:
        lt = json.load(open(latest, encoding='utf-8'))
    except (OSError, ValueError) as e:
        return False, 'no readable T3a record %s (%s)' % (latest, e), {}
    proof = str(lt.get('gpu_identity_proof', ''))
    info = {'latest': latest, 'proof': proof, 'guard_md5': lt.get('guard_md5'), 'production_fullgpu_md5': lt.get('production_fullgpu_md5')}
    if not lt.get('ok') or not proof.upper().startswith('PASS'):
        return False, 'gate T3a has not passed (%s)' % proof[:120], info
    prod = os.path.join(roots['GLW'], 'engine_v373', 'v041-fullgpu', 'fullgpu.exe')
    pm = md5(prod)
    info['production_now'] = pm
    if pm != lt.get('production_fullgpu_md5'):
        return False, 'the guard was proven against fullgpu %s but production is now %s (rebuild N3 + re-run T3a)' % (
            lt.get('production_fullgpu_md5'), pm), info
    gp = os.path.join(os.path.dirname(latest), *lt['guard_fullgpu'].replace('\\', '/').split('/'))
    if not os.path.isfile(gp) or md5(gp) != lt.get('guard_md5'):
        return False, 'the T3a-tested guard exe %s (md5 %s) is missing or changed' % (gp, lt.get('guard_md5')), info
    return True, 'T3a PASS for the current production fullgpu %s; guard %s' % (pm[:8], lt['guard_md5'][:8]), info


def main():
    ok, why, info = verdict()
    if '--json' in sys.argv:
        print(json.dumps(dict(info, ready=ok, reason=why), indent=1))
    else:
        print('custom engine %s: %s' % ('READY' if ok else 'NOT ready', why))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
