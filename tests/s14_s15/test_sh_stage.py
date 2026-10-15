"""test_sh_stage.py -- S1.5 CPU-only tests of src\\standalone\\sh_stage.py on the triplicate lightprobe run dirs. No GPU.

  py -B test_sh_stage.py [--glw "G:\\...\\GPU Lightmap Work"] [--work DIR] [--keep]

Inputs are READ ONLY (every file is copied into WORK first; nothing under GPU Lightmap Work or any tags tree is written):
  farm_w84\\tagw_stage (+ report)  <-  _research\\lightprobes\\e2e_B_probes_w84_final\\<ltmp> (SH written into it), probes.bin, floats\\
  farm_w82\\tagw_stage (+ report)  <-  e2e_B_probes_w82\\<ltmp>, e2e_B2_probes_w82\\<ltmp>
  the live triplicate ltmp + its two lightmap bitmaps (copied; the target of the probes-only round trip is a SCRATCH folder)
Tests:
  merge accepts the real SH (3 pairs); the merged stage passes lmw_install (production, unmodified) into a SCRATCH target and
  restores; merge REFUSES hand-flipped non-SH bytes (same environment's other fields, a cluster float, a data blob, the file
  header, a block count), a cross-run SH, a bad CRC, a size change, a NaN SH, a wrong point count, a second merge, a farm-dir
  stage, a stage under ROOT\\tags -- each leaving the stage byte-identical; an SH-byte flip is ACCEPTED (positive control);
  single builds a one-ltmp stage from the w84 pair and from lp_write_sh on a COPY of the live ltmp, and each installs into a
  scratch target changing ONLY the ltmp, then restores.
Last line: RESULT: PASS n/n | FAIL ..."""
import argparse, hashlib, json, os, shutil, struct, subprocess, sys, tempfile, time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, '..', '..'))
SH_STAGE = os.path.join(PROJ, 'src', 'standalone', 'sh_stage.py')
GLW_DEFAULT = os.environ.get('H2LM_TEST_GLW', '')   # <H2EK_ROOT>\GPU Lightmap Work (production, read only)
LT = 'triplicate_triplicate_lightmap.scenario_structure_lightmap'
BM = ('triplicate_triplicate_lightmap_bitmaps.bitmap', 'triplicate_triplicate_lightmap_truecolor_bitmaps.bitmap')
RESULTS = []


def check(name, ok, detail=''):
    RESULTS.append((name, bool(ok), detail))
    print('%s  %s%s' % ('PASS' if ok else 'FAIL', name, ('  -- ' + str(detail)[:900]) if detail and not ok else ''), flush=True)
    return ok


def md5f(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 22), b''):
            h.update(c)
    return h.hexdigest()


def env():
    e = {k: v for k, v in os.environ.items()
         if not k.upper().startswith(('GPULM_', 'H2LM_', 'OSOYOOS_')) and k.upper() not in ('PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP')}
    e['PYTHONDONTWRITEBYTECODE'] = '1'
    return e


def run(args, cwd):
    p = subprocess.run([sys.executable, '-B'] + args, cwd=cwd, env=env(), stdin=subprocess.DEVNULL, capture_output=True, text=True,
                       creationflags=0x08000000 | 0x00004000)
    return p.returncode, p.stdout + p.stderr


def copy_files(src_dir, names, dst_dir):
    os.makedirs(dst_dir, exist_ok=True)
    for n in names:
        shutil.copyfile(os.path.join(src_dir, n), os.path.join(dst_dir, n))


def safe_rmtree(d):
    """remove OUR temp dir only if nothing inside is a reparse point (junction/symlink); otherwise keep it and say so."""
    for root, dirs, files in os.walk(d):
        for n in dirs + files:
            p = os.path.join(root, n)
            if os.path.islink(p) or (getattr(os.lstat(p), 'st_file_attributes', 0) & 0x400):
                print('NOT removing %s: %s is a reparse point' % (d, p))
                return
    shutil.rmtree(d)


def snapshot(d):
    return {n: md5f(os.path.join(d, n)) for n in sorted(os.listdir(d)) if os.path.isfile(os.path.join(d, n))}, sorted(os.listdir(d))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--glw', default=GLW_DEFAULT)
    ap.add_argument('--work', default=None)
    ap.add_argument('--keep', action='store_true')
    a = ap.parse_args()
    GLW = a.glw
    H2 = os.path.abspath(os.path.join(GLW, '..'))
    TAGIO = os.path.join(GLW, 'engine_v373', 'v043-tagwriter')
    INSTALL = os.path.join(TAGIO, 'lmw_install.py')
    LP = os.path.join(GLW, '_research', 'lightprobes')
    LIVE_DIR = os.path.join(H2, 'tags', 'scenarios', 'multi', 'triplicate')
    work = os.path.abspath(a.work) if a.work else tempfile.mkdtemp(prefix='s15_sh_stage_')
    if os.path.normcase(work).startswith(os.path.normcase(H2) + os.sep):
        sys.exit('REFUSED: --work must not be inside %s' % H2)
    if len(work) > 90:   # lmw_install's auto-backup path adds ~130 chars; Windows MAX_PATH (260) applies without LongPathsEnabled
        sys.exit('REFUSED: --work %s is %d chars; use one of <= 90 (the default temp dir is fine)' % (work, len(work)))
    os.makedirs(work, exist_ok=True)
    print('work dir: %s' % work)
    sys.path.insert(0, TAGIO)
    import lmw_tagio as T                                   # read-only use (dont_write_bytecode is set)
    pyc = os.path.join(TAGIO, '__pycache__')
    pyc_before = os.path.exists(pyc)

    # ---------------------------------------------------------------- preconditions: the read-only inputs are what they were
    stages = {}
    for w in (84, 82):
        sd = os.path.join(GLW, 'farm_w%d' % w, 'tagw_stage')
        rep = json.load(open(os.path.join(sd, 'tagw_report.json')))
        ok = rep.get('status') == 'written' and all(md5f(os.path.join(sd, n)) == o['md5'] for n, o in rep['outputs'].items())
        check('precondition: farm_w%d\\tagw_stage matches its report (read only)' % w, ok)
        stages[w] = (sd, sorted(rep['outputs']) + ['tagw_report.json'])
    SH = {'w84': os.path.join(LP, 'e2e_B_probes_w84_final', LT), 'w82': os.path.join(LP, 'e2e_B_probes_w82', LT),
          'w82b': os.path.join(LP, 'e2e_B2_probes_w82', LT)}
    PROBES = {k: os.path.join(os.path.dirname(v), 'probes.bin') for k, v in SH.items()}
    for k, p in PROBES.items():
        b = open(p, 'rb').read()
        check('precondition: %s probes.bin = 11 points' % k, struct.unpack_from('<I', b)[0] == 11 and len(b) == 136)
    # local copies of every input
    inp = os.path.join(work, 'inputs'); os.makedirs(inp, exist_ok=True)
    shc = {}
    for k, p in SH.items():
        shc[k] = os.path.join(inp, 'sh_%s.ltmp' % k); shutil.copyfile(p, shc[k])
        shutil.copyfile(PROBES[k], os.path.join(inp, 'probes_%s.bin' % k))
    probes = os.path.join(inp, 'probes_w84.bin')

    def fresh_stage(w, name):
        d = os.path.join(work, name, 'final_stage')
        copy_files(stages[w][0], stages[w][1], d)
        return d

    def sh(*args):
        return run([SH_STAGE] + list(args) + ['--tagio', TAGIO], cwd=work)

    # ---------------------------------------------------------------- merge accepts the real SH
    merged = {}
    for k, w in (('w84', 84), ('w82', 82), ('w82b', 82)):
        st = fresh_stage(w, 'merge_%s' % k)
        before = json.load(open(os.path.join(st, 'tagw_report.json')))['outputs']
        t0 = time.time()
        rc, out = sh('merge', '--stage', st, '--sh', shc[k], '--probes', os.path.join(inp, 'probes_%s.bin' % k))
        dt = time.time() - t0
        rep = json.load(open(os.path.join(st, 'tagw_report.json')))
        rec = json.load(open(os.path.join(st, 'sh_merge.json'))) if os.path.exists(os.path.join(st, 'sh_merge.json')) else {}
        ok = (rc == 0 and out.strip().splitlines()[-1].startswith('SH_STAGE: OK merge') and md5f(os.path.join(st, LT)) == md5f(shc[k])
              and rep['outputs'][LT]['md5'] == md5f(shc[k]) and rep['outputs'][LT]['size'] == os.path.getsize(shc[k])
              and os.path.normcase(rep['outputs'][LT]['path']) == os.path.normcase(os.path.join(st, LT))
              and all(rep['outputs'][n] == before[n] for n in before if n != LT) and rep['status'] == 'written')
        check('merge %s: accepts the real SH (exit 0 in %.1f s), stage ltmp = SH ltmp, report re-stamped, bitmaps untouched' % (k, dt), ok, out)
        g = rec.get('gate', {})
        # probes 0 and 10 compose to an all-zero cube (lp_compose mean-lum scale 0 -- the CPU's live tag has zero SH there too),
        # so lp_write_sh's zeros equal the skeleton's: 9 environments change, 0 and 10 stay zero (reported, not refused)
        check('merge %s: sh_merge.json: 11 envs == 11 points, 1173 differing bytes, envs 1-9 changed, 0/10 all-zero, CRC changed' % k,
              g.get('lighting_environments') == 11 and g.get('points') == 11 and g.get('diff_bytes') == 1173
              and g.get('envs_changed') == list(range(1, 10)) and g.get('crc_changed') is True and g.get('envs_all_zero_sh') == [0, 10], g)
        bk = os.path.join(st, '_sh_merge_backup')
        check('merge %s: the pre-merge ltmp + report are backed up byte-exact' % k,
              md5f(os.path.join(bk, LT)) == before[LT]['md5'] and md5f(os.path.join(bk, 'tagw_report.json')) == md5f(os.path.join(stages[w][0], 'tagw_report.json')))
        merged[k] = st
    rc, out = sh('merge', '--stage', merged['w84'], '--sh', shc['w84'], '--probes', probes)
    check('merge refuses a second merge into the same stage (exit 2)', rc == 2 and 'already merged' in out, out)

    # ---------------------------------------------------------------- lmw_install (production, unmodified) on the merged stage -> SCRATCH target
    def install_roundtrip(stage, label, expect_changed):
        tgt = os.path.join(work, 'target_%s' % label)
        os.makedirs(tgt)
        names = [LT] + list(BM)
        copy_files(LIVE_DIR, names, tgt)                     # copies of the live set: the scratch "tags folder"
        for root in (H2,):
            assert not os.path.normcase(os.path.realpath(tgt)).startswith(os.path.normcase(os.path.realpath(os.path.join(root, 'tags'))))
        pre, _ = snapshot(tgt)
        bak = os.path.join(work, 'bak_%s' % label); auto = os.path.join(work, 'autobak_%s' % label); mk = os.path.join(work, 'marker_%s.txt' % label)
        rc0, out0 = run([INSTALL, 'install', '--stage', stage, '--target-dir', tgt, '--backup-dir', bak, '--marker', mk,
                         '--auto-backup-root', auto, '--dry-run'], cwd=work)
        mid0, _ = snapshot(tgt)
        rc1, out1 = run([INSTALL, 'install', '--stage', stage, '--target-dir', tgt, '--backup-dir', bak, '--marker', mk,
                         '--auto-backup-root', auto], cwd=work)
        rep = json.load(open(os.path.join(stage, 'tagw_report.json')))
        post, _ = snapshot(tgt)
        changed = sorted(n for n in pre if post.get(n) != pre[n])
        ok1 = (rc0 == 0 and mid0 == pre and rc1 == 0 and not os.path.exists(mk) and changed == sorted(expect_changed)
               and all(post[n] == o['md5'] for n, o in rep['outputs'].items()))
        check('lmw_install (unmodified) installs the %s stage into a SCRATCH target: exit 0, changed exactly %s, md5 = report' % (label, expect_changed),
              ok1, (rc0, rc1, changed, out1))
        rc2, out2 = run([INSTALL, 'restore', '--backup-dir', bak, '--target-dir', tgt, '--marker', mk], cwd=work)
        back = {n: md5f(os.path.join(tgt, n)) for n in pre}
        check('lmw_install restore (%s): exit 0, the scratch target is back to its pre-install md5s' % label, rc2 == 0 and back == pre, (rc2, out2))

    install_roundtrip(merged['w84'], 'merged_w84', [LT] + list(BM))

    # ---------------------------------------------------------------- refusals: hand-flipped non-SH bytes etc.
    base = open(shc['w84'], 'rb').read()
    W = T.Walk(base, 'ltmp', keep_fields=True)
    F = {T.npath(p): (o, t, s) for p, o, t, s in W.fields}
    env3 = 'root[0].lightmap groups[0].lighting environments[3].'
    cluster_float = next(o for p, o, t, s in W.fields if 'clusters' in p and t == 0x13 and 'lighting environments' not in p)
    data_blob = next((pos + sz // 2) for p, pos, sz in W.datas if sz > 64)
    blk = next(b for b in W.blocks if b.count > 0 and b.path != 'root' and 'lighting environments' not in b.path)

    def mut(off, xor=0x01, crc=True, b=None):
        d = bytearray(base if b is None else b); d[off] ^= xor
        return T.set_crc(bytes(d)) if crc else bytes(d)

    nan_b = bytearray(base); struct.pack_into('<f', nan_b, F[env3.replace('[3]', '[2]') + 'red coefficient[0]'][0], float('nan'))
    cases = [
        ('env 3 sample_point (a non-SH field of an environment)', mut(F[env3 + 'sample_point'][0] + 1), ['--probes', probes], 'sample_point'),
        ('env 3 flags', mut(F[env3 + 'flags'][0]), ['--probes', probes], 'flags'),
        ('env 3 specular bitmap index', mut(F[env3 + 'specular bitmap index'][0]), ['--probes', probes], 'specular bitmap index'),
        ('env 3 color tint', mut(F[env3 + 'color tint'][0] + 2), ['--probes', probes], 'color tint'),
        ('a cluster float at 0x%X' % cluster_float, mut(cluster_float + 1), ['--probes', probes], 'clusters'),
        ('a data blob byte at 0x%X' % data_blob, mut(data_blob), ['--probes', probes], 'data of'),
        ('a file header byte (+0x10, outside the CRC word)', mut(0x10, crc=False), ['--probes', probes], 'file header'),
        ('an SH byte flipped WITHOUT re-stamping the CRC', mut(F[env3 + 'red coefficient[4]'][0], crc=False), ['--probes', probes], 'CRC'),
        ('a NaN SH coefficient (CRC re-stamped)', T.set_crc(bytes(nan_b)), ['--probes', probes], 'non-finite'),
        ('a truncated SH ltmp', base[:-4], ['--probes', probes], 'bytes'),
        ('the SH of another run (w82 into the w84 stage)', open(shc['w82'], 'rb').read(), ['--probes', probes], 'outside the SH fields'),
        ('10 points vs 11 environments', base, ['--points', '10'], 'k == points'),
        ('both --probes and --points', base, ['--probes', probes, '--points', '11'], 'exactly one'),
    ]
    blkcnt = bytearray(base); blkcnt[blk.hdr + 4] ^= 0x01        # the block header's field-set version word
    cases.append(('a block header word (%s)' % blk.path, T.set_crc(bytes(blkcnt)), ['--probes', probes], ''))
    for i, (name, data, pts, why) in enumerate(cases):
        st = fresh_stage(84, 'refuse_%02d' % i)
        snap0 = snapshot(st)
        bad = os.path.join(inp, 'bad_%02d.ltmp' % i); open(bad, 'wb').write(data)
        rc, out = sh('merge', '--stage', st, '--sh', bad, *pts)
        last = out.strip().splitlines()[-1] if out.strip() else ''
        check('merge REFUSES %s (exit 2, names %r, stage byte-identical)' % (name, why or 'the walk'),
              rc == 2 and last.startswith('SH_STAGE: REFUSED') and why in last and snapshot(st) == snap0, out)
    # positive control: an SH coefficient flipped (CRC re-stamped) is accepted
    st = fresh_stage(84, 'accept_shflip')
    good = os.path.join(inp, 'good_shflip.ltmp'); open(good, 'wb').write(mut(F[env3 + 'green coefficient[7]'][0] + 1))
    rc, out = sh('merge', '--stage', st, '--sh', good, '--probes', probes)
    check('merge ACCEPTS an SH-coefficient byte flip with the CRC re-stamped (positive control)', rc == 0 and md5f(os.path.join(st, LT)) == md5f(good), out)
    # guards: a farm-named dir and a stage under ROOT\tags (copies, never the real farm)
    st = os.path.join(work, 'guard', 'farm_w84', 'tagw_stage'); copy_files(stages[84][0], stages[84][1], st)
    snap0 = snapshot(st)
    rc, out = sh('merge', '--stage', st, '--sh', shc['w84'], '--probes', probes)
    check('merge refuses a stage inside a farm_wNN dir (exit 2, untouched)', rc == 2 and 'farm_w84' in out and snapshot(st) == snap0, out)
    fake_root = os.path.join(work, 'Fake H2EK'); st = os.path.join(fake_root, 'tags', 'scenarios', 'x'); copy_files(stages[84][0], stages[84][1], st)
    snap0 = snapshot(st)
    rc, out = sh('merge', '--stage', st, '--sh', shc['w84'], '--probes', probes, '--h2-root', fake_root)
    check('merge refuses a stage inside ROOT\\tags (exit 2, untouched)', rc == 2 and 'tags tree' in out and snapshot(st) == snap0, out)
    rc, out = sh('check', '--src', os.path.join(stages[84][0], LT), '--sh', shc['w84'], '--probes', probes)
    check('check (read-only gate) passes on the real w84 pair', rc == 0 and 'SH_STAGE: OK check' in out, out)
    rc, out = sh('check', '--src', os.path.join(stages[84][0], LT), '--sh', os.path.join(inp, 'bad_00.ltmp'), '--probes', probes)
    check('check refuses the sample_point flip', rc == 2 and 'sample_point' in out, out)

    # ---------------------------------------------------------------- single
    src84 = os.path.join(inp, 'src_w84.ltmp'); shutil.copyfile(os.path.join(stages[84][0], LT), src84)
    st = os.path.join(work, 'single_w84', 'stage')
    rc, out = sh('single', '--stage', st, '--src', src84, '--sh', shc['w84'], '--probes', probes, '--name', LT)
    rep = json.load(open(os.path.join(st, 'tagw_report.json'))) if os.path.exists(os.path.join(st, 'tagw_report.json')) else {}
    check('single (w84 pair): exit 0, a one-ltmp stage with status written and md5 = SH',
          rc == 0 and sorted(os.listdir(st)) == sorted([LT, 'tagw_report.json', 'sh_merge.json']) and rep.get('status') == 'written'
          and list(rep.get('outputs', {})) == [LT] and rep['outputs'][LT]['md5'] == md5f(shc['w84'])
          and rep['inputs']['src']['md5'] == md5f(src84), out)
    rc, out = sh('single', '--stage', st, '--src', src84, '--sh', shc['w84'], '--probes', probes, '--name', LT)
    check('single refuses an existing non-empty stage dir', rc == 2 and 'not an empty folder' in out, out)
    rc, out = sh('single', '--stage', os.path.join(work, 'single_badname'), '--src', src84, '--sh', shc['w84'], '--probes', probes, '--name', 'x.bitmap')
    check('single refuses a staged name that is not *.scenario_structure_lightmap', rc == 2, out)
    # probes-only on the LIVE ltmp (a copy): lp_write_sh (production, CPU) writes the w84 floats into it, single stages it
    live_copy = os.path.join(inp, 'live_copy', LT); os.makedirs(os.path.dirname(live_copy)); shutil.copyfile(os.path.join(LIVE_DIR, LT), live_copy)
    floats = os.path.join(work, 'floats_w84'); os.makedirs(floats)
    for n in os.listdir(os.path.join(LP, 'e2e_B_probes_w84_final', 'floats')):
        if n.endswith('.float'):
            shutil.copyfile(os.path.join(LP, 'e2e_B_probes_w84_final', 'floats', n), os.path.join(floats, n))
    sh_live = os.path.join(work, 'sh_live', LT)
    rc, out = run([os.path.join(LP, 'lp_write_sh.py'), live_copy, sh_live, floats], cwd=work)
    check('lp_write_sh (production, CPU) writes the w84 probe floats into a COPY of the live ltmp', rc == 0 and 'wrote 11 environment(s)' in out, out)
    st = os.path.join(work, 'single_live', 'stage')
    rc, out = sh('single', '--stage', st, '--src', live_copy, '--sh', sh_live, '--probes', probes)
    rec = json.load(open(os.path.join(st, 'sh_merge.json'))) if os.path.exists(os.path.join(st, 'sh_merge.json')) else {}
    gl = rec.get('gate', {})
    check('single (live copy): exit 0, 11 envs == 11 points, envs 1-9 changed vs the CPU live SH, 0/10 zero in both',
          rc == 0 and gl.get('envs_changed') == list(range(1, 10)) and gl.get('envs_all_zero_sh') == [0, 10], out)
    install_roundtrip(st, 'single_live', [LT])

    # ---------------------------------------------------------------- the read-only inputs are untouched
    for w in (84, 82):
        sd = stages[w][0]
        rep = json.load(open(os.path.join(sd, 'tagw_report.json')))
        check('post: farm_w%d\\tagw_stage still matches its report' % w, all(md5f(os.path.join(sd, n)) == o['md5'] for n, o in rep['outputs'].items()))
    check('post: no __pycache__ was created in the production tagwriter dir', os.path.exists(pyc) == pyc_before)

    n_ok = sum(1 for _, ok, _ in RESULTS if ok)
    if not a.keep and not a.work:
        safe_rmtree(work)                         # our own temp dir: plain file copies only (checked)
    else:
        print('kept: %s' % work)
    print('RESULT: %s %d/%d' % ('PASS' if n_ok == len(RESULTS) else 'FAIL', n_ok, len(RESULTS)))
    return 0 if n_ok == len(RESULTS) else 1


if __name__ == '__main__':
    sys.exit(main())
