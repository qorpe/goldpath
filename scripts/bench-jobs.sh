#!/usr/bin/env bash
# Jobs RFC §6 performance proofs (module excellence bar): runs the Bench-trait tests and
# prints the numbers to record in ops/jobs-benchmarks.md. Real PostgreSQL via Testcontainers.
#
# Provider (#556, G7 Oracle 8/12): GOLDPATH_BENCH_PROVIDER=postgres (the default: every PostgreSQL
# Category=Bench class, as before) or oracle (JobsBenchOracleTests on the Oracle collection's pinned
# container). An Oracle run is refused unless every BENCH line it printed says `provider=oracle`.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
cd "$ROOT"
PROVIDER="${GOLDPATH_BENCH_PROVIDER:-postgres}"
case "$PROVIDER" in
  # PostgreSQL: every Bench class that is not an Oracle twin, exactly the set this script ran before #556.
  postgres) FILTER="Category=Bench&Category!=Oracle" ;;
  oracle)   FILTER="FullyQualifiedName~Integration.JobsBenchOracleTests." ;;
  *) echo "GOLDPATH_BENCH_PROVIDER must be postgres or oracle, not '$PROVIDER'" >&2; exit 2 ;;
esac
OUT=$(mktemp)
trap 'rm -f "$OUT"' EXIT
set +e
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" > "$OUT" 2>&1
STATUS=$?
set -e
grep -aE "BENCH|Passed!|Failed" "$OUT" || true
if [ "$STATUS" -ne 0 ]; then
  exit "$STATUS"
fi
if [ "$PROVIDER" = oracle ]; then
  if ! grep -aqE "^\s*BENCH" "$OUT" || grep -aE "^\s*BENCH" "$OUT" | grep -avq "provider=oracle"; then
    echo "bench-jobs: GOLDPATH_BENCH_PROVIDER=oracle, but the run printed no Oracle bench line; nothing was measured on Oracle" >&2
    exit 1
  fi
fi
