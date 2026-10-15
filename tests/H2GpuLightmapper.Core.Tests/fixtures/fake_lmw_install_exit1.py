"""S2.4 test stand-in for lmw_install.py that reports a failure AFTER a replace (exit 1), to test the shell's mapping.
It writes nothing."""
import sys
print('INSTALL: *** FAILED after a replace: fake failure for the wrapper test -- the MARKER stays', flush=True)
sys.exit(1)
