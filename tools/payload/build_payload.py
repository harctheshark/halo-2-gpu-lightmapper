"""build_payload.py -- S1.9 driver: stage the engine, zip the payload, build the Release exe that embeds it. Stdlib only.

  1. stage_engine.py  -> build\\s19\\stage\\        (production copied by md5 at build time + the reviewed patches, verified)
  2. zip_payloads.py  -> build\\s19\\payload\\      (deterministic zips + MANIFEST.json + NOTICES)
  3. dotnet build src\\H2GpuLightmapper (Release, offline, no build servers, BELOW_NORMAL, no window) with
     -p:PayloadDir=build\\s19\\payload\\ -p:OutDir=build\\s19\\exe\\ -p:BaseIntermediateOutputPath=build\\s19\\obj\\
     (a separate obj, so the dev build never inherits the payload)
  4. checks: the exe's embedded MANIFEST.json and zips are byte-identical to the payload dir (read from the PE's .NET
     resources), the exe loads its own payload (--cli doctor --json, sandboxed --localappdata, read-only), NOTICES.txt beside it
  5. build\\s19\\BUILD.json: exe path / size / md5, payload, engine md5s, git HEAD.

Re-runnable at any time -- in particular after an engine promotion (new fullgpu.exe + kernel headers): nothing in here
pins an engine binary; a gpu_bake.ps1 change is refused by the patch series until the patches are re-reviewed.

  py -I -B tools\\payload\\build_payload.py [--glw DIR] [--with-custom-engine] [--skip-exe] [--skip-stage]
Exit codes: 0 ok, 2 refused (see the step's message), 3 the exe build or a check failed, 1 usage."""
import argparse
import json
import os
import struct
import subprocess
import sys
import time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import h2lm_payload_lib as L   # noqa: E402

CREATE_NO_WINDOW = 0x08000000
CSPROJ = os.path.join(L.PROJ, 'src', 'H2GpuLightmapper', 'H2GpuLightmapper.csproj')
EXE_NAME = 'H2 GPU Lightmapper.exe'


def run(argv, env=None, cwd=None, timeout=1800):
    t = time.time()
    p = subprocess.run(argv, env=env, cwd=cwd, creationflags=CREATE_NO_WINDOW, stdin=subprocess.DEVNULL,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    return p.returncode, p.stdout.decode('utf-8', 'replace'), time.time() - t


def dotnet_env():
    env = dict(os.environ)
    for k, v in (('DOTNET_CLI_TELEMETRY_OPTOUT', '1'), ('DOTNET_NOLOGO', '1'), ('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1'),
                 ('DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', '1'), ('MSBUILDDISABLENODEREUSE', '1')):
        env[k] = v
    return env


# ------------------------------------------------------------------------------------------- managed resources of the exe
def managed_resources(exe):
    """{name: (offset, length)} of the embedded (manifest) resources of a .NET PE, read from the CLI header + metadata
    ManifestResource table. Minimal ECMA-335 reader: enough to prove the exe embeds the payload byte for byte."""
    d = open(exe, 'rb').read()
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    nsec = struct.unpack_from('<H', d, pe + 6)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', d, opt)[0]
    dd = opt + (96 if magic == 0x10B else 112)
    secs = []
    so = opt + struct.unpack_from('<H', d, pe + 20)[0]
    for i in range(nsec):
        vsize, va, rsz, rptr = struct.unpack_from('<IIII', d, so + 40 * i + 8)   # VirtualSize, VirtualAddress, SizeOfRawData, PointerToRawData
        secs.append((va, max(vsize, rsz), rptr))

    def off(rva):
        for va, sz, rp in secs:
            if va <= rva < va + sz:
                return rva - va + rp
        raise ValueError('rva %x' % rva)
    cli_rva = struct.unpack_from('<I', d, dd + 14 * 8)[0]
    c = off(cli_rva)
    md_rva, md_sz = struct.unpack_from('<II', d, c + 8)
    res_rva, res_sz = struct.unpack_from('<II', d, c + 24)
    m = off(md_rva)
    vlen = struct.unpack_from('<I', d, m + 12)[0]
    p = m + 16 + vlen
    nstreams = struct.unpack_from('<H', d, p + 2)[0]
    p += 4
    streams = {}
    for _ in range(nstreams):
        so_, ssz = struct.unpack_from('<II', d, p)
        p += 8
        e = d.index(b'\0', p)
        name = d[p:e].decode('ascii')
        p = (e + 4) & ~3
        streams[name] = (m + so_, ssz)
    ts, _ = streams['#~']
    heap_sizes = d[ts + 6]
    valid = struct.unpack_from('<Q', d, ts + 8)[0]
    p = ts + 24
    rows = {}
    for t in range(64):
        if valid >> t & 1:
            rows[t] = struct.unpack_from('<I', d, p)[0]
            p += 4
    si = 4 if heap_sizes & 1 else 2
    gi = 4 if heap_sizes & 2 else 2
    bi = 4 if heap_sizes & 4 else 2

    def idx(*tables):
        return 4 if max(rows.get(t, 0) for t in tables) >= 0x10000 else 2

    def cidx(bits, *tables):
        return 4 if max(rows.get(t, 0) for t in tables) >= (1 << (16 - bits)) else 2
    ri = {
        0x00: 2 + si + gi * 3, 0x01: cidx(2, 0x00, 0x1A, 0x23, 0x01) + si * 2, 0x02: 4 + si * 2 + cidx(2, 0x02, 0x01, 0x1B) + idx(0x04) + idx(0x06),
        0x03: idx(0x04), 0x04: 2 + si + bi, 0x05: idx(0x06), 0x06: 4 + 2 + 2 + si + bi + idx(0x08), 0x07: idx(0x08),
        0x08: 2 + 2 + si, 0x09: idx(0x02) + cidx(2, 0x02, 0x01, 0x1B), 0x0A: cidx(3, 0x02, 0x01, 0x1A, 0x06, 0x1B) + si + bi,
        0x0B: 2 + cidx(2, 0x04, 0x08, 0x17) + bi,
        0x0C: cidx(5, 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B) + cidx(3, 0x06, 0x0A) + bi,
        0x0D: cidx(1, 0x04, 0x08) + bi, 0x0E: 2 + cidx(2, 0x02, 0x06, 0x20) + bi, 0x0F: 2 + 4 + idx(0x02), 0x10: 4 + idx(0x04),
        0x11: bi, 0x12: idx(0x02) + idx(0x14), 0x13: idx(0x14), 0x14: 2 + si + cidx(2, 0x02, 0x01, 0x1B), 0x15: idx(0x02) + idx(0x17),
        0x16: idx(0x17), 0x17: 2 + si + bi, 0x18: 2 + idx(0x06) + cidx(1, 0x14, 0x17), 0x19: idx(0x02) + cidx(1, 0x06, 0x0A) * 2,
        0x1A: si, 0x1B: bi, 0x1C: 2 + cidx(1, 0x04, 0x06) + si + idx(0x1A), 0x1D: 4 + idx(0x04), 0x1E: 4 + 4, 0x1F: 4,
        0x20: 4 + 2 + 2 + 2 + 2 + 4 + bi + si + si, 0x21: 4, 0x22: 4 + 4 + 4, 0x23: 2 * 4 + 4 + bi + si + si + bi,
        0x24: 4 + idx(0x23), 0x25: 4 * 3 + idx(0x23), 0x26: 4 + si + bi, 0x27: 4 + 4 + si + si + cidx(2, 0x26, 0x23, 0x27),
    }
    p_tab = p
    for t in sorted(rows):
        if t == 0x28:
            break
        if t not in ri:
            raise ValueError('metadata table 0x%x not handled' % t)
        p_tab += ri[t] * rows[t]
    strings_off = streams['#Strings'][0]
    impl = cidx(2, 0x26, 0x23, 0x27)
    out = {}
    q = p_tab
    for _ in range(rows.get(0x28, 0)):
        offset, flags = struct.unpack_from('<II', d, q)
        q += 8
        nidx = struct.unpack_from('<I' if si == 4 else '<H', d, q)[0]
        q += si
        impl_v = struct.unpack_from('<I' if impl == 4 else '<H', d, q)[0]
        q += impl
        e = d.index(b'\0', strings_off + nidx)
        name = d[strings_off + nidx:e].decode('utf-8')
        if impl_v == 0:      # embedded in this file
            ro = off(res_rva) + offset
            ln = struct.unpack_from('<I', d, ro)[0]
            out[name] = (ro + 4, ln)
    return out, d


def check_exe(exe, payload_dir):
    res, d = managed_resources(exe)
    probs, emb = [], {}
    want = ['MANIFEST.json'] + sorted(f for f in os.listdir(payload_dir) if f.lower().endswith('.zip'))
    for f in want:
        name = 'payload/' + f
        if name not in res:
            probs.append('the exe does not embed %s' % name)
            continue
        o, n = res[name]
        m = L.md5_bytes(d[o:o + n])
        emb[name] = {'size': n, 'md5': m}
        if m != L.md5_file(os.path.join(payload_dir, f)):
            probs.append('%s embedded md5 %s != %s' % (name, m, L.md5_file(os.path.join(payload_dir, f))))
    extra = sorted(k for k in res if k.startswith('payload/') and k not in emb)
    if extra:
        probs.append('the exe embeds payload resources that are not in the payload dir: %s' % extra)
    return probs, emb


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--glw')
    ap.add_argument('--with-custom-engine', action='store_true')
    ap.add_argument('--skip-stage', action='store_true', help='reuse build\\s19\\stage and build\\s19\\payload (exe only)')
    ap.add_argument('--skip-exe', action='store_true')
    a = ap.parse_args(argv)
    L.below_normal()
    t0 = time.time()
    py = [sys.executable, '-I', '-B']
    stage, payload = os.path.join(L.BUILD, 'stage'), os.path.join(L.BUILD, 'payload')
    exe_dir, obj_dir = os.path.join(L.BUILD, 'exe'), os.path.join(L.BUILD, 'obj')
    steps = {}
    if not a.skip_stage:
        args = py + [os.path.join(HERE, 'stage_engine.py'), '--out', stage]
        if a.glw:
            args += ['--glw', a.glw]
        if a.with_custom_engine:
            args += ['--with-custom-engine']
        rc, out, s = run(args)
        print(out.rstrip())
        steps['stage'] = {'rc': rc, 'seconds': round(s, 1)}
        if rc != 0:
            print('BUILD REFUSED at stage_engine (exit %d)' % rc)
            return 2 if rc == 2 else 3
        rc, out, s = run(py + [os.path.join(HERE, 'zip_payloads.py'), '--stage', stage, '--out', payload])
        print(out.rstrip())
        steps['zip'] = {'rc': rc, 'seconds': round(s, 1)}
        if rc != 0:
            print('BUILD REFUSED at zip_payloads (exit %d)' % rc)
            return 2 if rc == 2 else 3
    man_p = os.path.join(payload, 'MANIFEST.json')
    if not os.path.isfile(man_p):
        print('BUILD REFUSED: no %s' % man_p)
        return 2
    man = json.load(open(man_p, encoding='utf-8'))
    srep = json.load(open(os.path.join(stage, 'stage_report.json'), encoding='utf-8'))
    # small build records kept in git beside BUILD.json (the stage / zips / exe themselves are gitignored build products)
    for src, dst in ((man_p, 'MANIFEST.json'), (os.path.join(stage, 'stage_report.json'), 'stage_report.json'),
                     (os.path.join(stage, 'ENGINE_MANIFEST.txt'), 'ENGINE_MANIFEST.txt')):
        with open(src, 'rb') as f:
            data = f.read()
        with open(os.path.join(L.BUILD, dst), 'wb') as f:
            f.write(data)
    build = {
        'format': 'h2lm-build/1', 'tool': 'build_payload.py (S1.9)', 'built': time.strftime('%Y-%m-%dT%H:%M:%S'),
        'appVersion': man['appVersion'], 'payload_dir': payload, 'manifest_md5': L.md5_file(man_p),
        'components': [{k: c[k] for k in ('name', 'resource', 'dirName', 'target', 'zipSize', 'zipMd5', 'fileCount', 'totalBytes', 'maxPathLen')}
                       for c in man['components']],
        'payload_bytes': sum(c['zipSize'] for c in man['components']) + os.path.getsize(man_p),
        'engine': srep['engine'], 'stage_content_hash': srep['content_hash'], 'custom_engine': srep.get('custom_engine'),
        'notes': srep.get('notes', []), 'warnings': srep.get('warnings', []), 'steps': steps,
    }
    try:
        build['git_head'] = subprocess.run(['git', '-C', L.PROJ, 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True,
                                           creationflags=CREATE_NO_WINDOW).stdout.strip()
    except OSError:
        pass
    rc_all = 0
    if not a.skip_exe:
        cmd = ['dotnet', 'build', CSPROJ, '-c', 'Release', '-nodeReuse:false', '--disable-build-servers',
               '-p:UseSharedCompilation=false', '-p:PayloadDir=' + payload + '\\', '-p:EmbedPayload=true',
               '-p:OutDir=' + exe_dir + '\\', '-p:BaseIntermediateOutputPath=' + obj_dir + '\\', '-v:m', '-clp:NoSummary']
        rc, out, s = run(cmd, env=dotnet_env(), cwd=os.path.dirname(CSPROJ))
        steps['dotnet'] = {'rc': rc, 'seconds': round(s, 1), 'command': subprocess.list2cmdline(cmd)}
        tail = '\n'.join(out.rstrip().splitlines()[-15:])
        print(tail)
        exe = os.path.join(exe_dir, EXE_NAME)
        if rc != 0 or not os.path.isfile(exe):
            print('BUILD FAILED: dotnet build exit %d' % rc)
            build['exe_error'] = tail
            L.write_json(os.path.join(L.BUILD, 'BUILD.json'), build)
            return 3
        probs, emb = check_exe(exe, payload)
        build['exe'] = {'path': exe, 'size': os.path.getsize(exe), 'md5': L.md5_file(exe), 'embedded': emb}
        with open(os.path.join(payload, 'NOTICES.txt'), 'rb') as f:
            notices = f.read()
        with open(os.path.join(exe_dir, 'NOTICES.txt'), 'wb') as f:
            f.write(notices)
        # the exe reads its own payload (embedded MANIFEST.json validated by the shell's loader); sandboxed per-user folder
        lad = os.path.join(L.BUILD, 'tmp', 'lad_%d' % os.getpid())
        os.makedirs(lad, exist_ok=True)
        rc2, out2, s2 = run([exe, '--cli', 'doctor', '--json', '--localappdata', lad], timeout=300)
        doc = None
        try:
            doc = json.loads(out2[out2.index('{'):out2.rindex('}') + 1])
        except ValueError:
            probs.append('doctor printed no JSON (exit %d): %s' % (rc2, out2[-400:]))
        if doc is not None:
            build['doctor'] = {k: doc.get(k) for k in ('exe', 'appVersion', 'payload', 'payloadError', 'customEnabled')}
            if not str(doc.get('payload') or '').startswith('embedded') or doc.get('payloadError'):
                probs.append('the exe does not report its embedded payload: %s / %s' % (doc.get('payload'), doc.get('payloadError')))
            if doc.get('appVersion') != man['appVersion'] or doc.get('exe') != man['appVersion']:
                probs.append('version mismatch: exe %s, payload appVersion %s, manifest %s' % (doc.get('exe'), doc.get('appVersion'), man['appVersion']))
        try:
            L.safe_remove_tree(lad, L.BUILD)
        except L.Refused as e:
            probs.append(str(e))
        build['exe_checks'] = probs
        if probs:
            rc_all = 3
            print('EXE CHECKS FAILED:\n  ' + '\n  '.join(probs))
        else:
            print('EXE OK: %s  %.1f MB (%d bytes)  md5 %s  embeds MANIFEST.json + %d zips byte-identical; doctor: payload %s, appVersion %s' % (
                exe, build['exe']['size'] / 1048576.0, build['exe']['size'], build['exe']['md5'], len(emb) - 1,
                build['doctor']['payload'], build['doctor']['appVersion']))
    build['seconds'] = round(time.time() - t0, 1)
    L.write_json(os.path.join(L.BUILD, 'BUILD.json'), build)
    return rc_all


if __name__ == '__main__':
    sys.exit(main())
