#!/usr/bin/env bash
# Approvals' first benchmark table (GA gate G13, #596): request throughput, decide throughput through a two-signer
# quorum with four-eyes live, worklist latency for one role, and one escalation sweep. Real PostgreSQL via
# Testcontainers; record the numbers in packages/Goldpath.Approvals/ops/approvals-benchmarks.md.
#
#   scripts/bench-approvals.sh
#
# Knob (environment): GOLDPATH_BENCH_APPROVALS_N (10000) — requests per proof.
# Override the class only to prove the refusal: GOLDPATH_BENCH_APPROVALS_CLASS (ApprovalsBenchTests).
#
# The run is refused unless it printed every proof's BENCH-APPROVALS line, so a filter that matched nothing (a renamed
# class, a skipped fact) can never pass as a measurement.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
cd "$ROOT"
CLASS="${GOLDPATH_BENCH_APPROVALS_CLASS:-ApprovalsBenchTests}"
OUT=$(mktemp)
trap 'rm -f "$OUT"' EXIT
set +e
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "FullyQualifiedName~Integration.$CLASS." \
  --logger "console;verbosity=detailed" > "$OUT" 2>&1
STATUS=$?
set -e
grep -aE "BENCH-APPROVALS|Passed!|Failed|No test matches" "$OUT" || true
if [ "$STATUS" -ne 0 ]; then
  exit "$STATUS"
fi
missing=""
for proof in request decide worklist escalate; do
  if ! grep -aqE "BENCH-APPROVALS $proof " "$OUT"; then
    missing="$missing $proof"
  fi
done
if [ -n "$missing" ]; then
  echo "bench-approvals: no BENCH-APPROVALS line for:$missing; nothing was measured for it" >&2
  exit 1
fi
