"""pe_util.py -- read-only PE32+ inspection for the N3 fullgpu host-only rebuild (H2 GPU Lightmapper, step S1.7).

Pure functions over bytes; nothing here writes a file or runs a process.

  parse(data)            -> dict: machine, timestamp, checksum, sections, rich (compiler build ids), debug entries, exports
  normalized(data)       -> bytes with every link-time stamp zeroed (COFF TimeDateStamp, optional-header CheckSum,
                            each debug-directory TimeDateStamp, the export directory TimeDateStamp)
  dxbc_blobs(data)       -> [(file_offset, size, md5)] for every embedded DXBC container (the compiled HLSL kernels)
  diff_ranges(a, b)      -> [(start, end)] half-open byte ranges where two equal-length buffers differ
"""
import hashlib
import struct

DEBUG_TYPES = {1: "COFF", 2: "CODEVIEW", 9: "BORLAND", 10: "RESERVED10", 12: "VC_FEATURE", 13: "POGO",
               14: "ILTCG", 16: "REPRO", 20: "EX_DLLCHARACTERISTICS"}


def _u16(d, o):
    return struct.unpack_from("<H", d, o)[0]


def _u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def _headers(data):
    if data[:2] != b"MZ":
        raise ValueError("not an MZ image")
    pe = _u32(data, 0x3C)
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("no PE signature")
    coff = pe + 4
    nsec = _u16(data, coff + 2)
    opt_size = _u16(data, coff + 16)
    opt = coff + 20
    magic = _u16(data, opt)
    if magic != 0x20B:
        raise ValueError("not PE32+ (magic %04X)" % magic)
    secs = []
    so = opt + opt_size
    for i in range(nsec):
        b = so + i * 40
        name = data[b:b + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, rptr = struct.unpack_from("<IIII", data, b + 8)
        secs.append({"name": name, "va": va, "vsize": vsize, "raw_ptr": rptr, "raw_size": rsize})
    return pe, coff, opt, secs


def _rva_to_off(secs, rva):
    for s in secs:
        if s["va"] <= rva < s["va"] + max(s["vsize"], s["raw_size"]):
            return s["raw_ptr"] + (rva - s["va"])
    return None


def _datadir(data, opt, idx):
    # PE32+: NumberOfRvaAndSizes at opt+108, directories from opt+112
    n = _u32(data, opt + 108)
    if idx >= n:
        return 0, 0
    return struct.unpack_from("<II", data, opt + 112 + idx * 8)


def rich(data):
    """Decoded Rich header: [(prodid, build, count)], or [] when absent."""
    pe = _u32(data, 0x3C)
    end = data.find(b"Rich", 0x80, pe)
    if end < 0:
        return []
    key = _u32(data, end + 4)
    out = []
    o = end - 8
    while o >= 0x80:
        a = _u32(data, o) ^ key
        b = _u32(data, o + 4) ^ key
        if a == 0x536E6144:  # 'DanS'
            break
        out.append((a >> 16, a & 0xFFFF, b))
        o -= 8
    out.reverse()
    # the first three dwords after DanS are zero padding (decoded as (0,0,0)); drop them
    return [e for e in out if e != (0, 0, 0)]


def _stamp_offsets(data):
    """File offsets of every 4-byte link-time stamp: (label, offset)."""
    pe, coff, opt, secs = _headers(data)
    offs = [("coff.TimeDateStamp", coff + 4), ("opt.CheckSum", opt + 64)]
    rva, size = _datadir(data, opt, 6)
    if rva and size:
        base = _rva_to_off(secs, rva)
        for i in range(size // 28):
            offs.append(("debug[%d].TimeDateStamp" % i, base + i * 28 + 4))
    rva, size = _datadir(data, opt, 0)
    if rva and size:
        offs.append(("export.TimeDateStamp", _rva_to_off(secs, rva) + 4))
    return offs


def parse(data):
    pe, coff, opt, secs = _headers(data)
    info = {
        "machine": "%04X" % _u16(data, coff),
        "timestamp": _u32(data, coff + 4),
        "checksum": _u32(data, opt + 64),
        "linker": "%d.%d" % (data[opt + 2], data[opt + 3]),
        "subsystem": _u16(data, opt + 68),
        "sections": secs,
        "rich": [{"prodid": p, "build": b, "count": c} for p, b, c in rich(data)],
        "debug": [],
        "exports": [],
    }
    rva, size = _datadir(data, opt, 6)
    if rva and size:
        base = _rva_to_off(secs, rva)
        for i in range(size // 28):
            ch, ts, ver, typ, dsz, daddr, dptr = struct.unpack_from("<IIIIIII", data, base + i * 28)
            ent = {"type": DEBUG_TYPES.get(typ, str(typ)), "timestamp": ts, "size": dsz}
            if typ == 2 and dptr and data[dptr:dptr + 4] == b"RSDS":
                ent["pdb"] = data[dptr + 24:dptr + dsz].split(b"\0")[0].decode("mbcs", "replace")
            info["debug"].append(ent)
    rva, size = _datadir(data, opt, 0)
    if rva and size:
        eo = _rva_to_off(secs, rva)
        nnames = _u32(data, eo + 24)
        funcs = _rva_to_off(secs, _u32(data, eo + 28))
        names = _rva_to_off(secs, _u32(data, eo + 32))
        ords = _rva_to_off(secs, _u32(data, eo + 36))
        for i in range(nnames):
            no = _rva_to_off(secs, _u32(data, names + i * 4))
            nm = data[no:data.index(b"\0", no)].decode("ascii")
            ordinal = _u16(data, ords + i * 2)
            frva = _u32(data, funcs + ordinal * 4)
            fo = _rva_to_off(secs, frva)
            val = _u32(data, fo) if fo is not None else None
            sec = next((s["name"] for s in secs if s["va"] <= frva < s["va"] + max(s["vsize"], s["raw_size"])), None)
            info["exports"].append({"name": nm, "rva": frva, "section": sec, "value_u32": val})
    return info


def normalized(data):
    b = bytearray(data)
    for _, o in _stamp_offsets(data):
        b[o:o + 4] = b"\0\0\0\0"
    return bytes(b)


def stamp_labels(data):
    return [lbl for lbl, _ in _stamp_offsets(data)]


def dxbc_blobs(data):
    """Every embedded DXBC container: 'DXBC' + 16-byte checksum + u32 1 + u32 total size + u32 chunk count."""
    out = []
    i = data.find(b"DXBC")
    while i >= 0:
        if i + 32 <= len(data) and _u32(data, i + 20) == 1:
            size = _u32(data, i + 24)
            nchunk = _u32(data, i + 28)
            if 32 <= size <= len(data) - i and 0 < nchunk < 64:
                blob = data[i:i + size]
                out.append((i, size, hashlib.md5(blob).hexdigest()))
                i = data.find(b"DXBC", i + size)
                continue
        i = data.find(b"DXBC", i + 4)
    return out


def diff_ranges(a, b):
    if len(a) != len(b):
        raise ValueError("length differs: %d vs %d" % (len(a), len(b)))
    out = []
    i = 0
    n = len(a)
    # fast path: compare 64 KiB blocks, then bytes inside differing blocks
    blk = 65536
    while i < n:
        j = min(n, i + blk)
        if a[i:j] == b[i:j]:
            i = j
            continue
        k = i
        while k < j:
            if a[k] != b[k]:
                s = k
                while k < n and a[k] != b[k]:
                    k += 1
                if out and out[-1][1] == s:
                    out[-1] = (out[-1][0], k)
                else:
                    out.append((s, k))
            else:
                k += 1
        i = max(j, k)
    return out


def section_of(info, off):
    for s in info["sections"]:
        if s["raw_ptr"] <= off < s["raw_ptr"] + s["raw_size"]:
            return s["name"]
    return "headers" if off < (info["sections"][0]["raw_ptr"] if info["sections"] else 0x400) else "overlay"
