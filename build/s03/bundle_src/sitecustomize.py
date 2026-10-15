# H2 GPU Lightmapper Python bundle (S0.3): restore CPython's sys.path[0] under the ._pth file.
#
# Why: the bundle's python310._pth (verified on 3.10.5) makes CPython isolated -- no registry, no PYTHONPATH/PYTHONHOME,
# no user site (with the bundle's patched site.py) -- but isolated mode in 3.10 ALSO skips Modules/main.c's
# pymain_run_python "path0" step (`else if (!config->isolated)`), so `py C:\x\script.py` would NOT get C:\x on sys.path
# and an own-dir sibling import (lmw_vertex -> lmw_atlas / lmw_grid, lmw_charts -> lmw_grid) would fail. Production runs a
# normal, non-isolated Python, so this file re-adds exactly what CPython 3.10 would have added, at sys.path[0]:
#   script  -> the directory of GetFullPathNameW(argv[0]) (drop the trailing separator unless it is a drive root)
#   -c      -> ''          (cwd, resolved at import time, as CPython does)
#   -m      -> os.getcwd()
#   '-'     -> the cwd (GetFullPathNameW('-') -> cwd\-)
# A port of Python/pathconfig.c _PyPathConfig_ComputeSysPath0 (3.10, the MS_WINDOWS branch). Nothing is added when -I was
# really given on the command line (an explicit -I keeps its documented "no script dir" meaning), when argv is empty, or
# when argv[0] is a directory / zip that CPython itself already inserted (main_importer_path).
# Only `sys` and `os` are used (both already imported by site), so sys.modules is unchanged by this file.
import sys
import os


def _explicit_isolated(orig):
    """True if the real command line carried -I (alone or in a short-option cluster) before the script / -c / -m."""
    i = 1
    while i < len(orig):
        a = orig[i]
        if a == '--' or a == '-' or not a.startswith('-'):
            return False
        if a.startswith('--'):
            if a == '--check-hash-based-pycs':
                i += 1
            i += 1
            continue
        for j, ch in enumerate(a[1:], 1):
            if ch == 'I':
                return True
            if ch in 'cm':
                return False
            if ch in 'WX':
                if j == len(a) - 1:
                    i += 1          # the value is the next argument
                break
        i += 1
    return False


def _path0(argv0):
    if argv0 == '-m':
        return os.getcwd()
    if argv0 == '-c':
        return ''
    path0 = argv0
    try:
        import nt
        path0 = nt._getfullpathname(argv0)          # GetFullPathNameW
    except Exception:
        path0 = argv0                               # CPython keeps argv[0] when GetFullPathNameW fails ('' etc.)
    p = path0.rfind('\\')
    q = path0.rfind('/', p if p >= 0 else 0)
    if q >= 0:
        p = q
    if p < 0:
        return ''
    n = p + 1
    if n > 1 and path0[p - 1] != ':':
        n -= 1                                      # drop the trailing separator (keep it for "C:\")
    return path0[:n]


def _h2lm_fix_path0():
    if not sys.flags.isolated:
        return                                      # a normal start: CPython inserts path0 itself
    argv = getattr(sys, 'argv', None)
    if not argv:
        return
    if _explicit_isolated(getattr(sys, 'orig_argv', []) or []):
        return
    a0 = argv[0]
    if a0 not in ('-c', '-m', '-', '') and (os.path.isdir(a0) or a0.lower().endswith(('.zip', '.pyz', '.pyzw'))):
        return                                      # CPython's main_importer_path case already inserted it
    sys.path.insert(0, _path0(a0))


_h2lm_fix_path0()
del _h2lm_fix_path0
