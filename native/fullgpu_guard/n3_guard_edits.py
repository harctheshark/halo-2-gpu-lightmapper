"""n3_guard_edits.py -- the N3 edits to a COPY of gather.cpp (H2 GPU Lightmapper standalone, step S1.7).

The edits are line-anchored, never offset- or fuzz-based:
  * every anchor is a block of CONSECUTIVE WHOLE LINES (compared exactly, EOL excluded) that must occur EXACTLY ONCE;
  * "replace" swaps exactly one line of the block (old text asserted) for one new line;
  * "insert" adds new lines before anchor line `at` (at == len(anchor) inserts after the block);
  * nothing is ever deleted, so an anchor can never eat a span (memory: patch-anchor-span-delete);
  * every new line takes the EOL of its anchor line (gather.cpp is CRLF throughout).
apply() refuses (raises) on any anchor that is missing or ambiguous, then proves the edit is reversible: dropping the
inserted lines and restoring the replaced ones reproduces the original bytes exactly.

The production engine is about to be promoted (closest-hit tie rule). If a promoted gather.cpp moves one of these
anchors, apply() refuses and names the edit; re-anchor it here, never loosen the matcher.
"""
import difflib

GUARD_HEADER = "n3_palloc_guard.h"

I36 = " " * 36
I40 = " " * 40
I44 = " " * 44
I48 = " " * 48
I32 = " " * 32

EDITS = [
    {   # (a)+(b) the guard header: the predicates, the FATAL printers and the two hybrid-GPU exports
        "id": "E1_include_guard_header",
        "anchor": ["#define NOMINMAX", "#include <windows.h>", "#include <d3d11.h>"],
        "op": "insert", "at": 3,
        "lines": ['#include "n3_palloc_guard.h"   // N3 (H2 GPU Lightmapper standalone): palloc uint32 overflow FATAL'
                  ' + NvOptimusEnablement / AmdPowerXpressRequestHighPerformance exports'],
    },
    {   # (a) surface lights: the per-light candidate count
        "id": "E2_surface_count",
        "anchor": [I36 + "for(size_t j=0;j<mgv.size();j++) if(mgv[j]==ko && ixv[j]==kt){",
                   I40 + "found=true; if(pwv[j]>0.0f) cnt=(uint32_t)((float)alv[j]/pwv[j]); break; }",
                   I36 + "if(!found) ++unmatched;",
                   I36 + "palloc[i]=(uint32_t)run; run+=cnt; }"],
        "op": "replace", "target": 1,
        "new": I40 + "found=true; if(pwv[j]>0.0f){ const float n3q=(float)alv[j]/pwv[j];"
                     " if(!n3_cnt_fits(n3q)){ n3_palloc_fatal_light(\"surface\",i,n3q); fclose(lf); return 9; }"
                     " cnt=(uint32_t)n3q; } break; }   // N3 guard: FATAL instead of a uint32 wrap",
    },
    {   # (a) analytic lights: the per-light candidate count
        "id": "E3_analytic_count",
        "anchor": [I36 + "for(size_t j=0;j<mgv.size();j++) if(mgv[j]==ap.keyOrd && ixv[j]==ap.keyTag){",
                   I40 + "found=true; if(pwv[j]>0.0f) cnt=(uint32_t)((float)alv[j]/pwv[j]); break; }"
                         "   // v208 alv IS the count -- see the surface join above",
                   I36 + "if(!found) ++unmatchedAna;",
                   I36 + "palloc.push_back((uint32_t)run); run+=cnt;"],
        "op": "replace", "target": 1,
        "new": I40 + "found=true; if(pwv[j]>0.0f){ const float n3q=(float)alv[j]/pwv[j];"
                     " if(!n3_cnt_fits(n3q)){ n3_palloc_fatal_light(\"analytic\",i,n3q); fclose(lf); return 9; }"
                     " cnt=(uint32_t)n3q; } break; }   // v208 alv IS the count -- see the surface join above"
                     " (N3 guard: FATAL instead of a uint32 wrap)",
    },
    {   # (a) sky emitters: the per-emitter candidate count
        "id": "E4_sky_count",
        "anchor": [I44 + "for(size_t j=0;j<mgv.size();j++) if(mgv[j]==ko && ixv[j]==kt){",
                   I48 + "fnd=true;",
                   I48 + "if(pwv[j]>0.0f) c1=(uint32_t)((float)alv[j]/pwv[j]); break; }  // v210 attempts",
                   I44 + "if(!fnd) ++skyUnmatched;"],
        "op": "replace", "target": 2,
        "new": I48 + "if(pwv[j]>0.0f){ const float n3q=(float)alv[j]/pwv[j];"
                     " if(!n3_cnt_fits(n3q)){ n3_palloc_fatal_light(\"sky\",e,n3q); fclose(lf); return 9; }"
                     " c1=(uint32_t)n3q; } break; }  // v210 attempts (N3 guard: FATAL instead of a uint32 wrap)",
    },
    {   # (a) the running total: every palloc offset is (uint32_t)run, and run only grows
        "id": "E5_total",
        "anchor": [I32 + "if(!(cullR>0.0f)) cullR=1.0f;",
                   I32 + "lpOk=(run>0);"],
        "op": "insert", "at": 1,
        "lines": [I32 + "if(!n3_run_fits(run)){ n3_palloc_fatal_total((unsigned long long)run,nCastLights); fclose(lf); return 9; }"
                        "   // N3 guard: palloc holds uint32 offsets"],
    },
]


def _split(data):
    """bytes -> [(text, eol)] with eol in (b'\\r\\n', b'\\n', b'')."""
    out = []
    for raw in data.splitlines(keepends=True):
        if raw.endswith(b"\r\n"):
            out.append((raw[:-2].decode("utf-8"), b"\r\n"))
        elif raw.endswith(b"\n"):
            out.append((raw[:-1].decode("utf-8"), b"\n"))
        else:
            out.append((raw.decode("utf-8"), b""))
    return out


def _join(lines):
    return b"".join(t.encode("utf-8") + e for t, e in lines)


def _find_unique(lines, anchor, eid):
    texts = [t for t, _ in lines]
    n = len(anchor)
    hits = [i for i in range(len(texts) - n + 1) if texts[i:i + n] == anchor]
    if len(hits) != 1:
        raise RuntimeError("N3 edit %s: anchor matched %d times (need exactly 1) -- re-anchor it in n3_guard_edits.py"
                           % (eid, len(hits)))
    return hits[0]


def apply(data, edits=EDITS):
    """Apply the edits to gather.cpp bytes. Returns (new_bytes, report). Raises on any ambiguity."""
    lines = _split(data)
    plan = []
    for ed in edits:
        at = _find_unique(lines, ed["anchor"], ed["id"])
        plan.append((at, ed))
    # apply bottom-up so earlier line numbers stay valid; anchors may not overlap
    spans = sorted((at, at + len(ed["anchor"]), ed["id"]) for at, ed in plan)
    for (a0, a1, i0), (b0, b1, i1) in zip(spans, spans[1:]):
        if b0 < a1:
            raise RuntimeError("N3 edits %s and %s have overlapping anchors" % (i0, i1))
    report = []
    for at, ed in sorted(plan, key=lambda p: -p[0]):
        if ed["op"] == "replace":
            k = at + ed["target"]
            old, eol = lines[k]
            assert old == ed["anchor"][ed["target"]]
            lines[k] = (ed["new"], eol)
            report.append({"id": ed["id"], "op": "replace", "line": k + 1})
        elif ed["op"] == "insert":
            ref = at + min(ed["at"], len(ed["anchor"]) - 1)
            eol = lines[ref][1] or b"\r\n"
            pos = at + ed["at"]
            for j, t in enumerate(ed["lines"]):
                lines.insert(pos + j, (t, eol))
            report.append({"id": ed["id"], "op": "insert", "line": pos + 1, "count": len(ed["lines"])})
        else:
            raise RuntimeError("unknown op %r" % ed["op"])
    out = _join(lines)
    if revert(out, edits) != data:
        raise RuntimeError("N3 edits are not reversible -- refusing")
    report.sort(key=lambda r: r["line"])
    return out, report


def revert(data, edits=EDITS):
    """Undo apply(): remove inserted lines, restore replaced ones (each located uniquely by its NEW text)."""
    lines = _split(data)
    for ed in edits:
        texts = [t for t, _ in lines]
        if ed["op"] == "replace":
            hits = [i for i, t in enumerate(texts) if t == ed["new"]]
            if len(hits) != 1:
                raise RuntimeError("revert %s: new line found %d times" % (ed["id"], len(hits)))
            lines[hits[0]] = (ed["anchor"][ed["target"]], lines[hits[0]][1])
        else:
            n = len(ed["lines"])
            hits = [i for i in range(len(texts) - n + 1) if texts[i:i + n] == ed["lines"]]
            if len(hits) != 1:
                raise RuntimeError("revert %s: inserted block found %d times" % (ed["id"], len(hits)))
            del lines[hits[0]:hits[0] + n]
    return _join(lines)


def unified_diff(old, new, name="gather.cpp"):
    a = old.decode("utf-8").splitlines(keepends=False)
    b = new.decode("utf-8").splitlines(keepends=False)
    return "\n".join(difflib.unified_diff(a, b, "a/" + name, "b/" + name, lineterm="", n=2)) + "\n"
