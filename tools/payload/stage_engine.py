"""stage_engine.py -- S1.9: stage the engine instance (INST) of the H2 GPU Lightmapper payload. Stdlib only.

Builds <out>\\inst\\ (the tree the shell extracts to WORK\\_runtime\\<appVersion>-<h8>\\, mirroring the GPU Lightmap Work code
layout) plus <out>\\stage_report.json, and regenerates ENGINE_MANIFEST into the build dir only (<out>\\ENGINE_MANIFEST.txt).

  * production files (tools\\payload\\payload_spec.json "groups") are copied BY MD5 AT BUILD TIME: each source is hashed, its
    bytes copied, the copy re-hashed; NO md5 of an engine binary is hard-coded (the tie-rule promotion replaces fullgpu.exe and
    its kernel headers) -- the md5s are recorded in the report / MANIFEST.json;
  * the reviewed patches are applied to COPIES: gpu_bake.ps1 P1->P3->P2 (engine_v373) and P1->P3 (dev_AO) with the build-time
    token OSOY_LAUNCH_MD5 = the staged osoy_launch.exe (tools\\patch_series.py), P5 / P6 (patches\\series_research.json), N4
    (patches\\N4_ao_seamfix, strict: base / patch / result md5 pinned in N4_MANIFEST.json);
  * built pieces are taken only when they are the tested build: osoy_launch.exe (S1.3 P4: md5 pin + P4 applied to the
    production source gives the built source), gpucheck.exe / gpucheck_plain.exe (S1.6 build record md5 + source md5);
  * new files: sh_stage.py (N1), ao_ballgrid.py (N4 add, md5-pinned); the bounds DB (pinned 6814f29a);
  * review fix (finding 15): the AO trace engine (dev_AO) must carry the lightmap engine's kernel headers (every .h / .hlsl in
    both v041-fullgpu folders md5-equal); otherwise the payload ships with AO DISABLED (stage_report features.ao ->
    MANIFEST.json features.ao, which the shell checks before it plans an AO trace);
  * V1 (2026-10-14): the AO engine's production source is _research\\ao\\dev_AO2 (engine_v373 TIE1 + the AO additions), staged
    under the unchanged INST name _research\\ao\\dev_AO; AO is enabled only when its kernel files (v041-fullgpu + v015-direct)
    equal engine_v373's AND the V1 neutrality proof (inst.ao_engine.proof) names the exact fullgpu / gather.cpp md5s on disk;
  * V5 (2026-10-14): light probes (Standard on a probe BSP, Lightprobes only) are enabled (MANIFEST features.probes) only when the
    gate-T4 proof (inst.probes_gate.proof, verdict PASS) names the md5 on disk of every probe-path file (inst.probes_gate.files);
  * U1 (2026-10-15, the user's OK): P1-P3 (+ F24, lm_tagwriter --tool) are UPSTREAMED into production gpu_bake.ps1 (engine_v373,
    dev_AO2). patch_series proves production == the chain of the pinned pre-upstream base + the payload's token values, byte for
    byte; such a target is staged as a PRISTINE production copy (kind prod, role upstreamed:...), and gates that pin a series
    target's base (the T4 probes proof names gpu_bake.ps1 = "the P1-P3 base") are checked against that verified base md5;
  * U2 (2026-10-15): the three lmw_tagio.py walkers (engine_v373 and dev_AO tag writers, the GLW-root copy) come from the patch
    series patches\\tagio\\series_tagio.json (SHORT block elements read as tool_fast does: the 2004 stock MP scenarios'
    decorators cache blocks); production keeps the bases until the user approves an upstream;
  * ASSERTS at the end: production re-hashed (unchanged during the build), every pristine staged file == production, every
    patched file == its pinned result, every staged byte == the report; the patched gpu_bake.ps1 code references all resolve
    inside INST; every local Python import of a shipped .py resolves inside INST and nothing imports scipy (N4); the 4 slot
    DLLs carry the 60-char pristine G: path exactly twice; PE machine / import audit (report only).

  py -I -B tools\\payload\\stage_engine.py [--out build\\s19\\stage] [--glw DIR] [--h2root DIR] [--spec F]
                                          [--with-custom-engine] [--selftest-mutate REL]
Exit codes: 0 staged, 2 refused (drift / pin mismatch / missing input / failed assertion; the previous stage is left as it
was), 1 usage / internal error. Production is only read; the output must be under build\\s19 (or --out-root-ok)."""
import argparse
import ast
import json
import os
import re
import sys
import time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import h2lm_payload_lib as L   # noqa: E402
from h2lm_payload_lib import Refused   # noqa: E402

PS = L.load_module('h2lm_patch_series', os.path.join(L.PROJ, 'tools', 'patch_series.py'))
SP = PS.SP   # tools\strict_patch.py, the hunk engine patch_series loaded

TOOL = 'stage_engine.py (S1.9)'


class Stager:
    def __init__(self, spec, roots, inst_dir, with_custom=False, mutate=None):
        self.spec = spec
        self.roots = roots
        self.inst = inst_dir
        self.with_custom = with_custom
        self.mutate = mutate
        self.entries = {}          # dest -> record
        self.sources = {}          # abs source -> md5 at copy time (re-hashed at the end)
        self.upstream_bases = {}   # U1: abs production path of an upstreamed series target -> its verified pre-upstream base md5
        self.notes = []
        self.warnings = []

    # ------------------------------------------------------------------ helpers
    def _add(self, dest, data, rec):
        dest = dest.strip('/')
        if dest in self.entries:
            raise Refused('two spec entries write %s' % dest)
        for k in self.entries:
            if k.lower() == dest.lower():
                raise Refused('%s and %s differ only in case' % (k, dest))
        p = os.path.join(self.inst, *dest.split('/'))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, 'xb') as f:
            f.write(data)
        if L.md5_file(p) != L.md5_bytes(data):
            raise Refused('%s: the written copy does not verify' % dest)
        rec.update(path=dest, size=len(data), md5=L.md5_bytes(data))
        self.entries[dest] = rec
        return rec

    def _read_source(self, path):
        try:
            data = open(path, 'rb').read()
        except OSError as e:
            raise Refused('cannot read %s: %s' % (path, e))
        m = L.md5_bytes(data)
        if path in self.sources and self.sources[path] != m:
            raise Refused('%s changed while it was being read twice (%s -> %s)' % (path, self.sources[path], m))
        self.sources[path] = m
        return data, m

    # ------------------------------------------------------------------ production copies
    def stage_groups(self):
        for g in self.spec['inst']['groups']:
            src_dir = L.resolve(g['from'], self.roots)
            if not os.path.isdir(src_dir):
                raise Refused('group %s: %s is not a folder' % (g['id'], src_dir))
            todo = list(g.get('files', []))
            for d in g.get('dirs', []):
                ad = os.path.join(src_dir, *d.split('/'))
                if not os.path.isdir(ad):
                    raise Refused('group %s: dir %s is missing' % (g['id'], ad))
                for dp, dns, fns in os.walk(ad):
                    dns.sort()
                    for fn in sorted(fns):
                        if fn.lower().endswith('.pyc') or '__pycache__' in dp:
                            continue
                        todo.append(os.path.relpath(os.path.join(dp, fn), src_dir).replace('\\', '/'))
            for rel in todo:
                src = os.path.join(src_dir, *rel.split('/'))
                if not os.path.isfile(src):
                    raise Refused('group %s: %s is missing in production' % (g['id'], src))
                if L.is_reparse(src):
                    raise Refused('group %s: %s is a reparse point' % (g['id'], src))
                data, m = self._read_source(src)
                dest = (g['to'] + '/' + rel) if g['to'] else rel
                self._add(dest, data, {'kind': 'prod', 'role': 'prod', 'sourceMd5': m, 'source': src, 'group': g['id']})

    def stage_pinned(self):
        for p in self.spec['inst'].get('pinned', []):
            src = L.resolve(p['from'], self.roots)
            data, m = self._read_source(src)
            if m != p['expect_md5']:
                raise Refused('%s: md5 %s is not the pinned %s (%s)' % (src, m, p['expect_md5'], p.get('label', '')))
            self._add(p['to'], data, {'kind': 'prod', 'role': p['role'], 'sourceMd5': m, 'source': src, 'label': p.get('label')})

    # ------------------------------------------------------------------ built + new pieces
    def stage_built(self):
        for b in self.spec['inst'].get('built', []):
            src = L.resolve(b['from'], self.roots)
            data, m = self._read_source(src)
            rec = {'kind': 'built', 'role': b['role'], 'source': src, 'label': b.get('label'), 'rebuild': b.get('rebuild')}
            if 'expect_md5' in b and m != b['expect_md5']:
                raise Refused('%s: md5 %s is not the tested %s build %s -- rebuild/re-test it (%s) and update the spec pin'
                              % (src, m, b.get('label'), b['expect_md5'], b.get('rebuild')))
            if 'record' in b:
                recf = L.resolve(b['record'], self.roots)
                r = json.load(open(recf, encoding='utf-8')).get(b['record_key'])
                if not r:
                    raise Refused('%s: no %r record' % (recf, b['record_key']))
                if r.get('md5') != m or r.get('size') != len(data):
                    raise Refused('%s: md5 %s / size %d is not the build record %s / %s (%s)' % (src, m, len(data), r.get('md5'), r.get('size'), recf))
                if 'source' in b:
                    sm = L.md5_file(L.resolve(b['source'], self.roots))
                    if r.get('source_md5') != sm:
                        raise Refused('%s was built from source %s but %s is now %s -- rebuild (%s)' % (src, r.get('source_md5'), b['source'], sm, b.get('rebuild')))
                    rec['builtFromSourceMd5'] = sm
                rec['record'] = recf
            if 'provenance' in b:
                pv = b['provenance']
                praw = open(os.path.join(L.PROJ, *pv['patch'].split('/')), 'rb').read()
                prod_src, pm = self._read_source(L.resolve(pv['prod_source'], self.roots))
                built_src = open(L.resolve(pv['built_source'], self.roots), 'rb').read()
                bm = L.md5_bytes(built_src)
                hdr = ('# source: x\n# base-md5: %s\n# result-md5: %s\n' % (pm, bm)).encode('ascii')
                try:
                    out = SP.apply_bytes(SP.read_patch(hdr + praw), prod_src)
                except SP.PatchError as e:
                    raise Refused('%s does not take production %s to the built source %s: %s' % (pv['patch'], pv['prod_source'], pv['built_source'], e))
                rec['provenance'] = {'patch': pv['patch'], 'patch_md5': L.md5_bytes(praw), 'prod_source_md5': pm, 'built_source_md5': L.md5_bytes(out)}
            if 'replaces' in b:
                rp = L.resolve(b['replaces'], self.roots)
                _, rm = self._read_source(rp)
                rec['sourceMd5'] = rm          # the production file this build replaces
                rec['replaces'] = rp
            self._add(b['to'], data, rec)

    def stage_new(self):
        for n in self.spec['inst'].get('new', []):
            src = L.resolve(n['from'], self.roots)
            data, _ = self._read_source(src)
            self._add(n['to'], data, {'kind': 'new', 'role': n['role'], 'source': src, 'label': n.get('label')})

    # ------------------------------------------------------------------ patched copies
    def stage_series(self):
        for s in self.spec['inst'].get('series', []):
            sf = os.path.join(L.PROJ, *s['file'].split('/'))
            try:
                series = PS.load_series(sf)
            except PS.Refused as e:
                raise Refused(str(e))
            sets = {}
            for tok, how in s.get('tokens', {}).items():
                kind, dest = how.split(':', 1)
                if dest not in self.entries:
                    raise Refused('token %s needs %s staged first' % (tok, dest))
                if kind != 'md5upper':
                    raise Refused('token %s: unknown source %s' % (tok, kind))
                sets[tok] = self.entries[dest]['md5'].upper()
            glw = self.roots['GLW']
            for t in series['targets']:
                try:
                    data, prec = PS.build_target(series, os.path.dirname(sf), t, glw, sets)
                except PS.Refused as e:
                    raise Refused('%s: %s' % (s['label'], e))
                src = os.path.join(glw, *t.get('src', t['path']).split('/'))   # V1: "src" = production path when it is not the INST path
                _, sm = self._read_source(src)   # recorded for the end-of-build re-hash
                role = s['roles'].get(t['path'])
                if not role:
                    raise Refused('series %s target %s has no role in the spec' % (s['id'], t['path']))
                if prec.get('upstreamed'):
                    # U1 (2026-10-15): production already holds this series -- patch_series proved production == chain(pinned base)
                    # + the payload's token values byte for byte, so the INST file is a PRISTINE production copy (kind prod)
                    if sm != prec['production_md5'] or L.md5_bytes(data) != sm:
                        raise Refused('%s: the upstreamed %s is not production verbatim (%s / %s / %s)'
                                      % (s['label'], t['path'], sm, prec['production_md5'], L.md5_bytes(data)))
                    self.upstream_bases[src] = prec['base_md5']
                    what = role.split(':', 1)[-1]
                    self.notes.append('%s %s: production %s ALREADY HOLDS %s (U1 upstream; == the chain of the pinned base %s + tokens %s): '
                                      'copied verbatim' % (s['label'], t['path'], sm[:8], what, prec['base_md5'][:8], prec.get('tokens')))
                    self._add(t['path'], data, {'kind': 'prod', 'role': 'upstreamed:' + what, 'sourceMd5': sm, 'source': src,
                                                'label': s['label'], 'series': s['file'],
                                                'upstream': {'base_md5': prec['base_md5'], 'base_file': prec.get('base_file'),
                                                             'patches': prec['patches'], 'preTokenMd5': prec['pre_token_md5'],
                                                             'tokens': prec.get('tokens', {})}})
                    continue
                self._add(t['path'], data, {'kind': 'patched', 'role': role, 'sourceMd5': prec['base_md5'], 'source': src,
                                            'label': s['label'], 'series': s['file'], 'patches': prec['patches'],
                                            'preTokenMd5': prec['pre_token_md5'], 'tokens': prec.get('tokens', {})})

    def stage_n4(self):
        n = self.spec['inst'].get('n4')
        if not n:
            return
        mf = os.path.join(L.PROJ, *n['manifest'].split('/'))
        M = json.load(open(mf, encoding='utf-8'))
        if not (M.get('ship') is True and M.get('gate', {}).get('verdict') == 'PASS'):
            raise Refused('%s: N4 is not gated PASS / ship (v1 would need scipy instead)' % mf)
        mdir = os.path.dirname(mf)
        # A1 2026-10-15: N4 patches TWO production files (ao_seamfix.py and ao_denoise.py): the manifest has `targets` (the S1.8 manifest had one `target`)
        for t in (M.get('targets') or [M['target']]):
            praw = open(os.path.join(mdir, t['patch']), 'rb').read()
            if L.md5_bytes(praw) != t['patch_md5']:
                raise Refused('%s: md5 %s is not the gated %s' % (t['patch'], L.md5_bytes(praw), t['patch_md5']))
            src = os.path.join(self.roots['GLW'], *t['path'].split('/'))
            sdata, sm = self._read_source(src)
            if sm != t['pristine_md5']:
                raise Refused('%s: production md5 %s is not the N4 base %s -- re-make N4 (tests\\a1_ao_fix\\remake_n4.py) and re-run its gate (ab_n4.py)' % (src, sm, t['pristine_md5']))
            if t.get('base_file'):          # the project's pinned copy of the pristine base must be the base the patch was gated on
                bdata = open(os.path.join(L.PROJ, *t['base_file'].split('/')), 'rb').read()
                if L.md5_bytes(bdata) != t['pristine_md5']:
                    raise Refused('%s: the pinned base copy md5 %s is not %s' % (t['base_file'], L.md5_bytes(bdata), t['pristine_md5']))
            try:
                if praw.startswith(b'#'):   # a strict_patch.py patch with its own header: check it names the manifest's md5s
                    P = SP.read_patch(praw)
                    if P['source'] != t['path'] or P['base-md5'] != t['pristine_md5'] or P['result-md5'] != t['patched_md5']:
                        raise Refused('%s: its header (%s %s -> %s) is not the manifest\'s (%s %s -> %s)' %
                                      (t['patch'], P['source'], P['base-md5'][:8], P['result-md5'][:8], t['path'], t['pristine_md5'][:8], t['patched_md5'][:8]))
                else:                       # the S1.8 form: a bare diff, the header comes from the manifest
                    hdr = ('# source: %s\n# base-md5: %s\n# result-md5: %s\n' % (t['path'], t['pristine_md5'], t['patched_md5'])).encode('ascii')
                    P = SP.read_patch(hdr + praw)
                out = SP.apply_bytes(P, sdata)
            except SP.PatchError as e:
                raise Refused('N4: %s' % e)
            self._add(t['path'], out, {'kind': 'patched', 'role': n['role'], 'sourceMd5': sm, 'source': src, 'label': n['label'],
                                       'patches': [{'file': n['manifest'].rsplit('/', 1)[0] + '/' + t['patch'], 'patch_md5': t['patch_md5'], 'result_md5': t['patched_md5']}]})
        for a in M.get('adds', []):
            ap = os.path.join(mdir, *a['src'].split('/'))
            data, am = self._read_source(ap)
            if am != a['md5'] or len(data) != a['size']:
                raise Refused('%s: md5 %s / %d is not the gated %s / %d' % (ap, am, len(data), a['md5'], a['size']))
            self._add(a['path'], data, {'kind': 'new', 'role': n['add_role'], 'source': ap, 'label': n['label']})

    # ------------------------------------------------------------------ optional Custom engine (gate T3a)
    def stage_custom(self):
        ce = self.spec['inst'].get('custom_engine', {})
        latest_p = L.resolve(ce['latest'], self.roots)
        if not self.with_custom:
            self.notes.append('engine_custom/ NOT shipped (Custom runs engine_v373 behind the dev flag until gate T3a passes; '
                              'pass --with-custom-engine after T3a)')
            return {'shipped': False, 'reason': 'not requested (gate T3a pending)'}
        try:
            lt = json.load(open(latest_p, encoding='utf-8'))
        except (OSError, ValueError) as e:
            raise Refused('--with-custom-engine: %s: %s' % (latest_p, e))
        proof = str(lt.get('gpu_identity_proof', ''))
        if not lt.get('ok') or not proof.upper().startswith('PASS'):
            raise Refused('--with-custom-engine: gate T3a has not passed (%s: gpu_identity_proof = %r)' % (latest_p, proof))
        prod_fg = os.path.join(self.roots['GLW'], 'engine_v373', 'v041-fullgpu', 'fullgpu.exe')
        _, pm = self._read_source(prod_fg)
        if lt.get('production_fullgpu_md5') != pm:
            raise Refused('--with-custom-engine: the guard was built against fullgpu %s but production is now %s -- re-run '
                          'native\\fullgpu_guard\\build_fullgpu_guard.py and gate T3a' % (lt.get('production_fullgpu_md5'), pm))
        gp = os.path.join(os.path.dirname(latest_p), *lt['guard_fullgpu'].replace('\\', '/').split('/'))
        gdata, gm = self._read_source(gp)
        if gm != lt.get('guard_md5'):
            raise Refused('--with-custom-engine: %s md5 %s is not the T3a-tested %s' % (gp, gm, lt.get('guard_md5')))
        for dest, rec in sorted(list(self.entries.items())):
            if not dest.startswith('engine_v373/'):
                continue
            nd = 'engine_custom/' + dest[len('engine_v373/'):]
            if nd == 'engine_custom/v041-fullgpu/fullgpu.exe':
                self._add(nd, gdata, {'kind': 'built', 'role': 'built:N3', 'source': gp, 'label': 'S1.7 N3 (T3a PASS)',
                                      'sourceMd5': rec['sourceMd5'], 'replaces': rec.get('source')})
            else:
                data = open(os.path.join(self.inst, *dest.split('/')), 'rb').read()
                r2 = {k: v for k, v in rec.items() if k not in ('path', 'size', 'md5')}
                r2['copyOf'] = dest
                self._add(nd, data, r2)
        return {'shipped': True, 'latest': latest_p, 'guard_md5': gm, 'proof': proof}

    # ------------------------------------------------------------------ AO engine sync (review fix, finding 15)
    def check_ao_engine(self):
        """the AO trace (gpu_ao trace -> dev_AO's fullgpu in GPULM_AO mode) re-runs texelgen, the gate-first pre-pass and the
        gate walk to pick 'the bake's final cells' as the AO representatives. That premise holds only while dev_AO's kernels
        are the lightmap engine's: every .h / .hlsl present in BOTH _research/ao/dev_AO/v041-fullgpu and engine_v373/v041-fullgpu
        must have the same md5 (production is only read). On a mismatch the payload ships with AO DISABLED (MANIFEST features.ao)
        and the shell refuses --ao; fix = rebuild dev_AO's fullgpu on the current headers (host relink with the AO kernels),
        re-run the AO neutrality check, then T2-E.
        V1 (2026-10-14): the AO engine is the spec's inst.ao_engine group (its production source: _research/ao/dev_AO2, staged
        as INST _research/ao/dev_AO); the comparison covers every .h / .hlsl / .hlsli of v041-fullgpu AND v015-direct (gather.hlsl
        #includes ../v015-direct), and a kernel file the lightmap engine has but the AO engine lacks is a mismatch too. Header
        equality cannot prove the HOST (gather.cpp + the AO hunks) neutral, so AO is enabled only with the V1 proof record
        (inst.ao_engine.proof, verdict PASS: static + keyed-identical bake + deterministic AO trace) naming the AO fullgpu.exe,
        production fullgpu.exe and both gather.cpp md5s that are on disk NOW. Any production / AO engine change -> AO DISABLED until
        tests\\v1_ao_engine is re-run for it."""
        glw = self.roots['GLW']
        aoe = self.spec['inst'].get('ao_engine') or {}
        grp = next((g for g in self.spec['inst']['groups'] if g['id'] == aoe.get('group')), None)
        base = {'enabled': False, 'compared': 0, 'differ': [], 'missing': []}
        if not grp:
            return dict(base, reason='the payload spec names no AO engine group (inst.ao_engine.group)')
        ea, eb = os.path.join(glw, 'engine_v373'), L.resolve(grp['from'], self.roots)
        names, differ, missing = [], [], []
        for sub in ('v041-fullgpu', 'v015-direct'):
            a, b = os.path.join(ea, sub), os.path.join(eb, sub)
            if not (os.path.isdir(a) and os.path.isdir(b)):
                return dict(base, reason='cannot compare the AO engine with the lightmap engine (%s / %s missing)' % (a, b))
            for n in sorted(os.listdir(a)):
                if not n.lower().endswith(('.h', '.hlsl', '.hlsli')) or not os.path.isfile(os.path.join(a, n)):
                    continue
                if not os.path.isfile(os.path.join(b, n)):
                    missing.append(sub + '/' + n)
                    continue
                names.append(sub + '/' + n)
                _, ma = self._read_source(os.path.join(a, n))     # re-hashed at the end like every other input
                _, mb = self._read_source(os.path.join(b, n))
                if ma != mb:
                    differ.append({'file': sub + '/' + n, 'engine_v373': ma, 'ao_engine': mb})
        fa = self._read_source(os.path.join(ea, 'v041-fullgpu', 'fullgpu.exe'))[1]
        fb = self._read_source(os.path.join(eb, 'v041-fullgpu', 'fullgpu.exe'))[1]
        ga = self._read_source(os.path.join(ea, 'v041-fullgpu', 'gather.cpp'))[1]
        gb = self._read_source(os.path.join(eb, 'v041-fullgpu', 'gather.cpp'))[1]
        rec = dict(base, compared=len(names), differ=differ, missing=missing, ao_engine_dir=eb, engine_v373_fullgpu=fa, ao_engine_fullgpu=fb,
                   engine_v373_gather_cpp=ga, ao_engine_gather_cpp=gb)
        if not names:
            rec.update(reason='no common kernel header between the AO engine and engine_v373 (layout changed?)')
        elif differ or missing:
            rec.update(reason='the AO trace engine (%s) is out of sync with the lightmap engine: %d of %d kernel files differ (%s), %d missing -- '
                       'its fullgpu %s was not built on engine_v373\'s kernels (production fullgpu %s); rebuild it on the current headers, '
                       're-run tests\\v1_ao_engine and T2-E' % (eb, len(differ), len(names), ', '.join(d['file'] for d in differ[:4]), len(missing),
                                                                  (fb or '?')[:8], (fa or '?')[:8]))
        else:
            pf = L.resolve(aoe['proof'], self.roots) if aoe.get('proof') else None
            try:
                proof = json.load(open(pf, encoding='utf-8')) if pf else None
                if pf:
                    self._read_source(pf)
            except (OSError, ValueError) as e:
                proof, rec['proof_error'] = None, str(e)
            want = {'verdict': 'PASS', 'ao_fullgpu_md5': fb, 'production_fullgpu_md5': fa, 'production_gather_cpp_md5': ga, 'ao_gather_cpp_md5': gb}
            bad = sorted(k for k, v in want.items() if not proof or proof.get(k) != v)
            rec['proof'] = pf
            if bad:
                rec.update(reason='the AO engine\'s kernel files match engine_v373, but no neutrality proof covers these binaries (%s: %s %s) -- '
                           're-run tests\\v1_ao_engine (keyed-identical bake + deterministic AO trace) for AO fullgpu %s / production %s'
                           % (pf, 'missing' if not proof else 'mismatched', ', '.join(bad), (fb or '?')[:8], (fa or '?')[:8]))
            else:
                rec.update(enabled=True, reason=None, proof_date=proof.get('date'), proof_checks=proof.get('checks'))
                self.notes.append('AO engine in sync: %d kernel files md5-equal, 0 missing (AO fullgpu %s, production %s); neutrality proof %s'
                                  % (len(names), fb[:8], fa[:8], proof.get('date')))
        if not rec['enabled']:
            self.warnings.append('AO DISABLED in this payload: ' + rec['reason'])
        return rec

    # ------------------------------------------------------------------ light probes (V5, gate T4)
    def check_probes(self):
        """V5 (2026-10-14): the light-probe pipeline (Standard on a probe BSP, Lightprobes only) is released only by gate T4
        (tests\\t4: SH light vs the CPU super cubes on triplicate, the lightmap stage untouched by the probe worker, sh_stage's
        refusal of a non-SH byte, Lightprobes only into a scratch copy). features.probes is enabled only when the T4 proof
        (inst.probes_gate.proof, verdict PASS) names, for EVERY file of inst.probes_gate.files, the md5 that is on disk NOW (the
        production engine binaries + gpu_bake.ps1 the probe worker runs, the lightprobe drivers, sh_stage, the tag walker). Any
        change to one of them -> light probes DISABLED until tests\\t4 is re-run for it (the shell then refuses Standard on a probe
        BSP / Lightprobes only, unless --dev-probes)."""
        pg = self.spec['inst'].get('probes_gate') or {}
        base = {'enabled': False}
        if not pg.get('proof') or not pg.get('files'):
            rec = dict(base, reason='the payload spec names no T4 proof (inst.probes_gate.proof / files)')
            self.warnings.append('LIGHT PROBES DISABLED in this payload: ' + rec['reason'])
            return rec
        now, upstreamed = {}, {}
        ub = {os.path.normcase(os.path.normpath(k)): v for k, v in self.upstream_bases.items()}
        for k, ref in sorted(pg['files'].items()):
            p = L.resolve(ref, self.roots)
            if not os.path.isfile(p):
                rec = dict(base, reason='a probe-path file named by inst.probes_gate is missing: %s' % p)
                self.warnings.append('LIGHT PROBES DISABLED in this payload: ' + rec['reason'])
                return rec
            now[k] = self._read_source(p)[1]                   # re-hashed at the end like every other input
            b = ub.get(os.path.normcase(os.path.normpath(p)))
            if b:
                # U1: the gate names gpu_bake.ps1 as "the P1-P3 base" (the series pins the patched result). Production now IS the
                # upstreamed series, verified == the chain of the pinned base by stage_series, so the gate checks that base md5.
                upstreamed[k] = {'production_md5': now[k], 'base_md5': b}
                now[k] = b
        pf = L.resolve(pg['proof'], self.roots)
        try:
            proof = json.load(open(pf, encoding='utf-8')) if os.path.isfile(pf) else None
            if proof is not None:
                self._read_source(pf)
        except (OSError, ValueError) as e:
            proof = None
            base['proof_error'] = str(e)
        rec = dict(base, proof=pf, files=now)
        if upstreamed:
            rec['upstreamed'] = upstreamed
        if not proof or proof.get('verdict') != 'PASS':
            rec['reason'] = 'gate T4 has not passed for this engine (%s: %s)' % (pf, 'missing' if not proof else 'verdict %r' % proof.get('verdict'))
        else:
            pfiles = proof.get('files') or {}
            bad = sorted(k for k in now if pfiles.get(k) != now[k])
            if bad:
                rec['reason'] = ('the T4 proof (%s, %s) does not cover the probe-path files on disk now: %s changed -- re-run tests\\t4 '
                                 '(Standard super on triplicate vs the CPU super cubes) for them' % (pf, proof.get('date'), ', '.join(bad)))
            else:
                rec.update(enabled=True, reason=None, proof_date=proof.get('date'), proof_checks=proof.get('checks'))
                self.notes.append('light probes released by the T4 proof %s (%d probe-path files md5-equal%s; SH light %s%% vs the CPU super cubes)'
                                  % (proof.get('date'), len(now), (' -- %s via its verified pre-upstream base (U1)' % ', '.join(sorted(upstreamed)))
                                     if upstreamed else '', (proof.get('checks') or {}).get('sh_mean_recon_pct')))
        if not rec['enabled']:
            self.warnings.append('LIGHT PROBES DISABLED in this payload: ' + rec['reason'])
        return rec

    # ------------------------------------------------------------------ roles
    def tag_roles(self):
        slots = list(self.spec['inst'].get('dllslot', []))
        if self.with_custom and any(k.startswith('engine_custom/') for k in self.entries):
            slots += ['engine_custom/' + s[len('engine_v373/'):] for s in slots if s.startswith('engine_v373/')]
        for s in slots:
            r = self.entries.get(s)
            if r is None:
                raise Refused('dllslot %s is not staged' % s)
            if r['kind'] != 'prod':
                raise Refused('dllslot %s must be a pristine production copy (the shell patches the slots per root)' % s)
            data = open(os.path.join(self.inst, *s.split('/')), 'rb').read()
            n = data.count(L.PRISTINE_SLOT)
            if n != 2:
                raise Refused('dllslot %s holds the pristine 60-char slot path %d times, expected exactly 2 (S0.4)' % (s, n))
            r['role'] = 'dllslot'
        for dest, r in self.entries.items():
            if dest.lower().endswith('gpulm_estimator.dll') and r['role'] != 'dllslot':
                raise Refused('%s is a gpulm_estimator.dll without the dllslot role (the shell would slot-patch it anyway)' % dest)

    # ------------------------------------------------------------------ checks
    def recheck(self):
        """production re-hashed (unchanged during the build); every staged file re-read == the report; pristine == production"""
        probs = []
        if self.mutate:
            mp = os.path.join(self.roots['GLW'], *self.mutate.split('/'))
            with open(mp, 'ab') as f:
                f.write(b'\n')
            self.notes.append('SELFTEST: appended one byte to %s after the copy' % mp)
        for src, m in sorted(self.sources.items()):
            try:
                now = L.md5_file(src)
            except OSError as e:
                probs.append('%s: unreadable at the end of the build: %s' % (src, e))
                continue
            if now != m:
                probs.append('%s changed DURING the build (%s -> %s): production is moving (an engine promotion?) -- re-run when it is done' % (src, m, now))
        for dest, r in self.entries.items():
            p = os.path.join(self.inst, *dest.split('/'))
            if L.md5_file(p) != r['md5'] or os.path.getsize(p) != r['size']:
                probs.append('%s: staged bytes changed after staging' % dest)
            if r['kind'] == 'prod' and 'copyOf' not in r and r['md5'] != r['sourceMd5']:
                probs.append('%s: pristine copy %s != production %s' % (dest, r['md5'], r['sourceMd5']))
        on_disk = set()
        for dp, dns, fns in os.walk(self.inst):
            for fn in fns:
                on_disk.add(os.path.relpath(os.path.join(dp, fn), self.inst).replace('\\', '/'))
        extra = sorted(on_disk - set(self.entries))
        if extra:
            probs.append('unexpected files in the stage: %s' % ', '.join(extra[:10]))
        if probs:
            raise Refused('end-of-build verification failed:\n  ' + '\n  '.join(probs))

    def check_gpu_bake_refs(self):
        """every code reference of the patched gpu_bake.ps1 copies resolves inside INST; no $W code reference is left"""
        out = []
        ref = re.compile(r'\$(WC|BIN|TW|OSOY|PSScriptRoot|W)\\([A-Za-z0-9_.\\\-]+)')
        code_ext = ('.py', '.exe', '.dll', '.ps1', '.bin', '.json')
        work_ok = ('farm_w', '_gpulm_skel', '_TAGBAK_auto', 'gpulm.log')
        for gb in [d for d in self.entries if d.endswith('/gpu_bake.ps1')]:
            sdir = os.path.dirname(os.path.join(self.inst, *gb.split('/')))
            base = {'WC': self.inst, 'BIN': sdir, 'PSScriptRoot': sdir, 'TW': os.path.join(sdir, 'v043-tagwriter'),
                    'OSOY': os.path.join(self.inst, 'osoyoos')}
            text = open(os.path.join(self.inst, *gb.split('/')), encoding='utf-8-sig', errors='replace').read()
            missing, wcode, n = [], [], 0
            in_block = False
            for ln, line in enumerate(text.splitlines(), 1):
                code, rest = '', line                  # the code part of the line: outside <# #> blocks and before a # comment
                while rest:
                    if in_block:
                        e = rest.find('#>')
                        if e < 0:
                            rest = ''
                        else:
                            rest, in_block = rest[e + 2:], False
                    else:
                        s = rest.find('<#')
                        if s < 0:
                            code += rest
                            rest = ''
                        else:
                            code += rest[:s]
                            rest, in_block = rest[s + 2:], True
                code = code.split('#', 1)[0]
                for var, rel in ref.findall(line):
                    rel = rel.rstrip('\\.')
                    if var == 'W':
                        if rel.startswith(work_ok) or rel in ('GPU',):
                            continue
                        if ('$W\\' + rel) in code and rel.lower().endswith(code_ext):
                            wcode.append('%d: $W\\%s' % (ln, rel))
                        continue
                    n += 1
                    target = os.path.join(base[var], *rel.split('\\'))
                    if not os.path.exists(target):
                        if ('$%s\\%s' % (var, rel)) in code:
                            missing.append('%d: $%s\\%s' % (ln, var, rel))
            out.append({'script': gb, 'refs_checked': n, 'missing': missing, 'w_code_refs': wcode})
            if missing:
                raise Refused('%s: code references that are not in INST: %s' % (gb, '; '.join(missing[:10])))
            if wcode:
                raise Refused('%s: $W (WORK) references to code files P1 did not move to $WC: %s' % (gb, '; '.join(wcode[:10])))
        return out

    def check_imports(self):
        """every import of a shipped .py resolves (stdlib / numpy / a module shipped where Python will look); no scipy"""
        std = set(getattr(sys, 'stdlib_module_names', ())) | set(sys.builtin_module_names)
        ext_ok = {'numpy'}
        extra_dirs = [os.path.join(self.inst, 'v043-tagwriter'), os.path.join(self.inst, 'engine_v373', 'v043-tagwriter')]
        bad, optional, scanned = [], [], 0
        for dest in sorted(self.entries):
            if not dest.endswith('.py'):
                continue
            p = os.path.join(self.inst, *dest.split('/'))
            try:
                tree = ast.parse(open(p, 'rb').read(), filename=dest)
            except SyntaxError as e:
                raise Refused('%s does not parse: %s' % (dest, e))
            scanned += 1
            own = os.path.dirname(p)
            guarded = set()
            for node in ast.walk(tree):
                if isinstance(node, ast.Try) and any(
                        isinstance(h.type, ast.Name) and h.type.id in ('ImportError', 'ModuleNotFoundError', 'Exception')
                        or (isinstance(h.type, ast.Tuple) and any(isinstance(e, ast.Name) and e.id in ('ImportError', 'ModuleNotFoundError') for e in h.type.elts))
                        or h.type is None for h in node.handlers):
                    for b in node.body:
                        for sub in ast.walk(b):
                            guarded.add(id(sub))
            for node in ast.walk(tree):
                mods = []
                if isinstance(node, ast.Import):
                    mods = [a.name for a in node.names]
                elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                    mods = [node.module]
                for mod in mods:
                    top = mod.split('.')[0]
                    if top == 'scipy':
                        bad.append('%s:%d imports scipy (v1 ships without scipy, N4)' % (dest, node.lineno))
                        continue
                    if top in std or top in ext_ok or top == '__future__':
                        continue
                    if any(os.path.isfile(os.path.join(d, top + '.py')) or os.path.isfile(os.path.join(d, top, '__init__.py'))
                           for d in [own] + extra_dirs):
                        continue
                    (optional if id(node) in guarded else bad).append('%s:%d import %s' % (dest, node.lineno, mod))
        if bad:
            raise Refused('unresolved imports in the payload: %s' % '; '.join(bad[:20]))
        return {'scanned': scanned, 'optional_unresolved': optional}

    def pe_audit(self):
        pd = L.load_module('h2lm_pe_deps', os.path.join(L.PROJ, 'build', 's03', 'pe_deps.py'))
        machines = {0x14c: 'x86', 0x8664: 'x64'}
        rows, x64_vc = [], []
        for dest in sorted(self.entries):
            if not dest.lower().endswith(('.exe', '.dll')):
                continue
            mach, imps, dimps = pd.pe_imports(os.path.join(self.inst, *dest.split('/')))
            arch = machines.get(mach, hex(mach))
            vc = sorted(set(n.lower() for n in imps + dimps if pd._is_vc(n)))
            rows.append({'path': dest, 'machine': arch, 'imports': sorted(set(imps)), 'delay': sorted(set(dimps)), 'vc_redist': vc})
            if vc and arch == 'x64':
                x64_vc.append('%s -> %s' % (dest, ', '.join(vc)))
        if x64_vc:
            self.warnings.append('x64 binaries that import the VC++ x64 runtime (absent on a clean PC; preflight only checks the '
                                 'x86 one): %s' % '; '.join(x64_vc))
        return rows


def regenerate_engine_manifest(entries, glw, out_path):
    """ENGINE_MANIFEST.txt regenerated INTO THE BUILD DIR (never production's): the production md5 of every staged production
    / patch-base file, plus the drift of production's own (append-log) ENGINE_MANIFEST.txt against the files it names."""
    lines = ['# ENGINE_MANIFEST regenerated by %s at build time -- production md5s of what this payload staged' % TOOL,
             '# <production md5>  <production path relative to GPU Lightmap Work>  ->  <payload path> [kind role]']
    for dest in sorted(entries):
        r = entries[dest]
        src = r.get('source') or ''
        if r.get('copyOf'):
            continue
        try:
            rel = os.path.relpath(src, glw).replace('/', '\\') if src.lower().startswith(glw.lower()) else src
        except ValueError:
            rel = src
        lines.append('%s  %s  ->  %s [%s %s]' % (r.get('sourceMd5') or r['md5'], rel, dest, r['kind'], r['role']))
    drift = []
    pm = os.path.join(glw, 'engine_v373', 'ENGINE_MANIFEST.txt')
    if os.path.isfile(pm):
        last = {}
        for line in open(pm, encoding='utf-8', errors='replace'):
            m = re.match(r'^([0-9a-f]{32})\s+(\S.*\S)\s*$', line.strip())
            if m:
                last[m.group(2).replace('/', '\\').lower()] = m.group(1)
        for dest, r in entries.items():
            if not dest.startswith('engine_v373/') or r.get('copyOf'):
                continue
            key = dest[len('engine_v373/'):].replace('/', '\\').lower()
            if key in last and last[key] != r.get('sourceMd5'):
                drift.append({'file': dest, 'engine_manifest_md5': last[key], 'production_md5': r.get('sourceMd5')})
        lines.append('# production engine_v373\\ENGINE_MANIFEST.txt (append log) vs the files: %d drift(s)%s' % (
            len(drift), ''.join('\n#   %s: manifest %s, file %s' % (d['file'], d['engine_manifest_md5'], d['production_md5']) for d in drift)))
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return drift


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--out', default=os.path.join(L.BUILD, 'stage'))
    ap.add_argument('--spec', default=L.SPEC)
    ap.add_argument('--glw')
    ap.add_argument('--h2root')
    ap.add_argument('--with-custom-engine', action='store_true')
    ap.add_argument('--selftest-mutate', help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    L.below_normal()
    t0 = time.time()
    try:
        spec = L.load_spec(a.spec)
        roots = dict(spec['roots'])
        if a.glw:
            roots['GLW'] = os.path.abspath(a.glw)
        if a.h2root:
            roots['H2ROOT'] = os.path.abspath(a.h2root)
        if a.selftest_mutate:
            prod_glw = spec['roots']['GLW']
            same = os.path.normcase(os.path.realpath(roots['GLW'])) == os.path.normcase(os.path.realpath(prod_glw))
            try:
                same = same or os.path.samefile(roots['GLW'], prod_glw)
            except OSError:
                pass
            if same or not os.path.realpath(roots['GLW']).lower().startswith(L.BUILD.lower() + os.sep):
                raise Refused('--selftest-mutate is only allowed on a test copy under %s (--glw), never on production' % L.BUILD)
        out = os.path.abspath(a.out)
        if not out.lower().startswith(L.BUILD.lower() + os.sep):
            raise Refused('--out %s must be under %s' % (out, L.BUILD))
        for r in roots.values():
            if out.lower().startswith(os.path.abspath(r).lower() + os.sep):
                raise Refused('--out %s is inside a source root %s' % (out, r))
        tmp = out + '.tmp_%d' % os.getpid()
        if os.path.exists(tmp):
            raise Refused('%s exists' % tmp)
        os.makedirs(os.path.join(tmp, 'inst'))
        st = Stager(spec, roots, os.path.join(tmp, 'inst'), a.with_custom_engine, a.selftest_mutate)
        try:
            st.stage_built()        # first: the gpu_bake token is the staged osoy_launch.exe md5
            st.stage_new()
            st.stage_pinned()
            st.stage_groups()
            st.stage_series()
            st.stage_n4()
            custom = st.stage_custom()
            ao = st.check_ao_engine()
            probes = st.check_probes()
            st.tag_roles()
            refs = st.check_gpu_bake_refs()
            imps = st.check_imports()
            pe = st.pe_audit()
            st.recheck()
        except Refused:
            print('stage_engine: the partial stage is left in %s (not published)' % tmp, file=sys.stderr)
            raise
        files = [st.entries[k] for k in sorted(st.entries)]
        counts = {}
        for r in files:
            counts[r['kind']] = counts.get(r['kind'], 0) + 1
        drift = regenerate_engine_manifest(st.entries, roots['GLW'], os.path.join(tmp, 'ENGINE_MANIFEST.txt'))
        if drift:
            st.notes.append('production ENGINE_MANIFEST.txt is stale for %d file(s) (informational; the payload records the real md5s): %s'
                            % (len(drift), ', '.join(d['file'] for d in drift)))
        rep = {
            'format': 'h2lm-stage-report/1', 'tool': TOOL, 'spec': os.path.abspath(a.spec), 'spec_md5': L.md5_file(a.spec),
            'roots': roots, 'built': time.strftime('%Y-%m-%dT%H:%M:%S'), 'seconds': round(time.time() - t0, 1),
            'inst_dir': os.path.join(out, 'inst'), 'counts': counts, 'file_count': len(files),
            'total_bytes': sum(r['size'] for r in files), 'content_hash': L.content_hash(files),
            'engine': {k: st.entries[k]['md5'] for k in ('engine_v373/gpu_bake.ps1', 'engine_v373/v041-fullgpu/fullgpu.exe',
                                                          '_research/ao/dev_AO/gpu_bake.ps1', '_research/ao/dev_AO/v041-fullgpu/fullgpu.exe',
                                                          'osoyoos/osoy_launch.exe') if k in st.entries},
            'custom_engine': custom, 'ao_engine': ao, 'probes_gate': probes,
            'features': {'ao': {'enabled': ao['enabled'], 'reason': ao['reason']},
                         'probes': {'enabled': probes['enabled'], 'reason': probes['reason']}},
            'gpu_bake_refs': refs, 'imports': imps, 'pe': pe,
            'engine_manifest_drift': drift, 'notes': st.notes, 'warnings': st.warnings,
            'sources': {k: st.sources[k] for k in sorted(st.sources)},     # every input read (md5 at read time == at the end)
            'files': files,
        }
        L.write_json(os.path.join(tmp, 'stage_report.json'), rep)
        if os.path.exists(out):
            L.safe_remove_tree(out, L.BUILD)
        os.replace(tmp, out)
        print('STAGE OK: %d files, %.1f MB, content %s (%s) -> %s  [%.1f s]' % (
            len(files), rep['total_bytes'] / 1e6, rep['content_hash'][:8],
            ', '.join('%s %d' % kv for kv in sorted(counts.items())), out, time.time() - t0))
        for k, v in rep['engine'].items():
            print('  %-48s %s' % (k, v))
        for n in st.notes:
            print('  NOTE: ' + n)
        for w in st.warnings:
            print('  WARNING: ' + w)
        return 0
    except Refused as e:
        print('stage_engine: REFUSED: %s' % e, file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
