# Scenario campaign — every claim retold as a business story, then executed

Status: LIVING plan (owner + agent co-author; started 2026-07-29). This is the program the
owner set: **finish the UI, then walk EVERY capability through real business scenarios,
step by step, nothing skipped.** The unit/integration/smoke gates prove the mechanics;
this campaign proves the STORIES a buyer or an operator would actually tell.

## How this file works

- Anyone adds a scenario with the template below. A scenario is not "done" when the code
  exists — it is done when the **evidence** column points at a run someone can replay.
- Scenarios that need scale hardware say so in `needs` and wait for it rather than being
  quietly downgraded.
- Sector-specific numbers are fine; CUSTOMER names are not (public repo — the private
  briefs stay outside).

### Template

```
S-<AREA>-<NN> — <one-line business sentence>
  modules:  <which of the ladder + cross-cutting it exercises>
  given:    <starting state, in business words>
  when:     <what the operator/system does — step by step>
  then:     <what must be observably true, including on the CONSOLE>
  needs:    <local | scale-rig (spec) | second-service | nothing special>
  evidence: <empty until the run exists: script/test name + where its output lives>
```

## Phase order (the owner's sequence)

1. **UI finalization** — the owner's style feedback batches on the live console +
   campaign R1's governor fields. UI is DONE when the owner says so, not before.
2. **Scenario execution** — the table below, top to bottom, each scenario driven for
   real and its evidence linked.
3. **Scale block last** — the 30M-class scenarios on a sized rig, because their numbers
   only mean something once the functional stories all pass.

## Seeded scenarios (v0 — owner extends)

S-FLEET-01 — A 2.3M-device config push finishes in five working days without melting anything
  modules:  campaign (R1 fields), jobs, console governor
  given:    2.3M enumerable targets; policy tps=800, dailyQuota=500000, window 10:00-21:00
            Europe/Istanbul, excludedDays=[Sat,Sun], endDate=+7d
  when:     operator creates the campaign, watches day 1, throttles to 300 mid-day when the
            downstream complains, restores to 800, lets it run
  then:     daily released never exceeds the quota; nothing releases outside the window or
            on the weekend; the governor shows the live numbers; day-5 completion; the verb
            log carries every throttle with its actor
  needs:    scale-rig (targets can be synthetic; timing compressed via clock control where honest)
  evidence: —
  notes:    the list-targeted variant (targets uploaded as a device list through Goldpath.Bulk,
            the app's selector reading the batch's staged rows) has its INTAKE leg measured:
            10M rows uploaded, validated and reported in 828 s on the agent's sandbox, peak
            managed heap 541 MiB (#300, packages/Goldpath.Bulk/ops/bulk-benchmarks.md). The
            campaign over that list is not run

S-FLEET-02 — 30M inventory: one campaign sustains the configured TPS for an hour, flat
  modules:  campaign, jobs, broker, store
  given:    30M synthetic targets; tps=2000; sized rig (the CI profile cannot host this)
  when:     release for 60 minutes under measurement
  then:     p95 release jitter within bound; store and broker headroom recorded; the numbers
            published as a bench report the way H6 benches are
  needs:    scale-rig — SPEC TO BE WRITTEN WITH THE OWNER before any claim
  evidence: —

S-FLEET-02a — One leader sustains 1000 TPS on a real broker (local profile of S-FLEET-02)
  modules:  campaign (pacer, item consumer, outcome sink), jobs, broker, store
  given:    Testcontainers RabbitMQ 4 + PostgreSQL 17; one campaign, tps=1000, the package
            defaults for tick, slice and pacer cron; 32 competing item consumers; a simulated
            target answering after 20 ms; enough synthetic targets to stay busy throughout
  when:     release for 15 minutes under measurement (a 2-minute smoke proves the bench only),
            then pause and let claimed work drain
  then:     at least 95% of one-second windows within ±5% of 1000; zero windows above 1000
            (the burst rule of #292); exactly-once: sink outcomes = durable terminal items = handler
            executions, nothing executed twice
  needs:    local (thresholds approved by the owner on #283, 2026-10-20)
  evidence: CampaignBrokerBenchTests via scripts/bench-campaign.sh broker; numbers in
            packages/Goldpath.Campaign/ops/campaign-benchmarks.md. RED on today's release
            path (2026-10-22 baseline): the rate thresholds fail, exactly-once holds

S-FLEET-03 — Two campaigns under one GlobalTps never jointly exceed it
  modules:  campaign R1.4
  given:    GlobalTps=1000; two campaigns tps=800 each
  when:     both run concurrently
  then:     no one-second window releases >1000 across the pair (this is R1's own DoD,
            promoted to a story)
  needs:    local
  evidence: tests/Goldpath.Campaign.Tests/BurstControlTests.cs —
            `ScenarioFleet03_TwoCampaignsAt800_UnderGlobalTps1000_NeverReleaseAbove1000InAnySecondOfTicks`
            runs the pacer at these numbers with an idle stretch first (both campaigns held at
            MaxInFlight 1 for 30 ticks of 50 ms while every bucket refills, then released hungry for
            40 ticks), counts each tick's releases exactly and holds every 20 consecutive ticks to
            1000: 1000 in the first tick after the relief, 50 a tick after it. Red before #292: one
            tick released 1000 and one second 1950. The same holds per bucket (campaign Tps, type
            MaxTps, GlobalTps) in `AfterAnIdleStretch_…`, and on a real broker and PostgreSQL in
            tests/Goldpath.IntegrationTests/CampaignR1Tests.cs (GlobalTps 20, two campaigns, every
            second of ticks of the run checked against 20 with no tolerance; replay with
            `dotnet test --filter FullyQualifiedName~CampaignR1Tests`). The broker test has not been
            seen red: its leadership slices start with empty buckets, so its runs held no idle
            stretch inside one slice (2026-10-29, three runs on the old code)

S-FLEET-04 — Pause stops every worker within three seconds
  modules:  campaign (R2.6 claim guard, pause/resume verbs, orphan sweep), broker, store
  given:    Testcontainers RabbitMQ 4 + PostgreSQL 17; two hosts on one item queue — a leader
            (pacer + consumers) and a worker (consumers only), four consumer slots each; a
            device answering after 600 ms; tps=200, so the queue is at least ten times deeper
            than the eight slots when the operator acts
  when:     the operator pauses through the admin verb; eight seconds later resumes
  then:     no item's ClaimedAt and no handler call later than the pause commit + 3 s; the
            calls in flight at the pause are no more than the slots; the backlog stays queued;
            after resume every device is called exactly once and the campaign completes
  needs:    local (the 3-second bar is a device-fleet adopter's acceptance number)
  evidence: tests/Goldpath.IntegrationTests/ScenarioFleet04Tests.cs — replay with
            `dotnet test --filter FullyQualifiedName~ScenarioFleet04` (2026-10-23, ~44 s green
            six runs out of six: no claim after the pause commit in any run, the last in-flight
            call returned within 423 ms, a 164-item backlog left untouched; numbers in
            packages/Goldpath.Campaign/ops/campaign-benchmarks.md. Red when the claim guard
            checks only the item's state: 64 claims after +3 s, the first at +3.4 s, the queue
            still draining at +7.8 s)

S-FIN-01 — A salary file with one bad row pays everyone except that row, after a second pair of eyes
  modules:  bulk, jobs, notification, console
  given:    a 10K-row payment file, row 4 121 refused by core banking at EXECUTION
            (validation-clean on purpose: an intake-invalid row BLOCKS the gate — that
            refusal is its own, already-proven story)
  when:     upload as payroll-clerk → validation report → payroll-SUPERVISOR approves →
            execute → the bad row's owner is notified (dedup-keyed per batch+row)
  then:     9 999 executed exactly once; row 4 121 in the repair queue with the refusal's
            own words; the approver identity stamped (DecidedBy) and the clerk durable in
            the jobs admin audit; the owner notified THROUGH the app's own
            failure→notification component (repair queue in, dedup-keyed request out —
            replaying it answers the same id, never a second mail), evidenced AND landed
            in a real inbox
  needs:    local (testcontainers: postgres + smtp4dev)
  evidence: tests/Goldpath.IntegrationTests/ScenarioFin01Tests.cs — replay with
            `dotnet test --filter FullyQualifiedName~ScenarioFin01` (2026-08-06, 1m21s
            green: 10 000 rows, chunk 500, exactly-once by sink count AND distinct row
            numbers; the console half of the story is the smoke's four-eyes journey,
            which drives the SAME contract this scenario drives at the API)

S-FIN-02 — A litigation hold survives an erasure request, and the chain still verifies
  modules:  archival
  given:    an archived instruction under legal hold
  when:     a KVKK erasure arrives for it → refused; hold lifted → erasure runs
  then:     the refusal is explicit while held; after erasure the document is gone, the
            chain verifies, and the erasure receipt names actor+time
  needs:    local
  evidence: tests/Goldpath.IntegrationTests/ScenarioFin02Tests.cs — replay with
            `dotnet test --filter FullyQualifiedName~ScenarioFin02` (2026-10-21, green in
            ~1 s after the container: erasure refused under hold with a reason naming it and
            the document untouched; the hold lifted by counsel, stamped on its own row;
            erasure redacts the classified field; admin verification returns no finding;
            the receipt names the requester and time. Red when the hold guard is removed)

S-OPS-01 — The 03:00 incident: stop the world, fix, resume, answer for it
  modules:  jobs, console
  given:    a fleet mid-run
  when:     pause-all → verify nothing fires → resume-all → morning-after audit read
  then:     durable cluster-wide stop (survives a host restart while paused); the audit
            answers "who stopped the night"
  needs:    local
  evidence: tests/Goldpath.IntegrationTests/ScenarioOps01Tests.cs — replay with
            `dotnet test --filter FullyQualifiedName~ScenarioOps01` (2026-10-21, 28 s green:
            a one-second cron fleet on real PostgreSQL; pause-all, six quiet seconds; the
            host stopped and a new one started on the same store, six more quiet seconds
            and the fleet status still paused; resume-all and runs land again; the admin
            audit holds pause-all by the night operator and resume-all by the morning one.
            Red when pause-all is replaced by an in-process standby)

S-OPS-02 — A mid-flight operator mistake is refused in the server's words, not a 500
  modules:  every admin surface
  given:    the live console
  when:     bad cron, unknown timezone, double-approve, erase-under-hold — each attempted
            from the SCREEN
  then:     every refusal arrives as the server's sentence in the UI; zero 500s
  needs:    local
  evidence: ui/console/e2e/refusals.spec.ts, run by `scripts/console-smoke.sh` (the
            `console-smoke` workflow and the nightly). Replay locally with
            `bash scripts/console-smoke.sh`. 2026-10-25, local, green: three tests, beside
            console.spec.ts and a11y.spec.ts (38 passed). A bad cron and an unknown timezone
            are refused inside the reschedule form, in the host's words. A second signature
            by the same approver answers `AlreadySigned`. An erase under a legal hold answers
            "lift the hold first". Each verb is asserted to answer 400, and any 5xx or
            unreadable request fails the run. Red proof: with the timezone refusal turned
            back into an unhandled `TimeZoneNotFoundException`, the spec fails on "reschedule
            answered nothing the browser could read (a 5xx without CORS headers)".

## Scale-rig note (for S-FLEET-02 class)

The CI reference profile proves mechanics, not telco scale. Before any 30M claim: the
owner and the agent write the rig spec TOGETHER (hardware, dataset shape, clock policy,
pass thresholds) into this file, and the run publishes its numbers like the H6 benches —
a claim without its rig spec does not get made.
