# Goldpath.Campaign

Governed mass-execution — **L4 of the execution ladder**, the top rung. A campaign is a
durable plan: targets are enumerated ahead into rows, then RELEASED to competing broker
consumers at a pace the operator governs live:

```
create → enumerate (streaming, ceilinged) → release under policy → consumers execute
              │                                  │                        │
              │ watermark checkpoint             │ TPS / daily quota /    │ claim-before-
              │ (takeover-safe)                  │ window / max-in-flight │ execute, then
              │                                  │ (all LIVE-adjustable)  │ publish outcome
              └── ceiling hit → PAUSED           └── single leader,       └── batching sink
                  (a decision, not a default)        in-memory ticks          writes truth
```

The pacer is a LONG-LIVED leader run on Goldpath.Jobs: the ~1-minute cron only guarantees a
leader exists; pacing happens on in-memory ticks (250ms default) against durable
watermarks — leader death means the next fire takes over from the row, mid-campaign. A
leadership slice (`LeadershipSlice`, 60 s) is at least the cron's period, so the next fire is
already due when a slice returns and starts as it returns. Leadership is continuous, with a
measured hand-over of under 0.6 s (the longest gap between two releases at 50 TPS with a 250 ms tick: 583 ms and 459 ms on two runs) (campaign RFC §8 item 4); keep the slice at least the period if you
change either.

## Wire it

```csharp
builder.AddGoldpathCampaign<WebApplicationBuilder, OrdersDbContext>(campaign =>
{
    campaign.AddCampaign<DormantCustomer>("winback", c => c
        .MaxTargets(2_000_000)                 // MANDATORY — unbounded L4 is an outage
        .Targets((services, parameters) => services.GetRequiredService<OrdersDbContext>()
            .Customers.Where(x => x.LastOrderAt < DateTime.Parse(parameters["before"]))
            .OrderBy(x => x.Id)                // stable order — the watermark depends on it
            .Select(x => new DormantCustomer(x.Id, x.Email))
            .AsAsyncEnumerable())
        .DefaultPolicy(p => p with { Tps = 100, DailyQuota = 500_000, MaxInFlight = 2_000 }));
});
builder.Services.AddScoped<IGoldpathCampaignItemHandler<DormantCustomer>, WinbackHandler>();

builder.AddGoldpathJobs<WebApplicationBuilder, OrdersDbContext>(jobs =>
{
    jobs.ConnectionName = "ordersdb";
    jobs.AddGoldpathCampaignJobs<OrdersDbContext>();          // the pacer leader
});

builder.AddGoldpathMessaging(messaging => messaging          // campaign REQUIRES a broker
    .AddGoldpathCampaign<OrdersDbContext>()                    // item handler + batching outcome handler, one queue
    .UseRabbitMq());

// DbContext: modelBuilder.AddGoldpathCampaign();  modelBuilder.AddGoldpathJobs();
```

## Tuning during an incident (R3.3)

Four options reload from configuration without a restart — the dials an operator turns while a
target system is struggling:

```json
{
  "Goldpath": {
    "Campaign": {
      "RetryBackoff": [ "00:05:00", "00:30:00" ],
      "RetryJitter": 0.2,
      "OrphanReleaseAfter": "00:10:00",
      "StaleClaimAfter": "00:20:00"
    }
  }
}
```

- **Precedence:** a key present in `Goldpath:Campaign` wins; the value set in the
  `AddGoldpathCampaign` delegate is the default, and a key removed from configuration falls back to
  it. The change reaches the next tick and the next outcome flush.
- **A ladder replaces, never appends:** the two rungs above are the whole ladder. Rungs are
  read in index order, and one that does not parse refuses the reload rather than vanishing.
- **A bad reload is refused whole** — an empty ladder, a negative rung, jitter outside [0, 1], a
  non-positive age, a rung or age longer than 365 days (the engine adds rungs to and subtracts ages
  from the current instant, so `TimeSpan.MaxValue` as "switch it off" would otherwise throw on every
  tick; to stop the re-release, set the age to `365.00:00:00`), a value that does not parse (numbers and times are read invariantly: `0.5`,
  never `0,5`). The last valid values stay in force, a warning names `Goldpath:Campaign:<key>`,
  and `goldpath_campaign_tuning_reload_refused_total` counts it. An invalid section at startup is
  refused the same way and the code values hold. An empty or whitespace-only value reads as unset.
- **Not reloaded:** `LeaderTick`, `LeadershipSlice` and the batch sizes are startup values.
  `GlobalTps` is not an option the pacer reads any more: it seeds the global ceiling row (R3.2, below).

## R2: a target that answers later, a shared ceiling, a keyset takeover

```csharp
// Typed results — the R1 handler (return = success, throw = retryable) still works unchanged.
public sealed class ConfigPushHandler : IGoldpathCampaignActionHandler<DeviceTarget>
{
    public async Task<GoldpathCampaignActionResult> ExecuteAsync(DeviceTarget target, GoldpathCampaignItemContext context, CancellationToken ct)
    {
        // The callback path names the campaign; the core fills in its own correlation id and signs its answer.
        var callback = $"/goldpath/campaign/callbacks/{context.Type}/{context.CampaignId:N}/{{correlationId}}";
        var answer = await _core.PushAsync(target.DeviceId, idempotencyKey: context.IdempotencyKey, callback, ct);
        return answer switch
        {
            PushAnswer.Done => GoldpathCampaignActionResult.Succeeded(),
            PushAnswer.Queued q => GoldpathCampaignActionResult.Accepted(q.CorrelationId, ackTimeout: TimeSpan.FromHours(6)),
            PushAnswer.Unsupported => GoldpathCampaignActionResult.Failed("UNSUPPORTED_DEVICE", "no management client", retryable: false),
            _ => GoldpathCampaignActionResult.Failed("CORE_TIMEOUT", "no answer in 30s", retryable: true),
        };
    }
}

app.MapGoldpathCampaignCallbacks<OrdersDbContext>();   // the answer: POST {prefix}/{type}/{campaignId}/{correlationId}, signed

campaign.AddCampaign<DeviceTarget>("config-push", c => c
    .MaxTargets(50_000_000)
    .MaxTps(200)                                        // shared by EVERY config-push campaign — the target system's rate
    .SignedCallbacks(builder.Configuration["Campaign:ConfigPush:CallbackSecret"]!)   // HMAC key, >= 32 bytes, never in source
    .TargetsAfter((services, parameters, afterKey) =>   // keyset: a takeover reopens AFTER the last key, no skip read
        services.GetRequiredService<OrdersDbContext>().Devices.AsNoTracking()
            .Where(d => afterKey == null || d.Id.CompareTo(afterKey) > 0)
            .OrderBy(d => d.Id)
            .Select(d => new DeviceTarget(d.Id, d.PushToken))
            .AsAsyncEnumerable(),
        target => target.DeviceId));
```

Every callback carries `X-Goldpath-Timestamp` (unix seconds) and `X-Goldpath-Signature:
v1=<hex>` — HMAC-SHA256 with the type's secret over `{timestamp}\n{METHOD}\n{path}\n{body}`
(`GoldpathCampaignCallbackSignature.Sign` computes it). The target system holds that secret
and nothing else — no ops credential. Unsigned, stale (more than five minutes off) or forged
is `401`; mapping the surface with no signing type refuses to start (GP1704 at build time).
A callback that arrives before its acceptance is visible is parked (`202`) and settles when
the acceptance lands. An accepted item holds an in-flight slot until its answer or its
deadline, so size `MaxInFlight` for the acceptances a campaign keeps outstanding — or give the
acceptances a ceiling of their own with `MaxAwaitingAck` (R3, below).

An operator sets `Priority` (High · Normal · Low) on the policy — live, through the throttle
verb — and a contended ceiling (`GlobalTps`, a type's `MaxTps`) splits 3 : 2 : 1 by weighted
fair share, the fractional part carried from tick to tick so a Low campaign on a small
ceiling still moves; alone under its own policy a campaign runs at its own TPS whatever its
priority.

## R3: target systems as data

Which target systems exist, and which types call them, is code. How fast one may be called is a
row in `GoldpathCampaignCeilings`, which the leader seeds from code and an operator changes live:

```csharp
builder.AddGoldpathCampaign<WebApplicationBuilder, OrdersDbContext>(campaign =>
{
    campaign.AddTargetSystem("device-core", maxTps: 200);   // the seed, not the live value
    campaign.GlobalTps = 1_000;                              // also a seed now: the pacer reads the global row
    campaign.AddCampaign<DeviceTarget>("config-push", c => c.TargetSystem("device-core") /* … */);
    campaign.AddCampaign<DeviceTarget>("firmware-push", c => c.TargetSystem("device-core") /* … */);
});
```

- **One bucket per system.** Campaigns of every type that names a system share its ceiling by the same
  weighted fair share as above, with its own carried credit. The buckets nest: a type's own `.MaxTps(...)`
  binds first, then its target system's, then `GlobalTps`. A type that names no system keeps its `MaxTps`
  from code, exactly as in R2. Like the others, the bucket banks at most one tick's worth (#292).
- **Startup check.** A type naming a system that the same `configure` delegate never declares refuses at
  startup and names the declared ones. The declaration may come after the type. Names compare exactly, and
  two declarations that differ only in case are refused, because a database collation may treat them as one.
- **The rows.** At the start of every leadership slice, the leader inserts a missing row from code: one per
  system, plus the global row (`Name = "*"`; `MaxTps` null = off). A changed code value is followed only
  while nobody moved the row (`MaxTps == SeedMaxTps`). Otherwise the operator's value stays, and a warning
  names both values. A row edited between ticks bites on the next tick, because one query a tick reads them.
- **Not yet:** the audited admin verbs and routes, and the governor's editor, are R3-S9 (#318). Until then a
  row is changed in the database by an operator who has the rights to do so.
- **Seen in:** the detail's `targetSystem`, the gauge `goldpath_campaign_target_system_max_tps{target_system}`,
  `goldpath_campaign_global_tps` (now read from the row), and `target_system` as a value of
  `goldpath_campaign_binding_constraint`.

## R3: a scheduled start

A campaign may carry `StartAt` (an instant; the admin API stores and returns it in UTC):

```csharp
await admin.CreateAsync("config-push", "Monday push", parameters,
    new GoldpathCampaignThrottle(Tps: 200) { StartAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(3)) },
    tenant: null, actor: "operator", cancellationToken);
```

The snapshot is still taken when the leader adopts the campaign, so the list can be looked at
before the push. When the snapshot completes with the start still ahead, the campaign is
**`Ready`**: waiting for its start, not stuck. It releases nothing until `StartAt` passes; the
pacer then starts it (audited `start` with the actor `system`) and releases in the same tick.
Without a start, or with one that passed during the snapshot, the campaign goes straight to
`Running`, as before. `StartAt` is the earliest start, not a promise: a snapshot still running
at `StartAt` starts the campaign when it completes.

The `throttle` verb moves the start (`startAt`) or clears it (`clearStartAt`) until the campaign
has started (`Running`, or anything released); after that the move is refused. A start in the
past means now, and a start after the end date's local day is refused. `pause` accepts a
`Ready` campaign, and `resume` returns it to `Ready` while its start is still ahead. A `Ready`
campaign past its end date expires, like a running one.

### Two operators, one policy (R3.5)

Every applied throttle moves the campaign's `PolicyVersion` by one; nothing else moves it.
The detail and the list return it. A caller that read version *n* can pass it to
`ThrottleAsync(id, patch, expectedPolicyVersion: n, actor, ct)`: when somebody else's throttle
landed in between, the call is refused (`Stale = true`) in words that name the current version
and who changed it, and the refusal is audited as `throttle-refused`. A throttle without a
version (scripts, R2 clients, today's HTTP route) keeps last-writer-wins. The HTTP shape — the
version as the `ETag`, an optional `If-Match`, `412` — is admin-contract revision R4's.

## R3: a ceiling for outstanding acknowledgements

With acknowledgement deadlines in hours, `MaxInFlight` sized for the acceptances a campaign
keeps outstanding (about `Tps × ackTimeout`) no longer limits the live calls hitting the target
system. `MaxAwaitingAck` splits the two:

```csharp
// Ten calls at a time into the adapter, and up to fifty thousand devices allowed to answer later.
new GoldpathCampaignThrottle(Tps: 200, MaxInFlight: 10) { MaxAwaitingAck = 50_000 }
```

- **Null (the default) is R2's rule, line for line:** an acceptance keeps its `MaxInFlight` slot.
- **Set, `MaxInFlight` caps live work:** everything in flight except the acceptances. A retry
  waiting on its rung keeps its slot, as in R2.
- **`MaxAwaitingAck` caps the acceptances.** A release holds room under both ceilings until its
  outcome says which one it stays under, since any live call may come back accepted. So the
  pacer releases only while `MaxAwaitingAck − in flight` is positive too, and the number of
  acceptances never passes the ceiling.
- **How it is counted:** the campaign row's `AwaitingAckCount` is kept by the outcome flush,
  which counts the rows it moved into and out of `AwaitingAck`; the ack sweep goes through the
  same flush. Like every counter here it is a cache: the leader re-counts it from the items at
  the start of every leadership slice.
- **What is exact and what is not:** the acceptance ceiling is read from the watermarks
  (`ReleasedThrough − SucceededCount − FailedCount`), so it holds whatever the count says. The
  split of live work from acceptances rides the cached count. A flush's item updates and its
  counter update are separate statements, so a re-count that lands between them can be off by
  that flush's rows until the next slice, and `MaxInFlight` by as much. The pacer clamps the
  count to what is in flight, so a wrong count can never make live work look negative, and the
  detail's `AwaitingAck` and the gauge read it through the same clamp, so an operator never sees a
  negative number of outstanding acknowledgements (an upgraded row settled before the first slice's
  re-count would otherwise read `-1`).
- **Where it shows:** the detail returns `MaxAwaitingAck` and `AwaitingAck`, and the
  `goldpath_campaign_awaiting_ack` gauge is tagged like `goldpath_campaign_in_flight`.
  `goldpath_campaign_binding_constraint` reads `awaiting_ack` when the acknowledgement room is the
  tighter of the two (a tie reads `in_flight`).
- **Refused when not positive:** a type's `DefaultPolicy(...)` with a non-positive
  `MaxAwaitingAck` throws at registration, and `create` checks the policy the campaign would run
  under — the type's default with the patch applied — so neither path can make a campaign that
  never releases.
- **Changing it:** `throttle` sets it (`maxAwaitingAck`, positive; omitted keeps it) or clears it
  (`clearMaxAwaitingAck`, back to R2's rule), audited old → new. The console's governor shows the
  live calls against `MaxInFlight` and the acknowledgements, with everything in flight, against
  `MaxAwaitingAck`, and edits the ceiling (`max awaiting ack`; `clear max awaiting ack` while one is set).
- **Proven on a real broker:** `CampaignAwaitingAckProof` (RabbitMQ + PostgreSQL, a target system that
  answers eight seconds after it accepts) holds live calls at or below `MaxInFlight = 4` while ten times as
  many acknowledgements are outstanding, and every item completes exactly once.

## Auto-suspend on a failure ratio (R3.6)

A campaign may stop itself when its target system starts failing, before the operator notices:

```csharp
new GoldpathCampaignThrottle(Tps: 200)
{
    SuspendOnFailureRatio = 0.3,   // in (0, 1]; null (the default) = never
    SuspendWindow = 1_000,         // outcomes per window (the default)
    SuspendMinSample = 100,        // outcomes before the ratio may judge (the default)
}
```

Every completed attempt is an outcome and every failed one a failure, **retryable failures
included** (a melting target system shows up as timeouts first), as are an `ACK_TIMEOUT` and an
interrupted claim; an acceptance is not an outcome until it settles. The two counters ride the
sink's campaign update, so they cost no write. Each leader tick the pacer reads them off the row it
loaded: at least `SuspendMinSample` outcomes at or above the ratio moves the campaign to
**`Suspended`**; otherwise, once the window holds `SuspendWindow` outcomes, the window restarts (by
subtracting what was read, so an outcome flushed in between is kept). Detection lands one tick after
the flush that crosses the threshold, and the ratio covers the outcomes since the window last
restarted: a tumbling window evaluated early, not an exact "last N".

`Suspended` is not `Paused`: the system stopped it, and the detail says why in
`suspendedReason` — *failure ratio 0.52 ≥ 0.3 over 400 outcomes since 2026-11-02T07:42:05Z
(minimum sample 100); most frequent code TIMEOUT (61%)*, the code read from a sample of at most 500 of
the window's failures. The flip is audited `suspend` with the actor `system` and counted in
`goldpath_campaign_suspended_total{type}`. A suspended campaign claims nothing and releases nothing; what
is in flight drains, and its retries wait on the ladder for the resume. `resume` clears the reason and
restarts the window, so the failures that suspended it cannot suspend it again; `abort` and `throttle`
accept it (lower the rate, then resume); `pause` refuses it. There is no automatic resume.
`failureRatioNow` on the detail shows the current window's ratio whether or not a ratio is set, and
switching a ratio on through `throttle` restarts the window, so outcomes from before it existed do not
judge the campaign. A ratio outside (0, 1], a non-positive window or sample, or a sample larger than
the window (which could never suspend) is refused on create and on throttle.

## The guarantees

- **The ceiling is mandatory:** a type without `MaxTargets` refuses to bake (GP1701);
  an enumeration that exceeds it PAUSES the campaign for a human decision.
- **Policy is live:** TPS, daily quota, send window (timezone-aware, overnight windows
  supported) and max-in-flight are row values the pacer re-reads every tick — a throttle
  takes effect within one tick, no restart, no redeploy.
- **Double-send is structurally impossible:** the consumer CLAIMS the item row
  (state-guarded update) before the handler's external call; a broker redelivery claims
  zero rows and drops. Claimed-but-unstamped items are swept to the repair queue —
  confirm with the provider, then replay; never silently re-send.
- **30M items never mean 30M writes:** outcomes are published, batched by the sink, and
  flushed as set-based updates; enumeration and release are batched the same way.
- **Repair, not requeue:** every failed item lands in the jobs repair queue with its
  coordinate (`{campaign}#{seq}`); `replay-items` re-executes through your handler.
- **Takeover-safe:** enumeration and release advance durable watermarks; a new leader
  resumes exactly where the dead one stopped — after the last KEY for a keyset type, by
  count otherwise.
- **An acceptance is not a success:** an item a target system accepted waits in
  `AwaitingAck` until its callback or its deadline; a missed deadline is a retryable
  failure, and the callback surface is idempotent (a repeated webhook publishes nothing).
  The sweep reaches every campaign with work in flight — paused, aborted, finished — so
  nothing accepted waits forever.
- **A callback is signed or it is nothing:** HMAC with the type's secret, a timestamp
  window against replay, two secrets during a rotation; there is no unsigned path.
- **Pause means now:** a message that reaches a consumer after the pause claims nothing;
  a release the broker lost is published again after `OrphanReleaseAfter`, and resume
  brings every unclaimed release back on the next tick.

Ops surface (create/pause/resume/abort/throttle admin API, per-campaign panel, runbooks)
ships in S2; run views live in the JOBS console today — the pacer IS a jobs run.
