using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The bus conformance suite — the facts a bus must hold for Goldpath, proven on real
/// RabbitMQ + PostgreSQL THROUGH THE SEAM (ADR-0013 §3; messaging-exit RFC §7.1 S2). The
/// oracle is MassTransit 8.5.11; a second engine must pass every fact here unchanged before
/// it can become the default (S3 is the same list run on both).
/// </summary>
[Collection("conformance")]
public sealed class BusConformanceTests(ConformanceContainers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S2_1_exactly_once_the_same_message_delivered_twice_handles_once()
    {
        var observed = new Observed();
        using var host = await containers.BuildHostAsync(observed);
        await host.StartAsync();
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // Two deliveries with ONE message id — what a re-delivering broker or a retrying
        // publisher produces. The inbox dedups on the id the context exposes.
        await host.PublishCommittedRawAsync(bus => bus.Publish(new Ping { Id = id, Payload = "once" }, ctx => ctx.MessageId = messageId));
        await host.PublishCommittedRawAsync(bus => bus.Publish(new Ping { Id = id, Payload = "once" }, ctx => ctx.MessageId = messageId));

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
        using var host = await containers.BuildHostAsync(observed, tenant: "acme");
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
        using var host = await containers.BuildHostAsync(observed);
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
        using var host = await containers.BuildHostAsync(observed);
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
        using var host = await containers.BuildHostAsync(observed);
        await host.StartAsync();
        var wide = Guid.NewGuid();
        var narrow = Guid.NewGuid();

        // A producer on a NEWER contract adds a field; an older one omits Payload. Both must
        // reach a handler compiled against today's shape — versioning by tolerance.
        await host.PublishCommittedRawAsync(bus => bus.Publish<Ping>(new { Id = wide, Payload = "wide", Extra = "ignored" }));
        await host.PublishCommittedRawAsync(bus => bus.Publish<Ping>(new { Id = narrow }));

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
        using var host = await containers.BuildHostAsync(observed);
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
        using var host = await containers.BuildHostAsync(observed);
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
        // Slow handlers and more messages than the prefetch window: a graceful stop lets the
        // in-flight ones finish and leaves the rest on the queue for the next process.
        var observed = new Observed { HandlerDelay = TimeSpan.FromSeconds(2) };
        using (var first = await containers.BuildHostAsync(observed))
        {
            await first.StartAsync();
            for (var i = 0; i < 40; i++)
            {
                await first.PublishCommittedAsync(new Ping { Id = Guid.NewGuid(), Payload = $"m{i}" });
            }

            await observed.WaitForPingsAsync(1, Wait);
            await first.StopAsync();   // graceful: in-flight handlers finish, the rest stay queued
        }

        var afterFirst = observed.PingCount;
        Assert.InRange(afterFirst, 1, 39);

        using var second = await containers.BuildHostAsync(observed);
        await second.StartAsync();
        await observed.WaitForPingsAsync(40, TimeSpan.FromSeconds(120));
        Assert.Equal(40, observed.PingCount);
        Assert.Equal(40, observed.Pings.Select(p => p.Event.Id).Distinct().Count());   // no duplicates across the two lifetimes
        await second.StopAsync();
    }
}

[CollectionDefinition("conformance")]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceContainers>;
