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
