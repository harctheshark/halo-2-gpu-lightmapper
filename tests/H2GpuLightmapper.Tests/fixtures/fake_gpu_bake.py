"""S2.5/S3 test stand-in for the engine's bake program (TEST payload only; a sandbox H2EK). The fake h2lm-bake.exe next to this file runs it as
`py -I -B fake_gpu_bake.py <the bake arguments>` and pumps its output. It takes the same parameter set the shell passes (`-Name value`, switches,
case-insensitive names; `--list-params` prints the full set as "Name<TAB>type"; the parameters THIS engine takes are the ones listed in the
bake_params.txt beside this file -- the test payload leaves names out to make an older engine, which then refuses the parameter like the real
program), refuses to run without the H2LM_* contract, prints the banners the shell parses, and writes the farm files the shell reads:
capture_id.json (soup crc), gpulm.log, the probe-ready sky files under GPULM_SKY_HI=1, OUT\\skeleton, OUT\\tagw_stage (+ a 'written'
tagw_report.json). With -WriteBack -WriteBackMode direct it installs with the REAL lmw_install.py (INST copy) into the SANDBOX tags, exactly like
production phase 4b. Never a GPU, never the tool. A thrown error is the error block on stderr (the last "|" line = the message), exit 1.
Behaviour knobs: $W\\fake_control.txt lines "gpu_bake=ok|fail_bake|fail_tdr|nostage|slow3|slow4b|nostage_once|nostage_once_slow4b|nodirect|wipetemp|quarantine_mid|nofp64"
and "gpu_bake_wait". Every call is appended to $W\\fake_calls.log as one JSON line (argv = the arguments as received).
    nostage            [4a] makes no stage (with -WriteBack: the tool write-back [4], as production falls back)
    nostage_once       U3: no stage in a stage-only bake; with -WriteBack a stage + [4b] (the fallback's own direct writer works)
    nostage_once_slow4b  the same, the [4b] takes 5 s (a cancel during it is deferred)
    nodirect           U3: the pre-flight "-WriteBackMode direct cannot run": stage-only THROWS before any phase; with -WriteBack it notes it,
                       switches to tool mode and the tool write-back [4] writes the set
    wipetemp           0.6.8: after the stage-only [4a] stage, delete every file under H2LM_FARMS that can be deleted (the user clearing
                       "Temp (safe to delete)" mid-bake: a held .lock stays), then exit 0 as if nothing happened
    gpu_bake_wait      U3: after a stage-only [4a] stage, write $W\\fake_gpu_bake_waiting.flag and wait (<= 120 s) for
                       $W\\fake_gpu_bake_go.flag (the test holds a file before the shell's install)
Engine variants come from bake_params.txt: a name left out = a parameter the engine does not take; no "GPULM_COMPAT<TAB>env" line = the 0.6.3 shape
(the variable is not validated and no "Compatibility mode" line is printed). Stdlib only."""
import hashlib
import json
import ntpath
import os
import random
import re
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)
    sys.stderr.reconfigure(encoding='utf-8', line_buffering=True)
except Exception:
    pass

SCRIPT = os.path.abspath(__file__)
HERE = os.path.dirname(SCRIPT)
# (name, type, default); the order is the list-params order
PARAMS = [
    ('Scenario', 'string', ''), ('Bsp', 'string', ''), ('Quality', 'string', 'super'), ('Worker', 'int', 6), ('WriteBack', 'switch', False),
    ('WriteBackMode', 'string', 'tool'), ('SkipCapture', 'switch', False), ('PluckerFix', 'switch', False), ('SceneryFix', 'switch', False),
    ('WriterOnly', 'switch', False), ('ToolOverlay', 'switch', False), ('Responsive', 'switch', False), ('ExactVertexLighting', 'switch', False),
    ('CaptureOnly', 'switch', False), ('SceneryOwnPermutationOnly', 'switch', False), ('Photons', 'int', 0), ('MCSamples', 'int', 0),
]
SETS = {'Quality': ('low', 'super', 'custom'), 'WriteBackMode': ('tool', 'direct', 'both')}
RANGES = {'Photons': (1000000, 100000000), 'MCSamples': (1, 64)}
ENV_PARAMS = ['GPULM_COMPAT']


def out(s=''):
    sys.stdout.write(s + '\n')
    sys.stdout.flush()


def die(msg, line=1, text='throw "..."'):
    """A thrown error: PowerShell's error block on stderr (the shell takes the last '|' line that is not an underline as the reason)."""
    sys.stdout.flush()
    sys.stderr.write('Exception: %s:%d\nLine |\n%5d |  %s\n     |  %s\n     | %s\n' % (SCRIPT, line, line, text, '~' * len(text), msg))
    sys.stderr.flush()
    sys.exit(1)


def my_params():
    """name -> type of the parameters (and env names) this engine takes: bake_params.txt beside the script (absent = the full set)."""
    p = os.path.join(HERE, 'bake_params.txt')
    if not os.path.isfile(p):
        return {n.lower(): t for n, t, _ in PARAMS}, set(ENV_PARAMS)
    names, envs = {}, set()
    with open(p, encoding='utf-8') as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith('#'):
                continue
            parts = line.split()
            if len(parts) != 2:
                continue
            if parts[1].lower() == 'env':
                envs.add(parts[0].upper())
            else:
                names.setdefault(parts[0].lower(), parts[1].lower())
    return names, envs


def bind(argv, takes):
    spec = {n.lower(): (n, t, d) for n, t, d in PARAMS if n.lower() in takes}
    vals = {n: d for n, t, d in PARAMS}
    bound = {}
    i = 0
    while i < len(argv):
        a = argv[i]
        if not (a.startswith('-') and len(a) > 1):
            die("A positional parameter cannot be found that accepts argument '%s'." % a)
        s = spec.get(a.lstrip('-').lower())
        if s is None:
            die("A parameter cannot be found that matches parameter name '%s'." % a.lstrip('-'))
        name, typ, _ = s
        if typ == 'switch':
            vals[name] = True
            bound[name] = 'True'
            i += 1
            continue
        if i + 1 >= len(argv):
            die("Missing an argument for parameter '%s'. Specify a parameter of type 'System.%s' and try again." % (name, 'Int32' if typ == 'int' else 'String'))
        v = argv[i + 1]
        i += 2
        if typ == 'int':
            try:
                v = int(v)
            except ValueError:
                die("Cannot process argument transformation on parameter '%s'. Cannot convert value \"%s\" to type \"System.Int32\"." % (name, v))
        if name in SETS and v.lower() not in SETS[name]:
            die("Cannot validate argument on parameter '%s'. The argument \"%s\" does not belong to the set \"%s\" specified by the ValidateSet attribute. Supply an argument that is in the set and then try the command again." % (name, v, ','.join(SETS[name])))
        if name in RANGES and not (RANGES[name][0] <= v <= RANGES[name][1]):
            die("Cannot validate argument on parameter '%s'. The %d argument is out of the allowed range %d..%d. Supply an argument in that range and then try the command again." % (name, v, RANGES[name][0], RANGES[name][1]))
        vals[name] = v
        bound[name] = str(v)
    return vals, bound


def find_on_path(name):
    for d in os.environ.get('PATH', '').split(';'):
        d = d.strip().strip('"')
        if not d:
            continue
        p = os.path.join(d, name + '.exe')
        if os.path.isfile(p):
            return p
    return None


def phase(n, t):
    out()
    out('[%s] %s' % (n, t))


def note(m):
    out('      *** ' + m)


def set_content(path, text):
    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write(text + '\r\n')


def write_bytes(path, data):
    with open(path, 'wb') as f:
        f.write(data)


def md5_of(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        h.update(f.read())
    return h.hexdigest()


def main(argv):
    if len(argv) == 1 and argv[0].lower() == '--list-params':
        for n, t, _ in PARAMS:
            out('%s\t%s' % (n, t))
        for e in ENV_PARAMS:
            out('%s\tenv' % e)
        return 0
    takes, envs = my_params()
    v, bound = bind(argv, takes)
    compat_engine = 'GPULM_COMPAT' in envs
    Scenario, Bsp, Quality, Worker, WriteBack, WBMode = v['Scenario'], v['Bsp'], v['Quality'], v['Worker'], v['WriteBack'], v['WriteBackMode']
    q = Quality.lower()
    wbm = WBMode.lower()
    Photons, MCSamples = v['Photons'], v['MCSamples']
    # engine 0.6.4 Compatibility mode (DF64): the engine validates GPULM_COMPAT (0 / 1 / unset)
    if compat_engine and 'GPULM_COMPAT' in os.environ:
        c = os.environ['GPULM_COMPAT']
        if c not in ('', '0', '1'):
            die("GPULM_COMPAT='%s' -- expected 0, 1 or unset" % c, 30, 'throw "GPULM_COMPAT=..."')
        if c == '1':
            note('GPULM_COMPAT=1: the FP32-only double-single (df64) kernel set is FORCED')
    for name in ('H2LM_ROOT', 'H2LM_WORK', 'H2LM_CODE'):
        x = os.environ.get(name)
        if not x or not os.path.isdir(x):
            die("H2LM: %s is not an existing folder ('%s')" % (name, x or ''), 33, 'throw "H2LM: ..."')
    if os.environ.get('H2LM_NOWINDOW') != '1':
        die("H2LM: H2LM_NOWINDOW is '%s', the shell always sets 1" % os.environ.get('H2LM_NOWINDOW', ''), 35, 'throw "H2LM: ..."')
    ROOT, W, WC = os.environ['H2LM_ROOT'], os.environ['H2LM_WORK'], os.environ['H2LM_CODE']
    if os.environ.get('H2LM_FARMS'):
        if not os.path.isdir(os.environ['H2LM_FARMS']):
            die("H2LM: H2LM_FARMS='%s' must be an existing folder" % os.environ['H2LM_FARMS'], 37, 'throw "H2LM: ..."')
        FARMS = os.environ['H2LM_FARMS']
    else:
        FARMS = W
    OUT = os.path.join(FARMS, 'farm_w%02d' % Worker)
    TW = os.path.join(HERE, 'v043-tagwriter')
    scen_rel = ntpath.dirname(Scenario)
    scen_leaf = ntpath.basename(Scenario)
    tag_dir_live = os.path.join(ROOT, 'tags', scen_rel)
    ltmp_name = '%s_%s_lightmap' % (scen_leaf, Bsp)
    if q == 'custom':
        if not Photons or not MCSamples:
            die('-Quality custom needs -Photons and -MCSamples', 41, 'throw "-Quality custom needs ..."')
    elif Photons or MCSamples:
        die('-Photons / -MCSamples are only valid with -Quality custom', 42, 'throw "-Photons / -MCSamples ..."')

    mode = 'ok'
    wait_go = False
    ctl = os.path.join(W, 'fake_control.txt')
    if os.path.isfile(ctl):
        with open(ctl, encoding='utf-8-sig') as f:
            for l in f.read().splitlines():
                m = re.match(r'^gpu_bake=(\S+)', l, re.I)
                if m:
                    mode = m.group(1)
                if re.match(r'^gpu_bake_wait\s*$', l, re.I):
                    wait_go = True
    gp = ['%s=%s' % (k, os.environ[k]) for k in sorted(os.environ, key=str.lower) if k.upper().startswith('GPULM_')]
    dv = [k for k in sorted(os.environ, key=str.lower) if k.upper().startswith('DEVP_') or k.upper().startswith('V370_')]   # review fix 19: must be empty
    rec = dict(tool='gpu_bake', script=SCRIPT, argv=list(argv), bound=['%s=%s' % (k, bound[k]) for k in sorted(bound, key=str.lower)],
               cwd=os.getcwd(), h2lm_root=ROOT, h2lm_work=W, h2lm_code=WC, gpulm=gp, devp=dv, pythonpath=os.environ.get('PYTHONPATH'), mode=mode,
               secret_inherited=bool(os.environ.get('H2LMTEST_API_TOKEN')), py=find_on_path('py'))
    with open(os.path.join(W, 'fake_calls.log'), 'a', encoding='utf-8', newline='') as f:
        f.write(json.dumps(rec, separators=(',', ':')) + '\r\n')

    out('fake gpu_bake: %s / %s  worker farm_w%02d  quality %s  fixes %s%s' % (Scenario, Bsp, Worker, Quality, 'True' if v['PluckerFix'] else 'False', 'True' if v['SceneryFix'] else 'False'))
    # U3: the production pre-flight (before phase 1): with -WriteBack it falls back to tool mode, stage-only it throws
    if mode == 'nodirect' and wbm != 'tool':
        m = '-WriteBackMode %s cannot run: fake: lm_tagwriter.py is missing' % WBMode
        if WriteBack:
            note('%s -- using -WriteBackMode tool' % m)
            wbm = 'tool'
        else:
            die(m, 60, 'throw $m')
    if not v['SkipCapture']:
        phase(1, 'CAPTURE - fake capture  [sever=3]')
        os.makedirs(OUT, exist_ok=True)
        crc = '%08X' % random.randint(0, 2147483646)
        set_content(os.path.join(OUT, 'capture_id.json'), '{"soup_crc32": "%s", "stamped_by": "fake"}' % crc)
        budget = Photons if q == 'custom' else (20000000 if q == 'super' else 5000000)
        set_content(os.path.join(OUT, 'gpulm.log'), 'g_photon_budget=%d' % budget)
        if os.environ.get('GPULM_SKY_HI') == '1':
            set_content(os.path.join(OUT, 'sky_tex_hi.bin'), 'sky hi')
            set_content(os.path.join(OUT, 'lp_globals.bin'), 'lp globals')
        note('fake capture %s' % crc)
    else:
        if not os.path.isfile(os.path.join(OUT, 'capture_id.json')):
            die('PHASE 1: -SkipCapture but %s has no capture' % OUT, 75, 'throw "PHASE 1: -SkipCapture ..."')
        note('reusing the capture in %s (-SkipCapture)' % OUT)
    if v['CaptureOnly']:
        out()
        out('CAPTURE ONLY - stopped after phase 1.  0.1s   sever=3')
        return 0
    phase(2, 'DERIVE - fake derive')
    phase(3, 'GPU BAKE - fake bake')
    if compat_engine:
        if os.environ.get('GPULM_COMPAT') == '1' or mode == 'nofp64':
            out('      Compatibility mode (no FP64): fake -- the FP32-only double-single kernels')
        else:
            out('      Compatibility mode: off (native FP64 kernels; fake)')
    if os.environ.get('GPULM_PROBES'):
        out('      PROBE: traced %s' % os.environ['GPULM_PROBES'])
        set_content(os.path.join(OUT, 'vertex_out.bin'), 'v')
    if mode == 'fail_bake':
        die('PHASE 3: the fake bake failed on purpose', 90, 'throw "PHASE 3: the fake bake failed on purpose"')
    if mode == 'quarantine_mid':      # 0.6.8 RC: an antivirus removes INST's fullgpu.exe while the bake runs (the bake then fails on it)
        qf = os.path.normpath(os.path.join(WC, 'engine_v373/v041-fullgpu/fullgpu.exe'))
        os.remove(qf)
        die('PHASE 3: MISSING %s' % qf, 92, 'throw "PHASE 3: MISSING $qf"')
    if mode == 'fail_tdr':      # X2: the engine's device-loss lines, then the throw (stdout like the real DIRECT: lines)
        out('DIRECT: *** GPU CAST DEVICE REMOVED (DXGI_ERROR_DEVICE_HUNG) -- the GPU device was RESET by Windows (TDR); nothing was written')
        die('PHASE 3: fullgpu exited 3 after the device was lost', 95, 'throw "PHASE 3: fullgpu exited 3 ..."')
    if mode == 'slow3':
        time.sleep(60)
        out('SHOULD NOT PRINT (slow3)')

    if wbm != 'tool':
        phase('4a', 'TAG WRITER - fake -> %s\\tagw_stage%s' % (OUT, ' -> install' if WriteBack else ' (stage only; nothing is installed)'))
        no_stage = mode == 'nostage' or (mode in ('nostage_once', 'nostage_once_slow4b') and not WriteBack)
        if no_stage:
            note('tag writer FAILED: fake skeleton refused')
            if WriteBack:
                note('direct tag writer FAILED -- falling back to the tool write-back: fake')
        else:
            stage = os.path.join(OUT, 'tagw_stage')
            os.makedirs(stage, exist_ok=True)
            os.makedirs(os.path.join(OUT, 'skeleton'), exist_ok=True)
            set_content(os.path.join(OUT, 'skeleton', 'skel.txt'), 'fake skeleton')
            outs = {}
            for n in ('%s.scenario_structure_lightmap' % ltmp_name, '%s_bitmaps.bitmap' % ltmp_name, '%s_truecolor_bitmaps.bitmap' % ltmp_name):
                live = os.path.join(tag_dir_live, n)
                if os.path.isfile(live):
                    with open(live, 'rb') as f:
                        data = f.read()
                else:
                    data = ('FAKE %s' % n).encode('ascii')
                p = os.path.join(stage, n)
                write_bytes(p, data + ('|BAKED %s %d' % (Quality, Worker)).encode('ascii'))
                outs[n] = dict(path=p, md5=md5_of(p), size=os.path.getsize(p))
            set_content(os.path.join(stage, 'tagw_report.json'), json.dumps(dict(writer='fake', status='written', outputs=outs), indent=2))
            out('      stage written + validated: %s' % stage)
            if mode == 'wipetemp' and os.environ.get('H2LM_FARMS'):
                gone = 0
                for d in os.scandir(os.environ['H2LM_FARMS']):
                    if not d.is_dir(follow_symlinks=False) or (getattr(d.stat(follow_symlinks=False), 'st_file_attributes', 0) & 0x400):
                        continue
                    for f in os.scandir(d.path):
                        if f.is_file(follow_symlinks=False):
                            try:
                                os.remove(f.path)
                                gone += 1
                            except OSError:
                                pass
                out('      (fake) the temporary folder was cleared: %d file(s) deleted' % gone)
            if wait_go and not WriteBack:
                set_content(os.path.join(W, 'fake_gpu_bake_waiting.flag'), 'waiting')
                t0 = time.time()
                while not os.path.exists(os.path.join(W, 'fake_gpu_bake_go.flag')) and time.time() - t0 < 120:
                    time.sleep(0.1)
                out('      (fake) go after %d ms' % int((time.time() - t0) * 1000))
            if WriteBack and wbm == 'direct':
                phase('4b', 'INSTALL - back up the live set, marker, atomic per-file replace, verify')
                if mode in ('slow4b', 'nostage_once_slow4b'):
                    out('      INSTALL: (fake) slow')
                    time.sleep(5)
                py = find_on_path('py') or sys.executable
                p = subprocess.Popen([py, os.path.join(TW, 'lmw_install.py'), 'install', '--stage', stage, '--target-dir', tag_dir_live,
                                      '--backup-dir', os.path.join(OUT, 'tag_backup_before_writeback'), '--auto-backup-root', os.path.join(W, '_TAGBAK_auto'),
                                      '--marker', os.path.join(OUT, 'writeback_in_progress.txt')], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
                for raw in p.stdout:
                    out('      ' + raw.decode('utf-8', 'replace').rstrip('\r\n'))
                ix = p.wait()
                if ix == 2:
                    die('PHASE 4b: lmw_install.py REFUSED before changing anything', 110, 'throw "PHASE 4b: lmw_install.py REFUSED ..."')
                if ix != 0:
                    die('PHASE 4b: lmw_install.py FAILED AFTER A REPLACE (exit %d)' % ix, 111, 'throw "PHASE 4b: lmw_install.py FAILED ..."')
                out('      direct write-back installed + verified')
    if WriteBack and mode in ('nostage', 'nodirect'):
        phase(4, 'WRITE-BACK - fake tool write-back')
        # production: Start-OsoyTool "writeback" adds --quality-row P MC for -Quality custom (P2); the fake records what it got
        row = ' quality-row %d %d' % (Photons, MCSamples) if q == 'custom' else ''
        for n in ('%s.scenario_structure_lightmap' % ltmp_name, '%s_bitmaps.bitmap' % ltmp_name):
            write_bytes(os.path.join(tag_dir_live, n), ('TOOL WRITE-BACK %s %s%s' % (n, Quality, row)).encode('ascii'))
    phase('4c', 'VERIFY - fake')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
