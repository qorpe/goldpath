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
  *) echo "usage: $0 [all|broker|smoke|enum]" >&2; exit 2 ;;
esac
dotnet test tests/Goldpath.IntegrationTests --nologo --filter "$FILTER" --logger "console;verbosity=detailed" \
  | grep -E "BENCH-CAMPAIGN|^(Passed|Failed)!|^\s+(Passed|Failed) Goldpath|Assert\.|windows were within|within 3 minutes|did not drain"
