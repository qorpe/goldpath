#!/usr/bin/env bash
# Golden-manifest shape validation (ADR-0008 locally): pack → install template from source →
# generate the given shape → build against the local feed → run the smoke suite (real containers).
# Usage: validate-gm.sh <ShapeName> [extra dotnet-new args, e.g. --db sqlserver --broker none]
set -euo pipefail

NAME=${1:?usage: validate-gm.sh <ShapeName> [dotnet-new args...]}
shift || true
# Which template the shape exercises (goldpath-solution | goldpath-worker).
TEMPLATE=${GOLDPATH_GM_TEMPLATE:-goldpath-solution}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Local macOS: prefer the user SDK install; CI images already have dotnet on PATH.
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

# pwd -P: canonicalize (macOS /tmp is a symlink to /private/tmp — two spellings of one
# path make NuGet restore collide on its own generated files).
WORK=$(cd "${GOLDPATH_GM_WORK:-"$(mktemp -d /tmp/goldpath-gm.XXXXXX)"}" && pwd -P)
FEED="$WORK/feed"
APP="$WORK/$NAME"
echo "── shape: $NAME ($*) · workdir: $WORK"

# Same-version repacks poison the global cache (0.1.0-preview.1 never bumps between local
# runs): purge cached Goldpath packages so the generated app restores from THIS run's feed.
rm -rf "$HOME/.nuget/packages/goldpath."*

if [ ! -d "$FEED" ] || [ -z "$(ls -A "$FEED" 2>/dev/null)" ]; then
  # The console's dist FIRST: the local feed shadows nuget.org for the same version, so a
  # Goldpath.Console packed without its assets is the one a generated app restores — and
  # its page answers 500, which is exactly what the nightly saw on 2026-07-27. This lane
  # validating a generated app is OUR CI, not an adopter's machine: "adopters never run
  # Node" is a claim about the apps we generate, not about the gate that proves them.
  if command -v pnpm >/dev/null 2>&1; then
    echo "── build the console (its assets ship inside Goldpath.Console)"
    bash "$ROOT/scripts/build-console.sh" >/dev/null
  else
    # Warning-then-carry-on is how the 500 came back under a different trigger: the feed
    # would ship an asset-less console and every shape that renders a page would fail far
    # from here (review R3 on the fix that added this).
    echo "── pnpm not found, so the console cannot be built"
    echo "   this lane packs a local feed that SHADOWS nuget.org, so it would ship an"
    echo "   asset-less console and every shape composing an operational module would fail"
    echo "   on its console page. Install pnpm (10.x) and re-run."
    exit 1
  fi

  echo "── pack repo packages -> local feed"
  dotnet pack "$ROOT/Goldpath.sln" -c Release -o "$FEED" --nologo -v q
fi

dotnet new uninstall "$ROOT/templates/$TEMPLATE" >/dev/null 2>&1 || true
dotnet new install "$ROOT/templates/$TEMPLATE" --force >/dev/null

rm -rf "$APP"
if [ "${GOLDPATH_GM_VIA_CLI:-}" = "1" ]; then
  # The CLI's own generation verb, not dotnet new. Every shape used to bypass it, so
  # `goldpath new solution|worker` — the command the guides tell adopters to type — had
  # no end-to-end proof at all (preview.8 coverage audit, 2026-09-05). Its implicit
  # `db init` runs BEFORE the feed is wired below and is expected to report the migration
  # as pending; the explicit `db init` further down is what must succeed.
  KIND=${TEMPLATE#goldpath-}
  echo "── generate (goldpath new $KIND — the VERB, not dotnet new)"
  dotnet run --project "$ROOT/tools/Goldpath.Cli" -- new "$KIND" -n "$NAME" -o "$APP" "$@"
  test -f "$APP/.goldpath/manifest.yaml" || { echo "── THE VERB GENERATED NO APP"; exit 1; }
else
  echo "── generate ($TEMPLATE)"
  dotnet new "$TEMPLATE" -n "$NAME" -o "$APP" "$@" >/dev/null
fi
python3 - "$APP/nuget.config" "$FEED" <<'PY'
import sys
path, feed = sys.argv[1], sys.argv[2]
s = open(path).read().replace("<!-- GOLDPATH_FEED -->", f'<add key="goldpath-local" value="{feed}" />')
open(path, "w").write(s)
PY

# The engine, resolved ONCE and BEFORE any CLI verb runs. Pinned, consumed as the
# PUBLISHED tool (README dependency policy: source references are the exception, not the
# silent default). A local checkout is used only via an explicit GOLDPATH_SPECDRIFT_SRC
# and announces itself — a green local run must mean a green CI run.
#
# It is exported as GOLDPATH_SPECDRIFT because the post-generation verbs (goldpath new
# service|gateway) gate their own result through the engine: the nightly's GmSixGateway
# shape was red for three weeks on "could not run 'specdrift'" — the tool was installed
# only at spec-lint, AFTER those verbs, and into a directory that is not on PATH.
SPECDRIFT_VERSION=0.4.2
if [ -n "${GOLDPATH_SPECDRIFT_SRC:-}" ]; then
  echo "── engine: using LOCAL specdrift checkout (GOLDPATH_SPECDRIFT_SRC=$GOLDPATH_SPECDRIFT_SRC) — not the $SPECDRIFT_VERSION pin"
  SPECDRIFT="dotnet run --project $GOLDPATH_SPECDRIFT_SRC/src/Specdrift --"
else
  [ -x "$WORK/tools/specdrift" ] || dotnet tool install --tool-path "$WORK/tools" specdrift --version "$SPECDRIFT_VERSION" >/dev/null
  SPECDRIFT="$WORK/tools/specdrift"
fi
export GOLDPATH_SPECDRIFT="$SPECDRIFT"

# Build BEFORE `db init`, not only after it (#279). `db init` is the first thing to compile
# the generated app, through `dotnet ef`, and `dotnet ef` reports a compile failure as the
# two words "Build failed" and nothing else. In the T30 proof (#273) a planted template
# defect — GP2102 dropped from WarningsNotAsErrors — turned GmEverything red at that step,
# and the log never named GP2102: the cause had to be read off the diff. Built here, the
# same failure fails with the compiler's own diagnostic (rule id, file:line). The same
# holds for every `db` verb in the post loop below (GmGrownRest's case). The build step
# after the loop stays, because the post-generation verbs add heads and `db init` adds the
# migrations; on the happy path it is incremental. -m:1 for the reason given there.
echo "── build (before db init, so a compile error names its rule)"
dotnet build "$APP" --nologo -v q -m:1

echo "── initial migration (goldpath db init — Development migrates, EnsureCreated is gone)"
(cd "$APP" && dotnet run --project "$ROOT/tools/Goldpath.Cli" -- db init --path .)

# Optional post-generation CLI verbs (';'-separated) — the multi-head shapes are built by
# the SAME verbs an adopter runs (goldpath new service|gateway), never by a special path.
if [ -n "${GOLDPATH_GM_POST:-}" ]; then
  IFS=';' read -ra GM_POSTS <<< "$GOLDPATH_GM_POST"
  for post in "${GM_POSTS[@]}"; do
    # A `db` verb compiles through `dotnet ef` too (#279): in the T30 proof GmGrownRest only
    # met the planted defect after `add feature fileexchange`, and its `db add GrownRest`
    # said "Build failed" and nothing more. Build first so the rule is named here as well.
    case "$post" in
      db\ *)
        echo "── build (before goldpath $post, so a compile error names its rule)"
        dotnet build "$APP" --nologo -v q -m:1 ;;
    esac
    echo "── goldpath $post"
    (cd "$APP" && dotnet run --project "$ROOT/tools/Goldpath.Cli" -- $post --path .)
  done
fi

echo "── build"
# -m:1: the generated app has multiple heads referencing the Api project; .NET 10's
# StaticWebAssets cache (rjsmrazor.dswa.cache.json) races under parallel node builds.
dotnet build "$APP" --nologo -v q -m:1
# Worker shapes have no API head: probes, not business contracts — no OpenAPI artifact.
if [ -d "$APP/src/$NAME.Api" ]; then
  test -s "$APP/src/$NAME.Api/openapi/$NAME.Api.json"   && echo "── openapi artifact OK (Spec Engine drift input)"   || { echo "── OPENAPI ARTIFACT MISSING"; exit 1; }
fi

echo "── spec-lint (specdrift: validate + drift)"
$SPECDRIFT validate "$APP/.goldpath/manifest.yaml"   --schema "$ROOT/schemas/manifest/v1/goldpath-manifest.schema.json"   --rules "$APP/.specdrift/rules.yaml"
# First generation: commit the contract (what a team does on day one), then drift must be clean.
if [ -d "$APP/src/$NAME.Api" ]; then
  mkdir -p "$APP/specs"
  cp "$APP/src/$NAME.Api/openapi/$NAME.Api.json" "$APP/specs/$NAME.Api.json"
fi
$SPECDRIFT drift --repo "$APP"

echo "── smoke (real AppHost + containers)"
dotnet test "$APP/tests/$NAME.SmokeTests" --no-build --nologo

echo "── $NAME GREEN"
