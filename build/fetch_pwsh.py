# S0.2 / S1.9: the portable PowerShell 7.6.6 runtime for the standalone exe.
#
#   verify                       sha256 + size of cache\PowerShell-7.6.6-win-x64.zip against the PINNED release values
#   fetch --allow-download       download it ONLY if it is missing or wrong (the user approved this one download, 2026-10-14);
#                                without --allow-download a missing/bad zip is an error, never a silent download
#   extract --out DIR            verify, then extract into DIR via DIR.tmp_<pid> + atomic rename, write pwsh_files.json
#                                (relpath, size, md5 of every file) and a .complete marker. DIR must not exist (never overwrites).
#   check-dir --dir DIR          re-hash an extracted DIR against its pwsh_files.json (exit 1 on any mismatch / extra / missing)
#
# Pinned from three independent published sources (2026-10-14), all identical:
#   * GitHub API asset digest      sha256:02fe458b...c860  (release v7.6.6, published 2026-09-08T20:28:01Z)
#   * the release notes "SHA256 Hashes of the release artifacts" section
#   * the release asset hashes.sha256 (UTF-16LE; its own sha256 388b105a... equals the notes' value)
# Run with:  "C:\Program Files\Python310\python.exe" -I build\fetch_pwsh.py <verb> ...
import argparse, hashlib, json, os, sys, time, zipfile

VERSION = '7.6.6'
ZIP_NAME = 'PowerShell-7.6.6-win-x64.zip'
URL = 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.zip'
SHA256 = '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860'
SIZE = 106328873
HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
CACHE = os.path.join(PROJECT, 'cache')


def below_normal():
    try:
        import ctypes
        k = ctypes.windll.kernel32
        k.SetPriorityClass(k.GetCurrentProcess(), 0x4000)
    except Exception:
        pass


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def verify_zip(path):
    if not os.path.isfile(path):
        return False, 'missing: %s' % path
    sz = os.path.getsize(path)
    if sz != SIZE:
        return False, 'size %d != pinned %d' % (sz, SIZE)
    d = sha256_of(path)
    if d != SHA256:
        return False, 'sha256 %s != pinned %s' % (d, SHA256)
    return True, 'OK sha256 %s size %d' % (d, sz)


def cmd_verify(a):
    ok, msg = verify_zip(a.zip)
    print(('VERIFY ' if ok else 'VERIFY FAIL ') + msg)
    return 0 if ok else 1


def cmd_fetch(a):
    ok, msg = verify_zip(a.zip)
    if ok:
        print('cached, ' + msg)
        return 0
    if not a.allow_download:
        print('REFUSED: %s and --allow-download was not given (the download needs the user\'s approval)' % msg)
        return 2
    import urllib.request
    part = a.zip + '.part'
    print('downloading %s -> %s' % (URL, part))
    with urllib.request.urlopen(URL, timeout=120) as r, open(part, 'wb') as f:
        while True:
            b = r.read(1 << 20)
            if not b:
                break
            f.write(b)
    ok, msg = verify_zip(part)
    if not ok:
        print('DOWNLOAD FAILED VERIFY: ' + msg + ' (kept as .part for inspection)')
        return 1
    os.replace(part, a.zip)
    print('fetched, ' + msg)
    return 0


def md5_bytes(b):
    return hashlib.md5(b).hexdigest()


def cmd_extract(a):
    ok, msg = verify_zip(a.zip)
    if not ok:
        print('REFUSED: zip does not verify: ' + msg)
        return 1
    out = os.path.abspath(a.out)
    if os.path.exists(out):
        print('REFUSED: %s already exists (extract never overwrites; check-dir verifies an existing one)' % out)
        return 2
    tmp = '%s.tmp_%d' % (out, os.getpid())
    os.makedirs(tmp)
    files = []
    t0 = time.time()
    with zipfile.ZipFile(a.zip) as z:
        for zi in z.infolist():
            name = zi.filename.replace('\\', '/')
            if name.endswith('/'):
                continue
            # zip-slip guard: no absolute names, no '..'
            parts = name.split('/')
            if name.startswith('/') or ':' in parts[0] or '..' in parts:
                print('REFUSED: unsafe member name %r' % name)
                return 1
            data = z.read(zi)
            dst = os.path.join(tmp, *parts)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, 'wb') as f:
                f.write(data)
            files.append({'path': '/'.join(parts), 'size': len(data), 'md5': md5_bytes(data)})
    files.sort(key=lambda e: e['path'].lower())
    man = {'component': 'pwsh', 'version': VERSION, 'zip': ZIP_NAME, 'zip_sha256': SHA256, 'zip_size': SIZE,
           'file_count': len(files), 'total_bytes': sum(e['size'] for e in files), 'files': files}
    with open(os.path.join(tmp, 'pwsh_files.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(man, f, indent=1)
    os.replace(tmp, out)
    with open(os.path.join(out, '.complete'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('%s %s %d files\n' % (ZIP_NAME, SHA256, len(files)))
    print('extracted %d files, %.1f MB, in %.1f s -> %s' % (len(files), man['total_bytes'] / 1048576.0, time.time() - t0, out))
    return 0


def cmd_check_dir(a):
    d = os.path.abspath(a.dir)
    mp = os.path.join(d, 'pwsh_files.json')
    if not os.path.isfile(os.path.join(d, '.complete')) or not os.path.isfile(mp):
        print('CHECK FAIL: %s has no .complete / pwsh_files.json' % d)
        return 1
    man = json.load(open(mp, encoding='utf-8'))
    bad = []
    seen = set()
    for e in man['files']:
        p = os.path.join(d, *e['path'].split('/'))
        seen.add(e['path'].lower())
        if not os.path.isfile(p):
            bad.append('missing ' + e['path'])
            continue
        b = open(p, 'rb').read()
        if len(b) != e['size'] or md5_bytes(b) != e['md5']:
            bad.append('changed ' + e['path'])
    for dp, dns, fns in os.walk(d):
        for fn in fns:
            rel = os.path.relpath(os.path.join(dp, fn), d).replace('\\', '/')
            if rel in ('pwsh_files.json', '.complete'):
                continue
            if rel.lower() not in seen:
                bad.append('extra ' + rel)
    for b in bad[:50]:
        print('  ' + b)
    print('CHECK %s: %d files, %d problems' % ('OK' if not bad else 'FAIL', len(man['files']), len(bad)))
    return 0 if not bad else 1


def main():
    below_normal()
    ap = argparse.ArgumentParser(description=__doc__)
    sp = ap.add_subparsers(dest='verb', required=True)
    p = sp.add_parser('verify'); p.add_argument('--zip', default=os.path.join(CACHE, ZIP_NAME)); p.set_defaults(fn=cmd_verify)
    p = sp.add_parser('fetch'); p.add_argument('--zip', default=os.path.join(CACHE, ZIP_NAME))
    p.add_argument('--allow-download', action='store_true'); p.set_defaults(fn=cmd_fetch)
    p = sp.add_parser('extract'); p.add_argument('--zip', default=os.path.join(CACHE, ZIP_NAME))
    p.add_argument('--out', required=True); p.set_defaults(fn=cmd_extract)
    p = sp.add_parser('check-dir'); p.add_argument('--dir', required=True); p.set_defaults(fn=cmd_check_dir)
    a = ap.parse_args()
    return a.fn(a)


if __name__ == '__main__':
    sys.exit(main())
