"""mapbake.py -- the entry point of H2 GPU Lightmapper's "bake straight from a .map" path (milestone 2).

Milestone 3: AO and light probes are baked by the ordinary pipeline (the inject verb takes whatever final stage it made: a lightmap + truecolor bitmap, or the
lightmap only for a Lightprobes-only bake), and a Vista map (cache v8) is read through its MCC form (h2conv's reverse converter, kept in the run folder's h2work)
and, when asked, converted back to Vista by h2conv (--to-vista).

The exe runs it with the bundled Python, one verb at a time, and reads the JSON/exit code.  The working files of a run live in the temporary per-run
folder the exe made, and `cleanup` deletes it.

The converter that reads and writes the map is the separate program "h2conv" (Halo 2 MCC to Vista Converter, GPL-3.0-or-later; see NOTICES): this
script only starts its command line tools (read, inject and query) as child processes and never imports
or copies their code (aggregation, not linking).  Stdlib only.

  query    --map M --kit TAGS [--cache F] [--work-dir D] --out JSON   what the map is (container, skies, cube maps, bsps); refusals in plain words
  prepare  --map M --kit TAGS --tool-root ROOT --run DIR [--root-name r] [--sky-cubemap SKY=VAL ...]
                                                              read ONLY the lightmapper's dependency subset of the map into DIR\\tags_x, then build the real-copy
                                                              overlay root DIR\\root (0.6.8: DIR\\r; the kit tags the map needs (0.6.9) copied + the tree read from the map on top; never links)
  inject   --map M --run DIR --stage STAGE --scenario S --bsp B --out FINAL [--ltmp-only] [--vista-out yes|no]
                                                              inject ONLY the lightmap + its bitmap (or just the lightmap) into a COPY of the map, validate, publish FINAL
                                                              (a Vista map: through its MCC form; --vista-out yes converts the result back to Vista)
  validate --map M                                            h2conv's map validators
  snap     --tags TAGS --out F  /  snapdiff A B               live-tag guard (stat snapshot / difference list)
  junction --link L --target T                                the overlay work folder's farm_wNN -> the real farm folder (only inside a _mapbake run folder)
  cleanup  --run DIR [--keep NAME ...]                        delete the run folder (only links that point inside it are unlinked, never followed; any other
                                                              link refuses the delete), keeping the named children
  wipefarm --farm farm_wNN [--keep-stage]                     delete the derived geometry / texture products of a worker's farm folder (logs and engine copies stay)
  scan     --root DIR                                         list the tag / map files below DIR (exit 1 when any is found)
"""
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import time
import zlib

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
STAGED = bool(os.environ.get('H2LM_STAGE_CH'))
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


def run_h2conv(home, module, args, log_path=None, cwd=None, dirs=None):
    """Start `python -m <module> <args>` as a child process (the bundled Python, no window, below normal priority).  Output is mirrored to this
    process' stdout (prefixed) and to log_path.  -> (exit code, last lines)"""
    shim = os.path.join(HERE, 'h2conv_run.py')
    cmd = [sys.executable, shim, home, module] + list(args)
    say('start %s %s' % (module, ' '.join(args[:3]) + (' ...' if len(args) > 3 else '')))
    t0 = time.time()
    tail = []
    log = open(log_path, 'w', encoding='utf-8', errors='replace') if log_path else None
    try:
        env = None
        if STAGED:
            env = dict(os.environ)
            vd = [d for d in (env.get('H2LM_STAGE_DIRS', '').split(';') + list(dirs or [])) if d]
            env['H2LM_STAGE_DIRS'] = ';'.join(dict.fromkeys(os.path.abspath(d) for d in vd))
        p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd=cwd or home, env=env,
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
    rc, tail = run_h2conv(home, 'h2conv._rp', [a.map, '--kit', a.kit] + (['--cache', a.cache] if a.cache else []) + (wd[0] if wd else []) + ['--out', out],
                          dirs=[os.path.abspath(a.work_dir)] if getattr(a, 'work_dir', None) else None)
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


MAX_TOOL_PATH = 259      # tool_fast is not long-path aware: a full path of 260+ characters (MAX_PATH incl. the NUL) cannot be opened
COPY_THREADS = 8         # 0.6.12: parallel file copies of the overlay (I/O bound; the process runs below normal priority)


def _mcc_range(f, h, start, length):
    """bytes [start, start+length) of the DECOMPRESSED image of a chunk-compressed MCC map (header 0x380 bytes, then chunks that each inflate to
    the chunk size), or None when the map does not follow that layout"""
    cs, tbl_off, n = struct.unpack_from('<I', h, 0x308)[0], struct.unpack_from('<I', h, 0x310)[0], struct.unpack_from('<I', h, 0x314)[0]
    if cs <= 0 or n <= 0 or start < 0x380 or length <= 0:
        return None
    first, last = (start - 0x380) // cs, (start + length - 1 - 0x380) // cs
    if last >= n:
        return None
    f.seek(tbl_off + first * 8)
    tbl = list(struct.iter_unpack('<iI', f.read((last - first + 1) * 8)))
    parts = []
    for i, (csize, coff) in zip(range(first, last + 1), tbl):
        f.seek(coff)
        blob = f.read(abs(csize))
        data = blob if csize < 0 else zlib.decompress(blob)
        if i < n - 1 and len(data) != cs:
            return None
        parts.append(data)
    base = 0x380 + first * cs
    out = b''.join(parts)[start - base:start - base + length]
    return out if len(out) == length else None


STAGE_MAGIC = b'H2LMSTG\x01'


def _open_cache(path):
    """a binary reader of a cache file: the file itself, or -- the temporary MCC form of a Vista map in a .map bake's run folder -- the
    stage container's content (read on demand; nothing is written)"""
    if STAGED:
        try:
            with open(lp(path), 'rb') as f:
                head = f.read(len(STAGE_MAGIC))
        except OSError:
            head = b''
        if head == STAGE_MAGIC:
            import io
            sys.path.insert(0, HERE)
            try:
                import stagecache
            finally:
                sys.path.pop(0)
            return io.BufferedReader(stagecache.StageReader(lp(path), io.open), buffer_size=1 << 16)
    return open(lp(path), 'rb')


def map_tag_names(path):
    """0.6.8 RC: the names of every tag in a cache file (= every tag tool_fast loads for its scenario), lower case without the group
    extension; None when the table cannot be read (the caller then copies every kit tag). Read in memory only, never written.
    0.6.12: the table is one NUL-terminated name per tag. The header words beside it are NOT the tag count: 0x2CC of a Vista header is 15065
    in every map and 0x20 of a retail (compressed) MCC map is 23131, so the old check 'names * 2 >= that count' failed for every Vista and
    every retail MCC map, and those bakes copied the whole kit. The table is now checked on its own (well formed, printable names) and the
    caller cross-checks it against the tags read from the map."""
    try:
        with _open_cache(path) as f:
            h = f.read(0x800)
            if len(h) < 0x380 or h[:4] != b'daeh':
                return None
            ver = struct.unpack_from('<I', h, 4)[0]
            if ver == 13:                                                    # MCC: offset / size at 0x24 / 0x28 (offset in the decompressed image)
                off, size = struct.unpack_from('<Ii', h, 0x24)
                if struct.unpack_from('<H', h, 0x1C)[0] & 1:
                    buf = _mcc_range(f, h, off, size)
                else:
                    f.seek(off)
                    buf = f.read(size)
            elif ver == 8 and len(h) == 0x800:                                # Vista: buffer offset / size at 0x2D0 / 0x2D4
                off, size = struct.unpack_from('<II', h, 0x2D0)
                f.seek(off)
                buf = f.read(size)
            else:
                return None
    except Exception:                                                              # noqa: BLE001  (unreadable: the caller copies the whole kit)
        return None
    if not buf or size <= 0 or len(buf) != size or not buf.endswith(b'\0'):
        return None
    raw = buf[:-1].split(b'\0')
    if len(raw) < 16 or sum(1 for n in raw if not n or any(c < 0x20 or c == 0x7F for c in n)) > len(raw) // 1000:
        return None
    names = set(n.decode('latin-1').strip().lower().replace('/', BS) for n in raw)
    names.discard('')
    return names or None


def overlay_path_check(ov_tags, rels):
    """0.6.12: the full overlay path of every file the copy WILL hold (rels: paths relative to the overlay's tags folder).
    -> ([(length, rel)] past MAX_TOOL_PATH, longest first; (longest length, its rel))"""
    base = len(ov_tags) + 1
    out, longest = [], (0, '')
    for r in rels:
        n = base + len(r)
        if n > longest[0]:
            longest = (n, r)
        if n > MAX_TOOL_PATH:
            out.append((n, r))
    return sorted(out, reverse=True), longest


def unreachable_overlay_paths(kit_tags, ov_tags, names=None):
    """0.6.8 RC: the kit tag files the overlay copy WOULD hold at a full path tool_fast cannot open (> MAX_TOOL_PATH characters), with the
    same file selection as build_overlay. A tag the tool cannot open is silently missing in the bake (2026-10-29: on a 92-character H2EK
    root, 21 tags headlong uses -- ui screens, a first-person animation graph, a looping sound -- sat past it and almost every surface lost its
    diffuse bitmap -> grey albedo). names = map_tag_names(): only the kit tags the map uses count (a kit tag the scenario never loads may be
    unreachable without harm); None = every kit tag counts.
    Returns ([(length, rel), ...] of the counted tags, (longest counted length, its rel), number of unreachable tags not counted)."""
    out, longest, ignored = [], (0, ''), 0
    kit_tags = lp(kit_tags)                                                       # a plain walk silently skips folders past MAX_PATH
    for dp, dn, fn in os.walk(kit_tags):
        dn[:] = [d for d in dn if d.lower() != OFF_LIMITS and not (rel_is_top(dp, kit_tags) and d.lower() == 'temp_scenario')
                 and not is_reparse(os.path.join(dp, d))]
        rel = os.path.relpath(dp, kit_tags)
        for f in fn:
            if f.lower().endswith(SKIP_EXT) or '(auto-backup' in f.lower():
                continue
            r = f if rel == '.' else os.path.join(rel, f)
            n = len(os.path.join(ov_tags, r))
            if names is not None and os.path.splitext(r)[0].lower() not in names:
                ignored += n > MAX_TOOL_PATH
                continue
            longest = max(longest, (n, r))
            if n > MAX_TOOL_PATH:
                out.append((n, r))
    return sorted(out, reverse=True), longest, ignored


def rel_is_top(dp, kit_tags):
    return os.path.normcase(os.path.abspath(dp)) == os.path.normcase(os.path.abspath(kit_tags))


# 0.6.9: the overlay holds the kit tags the MAP NEEDS, not the whole kit. The map's tag-name table is its dependency closure (every tag
# the game loads for the scenario, hence every tag tool_fast loads to light it). On top of that, always copied (inclusive on purpose):
# the small shared folders below (folders the tool itself loads whatever the map: the multiplayer player models, the shader collections, the HUD ...), every file of the scenario's folder and of its structure bsp's folder (the skeleton run copies / links
# them), and the loose files of the tags root. Every kit FOLDER is created (empty ones too), so a folder link into the overlay still resolves.
KEEP_DIRS = ('globals', 'shaders', 'rasterizer', 'cubemaps', 'effects', 'multiplayer', 'ai', 'camera', 'cursors', 'scenarios' + BS + 'shaders', 'scenarios' + BS + 'ui',
             'ui' + BS + 'hud', 'objects' + BS + 'multi', 'objects' + BS + 'characters' + BS + 'dervish', 'objects' + BS + 'characters' + BS + 'masterchief')
SKIP_IN_FOLDER = ('radiance_bitmaps',)     # the CPU tool's own old radiance output beside the scenario: nothing reads it (its lightmap tag is never copied)


def scan_kit(kit_tags):
    """One pass over the kit tags with the exclusions the overlay copy has always had (Sapien's scratch folder, the off-limits folder, links).
    -> {'dirs': [rel], 'files': [(rel, size)], 'reparse': [rel], 'total_files': n, 'total_bytes': n}"""
    dirs, files, reparse = [], [], []
    n = tot = 0
    stack = ['']
    while stack:
        rel = stack.pop()
        try:
            ents = list(os.scandir(lp(os.path.join(kit_tags, rel) if rel else kit_tags)))
        except OSError:
            continue
        for e in ents:
            r = e.name if not rel else rel + BS + e.name
            try:
                st = e.stat(follow_symlinks=False)
            except OSError:
                continue
            if st.st_file_attributes & FILE_ATTRIBUTE_REPARSE_POINT:
                reparse.append(r)
                continue
            if st.st_file_attributes & 0x10:
                if e.name.lower() == OFF_LIMITS or (not rel and e.name.lower() == 'temp_scenario'):
                    continue                                                           # Sapien's scratch scenarios are not part of any map
                dirs.append(r)
                stack.append(r)
            else:
                files.append((r, st.st_size))
                n += 1
                tot += st.st_size
    dirs.sort()
    files.sort()
    return {'dirs': dirs, 'files': files, 'reparse': reparse, 'total_files': n, 'total_bytes': tot}


def select_kit_files(scan, names=None, scen_dirs=(), extrel=frozenset()):
    """The kit files the overlay copy holds. names = map_tag_names() (None: the whole kit, as before 0.6.9); scen_dirs = tag folders (relative, any
    slash) whose files are all copied. A file the map's own tree (extrel, lower case) replaces is not copied from the kit.
    -> {'files': [(rel, size)], 'bytes': n, 'skipped': n, 'overridden': n, 'unused': n}"""
    pre = tuple(d.replace('/', BS).strip(BS).lower() + BS for d in scen_dirs if d)
    keep_dirs = tuple(d.lower() + BS for d in KEEP_DIRS)
    out, nbytes, skipped, over, unused = [], 0, 0, 0, 0
    for r, sz in scan['files']:
        low = r.lower()
        if low in extrel:
            over += 1
            continue
        if low.endswith(SKIP_EXT) or '(auto-backup' in low:
            skipped += 1
            continue
        if names is not None and BS in low and os.path.splitext(low)[0] not in names:
            if not low.startswith(keep_dirs) and not (low.startswith(pre) and not any(x in low for x in SKIP_IN_FOLDER)):
                unused += 1
                continue
        out.append((r, sz))
        nbytes += sz
    return {'files': out, 'bytes': nbytes, 'skipped': skipped, 'overridden': over, 'unused': unused}


def build_overlay(kit_tags, ext_tags, ov_tags, scan=None, sel=None):
    """REAL COPIES only: the selected kit tag files (default: every kit tag file except other maps' bsp/lightmap tags and archives) are copied to the overlay,
    then the tree read from the map is copied on top (the map's files win).  Nothing is hard-linked or junctioned; a reparse point in the kit is skipped and reported."""
    n_ext = 0
    t0 = time.time()
    if scan is None:
        scan = scan_kit(kit_tags)
    if sel is None:
        extrel = set()
        for dp, dn, fn in os.walk(ext_tags):
            for f in fn:
                extrel.add(os.path.relpath(os.path.join(dp, f), ext_tags).lower())
        sel = select_kit_files(scan, None, (), extrel)
    os.makedirs(lp(ov_tags), exist_ok=True)
    for d in scan['dirs']:
        os.makedirs(lp(os.path.join(ov_tags, d)), exist_ok=True)
    n_kit = 0
    reparse = list(scan['reparse'])
    # 0.6.12: COPY_THREADS copies at once (one file at a time ran at ~60 MB/s on an NVMe drive: small files are latency bound); the
    # progress line 'overlay: i of N kit tags copied' is read by the app's progress bar
    from concurrent.futures import ThreadPoolExecutor

    def _cp(r):
        shutil.copyfile(lp(os.path.join(kit_tags, r)), lp(os.path.join(ov_tags, r)))

    total, t_say = len(sel['files']), time.time()
    with ThreadPoolExecutor(max_workers=COPY_THREADS) as ex:
        for _ in ex.map(_cp, (r for r, _sz in sel['files'])):
            n_kit += 1
            if time.time() - t_say >= 2.0:
                t_say = time.time()
                say('  overlay: %d of %d kit tags copied (%.0f s)' % (n_kit, total, t_say - t0))
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
    return {'kit_copied': n_kit, 'kit_skipped': sel['skipped'], 'kit_unused': sel['unused'], 'kit_bytes_copied': sel['bytes'], 'map_copied': n_ext,
            'kit_overridden': sel['overridden'], 'reparse_skipped': reparse, 'seconds': round(time.time() - t0, 1)}


def assert_real_copies(root_dir):
    """the overlay holds no hard link and no reparse point (a stray write could otherwise reach the live tags)"""
    bad = []
    n = 0
    # 0.6.8 RC: walk the \\?\ spelling -- a plain os.walk silently skips every folder past MAX_PATH (it counted 454 files too few on a
    # 92-character H2EK root, and those folders were not checked for links at all)
    for dp, dn, fn in os.walk(lp(root_dir)):
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


def _is_staged(path):
    try:
        fd = os.open(lp(path), os.O_RDONLY | getattr(os, 'O_BINARY', 0))
        try:
            return os.read(fd, 8) == b'H2LMSTG\x01'
        finally:
            os.close(fd)
    except OSError:
        return False


def loose_files(root, min_size=1):
    out = []
    for dp, dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            try:
                if os.stat(lp(p)).st_size < min_size:
                    continue
            except OSError:
                continue
            if not _is_staged(p):
                out.append(p)
    return out


LIBFBXSDK_MD5 = '8146976216f143923aab24a9be73fdea'


def install_toolshim(base_src, root):
    shim = os.environ.get('H2LM_SHIM_DLL') or os.path.join(HERE, 'toolshim', 'libfbxsdk.dll')
    if not os.path.isfile(shim):
        raise MapbakeError('a required component of this build is missing (mapbake helper); reinstall the app')
    if md5_file(base_src) != LIBFBXSDK_MD5:
        raise MapbakeError('libfbxsdk.dll of this H2EK is not the supported build (md5 %s): verify the H2EK files in Steam, then bake again' % md5_file(base_src))
    shutil.copyfile(base_src, os.path.join(root, 'libfbxsdk_core.dll'))
    shutil.copyfile(shim, os.path.join(root, 'libfbxsdk.dll'))


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
    if a.root_name not in ('root', 'r'):
        raise MapbakeError('--root-name must be root or r')
    root = os.path.join(run, a.root_name)                                         # 0.6.8: 'r' (MAX_PATH headroom under WORK\\Temp (safe to delete)\\m<id>)
    logs = os.path.join(run, 'logs')
    for d in (tags_x, root):
        if os.path.exists(d):
            raise MapbakeError('%s already exists' % d)
    os.makedirs(logs, exist_ok=True)
    rep = {'schema': 'h2lm-mapbake-prepare/1', 'map': a.map, 'run': run}

    # ---- free space (the overlay is a real copy of the kit tags the map needs; 0.6.9: not the whole kit)
    t0 = time.time()
    # the map's tag-name table = its dependency closure; None (table unreadable, or H2LM_MAPBAKE_WHOLE_KIT set) = every kit tag, as before 0.6.9
    names = None if os.environ.get('H2LM_MAPBAKE_WHOLE_KIT') else map_tag_names(a.map)
    scan = scan_kit(kit)
    n_kit, kit_bytes = scan['total_files'], scan['total_bytes']
    free = shutil.disk_usage(run).free
    sel0 = select_kit_files(scan, names)                                          # before the map is read: its scenario folders are not known yet
    need = int(sel0['bytes'] * 1.05) + (3 << 30)
    say('kit tags: %d files, %.1f GB; the temporary copy holds %s%.2f GB; free on the run drive: %.1f GB (need about %.1f GB)' % (
        n_kit, kit_bytes / 1e9, ('the %d the map needs: ' % len(sel0['files'])) if names is not None else 'all of them: ', sel0['bytes'] / 1e9, free / 1e9, need / 1e9))
    if free < need:
        raise MapbakeError('not enough free disk space on the drive of %s: the temporary copy needs about %.1f GB, %.1f GB are free' % (run, need / 1e9, free / 1e9))
    rep['kit'] = {'files': n_kit, 'bytes': kit_bytes, 'free': free}
    # 0.6.12: the overlay path check runs after the map is read, on the files the copy will actually hold (see below)

    # ---- 1. read ONLY the dependency subset of the map (the lightmapper's closure: visual deps + placed objects)
    ext_report = os.path.join(logs, 'read_report.json')
    args = [a.map, tags_x, '--deps', 'visual', '--include', 'scen,bloc,mach,ctrl,lifi', '--kit', kit, '--kit-policy', 'identical', '--write-identical',
            '--force-output-dir', '--report', ext_report, '--quiet']
    for s in a.sky_cubemap or []:
        args += ['--sky-cubemap', s]
    if map_version(a.map) == 8:
        # a Vista map: h2conv reverse-converts it to its MCC form first; that temporary map lives in the run folder (kept for the inject, deleted with the run folder)
        args += ['--work-dir', os.path.join(run, 'h2work'), '--keep-temp']
        rep['vista_input'] = True
    rc, tail = run_h2conv(home, 'h2conv._ra', args, os.path.join(logs, 'read.log'), dirs=[tags_x, os.path.join(run, 'h2work')])
    if rc != 0 or not os.path.isfile(ext_report):
        raise MapbakeError('reading the map failed (exit %d): %s' % (rc, ' | '.join(tail[-3:])))
    if STAGED:
        clear = loose_files(tags_x) + loose_files(os.path.join(run, 'h2work'), min_size=1 << 16)
        if clear:
            for c in clear:
                try:
                    os.remove(lp(c))
                except OSError:
                    pass
            raise MapbakeError('preparing the map\'s files failed (%d file(s), the first %s); they were deleted. Bake again; if it repeats, reinstall the app'
                               % (len(clear), os.path.basename(clear[0])))
    er = json.load(open(ext_report, encoding='utf-8'))
    counts = er.get('counts', {})
    rep['read'] = {'counts': counts, 'seconds': er.get('seconds'), 'plan_warnings': (er.get('plan') or {}).get('warnings', [])[:20],
                      'sky_cubemap': er.get('sky_cubemap')}
    if STAGED:
        try:
            red = {k: v for k, v in er.items() if k not in ('tags',)}
            red['tags_listed'] = len(er.get('tags', []))
            red['note'] = 'the per-tag list is not kept'
            write_json(ext_report, red)
        except Exception:                                                          # noqa: BLE001
            pass
    if counts.get('failed'):
        bad = [t for t in er.get('tags', []) if t.get('status') == 'failed'][:5]
        raise MapbakeError('%d tag(s) of the map could not be read: %s' % (counts['failed'], '; '.join('%s:%s (%s)' % (b['group'], b['name'], (b.get('error') or '')[:80]) for b in bad)))
    scn = [t for t in er.get('tags', []) if t.get('group') == 'scnr']
    bsps = [t for t in er.get('tags', []) if t.get('group') == 'sbsp']
    if len(scn) != 1 or not bsps:
        raise MapbakeError('reading the map did not produce one scenario and its structure bsp(s) (scenario %d, bsp %d)' % (len(scn), len(bsps)))
    rep['scenario'] = scn[0]['name']
    rep['bsps'] = [b['name'] for b in bsps]
    # 0.6.9: the final selection -- the map's tag-name closure + the small shared folders + every file of the scenario's / bsp's folders
    ext_rel = set()
    for dp, dn, fn in os.walk(tags_x):
        for f in fn:
            ext_rel.add(os.path.relpath(os.path.join(dp, f), tags_x).lower())
    scen_dirs = sorted(set(os.path.dirname(t['file'].replace('/', BS)) for t in scn + bsps))
    # a sky cube map chosen from the KIT (the map holds none of its own) is not in the map's tag-name table, but the capture reads it
    extra = set()
    for ch in er.get('sky_cubemap') or []:
        for c in [ch.get('cubemap')] + list(ch.get('candidates') or []):
            if c:
                extra.add(str(c).replace('/', BS).lower())
    # 0.6.12: a Vista map is baked through its MCC form, which loads ~1,000 more tags than the Vista table names (the MCC globals' remastered
    # sounds and vehicles): its own table is added. Unreadable -> the whole kit, as before.
    names_src = 'map'
    if names is not None and rep.get('vista_input'):
        tmp = er.get('source_temp_mcc')
        mn = map_tag_names(tmp) if tmp and os.path.isfile(lp(tmp)) else None
        if mn is None:
            say('the tag list of the map\'s MCC form could not be read: every kit tag is copied')
            names = None
        else:
            names_src = 'map + its MCC form'
            names = names | mn
    # 0.6.12: cross-check -- every tag read from the map must be in the table (else the table is not this map's: copy the whole kit)
    if names is not None:
        read_names = set(str(t.get('name') or '').replace('/', BS).lower() for t in er.get('tags', [])) - {''}
        miss = sorted(read_names - names - extra)
        if len(miss) > max(2, len(read_names) // 100):
            say('the map\'s tag list misses %d of the %d tags read from it (e.g. %s): every kit tag is copied' % (len(miss), len(read_names), miss[0]))
            names = None
    sel = select_kit_files(scan, (names | extra) if names is not None else None, scen_dirs, ext_rel)
    # ---- 0.6.8 RC: every file of the overlay must stay openable by tool_fast, else the bake would silently miss textures
    # the capture reads root\tags (this is where the textures went missing); the skeleton run reads the same tags through the longer
    # root\w\_gpulm_skel\w2NN\tags, but a skeleton that differs from the capture is refused by gpu_bake's own soup / key guards.
    # 0.6.12: checked on the files the copy WILL hold (the kit selection + the tree read from the map), not on kit tags the map never loads
    ov_tags = os.path.join(root, 'tags')
    chosen = set(r.lower() for r, _sz in sel['files'])
    far, longest = overlay_path_check(ov_tags, [r for r, _sz in sel['files']] + sorted(ext_rel))
    ignored = sum(1 for r, _sz in scan['files'] if r.lower() not in chosen and len(ov_tags) + 1 + len(r) > MAX_TOOL_PATH)
    rep['overlay_paths'] = {'longest': longest[0], 'map_tag_names': len(names) if names is not None else None, 'names_from': names_src if names is not None else None,
                            'unused_past_limit': ignored}
    if far:
        over = far[0][0] - MAX_TOOL_PATH
        which = 'tag(s) this map uses' if names is not None else 'kit tag file(s) (the map\'s tag list could not be read, so every kit tag is copied)'
        raise MapbakeError('this H2EK is on too long a path for baking from a .map: in the temporary copy of the kit, %d %s would need more than '
                           '%d characters (the longest, %d characters: %s), which tool_fast cannot open, so the map would bake with missing textures. '
                           'Move the H2EK to a folder at least %d characters shorter (or shorten those tag folder names), then bake again. Nothing was written.'
                           % (len(far), which, MAX_TOOL_PATH, far[0][0], far[0][1], over))
    say('overlay paths: every %s stays within %d characters (longest %d)%s' % (
        'tag this map uses' if names is not None else 'kit tag', MAX_TOOL_PATH, longest[0],
        ('; %d kit tag file(s) this map does not use are past it (not copied, not needed)' % ignored) if ignored else ''))
    need = int(sel['bytes'] * 1.05) + (3 << 30)
    free = shutil.disk_usage(run).free
    say('overlay: %d kit tags (%.2f GB) will be copied%s; free on the run drive: %.1f GB (need about %.1f GB)' % (
        len(sel['files']), sel['bytes'] / 1e9, (', %d kit tag files this map does not need are left out' % sel['unused']) if names is not None else '', free / 1e9, need / 1e9))
    if free < need:
        raise MapbakeError('not enough free disk space on the drive of %s: the temporary copy needs about %.1f GB, %.1f GB are free' % (run, need / 1e9, free / 1e9))
    rep['overlay_plan'] = {'whole_kit': names is None, 'kit_files': len(sel['files']), 'kit_bytes': sel['bytes'], 'left_out': sel['unused'], 'folders_copied_whole': scen_dirs,
                           'need': need, 'free': free}
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
            if f == 'libfbxsdk.dll' and STAGED:
                install_toolshim(src, root)
                continue
            shutil.copyfile(src, os.path.join(root, f))
            if f == 'tool_fast.exe' and md5_file(src) != md5_file(os.path.join(root, f)):
                raise MapbakeError('the copy of tool_fast.exe does not verify')
    # tool_fast exits silently (code 127, before it logs anything) when these folders are missing from its root: they must exist
    for d in ('data', 'reports', 'shader_cache'):
        os.makedirs(os.path.join(root, d), exist_ok=True)
    ov = build_overlay(kit, tags_x, os.path.join(root, 'tags'), scan, sel)
    say('overlay: %d kit tags copied (%.2f GB), %d skipped (other maps\' bsp / lightmap tags), %d not needed by this map, %d tags from the map on top (%d kit files overridden) in %.0f s'
        % (ov['kit_copied'], ov['kit_bytes_copied'] / 1e9, ov['kit_skipped'], ov['kit_unused'], ov['map_copied'], ov['kit_overridden'], ov['seconds']))
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
    rep_json = os.path.join(logs, 'write_report.json')
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
    rc, tail = run_h2conv(home, 'h2conv._rc', args, os.path.join(logs, 'write.log'), dirs=[inj, os.path.join(run, 'h2work')])
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
    say('injected: %s' % (', '.join(replaced) if replaced else '(see write_report.json)'))
    added = (ir.get('build') or {}).get('tags_added') or []
    odd = [r for r in reps if r.get('group') not in ('sbsp', 'ltmp', 'bitm')]
    if added or odd or len(reps) != (2 if ltmp_only else 3):
        raise MapbakeError('the injection touched more than the lightmap and its bitmap (replaced %d, added %d: %s)'
                           % (len(reps), len(added), ', '.join(replaced)[:300]))
    # ---- validate the product (the MCC map h2conv made; a Vista result is validated too: header / directory / XOR checksum / tag table)
    vrc, vtail = run_h2conv(home, 'h2conv._rp', [out_tmp, '--kit', 'none', '--validate', '--out', os.path.join(logs, 'validate.json')], os.path.join(logs, 'validate.json.log'))
    vj = json.load(open(os.path.join(logs, 'validate.json'), encoding='utf-8')) if os.path.isfile(os.path.join(logs, 'validate.json')) else {}
    if vrc != 0 or vj.get('problems'):
        raise MapbakeError('the injected map did not validate: %s' % '; '.join((vj.get('problems') or vtail)[:5]))
    say('validate_file + plugin walk: OK')
    if vista_out:
        out_tmp_vista = vpath
        vrc, vtail = run_h2conv(home, 'h2conv._rp', [out_tmp_vista, '--kit', 'none', '--validate', '--out', os.path.join(logs, 'validate_vista.json')], os.path.join(logs, 'validate_vista.log'))
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
           'write_report': ir.get('counts') or ir.get('summary')}
    write_json(os.path.join(logs, 'inject_result.json'), res)
    if a.report:
        write_json(a.report, res)
    return 0


def cmd_validate(a):
    home = h2conv_home(a)
    out = a.out or os.path.join(os.path.dirname(os.path.abspath(a.map)), 'validate.json')
    rc, tail = run_h2conv(home, 'h2conv._rp', [a.map, '--kit', 'none', '--validate', '--out', out])
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


def _is_run_folder(run):
    low = run.lower().rstrip(os.sep) + os.sep
    base = os.path.basename(run.rstrip(os.sep)).lower()
    return base.startswith('m') and ((os.sep + '_mapbake' + os.sep) in low or (os.sep + 'temp (safe to delete)' + os.sep) in low)


def _links_below(top):
    out = []
    for dp, dn, fn in os.walk(lp(top)):
        keep = []
        for d in dn:
            full = os.path.join(dp, d)
            if is_reparse(full):
                out.append(full)
            else:
                keep.append(d)
        dn[:] = keep
        out += [os.path.join(dp, f) for f in fn if is_reparse(os.path.join(dp, f))]
    return out


def _target_inside(link, run):
    # the link's target, read without following it further; a symbolic link / unreadable target counts as outside (fail closed)
    try:
        t = os.readlink(link)
    except (OSError, ValueError):
        return False
    for pre in ('\\\\?\\', '\\??\\'):
        if t.startswith(pre):
            t = t[len(pre):]
    t = os.path.normcase(os.path.abspath(t)).rstrip(os.sep)
    r = os.path.normcase(os.path.abspath(run)).rstrip(os.sep)
    return t == r or t.startswith(r + os.sep)


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
    if not _is_run_folder(run):
        raise MapbakeError('cleanup only deletes a .map run folder (_mapbake\\m* or Temp (safe to delete)\\m*): %s' % run)
    if not os.path.isdir(run):
        say('nothing to clean up (%s)' % run)
        return 0
    keep = set(k.lower() for k in (a.keep or []))
    unlinked = 0
    rest = []
    # a file another program holds for a moment (a virus scanner, the search indexer, a monitor that opened it) cannot be deleted just then: try again a few times
    for attempt in range(8):
        for n in os.listdir(run):
            p = os.path.join(run, n)
            if n.lower() in keep:
                continue
            try:
                if is_reparse(p):
                    if not _target_inside(p, run):
                        raise MapbakeError('tripwire: %s is a link that does not point inside the run folder -- nothing more is deleted' % p)
                    try:
                        os.rmdir(p)
                    except OSError:
                        os.unlink(p)
                    unlinked += 1
                elif os.path.isdir(p):
                    bad = [x for x in _links_below(p) if not _target_inside(x, run)]
                    if bad:
                        raise MapbakeError('tripwire: %s holds a link that does not point inside the run folder (%s) -- it is not deleted' % (p, bad[0]))
                    unlinked += unlink_reparse_points(p)
                    if _links_below(p):
                        raise MapbakeError('a link is still inside %s after the unlink -- it is not deleted' % p)
                    rmtree_force(p)
                else:
                    os.remove(lp(p))
            except OSError:
                pass
        rest = [n for n in os.listdir(run) if n.lower() not in keep]
        if not rest:
            break
        time.sleep(1 + attempt)
    if rest:
        left = []
        for n in rest:
            for dp, dn, fn in os.walk(os.path.join(run, n)):
                left += [os.path.join(dp, f) for f in fn]
            if not left:
                left.append(os.path.join(run, n))
        say('WARNING: %s is not empty after %d tries: %d file(s) are still there (held by another program?), for example %s' % (run, attempt + 1, len(left), ', '.join(left[:3])))
        return 1
    say('cleaned up %s (%d link(s) unlinked, kept: %s)' % (run, unlinked, ', '.join(sorted(keep)) or 'nothing'))
    return 0


def cmd_wipefarm(a):
    wipe_farm(a.farm, keep_stage=a.keep_stage)
    return 0


FARM_KEEP_EXT = ('.log', '.txt', '.json', '.exe', '.dll')           # what stays in a worker's farm folder: logs / reports / the engine copies; every derived data file goes
FARM_DROP_NAMES = ('guard_usedset.txt',)                      # a .txt that lists the tags the map uses: not kept


def wipe_farm(farm, keep_stage=False):
    """Delete the derived geometry / texture products a bake left in a worker's farm folder (the capture: triangles, bitmaps, atlases ...; the lightmapper's own copy of what was
    read from the map).  Logs, reports and the engine copies stay.  A map bake never reuses a capture, so nothing depends on them."""
    farm = os.path.abspath(farm)
    base = os.path.basename(farm).lower()
    if not (base.startswith('farm_w') and base[6:].isdigit()):
        raise MapbakeError('wipe-farm only wipes a farm_wNN folder: %s' % farm)
    if not os.path.isdir(farm):
        return
    n = nbytes = 0
    for dp, dn, fn in os.walk(farm):
        dn[:] = [d for d in dn if not is_reparse(os.path.join(dp, d)) and not (keep_stage and d.lower() == 'tagw_stage' and dp == farm)]      # the engine copies (.exe / .dll) in any folder stay; the estimator's geometry dumps beside them go
        for f in fn:
            if f.lower().endswith(FARM_KEEP_EXT) and f.lower() not in FARM_DROP_NAMES:
                continue
            p = os.path.join(dp, f)
            try:
                nbytes += os.stat(lp(p)).st_size
                os.remove(lp(p))
                n += 1
            except OSError:
                pass
    for d in sorted((x[0] for x in os.walk(farm, topdown=False)), key=len, reverse=True):
        if d != farm and not is_reparse(d):
            try:
                os.rmdir(lp(d))
            except OSError:
                pass
    say('removed %d derived file(s) (%.0f MB) from %s' % (n, nbytes / 1e6, farm))


def cmd_scan(a):
    """list the files below ROOT that look like a Halo 2 tag ('!MLB' 'dfbt' at 0x3C) or a map ('daeh'); exit 1 when any is found"""
    root = os.path.abspath(a.root)
    bad = 0
    n = 0
    for dp, dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            try:
                fd = os.open(lp(p), os.O_RDONLY | getattr(os, 'O_BINARY', 0))
                try:
                    head = os.read(fd, 128)
                finally:
                    os.close(fd)
            except OSError:
                continue
            n += 1
            if (len(head) >= 0x44 and head[0x3C:0x44] == b'!MLBdfbt') or head[:4] == b'daeh':
                bad += 1
                say('TAG/MAP SIGNATURE: %s' % p)
    say('scan: %d files, %d with a tag / map signature' % (n, bad))
    return 1 if bad else 0


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
    p.add_argument('--sky-cubemap', action='append', default=[]); p.add_argument('--report'); p.add_argument('--root-name', default='root')
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
    p = sub.add_parser('wipefarm')
    p.add_argument('--farm', required=True); p.add_argument('--keep-stage', action='store_true')
    p.set_defaults(fn=cmd_wipefarm)
    p = sub.add_parser('scan')
    p.add_argument('--root', required=True)
    p.set_defaults(fn=cmd_scan)
    a = ap.parse_args(argv)
    try:
        return a.fn(a)
    except MapbakeError as e:
        sys.stdout.write('mapbake| REFUSED: %s\n' % e)
        return 2


if __name__ == '__main__':
    sys.exit(main())
