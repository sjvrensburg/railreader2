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
  dotnet run -c Release --project src/RailReader2.Cli -- references "$pdf" \
    --layout heron --ground-truth "$labels" "$@" || status=$?
done
exit $status
