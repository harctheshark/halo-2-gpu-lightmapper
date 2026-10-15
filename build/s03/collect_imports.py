# S0.3: static (AST) import-chain inventory of every Python script the standalone will run under the bundle. Read-only.
# Walks every Import / ImportFrom node (top level, in functions, in try blocks), follows LOCAL imports (a .py that exists in
# one of the pipeline dirs) recursively, and writes import_chain.json:
#   scripts[]   : path, group, md5, imports (module, names, lineno, local|external), sys_path_edits, dynamic_imports,
#                 relies_on_path0 (imports an own-dir sibling without editing sys.path itself), import_safe (top level holds
#                 only imports/defs/classes/simple assignments/__main__ guard -> importing it executes no pipeline work)
#   external    : {module: [names imported with "from module import ..."]}   -- what the bundle must provide
#   local       : {module: [dirs it resolves to]}
# usage: python -I collect_imports.py <GPU Lightmap Work dir> <out.json>
import ast, os, sys, json, hashlib

W = sys.argv[1]
OUT = sys.argv[2]
E = os.path.join(W, 'engine_v373')
AO = os.path.join(W, '_research', 'ao')
LP = os.path.join(W, '_research', 'lightprobes')
TW = ('lm_tagwriter', 'lmw_atlas', 'lmw_charts', 'lmw_grid', 'lmw_install', 'lmw_scenery', 'lmw_skeleton', 'lmw_tagio',
      'lmw_validate', 'lmw_vertex')
ENG = ('pipeline_guard', 'derive_owner_table', 'build_objgrids', 'build_seam_lists_fast', 'bp_build', 'merge_parts_seg')
ROOTS = [
    ('tagwriter (engine_v373)', os.path.join(E, 'v043-tagwriter'), TW),
    ('engine_v373', E, ENG),
    ('$W helpers', W, ('sky_gate', 'build_surf_gpu', 'build_sky_gpu', 'cull_marker_check', 'build_vertdir',
                       'promote_owner_table', 'build_seam_lists')),
    ('$W\\v043-tagwriter (lightprobes lmw_tagio)', os.path.join(W, 'v043-tagwriter'), ('lmw_tagio',)),
    ('AO driver', AO, ('gpu_ao', 'ao_seamfix')),
    ('AO dev_AO engine', os.path.join(AO, 'dev_AO'), ENG + ('ao_merge_parts',)),
    ('AO dev_AO tagwriter', os.path.join(AO, 'dev_AO', 'v043-tagwriter'), TW),
    ('lightprobes', LP, ('gpu_lightprobes', 'lp_probes', 'lp_globals', 'lp_compose', 'lp_nearpoint', 'lp_sh', 'lp_write_sh')),
]
# where a local import can resolve (own dir first, then these, in order)
LOCAL_DIRS = [os.path.join(E, 'v043-tagwriter'), E, W, os.path.join(W, 'v043-tagwriter'), AO, os.path.join(AO, 'dev_AO'),
              os.path.join(AO, 'dev_AO', 'v043-tagwriter'), LP]
# gpu_bake.ps1 inline `py -c` snippets (lines 591, 698, 1037 of engine_v373 eb8e6bf2)
INLINE = {'json': [], 'os': [], 'struct': [], 'numpy': []}


def md5(p):
    return hashlib.md5(open(p, 'rb').read()).hexdigest()


def resolve_local(mod, own):
    top = mod.split('.')[0]
    hits = []
    for d in [own] + LOCAL_DIRS:
        if os.path.isfile(os.path.join(d, top + '.py')) or os.path.isfile(os.path.join(d, top, '__init__.py')):
            if d not in hits:
                hits.append(d)
    return hits


SAFE_TOP = (ast.Import, ast.ImportFrom, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef, ast.Assign, ast.AnnAssign,
            ast.AugAssign, ast.Pass)


def top_level_unsafe(tree):
    """top-level statements that could do work on import (besides imports/defs/assignments/__main__ guard/docstring/
    sys.path edits / try-imports / priority calls)."""
    bad = []
    for n in tree.body:
        if isinstance(n, SAFE_TOP):
            continue
        if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant):
            continue  # docstring
        if isinstance(n, ast.If):
            t = n.test
            if (isinstance(t, ast.Compare) and isinstance(t.left, ast.Name) and t.left.id == '__name__'):
                continue
        if isinstance(n, ast.Try) and all(isinstance(b, (ast.Import, ast.ImportFrom, ast.Assign, ast.Expr, ast.Pass))
                                          for b in n.body):
            continue  # try: import x / priority call  except: ...
        if isinstance(n, ast.Expr) and isinstance(n.value, ast.Call):
            src = ast.unparse(n.value)
            if src.startswith(('sys.path.insert', 'sys.path.append', 'warnings.filterwarnings', 'np.seterr')):
                continue
        bad.append('%d: %s' % (n.lineno, ast.unparse(n)[:120].replace('\n', ' ')))
    return bad


def scan(path, group):
    src = open(path, 'rb').read()
    tree = ast.parse(src, path)
    own = os.path.dirname(path)
    imps, edits, dyn = [], [], []
    for n in ast.walk(tree):
        if isinstance(n, ast.Import):
            for a in n.names:
                imps.append(dict(module=a.name, names=[], lineno=n.lineno))
        elif isinstance(n, ast.ImportFrom):
            if n.level:
                imps.append(dict(module='.' * n.level + (n.module or ''), names=[a.name for a in n.names], lineno=n.lineno))
            else:
                imps.append(dict(module=n.module, names=[a.name for a in n.names], lineno=n.lineno))
        elif isinstance(n, ast.Call):
            s = ast.unparse(n.func)
            if s in ('sys.path.insert', 'sys.path.append', 'sys.path.extend'):
                edits.append('%d: %s' % (n.lineno, ast.unparse(n)[:140]))
            elif s in ('importlib.import_module', '__import__', 'importlib.util.spec_from_file_location', 'runpy.run_path',
                       'runpy.run_module', 'exec', 'ctypes.CDLL', 'ctypes.WinDLL', 'ctypes.cdll.LoadLibrary',
                       'ctypes.windll.LoadLibrary') or s.endswith(('.LoadLibrary', 'CDLL', 'WinDLL')):
                dyn.append('%d: %s' % (n.lineno, ast.unparse(n)[:140]))
    own_sib = False
    for i in imps:
        hits = resolve_local(i['module'], own)
        i['local'] = hits
        if hits and hits[0] == own:
            own_sib = True
    return dict(path=path, group=group, md5=md5(path), imports=imps, sys_path_edits=edits, dynamic=dyn,
                relies_on_path0=bool(own_sib and not edits), import_safe_violations=top_level_unsafe(tree))


queue = []
for g, d, mods in ROOTS:
    for m in mods:
        p = os.path.join(d, m + '.py')
        if os.path.isfile(p):
            queue.append((p, g))
        else:
            print('MISSING script: %s' % p)
seen, scripts = set(), []
while queue:
    p, g = queue.pop(0)
    k = os.path.normcase(os.path.abspath(p))
    if k in seen:
        continue
    seen.add(k)
    r = scan(p, g)
    scripts.append(r)
    for i in r['imports']:
        for d in i['local'][:1]:   # follow the copy the script would really load (own dir first)
            top = i['module'].split('.')[0]
            q = os.path.join(d, top + '.py')
            if os.path.isfile(q):
                queue.append((q, g + ' (followed)'))

external, local = {}, {}
for s in scripts:
    for i in s['imports']:
        if i['local']:
            local.setdefault(i['module'], [])
            for d in i['local']:
                if d not in local[i['module']]:
                    local[i['module']].append(d)
        else:
            external.setdefault(i['module'], [])
            for n in i['names']:
                if n not in external[i['module']]:
                    external[i['module']].append(n)
for m, ns in INLINE.items():
    external.setdefault(m, [])
res = dict(work_dir=W, scripts=scripts, external=dict(sorted(external.items())), local=dict(sorted(local.items())))
with open(OUT, 'w') as f:
    json.dump(res, f, indent=1)
print('scripts scanned: %d' % len(scripts))
print('external modules (%d): %s' % (len(external), ', '.join(sorted(external))))
print('local modules (%d): %s' % (len(local), ', '.join(sorted(local))))
for s in scripts:
    flags = []
    if s['relies_on_path0']:
        flags.append('RELIES_ON_PATH0')
    if s['import_safe_violations']:
        flags.append('NOT_IMPORT_SAFE(%d)' % len(s['import_safe_violations']))
    if s['dynamic']:
        flags.append('DYNAMIC(%d)' % len(s['dynamic']))
    if flags:
        print('  %-70s %s' % (os.path.relpath(s['path'], W), ' '.join(flags)))
