#!/usr/bin/env bash
# OWASP ZAP baseline scan (GA gate G15, owner decision 2026-10-21 Q9: free CodeQL + ZAP in CI
# instead of a paid pentest). A PASSIVE scan — ZAP spiders the target and reports what the
# responses reveal (headers, cookies, disclosure); it sends no attack payloads.
#
# The target is a GENERATED app, not a purpose-built host: the golden-manifest shape
# GmBulkOnly (`--features bulk --broker none --auth none`), which carries an API and the
# console that API serves, on PostgreSQL only. validate-gm.sh generates, builds and proves it
# exactly as the GM lane does; this script then runs its API in Production and scans it
# twice: every operation of its OpenAPI document, and the console it serves.
#
#   scripts/zap-baseline.sh            # needs docker, dotnet, pnpm (the console is packed)
#
# Every alert listed in scripts/zap-baseline.conf carries its reason: IGNORE (not a weakness
# here) or INFO (a real open finding, reported but not failing until fixed or signed; each
# names its ledger entry). Any unlisted alert fails the run (zap-baseline.py exits non-zero
# on a WARN). Reports land in
# $ZAP_OUT (default ./zap-report) for the workflow to upload.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; fi

# Pinned by digest (2.16.1): the scanner's own rules change between releases, and an
# unpinned scanner would turn a green night red with no change on our side.
ZAP_IMAGE="ghcr.io/zaproxy/zaproxy:2.16.1@sha256:7840969c7c9fead565bf9734b12f49f6886db90b1d35b1f74d79710bbd081dab"
SHAPE=GmBulkOnly
PG_NAME="goldpath-zap-pg"
PORT=5380
TARGET="http://127.0.0.1:$PORT"
OUT=$(mkdir -p "${ZAP_OUT:-$ROOT/zap-report}" && cd "${ZAP_OUT:-$ROOT/zap-report}" && pwd)
chmod 777 "$OUT"   # the scanner runs as its own uid inside the container
export GOLDPATH_GM_WORK=${GOLDPATH_GM_WORK:-$(mktemp -d /tmp/goldpath-zap.XXXXXX)}
APP="$GOLDPATH_GM_WORK/$SHAPE"
API_PID=""

cleanup() {
  [ -n "$API_PID" ] && kill "$API_PID" 2>/dev/null || true
  docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "── the target: golden-manifest shape $SHAPE, generated and proven by validate-gm.sh"
bash "$ROOT/scripts/validate-gm.sh" "$SHAPE" --features bulk --broker none --auth none

echo "── postgres"
docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
docker run -d --name "$PG_NAME" -e POSTGRES_PASSWORD=zap -e POSTGRES_DB=ordersdb -p 55433:5432 postgres:17-alpine >/dev/null
for _ in $(seq 1 120); do docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 1; done
docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || { echo "postgres never came up"; exit 1; }
export ConnectionStrings__ordersdb="Host=127.0.0.1;Port=55433;Database=ordersdb;Username=postgres;Password=zap"

start_api() {
  ASPNETCORE_ENVIRONMENT="$1" ASPNETCORE_URLS="$TARGET" \
    dotnet run --no-build --no-launch-profile --project "$APP/src/$SHAPE.Api" >"$OUT/api-$1.log" 2>&1 &
  API_PID=$!
  for _ in $(seq 1 120); do
    curl -fsS "$TARGET/health/ready" >/dev/null 2>&1 && return 0
    kill -0 "$API_PID" 2>/dev/null || { echo "the API exited:"; tail -40 "$OUT/api-$1.log"; exit 1; }
    sleep 1
  done
  echo "the API never became ready:"; tail -40 "$OUT/api-$1.log"; exit 1
}

# Development applies the migrations (the template's own rule); the scan then runs against
# PRODUCTION, the posture an attacker meets — no developer exception page, HSTS on.
echo "── migrate (one Development start), then the Production start that is scanned"
start_api Development
kill "$API_PID"; wait "$API_PID" 2>/dev/null || true; API_PID=""
start_api Production

# Both legs are PASSIVE. The API has no page to spider (its root is a 404), so its leg imports
# the app's own OpenAPI document and requests every operation (zap-api-scan.py -S: safe mode,
# no active attack); the console leg spiders the page the API serves.
# --network host: the API listens on the runner's loopback. -I is NOT passed: a WARN that the
# rules file does not settle with a reason fails the run.
zap() {
  docker run --rm --network host -v "$ROOT/scripts:/zap/rules:ro" -v "$OUT:/zap/wrk:rw" "$ZAP_IMAGE" "$@"
}

status=0
cp "$APP/src/$SHAPE.Api/openapi/$SHAPE.Api.json" "$OUT/openapi.json"
echo "── zap api scan (passive, from the app's OpenAPI document)"
zap zap-api-scan.py -t /zap/wrk/openapi.json -f openapi -S -O "$TARGET" -c /zap/rules/zap-baseline.conf \
  -r zap-api.html -J zap-api.json -w zap-api.md || status=$?
echo "── zap baseline: the served console"
zap zap-baseline.py -t "$TARGET/goldpath/console/" -c /zap/rules/zap-baseline.conf -m 2 \
  -r zap-console.html -J zap-console.json -w zap-console.md || status=$?
echo "── reports: $OUT"
exit "$status"
