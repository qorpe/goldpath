#!/usr/bin/env bash
# The train-freshness gate — platform RFC §2b, risk R-1 (version skew).
#
# A product repo binds to a PUBLISHED train (ADR-0012), which is the whole point: it cannot
# fork the core by accident. The failure mode that replaces forking is quieter — the product
# simply never updates its pin, and months later it is running a train nobody supports. D3's
# support window is latest-only, so falling behind must be VISIBLE, never ambient.
#
# Run it from a product repo (or from this monorepo against samples/corpay, which is the
# adopter-shaped rehearsal):
#   scripts/train-freshness.sh [path-to-repo-root]
#
# Tolerance is deliberately the same as kit-freshness so a team learns one rule, not two:
# the newest published train, or fail.
set -euo pipefail

ROOT=${1:-.}
PROPS="$ROOT/Directory.Packages.props"
PACKAGE="Goldpath.Abstractions"   # every composition takes it; it dates the whole train

if [ ! -f "$PROPS" ]; then
  echo "train-freshness: no Directory.Packages.props under $ROOT — nothing to check." >&2
  exit 2
fi

# An if/then, never `grep … && VAR=…`: under `set -e` the && form exits the script SILENTLY
# when the grep misses, reporting nothing while returning failure (the bug ledger-check.sh
# shipped with once — the same trap deserves the same defence).
if ! PINNED=$(grep -o "\"$PACKAGE\" Version=\"[^\"]*\"" "$PROPS" | head -1 | sed 's/.*Version="//; s/"//'); then
  PINNED=""
fi
if [ -z "$PINNED" ]; then
  echo "train-freshness: $PACKAGE is not pinned in $PROPS — a product must bind to a train." >&2
  exit 1
fi

# `tr`, not ${VAR,,}: the latter is bash 4 and macOS ships bash 3.2, where it expands to a
# malformed URL. The first version of this gate did exactly that — curl failed, the
# offline branch caught it, and the script reported a cheerful honest skip while testing
# NOTHING. A false green is worse than a red.
LOWER=$(printf '%s' "$PACKAGE" | tr '[:upper:]' '[:lower:]')

# Air-gapped runs are a first-class scenario (constitution). No registry, no verdict: skip
# HONESTLY rather than fail a legitimate offline build, and never claim freshness we did not
# verify. But the skip covers NETWORK failure only — curl's connectivity codes. Anything
# else (a 404, a malformed request, a feed that changed shape) is OUR bug and must be loud,
# because that is the exact seam a silent skip hides in.
set +e
FEED=$(curl -fsS --max-time 20 "https://api.nuget.org/v3-flatcontainer/$LOWER/index.json" 2>/dev/null)
CURL_STATUS=$?
set -e
case "$CURL_STATUS" in
  0) ;;
  6|7|28|35)   # DNS failure · connection refused · timeout · TLS handshake — genuinely offline
    echo "train-freshness: nuget.org unreachable (curl $CURL_STATUS) — skipped honestly (air-gapped run; pinned $PINNED unverified, not stale)."
    exit 0 ;;
  *)
    echo "train-freshness: the registry request FAILED with curl status $CURL_STATUS — not an offline run." >&2
    echo "  Refusing to report a skip: that would look identical to a pass. Fix the request." >&2
    exit 1 ;;
esac
if [ -z "$FEED" ]; then
  echo "train-freshness: the registry answered with an empty body — refusing to guess." >&2
  exit 1
fi

# The feed lists versions oldest-first; the newest entry is the current train. Prereleases
# count, because below 1.0 the preview line IS the supported line (SECURITY.md).
LATEST=$(printf '%s' "$FEED" | python3 -c "import json,sys; v=json.load(sys.stdin)['versions']; print(v[-1] if v else '')")
if [ -z "$LATEST" ]; then
  echo "train-freshness: the feed carried no versions — treating as unreachable, skipped honestly."
  exit 0
fi

# The release PR, in THIS repository only (#312). The release PR bumps the train to a version
# nuget.org does not have yet, and it must move CorPay's pins to that version itself: when the
# pins moved after the tag instead, the publish turned this gate red on main and on every open
# pull request until a follow-up PR caught up (v0.1.0-preview.9, #307). So, when this script
# runs from the goldpath repository (its own packages/ beside it — an adopter's copy has none)
# and the repository's train is not on the feed yet, the product must pin exactly that train.
# An adopter's run never enters this branch, so the gate means the same thing for them.
SELF_ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$SELF_ROOT/packages/$PACKAGE" ] && [ -f "$SELF_ROOT/Directory.Build.props" ]; then
  TRAIN=$(python3 - "$SELF_ROOT/Directory.Build.props" <<'PY'
import re, sys
props = open(sys.argv[1]).read()
prefix = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props)
suffix = re.search(r"<VersionSuffix>([^<]*)</VersionSuffix>", props)
print((prefix.group(1) if prefix else "") + ("-" + suffix.group(1) if suffix and suffix.group(1) else ""))
PY
)
  PUBLISHED=$(printf '%s' "$FEED" | python3 -c "import json,sys; v=json.load(sys.stdin)['versions']; print('yes' if sys.argv[1] in v else 'no')" "$TRAIN")
  if [ -n "$TRAIN" ] && [ "$PUBLISHED" = "no" ]; then
    if [ "$PINNED" = "$TRAIN" ]; then
      echo "train-freshness: release in flight — pinned $PINNED is this repository's train, not published yet (latest $LATEST)."
      exit 0
    fi
    echo "train-freshness: this repository's train is $TRAIN (not published yet), but the product pins $PINNED." >&2
    echo "  The release PR moves the product's Goldpath.* pins to $TRAIN in the same PR" >&2
    echo "  (docs/ops/release-checklist.md); after the tag it would turn every PR red." >&2
    exit 1
  fi
fi

if [ "$PINNED" = "$LATEST" ]; then
  echo "train-freshness: pinned $PINNED == latest — on the current train."
  exit 0
fi

echo "train-freshness: pinned $PINNED is behind the published train $LATEST." >&2
echo "  A product is supported on the CURRENT train only (versioning RFC D3). Take the new" >&2
echo "  train in its own slice — pins, migrations, and the proofs that go with them." >&2
exit 1
