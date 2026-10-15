# S0.3: one-screen summary of verify_pybundle.py reports.   usage: python -I summarize.py out\verify_*.json
import sys, json
for p in sys.argv[1:]:
    r = json.load(open(p))
    print('==', r['bundle'], 'bundle_id', r['bundle_id'], 'GREEN' if r['ok'] else 'RED')
    for c in r['checks']:
        n = c['name'][:2]
        if not c['ok']:
            print('  %s %s' % ('FAIL' if c['must'] else 'INFO', c['name']))
        if n == 'M1':
            for k, v in c['detail'].items():
                print('  M1 %s: bundle %d / system %d import ok; regressions %s; third-party-only %s'
                      % (k, v['bundle_ok'], v['system_ok'], v['regressions'] or 'none', sorted(v['third_party_only'])))
        if n == 'H1':
            for k, v in c['detail'].items():
                print('  H1 %-34s ok=%s imported=%d failed=%s expected=%s' % (k, v['ok'], len(v['imported']), sorted(v['failed']),
                                                                          v['expected_failures']))
        if n in ('L1', 'L2', 'F1', 'E1'):
            print('  %s %s' % (n, json.dumps(c['detail'])[:700]))
    pr = r.get('probe', {})
    print('  probe scipy:', pr.get('scipy'))
    print('  probe vc runtime loaded:', pr.get('vc_runtime_loaded'))
    print('  probe sys:', {k: pr.get('sys', {}).get(k) for k in ('isolated', 'no_user_site', 'site_ENABLE_USER_SITE',
                                                                 'exit_builtin', 'stdout_encoding')})
