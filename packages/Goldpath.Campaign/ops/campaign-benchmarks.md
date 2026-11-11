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
  part's. *(#284: since then the default slice is 60 s, so leadership is continuous;
  `CampaignContinuousLeadershipTests` measures every 10-second window within ±5% across
  two hand-overs at 50 TPS. The table above is the 50-second baseline and is not re-run here.)*
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

## S-FLEET-02a after batched publishing (#285, 2026-11-10) — still RED on this profile

Part 3 of 3. Every publish path of the pacer — a tick's new releases, the ripe retries, the orphan re-releases — hands its
messages to the seam's internal batch member as one batch (at most 64 publishes outstanding on the wire with no outbox)
instead of awaiting one broker round trip per item. The bench and its thresholds are unchanged:
`scripts/bench-campaign.sh smoke` (2 minutes) and `scripts/bench-campaign.sh broker` (15 minutes), at the package
defaults (continuous leadership since #284).

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker Engine 29, everything
co-located. It is the same profile as the baseline above. The runs were sequential, with nothing else running.
**Run id: none.** These are local runs; CI does not run the broker bench.
**Code:** `main` at `2782d3c` (before), and `agent/285-batched-release` at `86615f6` (after).

| Run | Code | Mean | p50 / p95 / min / max | Within ±5% of 1000 | Above 1000 | Exactly-once |
|---|---|---|---|---|---|---|
| smoke, 120 s | before (`2782d3c`) | **584.4/s** | 598 / 752 / 273 / 849 | **0 / 120 = 0.0%** | 0 | 71,179 = 71,179 = 71,179, 0 twice |
| smoke, 120 s | after (`86615f6`) | **969.2/s** | 1000 / 1036 / 619 / 1250 | **87 / 120 = 72.5%** | **10** | 97,328 = 97,328 = 97,328, 0 twice |
| broker, 900 s | after (`86615f6`) | **809.0/s** | 808 / 1058 / 250 / 1250 | **338 / 900 = 37.6%** | **50** | 724,500 = 724,500 = 724,500, 0 twice |

The 15-minute "before" number is the 2026-10-22 baseline above, 524.1/s. It was measured at the 50-second slice that
#284 has since replaced, and it was not re-run tonight.

**Per-tick database commands:** **7** per steady tick for one hungry, fully enumerated campaign, on `main` and on this
branch alike. `TickQueryCountTests` measures this on a tick-exact clock and pins it. This part removes no query, because
the publish was the round trip that dominated.
**Since 2026-11-11 it is 8:** R3-S8 (#290) reads the live ceilings, meaning the global row and each declared target
system's, in one query a tick. `TickQueryCountTests` pins 8.

### Reading the numbers

- **The publish path is no longer the binding constraint.**
  - Over two minutes, the leader releases at the configured rate: p50 exactly 1000, from 598.
  - Before, a 250-item tick was 250 sequential confirm round trips. Now it is four windows of at most 64.
- **It does not hold for 15 minutes on this profile.** The mean falls to 809/s, and the minimum to 250, one tick's worth.
  - In the smoke, 20,172 items were still queued at the pause; the 32 consumers executed 811/s against 1000 released.
  - Bench process, broker and database share 4 CPUs, and the item consumers' work grows with the backlog. With that
    load, the pacer's ticks run late.
  - Nothing in the publish path explains the decay. The co-located profile can be told apart from the code only by a
    run with the consumers on their own host: the CI reference profile, or the owner's Lima VM.
- **The windows above 1000 are the governor's tick arithmetic, not the publish.**
  - A tick refills by the time since the last one, and #292 allows a one-tick bank. A tick that runs late releases
    what it missed.
  - A one-second wall-clock window can therefore hold five tick bursts: the maximum, 1250, is exactly 5 × 250.
  - #292's burst rule, which S-FLEET-02a cites, is stated over a second OF TICKS (`BurstControlTests`), and it holds.
    This bench counts wall-clock seconds.
  - Before this change, the leader never released fast enough to show it.
  - Whether S-FLEET-02a's "zero windows above 1000" means wall-clock seconds, and so a smoother pacer, or seconds of
    ticks, is the owner's question in open-threads T67.

## S-FLEET-04 — pause stops every worker within three seconds (2026-10-23)

`ScenarioFleet04Tests` (#286; not a bench — it runs with the integration suite and asserts the
bar): two hosts on one RabbitMQ 4 item queue and one PostgreSQL 17 store, a leader (pacer +
consumers) and a worker (consumers only), **four consumer slots each**, a simulated device
answering after **600 ms**, `Tps = 200`, 400 targets. The operator pauses through the admin
verb once the queue holds at least 20 times the eight slots, watches for eight seconds, then
resumes. Every latency below is measured from the instant read just before the verb was called,
a lower bound on the commit (the pause's audit row landed 28–49 ms later).

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker
Engine 29, everything co-located. Code: `main` at `e9b2e4c` plus the test only. **Run id: none**
— local runs, not CI runs.

| Run | Backlog at the pause | Last claim | Last handler start | Last in-flight call returned | In flight at the pause | Peak concurrent calls | Claims after +3 s |
|---|---|---|---|---|---|---|---|
| 1 | 164 | −258 ms | −247 ms | +358 ms | 8 | 8 | 0 |
| 2 | 164 | −354 ms | −310 ms | +293 ms | 8 | 8 | 0 |
| 3 | 164 | −372 ms | −313 ms | +317 ms | 8 | 8 | 0 |
| 4 | 164 | −295 ms | −252 ms | +365 ms | 8 | 8 | 0 |
| 5 | 164 | −224 ms | −216 ms | +410 ms | 8 | 8 | 0 |
| 6 | 164 | −237 ms | −189 ms | +423 ms | 8 | 8 | 0 |

**Worst case over the six runs:** no item was claimed after the pause in any run, and the last
call in flight at the pause returned **423 ms** after it — under one device latency, against
the **3-second** bar. After resume, every run completed all 400 devices exactly once across
both hosts.

**The same test with the claim guard checking only the item's state** (the guard before R2.6):
**64** items claimed later than the pause + 3 s, the first at **+3.4 s**, the last at **+7.8 s**
with the queue still draining. That is the red the bar exists to catch.

### Reading the numbers

- **"Last claim" is negative by design.** With every slot busy when the operator acts, the last
  claim lands before the pause; the next free slot meets a paused campaign and claims nothing.
- **What the operator waits for is the calls already in flight**, at most one per slot, each
  bounded by the target's own latency. A target answering in 10 s means a pause that settles in
  about 10 s, though nothing new starts after the commit.
- **Slots are set on the item consumer** (`ConcurrentMessageLimit`). A bus-level
  `PrefetchCount` / `ConcurrentMessageLimit` did not reach the item endpoint in this test's
  first run (98 calls at once on two hosts configured for four each), and the item consumer
  shares its endpoint with the outcome sink. T48 tracks what that means for the S-FLEET-02a
  numbers above, whose "32 consumers" were set at the bus level.

## S-FLEET-02 — the rig (no result yet)

S-FLEET-02 is one campaign over **30,000,000** synthetic targets, seeded before the measured hour, then released
in real time for **60 minutes at 2,000 TPS** through the whole path on real RabbitMQ + PostgreSQL. Its pass
thresholds are the ones the owner signed on [#326](https://github.com/qorpe/goldpath/issues/326#issuecomment-6387926062)
([scenario-campaign.md](../../../docs/strategy/scenario-campaign.md#s-fleet-02s-rig-spec--signed-2026-10-24)). The run
is [#349](https://github.com/qorpe/goldpath/issues/349)'s owner step, tracked in open-threads T59. **This section
publishes no number, and S-FLEET-02 claims nothing until it does.**

### Running it

| Lane | Command | Where the results go |
|---|---|---|
| Mac, the pass lane | inside the Lima `bench` VM: `scripts/bench-campaign.sh fleet02` | `artifacts/bench/fleet02-<UTC>/` |
| Runner, the repeatable lane | dispatch `bench-fleet02.yml` (ubuntu-latest, 360-minute cap, full dataset by default) | the run's `fleet02-<run id>` artifact, kept 90 days, and the job summary |

The `bench` VM is a dedicated one, separate from the `ci` runner VM. The spec gives it all but 2 performance cores
and 75% of RAM, with its disk on the internal SSD. For example, on a Mac with 10 performance cores and 32 GB:
`limactl create --name=bench --cpus=8 --memory=24 --disk=100 template://docker`. The VM's own lines come from
`host.txt`. The **host's** lines are taken on the Mac, because a VM cannot read them:
`sysctl -n hw.model`, `sw_vers -productVersion` and `limactl list bench`.

The mode exports `GOLDPATH_BENCH_FLEET02=1`. Without it `CampaignFleet02BenchTests` shows as **skipped**, so
`bench.yml`'s monthly `Category=Bench` run never starts an hours-long bench. A skip is never reported as a pass: the
script exits 1 when the bench log shows one. The mode builds and runs the tests in **Release**, because this run
produces the published number, and `host.txt` records that configuration beside the commit and the run link.

| Knob (environment) | fleet02 default | What it is |
|---|---|---|
| `GOLDPATH_BENCH_CAMPAIGN_TARGETS` | 30000000 | the dataset, independent of the window (the spec says 30M, never shrunk) |
| `GOLDPATH_BENCH_CAMPAIGN_SECONDS` | 3600 | the measured window |
| `GOLDPATH_BENCH_CAMPAIGN_TPS` | 2000 | the configured rate, which is also the per-second ceiling |
| `GOLDPATH_BENCH_CAMPAIGN_FLOOR_TPS` | 2000 | the signed mean |
| `GOLDPATH_BENCH_CAMPAIGN_CONSUMERS` | 32 | the bus-level consumer knob; the run reports the peak concurrent handler calls it actually reached (open-threads T48) |
| `GOLDPATH_BENCH_CAMPAIGN_LATENCY_MS` | 20 | the simulated target's latency |
| `GOLDPATH_BENCH_CAMPAIGN_SEED_MINUTES` | 180 | the most the seeding may take before the run fails |
| `GOLDPATH_BENCH_OUT` | `artifacts/bench/fleet02-<UTC>` | the output directory |

### What one run writes

- `host.txt`: date, commit, run id or command, kernel, CPU model, logical CPUs, memory, disk, Docker and .NET versions,
  and every `GOLDPATH_BENCH_*` knob.
- `release-per-second.csv`: the released count of each whole second of the window, at the publish instant (the same
  clock as S-FLEET-02a). `send-per-second.csv` is the same series at the instant the pacer handed each message to the
  bus. It is a diagnostic, not the verdict.
- `headroom.csv` (`scripts/bench-headroom.sh`, one row every 10 s on a fixed cadence): host CPU busy and iowait, memory
  used and available, disk read and write rates and the busiest disk's utilisation; both containers' CPU and memory;
  RabbitMQ's ready and unacknowledged messages; PostgreSQL's backends and cumulative commits, block reads and hits.
  The first saturated resource is read from this series by the report's author. The rig does not guess it.
- `verdict.txt` and the `BENCH-CAMPAIGN fleet02:` lines in `bench.log`.

### How the verdict reads the signed thresholds

`CampaignFleet02Verdict` computes it, and `CampaignFleet02VerdictTests` pins the arithmetic without a container.

- **Mean:** published messages in the window, divided by its seconds, against `_FLOOR_TPS`.
- **p95 jitter:** the nearest-rank 95th percentile of each second's deviation from the configured rate, so at least
  95% of the seconds fall within ±5%. A second at exactly 1,900 for 2,000 is inside the band.
- **Ceiling:** no whole second above the configured rate.
- **Lost and duplicated.** A publish with no distinct released item is a duplicate, and so is a target executed twice.
  The following are lost: a terminal item the sink did not count, a target executed with no terminal row (or the
  reverse), and an item released with no publish. Items the pause strands on the queue stay durable `Released` rows.
  They are counted as **stranded, not lost**. That is how the rig reads "the sink's count equals the released count"
  when the hour ends with a pause.
- **Label:** `FAIL` if anything was lost, doubled or above the ceiling, whatever the rate. Otherwise `HARDWARE CEILING`
  if the mean missed the floor (signed point 4: it still publishes, with TPS per logical CPU). Otherwise `FAIL` if the
  jitter missed, and `PASS` if not.

### Proving the rig (not a result)

Two short runs on the overnight agent's cloud sandbox (Linux x64, 4 logical CPUs / 16 GB, Docker Engine 29, everything
co-located), 2026-11-04, branch `agent/349-fleet02-rig` off `main`@`259bd53`, no run id. The command was
`GOLDPATH_BENCH_CAMPAIGN_TARGETS=200000 GOLDPATH_BENCH_CAMPAIGN_SECONDS=60 GOLDPATH_BENCH_CAMPAIGN_TPS=500
GOLDPATH_BENCH_CAMPAIGN_FLOOR_TPS=500 scripts/bench-campaign.sh fleet02`. The second run added the send-instant
diagnostic.

| | Run 1 | Run 2 |
|---|---|---|
| Seeding, 200,000 targets (Created → snapshot complete, held at `Ready`) | 55.7 s | 60.7 s |
| Released before the start was cleared | 0 | 0 |
| Mean, at the publish instant | 478.9/s | 477.6/s |
| Per-second p5 / p50 / p95 | 408 / 483 / 532 | 368 / 492 / 532 |
| Seconds within ±5% of 500 | 35 / 60 | 29 / 60 |
| Seconds above 500, at the publish instant / at the send instant | 17 / — | 16 / 15 (max 561 / 560) |
| Published · stranded · terminal · sink · executed · twice | 29,625 · 124 · 29,501 · 29,501 · 29,501 · 0 | 29,625 · 130 · 29,495 · 29,495 · 29,495 · 0 |
| Peak concurrent handler calls (bus knob 32) | 156 | 151 |
| Label | FAIL (ceiling) | FAIL (ceiling) |

These runs prove that the rig seeds without releasing, measures, accounts for every item and writes its files. Their
rates are not S-FLEET-02's: the dataset is 150 times smaller and the hour is a minute. They produced three readings.
The first two are owed to the owner before the run, and both are on #349:

- **At 500 TPS, about a quarter of the whole seconds exceed the ceiling, at the send instant as well as the publish
  instant.** The pacer's bucket refills one tick's worth per tick, and #292's strict rule is proven at **tick
  granularity**. A tick's batch is handed to the bus over time, though, so a whole second of message instants can hold
  part of a fifth tick: 560 is 500 plus about half a 125-message tick. At 1000 TPS S-FLEET-02a never saw this, because
  its release path bound at about 600/s. The signed "no one-second window above the ceiling" therefore needs a
  definition before the hour is judged: message instants, or ticks.
- **The floor equals the ceiling.** With the configured rate at 2,000 and no second allowed above it, a mean of at
  least 2,000 passes only if every second is exactly 2,000. The fact prints that note whenever the floor is not below
  the configured rate.
- **The bus knob does not hold.** 32 configured consumers ran up to 156 handler calls at once, which is open-threads
  T48 measured.

The shared host changed nothing S-FLEET-02a measures. Its 2-minute smoke on the new `CampaignReleaseRig`, run the
same night on the same sandbox, was red on rate as its baseline is: 654.6/s mean, p50 651, p95 750, max 815,
0 of 120 seconds in band and 0 above the ceiling. Exactly-once held over 79,250 items, and the run reached 179 peak
concurrent handler calls against a knob of 32. `main`@`259bd53`, before the extraction, gave 649.2/s, p50 647, p95 771,
max 851, 0 in band and 0 above, with exactly-once over 78,770 items.

## Admin-contract R4.3 — `GET /{id}/outcomes` on a 1M-item campaign (2026-11-11)

The first admin read that scans more than one page (admin-contract R4.3, #296): two grouped counts
over one campaign's items, `GROUP BY State` and, over the failed items, `GROUP BY ErrorCode`.
`CampaignBenchTests.Outcomes_on_a_one_million_item_campaign` via `scripts/bench-campaign.sh`
seeds 1,000,000 items set-based (60% `Succeeded`, 30% `Failed` under 25 codes plus no code, 10%
`Pending`), runs `ANALYZE`, makes one warm-up call, then times five calls of
`GoldpathCampaignAdminService.GetOutcomesAsync`.

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs (Intel Xeon @ 2.10 GHz)
/ 15 GB, Docker Engine 29.9.2, PostgreSQL 17 (`postgres:17-alpine`) in Testcontainers, co-located.
Release build. **Commit:** `7ed23b0`. **Run:** local, `dotnet test tests/Goldpath.IntegrationTests -c
Release --filter "FullyQualifiedName~CampaignBenchTests.Outcomes_on_a_one_million_item_campaign"`,
twice.

| Run | Median of 5 | Slowest of 5 |
|---|---|---|
| 1 | **116 ms** | 122 ms |
| 2 | **107 ms** | 129 ms |

- **Against the governor's poll:** R4.3 says that if the call takes longer than the poll interval
  (5 s), the console polls it less often than the detail. At about 110 ms it does not, so the
  console may poll both on the same interval.
- **What it scales with:** the campaign's item count, not the page size. The `(CampaignId, State, …)`
  indexes serve the campaign predicate; no index was added for it (R3's one-migration rule). The
  scan grows with the campaign, and no 30M figure is claimed here: it is measured on the S-FLEET-02
  rig before anyone promises one.
- **Not measured here:** Oracle, SQL Server, and the CI reference profile.

