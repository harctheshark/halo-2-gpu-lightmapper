"""build_vertdir_fast.py -- build vertdir_fast.exe (0.6.12): the native builder of vertdir.bin, byte-identical to build_vertdir.py's default
path (build_vertdir.py runs it first and falls back to its own Python path on any non-zero exit).

  "/c/Program Files/Python310/python.exe" -I -B native/vertdir_fast/build_vertdir_fast.py [--out-dir DIR] [--vcvars BAT]

Same build recipe as native/twnative/build_twnative.py: cl /nologo /O2 /fp:strict /MT /EHsc /W3 /Brepro + vertdir_fast.rc (VERSIONINFO + the
asInvoker / Windows 10-11 manifest) + /link /Brepro /MANIFEST:NO, no debug info. It builds TWICE into separate folders and requires the same
md5. The result goes to <out-dir>\\vertdir_fast.exe (an existing different file is renamed .bak_<ts> first) + build_vertdir_fast.json. Never runs
the exe. Children: CREATE_NO_WINDOW, BELOW_NORMAL; CL / _CL_ / LINK / _LINK_ removed from the build environment."""
import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
DEFAULT_VCVARS = r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
NAME = "vertdir_fast.exe"
SRCS = ("vertdir_fast.cpp", "vertdir_fast.rc", "vertdir_fast.manifest")
CREATE_NO_WINDOW, BELOW_NORMAL = 0x08000000, 0x00004000
EXPECT_TOOLSET = "14.44"


def md5(p):
    return hashlib.md5(Path(p).read_bytes()).hexdigest()


def run(cmd, env=None, cwd=None):
    if isinstance(cmd, list) and env is not None and not os.path.isabs(cmd[0]):
        exe = shutil.which(cmd[0], path=env.get("PATH") or env.get("Path"))
        if not exe:
            raise SystemExit("%s not found on the build PATH" % cmd[0])
        cmd = [exe] + cmd[1:]
    r = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    return r.returncode, r.stdout.decode("mbcs", "replace"), r.stderr.decode("mbcs", "replace")


def msvc_env(vcvars):
    base = {k: v for k, v in os.environ.items() if k.upper() not in ("CL", "_CL_", "LINK", "_LINK_")}
    rc, out, err = run('cmd.exe /d /s /c ""%s" x64 >nul 2>&1 && set"' % vcvars, env=base)
    if rc != 0:
        raise SystemExit("vcvarsall x64 failed (%d): %s" % (rc, err))
    env = {}
    for line in out.splitlines():
        if "=" in line and not line.startswith("="):
            k, v = line.split("=", 1)
            env[k] = v
    for k in ("CL", "_CL_", "LINK", "_LINK_"):
        env.pop(k, None)
    if not env.get("VCToolsVersion", "").startswith(EXPECT_TOOLSET):
        raise SystemExit("toolset %r is not MSVC %s" % (env.get("VCToolsVersion"), EXPECT_TOOLSET))
    return env


def build_once(env, d):
    d.mkdir(parents=True, exist_ok=True)
    for f in SRCS:
        shutil.copyfile(HERE / f, d / f)
    rcl = ["rc.exe", "/nologo", "/fo", "vertdir_fast.res", "vertdir_fast.rc"]
    rc, so, se = run(rcl, env=env, cwd=str(d))
    if rc != 0:
        raise SystemExit("rc FAILED (%d): %s" % (rc, so + se))
    cl = ["cl.exe", "/nologo", "/O2", "/fp:strict", "/MT", "/EHsc", "/W3", "/Brepro", "vertdir_fast.cpp", "vertdir_fast.res", "/Fe:" + NAME,
          "/link", "/Brepro", "/MANIFEST:NO"]
    rc, so2, se2 = run(cl, env=env, cwd=str(d))
    (d / "build.log").write_text(so + se + so2 + se2, encoding="utf-8")
    if rc != 0 or not (d / NAME).exists():
        raise SystemExit("cl FAILED (%d): %s" % (rc, so2 + se2))
    return d / NAME, " ".join(rcl[1:]), " ".join(cl[1:]), [l for l in (so2 + se2).splitlines() if "warning" in l]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(PROJECT / "build" / "vertdir_fast"))
    ap.add_argument("--vcvars", default=DEFAULT_VCVARS)
    a = ap.parse_args()
    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    env = msvc_env(a.vcvars)
    work = out / ("obj_" + ts)
    e1, rcl, cll, warn = build_once(env, work / "a")
    e2, _, _, _ = build_once(env, work / "b")
    m1, m2 = md5(e1), md5(e2)
    if m1 != m2:
        raise SystemExit("NOT REPRODUCIBLE: build a %s, build b %s" % (m1, m2))
    dst = out / NAME
    if dst.exists() and md5(dst) != m1:
        dst.rename(out / (NAME + ".bak_" + ts))
    if not dst.exists():
        shutil.copyfile(e1, dst)
    rec = {"exe": str(dst), "md5": md5(dst), "size": dst.stat().st_size, "source": "native/vertdir_fast/vertdir_fast.cpp",
           "source_md5": md5(HERE / "vertdir_fast.cpp"), "rc_md5": md5(HERE / "vertdir_fast.rc"), "manifest_md5": md5(HERE / "vertdir_fast.manifest"),
           "arch": "x64", "VCToolsVersion": env.get("VCToolsVersion"), "WindowsSDKVersion": env.get("WindowsSDKVersion", "").strip("\\"),
           "vcvars": "vcvarsall x64", "rc": rcl, "cl": cll, "reproducible": {"build_a": m1, "build_b": m2}, "warnings": warn,
           "built": datetime.datetime.now().isoformat(timespec="seconds")}
    (out / "build_vertdir_fast.json").write_text(json.dumps({"vertdir_fast": rec}, indent=1), encoding="utf-8")
    print("%s BUILD OK  md5 %s  %d bytes  x64  toolset %s  (two builds identical)" % (NAME, rec["md5"], rec["size"], rec["VCToolsVersion"]))
    for w in warn:
        print("  " + w)


if __name__ == "__main__":
    main()
