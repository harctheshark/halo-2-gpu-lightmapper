"""S1.9 payload builder tests. Run AFTER a build (py -I -B tools\\payload\\build_payload.py) -- from Bash:
    bash tests/s19_payload/run_tests.sh            (builds first, then this)
    py -I -B tests\\s19_payload\\test_payload.py [--only NAME,...] [--keep]

No GPU, no bake, no tool_fast run, no live-tag write. Production is only READ (the refusal tests run on a COPY of the
production inputs under build\\s19\\t\\fakeglw). The extraction test runs the built exe with a sandbox H2EK root and a
sandbox per-user folder under %TEMP%\\s19 (Direct slot, so no junction is created). Everything runs BELOW_NORMAL with no
window. Exit 0 = all PASS. Results: tests\\s19_payload\\results\\last_run.json."""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import traceback
import zipfile

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(os.path.dirname(HERE))
TOOLS = os.path.join(PROJ, 'tools', 'payload')
sys.path.insert(0, TOOLS)
import h2lm_payload_lib as L   # noqa: E402

PY = [sys.executable, '-I', '-B']
CREATE_NO_WINDOW = 0x08000000
B = L.BUILD
T = os.path.join(B, 't')
STAGE, PAYLOAD = os.path.join(B, 'stage'), os.path.join(B, 'payload')
EXE = os.path.join(B, 'exe', 'H2 GPU Lightmapper.exe')
SPEC = L.load_spec()
GLW = SPEC['roots']['GLW']
H2ROOT = SPEC['roots']['H2ROOT']
# 0.6.8 RC: the sandbox H2EK root (where the INST -- with the tool loader -- is extracted) lives in the user's Temp - Deletable folder (a
# Windows Security exclusion of the user's; never AppData / %TEMP%): H2LM_S19_SANDBOX overrides it. Deepest INST path there: 217 characters
# (incl. the .tmp_ extraction dir) < MAX_PATH. The per-user folders (rt runtimes: pwsh / py / mapbake / notices, no injector) and the DLL-slot
# alias junction stay in %TEMP%\s19aux: the alias is the one link the shell makes, and Temp - Deletable must hold no links.
S19_PARENT = os.environ.get('H2LM_S19_PARENT') or os.path.join(SPEC['roots']['GLW'], 'Temp - Deletable (Max 250gbs)')
SANDBOX = os.environ.get('H2LM_S19_SANDBOX') or os.path.join(S19_PARENT, 's19')
AUX = os.path.join(os.environ['TEMP'], 's19aux')

# what other steps proved (S1.1/S1.2, S1.3, S1.4, S1.8 PROGRESS / interfaces): the payload must carry exactly these bytes
# U1 (2026-10-15): production gpu_bake.ps1 HOLDS the series (P1->P3->P2->F24 / P1->P3->F24, pin E2DBA7AF): (pre-upstream base, production
# md5 = the shipped bytes); the builder stages them as verified PRISTINE copies (kind prod, role upstreamed:...)
EXPECT_UPSTREAMED = {
    'engine_v373/gpu_bake.ps1': ('eb8e6bf2d26fc3efa3a42b24560fd9ed', 'd16ac070fc973e6d43738cc89903670d'),   # 0.6.9 rc069_a2: + P19 (the dev_RB promotion, d16ac070; e976ba73 = rc069_a1); 0.6.8 RC: + P16 ToolLoader (e976ba73; 30c27781 = the dev_XC promotion P15; 669dcc14 = 0.6.7); W7 0.6.7 promotion (cefcf497 = 0.6.6); W6 0.6.6 promotion (cabc8449); W5 2026-10-22: the 0.6.5 promotion (W3: 7a033e06; W2: f13328c7, 15771590, fe88bd8b)
    '_research/ao/dev_AO/gpu_bake.ps1': ('405570d10d890b5b0ca9505904777a05', 'ff8de832ccace002566a197ccf860af5'),   # 0.6.9 rc069_a3: + P20 (dev_AO14: the -AOOnly loop forwards the sub-chart cut; b34c221e = dev_AO13); 0.6.8 RC: dev_AO11 + P16 (b34c221e; d95fb296 = P15; 92ee0226 = dev_AO10); W7 0.6.7: dev_AO10 (59d529c5 = dev_AO9, W6 0.6.6): dev_AO9 (dev_AO8 7ff83e05); production source _research/ao/dev_AO8 (W5 0.6.5; W3: dev_AO6 cd488322; W2: dev_AO5 1a0318ac; was dev_AO4 440ce318, dev_AO2 bac932e8)
}
# 0.6.8: the two gpu_bake.ps1 copies are production (the verified U1 upstream, md5 as above) + the post-upstream series P13 (H2LM_FARMS) + P14
# (comment only: the shipped comment no longer names the author's test map); path -> (production md5, payload md5)
EXPECT_POST = {
    # 0.6.8 RC: production = the dev_XC promotion (P15, which holds P13), post_upstream = P14 only (2f493706 / 5af79701 = the 0.6.7-cast dry run)
    # + P16 (the tool loader: one $INJ path line, promoted into production; 023d7a55 / 7937c908 = rc068_xc3)
    # + post-upstream P17 (the four tool command lines quote the scenario and the BSP; bb22f710 / d312fe82 = rc068_xc4)
    'engine_v373/gpu_bake.ps1': ('d16ac070fc973e6d43738cc89903670d', '1da2c1fffe01c67b4e16877da763255a'),   # 0.6.9 rc069_a2: P17 / P14 / P18 re-made on d16ac070 (rc069_a1: e976ba73 -> fbf47cfc); 0.6.9: + post-upstream P18 (-Rt RT mode + GPULM_FASTNQ; 6140a173 = rc068_xc4..xc9)
    '_research/ao/dev_AO/gpu_bake.ps1': ('ff8de832ccace002566a197ccf860af5', '8b0e2904bdd593e0bc11f1d76fdf8587'),   # 0.6.9 rc069_a3: P17 / P14 re-made on ff8de832 (rc069_a2: b34c221e -> 4adb7566)
}
EXPECT_PATCHED = {
    '_research/lightprobes/gpu_lightprobes.py': ('0668095dc7425a6a195a77a226d30ca7', '21958cbdcf2fdd6b122a69d48e77354c'),   # 0.6.8: + P5c (--farm under H2LM_FARMS; P5 + P5b = 3634172b); P5 + P5b (0.6.5 split-BSP marker; P5 alone = ffb288d4)
    '_research/ao/gpu_ao.py': ('04b171cabd16084eff3190de48722f89', 'a8914dc88a0271d73938679ec6511cbe'),   # P6 + P6b + P6c (0.6.5 split-BSP; P6+P6b = 56eba64d) (A1 2026-10-15: re-made on the AO-noise workflow's gpu_ao.py; + stage --tool) + P6b (W2 0.6.3: stage --exact-vertex-lighting; P6 alone = c4a0277f)
    '_research/ao/ao_seamfix.py': ('93c2fe8c5ce812358b58b7c2073da030', '7b239011f1a9d6becbc11ce593398cd6'),   # N4 (A1 re-make: + stacked-layer rule)
    '_research/ao/ao_denoise.py': ('5da70c2fe14fbb2decd78ce8902b418a', '70522fac1d61f372e6c5af358592e7cf'),   # N4 (A1: ao_denoise.bilateral_raw without scipy)
    # U2 (2026-10-15): the three lmw_tagio.py walkers read SHORT block elements as tool_fast does (patches/tagio/series_tagio.json)
    'engine_v373/v043-tagwriter/lmw_tagio.py': ('a0326b65fcf81d9432690ec90931e523', 'b069e88ded71f575550b344cbdf912bc'),   # 0.6.9: dev_TW's writer file + U2b (U2 = 52dc580e -> b79b25cf no longer applies)
    '_research/ao/dev_AO/v043-tagwriter/lmw_tagio.py': ('52dc580ef26eaf26e370f03470c14804', 'b79b25cf305a617346ef5f0296f040f6'),   # production source dev_AO2
    'v043-tagwriter/lmw_tagio.py': ('e8aeb703b786b8c995297834abdc73f6', '79673a03383cd0ab9cdfd371dc76598c'),
}
EXPECT_BUILT = {'osoyoos/osoy_launch.exe': 'e2dba7afcaf6967a900ade1d59d01f5d',
                # 0.6.8 RC (P16): the tool loader built from the unchanged inject.cpp (build	oolloaderuild_toolloader.json, reproducible)
                'v014-inject-estimator/H2GpuLightmapper.ToolLoader.exe': '43872d6bd93b3e891bca7f2f8908dd8c',
                # 0.6.9: the native tag-writer helper built from tw_native.cpp with VERSIONINFO + asInvoker manifest (build/twnative/build_twnative.json, reproducible)
                'engine_v373/v043-tagwriter/bin/tw_native.exe': '6178da9ac4432a42f0be7d4e6e4cebe4'}
EXPECT_NEW = {'_research/ao/ao_ballgrid.py': '1ef5e617bfd2ab11d3366e018a0259e6', 'standalone/sh_stage.py': None,
              'standalone/farm_products.json': 'PROJ:src/standalone/farm_products.json',
              '_research/ao/ao_existing.py': 'PROJ:src/standalone/ao_existing.py'}      # 0.6.8 xc8: AO only      # 0.6.8: the engine-owned product list (shipped from the project tree)      # ao_ballgrid: A1 (+ ball_blocks)
SHELL_NEEDS = ['engine_v373/gpu_bake.ps1', 'engine_v373/v043-tagwriter/lmw_install.py', 'engine_v373/v014-inject-estimator/gpulm_estimator.dll',
               'engine_v373/v041-fullgpu/fullgpu.exe', '_research/ao/gpu_ao.py', '_research/ao/dev_AO/gpu_bake.ps1',
               '_research/lightprobes/gpu_lightprobes.py', 'standalone/sh_stage.py', 'v043-tagwriter/lmw_tagio.py',
               'v043-tagwriter/lmw_defs.json', 'standalone/gpucheck_plain.exe',
               '_research/ao/ao_seamfix.py', '_research/ao/ao_denoise.py', '_research/ao/ao_ballgrid.py']      # A1: gpu_ao imports all three
RESULTS = []


def run(argv, timeout=1200, env=None):
    t = time.time()
    p = subprocess.run(argv, creationflags=CREATE_NO_WINDOW, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, timeout=timeout, env=env)
    return p.returncode, p.stdout.decode('utf-8', 'replace'), p.stderr.decode('utf-8', 'replace'), time.time() - t


def check(cond, msg):
    if not cond:
        raise AssertionError(msg)


def manifest():
    return json.load(open(os.path.join(PAYLOAD, 'MANIFEST.json'), encoding='utf-8'))


def stage_report():
    return json.load(open(os.path.join(STAGE, 'stage_report.json'), encoding='utf-8'))


def inst_component(m=None):
    return [c for c in (m or manifest())['components'] if c['target'] == 'inst'][0]


def guard_record():
    """the REAL N3 / T3a record the payload spec names (0.6.9: build\\fullgpu_guard_069\\LATEST.json; never edited by the tests)"""
    return json.load(open(guard_record_path(), encoding='utf-8'))


def guard_record_path():
    return L.resolve(L.load_spec()['inst']['custom_engine']['latest'], L.load_spec()['roots'])


def custom_shipped():
    """V2 (T3a): the build ships engine_custom/ (= engine_v373 + the N3 guarded fullgpu) when it was run --with-custom-engine,
    which stage_engine allows only with a T3a PASS record for the CURRENT production fullgpu"""
    return bool(stage_report().get('custom_engine', {}).get('shipped'))


def check_custom_guard(md5_in_payload):
    """the guard fullgpu the payload carries is the one T3a proved, built against the production fullgpu on disk NOW"""
    lt = guard_record()
    prod = L.md5_file(os.path.join(GLW, 'engine_v373', 'v041-fullgpu', 'fullgpu.exe'))
    check(lt.get('ok') and str(lt.get('gpu_identity_proof', '')).upper().startswith('PASS'), 'LATEST.json is not a T3a PASS: %r' % lt.get('gpu_identity_proof'))
    check(lt['production_fullgpu_md5'] == prod, 'the guard was proven against fullgpu %s, production is %s' % (lt['production_fullgpu_md5'], prod))
    check(md5_in_payload == lt['guard_md5'], 'engine_custom fullgpu %s is not the T3a-tested guard %s' % (md5_in_payload, lt['guard_md5']))
    return lt


# ----------------------------------------------------------------------------------------------------------- tests
def t_manifest_and_zips():
    """MANIFEST.json obeys the shell's format-1 rules and every zip holds exactly its manifest entry, byte for byte, in the
    deterministic format; dirNames are the content hashes; the exe embeds MANIFEST.json + every zip byte-identically"""
    m = manifest()
    check(m['format'] == 1 and m['appVersion'] == L.app_version(), 'format / appVersion')
    names = {c['name'] for c in m['components']}
    check(names == {'pwsh-7.6.6', 'py310-np226', 'mapbake', 'notices', 'engine'}, 'components %s' % names)
    for c in m['components']:
        zp = os.path.join(PAYLOAD, c['resource'].split('/', 1)[1])
        check(c['zipMd5'] == L.md5_file(zp) and c['zipSize'] == os.path.getsize(zp), '%s zip md5/size' % c['name'])
        probs = L.check_det_zip(zp, c['files'])
        check(not probs, '%s: %s' % (c['name'], probs[:3]))
        h8 = L.content_hash(c['files'])[:8]
        check(c['dirName'].endswith('-' + h8), '%s dirName %s does not end in its content hash %s' % (c['name'], c['dirName'], h8))
        check(c['fileCount'] == len(c['files']) and c['totalBytes'] == sum(f['size'] for f in c['files']), '%s counts' % c['name'])
        check(c['maxPathLen'] == max(len(f['path']) for f in c['files']), '%s maxPathLen' % c['name'])
        for f in c['files']:
            check('__pycache__' not in f['path'] and not f['path'].endswith('.pyc') or c['name'] == 'py310-np226',
                  '%s carries bytecode %s' % (c['name'], f['path']))
    check(inst_component(m)['dirName'] == '%s-%s' % (m['appVersion'], L.content_hash(inst_component(m)['files'])[:8]), 'INST dirName')
    bp = L.load_module('h2lm_build_payload', os.path.join(TOOLS, 'build_payload.py'))
    probs, emb = bp.check_exe(EXE, PAYLOAD)
    check(not probs, 'exe resources: %s' % probs)
    return '%d components, exe embeds %d resources byte-identical' % (len(m['components']), len(emb))


def t_inst_equals_production():
    """T0: every INST file equals the CURRENT production md5 except the declared P / N / built files, which equal the
    results the S1.1-S1.8 steps tested"""
    c = inst_component()
    declared = (set(EXPECT_PATCHED) | set(EXPECT_UPSTREAMED) | set(EXPECT_POST) | set(EXPECT_BUILT) | set(EXPECT_NEW) |
                {'standalone/gpucheck.exe', 'standalone/gpucheck_plain.exe'})
    rep = stage_report()
    srcmap = {r['path']: r for r in rep['files']}
    twins = {f['path']: f['md5'] for f in c['files']}
    n_prod, n_custom = 0, 0
    for f in c['files']:
        p, r = f['path'], srcmap[f['path']]
        if p.startswith('engine_custom/'):
            # V2 (T3a PASS): engine_custom/ = the INST engine_v373 file for file, except the N3 guarded fullgpu
            if p == 'engine_custom/v041-fullgpu/fullgpu.exe':
                check_custom_guard(f['md5'])
                check(r.get('role') == 'built:N3', '%s role %s' % (p, r.get('role')))
            elif p == 'engine_custom/v041-fullgpu/fullgpu_rt.exe':
                # 0.6.9: Custom's RT host = the RT variant with the N3 guard edits (T3a record guard_rt_md5 + gpu_identity_proof_rt), never the plain RT exe
                lt = guard_record()
                check(str(lt.get('gpu_identity_proof_rt', '')).upper().startswith('PASS') and f['md5'] == lt.get('guard_rt_md5'),
                      'engine_custom RT host %s is not the T3a-tested RT guard %s' % (f['md5'], lt.get('guard_rt_md5')))
                check(r.get('role') == 'built:N3rt' and f['md5'] != twins.get('engine_v373/v041-fullgpu/fullgpu_rt.exe'), '%s role %s' % (p, r.get('role')))
            else:
                twin = 'engine_v373/' + p[len('engine_custom/'):]
                check(twin in twins and f['md5'] == twins[twin], '%s differs from its INST twin %s' % (p, twin))
            n_custom += 1
            continue
        if p in EXPECT_POST:
            prod, res = EXPECT_POST[p]
            check(f['md5'] == res, '%s md5 %s != production + post-upstream series %s' % (p, f['md5'], res))
            check(L.md5_file(r['source']) == prod and r.get('kind') == 'patched' and r.get('role', '').startswith('upstreamed+post:')
                  and (r.get('upstream') or {}).get('base_md5') == EXPECT_UPSTREAMED[p][0],
                  '%s: not staged as production %s + the post-upstream series (%s)' % (p, prod, {k: r.get(k) for k in ('kind', 'role')}))
        elif p in EXPECT_UPSTREAMED:
            base, res = EXPECT_UPSTREAMED[p]
            check(f['md5'] == res == f.get('sourceMd5') == L.md5_file(r['source']), '%s md5 %s != upstreamed production %s' % (p, f['md5'], res))
            check(r.get('kind') == 'prod' and r.get('role', '').startswith('upstreamed:') and (r.get('upstream') or {}).get('base_md5') == base,
                  '%s: not staged as the verified upstream of %s (%s)' % (p, base, {k: r.get(k) for k in ('kind', 'role', 'upstream')}))
        elif p in EXPECT_PATCHED:
            base, res = EXPECT_PATCHED[p]
            check(f['md5'] == res, '%s md5 %s != tested %s' % (p, f['md5'], res))
            check(f.get('sourceMd5') == base and L.md5_file(r['source']) == base, '%s base' % p)
        elif p in EXPECT_BUILT:
            check(f['md5'] == EXPECT_BUILT[p], '%s %s' % (p, f['md5']))
        elif p == 'standalone/scan_scenery_bounds.py':      # W1: SF1's scanner, shipped from the project tree
            check(f['md5'] == L.md5_file(os.path.join(PROJ, 'tools', 'scan_scenery_bounds.py')), '%s %s' % (p, f['md5']))
        elif p in EXPECT_NEW:
            want = EXPECT_NEW[p] or L.md5_file(os.path.join(PROJ, 'src', 'standalone', 'sh_stage.py'))
            if want.startswith('PROJ:'):
                want = L.md5_file(os.path.join(PROJ, *want[5:].split('/')))
            check(f['md5'] == want, '%s %s != %s' % (p, f['md5'], want))
        elif p.startswith('standalone/gpucheck'):
            rec = json.load(open(os.path.join(PROJ, 'build', 'gpucheck', 'build_gpucheck.json')))
            check(f['md5'] == rec['gpucheck_plain' if 'plain' in p else 'gpucheck']['md5'], p)
        else:
            check(p not in declared and r['kind'] == 'prod', p)
            check(r['source'].lower().startswith((GLW.lower() + os.sep, H2ROOT.lower() + os.sep)) or (p == 'standalone/h2v_scenery_bounds.db' and r['source'].lower().startswith(os.path.join(PROJ, 'tools', 'payload', 'data').lower() + os.sep)), '%s source %s is not production' % (p, r['source']))
            check(f['md5'] == L.md5_file(r['source']) == f.get('sourceMd5'), '%s != production %s' % (p, r['source']))
            n_prod += 1
    bdb = [f for f in c['files'] if f['path'] == 'standalone/h2v_scenery_bounds.db'][0]
    check(bdb['md5'] == '6814f29a8210aaa3871b68e8ea659c93' == L.md5_file(os.path.join(PROJ, 'tools', 'payload', 'data', 'h2v_scenery_bounds.db')), 'bounds DB (W1: the project copy of the Osoyoos original; the live file is extended by the scanner)')
    # the production fullgpu.exe is copied by its CURRENT md5 (no pin): equal to production now
    fg = [f for f in c['files'] if f['path'] == 'engine_v373/v041-fullgpu/fullgpu.exe'][0]
    check(fg['md5'] == L.md5_file(os.path.join(GLW, 'engine_v373', 'v041-fullgpu', 'fullgpu.exe')), 'fullgpu')
    n_eng = sum(1 for f in c['files'] if f['path'].startswith('engine_v373/'))
    check(n_custom == (n_eng if custom_shipped() else 0), 'engine_custom has %d files, engine_v373 %d (shipped %s)' % (n_custom, n_eng, custom_shipped()))
    return '%d pristine files == production now, %d declared P/N/built files == their tested results (fullgpu %s)%s' % (
        n_prod, len(c['files']) - n_prod - n_custom, fg['md5'][:8],
        '; engine_custom = %d files == engine_v373 except the T3a guard %s' % (n_custom, guard_record()['guard_md5'][:8]) if n_custom else '')


def t_roles_layout():
    """the shell's INST contract: required paths, 4 pristine slot DLLs, boundsdb / gpucheck roles, runtimes at their roots,
    no scipy, no cleanup_farm.ps1, no backups, no research-only files"""
    m = manifest()
    c = inst_component(m)
    paths = {f['path']: f for f in c['files']}
    for p in SHELL_NEEDS:
        check(p in paths, 'INST lacks %s' % p)
    slots = sorted(p for p, f in paths.items() if 'dllslot' in f.get('role', '').split(','))
    nslot = 6 if custom_shipped() else 4          # engine_custom/ adds its own pair (the same two pristine builds)
    check(len(slots) == nslot and all(p.endswith('gpulm_estimator.dll') for p in slots), 'dllslot %s' % slots)
    want_slots = ['d6147fce6e3979a37178d2e14d092227', '56b774df2fce703a9a63da793aab7c8c', 'd6147fce6e3979a37178d2e14d092227', '5c6aeea9d1ffbaaaa6351d7ba5e77360']      # 0.6.9: engine v014 + the NEW fast-NeuQuant skeleton DLL, the AO engine's 0.6.4 pair
    if nslot == 6:
        want_slots += ['d6147fce6e3979a37178d2e14d092227', '56b774df2fce703a9a63da793aab7c8c']
    check(sorted(paths[p]['md5'] for p in slots) == sorted(want_slots),   # W3 2026-10-21: the 0.6.4 estimator DLLs (W1: b2370477 / f0fb73ac; was aaf10a0f)
          'slot md5s')
    roles = lambda r: [p for p, f in paths.items() if r in f.get('role', '').split(',')]
    check(roles('boundsdb') == ['standalone/h2v_scenery_bounds.db'], 'boundsdb')
    check(roles('gpucheck') == ['standalone/gpucheck.exe'], 'gpucheck')
    bad = [p for p in paths if p.endswith('cleanup_farm.ps1') or (p.endswith('.md') and not p.endswith('/CULLMARK_SPEC.md')) or      # 0.6.7: CULLMARK_SPEC.md ships beside the engine (the spec of the marker-map port)
            '.bak' in p or '/_backup' in p or '__pycache__' in p
           or '/ao_seam_' in p or 'scnobs' in p or p.endswith('.h') or p.endswith('.cpp')]
    check(not bad, 'unexpected files in INST: %s' % bad)
    rt = [x for x in m['components'] if x['target'] == 'rt']
    check(any(any(f['path'] == 'pwsh.exe' for f in x['files']) for x in rt), 'pwsh.exe')
    pyc = [x for x in rt if any(f['path'] == 'py.exe' for f in x['files'])][0]
    check(not any(f['path'].startswith('site-packages/scipy') for f in pyc['files']), 'scipy shipped')
    rep = stage_report()
    check(all(not r['missing'] and not r['w_code_refs'] for r in rep['gpu_bake_refs']), 'gpu_bake refs')
    check(rep['imports']['scanned'] >= 50 and not rep['imports']['optional_unresolved'], 'imports %s' % rep['imports'])
    ce = [p for p in paths if p.startswith('engine_custom/')]
    if rep['custom_engine']['shipped']:
        check_custom_guard(paths['engine_custom/v041-fullgpu/fullgpu.exe']['md5'])
        check(rep['custom_engine'].get('guard_md5') == guard_record()['guard_md5'] and 'engine_custom/gpu_bake.ps1' in paths
              and len(ce) == sum(1 for p in paths if p.startswith('engine_v373/')), 'engine_custom layout / report %s' % rep['custom_engine'])
    else:
        check(not ce, 'engine_custom shipped without --with-custom-engine')
    return '%d INST files; slots %s; refs %s; %d .py import-checked; engine_custom %s' % (
        len(paths), ', '.join(s.split('/')[0] for s in slots), '/'.join(str(r['refs_checked']) for r in rep['gpu_bake_refs']), rep['imports']['scanned'],
        'shipped (%d files, guard %s)' % (len(ce), rep['custom_engine'].get('guard_md5', '')[:8]) if ce else 'not shipped')


def t_determinism():
    """a second, independent stage + zip run gives byte-identical zips / MANIFEST.json; a second exe build is byte-identical"""
    sb, pb = os.path.join(T, 'stage_b'), os.path.join(T, 'payload_b')
    extra = ['--with-custom-engine'] if custom_shipped() else []      # the same build options as the payload under test
    rc, o, e, s1 = run(PY + [os.path.join(TOOLS, 'stage_engine.py'), '--out', sb] + extra)
    check(rc == 0, 'stage B exit %d: %s' % (rc, e[-500:]))
    rc, o, e, s2 = run(PY + [os.path.join(TOOLS, 'zip_payloads.py'), '--stage', sb, '--out', pb, '--report', os.path.join(T, 'payload_b_report.json')])
    check(rc == 0, 'zip B exit %d: %s' % (rc, e[-500:]))
    same = []
    for f in sorted(os.listdir(PAYLOAD)):
        if f.endswith(('.zip', '.json', '.txt')):
            a, b = L.md5_file(os.path.join(PAYLOAD, f)), L.md5_file(os.path.join(pb, f))
            check(a == b, '%s differs between builds (%s vs %s)' % (f, a, b))
            same.append('%s %s' % (f, a[:8]))
    check(json.load(open(os.path.join(sb, 'stage_report.json')))['content_hash'] == stage_report()['content_hash'], 'stage content hash')
    md5_1 = L.md5_file(EXE)
    keep = {n: open(os.path.join(B, n), 'rb').read() for n in ('BUILD.json', 'MANIFEST.json', 'stage_report.json', 'ENGINE_MANIFEST.txt')
            if os.path.isfile(os.path.join(B, n))}
    try:
        rc, o, e, s3 = run(PY + [os.path.join(TOOLS, 'build_payload.py'), '--skip-stage'])
    finally:
        for n, data in keep.items():                 # the full build's records stay the ones on disk
            open(os.path.join(B, n), 'wb').write(data)
    check(rc == 0, 'exe rebuild exit %d: %s' % (rc, (o + e)[-800:]))
    md5_2 = L.md5_file(EXE)
    check(md5_1 == md5_2, 'exe md5 changed on rebuild: %s -> %s' % (md5_1, md5_2))
    return 'identical: %s; exe %s twice' % (', '.join(same), md5_1[:8])


def _fake_glw():
    """a COPY of every production input the stage read under GLW (refusal tests mutate this copy, never production)"""
    fg = os.path.join(T, 'fakeglw')
    if os.path.isdir(fg):
        return fg
    for src in stage_report()['sources']:
        if not src.lower().startswith(GLW.lower() + os.sep):
            continue
        dst = os.path.join(fg, os.path.relpath(src, GLW))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(src, dst)
    return fg


def _spec_variant(name, edit):
    s = json.loads(json.dumps(SPEC))
    edit(s)
    p = os.path.join(T, 'spec_%s.json' % name)
    json.dump(s, open(p, 'w'), indent=1)
    return p


def _refused(args, want, label):
    rc, o, e, _ = run(PY + [os.path.join(TOOLS, 'stage_engine.py')] + args)
    check(rc == 2, '%s: exit %d (want 2 refused); out %s err %s' % (label, rc, o[-300:], e[-300:]))
    check(want.lower() in e.lower(), '%s: refusal does not say %r: %s' % (label, want, e[-500:]))
    return label


def t_refusals():
    """stage_engine refuses (exit 2, the published stage untouched) on: drifted production gpu_bake, a missing production
    file, a code reference the spec forgot, an import the spec forgot, a wrong built-binary pin, production changing during
    the build, --selftest-mutate on production, --with-custom-engine before T3a, an output outside build\\s19; zip_payloads
    refuses a stage whose bytes no longer match its report"""
    os.makedirs(T, exist_ok=True)
    before = (L.md5_file(os.path.join(STAGE, 'stage_report.json')), L.md5_file(os.path.join(PAYLOAD, 'MANIFEST.json')))
    fg = _fake_glw()
    out = lambda n: ['--out', os.path.join(T, 'stage_' + n)]
    done = []
    # sanity: the copy itself stages fine (so every refusal below is caused by its one change)
    rc, o, e, _ = run(PY + [os.path.join(TOOLS, 'stage_engine.py'), '--glw', fg] + out('fakeok'))
    check(rc == 0, 'fake GLW copy does not stage: %s' % e[-500:])
    # 1. drift
    gb = os.path.join(fg, 'engine_v373', 'gpu_bake.ps1')
    orig = open(gb, 'rb').read()
    open(gb, 'wb').write(orig[:-3] + bytes([orig[-3] ^ 1]) + orig[-2:])
    try:
        done.append(_refused(['--glw', fg] + out('drift'), 'production drifted', 'drifted gpu_bake.ps1'))
    finally:
        open(gb, 'wb').write(orig)
    # 2. missing production file
    sg = os.path.join(fg, 'sky_gate.py')
    os.rename(sg, sg + '.away')
    try:
        done.append(_refused(['--glw', fg] + out('missing'), 'missing in production', 'missing sky_gate.py'))
    finally:
        os.rename(sg + '.away', sg)
    # 3. a code reference the spec forgot
    def drop(name):
        def ed(s):
            for g in s['inst']['groups']:
                if name in g['files']:
                    g['files'].remove(name)
        return ed
    done.append(_refused(['--spec', _spec_variant('noskygate', drop('sky_gate.py'))] + out('ref'), 'code references that are not in INST', 'forgotten $WC ref'))
    # 4. an import the spec forgot
    done.append(_refused(['--spec', _spec_variant('nonear', drop('lp_nearpoint.py'))] + out('imp'), 'unresolved imports', 'forgotten lp_nearpoint import'))
    # 5. wrong built-binary pin
    def badpin(s):
        s['inst']['built'][0]['expect_md5'] = '0' * 32
    done.append(_refused(['--spec', _spec_variant('badpin', badpin)] + out('pin'), 'is not the tested', 'osoy_launch pin'))
    # 6. production changes while the build runs (hook appends a byte to the COPY after the copy step)
    done.append(_refused(['--glw', fg, '--selftest-mutate', 'engine_v373/pipeline_guard.py'] + out('mut'), 'changed DURING the build', 'mid-build change'))
    _fake_glw_restore(fg)
    # 7. the mutate hook never runs on production
    done.append(_refused(['--selftest-mutate', 'engine_v373/pipeline_guard.py'] + out('mutprod'), 'only allowed on a test copy', 'mutate on production'))
    # 8. Custom engine before T3a: a TEST copy of the real record marked pending (the real one says PASS once T3a passed)
    def pending(s):
        p = os.path.join(T, 'LATEST_pending.json')
        json.dump(dict(guard_record(), gpu_identity_proof='pending (S1.9 TEST RECORD -- not the real one)'), open(p, 'w'), indent=1)
        s['inst']['custom_engine']['latest'] = 'PROJ:' + os.path.relpath(p, PROJ).replace('\\', '/')
    done.append(_refused(['--spec', _spec_variant('custompending', pending), '--with-custom-engine'] + out('custom'),
                         'T3a has not passed', 'custom engine before T3a'))
    # 9. output outside build\s19
    done.append(_refused(['--out', os.path.join(PROJ, 'build', 's19_elsewhere')], 'must be under', 'out of tree'))
    check(not os.path.exists(os.path.join(PROJ, 'build', 's19_elsewhere')), 'out-of-tree dir created')
    # 10. zip_payloads on a tampered stage
    ts = os.path.join(T, 'stage_tamper')
    shutil.copytree(STAGE, ts)
    p = os.path.join(ts, 'inst', 'engine_v373', 'pipeline_guard.py')
    open(p, 'ab').write(b'#')
    rc, o, e, _ = run(PY + [os.path.join(TOOLS, 'zip_payloads.py'), '--stage', ts, '--out', os.path.join(T, 'payload_tamper'),
                            '--report', os.path.join(T, 'payload_tamper_report.json')])
    check(rc == 2 and 'differ' in e and not os.path.exists(os.path.join(T, 'payload_tamper')), 'tampered stage: exit %d %s' % (rc, e[-300:]))
    done.append('tampered stage (zip_payloads)')
    after = (L.md5_file(os.path.join(STAGE, 'stage_report.json')), L.md5_file(os.path.join(PAYLOAD, 'MANIFEST.json')))
    check(before == after, 'a refused run touched the published stage / payload')
    return '%d refusals: %s' % (len(done), '; '.join(done))


def _fake_glw_restore(fg):
    src = os.path.join(GLW, 'engine_v373', 'pipeline_guard.py')
    shutil.copyfile(src, os.path.join(fg, 'engine_v373', 'pipeline_guard.py'))


def t_extract_with_shell():
    """the BUILT exe extracts its embedded payload with the real shell code (sandbox H2EK root + per-user folder): the
    quick check and the full md5 of everything pass, the INST slot DLLs hold the sandbox WORK base, the extracted pwsh is
    7.6.6, the extracted py runs numpy 2.2.6 isolated, the extracted gpu_bake.ps1 is the tested one and parses with 0 errors"""
    unlink_alias()
    if os.path.exists(SANDBOX):
        L.safe_remove_tree(SANDBOX, os.path.dirname(SANDBOX))
    if os.path.exists(AUX):
        L.safe_remove_tree(AUX, os.environ['TEMP'])
    root, lad = os.path.join(SANDBOX, 'r'), os.path.join(AUX, 'lad')
    os.makedirs(os.path.join(root, 'tags'))
    os.makedirs(lad)
    work = os.path.join(root, 'GPU Lightmap Work')
    # 0.6.8 RC: the sandbox is no longer short (Temp - Deletable): a farms payload's slot is then the alias under AUX (checked below);
    # a payload WITHOUT farms would need a Direct slot (WORK <= 60 characters), which this location cannot give
    check(((manifest().get('features') or {}).get('farms') or {}).get('enabled') is True or len(work) + 1 <= 60,
          'a payload without farms needs a short sandbox WORK (Direct slot): %s -- set H2LM_S19_SANDBOX' % work)
    rc, o, e, s = run([EXE, '--cli', 'doctor', '--h2ek', root, '--localappdata', lad, '--alias-localappdata', os.path.join(AUX, 'ala'),
                       '--alias-volume-root', os.path.join(AUX, 'avr'), '--no-gpu', '--repair'], timeout=600)
    lines = o.splitlines()
    check(any('runtime quick check: ok' in x for x in lines), 'quick check: %s' % o[-1500:])
    check(any('repair (full md5 of everything): ok' in x for x in lines), 'full md5: %s' % o[-1500:])
    m = manifest()
    rtroot = os.path.join(lad, 'H2GpuLightmapper', 'rt')
    dirs = {c['name']: (os.path.join(rtroot, c['dirName']) if c['target'] == 'rt' else os.path.join(work, '_runtime', c['dirName'])) for c in m['components']}
    for c in m['components']:
        d = dirs[c['name']]
        check(os.path.isfile(os.path.join(d, '.complete')), '%s not extracted to %s' % (c['name'], d))
        for f in c['files']:
            p = os.path.join(d, *f['path'].split('/'))
            if c['target'] == 'inst' and 'dllslot' in f.get('role', ''):
                b = open(p, 'rb').read()
                # 0.6.8: with features.farms the slot names WORK\Temp (safe to delete) (Direct when it fits 60 characters, else the alias under
                # --alias-localappdata\H2GLM\<k6>); without it, WORK as before
                farms = ((m.get('features') or {}).get('farms') or {}).get('enabled') is True
                slotdir = os.path.join(work, 'Temp (safe to delete)') if farms else work
                base = (slotdir + '\\').encode('mbcs') if len(slotdir) + 1 <= 60 else (os.path.join(AUX, 'ala', 'H2GLM') + '\\').encode('mbcs')
                check(b.count(L.PRISTINE_SLOT) == 0 and b.count(base) == 2 and len(b) == f['size'], '%s slots (want %s)' % (f['path'], base))
            else:
                check(L.md5_file(p) == f['md5'], '%s/%s md5' % (c['name'], f['path']))
    inst = dirs['engine']
    check(L.md5_file(os.path.join(inst, 'engine_v373', 'gpu_bake.ps1')) == EXPECT_POST['engine_v373/gpu_bake.ps1'][1], 'gpu_bake')
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(('PYTHON', 'PSMODULEPATH', 'GPULM_', 'H2LM_', 'OSOYOOS_'))}
    pwsh = os.path.join(dirs['pwsh-7.6.6'], 'pwsh.exe')
    env2 = dict(env, H2LM_T1=os.path.join(inst, 'engine_v373', 'gpu_bake.ps1'), H2LM_T2=os.path.join(inst, '_research', 'ao', 'dev_AO', 'gpu_bake.ps1'))
    rc, o, e, _ = run([pwsh, '-NoProfile', '-NonInteractive', '-Command',
                       '$v=$PSVersionTable.PSVersion.ToString(); $n=0; foreach($f in $env:H2LM_T1,$env:H2LM_T2){$er=$null; '
                       '[void][System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$null,[ref]$er); $n+=$er.Count}; '
                       '"PWSH $v $PSHOME PARSEERR $n"'], env=env2, timeout=120)
    import re
    line = [re.match(r'^PWSH (\S+) (.*) PARSEERR (\d+)\s*$', x) for x in o.splitlines()]
    line = [x for x in line if x]
    check(rc == 0 and line, 'pwsh run: %d %s %s' % (rc, o[-300:], e[-300:]))
    ver, home, nerr = line[-1].groups()
    check(ver == '7.6.6' and os.path.normcase(home.rstrip('\\')) == os.path.normcase(dirs['pwsh-7.6.6']) and nerr == '0',
          'pwsh: %s' % line[-1].group(0))
    py = os.path.join(dirs['py310-np226'], 'py.exe')
    rc, o, e, _ = run([py, '-c', 'import sys,numpy;print("PY",sys.version.split()[0],numpy.__version__,sys.flags.isolated,sys.executable)'],
                      env=dict(env, PYTHONPATH=r'C:\bogus', PYTHONHOME=r'C:\bogus'), timeout=120)
    pl = [x for x in o.splitlines() if x.startswith('PY ')]
    check(rc == 0 and pl and pl[-1].split()[1:4] == ['3.10.5', '2.2.6', '1'] and os.path.normcase(pl[-1].split(' ', 4)[4]) == os.path.normcase(py),
          'py: %s %s' % (o[-300:], e[-300:]))
    mk = json.load(open(os.path.join(inst, '.complete'), encoding='utf-8'))
    # 0.6.8: a farms payload names WORK\Temp (safe to delete) in the slot; on this sandbox (79 characters) that is the alias under
    # --alias-localappdata (outside Temp and WORK), the ONLY link the shell may make; without farms: WORK, Direct, no link (as before)
    farms = ((m.get('features') or {}).get('farms') or {}).get('enabled') is True
    tempdir = os.path.join(work, 'Temp (safe to delete)')
    rp = [os.path.join(dp, n) for top in (SANDBOX, AUX) for dp, dns, fns in os.walk(top) for n in dns + fns if L.is_reparse(os.path.join(dp, n))]
    if farms and len(tempdir) + 1 > 60:
        ala = os.path.join(AUX, 'ala', 'H2GLM')
        check(mk.get('slotKind') == 'AliasLocalAppData' and (mk.get('slotBase') or '').lower().startswith(ala.lower() + '\\'), 'INST marker %s' % mk)
        check(len(rp) == 1 and os.path.dirname(rp[0]).lower() == ala.lower() and os.path.normcase(os.path.realpath(rp[0])) == os.path.normcase(tempdir),
              'the only link is the slot alias -> the sandbox Temp: %s' % rp)
    else:
        check(mk.get('slotBase') == (tempdir if farms else work) + '\\' and mk.get('slotKind') == 'Direct', 'INST marker %s' % mk)
        check(not rp, 'sandbox holds reparse points: %s' % rp)
    return 'extracted %d components (%.1f s doctor); pwsh 7.6.6, py 3.10.5 + numpy 2.2.6 isolated; slot base %s; gpu_bake x2 parse 0 errors' % (
        len(m['components']), s, work + '\\')


def t_dry_run_h2ekmine():
    """the built exe plans a real bake on ProdRoot from its EMBEDDED manifest, read-only: the gpu-bake step runs the payload's
    pwsh on INST\\engine_v373\\gpu_bake.ps1 (WORK\\_runtime\\<appVersion>-<h8>); nothing is written to WORK"""
    work = os.path.join(H2ROOT, 'GPU Lightmap Work')
    # V5 (2026-10-14): since the T2 / T3 / T4 validation runs the exe has its own WORK\_runtime and WORK\standalone on ProdRoot
    # (by design), so "absent" can no longer be the precondition: the dry run must leave WORK's top level AND both exe trees
    # exactly as they were (every path, size and mtime)
    def deep(d):
        out = []
        for dp, dns, fns in os.walk(d):
            dns[:] = [n for n in dns if not L.is_reparse(os.path.join(dp, n))]
            for n in fns:
                st = os.stat(os.path.join(dp, n))
                out.append((os.path.relpath(os.path.join(dp, n), d), st.st_size, st.st_mtime_ns))
        return sorted(out)
    snap = lambda: (sorted(os.listdir(work)), deep(os.path.join(work, '_runtime')), deep(os.path.join(work, 'standalone')))
    before = snap()
    had_runtime = os.path.exists(os.path.join(work, '_runtime'))
    lad = os.path.join(T, 'lad_dry')
    os.makedirs(lad, exist_ok=True)
    rc, o, e, _ = run([EXE, '--cli', 'bake', '--h2ek', H2ROOT, '--scenario', r'scenarios\multi\cyclotron\cyclotron', '--bsp', 'cyclotron',
                       '--mode', 'lightmap', '--quality', 'super', '--dry-run', '--json', '--localappdata', lad], timeout=300)
    check(rc == 0, 'dry run exit %d: %s %s' % (rc, o[-500:], e[-500:]))
    plan = json.loads(o[o.index('{'):o.rindex('}') + 1])
    m = manifest()
    inst = inst_component(m)
    pw = [c for c in m['components'] if c['name'] == 'pwsh-7.6.6'][0]
    check(str(plan.get('payload', '')).startswith('embedded'), 'payload %s' % plan.get('payload'))
    check(os.path.normcase(plan['inst']) == os.path.normcase(os.path.join(work, '_runtime', inst['dirName'])), 'inst %s' % plan['inst'])
    gb = [s for s in plan['steps'] if s['id'] == 'gpu-bake'][0]
    check(os.path.normcase(gb['exe']) == os.path.normcase(os.path.join(lad, 'H2GpuLightmapper', 'rt', pw['dirName'], 'pwsh.exe')), 'exe %s' % gb['exe'])
    check(os.path.normcase(os.path.join(plan['inst'], 'engine_v373', 'gpu_bake.ps1')) in [os.path.normcase(a) for a in gb['args']], 'args %s' % gb['args'])
    check(snap() == before and os.path.exists(os.path.join(work, '_runtime')) == had_runtime, 'the dry run wrote into production WORK')
    return 'gpu-bake: %s ... %s (INST %s), WORK untouched (top level + %d _runtime / %d standalone files unchanged)' % (
        os.path.basename(gb['exe']), ' '.join(gb['args'][4:10]), inst['dirName'], len(before[1]), len(before[2]))


def t_custom_engine_gate():
    """--with-custom-engine stages engine_custom/ = engine_v373 + the N3 guard exe ONLY with a T3a PASS record; exercised
    with a TEST record (a copy of LATEST.json marked PASS, never the real one) in build\\s19\\t -- the payload is not built"""
    real = guard_record()
    gsrc = os.path.join(os.path.dirname(guard_record_path()), *real['guard_fullgpu'].split(chr(92)))
    prod_fg = L.md5_file(os.path.join(L.resolve_glw(L.load_spec()['roots']['GLW']), 'engine_v373', 'v041-fullgpu', 'fullgpu.exe'))
    if real.get('production_fullgpu_md5') != prod_fg:
        # 0.6.8 RC: a promoted fullgpu (dev_XC 3f5acd1b) has no N3 guard build yet (native\fullgpu_guard + gate T3a): nothing to stage
        return 'SKIPPED: the guard record names production fullgpu %s, production is %s now (rebuild the guard, re-run T3a)' % (str(real.get('production_fullgpu_md5'))[:8], prod_fg[:8])
    if not os.path.isfile(gsrc):
        return 'SKIPPED: no guard exe at %s' % gsrc
    fg = os.path.join(T, 'fg')
    os.makedirs(os.path.join(fg, 'guard'), exist_ok=True)
    shutil.copyfile(gsrc, os.path.join(fg, 'guard', 'fullgpu.exe'))
    lt = dict(real, gpu_identity_proof='PASS (S1.9 TEST RECORD -- not a real T3a result)', guard_fullgpu='guard\\fullgpu.exe')
    rtsrc = os.path.join(os.path.dirname(guard_record_path()), *real['guard_rt_fullgpu'].split(chr(92))) if real.get('guard_rt_fullgpu') else None
    if rtsrc and os.path.isfile(rtsrc):
        os.makedirs(os.path.join(fg, 'guard_rt'), exist_ok=True)
        shutil.copyfile(rtsrc, os.path.join(fg, 'guard_rt', 'fullgpu_rt.exe'))
        lt.update(guard_rt_fullgpu='guard_rt\\fullgpu_rt.exe', gpu_identity_proof_rt='PASS (S1.9 TEST RECORD -- not a real T3a result)')
    else:
        rtsrc = None
    json.dump(lt, open(os.path.join(fg, 'LATEST.json'), 'w'), indent=1)
    sp = _spec_variant('custom', lambda s: s['inst']['custom_engine'].__setitem__('latest', 'PROJ:' + os.path.relpath(os.path.join(fg, 'LATEST.json'), PROJ).replace('\\', '/')))
    so = os.path.join(T, 'stage_custom')
    rc, o, e, _ = run(PY + [os.path.join(TOOLS, 'stage_engine.py'), '--spec', sp, '--with-custom-engine', '--out', so])
    check(rc == 0, 'custom stage exit %d: %s' % (rc, e[-600:]))
    rep = json.load(open(os.path.join(so, 'stage_report.json')))
    files = {r['path']: r for r in rep['files']}
    ce = {p: r for p, r in files.items() if p.startswith('engine_custom/')}
    ev = {p: r for p, r in files.items() if p.startswith('engine_v373/')}
    check(len(ce) == len(ev) - (0 if rtsrc else 1), 'engine_custom has %d files, engine_v373 %d (RT guard %s)' % (len(ce), len(ev), bool(rtsrc)))
    for p, r in ce.items():
        twin = ev['engine_v373/' + p[len('engine_custom/'):]]
        if p.endswith('v041-fullgpu/fullgpu_rt.exe'):
            check(r['md5'] == real['guard_rt_md5'] and r['role'] == 'built:N3rt' and r['sourceMd5'] == twin['md5'], 'RT guard %s' % r)
        elif p.endswith('v041-fullgpu/fullgpu.exe'):
            check(r['md5'] == real['guard_md5'] and r['role'] == 'built:N3' and r['sourceMd5'] == twin['md5'], 'guard fullgpu %s' % r)
        else:
            check(r['md5'] == twin['md5'], '%s differs from engine_v373' % p)
    slots = [p for p, r in files.items() if r['role'] == 'dllslot']
    check(len(slots) == 6, 'slots %s' % slots)
    check(rep['custom_engine']['shipped'] is True, 'report')
    if rtsrc:
        # without the RT proof record the RT host is simply not shipped in engine_custom (the guarded D3D11 fullgpu.exe runs)
        json.dump({k: v for k, v in lt.items() if k != 'gpu_identity_proof_rt'}, open(os.path.join(fg, 'LATEST.json'), 'w'), indent=1)
        so3 = os.path.join(T, 'stage_custom3')
        rc3, o3, e3, _ = run(PY + [os.path.join(TOOLS, 'stage_engine.py'), '--spec', sp, '--with-custom-engine', '--out', so3])
        check(rc3 == 0, 'custom stage without the RT record exit %d: %s' % (rc3, e3[-400:]))
        f3 = {r['path'] for r in json.load(open(os.path.join(so3, 'stage_report.json')))['files']}
        check('engine_custom/v041-fullgpu/fullgpu_rt.exe' not in f3, 'the plain RT exe must never reach engine_custom')
        json.dump(lt, open(os.path.join(fg, 'LATEST.json'), 'w'), indent=1)
    # a guard built against another production fullgpu is refused
    json.dump(dict(lt, production_fullgpu_md5='0' * 32), open(os.path.join(fg, 'LATEST.json'), 'w'), indent=1)
    _refused(['--spec', sp, '--with-custom-engine', '--out', os.path.join(T, 'stage_custom2')], 'the guard was built against', 'stale guard')
    return 'engine_custom = engine_v373 (%d files) + guard %s; 6 slot DLLs; a stale guard is refused' % (len(ce), real['guard_md5'][:8])


def t_ao_driver_defaults():
    """A1 (2026-10-15): the shipped AO driver carries the approved recipe's defaults -- the shell passes neither --rays nor --seam-stack-eps, so
    these ARE the exe's AO settings (trace 1024 rays, seam filter with the stacked-layer rule at 0.004 wu) -- and the three modules it imports
    are scipy-free (N4)"""
    ao = os.path.join(STAGE, 'inst', '_research', 'ao')
    g = open(os.path.join(ao, 'gpu_ao.py'), 'rb').read().decode('latin-1')
    check("p.add_argument('--rays', type=int, default=1024," in g, 'trace --rays default is not 1024')
    check("p.add_argument('--seam-stack-eps', type=float, default=0.004," in g, 'apply --seam-stack-eps default is not 0.004')
    check("'--tool', os.path.join(H2, 'tool_fast.exe')" in g and "os.environ.get('H2LM_ROOT')" in g and "cmd += ['-Quality', a.quality]" in g, 'P6 edits missing')
    for f in ('ao_seamfix.py', 'ao_denoise.py', 'ao_ballgrid.py'):
        t = open(os.path.join(ao, f), 'rb').read().decode('latin-1')
        import ast as _ast
        tree = _ast.parse(t)
        bad = [n for n in _ast.walk(tree) if (isinstance(n, _ast.Import) and any(a.name.split('.')[0] == 'scipy' for a in n.names))
               or (isinstance(n, _ast.ImportFrom) and (n.module or '').split('.')[0] == 'scipy')
               or (isinstance(n, _ast.Call) and getattr(n.func, 'id', getattr(n.func, 'attr', '')) == 'cKDTree')]
        check(not bad, '%s imports scipy / builds a cKDTree (line %s)' % (f, [getattr(n, 'lineno', '?') for n in bad]))
    return 'trace --rays 1024, apply --seam-stack-eps 0.004, P6 edits present; ao_seamfix / ao_denoise / ao_ballgrid scipy-free'


def t_no_bytecode_in_production():
    """the builder never imports production code: no __pycache__ appeared in the production code dirs it read"""
    dirs = [os.path.join(GLW, d) for d in ('engine_v373', os.path.join('engine_v373', 'v043-tagwriter'), os.path.join('_research', 'ao'),
                                           os.path.join('_research', 'ao', 'dev_AO'), os.path.join('_research', 'ao', 'dev_AO2'), os.path.join('_research', 'ao', 'dev_AO4'), os.path.join('_research', 'ao', 'dev_AO5'),
                                           os.path.join('_research', 'lightprobes'))]
    new = []
    for d in dirs:
        pc = os.path.join(d, '__pycache__')
        if os.path.isdir(pc) and os.path.getmtime(pc) > START - 5:
            new.append(pc)
    check(not new, 'new __pycache__: %s' % new)
    return 'none'


def unlink_alias():
    """the DLL-slot alias junction AUX\\ala\\H2GLM\\<k6> (-> the sandbox's Temp): unlink the LINK only (RemoveDirectory on the junction, never
    followed) when it points inside this test's sandbox; anything else is left for safe_remove_tree to refuse"""
    ala = os.path.join(AUX, 'ala', 'H2GLM')
    if os.path.isdir(ala):
        for k in os.listdir(ala):
            j = os.path.join(ala, k)
            if os.lstat(j).st_file_attributes & 0x400 and os.path.realpath(j).lower().startswith(SANDBOX.lower()):
                os.rmdir(j)


def t_tool_args_quoted():
    """0.6.8 RC (P17): every gpu_bake.ps1 the payload ships builds the four tool command lines with the scenario and the BSP quoted (a name with
    spaces reaches tool_fast as ONE argument), and none keeps an unquoted form"""
    q = '`"'
    want = ['$a = "lightmaps ' + q + '$($o.ScenArg)' + q + ' ' + q + '$Bsp' + q + ' low',
            '$a = "lightmaps-farm-worker ' + q + '$Scenario' + q + ' ' + q + '$Bsp' + q + ' $capQ',
            '$aFullOverride = "lightmaps ' + q + '$($ovo.ScenArg)' + q + ' ' + q + '$Bsp' + q + ' low',
            '$aFull = "lightmaps ' + q + '$Scenario' + q + ' ' + q + '$Bsp' + q + ' low']
    old = ['$a = "lightmaps $($o.ScenArg) $Bsp', '$a = "lightmaps-farm-worker $Scenario $Bsp', '$aFullOverride = "lightmaps $($ovo.ScenArg) $Bsp', '$aFull = "lightmaps $Scenario $Bsp']
    n = 0
    for rel in ('engine_v373/gpu_bake.ps1', 'engine_custom/gpu_bake.ps1', '_research/ao/dev_AO/gpu_bake.ps1'):
        p = os.path.join(STAGE, 'inst', *rel.split('/'))
        if not os.path.isfile(p):
            continue
        t = open(p, encoding='utf-8', errors='replace').read()
        for w in want:
            check(t.count(w) == 1, '%s lacks the quoted tool line %s' % (rel, w))
        for o in old:
            check(o not in t, '%s still has the unquoted tool line %s' % (rel, o))
        n += 1
    check(n >= 2, 'no gpu_bake.ps1 found in the stage')
    return '%d gpu_bake.ps1: the four tool command lines quote the scenario and the BSP' % n


def t_no_inject_exe():
    """0.6.8 RC: the tool loader is H2GpuLightmapper.ToolLoader.exe (P16): no shipped file is NAMED inject.exe and no shipped file names it as
    a PATH (v014-inject-estimator\\inject.exe, either slash, ASCII or UTF-16); the loader is shipped, md5 = its build record. Comments / log
    texts that only mention the old name in prose are not paths and are allowed."""
    import re
    import zipfile
    pat = re.compile(rb'inject-estimator[\\/]+inject\.exe', re.I)
    pat16 = re.compile(re.escape('inject-estimator').encode('utf-16-le') + rb'(?:\\\x00|/\x00)+' + re.escape('inject.exe').encode('utf-16-le'), re.I)
    named, refs, n = [], [], 0
    for z in sorted(os.listdir(PAYLOAD)):
        if not z.endswith('.zip'):
            continue
        with zipfile.ZipFile(os.path.join(PAYLOAD, z)) as zf:
            for i in zf.infolist():
                n += 1
                if i.filename.replace('\\', '/').split('/')[-1].lower() == 'inject.exe':
                    named.append('%s!%s' % (z, i.filename))
                data = zf.read(i)
                if pat.search(data) or pat16.search(data):
                    refs.append('%s!%s' % (z, i.filename))
    for f in (EXE, os.path.join(PAYLOAD, 'MANIFEST.json')):
        data = open(f, 'rb').read()
        n += 1
        if pat.search(data) or pat16.search(data):
            refs.append(os.path.basename(f))
    check(not named, 'shipped files named inject.exe: %s' % named)
    check(not refs, 'shipped files that reference v014-inject-estimator\\inject.exe: %s' % refs[:10])
    m = manifest()
    inst = [c for c in m['components'] if c['target'] == 'inst'][0]
    tl = [f for f in inst['files'] if f['path'] == 'v014-inject-estimator/H2GpuLightmapper.ToolLoader.exe']
    rec = json.load(open(os.path.join(L.PROJ, 'build', 'toolloader', 'build_toolloader.json'), encoding='utf-8'))['toolloader']
    check(len(tl) == 1 and tl[0]['md5'] == rec['md5'], 'the tool loader is shipped with its build md5 %s: %s' % (rec['md5'], tl))
    return '%d files / zip members: none named inject.exe, none referencing its path; ToolLoader %s shipped' % (n, rec['md5'][:8])


TESTS = [('manifest_and_zips', t_manifest_and_zips), ('inst_equals_production', t_inst_equals_production), ('roles_layout', t_roles_layout),
         ('refusals', t_refusals), ('extract_with_shell', t_extract_with_shell), ('dry_run_h2ekmine', t_dry_run_h2ekmine),
         ('custom_engine_gate', t_custom_engine_gate), ('ao_driver_defaults', t_ao_driver_defaults), ('determinism', t_determinism),
         ('no_bytecode_in_production', t_no_bytecode_in_production), ('no_inject_exe', t_no_inject_exe), ('tool_args_quoted', t_tool_args_quoted)]
START = time.time()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--only')
    ap.add_argument('--keep', action='store_true')
    a = ap.parse_args()
    L.below_normal()
    for f in (os.path.join(PAYLOAD, 'MANIFEST.json'), os.path.join(STAGE, 'stage_report.json'), EXE):
        if not os.path.isfile(f):
            print('SETUP REFUSED: %s missing -- run tools\\payload\\build_payload.py first' % f)
            return 2
    if os.path.exists(T):
        L.safe_remove_tree(T, B)
    os.makedirs(T)
    only = set(a.only.split(',')) if a.only else None
    for name, fn in TESTS:
        if only and name not in only:
            continue
        t0 = time.time()
        try:
            detail = fn()
            RESULTS.append({'test': name, 'ok': True, 'detail': detail, 'seconds': round(time.time() - t0, 1)})
            print('PASS %-26s %5.1fs  %s' % (name, time.time() - t0, detail))
        except Exception as e:
            RESULTS.append({'test': name, 'ok': False, 'detail': '%s: %s' % (type(e).__name__, e), 'seconds': round(time.time() - t0, 1),
                            'trace': traceback.format_exc()[-2000:]})
            print('FAIL %-26s %5.1fs  %s: %s' % (name, time.time() - t0, type(e).__name__, str(e)[:1500]))
    if not a.keep:
        unlink_alias()
        for d, under in ((T, B), (SANDBOX, os.path.dirname(SANDBOX)), (AUX, os.environ['TEMP'])):
            try:
                if os.path.exists(d):
                    L.safe_remove_tree(d, under)
            except L.Refused as e:
                print('cleanup: %s' % e)
    npass = sum(r['ok'] for r in RESULTS)
    os.makedirs(os.path.join(HERE, 'results'), exist_ok=True)
    json.dump({'when': time.strftime('%Y-%m-%dT%H:%M:%S'), 'passed': npass, 'failed': len(RESULTS) - npass, 'results': RESULTS,
               'build': json.load(open(os.path.join(B, 'BUILD.json')))}, open(os.path.join(HERE, 'results', 'last_run.json'), 'w'), indent=1)
    print('S1.9 TESTS: %d passed, %d failed (%.0f s)' % (npass, len(RESULTS) - npass, time.time() - START))
    return 0 if npass == len(RESULTS) else 1


if __name__ == '__main__':
    sys.exit(main())
