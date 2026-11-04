#!/usr/bin/env bash
# Campaign RFC §6 performance proofs: 1M enumeration + pacer precision (200 TPS, live
# throttle to 50) + 100k outcome sink, on real PostgreSQL; and S-FLEET-02a — one campaign at
# 1000 TPS through the whole release path on real RabbitMQ + PostgreSQL (Testcontainers).
# Record the numbers in packages/Goldpath.Campaign/ops/campaign-benchmarks.md.
#
#   scripts/bench-campaign.sh            # everything; the broker bench runs 15 minutes
#   scripts/bench-campaign.sh smoke      # the broker bench only, 2 minutes (proves the bench, not a result)
#   scripts/bench-campaign.sh broker     # the broker bench only, full length
#   scripts/bench-campaign.sh enum       # enumeration THROUGH THE PACER only (#288); size from
#                                        # GOLDPATH_BENCH_CAMPAIGN_ENUM_COUNT (1000000)
#   scripts/bench-campaign.sh fleet02    # S-FLEET-02 (#349, the rig spec signed on #326): 30M targets
#                                        # seeded, then 60 minutes at 2000 TPS, with the 10-second
#                                        # headroom sampler beside it. Hours, not minutes; never in `all`.
#
# fleet02 writes everything to GOLDPATH_BENCH_OUT (artifacts/bench/fleet02-<UTC>): the bench log,
# release-per-second.csv, verdict.txt, headroom.csv and host.txt (what the machine running it
# says about itself). Its knobs, beside the broker ones: _TARGETS (30000000), _FLOOR_TPS (2000),
# _SEED_MINUTES (180); _SECONDS defaults to 3600 and _TPS to 2000 in this mode. A shorter run
# for proving the rig (not a result): GOLDPATH_BENCH_CAMPAIGN_TARGETS=200000
# GOLDPATH_BENCH_CAMPAIGN_SECONDS=60 GOLDPATH_BENCH_CAMPAIGN_TPS=500 scripts/bench-campaign.sh fleet02
#
# Broker-bench knobs (environment): GOLDPATH_BENCH_CAMPAIGN_SECONDS (900), _TPS (1000),
# _CONSUMERS (32), _LATENCY_MS (20).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ -d "$HOME/.dotnet/sdk" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
cd "$ROOT"
case "${1:-all}" in
  all)    FILTER="FullyQualifiedName~CampaignBenchTests|FullyQualifiedName~CampaignBrokerBenchTests|FullyQualifiedName~CampaignPacerEnumerationBenchTests" ;;
  broker) FILTER="FullyQualifiedName~CampaignBrokerBenchTests" ;;
  enum)   FILTER="FullyQualifiedName~CampaignPacerEnumerationBenchTests" ;;
  smoke)  FILTER="FullyQualifiedName~CampaignBrokerBenchTests"; export GOLDPATH_BENCH_CAMPAIGN_SECONDS="${GOLDPATH_BENCH_CAMPAIGN_SECONDS:-120}" ;;
  fleet02) FILTER="FullyQualifiedName~CampaignFleet02BenchTests" ;;
  *) echo "usage: $0 [all|broker|smoke|enum|fleet02]" >&2; exit 2 ;;
esac

if [ "${1:-all}" = fleet02 ]; then
  export GOLDPATH_BENCH_FLEET02=1
  export GOLDPATH_BENCH_CAMPAIGN_SECONDS="${GOLDPATH_BENCH_CAMPAIGN_SECONDS:-3600}"
  export GOLDPATH_BENCH_CAMPAIGN_TPS="${GOLDPATH_BENCH_CAMPAIGN_TPS:-2000}"
  export GOLDPATH_BENCH_CAMPAIGN_TARGETS="${GOLDPATH_BENCH_CAMPAIGN_TARGETS:-30000000}"
  OUT="${GOLDPATH_BENCH_OUT:-$ROOT/artifacts/bench/fleet02-$(date -u +%Y%m%dT%H%M%SZ)}"
  mkdir -p "$OUT"
  export GOLDPATH_BENCH_OUT=$(cd "$OUT" && pwd)
  # The report header's machine line, as the machine running the bench sees itself. On the Mac
  # lane this is the Lima `bench` VM; the host's lines (sysctl hw.model, sw_vers, limactl list)
  # are taken on the Mac itself — see ops/campaign-benchmarks.md, "S-FLEET-02 — the rig".
  {
    echo "date_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "commit=$(git rev-parse HEAD 2>/dev/null || echo unknown)"
    echo "run=${GITHUB_RUN_ID:+$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID}${GITHUB_RUN_ID:-local: $0 fleet02}"
    echo "kernel=$(uname -srm)"
    echo "cpu_model=$(awk -F': ' '/^model name/{print $2; exit}' /proc/cpuinfo 2>/dev/null)"
    echo "logical_cpus=$(nproc)"
    echo "mem_total_mib=$(awk '/^MemTotal:/{print int($2/1024)}' /proc/meminfo 2>/dev/null)"
    echo "disk_root=$(df -h --output=size,fstype / 2>/dev/null | tail -1 | xargs)"
    echo "docker=$(docker version --format '{{.Server.Version}}' 2>/dev/null)"
    echo "dotnet=$(dotnet --version 2>/dev/null)"
    env | grep -E '^GOLDPATH_BENCH_' | sort
  } > "$GOLDPATH_BENCH_OUT/host.txt"
  "$ROOT/scripts/bench-headroom.sh" "$GOLDPATH_BENCH_OUT/headroom.csv" 10 &
  SAMPLER=$!
  trap 'kill "$SAMPLER" 2>/dev/null || true' EXIT
  set +e
  dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" > "$GOLDPATH_BENCH_OUT/bench.log" 2>&1
  STATUS=$?
  set -e
  grep -aE "BENCH-CAMPAIGN|^(Passed|Failed|Skipped)!|^\s+(Passed|Failed|Skipped) Goldpath|Assert\.|Error Message" "$GOLDPATH_BENCH_OUT/bench.log" || true
  echo "fleet02: results in $GOLDPATH_BENCH_OUT"
  # A skip is not a run: the opt-in did not reach the test.
  if grep -aqE "^\s+Skipped Goldpath.*CampaignFleet02BenchTests" "$GOLDPATH_BENCH_OUT/bench.log"; then
    echo "fleet02: the bench was SKIPPED, nothing was measured" >&2
    exit 1
  fi
  exit "$STATUS"
fi
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" \
  | grep -E "BENCH-CAMPAIGN|^(Passed|Failed)!|^\s+(Passed|Failed) Goldpath|Assert\.|windows were within|within 3 minutes|did not drain"
