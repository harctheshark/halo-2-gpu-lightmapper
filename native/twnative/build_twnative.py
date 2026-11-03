"""build_twnative.py -- build tw_native.exe (0.6.9): the native helper of the direct tag writer (vertex-lit buckets, direction tables).

  "/c/Program Files/Python310/python.exe" -I -B native/twnative/build_twnative.py [--out-dir DIR] [--vcvars BAT]

Source: tw_native.cpp here, byte-identical to KEEP engine_v374\\v043-tagwriter\\native\\tw_native.cpp (dev_TW's, md5 4abcc016; the logic is
unchanged). The dev build line was `cl /nologo /O2 /fp:strict /EHsc /W3 tw_native.cpp /Fe:tw_native.exe` under `vcvarsall x64` (no /arch: plain
SSE2 scalar single, every float32 op rounds like numpy's). This build keeps those flags and adds only: /MT (cl's default, stated), /Brepro
(deterministic), twnative.rc (VERSIONINFO + the asInvoker / Windows 10-11 manifest, like the tool loader) and /MANIFEST:NO (the manifest comes
from the .rc). It builds TWICE into separate folders and requires the same md5. The result goes to <out-dir>\\tw_native.exe (an existing different
file is renamed .bak_<ts> first) + build_twnative.json (record key "twnative"). Never runs the exe. Children: CREATE_NO_WINDOW, BELOW_NORMAL;
CL / _CL_ / LINK / _LINK_ removed from the build environment."""
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
ORIGINAL_SRC_MD5 = "4abcc016695b60553caa66db7b63223a"      # dev_TW / dev_069 / engine_v374 v043-tagwriter\native\tw_native.cpp
NAME = "tw_native.exe"
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
    for f in ("tw_native.cpp", "twnative.rc", "twnative.manifest"):
        shutil.copyfile(HERE / f, d / f)
    rcl = ["rc.exe", "/nologo", "/fo", "twnative.res", "twnative.rc"]
    rc, so, se = run(rcl, env=env, cwd=str(d))
    if rc != 0:
        raise SystemExit("rc FAILED (%d): %s" % (rc, so + se))
    cl = ["cl.exe", "/nologo", "/O2", "/fp:strict", "/MT", "/EHsc", "/W3", "/Brepro", "tw_native.cpp", "twnative.res", "/Fe:" + NAME,
          "/link", "/Brepro", "/MANIFEST:NO"]
    rc, so2, se2 = run(cl, env=env, cwd=str(d))
    (d / "build.log").write_text(so + se + so2 + se2, encoding="utf-8")
    if rc != 0 or not (d / NAME).exists():
        raise SystemExit("cl FAILED (%d): %s" % (rc, so2 + se2))
    return d / NAME, " ".join(rcl[1:]), " ".join(cl[1:]), [l for l in (so2 + se2).splitlines() if "warning" in l]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(PROJECT / "build" / "twnative"))
    ap.add_argument("--vcvars", default=DEFAULT_VCVARS)
    a = ap.parse_args()
    src_md5 = md5(HERE / "tw_native.cpp")
    if src_md5 != ORIGINAL_SRC_MD5:
        raise SystemExit("tw_native.cpp md5 %s is not the dev_TW source %s (the helper's logic must not change)" % (src_md5, ORIGINAL_SRC_MD5))
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
    rec = {"exe": str(dst), "md5": md5(dst), "size": dst.stat().st_size, "source": "native/twnative/tw_native.cpp", "source_md5": src_md5,
           "source_original": "KEEP dev_TW / engine_v374 v043-tagwriter\\native\\tw_native.cpp (md5 %s)" % ORIGINAL_SRC_MD5,
           "rc_md5": md5(HERE / "twnative.rc"), "manifest_md5": md5(HERE / "twnative.manifest"),
           "arch": "x64", "VCToolsVersion": env.get("VCToolsVersion"), "WindowsSDKVersion": env.get("WindowsSDKVersion", "").strip("\\"),
           "vcvars": "vcvarsall x64", "rc": rcl, "cl": cll, "original_cl": "cl /nologo /O2 /fp:strict /EHsc /W3 tw_native.cpp /Fe:tw_native.exe (vcvarsall x64)",
           "reproducible": {"build_a": m1, "build_b": m2}, "warnings": warn, "built": datetime.datetime.now().isoformat(timespec="seconds")}
    (out / "build_twnative.json").write_text(json.dumps({"twnative": rec}, indent=1), encoding="utf-8")
    print("%s BUILD OK  md5 %s  %d bytes  x64  toolset %s  (two builds identical)" % (NAME, rec["md5"], rec["size"], rec["VCToolsVersion"]))
    for w in warn:
        print("  " + w)


if __name__ == "__main__":
    main()
