using System.Diagnostics;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// S4, mixed-fleet wire compatibility (RFC goldpath-messaging-exit §7.1 S4, mandatory since 2026-10-21; RFC
/// goldpath-messaging-engines §4.4): during a rolling deployment a process on one engine and a process on the
/// other share one broker and one database, so what either one sends, commits or has already handled must mean
/// the same to the other. Each fact runs two hosts — two processes of one fleet — on the same containers, one
/// per engine, through the seam only; the class runs once per direction, so "old publisher → new consumer and
/// the reverse" is the same list read both ways.
/// </summary>
public abstract class MixedFleetTests(ConformanceContainers containers, string from, string to)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S4_1_what_one_engine_publishes_the_other_handles_with_its_tenant_correlation_and_message_id()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var observed = new Observed();

        // The consumer starts first: it declares the endpoint's queue and binds it, so the publish has somewhere to land.
        await using var consumer = await containers.BuildHostAsync(to, observed);
        await consumer.StartAsync();
        await using var publisher = await containers.BuildHostAsync(from, new Observed(), tenant: "acme", consumes: false);
        await publisher.StartAsync();

        // An application's publish: through the seam, inside a committed transaction, the tenant from the request's
        // context and the correlation from its activity — the envelope and the outbox row the publisher's engine writes.
        var committed = Guid.NewGuid();
        using (var activity = new ActivitySource("conformance").StartActivity("request"))
        {
            activity!.SetTag("goldpath.correlation_id", "corr-committed");
            await publisher.PublishCommittedAsync(new Ping { Id = committed, Payload = "committed" });
        }

        // An upstream service's publish, with the message id set: the id is the inbox's key on both sides (§4.4).
        var direct = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await publisher.PublishAsync(new Ping { Id = direct, Payload = "direct" }, message =>
        {
            message.MessageId = messageId;
            message.Tenant = "globex";
            message.CorrelationId = "corr-direct";
        });

        await WaitForAsync(observed, [committed, direct]);
        var fromCommit = Assert.Single(observed.Pings, p => p.Event.Id == committed);
        Assert.Equal("committed", fromCommit.Event.Payload);
        Assert.Equal("acme", fromCommit.Context.Tenant!.Value.Value);
        Assert.Equal("corr-committed", fromCommit.Context.CorrelationId);
        Assert.Equal("acme", fromCommit.Context.Headers[GoldpathHeaders.TenantId]);
        Assert.NotNull(fromCommit.Context.MessageId);

        var fromUpstream = Assert.Single(observed.Pings, p => p.Event.Id == direct);
        Assert.Equal("direct", fromUpstream.Event.Payload);
        Assert.Equal(messageId, fromUpstream.Context.MessageId);
        Assert.Equal("globex", fromUpstream.Context.Tenant!.Value.Value);
        Assert.Equal("corr-direct", fromUpstream.Context.CorrelationId);

        await publisher.StopAsync();
        await consumer.StopAsync();
    }

    [Fact]
    public async Task S4_2_a_message_handled_under_one_engine_and_redelivered_under_the_other_is_not_handled_again()
    {
        // The rolling deployment's duplicate: the old process handled the message and committed its inbox row, then
        // the broker (or a retrying upstream) delivers the same message id to a process already on the new engine.
        // The inbox both engines share — InboxState keyed by message id and consumer (one per handler) — must recognise it.
        var observed = new Observed();
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var before = await containers.BuildHostAsync(from, observed))
        {
            await before.StartAsync();
            await before.PublishAsync(new Ping { Id = id, Payload = "first" }, message => message.MessageId = messageId);
            await WaitForAsync(observed, [id]);
            await before.StopAsync();
        }

        Assert.Single(observed.Pings, p => p.Event.Id == id);

        await using var after = await containers.BuildHostAsync(to, observed);
        await after.StartAsync();
        await after.PublishAsync(new Ping { Id = id, Payload = "again" }, message => message.MessageId = messageId);

        // The redelivery must be consumed (acknowledged) without running the handler: wait for the queue to drain,
        // then a grace period for a wrong second handling.
        var deadline = DateTime.UtcNow + Wait;
        while (await containers.QueueDepthAsync("ping") > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        var only = Assert.Single(observed.Pings, p => p.Event.Id == id);
        Assert.Equal("first", only.Event.Payload);
        Assert.Equal(messageId, only.Context.MessageId);
        await after.StopAsync();
    }

    [Fact]
    public async Task S4_3_an_outbox_row_one_engine_committed_and_never_relayed_is_delivered_by_the_other()
    {
        // The process on the old engine commits and stops before its relay sends the row; the process that replaces
        // it runs the new engine. The row is the publish (§4.4: one schema on disk, relayed whichever engine wrote it).
        var observed = new Observed();
        var id = Guid.NewGuid();
        await using (var old = await containers.BuildHostAsync(from, new Observed(), tenant: "acme", consumes: false))
        {
            await old.PublishCommittedAsync(new Ping { Id = id, Payload = "left-behind" });   // its bus never starts
        }

        await using var replacement = await containers.BuildHostAsync(to, observed);
        await replacement.StartAsync();
        await WaitForAsync(observed, [id]);
        var delivered = Assert.Single(observed.Pings, p => p.Event.Id == id);
        Assert.Equal("left-behind", delivered.Event.Payload);
        Assert.Equal("acme", delivered.Context.Tenant!.Value.Value);
        await Task.Delay(TimeSpan.FromSeconds(3));   // grace for a second relay of the same row
        Assert.Single(observed.Pings, p => p.Event.Id == id);
        await replacement.StopAsync();
    }

    [Fact]
    public async Task S4_4_a_handler_the_new_release_adds_runs_for_a_redelivery_the_handler_already_there_skips()
    {
        // The inbox keeps one row per handler on both engines (§4.4): a rolling deployment that also adds a handler
        // to an endpoint must run the new handler for a message the old process handled, and only the new one.
        var observed = new Observed();
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var before = await containers.BuildHostAsync(from, observed))
        {
            await before.StartAsync();
            await before.PublishAsync(new Ping { Id = id, Payload = "first" }, message => message.MessageId = messageId);
            await WaitForAsync(observed, [id]);
            await before.StopAsync();
        }

        await using var after = await containers.BuildHostAsync(to, observed, compose: messaging =>
            messaging.AddHandler<Ping, WolverineAdapterTests.AlsoPingHandler>(GoldpathHandlerEndpoints.NameOf(typeof(PingHandler))));
        await after.StartAsync();
        await after.PublishAsync(new Ping { Id = id, Payload = "again" }, message => message.MessageId = messageId);

        var deadline = DateTime.UtcNow + Wait;
        while (!observed.AlsoPinged.Contains(id) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));   // grace for a wrong second run of either handler
        Assert.Single(observed.AlsoPinged, seen => seen == id);
        Assert.Equal("first", Assert.Single(observed.Pings, p => p.Event.Id == id).Event.Payload);
        await after.StopAsync();
    }

    [Fact]
    public async Task S4_5_a_message_one_engine_faulted_to_the_error_queue_is_handled_by_the_other_once_shovelled_back()
    {
        // The runbook's poison path across a rollout: the release with the bug faults the message into <queue>_error,
        // the fix ships on the other engine, and the operator shovels the message back. What one engine parks, the
        // other must read — the error queue is the only copy of what the producer promised.
        var queue = GoldpathHandlerEndpoints.NameOf(typeof(RepairableHandler));
        var id = Guid.NewGuid();
        await using (var broken = await containers.BuildHostAsync(from, new Observed { Broken = true }, compose: messaging =>
            messaging.AddHandler<Repairable, RepairableHandler>()))
        {
            await broken.StartAsync();
            await broken.PublishAsync(new Repairable { Id = id }, message =>
            {
                message.Tenant = "acme";
                message.CorrelationId = "corr-poison";
            });

            var deadline = DateTime.UtcNow + Wait;
            while (await containers.QueueDepthAsync($"{queue}_error") < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            await broken.StopAsync();
        }

        var fixedObserved = new Observed();
        await using var fixedHost = await containers.BuildHostAsync(to, fixedObserved, compose: messaging =>
            messaging.AddHandler<Repairable, RepairableHandler>());
        await fixedHost.StartAsync();
        Assert.True(await containers.ShovelAsync($"{queue}_error", queue) >= 1, $"nothing was parked in {queue}_error");

        var handled = DateTime.UtcNow + Wait;
        while (!fixedObserved.Repaired.Any(r => r.Id == id) && DateTime.UtcNow < handled)
        {
            await Task.Delay(100);
        }

        var (_, context) = Assert.Single(fixedObserved.Repaired, r => r.Id == id);
        Assert.Equal("acme", context.Tenant!.Value.Value);
        Assert.Equal("corr-poison", context.CorrelationId);
        // The management API's counts trail the broker by a few seconds, so the emptied error queue is waited for.
        var drained = DateTime.UtcNow + Wait;
        while (await containers.QueueDepthAsync($"{queue}_error") > 0 && DateTime.UtcNow < drained)
        {
            await Task.Delay(500);
        }

        Assert.Equal(0, await containers.QueueDepthAsync($"{queue}_error"));
        await fixedHost.StopAsync();
    }

    private static async Task WaitForAsync(Observed observed, Guid[] ids)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!ids.All(id => observed.Pings.Any(p => p.Event.Id == id)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}

/// <summary>S4 forwards: the fleet's old processes on the default engine, its new ones on Wolverine.</summary>
[Collection("conformance")]
public sealed class MassTransitToWolverineFleet(ConformanceContainers containers) : MixedFleetTests(containers, "masstransit", "wolverine");

/// <summary>S4 backwards (the rollback's wire, S6): the fleet's old processes on Wolverine, its new ones on the default engine.</summary>
[Collection("conformance")]
public sealed class WolverineToMassTransitFleet(ConformanceContainers containers) : MixedFleetTests(containers, "wolverine", "masstransit");
