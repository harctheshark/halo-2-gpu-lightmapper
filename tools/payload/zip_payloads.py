"""zip_payloads.py -- S1.9: the payload resources of H2 GPU Lightmapper.exe. Stdlib only.

Writes <out>\\ (default build\\s19\\payload\\; the exe build embeds every *.zip there plus MANIFEST.json):
  rt-pwsh-7.6.6.zip      PowerShell 7.6.6, re-packed from the sha256-verified official PowerShell-7.6.6-win-x64.zip (S0.2)
  rt-py310-np226.zip     the S0.3 isolated CPython 3.10.5 + numpy 2.2.6 bundle, verbatim after a full check (format, every entry
                         md5 == its H2LM_PYBUNDLE.json, bundle_id recomputed, no scipy: the N4 gate passed)
  NOTICES.zip            NOTICES.txt + licenses\\ (PowerShell LICENSE + ThirdPartyNotices, CPython / PSF, NumPy, Osoyoos MIT)
  engine-<ver>.zip       INST, from stage_engine.py's stage (every staged file re-hashed against its stage report first)
  MANIFEST.json          format 1 (S0.1 / S2.1): per component name, resource, dirName, target, zipSize, zipMd5, fileCount,
                         totalBytes, maxPathLen, files[{path, size, md5, sourceMd5?, role?}]
  NOTICES.txt            a copy for the release folder (not embedded)
and build\\s19\\payload_report.json (sources, sizes, hashes; the only file with a time stamp).

Deterministic: zips are sorted, '/'-separated, 1980-01-01 stamped, mode 0644, create_system 0, DEFLATE level 9, no directory
entries; MANIFEST.json carries no time stamp; so the same inputs give byte-identical zips, MANIFEST.json and exe.
Content hashes name the versioned extraction dirs: dirName = <name>-<h8> (INST: <appVersion>-<h8>), h8 = the first 8 hex of
md5 over the sorted '<path> <md5>\\n' lines of the component's files.

  py -I -B tools\\payload\\zip_payloads.py [--stage build\\s19\\stage] [--out build\\s19\\payload] [--spec F]
Exit codes: 0 ok, 2 refused (an input is not what was verified; the previous payload dir is left as it was), 1 usage."""
import argparse
import json
import os
import re
import shutil
import sys
import time
import zipfile

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import h2lm_payload_lib as L   # noqa: E402
from h2lm_payload_lib import Refused   # noqa: E402

TOOL = 'zip_payloads.py (S1.9)'


def component(name, zip_path, files, target, dir_tmpl, app_version):
    h8 = L.content_hash(files)[:8]
    return {
        'name': name,
        'resource': 'payload/' + os.path.basename(zip_path),
        'dirName': dir_tmpl.format(appVersion=app_version, h8=h8),
        'target': target,
        'zipSize': os.path.getsize(zip_path),
        'zipMd5': L.md5_file(zip_path),
        'fileCount': len(files),
        'totalBytes': sum(f['size'] for f in files),
        'maxPathLen': max(len(f['path']) for f in files),
        'files': files,
    }


# ---------------------------------------------------------------------------------------------------------------- INST
def zip_inst(spec, stage, out_dir, app_version):
    rep_p = os.path.join(stage, 'stage_report.json')
    try:
        rep = json.load(open(rep_p, encoding='utf-8'))
    except (OSError, ValueError) as e:
        raise Refused('no usable stage report %s (%s): run stage_engine.py first' % (rep_p, e))
    if rep.get('format') != 'h2lm-stage-report/1':
        raise Refused('%s: format %r' % (rep_p, rep.get('format')))
    inst = os.path.join(stage, 'inst')
    entries, files = [], []
    on_disk = set()
    for dp, dns, fns in os.walk(inst):
        for fn in fns:
            on_disk.add(os.path.relpath(os.path.join(dp, fn), inst).replace('\\', '/'))
    listed = set(r['path'] for r in rep['files'])
    if on_disk != listed:
        raise Refused('stage %s differs from its report: extra %s, missing %s' % (inst, sorted(on_disk - listed)[:5], sorted(listed - on_disk)[:5]))
    for r in rep['files']:
        data = open(os.path.join(inst, *r['path'].split('/')), 'rb').read()
        if L.md5_bytes(data) != r['md5'] or len(data) != r['size']:
            raise Refused('%s: staged bytes differ from the stage report (re-run stage_engine.py)' % r['path'])
        entries.append((r['path'], data))
        f = {'path': r['path'], 'size': r['size'], 'md5': r['md5']}
        if r.get('sourceMd5'):
            f['sourceMd5'] = r['sourceMd5']
        f['role'] = r['role']
        files.append(f)
    if L.content_hash(files) != rep['content_hash']:
        raise Refused('stage content hash mismatch')
    ispec = spec['inst']
    zp = os.path.join(out_dir, ispec['zip'].format(appVersion=app_version))
    recs = L.write_det_zip(zp, entries)
    files.sort(key=lambda f: f['path'])
    if [(x['path'], x['md5']) for x in recs] != [(f['path'], f['md5']) for f in files]:
        raise Refused('INST zip order / content mismatch')
    return component(ispec['name'], zp, files, 'inst', ispec['dirName'], app_version), rep


# ---------------------------------------------------------------------------------------------------------------- pwsh
def zip_pwsh(spec, out_dir, app_version, keep):
    ps = spec['rt']['pwsh']
    src = L.resolve(ps['source_zip'], {})
    fp = L.load_module('h2lm_fetch_pwsh', L.resolve(ps['verifier'], {}))
    ok, msg = fp.verify_zip(src)
    if not ok:
        raise Refused('PowerShell zip %s: %s (build\\fetch_pwsh.py fetch --allow-download re-fetches the approved release)' % (src, msg))
    entries = []
    with zipfile.ZipFile(src) as z:
        for i in z.infolist():
            if i.is_dir():
                continue
            name = i.filename.replace('\\', '/')
            data = z.read(i)            # CRC-checked by zipfile
            entries.append((name, data))
            if name in keep:
                keep[name] = data
    names = set(n for n, _ in entries)
    if 'pwsh.exe' not in names:
        raise Refused('%s has no pwsh.exe at its root' % src)
    zp = os.path.join(out_dir, ps['zip'])
    files = L.write_det_zip(zp, entries)
    return component(ps['name'], zp, files, 'rt', ps['dirName'], app_version), {'source_zip': src, 'source_check': msg,
                                                                                     'version': fp.VERSION, 'sha256': fp.SHA256}


# ---------------------------------------------------------------------------------------------------------------- py
PERSONAL_PATH = re.compile(r'(?i)[a-z]:\\users\\')
APPDATA_ROAMING = re.compile(r'(?i)^[a-z]:\\users\\[^\\]+\\appdata\\roaming(?=\\|$)')


def scrub_pybundle_json(data):
    """E1 (0.6.1): H2LM_PYBUNDLE.json records the build machine's Python folder and its USER site-packages folder
    (C:\\Users\\<name>\\AppData\\Roaming\\Python\\Python310\\site-packages): the user name must not ship. The user site is the
    directory the bundle switches OFF, so its generic spelling %APPDATA%\\Python\\Python310\\site-packages says the same thing.
    The JSON is re-dumped exactly as make_pybundle.py writes it (indent 1, sorted keys, LF); a file that does not round-trip is
    refused, and so is anything else under C:\\Users\\ left in it. -> (bytes, changed)"""
    text = data.decode('utf-8')
    bm = json.loads(text)
    if json.dumps(bm, indent=1, sort_keys=True) != text:
        raise Refused('H2LM_PYBUNDLE.json does not round-trip through json.dumps(indent=1, sort_keys=True): not re-writing it')
    changed = False
    src = bm.get('source')
    if isinstance(src, dict):
        for k, v in list(src.items()):
            if isinstance(v, str) and APPDATA_ROAMING.match(v):
                src[k] = APPDATA_ROAMING.sub('%APPDATA%', v, count=1)
                changed = True
    out = json.dumps(bm, indent=1, sort_keys=True)
    m = PERSONAL_PATH.search(out)
    if m:
        raise Refused('H2LM_PYBUNDLE.json still holds a C:\\Users\\ path after the scrub (offset %d)' % m.start())
    return out.encode('utf-8'), changed


def with_member(files, path, data):
    """the files[] list with one member's size / md5 replaced"""
    return [dict(f, size=len(data), md5=L.md5_bytes(data)) if f['path'] == path else f for f in files]


def zip_py(spec, out_dir, app_version, keep):
    py = spec['rt']['py']
    src = L.resolve(py['source_zip'], {})
    if not os.path.isfile(src):
        raise Refused('no Python bundle zip %s -- build it: %s' % (src, py.get('rebuild')))
    files, entries = [], {}
    with zipfile.ZipFile(src) as z:
        bm = json.loads(z.read('H2LM_PYBUNDLE.json'))
        for i in z.infolist():
            if i.is_dir():
                raise Refused('%s: directory entry %s' % (src, i.filename))
            data = z.read(i)
            files.append({'path': i.filename, 'size': len(data), 'md5': L.md5_bytes(data)})
            entries[i.filename] = data
            if i.filename in keep:
                keep[i.filename] = data
    if bm.get('schema') != 'h2lm-pybundle/1':
        raise Refused('%s: H2LM_PYBUNDLE.json schema %r' % (src, bm.get('schema')))
    others = sorted((f for f in files if f['path'] != 'H2LM_PYBUNDLE.json'), key=lambda f: f['path'])
    want = sorted(({'path': f['path'], 'size': f['size'], 'md5': f['md5']} for f in bm['files']), key=lambda f: f['path'])
    if others != want:
        raise Refused('%s: entries differ from its H2LM_PYBUNDLE.json files[]' % src)
    bid = L.content_hash(others)
    if bid != bm['bundle_id']:
        raise Refused('%s: recomputed bundle_id %s != %s' % (src, bid, bm['bundle_id']))
    if bm.get('scipy') is not None or any(f['path'].startswith('site-packages/scipy') for f in files):
        raise Refused('%s carries scipy; v1 ships the numpy-only bundle (N4 gate PASS)' % src)
    if not any(f['path'] == 'py.exe' for f in files):
        raise Refused('%s has no py.exe at its root' % src)
    probs = L.check_det_zip(src, files)
    if probs:
        raise Refused('%s is not in the deterministic payload format: %s' % (src, '; '.join(probs[:5])))
    zp = os.path.join(out_dir, py['zip'])
    # E1: only H2LM_PYBUNDLE.json may change (bundle_id is computed over every OTHER member, so it stays); every other member is
    # carried byte for byte and re-verified. A bundle whose JSON needs no scrub is copied verbatim, as before.
    new_json, scrubbed = scrub_pybundle_json(entries['H2LM_PYBUNDLE.json'])
    if scrubbed:
        files = with_member(files, 'H2LM_PYBUNDLE.json', new_json)
        entries['H2LM_PYBUNDLE.json'] = new_json
        L.write_det_zip(zp, sorted(entries.items()))
        probs = L.check_det_zip(zp, files)
        if probs:
            raise Refused('the scrubbed Python bundle does not verify: %s' % '; '.join(probs[:5]))
    else:
        shutil.copyfile(src, zp)
        if L.md5_file(zp) != L.md5_file(src):
            raise Refused('copy of %s does not verify' % src)
    return component(py['name'], zp, files, 'rt', py['dirName'], app_version), {
        'source_zip': src, 'bundle_id': bm['bundle_id'], 'python': bm.get('python'), 'numpy': bm.get('numpy'), 'scipy': bm.get('scipy'),
        'pybundle_json_scrubbed': scrubbed}


# ---------------------------------------------------------------------------------------------------------------- notices
SECTIONS = """1. PowerShell {pwsh_version} -- Copyright (c) Microsoft Corporation -- MIT License
   The unmodified files of the official PowerShell-{pwsh_version}-win-x64.zip (sha256 {pwsh_sha256}).
   PowerShell's ThirdPartyNotices.txt lists the open-source components PowerShell itself contains (the .NET runtime and
   libraries among them) and their licenses.
   Files: licenses\\PowerShell\\LICENSE.txt, licenses\\PowerShell\\ThirdPartyNotices.txt

2. Python {python} -- Copyright (c) Python Software Foundation -- PSF License Agreement for Python
   An isolated copy of the official Windows x64 CPython {python} files (bundle {bundle_id}). site.py is patched so the user
   site-packages stays off in isolated mode, and a sitecustomize.py is added. CPython's LICENSE.txt also carries the
   licenses of the libraries CPython includes (OpenSSL, libffi, SQLite, zlib, bzip2, xz, expat, ...).
   File: licenses\\CPython\\LICENSE.txt

3. NumPy {numpy} -- Copyright (c) NumPy Developers -- BSD 3-Clause License
   numpy's LICENSE.txt also carries the licenses of the libraries its wheel bundles (OpenBLAS, LAPACK, the GCC runtime).
   File: licenses\\NumPy\\LICENSE.txt

4. Microsoft Visual C++ runtime DLLs -- Copyright (c) Microsoft Corporation
   vcruntime140.dll and vcruntime140_1.dll (shipped with CPython) and numpy.libs\\msvcp140-*.dll (shipped with NumPy), as
   redistributed by those projects under the Microsoft Visual C++ redistributable terms.

5. Osoyoos Launcher -- Copyright (c) 2017 num0005, Copyright (c) 2022 Osoyoos Launcher Contributors -- MIT License
   osoyoos\\H2ToolHooks.dll is built from the Osoyoos launcher's H2ToolHooks source (the Halo 2 tool fixes this program
   turns on). standalone\\h2v_scenery_bounds.db is the scenery bounds database the Osoyoos scenery fix reads; it is copied
   into your H2EK folder only when that folder has none.
   File: licenses\\Osoyoos\\LICENSE.md"""


def zip_notices(spec, out_dir, app_version, keep, pwsh_info, py_info):
    n = spec['notices']
    oso_p = L.resolve(n['osoyoos_license'], {})
    oso = open(oso_p, 'rb').read()
    if L.md5_bytes(oso) != n['osoyoos_license_md5']:
        raise Refused('%s md5 %s is not the recorded %s' % (oso_p, L.md5_bytes(oso), n['osoyoos_license_md5']))
    lic = [('licenses/PowerShell/LICENSE.txt', keep['LICENSE.txt@pwsh']),
           ('licenses/PowerShell/ThirdPartyNotices.txt', keep['ThirdPartyNotices.txt']),
           ('licenses/CPython/LICENSE.txt', keep['LICENSE.txt@py']),
           ('licenses/NumPy/LICENSE.txt', keep['site-packages/numpy-2.2.6.dist-info/LICENSE.txt']),
           ('licenses/Osoyoos/LICENSE.md', oso)]
    for p, d in lic:
        if not d:
            raise Refused('license text %s not found in its component' % p)
    head = open(L.resolve(n['header'], {}), encoding='utf-8').read()
    sec = SECTIONS.format(pwsh_version=pwsh_info['version'], pwsh_sha256=pwsh_info['sha256'], python=py_info['python'],
                          numpy=py_info['numpy'], bundle_id=py_info['bundle_id'][:8])
    parts = [head.replace('{COMPONENTS}', sec).rstrip('\n'), '']
    for p, d in lic:
        t = d.decode('utf-8', 'replace').replace('\r\n', '\n').strip('\n﻿')
        parts += ['', '=' * 118, p.replace('/', '\\'), '=' * 118, '', t, '']
    text = '\n'.join(parts).replace('\n', '\r\n').encode('utf-8')
    entries = [('NOTICES.txt', text)] + lic
    zp = os.path.join(out_dir, n['zip'])
    files = L.write_det_zip(zp, entries)
    with open(os.path.join(out_dir, 'NOTICES.txt'), 'wb') as f:
        f.write(text)
    return component(n['name'], zp, files, 'rt', n['dirName'], app_version)


# ---------------------------------------------------------------------------------------------------------------- main
def validate_manifest(m):
    """the checks the shell's PayloadManifest.Validate / installer make, so a bad manifest never reaches an exe"""
    if m['format'] != 1 or not m['components']:
        raise Refused('manifest format / components')
    dirs = set()
    for c in m['components']:
        if c['target'] not in ('rt', 'inst'):
            raise Refused('%s: target %s' % (c['name'], c['target']))
        if any(ch in c['dirName'] for ch in '<>:"/\\|?*') or c['dirName'].startswith('.'):
            raise Refused('%s: bad dirName %s' % (c['name'], c['dirName']))
        if (c['target'], c['dirName'].lower()) in dirs:
            raise Refused('duplicate dirName %s' % c['dirName'])
        dirs.add((c['target'], c['dirName'].lower()))
        if c['fileCount'] != len(c['files']):
            raise Refused('%s: fileCount' % c['name'])
        seen = set()
        for f in c['files']:
            p = f['path']
            if not p or p.startswith('/') or '\\' in p or ':' in p or any(x in ('', '.', '..') for x in p.split('/')):
                raise Refused('%s: unsafe path %r' % (c['name'], p))
            if p.lower() in seen:
                raise Refused('%s: duplicate path %s' % (c['name'], p))
            seen.add(p.lower())
            if len(f['md5']) != 32:
                raise Refused('%s: %s md5' % (c['name'], p))
        probs = L.check_det_zip(os.path.join(m['_dir'], c['resource'].split('/', 1)[1]), c['files'])
        if probs:
            raise Refused('%s zip does not match its manifest entry: %s' % (c['name'], '; '.join(probs[:5])))
    inst = [c for c in m['components'] if c['target'] == 'inst']
    if len(inst) != 1:
        raise Refused('exactly one inst component is required, found %d' % len(inst))
    rt = [c for c in m['components'] if c['target'] == 'rt']
    if not any(any(f['path'] == 'pwsh.exe' for f in c['files']) for c in rt):
        raise Refused('no rt component holds pwsh.exe at its root')
    if not any(any(f['path'] == 'py.exe' for f in c['files']) for c in rt):
        raise Refused('no rt component holds py.exe at its root')
    ip = set(f['path'] for f in inst[0]['files'])
    for need in ('engine_v373/gpu_bake.ps1', 'engine_v373/v043-tagwriter/lmw_install.py'):
        if need not in ip:
            raise Refused('INST lacks %s (the shell resolves it)' % need)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--stage', default=os.path.join(L.BUILD, 'stage'))
    ap.add_argument('--out', default=os.path.join(L.BUILD, 'payload'))
    ap.add_argument('--spec', default=L.SPEC)
    ap.add_argument('--report', default=None, help='default: <out>_report.json beside --out')
    a = ap.parse_args(argv)
    L.below_normal()
    t0 = time.time()
    try:
        spec = L.load_spec(a.spec)
        ver = L.app_version()
        out = os.path.abspath(a.out)
        if not out.lower().startswith(L.BUILD.lower() + os.sep):
            raise Refused('--out %s must be under %s' % (out, L.BUILD))
        tmp = out + '.tmp_%d' % os.getpid()
        if os.path.exists(tmp):
            raise Refused('%s exists' % tmp)
        os.makedirs(tmp)
        keep_pwsh = {'LICENSE.txt': None, 'ThirdPartyNotices.txt': None}
        keep_py = {'LICENSE.txt': None, 'site-packages/numpy-2.2.6.dist-info/LICENSE.txt': None}
        c_pwsh, i_pwsh = zip_pwsh(spec, tmp, ver, keep_pwsh)
        print('  pwsh   %-28s %8.1f MB  %d files  %s' % (c_pwsh['resource'], c_pwsh['zipSize'] / 1e6, c_pwsh['fileCount'], c_pwsh['dirName']))
        c_py, i_py = zip_py(spec, tmp, ver, keep_py)
        print('  py     %-28s %8.1f MB  %d files  %s' % (c_py['resource'], c_py['zipSize'] / 1e6, c_py['fileCount'], c_py['dirName']))
        keep = {'LICENSE.txt@pwsh': keep_pwsh['LICENSE.txt'], 'ThirdPartyNotices.txt': keep_pwsh['ThirdPartyNotices.txt'],
                'LICENSE.txt@py': keep_py['LICENSE.txt'],
                'site-packages/numpy-2.2.6.dist-info/LICENSE.txt': keep_py['site-packages/numpy-2.2.6.dist-info/LICENSE.txt']}
        c_not = zip_notices(spec, tmp, ver, keep, i_pwsh, i_py)
        print('  notice %-28s %8.1f MB  %d files  %s' % (c_not['resource'], c_not['zipSize'] / 1e6, c_not['fileCount'], c_not['dirName']))
        c_inst, srep = zip_inst(spec, os.path.abspath(a.stage), tmp, ver)
        print('  inst   %-28s %8.1f MB  %d files  %s' % (c_inst['resource'], c_inst['zipSize'] / 1e6, c_inst['fileCount'], c_inst['dirName']))
        feats = srep.get('features')
        if not isinstance(feats, dict) or not isinstance(feats.get('ao'), dict) or not isinstance(feats['ao'].get('enabled'), bool):
            raise Refused('the stage report has no features.ao verdict (review fix 15): re-run stage_engine.py')
        if not isinstance(feats.get('probes'), dict) or not isinstance(feats['probes'].get('enabled'), bool):
            raise Refused('the stage report has no features.probes verdict (V5, gate T4): re-run stage_engine.py')
        man = {'format': 1, 'appVersion': ver, 'components': [c_pwsh, c_py, c_not, c_inst],
               'features': {'ao': {k: v for k, v in feats['ao'].items() if v is not None},
                            'probes': {k: v for k, v in feats['probes'].items() if v is not None}}}
        if not feats['ao']['enabled']:
            print('  WARNING: AO is DISABLED in this payload: %s' % feats['ao'].get('reason'))
        if not feats['probes']['enabled']:
            print('  WARNING: light probes are DISABLED in this payload: %s' % feats['probes'].get('reason'))
        validate_manifest(dict(man, _dir=tmp))
        man_md5 = L.write_json(os.path.join(tmp, 'MANIFEST.json'), man)
        rep = {
            'format': 'h2lm-payload-report/1', 'tool': TOOL, 'built': time.strftime('%Y-%m-%dT%H:%M:%S'),
            'seconds': round(time.time() - t0, 1), 'appVersion': ver, 'payload_dir': out, 'manifest_md5': man_md5,
            'stage_report': os.path.join(os.path.abspath(a.stage), 'stage_report.json'),
            'stage_content_hash': srep['content_hash'], 'stage_engine': srep['engine'],
            'sources': {'pwsh': i_pwsh, 'py': i_py},
            'zips': [{k: c[k] for k in ('name', 'resource', 'dirName', 'target', 'zipSize', 'zipMd5', 'fileCount', 'totalBytes', 'maxPathLen')}
                     for c in man['components']],
            'payload_bytes': sum(c['zipSize'] for c in man['components']) + os.path.getsize(os.path.join(tmp, 'MANIFEST.json')),
        }
        report = a.report or (out + '_report.json')
        if os.path.exists(out):
            L.safe_remove_tree(out, L.BUILD)
        os.replace(tmp, out)
        L.write_json(report, rep)
        print('PAYLOAD OK: %d components, %.1f MB embedded (zips + MANIFEST.json %s), appVersion %s -> %s  [%.1f s]' % (
            len(man['components']), rep['payload_bytes'] / 1e6, man_md5[:8], ver, out, time.time() - t0))
        return 0
    except Refused as e:
        print('zip_payloads: REFUSED: %s' % e, file=sys.stderr)
        try:
            if 'tmp' in locals() and os.path.isdir(tmp):
                L.safe_remove_tree(tmp, L.BUILD)      # partial zips only; the previous payload dir is untouched
        except Refused as e2:
            print('zip_payloads: (partial output kept: %s)' % e2, file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
