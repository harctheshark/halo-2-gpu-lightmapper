@echo off
rem S1.3 / P4 build (H2 GPU Lightmapper standalone). Same compiler + flags as production osoyoos\build_osoy_launch_new.bat
rem (vcvarsall x86, cl /O2 /W3 /MT /D_CRT_SECURE_NO_WARNINGS, /SUBSYSTEM:CONSOLE kernel32.lib), but every output goes into
rem THIS project only:  build\out\osoy_launch.exe  (+ build\osoy_selftest\fake_tool_p4.exe, the self-test target).
rem It NEVER writes into G:\...\GPU Lightmap Work\osoyoos\ -- the production osoy_launch.exe (c304184e) is never replaced.
rem x86: tool_fast.exe is x86 and the helper takes ntdll!LdrLoadDll from its OWN (WOW64) ntdll.
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul || exit /b 1
set "HERE=%~dp0"
set "PROJ=%~dp0..\.."
set "OUT=%PROJ%\build\out"
set "OBJ=%PROJ%\build\obj\osoy_launch"
set "ST=%PROJ%\build\osoy_selftest"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%ST%" mkdir "%ST%"
rem /Brepro (compiler + linker): reproducible output -- the PE timestamp/GUIDs become content hashes, the code is identical --
rem so a rebuild of the same source with the same toolset gives the SAME md5, which gpu_bake.ps1 pins (S1.2) and the payload
rem builder (S1.9) re-checks.
cl /nologo /O2 /W3 /MT /D_CRT_SECURE_NO_WARNINGS /Brepro "%HERE%osoy_launch.c" /Fo"%OBJ%\\" /Fe:"%OUT%\osoy_launch.exe" /link /SUBSYSTEM:CONSOLE /Brepro kernel32.lib >"%OBJ%\build_osoy_launch_p4.log" 2>&1
if errorlevel 1 ( type "%OBJ%\build_osoy_launch_p4.log" & echo BUILD FAILED osoy_launch & exit /b 1 )
type "%OBJ%\build_osoy_launch_p4.log"
cl /nologo /O1 /W3 /MT /Brepro "%PROJ%\tests\osoy_launch\fake_tool_p4.c" /Fo"%OBJ%\\" /Fe:"%ST%\fake_tool_p4.exe" /link /BASE:0x400000 /FIXED /DYNAMICBASE:NO /SUBSYSTEM:CONSOLE /Brepro user32.lib > "%OBJ%\build_fake_tool_p4.log" 2>&1
if errorlevel 1 ( type "%OBJ%\build_fake_tool_p4.log" & echo BUILD FAILED fake_tool_p4 & exit /b 1 )
echo BUILD OK
