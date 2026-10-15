"""Authoring helper (not shipped): the P2 edits (quality block rewrite = B1 fix, -Quality custom) on the engine_v373 copy,
each with an exact expected count. usage: py -I p2_edit.py <file>  (in place; refuses on any mismatch). CRLF files only."""
import sys

N = b'\r\n'


def L(*lines):
    return N.join(lines) + N


EDITS = [
    # 1. ValidateSet + a pointer comment
    (L(b'    [ValidateSet("low","super")][string]$Quality = "super",'),
     L(b'    # H2LM P2: custom = the tool\'s \'cuban\' row rewritten by osoy_launch.exe --quality-row (-Photons / -MCSamples, the last parameters)',
       b'    [ValidateSet("low","super","custom")][string]$Quality = "super",'), 1),
    # 2. the two new parameters, appended LAST so no existing positional parameter moves
    (L(b'    [ValidateRange(0,64)][int]$RescueSalt = 64', b')'),
     L(b'    [ValidateRange(0,64)][int]$RescueSalt = 64,',
       b"    # >>> H2LM P2 (standalone exe, 2026-10-14) CUSTOM QUALITY: -Quality custom -Photons P -MCSamples MC. The capture and the tool",
       b"    # write-back go through osoy_launch.exe --quality-row P MC, which rewrites the tool's 'cuban' quality row in memory (tool still",
       b"    # suspended) to {subpixel 4, MC, draft 0, P photons, 0, search 4.0, 0} -- super with only the photon budget and the secondary MC",
       b"    # changed, so Custom(20000000, 8) IS super -- and the capture token is 'cuban'. Both are required with custom, refused without it.",
       b"    [ValidateRange(1000000,100000000)][int]$Photons = 0,",
       b"    [ValidateRange(1,64)][int]$MCSamples = 0",
       b"    # <<< H2LM P2",
       b')'), 1),
    # 3. the argument check, before anything runs (right after the P3 block)
    (L(b'# <<< H2LM P3'),
     L(b'# <<< H2LM P3',
       b'# >>> H2LM P2 the custom-quality arguments, checked before anything runs',
       b'if ($Quality -eq "custom") {',
       b'    if (-not $Photons -or -not $MCSamples) { throw "-Quality custom needs -Photons <1000000..100000000> and -MCSamples <1..64>" }',
       b"    if ($CaptureQuality) { throw \"-Quality custom: -CaptureQuality is not allowed (the capture runs the custom row itself, token 'cuban')\" }",
       b'} elseif ($Photons -or $MCSamples) { throw "-Photons / -MCSamples are only valid with -Quality custom" }',
       b'# <<< H2LM P2'), 1),
    # 4. $osoyLaunch: the launch path (= $osoyOn with low / super)
    (L(b'$osoyOn      = [bool]$PluckerFix -or [bool]$SceneryFix'),
     L(b'$osoyOn      = [bool]$PluckerFix -or [bool]$SceneryFix',
       b'$osoyLaunch  = $osoyOn -or $Quality -eq "custom"     # H2LM P2: a custom capture / write-back runs through osoy_launch.exe (--quality-row) even with both fixes off'), 1),
    (L(b'if ($osoyOn) {', b'    foreach ($osoyK in $OSOY_PIN.Keys) {'),
     L(b'if ($osoyLaunch) {     # H2LM P2 (was $osoyOn; identical with low / super): the pins are checked whenever osoy_launch.exe will run',
       b'    foreach ($osoyK in $OSOY_PIN.Keys) {'), 1),
    # 5. --quality-row on every osoy_launch run of a custom bake except the skeleton
    (L(b"    if ($h2lmNoWin) { $la += '--nowindow' }     # H2LM P3: the tool gets CREATE_NO_WINDOW (never DETACHED_PROCESS: H2ToolHooks calls AllocConsole)"),
     L(b"    if ($h2lmNoWin) { $la += '--nowindow' }     # H2LM P3: the tool gets CREATE_NO_WINDOW (never DETACHED_PROCESS: H2ToolHooks calls AllocConsole)",
       b"    if ($Quality -eq \"custom\" -and $runTag -ne \"skeleton\") { $la += @('--quality-row', \"$Photons\", \"$MCSamples\") }     # H2LM P2: 'cuban' = {4, MC, 0, P, 0, 4.0, 0}; the skeleton stays 'low'"), 1),
    # 6. the post-capture verify function
    (L(b'# <<< v370 osoyoos fixes'),
     L(b'# <<< v370 osoyoos fixes',
       b"# >>> H2LM P2 a custom capture's own record of its quality, checked after phase 1 (and on -SkipCapture): a 'cuban' row the tool never",
       b"# got (the stock row {1, 1, 0, 50000, 0, 1.0, 0}: ssN 1, MC 1, 50,000 photons) or another quality's capture must never bake as custom",
       b'function Assert-CustomCapture([string]$dir) {',
       b'    foreach ($f in "$dir\\gather_dims.bin", "$dir\\raster_globals.bin", "$dir\\gpulm.log") {',
       b'        if (-not (Test-Path $f)) { throw "CUSTOM: $f is missing -- the capture\'s quality cannot be verified" } }',
       b'    $gd = [IO.File]::ReadAllBytes("$dir\\gather_dims.bin")',
       b'    $rg = [IO.File]::ReadAllBytes("$dir\\raster_globals.bin")',
       b'    $bad = @()',
       b'    if ($gd.Length -ne 12 -or [BitConverter]::ToUInt32($gd, 0) -ne 2) { $bad += "gather_dims.bin is not {u32 2, i32 main, i32 sec}" }',
       b'    elseif ([BitConverter]::ToInt32($gd, 4) -ne 2 * $MCSamples -or [BitConverter]::ToInt32($gd, 8) -ne $MCSamples) {',
       b'        $bad += ("gather_dims ({0}, {1}) is not ({2}, {3})" -f [BitConverter]::ToInt32($gd, 4), [BitConverter]::ToInt32($gd, 8), (2 * $MCSamples), $MCSamples) }',
       b'    if ($rg.Length -lt 28) { $bad += "raster_globals.bin is shorter than 7 floats" }',
       b'    elseif ([BitConverter]::ToSingle($rg, 24) -ne 4.0) { $bad += "raster ssN $([BitConverter]::ToSingle($rg, 24)) is not 4" }',
       b'    $bv = @(Select-String -Path "$dir\\gpulm.log" -Pattern "g_photon_budget=(\\d+)" | ForEach-Object { [long]$_.Matches[0].Groups[1].Value } | Sort-Object -Unique)',
       b"    if ($bv.Count -ne 1 -or $bv[0] -ne $Photons) { $bad += \"photon budget in gpulm.log: $(if ($bv.Count) { $bv -join ', ' } else { 'no g_photon_budget line' }) (expected $Photons)\" }",
       b'    if ($bad.Count) { throw ("CUSTOM: the capture in {0} is NOT custom P {1} / MC {2}: {3}" -f $dir, $Photons, $MCSamples, ($bad -join \'; \')) }',
       b'    Write-Host ("      custom capture verified: gather_dims ({0}, {1}), raster ssN 4, photon budget {2:n0}" -f (2 * $MCSamples), $MCSamples, $Photons) -ForegroundColor Green }',
       b'# <<< H2LM P2'), 1),
    # 7. the capture token
    (L(b'    $capQ = if ($CaptureQuality) { $CaptureQuality } else { $Quality }'),
     L(b'    $capQ = if ($CaptureQuality) { $CaptureQuality } elseif ($Quality -eq "custom") { "cuban" } else { $Quality }     # H2LM P2: custom captures on the rewritten \'cuban\' row'), 1),
    # 8. the v372W CPU-cast process is a plain launch (no --quality-row): never with custom
    (L(b'    elseif ($osoyOn) { Write-Host "      v372W: the Osoyoos hooks are ON -- the CPU-cast process is not supported with them yet (statistical suns keep the GPU lottery)" -ForegroundColor Yellow }'),
     L(b'    elseif ($osoyOn) { Write-Host "      v372W: the Osoyoos hooks are ON -- the CPU-cast process is not supported with them yet (statistical suns keep the GPU lottery)" -ForegroundColor Yellow }',
       b"    elseif ($Quality -eq \"custom\") { Write-Host \"      v372W: -Quality custom -- the CPU-cast process is not run (a plain launch has no --quality-row: it would cast the stock 'cuban' row); statistical suns keep the GPU lottery\" -ForegroundColor Yellow }     # H2LM P2"), 1),
    # 9. the capture launch path
    (L(b'    if ($osoyOn) { $p = Start-OsoyTool $a "capture" $OUT }      # v370: H2ToolHooks.dll in BEFORE the first instruction, proven'),
     L(b'    if ($osoyLaunch) { $p = Start-OsoyTool $a "capture" $OUT }      # v370: H2ToolHooks.dll in BEFORE the first instruction, proven (H2LM P2: $osoyLaunch)'), 1),
    # 10. the post-capture verify
    (L(b'    Assert-NoHookCollision "$OUT\\gpulm.log" "capture"     # v370 (a no-op with the switches off)'),
     L(b'    Assert-NoHookCollision "$OUT\\gpulm.log" "capture"     # v370 (a no-op with the switches off)',
       b'    if ($Quality -eq "custom") { Assert-CustomCapture $OUT }     # H2LM P2'), 1),
    # 11. the same verify on a reused capture
    (L(b'        else { Write-Host "      v370: the capture\'s Osoyoos fixes ($capDesc) match this bake" } } }'),
     L(b'        else { Write-Host "      v370: the capture\'s Osoyoos fixes ($capDesc) match this bake" } } }',
       b'if ($SkipCapture -and $Quality -eq "custom") { Assert-CustomCapture $OUT }     # H2LM P2: a reused capture must be this custom P / MC too'), 1),
    # 12. the write-back launch path
    (L(b'    if ($osoyOn) { $p = Start-OsoyTool $aFull "writeback" $OUT }      # v370: H2ToolHooks.dll in BEFORE the first instruction, proven'),
     L(b'    if ($osoyLaunch) { $p = Start-OsoyTool $aFull "writeback" $OUT }      # v370: H2ToolHooks.dll in BEFORE the first instruction, proven (H2LM P2: $osoyLaunch)'), 1),
    # 13. the quality block: independent ifs (B1)
    (L(b'if ($Quality -eq "super") { $env:GPULM_SS_N="4"; $env:GPULM_RAY_MULT="1"; $args2=@(16,8,4096) }'),
     L(b'# >>> H2LM P2 THE QUALITY BLOCK AS INDEPENDENT IFS (fixes B1: the low-quality `else` used to hang off the SURF_SAMPCHUNK `if` below, so a',
       b'# SUPER bake started with GPULM_SURF_SAMPCHUNK already in the environment lost SS_N / RAY_MULT / 16 8 4096 and baked at fullgpu\'s',
       b'# RAY_MULT default 2 = 512 rays/call, 4x the CPU). With GPULM_SURF_SAMPCHUNK unset -- the launcher (run_bake.ps1) clears every GPULM_*,',
       b'# the standalone builds the environment from scratch -- low and super set exactly what the two old lines set.',
       b'# custom: the super settings with the gather dims of MC -- main 2*MC, sec MC (= the capture\'s gather_dims.bin, verified after phase 1)',
       b'# -- and a fixed chunk of 524288 rays per dispatch clamped to 64..4096 calls: 16 8 4096 at MC 8 (= super), 32 16 1024 at MC 16, 128 64 64 at 64.',
       b'if ($Quality -eq "super") { $env:GPULM_SS_N="4"; $env:GPULM_RAY_MULT="1"; $args2=@(16,8,4096) }',
       b'if ($Quality -eq "custom") { $env:GPULM_SS_N="4"; $env:GPULM_RAY_MULT="1"',
       b'    $args2=@((2 * $MCSamples), $MCSamples, [Math]::Min(4096, [Math]::Max(64, [int][Math]::Floor(524288.0 / (2 * $MCSamples * $MCSamples))))) }',
       b"if ($Quality -eq \"low\") { Clear-Gpulm 'GPULM_SS_N','GPULM_RAY_MULT'; $args2=@() }",
       b'# <<< H2LM P2'), 1),
    (L(b"if (-not (Test-Path Env:GPULM_SURF_SAMPCHUNK) -and $Quality -eq \"super\") { $env:GPULM_SURF_SAMPCHUNK = \"16\" } else { Clear-Gpulm 'GPULM_SS_N','GPULM_RAY_MULT'; $args2=@() }"),
     L(b'if (-not (Test-Path Env:GPULM_SURF_SAMPCHUNK) -and $Quality -ne "low") { $env:GPULM_SURF_SAMPCHUNK = "16" }     # H2LM P2: super + custom; NO else (B1)'), 1),
]

p = sys.argv[1]
d = open(p, 'rb').read()
if d.count(b'\n') != d.count(b'\r\n'):
    sys.exit('REFUSED: %s is not uniformly CRLF' % p)
for i, (old, new, n) in enumerate(EDITS, 1):
    c = d.count(old)
    if c != n:
        sys.exit('REFUSED: edit %d: %r occurs %d times, expected %d' % (i, old[:90], c, n))
    d = d.replace(old, new)
open(p, 'wb').write(d)
print('P2: %d edits applied to %s' % (len(EDITS), p))
