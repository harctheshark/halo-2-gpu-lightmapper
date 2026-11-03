"""build_toolloader.py -- build H2GpuLightmapper.ToolLoader.exe (0.6.8): the tool loader that replaces inject.exe.

  "/c/Program Files/Python310/python.exe" -I -B native/toolloader/build_toolloader.py [--out-dir DIR] [--vcvars BAT]

Source: inject.cpp here, byte-identical to KEEP\\v001-device-probe\\inject.cpp (checked: same md5 as the copy the original was built from;
the logic, arguments, exit codes and output text are unchanged). The original build line was `cl /nologo /O2 /EHsc /W3 inject.cpp
/Fe:inject.exe` under `vcvarsall x86` (x86: tool_fast.exe is 32-bit, so the loader must be too). This build keeps those flags and adds only:
/MT (cl's default, stated), /Brepro (deterministic), toolloader.rc (VERSIONINFO + the asInvoker / Windows 10-11 manifest) and /MANIFEST:NO
(the manifest comes from the .rc). It builds TWICE into separate folders and requires the same md5. The result goes to
<out-dir>\\H2GpuLightmapper.ToolLoader.exe (an existing different file is renamed .bak_<ts> first) + build_toolloader.json.
Never runs the exe. Children: CREATE_NO_WINDOW, BELOW_NORMAL; CL / _CL_ / LINK / _LINK_ removed from the build environment."""
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
ORIGINAL_SRC_MD5 = "fa4eda2ad6ea2872d05db812f20801f8"      # KEEP\v001-device-probe\inject.cpp (2026-07-17)
NAME = "H2GpuLightmapper.ToolLoader.exe"
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
    rc, out, err = run('cmd.exe /d /s /c ""%s" x86 >nul 2>&1 && set"' % vcvars, env=base)
    if rc != 0:
        raise SystemExit("vcvarsall x86 failed (%d): %s" % (rc, err))
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
    for f in ("inject.cpp", "toolloader.rc", "toolloader.manifest"):
        shutil.copyfile(HERE / f, d / f)
    rcl = ["rc.exe", "/nologo", "/fo", "toolloader.res", "toolloader.rc"]
    rc, so, se = run(rcl, env=env, cwd=str(d))
    if rc != 0:
        raise SystemExit("rc FAILED (%d): %s" % (rc, so + se))
    cl = ["cl.exe", "/nologo", "/O2", "/MT", "/EHsc", "/W3", "/Brepro", "inject.cpp", "toolloader.res", "/Fe:" + NAME,
          "/link", "/Brepro", "/MANIFEST:NO"]
    rc, so2, se2 = run(cl, env=env, cwd=str(d))
    (d / "build.log").write_text(so + se + so2 + se2, encoding="utf-8")
    if rc != 0 or not (d / NAME).exists():
        raise SystemExit("cl FAILED (%d): %s" % (rc, so2 + se2))
    return d / NAME, " ".join(rcl[1:]), " ".join(cl[1:]), [l for l in (so2 + se2).splitlines() if "warning" in l]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(PROJECT / "build" / "toolloader"))
    ap.add_argument("--vcvars", default=DEFAULT_VCVARS)
    a = ap.parse_args()
    src_md5 = md5(HERE / "inject.cpp")
    if src_md5 != ORIGINAL_SRC_MD5:
        raise SystemExit("inject.cpp md5 %s is not the original %s (the loader's logic must not change)" % (src_md5, ORIGINAL_SRC_MD5))
    out = Path(a.out_dir)
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
    rec = {"exe": str(dst), "md5": md5(dst), "size": dst.stat().st_size, "source": "native/toolloader/inject.cpp", "source_md5": src_md5,
           "source_original": "KEEP\\v001-device-probe\\inject.cpp (md5 %s)" % ORIGINAL_SRC_MD5,
           "rc_md5": md5(HERE / "toolloader.rc"), "manifest_md5": md5(HERE / "toolloader.manifest"),
           "arch": "x86", "VCToolsVersion": env.get("VCToolsVersion"), "WindowsSDKVersion": env.get("WindowsSDKVersion", "").strip("\\"),
           "vcvars": "vcvarsall x86", "rc": rcl, "cl": cll, "original_cl": "cl /nologo /O2 /EHsc /W3 inject.cpp /Fe:inject.exe (vcvarsall x86)",
           "reproducible": {"build_a": m1, "build_b": m2}, "warnings": warn, "built": datetime.datetime.now().isoformat(timespec="seconds")}
    (out / "build_toolloader.json").write_text(json.dumps({"toolloader": rec}, indent=1), encoding="utf-8")   # payload_spec record_key "toolloader"
    print("%s BUILD OK  md5 %s  %d bytes  x86  toolset %s  (two builds identical)" % (NAME, rec["md5"], rec["size"], rec["VCToolsVersion"]))
    for w in warn:
        print("  " + w)


if __name__ == "__main__":
    main()
