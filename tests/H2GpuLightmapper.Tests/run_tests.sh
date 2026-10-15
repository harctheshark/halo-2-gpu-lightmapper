#!/usr/bin/env bash
# S2.5 + S3.1-S3.4: build the shell exe + the tests offline (BELOW_NORMAL, no build servers) and run every test hidden at
# BELOW_NORMAL. From the Bash tool only. No GPU, no bake, no live-tag write (sandbox H2EK roots only); production is only read.
#   bash tests/H2GpuLightmapper.Tests/run_tests.sh [--filter text] [--keep]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"
PYEXE='/c/Program Files/Python310/python.exe'
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
(cd "$HERE" && cmd //c "start /b /belownormal /wait dotnet build -c Release -nodeReuse:false --disable-build-servers -p:UseSharedCompilation=false -v:q -clp:NoSummary") || exit 1
EXE="$(cygpath -w "$HERE/bin/Release/net48/H2GpuLightmapper.Tests.exe")"
mkdir -p "$PROJ/build/s25"
"$PYEXE" -I "$PROJ/src/spike_s01_wpf/tools/run_bn.py" -- "$EXE" "$@" | tee "$PROJ/build/s25/last_run.txt"
exit "${PIPESTATUS[0]}"
