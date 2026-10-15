"""patch_series.py -- apply a CHAIN of strict patches to copies of production files, then fill build-time tokens. Stdlib only.

The hunk engine is tools\\strict_patch.py (read_patch / apply_bytes: base-md5 pinned, every hunk at exactly its line, no fuzz, no
offset, result-md5 pinned). This driver adds what one patch per file cannot express:
  * several patches on ONE file, in order (gpu_bake.ps1: P1 -> P3 -> P2): each patch's base-md5 must be the previous one's
    result-md5, the first one's the production file's md5, and every patch must name the target as its '# source:';
  * build-time TOKENS: the final text may carry %%H2LM:NAME%% placeholders (the payload osoy_launch.exe md5 is known only when
    S1.3 has built it). Exactly the target's declared tokens must be present; each needs --set NAME=VALUE matching the series regex;
    none may survive. The pre-token md5 is the last patch's result-md5; the report records the final md5 too.
  * UPSTREAMED production (U1, 2026-10-15, with the user's OK): a target may declare "upstream": {"md5": <production md5 after the
    upstream>, "base_file": <the pinned pre-upstream base, relative to the series file>}. When production has that md5, the chain is
    applied to the pinned base (its md5 must be base_md5) and the result -- tokens filled -- must be BYTE-IDENTICAL to production:
    the series becomes a verified no-op and the output is production verbatim. The token values production carries are read back
    from it (check mode uses them); apply mode refuses when a --set value differs from what production pins (e.g. a rebuilt
    osoy_launch.exe: re-upstream production first). Production at base_md5 still takes the patches as before.

    py -I patch_series.py check --series S.json --src-root GLW
    py -I patch_series.py apply --series S.json --src-root GLW --out-root DIR --set NAME=VALUE [...] [--report R.json]
                                [--stages-out DIR] [--overwrite]
      GLW = the production "GPU Lightmap Work" folder (read only). DIR must not be inside it. An existing output is replaced only
      when byte-identical or with --overwrite (outputs are build products under build\\).

Series JSON ("h2lm-patch-series/1"):
  {"format": "h2lm-patch-series/1",
   "tokens": {"NAME": "<full-match regex>"},
   "targets": [{"path": "engine_v373/gpu_bake.ps1",           # relative to GLW and to DIR, forward slashes
                "src": "<optional: the production path (relative to GLW) when it is not 'path'>",
                "base_md5": "<production md5>",
                "patches": ["P1_....patch", "P3_....patch"],    # relative to the series file, applied in this order
                "tokens": ["NAME"],
                "upstream": {"md5": "<production md5 after U1>", "base_file": "../base/<path>"}}]}   # optional (U1)
Exit codes: 0 ok, 2 refused (nothing written), 1 usage."""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import sys

sys.dont_write_bytecode = True
FORMAT = "h2lm-patch-series/1"
TOKEN_RE = re.compile(rb"%%H2LM:([A-Z0-9_]+)%%")
HERE = os.path.dirname(os.path.abspath(__file__))


def _load_engine():
    p = os.path.join(HERE, "strict_patch.py")
    spec = importlib.util.spec_from_file_location("h2lm_strict_patch", p)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m, hashlib.md5(open(p, "rb").read()).hexdigest()


SP, SP_MD5 = _load_engine()


class Refused(Exception):
    pass


def md5(b):
    return hashlib.md5(b).hexdigest()


def inside(child, parent):
    c = os.path.normcase(os.path.realpath(child))
    p = os.path.normcase(os.path.realpath(parent))
    return c == p or c.startswith(p.rstrip("\\/") + os.sep)


def load_series(path):
    try:
        s = json.load(open(path, "r", encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise Refused("series %s: %s" % (path, e))
    if s.get("format") != FORMAT:
        raise Refused("series %s: format %r, expected %r" % (path, s.get("format"), FORMAT))
    if not s.get("targets"):
        raise Refused("series %s: no targets" % path)
    for t in s["targets"]:
        for k in ("path", "base_md5", "patches"):
            if k not in t:
                raise Refused("series %s: a target lacks %r" % (path, k))
        if not t["patches"]:
            raise Refused("series %s: %s has no patch" % (path, t["path"]))
        for n in t.get("tokens", []):
            if n not in s.get("tokens", {}):
                raise Refused("series %s: %s: token %s has no value regex" % (path, t["path"], n))
    return s


def _token_values(series, cur, prod, rel):
    """U1: read back the token values an upstreamed production file carries: production must be `cur` (the pre-token chain result)
    with every %%H2LM:NAME%% replaced by one value matching the series regex (the same value for every occurrence of a name)"""
    parts = TOKEN_RE.split(cur)          # [literal, NAME, literal, NAME, ..., literal]
    pat, names = b"", []
    for i, p in enumerate(parts):
        if i % 2 == 0:
            pat += re.escape(p)
        else:
            n = p.decode()
            rx = series["tokens"][n]
            rx = rx[1:] if rx.startswith("^") else rx
            rx = rx[:-1] if rx.endswith("$") else rx
            pat += b"(" + rx.encode("ascii") + b")"
            names.append(n)
    m = re.fullmatch(pat, prod, re.S)
    if not m:
        raise Refused("%s: production has the upstream md5 but is not the chain result of the pinned base with any token value "
                      "(series / patches edited after the upstream?)" % rel)
    vals = {}
    for n, v in zip(names, m.groups()):
        v = v.decode("ascii")
        if vals.setdefault(n, v) != v:
            raise Refused("%s: production carries two different values for token %s (%s, %s)" % (rel, n, vals[n], v))
    return vals


def build_target(series, sdir, t, src_root, sets, stages_out=None, check_only=False):
    """-> (final bytes, record); raises Refused"""
    rel = t["path"]
    # V1 (2026-10-14): an optional "src" reads the production base from another path than the output path (the payload keeps
    # INST\_research\ao\dev_AO as the AO engine's dir name while production's current AO engine is _research\ao\dev_AO2)
    rel_src = t.get("src", rel)
    src = os.path.join(src_root, *rel_src.split("/"))
    try:
        data = open(src, "rb").read()
    except OSError as e:
        raise Refused("%s: cannot read the production file %s: %s" % (rel, rel_src, e))
    up = t.get("upstream")
    prod = None                  # U1: production bytes when production already holds this series (verified below)
    if md5(data) != t["base_md5"]:
        if not (up and md5(data) == up.get("md5")):
            raise Refused("%s: production %s md5 %s is neither the reviewed base %s%s -- production drifted: re-review and re-make the patches"
                          % (rel, rel_src, md5(data), t["base_md5"], (" nor its upstream %s" % up.get("md5")) if up else ""))
        bf = os.path.normpath(os.path.join(sdir, *up["base_file"].split("/")))
        try:
            base = open(bf, "rb").read()
        except OSError as e:
            raise Refused("%s: production is the upstream %s but the pinned base %s is unreadable: %s" % (rel, up["md5"], bf, e))
        if md5(base) != t["base_md5"]:
            raise Refused("%s: the pinned base %s md5 %s is not base_md5 %s" % (rel, bf, md5(base), t["base_md5"]))
        prod, data = data, base
    rec = {"path": rel, "base_md5": md5(data), "patches": []}
    if prod is not None:
        rec["upstreamed"] = True
        rec["production_md5"] = md5(prod)
        rec["base_file"] = os.path.relpath(bf, sdir).replace("\\", "/")
    if rel_src != rel:
        rec["src"] = rel_src
    cur = data
    for i, pf in enumerate(t["patches"], 1):
        pp = os.path.join(sdir, pf)
        try:
            praw = open(pp, "rb").read()
            P = SP.read_patch(praw)
        except OSError as e:
            raise Refused("%s: cannot read %s: %s" % (rel, pf, e))
        except SP.PatchError as e:
            raise Refused("%s: %s: %s" % (rel, pf, e))
        if P["source"] != rel:
            raise Refused("%s: %s names '# source: %s'" % (rel, pf, P["source"]))
        if P["base-md5"] != md5(cur):
            raise Refused("%s: %s is pinned to base %s but the chain is at %s (patch order / re-review)" % (rel, pf, P["base-md5"], md5(cur)))
        try:
            cur = SP.apply_bytes(P, cur)
        except SP.PatchError as e:
            raise Refused("%s: %s: %s" % (rel, pf, e))
        rec["patches"].append({"file": pf, "patch_md5": md5(praw), "title": P.get("title", ""), "hunks": len(P["hunks"]),
                               "result_md5": md5(cur)})
        if stages_out:
            os.makedirs(stages_out, exist_ok=True)
            open(os.path.join(stages_out, "%s.%d_%s" % (rel.replace("/", "__"), i, os.path.splitext(pf)[0])), "wb").write(cur)
    rec["pre_token_md5"] = md5(cur)
    found = sorted(set(m.group(1).decode() for m in TOKEN_RE.finditer(cur)))
    declared = sorted(t.get("tokens", []))
    if found != declared:
        raise Refused("%s: the patched text carries tokens %s, the series declares %s" % (rel, found, declared))
    if prod is not None:
        pv = _token_values(series, cur, prod, rel)
        rec["production_tokens"] = pv
        if check_only:
            sets = dict(sets, **pv)
        else:
            bad = ["%s: production pins %s, this build sets %s" % (n, pv[n], sets[n]) for n in declared if n in sets and sets[n] != pv[n]]
            if bad:
                raise Refused("%s: production holds the upstreamed series with other token values (%s) -- re-upstream production "
                              "for this build (e.g. a rebuilt payload osoy_launch.exe) or build with production's values" % (rel, "; ".join(bad)))
    for n in declared:
        if n not in sets:
            raise Refused("%s: token %s needs --set %s=<value>" % (rel, n, n))
        if not re.fullmatch(series["tokens"][n], sets[n]):
            raise Refused("%s: --set %s=%r does not match %s" % (rel, n, sets[n], series["tokens"][n]))
        cur = cur.replace(b"%%H2LM:" + n.encode() + b"%%", sets[n].encode("ascii"))
    if b"%%H2LM:" in cur:
        raise Refused("%s: a %%%%H2LM: token survived the substitution" % rel)
    rec["tokens"] = {n: sets[n] for n in declared}
    rec["out_md5"] = md5(cur)
    if prod is not None and cur != prod:      # cannot happen after _token_values; kept as the byte-level proof of the no-op
        raise Refused("%s: production (upstream %s) != the chain result %s of the pinned base" % (rel, md5(prod), md5(cur)))
    return cur, rec


def run(a, write):
    series = load_series(a.series)
    sdir = os.path.dirname(os.path.abspath(a.series))
    sets = {}
    for kv in (a.set or []):
        if "=" not in kv:
            raise Refused("--set %r: expected NAME=VALUE" % kv)
        k, v = kv.split("=", 1)
        if k in sets:
            raise Refused("--set %s given twice" % k)
        sets[k] = v
    declared = set(n for t in series["targets"] for n in t.get("tokens", []))
    if set(sets) - declared:
        raise Refused("--set %s: no target declares it" % ", ".join(sorted(set(sets) - declared)))
    if write and inside(a.out_root, a.src_root):
        raise Refused("--out-root %s is inside --src-root %s -- production is never written" % (a.out_root, a.src_root))
    if not write:
        # check: prove the chain; token VALUES are not needed, so each declared token gets a value matching its regex if one is easy
        for n in declared:
            sets.setdefault(n, next((c for c in ("0" * 32, "0" * 8, "0") if re.fullmatch(series["tokens"][n], c)), ""))
    built = [(t,) + build_target(series, sdir, t, a.src_root, sets, a.stages_out if write else None, check_only=not write)
             for t in series["targets"]]
    if write:
        for t, data, rec in built:
            out = os.path.join(a.out_root, *t["path"].split("/"))
            if os.path.exists(out) and open(out, "rb").read() != data and not a.overwrite:
                raise Refused("%s exists and differs (pass --overwrite to replace a previous build output)" % out)
        for t, data, rec in built:
            out = os.path.join(a.out_root, *t["path"].split("/"))
            os.makedirs(os.path.dirname(out), exist_ok=True)
            tmp = out + ".tmp_patch_series"
            with open(tmp, "wb") as f:
                f.write(data)
                f.flush()
                os.fsync(f.fileno())
            if md5(open(tmp, "rb").read()) != rec["out_md5"]:
                raise Refused("%s: the written file does not verify" % out)
            os.replace(tmp, out)
            rec["out"] = out
    report = {"format": "h2lm-patch-series-report/1", "mode": "apply" if write else "check",
              "series": os.path.abspath(a.series), "series_md5": md5(open(a.series, "rb").read()),
              "engine": os.path.join(HERE, "strict_patch.py"), "engine_md5": SP_MD5,
              "targets": [rec for _, _, rec in built]}
    if write and a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        json.dump(report, open(a.report, "w", encoding="utf-8"), indent=1)
    for _, _, rec in built:
        print("%s %s: %s -> %s -> %s (%s)%s" % ("APPLIED" if write else "CHECKED", rec["path"], rec["base_md5"][:8], rec["pre_token_md5"][:8],
                                              rec["out_md5"][:8] if (write or rec.get("upstreamed")) else "tokens not filled",
                                              " + ".join("%s/%dh" % (os.path.splitext(p["file"])[0], p["hunks"]) for p in rec["patches"]),
                                              ("  UPSTREAMED: production %s == the chain of the pinned base %s (verified no-op; tokens %s)"
                                               % (rec["production_md5"][:8], rec["base_file"], rec["production_tokens"])) if rec.get("upstreamed") else ""))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)
    for name in ("check", "apply"):
        p = sp.add_parser(name)
        p.add_argument("--series", required=True)
        p.add_argument("--src-root", required=True)
        if name == "apply":
            p.add_argument("--out-root", required=True)
            p.add_argument("--set", action="append")
            p.add_argument("--report")
            p.add_argument("--stages-out")
            p.add_argument("--overwrite", action="store_true")
    a = ap.parse_args(argv)
    if a.cmd == "check":
        a.set, a.out_root, a.report, a.stages_out, a.overwrite = [], None, None, None, False
    try:
        return run(a, a.cmd == "apply")
    except Refused as e:
        print("patch_series: REFUSED: %s" % e, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
