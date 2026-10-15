"""u2_tagio_edit.py -- U2 (2026-10-15): author the lmw_tagio.py "short element" change on the pinned production bases.

lmw_tagio's Walk refused any block whose FILE element size differs from its frozen field set's size. The 2004 stock MP scenarios
(backwash, burial_mounds, containment, cyclotron, deltatap, elongation, headlong, triplicate, turf, zanzibar) store their
decorators 'cache blocks' as version 0 with 52-byte elements; tool_fast's field set (and every tool re-save) is 60 bytes.
tool_fast reads such a block by taking the file element as the PREFIX of the in-memory element and leaving the rest zero
(sub_622B11 / sub_622E95 / sub_6231EF, tests\\u2_stock_decorators\\PROGRESS.md). This edit teaches Walk exactly that rule:
  * 0 < file size < field-set size: walk each element with the field set's layout over its file bytes; a field that lies past the
    file element's end is zero (owns nothing in the child stream; a struct there has no header) and is not recorded (no count
    address, tag reference, data or field entry: it has no bytes in the file); W.short[path] = (file size, field-set size);
  * refused as before: file size 0 or > field-set size; NEW refusals (not modelled): a child-owning field that straddles the end
    of a short element, a struct inside a short element whose size differs from the layout's, expect_fields on a field past the
    end of a short element, a leaf splice of a short block;
  * equal sizes (every resaved tag): byte-for-byte the old code path (same checks, same messages, same records).
Exact-count byte replacements (LF); anything else refuses.

  py -I -B u2_tagio_edit.py <base lmw_tagio.py> <out file>"""
import hashlib
import sys

BASES = {'52dc580ef26eaf26e370f03470c14804': 'engine_v373 / dev_AO2 v043-tagwriter',
         'e8aeb703b786b8c995297834abdc73f6': 'GLW-root v043-tagwriter'}

R = []   # (old, new) exact byte replacements, each must occur exactly once

R.append((b"""               string_id/old_string_id -> (value >> 24) bytes

Also here:""", b"""               string_id/old_string_id -> (value >> 24) bytes
  SHORT elements (U2, 2026-10-15): a block's FILE element may be smaller than its field set (the 2004 stock MP scenarios'
             decorators 'cache blocks': version 0, 52 bytes; the field set and every tool re-save: 60).  tool_fast (sub_622B11 /
             sub_622E95 / sub_6231EF, 'BLM!' files) reads the file element as the PREFIX of the in-memory element, the rest zero;
             a field at / past the file element's end is zero and owns nothing in the child stream (a struct there has no
             header).  Walk does the same and lists such blocks in W.short; a file element LARGER than its field set is refused.

Also here:"""))

R.append((b"""    def __init__(self, data, group, D=None, keep_fields=False):
        self.d = data
        self.D = D or defs()
        self.group = group
        self.blocks = []
        self.bpath = {}
        self.cnt_addr = {}
        self.tagrefs = []
        self.datas = []
        self.fields = [] if keep_fields else None
""", b"""    short = None    # U2: block path -> (file element size, field-set size) of every block read SHORT (see the module doc)

    def __init__(self, data, group, D=None, keep_fields=False):
        self.d = data
        self.D = D or defs()
        self.group = group
        self.blocks = []
        self.bpath = {}
        self.cnt_addr = {}
        self.tagrefs = []
        self.datas = []
        self.fields = [] if keep_fields else None
        self.short = {}
"""))

R.append((b"""        fv, size = self.D.fieldset(bd, ver)
        if esz != size:
            raise TagIOError('%s @0x%X: element size %d, field set says %d' % (path, pos, esz, size))
        lay, tot = self.D.layout(fv)
        if tot != esz:
            raise TagIOError('%s: layout total %d != element size %d' % (path, tot, esz))
        b = Blk(path, pos, cnt, esz, pos + 16, self.D.bd[bd]['name'], ver, fv)
        self.blocks.append(b)
        self.bpath[path] = b
        base = pos + 16
        pos = base + cnt * esz
        for k in range(cnt):
            pos = self._children(pos, base + k * esz, fv, '%s[%d]' % (path, k))
        return pos

    def _children(self, pos, eoff, fields_va, path):
        d = self.d
        lay, tot = self.D.layout(fields_va)
        for off, t, nm, df, ex in lay:
            fo = eoff + off
            fp = path + '.' + (nm or '?')
            if self.fields is not None and t != T_STRUCT:
""", b"""        fv, size = self.D.fieldset(bd, ver)
        elim = None
        if esz != size:
            # U2: tool_fast reads a SHORT file element as the prefix of its element, the rest zero (module doc); larger: refused
            if not 0 < esz < size:
                raise TagIOError('%s @0x%X: element size %d, field set says %d' % (path, pos, esz, size))
            elim = esz
        lay, tot = self.D.layout(fv)
        if tot != size:
            raise TagIOError('%s: layout total %d != element size %d' % (path, tot, size))
        b = Blk(path, pos, cnt, esz, pos + 16, self.D.bd[bd]['name'], ver, fv)
        self.blocks.append(b)
        self.bpath[path] = b
        if elim is not None:
            if self.short is None:     # a Walk subclass that does not run Walk.__init__
                self.short = {}
            self.short[path] = (esz, size)
        base = pos + 16
        pos = base + cnt * esz
        for k in range(cnt):
            pos = self._children(pos, base + k * esz, fv, '%s[%d]' % (path, k), elim)
        return pos

    def _children(self, pos, eoff, fields_va, path, elim=None):
        \"\"\"elim (U2): the file size of a SHORT element (None: the whole element is in the file).  A field that does not fit in
        the file element is zero in the tool's element: it owns nothing in the child stream and is not recorded.\"\"\"
        d = self.d
        lay, tot = self.D.layout(fields_va)
        for off, t, nm, df, ex in lay:
            fo = eoff + off
            fp = path + '.' + (nm or '?')
            if elim is not None and off + self.D.fsize(t, df, ex) > elim:
                if off < elim and t in CHILD_TYPES:
                    raise TagIOError('%s: a %d-byte field (type 0x%X) at +%d straddles the end of a short %d-byte element (not '
                                     'modelled)' % (fp, self.D.fsize(t, df, ex), t, off, elim))
                continue
            if self.fields is not None and t != T_STRUCT:
"""))

R.append((b"""                fv, size = self.D.fieldset(S['bd'], ver)
                if sz != size or cnt != 1:
                    raise TagIOError('%s: struct size %d / field set %d, count %d' % (fp, sz, size, cnt))
                pos = self._children(pos + 16, fo, fv, fp)
""", b"""                fv, size = self.D.fieldset(S['bd'], ver)
                if sz != size or cnt != 1:
                    raise TagIOError('%s: struct size %d / field set %d, count %d' % (fp, sz, size, cnt))
                if elim is not None and sz != self.D.fsize(t, df, ex):
                    raise TagIOError('%s: a %d-byte struct inside a short element whose layout has %d (not modelled)' % (
                        fp, sz, self.D.fsize(t, df, ex)))
                pos = self._children(pos + 16, fo, fv, fp)
"""))

R.append((b"""        L = self.elem_layout(blk)
        for nm, off in expect.items():
            if nm not in L or L[nm][0] != off:
                raise TagIOError('%s (%s v%d): field %r expected at +%d, definition has %r' % (
                    blk.path, blk.name, blk.ver, nm, off, L.get(nm)))
""", b"""        L = self.elem_layout(blk)
        for nm, off in expect.items():
            if nm not in L or L[nm][0] != off:
                raise TagIOError('%s (%s v%d): field %r expected at +%d, definition has %r' % (
                    blk.path, blk.name, blk.ver, nm, off, L.get(nm)))
            if self.short and blk.path in self.short and off + L[nm][2] > blk.esize:
                raise TagIOError('%s (%s v%d): field %r at +%d lies past the end of the short %d-byte file element' % (
                    blk.path, blk.name, blk.ver, nm, off, blk.esize))
"""))

R.append((b"""    b = W.block(path)
    lay, _ = W.D.layout(b.fields_va)
    if any(t in CHILD_TYPES for _, t, _, _, _ in lay):
""", b"""    b = W.block(path)
    if W.short and b.path in W.short:
        raise TagIOError('%s: a short (old-layout) block is not spliced' % path)
    lay, _ = W.D.layout(b.fields_va)
    if any(t in CHILD_TYPES for _, t, _, _, _ in lay):
"""))


def main(argv):
    if len(argv) != 2:
        sys.exit(__doc__)
    src, out = argv
    b = open(src, 'rb').read()
    m = hashlib.md5(b).hexdigest()
    if m not in BASES:
        sys.exit('REFUSED: %s md5 %s is not a pinned base (%s)' % (src, m, ', '.join(BASES)))
    if b'\r\n' in b:
        sys.exit('REFUSED: %s has CRLF line ends (the bases are LF)' % src)
    nb = b
    for i, (old, new) in enumerate(R, 1):
        n = nb.count(old)
        if n != 1:
            sys.exit('REFUSED: replacement %d: the old text occurs %d times in %s (expected 1)' % (i, n, src))
        nb = nb.replace(old, new)
    if nb.count(b'U2') < 4:
        sys.exit('REFUSED: the edit did not land')
    with open(out, 'xb') as f:
        f.write(nb)
    print('%s (%s, %s) -> %s (%s, %+d bytes)' % (src, m[:8], BASES[m], out, hashlib.md5(nb).hexdigest(), len(nb) - len(b)))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
