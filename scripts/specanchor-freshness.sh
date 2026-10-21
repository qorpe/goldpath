#!/usr/bin/env bash
# The specanchor-freshness gate (goldpath-authoring RFC V1; delivery-cycle D6): the three
# specanchor artefact schemas Goldpath writes against are PINNED byte copies, and a pin may
# neither be edited in place nor fall silently behind the repository that owns the format.
# The same discipline as kit-freshness and train-freshness, for a schema instead of a package.
#
# Three checks, in order:
#   1. integrity (offline, always runs): every pinned file hashes to what pin.json records.
#      A local edit to a copy is a fork of specanchor's format, which D6 forbids — the change
#      belongs upstream, and arrives here by re-pinning.
#   2. truth (network): upstream at the pinned ref serves exactly these bytes, so pin.json does
#      not name a commit it was never copied from.
#   3. freshness (network): upstream's newest TAG — or, while specanchor publishes none, its
#      default branch — still serves these bytes. And once a tag exists, a commit pin is red:
#      the repository forbids "latest" and pins to releases wherever a release exists.
#
# Air-gapped runs are a first-class scenario (constitution): check 1 always runs; checks 2–3
# skip HONESTLY when the network is genuinely absent, and a failure that is NOT a network
# failure is loud, because a silent skip looks identical to a pass (the train-freshness lesson).
#
# Test seams, used only to plant faults when proving this gate: SPECANCHOR_RAW_BASE replaces
# the raw-content base URL (a file:// URL works); SPECANCHOR_LATEST_REF skips the tag/branch
# lookup and names the ref to compare against; SPECANCHOR_GIT_URL replaces the repository URL
# the tags and the default branch are listed from (a local path works).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PIN_DIR="$ROOT/schemas/specanchor/v1"
PIN="$PIN_DIR/pin.json"
[ -f "$PIN" ] || { echo "specanchor-freshness: $PIN is missing — nothing records what is pinned." >&2; exit 1; }

read_pin() { python3 -c "import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])" "$PIN" "$1"; }
REPO=$(read_pin repository)
REF=$(read_pin ref)
REF_KIND=$(read_pin refKind)
FILES=$(python3 -c "
import json, sys
for name, entry in json.load(open(sys.argv[1]))['files'].items():
    print(name, entry['upstream'], entry['sha256'])" "$PIN")

sha() { shasum -a 256 | cut -d' ' -f1; }

# 1 — integrity, offline.
FAILED=0
while read -r name upstream want; do
  if [ ! -f "$PIN_DIR/$name" ]; then
    echo "specanchor-freshness: $name is recorded in pin.json but missing from $PIN_DIR." >&2; FAILED=1; continue
  fi
  got=$(sha < "$PIN_DIR/$name")
  if [ "$got" != "$want" ]; then
    echo "specanchor-freshness: $name was edited in place (sha256 $got, pin.json records $want)." >&2
    echo "  The copy is specanchor's format, not ours: change it upstream, then re-pin." >&2
    FAILED=1
  fi
done <<< "$FILES"
[ "$FAILED" -eq 0 ] || exit 1
echo "specanchor-freshness: the three pinned schemas match pin.json ($REF_KIND ${REF:0:12})."

RAW_BASE=${SPECANCHOR_RAW_BASE:-https://raw.githubusercontent.com/$REPO}
GIT_URL=${SPECANCHOR_GIT_URL:-https://github.com/$REPO}

# Fetches one upstream file at a ref; exit 3 means "genuinely offline", anything else non-zero
# is a real failure the caller must report.
fetch() {
  local ref=$1 path=$2 body status
  set +e
  body=$(curl -fsS --max-time 20 --retry 2 --retry-delay 2 "$RAW_BASE/$ref/$path" 2>/dev/null)
  status=$?
  set -e
  case "$status" in
    0) printf '%s' "$body" ;;
    6|7|28|35) return 3 ;;   # DNS · connection refused · timeout · TLS handshake
    *) echo "specanchor-freshness: fetching $path at $ref FAILED (curl $status) — not an offline run." >&2; return 1 ;;
  esac
}

# 2 — truth: the pinned ref serves the pinned bytes. `$(...)` strips trailing newlines, so the
# comparison hashes the body exactly as the local file reads after the same stripping.
while read -r name upstream want; do
  set +e
  body=$(fetch "$REF" "$upstream")
  status=$?
  set -e
  if [ "$status" -eq 3 ]; then
    echo "specanchor-freshness: upstream unreachable — skipped honestly (air-gapped run; the pin is intact, its freshness unverified, not stale)."
    exit 0
  fi
  [ "$status" -eq 0 ] || exit 1
  local_body=$(cat "$PIN_DIR/$name")
  if [ "$body" != "$local_body" ]; then
    echo "specanchor-freshness: $REPO@$REF does not serve the pinned $name — pin.json names a ref the copy did not come from." >&2
    exit 1
  fi
done <<< "$FILES"

# 3 — freshness: compare against the newest tag, or the default branch while there are none.
# One retry, like curl's: a single dropped connection on a hosted runner is not a verdict.
ls_remote() { git ls-remote "$@" 2>/dev/null || { sleep 2; git ls-remote "$@" 2>/dev/null; }; }
if [ -n "${SPECANCHOR_LATEST_REF:-}" ]; then
  LATEST=$SPECANCHOR_LATEST_REF; LATEST_KIND="override"
else
  if ! TAGS=$(ls_remote --tags --refs "$GIT_URL"); then
    echo "specanchor-freshness: listing $REPO's tags FAILED although its files were reachable — refusing to report a skip." >&2
    exit 1
  fi
  TAG=$(printf '%s\n' "$TAGS" | sed -n 's|.*refs/tags/||p' | sort -V | tail -1)
  if [ -n "$TAG" ]; then
    LATEST=$TAG; LATEST_KIND="tag"
    if [ "$REF_KIND" != "tag" ]; then
      echo "specanchor-freshness: $REPO now publishes releases (newest: $TAG) but the pin is a $REF_KIND." >&2
      echo "  Re-pin to the release: copy the three files at $TAG, set ref/refKind/sha256 in pin.json." >&2
      exit 1
    fi
  else
    if ! HEAD_LINE=$(ls_remote "$GIT_URL" HEAD) || [ -z "$HEAD_LINE" ]; then
      echo "specanchor-freshness: reading $REPO's default branch FAILED although its files were reachable." >&2
      exit 1
    fi
    LATEST=$(printf '%s' "$HEAD_LINE" | cut -f1); LATEST_KIND="default branch (no release published yet)"
  fi
fi

if [ "$LATEST" = "$REF" ]; then
  echo "specanchor-freshness: pinned at $REPO's newest $LATEST_KIND — fresh."
  exit 0
fi
STALE=0
while read -r name upstream want; do
  body=$(fetch "$LATEST" "$upstream") || { echo "specanchor-freshness: could not read $upstream at $LATEST." >&2; exit 1; }
  if [ "$body" != "$(cat "$PIN_DIR/$name")" ]; then
    echo "specanchor-freshness: $name changed upstream at $LATEST ($LATEST_KIND) — the pin is behind." >&2
    STALE=1
  fi
done <<< "$FILES"
if [ "$STALE" -ne 0 ]; then
  echo "  specanchor moved its format. Re-pin deliberately, in its own change: copy the files at the new ref," >&2
  echo "  update pin.json, and run scripts/corpus-check.sh — a schema change can turn the corpus red." >&2
  exit 1
fi
echo "specanchor-freshness: $REPO's $LATEST_KIND (${LATEST:0:12}) serves the same three schemas — fresh."
