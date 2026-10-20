#!/usr/bin/env bash
# Mutation gate (foundation §8.2: "an ungameable metric"): every package below runs Stryker
# with break-at 70 — below the threshold, no merge. Usage: mutation-gate.sh [PackageName]
#
# Iteration mode: GOLDPATH_MUTATE_SINCE=origin/main mutation-gate.sh Goldpath.X mutates ONLY the
# files changed since that git target (minutes instead of hours). Diff mode has a blind
# spot — deleting a test that guarded UNCHANGED code goes unseen — so the AUTHORITATIVE
# score stays the full run (CI on main / the pre-MR local pass).
#
# Scope: Stryker runs FROM THE PACKAGE'S OWN DIRECTORY, never from the repository root. At the
# root it finds Goldpath.sln and switches to solution mode, which ignores `test-projects` and
# runs every test project whose build references the package — for Abstractions 702 tests
# instead of its own 9, the integration host included (measured 2026-10-19, Stryker 4.16 and
# 5.0). A config's `test-projects` are therefore relative to the package directory
# (`../../tests/...`), and the gate fails a run whose log shows the scope was not honored.
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
  # The package directory (the one holding the config's `project`) and the number of test
  # projects the config names — the scope the run must report back.
  read -r project_dir expected < <(python3 - "$ROOT" "$cfg" <<'PY'
import json, os, subprocess, sys
root, cfg = sys.argv[1], sys.argv[2]
c = json.load(open(os.path.join(root, cfg)))["stryker-config"]
hits = subprocess.run(["git", "-C", root, "ls-files", "*/" + c["project"]],
                      capture_output=True, text=True).stdout.split()
print(os.path.dirname(hits[0]) if len(hits) == 1 else "-", len(c.get("test-projects", [])))
PY
  )
  if [ "$project_dir" = "-" ] || [ "$expected" -lt 1 ]; then
    echo "   $cfg must name exactly one tracked project and at least one test project"
    FAILED+=("$name"); continue
  fi
  started=$SECONDS
  # shellcheck disable=SC2086 — each flag is a single token by construction
  if ! (cd "$ROOT/$project_dir" && dotnet stryker -f "$ROOT/$cfg" $SINCE_FLAG $CONCURRENCY_FLAG) 2>&1 \
      | tee "$log" | { grep --line-buffered -E "$SCOPE_LINES" || true; }; then
    FAILED+=("$name")
  fi
  elapsed=$(( SECONDS - started ))
  echo "   $name took $(( elapsed / 60 ))m $(( elapsed % 60 ))s (full log: StrykerOutput/gate-$name.log)"
  # A score is only as good as its scope: solution mode, or a test-project count other than
  # the config's, means the tests behind the number are not the ones the config names.
  if grep -q "Stryker will mutate solution" "$log"; then
    echo "   SCOPE RED: Stryker ran in solution mode and ignored the config's test-projects"
    FAILED+=("$name(scope)")
  elif ! grep -q "Analyzing $expected test project(s)" "$log"; then
    echo "   SCOPE RED: the config names $expected test project(s); the log does not say Stryker analyzed them"
    FAILED+=("$name(scope)")
  fi
done

if [ ${#FAILED[@]} -gt 0 ]; then
  echo "── MUTATION GATE RED: ${FAILED[*]}"
  exit 1
fi
echo "── MUTATION GATE GREEN"
