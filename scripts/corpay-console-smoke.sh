#!/usr/bin/env bash
# GA gate G11 (open-threads T1, #325): the console DRIVES the adopter-shaped sample, not only a
# purpose-built host. CorPay is brought up on its PUBLISHED train — the API head behind its OpenID
# floor and the EOD worker's internal head — on real Postgres, RabbitMQ and Redis, with a pinned
# Keycloak importing the committed realm (ui/console/e2e/corpay-realm/realm.json: maker, approver
# and a viewer without the ops role). Playwright then drives the console each head SERVES
# (ui/console/e2e/corpay.spec.ts): the EOD job run, paused and resumed; a payment file through the
# four-eyes gate by two principals, the batch naming its approver and the jobs admin audit naming the maker
# (an approval writes no admin-audit row of its own: open-threads T63); and the approved file's execution,
# pinned to #539's failure shape until that defect lands (open-threads T62).
#
#   scripts/corpay-console-smoke.sh [playwright args…]
#
# The proof's planted fault (#325): CORPAY_EXPECTED_APPROVER=maker scripts/corpay-console-smoke.sh
# must fail on the audit and the batch's decider, which name the approver.
#
# Needs: docker, dotnet, pnpm, and Playwright's chromium (pnpm exec playwright install chromium).
# Exits non-zero on the first failure — no green-by-cleanup.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
CORPAY="$ROOT/samples/corpay"
if [ -d "$HOME/.dotnet/sdk" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; fi

# Pinned by exact tag (Goldpath rule: no "latest"); the Auth RFC names Keycloak as the reference IdP.
KEYCLOAK_IMAGE="keycloak/keycloak:26.0.7"
PG_NAME="goldpath-corpay-smoke-pg"
MQ_NAME="goldpath-corpay-smoke-mq"
REDIS_NAME="goldpath-corpay-smoke-redis"
KC_NAME="goldpath-corpay-smoke-kc"
KC_PORT=58080
API_URL="http://localhost:5340"
EOD_URL="http://localhost:5341"
LOGS="${CORPAY_SMOKE_LOGS:-/tmp}"
API_PID=""
EOD_PID=""

cleanup() {
  [ -n "$API_PID" ] && kill "$API_PID" 2>/dev/null || true
  [ -n "$EOD_PID" ] && kill "$EOD_PID" 2>/dev/null || true
  for name in "$PG_NAME" "$MQ_NAME" "$REDIS_NAME" "$KC_NAME"; do docker rm -f "$name" >/dev/null 2>&1 || true; done
}
trap cleanup EXIT

# BOUNDED waits: an unbounded wait on a container that died is a hang with no output at all.
wait_for() { # <what> <seconds> <command…>
  local what=$1 seconds=$2; shift 2
  for _ in $(seq 1 "$seconds"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "$what never came up"; return 1
}

echo "── containers: postgres, rabbitmq, redis, keycloak ($KEYCLOAK_IMAGE)"
for name in "$PG_NAME" "$MQ_NAME" "$REDIS_NAME" "$KC_NAME"; do docker rm -f "$name" >/dev/null 2>&1 || true; done
docker run -d --name "$PG_NAME" -e POSTGRES_PASSWORD=smoke -e POSTGRES_DB=ordersdb -p 56432:5432 postgres:17-alpine >/dev/null
# The cookie is seeded before the server starts, as console-smoke.sh does, for Docker hosts that
# mount /var/lib/rabbitmq so the rabbitmq user cannot create it.
docker run -d --name "$MQ_NAME" -p 56672:5672 rabbitmq:4-alpine sh -c \
  'echo smokecookie > /var/lib/rabbitmq/.erlang.cookie \
   && chmod 400 /var/lib/rabbitmq/.erlang.cookie \
   && chown rabbitmq:rabbitmq /var/lib/rabbitmq/.erlang.cookie \
   && exec docker-entrypoint.sh rabbitmq-server' >/dev/null
docker run -d --name "$REDIS_NAME" -p 56379:6379 redis:7-alpine >/dev/null
docker run -d --name "$KC_NAME" -p "$KC_PORT:8080" \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  -v "$ROOT/ui/console/e2e/corpay-realm:/opt/keycloak/data/import:ro" \
  "$KEYCLOAK_IMAGE" start-dev --import-realm --http-port=8080 >/dev/null

# TCP on purpose (-h 127.0.0.1): initdb's temporary server answers on the socket (console-smoke.sh).
wait_for postgres 120 docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U postgres || { docker logs "$PG_NAME" 2>&1 | tail -20; exit 1; }
wait_for "the broker" 180 docker exec "$MQ_NAME" rabbitmq-diagnostics -q check_port_connectivity || { docker logs "$MQ_NAME" 2>&1 | tail -20; exit 1; }
wait_for redis 60 docker exec "$REDIS_NAME" redis-cli ping || { docker logs "$REDIS_NAME" 2>&1 | tail -20; exit 1; }
wait_for keycloak 180 curl -sf "http://localhost:$KC_PORT/realms/corpay/.well-known/openid-configuration" || { docker logs "$KC_NAME" 2>&1 | tail -30; exit 1; }

echo "── CorPay on its published train"
dotnet build "$CORPAY/CorPay.sln" --nologo -v quiet
DB="Host=localhost;Port=56432;Database=ordersdb;Username=postgres;Password=smoke"
# Development: CorPay applies its own migrations at start (it is the sample's documented first run);
# the authority is plain http only because the IdP is a local container — RequireHttpsMetadata stays
# true everywhere else.
ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__ordersdb="$DB" \
  ConnectionStrings__messaging="amqp://guest:guest@localhost:56672" \
  ConnectionStrings__redis="localhost:56379" \
  Goldpath__Auth__Authority="http://localhost:$KC_PORT/realms/corpay" \
  Goldpath__Auth__Audience="corpay-api" \
  Goldpath__Auth__RequireHttpsMetadata=false \
  dotnet run --no-build --no-launch-profile --project "$CORPAY/src/CorPay.Api" --urls "$API_URL" > "$LOGS/corpay-smoke-api.log" 2>&1 &
API_PID=$!
wait_for "the API head" 180 curl -sf "$API_URL/health/ready" || { tail -60 "$LOGS/corpay-smoke-api.log"; exit 1; }

# The EOD worker starts after the API: the API's context owns the shared tables' DDL (T12).
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__ordersdb="$DB" \
  dotnet run --no-build --no-launch-profile --project "$CORPAY/src/CorPay.EodWorker" --urls "$EOD_URL" > "$LOGS/corpay-smoke-eod.log" 2>&1 &
EOD_PID=$!
wait_for "the EOD worker" 180 curl -sf "$EOD_URL/health/ready" || { tail -60 "$LOGS/corpay-smoke-eod.log"; exit 1; }

echo "── playwright, through the consoles CorPay serves"
cd "$ROOT/ui/console"
CORPAY_API_URL="$API_URL" CORPAY_EOD_URL="$EOD_URL" \
  CORPAY_TOKEN_URL="http://localhost:$KC_PORT/realms/corpay/protocol/openid-connect/token" \
  pnpm exec playwright test e2e/corpay.spec.ts "$@"
