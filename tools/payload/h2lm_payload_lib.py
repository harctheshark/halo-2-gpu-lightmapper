"""h2lm_payload_lib.py -- shared helpers of the S1.9 payload builder (stage_engine.py, zip_payloads.py, build_payload.py).
Stdlib only. Never writes outside the paths its callers pass; never touches production (callers only READ production)."""
import hashlib
import importlib.util
import io
import json
import lzma
import os
import stat
import sys
import zipfile

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(os.path.dirname(HERE))          # ...\H2 GPU Lightmapper
SPEC = os.path.join(HERE, 'payload_spec.json')
BUILD = os.path.join(PROJ, 'build', 's19')
if os.environ.get('H2LM_BUILD_DIR'):                    # dev builds (task SPLITBSP, 2026-10-21): a build tree of its own, so build\s19 (the shipped record) is never touched. Unset = as before.
    BUILD = os.path.abspath(os.environ['H2LM_BUILD_DIR'])
CHUNK = 1 << 20
ZIP_DT = (1980, 1, 1, 0, 0, 0)
ZIP_ATTR = 0o644 << 16
PRISTINE_SLOT = b'G:\\SteamLibrary\\steamapps\\common\\H2EKMine\\GPU Lightmap Work\\'   # 60 chars (S0.4)


class Refused(Exception):
    """a build input is not what was reviewed / tested: nothing is published (exit 2)"""


def below_normal():
    try:
        import ctypes
        k = ctypes.windll.kernel32
        k.SetPriorityClass(k.GetCurrentProcess(), 0x4000)   # BELOW_NORMAL; children inherit it
    except Exception:
        pass


def md5_bytes(b):
    return hashlib.md5(b).hexdigest()


def md5_file(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(CHUNK), b''):
            h.update(b)
    return h.hexdigest()


def sha256_file(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(CHUNK), b''):
            h.update(b)
    return h.hexdigest()


def load_module(name, path):
    """import a project tool by path (never via sys.path, so nothing else is picked up); no bytecode is written"""
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def load_spec(path=SPEC):
    s = json.load(open(path, 'r', encoding='utf-8'))
    if s.get('format') != 'h2lm-payload-spec/1':
        raise Refused('spec %s: format %r' % (path, s.get('format')))
    if isinstance(s.get('roots'), dict) and s['roots'].get('GLW'):
        s['roots']['GLW'] = resolve_glw(s['roots']['GLW'])
    return s


def resolve_glw(glw):
    # 0.6.8 (2026-10-29): the production code tree ("GLW") no longer has to sit directly in GPU Lightmap Work. H2LM_GLW overrides the
    # spec; else the spec's folder when it holds engine_v373; else the ONE direct subfolder of it that does (the dev layout of 2026-10-29:
    # GPU Lightmap Work\H2GPULM (Max Size - 100GBs)\engine_v373). Two candidates refuse (ambiguous). Read only.
    env = os.environ.get('H2LM_GLW')
    if env:
        return os.path.abspath(env)
    if os.path.isdir(os.path.join(glw, 'engine_v373')) or not os.path.isdir(glw):
        return glw
    hits = [os.path.join(glw, d) for d in sorted(os.listdir(glw)) if os.path.isdir(os.path.join(glw, d, 'engine_v373'))]
    if len(hits) > 1:
        raise Refused('GLW %s: several subfolders hold engine_v373 (%s) -- set H2LM_GLW' % (glw, ', '.join(hits)))
    return hits[0] if hits else glw


def resolve(ref, roots):
    """'KEY:rel/path' -> absolute path. KEY is GLW / H2ROOT (spec roots, overridable) or PROJ (this project)."""
    if ':' not in ref or len(ref.split(':', 1)[0]) < 2:
        raise Refused('bad source reference %r (want KEY:rel/path)' % ref)
    key, rel = ref.split(':', 1)
    base = PROJ if key == 'PROJ' else roots.get(key)
    if not base:
        raise Refused('source reference %r: unknown root %s' % (ref, key))
    return os.path.normpath(os.path.join(base, *[p for p in rel.split('/') if p])) if rel else os.path.normpath(base)


def app_version():
    """<Version> of the shell exe project (the exe's AssemblyVersion and the manifest appVersion must agree)"""
    import re
    p = os.path.join(PROJ, 'src', 'H2GpuLightmapper', 'H2GpuLightmapper.csproj')
    m = re.search(r'<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>', open(p, encoding='utf-8').read())
    if not m:
        raise Refused('no <Version>x.y.z</Version> in %s' % p)
    return m.group(1)


def content_hash(files):
    """md5 over the sorted '<path> <md5>\\n' lines: names a versioned extraction dir by its content"""
    return md5_bytes(''.join('%s %s\n' % (f['path'], f['md5']) for f in sorted(files, key=lambda f: f['path'])).encode('utf-8'))


def zipinfo(arc):
    zi = zipfile.ZipInfo(arc, ZIP_DT)
    zi.compress_type = zipfile.ZIP_DEFLATED
    zi.external_attr = ZIP_ATTR
    zi.create_system = 0
    return zi


# ---------------------------------------------------------------------------------------------------------------- pack (1.0)
# A component zip may keep some of its files inside ONE LZMA stream, the "pack" entry: the files' bytes back to back in
# path order. Zip's deflate cannot see a repeat that is megabytes away; one LZMA stream can, and the engine folder holds
# several programs that are almost the same file (31.4 MB of zip entries pack to about 4 MB).
#   PACK_NAME   stored (not deflated) entry: the LZMA_Alone stream
#   PACK_INDEX  deflated text entry, one "size<TAB>path" line per packed file, in pack order (for the build tools and
#               the release scanners; the shell reads the manifest's "packed" flags and checks this list against them)
# H2LM_PACK=0 writes plain zips as before. H2LM_PACK_PRESET (default 9) is the LZMA preset.
PACK_NAME = '__pack__.lzma'
PACK_INDEX = PACK_NAME + '.index'
PACK_DICT_MAX = 64 << 20          # the shell holds this much while it unpacks
PACK_BINARY_EXT = ('.exe', '.dll', '.pyd', '.bin', '.pdb')


def pack_enabled():
    return os.environ.get('H2LM_PACK', '1') != '0'


def pack_binaries(arc, data):
    """INST: programs and libraries only; scripts and other text stay ordinary zip entries (the shell reads some of them
    straight from the zip at start-up)"""
    return arc.lower().endswith(PACK_BINARY_EXT)


def pack_all(arc, data):
    return True


def pack_dict_size(total):
    d = 1 << 16
    while d < total and d < PACK_DICT_MAX:
        d <<= 1
    return d


def lzma_pack(blob):
    filt = [{'id': lzma.FILTER_LZMA1, 'preset': int(os.environ.get('H2LM_PACK_PRESET', '9')), 'dict_size': pack_dict_size(len(blob))}]
    out = lzma.compress(blob, format=lzma.FORMAT_ALONE, filters=filt)
    if lzma.decompress(out, format=lzma.FORMAT_ALONE) != blob:
        raise Refused('the LZMA pack does not read back')
    return out


def parse_pack_index(text):
    """-> [(path, size)] in pack order"""
    out = []
    for line in text.split('\n'):
        if not line:
            continue
        size, _, path = line.partition('\t')
        out.append((path, int(size)))
    return out


class _PackedInfo(object):
    """stands in for a zipfile.ZipInfo of a packed file (the fields the tools use)"""
    def __init__(self, name, size):
        self.filename = name
        self.file_size = size
        self.packed = True
        self.date_time = ZIP_DT

    def is_dir(self):
        return False


class PayloadZip(object):
    """Read-only view of a payload component zip with the pack opened up: namelist / infolist / read behave as if every
    packed file were an ordinary entry, and the pack entry and its index are not listed. `src` is a path, bytes or a
    file object. A zip without a pack behaves exactly like zipfile.ZipFile for these calls."""
    def __init__(self, src):
        if isinstance(src, (bytes, bytearray)):
            src = io.BytesIO(bytes(src))
        self.zip = zipfile.ZipFile(src)
        self._order = []
        self._data = None
        names = set(self.zip.namelist())
        self.has_pack = PACK_NAME in names
        if self.has_pack:
            if PACK_INDEX not in names:
                raise Refused('payload zip has %s but no %s' % (PACK_NAME, PACK_INDEX))
            self._order = parse_pack_index(self.zip.read(PACK_INDEX).decode('utf-8'))
        self._packed_names = set(p for p, _ in self._order)

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()

    def close(self):
        self.zip.close()

    def _load(self):
        if self._data is None:
            blob = lzma.decompress(self.zip.read(PACK_NAME), format=lzma.FORMAT_ALONE)
            if len(blob) != sum(s for _, s in self._order):
                raise Refused('pack holds %d bytes, its index lists %d' % (len(blob), sum(s for _, s in self._order)))
            self._data, off = {}, 0
            for p, s in self._order:
                self._data[p] = blob[off:off + s]
                off += s
        return self._data

    def infolist(self):
        out = [i for i in self.zip.infolist() if i.filename not in (PACK_NAME, PACK_INDEX)]
        out += [_PackedInfo(p, s) for p, s in self._order]
        return sorted(out, key=lambda i: i.filename)

    def namelist(self):
        return [i.filename for i in self.infolist()]

    def is_packed(self, name):
        return name in self._packed_names

    def read(self, name):
        if not isinstance(name, str):
            name = name.filename
        if name in self._packed_names:
            return self._load()[name]
        return self.zip.read(name)


def write_det_zip(out_zip, entries, pack=None):
    """deterministic zip (the S0.1 / S0.3 convention): entries [(arcname, bytes)] are written sorted by name with '/'
    separators, 1980-01-01 stamps, mode 0644, create_system 0, DEFLATE level 9, no directory entries, no extra fields.
    1.0: with `pack` (a function (arcname, bytes) -> bool) the chosen files are not written as entries; they go, in name
    order, into the stored PACK_NAME entry as one LZMA stream, with the PACK_INDEX list beside it.
    -> [{'path','size','md5'[,'packed': True]}] in name order. out_zip must not exist."""
    if os.path.exists(out_zip):
        raise Refused('%s exists' % out_zip)
    names = [a for a, _ in entries]
    if len(set(n.lower() for n in names)) != len(names):
        raise Refused('%s: duplicate (case-insensitive) entry names' % out_zip)
    if any(n.lower() in (PACK_NAME, PACK_INDEX) for n in names):
        raise Refused('%s: an entry has the name of the pack' % out_zip)
    ordered = sorted(entries, key=lambda e: e[0])
    for arc, _ in ordered:
        if '\\' in arc or arc.startswith('/') or arc.endswith('/') or any(p in ('', '.', '..') for p in arc.split('/')):
            raise Refused('%s: unsafe entry name %r' % (out_zip, arc))
    packed = [(a, d) for a, d in ordered if pack(a, d)] if (pack and pack_enabled()) else []
    pk = set(a for a, _ in packed)
    items = [(a, d, False) for a, d in ordered if a not in pk]
    if packed:
        items.append((PACK_NAME, lzma_pack(b''.join(d for _, d in packed)), True))
        items.append((PACK_INDEX, ''.join('%d\t%s\n' % (len(d), a) for a, d in packed).encode('utf-8'), False))
    tmp = out_zip + '.tmp_%d' % os.getpid()
    with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for arc, data, stored in sorted(items, key=lambda e: e[0]):
            zi = zipinfo(arc)
            if stored:
                zi.compress_type = zipfile.ZIP_STORED
                z.writestr(zi, data)
            else:
                z.writestr(zi, data, compresslevel=9)
    os.replace(tmp, out_zip)
    recs = []
    for arc, data in ordered:
        r = {'path': arc, 'size': len(data), 'md5': md5_bytes(data)}
        if arc in pk:
            r['packed'] = True
        recs.append(r)
    return recs


def check_det_zip(zip_path, files, strict_format=True):
    """re-open a payload zip and prove it holds exactly `files` ({'path','size','md5'}), byte for byte, in the deterministic
    format. -> list of problems (empty = ok)."""
    probs = []
    want = {f['path']: f for f in files}
    with zipfile.ZipFile(zip_path) as z:
        il = z.infolist()
        names = [i.filename for i in il]
        if strict_format:
            if names != sorted(names):
                probs.append('entries are not sorted')
            for i in il:
                if i.is_dir():
                    probs.append('directory entry %s' % i.filename)
                if i.date_time != ZIP_DT:
                    probs.append('%s: date %s' % (i.filename, i.date_time))
                if i.compress_type != (zipfile.ZIP_STORED if i.filename == PACK_NAME else zipfile.ZIP_DEFLATED):
                    probs.append('%s: compress_type %d' % (i.filename, i.compress_type))
                if i.create_system != 0 or i.external_attr != ZIP_ATTR:
                    probs.append('%s: create_system %d attr %o' % (i.filename, i.create_system, i.external_attr))
                if i.extra:
                    probs.append('%s: extra field' % i.filename)
        got = set(n for n in names if not n.endswith('/') and n not in (PACK_NAME, PACK_INDEX))
        # 1.0: the pack. Its index must list exactly the files flagged "packed", in the order of `files`; none of them may
        # also be an entry; every packed file is read out of the LZMA stream and compared like an entry.
        want_packed = [(f['path'], f['size']) for f in files if f.get('packed')]
        if (PACK_NAME in names) != bool(want_packed) or (PACK_INDEX in names) != bool(want_packed):
            probs.append('pack entry / index present %s / %s, packed files listed %d' % (PACK_NAME in names, PACK_INDEX in names, len(want_packed)))
        elif want_packed:
            idx = parse_pack_index(z.read(PACK_INDEX).decode('utf-8'))
            if idx != want_packed:
                probs.append('the pack index differs from the packed files of the manifest')
            else:
                for p, _ in want_packed:
                    if p in got:
                        probs.append('%s is packed and also an entry' % p)
                try:
                    blob = lzma.decompress(z.read(PACK_NAME), format=lzma.FORMAT_ALONE)
                except lzma.LZMAError as e:
                    blob = None
                    probs.append('the pack does not decode: %s' % e)
                if blob is not None:
                    if len(blob) != sum(s for _, s in want_packed):
                        probs.append('the pack holds %d bytes, the manifest lists %d' % (len(blob), sum(s for _, s in want_packed)))
                    else:
                        off = 0
                        for p, s in want_packed:
                            h = hashlib.md5(blob[off:off + s]).hexdigest()
                            off += s
                            if h != want[p]['md5']:
                                probs.append('%s: packed md5 %s, manifest %s' % (p, h, want[p]['md5']))
                        got |= set(p for p, _ in want_packed)
        for n in sorted(got - set(want)):
            probs.append('extra entry %s' % n)
        for n in sorted(set(want) - got):
            probs.append('missing entry %s' % n)
        for i in il:
            f = want.get(i.filename)
            if f is None:
                continue
            h, n = hashlib.md5(), 0
            with z.open(i) as r:
                for b in iter(lambda: r.read(CHUNK), b''):
                    h.update(b)
                    n += len(b)
            if n != f['size'] or h.hexdigest() != f['md5']:
                probs.append('%s: size %d md5 %s, manifest %d %s' % (i.filename, n, h.hexdigest(), f['size'], f['md5']))
    return probs


def is_reparse(p):
    try:
        return bool(os.lstat(p).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)
    except (OSError, AttributeError):
        return False


def safe_remove_tree(path, must_be_under):
    """remove a BUILD OUTPUT tree of this project. Refuses (removing nothing) when the path is not strictly under
    `must_be_under`, or when the tree holds any reparse point (junction / symlink) anywhere. -> files removed."""
    p = os.path.normcase(os.path.realpath(path))
    base = os.path.normcase(os.path.realpath(must_be_under))
    if not p.startswith(base.rstrip('\\/') + os.sep):
        raise Refused('refusing to remove %s: not under %s' % (path, must_be_under))
    if not os.path.exists(path):
        return 0
    if is_reparse(path):
        raise Refused('refusing to remove %s: it is a reparse point' % path)
    rp = []
    for dp, dns, fns in os.walk(path):
        for n in dns + fns:
            if is_reparse(os.path.join(dp, n)):
                rp.append(os.path.join(dp, n))
    if rp:
        raise Refused('refusing to remove %s: it holds reparse points (%s)' % (path, '; '.join(rp[:5])))
    n = 0
    for dp, dns, fns in os.walk(path, topdown=False):
        for f in fns:
            fp = os.path.join(dp, f)
            os.chmod(fp, stat.S_IWRITE | stat.S_IREAD)
            os.remove(fp)
            n += 1
        for d in dns:
            os.rmdir(os.path.join(dp, d))
    os.rmdir(path)
    return n


def write_json(path, obj):
    """deterministic JSON (LF, indent 1, ASCII), written via a temp file + replace"""
    data = (json.dumps(obj, indent=1, ensure_ascii=True) + '\n').encode('ascii')
    tmp = path + '.tmp_%d' % os.getpid()
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)
    return md5_bytes(data)


def rel_win(p):
    return p.replace('/', '\\')
