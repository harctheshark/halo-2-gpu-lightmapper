"""mapbake.py -- the entry point of H2 GPU Lightmapper's "bake straight from a .map" path (milestone 2).

Milestone 3: AO and light probes are baked by the ordinary pipeline (the inject verb takes whatever final stage it made: a lightmap + truecolor bitmap, or the
lightmap only for a Lightprobes-only bake), and a Vista map (cache v8) is read through its MCC form (h2conv's reverse converter, kept in the run folder's h2work)
and, when asked, converted back to Vista by h2conv.inject --to-vista.

The exe runs it with the bundled Python, one verb at a time, and reads the JSON/exit code.  The working files of a run live in the temporary per-run
folder the exe made, and `cleanup` deletes it.

The converter that reads and writes the map is the separate program "h2conv" (Halo 2 MCC to Vista Converter, GPL-3.0-or-later; see NOTICES): this
script only starts its command line tools (read, inject and query) as child processes and never imports
or copies their code (aggregation, not linking).  Stdlib only.

  query    --map M --kit TAGS [--cache F] [--work-dir D] --out JSON   what the map is (container, skies, cube maps, bsps); refusals in plain words
  prepare  --map M --kit TAGS --tool-root ROOT --run DIR [--sky-cubemap SKY=VAL ...]
                                                              read ONLY the lightmapper's dependency subset of the map into DIR\\tags_x, then build the real-copy
                                                              overlay root DIR\\root (kit tags copied + the tree read from the map on top; never links)
  inject   --map M --run DIR --stage STAGE --scenario S --bsp B --out FINAL [--ltmp-only] [--vista-out yes|no]
                                                              inject ONLY the lightmap + its bitmap (or just the lightmap) into a COPY of the map, validate, publish FINAL
                                                              (a Vista map: through its MCC form; --vista-out yes converts the result back to Vista)
  validate --map M                                            h2conv's map validators
  snap     --tags TAGS --out F  /  snapdiff A B               live-tag guard (stat snapshot / difference list)
  junction --link L --target T                                the overlay work folder's farm_wNN -> the real farm folder (only inside a _mapbake run folder)
  cleanup  --run DIR [--keep NAME ...]                        delete the run folder (junctions unlinked first), keeping the named children
"""
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
BS = chr(92)
OFF_LIMITS = 'do not look in here claude'
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
CREATE_NO_WINDOW = 0x08000000
BELOW_NORMAL = 0x00004000


class MapbakeError(Exception):
    pass


def below_normal():
    try:
        k = ctypes.windll.kernel32
        k.SetPriorityClass(k.GetCurrentProcess(), BELOW_NORMAL)
    except Exception:                                                              # noqa: BLE001
        pass


def say(msg):
    sys.stdout.write('mapbake| %s\n' % msg)
    sys.stdout.flush()


def lp(path):
    """extended-length path (the overlay and the kit have deep paths)"""
    p = os.path.abspath(path)
    if p.startswith(BS * 2):
        return p
    return BS * 2 + '?' + BS + p


def is_reparse(path):
    try:
        return bool(os.lstat(path).st_file_attributes & FILE_ATTRIBUTE_REPARSE_POINT)
    except (OSError, AttributeError):
        return False


def md5_file(path):
    h = hashlib.md5()
    with open(lp(path), 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def write_json(path, obj):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(obj, fh, indent=1)


def map_version(path):
    """the cache version word of a map (13 = MCC, 8 = Vista), 0 when it is not a cache file"""
    try:
        with open(path, 'rb') as fh:
            h = fh.read(8)
    except OSError:
        return 0
    if len(h) < 8 or h[:4] != b'daeh':
        return 0
    return int.from_bytes(h[4:8], 'little')


# ------------------------------------------------------------------------------------------------------------------ h2conv as a separate program
def h2conv_home(a):
    home = getattr(a, 'h2conv_home', None) or os.environ.get('H2LM_H2CONV_HOME') or HERE
    if not os.path.isdir(os.path.join(home, 'h2conv')):
        raise MapbakeError('the map reader (h2conv) is missing: %s' % os.path.join(home, 'h2conv'))
    return home


def run_h2conv(home, module, args, log_path=None, cwd=None):
    """Start `python -m <module> <args>` as a child process (the bundled Python, no window, below normal priority).  Output is mirrored to this
    process' stdout (prefixed) and to log_path.  -> (exit code, last lines)"""
    shim = os.path.join(HERE, 'h2conv_run.py')
    cmd = [sys.executable, shim, home, module] + list(args)
    say('start %s %s' % (module, ' '.join(args[:3]) + (' ...' if len(args) > 3 else '')))
    t0 = time.time()
    tail = []
    log = open(log_path, 'w', encoding='utf-8', errors='replace') if log_path else None
    try:
        p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd=cwd or home,
                             creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
        for raw in p.stdout:
            line = raw.decode('utf-8', 'replace').rstrip('\r\n')
            if log:
                log.write(line + '\n')
            tail.append(line)
            del tail[:-40]
            sys.stdout.write('h2conv| %s\n' % line)
            sys.stdout.flush()
        rc = p.wait()
    finally:
        if log:
            log.close()
    say('%s finished with exit %d in %.1f s' % (module, rc, time.time() - t0))
    return rc, tail


# ------------------------------------------------------------------------------------------------------------------ query
def cmd_query(a):
    home = h2conv_home(a)
    out = a.out
    wd = [['--work-dir', os.path.abspath(a.work_dir)]] if getattr(a, 'work_dir', None) else []
    rc, tail = run_h2conv(home, 'h2conv.mapbake_query', [a.map, '--kit', a.kit] + (['--cache', a.cache] if a.cache else []) + (wd[0] if wd else []) + ['--out', out])
    if rc != 0 or not os.path.isfile(out):
        r = {'schema': 'h2lm-mapbake-query/1', 'ok': False, 'map': a.map, 'error': 'the map reader failed (exit %d): %s' % (rc, ' | '.join(tail[-4:]))}
        write_json(out, r)
        return 2
    return 0


# ------------------------------------------------------------------------------------------------------------------ snapshots (live-tag guard)
SNAP_SKIP_DIRS = (OFF_LIMITS, 'temp_scenario')


def snap_tree(tags):
    """{relative path: [size, mtime_ns]} (stat only).  Sapien's scratch folder, its auto-backups and the off-limits folder are not part of the guard."""
    d = {}
    for dp, dn, fn in os.walk(tags):
        dn[:] = [x for x in dn if x.lower() not in SNAP_SKIP_DIRS]
        for f in fn:
            if '(auto-backup' in f.lower():
                continue
            p = os.path.join(dp, f)
            try:
                st = os.stat(p)
            except OSError:
                continue
            d[os.path.relpath(p, tags)] = [st.st_size, st.st_mtime_ns]
    return d


def cmd_snap(a):
    d = snap_tree(a.tags)
    write_json(a.out, {'tags': a.tags, 'files': d})
    say('snapshot: %d files' % len(d))
    return 0


def cmd_snapdiff(a):
    A = json.load(open(a.a, encoding='utf-8'))['files']
    B = json.load(open(a.b, encoding='utf-8'))['files']
    diffs = []
    for k in sorted(set(A) | set(B)):
        if k not in B:
            diffs.append('REMOVED ' + k)
        elif k not in A:
            diffs.append('ADDED ' + k)
        elif A[k] != B[k]:
            diffs.append('CHANGED ' + k)
    for x in diffs[:200]:
        say('LIVE TAG ' + x)
    say('snapshot diff: %d files before, %d after, %d difference(s)' % (len(A), len(B), len(diffs)))
    if a.out:
        write_json(a.out, {'differences': diffs})
    return 1 if diffs else 0


# ------------------------------------------------------------------------------------------------------------------ prepare
SKIP_EXT = ('.rar', '.zip', '.scenario_structure_bsp', '.scenario_structure_lightmap')     # other maps' bsp / lightmaps + archives: never read for this scenario
ROOT_FILES = ('tool_fast.exe', 'libfbxsdk.dll', 'custom_lightmap_quality.conf', 'SceneryFixDependencies.db', 'h2v_scenery_bounds.db')


def tree_size(root):
    n = tot = 0
    for dp, dn, fn in os.walk(root):
        dn[:] = [x for x in dn if x.lower() != OFF_LIMITS]
        for f in fn:
            try:
                tot += os.stat(os.path.join(dp, f)).st_size
                n += 1
            except OSError:
                pass
    return n, tot


def rel_is_top(dp, kit_tags):
    return os.path.normcase(os.path.abspath(dp)) == os.path.normcase(os.path.abspath(kit_tags))


def build_overlay(kit_tags, ext_tags, ov_tags):
    """REAL COPIES only: every kit tag file (except other maps' bsp/lightmap tags and archives) is copied to the overlay, then the tree read from the map is copied on
    top (the map's files win).  Nothing is hard-linked or junctioned; a reparse point in the kit is skipped and reported."""
    extrel = set()
    for dp, dn, fn in os.walk(ext_tags):
        for f in fn:
            extrel.add(os.path.relpath(os.path.join(dp, f), ext_tags).lower())
    n_kit = n_ext = n_over = n_skip = 0
    reparse = []
    t0 = time.time()
    for dp, dn, fn in os.walk(kit_tags):
        keep = []
        for d in dn:
            full = os.path.join(dp, d)
            if d.lower() == OFF_LIMITS or (rel_is_top(dp, kit_tags) and d.lower() == 'temp_scenario'):
                continue                                                           # Sapien's scratch scenarios are not part of any map
            if is_reparse(full):
                reparse.append(os.path.relpath(full, kit_tags))
                continue
            keep.append(d)
        dn[:] = keep
        rel = os.path.relpath(dp, kit_tags)
        dst_dir = ov_tags if rel == '.' else os.path.join(ov_tags, rel)
        os.makedirs(lp(dst_dir), exist_ok=True)
        for f in fn:
            r = f if rel == '.' else os.path.join(rel, f)
            src = os.path.join(dp, f)
            if r.lower() in extrel:
                n_over += 1
                continue
            if f.lower().endswith(SKIP_EXT) or '(auto-backup' in f.lower():
                n_skip += 1
                continue
            if is_reparse(src):
                reparse.append(r)
                continue
            shutil.copyfile(lp(src), lp(os.path.join(dst_dir, f)))
            n_kit += 1
            if n_kit % 6000 == 0:
                say('  overlay: %d kit tags copied (%.0f s)' % (n_kit, time.time() - t0))
    for dp, dn, fn in os.walk(ext_tags):
        rel = os.path.relpath(dp, ext_tags)
        dst_dir = ov_tags if rel == '.' else os.path.join(ov_tags, rel)
        os.makedirs(lp(dst_dir), exist_ok=True)
        for f in fn:
            d = os.path.join(dst_dir, f)
            if os.path.exists(lp(d)):
                raise MapbakeError('overlay: %s exists before the copy from the map' % d)
            shutil.copyfile(lp(os.path.join(dp, f)), lp(d))
            n_ext += 1
    return {'kit_copied': n_kit, 'kit_skipped': n_skip, 'extracted_copied': n_ext, 'kit_overridden': n_over, 'reparse_skipped': reparse,
            'seconds': round(time.time() - t0, 1)}


def assert_real_copies(root_dir):
    """the overlay holds no hard link and no reparse point (a stray write could otherwise reach the live tags)"""
    bad = []
    n = 0
    for dp, dn, fn in os.walk(root_dir):
        for d in dn:
            if is_reparse(os.path.join(dp, d)):
                bad.append('reparse point ' + os.path.join(dp, d))
        for f in fn:
            p = os.path.join(dp, f)
            n += 1
            try:
                st = os.lstat(lp(p))
            except OSError:
                continue
            if st.st_nlink > 1 or (getattr(st, 'st_file_attributes', 0) & FILE_ATTRIBUTE_REPARSE_POINT):
                bad.append(p)
        if len(bad) > 5:
            break
    if bad:
        raise MapbakeError('the overlay is not made of real copies: %s' % '; '.join(bad[:5]))
    return n


def cmd_prepare(a):
    below_normal()
    home = h2conv_home(a)
    run = os.path.abspath(a.run)
    kit = os.path.abspath(a.kit)
    tool_root = os.path.abspath(a.tool_root)
    if not os.path.isfile(os.path.join(tool_root, 'tool_fast.exe')):
        raise MapbakeError('no tool_fast.exe in %s' % tool_root)
    for p in (run,):
        if os.path.commonpath([os.path.normcase(p), os.path.normcase(kit)]) == os.path.normcase(kit):
            raise MapbakeError('the run folder is inside the kit tags: %s' % p)
    tags_x = os.path.join(run, 'tags_x')
    root = os.path.join(run, 'root')
    logs = os.path.join(run, 'logs')
    for d in (tags_x, root):
        if os.path.exists(d):
            raise MapbakeError('%s already exists' % d)
    os.makedirs(logs, exist_ok=True)
    rep = {'schema': 'h2lm-mapbake-prepare/1', 'map': a.map, 'run': run}

    # ---- free space (the overlay is a real copy of the kit tags)
    t0 = time.time()
    n_kit, kit_bytes = tree_size(kit)
    free = shutil.disk_usage(run).free
    need = int(kit_bytes * 1.05) + (3 << 30)
    say('kit tags: %d files, %.1f GB; free on the run drive: %.1f GB (need about %.1f GB)' % (n_kit, kit_bytes / 1e9, free / 1e9, need / 1e9))
    if free < need:
        raise MapbakeError('not enough free disk space on the drive of %s: the temporary copy needs about %.1f GB, %.1f GB are free' % (run, need / 1e9, free / 1e9))
    rep['kit'] = {'files': n_kit, 'bytes': kit_bytes, 'free': free}

    # ---- 1. read ONLY the dependency subset of the map (the lightmapper's closure: visual deps + placed objects)
    ext_report = os.path.join(logs, 'extract_report.json')
    args = [a.map, tags_x, '--deps', 'visual', '--include', 'scen,bloc,mach,ctrl,lifi', '--kit', kit, '--kit-policy', 'identical', '--write-identical',
            '--force-output-dir', '--report', ext_report, '--quiet']
    for s in a.sky_cubemap or []:
        args += ['--sky-cubemap', s]
    if map_version(a.map) == 8:
        # a Vista map: h2conv reverse-converts it to its MCC form first; that temporary map lives in the run folder (kept for the inject, deleted with the run folder)
        args += ['--work-dir', os.path.join(run, 'h2work'), '--keep-temp']
        rep['vista_input'] = True
    rc, tail = run_h2conv(home, 'h2conv.extract', args, os.path.join(logs, 'extract.log'))
    if rc != 0 or not os.path.isfile(ext_report):
        raise MapbakeError('reading the map failed (exit %d): %s' % (rc, ' | '.join(tail[-3:])))
    er = json.load(open(ext_report, encoding='utf-8'))
    counts = er.get('counts', {})
    rep['extract'] = {'counts': counts, 'seconds': er.get('seconds'), 'plan_warnings': (er.get('plan') or {}).get('warnings', [])[:20],
                      'sky_cubemap': er.get('sky_cubemap')}
    if counts.get('failed'):
        bad = [t for t in er.get('tags', []) if t.get('status') == 'failed'][:5]
        raise MapbakeError('%d tag(s) of the map could not be read: %s' % (counts['failed'], '; '.join('%s:%s (%s)' % (b['group'], b['name'], (b.get('error') or '')[:80]) for b in bad)))
    scn = [t for t in er.get('tags', []) if t.get('group') == 'scnr']
    bsps = [t for t in er.get('tags', []) if t.get('group') == 'sbsp']
    if len(scn) != 1 or not bsps:
        raise MapbakeError('reading the map did not produce one scenario and its structure bsp(s) (scenario %d, bsp %d)' % (len(scn), len(bsps)))
    rep['scenario'] = scn[0]['name']
    rep['bsps'] = [b['name'] for b in bsps]
    for t in scn + bsps:
        if not os.path.isfile(lp(os.path.join(tags_x, t['file'].replace('/', BS)))):
            raise MapbakeError('tag %s read from the map is missing from %s' % (t['file'], tags_x))
    # the sky cube map choice that was applied (for the log)
    for ch in er.get('sky_cubemap') or []:
        say('sky %s: cube map %s' % (ch.get('sky'), ch.get('cubemap') or 'NONE (%s)' % ch.get('note')))

    # ---- 2. the overlay root: real copies of the root files, the kit tags, then the tags read from the map on top
    os.makedirs(root)
    for f in ROOT_FILES:
        src = os.path.join(tool_root, f)
        if os.path.isfile(src):
            shutil.copyfile(src, os.path.join(root, f))
            if f == 'tool_fast.exe' and md5_file(src) != md5_file(os.path.join(root, f)):
                raise MapbakeError('the copy of tool_fast.exe does not verify')
    # tool_fast exits silently (code 127, before it logs anything) when these folders are missing from its root: they must exist
    for d in ('data', 'reports', 'shader_cache'):
        os.makedirs(os.path.join(root, d), exist_ok=True)
    ov = build_overlay(kit, tags_x, os.path.join(root, 'tags'))
    say('overlay: %d kit tags copied, %d skipped (other maps\' bsp / lightmap tags), %d tags from the map on top (%d kit files overridden) in %.0f s'
        % (ov['kit_copied'], ov['kit_skipped'], ov['extracted_copied'], ov['kit_overridden'], ov['seconds']))
    if ov['reparse_skipped']:
        say('overlay: skipped %d link(s) inside the kit tags (never followed): %s' % (len(ov['reparse_skipped']), ', '.join(ov['reparse_skipped'][:3])))
    rep['overlay'] = ov
    n = assert_real_copies(root)
    say('overlay check: %d files, no hard links, no links' % n)
    rep['seconds'] = round(time.time() - t0, 1)
    write_json(os.path.join(run, 'prepare_report.json'), rep)
    if a.report:
        write_json(a.report, rep)
    return 0


# ------------------------------------------------------------------------------------------------------------------ inject
def newest_stage_file(stage, suffix):
    hits = [f for f in os.listdir(stage) if f.lower().endswith(suffix)]
    return hits


def cmd_inject(a):
    below_normal()
    home = h2conv_home(a)
    run = os.path.abspath(a.run)
    logs = os.path.join(run, 'logs')
    os.makedirs(logs, exist_ok=True)
    scen = a.scenario.replace('/', BS).strip(BS)
    folder, leaf = os.path.split(scen)
    bsp = a.bsp
    lm_base = '%s_%s_lightmap' % (leaf, bsp)
    stage = os.path.abspath(a.stage)
    ltmp_name = lm_base + '.scenario_structure_lightmap'
    tc_name = lm_base + '_truecolor_bitmaps.bitmap'
    ltmp_only = bool(a.ltmp_only)                                                  # a Lightprobes-only bake changes the lightmap tag only (the probes live in it)
    for n in ((ltmp_name,) if ltmp_only else (ltmp_name, tc_name)):
        if not os.path.isfile(os.path.join(stage, n)):
            raise MapbakeError('the bake stage lacks %s (%s)' % (n, stage))
    vista_in = map_version(a.map) == 8
    vista_out = vista_in and a.vista_out == 'yes'
    if a.vista_out == 'yes' and not vista_in:
        raise MapbakeError('--vista-out yes is for a Vista map only')
    inj = os.path.join(run, 'inject_tags')
    if os.path.exists(inj):
        shutil.rmtree(inj)
    bsp_rel = (a.bsp_rel or (folder + BS + bsp if folder else bsp)).replace('/', BS).strip(BS)
    src_bsp = os.path.join(run, 'tags_x', bsp_rel + '.scenario_structure_bsp')
    if not os.path.isfile(lp(src_bsp)):
        raise MapbakeError('the structure bsp %s read from the map is missing' % src_bsp)
    files = {bsp_rel + '.scenario_structure_bsp': src_bsp,
             (folder + BS if folder else '') + ltmp_name: os.path.join(stage, ltmp_name)}
    if not ltmp_only:
        files[(folder + BS if folder else '') + tc_name] = os.path.join(stage, tc_name)
    for rel, src in files.items():
        dst = os.path.join(inj, rel)
        os.makedirs(os.path.dirname(lp(dst)), exist_ok=True)
        shutil.copyfile(lp(src), lp(dst))
    out_tmp = os.path.join(run, 'out', os.path.basename(a.out))
    os.makedirs(os.path.dirname(out_tmp), exist_ok=True)
    if os.path.exists(out_tmp):
        os.remove(out_tmp)
    rep_json = os.path.join(logs, 'inject_report.json')
    fw = lambda p: p.replace(BS, '/')                                              # noqa: E731  (h2conv takes tag paths with forward slashes from a shell)
    args = [a.map, inj, out_tmp, '--bsp', fw(bsp_rel), '--lightmap', fw((folder + BS if folder else '') + lm_base), '--scnr-clusters', 'keep', '--shift-align', '0',
            '--report', rep_json, '--overwrite', '--force-output-dir']      # the run folder lives under the work folder inside the H2EK: h2conv refuses that by default
    if vista_in:
        args += ['--work-dir', os.path.join(run, 'h2work')]                        # the reverse conversion (Vista -> MCC) already made is reused from here
    vname = None
    if vista_out:
        vname = a.vista_name
        args += ['--to-vista']
        if vname:
            args += ['--vista-name', vname]
    rc, tail = run_h2conv(home, 'h2conv.inject', args, os.path.join(logs, 'inject.log'))
    if rc != 0 or not os.path.isfile(out_tmp):
        raise MapbakeError('injecting the lightmap into the map failed (exit %d): %s' % (rc, ' | '.join(tail[-3:])))
    ir = json.load(open(rep_json, encoding='utf-8')) if os.path.isfile(rep_json) else {}
    if vista_out:
        vo = ir.get('vista_output') or {}
        vpath = vo.get('path')
        if not vpath or not os.path.isfile(vpath):
            raise MapbakeError('the conversion back to Vista produced no map (%s)' % (vpath or 'no path in the report'))
        say('converted back to Vista: %s (%d bytes, name %s)' % (vpath, os.path.getsize(vpath), vo.get('name')))
        if not vo.get('ok') or vo.get('failures'):
            raise MapbakeError('the conversion back to Vista reported problems: failed tags %s, validate_file %s, encode failures %s'
                               % (vo.get('failures'), vo.get('validate_file'), vo.get('encode_failures')))
    reps = [t for t in (ir.get('tags') or []) if str(t.get('action', '')).startswith('replace')]
    replaced = sorted('%s:%s' % (t.get('group'), t.get('name')) for t in reps)
    say('injected: %s' % (', '.join(replaced) if replaced else '(see inject_report.json)'))
    added = (ir.get('build') or {}).get('tags_added') or []
    odd = [r for r in reps if r.get('group') not in ('sbsp', 'ltmp', 'bitm')]
    if added or odd or len(reps) != (2 if ltmp_only else 3):
        raise MapbakeError('the injection touched more than the lightmap and its bitmap (replaced %d, added %d: %s)'
                           % (len(reps), len(added), ', '.join(replaced)[:300]))
    # ---- validate the product (the MCC map h2conv.inject made; a Vista result is validated too: header / directory / XOR checksum / tag table)
    vrc, vtail = run_h2conv(home, 'h2conv.mapbake_query', [out_tmp, '--kit', 'none', '--validate', '--out', os.path.join(logs, 'validate.json')], os.path.join(logs, 'validate.json.log'))
    vj = json.load(open(os.path.join(logs, 'validate.json'), encoding='utf-8')) if os.path.isfile(os.path.join(logs, 'validate.json')) else {}
    if vrc != 0 or vj.get('problems'):
        raise MapbakeError('the injected map did not validate: %s' % '; '.join((vj.get('problems') or vtail)[:5]))
    say('validate_file + plugin walk: OK')
    if vista_out:
        out_tmp_vista = vpath
        vrc, vtail = run_h2conv(home, 'h2conv.mapbake_query', [out_tmp_vista, '--kit', 'none', '--validate', '--out', os.path.join(logs, 'validate_vista.json')], os.path.join(logs, 'validate_vista.log'))
        vj = json.load(open(os.path.join(logs, 'validate_vista.json'), encoding='utf-8')) if os.path.isfile(os.path.join(logs, 'validate_vista.json')) else {}
        if vrc != 0 or vj.get('problems') or map_version(out_tmp_vista) != 8:
            raise MapbakeError('the Vista map did not validate: %s' % '; '.join((vj.get('problems') or vtail)[:5]))
        say('Vista validate_file (header, checksum, tag table): OK')
        out_tmp = out_tmp_vista
    # ---- publish: never over the input; an earlier output is renamed aside, not deleted
    final = os.path.abspath(a.out)
    src_map = os.path.abspath(a.map)
    if os.path.normcase(final) == os.path.normcase(src_map):
        raise MapbakeError('the output would overwrite the input map')
    os.makedirs(os.path.dirname(final), exist_ok=True)
    moved = None
    if os.path.exists(final):
        moved = final + '.old_' + time.strftime('%Y%m%d_%H%M%S')
        os.replace(final, moved)
        say('an earlier output was set aside: %s' % moved)
    part = final + '.part'
    shutil.copyfile(out_tmp, part)
    m_tmp, m_part = md5_file(out_tmp), md5_file(part)
    if m_tmp != m_part:
        os.remove(part)
        raise MapbakeError('the copy of the result does not verify')
    os.replace(part, final)
    say('output: %s (%d bytes, md5 %s)' % (final, os.path.getsize(final), m_part))
    res = {'schema': 'h2lm-mapbake-inject/1', 'output': final, 'size': os.path.getsize(final), 'md5': m_part, 'replaced': replaced, 'set_aside': moved,
           'format': 'vista' if vista_out else 'mcc', 'vista_input': vista_in,
           'inject_report': ir.get('counts') or ir.get('summary')}
    write_json(os.path.join(logs, 'inject_result.json'), res)
    if a.report:
        write_json(a.report, res)
    return 0


def cmd_validate(a):
    home = h2conv_home(a)
    out = a.out or os.path.join(os.path.dirname(os.path.abspath(a.map)), 'validate.json')
    rc, tail = run_h2conv(home, 'h2conv.mapbake_query', [a.map, '--kit', 'none', '--validate', '--out', out])
    j = json.load(open(out, encoding='utf-8')) if os.path.isfile(out) else {}
    return 0 if rc == 0 and not j.get('problems') else 1


# ------------------------------------------------------------------------------------------------------------------ junction (overlay work folder -> the real farm folder)
def cmd_junction(a):
    """one junction: LINK (must not exist, inside a _mapbake run folder) -> TARGET (an existing folder).  Used for the overlay work folder's farm_wNN only."""
    link = os.path.abspath(a.link)
    target = os.path.abspath(a.target)
    if (os.sep + '_mapbake' + os.sep).lower() not in link.lower():
        raise MapbakeError('a junction may only be made inside a _mapbake run folder: %s' % link)
    if not os.path.isdir(target):
        raise MapbakeError('junction target is not a folder: %s' % target)
    if os.path.lexists(link):
        raise MapbakeError('%s already exists' % link)
    import _winapi
    _winapi.CreateJunction(target, link)
    say('junction %s -> %s' % (link, target))
    return 0


# ------------------------------------------------------------------------------------------------------------------ cleanup
def unlink_reparse_points(top):
    """remove every junction / symlink below top WITHOUT following it (os.rmdir/unlink on the link itself); returns the count"""
    n = 0
    for dp, dn, fn in os.walk(top):
        keep = []
        for d in dn:
            full = os.path.join(dp, d)
            if is_reparse(full):
                try:
                    os.rmdir(full)
                except OSError:
                    os.unlink(full)
                n += 1
            else:
                keep.append(d)
        dn[:] = keep
        for f in fn:
            full = os.path.join(dp, f)
            if is_reparse(full):
                os.unlink(full)
                n += 1
    return n


def rmtree_force(path):
    def onerr(func, p, exc):
        try:
            os.chmod(p, 0o700)
            func(p)
        except OSError:
            pass
    shutil.rmtree(lp(path), onerror=onerr)


def cmd_cleanup(a):
    run = os.path.abspath(a.run)
    if (os.sep + '_mapbake' + os.sep).lower() not in run.lower() + os.sep:
        raise MapbakeError('cleanup only deletes a _mapbake run folder: %s' % run)
    if not os.path.isdir(run):
        say('nothing to clean up (%s)' % run)
        return 0
    keep = set(k.lower() for k in (a.keep or []))
    names = [n for n in os.listdir(run)]
    unlinked = 0
    for n in names:
        p = os.path.join(run, n)
        if n.lower() in keep:
            continue
        if is_reparse(p):
            try:
                os.rmdir(p)
            except OSError:
                os.unlink(p)
            unlinked += 1
            continue
        if os.path.isdir(p):
            unlinked += unlink_reparse_points(p)
            rmtree_force(p)
        else:
            os.remove(p)
    say('cleaned up %s (%d link(s) unlinked, kept: %s)' % (run, unlinked, ', '.join(sorted(keep)) or 'nothing'))
    return 0


# ------------------------------------------------------------------------------------------------------------------ main
def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('--h2conv-home', default=None, help=argparse.SUPPRESS)
    sub = ap.add_subparsers(dest='verb', required=True)
    p = sub.add_parser('query')
    p.add_argument('--map', required=True); p.add_argument('--kit', required=True); p.add_argument('--cache'); p.add_argument('--out', required=True); p.add_argument('--work-dir')
    p.set_defaults(fn=cmd_query)
    p = sub.add_parser('prepare')
    p.add_argument('--map', required=True); p.add_argument('--kit', required=True); p.add_argument('--tool-root', required=True); p.add_argument('--run', required=True)
    p.add_argument('--sky-cubemap', action='append', default=[]); p.add_argument('--report')
    p.set_defaults(fn=cmd_prepare)
    p = sub.add_parser('inject')
    p.add_argument('--map', required=True); p.add_argument('--run', required=True); p.add_argument('--stage', required=True)
    p.add_argument('--scenario', required=True); p.add_argument('--bsp', required=True); p.add_argument('--bsp-rel'); p.add_argument('--out', required=True); p.add_argument('--report')
    p.add_argument('--ltmp-only', action='store_true'); p.add_argument('--vista-out', choices=('yes', 'no'), default='no'); p.add_argument('--vista-name')
    p.set_defaults(fn=cmd_inject)
    p = sub.add_parser('validate')
    p.add_argument('--map', required=True); p.add_argument('--out')
    p.set_defaults(fn=cmd_validate)
    p = sub.add_parser('snap')
    p.add_argument('--tags', required=True); p.add_argument('--out', required=True)
    p.set_defaults(fn=cmd_snap)
    p = sub.add_parser('snapdiff')
    p.add_argument('a'); p.add_argument('b'); p.add_argument('--out')
    p.set_defaults(fn=cmd_snapdiff)
    p = sub.add_parser('junction')
    p.add_argument('--link', required=True); p.add_argument('--target', required=True)
    p.set_defaults(fn=cmd_junction)
    p = sub.add_parser('cleanup')
    p.add_argument('--run', required=True); p.add_argument('--keep', action='append', default=[])
    p.set_defaults(fn=cmd_cleanup)
    a = ap.parse_args(argv)
    try:
        return a.fn(a)
    except MapbakeError as e:
        sys.stdout.write('mapbake| REFUSED: %s\n' % e)
        return 2


if __name__ == '__main__':
    sys.exit(main())
