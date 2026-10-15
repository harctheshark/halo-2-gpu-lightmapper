#!/usr/bin/env bash
# S1.9 payload builder: build (stage -> zips + MANIFEST.json -> Release exe embedding the payload), then run the tests.
# From the Bash tool only. No GPU, no bake, no tool_fast run, no live-tag write; production is only read. BELOW_NORMAL, no window.
#   bash tests/s19_payload/run_tests.sh [--no-build] [--with-custom-engine | --no-custom-engine] [--only name,name] [--keep]
# V2 (T3a): by default the build ships engine_custom/ (the N3 guarded fullgpu) exactly when tools/payload/custom_engine_ready.py
# says the T3a PASS record matches the production fullgpu on disk now (an engine promotion turns it off again until T3a is
# re-run); --with-custom-engine forces the request (stage_engine still refuses without the record), --no-custom-engine omits it.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"
PYEXE='/c/Program Files/Python310/python.exe'
BUILD=1
CUSTOM=auto
ARGS=()
for a in "$@"; do
  case "$a" in
    --no-build) BUILD=0 ;;
    --with-custom-engine) CUSTOM=on ;;
    --no-custom-engine) CUSTOM=off ;;
    *) ARGS+=("$a") ;;
  esac
done
if [ "$BUILD" = "1" ]; then
  if [ "$CUSTOM" = "auto" ]; then
    if "$PYEXE" -I -B "$PROJ/tools/payload/custom_engine_ready.py"; then CUSTOM=on; else CUSTOM=off; fi
  fi
  if [ "$CUSTOM" = "on" ]; then
    "$PYEXE" -I -B "$PROJ/tools/payload/build_payload.py" --with-custom-engine || exit $?
  else
    "$PYEXE" -I -B "$PROJ/tools/payload/build_payload.py" || exit $?
  fi
fi
mkdir -p "$HERE/results"
"$PYEXE" -I -B "$HERE/test_payload.py" ${ARGS[@]+"${ARGS[@]}"} | tee "$HERE/results/last_run.txt"
exit "${PIPESTATUS[0]}"
