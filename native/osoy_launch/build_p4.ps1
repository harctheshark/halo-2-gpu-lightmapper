# build_p4.ps1 -- runs build_osoy_launch_p4.bat at BELOW_NORMAL priority (cl/link inherit it) and prints the output md5s.
# usage (from Bash): pwsh -NoProfile -NonInteractive -File '<this>'
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$here = $PSScriptRoot
$proj = Split-Path (Split-Path $here)
& cmd.exe /d /c "`"$here\build_osoy_launch_p4.bat`""
$x = $LASTEXITCODE
if ($x -ne 0) { "BUILD EXIT $x"; exit $x }
foreach ($f in "$proj\build\out\osoy_launch.exe", "$proj\build\osoy_selftest\fake_tool_p4.exe") {
    "{0}  {1}  {2}" -f (Get-FileHash $f -Algorithm MD5).Hash, (Get-Item $f).Length, $f }
exit 0
