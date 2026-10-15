# Run a command at BELOW_NORMAL priority with no console window, time it, and pass its output through.
# Children inherit BELOW_NORMAL (Windows rule for BELOW_NORMAL/IDLE parents).
#   usage: python -I run_bn.py [--env-clean NAME,NAME] -- <exe> <args...>
# Prints "RUN_BN wall=<seconds> rc=<code>" as the last line; exits with the child's code.
import ctypes, os, subprocess, sys, time

try:
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL
except Exception:
    pass

argv = sys.argv[1:]
env = dict(os.environ)
if argv and argv[0] == '--env-clean':
    for n in argv[1].split(','):
        env.pop(n, None)
    argv = argv[2:]
if argv and argv[0] == '--':
    argv = argv[1:]
for k, v in (('DOTNET_CLI_TELEMETRY_OPTOUT', '1'), ('DOTNET_NOLOGO', '1'),
             ('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1'), ('DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', '1'),
             ('MSBUILDDISABLENODEREUSE', '1'), ('DOTNET_GENERATE_ASPNET_CERTIFICATE', 'false')):
    env.setdefault(k, v)
CREATE_NO_WINDOW = 0x08000000
t = time.perf_counter()
p = subprocess.run(argv, env=env, creationflags=CREATE_NO_WINDOW, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
wall = time.perf_counter() - t
sys.stdout.write(p.stdout.decode('utf-8', 'replace'))
print('RUN_BN wall=%.2f rc=%d' % (wall, p.returncode))
sys.exit(p.returncode)
