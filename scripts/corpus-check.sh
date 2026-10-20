#!/usr/bin/env bash
# The corpus gate: every valid corpus entry must PASS schema validation and every invalid one
# must FAIL — the negative claims are executed, not assumed. Uses the published specdrift
# tool (pin it before calling: dotnet tool install -g specdrift).
#
# Three schema families are gated, each with its own valid/ + invalid/ corpus:
#   - the manifest            schemas/manifest/v1           (one schema)
#   - specanchor's artefacts  schemas/specanchor/v1         (rule / ledger-term / spec, chosen
#                                                            by the entry's file-name prefix)
#   - authoring's decisions   schemas/authoring/v1          (goldpath-authoring RFC V1)
#
# An invalid entry is proven only when specdrift REJECTS it with findings (exit 1). Exit 2 is
# specdrift saying it could not judge at all — an unknown schema keyword, an unreadable file —
# and counting that as "correctly rejected" would let a broken schema pass its own negative
# corpus. A YAML invalid entry also names its planted defect on a `# expect: <text>` line,
# and the findings must contain that text: an entry that fails for a DIFFERENT reason than
# the one it was written to plant proves nothing about that reason.
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
command -v specdrift >/dev/null 2>&1 || { echo "corpus-check: specdrift not on PATH (dotnet tool install -g specdrift)"; exit 1; }

# The schema an entry is judged against. specanchor's corpus is one folder for three schemas,
# so the file name says which (rule-*, ledger-term-*, spec-*); an entry matching none is a
# corpus defect, not a pass.
schema_for() {
  local family=$1 name
  name=$(basename "$2")
  case "$family" in
    manifest) echo "$ROOT/schemas/manifest/v1/goldpath-manifest.schema.json" ;;
    authoring) echo "$ROOT/schemas/authoring/v1/decisions.schema.json" ;;
    specanchor)
      case "$name" in
        rule-*) echo "$ROOT/schemas/specanchor/v1/rule.schema.v1.json" ;;
        ledger-term-*) echo "$ROOT/schemas/specanchor/v1/ledger-term.schema.v1.json" ;;
        spec-*) echo "$ROOT/schemas/specanchor/v1/spec.schema.v1.json" ;;
        *) echo "" ;;
      esac ;;
  esac
}

PASS=0; FAIL=0
for family in manifest specanchor authoring; do
  CORPUS="$ROOT/schemas/$family/v1/corpus"
  echo "── $family"

  # The tool must actually RUN, not merely exist. `specdrift` is a dotnet tool shim: on a
  # machine whose SDK is missing or on the wrong PATH it launches, fails, and exits non-zero
  # — which a naive invalid half reads as "correctly rejected" and reports as PASS. So the
  # shim is proven to work on a KNOWN-GOOD entry of each corpus before anything is judged.
  probe=$(ls "$CORPUS"/valid/*.json "$CORPUS"/valid/*.yaml 2>/dev/null | head -1)
  [ -n "$probe" ] || { echo "corpus-check: $family's valid corpus is empty — there is nothing to prove"; exit 1; }
  if ! specdrift validate "$probe" --schema "$(schema_for "$family" "$probe")" >/dev/null 2>&1; then
    echo "corpus-check: specdrift cannot validate a known-good $family entry — the tool itself is broken here:"
    specdrift validate "$probe" --schema "$(schema_for "$family" "$probe")" 2>&1 | tail -n 8 | sed 's/^/  | /'
    exit 1
  fi

  for f in "$CORPUS"/valid/*.json "$CORPUS"/valid/*.yaml; do
    [ -e "$f" ] || continue
    schema=$(schema_for "$family" "$f")
    if [ -z "$schema" ]; then
      echo "  FAIL valid/$(basename "$f") names no schema (rule-*, ledger-term-* or spec-*)"; FAIL=$((FAIL+1)); continue
    fi
    if specdrift validate "$f" --schema "$schema" >/dev/null 2>&1; then
      echo "  PASS valid/$(basename "$f") validates"; PASS=$((PASS+1))
    else
      echo "  FAIL valid/$(basename "$f") should validate but does not:"
      specdrift validate "$f" --schema "$schema" 2>&1 | tail -n 5 | sed 's/^/       | /'
      FAIL=$((FAIL+1))
    fi
  done

  for f in "$CORPUS"/invalid/*.json "$CORPUS"/invalid/*.yaml; do
    [ -e "$f" ] || continue
    name="invalid/$(basename "$f")"
    schema=$(schema_for "$family" "$f")
    if [ -z "$schema" ]; then
      echo "  FAIL $name names no schema (rule-*, ledger-term-* or spec-*)"; FAIL=$((FAIL+1)); continue
    fi
    expect=""
    if [ "${f##*.}" = "yaml" ]; then
      expect=$(sed -n 's/^# expect: //p' "$f" | head -1)
      if [ -z "$expect" ]; then
        echo "  FAIL $name carries no '# expect: <text>' line naming its planted defect"; FAIL=$((FAIL+1)); continue
      fi
    fi
    out=$(specdrift validate "$f" --schema "$schema" 2>&1); code=$?
    if [ "$code" -eq 0 ]; then
      echo "  FAIL $name should be rejected but validates"; FAIL=$((FAIL+1))
    elif [ "$code" -ne 1 ]; then
      echo "  FAIL $name: specdrift could not judge it (exit $code) — that is not a rejection:"
      echo "$out" | tail -n 5 | sed 's/^/       | /'
      FAIL=$((FAIL+1))
    elif [ -n "$expect" ] && ! printf '%s' "$out" | grep -qF -- "$expect"; then
      echo "  FAIL $name was rejected, but not for its planted defect ('$expect'):"
      echo "$out" | tail -n 5 | sed 's/^/       | /'
      FAIL=$((FAIL+1))
    else
      echo "  PASS $name rejected${expect:+ ($expect)}"; PASS=$((PASS+1))
    fi
  done
done
echo "── corpus gate: $PASS pass, $FAIL fail"
exit $((FAIL > 0 ? 1 : 0))
