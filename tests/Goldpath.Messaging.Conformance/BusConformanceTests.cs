using System.Diagnostics;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The bus conformance suite — the facts a bus must hold for Goldpath, proven on real
/// RabbitMQ + PostgreSQL THROUGH THE SEAM (ADR-0013 §3; messaging-exit RFC §7.1 S2). The
/// oracle is MassTransit 8.5.12; a second engine must pass every fact here unchanged before
/// it can become the default (S3 is the same list run on both). The class runs once per
/// engine: each concrete class below names one by its manifest value, nothing more.
/// </summary>
public abstract class BusConformanceTests(ConformanceContainers containers, string engine)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S2_1_exactly_once_the_same_message_delivered_twice_handles_once()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        await host.StartAsync();
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // Two deliveries with ONE message id — what a re-delivering broker or a retrying
        // publisher produces. The inbox dedups on the id the context exposes.
        await host.PublishAsync(new Ping { Id = id, Payload = "once" }, message => message.MessageId = messageId);
        await host.PublishAsync(new Ping { Id = id, Payload = "once" }, message => message.MessageId = messageId);

        await observed.WaitForPingsAsync(1, Wait);
        await Task.Delay(TimeSpan.FromSeconds(3));   // grace for a wrong second delivery
        var only = Assert.Single(observed.Pings);
        Assert.Equal(messageId, only.Context.MessageId);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_2_tenant_and_correlation_cross_the_broker_into_the_context()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed, tenant: "acme");
        await host.StartAsync();

        using (var activity = new ActivitySource("conformance").StartActivity("request"))
        {
            activity!.SetTag("goldpath.correlation_id", "corr-42");
            await host.PublishCommittedAsync(new Ping { Id = Guid.NewGuid(), Payload = "headers" });
        }

        await observed.WaitForPingsAsync(1, Wait);
        var (_, context) = Assert.Single(observed.Pings);
        Assert.Equal("acme", context.Tenant!.Value.Value);
        Assert.Equal("corr-42", context.CorrelationId);
        Assert.Equal("acme", context.Headers[GoldpathHeaders.TenantId]);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_3_a_poison_message_walks_the_retry_ladder_then_faults_out()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        await host.StartAsync();

        await host.PublishCommittedAsync(new Poison { Id = Guid.NewGuid() });

        // initial + ImmediateCount retries, each told which attempt it is...
        var deadline = DateTime.UtcNow + Wait;
        while (observed.PoisonAttempts.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal([0, 1, 2], observed.PoisonAttempts.Order());

        // ...then the ERROR QUEUE — the operator's fact (runbook: faults land in <queue>_error),
        // read from the broker itself rather than from an engine's fault message.
        var depth = 0;
        deadline = DateTime.UtcNow + Wait;
        while (depth == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            depth = await containers.QueueDepthAsync("poison_error");
        }

        Assert.Equal(1, depth);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_4_a_large_payload_round_trips_intact()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        await host.StartAsync();
        var payload = new string('x', 1024 * 1024);   // 1 MiB — a bordereau, not a ping

        await host.PublishCommittedAsync(new Ping { Id = Guid.NewGuid(), Payload = payload });

        await observed.WaitForPingsAsync(1, Wait);
        Assert.Equal(payload.Length, Assert.Single(observed.Pings).Event.Payload.Length);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_5_a_wider_message_is_tolerated_and_a_narrower_one_defaults()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        await host.StartAsync();
        var wide = Guid.NewGuid();
        var narrow = Guid.NewGuid();

        // A producer on a NEWER contract adds a field; an older one omits Payload. Both must
        // reach a handler compiled against today's shape — versioning by tolerance.
        await host.PublishAsAsync<Ping>(new { Id = wide, Payload = "wide", Extra = "ignored" });
        await host.PublishAsAsync<Ping>(new { Id = narrow });

        await observed.WaitForPingsAsync(2, Wait);
        Assert.Equal("wide", observed.Pings.Single(p => p.Event.Id == wide).Event.Payload);
        Assert.True(string.IsNullOrEmpty(observed.Pings.Single(p => p.Event.Id == narrow).Event.Payload));
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_6_a_commit_before_the_bus_exists_is_delivered_once_the_bus_starts()
    {
        // The crash between commit and publish: the outbox row IS the publish. A process
        // that dies after the commit hands the row to the next process; here the "next
        // process" is the same host, started only after the commit.
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        var id = Guid.NewGuid();

        await host.PublishCommittedAsync(new Ping { Id = id, Payload = "after-crash" });   // no bus running yet
        Assert.Empty(observed.Pings);

        await host.StartAsync();
        await observed.WaitForPingsAsync(1, Wait);
        Assert.Equal(id, Assert.Single(observed.Pings).Event.Id);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_7_a_broker_outage_delays_delivery_and_loses_nothing()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(engine, observed);
        await host.StartAsync();
        var id = Guid.NewGuid();

        await containers.Rabbit.PauseAsync();
        try
        {
            // The publish still commits: the outbox is the buffer, the app never blocks on the broker.
            await host.PublishCommittedAsync(new Ping { Id = id, Payload = "during-outage" });
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Empty(observed.Pings);
        }
        finally
        {
            await containers.Rabbit.UnpauseAsync();
        }

        await observed.WaitForPingsAsync(1, TimeSpan.FromSeconds(90));   // reconnect + delivery loop
        Assert.Equal(id, Assert.Single(observed.Pings).Event.Id);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_8_a_graceful_stop_mid_stream_drains_across_processes_without_duplicates()
    {
        // A graceful stop lets the in-flight handlers finish and leaves the rest on the queue
        // for the next process. That needs MORE work queued than the engine holds in flight,
        // and the window is the engine's business — the oracle's grows with the machine's
        // cores (measured: 16 at 1 or 4 cores, 20 at 10, 32 at 16), another engine's may not.
        // So the window is MEASURED — handlers wait at a gate, the count that entered settles
        // at the window — and the backlog is grown until at least twice the window waits
        // behind it. A fixed 40 let a 10-core machine finish everything before the stop.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new Observed { HandlerGate = gate.Task, HandlerDelay = TimeSpan.FromSeconds(1) };
        var published = 0;
        int window;
        await using (var first = await containers.BuildHostAsync(engine, observed))
        {
            await first.StartAsync();
            var target = 40;
            while (true)
            {
                for (; published < target; published++)
                {
                    await first.PublishCommittedAsync(new Ping { Id = Guid.NewGuid(), Payload = $"m{published}" });
                }

                window = await observed.WaitForStartedToSettleAsync(TimeSpan.FromSeconds(2), Wait);
                if (window > 0 && published >= 3 * window)
                {
                    break;
                }

                target = Math.Max(published * 2, 3 * window);
                Assert.True(target <= 5_000, $"the engine took {window} of {published} messages in flight and kept going — no window to measure");
            }

            // Stop first, then open the gate: the in-flight handlers finish during the stop.
            // Each still takes a second after the gate, so an engine slow to stop consuming
            // starts at most one more wave — and two windows are still short of the backlog.
            var stopping = first.StopAsync();
            gate.SetResult();
            await stopping;
        }

        var afterFirst = observed.PingCount;
        Assert.True(afterFirst >= window, $"{afterFirst} handled before the stop, but {window} were in flight — a graceful stop finishes them");
        Assert.True(afterFirst < published, $"all {published} handled by the first process — nothing was left queued, so the handover went unproven");

        await using var second = await containers.BuildHostAsync(engine, observed);
        await second.StartAsync();
        await observed.WaitForPingsAsync(published, TimeSpan.FromSeconds(120));
        Assert.Equal(published, observed.PingCount);
        Assert.Equal(published, observed.Pings.Select(p => p.Event.Id).Distinct().Count());   // no duplicates across the two lifetimes
        await second.StopAsync();
    }
}

/// <summary>
/// The batch contract's facts (RFC goldpath-messaging-engines §4.1 row 12, §7 Part 3), once per engine.
/// </summary>
public abstract class BatchConformanceTests(ConformanceContainers containers, string engine)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // The batch contract (RFC goldpath-messaging-engines §4.1 row 12, §7 Part 3): delivery in batches
    // bounded by size OR time, and one batch in flight per endpoint when the concurrency limit is 1.
    // Campaign's outcome sink is built on all three; a second engine must hold each.

    [Fact]
    public async Task S2_9_a_batch_never_holds_more_than_its_message_limit()
    {
        var batches = new BatchesObserved();
        await using var host = await containers.BuildHostAsync(
            engine, new Observed(), batches: (batches, new GoldpathBatchOptions { MessageLimit = 5, TimeLimit = TimeSpan.FromSeconds(2) }));
        await host.StartAsync();

        for (var i = 0; i < 23; i++)
        {
            await host.PublishAsync(new Tick { Number = i });
        }

        await batches.WaitForDeliveredAsync(23, Wait);
        var sizes = batches.Batches.Select(b => b.Size).ToList();
        Assert.Equal(23, batches.Delivered);
        Assert.Equal(23, batches.Batches.SelectMany(b => b.Numbers).Distinct().Count());
        Assert.All(sizes, size => Assert.InRange(size, 1, 5));
        Assert.Contains(5, sizes);   // the limit is reached, not merely never exceeded: 23 queued deliveries fill a batch
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_10_a_partial_batch_is_delivered_once_its_time_limit_passes()
    {
        var batches = new BatchesObserved();
        await using var host = await containers.BuildHostAsync(
            engine, new Observed(), batches: (batches, new GoldpathBatchOptions { MessageLimit = 100, TimeLimit = TimeSpan.FromMilliseconds(500) }));
        await host.StartAsync();

        var published = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            await host.PublishAsync(new Tick { Number = i });
        }

        // Three deliveries never fill a batch of a hundred; only the time limit can release them.
        await batches.WaitForDeliveredAsync(3, Wait);
        Assert.Equal(3, batches.Delivered);
        var last = batches.Batches.Max(b => b.At);
        Assert.True(last - published < TimeSpan.FromSeconds(10), $"the partial batch waited {(last - published).TotalSeconds:F1}s for a 500 ms time limit");
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_11_with_a_concurrency_limit_of_one_batches_never_overlap()
    {
        var batches = new BatchesObserved { Hold = TimeSpan.FromMilliseconds(300) };
        await using var host = await containers.BuildHostAsync(
            engine, new Observed(), batches: (batches, new GoldpathBatchOptions { MessageLimit = 4, TimeLimit = TimeSpan.FromMilliseconds(100), ConcurrencyLimit = 1 }));
        await host.StartAsync();

        for (var i = 0; i < 40; i++)
        {
            await host.PublishAsync(new Tick { Number = i });
        }

        await batches.WaitForDeliveredAsync(40, Wait);
        Assert.Equal(40, batches.Delivered);
        Assert.True(batches.Batches.Count >= 10, $"40 deliveries at four a batch took {batches.Batches.Count} batches");   // enough batches that overlap had every chance
        Assert.Equal(1, batches.Peak);
        await host.StopAsync();
    }

    [Fact]
    public async Task S2_12_what_a_batch_handler_publishes_is_delivered()
    {
        // The handler publishes through the seam and saves nothing: with an outbox composed, committing what it
        // published is the engine's work, as it is for a single-message handler. A publish lost here is an
        // acknowledged delivery whose consequence never leaves the process.
        var observed = new Observed();
        var batches = new BatchesObserved { PublishFromBatch = true };
        await using var host = await containers.BuildHostAsync(
            engine, observed, batches: (batches, new GoldpathBatchOptions { MessageLimit = 5, TimeLimit = TimeSpan.FromMilliseconds(500) }));
        await host.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            await host.PublishAsync(new Tick { Number = i });
        }

        await batches.WaitForDeliveredAsync(3, Wait);
        Assert.Equal(3, batches.Delivered);
        await observed.WaitForPingsAsync(batches.Batches.Count, Wait);
        Assert.Equal(batches.Batches.Count, observed.PingCount);
        Assert.All(observed.Pings, p => Assert.Equal("from-batch", p.Event.Payload));
        await host.StopAsync();
    }
}

/// <summary>The suite on the oracle, MassTransit 8.5.12.</summary>
[Collection("conformance")]
public sealed class MassTransitBusConformance(ConformanceContainers containers) : BusConformanceTests(containers, "masstransit");

/// <summary>The suite on the Wolverine adapter (G5): the same facts, unchanged.</summary>
[Collection("conformance")]
public sealed class WolverineBusConformance(ConformanceContainers containers) : BusConformanceTests(containers, "wolverine");

/// <summary>The batch facts on the oracle.</summary>
[Collection("conformance")]
public sealed class MassTransitBatchConformance(ConformanceContainers containers) : BatchConformanceTests(containers, "masstransit");

/// <summary>The batch facts on the Wolverine adapter.</summary>
[Collection("conformance")]
public sealed class WolverineBatchConformance(ConformanceContainers containers) : BatchConformanceTests(containers, "wolverine");

[CollectionDefinition("conformance")]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceContainers>;
