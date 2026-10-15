<#
  S2.5/S3 test stand-in for INST\engine_v373\gpu_bake.ps1 (TEST payload only; a sandbox H2EK). It takes the real (P1/P2-patched)
  parameter set the shell passes, refuses to run without the H2LM_* contract, prints the banners the shell parses, and writes
  the farm files the shell reads: capture_id.json (soup crc), gpulm.log, the probe-ready sky files under GPULM_SKY_HI=1,
  OUT\skeleton, OUT\tagw_stage (+ a 'written' tagw_report.json). With -WriteBack -WriteBackMode direct it installs with the
  REAL lmw_install.py (INST copy) into the SANDBOX tags, exactly like production phase 4b. Never a GPU, never the tool.
  Behaviour knobs: $W\fake_control.txt lines "gpu_bake=ok|fail_bake|nostage|slow3|slow4b|nostage_once|nostage_once_slow4b|nodirect"
  and "gpu_bake_wait". Every call is appended to $W\fake_calls.log as one JSON line.
    nostage            [4a] makes no stage (with -WriteBack: the tool write-back [4], as production falls back)
    nostage_once       U3: no stage in a stage-only bake; with -WriteBack a stage + [4b] (the fallback's own direct writer works)
    nostage_once_slow4b  the same, the [4b] takes 5 s (a cancel during it is deferred)
    nodirect           U3: gpu_bake's pre-flight "-WriteBackMode direct cannot run": stage-only THROWS before any phase; with
                       -WriteBack it notes it, switches to tool mode and the tool write-back [4] writes the set
    gpu_bake_wait      U3: after a stage-only [4a] stage, write $W\fake_gpu_bake_waiting.flag and wait (<= 120 s) for
                       $W\fake_gpu_bake_go.flag (the test holds a file before the shell's install)
#>
param(
    [string]$Scenario = "",
    [string]$Bsp = "",
    [ValidateSet("low","super","custom")][string]$Quality = "super",
    [int]$Worker = 6,
    [switch]$WriteBack,
    [ValidateSet("tool","direct","both")][string]$WriteBackMode = "tool",
    [switch]$SkipCapture,
    [switch]$PluckerFix,
    [switch]$SceneryFix,
    [ValidateRange(1000000,100000000)][int]$Photons = 0,
    [ValidateRange(1,64)][int]$MCSamples = 0
)
$ErrorActionPreference = "Stop"
function Phase($n, $t) { Write-Host ""; Write-Host ("[{0}] {1}" -f $n, $t) }
function Note([string]$m) { Write-Host "      *** $m" }

foreach ($v in "H2LM_ROOT", "H2LM_WORK", "H2LM_CODE") {
    $x = [Environment]::GetEnvironmentVariable($v)
    if (-not $x -or -not (Test-Path -LiteralPath $x -PathType Container)) { throw "H2LM: $v is not an existing folder ('$x')" } }
if ($env:H2LM_NOWINDOW -ne "1") { throw "H2LM: H2LM_NOWINDOW is '$($env:H2LM_NOWINDOW)', the shell always sets 1" }
$ROOT = $env:H2LM_ROOT; $W = $env:H2LM_WORK; $WC = $env:H2LM_CODE
$OUT = Join-Path $W ("farm_w{0:D2}" -f $Worker)
$TW = Join-Path $PSScriptRoot "v043-tagwriter"
$scenRel = Split-Path $Scenario
$scenLeaf = Split-Path $Scenario -Leaf
$tagDirLive = Join-Path (Join-Path $ROOT "tags") $scenRel
$ltmpName = "{0}_{1}_lightmap" -f $scenLeaf, $Bsp
if ($Quality -eq "custom") { if (-not $Photons -or -not $MCSamples) { throw "-Quality custom needs -Photons and -MCSamples" } }
elseif ($Photons -or $MCSamples) { throw "-Photons / -MCSamples are only valid with -Quality custom" }

$mode = "ok"
$waitGo = $false
$ctl = Join-Path $W "fake_control.txt"
if (Test-Path -LiteralPath $ctl) { foreach ($l in Get-Content -LiteralPath $ctl) { if ($l -match '^gpu_bake=(\S+)') { $mode = $Matches[1] }; if ($l -match '^gpu_bake_wait\s*$') { $waitGo = $true } } }
$gp = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GPULM_*' } | ForEach-Object { "$($_.Name)=$($_.Value)" })
$dv = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'DEVP_*' -or $_.Name -like 'V370_*' } | ForEach-Object { $_.Name })   # review fix 19: must be empty
$rec = [ordered]@{ tool = "gpu_bake"; script = $PSCommandPath; argv = @([Environment]::GetCommandLineArgs()); bound = @($PSBoundParameters.Keys | Sort-Object | ForEach-Object { "$_=$($PSBoundParameters[$_])" });
                   cwd = (Get-Location).Path; h2lm_root = $ROOT; h2lm_work = $W; h2lm_code = $WC; gpulm = $gp; devp = $dv; pythonpath = $env:PYTHONPATH; mode = $mode;
                   secret_inherited = [bool][Environment]::GetEnvironmentVariable("H2LMTEST_API_TOKEN");
                   py = (Get-Command py -CommandType Application | Select-Object -First 1).Source; psver = "$($PSVersionTable.PSVersion)" }
Add-Content -LiteralPath (Join-Path $W "fake_calls.log") -Value ($rec | ConvertTo-Json -Compress -Depth 4)

Write-Host ("fake gpu_bake: {0} / {1}  worker farm_w{2:D2}  quality {3}  fixes {4}{5}" -f $Scenario, $Bsp, $Worker, $Quality, [bool]$PluckerFix, [bool]$SceneryFix)
# U3: the production pre-flight (before phase 1): with -WriteBack it falls back to tool mode, stage-only it throws
if ($mode -eq "nodirect" -and $WriteBackMode -ne "tool") {
    $m = "-WriteBackMode $WriteBackMode cannot run: fake: lm_tagwriter.py is missing"
    if ($WriteBack) { Note "$m -- using -WriteBackMode tool"; $WriteBackMode = "tool" }
    else { throw $m } }
if (-not $SkipCapture) {
    Phase 1 "CAPTURE - fake capture  [sever=3]"
    New-Item -ItemType Directory -Force -Path $OUT | Out-Null
    $crc = "{0:X8}" -f (Get-Random -Maximum 2147483647)
    Set-Content -LiteralPath (Join-Path $OUT "capture_id.json") -Value ('{"soup_crc32": "' + $crc + '", "stamped_by": "fake"}')
    $budget = if ($Quality -eq "custom") { $Photons } elseif ($Quality -eq "super") { 20000000 } else { 5000000 }
    Set-Content -LiteralPath (Join-Path $OUT "gpulm.log") -Value ("g_photon_budget={0}" -f $budget)
    if ($env:GPULM_SKY_HI -eq "1") {
        Set-Content -LiteralPath (Join-Path $OUT "sky_tex_hi.bin") -Value "sky hi"
        Set-Content -LiteralPath (Join-Path $OUT "lp_globals.bin") -Value "lp globals" }
    Note "fake capture $crc"
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $OUT "capture_id.json"))) { throw "PHASE 1: -SkipCapture but $OUT has no capture" }
    Note "reusing the capture in $OUT (-SkipCapture)"
}
Phase 2 "DERIVE - fake derive"
Phase 3 "GPU BAKE - fake bake"
if ($env:GPULM_PROBES) { Write-Host ("      PROBE: traced {0}" -f $env:GPULM_PROBES); Set-Content -LiteralPath (Join-Path $OUT "vertex_out.bin") -Value "v" }
if ($mode -eq "fail_bake") { throw "PHASE 3: the fake bake failed on purpose" }
if ($mode -eq "slow3") { Start-Sleep -Seconds 60; Write-Host "SHOULD NOT PRINT (slow3)" }

if ($WriteBackMode -ne "tool") {
    Phase "4a" ("TAG WRITER - fake -> $OUT\tagw_stage" + $(if ($WriteBack) { " -> install" } else { " (stage only; nothing is installed)" }))
    $noStage = $mode -eq "nostage" -or (($mode -eq "nostage_once" -or $mode -eq "nostage_once_slow4b") -and -not $WriteBack)
    if ($noStage) {
        Note "tag writer FAILED: fake skeleton refused"
        if ($WriteBack) { Note "direct tag writer FAILED -- falling back to the tool write-back: fake" }
    } else {
        $stage = Join-Path $OUT "tagw_stage"
        New-Item -ItemType Directory -Force -Path $stage, (Join-Path $OUT "skeleton") | Out-Null
        Set-Content -LiteralPath (Join-Path $OUT "skeleton\skel.txt") -Value "fake skeleton"
        $outs = [ordered]@{}
        foreach ($n in "$ltmpName.scenario_structure_lightmap", "${ltmpName}_bitmaps.bitmap", "${ltmpName}_truecolor_bitmaps.bitmap") {
            $live = Join-Path $tagDirLive $n
            $bytes = if (Test-Path -LiteralPath $live) { [IO.File]::ReadAllBytes($live) } else { [Text.Encoding]::ASCII.GetBytes("FAKE $n") }
            $p = Join-Path $stage $n
            [IO.File]::WriteAllBytes($p, $bytes + [Text.Encoding]::ASCII.GetBytes("|BAKED $Quality $Worker"))
            $outs[$n] = [ordered]@{ path = $p; md5 = (Get-FileHash -LiteralPath $p -Algorithm MD5).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $p).Length } }
        $report = [ordered]@{ writer = "fake"; status = "written"; outputs = $outs }
        Set-Content -LiteralPath (Join-Path $stage "tagw_report.json") -Value ($report | ConvertTo-Json -Depth 5)
        Write-Host "      stage written + validated: $stage"
        if ($waitGo -and -not $WriteBack) {
            Set-Content -LiteralPath (Join-Path $W "fake_gpu_bake_waiting.flag") -Value "waiting"
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-Path -LiteralPath (Join-Path $W "fake_gpu_bake_go.flag")) -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 100 }
            Write-Host "      (fake) go after $([int]$sw.Elapsed.TotalMilliseconds) ms" }
        if ($WriteBack -and $WriteBackMode -eq "direct") {
            Phase "4b" "INSTALL - back up the live set, marker, atomic per-file replace, verify"
            if ($mode -eq "slow4b" -or $mode -eq "nostage_once_slow4b") { Write-Host "      INSTALL: (fake) slow"; Start-Sleep -Seconds 5 }
            & py (Join-Path $TW "lmw_install.py") install --stage $stage --target-dir $tagDirLive --backup-dir (Join-Path $OUT "tag_backup_before_writeback") `
                 --auto-backup-root (Join-Path $W "_TAGBAK_auto") --marker (Join-Path $OUT "writeback_in_progress.txt") 2>&1 | ForEach-Object { "      $_" }
            $ix = $LASTEXITCODE
            if ($ix -eq 2) { throw "PHASE 4b: lmw_install.py REFUSED before changing anything" }
            if ($ix -ne 0) { throw "PHASE 4b: lmw_install.py FAILED AFTER A REPLACE (exit $ix)" }
            Write-Host "      direct write-back installed + verified"
        }
    }
}
if ($WriteBack -and ($mode -eq "nostage" -or $mode -eq "nodirect")) {
    Phase 4 "WRITE-BACK - fake tool write-back"
    # production: Start-OsoyTool "writeback" adds --quality-row P MC for -Quality custom (P2); the fake records what it got
    $row = if ($Quality -eq "custom") { " quality-row $Photons $MCSamples" } else { "" }
    foreach ($n in "$ltmpName.scenario_structure_lightmap", "${ltmpName}_bitmaps.bitmap") {
        $p = Join-Path $tagDirLive $n
        [IO.File]::WriteAllBytes($p, [Text.Encoding]::ASCII.GetBytes("TOOL WRITE-BACK $n $Quality$row")) }
}
Phase "4c" "VERIFY - fake"
exit 0
