"""strict_patch.py -- make / check / apply the H2 GPU Lightmapper engine patches (P5, P6, ...) byte-exactly. Stdlib only.

A patch file is a plain unified diff (git apply / patch can read it) with a comment header that pins both ends:

    # title: P5 gpu_lightprobes.py passthroughs
    # source: _research/lightprobes/gpu_lightprobes.py      (path relative to GPU Lightmap Work, forward slashes)
    # base-md5: 0668095dc7425a6a195a77a226d30ca7            (the pristine production file the diff was made against)
    # result-md5: ...                                         (the patched file)
    --- a/_research/lightprobes/gpu_lightprobes.py
    +++ b/_research/lightprobes/gpu_lightprobes.py
    @@ -l,s +l,s @@
    ...

STRICT means: the input must have base-md5 (so no fuzz and no offset can ever be needed), every hunk's old lines must sit at
exactly the line its header names, hunks must be in order and must not overlap, and the output must have result-md5.
Line endings are bytes: a CRLF file's diff lines carry their CR (keep the .patch files out of git's eol conversion).

  py strict_patch.py make  --base PRISTINE --new EDITED --source REL --title TEXT --out X.patch
  py strict_patch.py check --patch X.patch --src FILE            exit 0 = FILE is the pinned base and the patch applies
  py strict_patch.py apply --patch X.patch --src FILE --out OUT   OUT must not exist; written only when result-md5 matches
  py strict_patch.py info  --patch X.patch                        the header as JSON
Exit codes: 0 ok, 2 refused (md5 / context / format), 1 usage.
As a module: read_patch(path) -> dict, apply_bytes(patch_dict, src_bytes) -> bytes (raises PatchError)."""
import argparse, difflib, hashlib, json, os, re, sys

sys.dont_write_bytecode = True
HUNK = re.compile(rb'^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@')


class PatchError(Exception):
    pass


def md5b(b):
    return hashlib.md5(b).hexdigest()


def lines_keep(b):
    """split bytes into lines that keep their own terminator (LF or CRLF); a last line without LF is kept as is."""
    out, i = [], 0
    while i < len(b):
        j = b.find(b'\n', i)
        if j < 0:
            out.append(b[i:])
            break
        out.append(b[i:j + 1])
        i = j + 1
    return out


def make_patch(base, new, source, title):
    if b'\\ No newline' in base + new:
        raise PatchError('the marker text "\\ No newline" occurs in a file; not supported')
    if not base.endswith(b'\n') or not new.endswith(b'\n'):
        raise PatchError('both files must end with a newline (strict format has no "\\ No newline" lines)')
    a = [l.decode('latin-1') for l in lines_keep(base)]
    b = [l.decode('latin-1') for l in lines_keep(new)]
    src = source.replace('\\', '/')
    head = ['# title: %s\n' % title, '# source: %s\n' % src, '# base-md5: %s\n' % md5b(base), '# result-md5: %s\n' % md5b(new)]
    body = list(difflib.unified_diff(a, b, 'a/' + src, 'b/' + src, n=3, lineterm='\n'))
    if not body:
        raise PatchError('the files are identical')
    # difflib writes '--- a/x\n' / '+++ b/x\n' / '@@ ... @@\n' with lineterm, and content lines with their own endings
    return ''.join(head + body).encode('latin-1')


def read_patch(path_or_bytes):
    raw = path_or_bytes if isinstance(path_or_bytes, bytes) else open(path_or_bytes, 'rb').read()
    L = lines_keep(raw)
    meta, i = {}, 0
    while i < len(L) and L[i].startswith(b'#'):
        m = re.match(rb'^# ([a-z0-9-]+): ?(.*?)\r?\n?$', L[i])
        if m:
            meta[m.group(1).decode()] = m.group(2).decode('latin-1')
        i += 1
    for k in ('source', 'base-md5', 'result-md5'):
        if k not in meta:
            raise PatchError('patch header lacks "# %s:"' % k)
    if i + 1 >= len(L) or not L[i].startswith(b'--- ') or not L[i + 1].startswith(b'+++ '):
        raise PatchError('no ---/+++ file header after the comment header')
    i += 2
    hunks = []
    while i < len(L):
        m = HUNK.match(L[i])
        if not m:
            raise PatchError('line %d: expected a hunk header, got %r' % (i + 1, L[i][:60]))
        ol, oc = int(m.group(1)), int(m.group(2) if m.group(2) is not None else 1)
        nl, nc = int(m.group(3)), int(m.group(4) if m.group(4) is not None else 1)
        i += 1
        old, new = [], []
        while i < len(L) and not L[i].startswith(b'@@'):
            t, body = L[i][:1], L[i][1:]
            if t == b' ':
                old.append(body); new.append(body)
            elif t == b'-':
                old.append(body)
            elif t == b'+':
                new.append(body)
            elif t == b'\\':
                raise PatchError('line %d: "\\ No newline" is not allowed in a strict patch' % (i + 1))
            else:
                raise PatchError('line %d: bad diff line %r' % (i + 1, L[i][:60]))
            i += 1
        if len(old) != oc or len(new) != nc:
            raise PatchError('hunk @@ -%d,%d +%d,%d @@ has %d old / %d new lines' % (ol, oc, nl, nc, len(old), len(new)))
        hunks.append(dict(old_start=ol, old=old, new_start=nl, new=new))
    if not hunks:
        raise PatchError('no hunks')
    meta['hunks'] = hunks
    return meta


def apply_bytes(P, src):
    if md5b(src) != P['base-md5']:
        raise PatchError('source md5 %s is not the pinned base %s (no fuzz, no offset: refusing)' % (md5b(src), P['base-md5']))
    S = lines_keep(src)
    out, pos, delta = [], 0, 0
    for h in P['hunks']:
        start = h['old_start'] - 1 if h['old'] else h['old_start']   # an empty old side names the line BEFORE the insertion
        if start < pos:
            raise PatchError('hunk at old line %d overlaps or is out of order' % h['old_start'])
        if S[start:start + len(h['old'])] != h['old']:
            raise PatchError('hunk at old line %d: context/removed lines do not match at exactly that line' % h['old_start'])
        want_new = start + delta + 1 if h['new'] else start + delta
        if h['new_start'] != want_new:
            raise PatchError('hunk at old line %d: new-side line %d, expected %d' % (h['old_start'], h['new_start'], want_new))
        out += S[pos:start] + h['new']
        pos = start + len(h['old'])
        delta += len(h['new']) - len(h['old'])
    out += S[pos:]
    res = b''.join(out)
    if md5b(res) != P['result-md5']:
        raise PatchError('patched md5 %s is not the pinned result %s' % (md5b(res), P['result-md5']))
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest='cmd', required=True)
    m = sp.add_parser('make'); m.add_argument('--base', required=True); m.add_argument('--new', required=True)
    m.add_argument('--source', required=True); m.add_argument('--title', required=True); m.add_argument('--out', required=True)
    c = sp.add_parser('check'); c.add_argument('--patch', required=True); c.add_argument('--src', required=True)
    p = sp.add_parser('apply'); p.add_argument('--patch', required=True); p.add_argument('--src', required=True); p.add_argument('--out', required=True)
    i = sp.add_parser('info'); i.add_argument('--patch', required=True)
    a = ap.parse_args(argv)
    try:
        if a.cmd == 'make':
            if os.path.exists(a.out):
                print('strict_patch: REFUSED: %s exists' % a.out); return 2
            data = make_patch(open(a.base, 'rb').read(), open(a.new, 'rb').read(), a.source, a.title)
            apply_bytes(read_patch(data), open(a.base, 'rb').read())          # round trip before writing
            with open(a.out, 'wb') as f:
                f.write(data)
            print('strict_patch: wrote %s (%d bytes)' % (a.out, len(data))); return 0
        if a.cmd == 'info':
            P = read_patch(a.patch)
            print(json.dumps(dict({k: v for k, v in P.items() if k != 'hunks'}, hunks=len(P['hunks'])), indent=1)); return 0
        P = read_patch(a.patch)
        res = apply_bytes(P, open(a.src, 'rb').read())
        if a.cmd == 'check':
            print('strict_patch: OK %s applies to %s -> %s' % (os.path.basename(a.patch), P['base-md5'], P['result-md5'])); return 0
        if os.path.exists(a.out):
            print('strict_patch: REFUSED: %s exists' % a.out); return 2
        tmp = a.out + '.tmp_strict_patch'
        with open(tmp, 'wb') as f:
            f.write(res); f.flush(); os.fsync(f.fileno())
        if md5b(open(tmp, 'rb').read()) != P['result-md5']:
            raise PatchError('written file does not verify')
        os.replace(tmp, a.out)
        print('strict_patch: applied %s -> %s (%s)' % (os.path.basename(a.patch), a.out, P['result-md5'])); return 0
    except PatchError as e:
        print('strict_patch: REFUSED: %s' % e); return 2


if __name__ == '__main__':
    sys.exit(main())
