#!/usr/bin/env bash
# Bulk RFC §6 performance proofs: 10k-file intake (the finance card's number) and 100k-row
# execute throughput; and the 10M-row device-list intake (#300: upload → validate → report,
# rows/s, peak managed heap, database size). Real PostgreSQL via Testcontainers; record the
# numbers in packages/Goldpath.Bulk/ops/bulk-benchmarks.md.
#
#   scripts/bench-bulk.sh            # everything (the 10M intake takes about 15 minutes)
#   scripts/bench-bulk.sh card       # the 10k intake and the 100k execute only
#   scripts/bench-bulk.sh intake     # the 10M intake only
#
# Intake knob (environment): GOLDPATH_BENCH_BULK_INTAKE_ROWS (10000000).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
cd "$ROOT"
case "${1:-all}" in
  all)    FILTER="FullyQualifiedName~BulkBenchTests|FullyQualifiedName~BulkIntakeBenchTests" ;;
  card)   FILTER="FullyQualifiedName~BulkBenchTests" ;;
  intake) FILTER="FullyQualifiedName~BulkIntakeBenchTests" ;;
  *) echo "usage: $0 [all|card|intake]" >&2; exit 2 ;;
esac
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" \
  | grep -E "BENCH-BULK|Passed!|Failed"
