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
#
# Provider (#556, G7 Oracle 8/12): GOLDPATH_BENCH_PROVIDER=postgres (the default) or oracle. Oracle runs the
# same facts on the Oracle collection's pinned container (OracleFixture.Image), and the run is refused unless
# every bench line it printed says `provider=oracle`, so an Oracle run can never silently measure PostgreSQL.
#   GOLDPATH_BENCH_PROVIDER=oracle GOLDPATH_BENCH_BULK_INTAKE_ROWS=1000000 scripts/bench-bulk.sh
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
cd "$ROOT"
PROVIDER="${GOLDPATH_BENCH_PROVIDER:-postgres}"
case "$PROVIDER" in
  postgres) CARD="BulkBenchTests"; INTAKE="BulkIntakeBenchTests" ;;
  oracle)   CARD="BulkBenchOracleTests"; INTAKE="BulkIntakeBenchOracleTests" ;;
  *) echo "GOLDPATH_BENCH_PROVIDER must be postgres or oracle, not '$PROVIDER'" >&2; exit 2 ;;
esac
case "${1:-all}" in
  all)    FILTER="FullyQualifiedName~Integration.$CARD.|FullyQualifiedName~Integration.$INTAKE." ;;
  card)   FILTER="FullyQualifiedName~Integration.$CARD." ;;
  intake) FILTER="FullyQualifiedName~Integration.$INTAKE." ;;
  *) echo "usage: $0 [all|card|intake]" >&2; exit 2 ;;
esac
OUT=$(mktemp)
trap 'rm -f "$OUT"' EXIT
set +e
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" > "$OUT" 2>&1
STATUS=$?
set -e
grep -aE "BENCH-BULK|Passed!|Failed" "$OUT" || true
if [ "$STATUS" -ne 0 ]; then
  exit "$STATUS"
fi
if [ "$PROVIDER" = oracle ]; then
  # Every bench line must say it measured Oracle, and there must be at least one.
  if ! grep -aq "BENCH-BULK" "$OUT" || grep -a "BENCH-BULK" "$OUT" | grep -avq "provider=oracle"; then
    echo "bench-bulk: GOLDPATH_BENCH_PROVIDER=oracle, but the run printed no Oracle bench line; nothing was measured on Oracle" >&2
    exit 1
  fi
fi
