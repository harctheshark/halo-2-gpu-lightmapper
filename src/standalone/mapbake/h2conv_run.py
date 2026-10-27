"""h2conv_run.py <h2conv home> <module> [args...] -- starts one command line tool of the separate program "h2conv" (GPL-3.0-or-later) in this process
as `python -m <module>`: the bundled Python runs isolated (no script directory on sys.path), so the h2conv folder is put on sys.path first and the module is
run with runpy.  No bytecode is written (the runtime folder is md5-checked).  This file only launches h2conv; it contains none of its code."""
import os
import runpy
import sys

sys.dont_write_bytecode = True

if len(sys.argv) < 3:
    sys.stderr.write('usage: h2conv_run.py <h2conv home> <module> [args...]\n')
    sys.exit(2)
home, module = sys.argv[1], sys.argv[2]
sys.path.insert(0, home)
sys.argv = [module] + sys.argv[3:]
runpy.run_module(module, run_name='__main__', alter_sys=True)
