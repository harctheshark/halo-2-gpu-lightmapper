"""test_p5_p6.py -- S1.4 static tests of P5 (gpu_lightprobes.py) and P6 (gpu_ao.py). No GPU, no child process is ever started
(argv_harness.py fakes subprocess.call), nothing under GPU Lightmap Work or any tags tree is written.

  py -B test_p5_p6.py [--glw "G:\\...\\GPU Lightmap Work"] [--work DIR] [--keep]

 1. copy the pristine production drivers BY MD5 (must equal each patch's base-md5) into WORK\\orig\\_research\\...
 2. strict_patch: apply P5/P6 into WORK\\patched\\_research\\... (result-md5 pinned); negative cases: a 1-byte-changed base and
    a shifted base must both be REFUSED (no fuzz, no offset)
 3. --help: gpu_ao top level and every non-trace sub-command byte-identical; gpu_lightprobes and `gpu_ao trace` keep every
    pre-patch option line (as a prefix of the options section) and add only the new options
 4. default argv: identical pre-patch command lines -> identical recorded child argv / env delta / exit, orig vs patched
 5. the new passthroughs launch what the design says (P5: --h2-root/--farm/--pwsh+Bypass/fixes/custom; P6: -Quality, H2LM_ROOT)
 6. P5 refusals happen before any child and before OUT is created
Exit 0 = all green; the last line is  RESULT: PASS n/n  or  RESULT: FAIL ..."""
import argparse, hashlib, json, os, re, shutil, subprocess, sys, tempfile, time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(PROJ, 'tools'))
import strict_patch as SP  # noqa: E402

GLW_DEFAULT = os.environ.get('H2LM_TEST_GLW', '')   # <H2EK_ROOT>\GPU Lightmap Work (production, read only)
P5 = os.path.join(PROJ, 'patches', 'P5_gpu_lightprobes.patch')
P6 = os.path.join(PROJ, 'patches', 'P6_gpu_ao.patch')
P6B = os.path.join(PROJ, 'patches', 'P6b_gpu_ao_exactvl.patch')       # W2 (0.6.3): gpu_ao stage --exact-vertex-lighting (the series chain is P6 -> P6b)
RESULTS = []


def check(name, ok, detail=''):
    RESULTS.append((name, bool(ok), detail))
    print('%s  %s%s' % ('PASS' if ok else 'FAIL', name, ('  -- ' + detail) if detail and not ok else ''), flush=True)
    return ok


def md5f(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def child_env(extra=None, drop=()):
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(('GPULM_', 'H2LM_', 'OSOYOOS_')) and k.upper() not in ('PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP')}
    env['COLUMNS'] = '200'
    env['PYTHONDONTWRITEBYTECODE'] = '1'
    for k in drop:
        env.pop(k, None)
    env.update(extra or {})
    return env


def harness(work, target, argv, mode='main', effects=True, env_extra=None, farm_for_effects=None, tag='h'):
    n = len(os.listdir(os.path.join(work, 'specs')))
    spec = os.path.join(work, 'specs', '%03d_%s.json' % (n, tag))
    out = spec[:-5] + '.result.json'
    json.dump(dict(target=target, argv=argv, out=out, mode=mode, effects=effects, farm_for_effects=farm_for_effects), open(spec, 'w'), indent=1)
    rc = subprocess.call([sys.executable, '-B', os.path.join(HERE, 'argv_harness.py'), spec], cwd=work, env=child_env(env_extra),
                         stdin=subprocess.DEVNULL, creationflags=0x08000000 | 0x00004000)
    if rc != 0 or not os.path.exists(out):
        raise RuntimeError('harness rc %d for %s' % (rc, spec))
    r = json.load(open(out))
    if r.get('error'):
        raise RuntimeError('harness error for %s:\n%s' % (spec, r['error']))
    return r


def norm(obj, subs):
    """replace run-specific prefixes (tree roots, OUT dirs) with tokens; case-insensitive on Windows paths."""
    s = json.dumps(obj, sort_keys=True)
    for a, b in subs:
        for v in {a, a.replace('\\', '\\\\')}:
            s = re.sub(re.escape(v), b.replace('\\', '\\\\'), s, flags=re.I)
    return json.loads(s)


def options_section(text):
    """the lines of argparse's 'options:' section (the help body after the usage/description)"""
    L = text.splitlines()
    for i, l in enumerate(L):
        if l.strip() in ('options:', 'optional arguments:'):
            return L[i + 1:]
    return None


def pick_worker(glw):
    for w in list(range(298, 289, -1)) + [99, 98, 97]:
        if not os.path.exists(os.path.join(glw, 'farm_w%02d' % w)):
            return w
    raise RuntimeError('no free test worker number')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--glw', default=GLW_DEFAULT)
    ap.add_argument('--work', default=None, help='scratch dir (default: a new temp dir)')
    ap.add_argument('--keep', action='store_true', help='keep the scratch dir')
    a = ap.parse_args()
    work = os.path.abspath(a.work) if a.work else tempfile.mkdtemp(prefix='s14_p5p6_')
    if os.path.normcase(work).startswith(os.path.normcase(os.path.abspath(os.path.join(a.glw, '..')))):
        sys.exit('REFUSED: --work must not be inside the H2EK root')
    os.makedirs(os.path.join(work, 'specs'), exist_ok=True)
    print('work dir: %s' % work)

    # ---------------------------------------------------------------- 1+2: pristine copies by md5, strict apply
    trees = {}
    for tree in ('orig', 'patched'):
        trees[tree] = os.path.join(work, tree)
    files = {}
    for key, pf in (('lp', P5), ('ao', P6)):
        P = SP.read_patch(pf)
        src = os.path.join(a.glw, *P['source'].split('/'))
        raw = open(src, 'rb').read()
        check('%s: production %s md5 == patch base-md5 %s' % (key, P['source'], P['base-md5'][:8]), SP.md5b(raw) == P['base-md5'],
              'production is %s' % SP.md5b(raw))
        o = os.path.join(trees['orig'], *P['source'].split('/'))
        p = os.path.join(trees['patched'], *P['source'].split('/'))
        for d in (o, p):
            os.makedirs(os.path.dirname(d), exist_ok=True)
        open(o, 'wb').write(raw)
        res = SP.apply_bytes(P, raw)
        open(p, 'wb').write(res)
        check('%s: strict apply -> result-md5 %s' % (key, P['result-md5'][:8]), md5f(p) == P['result-md5'] and md5f(o) == P['base-md5'])
        # negatives: a changed base byte; a shifted base (one extra line at the top, md5 pin bypassed to prove no-offset)
        bad = bytearray(raw); bad[len(bad) // 2] ^= 0x01
        try:
            SP.apply_bytes(P, bytes(bad)); ok = False
        except SP.PatchError:
            ok = True
        check('%s: a 1-byte-changed base is REFUSED (md5 pin)' % key, ok)
        shifted = (b'# shifted\r\n' if b'\r\n' in raw else b'# shifted\n') + raw
        P2 = dict(P, **{'base-md5': SP.md5b(shifted)})
        try:
            SP.apply_bytes(P2, shifted); ok = False
        except SP.PatchError as e:
            ok = 'exactly that line' in str(e)
        check('%s: a 1-line-shifted base is REFUSED even with its md5 accepted (no offset)' % key, ok)
        files[key] = (o, p)
    lp_o, lp_p = files['lp']
    ao_o, ao_p = files['ao']
    so_dir = os.path.join(work, 'series_out')
    p = subprocess.run([sys.executable, '-I', '-B', os.path.join(PROJ, 'tools', 'patch_series.py'), 'apply', '--series',
                        os.path.join(PROJ, 'patches', 'series_research.json'), '--src-root', a.glw, '--out-root', so_dir],
                       capture_output=True, text=True, stdin=subprocess.DEVNULL, creationflags=0x08000000 | 0x00004000)
    # W2 (0.6.3): the gpu_ao chain is P6 -> P6b
    PB = SP.read_patch(P6B)
    pb_path = os.path.join(work, 'patched_pb', '_research', 'ao', 'gpu_ao.py')
    os.makedirs(os.path.dirname(pb_path), exist_ok=True)
    open(pb_path, 'wb').write(SP.apply_bytes(PB, open(ao_p, 'rb').read()))
    check('P6b: strict apply on the P6 result -> result-md5 %s' % PB['result-md5'][:8], md5f(ao_p) == PB['base-md5'] and md5f(pb_path) == PB['result-md5'])
    check('patch_series.py apply patches/series_research.json == the strict_patch results (P5; P6 then P6b)', p.returncode == 0
          and md5f(os.path.join(so_dir, '_research', 'lightprobes', 'gpu_lightprobes.py')) == md5f(lp_p)
          and md5f(os.path.join(so_dir, '_research', 'ao', 'gpu_ao.py')) == md5f(pb_path), p.stdout + p.stderr)
    subs_tree = [(trees['orig'], '<TREE>'), (trees['patched'], '<TREE>')]

    # ---------------------------------------------------------------- 3: --help
    ro = harness(work, ao_o, ['--help'], effects=False, tag='aohelp_o')
    rp = harness(work, ao_p, ['--help'], effects=False, tag='aohelp_p')
    check('gpu_ao --help: byte-identical, exit 0', ro['stdout'] == rp['stdout'] and ro['exit'] == rp['exit'] == 0)
    for sub in ('apply', 'stage', 'install', 'selftest', 'check', 'consistency', 'seams', 'stats'):
        ro = harness(work, ao_o, [sub, '--help'], effects=False, tag='aohelp_%s_o' % sub)
        rp = harness(work, ao_p, [sub, '--help'], effects=False, tag='aohelp_%s_p' % sub)
        check('gpu_ao %s --help: byte-identical' % sub, ro['stdout'] == rp['stdout'] and ro['exit'] == rp['exit'] == 0)
    ro = harness(work, ao_o, ['trace', '--help'], effects=False, tag='aohelp_trace_o')
    rp = harness(work, ao_p, ['trace', '--help'], effects=False, tag='aohelp_trace_p')
    so, sp_ = options_section(ro['stdout']), options_section(rp['stdout'])
    check('gpu_ao trace --help: every pre-P6 option line kept (prefix), only --quality added',
          so and sp_ and sp_[:len(so)] == so and len(sp_) == len(so) + 1 and sp_[-1].strip().startswith('--quality Q'),
          'orig %d lines, patched %d' % (len(so or []), len(sp_ or [])))
    ro = harness(work, lp_o, ['--help'], effects=False, tag='lphelp_o')
    rp = harness(work, lp_p, ['--help'], effects=False, tag='lphelp_p')
    so, sp_ = options_section(ro['stdout']), options_section(rp['stdout'])
    new_opts = ['--h2-root DIR', '--farm DIR', '--pwsh EXE', '--plucker-fix', '--scenery-fix', '--photons N', '--mc-samples N']
    added = [l.strip() for l in (sp_ or [])[len(so or []):] if l.startswith('  -')]
    check('gpu_lightprobes --help: every pre-P5 option line kept (prefix), exit 0',
          so and sp_ and sp_[:len(so)] == so and ro['exit'] == rp['exit'] == 0)
    check('gpu_lightprobes --help: adds exactly %s' % ', '.join(new_opts),
          [x.split('  ')[0] for x in added] == new_opts, 'added %r' % added)

    # ---------------------------------------------------------------- 4: default argv identical (P5)
    W = pick_worker(a.glw)
    print('test worker %d (farm_w%02d does not exist under %s)' % (W, W, a.glw))
    scen = r'scenarios\multi\triplicate\triplicate'
    cases = [
        ('minimal', ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W)]),
        ('all-pre-P5-flags', ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W), '--quality', 'super', '--skip-capture',
                              '--stage', r'C:\fake\engine_v373', '--sever', '2', '--ltmp-src', r'C:\fake\final_stage\x.scenario_structure_lightmap']),
    ]
    for name, argv in cases:
        oo, op = os.path.join(work, 'lp_out_%s_o' % name), os.path.join(work, 'lp_out_%s_p' % name)
        ro = harness(work, lp_o, argv + ['--out', oo], tag='lp_%s_o' % name)
        rp = harness(work, lp_p, argv + ['--out', op], tag='lp_%s_p' % name)
        no = norm(dict(calls=ro['calls'], exit=ro['exit']), subs_tree + [(oo, '<OUT>')])
        np_ = norm(dict(calls=rp['calls'], exit=rp['exit']), subs_tree + [(op, '<OUT>')])
        check('gpu_lightprobes default argv (%s): child argv/env/exit identical orig vs P5 (%d calls)' % (name, len(no['calls'])),
              no == np_ and len(no['calls']) == 2, json.dumps([no, np_])[:600])
    bake = np_['calls'][1]['args']
    check('gpu_lightprobes default bake argv is the pre-P5 pwsh line (no -ExecutionPolicy, no fixes)',
          bake[:4] == ['pwsh', '-NoProfile', '-NonInteractive', '-File'] and '-ExecutionPolicy' not in bake
          and '-PluckerFix' not in bake and '-SceneryFix' not in bake
          and set(np_['calls'][1]['env_h2lm_gpulm'] or {}) == {'GPULM_PROBES', 'GPULM_SKY_HI'}, bake)

    # ---------------------------------------------------------------- 4b: default argv identical (P6)
    eng = os.path.join(work, 'fake_engine'); os.makedirs(eng, exist_ok=True); open(os.path.join(eng, 'gpu_bake.ps1'), 'w').write('# fake\n')
    os.makedirs(os.path.join(eng, 'v043-tagwriter'), exist_ok=True)
    trace_cases = [('minimal', []), ('all-pre-P6-flags', ['--plucker-fix', '--scenery-fix', '--shading-normal', '--strict-window', '--rep',
                                                           '--distance', '0.5', '--rays', '32'])]
    for name, extra in trace_cases:
        rr = {}
        for side, tgt in (('o', ao_o), ('p', ao_p)):
            farm = os.path.join(work, 'ao_%s_%s' % (name, side), 'farm_w%02d' % W); os.makedirs(farm, exist_ok=True)
            argv = ['trace', '--farm', farm, '--worker', str(W), '--scenario', scen, '--bsp', 'triplicate', '--engine', eng] + extra
            r = harness(work, tgt, argv, farm_for_effects=farm, tag='aotrace_%s_%s' % (name, side))
            rr[side] = norm(dict(calls=r['calls'], exit=r['exit']), subs_tree + [(os.path.dirname(farm), '<FARMROOT>')])
        check('gpu_ao trace default argv (%s): identical orig vs P6, no -Quality' % name,
              rr['o'] == rr['p'] and len(rr['p']['calls']) == 1 and '-Quality' not in rr['p']['calls'][0]['args'] and rr['p']['exit'] == 0,
              json.dumps(rr)[:600])
    # stage (scen_paths uses H2): identical with H2LM_ROOT unset
    rr = {}
    for side, tgt in (('o', ao_o), ('p', ao_p)):
        farm = os.path.join(work, 'aostage_%s' % side, 'farm_w%02d' % W)
        os.makedirs(os.path.join(farm, 'ao', 's10e05', 'run'), exist_ok=True); os.makedirs(os.path.join(farm, 'skeleton'), exist_ok=True)
        open(os.path.join(farm, 'ao', 's10e05', 'run', 'vertdir.bin'), 'wb').write(b'')
        open(os.path.join(farm, 'ao', 's10e05', 'AO_NOT_CPU_FAITHFUL.txt'), 'w').write('x')
        r = harness(work, tgt, ['stage', '--farm', farm, '--worker', str(W), '--scenario', scen, '--bsp', 'triplicate', '--engine', eng,
                                '--tag', 's10e05'], tag='aostage_%s' % side)
        rr[side] = norm(dict(calls=r['calls'], exit=r['exit']), subs_tree + [(os.path.dirname(farm), '<FARMROOT>')])
        if side == 'p': raw_p = r
    # A1 2026-10-15 (review finding 24, the AO stage path): P6 also passes --tool <ROOT>/tool_fast.exe to lm_tagwriter (its defs_pin gate then checks
    # THIS root's tool); with H2LM_ROOT unset ROOT == H2 and that is lmw_tagio's own default tool. Apart from that one pair the argv is the pre-P6 one.
    def drop_tool(x):
        x = json.loads(json.dumps(x)); a0 = x['calls'][0]['args']
        if '--tool' in a0:
            i = a0.index('--tool'); del a0[i:i + 2]
        return x
    a_raw = raw_p['calls'][0]['args'] if raw_p['calls'] else []
    tool_ok = (a_raw.count('--tool') == 1 and os.path.normcase(a_raw[a_raw.index('--tool') + 1]) == os.path.normcase(os.path.join(work, 'tool_fast.exe')))
    check('gpu_ao stage default argv: identical orig vs P6 apart from --tool <H2>/tool_fast.exe (H2LM_ROOT unset)',
          rr['o'] == drop_tool(rr['p']) and len(rr['p']['calls']) == 2 and tool_ok and '--tool' not in json.dumps(rr['o']), json.dumps(rr)[:600])
    # H2 resolution
    go = harness(work, ao_o, [], mode='globals', tag='aoglob_o')['globals']
    gp = harness(work, ao_p, [], mode='globals', tag='aoglob_p')['globals']
    gpe = harness(work, ao_p, [], mode='globals', env_extra={'H2LM_ROOT': ''}, tag='aoglob_p_empty')['globals']
    fake_root = os.path.join(work, 'Fake H2EK')
    os.makedirs(os.path.join(fake_root, 'tags', 'scenarios', 'multi', 'triplicate'), exist_ok=True)
    gpr = harness(work, ao_p, [], mode='globals', env_extra={'H2LM_ROOT': fake_root}, tag='aoglob_p_root')['globals']
    check('gpu_ao H2: unset and empty H2LM_ROOT give the pre-P6 value', go['H2'] == gp['H2'] == gpe['H2'] and go['H2'] == work,
          '%r %r %r' % (go, gp, gpe))
    check('gpu_ao H2: H2LM_ROOT overrides it (GLW stays code-relative)', os.path.normcase(gpr['H2']) == os.path.normcase(fake_root)
          and gpr['GLW'] == gp['GLW'], repr(gpr))

    # ---------------------------------------------------------------- 5: new passthroughs
    # P6 trace --quality
    for q in ('low', 'super'):
        farm = os.path.join(work, 'aoq_%s' % q, 'farm_w%02d' % W); os.makedirs(farm, exist_ok=True)
        r = harness(work, ao_p, ['trace', '--farm', farm, '--worker', str(W), '--scenario', scen, '--bsp', 'triplicate', '--engine', eng,
                                 '--plucker-fix', '--scenery-fix', '--quality', q], farm_for_effects=farm, tag='aoq_%s' % q)
        args = r['calls'][0]['args'] if r['calls'] else []
        i = args.index('-Quality') if '-Quality' in args else -1
        check('gpu_ao trace --quality %s -> gpu_bake -Quality %s (once), exit 0' % (q, q),
              i > 0 and args[i + 1] == q and args.count('-Quality') == 1 and r['exit'] == 0, repr(args))
    farm = os.path.join(work, 'aoq_custom', 'farm_w%02d' % W); os.makedirs(farm, exist_ok=True)
    r = harness(work, ao_p, ['trace', '--farm', farm, '--worker', str(W), '--engine', eng, '--quality', 'custom'], tag='aoq_custom')
    check('gpu_ao trace --quality custom is rejected by argparse (exit 2, nothing launched)', r['exit'] == 2 and not r['calls'])
    # P6 stage with H2LM_ROOT: lm_tagwriter gets the ROOT's tags
    farm = os.path.join(work, 'aostage_root', 'farm_w%02d' % W)
    os.makedirs(os.path.join(farm, 'ao', 's10e05', 'run'), exist_ok=True); os.makedirs(os.path.join(farm, 'skeleton'), exist_ok=True)
    open(os.path.join(farm, 'ao', 's10e05', 'run', 'vertdir.bin'), 'wb').write(b'')
    open(os.path.join(farm, 'ao', 's10e05', 'AO_NOT_CPU_FAITHFUL.txt'), 'w').write('x')
    r = harness(work, ao_p, ['stage', '--farm', farm, '--worker', str(W), '--scenario', scen, '--bsp', 'triplicate', '--engine', eng,
                             '--tag', 's10e05'], env_extra={'H2LM_ROOT': fake_root}, tag='aostage_root')
    args = r['calls'][0]['args'] if r['calls'] else []
    want_bsp = os.path.join(fake_root, 'tags', scen, '..', 'triplicate.scenario_structure_bsp')
    check('gpu_ao stage with H2LM_ROOT: lm_tagwriter --bsp/--scenario under ROOT\\tags',
          '--bsp' in args and os.path.normcase(os.path.normpath(args[args.index('--bsp') + 1])) == os.path.normcase(os.path.normpath(want_bsp))
          and os.path.normcase(args[args.index('--scenario') + 1]).startswith(os.path.normcase(os.path.join(fake_root, 'tags'))), repr(args))
    check('gpu_ao stage with H2LM_ROOT: lm_tagwriter --tool = ROOT/tool_fast.exe (finding 24)',
          args.count('--tool') == 1 and os.path.normcase(args[args.index('--tool') + 1]) == os.path.normcase(os.path.join(fake_root, 'tool_fast.exe')), repr(args))

    # W2 (0.6.3) P6b: `stage` without the flag launches exactly what P6 launched; with it, the same argv + --exact-vertex-lighting (last); other verbs' --help untouched
    farm = os.path.join(work, 'aostage_p6b', 'farm_w%02d' % W)
    os.makedirs(os.path.join(farm, 'ao', 's10e05', 'run'), exist_ok=True); os.makedirs(os.path.join(farm, 'skeleton'), exist_ok=True)
    open(os.path.join(farm, 'ao', 's10e05', 'run', 'vertdir.bin'), 'wb').write(b'')
    open(os.path.join(farm, 'ao', 's10e05', 'AO_NOT_CPU_FAITHFUL.txt'), 'w').write('x')
    sargs = ['stage', '--farm', farm, '--worker', str(W), '--scenario', scen, '--bsp', 'triplicate', '--engine', eng, '--tag', 's10e05', '--overwrite']
    renv = {'H2LM_ROOT': fake_root}          # the same ROOT for the three trees (--tool <ROOT>\tool_fast.exe)
    r6 = harness(work, ao_p, sargs, env_extra=renv, tag='aostage_p6')
    rb = harness(work, pb_path, sargs, env_extra=renv, tag='aostage_p6b')
    re_ = harness(work, pb_path, sargs + ['--exact-vertex-lighting'], env_extra=renv, tag='aostage_p6b_evl')
    c6 = r6['calls'][0]['args'] if r6['calls'] else []
    cb = rb['calls'][0]['args'] if rb['calls'] else []
    ce = re_['calls'][0]['args'] if re_['calls'] else []
    check('gpu_ao stage (P6b, no flag): the launched lm_tagwriter / validate argv is identical to P6 (2 children, same exit)',
          len(r6['calls']) == len(rb['calls']) == 2 and c6 == cb and r6['exit'] == rb['exit'] and r6['calls'][1]['args'] == rb['calls'][1]['args'], repr((c6, cb)))
    check('gpu_ao stage --exact-vertex-lighting -> lm_tagwriter gets it once, after --tool <ROOT>/tool_fast.exe; everything else identical; lmw_validate unchanged',
          ce == cb + ['--exact-vertex-lighting'] and re_['calls'][1]['args'] == rb['calls'][1]['args'] and re_['exit'] == rb['exit'], repr(ce))
    for verb in ('trace', 'apply', 'install'):
        h6 = harness(work, ao_p, [verb, '--help'], effects=False, tag='aohelp6_' + verb)
        hb = harness(work, pb_path, [verb, '--help'], effects=False, tag='aohelp6b_' + verb)
        check('gpu_ao %s --help: byte-identical after P6b' % verb, h6['stdout'] == hb['stdout'] and h6['exit'] == hb['exit'] == 0)
    hs6 = harness(work, ao_p, ['stage', '--help'], effects=False, tag='aohelp6_stage')
    hsb = harness(work, pb_path, ['stage', '--help'], effects=False, tag='aohelp6b_stage')
    o6 = set(re.findall(r'--[a-z][a-z-]*', hs6['stdout'])); ob = set(re.findall(r'--[a-z][a-z-]*', hsb['stdout']))
    check('gpu_ao stage --help: every option kept, only --exact-vertex-lighting added (argparse re-wraps the column)', hsb['exit'] == 0 and ob - o6 == {'--exact-vertex-lighting'} and o6 <= ob, repr((sorted(o6), sorted(ob))))

    # P5 full standalone line, with a fake farm that lets the driver walk to the end
    def fake_farm(root, w):
        f = os.path.join(root, 'GPU Lightmap Work', 'farm_w%02d' % w); os.makedirs(f, exist_ok=True)
        open(os.path.join(f, 'gpu_bake.log'), 'w').write('... PROBE: traced 11 probes ...\n')
        for n in ('vertex_out.bin', 'sky_tex_hi.bin', 'lp_globals.bin', 'sky_tex.bin'):
            open(os.path.join(f, n), 'wb').write(b'')
        return f
    farm = fake_farm(fake_root, W)
    pw = r'C:\rt\pwsh-7.6.6-x\pwsh.exe'
    eng5 = r'C:\INST\engine_v373'
    out5 = os.path.join(work, 'lp_out_standalone')
    ltmp_src = os.path.join(work, 'final_stage', 'triplicate_triplicate_lightmap.scenario_structure_lightmap')
    r = harness(work, lp_p, ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W), '--quality', 'super', '--skip-capture',
                             '--h2-root', fake_root, '--farm', farm, '--pwsh', pw, '--stage', eng5, '--plucker-fix', '--scenery-fix',
                             '--ltmp-src', ltmp_src, '--out', out5],
                env_extra={'H2LM_ROOT': fake_root, 'H2LM_WORK': os.path.join(fake_root, 'GPU Lightmap Work')}, tag='lp_standalone')
    C = r['calls']
    ok = len(C) == 5 and r['exit'] is None
    check('P5 standalone line: walks all 5 steps (lp_probes, gpu_bake, lp_globals, lp_compose, lp_write_sh)', ok,
          'exit %r, %d calls' % (r['exit'], len(C)))
    if ok:
        b = C[1]['args']
        want = [pw, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(eng5, 'gpu_bake.ps1'),
                '-Scenario', scen, '-Bsp', 'triplicate', '-Worker', str(W), '-Quality', 'super', '-SkipCapture', '-PluckerFix', '-SceneryFix']
        check('P5 bake argv = bundled pwsh + -ExecutionPolicy Bypass + fixes', b == want, repr(b))
        E = C[1]['env_h2lm_gpulm'] or {}
        env_ok = E.get('H2LM_ROOT') == fake_root and E.get('GPULM_SKY_HI') == '1' and set(E) == {'H2LM_ROOT', 'H2LM_WORK', 'GPULM_SKY_HI', 'GPULM_PROBES'}
        check('P5 bake env: H2LM_ROOT/H2LM_WORK passed through, GPULM_SKY_HI=1, GPULM_PROBES -> OUT\\probes.bin, nothing else GPULM', env_ok and
              os.path.normcase(E['GPULM_PROBES']) == os.path.normcase(os.path.join(out5, 'probes.bin')), repr(E))
        check('P5 lp_probes reads ROOT\\tags\\<scenario>.scenario',
              os.path.normcase(C[0]['args'][2]) == os.path.normcase(os.path.join(fake_root, 'tags', scen + '.scenario')), repr(C[0]['args']))
        check('P5 lp_globals / lp_compose read --farm', os.path.normcase(C[2]['args'][2]) == os.path.normcase(farm)
              and os.path.normcase(C[3]['args'][2]) == os.path.normcase(farm), repr([C[2]['args'], C[3]['args']]))
        check('P5 lp_write_sh writes the SH into a copy of --ltmp-src under OUT',
              C[4]['args'][2] == ltmp_src and os.path.normcase(C[4]['args'][3]) == os.path.normcase(os.path.join(out5, os.path.basename(ltmp_src))),
              repr(C[4]['args']))
    # default ltmp src follows --h2-root
    out6 = os.path.join(work, 'lp_out_rootsrc')
    r = harness(work, lp_p, ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W), '--h2-root', fake_root, '--farm', farm, '--out', out6],
                tag='lp_rootsrc')
    C = r['calls']
    check('P5 --h2-root: the default --ltmp-src is ROOT\\tags\\...\\<leaf>_<bsp>_lightmap', len(C) == 5 and os.path.normcase(C[4]['args'][2]) ==
          os.path.normcase(os.path.join(fake_root, 'tags', r'scenarios\multi\triplicate', 'triplicate_triplicate_lightmap.scenario_structure_lightmap')),
          repr(C[-1]['args'] if C else None))
    check('P5 --h2-root without H2LM_ROOT in the env: the bake gets H2LM_ROOT=ROOT', len(C) == 5 and
          (C[1]['env_h2lm_gpulm'] or {}).get('H2LM_ROOT') == fake_root and C[1]['args'][0] == 'pwsh' and '-ExecutionPolicy' not in C[1]['args'],
          repr(C[1] if len(C) > 1 else None))
    # custom passthrough
    out7 = os.path.join(work, 'lp_out_custom')
    r = harness(work, lp_p, ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W), '--quality', 'custom', '--photons', '50000000',
                             '--mc-samples', '16', '--farm', farm, '--h2-root', fake_root, '--out', out7], tag='lp_custom')
    b = r['calls'][1]['args'] if len(r['calls']) > 1 else []
    check('P5 --quality custom --photons 50000000 --mc-samples 16 -> -Quality custom -Photons 50000000 -MCSamples 16',
          b[-6:] == ['-Quality', 'custom', '-Photons', '50000000', '-MCSamples', '16'], repr(b))

    # ---------------------------------------------------------------- 6: refusals (no child, no OUT)
    other_root = os.path.join(work, 'Other H2EK'); os.makedirs(os.path.join(other_root, 'tags'), exist_ok=True)
    refusals = [
        ('--photons without --quality custom', ['--photons', '1000000'], {}),
        ('--mc-samples without --quality custom', ['--quality', 'super', '--mc-samples', '8'], {}),
        ('--farm leaf is not farm_wNN of --worker', ['--farm', os.path.join(fake_root, 'GPU Lightmap Work', 'farm_w%02d' % (W - 1))], {}),
        ('--farm outside H2LM_WORK', ['--farm', farm], {'H2LM_WORK': os.path.join(work, 'elsewhere')}),
        ('--h2-root without a tags folder', ['--h2-root', os.path.join(work, 'no_such_root')], {}),
        ('--h2-root disagrees with H2LM_ROOT', ['--h2-root', fake_root], {'H2LM_ROOT': other_root}),
        ('--out inside ROOT\\tags', ['--h2-root', fake_root, '--out!', os.path.join(fake_root, 'tags', 'lp_out')], {}),
    ]
    for i, (name, extra, envx) in enumerate(refusals):
        outx = os.path.join(work, 'lp_refuse_%d' % i)
        argv = ['--scenario', scen, '--bsp', 'triplicate', '--worker', str(W)]
        if '--out!' in extra:
            j = extra.index('--out!'); outx = extra[j + 1]; extra = extra[:j] + extra[j + 2:]
        r = harness(work, lp_p, argv + extra + ['--out', outx], env_extra=envx, tag='lp_refuse_%d' % i)
        check('P5 refuses %s (REFUSED, no child, OUT not created)' % name,
              isinstance(r['exit'], str) and r['exit'].startswith('REFUSED') and not r['calls'] and not os.path.exists(outx),
              'exit %r calls %d out-exists %s' % (r['exit'], len(r['calls']), os.path.exists(outx)))

    n_ok = sum(1 for _, ok, _ in RESULTS if ok)
    if not a.keep and not a.work:
        for root, dirs, fs in os.walk(work):      # our own temp dir: refuse to remove it if anything inside is a reparse point
            if any(os.path.islink(os.path.join(root, n)) or (getattr(os.lstat(os.path.join(root, n)), 'st_file_attributes', 0) & 0x400)
                   for n in dirs + fs):
                print('NOT removing %s (a reparse point inside)' % work); break
        else:
            shutil.rmtree(work)
    else:
        print('kept: %s' % work)
    print('RESULT: %s %d/%d' % ('PASS' if n_ok == len(RESULTS) else 'FAIL', n_ok, len(RESULTS)))
    return 0 if n_ok == len(RESULTS) else 1


if __name__ == '__main__':
    sys.exit(main())
