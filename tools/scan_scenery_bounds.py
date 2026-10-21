"""scan_scenery_bounds.py -- keep SceneryFixDependencies.db (and the legacy h2v_scenery_bounds.db) in step with the H2EK's tags.

The Osoyoos scenery fix (H2ToolHooks.dll, add_occluder_vertex 0x49DA6A / emissive 0x499603) decompresses a scenery section's
vertices with the model's position compression bound. Older hooks looked the bound up by the MCC key (vertex count, part
count, strip-index total) only. Dozens of stock models (granite boulders, bushes, trees, 2way teleporters ...) share a key
with a model of a DIFFERENT size, so one of the two was decompressed at the wrong scale. Hook v5 (SFD) identifies the render
model by its TAG NAME at run time, so SceneryFixDependencies.db is keyed by (render_model path, section key) and has no
collisions. The legacy file keeps the key-only format for the old DLL.

SceneryFixDependencies.db line (hook v5; the first 22 columns are the legacy columns, so a legacy parser reads it too):
    <14 section words> <xmin xmax ymin ymax zmin zmax> <ambig> <striptot> <part0 box x6> | <render_model tag path, no extension>
  ambig = 1 when another model uses the same (vertex count, part count, strip total) key with a different bound (the name
  is what tells them apart); 2 = the section is NOT position-compressed (bounds are zero; the hook leaves it alone instead of
  guessing a bound from its key). part0 box = the tag's first-part position box (all zero for 'Geometry Postprocessed' models).
Legacy h2v_scenery_bounds.db line:  <14 words> <6 bounds> <ambig> <striptot>

What this does, per scanned render_model (tags-relative path):
  ok        every compressed section has a SFD line with the same name + key + bound
  missing   a compressed section has no line (or its bound differs: 'updated') -> --apply appends it
  conflict  TRUE conflict only: two sections of ONE model share a key but have different bounds (a name cannot tell them
            apart). Reported, still appended; exit 3.
  nobound   compressed sections whose bound could not be read from the tag (the DB cannot fix those)
Sky models are skipped (0.6.4): a sky never needs the scenery fix (it is not scenery; no lightmap occluder is built from it), so every render_model that a .sky
tag reaches (sky -> .model -> .render_model, or a direct render_model reference) or that lives in a folder named 'sky' / 'skies' is left out of the scan, the
database and the report; the report lists them under sky_excluded. With --scenario the scenario's own skies block (its .sky references) is read; a full scan reads
every .sky tag under tags\\. --include-sky turns the skip off.
First run (no named lines in SceneryFixDependencies.db, e.g. absent or a copy of the legacy file): the file is SEEDED from a
full scan of every render_model under tags\\ ; --scenario then only decides which models are reported as 'added'.
The legacy file is kept in step (models of the scanned set whose key+bound is not in it and does not clash with another bound
for that key); disable with --no-legacy. Every file that changes is first copied to <file>.bak_<timestamp>; lines are only
ever appended (existing lines stay byte-for-byte), via a temp file + os.replace.

Usage:
  py scan_scenery_bounds.py <root> [--db SFD_PATH] [--legacy-db PATH] [--no-legacy] [--json OUT] [--apply]
                            [--allow-conflicts] [--scenery-only] [--include-sky] [--scenario tags-relative-scenario] [subdir ...]
    <root>   the folder that CONTAINS tags\\ (the H2EK root; the hook reads <root>\\SceneryFixDependencies.db)
    --db     the SceneryFixDependencies.db to compare with / update (default <root>\\SceneryFixDependencies.db). A path whose
             file name is h2v_scenery_bounds.db is taken as --legacy-db (old callers) and the SFD file goes next to it.
    --scenario  scan (and report) only the render_models that scenario's scenery places: scenario -> .scenery -> .model -> .render_model
Exit: 0 nothing missing (or applied), 1 entries missing (not applied), 2 usage, 3 true conflicts present.
"""
import os, sys, json, struct, shutil, time, argparse, re

SFD_NAME = 'SceneryFixDependencies.db'
LEGACY_NAME = 'h2v_scenery_bounds.db'


def valid_bounds(f):
    return (f[0] < f[1] and f[2] < f[3] and f[4] < f[5] and all(-1e5 < x < 1e5 for x in f[:6])
            and (f[1] - f[0]) < 4000 and (f[3] - f[2]) < 4000 and (f[5] - f[4]) < 4000)


def secwords_ok(w):
    cls, pad, vc, tris, parts, stris, sparts, opq, ovc, opc, nodes, srt, cls2, flags = w
    return (cls <= 2 and pad == 0 and 0 < vc < 50000 and 0 < tris < 50000 and 1 <= parts <= 128 and stris <= tris
            and sparts <= parts and opq <= vc and ovc <= vc and opc <= parts and (nodes & 0xFF) <= 8 and (nodes >> 8) <= 8
            and srt <= tris and cls2 <= 2 and flags <= 7)


def parse_render_model(d):
    """-> (sections, status). sections = [dict(w=words14, st=striptot, b=bound6|None, p0=part0box6|None, comp=bool)], one per
    section; comp=False = position not compressed (b is None). Same bound rules as generate_scenery_bounds_db.py (per-section
    compression record, else the root record)."""
    blocks = []
    i = -1
    while True:
        i = d.find(b'dfbt', i + 1)
        if i < 0 or i + 16 > len(d):
            break
        _, count, esz = struct.unpack_from('<3I', d, i + 4)
        blocks.append((i, count, esz))
    sec = None
    for (pos, count, esz) in blocks:
        if esz == 0x68 and count >= 1 and pos + 16 + count * 0x68 <= len(d):
            ws = [struct.unpack_from('<14H', d, pos + 16 + k * 0x68) for k in range(count)]
            if all(secwords_ok(w) for w in ws):
                sec = (pos, count, ws)
                break
    if not sec:
        return [], 'nosec'
    spos, scount, swords = sec
    root = None
    for (pos, count, esz) in blocks:
        if pos >= spos:
            break
        if esz == 0x38 and count >= 1:
            f = struct.unpack_from('<10f', d, pos + 16)
            if valid_bounds(f):
                root = f[:6]
    persec = [None] * scount
    striptot = [0] * scount
    p0 = [None] * scount
    j = 0
    lastcomp = None
    segok = True
    for (pos, count, esz) in blocks:
        if pos <= spos:
            continue
        if esz == 0x38 and count >= 1:
            f = struct.unpack_from('<10f', d, pos + 16)
            if valid_bounds(f):
                lastcomp = f[:6]
        elif esz == 0x48 and count >= 1:
            if j < scount and count == swords[j][4]:
                persec[j] = lastcomp
                lastcomp = None
                striptot[j] = sum(struct.unpack_from('<H', d, pos + 16 + k * 0x48 + 8)[0] for k in range(count))
                p0[j] = struct.unpack_from('<6f', d, pos + 16 + 0x30)          # part[0] position box (tag part +0x30..0x44)
                j += 1
            elif j < scount:
                segok = False
    if j != scount:
        segok = False
    out = []
    for k, w in enumerate(swords):
        comp = bool(w[13] & 1)
        if not comp:                                   # position NOT compressed: the hook must leave it alone (SFD marker row)
            box0 = p0[k] if (segok and p0[k] is not None and all(abs(x) < 1e5 for x in p0[k])) else None
            out.append(dict(w=tuple(w), st=striptot[k] if segok else 0, b=None, comp=False,
                            p0=None if box0 is None else tuple(round(x, 6) for x in box0)))
            continue
        if segok and persec[k] is not None:
            b = persec[k]
        elif root is not None:
            b = root
        else:
            b = None
        box = p0[k] if (segok and p0[k] is not None and all(abs(x) < 1e5 for x in p0[k])) else None
        out.append(dict(w=tuple(w), st=striptot[k] if segok else 0,
                        b=None if b is None else tuple(round(x, 6) for x in b), comp=True,
                        p0=None if box is None else tuple(round(x, 6) for x in box)))
    return out, 'ok'


_PATH_RE = None


def tag_path_strings(d):
    """Every null-terminated tags-relative path string in a tag file (H2EK tag references store the path text after the
    struct data, e.g. 'h1\\\\vehicles\\\\lifepod\\\\lifepod')."""
    global _PATH_RE
    if _PATH_RE is None:
        _PATH_RE = re.compile(rb'([A-Za-z0-9_ .()\-]+(?:\\[A-Za-z0-9_ .()\-]+)+)\x00')
    return {m.group(1).decode('ascii', 'replace').lower() for m in _PATH_RE.finditer(d)}


def scenario_render_models(tags, scenario_rel):
    """-> (sorted tags-relative .render_model paths the scenario's SCENERY places, [scenery tags], [unresolved .scenery])
    Chain: scenario -> *.scenery (the lightmapper only bakes the scenario scenery block) -> *.model -> *.render_model."""
    def rd(rel):
        try:
            return open(os.path.join(tags, rel), 'rb').read()
        except OSError:
            return None
    srel = scenario_rel if scenario_rel.lower().endswith('.scenario') else scenario_rel + '.scenario'
    d = rd(srel)
    if d is None:
        raise FileNotFoundError(os.path.join(tags, srel))
    scen = sorted(p + '.scenery' for p in tag_path_strings(d) if os.path.isfile(os.path.join(tags, p + '.scenery')))
    rms, bad = set(), []
    for s in scen:
        sd = rd(s) or b''
        models = [p for p in tag_path_strings(sd) if os.path.isfile(os.path.join(tags, p + '.model'))]
        got = False
        for m in models:
            md = rd(m + '.model') or b''
            for p in tag_path_strings(md):
                if os.path.isfile(os.path.join(tags, p + '.render_model')):
                    rms.add(p + '.render_model')
                    got = True
        if not got:
            bad.append(s)
    return sorted(rms), scen, bad


SKY_DIRS = ('sky', 'skies')


def is_sky_path(rel):
    """True when a tags-relative path has a folder named exactly 'sky' or 'skies' (the stock layout: scenarios\\skies\\...)"""
    parts = rel.replace('/', '\\').lower().split('\\')[:-1]
    return any(p in SKY_DIRS for p in parts)


def scenario_sky_tags(tags, scenario_rel):
    """-> tags-relative .sky tags the scenario's skies block references (found as path strings, like scenario_render_models does)"""
    srel = scenario_rel if scenario_rel.lower().endswith('.scenario') else scenario_rel + '.scenario'
    try:
        d = open(os.path.join(tags, srel), 'rb').read()
    except OSError:
        return []
    return sorted(p + '.sky' for p in tag_path_strings(d) if os.path.isfile(os.path.join(tags, p + '.sky')))


def sky_render_models(tags, sky_tag_rels):
    """-> set of lowercase tags-relative .render_model paths that the given .sky tags use: sky -> .model -> .render_model, or a direct
    .render_model reference"""
    out = set()

    def rd(rel):
        try:
            return open(os.path.join(tags, rel), 'rb').read()
        except OSError:
            return b''
    for s in sky_tag_rels:
        for p in tag_path_strings(rd(s)):
            if os.path.isfile(os.path.join(tags, p + '.render_model')):
                out.add((p + '.render_model').lower())
            if os.path.isfile(os.path.join(tags, p + '.model')):
                for q in tag_path_strings(rd(p + '.model')):
                    if os.path.isfile(os.path.join(tags, q + '.render_model')):
                        out.add((q + '.render_model').lower())
    return out


def canon(rel):
    """canonical model name the hook compares: tags-relative, lowercase, backslashes, no .render_model extension"""
    r = rel.replace('/', '\\').lower()
    if r.endswith('.render_model'):
        r = r[:-len('.render_model')]
    return r


def fmt6(b):
    return " ".join(f"{x:.6f}" for x in b)


def fmt_legacy(w, b, st, amb=0):
    return " ".join(str(x) for x in w) + " " + fmt6(b) + f" {amb} {st}"


def fmt_sfd(w, b, st, amb, p0, name):
    return (" ".join(str(x) for x in w) + " " + fmt6(b) + f" {amb} {st} " + fmt6(p0 if p0 else (0.0,) * 6) + " | " + name)


def same6(a, b, tol=1e-3):
    return max(abs(x - y) for x, y in zip(a, b)) < tol


def same_p0(a, b):
    za = a is None or all(abs(x) < 1e-6 for x in a)
    zb = b is None or all(abs(x) < 1e-6 for x in b)
    if za and zb:
        return True
    if za != zb:
        return False
    return same6(a, b)


class Db:
    """a DB file parsed into raw lines + indexes (named lines: by model name; every line: by MCC key)"""
    def __init__(self, path):
        self.path = path
        self.exists = os.path.exists(path)
        self.lines, self.nl = [], '\r\n'
        self.named = {}     # name -> [(key, bound, p0, unc)]   unc = SFD 'section not compressed' marker row (ambig column 2)
        self.anon = {}      # (vc, parts, st) -> [bound]   (every bound-carrying line, named or not)
        if self.exists:
            raw = open(path, 'rb').read()
            self.nl = '\r\n' if b'\r\n' in raw else '\n'
            self.lines = raw.decode('latin-1').splitlines()
            for ln in self.lines:
                self._index(ln)

    def _index(self, ln):
        if not ln.strip() or ln.lstrip().startswith('#'):
            return
        head, sep, name = ln.partition('|')
        p = head.split()
        if len(p) < 22:
            return
        try:
            w = [int(x) for x in p[:14]]
            b = tuple(float(x) for x in p[14:20])
            st = int(p[21])
        except ValueError:
            return
        key = (w[2], w[4], st)
        unc = sep and p[20] == '2'
        if not unc:
            self.anon.setdefault(key, []).append(b)
        if sep:
            p0 = tuple(float(x) for x in p[22:28]) if len(p) >= 28 else None
            self.named.setdefault(name.strip().lower(), []).append((key, b, p0, bool(unc)))

    def add_lines(self, new):
        for ln in new:
            self._index(ln)
        self.lines += new

    @property
    def n_named_lines(self):
        return sum(len(v) for v in self.named.values())

    def write(self, extra, header=None):
        """append `extra` lines (after an optional header comment); backup first; temp file + replace. -> backup path or None"""
        bak = None
        if self.exists:
            bak = f"{self.path}.bak_{time.strftime('%Y%m%d_%H%M%S')}"
            shutil.copy2(self.path, bak)
        body_lines = self.lines + (header or []) + extra
        body = self.nl.join(body_lines) + self.nl
        tmp = self.path + '.tmp'
        with open(tmp, 'wb') as f:
            f.write(body.encode('latin-1'))
        os.replace(tmp, self.path)
        return bak


SFD_HEADER = [
    "# SceneryFixDependencies.db -- scenery lightmap-fix dependency database (Osoyoos H2ToolHooks v5 / H2 GPU Lightmapper).",
    "# One line per compressed render_model section:",
    "#   <14 section words> <xmin xmax ymin ymax zmin zmax = model position bound> <ambig> <striptot> <part0 box x6> | <render_model tag path>",
    "# The hook identifies the model by its tag name at run time, then matches (vertex count, part count, strip total).",
    "# Written by scan_scenery_bounds.py (append-only; a timestamped .bak is made before every change). Safe to delete: it is rebuilt.",
]


def main(argv):
    try:
        sys.stdout.reconfigure(errors='replace')
    except (AttributeError, ValueError):
        pass
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('root')
    ap.add_argument('subdirs', nargs='*')
    ap.add_argument('--db')
    ap.add_argument('--legacy-db')
    ap.add_argument('--no-legacy', action='store_true')
    ap.add_argument('--json')
    ap.add_argument('--apply', action='store_true')
    ap.add_argument('--allow-conflicts', action='store_true')    # kept for old callers (legacy file: append clashing keys anyway)
    ap.add_argument('--scenery-only', action='store_true')
    ap.add_argument('--include-sky', action='store_true', help='also scan sky render_models (skipped by default: skies never need the scenery fix)')
    ap.add_argument('--scenario', help='tags-relative scenario path: scan only the render_models its scenery places')
    a = ap.parse_intermixed_args(argv)
    tags = os.path.join(a.root, 'tags')
    if not os.path.isdir(tags):
        print(f"no tags folder under {a.root}")
        return 2
    sfd_path = a.db or os.path.join(a.root, SFD_NAME)
    legacy_path = a.legacy_db or os.path.join(a.root, LEGACY_NAME)
    if a.db and os.path.basename(a.db).lower() == LEGACY_NAME:       # an old caller's --db was the legacy file
        legacy_path = a.db
        sfd_path = os.path.join(os.path.dirname(a.db), SFD_NAME)
    sfd = Db(sfd_path)
    leg = None if a.no_legacy else Db(legacy_path)
    seeding = sfd.n_named_lines == 0                                   # absent, empty or a legacy-format copy: needs the named lines
    t0 = time.time()

    def scan(rel_paths):
        res = []
        for rel in rel_paths:
            try:
                d = open(os.path.join(tags, rel), 'rb').read()
            except OSError:
                res.append((rel, 'unreadable', []))
                continue
            secs, st = parse_render_model(d)
            res.append((rel, st, secs))
        return res

    sky_tags = []                                                      # every .sky tag the walk met (tags-relative)

    def walk(roots):
        out = []
        for r in roots:
            for dirpath, _, files in os.walk(r):
                for fn in files:
                    if fn.lower().endswith('.sky'):
                        sky_tags.append(os.path.relpath(os.path.join(dirpath, fn), tags))
                if a.scenery_only and not any(f.lower().endswith('.scenery') for f in files):
                    continue
                for fn in files:
                    if fn.lower().endswith('.render_model'):
                        out.append(os.path.relpath(os.path.join(dirpath, fn), tags))
        return out

    scen_info = None
    scope = None                                                       # the rels the user asked about (None = everything scanned)
    if a.scenario:
        try:
            rms, scen, bad = scenario_render_models(tags, a.scenario)
        except FileNotFoundError as e:
            print(f"scenario not found: {e}")
            return 2
        scen_info = dict(scenario=a.scenario, scenery_tags=scen, render_models=rms, unresolved_scenery=bad)
        scope = set(m.lower() for m in rms)
        rels = list(rms)
        if seeding:                                                    # first run: seed the named lines from the whole tree
            seen = set(m.lower() for m in rms)
            rels += [r for r in walk([tags]) if r.lower() not in seen]
    else:
        rels = walk([os.path.join(tags, s) for s in a.subdirs] if a.subdirs else [tags])
    sky_excluded = []
    if not a.include_sky:
        if a.scenario:
            sky_tags = sorted(set(sky_tags) | set(scenario_sky_tags(tags, a.scenario)))
        skyset = sky_render_models(tags, sky_tags)
        keep = []
        for r in rels:
            (sky_excluded if (r.lower() in skyset or is_sky_path(r)) else keep).append(r)
        rels = keep
    models = scan(rels)

    report = dict(root=a.root, db=sfd_path, legacy_db=None if a.no_legacy else legacy_path, db_lines=len(sfd.lines),
                  models=len(models), ok=[], missing=[], conflict=[], nobound=[], updated=[],
                  nosec=[m[0] for m in models if m[1] != 'ok'], seeded=False, sky_excluded=sorted(sky_excluded, key=str.lower))
    # key usage over the scan + the existing named lines: informational (the name resolves every one of these)
    key_models = {}
    for rel, st, secs in models:
        for s in secs:
            if s['b'] is not None:
                key_models.setdefault((s['w'][2], s['w'][4], s['st']), {}).setdefault(canon(rel), s['b'])
    for name, lst in sfd.named.items():
        for key, b, _, unc in lst:
            if not unc:
                key_models.setdefault(key, {}).setdefault(name, b)
    key_collisions = 0
    for key, d in key_models.items():
        bl = list(d.values())
        if any(not same6(bl[0], x) for x in bl[1:]):
            key_collisions += 1
    report['key_collisions'] = key_collisions                          # keys shared by models with DIFFERENT bounds (name-resolved)

    add_sfd, add_leg = [], []
    added_models, also_added, updated_models, named_only = [], [], [], []
    new_named = {}                    # name -> [(key, bound, p0)] lines added in this run (dedupe)
    new_leg = {}                      # key -> [bound]
    legacy_clash_models = {}
    for rel, st, secs in models:
        if st != 'ok' or not secs:
            continue
        name = canon(rel)
        in_scope = scope is None or rel.lower() in scope
        have = sfd.named.get(name, [])
        miss = upd = conf = nob = new_info = 0
        seen_here = {}                # key -> bound within this model (true-conflict detection)
        for s in secs:
            w, b, p0, stot = s['w'], s['b'], s['p0'], s['st']
            key = (w[2], w[4], stot)
            if not s['comp']:                                          # uncompressed section: a marker row tells the hook to leave it alone
                cur = have + new_named.get(name, [])
                if not any(h[0] == key and h[3] for h in cur):
                    add_sfd.append(fmt_sfd(w, (0.0,) * 6, stot, 2, p0, name))
                    new_named.setdefault(name, []).append((key, (0.0,) * 6, p0, True))
                    miss += 1
                    if not have:
                        new_info += 1
                continue
            if b is None:
                nob += 1
                continue
            if key in seen_here and not same6(seen_here[key], b):
                conf += 1                                              # a name cannot tell these two sections apart
            seen_here.setdefault(key, b)
            # --- the named (SFD) line ---
            cur = have + new_named.get(name, [])
            known = sfd.anon.get(key, []) + (leg.anon.get(key, []) if leg is not None else [])
            if not any(h[0] == key and not h[3] and same6(h[1], b) for h in cur):
                if any(h[0] == key and not h[3] for h in have):
                    upd += 1                                           # the tag changed since the line was written
                else:
                    miss += 1
                amb = int(any(not same6(x, b) for x in known) or
                          any(not same6(x, b) for x in key_models.get(key, {}).values()))
                add_sfd.append(fmt_sfd(w, b, stot, amb, p0, name))
                new_named.setdefault(name, []).append((key, b, p0, False))
                if not have and not any(same6(x, b) for x in known):
                    new_info += 1                                      # no line of ANY kind gave this bound before
            # --- the legacy line (key only) ---
            if leg is not None:
                lh = leg.anon.get(key, []) + new_leg.get(key, [])
                if not any(same6(x, b) for x in lh):
                    clash = bool(lh) or any(not same6(x, b) for x in key_models.get(key, {}).values())
                    if clash and not a.allow_conflicts:
                        e = legacy_clash_models.setdefault(rel, dict(model=rel, sections=len(secs), conflict=0))
                        e['conflict'] += 1
                    elif in_scope:
                        add_leg.append(fmt_legacy(w, b, stot))
                        new_leg.setdefault(key, []).append(b)
        entry = dict(model=rel, sections=len(secs), missing=miss, updated=upd, conflict=conf, nobound=nob)
        if conf:
            report['conflict'].append(entry)
        elif miss or upd:
            report['missing'].append(entry)
        elif nob:
            report['nobound'].append(entry)
        else:
            report['ok'].append(entry)
        if upd:
            updated_models.append(rel)
        if new_info:
            (added_models if in_scope else also_added).append(rel)
        elif miss and not have:
            named_only.append(rel)
    report['added_models'] = added_models
    report['also_added_models'] = also_added
    report['updated_models'] = updated_models
    report['named_only_models'] = len(named_only)
    report['legacy_conflict'] = list(legacy_clash_models.values())
    report['lines_to_add'] = len(add_sfd)
    report['legacy_lines_to_add'] = len(add_leg)
    report['seeding'] = seeding
    if scen_info:
        report.update(scen_info)
    report['scan_seconds'] = round(time.time() - t0, 2)
    print(f"scanned {len(models)} render_models in {report['scan_seconds']} s against {sfd_path} ({len(sfd.lines)} lines"
          f"{', no named lines yet: SEEDING from the whole tags folder' if seeding else ''})")
    print(f"  ok {len(report['ok'])}   missing/updated {len(report['missing'])}   true conflicts {len(report['conflict'])}   "
          f"no bound {len(report['nobound'])}   unparsed {len(report['nosec'])}   -> {len(add_sfd)} line(s) to add"
          f"   [{key_collisions} shared key(s) with different bounds, told apart by tag name]")
    if sky_excluded:
        print(f"  skipped {len(sky_excluded)} sky render_model(s): a sky never needs the scenery fix")
    for m in added_models[:60]:
        print(f"  NEW MODEL  {m}  (was not in our database)")
    if len(added_models) > 60:
        print(f"  NEW MODEL  ... and {len(added_models) - 60} more (all listed in the --json report)")
    if also_added:
        print(f"  NEW MODEL  {len(also_added)} more render_model(s) outside the scenario's scenery were not in the database either")
    for m in updated_models:
        print(f"  UPDATED    {m}  (the tag's bound changed since its database line was written)")
    for e in report['conflict']:
        print(f"  CONFLICT   {e['model']}  ({e['conflict']} section(s) share a key with a different bound inside the SAME model)")
    if leg is not None:
        print(f"  legacy {legacy_path}: {len(add_leg)} line(s) to add, {len(legacy_clash_models)} model(s) share a key with another model's bound (left to the named database)")
    rc = 0
    if a.apply:
        if add_sfd:
            stamp = [f"# --- {len(add_sfd)} entr{'y' if len(add_sfd) == 1 else 'ies'} added by scan_scenery_bounds.py "
                     f"{time.strftime('%Y-%m-%d %H:%M:%S')}" + (f" (scenario {a.scenario})" if a.scenario else "") + " ---"]
            if not any(l.startswith('# SceneryFixDependencies.db') for l in sfd.lines):
                stamp = SFD_HEADER + stamp
            kept = len(sfd.lines)
            bak = sfd.write(add_sfd, stamp)
            print(f"  wrote {sfd_path}: {kept} kept + {len(add_sfd)} added" + (f" (backup {bak})" if bak else " (new file)"))
            report['applied'] = True
            report['seeded'] = seeding
            report['backup'] = bak
        if leg is not None and add_leg:
            kept = len(leg.lines)
            bak = leg.write(add_leg, None)
            print(f"  wrote {legacy_path}: {kept} kept + {len(add_leg)} added" + (f" (backup {bak})" if bak else " (new file)"))
            report['legacy_applied'] = True
    elif add_sfd or add_leg:
        rc = 1
    if report['conflict']:
        rc = 3
    if a.json:
        with open(a.json, 'w') as f:
            json.dump(report, f, indent=1)
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
