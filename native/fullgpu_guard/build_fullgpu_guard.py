"""build_fullgpu_guard.py -- N3: the guarded fullgpu.exe, a HOST-ONLY rebuild (H2 GPU Lightmapper standalone, step S1.7).

Parameterised on the engine dir, so it re-runs unchanged against a promoted engine (new fullgpu.exe + kernel headers):

  "/c/Program Files/Python310/python.exe" build_fullgpu_guard.py [--engine-dir DIR] [--out-root DIR] [--vcvars BAT]
        [--repro-dir DIR ...] [--no-repro] [--plan-farm DIR ...] [--skip-plan] [--allow-manifest-drift]

Nothing in the engine dir is written or executed in place. Sources are COPIED (md5-verified) into
<out-root>\\eng_<production fullgpu md5[:8]>\\work\\ and every exe runs from a private probe folder. No kernel header is ever
recompiled (fxc is never invoked). No GPU device is ever created: every behaviour probe exits before gather.cpp's
D3D11CreateDevice at :1053, and each run is killed after 60 s.

Stages (each recorded in BUILD_MANIFEST.json; exit 0 only when every required check passed):
  A  FIRST, before any compile:
     - every kernel blob the production fullgpu.exe embeds is traced to a header in the include closure of gather.cpp
       (DXBC md5s);
     - the closure's md5s agree with the engine's ENGINE_MANIFEST.txt;
     - the cl line is read from the engine's own build_fullgpu.bat (asserted equal to build_host_only.bat);
     - the local compiler build equals the one in production's Rich header.
  B  rebuild the UNMODIFIED host with that exact cl line.
     B0 byte-exact REPRODUCTION. MSVC names every lambda <lambda_HASH>, and HASH depends on the compile's
        (cwd, source-path) strings. It does not depend on the file's identity, timestamps, line shifts or added
        includes (measured; see PROGRESS.md). So a rebuild can match production byte for byte only from the
        directory production was compiled in. The script finds that directory (a
        v041-fullgpu under ..\\_research holding production's fullgpu.exe and md5-identical sources), compiles there
        READ-ONLY (cwd only; /Fo and /Fe point into the build folder; a before/after listing proves nothing was
        written), and requires the result to equal production with the link stamps zeroed (COFF TimeDateStamp,
        CheckSum, debug-dir stamps).
     B1 the md5-verified COPY built in work\\: the same size and DXBC sequence as production, the same strings once the
        lambda hash is masked, and the only differing strings are lambda names.
  C  the GUARDED host, compiled in the same work\\ path as B1. Its lambda names still differ: code added later in main
     changes the hash too.
     - n3_guard_edits.py is applied to the copied gather.cpp, plus n3_palloc_guard.h;
     - the edits add a palloc uint32 overflow FATAL (exit 9) and the NvOptimusEnablement /
       AmdPowerXpressRequestHighPerformance exports;
     - the DXBC sequence must equal production's;
     - every production string must survive (lambda-masked) and every string B1 has must be present;
     - there must be exactly the two exports, each with value 1 and in .data;
     - gpu_bake's stale-binary needles must be present, and there must be no new compiler warnings.
  D  a CPU-only unit test of n3_palloc_guard.h, built with fullgpu's compiler flags.
  E  behaviour probes on the GPU-free paths, comparing production, B1 and the guarded build. Exit code, stdout (path and
     timings masked) and part_plan.txt must all agree. The probes are:
     - an empty dir (exit 1);
     - GPULM_FULLGPU=1 without texelgen_soup.bin (exit 9);
     - a malformed GPULM_PART_RANGE (exit 9);
     - a part range past the soup's end (exit 9);
     - the v372 SEG part planner (GPULM_PART_PLAN_ONLY=1, exit 0), on capture inputs copied from real farm dirs.

The GPU identity proof (keyed-identical bakes at Super on cyclotron, headlong and mainmenu) is gate T3a, NOT this script.
"""
import argparse
import ctypes
import datetime
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import pe_util  # noqa: E402
import n3_guard_edits  # noqa: E402

DEFAULT_ENGINE = os.environ.get("H2LM_ENGINE_DIR", "")   # <H2EK_ROOT>\GPU Lightmap Work\engine_v373 (read only); or pass --engine-dir
DEFAULT_VCVARS = r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
DEFAULT_PLAN_FARMS = ["farm_w140", "farm_w165"]   # static captures (1-part and 2-part plans); copied, never written
PLAN_INPUTS = ["gatherq.bin", "tbuf_sorted.bin", "inst.bin", "tnrm.bin", "tri_matraw.bin", "vertex_q.bin",
               "scenery_q.bin", "texelgen_soup.bin", "raster_globals.bin", "seam_lists.bin"]
DENY_FARMS = {"farm_w149"}
CREATE_NO_WINDOW = 0x08000000
BELOW_NORMAL = 0x00004000
CHILD_ENV_DROP = ("GPULM_", "DEVP_", "OSOYOOS_", "H2LM_", "PYTHON")
PROBE_TIMEOUT_S = 60
MAX_REPRO_CANDIDATES = 3
LAMBDA_RE = re.compile(rb"<lambda_[0-9a-f]{32}>")


# ----------------------------------------------------------------------------------------------------------- utilities
def lower_priority():
    try:
        k = ctypes.windll.kernel32
        k.SetPriorityClass(k.GetCurrentProcess(), BELOW_NORMAL)
    except Exception:
        pass


def md5b(b):
    return hashlib.md5(b).hexdigest()


def md5f(p):
    return md5b(Path(p).read_bytes())


def now():
    return datetime.datetime.now().isoformat(timespec="seconds")


def run(cmd, env=None, cwd=None, timeout=None):
    if isinstance(cmd, list) and env is not None and not os.path.isabs(cmd[0]):
        # CreateProcess searches the PARENT's PATH, not the child env's: resolve against the child's PATH
        exe = shutil.which(cmd[0], path=env.get("PATH") or env.get("Path"))
        if not exe:
            raise SystemExit("%s not found on the build PATH" % cmd[0])
        cmd = [exe] + cmd[1:]
    try:
        r = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, timeout=timeout,
                           creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
        return r.returncode, r.stdout.decode("mbcs", "replace"), r.stderr.decode("mbcs", "replace")
    except subprocess.TimeoutExpired as e:
        return None, (e.stdout or b"").decode("mbcs", "replace"), "TIMEOUT after %ss" % timeout


def msvc_env(vcvars):
    base = {k: v for k, v in os.environ.items() if k.upper() not in ("CL", "_CL_", "LINK", "_LINK_")}
    # a STRING command line: list2cmdline would backslash-escape the quotes, which cmd.exe does not understand
    rc, out, err = run('cmd.exe /d /s /c ""%s" x64 >nul 2>&1 && set"' % vcvars, env=base)
    if rc != 0:
        raise SystemExit("vcvarsall failed (%s): %s" % (rc, err))
    env = {}
    for line in out.splitlines():
        if "=" in line and not line.startswith("="):
            k, v = line.split("=", 1)
            env[k] = v
    for k in ("CL", "_CL_", "LINK", "_LINK_"):
        env.pop(k, None)
    return env


def child_env(extra=None):
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(CHILD_ENV_DROP)}
    env.update(extra or {})
    return env


def backup_aside(p):
    """Rename an existing file/dir aside with a timestamp (the back-up-before-changing rule; never a delete)."""
    p = Path(p)
    if p.exists():
        dst = p.with_name(p.name + ".bak_" + datetime.datetime.now().strftime("%Y%m%d_%H%M%S"))
        p.rename(dst)
        return str(dst)
    return None


def link_or_copy(src, dst):
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def dir_listing(d):
    """Non-recursive (name, size, mtime_ns) listing: the before/after proof that a read-only compile wrote nothing."""
    out = {}
    with os.scandir(d) as it:
        for e in it:
            st = e.stat(follow_symlinks=False)
            out[e.name] = (st.st_size, st.st_mtime_ns, e.is_dir(follow_symlinks=False))
    return out


# ------------------------------------------------------------------------------------------------- source inspection
INC_RE = re.compile(r'^[ \t]*#[ \t]*include[ \t]*"([^"]+)"', re.M)
BLOB_RE = re.compile(r"const\s+BYTE\s+(\w+)\s*\[\s*\]\s*=\s*\{(.*?)\};", re.S)
NUM_RE = re.compile(r"\d+")


def include_closure(root_cpp):
    """Every quoted #include reachable from gather.cpp, in discovery order (+ includes that resolve to no file)."""
    seen, order, missing = {}, [], []
    queue = [Path(root_cpp)]
    while queue:
        f = queue.pop(0)
        text = f.read_bytes().decode("latin-1")
        for inc in INC_RE.findall(text):
            p = (f.parent / inc).resolve()
            if not p.exists():
                missing.append({"include": inc, "from": f.name})
                continue
            if p not in seen:
                seen[p] = [f.name]
                order.append(p)
                queue.append(p)
            else:
                seen[p].append(f.name)
    return order, seen, missing


def header_blobs(path):
    out = []
    text = Path(path).read_bytes().decode("latin-1")
    for name, body in BLOB_RE.findall(text):
        b = bytes(int(x) for x in NUM_RE.findall(body))
        out.append({"name": name, "size": len(b), "md5": md5b(b),
                    "dxbc": b[:4] == b"DXBC" and len(b) >= 28 and int.from_bytes(b[24:28], "little") == len(b)})
    return out


def read_manifest(engine):
    m = {}
    p = Path(engine) / "ENGINE_MANIFEST.txt"
    if not p.exists():
        return None
    for line in p.read_text(encoding="utf-8", errors="replace").splitlines():
        mm = re.match(r"^([0-9a-fA-F]{32})\s+(.+?)\s*$", line)
        if mm:
            m[mm.group(2).replace("/", "\\").lower()] = mm.group(1).lower()
    return m


def cl_line_from(bat):
    for line in Path(bat).read_text(encoding="utf-8", errors="replace").splitlines():
        s = line.strip()
        if s.lower().startswith("cl "):
            return re.sub(r"\s*>.*$", "", s).strip()
    return None


def section_strings(data, names=(".rdata", ".data"), n=8):
    """Printable ASCII runs (>= n) inside the data sections only (code bytes would add noise), lambda hashes masked."""
    info = pe_util.parse(data)
    out = set()
    for s in info["sections"]:
        if s["name"] in names:
            blob = data[s["raw_ptr"]:s["raw_ptr"] + s["raw_size"]]
            for m in re.finditer(rb"[\x20-\x7e]{%d,}" % n, blob):
                out.add(LAMBDA_RE.sub(b"<lambda_#>", m.group(0)))
    return out


def lambda_names(data):
    return sorted(set(x.decode() for x in LAMBDA_RE.findall(data)))


def gpu_bake_needles(engine):
    """The literal strings gpu_bake.ps1 Select-Strings in $FULLGPU (its stale-binary refusal)."""
    p = Path(engine) / "gpu_bake.ps1"
    if not p.exists():
        return []
    t = p.read_text(encoding="utf-8", errors="replace")
    out = re.findall(r'Select-String -Path \$FULLGPU -SimpleMatch -Pattern "([^"]+)"', t)
    m = re.search(r"\$needG372\s*=\s*@\((.*?)\$missG372", t, re.S)
    if m:
        out += re.findall(r'"([^"]+)"', re.sub(r"#[^\n]*", "", m.group(1)))
    return out


def norm_warnings(lines):
    return sorted(re.sub(r"\(\d+\)", "(#)", l) for l in lines)


# ----------------------------------------------------------------------------------------------------- build helpers
def compile_host(cwd, cl_line, env, out_dir, log_path):
    """The production cl line, run in `cwd` (gather.cpp relative, as the bat does) with /Fo and /Fe redirected to out_dir."""
    toks = cl_line.split()
    assert toks[0].lower() == "cl" and "gather.cpp" in toks
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    exe, obj = out_dir / "fullgpu.exe", out_dir / "gather.obj"
    args = []
    for t in toks[1:]:
        if t.lower().startswith("/fe"):
            args.append("/Fe:" + str(exe))
        else:
            args.append(t)
    i = args.index("gather.cpp")
    args.insert(i + 1, "/Fo" + str(obj))
    rc, so, se = run(["cl.exe"] + args, env=env, cwd=str(cwd))
    Path(log_path).write_text(so + se, encoding="utf-8")
    if obj.exists():
        obj.unlink()   # what build_host_only.bat does (a file this script just created)
    warn = [l.strip() for l in (so + se).splitlines() if ": warning " in l]
    if rc != 0 or not exe.exists():
        raise RuntimeError("cl failed (exit %s) in %s:\n%s" % (rc, cwd, (so + se)[-4000:]))
    return exe, warn, ["cl"] + args


def pe_summary(data):
    info = pe_util.parse(data)
    return {"md5": md5b(data), "size": len(data), "normalized_md5": md5b(pe_util.normalized(data)),
            "link_timestamp": info["timestamp"], "checksum": info["checksum"], "linker": info["linker"],
            "rich": info["rich"], "debug": info["debug"], "exports": info["exports"],
            "lambda_names": lambda_names(data),
            "sections": [{k: s[k] for k in ("name", "vsize", "raw_size")} for s in info["sections"]]}


def compare(ref, new, label):
    out = {"label": label}
    pn, nn = pe_util.normalized(ref), pe_util.normalized(new)
    out["raw_identical"] = ref == new
    out["normalized_identical"] = pn == nn
    out["stamps_zeroed"] = pe_util.stamp_labels(new)
    if len(pn) == len(nn) and pn != nn:
        rs = pe_util.diff_ranges(pn, nn)
        inf = pe_util.parse(new)
        secs = {}
        for a, b in rs:
            k = pe_util.section_of(inf, a)
            secs[k] = secs.get(k, 0) + (b - a)
        out["diff_range_count"] = len(rs)
        out["diff_bytes"] = sum(b - a for a, b in rs)
        out["diff_bytes_by_section"] = secs
        out["diff_ranges_first"] = [{"start": hex(a), "end": hex(b), "section": pe_util.section_of(inf, a)}
                                    for a, b in rs[:12]]
    elif len(pn) != len(nn):
        out["size_delta"] = len(nn) - len(pn)
    bp, bn = pe_util.dxbc_blobs(ref), pe_util.dxbc_blobs(new)
    out["dxbc_count"] = [len(bp), len(bn)]
    out["dxbc_sequence_identical"] = [b[2] for b in bp] == [b[2] for b in bn]
    sp, sn = section_strings(ref), section_strings(new)
    out["strings_missing"] = sorted(s.decode() for s in sp - sn)[:50]
    out["strings_new"] = sorted(s.decode() for s in sn - sp)[:80]
    lr, ln = lambda_names(ref), lambda_names(new)
    out["lambda_names_equal"] = lr == ln
    out["lambda_names"] = {"ref": lr, "new": ln}
    return out


def find_repro_dirs(engine, gather_md5, prod_md5):
    """v041-fullgpu dirs under <engine>\\..\\_research (depth <= 4) holding production's fullgpu.exe and gather.cpp."""
    root = Path(engine).parent / "_research"
    out = []
    for depth in range(1, 5):
        pat = str(root / os.path.join(*(["*"] * depth)) / "v041-fullgpu")
        for d in sorted(glob.glob(pat)):
            d = Path(d)
            try:
                if (d / "fullgpu.exe").stat().st_size != os.path.getsize(Path(engine) / "v041-fullgpu" / "fullgpu.exe"):
                    continue
                if md5f(d / "fullgpu.exe") == prod_md5 and md5f(d / "gather.cpp") == gather_md5:
                    # rank by how close a cl_*.log there was written to the exe's own mtime (the dir that LINKED it
                    # wrote both within seconds; a dir that only received a copy rarely has such a log)
                    t_exe = (d / "fullgpu.exe").stat().st_mtime
                    gaps = [abs(p.stat().st_mtime - t_exe) for p in d.glob("cl_*.log")]
                    out.append((min(gaps) if gaps else 1e12, str(d), d))
            except OSError:
                continue
    out.sort(key=lambda t: (t[0], t[1]))
    return [d for _, _, d in out]


# ---------------------------------------------------------------------------------------------------- probes (no GPU)
def norm_stdout(s, probe_dir):
    s = s.replace("\r\n", "\n").replace(str(probe_dir), "<DIR>")
    s = re.sub(r"in \d+ ms", "in # ms", s)
    s = re.sub(r"^(HOSTMEM:.*)$", lambda m: re.sub(r"\d+(\.\d+)?", "#", m.group(1)), s, flags=re.M)
    return s


def cache_plan_inputs(farm, cache_root):
    farm = Path(farm)
    if farm.name.lower() in DENY_FARMS:
        raise SystemExit("refusing %s (denylisted)" % farm)
    dst = Path(cache_root) / farm.name
    meta = dst / "inputs.json"
    if meta.exists():
        return dst, json.loads(meta.read_text(encoding="utf-8"))
    tmp = Path(cache_root) / (farm.name + ".tmp")
    if tmp.exists():
        backup_aside(tmp)
    tmp.mkdir(parents=True)
    rec = {"source": str(farm), "copied": now(), "files": {}}
    for n in PLAN_INPUTS + ["part_plan.txt", "gpu_bake_partplan.log", "capture_id.json"]:
        s = farm / n
        if s.exists() and s.is_file():
            shutil.copy2(s, tmp / n)   # a read of the farm dir; nothing there is written
            rec["files"][n] = {"md5": md5f(tmp / n), "size": (tmp / n).stat().st_size}
    if "texelgen_soup.bin" not in rec["files"]:
        raise SystemExit("%s lacks texelgen_soup.bin" % farm)
    (tmp / "inputs.json").write_text(json.dumps(rec, indent=1), encoding="utf-8")
    tmp.rename(dst)
    return dst, rec


def run_probe(exe, probe_dir, env_extra, inputs_dir=None, exclude=()):
    probe_dir = Path(probe_dir)
    probe_dir.mkdir(parents=True, exist_ok=False)
    shutil.copy2(exe, probe_dir / "fullgpu.exe")
    if inputs_dir:
        for n in PLAN_INPUTS:
            s = Path(inputs_dir) / n
            if s.exists() and n not in exclude:
                link_or_copy(s, probe_dir / n)
    before = set(p.name for p in probe_dir.iterdir())
    rc, so, se = run([str(probe_dir / "fullgpu.exe")], env=child_env(env_extra), cwd=str(probe_dir),
                     timeout=PROBE_TIMEOUT_S)
    (probe_dir / "stdout.txt").write_text(so, encoding="utf-8")
    if se:
        (probe_dir / "stderr.txt").write_text(se, encoding="utf-8")
    new_files = {}
    for p in probe_dir.iterdir():
        if p.name not in before and p.name not in ("stdout.txt", "stderr.txt"):
            new_files[p.name] = md5f(p)
    norm = norm_stdout(so, probe_dir)
    (probe_dir / "stdout_norm.txt").write_text(norm, encoding="utf-8")
    lines = norm.strip().splitlines()
    return {"exit": rc, "stdout_norm_md5": md5b(norm.encode("utf-8")), "outputs": new_files,
            "device_line": any(l.startswith("device fail") for l in lines) or rc is None,
            "last_lines": lines[-2:], "stderr": se.strip()[:400]}


# ------------------------------------------------------------------------------------------------------------- main
def main():
    lower_priority()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--engine-dir", default=DEFAULT_ENGINE)
    ap.add_argument("--out-root", default=str(PROJECT / "build" / "fullgpu_guard"))
    ap.add_argument("--vcvars", default=DEFAULT_VCVARS)
    ap.add_argument("--repro-dir", action="append", default=None,
                    help="the dir production fullgpu.exe was compiled in (default: auto-discovered under ..\\_research)")
    ap.add_argument("--no-repro", action="store_true", help="skip B0 (then the byte-exact identity is NOT shown)")
    ap.add_argument("--plan-farm", action="append", default=None,
                    help="farm dir whose capture feeds the part-planner probes (copied once; repeatable)")
    ap.add_argument("--skip-plan", action="store_true")
    ap.add_argument("--allow-manifest-drift", action="store_true",
                    help="downgrade an ENGINE_MANIFEST.txt md5 disagreement from FAIL to a warning")
    a = ap.parse_args()

    engine = Path(a.engine_dir).resolve()
    src_dir = engine / "v041-fullgpu"
    prod_exe = src_dir / "fullgpu.exe"
    if not (src_dir / "gather.cpp").exists() or not prod_exe.exists():
        raise SystemExit("not an engine dir (no v041-fullgpu\\gather.cpp + fullgpu.exe): %s" % engine)
    out_root = Path(a.out_root)
    out_root.mkdir(parents=True, exist_ok=True)
    prod = prod_exe.read_bytes()
    prod_md5 = md5b(prod)
    tag = "eng_" + prod_md5[:8]
    out = out_root / tag
    prev = backup_aside(out)
    out.mkdir(parents=True)
    R = {"step": "S1.7 N3 guarded fullgpu host-only build", "script_md5": md5f(__file__), "started": now(),
         "engine_dir": str(engine), "tag": tag, "previous_build_moved_to": prev, "checks": {}, "warnings": []}
    checks = R["checks"]

    def check(name, ok, required=True, **kw):
        checks[name] = dict(ok=bool(ok), required=required, **kw)
        print("%-40s %s %s" % (name, "PASS" if ok else ("FAIL" if required else "info"),
                                "" if ok else json.dumps(kw, default=str)[:700]))
        return ok

    # ---------------------------------------------------------------- A: production facts, BEFORE any compile
    R["production"] = dict(path=str(prod_exe), **pe_summary(prod))
    closure, used_by, missing = include_closure(src_dir / "gather.cpp")
    files = [src_dir / "gather.cpp"] + closure
    man = read_manifest(engine)
    sources, header_blob_index = [], {}
    for f in files:
        rel = os.path.relpath(f, engine)
        rec = {"rel": rel, "md5": md5f(f), "size": f.stat().st_size}
        blobs = header_blobs(f) if f.suffix.lower() == ".h" else []
        rec["kind"] = "kernel" if blobs else "host"
        if blobs:
            rec["blobs"] = blobs
            for b in blobs:
                header_blob_index.setdefault(b["md5"], []).append(rel + ":" + b["name"])
        if man is not None:
            rec["manifest_md5"] = man.get(rel.lower())
        sources.append(rec)
    R["sources"] = sources
    R["include_missing"] = missing
    prod_blobs = pe_util.dxbc_blobs(prod)
    prod_blob_md5 = set(b[2] for b in prod_blobs)
    R["production"]["dxbc"] = [{"offset": hex(o), "size": s, "md5": m, "headers": header_blob_index.get(m, [])}
                               for o, s, m in prod_blobs]
    for s in sources:
        for b in s.get("blobs", []):
            b["in_production"] = b["md5"] in prod_blob_md5
    untraced = [b for b in R["production"]["dxbc"] if not b["headers"]]
    bad_dxbc = [s["rel"] + ":" + b["name"] for s in sources for b in s.get("blobs", []) if not b["dxbc"]]
    not_embedded = [s["rel"] + ":" + b["name"] for s in sources for b in s.get("blobs", []) if not b["in_production"]]
    ok_a1 = check("A1_embedded_kernels_traced_to_headers", not untraced and not bad_dxbc,
                  production_blobs=len(prod_blobs), kernel_headers=sum(1 for s in sources if s["kind"] == "kernel"),
                  header_blobs=sum(len(s.get("blobs", [])) for s in sources),
                  untraced=untraced, malformed=bad_dxbc, header_blobs_not_embedded=not_embedded)
    checks["A1_embedded_kernels_traced_to_headers"].update(
        production_blobs=len(prod_blobs), kernel_headers=sum(1 for s in sources if s["kind"] == "kernel"),
        header_blobs=sum(len(s.get("blobs", [])) for s in sources), header_blobs_not_embedded=not_embedded)
    if man is None:
        R["warnings"].append("no ENGINE_MANIFEST.txt in the engine dir: A2 skipped")
        ok_a2 = True
    else:
        drift = [{"rel": s["rel"], "file": s["md5"], "manifest": s["manifest_md5"]} for s in sources
                 if s.get("manifest_md5") and s["manifest_md5"] != s["md5"]]
        unlisted = [s["rel"] for s in sources if not s.get("manifest_md5")]
        exe_m = man.get(os.path.relpath(prod_exe, engine).lower())
        if exe_m and exe_m != prod_md5:
            drift.append({"rel": os.path.relpath(prod_exe, engine), "file": prod_md5, "manifest": exe_m})
        ok_a2 = check("A2_sources_match_engine_manifest", not drift or a.allow_manifest_drift,
                      drift=drift, unlisted=unlisted, fullgpu_in_manifest=exe_m)
        checks["A2_sources_match_engine_manifest"].update(listed=len(sources) - len(unlisted), unlisted=unlisted,
                                                          fullgpu_in_manifest=exe_m)
        if drift and a.allow_manifest_drift:
            R["warnings"].append("ENGINE_MANIFEST drift allowed by --allow-manifest-drift: %s" % drift)
    cl_full = cl_line_from(src_dir / "build_fullgpu.bat")
    cl_host = cl_line_from(src_dir / "build_host_only.bat") if (src_dir / "build_host_only.bat").exists() else None
    ok_a3 = check("A3_cl_line_from_production_bat", bool(cl_full) and (cl_host in (None, cl_full)),
                  build_fullgpu_bat=cl_full, build_host_only_bat=cl_host)
    checks["A3_cl_line_from_production_bat"]["cl_line"] = cl_full
    R["cl_line"] = cl_full
    env = msvc_env(a.vcvars)
    rc, so, se = run(["cl.exe"], env=env)
    mver = re.search(r"Compiler Version (\d+)\.(\d+)\.(\d+)", so + se)
    cl_build = int(mver.group(3)) if mver else None
    prod_cc = sorted(set(e["build"] for e in R["production"]["rich"] if e["prodid"] in (259, 260, 261) and e["count"] == 1))
    R["toolchain"] = {"VCToolsVersion": env.get("VCToolsVersion"), "WindowsSDKVersion": env.get("WindowsSDKVersion", "").strip("\\"),
                      "cl_version": mver.group(0) if mver else None, "production_rich_compiler_builds": prod_cc}
    ok_a4 = check("A4_toolchain_matches_production_rich", cl_build is not None and cl_build in prod_cc,
                  local=cl_build, production_rich=prod_cc)
    if not (ok_a1 and ok_a2 and ok_a3):
        return finish(R, out, out_root, False, "stage A refused: production headers / manifest / cl line")

    # ---------------------------------------------------------------- B0: byte-exact reproduction (read-only compile)
    gather_md5 = md5f(src_dir / "gather.cpp")
    R["repro"] = {"attempts": []}
    ok_b0 = False
    if a.no_repro:
        R["repro"]["skipped"] = "--no-repro"
    else:
        cands = [Path(d) for d in a.repro_dir] if a.repro_dir else find_repro_dirs(engine, gather_md5, prod_md5)
        R["repro"]["candidates"] = [str(d) for d in cands]
        for i, d in enumerate(cands[:MAX_REPRO_CANDIDATES]):
            att = {"dir": str(d)}
            R["repro"]["attempts"].append(att)
            if d.resolve() == src_dir.resolve():
                att["skipped"] = "that is the production engine dir itself"
                continue
            bad = [s["rel"] for s in sources
                   if not (d / os.path.relpath(engine / s["rel"], src_dir)).exists()
                   or md5f(d / os.path.relpath(engine / s["rel"], src_dir)) != s["md5"]]
            if bad:
                att["skipped"] = "sources differ from the engine's: %s" % bad[:8]
                continue
            before = dir_listing(d)
            try:
                exe, warn, argv = compile_host(d, cl_full, env, out / ("repro%d" % i), out / ("repro%d" % i) / "cl.log")
            except RuntimeError as e:
                att["error"] = str(e)[:1500]
                continue
            after = dir_listing(d)
            att["dir_unchanged"] = before == after
            att["argv"] = argv
            if before != after:
                R["warnings"].append("repro compile changed the listing of %s: %s" % (
                    d, sorted(set(before.items()) ^ set(after.items()))[:10]))
            data = exe.read_bytes()
            att["exe"] = dict(path=str(exe), **pe_summary(data))
            att["vs_production"] = compare(prod, data, "repro")
            if att["vs_production"]["normalized_identical"] and att["dir_unchanged"]:
                ok_b0 = True
                R["repro"]["identical_from"] = str(d)
                break
    check("B0_repro_byte_identical_mod_link_stamps", ok_b0,
          attempts=[{k: v for k, v in t.items() if k in ("dir", "skipped", "error", "dir_unchanged")} for t in R["repro"]["attempts"]],
          **({"vs_production": {k: v for k, v in R["repro"]["attempts"][-1]["vs_production"].items()
                                if k in ("normalized_identical", "diff_bytes", "diff_bytes_by_section", "lambda_names_equal")}}
             if R["repro"]["attempts"] and "vs_production" in R["repro"]["attempts"][-1] else {}))

    # ---------------------------------------------------------------- B1: the md5-verified COPY, built in work\
    work = out / "work"
    for s in sources:
        dst = work / s["rel"]
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(engine / s["rel"], dst)
        if md5f(dst) != s["md5"]:
            raise RuntimeError("copy of %s does not match the engine" % s["rel"])
    wdir = work / os.path.relpath(src_dir, engine)
    unmod_exe, unmod_warn, unmod_argv = compile_host(wdir, cl_full, env, out / "unmod", out / "unmod" / "cl.log")
    unmod = unmod_exe.read_bytes()
    R["unmod"] = dict(path=str(unmod_exe), argv=unmod_argv, warnings=unmod_warn, **pe_summary(unmod))
    cu = compare(prod, unmod, "unmod-copy vs production")
    R["unmod"]["vs_production"] = cu
    check("B1a_copy_same_size_and_kernels", cu.get("size_delta", 0) == 0 and cu["dxbc_sequence_identical"],
          size_delta=cu.get("size_delta", 0), dxbc=cu["dxbc_count"])
    check("B1b_copy_strings_equal_lambda_masked", not cu["strings_missing"] and not cu["strings_new"],
          missing=cu["strings_missing"][:10], new=cu["strings_new"][:10])
    check("B1c_copy_binary_identical", cu["normalized_identical"], required=False,
          note="expected to differ ONLY through the path-derived lambda names (and the code placement they change)"
               " unless built from production's own build dir -- B0 is the byte-exact proof",
          lambda_names=cu["lambda_names"], diff_bytes=cu.get("diff_bytes"),
          diff_bytes_by_section=cu.get("diff_bytes_by_section"))

    # ---------------------------------------------------------------- C: guarded host, SAME work path as B1
    pristine = (src_dir / "gather.cpp").read_bytes()
    patched, edit_report = n3_guard_edits.apply(pristine)
    (wdir / "gather.cpp").rename(wdir / "gather.cpp.orig")   # the md5-verified copy stays beside it
    (wdir / "gather.cpp").write_bytes(patched)
    shutil.copy2(HERE / n3_guard_edits.GUARD_HEADER, wdir / n3_guard_edits.GUARD_HEADER)
    diff_txt = n3_guard_edits.unified_diff(pristine, patched)
    (out / "N3_gather.cpp.diff").write_text(diff_txt, encoding="utf-8")
    R["guard_edits"] = {"report": edit_report, "gather_cpp_md5_before": md5b(pristine),
                        "gather_cpp_md5_after": md5b(patched), "guard_header_md5": md5f(wdir / n3_guard_edits.GUARD_HEADER),
                        "edits_module_md5": md5f(HERE / "n3_guard_edits.py"), "diff": str(out / "N3_gather.cpp.diff")}
    guard_exe, guard_warn, guard_argv = compile_host(wdir, cl_full, env, out / "guard", out / "guard" / "cl.log")
    guard = guard_exe.read_bytes()
    R["guard"] = dict(path=str(guard_exe), argv=guard_argv, warnings=guard_warn, **pe_summary(guard))
    cg = compare(prod, guard, "guard vs production")
    cgu = compare(unmod, guard, "guard vs unmod-copy")
    R["guard"]["vs_production"] = cg
    R["guard"]["vs_unmod_copy"] = cgu
    check("C1_guard_dxbc_sequence_identical", cg["dxbc_sequence_identical"], count=cg["dxbc_count"])
    check("C2_guard_keeps_every_production_string", not cg["strings_missing"], missing=cg["strings_missing"][:10])
    check("C2b_guard_same_lambda_names_as_unmod_copy", cgu["lambda_names_equal"], required=False,
          note="informational: the hash also changes with later code in the enclosing function (bisect 2026-10-14: E1 alone"
               " keeps the names, E2-E5 change them), so the guarded build cannot share the unmodified build's names",
          names=cgu["lambda_names"])
    exps = {e["name"]: e for e in R["guard"]["exports"]}
    check("C3_guard_exports", set(exps) == {"NvOptimusEnablement", "AmdPowerXpressRequestHighPerformance"}
          and all(e["value_u32"] == 1 and e["section"] == ".data" for e in exps.values()), exports=R["guard"]["exports"])
    needles = gpu_bake_needles(engine)
    miss_n = [n for n in needles if n.encode() not in guard]
    check("C4_guard_has_gpu_bake_markers", bool(needles) and not miss_n, needles=needles, missing=miss_n)
    checks["C4_guard_has_gpu_bake_markers"]["needles"] = needles
    fatal = [s for s in cgu["strings_new"] if "PALLOC OVERFLOW" in s]
    other_new = [s for s in cgu["strings_new"] if "PALLOC OVERFLOW" not in s]
    check("C5_guard_new_strings_are_the_guard_only", len(fatal) >= 2 and not cgu["strings_missing"],
          fatal=fatal, other_new=other_new, missing_vs_unmod=cgu["strings_missing"])
    checks["C5_guard_new_strings_are_the_guard_only"].update(fatal=fatal, other_new=other_new)
    neww = sorted(set(norm_warnings(guard_warn)) - set(norm_warnings(unmod_warn)))
    check("C6_guard_no_new_compiler_warnings", not neww, new=neww)
    # the production cl line has no /Brepro, so the raw md5 changes with every link (2 stamps); the stamp-zeroed md5 is the
    # build's stable identity (same out-root path -> same lambda names -> same bytes)
    if prev and (Path(prev) / "guard" / "fullgpu.exe").exists():
        pg = (Path(prev) / "guard" / "fullgpu.exe").read_bytes()
        check("C7_guard_rebuild_reproducible", md5b(pe_util.normalized(pg)) == R["guard"]["normalized_md5"], required=False,
              previous=prev, previous_normalized_md5=md5b(pe_util.normalized(pg)), now=R["guard"]["normalized_md5"])

    # ---------------------------------------------------------------- D: unit test of the guard header
    u_dir = out / "unit"
    u_dir.mkdir()
    test_src = PROJECT / "tests" / "fullgpu_guard" / "n3_palloc_guard_test.cpp"
    shutil.copy2(test_src, u_dir / test_src.name)
    flags = [t for t in cl_full.split()[1:] if t.startswith("/") and not t.lower().startswith(("/fe", "/link"))]
    rc, so, se = run(["cl.exe"] + flags + ["/I" + str(HERE), test_src.name, "/Fe:n3_unit.exe"], env=env, cwd=str(u_dir))
    (u_dir / "cl.log").write_text(so + se, encoding="utf-8")
    if rc == 0 and (u_dir / "n3_unit.exe").exists():
        rc2, so2, se2 = run([str(u_dir / "n3_unit.exe")], env=child_env(), cwd=str(u_dir), timeout=60)
        (u_dir / "unit_stdout.txt").write_text(so2, encoding="utf-8")
        R["unit_test"] = {"flags": flags, "exit": rc2, "stdout": so2.replace("\r\n", "\n").strip().splitlines()}
        check("D1_guard_unit_test", rc2 == 0 and "N3 GUARD UNIT: PASS" in so2, exit=rc2,
              out=so2.strip().splitlines()[-1:] if so2.strip() else [])
    else:
        check("D1_guard_unit_test", False, compile=(so + se)[-1500:])

    # ---------------------------------------------------------------- E: behaviour probes (GPU-free paths only)
    exes = [("production", prod_exe), ("unmod", unmod_exe), ("guard", guard_exe)]
    probes = [("missing_inputs", {}, None, (), 1)]
    if not a.skip_plan:
        farms = a.plan_farm or [str(engine.parent / f) for f in DEFAULT_PLAN_FARMS]
        cache = out_root / "_plan_inputs"
        cache.mkdir(exist_ok=True)
        plan_env = {"GPULM_FULLGPU": "1", "GPULM_PART_PLAN_ONLY": "1", "GPULM_SEAM_LISTS": "1"}
        for k, f in enumerate(farms):
            d, rec = cache_plan_inputs(f, cache)
            nm = Path(f).name
            if k == 0:
                probes += [("no_soup_" + nm, {"GPULM_FULLGPU": "1"}, d, ("texelgen_soup.bin",), 9),
                           ("bad_part_range_" + nm, {"GPULM_FULLGPU": "1", "GPULM_PART_RANGE": "9:1"}, d, (), 9),
                           ("part_range_past_end_" + nm, {"GPULM_FULLGPU": "1", "GPULM_PART_RANGE": "0:4000000000"}, d, (), 9)]
            probes.append(("plan_" + nm, plan_env, d, (), 0))
    R["probes"] = {}
    for pname, envx, inp, excl, want in probes:
        res = {lbl: run_probe(exe, out / "probes" / pname / lbl, envx, inp, excl) for lbl, exe in exes}
        exits = {lbl: r["exit"] for lbl, r in res.items()}
        same = len(set(r["stdout_norm_md5"] for r in res.values())) == 1 and len(set(exits.values())) == 1 and \
            len(set(json.dumps(r["outputs"], sort_keys=True) for r in res.values())) == 1
        entry = {"env": envx, "inputs": str(inp) if inp else None, "excluded": list(excl), "expected_exit": want,
                 "results": res, "all_identical": same}
        if inp and want == 0:
            fp = Path(inp) / "part_plan.txt"
            got = res["production"]["outputs"].get("part_plan.txt")
            entry["farm_recorded_part_plan_md5"] = md5f(fp) if fp.exists() else None
            entry["matches_farm_recorded_plan"] = bool(got) and got == entry["farm_recorded_part_plan_md5"]
        R["probes"][pname] = entry
        check("E_" + pname, same and all(e == want for e in exits.values()) and
              not any(r["device_line"] for r in res.values()),
              exits=exits, identical=same, last=res["production"]["last_lines"])

    ok_all = all(c["ok"] for c in checks.values() if c["required"])
    status = ("PASS: production rebuilt byte-identical (link stamps aside) from its own build dir; the md5-verified copy differs "
              "only by path-derived lambda names; the guarded build keeps every production kernel and string, adds only the "
              "palloc FATAL + the 2 exports, and matches production on every GPU-free probe. GPU identity proof pending (T3a)."
              ) if ok_all else "FAIL: see checks"
    return finish(R, out, out_root, ok_all, status)


def finish(R, out, out_root, ok, status):
    R["ok"] = bool(ok)
    R["status"] = status
    R["finished"] = now()
    (out / "BUILD_MANIFEST.json").write_text(json.dumps(R, indent=1, default=str), encoding="utf-8")
    if ok:
        latest = out_root / "LATEST.json"
        backup_aside(latest)
        latest.write_text(json.dumps({
            "tag": R["tag"], "ok": True, "status": status, "built": R["finished"], "engine_dir": R["engine_dir"],
            "production_fullgpu_md5": R["production"]["md5"],
            "guard_fullgpu": os.path.relpath(R["guard"]["path"], out_root), "guard_md5": R["guard"]["md5"],
            "guard_normalized_md5": R["guard"]["normalized_md5"], "guard_size": R["guard"]["size"],
            "unmod_fullgpu": os.path.relpath(R["unmod"]["path"], out_root), "unmod_md5": R["unmod"]["md5"],
            "unmod_normalized_md5": R["unmod"]["normalized_md5"],
            "md5_note": "raw md5s change on every rebuild (the production cl line links 2 time stamps); *_normalized_md5 (stamps "
                        "zeroed, pe_util.normalized) is the stable identity. T3a must pin the exact guard exe it tested.",
            "repro_identical_from": R.get("repro", {}).get("identical_from"),
            "cl_line": R["cl_line"], "toolchain": R.get("toolchain"),
            "gpu_identity_proof": "pending (gate T3a: keyed-identical Super bakes on cyclotron, headlong, mainmenu)",
            "manifest": os.path.relpath(out / "BUILD_MANIFEST.json", out_root)}, indent=1), encoding="utf-8")
    print("\n%s\nmanifest: %s" % (status, out / "BUILD_MANIFEST.json"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
