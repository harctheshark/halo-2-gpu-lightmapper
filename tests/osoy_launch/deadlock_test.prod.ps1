# deadlock_test.ps1 -- 2026-10-05: reproduces the v370 skeleton deadlock (--hold + --stdout/--stderr while the CALLER reads the helper's
# output through a pipe, exactly like gpu_bake.ps1's Start-OsoyTool) against the self-test target fake_tool.exe.
# usage: pwsh -NoProfile -NonInteractive -File deadlock_test.ps1 -Helper <osoy_launch*.exe>
param([Parameter(Mandatory = $true)][string]$Helper)
$ErrorActionPreference = "Stop"
$D = Split-Path $PSScriptRoot
$T = $PSScriptRoot
Set-Content "$T\args.txt" "fake args"
$job = Start-ThreadJob -ScriptBlock {
    param($h, $d, $t)
    @(& $h --plucker --test-seed-stock --hold --dll "$d\H2ToolHooks.dll" --exe "$t\fake_tool.exe" --argsfile "$t\args.txt" --cwd $t `
         --stdout "$t\dl_out.txt" --stderr "$t\dl_err.txt" 2>&1 | ForEach-Object { "$_" })
} -ArgumentList $Helper, $D, $T
$done = Wait-Job $job -Timeout 20
if (-not $done) {
    # the deadlock: find the held child and kill it so the pipe closes
    $pidLine = $null
    Get-CimInstance Win32_Process -Filter "name='fake_tool.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Wait-Job $job -Timeout 10 | Out-Null
    "DEADLOCK: the caller's pipe read did not return within 20 s with --hold + --stdout ($Helper)"
    exit 1 }
$out = Receive-Job $job
$pm = $out | Select-String -Pattern '^PID=(\d+)' | Select-Object -First 1
if (-not $pm) { "NO PID line:"; $out; exit 2 }
$cpid = [int]$pm.Matches[0].Groups[1].Value
$r = @(& $Helper --resume $cpid 2>&1 | ForEach-Object { "$_" })
$p = Get-Process -Id $cpid -ErrorAction SilentlyContinue
if ($p) { $p.WaitForExit(10000) | Out-Null; if (-not $p.HasExited) { Stop-Process -Id $cpid -Force; "child did not exit after resume"; exit 3 } }
"OK: returned without deadlock; child pid $cpid resumed and exited; resume said: $($r -join ' | ')"
exit 0
