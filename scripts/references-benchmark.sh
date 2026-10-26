#!/usr/bin/env bash
# Score figure/table reference resolution against the labels in tests/references/.
# Usage: scripts/references-benchmark.sh [extra args for `references`, e.g. --min-precision 0.95]
set -euo pipefail
cd "$(dirname "$0")/.."
status=0
for labels in tests/references/*.labels.json; do
  name=$(basename "$labels" .labels.json)
  pdf="experiments/PDFs/$name.pdf"
  [ -f "$pdf" ] || { echo "skip $name (no $pdf)"; continue; }
  echo "== $name"
  rc=0
  dotnet run -c Release --project src/RailReader2.Cli -- references "$pdf" \
    --layout heron --ground-truth "$labels" --output /dev/null "$@" 2>&1 \
    | { grep -vE "Analysing page|written to" || true; } || rc=$?
  rc=${PIPESTATUS[0]:-$rc}
  [ "$rc" -eq 0 ] || status=$rc
done
exit $status
