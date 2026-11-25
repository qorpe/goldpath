# Goldpath.Approvals — measured performance (approvals RFC §6)

The first Approvals table (GA gate G13, #596). The approvals RFC §6 names per-ladder throughput and oldest-pending
as the module's operational signals; these are the first numbers behind them.

Profile: a cloud sandbox container, 4 vCPU (Intel Xeon @ 2.10 GHz), 15 GB, Docker 29.9.3, .NET SDK 10.0.115,
PostgreSQL 17 (`postgres:17-alpine`) in Testcontainers on the same host. Measured 2026-11-24 on `main@8e201ca` plus
the bench class itself (no package code differs). Command: `scripts/bench-approvals.sh` (tests: `ApprovalsBenchTests`,
Trait `Category=Bench`, run alone in its own collection). This is a development profile and a local run, not a CI
run: the CI reference profile is measured at the rc.1 re-baseline (#345).

What runs: the public `GoldpathApprovalEngine` on `GoldpathEfApprovalStore`, a two-rung ladder (`manager`, a quorum
of two distinct signers, escalating after 8 h to `general-manager`), N = 10,000 requests, all routed to the manager
rung, from 50 requesters. Re-run and update this file whenever the request, decide, worklist or sweep path changes.

| Proof | Budget | Measured (2026-11-24) | Verdict |
|---|---|---|---|
| `RequestAsync`: 10,000 requests | — (baseline) | **9.5 s ≈ 1,049 requests/s** (0.95 ms/request) | one insert per request |
| `DecideAsync`: grant all 10,000 through the quorum (20,000 decisions, two distinct checkers each, four-eyes live) | — (baseline) | **78.7 s ≈ 254 decisions/s** (3.93 ms/decision) | every request `Granted`, asserted |
| `WorklistAsync`: one role, 10,000 pending (20 samples after one warm-up) | — (baseline) | **p50 40.8 ms, p95 214.3 ms** (max 276.6 ms) | every pending request returned, asserted |
| `EscalateOverdueAsync`: one sweep, 10,000 overdue | — (baseline) | **11.8 s ≈ 851 moved/s** | all 10,000 moved to `general-manager`, asserted |

No accepted RFC declares an Approvals budget, so the Budget column says "— (baseline)" and no fact asserts a time.
Each fact asserts only that the work it timed is the work the row names.

**Two runs, same profile, same commit:** request 9.5 s / 9.7 s; decide 78.7 s / 78.5 s; worklist p50 40.8 / 42.9 ms
and p95 214.3 / 226.2 ms; sweep 11.8 s / 12.5 s. The largest spread is the sweep's, about 6%. The table holds run 1.

**The measured path is the guarded one.** Before the decide loop, the requester decides their own request and the
fact asserts `FourEyesViolation`; the loop then runs on the same engine and store. With the four-eyes check removed
from the engine the fact fails, so the table cannot be produced by a path that skipped it.

## Reading the numbers

- **A decision costs about four requests.** A request is one insert. A decision reads the request, reads its
  signatures (distinct-eyes checks the whole chain), and writes the signature and the new state as one unit checked
  against the version it read (#480). That is the price of the four-eyes and distinct-eyes guarantees, and it is
  per decision, so a quorum of two costs twice. 254 decisions/s is about 900,000 decisions an hour on this profile;
  the people deciding are the bound, not the engine.
- **The worklist is linear in the pending queue.** `WorklistAsync` reads every pending request
  (`IGoldpathApprovalStore.GetPendingAsync`) and filters by role, delegation and four-eyes in memory. 10,000 pending
  in about 40 ms is fine for a console; the p95 is several times the p50, which a refresh every few seconds hides
  and a page load does not. A queue an order of magnitude larger scales the read with it: there is no paging on
  the store seam today, so that is the number to re-measure before an adopter keeps 100,000 requests pending.
- **The sweep is one write per moved request.** It reads every pending request once and writes each overdue one
  with its escalation step in the trail. 10,000 overdue in 12 s fits well inside the five-minute schedule
  `AddGoldpathApprovalsJobs()` registers; a sweep that finds nothing overdue costs only the read.
- **Oldest-pending is not a separate cost.** It is the first row of a role's worklist, which is ordered by `PendingSince`, so the
  worklist row above is its price too.

## Where the numbers come from next

The monthly `bench.yml` runs `Category=Bench&Category!=Oracle`, so it picks `ApprovalsBenchTests` up on its next run
with no workflow change; its numbers stay in that run's artifact until Part 4/4 of G13 (#345) makes them committable.
No Oracle column: G7 owns those.
