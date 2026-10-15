# v370 osoy_launch.exe SELF-TEST -- exercises the REAL H2ToolHooks.dll install path without running tool_fast.exe.
# fake_tool.exe = an x86 exe fixed at 0x400000 whose .bss spans the five patch sites; --test-seed-stock writes the stock tool_fast
# prologues there (suspended), then the helper injects, verifies and resumes exactly as in a bake. Writes selftest.out.
$ErrorActionPreference = "Stop"
$D = Split-Path $PSScriptRoot; $T = $PSScriptRoot
$L = @()
function Run([string]$name, [string[]]$a, [int]$want) {
    $o = & "$D\osoy_launch.exe" @a 2>&1 | ForEach-Object { "$_" }
    $x = $LASTEXITCODE
    $script:L += "===== $name (want exit $want, got $x)"; $script:L += $o
    if ($x -ne $want) { $script:L += "*** FAIL"; $script:fail++ } }
$fail = 0
Set-Content "$T\args.txt" 'lightmaps scenarios\multi\x\x x low -tags_dir "C:\a b\tags" -data_dir data' -NoNewline
$com = @('--dll', "$D\H2ToolHooks.dll", '--exe', "$T\fake_tool.exe", '--argsfile', "$T\args.txt", '--cwd', $T, '--wait')
Run "plucker"         (@('--plucker', '--test-seed-stock') + $com) 0
Run "scenery"         (@('--scenery', '--test-seed-stock') + $com) 0
Run "plucker+scenery" (@('--plucker', '--scenery', '--test-seed-stock') + $com) 0
Run "NEG not stock"   (@('--plucker') + $com) 4
Run "NEG no fix"      ($com) 2
$env:OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX = "1"      # a leftover shell flag must NOT reach the child (the helper strips OSOYOOS_*)
Run "leftover OSOYOOS_ env stripped" (@('--plucker', '--test-seed-stock') + $com) 0
Remove-Item Env:OSOYOOS_INJECTOR_LIGHTMAP_SCENERY_FIX
# --hold / --resume (what gpu_bake.ps1 uses): the child must still be suspended after --hold, run after --resume
$o = @(& "$D\osoy_launch.exe" --plucker --scenery --test-seed-stock --hold --dll "$D\H2ToolHooks.dll" --exe "$T\fake_tool.exe" --argsfile "$T\args.txt" --cwd $T 2>&1 | ForEach-Object { "$_" })
$L += "===== hold + resume (exit $LASTEXITCODE)"; $L += $o
$hp = [int](($o | Select-String '^PID=(\d+)$').Matches[0].Groups[1].Value)
$pr = [Diagnostics.Process]::GetProcessById($hp); $null = $pr.Handle
Start-Sleep -Milliseconds 1500
$held = -not $pr.HasExited
$L += "held child still alive 1.5 s after --hold: $held"
$L += @(& "$D\osoy_launch.exe" --resume $hp 2>&1 | ForEach-Object { "$_" })
$done = $pr.WaitForExit(30000)
$L += "after --resume: exited=$done exit code=$($pr.ExitCode)"
if (-not ($held -and $done -and $pr.ExitCode -eq 0)) { $L += "*** FAIL"; $fail++ }
$L += ("SELFTEST {0}  (osoy_launch.exe md5 {1}, H2ToolHooks.dll md5 {2})" -f $(if ($fail) { "FAILED ($fail)" } else { "PASSED" }),
       (Get-FileHash "$D\osoy_launch.exe" -A MD5).Hash, (Get-FileHash "$D\H2ToolHooks.dll" -A MD5).Hash)
$L | Set-Content "$T\selftest.out"; $L | Select-Object -Last 1
