"""f24_edit.py -- U1 (2026-10-15): author F24 = review finding 24's second half on the PRE-TOKEN P1-P3 stage copies.

lm_tagwriter.py's defs_pin gate checks the tool_fast.exe the frozen tag definitions were made from. gpu_bake called the writer
without --tool, so the writer fell back to lmw_tagio.DEFAULT_TOOL = the dev PC's G:\\...\\H2EKMine\\tool_fast.exe (on any other
root: "tool not found; defs pin not checked"). F24 passes this bake's own tool, $TOOL (= "$ROOT\\tool_fast.exe"). With H2LM_* unset
$TOOL IS that default path, so production behaviour is unchanged.

Exactly ONE statement changes in each script (the $twArgs assignment in Invoke-TagWriterStage gets `, "--tool", $TOOL`), plus two
comment lines above it. The edit is an exact-count byte replacement (CRLF kept); anything else refuses.

  py -I -B f24_edit.py <stage dir from patch_series apply --stages-out> <out dir>
writes <out>\\engine_v373__gpu_bake.ps1.F24 and <out>\\_research__ao__dev_AO__gpu_bake.ps1.F24 (then strict_patch.py make)."""
import hashlib
import os
import sys

OLD = (b'    $twArgs = @("$TW\\lm_tagwriter.py", "--run-dir", $OUT, "--skeleton", "$OUT\\skeleton", "--stage", $stage, "--bsp", $bspLive,\r\n'
       b'                "--scenario", $scnLive, "--threads", "$WriterThreads", "--lightmap-name", "$scenRel\\$ltmpName")\r\n')
NEW = (b'    # F24 (H2 GPU Lightmapper review finding 24, upstreamed 2026-10-15): the writer\'s defs_pin gate checks THIS bake\'s tool ($TOOL =\r\n'
       b'    # $ROOT\\tool_fast.exe); without --tool it fell back to lmw_tagio\'s hard-coded H2EKMine path. H2LM_ROOT unset: the same path.\r\n'
       b'    $twArgs = @("$TW\\lm_tagwriter.py", "--run-dir", $OUT, "--skeleton", "$OUT\\skeleton", "--stage", $stage, "--bsp", $bspLive,\r\n'
       b'                "--scenario", $scnLive, "--threads", "$WriterThreads", "--lightmap-name", "$scenRel\\$ltmpName", "--tool", $TOOL)\r\n')
INPUTS = {  # pre-token chain results (patch_series --stages-out names) -> their md5 (series.json result of the last patch)
    'engine_v373__gpu_bake.ps1.3_P2_quality_engine_v373': '4722eff6bca25b5d6f8884cb2483ef3d',
    '_research__ao__dev_AO__gpu_bake.ps1.2_P3_nowindow_dev_AO': 'dc4e753edd290da1ddc76ebfac7fafc9',
}


def main(argv):
    if len(argv) != 2:
        sys.exit(__doc__)
    src, out = argv
    os.makedirs(out, exist_ok=True)
    for name, want in INPUTS.items():
        b = open(os.path.join(src, name), 'rb').read()
        if hashlib.md5(b).hexdigest() != want:
            sys.exit('REFUSED: %s md5 %s, expected %s' % (name, hashlib.md5(b).hexdigest(), want))
        if b.count(OLD) != 1:
            sys.exit('REFUSED: %s holds the $twArgs block %d times (expected 1)' % (name, b.count(OLD)))
        if b.count(b'"--tool"') != 0:
            sys.exit('REFUSED: %s already passes --tool' % name)
        nb = b.replace(OLD, NEW)
        dst = os.path.join(out, name.split('.ps1.')[0] + '.ps1.F24')
        open(dst, 'wb').write(nb)
        print('%s: %s -> %s (%+d bytes) -> %s' % (name, want[:8], hashlib.md5(nb).hexdigest(), len(nb) - len(b), dst))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
