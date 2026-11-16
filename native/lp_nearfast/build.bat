@echo off
rem builds lpnearfast.dll (x64, for the 64-bit python) from lpnearfast.c -- the search loop of lp_nearpoint.py's surface_terms()
rem /fp:precise + #pragma fp_contract(off) in the source: single float32 operations, no FMA; /MT: no runtime DLL beside it
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>nul
cd /d "%~dp0"
cl /nologo /O2 /fp:precise /W3 /MT /LD lpnearfast.c /Fe:lpnearfast.dll /link /DLL > build.log 2>&1
if errorlevel 1 ( type build.log & echo BUILD FAILED & exit /b 1 )
del /q lpnearfast.obj lpnearfast.exp lpnearfast.lib 2>nul
echo BUILD OK
