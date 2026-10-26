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
// Messaging:    messaging.AddGoldpathCampaign<OrdersDbContext>();   // or bus.AddGoldpathCampaignConsumers<…>() until G1 Part 4/4 (#328)
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
   what the operator sees and how long warmup takes at 1M/30M). **Hand-over between slices
   (#284):** leadership is continuous by default. `LeadershipSlice` (60 s) is at least the
   period of the default pacer cron (once a minute), and the jobs runner never runs the pacer
   twice at once, so the next fire is already due when a slice returns and starts as it
   returns; it does not wait for the next cron tick. The measured hand-over is about 0.6 s (the longest gap between two releases at 50 TPS with a 250 ms tick, on the run that proved it). A
   slice shorter than an adopter's cron period gives that remainder of every period no leader.
   After a crash the gap is the cluster's recovery instead: the dead node's run re-fires
   elsewhere once its check-in is missed, `CheckinInterval` (5 s) + `CheckinMisfireThreshold`
   (15 s) at the Jobs defaults. That bound is read from configuration, not measured here.
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

## Revision R2 — asynchronous targets, shared ceilings, keyset takeover (ACCEPTED 2026-10-07; **IMPLEMENTED 2026-10-07** — model+engine+pacer+sink+admin+callbacks+console; proofs: 30 unit cases (typed results, the ack sweep and the callback resolution, the configurable jittered ladder, the fair-share math, the type ceiling and priority on the pacer, the pause guard and orphaned releases, the keyset resume) and two real-broker integrations (acceptances settled over HTTP callbacks with two never-answered items walking the ladder; an ungraceful leader stop mid-enumeration resumed by key on a second host with nothing lost or doubled). Ships to adopters with preview.10 — one `goldpath db add CampaignR2` migration plus its one-line data step (`GoldpathCampaignR2Backfill`): two campaign columns, five item columns, one table (parked callbacks), one index replaced by three. **Amended 2026-10-20** after a review of the implementation — see *Amendments* at the end of this revision, R2.8 among them)

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
| R2.1 | Typed results: `IGoldpathCampaignActionHandler<T>` answers `Succeeded()`, `Failed(code, message, retryable)` or `Accepted(correlationId, ackTimeout)`. An acceptance parks the item in `AwaitingAck` (still in flight); the target system's answer arrives on `MapGoldpathCampaignCallbacks` (`POST {prefix}/{type}/{campaignId}/{correlationId}` — amended A1 — idempotent, a machine surface authenticated by its signature, R2.8); an acceptance nobody answered becomes a RETRYABLE failure (`ACK_TIMEOUT`) at its deadline, never a success. A non-retryable failure is terminal at once with its code kept for the report. The R1 handler keeps its contract untouched. | "the device answers when it wakes up — wait for it, but not forever" / "this one is permanently unsupported, do not retry it" |
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
  Approvals module already exists, so the binding is a day's work when the trigger fires
  (open-threads T37).
- **Finer roles than the ops floor; exports of item outcomes.** The admin surface stays one
  policy; exports ride the failed-items drill-down and the app's own reporting.
  *Reversed 2026-10-20 for roles (owner decision on #297).* Admin-contract
  [revision R4.1](goldpath-admin-contract.md) (PROPOSED 2026-10-25,
  [#304](https://github.com/qorpe/goldpath/issues/304)) adds a read role, `goldpath-ops-read`,
  which `goldpath-ops` also satisfies. It reads every admin surface, and the verbs stay
  `goldpath-ops`'s.
  - The non-goal never had an open-threads row. What remains of it does now (T50): an
    admin-only tier, and an approver role outside Approvals.
  - Exports stay a non-goal.
- **A distributed limiter, the Kafka rider, Oracle:** unchanged (R1, D4, T16).

### Compatibility

*Rewritten 2026-10-20 (amendment A9): the first text said "two source-level changes" and
"every default reproduces R1"; neither was true.*

Additive for adopters on the R1 handler: `IGoldpathCampaignItemHandler<T>` keeps
return-is-success / throw-is-retryable; the outcome message keeps its positional shape (the
new init-only members default to R1 behaviour); `Priority` defaults to Normal; no type has a
ceiling; no route of the frozen admin contract moves. Three things are not additive, and the
preview.10 upgrade guide carries each with its step:

- **Three removed public members**, all shipped in preview.8:
  `GoldpathCampaignEngine<TContext>.ExecuteItemAsync` gains `attempt` and returns the typed
  result (source- and binary-breaking); the positional `GoldpathCampaignInfo` constructor
  gains `Priority` before `WindowOpenNow` (source- and binary-breaking);
  `GoldpathCampaignThrottle` gains an optional trailing `Priority` (source-compatible,
  binary-breaking — recompile). Justified under the versioning RFC's D2 (0.x may break, never
  silently).
- **A default changed**: the ladder is 30s, 2m, **10m** with **±20% jitter**, where R1 waited
  2m before every attempt after the second, exactly. `RetryBackoff = [30s, 2m]`,
  `RetryJitter = 0` pins R1.
- **One migration with a data step**: `goldpath db add CampaignR2`, then
  `migrationBuilder.GoldpathCampaignR2Backfill()` at the end of `Up` (A4). Its three
  `CREATE INDEX` statements block writes on a large items table — apply between campaigns, or
  build them concurrently (the guide gives both providers' statements).

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
   SIGNED HTTP callback surface — half of them answering from inside the handler, before the
   acceptance exists (202 parked), half after it lands (202 published); 200 on a repeat, 404
   on an unknown id, 401 unsigned, forged or stale — two never answered — ack timeout, rung
   one, second attempt succeeded, every customer exactly once; the R2 backfill on real
   PostgreSQL; a keyset campaign whose first host is stopped ungracefully
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

### Amendments — the review of the implementation (ACCEPTED 2026-10-20 by the owner)

A review of the R2 implementation (2026-10-20) found nine places where the code or its
documents did not do what this revision says; six were proven by tests that asserted the
defect and passed. The owner decided all nine on 2026-10-20. R2 itself stays ACCEPTED as
written on 2026-10-07; these amend it.

| # | Finding | The amendment |
|---|---|---|
| A1 | A callback could find its item only after the sink wrote the correlation id, and the lookup spanned every campaign of the type: a prompt answer got 404 and the ladder executed the action again under a new idempotency key; a shared id could settle the wrong campaign | The route names the campaign (`{type}/{campaignId}/{correlationId}`) and the lookup stays inside it. A callback that outruns its acceptance — before the sink flushes, or before the handler has even returned — is **parked** (202) and the sink settles it in the flush that writes the acceptance; park-then-look-again closes the interleaving. Writing the acceptance synchronously was rejected: it closes only the first window, not the device that answers while our call to the target system is still returning. A spent acceptance keeps its id, so a repeat of a failure webhook is the idempotent 200 this revision promised |
| A2 | Outcomes were not bound to their attempt: a redelivered failure of attempt N moved attempt N+1 back onto the ladder and its success was lost | The outcome carries `Attempt`; every item update requires the row to still be on it (`Attempts == Attempt - 1`) |
| A3 | A keyset type resuming a row with no key (enumerated before R2, or while count-based) reopened from the start and sent every materialized target twice | The takeover derives the key from the target materialized last; a row that cannot name its key refuses with teaching text |
| A4 | `AwaitingRetry` rows written by R1 have no ripe instant, so they were never retried and their campaigns never completed; abort did not stamp them | `GoldpathCampaignR2Backfill()` — the migration's data step — stamps the R1 rung; the engine reads a missing instant as ripe (the net, not the plan); abort stamps waiting retries `Aborted` |
| A5 | A replay decremented the failed count before its handler ran; a throwing replay drifted the ledger below zero | The replay claims only a Failed row, and a throw puts it back, counted |
| A6 | The rounding unit always went to the heaviest claimant and nothing carried between ticks: on a small budget the lighter campaign got zero forever, contradicting R2.5's "nobody starves" | The exact fractional share is carried tick to tick as a credit; over any run every hungry claimant is within one unit of its weighted share (proven over every priority mix × MaxTps 1–25 × three ticks) |
| A7 | The callback surface defaulted to the ops policy (a target system needed an ops credential) and `exposeUnsecured` left forged successes one guessable id away | **R2.8** below |
| A8 | The ack and stale-claim sweeps ran only for campaigns releasing that tick: after an abort, during a closed window, on a paused or finished campaign, work in flight never drained | A drain sweep visits every campaign with work in flight; on a campaign the pacer no longer releases a retryable failure is terminal at once and filed for repair |
| A9 | The upgrade note and the CHANGELOG said two source-level removals and "every default reproduces R1" | Three removals, one binary-only; the ladder default change named with its pin; the `[schema]` marker; the index-build warning — *Compatibility* above, rewritten; R2 ships with preview.10 |

#### R2.8 — signed callbacks (ACCEPTED 2026-10-20 by the owner)

A target system's callback settles an item as succeeded or failed. Unauthenticated, anyone
who learns a correlation id records a send that never happened — the silent success R2.1
forbids. Behind the ops policy, the machine that posts device results could also abort,
throttle and replay every campaign. The callback is authenticated by what the target system
already has to hold anyway — a secret — and nothing more:

- **The secret is per campaign type**: `.SignedCallbacks(secret, previousSecret)` on the
  type, at least 32 bytes, read from configuration. A type without one has no callback
  surface.
- **The signature**: HMAC-SHA256 with that secret over `{unix seconds}\n{METHOD}\n{path}\n`
  followed by the raw body; `X-Goldpath-Timestamp` carries the seconds, `X-Goldpath-Signature`
  carries `v1=<lowercase hex>`. `path` is the request path exactly as sent, without the query.
  Compared in constant time against every active secret.
- **Replay is bounded twice**: a timestamp outside `CallbackTimestampTolerance` (5 minutes,
  either way) is refused, and inside the window a repeat is idempotent — the attempt-bound
  settle (A2) applies it at most once.
- **Rotation**: during a rotation two secrets are active (`previousSecret`); the runbook
  carries the order (deploy both, move the sender, drop the old one).
- **Unsigned is impossible, not discouraged**: `exposeUnsecured` is gone; mapping the surface
  with no signing type refuses to start; **GP1704** (Error, compilation-end) says so at build
  time — the standard ships with its verifier (ADR-0005). An authorization policy on top of
  the signature stays available, only when the adopter passes one explicitly; by default the
  signature alone authenticates and the app's fallback policy does not apply.
- **Not in this amendment**: mTLS (a deployment boundary, not a module feature — composes on
  top), per-campaign secrets (a type is the target system; one secret per sender), and
  asymmetric signatures (the trigger is the first target system that can only sign with a
  key pair).

Proofs: the signed path settles and a signed repeat is 200; seven unauthentic variants
(unsigned, another secret, body or path changed after signing, stale, future, garbage) are
401 and reach nothing; both secrets verify during a rotation; the refusal to start and GP1704
each have a red-then-green test; the recipe is pinned by a vector; the integration suite
signs every callback on real infrastructure and refuses unsigned, forged and stale ones.

## Revision R3 — scheduled start, ceilings as data, an acknowledgement ceiling, a policy version, auto-suspend (ACCEPTED 2026-10-21 — owner decision in chat, the seven questions Q1–Q7 answered as recommended; the merge of PR #313 records it. Implementation: the eleven slices below; R3-S1, the schema, landed 2026-10-23 (#314), the other ten not yet)

**Finding.** A coverage analysis on 2026-10-20 compared R2 with a device-fleet adopter's
bulk-operations design (controlled actions against tens of millions of devices, several target
systems behind one platform, acknowledgements that arrive hours later). The engine's shape held.
Five things the design needs are not in the module:

1. A campaign cannot be scheduled. §4 promises "create/schedule an instance", but a `Created`
   campaign is adopted on the leader's next tick, and nothing sits between "snapshot done" and
   "releasing" ([#289](https://github.com/qorpe/goldpath/issues/289)).
2. R2.4's ceiling is keyed by campaign TYPE and set in code. Two types that call one target
   system each get their own bucket, so the target system sees their sum. Neither that ceiling
   nor `GlobalTps` can change without a restart, and neither can the four module options an
   operator tunes during an incident ([#290](https://github.com/qorpe/goldpath/issues/290)).
3. Since R2.1, `MaxInFlight` counts acknowledgements still outstanding. With deadlines measured
   in hours an operator must size it near `Tps × ackTimeout` to keep releasing, and at that size
   it no longer protects the adapter from live calls
   ([#293](https://github.com/qorpe/goldpath/issues/293)).
4. Two operators throttling one running campaign do not change its `State`, the only
   concurrency token, so the last writer silently wins
   ([#298](https://github.com/qorpe/goldpath/issues/298)).
5. No campaign stops itself when its target system starts failing. Runbook item 3 is a human
   step, and a client-side circuit breaker cannot stop the pacer from releasing thousands more
   items into the failure ([#299](https://github.com/qorpe/goldpath/issues/299)).

**Owner decision (2026-10-20, recorded on #289, #290, #293, #298 and #299):** Campaign R3 is
approved as ONE revision covering exactly these five — StartAt + a READY state, target-system
ceilings as data + `IOptionsMonitor`, `MaxAwaitingAck` with null = today's behaviour, a throttle
version (optimistic concurrency), and a `Suspended` state for auto-suspend. One migration, one
train. This text came first (docs-only, [#303](https://github.com/qorpe/goldpath/issues/303)).
**Owner decision (2026-10-21, in chat):** R3 is ACCEPTED as written here, with Q1–Q7 answered
as recommended (*Owner decisions* at the end of this revision); the text below already reads
them as decided. Each code slice below is its own issue.

| # | Addition | The sentence for it |
|---|---|---|
| R3.1 | `StartAt` on the policy (an instant, shown in the policy's time zone; null = today). The snapshot still starts when the leader adopts the campaign; nothing releases before `StartAt`. A campaign whose snapshot is complete and whose start is still ahead is **`Ready`** (new state). `throttle` moves the start until the campaign has started, audited old → new | "the push starts Monday 10:00; the list is ready on Friday so we can look at it" |
| R3.2 | Target systems as data: `.TargetSystem(name)` on a type, `AddTargetSystem(name, maxTps)` on the options. Types that name one system share one bucket and R2.5's weighted fair share. The system's ceiling and `GlobalTps` live in rows that code only seeds, and an audited admin verb changes them live; the change bites within one leader tick | "the device core takes two hundred a second, whichever campaigns are running, and tonight it takes a hundred" |
| R3.3 | `RetryBackoff`, `RetryJitter`, `OrphanReleaseAfter` and `StaleClaimAfter` reload from configuration (`Goldpath:Campaign`) through `IOptionsMonitor`, without a restart | "stretch the ladder while the gateway is in maintenance; do not restart the leader to do it" |
| R3.4 | `MaxAwaitingAck` on the policy. Null keeps today's rule exactly (an acknowledgement counts against `MaxInFlight`). Set, `MaxInFlight` counts live work only and acknowledgements have their own ceiling | "ten calls at a time into the adapter, and up to fifty thousand devices allowed to answer later" |
| R3.5 | `PolicyVersion` on the campaign: every applied throttle increments it, the detail returns it, and a throttle that names the version it read is refused (and audited) when the policy moved since. A throttle that names none keeps last-writer-wins | "my TPS cut must not be undone by a quota change made from a stale screen" |
| R3.6 | Auto-suspend: `SuspendOnFailureRatio` (null = off) with `SuspendWindow` and `SuspendMinSample` on the policy. The pacer moves a campaign whose failure ratio crosses the threshold to **`Suspended`** (new state, distinct from `Paused`), stores the reason, audits it with the actor `system`, and *resume* is the operator's verb | "a failing target system stops the campaign before the operator does, and the screen says why" |

### R3.1 — scheduled start and the `Ready` state ([#289](https://github.com/qorpe/goldpath/issues/289), S-FLEET-01)

**State machine.** Two states join `GoldpathCampaignState`, appended so no stored value moves:
`Ready = 8` (R3.1) and `Suspended = 9` (R3.6). States are stored as integers, so neither needs
a schema change.

| From | Event | To | Who |
|---|---|---|---|
| `Created` | the leader adopts it | `Enumerating` | pacer (unchanged) |
| `Enumerating` | snapshot complete, `StartAt` null or passed | `Running` | pacer (R2 behaviour, unchanged) |
| `Enumerating` | snapshot complete, `StartAt` still ahead | **`Ready`** | pacer |
| **`Ready`** | `StartAt` passed (and, once [#291](https://github.com/qorpe/goldpath/issues/291) lands, every start gate open) | `Running` | pacer, audited `start` with actor `system` |
| **`Ready`** | pause · abort · end date passed | `Paused` · `Aborted` · `ExpiredIncomplete` | operator · operator · pacer |
| `Running` | failure ratio at or above the threshold over at least `SuspendMinSample` outcomes | **`Suspended`** | pacer, audited `suspend` with actor `system` (R3.6) |
| **`Suspended`** | resume · abort | `Running` · `Aborted` | operator |
| `Paused` | resume | `Enumerating` (snapshot unfinished) · **`Ready`** (start still ahead) · `Running` | operator |

```
Created ─► Enumerating ─┬─ start ahead ──► Ready ── StartAt passed ──┐
                        └─ no start, or start passed ────────────────┴─► Running ─ ratio ≥ threshold ─► Suspended
                                                                           ▲                                │
                                                                           └────────── resume (operator) ───┘
```

`Suspended`, like `Paused`, is not loaded by `LoadWorkableAsync`, so it neither releases nor
expires while stopped; the drain sweep (R2 review #8) still visits it because it holds work in
flight. `Ready` IS loaded, so the pacer can start it and expire it. The claim guard is unchanged
(`c.State == Running`): a `Ready` campaign has nothing released, and a `Suspended` one claims
nothing, which is R2.6's "pause means now" applied to the system's own stop. In
`ApplyOutcomesAsync` the ladder keeps running on a `Suspended` campaign exactly as on a `Paused`
one, so a resume finds its retries waiting.

**Design.** `GoldpathCampaign.StartAt` (`DateTimeOffset?`) is stored in UTC: Npgsql refuses a
non-zero offset on `timestamptz`, so the engine normalises it before writing, and the admin
detail returns UTC for the console to show in the policy's time zone. An instant has no DST
ambiguity; converting an operator's wall-clock time into it is the console's job, and the
console refuses a local time that does not exist (the spring-forward gap) and asks which one
is meant in the repeated hour. `EnumerateStepAsync`, which flips `Enumerating → Running` today
when the snapshot completes, flips to `Ready` instead while `policy.IsBeforeStart(now)`.
`PrepareTickAsync` starts a `Ready` campaign whose start has passed (state-token guarded, like
`TryExpireAsync`) and runs R1.2's expiry check on `Ready` too. The transition goes through one
internal start-gate check that ships with no gates; it is where #291's approval plugs in, as
amendment R3.7 (owner decision Q6).

`StartAt` is the earliest start, not a promise: a campaign whose snapshot is still running at
`StartAt` starts when the snapshot completes, and the console shows both facts.
[#288](https://github.com/qorpe/goldpath/issues/288) makes that window shorter; it does not
change the rule (releasing during enumeration stays out of scope, as #288 says).

**Admin and console.** `throttle` gains `StartAt` and `ClearStartAt` (clear = start as soon as
the snapshot is ready). Moving the start is refused with teaching text once the campaign has
started (`State == Running`, or `ReleasedThrough > 0` for a paused or suspended campaign) and
when the new start falls after `EndDate`'s local day. A start in the past means "now". The
change lands in `LastVerb` and the audit row old → new, like every other policy field.
`create` accepts `StartAt` in its policy. `pause` accepts `Ready`; `resume` returns a paused
campaign to `Ready` while its start is still ahead. The governor shows the start in the policy
time zone and moves it.

**Public API (additions only).**

```
Goldpath.GoldpathCampaignState.Ready = 8 -> Goldpath.GoldpathCampaignState
Goldpath.GoldpathCampaign.StartAt.get -> System.DateTimeOffset?
Goldpath.GoldpathCampaign.StartAt.set -> void
Goldpath.GoldpathCampaignPolicy.StartAt.get -> System.DateTimeOffset?
Goldpath.GoldpathCampaignPolicy.StartAt.init -> void
Goldpath.GoldpathCampaignPolicy.IsBeforeStart(System.DateTimeOffset utcNow) -> bool
Goldpath.GoldpathCampaignThrottle.StartAt.get -> System.DateTimeOffset?
Goldpath.GoldpathCampaignThrottle.StartAt.init -> void
Goldpath.GoldpathCampaignThrottle.ClearStartAt.get -> bool
Goldpath.GoldpathCampaignThrottle.ClearStartAt.init -> void
Goldpath.GoldpathCampaignInfo.StartAt.get -> System.DateTimeOffset?
Goldpath.GoldpathCampaignInfo.StartAt.init -> void
```

Every R3 field on `GoldpathCampaignThrottle` and `GoldpathCampaignInfo` is an init-only member
outside the positional parameter list, so no constructor changes; this repeats none of R2's
binary breaks (R2 *Compatibility*).

**Proof.** Unit (`R3StartTests`, red today because the field does not exist and the snapshot
flips straight to `Running`): a campaign with a future start completes its snapshot, reports
`Ready` and releases nothing until the fake clock passes `StartAt`; the R1 DST pair is the
pattern — a start at 03:30 local on Berlin's spring-forward Sunday (01:30Z) releases nothing at
01:29:59Z and releases at 01:30Z, and the same on the fall-back Sunday's repeated hour; moving
the start of a running campaign is refused in the server's words, and moving it while `Ready` is
audited old → new; a start after the end date is refused. Integration
(`CampaignR3Tests`, RabbitMQ + PostgreSQL in containers): create with a start 20 seconds out →
`Ready` → `Running`, and no item's `ClaimedAt` is earlier than `StartAt`. Console smoke: the
governor shows the start and moves it. Scenario: S-FLEET-01's "operator creates the campaign"
step gains a scheduled start.

### R3.2 — target-system ceilings as data ([#290](https://github.com/qorpe/goldpath/issues/290) part a + b, S-FLEET-03 extended)

**Declaration stays code; the number becomes policy.** D1 draws the line between types (code,
reviewed) and instances (data, live). R3 draws the same line one level up: WHICH target systems
exist and which types call them is code, shipped through a pull request; how fast a target
system may be called is policy, a row an operator changes live, with audit.

```csharp
builder.AddGoldpathCampaign<WebApplicationBuilder, AppDb>(campaign =>
{
    campaign.AddTargetSystem("device-core", maxTps: 200);            // the seed, not the live value
    campaign.GlobalTps = 1_000;                                       // R1.4 — now also a seed
    campaign.AddCampaign<DeviceTarget>("config-push", c => c.TargetSystem("device-core") /* … */);
    campaign.AddCampaign<DeviceTarget>("firmware-push", c => c.TargetSystem("device-core") /* … */);
});
```

A type that names a system nobody declared refuses at startup with teaching text, after the
whole `configure` delegate has run (the declaration may follow the type). A type may carry both
`.TargetSystem(...)` and R2.4's `.MaxTps(...)`: the buckets nest. A type that names no system
keeps R2.4's per-type `MaxTps` from code, exactly as R2 adopters have it today; to make a type's
rate live, name a target system.

**Persistence.** One small table, `GoldpathCampaignCeilings`: `Kind` (`Global = 0`,
`TargetSystem = 1`), `Name` (128; empty for the global row), `MaxTps` (`int?`, the live value;
null on the global row = off), `SeedMaxTps` (`int?`, the code value last seeded), `UpdatedAt`,
`UpdatedBy`; primary key `(Kind, Name)`. The leader reconciles the seed rows at the start of
every slice: it inserts a missing row from code and, by owner decision Q4,
follows a changed code value only while the operator has never moved the row (`MaxTps ==
SeedMaxTps`), otherwise keeps the operator's value and logs the disagreement. A concurrent
insert from the admin service is a primary-key conflict that both sides resolve by re-reading.
`GoldpathCampaignOptions.GlobalTps` keeps its name and meaning as the seed; code that reads it
reads the code value, and the pacer reads the row.

**Pacer.** One query per tick reads the ceiling rows (a handful), so a live change bites on the
next tick. `Allocate` gains one step between R2.4's type split and R1.4's global split: the
campaigns whose types name one system share its bucket under R2.5's weighted max-min fair share,
with its own credit ledger (`"target:" + name`) beside `"type:"` and `"global"`. Every bucket
banks at most one tick's worth of tokens: the burst rule
[#292](https://github.com/qorpe/goldpath/issues/292) sets for R1/R2's buckets (owner decision:
fixed, no knob) applies to the new one from its first line.

**Admin and console.** The ceilings need routes the frozen contract does not have, so they ride
admin-contract revision R4 ([#304](https://github.com/qorpe/goldpath/issues/304); owner decision Q5): `GET /ceilings` (both kinds, with the types
and running campaigns each one binds), `POST /ceilings/global/throttle`,
`POST /ceilings/target-systems/{name}/throttle`, and `GET /ceilings/audit`. A change is audited in
`GoldpathCampaignAudit` with `CampaignId = Guid.Empty` and action `ceiling`, detail old → new.
Using the existing table avoids changing the shipped, non-nullable `CampaignId`. Clearing is
allowed on the global row only; a target system without a ceiling is a typo, and the refusal
says to set a high number instead. The campaign detail names the target system that binds it,
and the governor shows that ceiling and lets an operator edit it.

**Why not the BCL's rate limiters (ADR-0003).** `System.Threading.RateLimiting` gates requests
one process makes. The pacer's buckets divide one release budget among competing campaigns by
weight, which no BCL limiter does. R2.4/R2.5 made this choice; R3 only moves the number from
code to a row.

**Public API (additions only).**

```
Goldpath.GoldpathCampaignCeiling
Goldpath.GoldpathCampaignCeiling.GoldpathCampaignCeiling() -> void
Goldpath.GoldpathCampaignCeiling.Kind.get -> Goldpath.GoldpathCampaignCeilingKind
Goldpath.GoldpathCampaignCeiling.Kind.set -> void
Goldpath.GoldpathCampaignCeiling.Name.get -> string!
Goldpath.GoldpathCampaignCeiling.Name.set -> void
Goldpath.GoldpathCampaignCeiling.MaxTps.get -> int?
Goldpath.GoldpathCampaignCeiling.MaxTps.set -> void
Goldpath.GoldpathCampaignCeiling.SeedMaxTps.get -> int?
Goldpath.GoldpathCampaignCeiling.SeedMaxTps.set -> void
Goldpath.GoldpathCampaignCeiling.UpdatedAt.get -> System.DateTimeOffset
Goldpath.GoldpathCampaignCeiling.UpdatedAt.set -> void
Goldpath.GoldpathCampaignCeiling.UpdatedBy.get -> string!
Goldpath.GoldpathCampaignCeiling.UpdatedBy.set -> void
Goldpath.GoldpathCampaignCeilingKind
Goldpath.GoldpathCampaignCeilingKind.Global = 0 -> Goldpath.GoldpathCampaignCeilingKind
Goldpath.GoldpathCampaignCeilingKind.TargetSystem = 1 -> Goldpath.GoldpathCampaignCeilingKind
Goldpath.GoldpathCampaignCeilingInfo
Goldpath.GoldpathCampaignCeilingInfo.GoldpathCampaignCeilingInfo(string! Kind, string! Name, int? MaxTps, int? CodeMaxTps, System.Collections.Generic.IReadOnlyList<string!>! Types, System.DateTimeOffset UpdatedAt, string! UpdatedBy) -> void
Goldpath.GoldpathCampaignCeilingInfo.Kind.get -> string!
Goldpath.GoldpathCampaignCeilingInfo.Kind.init -> void
Goldpath.GoldpathCampaignCeilingInfo.Name.get -> string!
Goldpath.GoldpathCampaignCeilingInfo.Name.init -> void
Goldpath.GoldpathCampaignCeilingInfo.MaxTps.get -> int?
Goldpath.GoldpathCampaignCeilingInfo.MaxTps.init -> void
Goldpath.GoldpathCampaignCeilingInfo.CodeMaxTps.get -> int?
Goldpath.GoldpathCampaignCeilingInfo.CodeMaxTps.init -> void
Goldpath.GoldpathCampaignCeilingInfo.Types.get -> System.Collections.Generic.IReadOnlyList<string!>!
Goldpath.GoldpathCampaignCeilingInfo.Types.init -> void
Goldpath.GoldpathCampaignCeilingInfo.UpdatedAt.get -> System.DateTimeOffset
Goldpath.GoldpathCampaignCeilingInfo.UpdatedAt.init -> void
Goldpath.GoldpathCampaignCeilingInfo.UpdatedBy.get -> string!
Goldpath.GoldpathCampaignCeilingInfo.UpdatedBy.init -> void
Goldpath.GoldpathCampaignCeilingPatch
Goldpath.GoldpathCampaignCeilingPatch.GoldpathCampaignCeilingPatch(int? MaxTps = null, bool ClearMaxTps = false) -> void
Goldpath.GoldpathCampaignCeilingPatch.MaxTps.get -> int?
Goldpath.GoldpathCampaignCeilingPatch.MaxTps.init -> void
Goldpath.GoldpathCampaignCeilingPatch.ClearMaxTps.get -> bool
Goldpath.GoldpathCampaignCeilingPatch.ClearMaxTps.init -> void
Goldpath.GoldpathCampaignOptions.AddTargetSystem(string! name, int maxTps) -> Goldpath.GoldpathCampaignOptions!
Goldpath.GoldpathCampaignOptions.TargetSystems.get -> System.Collections.Generic.IReadOnlyDictionary<string!, int>!
Goldpath.GoldpathCampaignType.TargetSystem.get -> string?
Goldpath.GoldpathCampaignTypeBuilder<TTarget>.TargetSystem(string! name) -> Goldpath.GoldpathCampaignTypeBuilder<TTarget!>!
Goldpath.GoldpathCampaignInfo.TargetSystem.get -> string?
Goldpath.GoldpathCampaignInfo.TargetSystem.init -> void
Goldpath.GoldpathCampaignAdminService<TContext>.GetCeilingsAsync(System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Goldpath.GoldpathCampaignCeilingInfo!>!>!
Goldpath.GoldpathCampaignAdminService<TContext>.ThrottleCeilingAsync(Goldpath.GoldpathCampaignCeilingKind kind, string! name, Goldpath.GoldpathCampaignCeilingPatch! patch, string! actor, System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<Goldpath.GoldpathAdminResult!>!
Goldpath.GoldpathCampaignAdminService<TContext>.GetCeilingAuditAsync(int take, System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Goldpath.GoldpathCampaignAudit!>!>!
```

`modelBuilder.AddGoldpathCampaign()` maps the new table; its signature does not change.

**Proof.** Unit (red today: the API does not exist): two campaigns of DIFFERENT types naming one
system share its bucket evenly when equal and four-to-one under priority, and never jointly
exceed it; a type naming no system keeps its R2 bucket; a row change applies on the next tick
with no restart; the seed rule (Q4) both ways; an undeclared system refuses at startup.
Integration (**S-FLEET-03 extended**, real broker + PostgreSQL, strict ceiling after #292): two
campaigns of different types on one system never jointly exceed its ceiling in any one-second
window; a live change through the admin verb bites within one tick; the audit row carries the
actor. Console smoke: the governor shows the binding ceiling and edits it.

### R3.3 — the module options an operator tunes reload without a restart ([#290](https://github.com/qorpe/goldpath/issues/290) part b)

`AddGoldpathCampaign` binds the configuration section `Goldpath:Campaign` through
`IOptionsMonitor` (composed, not rewritten — ADR-0003), the same section-per-package convention
every other Goldpath package follows (`Goldpath:Jobs`, `Goldpath:Bulk`, …). Four keys reload:
`RetryBackoff`, `RetryJitter`, `OrphanReleaseAfter`, `StaleClaimAfter`. The code values set in
the `configure` delegate stay the defaults. A key present in configuration wins, and a change
to it reaches the next tick and the next outcome flush. The properties on
`GoldpathCampaignOptions` keep their names and read through the monitor, so the engine, the
pacer, the sink and every shipped constructor are untouched.

Two details hold the change honest. First, the configuration binder adds configured items to a
pre-filled collection, so the section binds into an empty instance and code fills only the keys
configuration left unset: a rung list replaces, never appends. Second, a reload that fails
validation (an empty ladder, jitter outside [0, 1], a non-positive age) is refused: the last
valid values stay in force, and the refusal is logged with the offending key and counted
(`goldpath_campaign_tuning_reload_refused_total`), so a typo in a config map cannot stop the
pacer mid-slice. `GlobalTps` is not in this list: it is a row (R3.2). `LeaderTick`,
`LeadershipSlice` and the batch sizes stay startup values (#284 owns the slice defaults).

**Public API.** None.

**Proof.** Unit (red today: the options are a startup singleton): a configuration reload changes
the next tick's ladder rung and the orphan and stale-claim ages, with no restart; a refused reload
leaves the previous values in force and moves the counter.

### R3.4 — a ceiling for outstanding acknowledgements ([#293](https://github.com/qorpe/goldpath/issues/293))

**Rule.** `PrepareTickAsync` computes `inFlight = ReleasedThrough − SucceededCount −
FailedCount` today, which counts `Released`, `Processing`, `AwaitingRetry` and `AwaitingAck`.
With `MaxAwaitingAck` null that stays exactly the rule, line for line. With it set:

- `MaxInFlight` caps live work: `inFlight − AwaitingAckCount`. A waiting retry keeps its slot,
  the backpressure R1.3 chose deliberately (owner decision Q2; the issue's literal text counted
  `Released + Processing` only).
- `MaxAwaitingAck` caps `AwaitingAckCount`. An acceptance moves an item from the first ceiling
  to the second, and the pacer releases only while both have room.

**Counting without a scan.** A per-tick `COUNT(*)` over a campaign's `AwaitingAck` rows would
read a million index entries every 250 ms on exactly the campaigns this item exists for. The
campaign row gains `AwaitingAckCount`, maintained in the flush that moves rows in or out of
`AwaitingAck`: `ApplyOutcomesAsync` adds the acceptances it wrote and subtracts the settles and
failures whose prior state was `AwaitingAck` (the ack sweep goes through the same method). Like
every counter here it is a cache (D3): the leader re-counts it from the table once per slice.
R2's `(CampaignId, State, …)` indexes serve that count, and the slice is the takeover boundary
anyway.

**Admin and console.** `throttle` patches `MaxAwaitingAck` (omitted = keep, `ClearMaxAwaitingAck`
= back to today's rule), audited old → new. The detail returns the ceiling and the count, and the
governor shows and edits both. Runbook item 8's advice ("raise `MaxInFlight` toward Tps × the ack
timeout") is rewritten to "set `MaxAwaitingAck`".

**Public API (additions only).**

```
Goldpath.GoldpathCampaign.MaxAwaitingAck.get -> int?
Goldpath.GoldpathCampaign.MaxAwaitingAck.set -> void
Goldpath.GoldpathCampaign.AwaitingAckCount.get -> long
Goldpath.GoldpathCampaign.AwaitingAckCount.set -> void
Goldpath.GoldpathCampaignPolicy.MaxAwaitingAck.get -> int?
Goldpath.GoldpathCampaignPolicy.MaxAwaitingAck.init -> void
Goldpath.GoldpathCampaignThrottle.MaxAwaitingAck.get -> int?
Goldpath.GoldpathCampaignThrottle.MaxAwaitingAck.init -> void
Goldpath.GoldpathCampaignThrottle.ClearMaxAwaitingAck.get -> bool
Goldpath.GoldpathCampaignThrottle.ClearMaxAwaitingAck.init -> void
Goldpath.GoldpathCampaignInfo.MaxAwaitingAck.get -> int?
Goldpath.GoldpathCampaignInfo.MaxAwaitingAck.init -> void
Goldpath.GoldpathCampaignInfo.AwaitingAck.get -> long
Goldpath.GoldpathCampaignInfo.AwaitingAck.init -> void
```

**Proof.** Unit (red today): with the field set, the allowance ignores `AwaitingAck` for
`MaxInFlight` and stops at `MaxAwaitingAck`; with it null, the allowance equals R2's for every
mix of item states; a drifted counter is corrected at the next slice start. Integration (real
broker + PostgreSQL, a simulated target that acknowledges slowly): live calls stay at or below
`MaxInFlight` while ten times as many acknowledgements are outstanding, and every item completes
exactly once.

### R3.5 — a policy version on the throttle verb ([#298](https://github.com/qorpe/goldpath/issues/298), campaign half; S-OPS-02)

**Rule.** `GoldpathCampaign.PolicyVersion` (`long`, 0 on every existing row) increments by one
on every APPLIED throttle and on nothing else: the pacer, the sink, pause, resume, abort and the
auto-suspend never touch it (state transitions keep `State` as their token; #298 leaves them out
of scope). The admin detail returns it.

**Design.** It is deliberately NOT an EF concurrency token. A model-level token would sit in the
`WHERE` of every tracked campaign write, and `EnumerateStepAsync`, `ReleaseBatchAsync` and
`TryCompleteAsync` would start losing races to an operator's throttle; `EnumerateStepAsync` does
not catch `DbUpdateConcurrencyException` today. Instead the throttle becomes one conditional
`ExecuteUpdate` per attempt: it sets the patched policy columns and `PolicyVersion + 1`
`WHERE Id = @id AND State = @readState AND PolicyVersion = @readVersion`.

- With an expected version (the console always sends the one it read): a mismatch is refused in
  the server's words — "the policy changed since you read it (version 4, by *actor* at *time*);
  re-read and retry" — and the refusal is audited as `throttle-refused` with both versions.
- Without one (scripts, R2 clients): the service re-reads and re-applies on a lost race, so the
  last writer still wins, and every applied throttle still increments the version exactly once.

The HTTP shape (owner, 2026-10-20: an optional `If-Match` on `throttle`, the version as the
`ETag`, a stale version answered with `412` and teaching text) is
[admin-contract revision R4](https://github.com/qorpe/goldpath/issues/304)'s to write. The
service returns which case it was, so the endpoint can map it.

**Public API (additions only).**

```
Goldpath.GoldpathCampaign.PolicyVersion.get -> long
Goldpath.GoldpathCampaign.PolicyVersion.set -> void
Goldpath.GoldpathCampaignInfo.PolicyVersion.get -> long
Goldpath.GoldpathCampaignInfo.PolicyVersion.init -> void
Goldpath.GoldpathCampaignThrottleResult
Goldpath.GoldpathCampaignThrottleResult.GoldpathCampaignThrottleResult(bool Ok, string! Message, long PolicyVersion, bool Stale) -> void
Goldpath.GoldpathCampaignThrottleResult.Ok.get -> bool
Goldpath.GoldpathCampaignThrottleResult.Ok.init -> void
Goldpath.GoldpathCampaignThrottleResult.Message.get -> string!
Goldpath.GoldpathCampaignThrottleResult.Message.init -> void
Goldpath.GoldpathCampaignThrottleResult.PolicyVersion.get -> long
Goldpath.GoldpathCampaignThrottleResult.PolicyVersion.init -> void
Goldpath.GoldpathCampaignThrottleResult.Stale.get -> bool
Goldpath.GoldpathCampaignThrottleResult.Stale.init -> void
Goldpath.GoldpathCampaignAdminService<TContext>.ThrottleAsync(System.Guid id, Goldpath.GoldpathCampaignThrottle! patch, long? expectedPolicyVersion, string! actor, System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<Goldpath.GoldpathCampaignThrottleResult!>!
```

The shipped four-argument `ThrottleAsync` stays and calls the new overload with no expected
version.

**Proof.** Unit (red today: two throttles from one read both apply): two throttles read the same
version, the second is refused, and the audit shows one applied change and one refusal; two
throttles with no version both apply and the version moves by two; a pacer slice, a pause and a
resume leave the version where it was. Console smoke (with R4's route slice): a stale form is
refused, re-reads, and the operator's retry succeeds.

### R3.6 — auto-suspend on a failure ratio, and the `Suspended` state ([#299](https://github.com/qorpe/goldpath/issues/299), S-OPS-03)

**Policy.** `SuspendOnFailureRatio` (`double?` in (0, 1]; null = off, the default),
`SuspendWindow` (outcomes; default 1,000) and `SuspendMinSample` (default 100, the owner's
number). The issue proposed `PauseWhenFailureRatio`; the field is named after the state it
produces. The ratio counts every completed attempt, **retryable failures included**, as the
owner decided: a melting target system shows up as timeouts first. `ACK_TIMEOUT` and an
interrupted claim count as failures; an acceptance is not an outcome until it settles.

**Counting (owner decision Q3).** Two counters on the campaign row, `WindowOutcomes` and
`WindowFailures`, ride the campaign `UPDATE` that `ApplyOutcomesAsync` already runs once per
flush. The stale-claim sweep's `FailedCount` update carries them too. They cost no extra write.
Each tick, for a `Running` campaign with the ratio set, the pacer reads them from the row it has
already loaded:

- at least `SuspendMinSample` outcomes and `WindowFailures / WindowOutcomes ≥` the ratio →
  suspend;
- otherwise, at `SuspendWindow` outcomes, the window restarts: the pacer subtracts what it read
  (never zeroes, so a concurrent flush is not lost) and stamps `WindowStartedAt`.

Detection therefore lands one leader tick after the sink flush that crosses the threshold. The
ratio covers the outcomes since the window last restarted, which is at most `SuspendWindow` of
them. That is a tumbling window that is evaluated early, not an exact sliding "last N"; an exact
one needs a `(CampaignId, CompletedAt)` index on the thirty-million-row table and still cannot
see a retryable failure whose item later succeeded.

**Suspension.** The flip `Running → Suspended` is state-token guarded, like `TryExpireAsync`.
`SuspendedReason` (512) records the ratio, the window, the start of the window and the most
frequent error code in a sample of up to 500 of the window's failures, for example *failure
ratio 0.52 ≥ 0.30 over 400 outcomes since 10:42:05Z (minimum sample 100); most frequent code
TIMEOUT (61%)*. The sample query reads `Failed` and `AwaitingRetry` rows with
`CompletedAt ≥ WindowStartedAt`, stops at 500 rows and runs once per suspension. The
audit row is `suspend` with the actor `system`, and the reason is shown in the admin detail and
the console. `LastVerb` is not enough for this, because a throttle during the suspension would
overwrite it.

**Resume and the rest of the verbs.** `resume` accepts `Suspended` (the operator's verb, audited);
it clears `SuspendedReason`, restarts the window and, like a resume from `Paused`, marks every
`Released` row due (R2.6). `abort` and `throttle` accept `Suspended`, so an operator can lower the
rate before resuming; `pause` refuses it ("already stopped — resume or abort"). There is no
automatic resume (#299 out of scope).

**Public API (additions only).**

```
Goldpath.GoldpathCampaignState.Suspended = 9 -> Goldpath.GoldpathCampaignState
Goldpath.GoldpathCampaign.SuspendOnFailureRatio.get -> double?
Goldpath.GoldpathCampaign.SuspendOnFailureRatio.set -> void
Goldpath.GoldpathCampaign.SuspendWindow.get -> int?
Goldpath.GoldpathCampaign.SuspendWindow.set -> void
Goldpath.GoldpathCampaign.SuspendMinSample.get -> int?
Goldpath.GoldpathCampaign.SuspendMinSample.set -> void
Goldpath.GoldpathCampaign.WindowOutcomes.get -> long
Goldpath.GoldpathCampaign.WindowOutcomes.set -> void
Goldpath.GoldpathCampaign.WindowFailures.get -> long
Goldpath.GoldpathCampaign.WindowFailures.set -> void
Goldpath.GoldpathCampaign.WindowStartedAt.get -> System.DateTimeOffset?
Goldpath.GoldpathCampaign.WindowStartedAt.set -> void
Goldpath.GoldpathCampaign.SuspendedReason.get -> string?
Goldpath.GoldpathCampaign.SuspendedReason.set -> void
Goldpath.GoldpathCampaignPolicy.SuspendOnFailureRatio.get -> double?
Goldpath.GoldpathCampaignPolicy.SuspendOnFailureRatio.init -> void
Goldpath.GoldpathCampaignPolicy.SuspendWindow.get -> int
Goldpath.GoldpathCampaignPolicy.SuspendWindow.init -> void
Goldpath.GoldpathCampaignPolicy.SuspendMinSample.get -> int
Goldpath.GoldpathCampaignPolicy.SuspendMinSample.init -> void
Goldpath.GoldpathCampaignThrottle.SuspendOnFailureRatio.get -> double?
Goldpath.GoldpathCampaignThrottle.SuspendOnFailureRatio.init -> void
Goldpath.GoldpathCampaignThrottle.ClearSuspendOnFailureRatio.get -> bool
Goldpath.GoldpathCampaignThrottle.ClearSuspendOnFailureRatio.init -> void
Goldpath.GoldpathCampaignThrottle.SuspendWindow.get -> int?
Goldpath.GoldpathCampaignThrottle.SuspendWindow.init -> void
Goldpath.GoldpathCampaignThrottle.SuspendMinSample.get -> int?
Goldpath.GoldpathCampaignThrottle.SuspendMinSample.init -> void
Goldpath.GoldpathCampaignInfo.SuspendOnFailureRatio.get -> double?
Goldpath.GoldpathCampaignInfo.SuspendOnFailureRatio.init -> void
Goldpath.GoldpathCampaignInfo.SuspendWindow.get -> int
Goldpath.GoldpathCampaignInfo.SuspendWindow.init -> void
Goldpath.GoldpathCampaignInfo.SuspendMinSample.get -> int
Goldpath.GoldpathCampaignInfo.SuspendMinSample.init -> void
Goldpath.GoldpathCampaignInfo.SuspendedReason.get -> string?
Goldpath.GoldpathCampaignInfo.SuspendedReason.init -> void
Goldpath.GoldpathCampaignInfo.FailureRatioNow.get -> double?
Goldpath.GoldpathCampaignInfo.FailureRatioNow.init -> void
```

The row stores `SuspendWindow` and `SuspendMinSample` as nullable, so existing rows need no data
step; `PolicyOf` applies the defaults.

**Proof.** Unit (red today: nothing suspends): crossing the ratio suspends within one tick of the
flush, with the reason and a `system` audit row; below the minimum sample nothing suspends,
whatever the ratio; a `Suspended` campaign claims nothing; resume restarts the window, so the old
failures do not suspend it again; a concurrent flush during a window restart loses no outcome;
null never suspends. Integration (**S-OPS-03**, new, real broker + PostgreSQL): a simulated target
flips to 50% failures, the campaign suspends within one tick of the flush that crosses the
threshold with its reason, nothing more is claimed, and resume continues it to completion with
every item exactly once.

### The one migration

Every R3 column lands in ONE generated migration, named in the upgrade guide with its `[schema]`
marker. By owner decision Q1 it is SHARED with R2's on preview.10: R2 is unpublished, so an
adopter coming from preview.9 generates one migration for both, and the preview.10 guide's step
becomes `goldpath db add CampaignR2R3`, with R2's `GoldpathCampaignR2Backfill()` line still at the
end of its `Up`. The guide is rewritten by the slice that lands the schema (R3-S1).

| Table | Change | Default that reproduces R2 |
|---|---|---|
| `GoldpathCampaigns` | `StartAt` (`DateTimeOffset?`) | null — no `Ready` state |
| `GoldpathCampaigns` | `MaxAwaitingAck` (`int?`), `AwaitingAckCount` (`long`) | null · 0 (the first slice re-counts) |
| `GoldpathCampaigns` | `PolicyVersion` (`long`) | 0 |
| `GoldpathCampaigns` | `SuspendOnFailureRatio` (`double?`), `SuspendWindow` (`int?`), `SuspendMinSample` (`int?`), `WindowOutcomes` (`long`), `WindowFailures` (`long`), `WindowStartedAt` (`DateTimeOffset?`), `SuspendedReason` (512) | null (off) · 0 |
| `GoldpathCampaignCeilings` | new table, primary key `(Kind, Name)` | seeded from code by the leader |
| `GoldpathCampaignItems` | **nothing** | — |

What the table means for each provider:

- **No change to the items table.** R2's guide had to warn that three `CREATE INDEX` statements
  block writes on a thirty-million-row table. R3 adds no index and no item column, so the
  migration is a metadata change on PostgreSQL 11+ and SQL Server and can be applied while
  campaigns run.
- **No data step.** Every default above is R2's behaviour, so `GoldpathCampaignMigrations`
  gains no method, and there is no provider-specific SQL of the `GoldpathCampaignR2Backfill`
  kind for PostgreSQL, SQL Server or a later Oracle provider
  ([#287](https://github.com/qorpe/goldpath/issues/287)/[#305](https://github.com/qorpe/goldpath/issues/305))
  to translate.
- The new states are integers in the existing `State` column, and the existing
  `(State, CreatedAt)` index serves `LoadWorkableAsync` with `Ready` added.
- `double?` maps to `double precision` on PostgreSQL and `float` on SQL Server.
- `ModelContractTests` gains the column, max-length and table goldens in the schema slice.

### Metrics, runbooks, console

- **Instruments** (each one asserted by #294's export test once both have landed):
  - `goldpath_campaign_suspended_total{type}` (R3.6);
  - `goldpath_campaign_awaiting_ack`, a gauge tagged like `goldpath_campaign_in_flight` (R3.4);
  - `goldpath_campaign_tuning_reload_refused_total` (R3.3).
  - The configured-ceiling gauges #294 adds gain a `target_system` scope (R3.2).
  - #294's binding-constraint values gain `target_system` and `awaiting_ack`.
  - Whichever of #294 and the R3 slice lands second adds the overlap. `goldpath_campaign_in_flight`
    keeps its meaning (everything released and not terminal).
- **Alert candidates for [#295](https://github.com/qorpe/goldpath/issues/295)'s rule file**:
  a campaign suspended (`major`), and a tuning reload refused (`minor`).
- **Runbooks** (land with their slices):
  - item 1 learns `Ready` ("waiting for its start, not stuck") and `Suspended`;
  - item 3 points at auto-suspend;
  - item 8 is rewritten around `MaxAwaitingAck`;
  - new items: moving a scheduled start; changing a target-system ceiling during an incident;
    a suspended campaign (read the reason, check the target system, lower the rate, resume).
- **Console** (`CampaignPanel.tsx`):
  - the state list gains `Ready` and `Suspended`, and `Ready` joins the live set;
  - the governor grows the start, `MaxAwaitingAck`, the binding target system with its ceiling,
    the suspension policy and reason, and resume on `Suspended`;
  - it sends the policy version it read.
  - Each lands with its item's slice; the vitest floor and axe hold throughout.

### Verifiers (ADR-0005)

R3 adds no coding standard an adopter must follow, so **R3 ships no analyzer** and **GP1705
stays free** (the next id after GP1704 in `AnalyzerReleases.Unshipped.md`; owner decision Q7). Every
rule is a runtime invariant, and each has its verifier:

| Rule | Verifier |
|---|---|
| nothing releases before `StartAt`, DST included | `R3StartTests` (unit) + `CampaignR3Tests` (real broker) |
| the start cannot move once the campaign has started | admin refusal test |
| a type naming an undeclared target system | startup refusal with teaching text + its unit test |
| types sharing a system never exceed it jointly; a live change bites next tick | unit + S-FLEET-03 extended (real broker) |
| an invalid tuning reload never takes effect | unit + `goldpath_campaign_tuning_reload_refused_total` |
| live calls ≤ `MaxInFlight`, acknowledgements ≤ `MaxAwaitingAck`; null = R2 | unit (equality with R2 over every state mix) + slow-ack integration |
| a stale throttle is refused and audited; no version = last writer wins | unit + console smoke (with R4) |
| a failing campaign suspends with its reason; resume is human | unit + S-OPS-03 (real broker) |
| the migration's defaults reproduce R2 | `ModelContractTests` goldens + the R2 suites green unchanged on the R3 model |

### Deliberately NOT in this revision

- **Recurring campaigns** and **a snapshot taken at the start instead of at create.** The
  snapshot is taken when the leader adopts the campaign (owner decision). Trigger for the second:
  an adopter whose targets change between create and start in a way that matters. Until then,
  create closer to the start.
- **Releasing while the snapshot is still running**: unchanged (R1/R2; #288's non-goal).
- **Automatic resume**, and **suspension across all campaigns of one target system**. Trigger: the
  first adopter who wants one target system's failure to stop every campaign that calls it (it
  would follow R3.2).
- **Per-target-system acknowledgement ceilings** (#293 out of scope): R3.4 is per campaign.
- **A configurable burst capacity**: the owner fixed it at one tick on #292.
- **Optimistic concurrency on pause, resume and abort**: `State` already guards them.
- **Live `MaxTargets`**: the enumeration ceiling stays a code decision (runbook item 7).
- **Live per-type `MaxTps` without a target system**: name a target system instead.
- **Live `LeaderTick`, `LeadershipSlice` and batch sizes**: these are not dials for the middle
  of an incident, and #284 owns the slice defaults.
- **A distributed or cross-application limiter, Redis counters**: unchanged (R1).
- **Four-eyes approval at `Ready`** ([#291](https://github.com/qorpe/goldpath/issues/291)): the
  owner decided on 2026-10-20 that T37's trigger has fired and that the gate sits at `Ready`,
  through `Goldpath.Approvals`. It is not one of the five items approved for R3. R3.1 leaves it
  one internal start-gate check to plug into; the binding follows as amendment **R3.7**, written
  after this revision (owner decision Q6). It needs no campaign column, so it adds nothing to the
  one migration.
- **Read and mutate roles** ([#297](https://github.com/qorpe/goldpath/issues/297)): reversing
  R2's "finer roles than the ops floor" non-goal (owner, 2026-10-20) is recorded in this RFC by
  [#304](https://github.com/qorpe/goldpath/issues/304), together with admin-contract R4 and its
  ledger row, not by R3. Recorded: see R2's *Deliberately NOT* and admin-contract R4.1; ledger row T50.

### Compatibility

R2 has not shipped yet: it is on `main`, bound for preview.10. The published preview.9 carries
R1. R3 is additive against both:

- **No removed or changed public member.** Every addition is a new type, a new method overload
  or an init-only member. `GoldpathCampaignInfo` and `GoldpathCampaignThrottle` keep their
  positional constructors, so R3 adds no break to the ones R2 already lists.
- **Two new enum values**, `GoldpathCampaignState.Ready = 8` and `Suspended = 9`. An exhaustive
  `switch` over campaign states gains two cases, and a client of the admin API sees two new
  `state` strings. The console learns both in the slices that produce them.
- **No behaviour changes without opt-in.**
  - `StartAt` null → no `Ready`.
  - No target system → R2's buckets.
  - `MaxAwaitingAck` null → R2's in-flight rule.
  - `SuspendOnFailureRatio` null → nothing suspends.
  - No `Goldpath:Campaign` section → the code values.
  - A throttle without a version → last writer wins, as today.
  - The only new background act is the leader seeding the ceiling rows from code.
- **One migration, no data step, nothing on the items table** (above).
- The frozen admin contract's existing routes keep their shape. The detail and the throttle body
  gain fields the way R1 and R2 grew them (omitted = keep). The ceiling routes (R3.2) and the
  throttle's `If-Match` (R3.5) need admin-contract revision R4
  ([#304](https://github.com/qorpe/goldpath/issues/304)).
- No `MassTransit.` type enters any `PublicAPI.*.txt` (ADR-0013; GA readiness G1).

### Where each blocked issue lands

R3 is exactly the owner's five items. The other issues of the 2026-10-20 analysis stay
independent of it, and each has a reason:

| Issue | Home | Why |
|---|---|---|
| [#283](https://github.com/qorpe/goldpath/issues/283) 1000-TPS bench + baseline (S-FLEET-02a) | not in R3 | measures, changes no package; §6 owns the number |
| [#284](https://github.com/qorpe/goldpath/issues/284) continuous leadership | not in R3 | an options default, no public API (owner-approved); R3.2 and R3.4 add no work between slices |
| [#285](https://github.com/qorpe/goldpath/issues/285) batched release publishing | not in R3 | no public API (ADR-0013 forbids the engine's types in it); R3 adds one query per tick (the ceiling rows), which #285's query count includes |
| [#286](https://github.com/qorpe/goldpath/issues/286) pause within 3 s (S-FLEET-04) | not in R3 | proves R2.6; R3.6's `Suspended` uses the same claim guard, so S-OPS-03 inherits the proof |
| [#288](https://github.com/qorpe/goldpath/issues/288) snapshot off the leader tick | not in R3 | no public API; it shortens the time to `Ready` and leaves the R3.1 rule unchanged |
| [#289](https://github.com/qorpe/goldpath/issues/289) `StartAt` + `Ready` | **R3.1** | — |
| [#290](https://github.com/qorpe/goldpath/issues/290) target-system ceilings, tunable options | **R3.2** (part a + the verb of part b), **R3.3** (the reload of part b) | — |
| [#291](https://github.com/qorpe/goldpath/issues/291) four-eyes at `Ready` | amendment R3.7 (owner decision Q6); plugs into R3.1 | outside the owner's five |
| [#292](https://github.com/qorpe/goldpath/issues/292) burst control | not in R3 | a defect against R1's DoD, no knob (owner); R3.2's bucket inherits its one-tick rule |
| [#293](https://github.com/qorpe/goldpath/issues/293) `MaxAwaitingAck` | **R3.4** | — |
| [#294](https://github.com/qorpe/goldpath/issues/294) metrics RFC §7 promises | not in R3 | no public API; R3's instruments join its export test |
| [#295](https://github.com/qorpe/goldpath/issues/295) dashboard + alert rules | not in R3 | R3 hands it two alert candidates |
| [#296](https://github.com/qorpe/goldpath/issues/296) live governor, failures by code | not in R3 | admin-contract R4 (#304); R3's governor fields build on its polling |
| [#297](https://github.com/qorpe/goldpath/issues/297) read vs mutate roles | not in R3 | admin-contract R4 + the read role (#304) |
| [#298](https://github.com/qorpe/goldpath/issues/298) throttle optimistic concurrency | **R3.5** (the version and the service); the `If-Match`/`ETag`/`412` shape is R4's (#304) | — |
| [#299](https://github.com/qorpe/goldpath/issues/299) auto-suspend (S-OPS-03) | **R3.6** | — |

### Implementation slices (after acceptance; each one issue, each ≤ ~3 hours of agent time)

In dependency order. Each slice carries its own tests, its PublicAPI lines, its runbook and README
text, and the upgrade-guide line for what it adds. The R3 row in `open-threads.md` (T39) closes
with the last one.

| Slice | R3 item | Issue | Depends on | Scope |
|---|---|---|---|---|
| R3-S1 | all | [#314](https://github.com/qorpe/goldpath/issues/314) | acceptance | every column, the ceilings table, the two enum values, the model goldens, the preview.10 guide's one `goldpath db add CampaignR2R3` step (Q1); no behaviour. Landing the schema first makes it one migration whatever order the features land in |
| R3-S2 | R3.1 | [#289](https://github.com/qorpe/goldpath/issues/289) | S1 | engine + pacer + admin: `Ready`, the start gate check, the throttle rules, `R3StartTests` (DST pair included) |
| R3-S3 | R3.1 | [#315](https://github.com/qorpe/goldpath/issues/315) | S2 | `CampaignR3Tests` on the real broker, the console governor's start, the console smoke, S-FLEET-01's scheduled-start step |
| R3-S4 | R3.5 | [#298](https://github.com/qorpe/goldpath/issues/298) | S1 | `PolicyVersion`, the conditional throttle, the refusal audit, the overload; the HTTP half rides admin-contract R4's route slice ([#304](https://github.com/qorpe/goldpath/issues/304)) |
| R3-S5 | R3.4 | [#293](https://github.com/qorpe/goldpath/issues/293) | S1 | `AwaitingAckCount` in the flush and the sweep, the slice re-count, the pacer rule, the throttle field, unit proofs |
| R3-S6 | R3.4 | [#316](https://github.com/qorpe/goldpath/issues/316) | S5 | the slow-acknowledgement real-broker test, the governor fields, runbook item 8 |
| R3-S7 | R3.3 | [#317](https://github.com/qorpe/goldpath/issues/317) | acceptance (no schema) | `Goldpath:Campaign` through `IOptionsMonitor`, replace-not-append binding, the refused-reload path |
| R3-S8 | R3.2 | [#290](https://github.com/qorpe/goldpath/issues/290) | S1, #292 | registration, the seed reconcile, the pacer's target-system bucket, unit proofs |
| R3-S9 | R3.2 | [#318](https://github.com/qorpe/goldpath/issues/318) | S8, #304's contract text | the ceiling verbs, audit, routes under R4, S-FLEET-03 extended on the real broker, the governor editor |
| R3-S10 | R3.6 | [#299](https://github.com/qorpe/goldpath/issues/299) | S1 (soft: #294, so the new counter joins its export test) | the window counters in the flush and the sweep, the pacer's evaluation, `Suspended` across the verbs, the reason, unit proofs |
| R3-S11 | R3.6 | [#319](https://github.com/qorpe/goldpath/issues/319) | S10 | S-OPS-03 on the real broker, the console's suspended view and resume, the runbook item, the scenario entry, the RFC status line `IMPLEMENTED` |

#289, #290, #293, #298 and #299 were each too big for one run as written. Each now carries its
first part, retitled, and the later parts are new issues: #289 → #315, #293 → #316,
#290 → #317 (the options reload, which shares no code with the ceilings) and #318, #299 → #319.
#298 keeps the campaign half; its HTTP half rides admin-contract R4. R3-S1 (#314) and R3-S7 (#317)
depend on nothing but this revision's merge; every other slice waits on the slices its row names.

### Owner decisions (2026-10-21, in chat — all seven as recommended)

The revision was written with these seven open; the owner answered them on 2026-10-21 and the
text above reads them as decided.

- **Q1 — The migration on preview.10.** R2 is unpublished, and its guide named
  `goldpath db add CampaignR2`; once R3-S1 lands an adopter coming from preview.9 can only
  generate ONE migration, because EF diffs the whole model. Options were (a) ship R2 and R3
  together as one step, or (b) hold R3 to preview.11 as `CampaignR3`. **Owner decision
  2026-10-21: (a)** — one migration, `goldpath db add CampaignR2R3`, on preview.10, with R2's
  backfill line at the end of its `Up`.
- **Q2 — Does a waiting retry keep its `MaxInFlight` slot when `MaxAwaitingAck` is set?** #293's
  text counted `Released + Processing` only. **Owner decision 2026-10-21: yes** — `AwaitingRetry`
  keeps its slot (R1.3's backpressure), and the rule needs one counter, `AwaitingAckCount`.
- **Q3 — The auto-suspend window.** (a) Counters on the campaign row, evaluated every tick once
  `SuspendMinSample` is reached, restarting every `SuspendWindow` outcomes; or (b) an exact
  sliding "last N" over the items table (a new index on the thirty-million-row table). **Owner
  decision 2026-10-21: (a)**, `SuspendWindow` default 1,000.
- **Q4 — A code ceiling that changes after the row was seeded.** (a) Follow the code while the
  operator has never moved the row; (b) the row always wins; (c) the code always wins. **Owner
  decision 2026-10-21: (a)** — code follows only while `MaxTps == SeedMaxTps`; otherwise the
  operator's value stays and the disagreement is logged.
- **Q5 — The ceiling routes** (`GET /ceilings`, `POST /ceilings/global/throttle`,
  `POST /ceilings/target-systems/{name}/throttle`, `GET /ceilings/audit`). **Owner decision
  2026-10-21: fold them into admin-contract R4 ([#304](https://github.com/qorpe/goldpath/issues/304))**,
  so the contract is revised and frozen once.
- **Q6 — #291 (four-eyes at `Ready`).** **Owner decision 2026-10-21: an amendment, R3.7**, written
  after this revision lands. R3.1's `Ready → Running` keeps one internal start-gate check with no
  gates; R3.7 binds the Approvals subject to the campaign id, with no campaign column.
- **Q7 — An analyzer for R3?** The candidate was GP1705 (`.TargetSystem("x")` with no
  `AddTargetSystem("x", …)`). **Owner decision 2026-10-21: no analyzer** — the startup refusal
  is the verifier; GP1705 stays free.
