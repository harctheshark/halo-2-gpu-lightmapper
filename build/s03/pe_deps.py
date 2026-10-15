# S0.3: static PE dependency audit of a Python bundle (stdlib only, read-only).
# For every .exe / .dll / .pyd under the bundle it lists the import + delay-import DLL names and resolves each one the way
# the Windows loader will on a CLEAN PC (no VC++ x64 redist, no Python install):
#   apiset (api-ms-win-* / ext-ms-*)               -> in-box (UCRT is in-box on Win10+)
#   a file in the bundle root (the exe dir), the module's own dir, or a delvewheel *.libs dir -> bundled
#   a VC++ redist name (msvcp140*, vcruntime140*, vcomp140, concrt140, vccorlib140, mfc140*) not bundled -> MISSING_ON_CLEAN_PC
#   any other name present in System32            -> in-box (listed so a reviewer can eyeball it)
#   anything else                                 -> NOT_FOUND
# usage: python -I pe_deps.py <bundle_dir> [--json out.json]     exit 0 = clean, 1 = a dependency would be missing on a clean PC
import os, sys, struct, json

# VC++ x64 redist DLLs (incl. msvcp140_1/_2/_atomic_wait/_codecvt_ids, vcruntime140_1, and delvewheel-mangled copies)
VC_PREFIX =('msvcp140', 'vcruntime140', 'vcomp140', 'concrt140', 'vccorlib140', 'mfc140', 'ucrtbased')
SYS32 = os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32')


def _is_vc(name):
    n = name.lower()
    return n.startswith(VC_PREFIX)


def _rva2off(rva, sections):
    for va, vsz, raw, rawsz in sections:
        if va <= rva < va + max(vsz, rawsz):
            return rva - va + raw
    return None


def _cstr(d, off):
    e = d.index(b'\0', off)
    return d[off:e].decode('ascii', 'replace')


def pe_imports(path):
    """-> (machine, [dll names], [delay dll names]); raises ValueError if not a PE."""
    with open(path, 'rb') as f:
        d = f.read()
    if d[:2] != b'MZ':
        raise ValueError('no MZ')
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    if d[pe:pe + 4] != b'PE\0\0':
        raise ValueError('no PE')
    machine, nsec = struct.unpack_from('<HH', d, pe + 4)
    opt_sz = struct.unpack_from('<H', d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', d, opt)[0]
    dd = opt + (112 if magic == 0x20B else 96)
    ndd = struct.unpack_from('<I', d, dd - 4)[0]
    dirs = [struct.unpack_from('<II', d, dd + 8 * i) for i in range(min(ndd, 16))]
    sec = opt + opt_sz
    sections = []
    for i in range(nsec):
        s = sec + 40 * i
        vsz, va, rawsz, raw = struct.unpack_from('<IIII', d, s + 8)
        sections.append((va, vsz, raw, rawsz))
    imps, dly = [], []
    if len(dirs) > 1 and dirs[1][0]:
        off = _rva2off(dirs[1][0], sections)
        while off is not None:
            oft, ts, fwd, name, ft = struct.unpack_from('<IIIII', d, off)
            if not (oft or name or ft):
                break
            no = _rva2off(name, sections)
            if no is not None:
                imps.append(_cstr(d, no))
            off += 20
    if len(dirs) > 13 and dirs[13][0]:
        off = _rva2off(dirs[13][0], sections)
        while off is not None:
            attrs, name = struct.unpack_from('<II', d, off)
            if not name:
                break
            no = _rva2off(name, sections)  # attrs bit0 = RVA-based (always on modern linkers)
            if no is not None:
                dly.append(_cstr(d, no))
            off += 32
    return machine, imps, dly


def audit(root):
    root = os.path.abspath(root)
    files, libdirs = [], []
    for dp, dns, fns in os.walk(root):
        if dp.lower().endswith('.libs'):
            libdirs.append(dp)
        for f in fns:
            if f.lower().endswith(('.exe', '.dll', '.pyd')):
                files.append(os.path.join(dp, f))
    def names_in(d):
        try:
            return {n.lower(): os.path.join(d, n) for n in os.listdir(d)}
        except OSError:
            return {}
    rootnames = names_in(root)
    libnames = {}
    for ld in libdirs:
        libnames.update(names_in(ld))
    sysnames = names_in(SYS32)
    rows, problems, inbox = [], [], {}
    machines = {}
    for p in sorted(files):
        try:
            mach, imps, dly = pe_imports(p)
        except Exception as e:
            problems.append(dict(file=os.path.relpath(p, root), dll=None, why='unparseable: %s' % e))
            continue
        machines[{0x8664: 'x64', 0x14C: 'x86', 0xAA64: 'arm64'}.get(mach, hex(mach))] = machines.get(
            {0x8664: 'x64', 0x14C: 'x86', 0xAA64: 'arm64'}.get(mach, hex(mach)), 0) + 1
        own = names_in(os.path.dirname(p))
        deps = []
        for kind, lst in (('imp', imps), ('delay', dly)):
            for n in lst:
                ln = n.lower()
                stem = ln[:-4] if ln.endswith('.dll') else ln
                if ln.startswith(('api-ms-win-', 'ext-ms-')):
                    how = 'apiset'
                elif ln in rootnames:
                    how = 'bundle_root'
                elif ln in own:
                    how = 'own_dir'
                elif ln in libnames:
                    how = 'libs_dir'
                elif _is_vc(ln):
                    how = 'MISSING_ON_CLEAN_PC'
                elif ln in sysnames:
                    how = 'system32'
                    inbox[ln] = inbox.get(ln, 0) + 1
                else:
                    how = 'NOT_FOUND'
                deps.append(dict(dll=n, kind=kind, how=how))
                if how in ('MISSING_ON_CLEAN_PC', 'NOT_FOUND'):
                    problems.append(dict(file=os.path.relpath(p, root), dll=n, kind=kind, why=how))
        rows.append(dict(file=os.path.relpath(p, root), machine=mach, deps=deps))
    vc_in_bundle = sorted(os.path.relpath(p, root) for p in files
                          if _is_vc(os.path.basename(p)))
    return dict(root=root, pe_files=len(files), machines=machines, problems=problems,
                system32_dlls_used=dict(sorted(inbox.items())), vc_runtime_files_in_bundle=vc_in_bundle, rows=rows)


if __name__ == '__main__':
    a = sys.argv[1:]
    out = None
    if '--json' in a:
        i = a.index('--json'); out = a[i + 1]; del a[i:i + 2]
    r = audit(a[0])
    print('PE files: %d  machines: %s' % (r['pe_files'], r['machines']))
    print('VC runtime files inside the bundle: %s' % r['vc_runtime_files_in_bundle'])
    print('in-box System32 DLLs referenced: %s' % ', '.join('%s(%d)' % kv for kv in r['system32_dlls_used'].items()))
    for p in r['problems']:
        print('PROBLEM: %s -> %s (%s)' % (p['file'], p['dll'], p['why']))
    print('RESULT: %s' % ('CLEAN' if not r['problems'] else '%d PROBLEM(S)' % len(r['problems'])))
    if out:
        with open(out, 'w') as f:
            json.dump(r, f, indent=1)
    sys.exit(1 if r['problems'] else 0)
