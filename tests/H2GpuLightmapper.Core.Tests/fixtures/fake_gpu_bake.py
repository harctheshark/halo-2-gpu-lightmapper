"""S2.3 test stand-in for the engine's bake program (shipped in the TEST payload only, run by the fake h2lm-bake.exe next to it:
`py -I -B fake_gpu_bake.py <the bake arguments>`). It prints what the real program prints in the shapes the shell parses: phase banners
"[n] title" (preceded by an empty line), "      *** note", "GUARD: *** FAIL", "INSTALL:" lines, and a thrown error (an error block on stderr
whose last "|" line carries the message). It never touches tags, the GPU or the tool. Stdlib only.
Parameters (-Name value, case-insensitive; `--list-params` prints them as "Name<TAB>type"): Scenario, Bsp, Worker, Mode, PidFile."""
import os
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)
    sys.stderr.reconfigure(encoding='utf-8', line_buffering=True)
except Exception:
    pass

SCRIPT = os.path.abspath(__file__)
MODES = ('ok', 'fail', 'exit7', 'env', 'cancel3', 'defer4b', 'tree')
# name -> (type, default)
PARAMS = [('Scenario', 'string', ''), ('Bsp', 'string', ''), ('Worker', 'int', 200), ('Mode', 'string', 'ok'), ('PidFile', 'string', '')]


def out(s=''):
    sys.stdout.write(s + '\n')
    sys.stdout.flush()


def die(msg, line, text='throw "..."'):
    """A thrown error: PowerShell's error block on stderr (the shell takes the last '|' line that is not an underline as the reason)."""
    sys.stdout.flush()
    sys.stderr.write('Exception: %s:%d\nLine |\n%5d |  %s\n     |  %s\n     | %s\n' % (SCRIPT, line, line, text, '~' * len(text), msg))
    sys.stderr.flush()
    sys.exit(1)


def bind(argv):
    spec = {n.lower(): (n, t, d) for n, t, d in PARAMS}
    vals = {n: d for n, t, d in PARAMS}
    i = 0
    while i < len(argv):
        a = argv[i]
        if not (a.startswith('-') and len(a) > 1):
            die("A positional parameter cannot be found that accepts argument '%s'." % a, 20, 'param(...)')
        s = spec.get(a.lstrip('-').lower())
        if s is None:
            die("A parameter cannot be found that matches parameter name '%s'." % a.lstrip('-'), 20, 'param(...)')
        name, typ, _ = s
        if i + 1 >= len(argv):
            die("Missing an argument for parameter '%s'. Specify a parameter of type 'System.%s' and try again." % (name, 'Int32' if typ == 'int' else 'String'), 20, 'param(...)')
        v = argv[i + 1]
        i += 2
        if typ == 'int':
            try:
                v = int(v)
            except ValueError:
                die("Cannot process argument transformation on parameter '%s'. Cannot convert value \"%s\" to type \"System.Int32\"." % (name, v), 20, 'param(...)')
        elif name == 'Mode':
            hit = [m for m in MODES if m.lower() == v.lower()]
            if not hit:
                die("Cannot validate argument on parameter 'Mode'. The argument \"%s\" does not belong to the set \"%s\" specified by the ValidateSet attribute. Supply an argument that is in the set and then try the command again." % (v, ','.join(MODES)), 20, 'param(...)')
            v = hit[0]
        vals[name] = v
    return vals


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


def main(argv):
    if len(argv) == 1 and argv[0].lower() == '--list-params':
        for n, t, _ in PARAMS:
            out('%s\t%s' % (n, t))
        return 0
    p = bind(argv)
    mode, worker = p['Mode'], p['Worker']
    if mode == 'ok':
        phase(1, 'CAPTURE - fake capture  [sever=3]')
        note('fake note one')
        phase(2, 'DERIVE - fake derive')
        phase(3, 'GPU BAKE - fake bake')
        phase('4a', 'TAG WRITER - fake stage (stage only; nothing is installed)')
        out('      worker farm_w%02d scenario %s bsp %s' % (worker, p['Scenario'], p['Bsp']))
        return 0
    if mode == 'fail':
        phase(1, 'CAPTURE - fake capture')
        out('      GUARD: *** FAIL fake guard message')
        die('PHASE 1: the fake capture failed on purpose', 30, 'throw "PHASE 1: the fake capture failed on purpose"')
    if mode == 'exit7':
        phase(1, 'CAPTURE - fake')
        return 7
    if mode == 'env':
        phase(1, 'ENV - report')
        for n in ('H2LM_ROOT', 'H2LM_WORK', 'H2LM_CODE', 'H2LM_NOWINDOW', 'PYTHONIOENCODING', 'PYTHONDONTWRITEBYTECODE',
                  'PSModulePath', 'PSExecutionPolicyPreference', 'NO_COLOR', 'PYTHONPATH', 'PYTHONHOME'):
            out('ENV %s=%s' % (n, os.environ.get(n, '')))
        g = [k for k in os.environ if k.upper().startswith('GPULM_') or k.upper().startswith('OSOYOOS_')]
        out('ENV pipeline-vars-count=%d' % len(g))
        out('ENV PATH=%s' % os.environ.get('PATH', ''))
        out('ENV py=%s' % (find_on_path('py') or ''))
        import numpy
        out('ENV pyver=%s %s %s' % (sys.executable, numpy.__version__, sys.flags.isolated))
        out('ENV cwd=%s' % os.getcwd())
        return 0
    if mode == 'cancel3':
        phase(1, 'CAPTURE - fake')
        phase(3, 'GPU BAKE - fake long bake')
        time.sleep(60)
        out('SHOULD NOT PRINT (cancel3)')
        return 0
    if mode == 'defer4b':
        phase(3, 'GPU BAKE - fake')
        phase('4b', 'INSTALL - back up the live set, marker, atomic per-file replace, verify')
        out('      INSTALL: writing (fake)')
        time.sleep(4)
        out('      INSTALL: installed and verified 3 file(s) (fake)')
        phase('4c', 'A/B - fake')
        time.sleep(60)
        out('SHOULD NOT PRINT (defer4b)')
        return 0
    if mode == 'tree':
        phase(3, 'GPU BAKE - a fake process tree')
        py = find_on_path('py') or sys.executable
        gc = subprocess.Popen([py, '-c', 'import time; time.sleep(120)'], creationflags=0x08000000)
        if p['PidFile']:
            with open(p['PidFile'], 'w') as f:
                f.write(str(gc.pid))
        out('      started grandchild %d' % gc.pid)
        subprocess.call([py, '-c', "import time; print('blocking child up', flush=True); time.sleep(120)"])
        out('SHOULD NOT PRINT (tree)')
        return 0
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
