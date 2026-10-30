#!/usr/bin/env bash
# The air-gapped golden-manifest shape (GA gate G14, #338): the same pipeline as validate-gm.sh —
# generate → restore → build → db init → spec-lint → `goldpath db bundle` → apply → smoke — run a
# SECOND time with outbound network denied, the only sources being a local NuGet feed and a
# local image registry that a connected run filled first.
#
#   1. Connected (the mirror's side of the air gap). validate-gm.sh runs the shape as every other
#      shape runs, with its global-packages folder pointed at an empty directory after the
#      repository is packed, so that folder ends holding exactly the closure the app, its pinned
#      dotnet-ef tool and the CLI restored. Every image a container was created from during that
#      run is pushed to a registry on 127.0.0.1 and then removed from the daemon.
#   2. Denied. iptables rejects every outbound packet that is not loopback or a Docker bridge,
#      including a local HTTP proxy's port, and every forwarded one leaving the bridges; the proxy
#      variables are unset; and the script refuses to go on unless nuget.org and Docker Hub are
#      really unreachable. The closure is flattened into a feed, the images are pulled back from
#      the local registry only, and validate-gm.sh runs again into a fresh global-packages folder
#      with the generated app's nuget.config naming that feed and nothing else.
#
# Usage: validate-gm-offline.sh <ShapeName> [dotnet-new args...]
#        GOLDPATH_GM_OFFLINE_DROP=<package id>  removes that package from the offline feed: the
#        planted fault, which must fail at restore (NU1101) — the proof that nothing else serves it.
# The shape must use PostgreSQL: the apply step starts the shape's own postgres image.
# Needs root or passwordless sudo for iptables (a GitHub-hosted runner has it).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)

# ── --apply <app> <cli...>: validate-gm.sh's GOLDPATH_GM_BEFORE_SMOKE hook, in both phases.
if [ "${1:-}" = "--apply" ]; then
  APP=${2:?--apply <app> <cli...>}
  shift 2
  echo "── goldpath db bundle"
  (cd "$APP" && "$@" db bundle --path .)
  mapfile -t BUNDLES < <(find "$APP/artifacts/migrations" -maxdepth 1 -type f -name '*-migrations' | sort)
  [ "${#BUNDLES[@]}" -gt 0 ] || { echo "── db bundle produced no bundle under $APP/artifacts/migrations"; exit 1; }
  # The connected run builds the bundle only, so the closure holds what bundling restores. Which
  # postgres image the AppHost runs is known once it has run, so the apply is the offline run's.
  if [ -z "${GOLDPATH_GM_PG_IMAGE:-}" ]; then
    echo "── connected run: bundled; the apply runs offline, on the AppHost's own postgres image"
    exit 0
  fi

  PG=gm-offline-pg-$$
  PASSWORD=gm-offline-$RANDOM$RANDOM
  docker run -d --rm --name "$PG" -e POSTGRES_PASSWORD="$PASSWORD" -p 127.0.0.1::5432 "$GOLDPATH_GM_PG_IMAGE" >/dev/null
  trap 'docker rm -f "$PG" >/dev/null 2>&1 || true' EXIT
  for _ in $(seq 1 60); do
    docker exec "$PG" pg_isready -U postgres -h 127.0.0.1 >/dev/null 2>&1 && break
    sleep 1
  done
  docker exec "$PG" pg_isready -U postgres -h 127.0.0.1 >/dev/null || { echo "── postgres did not become ready"; exit 1; }
  PORT=$(docker port "$PG" 5432/tcp | head -1 | sed 's/.*://')
  for bundle in "${BUNDLES[@]}"; do
    owner=$(basename "$bundle" -migrations)
    connection="Host=127.0.0.1;Port=$PORT;Database=gm_offline_$(echo "$owner" | tr -c 'A-Za-z0-9\n' '_' | tr 'A-Z' 'a-z');Username=postgres;Password=$PASSWORD"
    echo "── apply $owner (and once more: a second apply must change nothing)"
    "$bundle" --connection "$connection"
    "$bundle" --connection "$connection" | tee "$APP/artifacts/migrations/$owner.second-apply.log"
    grep -q "No migrations were applied" "$APP/artifacts/migrations/$owner.second-apply.log" \
      || { echo "── the second apply of $owner applied something"; exit 1; }
  done
  exit 0
fi

NAME=${1:?usage: validate-gm-offline.sh <ShapeName> [dotnet-new args...]}
shift || true

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORK=$(cd "${GOLDPATH_GM_WORK:-"$(mktemp -d /tmp/goldpath-gm-offline.XXXXXX)"}" && pwd -P)
STAGE="$WORK/connected"
OFFLINE="$WORK/offline"
CLOSURE="$WORK/closure"
REGISTRY_IMAGE=docker.io/library/registry:2.8.3
REGISTRY=127.0.0.1:5000
REGISTRY_NAME=gm-offline-registry
SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO=sudo
echo "── offline shape: $NAME ($*) · workdir: $WORK"
mkdir -p "$STAGE" "$OFFLINE"

PROXY_VARS=(http_proxy https_proxy HTTP_PROXY HTTPS_PROXY all_proxy ALL_PROXY npm_config_https_proxy
  YARN_HTTPS_PROXY GLOBAL_AGENT_HTTPS_PROXY DOCKER_HTTPS_PROXY)
# Ports of every proxy that listens on loopback: loopback stays open for the registry and the
# AppHost, so a local forwarding proxy would otherwise carry traffic straight past the denial.
PROXY_PORTS=$(for v in "${PROXY_VARS[@]}"; do printf '%s\n' "${!v:-}"; done \
  | sed -nE 's#^[a-z]+://([^@/]*@)?(127\.[0-9.]+|localhost|\[::1\]):([0-9]+).*#\3#p' | sort -u)

restore_network() {
  for t in iptables ip6tables; do
    $SUDO "$t" -D OUTPUT -j GM_OFFLINE 2>/dev/null || true
    $SUDO "$t" -F GM_OFFLINE 2>/dev/null || true
    $SUDO "$t" -X GM_OFFLINE 2>/dev/null || true
  done
  $SUDO iptables -D DOCKER-USER -j GM_OFFLINE_FWD 2>/dev/null || true
  $SUDO iptables -F GM_OFFLINE_FWD 2>/dev/null || true
  $SUDO iptables -X GM_OFFLINE_FWD 2>/dev/null || true
}

cleanup() {
  restore_network
  docker rm -f "$REGISTRY_NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

deny_network() {
  restore_network
  for t in iptables ip6tables; do
    $SUDO "$t" -N GM_OFFLINE
    for port in $PROXY_PORTS; do
      $SUDO "$t" -A GM_OFFLINE -o lo -p tcp --dport "$port" -j REJECT
    done
    $SUDO "$t" -A GM_OFFLINE -o lo -j RETURN
  done
  # Docker's default address pool: the bridges the registry, the database and the AppHost's
  # containers sit on. Nothing else is reachable.
  $SUDO iptables -A GM_OFFLINE -d 172.16.0.0/12 -j RETURN
  for t in iptables ip6tables; do
    $SUDO "$t" -A GM_OFFLINE -j REJECT
    $SUDO "$t" -I OUTPUT 1 -j GM_OFFLINE
  done
  # Containers' own traffic is forwarded, not output: refuse anything leaving the bridges.
  $SUDO iptables -N GM_OFFLINE_FWD
  $SUDO iptables -A GM_OFFLINE_FWD -d 172.16.0.0/12 -j RETURN
  $SUDO iptables -A GM_OFFLINE_FWD -j REJECT
  $SUDO iptables -I DOCKER-USER 1 -j GM_OFFLINE_FWD
  for v in "${PROXY_VARS[@]}"; do unset "$v"; done

  # The run means nothing unless the network is really gone: prove it before relying on it.
  command -v curl >/dev/null || { echo "── curl is needed to check the denial: without it the check would pass vacuously"; exit 1; }
  if curl -sS -m 15 -o /dev/null https://api.nuget.org/v3/index.json 2>/dev/null; then
    echo "── nuget.org is still reachable: the network is NOT denied"; exit 1
  fi
  if timeout 60 docker pull docker.io/library/busybox:1.37.0 >/dev/null 2>&1; then
    echo "── Docker Hub is still reachable from the daemon: the network is NOT denied"; exit 1
  fi
  echo "── network denied: nuget.org and Docker Hub unreachable; loopback and the Docker bridges only"
}

# The image a container was created from, as the mirror stores it: registry host dropped.
mirror_ref() {
  local ref=$1 first=${1%%/*}
  if [ "$first" != "$ref" ] && { [[ "$first" == *.* ]] || [[ "$first" == *:* ]] || [ "$first" = localhost ]; }; then
    ref=${ref#*/}
  fi
  printf '%s/%s' "$REGISTRY" "$ref"
}

# ── 1. connected: the shape as every shape runs, the closure collected on the way
docker rm -f "$REGISTRY_NAME" >/dev/null 2>&1 || true
docker pull -q "$REGISTRY_IMAGE" >/dev/null
docker run -d --name "$REGISTRY_NAME" -p "$REGISTRY:5000" "$REGISTRY_IMAGE" >/dev/null

SINCE=$(date +%s)
GOLDPATH_GM_WORK="$STAGE" GOLDPATH_GM_APP_PACKAGES="$CLOSURE" \
  GOLDPATH_GM_BEFORE_SMOKE="$ROOT/scripts/validate-gm-offline.sh" \
  "$ROOT/scripts/validate-gm.sh" "$NAME" "$@"
UNTIL=$(date +%s)

# Every image a container was created from during the connected run, the registry's own excepted.
mapfile -t IMAGES < <(docker events --since "$SINCE" --until "$UNTIL" --filter type=container \
  --filter event=create --format '{{.Actor.Attributes.image}}' | grep -v "^$REGISTRY_IMAGE\$" | sort -u)
[ "${#IMAGES[@]}" -gt 0 ] || { echo "── the connected run created no container: nothing to mirror"; exit 1; }
PG_IMAGE=$(printf '%s\n' "${IMAGES[@]}" | grep -E '(^|/)postgres:' | head -1 || true)
[ -n "$PG_IMAGE" ] || { echo "── the shape ran no postgres container: the offline shape needs a PostgreSQL shape"; exit 1; }
echo "── mirror ${#IMAGES[@]} images into the local registry, then drop them from the daemon"
for image in "${IMAGES[@]}"; do
  mirror=$(mirror_ref "$image")
  echo "   $image -> $mirror"
  docker tag "$image" "$mirror"
  docker push -q "$mirror" >/dev/null
  docker rmi "$mirror" "$image" >/dev/null
done

echo "── flatten the closure into the offline feed ($(find "$CLOSURE" -name '*.nupkg' | wc -l) packages)"
mkdir -p "$OFFLINE/feed"
find "$CLOSURE" -name '*.nupkg' -exec cp {} "$OFFLINE/feed/" \;
if [ -n "${GOLDPATH_GM_OFFLINE_DROP:-}" ]; then
  drop=$(echo "$GOLDPATH_GM_OFFLINE_DROP" | tr 'A-Z' 'a-z')
  dropped=$(find "$OFFLINE/feed" -maxdepth 1 -regextype posix-extended \
    -regex ".*/${drop//./\\.}\\.[0-9][^/]*\\.nupkg" -print -delete | wc -l)
  [ "$dropped" -gt 0 ] || { echo "── GOLDPATH_GM_OFFLINE_DROP=$GOLDPATH_GM_OFFLINE_DROP matched no package in the feed"; exit 1; }
  echo "── PLANTED: $GOLDPATH_GM_OFFLINE_DROP removed from the offline feed ($dropped file): restore must fail"
fi
# The pinned specdrift the connected run installed (an air-gapped site carries its tools too).
cp -a "$STAGE/tools" "$OFFLINE/tools"

# ── 2. denied: the same shape again, from the feed and the registry alone
deny_network
echo "── pull the images back from the local registry only"
for image in "${IMAGES[@]}"; do
  mirror=$(mirror_ref "$image")
  docker pull -q "$mirror" >/dev/null
  docker tag "$mirror" "$image"
  docker rmi "$mirror" >/dev/null
done

GOLDPATH_GM_WORK="$OFFLINE" GOLDPATH_GM_APP_PACKAGES="$OFFLINE/packages" GOLDPATH_GM_FEED_ONLY=1 \
  GOLDPATH_GM_CLI="dotnet run --no-build --project $ROOT/tools/Goldpath.Cli --" \
  GOLDPATH_GM_PG_IMAGE="$PG_IMAGE" GOLDPATH_GM_BEFORE_SMOKE="$ROOT/scripts/validate-gm-offline.sh" \
  "$ROOT/scripts/validate-gm.sh" "$NAME" "$@"

echo "── $NAME GREEN OFFLINE (network denied; sources: $OFFLINE/feed and $REGISTRY)"
