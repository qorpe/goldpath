#!/usr/bin/env bash
# The U2 exit gate (console RFC D3): the REAL console driven against a REAL Goldpath app.
# Brings up Postgres, a web host composed from the packages (jobs + the frozen admin
# surface), and the console dev server; runs Playwright; tears everything down.
#
#   scripts/console-smoke.sh
#
# Needs: docker, dotnet, pnpm. Exits non-zero on the first failure — no green-by-cleanup.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; fi

PG_NAME="goldpath-console-smoke-pg"
MQ_NAME="goldpath-console-smoke-mq"
SERVICE_URL="http://localhost:5310"
# Two more REAL apps, so the gate can prove how the console behaves when it is REFUSED —
# not only when it is welcome: one behind the auth floor, one tenant-scoped (R1).
SECURED_URL="http://localhost:5312"
TENANT_URL="http://localhost:5313"
CONSOLE_URL="http://localhost:5201"
HOST_PID=""
SECURED_PID=""
TENANT_PID=""
VITE_PID=""

cleanup() {
  [ -n "$HOST_PID" ] && kill "$HOST_PID" 2>/dev/null || true
  [ -n "$SECURED_PID" ] && kill "$SECURED_PID" 2>/dev/null || true
  [ -n "$TENANT_PID" ] && kill "$TENANT_PID" 2>/dev/null || true
  [ -n "$VITE_PID" ] && kill "$VITE_PID" 2>/dev/null || true
  docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
  docker rm -f "$MQ_NAME" >/dev/null 2>&1 || true
  rm -f "$ROOT/ui/console/public/console.config.json" 2>/dev/null || true
}
trap cleanup EXIT

echo "── the console dist (this is the ONLY place Node is needed — adopters never run it)"
bash "$ROOT/scripts/build-console.sh" >/dev/null

echo "── postgres"
docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
docker run -d --name "$PG_NAME" -e POSTGRES_PASSWORD=smoke -e POSTGRES_DB=smoke -p 55432:5432 postgres:17-alpine >/dev/null
CONNECTION="Host=localhost;Port=55432;Database=smoke;Username=postgres;Password=smoke"
# 120s, not 60: a nightly runner is doing nine other things and a cold image pull plus
# first-boot initdb has taken longer than a minute there (it did, on 2026-07-27).
# TCP on purpose (-h 127.0.0.1): initdb's TEMPORARY server answers pg_isready on the unix
# socket, so the socket probe can break out during init and find the server "gone" a
# moment later when the temp server shuts down (it did, on the 2026-08-03 nightly). The
# real server is the only one that listens on TCP.
for _ in $(seq 1 120); do docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 1; done
docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || { echo "postgres never came up:"; docker logs "$PG_NAME" 2>&1 | tail -20; exit 1; }

echo "── rabbitmq (campaign RELEASES through a broker — campaign RFC D8, no stand-in)"
docker rm -f "$MQ_NAME" >/dev/null 2>&1 || true
# The cookie is seeded before the server starts: on some Docker hosts /var/lib/rabbitmq
# is mounted so that the rabbitmq user cannot create .erlang.cookie itself, and the node
# dies with "eacces" before it ever listens. Writing it as root costs nothing elsewhere.
docker run -d --name "$MQ_NAME" -p 55672:5672 rabbitmq:4-alpine sh -c \
  'echo smokecookie > /var/lib/rabbitmq/.erlang.cookie \
   && chmod 400 /var/lib/rabbitmq/.erlang.cookie \
   && chown rabbitmq:rabbitmq /var/lib/rabbitmq/.erlang.cookie \
   && exec docker-entrypoint.sh rabbitmq-server' >/dev/null
BROKER="amqp://guest:guest@localhost:55672"
# BOUNDED: an unbounded wait on a container that died turns a two-minute failure into a
# hang with no output at all.
for _ in $(seq 1 90); do
  docker exec "$MQ_NAME" rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1 && break
  sleep 2
done
docker exec "$MQ_NAME" rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1 || {
  echo "the broker never came up:"; docker logs "$MQ_NAME" 2>&1 | tail -20; exit 1;
}

echo "── the app (real packages, real Quartz, real broker, the FROZEN admin surface)"
# Product console link tiles (#257): one same-origin product (a stand-in page the host
# mounts at /product/console/, the Coexist shape) and one on ANOTHER origin (the auth-floored
# app's console, the Mockifyr shape). A second, links-only console rides /linked/console/.
GOLDPATH_CONSOLE_ORIGIN="$CONSOLE_URL" \
  GOLDPATH_CONSOLE_SERVICES="open=$SERVICE_URL;auth-floored=$SECURED_URL;tenant-scoped=$TENANT_URL" \
  GOLDPATH_CONSOLE_LINKS="Product=/product/console/;Floored console=$SECURED_URL/goldpath/console/" \
  GOLDPATH_LINKS_ONLY_CONSOLE="Product=/product/console/" \
  dotnet run --project "$ROOT/tests/Goldpath.Jobs.TestHost" -- "$CONNECTION" --console "$SERVICE_URL" --broker "$BROKER" --fleet console-smoke > /tmp/console-smoke-host.log 2>&1 &
HOST_PID=$!
for _ in $(seq 1 60); do
  grep -q "CONSOLEHOST-READY" /tmp/console-smoke-host.log && break
  sleep 1
done
grep -q "CONSOLEHOST-READY" /tmp/console-smoke-host.log || { echo "the app never came up:"; tail -30 /tmp/console-smoke-host.log; exit 1; }

echo "── the refusing apps (auth floor raised · tenant-scoped)"
docker exec "$PG_NAME" createdb -U postgres secured >/dev/null 2>&1 || true
docker exec "$PG_NAME" createdb -U postgres tenanted >/dev/null 2>&1 || true
GOLDPATH_CONSOLE_ORIGIN="$CONSOLE_URL" \
  dotnet run --project "$ROOT/tests/Goldpath.Jobs.TestHost" -- \
  "Host=localhost;Port=55432;Database=secured;Username=postgres;Password=smoke" \
  --console "$SECURED_URL" --secured --fleet secured-smoke > /tmp/console-smoke-secured.log 2>&1 &
SECURED_PID=$!
GOLDPATH_CONSOLE_ORIGIN="$CONSOLE_URL" \
  dotnet run --project "$ROOT/tests/Goldpath.Jobs.TestHost" -- \
  "Host=localhost;Port=55432;Database=tenanted;Username=postgres;Password=smoke" \
  --console "$TENANT_URL" --multitenant --fleet tenanted-smoke > /tmp/console-smoke-tenanted.log 2>&1 &
TENANT_PID=$!
for _ in $(seq 1 90); do
  grep -q "CONSOLEHOST-READY" /tmp/console-smoke-secured.log && grep -q "CONSOLEHOST-READY" /tmp/console-smoke-tenanted.log && break
  sleep 1
done
grep -q "CONSOLEHOST-READY" /tmp/console-smoke-secured.log || { echo "the secured app never came up:"; tail -20 /tmp/console-smoke-secured.log; exit 1; }
grep -q "CONSOLEHOST-READY" /tmp/console-smoke-tenanted.log || { echo "the tenant-scoped app never came up:"; tail -20 /tmp/console-smoke-tenanted.log; exit 1; }

# The cross-service registry the console reads at runtime (console RFC §3). Written here
# rather than committed: it names the three apps THIS run started, and an adopter's file
# names theirs. Vite serves public/ at the root, which is where the console looks.
mkdir -p "$ROOT/ui/console/public"
cat > "$ROOT/ui/console/public/console.config.json" <<JSON
{
  "services": [
    { "name": "open", "adminBaseUrl": "$SERVICE_URL" },
    { "name": "auth-floored", "adminBaseUrl": "$SECURED_URL" },
    { "name": "tenant-scoped", "adminBaseUrl": "$TENANT_URL" }
  ]
}
JSON

echo "── the console"
(cd "$ROOT/ui/console" && pnpm dev --port 5201 --strictPort > /tmp/console-smoke-vite.log 2>&1) &
VITE_PID=$!
for _ in $(seq 1 60); do
  curl -sf "$CONSOLE_URL" >/dev/null 2>&1 && break
  sleep 1
done
curl -sf "$CONSOLE_URL" >/dev/null || { echo "the console never came up:"; tail -20 /tmp/console-smoke-vite.log; exit 1; }

echo "── playwright"
cd "$ROOT/ui/console"
GOLDPATH_CONSOLE_URL="$CONSOLE_URL" GOLDPATH_SERVICE_URL="$SERVICE_URL" \
  GOLDPATH_SERVED_CONSOLE_URL="$SERVICE_URL/goldpath/console/" \
  GOLDPATH_LINKED_CONSOLE_URL="$SERVICE_URL/linked/console/" \
  GOLDPATH_SECURED_URL="$SECURED_URL" GOLDPATH_TENANT_URL="$TENANT_URL" \
  pnpm exec playwright test
