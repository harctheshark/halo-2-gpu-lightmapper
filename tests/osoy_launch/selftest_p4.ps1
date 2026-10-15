# selftest_p4.ps1 -- S1.3 / P4 self-test of the standalone osoy_launch.exe (--nowindow, --quality-row P MC).
# NO bake, NO GPU, NO window: every launch uses --nowindow. Writes <Work>\selftest_p4.out; the last line is the verdict.
#
#  A. regression = every case of production osoyoos\selftest\selftest.ps1 + deadlock_test.ps1, against fake_tool_p4.exe
#     (the v370 fake image at 0x400000), now with --nowindow;
#  B. the --nowindow proof: the child (with the REAL H2ToolHooks.dll, whose DllMain calls AllocConsole) has no visible console;
#  C. argument validation (exit 2, no child is ever created);
#  D. --quality-row against an image without the 'cuban' row -> exit 4, child terminated;
#  E. the REAL tool_fast.exe, from a PRIVATE COPY (md5 3a889d37) in <Work>\private_tool: launched SUSPENDED, H2ToolHooks
#     injected + proven, the 'cuban' row found / written / read back, the preset table dumped, then the child is TERMINATED
#     (--test-terminate) -- not one tool instruction runs. Plus the gpu_bake call shape (--hold + --quality-row): the row is
#     written before PID= is printed; the held private child is then killed, never resumed.
#
# usage (from Bash):  pwsh -NoProfile -NonInteractive -File '<this>' [-SkipRealTool]
param(
    [string]$Launcher = "",
    [string]$FakeTool = "",
    [string]$Work = "",
    [string]$OsoyDir = $(if ($env:H2LM_TEST_ROOT) { "$($env:H2LM_TEST_ROOT)\GPU Lightmap Work\osoyoos" } else { '' }),
    [string]$H2Root = $env:H2LM_TEST_ROOT,
    [switch]$SkipRealTool
)
$ErrorActionPreference = 'Stop'
try { (Get-Process -Id $PID).PriorityClass = 'BelowNormal' } catch { }
$Proj = Split-Path (Split-Path $PSScriptRoot)
if (-not $Launcher) { $Launcher = "$Proj\build\out\osoy_launch.exe" }
if (-not $Work)     { $Work = "$Proj\build\osoy_selftest" }
if (-not $FakeTool) { $FakeTool = "$Work\fake_tool_p4.exe" }
$PIN_DLL  = '9DEFF656B076EE101B22A7C3623FFE25'      # the user's H2ToolHooks.dll (gpu_bake.ps1 $OSOY_PIN)
$PIN_TOOL = '3A889D370A7BE537AF47FF8035ACD201'; $TOOL_SIZE = 8804592
$PIN_FBX  = '8146976216F143923AAB24A9BE73FDEA'
$PIN_DB   = '6814F29A8210AAA3871B68E8EA659C93'
$TABLE    = @('checkerboard', 'cuban', 'draft_low', 'draft_medium', 'draft_high', 'draft_super', 'direct_only', 'low', 'medium', 'high', 'super')

function Md5([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm MD5).Hash }
$WorkFull = [IO.Path]::GetFullPath($Work).TrimEnd('\') + '\'
$RootFull = [IO.Path]::GetFullPath($H2Root).TrimEnd('\') + '\'
if ($WorkFull.StartsWith($RootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing: Work $WorkFull is under the H2EK root $RootFull" }
foreach ($f in $Launcher, $FakeTool) { if (-not (Test-Path -LiteralPath $f)) { throw "missing $f (run native\osoy_launch\build_p4.ps1 first)" } }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

function Copy-Pinned([string]$src, [string]$dst, [string]$md5) {
    # read-only use of the source; the copy is verified. Never writes anywhere but $dst.
    if ((Test-Path -LiteralPath $dst) -and (Md5 $dst) -eq $md5) { return }
    if ((Md5 $src) -ne $md5) { throw "$src md5 $(Md5 $src) is not the pinned $md5" }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    if ((Md5 $dst) -ne $md5) { throw "copy $dst md5 mismatch" } }
$DLL = "$Work\H2ToolHooks.dll"
Copy-Pinned "$OsoyDir\H2ToolHooks.dll" $DLL $PIN_DLL

$Log = [Collections.Generic.List[string]]::new()
$script:fails = 0
function Note([string]$s) { $Log.Add($s) }
function Run([string]$name, [string[]]$a, [int]$want, [scriptblock]$check = $null) {
    $o = @(& $Launcher @a 2>&1 | ForEach-Object { "$_" })
    $x = $LASTEXITCODE
    Note "===== $name (want exit $want, got $x)"
    foreach ($ln in $o) { Note $ln }
    $ok = ($x -eq $want)
    if ($ok -and $check) {
        $r = & $check $o
        if ($r -ne $true) { $ok = $false; Note "*** CHECK FAILED: $r" } }
    if ($ok) { Note "ok" } else { Note "*** FAIL"; $script:fails++ }
    return , $o }
function Has([string[]]$o, [string]$re) { [bool]($o | Where-Object { $_ -match $re } | Select-Object -First 1) }
function Alive([string]$exePath) {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exePath } catch { $false } }) }

# ---------------------------------------------------------------------------------------------------------------- A. regression
$ArgsF = "$Work\args.txt"
Set-Content -LiteralPath $ArgsF 'lightmaps scenarios\multi\x\x x low -tags_dir "C:\a b\tags" -data_dir data' -NoNewline
$com = @('--dll', $DLL, '--exe', $FakeTool, '--argsfile', $ArgsF, '--cwd', $Work, '--wait', '--nowindow')
$okRun = { param($o) if (-not (Has $o 'child exit code 0x00000000')) { 'child did not exit 0' }
                     elseif (-not (Has $o '^OSOYLAUNCH --nowindow: CREATE_NO_WINDOW')) { 'no --nowindow line' } else { $true } }
$null = Run "plucker"         (@('--plucker', '--test-seed-stock') + $com) 0 {
    param($o) if ((@($o | Where-Object { $_ -match '^OSOYHOOK .* INSTALLED' })).Count -ne 2) { '2 plucker sites not INSTALLED' } else { & $okRun $o } }
$null = Run "scenery"         (@('--scenery', '--test-seed-stock') + $com) 0 {
    param($o) if ((@($o | Where-Object { $_ -match '^OSOYHOOK .* INSTALLED' })).Count -ne 3) { '3 scenery sites not INSTALLED' } else { & $okRun $o } }
$null = Run "plucker+scenery" (@('--plucker', '--scenery', '--test-seed-stock') + $com) 0 {
    param($o) if ((@($o | Where-Object { $_ -match '^OSOYHOOK .* INSTALLED' })).Count -ne 5) { '5 sites not INSTALLED' } else { & $okRun $o } }
$null = Run "NEG not stock"   (@('--plucker') + $com) 4 { param($o) if (Has $o 'TERMINATED before it ran') { $true } else { 'child not reported terminated' } }
$null = Run "NEG no fix"      ($com) 2
$env:OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX = "1"      # a leftover shell flag must NOT reach the child
$null = Run "leftover OSOYOOS_ env stripped" (@('--plucker', '--test-seed-stock') + $com) 0 {
    param($o) if (-not (Has $o 'removed inherited OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX')) { 'not stripped' }
              elseif ((@($o | Where-Object { $_ -match 'not requested: stock \(untouched\)' })).Count -ne 3) { 'scenery sites touched' } else { $true } }
Remove-Item Env:OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX

# --hold / --resume (the gpu_bake.ps1 call shape)
$o = @(& $Launcher --plucker --scenery --test-seed-stock --hold --nowindow --dll $DLL --exe $FakeTool --argsfile $ArgsF --cwd $Work 2>&1 | ForEach-Object { "$_" })
Note "===== hold + resume, --nowindow (exit $LASTEXITCODE)"; foreach ($ln in $o) { Note $ln }
$hp = [int](($o | Select-String '^PID=(\d+)$').Matches[0].Groups[1].Value)
$pr = [Diagnostics.Process]::GetProcessById($hp); $null = $pr.Handle
Start-Sleep -Milliseconds 1500
$held = -not $pr.HasExited
Note "held child still alive 1.5 s after --hold: $held"
foreach ($ln in @(& $Launcher --resume $hp 2>&1 | ForEach-Object { "$_" })) { Note $ln }
$done = $pr.WaitForExit(30000)
Note "after --resume: exited=$done exit code=$($pr.ExitCode)"
if ($held -and $done -and $pr.ExitCode -eq 0) { Note "ok" } else { Note "*** FAIL"; $script:fails++ }

# deadlock_test.ps1 (2026-10-05): --hold + --stdout/--stderr while the CALLER reads the helper's output through a pipe
Set-Content -LiteralPath "$Work\dl_args.txt" "fake args"
$job = Start-ThreadJob -ScriptBlock {
    param($h, $dll, $ft, $t)
    @(& $h --plucker --test-seed-stock --hold --nowindow --dll $dll --exe $ft --argsfile "$t\dl_args.txt" --cwd $t `
         --stdout "$t\dl_out.txt" --stderr "$t\dl_err.txt" 2>&1 | ForEach-Object { "$_" })
} -ArgumentList $Launcher, $DLL, $FakeTool, $Work
$jd = Wait-Job $job -Timeout 20
Note "===== deadlock test (--hold + --stdout/--stderr + piped caller), --nowindow"
if (-not $jd) {
    foreach ($p in Alive $FakeTool) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }   # only OUR fake tool
    Wait-Job $job -Timeout 10 | Out-Null
    Note "DEADLOCK: the caller's pipe read did not return within 20 s"; Note "*** FAIL"; $script:fails++ }
else {
    $jo = @(Receive-Job $job); foreach ($ln in $jo) { Note $ln }
    $pm = $jo | Select-String -Pattern '^PID=(\d+)' | Select-Object -First 1
    if (-not $pm) { Note "NO PID line"; Note "*** FAIL"; $script:fails++ }
    else {
        $cpid = [int]$pm.Matches[0].Groups[1].Value
        $cp = Get-Process -Id $cpid -ErrorAction SilentlyContinue; if ($cp) { $null = $cp.Handle }
        foreach ($ln in @(& $Launcher --resume $cpid 2>&1 | ForEach-Object { "$_" })) { Note $ln }
        if ($cp) { $null = $cp.WaitForExit(10000) }
        if ($cp -and -not $cp.HasExited) { Stop-Process -Id $cpid -Force; Note "child did not exit after resume"; Note "*** FAIL"; $script:fails++ }
        else { Note "returned without deadlock; child pid $cpid resumed and exited (exit $($cp.ExitCode))"
               # informational, as in production: an injected child's stdout never reaches --stdout (H2ToolHooks freopen()s it onto
               # CONOUT$ in DllMain); production's own selftest\dl_out.txt is 0 bytes too
               Note ("dl_out.txt {0} bytes (expected 0: H2ToolHooks re-points stdout at the console)" -f (Get-Item -LiteralPath "$Work\dl_out.txt").Length)
               if ($cp.ExitCode -eq 0) { Note "ok" } else { Note "*** FAIL: child exit $($cp.ExitCode)"; $script:fails++ } } } }
Remove-Job $job -Force -ErrorAction SilentlyContinue

# --------------------------------------------------------------------------------------------- B. --nowindow: no visible console
# fake_tool_p4 writes its report to $env:FAKE_TOOL_REPORT (passes through osoy_launch), since an injected child's stdout goes
# to its console, not to --stdout
$nwo = "$Work\nw_out.txt"; $nwe = "$Work\nw_err.txt"; $nwr = "$Work\nw_report.txt"
foreach ($f in $nwo, $nwe, $nwr) { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }
$env:FAKE_TOOL_REPORT = $nwr
$null = Run "--nowindow: the child (real H2ToolHooks DllMain AllocConsole) has no visible console" `
    (@('--plucker', '--scenery', '--test-seed-stock', '--stdout', $nwo, '--stderr', $nwe) + $com) 0 {
    param($o) $t = if (Test-Path -LiteralPath $nwr) { [string](Get-Content -LiteralPath $nwr -Raw) } else { "" }
    foreach ($ln in ($t -split "`r?`n")) { if ($ln) { Note "   child> $ln" } }
    if (-not $t) { 'no child report' }
    elseif (-not ($t -match 'console window 0x[0-9A-F]{8} visible=0')) { 'child console is VISIBLE (or no report line)' }
    elseif (-not ($t -match '0x49C09C = E9')) { 'hooks not seen by the child' } else { & $okRun $o } }
Remove-Item Env:FAKE_TOOL_REPORT

# ----------------------------------------------------------------------------------------------- C. argument validation (exit 2)
$seeded = @('--plucker', '--test-seed-stock') + $com
$noChild = { param($o) if (Has $o 'created SUSPENDED') { 'a child was created' } else { $true } }
foreach ($bad in @(@('0', '8'), @('100000001', '8'), @('1000000', '0'), @('1000000', '65'), @('5e7', '8'), @('50,000,000', '16'),
                  @('-5', '8'), @('1000000', '8x'), @(' 1000000', '8'), @('1000000000', '8'))) {
    $null = Run "NEG --quality-row $($bad -join ' ')" (@('--quality-row') + $bad + $seeded) 2 $noChild }
$null = Run "NEG --quality-row missing MC" ($seeded + @('--quality-row', '1000000')) 2 $noChild
$null = Run "NEG --test-terminate + --hold" (@('--plucker', '--test-seed-stock', '--test-terminate', '--hold', '--dll', $DLL, '--exe', $FakeTool, '--argsfile', $ArgsF, '--nowindow')) 2 $noChild
$null = Run "NEG --plucker without --dll" (@('--plucker', '--exe', $FakeTool, '--argsfile', $ArgsF, '--nowindow')) 2 $noChild

# ---------------------------------------------------------------------- D. --quality-row on an image with no 'cuban' row -> exit 4
$null = Run "NEG --quality-row on fake_tool (no cuban row), fixes on" (@('--quality-row', '50000000', '16') + $seeded) 4 {
    param($o) if (-not (Has $o 'found 0 time')) { 'not the row-not-found refusal' } elseif (-not (Has $o 'TERMINATED before it ran')) { 'child not terminated' } else { $true } }
$null = Run "NEG --quality-row on fake_tool, row-only (no fix, no --dll)" `
    (@('--quality-row', '50000000', '16', '--test-seed-stock', '--exe', $FakeTool, '--argsfile', $ArgsF, '--cwd', $Work, '--nowindow')) 4 {
    param($o) if (-not (Has $o 'found 0 time')) { 'not the row-not-found refusal' } elseif (-not (Has $o 'NOT injected')) { 'injected in row-only mode' } else { $true } }
Start-Sleep -Milliseconds 300
if ((Alive $FakeTool).Count) { Note "*** FAIL: a fake_tool_p4.exe is still alive"; $script:fails++ } else { Note "no fake_tool_p4.exe left alive" }

# ------------------------------------------------------------------------------- E. the REAL tool_fast.exe, private copy, suspended
$toolMd5 = ""
if (-not $SkipRealTool) {
    $PrivDir = "$Work\private_tool"; New-Item -ItemType Directory -Force -Path $PrivDir | Out-Null
    $PrivTool = "$PrivDir\tool_fast.exe"
    if ((Get-Item -LiteralPath "$H2Root\tool_fast.exe").Length -ne $TOOL_SIZE) { throw "$H2Root\tool_fast.exe is not $TOOL_SIZE bytes" }
    Copy-Pinned "$H2Root\tool_fast.exe" $PrivTool $PIN_TOOL
    Copy-Pinned "$H2Root\libfbxsdk.dll" "$PrivDir\libfbxsdk.dll" $PIN_FBX                     # its one non-system static import
    Copy-Pinned "$H2Root\h2v_scenery_bounds.db" "$PrivDir\h2v_scenery_bounds.db" $PIN_DB      # the scenery hook's cwd fallback
    $toolMd5 = Md5 $PrivTool
    $ArgsT = "$PrivDir\args.txt"
    Set-Content -LiteralPath $ArgsT 'lightmaps "scenarios\multi\selftest\selftest" selftest cuban 1 0' -NoNewline
    $rcom = @('--dll', $DLL, '--exe', $PrivTool, '--argsfile', $ArgsT, '--cwd', $PrivDir, '--nowindow', '--test-terminate')
    function RowOf([string[]]$o, [string]$re) {
        $m = $o | Select-String -Pattern $re | Select-Object -First 1
        if ($m) { $m.Matches[0].Groups[1].Value } else { "" } }
    $termOk = { param($o) if (-not (Has $o 'TERMINATED while still SUSPENDED \(exit code 0x0000DEAD\)')) { 'not terminated while suspended' } else { $true } }
    $tableOk = { param($o)
        $names = @(); for ($t = 0; $t -lt 11; $t++) { $names += (RowOf $o "^OSOYTABLE \[$t\] va=0x[0-9A-F]{8} name=(\S+) ") }
        if (($names -join ',') -ne ($TABLE -join ',')) { "preset table names: $($names -join ',')" } else { $true } }
    $qOkRe = { param([string]$p, [string]$m) "^OSOYROW QUALITY cuban photons=$p mc=$m ss=4 gq=4\.0 READBACK OK \(va 0x00A30970" }
    $stockRe = '^OSOYROW stock   va=0x00A30970 name=cuban ss=1 mc=1 is_draft=0 photons=50000 unknown=0 gather_quality=1\.000000 checkerboard=0 bytes=1400a30001000000010000000000000050c30000000000000000803f00000000$'

    $null = Run "REAL tool: stock 'cuban' row dump (plucker+scenery injected + proven, no write)" (@('--plucker', '--scenery') + $rcom) 0 {
        param($o) if ((@($o | Where-Object { $_ -match '^OSOYHOOK .* INSTALLED' })).Count -ne 5) { 'hooks not proven' }
                  elseif (-not (Has $o $stockRe)) { 'stock row not as expected' }
                  elseif (Has $o '^OSOYROW written') { 'wrote without --quality-row' }
                  else { $r = & $tableOk $o; if ($r -ne $true) { $r } else { & $termOk $o } } }
    foreach ($pm in @(@('50000000', '16'), @('100000000', '64'), @('1000000', '1'), @('1', '1'))) {
        $p = $pm[0]; $m = $pm[1]
        $null = Run "REAL tool: Custom $p / $m (plucker+scenery)" (@('--plucker', '--scenery', '--quality-row', $p, $m) + $rcom) 0 {
            param($o) if (-not (Has $o $stockRe)) { 'stock row not found first' }
                      elseif (-not (Has $o (& $qOkRe $p $m))) { 'no READBACK OK line' }
                      elseif (-not (Has $o "^OSOYROW written va=0x00A30970 name=cuban ss=4 mc=$m is_draft=0 photons=$p unknown=0 gather_quality=4\.000000 checkerboard=0 bytes=1400a300")) { 'written row wrong / name pointer changed' }
                      elseif (-not (Has $o "^OSOYTABLE \[1\] va=0x00A30970 name=cuban ss=4 mc=$m is_draft=0 photons=$p ")) { 'table dump does not show the new row' }
                      else { $r = & $tableOk $o; if ($r -ne $true) { $r } else { & $termOk $o } } } }
    # the free proof at row level: Custom(20M, 8) writes exactly the super row (all 28 data bytes; only the name pointer differs)
    $o20 = Run "REAL tool: Custom 20000000 / 8 == the super row" (@('--plucker', '--scenery', '--quality-row', '20000000', '8') + $rcom) 0 {
        param($o) $cb = RowOf $o '^OSOYROW written va=0x00A30970 .* bytes=([0-9a-f]{64})$'
                  $sb = RowOf $o '^OSOYTABLE \[10\] va=0x00A30A90 name=super .* bytes=([0-9a-f]{64})$'
                  Note "   cuban(20M,8) data bytes $($cb.Substring([Math]::Min(8, $cb.Length)))"
                  Note "   super        data bytes $($sb.Substring([Math]::Min(8, $sb.Length)))"
                  if (-not $cb -or -not $sb) { 'rows not dumped' }
                  elseif ($cb.Substring(8) -ne $sb.Substring(8)) { 'Custom(20M,8) row != super row' }
                  elseif ($cb.Substring(0, 8) -ne '1400a300') { 'name pointer changed' } else { & $termOk $o } }
    $null = Run "REAL tool: row-only Custom 50000000 / 16 (no fix, no --dll: written before the loader runs)" `
        (@('--quality-row', '50000000', '16', '--exe', $PrivTool, '--argsfile', $ArgsT, '--cwd', $PrivDir, '--nowindow', '--test-terminate')) 0 {
        param($o) if (-not (Has $o 'NOT injected')) { 'DLL injected in row-only mode' }
                  elseif (Has $o '^OSOYHOOK') { 'hook lines in row-only mode' }
                  elseif (-not (Has $o (& $qOkRe '50000000' '16'))) { 'no READBACK OK line' } else { & $termOk $o } }
    # a leftover OSOYOOS_INJECTOR_PATCH_QUALITY (the Osoyoos conf-file path) must be stripped, or H2ToolHooks would rewrite the row first
    $env:OSOYOOS_INJECTOR_PATCH_QUALITY = "1"
    $null = Run "REAL tool: leftover OSOYOOS_INJECTOR_PATCH_QUALITY is stripped, the row is still stock when found" (@('--plucker', '--quality-row', '20000000', '8') + $rcom) 0 {
        param($o) if (-not (Has $o 'removed inherited OSOYOOS_INJECTOR_PATCH_QUALITY')) { 'not stripped' }
                  elseif (-not (Has $o $stockRe)) { 'row not stock when found' }
                  elseif (-not (Has $o (& $qOkRe '20000000' '8'))) { 'no READBACK OK line' } else { & $termOk $o } }
    Remove-Item Env:OSOYOOS_INJECTOR_PATCH_QUALITY
    # the gpu_bake.ps1 call shape: --hold + --quality-row + --pidfile. The row is written BEFORE PID= ; the held private child is
    # then killed here (never resumed).
    $pidF = "$PrivDir\held.pid"; Remove-Item -LiteralPath $pidF -ErrorAction SilentlyContinue
    $ho = @(& $Launcher --plucker --scenery --quality-row 50000000 16 --hold --nowindow --pidfile $pidF --dll $DLL --exe $PrivTool --argsfile $ArgsT --cwd $PrivDir 2>&1 | ForEach-Object { "$_" })
    $hx = $LASTEXITCODE
    Note "===== REAL tool: --hold + --quality-row 50000000 16 (gpu_bake call shape; the held child is killed, never resumed) (exit $hx)"
    foreach ($ln in $ho) { Note $ln }
    $iRow = [array]::FindIndex([string[]]$ho, [Predicate[string]] { param($s) $s -match '^OSOYROW QUALITY cuban photons=50000000 mc=16 ss=4 gq=4\.0 READBACK OK' })
    $iPid = [array]::FindIndex([string[]]$ho, [Predicate[string]] { param($s) $s -match '^PID=\d+$' })
    $hpid = if ($iPid -ge 0) { [int]($ho[$iPid] -replace '^PID=', '') } else { 0 }
    $hp2 = if ($hpid) { Get-Process -Id $hpid -ErrorAction SilentlyContinue } else { $null }
    $heldOk = $hp2 -and ($hp2.Path -eq $PrivTool) -and -not $hp2.HasExited
    $pidOk = (Test-Path -LiteralPath $pidF) -and ((Get-Content -LiteralPath $pidF -Raw).Trim() -eq "$hpid")
    if ($hp2 -and $hp2.Path -eq $PrivTool) { Stop-Process -Id $hpid -Force; $null = $hp2.WaitForExit(10000) }
    Note "row line #$iRow, PID line #$iPid, held private child alive=$heldOk (path $($hp2.Path)), pidfile ok=$pidOk -> killed"
    if ($hx -eq 0 -and $iRow -ge 0 -and $iPid -gt $iRow -and $heldOk -and $pidOk) { Note "ok" } else { Note "*** FAIL"; $script:fails++ }

    Start-Sleep -Milliseconds 500
    $left = Alive $PrivTool
    if ($left.Count) { Note "*** FAIL: $($left.Count) private tool_fast.exe still alive"; $script:fails++ } else { Note "no private tool_fast.exe left alive" }
    if ((Md5 $PrivTool) -ne $PIN_TOOL) { Note "*** FAIL: the private tool_fast.exe changed on disk"; $script:fails++ }
    else { Note "private tool_fast.exe on disk unchanged ($PIN_TOOL): the row write is in-memory only" }
}
else { Note "===== E. REAL tool cases SKIPPED (-SkipRealTool)" }

$verdict = "SELFTEST P4 {0}  (osoy_launch.exe md5 {1}, H2ToolHooks.dll md5 {2}, tool_fast.exe md5 {3})" -f `
    $(if ($script:fails) { "FAILED ($($script:fails))" } else { "PASSED" }), (Md5 $Launcher), (Md5 $DLL), $(if ($toolMd5) { $toolMd5 } else { "skipped" })
Note $verdict
$Log | Set-Content -LiteralPath "$Work\selftest_p4.out"
$Log | Where-Object { $_ -match '^=====|^\*\*\*|^SELFTEST' }
exit $(if ($script:fails) { 1 } else { 0 })
