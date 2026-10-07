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
watermarks — leader death means the next fire takes over from the row, mid-campaign.

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

builder.AddGoldpathMessaging<WebApplicationBuilder>(bus =>    // campaign REQUIRES a broker
{
    bus.AddGoldpathCampaignConsumers<OrdersDbContext>();
});

// DbContext: modelBuilder.AddGoldpathCampaign();  modelBuilder.AddGoldpathJobs();
```

## R2: a target that answers later, a shared ceiling, a keyset takeover

```csharp
// Typed results — the R1 handler (return = success, throw = retryable) still works unchanged.
public sealed class ConfigPushHandler : IGoldpathCampaignActionHandler<DeviceTarget>
{
    public async Task<GoldpathCampaignActionResult> ExecuteAsync(DeviceTarget target, GoldpathCampaignItemContext context, CancellationToken ct)
    {
        var answer = await _core.PushAsync(target.DeviceId, idempotencyKey: context.IdempotencyKey, ct);
        return answer switch
        {
            PushAnswer.Done => GoldpathCampaignActionResult.Succeeded(),
            PushAnswer.Queued q => GoldpathCampaignActionResult.Accepted(q.CorrelationId, ackTimeout: TimeSpan.FromHours(6)),
            PushAnswer.Unsupported => GoldpathCampaignActionResult.Failed("UNSUPPORTED_DEVICE", "no management client", retryable: false),
            _ => GoldpathCampaignActionResult.Failed("CORE_TIMEOUT", "no answer in 30s", retryable: true),
        };
    }
}

app.MapGoldpathCampaignCallbacks<OrdersDbContext>();   // the target system's answer: POST {prefix}/{type}/{correlationId}

campaign.AddCampaign<DeviceTarget>("config-push", c => c
    .MaxTargets(50_000_000)
    .MaxTps(200)                                        // shared by EVERY config-push campaign — the target system's rate
    .TargetsAfter((services, parameters, afterKey) =>   // keyset: a takeover reopens AFTER the last key, no skip read
        services.GetRequiredService<OrdersDbContext>().Devices.AsNoTracking()
            .Where(d => afterKey == null || d.Id.CompareTo(afterKey) > 0)
            .OrderBy(d => d.Id)
            .Select(d => new DeviceTarget(d.Id, d.PushToken))
            .AsAsyncEnumerable(),
        target => target.DeviceId));
```

An operator sets `Priority` (High · Normal · Low) on the policy — live, through the throttle
verb — and a contended ceiling (`GlobalTps`, a type's `MaxTps`) splits 3 : 2 : 1 by weighted
fair share; alone under its own policy a campaign runs at its own TPS whatever its priority.

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
- **Pause means now:** a message that reaches a consumer after the pause claims nothing;
  a release the broker lost is published again after `OrphanReleaseAfter`, and resume
  brings every unclaimed release back on the next tick.

Ops surface (create/pause/resume/abort/throttle admin API, per-campaign panel, runbooks)
ships in S2; run views live in the JOBS console today — the pacer IS a jobs run.
