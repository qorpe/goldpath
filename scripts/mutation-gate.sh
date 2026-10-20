#!/usr/bin/env bash
# Mutation gate (foundation §8.2: "an ungameable metric"): every package below runs Stryker
# with break-at 70 — below the threshold, no merge. Usage: mutation-gate.sh [PackageName]
#
# Iteration mode: GOLDPATH_MUTATE_SINCE=origin/main mutation-gate.sh Goldpath.X mutates ONLY the
# files changed since that git target (minutes instead of hours). Diff mode has a blind
# spot — deleting a test that guarded UNCHANGED code goes unseen — so the AUTHORITATIVE
# score stays the full run (CI on main / the pre-MR local pass).
set -uo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

cd "$ROOT"
# A package argument that matches no config is a typo, and a typo must not print GREEN:
# `mutation-gate.sh Jobs` (config: Goldpath.Jobs) once ran nothing and reported a pass.
if [ $# -ge 1 ] && [ ! -f "stryker/$1.json" ]; then
  echo "── MUTATION GATE: no config stryker/$1.json — package names are Goldpath.<Name>; nothing ran."
  exit 2
fi
FAILED=()
SCOPE_LINES='Stryker will mutate solution|Analyzing [0-9]+ test project|Building (solution|test project)|Found project .* to mutate|Number of tests found|mutants created|mutants will be tested|mutation score|threshold| (ERR|FTL)\]'
for cfg in stryker/*.json; do
  name=$(basename "$cfg" .json)
  if [ $# -ge 1 ] && [ "$name" != "$1" ]; then continue; fi
  echo "── mutating $name"
  SINCE_FLAG=""
  if [ -n "${GOLDPATH_MUTATE_SINCE:-}" ]; then
    SINCE_FLAG="--since:${GOLDPATH_MUTATE_SINCE}"
    echo "   (diff mode: only changes since $GOLDPATH_MUTATE_SINCE — full run stays the authoritative score)"
  fi
  CONCURRENCY_FLAG=""
  if [ -n "${GOLDPATH_MUTATION_CONCURRENCY:-}" ]; then
    CONCURRENCY_FLAG="--concurrency ${GOLDPATH_MUTATION_CONCURRENCY}"
  fi
  # The full Stryker log lands beside its reports; the console gets the lines that say WHAT
  # ran — the scope (solution or test projects), the tests found, the mutant count — and the
  # verdict. Until 2026-10-20 only the last four lines survived, so no nightly log could show
  # which test projects a score came from.
  mkdir -p "$ROOT/StrykerOutput"
  log="$ROOT/StrykerOutput/gate-$name.log"
  started=$SECONDS
  # shellcheck disable=SC2086 — each flag is a single token by construction
  if ! dotnet stryker -f "$cfg" $SINCE_FLAG $CONCURRENCY_FLAG 2>&1 | tee "$log" \
      | { grep --line-buffered -E "$SCOPE_LINES" || true; }; then
    FAILED+=("$name")
  fi
  echo "   $name took $(( (SECONDS - started) / 60 )) min (full log: StrykerOutput/gate-$name.log)"
done

if [ ${#FAILED[@]} -gt 0 ]; then
  echo "── MUTATION GATE RED: ${FAILED[*]}"
  exit 1
fi
echo "── MUTATION GATE GREEN"
