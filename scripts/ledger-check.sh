#!/usr/bin/env bash
# The ledgers claim things about GitHub issues; an audit (2026-08-09) found them drifting
# in BOTH directions — open-threads said #98/#72 were closed while both were open, and
# GAP-LEDGER said #32/#33/#34 were open while all three had closed three weeks earlier.
# The asset's core promise is "nothing is postponed without landing in a ledger"; a ledger
# nobody cross-checks is worse than none, because it is trusted.
#
# This asserts the one thing a human cannot keep straight: every issue a ledger REFERENCES
# must have the state the ledger implies. Rules:
#   - a row saying "ISSUE #n" / "tracked in #n"  → #n must be OPEN
#   - a row saying "closed"/"CLOSED"/"RESOLVED" beside #n → #n must be CLOSED
set -euo pipefail
cd "$(dirname "$0")/.."

LEDGERS=(docs/strategy/open-threads.md samples/GAP-LEDGER.md docs/strategy/master-plan-2026-08.md docs/strategy/ga-gate-status.md)
FAILED=0

if ! command -v gh >/dev/null 2>&1 || ! gh auth status >/dev/null 2>&1; then
  echo "ledger-check: no authenticated gh — skipped honestly (the offline/air-gapped path)."
  exit 0
fi

for ledger in "${LEDGERS[@]}"; do
  [ -f "$ledger" ] || continue
  while IFS= read -r line; do
    # Every issue number referenced on this line.
    numbers=$(echo "$line" | grep -oE 'issues/[0-9]+|#[0-9]+' | grep -oE '[0-9]+' | sort -u || true)
    if [ -z "$numbers" ]; then continue; fi
    # What does the line CLAIM? (closed/resolved wins over open, e.g. "RESOLVED — #32 closed")
    # `cmd && var=x` returns 1 when cmd fails, which under `set -e` kills the script
    # SILENTLY — the first version of this gate did exactly that and reported nothing
    # while exiting 1. An if/then says what it means and cannot misfire.
    claim=""
    if echo "$line" | grep -qiE 'ISSUE \[?#|tracked in \[?#'; then claim="open"; fi
    if echo "$line" | grep -qiE 'closed|resolved|fixed'; then claim="closed"; fi
    if [ -z "$claim" ]; then continue; fi
    for n in $numbers; do
      state=$(gh issue view "$n" --json state --jq .state 2>/dev/null || echo "MISSING")
      if [ "$state" = "MISSING" ]; then continue; fi   # a PR number or another repo's issue
      actual=$([ "$state" = "OPEN" ] && echo "open" || echo "closed")
      if [ "$actual" != "$claim" ]; then
        echo "ledger-check: $ledger claims #$n is $claim, GitHub says $actual"
        echo "    $(echo "$line" | cut -c1-140)"
        FAILED=1
      fi
    done
  done < "$ledger"
done

if [ "$FAILED" -ne 0 ]; then
  echo "ledger-check: FAILED — reconcile the ledger with GitHub (or close/reopen the issue)."
  exit 1
fi
echo "ledger-check: every issue referenced by a ledger has the state the ledger claims."
