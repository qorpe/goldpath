#!/usr/bin/env bash
# Which feed the nightly's CorPay restores from (#378).
#
#   scripts/corpay-feed.sh              decide, and wire the packed feed when it is needed
#   scripts/corpay-feed.sh --selftest   the decision against planted registry answers
#
# CorPay proves the PUBLISHED train (ADR-0007), so it restores from nuget.org. Since #312 the
# release PR moves CorPay's Goldpath.* pins to the new train BEFORE that train is published
# (train-freshness.sh requires it), and from then until the publish nuget.org cannot restore
# them: the nightly's sample-corpay job went red on every release night, which is noise that
# hides a real red. So:
#
# - this repository's train (Directory.Build.props) IS on nuget.org → change nothing; CorPay
#   restores from nuget.org only, the job's purpose;
# - it is NOT on nuget.org yet (a release head, or main between merge and publish — the same
#   condition train-freshness.sh calls "release in flight") → pack this repository into a local
#   feed and add it to samples/corpay/nuget.config beside nuget.org, the way validate-gm.sh
#   wires a generated app.
#
# Anything this script cannot decide — the registry unreachable, an empty or malformed answer,
# no train in Directory.Build.props — is a refusal, never a guess: guessing nuget.org would
# reproduce the red this exists to remove, and guessing the packed feed would let a nightly
# prove CorPay against unpublished packages while the train IS published.
#
# The log line and, on Actions, the step summary name the feed used; GITHUB_OUTPUT receives
# source=nuget.org|packed, train=<version> and feed=<path or empty>.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PACKAGE="Goldpath.Abstractions"   # every composition takes it; it dates the whole train (as train-freshness.sh)

# decide <train> <registry index JSON> → prints "nuget.org" or "packed"; returns 1 with a
# reason on stderr when there is nothing to decide from.
decide() {
  python3 - "$1" "$2" <<'PY'
import json, sys
train, body = sys.argv[1], sys.argv[2]
def refuse(why):
    print(f"corpay-feed: {why} — refusing to guess.", file=sys.stderr)
    sys.exit(1)
if not train.strip():
    refuse("no train in Directory.Build.props")
try:
    versions = json.loads(body)["versions"]
except (ValueError, KeyError, TypeError):
    refuse("the registry's answer is not a version index")
if not isinstance(versions, list) or not versions:
    refuse("the registry listed no versions")
print("nuget.org" if train in versions else "packed")
PY
}

train_of() {
  python3 - "$1" <<'PY'
import re, sys
props = open(sys.argv[1]).read()
prefix = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props)
suffix = re.search(r"<VersionSuffix>([^<]*)</VersionSuffix>", props)
print((prefix.group(1) if prefix else "") + ("-" + suffix.group(1) if suffix and suffix.group(1) else ""))
PY
}

if [ "${1:-}" = "--selftest" ]; then
  # Each case: train, registry answer, expected verdict ("refuse" = exit 1).
  fail=0
  check() {
    local name=$1 train=$2 body=$3 want=$4 got
    if got=$(decide "$train" "$body" 2>/dev/null); then :; else got=refuse; fi
    if [ "$got" = "$want" ]; then
      echo "corpay-feed selftest: $name → $got"
    else
      echo "corpay-feed selftest FAILED: $name → $got, expected $want"; fail=1
    fi
  }
  check "published train restores from nuget.org" \
    0.1.0-preview.9 '{"versions":["0.1.0-preview.8","0.1.0-preview.9"]}' nuget.org
  check "release in flight packs the feed" \
    0.1.0-preview.10 '{"versions":["0.1.0-preview.8","0.1.0-preview.9"]}' packed
  check "an older published train is still published" \
    0.1.0-preview.8 '{"versions":["0.1.0-preview.8","0.1.0-preview.9"]}' nuget.org
  check "a prefix of a published version is not that version" \
    0.1.0-preview.1 '{"versions":["0.1.0-preview.10"]}' packed
  check "an empty version list refuses" \
    0.1.0-preview.10 '{"versions":[]}' refuse
  check "a malformed answer refuses" \
    0.1.0-preview.10 '<html>rate limited</html>' refuse
  check "an answer without versions refuses" \
    0.1.0-preview.10 '{"items":[]}' refuse
  check "no train refuses" \
    "" '{"versions":["0.1.0-preview.9"]}' refuse
  # The train is read the way train-freshness.sh reads it.
  tmp=$(mktemp -d)
  printf '<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix><VersionSuffix>preview.10</VersionSuffix></PropertyGroup></Project>' > "$tmp/p"
  [ "$(train_of "$tmp/p")" = "0.1.0-preview.10" ] && echo "corpay-feed selftest: train read as 0.1.0-preview.10" \
    || { echo "corpay-feed selftest FAILED: train read as '$(train_of "$tmp/p")'"; fail=1; }
  printf '<Project><PropertyGroup><VersionPrefix>1.0.0</VersionPrefix><VersionSuffix></VersionSuffix></PropertyGroup></Project>' > "$tmp/p"
  [ "$(train_of "$tmp/p")" = "1.0.0" ] && echo "corpay-feed selftest: a stable train read as 1.0.0" \
    || { echo "corpay-feed selftest FAILED: stable train read as '$(train_of "$tmp/p")'"; fail=1; }
  rm -rf "$tmp"
  [ "$fail" = 0 ] && echo "corpay-feed selftest: every case decided as expected."
  exit "$fail"
fi

TRAIN=$(train_of "$ROOT/Directory.Build.props")
LOWER=$(printf '%s' "$PACKAGE" | tr '[:upper:]' '[:lower:]')
set +e
INDEX=$(curl -fsS --max-time 20 "https://api.nuget.org/v3-flatcontainer/$LOWER/index.json" 2>/dev/null)
CURL_STATUS=$?
set -e
if [ "$CURL_STATUS" != 0 ]; then
  echo "corpay-feed: nuget.org did not answer (curl $CURL_STATUS), so whether train $TRAIN is published is unknown — refusing to guess." >&2
  exit 1
fi
SOURCE=$(decide "$TRAIN" "$INDEX")

FEED=""
if [ "$SOURCE" = "packed" ]; then
  # pwd -P: one spelling of the path, or NuGet collides on its own files (validate-gm.sh).
  FEED=${GOLDPATH_CORPAY_FEED:-"$(mktemp -d)/feed"}
  mkdir -p "$FEED"
  FEED=$(cd "$FEED" && pwd -P)
  CONFIG="$ROOT/samples/corpay/nuget.config"
  LINE="<add key=\"goldpath-local\" value=\"$FEED\" />"
  if ! grep -q "<!-- GOLDPATH_FEED -->" "$CONFIG" && ! grep -qF "$LINE" "$CONFIG"; then
    echo "corpay-feed: $CONFIG has no <!-- GOLDPATH_FEED --> marker to wire the feed into." >&2
    exit 1
  fi
  # The console's assets FIRST: CorPay references Goldpath.Console, and a console packed
  # without its dist answers 500 on its page (validate-gm.sh, 2026-07-27).
  command -v pnpm >/dev/null 2>&1 || { echo "corpay-feed: pnpm not found — the packed Goldpath.Console would ship without its console." >&2; exit 1; }
  echo "corpay-feed: train $TRAIN is not on nuget.org yet — packing this repository into $FEED"
  bash "$ROOT/scripts/build-console.sh" >/dev/null
  dotnet pack "$ROOT/Goldpath.sln" -c Release -o "$FEED" --nologo -v q
  [ -f "$FEED/$PACKAGE.$TRAIN.nupkg" ] || { echo "corpay-feed: the pack produced no $PACKAGE.$TRAIN.nupkg" >&2; exit 1; }
  # A same-version package cached by an earlier local run would shadow this run's feed.
  rm -rf "$HOME"/.nuget/packages/goldpath.*/"$TRAIN"
  python3 - "$CONFIG" "$LINE" <<'PY'
import sys
path, line = sys.argv[1], sys.argv[2]
s = open(path).read()
if line not in s:
    s = s.replace("<!-- GOLDPATH_FEED -->", line)
open(path, "w").write(s)
PY
  MSG="CorPay restores from the packed feed $FEED plus nuget.org: train $TRAIN is not published yet (release in flight)."
else
  MSG="CorPay restores from nuget.org only: train $TRAIN is published (ADR-0007)."
fi

echo "corpay-feed: $MSG"
[ -z "${GITHUB_STEP_SUMMARY:-}" ] || echo "**sample-corpay feed:** $MSG" >> "$GITHUB_STEP_SUMMARY"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  { echo "source=$SOURCE"; echo "train=$TRAIN"; echo "feed=$FEED"; } >> "$GITHUB_OUTPUT"
fi
