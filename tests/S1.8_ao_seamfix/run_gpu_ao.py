"""run_gpu_ao.py MODE PROD_AO_DIR PATCHED_DIR -- <gpu_ao.py argv...>
Runs the PRODUCTION gpu_ao.py (loaded read-only from PROD_AO_DIR, no bytecode written) with one of two seam filters:
  A  the pristine production ao_seamfix.py next to gpu_ao.py (scipy's cKDTree)
  B  the N4-patched ao_seamfix.py + ao_ballgrid.py from PATCHED_DIR, with scipy BLOCKED (any `import scipy*` raises ImportError)
gpu_ao.main() puts its own dir at sys.path[0] before it imports ao_seamfix, so for B the patched module is imported FIRST and sits
in sys.modules. The last stdout line reports which ao_seamfix ran and whether scipy got loaded (the A/B harness checks both)."""
import os, sys, json, importlib.util

sys.dont_write_bytecode = True
if os.name == 'nt':                                   # BELOW_NORMAL (the brief); children (build_vertdir) inherit it
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)


class _NoScipy:
    """meta-path finder: the B path must run with scipy absent, as in the standalone bundle"""
    def find_spec(self, name, path=None, target=None):
        if name == 'scipy' or name.startswith('scipy.'):
            raise ImportError('scipy is blocked on the S1.8 B path (%s)' % name)
        return None


def main():
    mode, prod, patched = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
    rest = sys.argv[sys.argv.index('--') + 1:]
    if mode not in ('A', 'B'):
        raise SystemExit('mode must be A or B')
    if mode == 'B':
        sys.meta_path.insert(0, _NoScipy())
        sys.path.insert(0, patched)
        import ao_seamfix                                         # the patched one (patched dir first on sys.path)
        if os.path.dirname(os.path.abspath(ao_seamfix.__file__)) != patched:
            raise SystemExit('B: imported the wrong ao_seamfix: %s' % ao_seamfix.__file__)
    spec = importlib.util.spec_from_file_location('gpu_ao', os.path.join(prod, 'gpu_ao.py'))
    G = importlib.util.module_from_spec(spec)
    sys.modules['gpu_ao'] = G
    spec.loader.exec_module(G)
    sys.argv = [os.path.join(prod, 'gpu_ao.py')] + rest
    rc = 0
    try:
        G.main()
    except SystemExit as e:
        rc = e.code if isinstance(e.code, int) else (0 if e.code is None else 1)
    sf = sys.modules.get('ao_seamfix')
    info = dict(mode=mode, rc=rc, ao_seamfix=getattr(sf, '__file__', None),
                ao_ballgrid=getattr(sys.modules.get('ao_ballgrid'), '__file__', None),
                scipy_loaded=sorted(k for k in sys.modules if k == 'scipy' or k.startswith('scipy.'))[:5])
    print('RUN_GPU_AO ' + json.dumps(info), flush=True)
    sys.exit(rc)


if __name__ == '__main__':
    main()
