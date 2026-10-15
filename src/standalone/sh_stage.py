"""sh_stage.py -- N1 of the H2 GPU Lightmapper standalone: put the GPU lightprobe SH (gpu_lightprobes.py -> lp_write_sh.py) into a
direct-writer stage that lmw_install.py then installs UNMODIFIED.  Never writes a tags tree; never touches a farm's own stage.

  merge   py sh_stage.py merge  --stage STAGE --sh SH.ltmp (--probes probes.bin | --points N) [--json PATH]
          STAGE is a COPY of the final lightmap stage (RUN\\final_stage: the bake's or AO's tagw_stage with its tagw_report.json).
          The SH ltmp must be lp_write_sh's output for exactly that stage's ltmp. The stage ltmp is replaced by it, the report's
          md5/size for it re-stamped, and STAGE\\sh_merge.json written. The pre-merge ltmp + report are kept in
          STAGE\\_sh_merge_backup\\ (lmw_install installs only what tagw_report.json lists).
  single  py sh_stage.py single --stage NEWSTAGE --src SRC.ltmp --sh SH.ltmp (--probes probes.bin | --points N) [--name NAME]
          probes-only: a one-file stage (the SH ltmp + a tagw_report.json with status 'written' + sh_merge.json). SRC is the
          ltmp lp_write_sh wrote into (a copy of the live one); NAME defaults to SRC's file name.
  check   py sh_stage.py check  --src SRC.ltmp --sh SH.ltmp (--probes probes.bin | --points N)      the gate alone, read-only

THE GATE (all three verbs): SRC and SH are both 'ltmp' tags that walk to exact EOF with lmw_tagio.Walk and pass the header CRC
rule; same size, same walker field table; exactly 1 lightmap group; its lighting-environment count k == the probe point count;
every byte that differs lies in the header CRC (+0x28..+0x2B) or, for some environment e < k, in one of the 29 fields
lp_write_sh writes:  red/green/blue coefficient[0..8], mean incoming light direction, incoming light intensity;  and every one
of those SH floats is finite. Anything else -> REFUSED with the differing fields named.

Options for every verb: --tagio DIR (the v043-tagwriter dir holding lmw_tagio.py + lmw_defs.json; default
H2LM_CODE\\engine_v373\\v043-tagwriter, else <this dir>\\..\\engine_v373\\v043-tagwriter, else <this dir>\\..\\v043-tagwriter),
--h2-root ROOT (also refuse stages under ROOT\\tags; H2LM_ROOT and the dev root are always refused), --allow-farm-stage (let
merge/single write inside a farm_wNN / _gpulm_skel dir; default refused).
Exit codes: 0 ok; 2 refused, nothing changed; 3 failed after a change (rolled back from _sh_merge_backup when possible).
The last stdout line is 'SH_STAGE: OK ...', 'SH_STAGE: REFUSED: ...' or 'SH_STAGE: FAILED: ...'."""
import argparse, hashlib, json, os, re, shutil, struct, sys, time

sys.dont_write_bytecode = True                       # never leave a __pycache__ in INST or a production tagwriter dir
VERSION = 'sh_stage v1 (2026-10-14)'
HERE = os.path.dirname(os.path.abspath(__file__))
DEV_ROOT = r'G:\SteamLibrary\steamapps\common\H2EKMine'
LTMP_EXT = '.scenario_structure_lightmap'
ENV_BLOCK = 'root[0].lightmap groups[0].lighting environments'
SH_FIELD = re.compile(r'^root\[0\]\.lightmap groups\[0\]\.lighting environments\[(\d+)\]\.'
                      r'(?:(red|green|blue) coefficient\[([0-8])\]|(mean incoming light direction)|(incoming light intensity))$')
CRC_LO, CRC_HI = 0x28, 0x2C


class Refused(Exception):
    pass


def log(m):
    print('sh_stage: ' + m, flush=True)


def md5b(b):
    return hashlib.md5(b).hexdigest()


def md5f(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 22), b''):
            h.update(c)
    return h.hexdigest()


def fsync_write(path, data):
    with open(path, 'wb') as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())


def atomic_write(path, data):
    tmp = path + '.sh_stage_tmp'
    fsync_write(tmp, data)
    if md5f(tmp) != md5b(data):
        raise IOError('%s does not verify after writing' % tmp)
    os.replace(tmp, path)


# ------------------------------------------------------------------------------------------------ lmw_tagio
def load_tagio(tdir):
    cands = [tdir] if tdir else [os.path.join(os.environ['H2LM_CODE'], 'engine_v373', 'v043-tagwriter')] if os.environ.get('H2LM_CODE') else []
    if not tdir:
        cands += [os.path.join(HERE, '..', 'engine_v373', 'v043-tagwriter'), os.path.join(HERE, '..', 'v043-tagwriter')]
    for d in cands:
        d = os.path.abspath(d)
        if os.path.exists(os.path.join(d, 'lmw_tagio.py')) and os.path.exists(os.path.join(d, 'lmw_defs.json')):
            sys.path.insert(0, d)
            import lmw_tagio as T
            if os.path.normcase(os.path.dirname(os.path.abspath(T.__file__))) != os.path.normcase(d):
                raise Refused('imported lmw_tagio from %s, not %s' % (T.__file__, d))
            return T, d
    raise Refused('no lmw_tagio.py + lmw_defs.json in %s (pass --tagio DIR)' % ' / '.join(cands))


# ------------------------------------------------------------------------------------------------ guards
def _real(p):
    return os.path.normcase(os.path.realpath(os.path.abspath(p))).rstrip('\\/')


def _under(p, root):
    rp, rr = _real(p), _real(root)
    return rp == rr or rp.startswith(rr + os.sep)


def guard_stage_dir(stage, a):
    roots = [DEV_ROOT] + [r for r in (os.environ.get('H2LM_ROOT'), a.h2_root) if r]
    for r in roots:
        if _under(stage, os.path.join(r, 'tags')):
            raise Refused('the stage %s is inside the tags tree of %s' % (stage, r))
    if not a.allow_farm_stage:
        parts = [x.lower() for x in re.split(r'[\\/]+', _real(stage))]
        bad = [x for x in parts if re.fullmatch(r'farm_w\d+', x) or x == '_gpulm_skel']
        if bad:
            raise Refused('the stage %s is inside %s (a farm\'s own products; merge into a COPY such as RUN\\final_stage, '
                          'or pass --allow-farm-stage)' % (stage, bad[0]))


def read_points(a):
    if (a.probes is None) == (a.points is None):
        raise Refused('give exactly one of --probes probes.bin / --points N')
    if a.points is not None:
        if a.points < 1:
            raise Refused('--points must be >= 1')
        return a.points, '--points %d' % a.points
    b = open(a.probes, 'rb').read()
    if len(b) < 4:
        raise Refused('%s: too short for a probes.bin' % a.probes)
    n = struct.unpack_from('<I', b, 0)[0]
    if n < 1 or len(b) != 4 + 12 * n:
        raise Refused('%s: %d bytes is not {u32 n=%d, n x float3}' % (a.probes, len(b), n))
    return n, '%s (md5 %s)' % (os.path.abspath(a.probes), md5b(b))


# ------------------------------------------------------------------------------------------------ the gate
def where(W, off, idx):
    """a readable name for a differing byte offset"""
    starts, ends, paths = idx
    import bisect
    i = bisect.bisect_right(starts, off) - 1
    if i >= 0 and off < ends[i]:
        return paths[i]
    if CRC_LO <= off < CRC_HI:
        return 'header CRC'
    if off < 0x40:
        return 'file header +0x%X' % off
    for b in W.blocks:
        if b.hdr <= off < b.hdr + 16:
            return 'block header of %s' % b.path
    for r in W.tagrefs:
        if r.nlen > 0 and r.pos <= off <= r.pos + r.nlen:
            return 'tag-reference name of %s' % r.path
    for p, pos, sz in W.datas:
        if pos <= off < pos + sz:
            return 'data of %s (+0x%X)' % (p, off - pos)
    return 'outside every field @0x%X' % off


def gate(T, src_bytes, sh_bytes, points, src_label='SRC', sh_label='SH'):
    """-> summary dict; raises Refused naming the first differing non-SH fields."""
    import numpy as np
    if len(src_bytes) != len(sh_bytes):
        raise Refused('%s is %d bytes, %s is %d: lp_write_sh never changes the size' % (sh_label, len(sh_bytes), src_label, len(src_bytes)))
    Ws = {}
    for lab, b in ((src_label, src_bytes), (sh_label, sh_bytes)):
        try:
            W = T.Walk(b, 'ltmp', keep_fields=True)
        except Exception as e:
            raise Refused('%s does not walk as an ltmp tag: %s' % (lab, e))
        if not W.eof_ok:
            raise Refused('%s does not walk to exact EOF (%d of %d bytes)' % (lab, W.end, len(b)))
        if not T.crc_ok(b):
            raise Refused('%s fails the header CRC rule' % lab)
        Ws[lab] = W
    Wa, Wb = Ws[src_label], Ws[sh_label]
    if Wa.fields != Wb.fields or [(x.path, x.hdr, x.count, x.esize) for x in Wa.blocks] != [(x.path, x.hdr, x.count, x.esize) for x in Wb.blocks]:
        raise Refused('%s and %s have different tag structures (walker field/block tables differ)' % (src_label, sh_label))
    ng = Wa.count('root[0].lightmap groups')
    if ng != 1:
        raise Refused('%d lightmap groups (lp_write_sh writes groups[0] only; 1 is required)' % ng)
    k = Wa.count(ENV_BLOCK) if any(T.npath(p) == T.npath(ENV_BLOCK) for p in Wa.cnt_addr) else 0
    if k != points:
        raise Refused('the lightmap has %d lighting environment(s) but the BSP has %d probe point(s) (k == points is required)' % (k, points))
    # the allowed byte ranges: exactly 29 fields per environment
    allowed, per_env, sh_fields = [], {}, []
    for p, o, t, s in Wa.fields:
        m = SH_FIELD.match(T.npath(p))
        if not m:
            continue
        e = int(m.group(1))
        want = 4 if m.group(2) else 12
        if s != want:
            raise Refused('%s is %d bytes, expected %d (not the definition lp_write_sh writes)' % (p, s, want))
        allowed.append((o, o + s))
        per_env[e] = per_env.get(e, 0) + 1
        sh_fields.append((e, o, s))
    if sorted(per_env) != list(range(k)) or any(v != 29 for v in per_env.values()):
        raise Refused('lighting environment SH fields: %s (want 29 in each of %d environments)' % (per_env, k))
    a8 = np.frombuffer(src_bytes, np.uint8)
    b8 = np.frombuffer(sh_bytes, np.uint8)
    diff = np.nonzero(a8 != b8)[0]
    ok = np.zeros(len(src_bytes), bool)
    ok[CRC_LO:CRC_HI] = True
    for lo, hi in allowed:
        ok[lo:hi] = True
    bad = diff[~ok[diff]]
    if bad.size:
        fl = sorted((o, o + s, p) for p, o, t, s in Wa.fields)
        idx = ([x[0] for x in fl], [x[1] for x in fl], [x[2] for x in fl])
        names = []
        for off in bad.tolist():
            nm = where(Wa, off, idx)
            if not names or names[-1][0] != nm:
                names.append((nm, off))
            if len(names) >= 20:
                break
        raise Refused('%d byte(s) outside the SH fields differ between %s and %s: %s' % (
            bad.size, src_label, sh_label, '; '.join('%s @0x%X' % (n, o) for n, o in names)))
    # every SH float of the result finite; per-environment change / all-zero report
    changed, zero = set(), []
    coeff_zero = {}
    for e, o, s in sh_fields:
        vals = struct.unpack_from('<%df' % (s // 4), sh_bytes, o)
        if not all(v == v and abs(v) != float('inf') for v in vals):
            raise Refused('%s: environment %d has a non-finite SH value at 0x%X' % (sh_label, e, o))
        if bytes(src_bytes[o:o + s]) != bytes(sh_bytes[o:o + s]):
            changed.add(e)
        if s == 4:
            coeff_zero[e] = coeff_zero.get(e, True) and vals[0] == 0.0
    zero = sorted(e for e, z in coeff_zero.items() if z)
    return dict(lighting_environments=k, points=points, diff_bytes=int(diff.size),
                crc_changed=bool(src_bytes[CRC_LO:CRC_HI] != sh_bytes[CRC_LO:CRC_HI]),
                envs_changed=sorted(changed), envs_all_zero_sh=zero,
                allowed='header CRC + per lighting environment e<k: red/green/blue coefficient[0..8], mean incoming light direction, '
                        'incoming light intensity (lp_write_sh\'s 29 fields)')


def tagio_info(T, tdir):
    D = T.defs()
    return dict(dir=tdir, lmw_tagio_md5=md5f(os.path.join(tdir, 'lmw_tagio.py')), lmw_defs_md5=md5f(os.path.join(tdir, 'lmw_defs.json')),
                defs_tool_md5=D.tool_md5)


def finfo(p, b=None):
    return dict(path=os.path.abspath(p), md5=md5b(b) if b is not None else md5f(p), size=len(b) if b is not None else os.path.getsize(p))


def verify_stage(stage):
    rep = json.load(open(os.path.join(stage, 'tagw_report.json')))
    if rep.get('status') != 'written':
        return 'report status %r' % rep.get('status')
    for n, o in rep.get('outputs', {}).items():
        p = os.path.join(stage, n)
        if not os.path.exists(p) or os.path.getsize(p) != o['size'] or md5f(p) != o['md5']:
            return '%s does not match its report md5/size' % n
    return None


# ------------------------------------------------------------------------------------------------ verbs
def cmd_check(a, T, tdir):
    pts, psrc = read_points(a)
    s = gate(T, open(a.src, 'rb').read(), open(a.sh, 'rb').read(), pts)
    log('gate: %d environment(s) == %d point(s) (%s); %d differing byte(s), all in SH fields/CRC; changed envs %s%s' % (
        s['lighting_environments'], pts, psrc, s['diff_bytes'], s['envs_changed'],
        ('; all-zero SH in envs %s' % s['envs_all_zero_sh']) if s['envs_all_zero_sh'] else ''))
    print('SH_STAGE: OK check')
    return 0


def cmd_merge(a, T, tdir):
    stage = os.path.abspath(a.stage)
    guard_stage_dir(stage, a)
    rp = os.path.join(stage, 'tagw_report.json')
    if not os.path.exists(rp):
        raise Refused('%s missing (merge needs a direct-writer stage)' % rp)
    if os.path.exists(os.path.join(stage, 'sh_merge.json')) or os.path.exists(os.path.join(stage, '_sh_merge_backup')):
        raise Refused('%s was already merged (sh_merge.json / _sh_merge_backup present): start from a fresh copy of the stage' % stage)
    rep_raw = open(rp, 'rb').read()
    rep = json.loads(rep_raw)
    if rep.get('status') != 'written':
        raise Refused('the stage report status is %r, not written' % rep.get('status'))
    outs = rep.get('outputs', {})
    lt = [n for n in outs if n.endswith(LTMP_EXT)]
    if len(lt) != 1:
        raise Refused('the stage report lists %d lightmap tags (1 required): %s' % (len(lt), lt))
    name = lt[0]
    bad = verify_stage(stage)
    if bad:
        raise Refused('the stage is not the one its report describes: %s' % bad)
    pts, psrc = read_points(a)
    lp = os.path.join(stage, name)
    src_b = open(lp, 'rb').read()
    sh_b = open(a.sh, 'rb').read()
    s = gate(T, src_b, sh_b, pts, 'the stage ltmp', 'the SH ltmp')
    if outs[name]['md5'] != md5b(src_b):
        raise Refused('the stage ltmp changed while reading it')
    # ---- backup, then replace (from here on a failure rolls back)
    bk = os.path.join(stage, '_sh_merge_backup')
    os.makedirs(bk)
    bl, br = os.path.join(bk, name), os.path.join(bk, 'tagw_report.json')
    fsync_write(bl, src_b)
    fsync_write(br, rep_raw)
    if md5f(bl) != md5b(src_b) or md5f(br) != md5b(rep_raw):
        raise Refused('the backup copies in %s do not verify (nothing replaced)' % bk)
    before = dict(md5=outs[name]['md5'], size=outs[name]['size'])
    try:
        atomic_write(lp, sh_b)
        rep['outputs'][name]['md5'] = md5b(sh_b)
        rep['outputs'][name]['size'] = len(sh_b)
        rep['outputs'][name]['path'] = lp                     # the copied report named the farm's stage file; this one is new
        rec = dict(tool=VERSION, verb='merge', time=time.strftime('%Y-%m-%d %H:%M:%S'), stage=stage, ltmp=name,
                   src=dict(finfo(lp, src_b), path=os.path.abspath(lp)), sh=finfo(a.sh, sh_b), result=dict(md5=md5b(sh_b), size=len(sh_b)),
                   points_source=psrc, gate=s, tagio=tagio_info(T, tdir), backup=dict(ltmp=bl, report=br),
                   report_restamped=dict(before=before, after=dict(md5=md5b(sh_b), size=len(sh_b))))
        rep['sh_merge'] = dict(tool=VERSION, record='sh_merge.json', sh=os.path.abspath(a.sh), pre_merge=before, envs=s['lighting_environments'])
        atomic_write(rp, json.dumps(rep, indent=1).encode())
        jp = a.json or os.path.join(stage, 'sh_merge.json')
        atomic_write(jp, json.dumps(rec, indent=1).encode())
        bad = verify_stage(stage)
        if bad:
            raise IOError('post-merge verification: %s' % bad)
        if md5f(lp) != md5b(sh_b):
            raise IOError('post-merge: the stage ltmp is not the SH ltmp')
    except Exception as e:
        try:
            atomic_write(lp, src_b)
            atomic_write(rp, rep_raw)
            rb = 'rolled back from %s' % bk if md5f(lp) == md5b(src_b) and md5f(rp) == md5b(rep_raw) else 'ROLLBACK DID NOT VERIFY: restore from %s' % bk
        except Exception as e2:
            rb = 'ROLLBACK FAILED (%s): restore from %s' % (e2, bk)
        print('SH_STAGE: FAILED: %s -- %s' % (e, rb))
        return 3
    log('merged %s: %d environment(s) == %d point(s); %d differing byte(s), all SH/CRC; changed envs %s%s' % (
        name, s['lighting_environments'], pts, s['diff_bytes'], s['envs_changed'],
        ('; WARNING all-zero SH in envs %s' % s['envs_all_zero_sh']) if s['envs_all_zero_sh'] else ''))
    log('report re-stamped %s %d -> %s %d; record %s' % (before['md5'], before['size'], md5b(sh_b), len(sh_b), jp))
    print('SH_STAGE: OK merge %s %s' % (name, md5b(sh_b)))
    return 0


def cmd_single(a, T, tdir):
    stage = os.path.abspath(a.stage)
    guard_stage_dir(stage, a)
    if os.path.exists(stage) and (not os.path.isdir(stage) or os.listdir(stage)):
        raise Refused('%s exists and is not an empty folder' % stage)
    name = a.name or os.path.basename(a.src)
    if not name.endswith(LTMP_EXT) or os.path.basename(name) != name:
        raise Refused('the staged name %r must be a bare *%s file name' % (name, LTMP_EXT))
    pts, psrc = read_points(a)
    src_b = open(a.src, 'rb').read()
    sh_b = open(a.sh, 'rb').read()
    s = gate(T, src_b, sh_b, pts, 'SRC', 'the SH ltmp')
    t0 = time.strftime('%Y-%m-%d %H:%M:%S')
    os.makedirs(stage, exist_ok=True)
    lp = os.path.join(stage, name)
    try:
        atomic_write(lp, sh_b)
        rec = dict(tool=VERSION, verb='single', time=t0, stage=stage, ltmp=name, src=finfo(a.src, src_b), sh=finfo(a.sh, sh_b),
                   result=dict(md5=md5b(sh_b), size=len(sh_b)), points_source=psrc, gate=s, tagio=tagio_info(T, tdir))
        rep = dict(writer=VERSION + ' single', started=t0, status='written',
                   args=dict(src=os.path.abspath(a.src), sh=os.path.abspath(a.sh), stage=stage, name=name, points=psrc),
                   inputs=dict(src=finfo(a.src, src_b), sh=finfo(a.sh, sh_b)),
                   outputs={name: dict(path=lp, md5=md5b(sh_b), size=len(sh_b), replaced_backup=None)},
                   sh_merge=dict(tool=VERSION, record='sh_merge.json', sh=os.path.abspath(a.sh), src_md5=md5b(src_b),
                                 envs=s['lighting_environments']),
                   note='probes-only stage: install it with lmw_install.py; inputs.src.md5 is the ltmp this SH was written into -- '
                        'the live ltmp must still have that md5 at install time', finished=time.strftime('%Y-%m-%d %H:%M:%S'))
        atomic_write(os.path.join(stage, 'tagw_report.json'), json.dumps(rep, indent=1).encode())
        jp = a.json or os.path.join(stage, 'sh_merge.json')
        atomic_write(jp, json.dumps(rec, indent=1).encode())
        bad = verify_stage(stage)
        if bad:
            raise IOError('post-stage verification: %s' % bad)
    except Exception as e:
        print('SH_STAGE: FAILED: %s -- the new stage %s is incomplete; do not install it' % (e, stage))
        return 3
    log('single-ltmp stage %s: %s %s (%d environment(s) == %d point(s); %d differing byte(s) vs SRC, all SH/CRC; changed envs %s%s)' % (
        stage, name, md5b(sh_b), s['lighting_environments'], pts, s['diff_bytes'], s['envs_changed'],
        ('; WARNING all-zero SH in envs %s' % s['envs_all_zero_sh']) if s['envs_all_zero_sh'] else ''))
    print('SH_STAGE: OK single %s %s' % (name, md5b(sh_b)))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest='cmd', required=True)

    def common(p):
        p.add_argument('--sh', required=True, help="lp_write_sh's output ltmp (gpu_lightprobes OUT\\<leaf>_<bsp>_lightmap.scenario_structure_lightmap)")
        p.add_argument('--probes', default=None, help="gpu_lightprobes OUT\\probes.bin: {u32 n, n x float3}; n = the point count")
        p.add_argument('--points', type=int, default=None, help='the probe point count, instead of --probes')
        p.add_argument('--tagio', default=None, help='the v043-tagwriter dir with lmw_tagio.py + lmw_defs.json')
        p.add_argument('--h2-root', default=None, help='also refuse a stage under this root\'s tags tree')
        p.add_argument('--json', default=None, help='where to write the sh_merge record (default STAGE\\sh_merge.json)')
        p.add_argument('--allow-farm-stage', action='store_true', help='allow a stage inside a farm_wNN / _gpulm_skel dir')
    m = sp.add_parser('merge', help='merge the SH ltmp into a COPY of the final stage'); common(m)
    m.add_argument('--stage', required=True)
    s = sp.add_parser('single', help='build a one-ltmp stage (probes-only)'); common(s)
    s.add_argument('--stage', required=True); s.add_argument('--src', required=True); s.add_argument('--name', default=None)
    c = sp.add_parser('check', help='the gate alone (read-only)'); common(c)
    c.add_argument('--src', required=True)
    a = ap.parse_args(argv)
    try:
        T, tdir = load_tagio(a.tagio)
        return {'merge': cmd_merge, 'single': cmd_single, 'check': cmd_check}[a.cmd](a, T, tdir)
    except Refused as e:
        print('SH_STAGE: REFUSED: %s' % e)
        return 2
    except (OSError, ValueError) as e:
        print('SH_STAGE: REFUSED: %s: %s' % (type(e).__name__, e))
        return 2


if __name__ == '__main__':
    sys.exit(main())
