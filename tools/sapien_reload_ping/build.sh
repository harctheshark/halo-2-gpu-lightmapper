#!/usr/bin/env bash
# Builds tools/sapien_reload_ping/sapien_reload_ping.exe with the .NET Framework csc (no SDK needed, no window; sub-second).
HERE="$(cd "$(dirname "$0")" && pwd)"
"/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" -nologo -target:exe -optimize+ \
  "-out:$(cygpath -w "$HERE/sapien_reload_ping.exe")" \
  "$(cygpath -w "$HERE/Program.cs")" "$(cygpath -w "$HERE/../../src/H2GpuLightmapper.Core/Safety/SapienReloadPing.cs")"
