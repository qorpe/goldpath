# Module RFC: Goldpath.Campaign (L4 — paced, multi-day, policy-governed fan-out)

> Status: v1.0 ACCEPTED — D1–D8 approved by Ömer (2026-07-09; D3 carries the
> single-leader implementation note below). Trigger: a telco-scale device-management (MDM) case
> (30M+ devices, paced bulk operations, screen-trackable by construction). The six design
> constraints from that analysis (goldpath-jobs.md §12) are BINDING inputs, not suggestions.
> Module excellence bar applies: §6–§8 are load-bearing. Effort L (S1–S3) — the LAST rung
> of the execution ladder; everything below it is shipped and proven.

## 0. Position on the ladder — and what is genuinely new

L4 = L2's run model + a POLICY ENGINE + broker fan-out (L1 consumers) + a counter store:

| Ingredient | Status |
|---|---|
| Durable run/item truth, checkpoint/resume, repair queue, console verbs | **Goldpath.Jobs — shipped** |
| Work distribution to competing consumers, inbox dedup | **Goldpath.Messaging (MassTransit) — shipped** |
| Fast counters | **Goldpath.Caching (Redis) — shipped** |
| Per-target message discipline (if a campaign SENDS to humans) | **Goldpath.Notification channel seam — shipped** |
| **Pacing policy (windows / quotas / TPS), leadership, live throttle, outcome aggregation at scale** | **THIS module** |

The genuinely new core is small and sharply bounded: a LEADER that releases work to the
broker no faster than policy allows, and an OUTCOME pipeline that keeps 30M item states
truthful without 30M per-item writes.

## 1. Scope / Non-Goals

**Scope:** runtime-created campaign instances over code-registered campaign types,
streaming target enumeration with a mandatory ceiling, paced release to MassTransit
consumers (windows + daily quota + TPS + max-in-flight), consumer-side claim-before-
external-call, batched outcome aggregation, live policy verbs (pause/resume/abort/
throttle), progress/ETA that an operator can trust, admin API + ops pack.

**Non-goals (deferrals with triggers):**
- Kafka rider (trigger: an inventory whose scale RabbitMQ cannot serve — the MassTransit
  rider seam is the plan of record, constraint 5/6; v1 ships RabbitMQ).
- Cross-campaign global rate governance (per-PROVIDER ceilings shared by campaigns —
  trigger: the first tenant running concurrent campaigns against one SMS gateway).
  *Boundary note (added with R1, after a review asked):* this non-goal is about
  respecting a THIRD PARTY's ceiling — dividing one gateway's 1000 TPS fairly among
  campaigns, which needs per-provider accounting and a fairness policy. R1.4's
  `GlobalTps` is the other direction: protecting OUR OWN platform with one blunt total
  across everything the pacer releases. Same word, different owner of the limit; the
  non-goal's trigger stands untouched. *R2 (2026-10-07): the trigger fired — a
  device-fleet adopter running several campaigns against one target system — and R2.4's
  per-type `MaxTps` with R2.5's weighted fair share is the per-provider accounting this
  bullet asked for. Closed.*
- Audience/segmentation tooling (the app's selector answers "who"; marketing segmentation
  is a product, not a module).
- A/B testing, multi-step journeys (drip sequences) — campaign orchestration products
  exist; we ship the enterprise DELIVERY layer they all lack.
- Device-protocol specifics from the MDM doc (OMA-DM etc.) — the consumer's domain.

## 2. Seam Map (the six constraints, made concrete)

- **Type vs instance (D1):** a campaign TYPE is code — `AddCampaign<TTarget>("mdm-config-push")`
  binds a streaming target SELECTOR (keyset-paged enumeration out of the app's own data,
  with a MANDATORY `MaxTargets` ceiling) and the consumer-side handler contract. A
  campaign INSTANCE is a ROW created at runtime through the admin API: which type, the
  selector's parameters, the policy, the schedule window. Operators launch campaigns;
  developers ship campaign types through PRs.
- **Leadership without clustered ticks (D2, constraint 1):** the PACER is a LONG-LIVED
  Goldpath.Jobs run — the cron (~1 min) only ensures a pacer exists; once running, the leader
  loops IN-MEMORY (sub-second ticks cost nothing locally), releasing micro-batches to the
  broker while policy allows, extending its run heartbeat, checkpointing released-through
  watermarks per campaign. Death → the next fire takes over from the checkpoint (the
  bulk-adopt pattern, proven). No per-second Quartz cluster locks — constraint 1 honored
  by construction.
- **Counters are cache, tables are truth (D3, constraint 3):** released/sent/failed
  counts and TPS windows live in Redis (Goldpath.Caching) for O(1) pacing decisions; the
  durable campaign/item tables remain the truth; the leader RECONCILES counters from the
  tables on warmup/takeover. Redis loss slows one warmup, corrupts nothing.
- **Fan-out and claim (D4, constraints 2/5/6):** the pacer publishes item batches through
  MassTransit (ONE messaging stack); competing consumers CLAIM the item row (state-guarded
  update) BEFORE any external call — a rebalance mid-item repairs, never double-sends
  (the discipline shipped in bulk/notification, now at L4 scale). Inbox dedup guards
  redelivery.
- **Outcomes without per-item hammering (D5, constraint 4):** consumers report outcomes
  as events; a SINK consumer buffers and flushes BATCHED durable updates (single UPDATE …
  WHERE Id IN (…) per flush) and bumps the fast counters. Per-item terminal evidence
  survives (who/when/what per target); write amplification does not.
- **Policy engine (D6):** window (start/end + timezone; outside it the pacer sleeps),
  daily quota, TPS ceiling, max-in-flight; ALL adjustable live on a RUNNING campaign
  (throttle down a screaming gateway without aborting the night). Pause/resume/abort are
  audited verbs; abort drains claims gracefully.
- **Human sends compose Notification's seam:** an item handler that messages a person
  SHOULD go through `IGoldpathNotificationChannel`/`IGoldpathNotifier` — evidence discipline is
  already built; campaign does not reinvent it (guidance + sample, not enforcement).

## 3. Manifest Surface

```yaml
features:
  campaign: true      # REQUIRES a broker — the schema rule enforces it (fan-out is the point)
```

Schema key joins WITH S3, plus a cross-field rule: `campaign` without a broker is a
validation error (like outbox). Drift rows: the guard pair (`Goldpath.Campaign`/`AddGoldpathCampaign`
+ `Goldpath.Jobs`/`AddGoldpathJobs`) — and `Goldpath.Messaging` presence rides the broker rule.

## 4. API Surface

```csharp
builder.AddGoldpathCampaign<WebApplicationBuilder, OrdersDbContext>(campaign =>
{
    campaign.AddCampaign<DeviceTarget>("mdm-config-push", c => c
        .MaxTargets(50_000_000)                              // the ceiling is a decision (GP1701)
        .Targets((services, parameters) => services.GetRequiredService<OrdersDbContext>()
            .Set<Device>()                                   // streaming keyset enumeration
            .Where(d => d.FirmwareVersion < parameters["minVersion"])
            .OrderBy(d => d.Id)                              // STABLE order: takeover resumes by count
            .Select(d => new DeviceTarget(d.Id, d.PushToken))
            .AsAsyncEnumerable())
        .DefaultPolicy(p => { p.Tps = 200; p.DailyQuota = 2_000_000; p.Window("09:00", "21:00", "Europe/Istanbul"); }));
});
builder.Services.AddScoped<IGoldpathCampaignItemHandler<DeviceTarget>, ConfigPushHandler>();

// Jobs wiring:  jobs.AddGoldpathCampaignJobs<OrdersDbContext>();   // pacer (leader) + reconciler
// Messaging:    bus.AddGoldpathCampaignConsumers<OrdersDbContext>();
// DbContext:    modelBuilder.AddGoldpathCampaign();  modelBuilder.AddGoldpathJobs();
```

**Admin API** (`/goldpath/admin/campaign`, every verb audited): create/schedule an instance
from a registered type (parameters + policy), campaign list/detail (released/succeeded/
failed/remaining, current TPS vs ceiling, ETA under current policy, window state),
pause/resume/abort, LIVE throttle (policy patch on a running campaign), item drill-down
(failed items delegate replay to the jobs console — same repair discipline).

## 5. Analyzer Rules (GOLDPATH17xx — new block)

- **GP1701 (error):** a campaign type without `MaxTargets` — an unbounded enumeration
  at L4 scale is an outage, not a campaign.
- **GP1702 (warning):** an item handler calling `SaveChanges` directly — outcomes flow
  through the sink (constraint 4); per-item saves at 30M melt the database.
- **GP1703 (info):** a campaign type whose handler messages humans without the
  notification seam — evidence discipline exists; bypassing it should be visible.

## 6. Performance (measured, not promised)

Reference profile, `scripts/bench-campaign.sh` → `ops/campaign-benchmarks.md`:
- Enumeration + item materialization: 1M targets (rows/s, memory flat).
- Pacer precision: configured TPS 200 → measured release rate within ±5% over a minute;
  throttle to 50 mid-run takes effect within one leader tick.
- Outcome sink throughput: 100k outcomes batched-flushed (rows/s, flush latency).
- End-to-end 100k-item campaign on RabbitMQ: wall time, exactly-once-or-repair
  reconciliation (sink counts == durable counts == consumer executions).
- Counter warmup: reconcile 1M-item campaign state from tables (seconds).

## 7. Observability (shipped)

`Goldpath.Campaign` meter: released/succeeded/failed totals per campaign, CURRENT TPS vs
ceiling (the governor panel), in-flight gauge, remaining + ETA-at-current-policy, window
state, quota consumption, sink flush lag, counter-reconcile events. Grafana board: the
operator's single screen per campaign (progress, rate governor, failure rate, ETA) —
"screen-trackable by construction" is the case's own bar. Runs ride the jobs dashboard.

## 8. Operational (runbook or it didn't happen)

1. Campaign not progressing (window closed? paused? leader dead → takeover; broker
   backlog vs consumer health).
2. Gateway screaming / provider rate-limits (LIVE throttle verb — the whole point;
   confirm on the TPS panel before and after).
3. Failure rate climbing (pause, drill into failed items, fix the world, resume; item
   replay via the jobs console).
4. Leader takeover after a crash (checkpointed watermark + counter warmup reconcile —
   what the operator sees and how long warmup takes at 1M/30M).
5. Redis loss mid-campaign (pacing degrades to reconcile-on-warmup; truth intact —
   the drill that proves constraint 3).
6. Abort semantics (drain in-flight claims, terminal-state the remainder as Aborted —
   evidence of what was NOT sent is evidence too).
7. Acceptances piling up (R2.1: `AwaitingAck` growing, `goldpath_campaign_ack_timeouts_total`
   climbing) — the callback surface is unreachable or the target system stopped answering.
   Check the surface first; the sweep turns every missed deadline into a retryable failure,
   and the ladder exhausts into the repair queue, never into a silent success.
8. `goldpath_campaign_orphans_rereleased_total` climbing with no pause in sight (R2.6) —
   consumers are refusing claims or the broker is losing messages: a half-stopped host whose
   consumers hold prefetched messages, a purged queue. The sweep keeps the campaign moving;
   the number says a host needs looking at.

## 9. Test Plan

- **Unit:** policy math (window/timezone edges, quota rollover at midnight tenant-time,
  TPS token accounting), pacer batch-size decisions, type registration guards, EF model
  contract, entity defaults, golden plan/watermark payloads.
- **Mutation:** ≥ 70 break, the standard config.
- **Integration (pg + RabbitMQ, real containers):** a 10k-item campaign end to end —
  create via admin → pacer releases under TPS → consumers claim + execute → sink
  reconciles EXACTLY (no item lost/double-executed); kill the pacer mid-campaign →
  takeover resumes from the watermark with counter reconcile; LIVE throttle observably
  changes the release rate; pause/resume/abort; window boundary honored; a poisoned
  item repairs + replays.
- **Bench:** §6 numbers.

## 10. Slices & DoD

- **S1 — the engine:** model (campaign/item/watermark), type registration, streaming
  enumeration, LONG-LIVED leader pacer with policy + counters + reconcile, MassTransit
  fan-out + consumer claim + outcome sink, kill/takeover proof, bench baseline.
- **S2 — the ops surface:** admin API (create/pause/resume/abort/throttle, audited) +
  the operator panel + runbooks + GP1701-1703.
- **S3 — the manifest word:** `features.campaign` + the broker cross-field rule +
  template sample + CLI recipe (JobsOptionsLines seam, fourth rider) + GM (broker'lı
  shape) → module closes → **the execution ladder is COMPLETE**.
- DoD: the case's architecture answered line by line; excellence-bar artifacts present;
  ledger updated.

## 11. Decision Points (Ömer)

- **D1 — Types are code, instances are data:** developers ship campaign TYPES through
  PRs (selector + handler + ceiling + default policy); operators create/schedule
  INSTANCES at runtime through the audited admin API. Accept?
- **D2 — The pacer is a LONG-LIVED jobs run** (leader loops in-memory, no per-second
  cluster ticks — constraint 1; cron only guarantees existence; takeover from the
  checkpointed watermark). Accept?
- **D3 — counters are CACHE, tables are truth, warmup reconciles** (constraint 3).
  Implementation note (S1): pacing is SINGLE-LEADER, so the fastest correct cache tier
  is the leader's own memory (one writer, zero network); takeover reconciles from the
  tables exactly as the constraint demands. A shared-Redis implementation slots behind
  the same `IGoldpathCampaignCounters` seam when the cross-campaign/global-governance
  trigger fires — Goldpath.Caching's HybridCache is a get-or-create cache, not an atomic
  counter store, and misusing it would fake the constraint rather than honor it.
  ACCEPTED (with this note, 2026-07-09). S1 !52, S2 !53, S3 shipped 2026-07-10 — module COMPLETE; the execution ladder is closed.
- **D4 — MassTransit/RabbitMQ v1; Kafka rider deferred** with the written 30M-scale
  trigger (constraints 5/6). Accept?
- **D5 — Outcomes flow through a batching SINK consumer** (constraint 4): per-item
  terminal evidence kept, per-item write amplification not. Accept?
- **D6 — Policy = window(+timezone) / daily quota / TPS / max-in-flight, ALL live-
  adjustable on a running campaign;** pause/resume/abort audited. Accept?
- **D7 — GP1701-1703** (ceiling-less type error; SaveChanges-in-handler warning;
  human-send-without-notification-seam info). Accept?
- **D8 — `features.campaign` REQUIRES a broker** (schema cross-field rule, like outbox);
  the schema key lands WITH S3. Accept?

## Revision R1 — device-fleet parity parameters (ACCEPTED 2026-07-29; **IMPLEMENTED 2026-08-03** — model+pacer+sink+admin+console; proofs: 9 unit cases incl. the DST pair and the ladder, a real-broker integration for the GlobalTps joint ceiling and the live exclusion halt/resume, and two console smoke journeys. Ships to adopters with preview.6 — one `goldpath db add CampaignR1` migration: three campaign columns + one item column)

**Finding.** A device-management-class adopter (tens of millions of targets, multi-day
pushes) plans a campaign with SIX dials. The policy carries four of them — TPS,
max-in-flight, daily quota, window(+timezone) — and the console adjusts all four live.
The missing two, and two operational behaviors around them, are this revision. Every
addition is a POLICY field or a pacer rule: no new module, no new screen concept — the
governor panel simply grows the fields.

| # | Addition | The operator's sentence for it |
|---|---|---|
| R1.1 | `ExcludedDays` (set of `DayOfWeek`, evaluated in the policy's `TimeZoneId`) | "weekends are off-limits" — today's workaround is pausing Friday night and REMEMBERING Monday |
| R1.2 | `EndDate` (date, policy timezone; pacer stops releasing past it, campaign reports `ExpiredIncomplete` rather than silently running forever) | "this push has 7 days, then whatever is left is a report, not a background surprise" |
| R1.3 | Per-item auto-retry: `MaxAttempts` (default 1 = today's behavior) + fixed backoff ladder (30s → 2m); an item that exhausts attempts lands where failures already land — the repair queue, replayable | "transient device timeouts should not need a human before breakfast" — the repair queue stays the terminal truth, retry only precedes it |
| R1.4 | A GLOBAL release gate: `GoldpathCampaignOptions.GlobalTps` (per-process ceiling across ALL running campaigns, enforced at the pacer; null = off). PLATFORM protection, not provider fairness — the per-provider governance non-goal in §1 keeps its own trigger (see the boundary note there) | "five campaigns at once must not melt the platform" — per-campaign limits cannot see each other |

### Deliberately NOT in this revision

- **Free-form target queries from the console.** The filter (`make=X AND fw=Y`) stays an
  enumerator in CODE. A UI that turns operator text into a 30M-row query is the same
  constitution breach as a UI that creates jobs (ADR-0001), and it is one privilege
  escalation away from a full-table export.
- **A distributed global limiter.** `GlobalTps` is per-process (the pacer is already a
  cluster singleton per app, so per-process IS per-app). A cross-APP limiter is an
  infrastructure product; adopters who need one put it where the industry does — at the
  gateway.
- **Kafka rider.** Unchanged from D4: written 30M-scale trigger stands.

### Compatibility

All four are additive policy fields with defaults equal to today's behavior
(`ExcludedDays` empty, `EndDate` null, `MaxAttempts` 1, `GlobalTps` null). Existing
campaigns and the frozen admin contract's routes are untouched; `throttle` learns the new
fields the way it learned the old ones (omitted = keep). One migration: three nullable
columns + one small retry-bookkeeping column on items.

### Test plan (DoD when implemented)

1. Unit: an excluded day releases nothing and the day AFTER resumes without a human;
   quota-day and excluded-day both respect the policy timezone across a DST boundary;
   `EndDate` passing mid-flight flips to `ExpiredIncomplete` and the remaining count is
   the report; attempt 2 fires after the first rung, attempt 3 after the second, and
   exhaustion lands in the repair queue with the LAST error kept.
2. Integration (real broker + pg): two campaigns under one `GlobalTps` never jointly
   exceed it in any one-second window; the console's throttle changes `ExcludedDays`
   live and the pacer honors it on the next tick.
3. Console: the governor grows the fields; the smoke drives an excluded-day flip and an
   end-date expiry against a real campaign.

## Revision R2 — asynchronous targets, shared ceilings, keyset takeover (ACCEPTED 2026-10-07; **IMPLEMENTED 2026-10-07** — model+engine+pacer+sink+admin+callbacks+console; proofs: 30 unit cases (typed results, the ack sweep and the callback resolution, the configurable jittered ladder, the fair-share math, the type ceiling and priority on the pacer, the pause guard and orphaned releases, the keyset resume) and two real-broker integrations (acceptances settled over HTTP callbacks with two never-answered items walking the ladder; an ungraceful leader stop mid-enumeration resumed by key on a second host with nothing lost or doubled). Ships to adopters with preview.9 — one `goldpath db add CampaignR2` migration: two campaign columns, five item columns, one index replaced by three)

**Finding.** A design review for a device-management-class adopter (tens of millions of
targets, several target systems behind one platform, pushes that run for days) compared
the module with the adopter's own architecture line by line. In four places the two
disagreed and the design was right:

1. A handler's whole vocabulary was *return* or *throw*. A target system that ACCEPTS a
   request and answers minutes later had no honest state — the handler either claimed
   success before the answer or held a consumer slot for the wait — and a permanent
   refusal (an unsupported device, a closed account) walked the retry ladder like a timeout.
2. Several campaigns against ONE target system shared nothing but the platform total
   (`GlobalTps`), and under that total the campaign created first drank first.
3. A message already sitting in the queue when the operator pressed *pause* was still
   executed: pause latency was the queue depth, not one message.
4. A takeover re-read every materialized row of the selector to skip it — seconds at a
   hundred thousand targets, minutes at thirty million.

Every addition is a handler contract, a policy field or a pacer rule; no new module, no
new screen concept.

| # | Addition | The sentence for it |
|---|---|---|
| R2.1 | Typed results: `IGoldpathCampaignActionHandler<T>` answers `Succeeded()`, `Failed(code, message, retryable)` or `Accepted(correlationId, ackTimeout)`. An acceptance parks the item in `AwaitingAck` (still in flight); the target system's answer arrives on `MapGoldpathCampaignCallbacks` (`POST {prefix}/{type}/{correlationId}`, idempotent, a machine surface with its own policy parameter); an acceptance nobody answered becomes a RETRYABLE failure (`ACK_TIMEOUT`) at its deadline, never a success. A non-retryable failure is terminal at once with its code kept for the report. The R1 handler keeps its contract untouched. | "the device answers when it wakes up — wait for it, but not forever" / "this one is permanently unsupported, do not retry it" |
| R2.2 | An idempotency key per attempt on the item context (`{campaign}#{seq}#{attempt}`) | "the gateway dedupes on a key — give me one that changes only when the retry is deliberate" |
| R2.3 | The ladder is configurable and jittered: `RetryBackoff` (default 30s, 2m, 10m), `RetryJitter` (default ±20%); the ripe instant is persisted per item (`NextAttemptAt`) and released off one indexed range | "a thousand items that failed in the same second must not come back in the same second" |
| R2.4 | A per-TYPE ceiling: `.MaxTps(n)` on the type, shared by every campaign of that type — the target system's protection, as `GlobalTps` is the platform's | "the gateway takes two hundred a second, whatever we run against it" |
| R2.5 | `Priority` (High 3 · Normal 2 · Low 1) on the policy, live through the throttle verb and the console; a contended shared ceiling splits by weighted max-min fair share — a claimant never gets more than it asked for, what it leaves is shared by weight, nobody starves, and a one-unit tick rotates among equals | "the security patch goes ahead of the marketing push, but the push still moves" |
| R2.6 | The claim guard reads the CAMPAIGN too: a message reaching a consumer after a pause or abort claims nothing. Released rows carry `ReleasedAt`; a row unclaimed past `OrphanReleaseAfter` (default 5m) is published again under the same allowance, and *resume* marks every Released row due at once | "pause means now" / "a message the broker lost is not an item lost" |
| R2.7 | A keyset selector: `.TargetsAfter((services, parameters, afterKey) => …, keyOf)`; the campaign row carries `EnumeratedKey` in the same write as the items it covers; a takeover reopens the selector after the key | "a new leader must not re-read thirty million rows to find its place" |

### Deliberately NOT in this revision

- **Target sources as data** (a filter, a group, an uploaded list picked from the
  console). Unchanged from R1: a selector is code, shipped through a PR; a console that
  turns operator text into a thirty-million-row query is ADR-0001's breach wearing a
  different hat. An adopter's catalogue of sources is one `AddCampaign` per source kind,
  each with its own ceiling.
- **A campaign whose items are themselves bulk batches** (a Bulk bridge). The two ladders
  meet at the handler: an item handler that enqueues a bulk batch is three lines of app
  code; a module seam would freeze a shape no second adopter has asked for.
- **Four-eyes on *create*** (an Approvals binding). Trigger: the first adopter whose policy
  requires a second person on a campaign start. The verb is already audited and the
  Approvals module already exists, so the binding is a day's work when the trigger fires.
- **Finer roles than the ops floor; exports of item outcomes.** The admin surface stays one
  policy; exports ride the failed-items drill-down and the app's own reporting.
- **A distributed limiter, the Kafka rider, Oracle:** unchanged (R1, D4, T16).

### Compatibility

Additive for adopters on the R1 handler: `IGoldpathCampaignItemHandler<T>` keeps
return-is-success / throw-is-retryable; the outcome message keeps its positional shape
(the new init-only members default to R1 behaviour); `RetryBackoff` defaults reproduce the
R1 pair plus one rung; `Priority` defaults to Normal; `OrphanReleaseAfter` defaults to five
minutes; no route of the frozen admin contract moves. Two source-level changes for code
that calls the engine or constructs the admin records directly:
`GoldpathCampaignEngine<TContext>.ExecuteItemAsync` gains an `attempt` parameter and returns
the typed result; `GoldpathCampaignInfo` gains `Priority` (positional) and
`GoldpathCampaignThrottle` gains an optional `Priority`. One migration:
`goldpath db add CampaignR2`.

### Test plan (DoD) — met

1. Unit: the three result shapes land in three item states and a non-retryable failure is
   terminal at once; the idempotency key names the attempt; a callback settles an accepted
   item and a repeated callback publishes nothing; a failed callback walks the ladder with
   the provider's code; a passed ack deadline is a retryable failure and then exhausts; the
   ladder reads its rungs from options and jitters within the bound; a ripe retry claims
   while an unripe one does not; the fair-share math (small demands first, weights, no
   starvation, rotation among equals); two campaigns of one type share its ceiling evenly
   when equal and four-to-one under priority; priority alone never throttles; priority is
   live through the throttle verb; a message arriving after the pause claims nothing and
   resume brings it back; a fresh release is not an orphan until its age passes; the pacer
   publishes orphans again under the same allowance; an aborted campaign refuses the claim;
   a takeover reopens a keyset selector after the last key with no skip read.
2. Integration (real broker + pg): forty customers, odd ones accepted and settled over the
   HTTP callback surface (202 on a pending acceptance, 200 on a repeat, 404 on an unknown
   id), two never answered — ack timeout, rung one, second attempt succeeded, every
   customer exactly once; a keyset campaign whose first host is stopped ungracefully
   mid-enumeration and taken over by a second host — the selector reopened after a key at
   or past the stop, the sequence dense, every customer exactly once.
3. Console: the governor shows priority and the throttle patches it.

### What the real broker taught (kept as evidence)

- **Publish-before-mark and the retry.** On RabbitMQ the retry's message reached a consumer
  before the AwaitingRetry→Released mark landed, and the claim guard — which had only ever
  seen Pending and Released — dropped every retry as a duplicate. R1's ladder had unit proof
  only, so the race had never been seen. A ripe `AwaitingRetry` row now claims; an unripe one
  still does not. This is why the DoD names a real broker.
- **A half-stopped host is a zombie.** Its consumers hold prefetched messages and its outcome
  sink swallows the live host's outcomes. The sweeps are the answer on their own clocks
  (`StaleClaimAfter`, `OrphanReleaseAfter`, runbook items 7–8); the takeover test stops the
  dead host's bus the way a killed process drops its connection.
