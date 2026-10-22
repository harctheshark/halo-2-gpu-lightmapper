"""h2lm_payload_lib.py -- shared helpers of the S1.9 payload builder (stage_engine.py, zip_payloads.py, build_payload.py).
Stdlib only. Never writes outside the paths its callers pass; never touches production (callers only READ production)."""
import hashlib
import importlib.util
import json
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
    return s


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


def write_det_zip(out_zip, entries):
    """deterministic zip (the S0.1 / S0.3 convention): entries [(arcname, bytes)] are written sorted by name with '/'
    separators, 1980-01-01 stamps, mode 0644, create_system 0, DEFLATE level 9, no directory entries, no extra fields.
    -> [{'path','size','md5'}] in zip order. out_zip must not exist."""
    if os.path.exists(out_zip):
        raise Refused('%s exists' % out_zip)
    names = [a for a, _ in entries]
    if len(set(n.lower() for n in names)) != len(names):
        raise Refused('%s: duplicate (case-insensitive) entry names' % out_zip)
    recs = []
    tmp = out_zip + '.tmp_%d' % os.getpid()
    with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for arc, data in sorted(entries, key=lambda e: e[0]):
            if '\\' in arc or arc.startswith('/') or arc.endswith('/') or any(p in ('', '.', '..') for p in arc.split('/')):
                raise Refused('%s: unsafe entry name %r' % (out_zip, arc))
            z.writestr(zipinfo(arc), data, compresslevel=9)
            recs.append({'path': arc, 'size': len(data), 'md5': md5_bytes(data)})
    os.replace(tmp, out_zip)
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
                if i.compress_type != zipfile.ZIP_DEFLATED:
                    probs.append('%s: compress_type %d' % (i.filename, i.compress_type))
                if i.create_system != 0 or i.external_attr != ZIP_ATTR:
                    probs.append('%s: create_system %d attr %o' % (i.filename, i.create_system, i.external_attr))
                if i.extra:
                    probs.append('%s: extra field' % i.filename)
        got = set(n for n in names if not n.endswith('/'))
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
