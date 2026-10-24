# Goldpath.Campaign — measured performance (campaign RFC §6)

Reference profile: local dev machine (Apple Silicon), PostgreSQL 17 in Testcontainers,
`scripts/bench-campaign.sh` (tests: `CampaignBenchTests`, Trait `Category=Bench`).
Re-run and update this file whenever the enumeration, release or sink path changes.

| Proof | Budget | Measured (2026-07-10) | Verdict |
|---|---|---|---|
| Enumeration: 1M targets materialized into item rows (the engine loop, not the pacer) | memory flat, minutes not hours | **15.3 s = 65,266 rows/s** | the engine loop takes 30M in ~8 min; a campaign enumerates through the pacer, measured in the #288 section below |
| Pacer precision: configured 200 TPS, measured over 15 s | ±5% steady-state | **186.7 released/s** (−6.7%, includes cold-start slice) | governor holds; see note |
| LIVE throttle: 200 → 50 TPS mid-run, next 15 s | takes effect within one tick | **50.0 released/s** — exact | D6 delivered |
| Outcome sink: 100k outcomes, batches of 200 | 30M items never mean 30M writes | **1.7 s = 57,462 outcomes/s** | set-based flushes hold |

## Reading the numbers

- **The engine loop is not the bottleneck:** 65k rows/s means the loop materializes a
  device-fleet-scale 30M plan inside a coffee break, and it streams, so memory stays flat. Until
  #288 an adopter's campaign did NOT get that rate: the pacer took one 2,000-row batch per
  250 ms tick, a ceiling of 8,000 rows/s. The #288 section below measures the pacer path.
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
| Enumeration: 1M targets materialized (the engine loop, not the pacer) | minutes not hours | **70.0 s = 14,277 rows/s** | the engine loop takes 30M in ~35 min on 4 vCPU; the pacer path is in the #288 section below |
| Pacer precision: configured 200 TPS over 15 s | ±5% steady-state | **140.0 released/s** | CPU/IO-bound below target on 4 vCPU — the governor UNDERSHOOTS, never over-releases; size TPS to the hardware |
| LIVE throttle: → 50 TPS, next 15 s | within one tick | **50.8 released/s** | exact on CI too — D6 holds everywhere |
| Outcome sink: 100k outcomes (batches of 200) | set-based writes | **5.5 s = 18,119 outcomes/s** | holds |

## #288 — enumeration through the pacer, the path a campaign takes (2026-10-24)

The enumeration rows above measure the **engine loop** (`EnumerateStepAsync` called back to back).
A campaign does not run that loop: its leader enumerates inside the pacer's tick. Until #288 the
pacer took **one** `EnumerationBatchSize` batch per campaign per tick, so at the defaults (2,000
rows, 250 ms) a snapshot could not pass **8,000 rows/s**, and 35M targets needed more than
73 minutes before the first release. Since #288 the tick spends what it would otherwise sleep
enumerating further (Phase 5). The step is guarded while another campaign releases, so its
releases keep their cadence.

`CampaignPacerEnumerationBenchTests` via `scripts/bench-campaign.sh enum`
(`GOLDPATH_BENCH_CAMPAIGN_ENUM_COUNT` sets the size): one campaign, nothing else running, the
**package defaults** (250 ms tick, 50 s slice, 2,000-row batch), PostgreSQL 17 in
Testcontainers, slices run back to back (the cron's gap between slices is not modelled). The
clock stops when the row flips to enumeration-complete. The target payload is the bench's
`{"Id":n}`; a wider target adds to the table size, not to the row count.

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker
Engine 29, everything co-located. It is neither the dev reference profile above nor the CI one;
the owner allowed the 35M number off the CI profile, labelled as such (#288). Code: `56cee73` on
`agent/288-enumeration-off-the-tick` (the "before" row: `main` at `e9b2e4c`). **Run id: none**,
one local run each. The CI-profile row is `bench.yml` dispatched on that branch at `a36532e`
(same pacer code), [run 40049231143](https://github.com/qorpe/goldpath/actions/runs/40049231143).

| Run | Code | Wall time | Rows/s | Items table + indexes | Leader peak (managed / working set) |
|---|---|---|---|---|---|
| 1M, engine loop (same machine, for scale) | `1231cbf` (engine unchanged by #288) | 57.4 s | 17,429 | — | — |
| 1M through the pacer, **before** #288 | `e9b2e4c` | 126.7 s | **7,895** | 81 + 60 MiB | 114 / 308 MiB |
| 1M through the pacer | `56cee73` | 59.5 s | **16,801** | 81 + 62 MiB | 122 / 310 MiB |
| 5M through the pacer | `56cee73` | 294.5 s | 16,975 | 403 + 310 MiB | 142 / 324 MiB |
| **35M through the pacer** | `56cee73` | **2,239.9 s = 37.3 min** | **15,625** | **2,820 + 2,168 MiB** (149 B/item) | **162 / 341 MiB** |
| 1M through the pacer, **CI reference profile** (ubuntu-latest, 4 vCPU / 16 GB) | `a36532e` | 71.4 s | **14,008** | 81 + 61 MiB | 146 / 483 MiB (the working set includes the benches that ran before it in the same process) |

### Reading the numbers

- **The pacer now runs at the engine loop's rate:** 16,801 against 17,429 rows/s at 1M on the
  same machine (96%). Before #288 it ran at the one-batch-per-tick ceiling (7,895 measured,
  8,000 in theory). On the CI profile it took 1M in 71.4 s, against the engine loop's 70.0 s
  published above (the same run's engine-loop bench gave 10,539 rows/s, but that one runs in
  parallel with the other benches and the pacer bench runs alone, so it is not compared here).
- **35M is S-FLEET-02's enumeration leg on PostgreSQL** on this profile: 37 minutes to `Running`.
  It is not the 30M rig run (G10c), which is still where a promise-grade number comes from.
- **Memory stays flat with size:** the leader's managed heap peaked at 122, 142 and 162 MiB for 1M,
  5M and 35M. The rate drifts down slowly as the item table's indexes grow (about 17,500 rows/s in
  the first slices, about 15,000 in the last).
- **Storage is about 149 bytes per item** with this payload: 5 GB for 35M, of which 43% is the
  indexes (the primary key and the three secondary ones).
- **With another campaign releasing, enumeration gets less:** a step then starts only if the
  slowest step of the tick still fits before the tick ends. This bench does not measure that
  case; the unit tests pin its arithmetic (`EnumerationAheadTests`).

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
(the bench adds tests only). **Run id: none** — a local sandbox run, not a CI run. The
first `bench.yml` run after merge records its run id here.

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
