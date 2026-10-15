"""build_gpucheck.py -- build gpucheck.exe (S1.6 / N2): x64, /MT, MSVC 14.44, deterministic (/Brepro).

Usage (from Bash):
  "/c/Program Files/Python310/python.exe" build_gpucheck.py [--out-dir DIR] [--vcvars BAT] [--run]

Output: <project>\\build\\gpucheck\\gpucheck.exe (+ build_gpucheck.json with md5 / toolset); --run also writes
gpucheck_<COMPUTERNAME>.json from a run on this PC. Objects / import lib go to <out-dir>\\obj. Every child runs with
CREATE_NO_WINDOW at BELOW_NORMAL priority; CL / _CL_ / LINK / _LINK_ are removed from the build environment.
"""
import argparse
import ctypes
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
DEFAULT_VCVARS = r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
CREATE_NO_WINDOW = 0x08000000
BELOW_NORMAL = 0x00004000
EXPECT_TOOLSET = "14.44"


def lower_priority():
    try:
        k = ctypes.windll.kernel32
        k.SetPriorityClass(k.GetCurrentProcess(), BELOW_NORMAL)
    except Exception:
        pass


def md5(p):
    return hashlib.md5(Path(p).read_bytes()).hexdigest()


def run(cmd, env=None, cwd=None):
    if isinstance(cmd, list) and env is not None and not os.path.isabs(cmd[0]):
        # CreateProcess searches the PARENT's PATH, not the child env's: resolve against the child's PATH
        exe = shutil.which(cmd[0], path=env.get("PATH") or env.get("Path"))
        if not exe:
            raise SystemExit("%s not found on the build PATH" % cmd[0])
        cmd = [exe] + cmd[1:]
    r = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    return r.returncode, r.stdout.decode("mbcs", "replace"), r.stderr.decode("mbcs", "replace")


def msvc_env(vcvars):
    base = {k: v for k, v in os.environ.items() if k.upper() not in ("CL", "_CL_", "LINK", "_LINK_")}
    # a STRING command line: list2cmdline would backslash-escape the quotes, which cmd.exe does not understand
    rc, out, err = run('cmd.exe /d /s /c ""%s" x64 >nul 2>&1 && set"' % vcvars, env=base)
    if rc != 0:
        raise SystemExit("vcvarsall failed (%d): %s" % (rc, err))
    env = {}
    for line in out.splitlines():
        if "=" in line and not line.startswith("="):
            k, v = line.split("=", 1)
            env[k] = v
    for k in ("CL", "_CL_", "LINK", "_LINK_"):
        env.pop(k, None)
    tv = env.get("VCToolsVersion", "")
    if not tv.startswith(EXPECT_TOOLSET):
        raise SystemExit("toolset %r is not MSVC %s" % (tv, EXPECT_TOOLSET))
    return env


def main():
    lower_priority()
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(PROJECT / "build" / "gpucheck"))
    ap.add_argument("--vcvars", default=DEFAULT_VCVARS)
    ap.add_argument("--run", action="store_true", help="run the built exe once on this PC and save its JSON")
    a = ap.parse_args()
    out = Path(a.out_dir)
    obj = out / "obj"
    obj.mkdir(parents=True, exist_ok=True)
    env = msvc_env(a.vcvars)
    src = HERE / "gpucheck.cpp"
    ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    recs = {}
    # gpucheck.exe: WITH the Optimus/PowerXpress exports (what the N3 guarded fullgpu gets on a hybrid laptop)
    # gpucheck_plain.exe: WITHOUT them (what an export-less exe such as the production fullgpu.exe gets)
    for name, defs in (("gpucheck", []), ("gpucheck_plain", ["/DGPUCHECK_NO_OPTIMUS_EXPORTS"])):
        exe = out / (name + ".exe")
        tmp = obj / (name + ".new.exe")
        if tmp.exists():
            tmp.rename(obj / (name + ".new.exe.bak_" + ts))
        cl = ["cl.exe", "/nologo", "/O2", "/MT", "/EHsc", "/W4", "/permissive-", "/Brepro", "/DUNICODE", "/D_UNICODE"] + defs + [
              str(src), "/Fo" + str(obj / (name + ".obj")), "/Fe" + str(tmp),
              "/link", "/Brepro", "/DYNAMICBASE", "/NXCOMPAT", "/IMPLIB:" + str(obj / (name + ".lib")),
              "d3d11.lib", "dxgi.lib", "advapi32.lib"]
        rc, so, se = run(cl, env=env, cwd=str(obj))
        (obj / (name + "_cl.log")).write_text(so + se, encoding="utf-8")
        if rc != 0 or not tmp.exists():
            print(so + se)
            raise SystemExit("%s BUILD FAILED (cl exit %d)" % (name, rc))
        if exe.exists():
            if md5(exe) == md5(tmp):
                tmp.unlink()   # /Brepro: an identical rebuild -- keep the existing file
            else:              # back up before replacing
                exe.rename(out / (name + ".exe.bak_" + ts))
                tmp.rename(exe)
        else:
            tmp.rename(exe)
        rec = {
            "exe": str(exe), "md5": md5(exe), "size": exe.stat().st_size, "source_md5": md5(src),
            "optimus_exports": not defs,
            "VCToolsVersion": env.get("VCToolsVersion"), "WindowsSDKVersion": env.get("WindowsSDKVersion", "").strip("\\"),
            "cl": " ".join(Path(c).name if c == str(src) else c for c in cl[1:]),
            "warnings": [l for l in (so + se).splitlines() if "warning" in l],
            "built": datetime.datetime.now().isoformat(timespec="seconds"),
        }
        print("%s BUILD OK  md5 %s  %d bytes  toolset %s" % (name, rec["md5"], rec["size"], rec["VCToolsVersion"]))
        for w in rec["warnings"]:
            print("  " + w)
        if a.run:
            rc2, so2, se2 = run([str(exe), "--pretty"], env=dict(os.environ), cwd=str(out))
            host = os.environ.get("COMPUTERNAME", "host")
            (out / ("%s_%s.json" % (name, host))).write_text(so2, encoding="utf-8")
            try:
                j = json.loads(so2)
            except Exception as e:
                raise SystemExit("%s output is not JSON: %s\n%s\n%s" % (name, e, so2, se2))
            rec["run"] = {"exit": rc2, "host": host, "verdict": j.get("verdict"),
                          "default_adapter": j.get("default_adapter", {}).get("name")}
            print("%s RUN exit %d  default adapter: %s  verdict ok=%s" %
                  (name, rc2, rec["run"]["default_adapter"], j["verdict"]["ok"]))
        recs[name] = rec
    (out / "build_gpucheck.json").write_text(json.dumps(recs, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
