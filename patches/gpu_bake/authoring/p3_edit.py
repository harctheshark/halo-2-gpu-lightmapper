"""Authoring helper (not shipped): the P3 edits (H2LM_NOWINDOW + the payload osoy_launch pin), each with an exact expected count.
usage: py -I p3_edit.py <file>   (edits the file in place; refuses on any count mismatch). CRLF files only."""
import sys

N = b'\r\n'

P3_BLOCK = N.join([
    b"# >>> H2LM P3 (standalone exe): H2LM_NOWINDOW=1 = no console window for any tool_fast run -- osoy_launch.exe gets --nowindow",
    b"# (CREATE_NO_WINDOW) and the three plain Start-Process launches (skeleton, capture, write-back) -WindowStyle Hidden. UNSET (every",
    b"# production caller) = an empty splat and no extra osoy_launch argument: the same command lines as before. Any other value is refused.",
    b"if ($null -ne $env:H2LM_NOWINDOW -and $env:H2LM_NOWINDOW -ne '1') { throw \"H2LM: H2LM_NOWINDOW='$($env:H2LM_NOWINDOW)' -- only 1 (or unset) is valid\" }",
    b"$h2lmNoWin = $env:H2LM_NOWINDOW -eq '1'",
    b"$h2lmHide  = if ($h2lmNoWin) { @{ WindowStyle = 'Hidden' } } else { @{} }",
    b"# <<< H2LM P3",
]) + N

PIN_OLD = b"$OSOY_PIN    = [ordered]@{ $OSOY_DLL = '9DEFF656B076EE101B22A7C3623FFE25'; $OSOY_LAUNCH = 'C304184E6DA7D7A0C7BB0E05900B926D' }" + N
PIN_NEW = N.join([
    b"# H2LM P3: relocated code (H2LM_CODE) runs the PAYLOAD's osoy_launch.exe (the --nowindow / --quality-row build), pinned to its own md5,",
    b"# filled in when the payload is built (the %%H2LM:OSOY_LAUNCH_MD5%% token, tools\\strict_patch.py --set); the production layout keeps",
    b"# the production pin.",
    b"$OSOY_PIN    = [ordered]@{ $OSOY_DLL = '9DEFF656B076EE101B22A7C3623FFE25'; $OSOY_LAUNCH = $(if ($env:H2LM_CODE) { '%%H2LM:OSOY_LAUNCH_MD5%%' } else { 'C304184E6DA7D7A0C7BB0E05900B926D' }) }",
]) + N

EDITS = [
    (b"# <<< H2LM P1" + N, b"# <<< H2LM P1" + N + P3_BLOCK, 1),
    (PIN_OLD, PIN_NEW, 1),
    (b"    if ($SceneryFix) { $la += '--scenery' }" + N,
     b"    if ($SceneryFix) { $la += '--scenery' }" + N +
     b"    if ($h2lmNoWin) { $la += '--nowindow' }     # H2LM P3: the tool gets CREATE_NO_WINDOW (never DETACHED_PROCESS: H2ToolHooks calls AllocConsole)" + N, 1),
    (b'-RedirectStandardOutput "$run\\tool_stdout.txt" -RedirectStandardError "$run\\tool_stderr.txt"' + N,
     b'-RedirectStandardOutput "$run\\tool_stdout.txt" -RedirectStandardError "$run\\tool_stderr.txt" @h2lmHide     # H2LM P3' + N, 1),
    (N + b"    $p = Start-Process -FilePath $TOOL -ArgumentList $a -WorkingDirectory $ROOT -PassThru" + N,
     N + b"    $p = Start-Process -FilePath $TOOL -ArgumentList $a -WorkingDirectory $ROOT -PassThru @h2lmHide     # H2LM P3" + N, 1),
    (N + b"    $p = Start-Process -FilePath $TOOL -ArgumentList $aFull -WorkingDirectory $ROOT -PassThru" + N,
     N + b"    $p = Start-Process -FilePath $TOOL -ArgumentList $aFull -WorkingDirectory $ROOT -PassThru @h2lmHide     # H2LM P3" + N, 1),
]

p = sys.argv[1]
d = open(p, 'rb').read()
if d.count(b'\n') != d.count(b'\r\n'):
    sys.exit('REFUSED: %s is not uniformly CRLF' % p)
for old, new, n in EDITS:
    c = d.count(old)
    if c != n:
        sys.exit('REFUSED: %r occurs %d times, expected %d' % (old[:80], c, n))
    d = d.replace(old, new)
open(p, 'wb').write(d)
print('P3: %d edits applied to %s' % (len(EDITS), p))
