# Goldpath.Campaign — measured performance (campaign RFC §6)

Reference profile: local dev machine (Apple Silicon), PostgreSQL 17 in Testcontainers,
`scripts/bench-campaign.sh` (tests: `CampaignBenchTests`, Trait `Category=Bench`).
Re-run and update this file whenever the enumeration, release or sink path changes.

| Proof | Budget | Measured (2026-07-10) | Verdict |
|---|---|---|---|
| Enumeration: 1M targets materialized into item rows | memory flat, minutes not hours | **15.3 s = 65,266 rows/s** | a 30M campaign enumerates in ~8 min |
| Pacer precision: configured 200 TPS, measured over 15 s | ±5% steady-state | **186.7 released/s** (−6.7%, includes cold-start slice) | governor holds; see note |
| LIVE throttle: 200 → 50 TPS mid-run, next 15 s | takes effect within one tick | **50.0 released/s** — exact | D6 delivered |
| Outcome sink: 100k outcomes, batches of 200 | 30M items never mean 30M writes | **1.7 s = 57,462 outcomes/s** | set-based flushes hold |

## Reading the numbers

- **Enumeration is not the bottleneck:** 65k rows/s means even a device-fleet-scale 30M
  plan materializes inside a coffee break — and it streams, so memory stays flat.
- **The pacer's −6.7% includes the cold start:** the measured window opens BEFORE the
  first tick banks budget and while the same slice is still enumerating; steady-state
  ticks track the ceiling (the throttle phase — pure steady-state — measured EXACTLY
  50.0/s). The governor never overshoots, which is the side that pages.
- **The throttle number is the product:** an operator typed a new ceiling on a LIVE
  campaign and the very next slice held it exactly — no restart, no redeploy, no drain.
- **Sink throughput is the honesty floor:** 57k outcomes/s of durable terminal evidence
  (two set-based item updates + relative counters per 200-batch). The consumers' real
  external calls dominate in production; the module's own write cost is ~17 µs/outcome.

## Reference profile (CI): ubuntu-latest, 4 vCPU / 16 GB — 2026-07-13

The PINNED profile adopters can rent and budget against (`bench.yml` dispatch,
run 29228081113; the dev-machine numbers above/below are the fast point, not the promise).

| Proof | Budget | CI measured | Verdict |
|---|---|---|---|
| Enumeration: 1M targets materialized | minutes not hours | **70.0 s = 14,277 rows/s** | a 30M campaign enumerates in ~35 min on 4 vCPU |
| Pacer precision: configured 200 TPS over 15 s | ±5% steady-state | **140.0 released/s** | CPU/IO-bound below target on 4 vCPU — the governor UNDERSHOOTS, never over-releases; size TPS to the hardware |
| LIVE throttle: → 50 TPS, next 15 s | within one tick | **50.8 released/s** | exact on CI too — D6 holds everywhere |
| Outcome sink: 100k outcomes (batches of 200) | set-based writes | **5.5 s = 18,119 outcomes/s** | holds |

## S-FLEET-02a — one leader at 1000 TPS on a real broker (baseline, 2026-10-22)

The first number above 200 TPS, and the first with a broker on the path (#283, Part 1 of 3:
measure, do not tune). `CampaignBrokerBenchTests` via `scripts/bench-campaign.sh broker`: one
campaign, `Tps = 1000`, MaxInFlight 100,000 (so it never binds), on Testcontainers
RabbitMQ 4 + PostgreSQL 17, with the **package defaults** for `LeaderTick` (250 ms),
`LeadershipSlice` (50 s) and the pacer cron (once a minute). 32 competing item consumers,
a simulated target answering after 20 ms, the batching outcome sink. 15 minutes measured from
the first release; then the campaign is paused and claimed work drains.

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker
Engine 29, everything co-located (bench process, broker, database). Not the CI reference
profile and not a dev machine; treat it as a third point. Code: `main` at `204fe6c`
(the bench adds tests only).

| Measure (one-second windows, 900 of them) | Threshold (S-FLEET-02a) | Measured | Verdict |
|---|---|---|---|
| Published item messages, mean | 1000/s | **524.1/s** (471,694 in 900 s) | below |
| Per-second p50 / p95 / min / max | — | **606 / 750 / 0 / 871** | — |
| Windows within ±5% of 1000 | ≥ 95% | **0 / 900 = 0.0%** | **RED** |
| Windows above the ceiling | 0 | **0** | holds |
| Empty windows | — | **136** (15%) | the slice/cron gap, below |
| Exactly-once: sink = durable = handler executions | equal, nothing twice | **472,664 = 472,664 = 472,664, 0 twice** | holds |

The 2-minute smoke (`scripts/bench-campaign.sh smoke`) on the same profile gave 526.0/s mean,
p50 602, p95 744, max 813, 18 empty windows, exactly-once over 63,821 items. It proves the
bench; it is not a result.

### Reading the numbers

- **The governor never over-releases.** Max 871, zero windows above 1000: the side that pages
  holds, as it did at 200 TPS.
- **About 15% of all seconds release nothing, by configuration.** The default leader leads for
  a 50-second slice per one-minute cron fire, so roughly 10 s in every 60 have no leader. The
  136 empty windows in 900 s match that gap. At these defaults, no tuning of the release path can
  reach 95% of windows within band. The slice/cron relation is a Part 2/3 question, not this
  part's.
- **Inside a slice, the leader releases about 600/s against 1000.** That is p50 606 and
  p95 750, with no window in band. The per-tick release path (publish, then stamp the batch)
  is the binding constraint on this profile, not the consumers: only 336 items were still queued
  at the pause.
- **Exactly-once holds at this rate** across 472,664 items on a real broker.

### A measurement note

The item row's `ReleasedAt` cannot be used as the release clock. `ReleaseBatchAsync` publishes
first and stamps the batch `Released` after. A consumer that claims a still-`Pending` row
first (claim accepts `Pending`) leaves the stamp's `State == Pending` guard nothing to update,
so on a fast consumer most settled items carry `ReleasedAt = null`. A first smoke run that used
the stamps counted 18,494 releases in its window, against 63,480 items executed overall. The
bench therefore stamps every item message at the bus with a publish observer.
