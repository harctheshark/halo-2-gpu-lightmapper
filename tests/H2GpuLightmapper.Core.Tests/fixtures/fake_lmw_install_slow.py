"""S2.4 test stand-in for a slow tag write: prints, sleeps 3 s, prints, exits 0. It writes nothing."""
import sys, time
print('INSTALL: replacing (fake, 3 s)', flush=True)
time.sleep(3)
print('INSTALL: installed and verified (fake)', flush=True)
sys.exit(0)
