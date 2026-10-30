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
  count to what is in flight, so a wrong count can never make live work look negative.
- **Where it shows:** the detail returns `MaxAwaitingAck` and `AwaitingAck`, and the
  `goldpath_campaign_awaiting_ack` gauge is tagged like `goldpath_campaign_in_flight`.
- **Changing it:** `throttle` sets it (`maxAwaitingAck`, positive; omitted keeps it) or clears it
  (`clearMaxAwaitingAck`, back to R2's rule), audited old → new. The console does not show or edit
  it yet (R3-S6, #316).

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
