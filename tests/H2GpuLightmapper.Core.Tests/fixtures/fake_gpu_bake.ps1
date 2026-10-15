<#
  S2.3 test stand-in for INST\engine_v373\gpu_bake.ps1 (shipped in the TEST payload only). It prints what the real script
  prints in the shapes the shell parses: phase banners "[n] title" (Phase), "      *** note" (Note), "GUARD: *** FAIL",
  "INSTALL:" lines, and a thrown error (the pwsh error block on stderr). It never touches tags, the GPU or the tool.
#>
param(
    [string]$Scenario = "",
    [string]$Bsp = "",
    [int]$Worker = 200,
    [ValidateSet("ok", "fail", "exit7", "env", "cancel3", "defer4b", "tree")][string]$Mode = "ok",
    [string]$PidFile = ""
)
$ErrorActionPreference = "Stop"
function Phase($n, $t) { Write-Host ""; Write-Host ("[{0}] {1}" -f $n, $t) -ForegroundColor Cyan }
function Note([string]$m) { Write-Host "      *** $m" -ForegroundColor Yellow }

switch ($Mode) {
    "ok" {
        Phase 1 "CAPTURE - fake capture  [sever=3]"
        Note "fake note one"
        Phase 2 "DERIVE - fake derive"
        Phase 3 "GPU BAKE - fake bake"
        Phase "4a" "TAG WRITER - fake stage (stage only; nothing is installed)"
        Write-Host ("      worker farm_w{0:D2} scenario {1} bsp {2}" -f $Worker, $Scenario, $Bsp)
        exit 0
    }
    "fail" {
        Phase 1 "CAPTURE - fake capture"
        Write-Host "      GUARD: *** FAIL fake guard message"
        throw "PHASE 1: the fake capture failed on purpose"
    }
    "exit7" { Phase 1 "CAPTURE - fake"; exit 7 }
    "env" {
        Phase 1 "ENV - report"
        foreach ($n in @("H2LM_ROOT", "H2LM_WORK", "H2LM_CODE", "H2LM_NOWINDOW", "PYTHONIOENCODING", "PYTHONDONTWRITEBYTECODE",
                         "PSModulePath", "PSExecutionPolicyPreference", "NO_COLOR", "PYTHONPATH", "PYTHONHOME")) {
            Write-Host ("ENV {0}={1}" -f $n, [Environment]::GetEnvironmentVariable($n)) }
        $g = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GPULM_*' -or $_.Name -like 'OSOYOOS_*' })
        Write-Host ("ENV pipeline-vars-count={0}" -f $g.Count)
        Write-Host ("ENV PATH={0}" -f $env:PATH)
        Write-Host ("ENV py={0}" -f (Get-Command py -CommandType Application | Select-Object -First 1).Source)
        Write-Host ("ENV pwsh={0} {1}" -f $PSVersionTable.PSVersion, $PSHOME)
        $pyv = & py -c "import sys,numpy;print(sys.executable, numpy.__version__, sys.flags.isolated)"
        Write-Host ("ENV pyver={0}" -f $pyv)
        Write-Host ("ENV cwd={0}" -f (Get-Location).Path)
        exit 0
    }
    "cancel3" {
        Phase 1 "CAPTURE - fake"
        Phase 3 "GPU BAKE - fake long bake"
        Start-Sleep -Seconds 60
        Write-Host "SHOULD NOT PRINT (cancel3)"
        exit 0
    }
    "defer4b" {
        Phase 3 "GPU BAKE - fake"
        Phase "4b" "INSTALL - back up the live set, marker, atomic per-file replace, verify"
        Write-Host "      INSTALL: writing (fake)"
        Start-Sleep -Seconds 4
        Write-Host "      INSTALL: installed and verified 3 file(s) (fake)"
        Phase "4c" "A/B - fake"
        Start-Sleep -Seconds 60
        Write-Host "SHOULD NOT PRINT (defer4b)"
        exit 0
    }
    "tree" {
        Phase 3 "GPU BAKE - a fake process tree"
        $py = (Get-Command py -CommandType Application | Select-Object -First 1).Source
        $p = Start-Process -FilePath $py -ArgumentList '-c', '"import time; time.sleep(120)"' -WindowStyle Hidden -PassThru
        if ($PidFile) { Set-Content -LiteralPath $PidFile -Value $p.Id }
        Write-Host ("      started grandchild {0}" -f $p.Id)
        & py -c "import time; print('blocking child up', flush=True); time.sleep(120)"
        Write-Host "SHOULD NOT PRINT (tree)"
        exit 0
    }
}
