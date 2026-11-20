"""seam_stitch.py -- SEAM STITCH ONLY (EXPERIMENTAL): stitch the lightmap seams of the lightmap that is ALREADY in the tags.

No lighting is baked. The installed lightmap set of the scenario / BSP (ours, the stock tool's or Bungie's) is read and the texels next to
its seams are changed, as little as possible, so that both sides of every seam between flat or gently curved faces agree (the engine's
lmw_stitch.py, the same code a bake with "Stitch lightmap seams" runs). The result is written as a normal stage (lightmap tag + truecolor
bitmap + P8 bitmap, tagw_report.json) that the shell validates and installs through lmw_install.py (backup first). Nothing is written to
the tags here. The lightmap then differs from the original tool's by design.

  precheck --tag-dir DIR --prefix PFX --bsp-file BSP --engine ENGINE [--tool TOOL_FAST.EXE]
           the installed set exists, parses, passes the tag checksum rule and belongs to this BSP. Exit 0 ok, 2 refused.
  stage    (the same) --out OUT [--overwrite]
           OUT receives stage\\ (the three files + tagw_report.json) and seamstitch_report.json. Exit 0 stage written, 2 refused in plain
           words (nothing written), 1 unexpected error.
  selftest lmw_stitch's own test (--engine ENGINE).

Only the two bitmaps change, and only the RGB of the changed texels inside them (patched in place: every other byte of the installed files
is kept, including the page headers); the coverage flag (alpha) never changes. A changed texel's P8 index is re-picked as the nearest entry
of its page's palette group. The lightmap tag is copied unchanged (its palettes and per-cluster palette groups stay valid)."""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import time

import numpy as np

sys.dont_write_bytecode = True
FLAGS = 0x08000000 | 0x00004000           # CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS


class Refused(Exception):
    pass


def say(m):
    print('[seam_stitch %s] %s' % (time.strftime('%H:%M:%S'), m), flush=True)


def md5b(b):
    return hashlib.md5(b).hexdigest()


def load_mods(engine):
    tw = os.path.join(os.path.abspath(engine), 'v043-tagwriter')
    if not os.path.isfile(os.path.join(tw, 'lmw_stitch.py')):
        raise Refused('the engine folder %s has no seam stitch (v043-tagwriter\\lmw_stitch.py): this build cannot stitch seams' % engine)
    sys.path.insert(0, tw)
    import lmw_tagio, lmw_stitch   # noqa: E402
    return tw, lmw_tagio, lmw_stitch


def open_installed(a):
    tw, TIO, ST = load_mods(a.engine)
    names = {'ltmp': a.prefix + '.scenario_structure_lightmap', 'tc': a.prefix + '_truecolor_bitmaps.bitmap', 'p8': a.prefix + '_bitmaps.bitmap'}
    paths = {k: os.path.join(a.tag_dir, v) for k, v in names.items()}
    miss = [v for k, v in names.items() if not os.path.isfile(paths[k])]
    if miss:
        raise Refused('this scenario / BSP has no complete lightmap in the tags yet (missing: %s). Seam stitch only changes an existing lightmap: bake the '
                      'lightmap first, then run it.' % ', '.join(miss))
    D = TIO.defs()
    ok, det = D.pin_check(a.tool) if a.tool else (None, 'no --tool given')
    if ok is False:
        raise Refused('the tool in %s is not the build this program knows how to read and write lightmaps for (%s)' % (a.tool, det))
    raw = {k: open(paths[k], 'rb').read() for k in names}
    try:
        LM = TIO.LMView(raw['ltmp'], D)
        tci = TIO.bitmap_info(raw['tc'], D)
        p8i = TIO.bitmap_info(raw['p8'], D)
        BSP = TIO.BSPView(open(a.bsp_file, 'rb').read(), D)
    except (TIO.TagIOError, OSError) as e:
        raise Refused('the installed lightmap (or the BSP) could not be read: %s. Nothing was changed.' % e)
    if not all(TIO.crc_ok(raw[k]) for k in names):
        raise Refused('the installed lightmap files fail the tag checksum rule: they are damaged or were edited by hand. Nothing was changed.')
    bsp_eff = BSP.checksum if BSP.checksum else (next(iter(BSP.cl_checksums)) if len(BSP.cl_checksums) == 1 else None)
    if not (bsp_eff is not None and LM.checksum == bsp_eff and BSP.cl_checksums <= {bsp_eff}):
        raise Refused('the installed lightmap does not belong to this BSP (its structure checksum is 0x%08X, the BSP has 0x%08X): the BSP was compiled again '
                      'after the lightmap was made. Bake the lightmap again first.' % (LM.checksum, bsp_eff or 0))
    if not (LM.ncl == BSP.ncl and LM.ndef == BSP.ndef and LM.ninst == BSP.ninst):
        raise Refused('the installed lightmap has a different structure than this BSP (clusters %d/%d, definitions %d/%d, instances %d/%d): bake the lightmap again.' % (
            LM.ncl, BSP.ncl, LM.ndef, BSP.ndef, LM.ninst, BSP.ninst))
    bad = [i for i, p in enumerate(tci['pages']) if not (p['fmt'] == 11 and p['depth'] == 1)]
    bad8 = [i for i, p in enumerate(p8i['pages']) if not (p['fmt'] == 18 and p['depth'] == 1)]
    if bad or bad8 or len(p8i['pages']) != len(tci['pages']):
        raise Refused('the installed lightmap bitmaps are not a lightmap page set (truecolor %d pages, P8 %d pages, %d in another format).' % (
            len(tci['pages']), len(p8i['pages']), len(bad) + len(bad8)))
    return dict(tw=tw, TIO=TIO, ST=ST, names=names, paths=paths, raw=raw, LM=LM, tci=tci, p8i=p8i, BSP=BSP)


def precheck(a):
    S = open_installed(a)
    say('precheck ok: the installed lightmap (%d clusters, %d pages) belongs to this BSP' % (S['LM'].ncl, len(S['tci']['pages'])))
    return 0


def stage(a):
    t0 = time.time()
    S = open_installed(a)
    TIO, ST, LM, BSP, tci, p8i, raw, names = S['TIO'], S['ST'], S['LM'], S['BSP'], S['tci'], S['p8i'], S['raw'], S['names']
    out = os.path.abspath(a.out)
    stg = os.path.join(out, 'stage')
    if os.path.exists(stg):
        if not a.overwrite:
            raise Refused('%s exists (use --overwrite)' % stg)
        for f in os.listdir(stg):
            os.remove(os.path.join(stg, f))
    tc_b, p8_b = bytearray(raw['tc']), bytearray(raw['p8'])
    lo_tc, lo_p8 = tci['lump'][0], p8i['lump'][0]
    # page arrays are VIEWS into the copies of the installed files: the stitch writes the RGB of the changed texels straight into them
    pages = [np.frombuffer(tc_b, np.uint8, p['w'] * p['h'] * 4, lo_tc + p['off']).reshape(p['h'], p['w'], 4) for p in tci['pages']]
    p8 = [np.frombuffer(p8_b, np.uint8, p['w'] * p['h'], lo_p8 + p['off']).reshape(p['h'], p['w']) for p in p8i['pages']]
    cri, iri = LM.crinfo(), LM.irinfo()
    cl_page = {c: int(cri['bm'][c]) for c in range(len(cri)) if int(cri['bm'][c]) >= 0}
    inst_page = {i: int(iri['bm'][i]) for i in range(len(iri)) if int(iri['bm'][i]) >= 0}
    pal_of = np.full(len(pages), -1, np.int64)
    for c, pg in cl_page.items():
        pal_of[pg] = int(cri['pal'][c])
    for i, pg in inst_page.items():
        pal_of[pg] = int(iri['pal'][i])
    if (pal_of < 0).any() or any(v >= LM.npal for v in pal_of):
        raise Refused('the installed lightmap\'s pages and palette groups do not match (%d pages without a valid group). Nothing was changed.' % int((pal_of < 0).sum()))
    tris = ST.triangles(LM, raw['ltmp'], BSP.inst, cl_page, inst_page)
    say('%d lightmapped triangles on %d pages' % (len(tris['P']), len(pages)))
    stats = ST.stitch(tris, pages, log=say)
    n8 = ST.repick_p8(p8, pages, pal_of, LM.palettes(), stats['changed'])
    say('P8: %d indices re-picked from the existing palettes' % n8)
    tc_new, p8_new = TIO.set_crc(bytes(tc_b)), TIO.set_crc(bytes(p8_b))
    os.makedirs(stg, exist_ok=True)
    rep_out = {}
    for key, data in (('ltmp', raw['ltmp']), ('tc', tc_new), ('p8', p8_new)):
        dst = os.path.join(stg, names[key])
        with open(dst + '.tmp', 'wb') as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.replace(dst + '.tmp', dst)
        rep_out[names[key]] = dict(path=dst, md5=md5b(data), size=len(data))
    with open(os.path.join(stg, 'SEAM_STITCH_NOT_CPU_FAITHFUL.txt'), 'w') as f:
        f.write('This stage was produced by Seam stitch only (an EXPERIMENTAL option): the texels next to the lightmap seams were changed so that both '
                'sides agree. It differs from the original tool\'s lightmap by design.\n')
    st = {k: v for k, v in stats.items() if k != 'changed'}
    st['p8_reindexed'] = n8
    treport = dict(writer='seam_stitch', status='written', started=time.strftime('%Y-%m-%d %H:%M:%S'), outputs=rep_out,
                   stats=dict(pages=dict(count=len(pages)), seam_stitch=st), warnings=['EXPERIMENTAL seam stitch: the lightmap differs from the original tool\'s by design'],
                   gates={}, source_set={names[k]: md5b(raw[k]) for k in names})
    json.dump(treport, open(os.path.join(stg, 'tagw_report.json'), 'w'), indent=1)
    vr = subprocess.run([sys.executable, os.path.join(S['tw'], 'lmw_validate.py'), '--check-set', stg, '--bsp', a.bsp_file, '--json', os.path.join(out, 'tagw_validate.json')],
                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=FLAGS)
    vtxt = vr.stdout.decode('mbcs', 'replace')
    open(os.path.join(out, 'tagw_validate.log'), 'w', encoding='utf-8').write(vtxt)
    for line in vtxt.splitlines():
        if 'FAIL' in line or 'checks,' in line:
            print('   ' + line.strip(), flush=True)
    if vr.returncode:
        treport['status'] = 'refused'
        json.dump(treport, open(os.path.join(stg, 'tagw_report.json'), 'w'), indent=1)
        raise RuntimeError('lmw_validate --check-set refused the stage (see %s)' % os.path.join(out, 'tagw_validate.log'))
    st.update(seconds=round(time.time() - t0, 1), stage=stg, installed_set_md5=treport['source_set'], stage_md5={k: v['md5'] for k, v in rep_out.items()})
    json.dump(st, open(os.path.join(out, 'seamstitch_report.json'), 'w'), indent=1)
    say('STAGE -> %s (%d seams, %d texels changed; seams stepping 6/255 or more: %d -> %d; %.1f s; nothing installed)' % (
        stg, st['seams'], st['texels_changed'], st['step_before'].get('ge6', 0), st['step_after'].get('ge6', 0), st['seconds']))
    return 0


def selftest(a):
    _, _, ST = load_mods(a.engine)
    return ST.selftest()


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    for name, f in (('precheck', precheck), ('stage', stage)):
        p = sub.add_parser(name)
        p.add_argument('--tag-dir', required=True, help='the folder that holds the lightmap set')
        p.add_argument('--prefix', required=True, help='the lightmap set\'s name without extension (<scenario>_<bsp>_lightmap)')
        p.add_argument('--bsp-file', required=True, help='the .scenario_structure_bsp the lightmap belongs to')
        p.add_argument('--engine', required=True, help='an engine folder holding v043-tagwriter (lmw_stitch.py, lmw_validate.py)')
        p.add_argument('--tool', default=None, help='tool_fast.exe of the H2EK (the field-definition pin)')
        if name == 'stage':
            p.add_argument('--out', required=True)
            p.add_argument('--overwrite', action='store_true')
        p.set_defaults(f=f)
    p = sub.add_parser('selftest')
    p.add_argument('--engine', required=True)
    p.set_defaults(f=selftest)
    a = ap.parse_args(argv)
    try:
        return a.f(a)
    except Refused as e:
        print('Seam stitch: REFUSED -- ' + str(e).rstrip().rstrip('.') + '. Nothing was written.', flush=True)
        return 2


if __name__ == '__main__':
    sys.exit(main())
