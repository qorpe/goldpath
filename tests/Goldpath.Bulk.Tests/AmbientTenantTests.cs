using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// #539 at the engine's own seam: the execute run and a replay pin the batch's tenant as the AMBIENT tenant (the one a
/// multi-tenant app's stamp and write guard read) around the row handler, restore what was there afterwards, and leave
/// it alone for a batch without a tenant. A row whose handler throws leaves nothing it added for the chunk's save. The
/// multi-tenancy write guard itself is proven on PostgreSQL by <c>BulkTenantTests</c> in the integration lane.
/// </summary>
public sealed class AmbientTenantTests : IDisposable
{
    private readonly BulkFixture _fixture = new(multiTenant: true);

    public void Dispose() => _fixture.Dispose();

    private async Task<GoldpathBulkBatch> ApprovedAsync(string? tenant, params (string Id, string Iban, string Amount, string? Note)[] rows)
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(rows), tenant);
        using var scope = _fixture.Scope();
        Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", null, CancellationToken.None)).Ok);
        return batch;
    }

    [Fact]
    public async Task Every_row_handler_runs_with_the_batchs_tenant_as_the_ambient_tenant_and_the_flow_gets_its_own_back()
    {
        await ApprovedAsync("t1", ("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        var seen = new List<TenantId?>();
        _fixture.Handler.Probe = (_, _) => seen.Add(GoldpathAmbientTenant.Current);

        using (Ambient("outer"))
        {
            await _fixture.ExecuteAllAsync(Guid.NewGuid());
            Assert.Equal(TenantId.Create("outer"), GoldpathAmbientTenant.Current);   // restored, also after a failed row
        }

        Assert.Equal([TenantId.Create("t1"), TenantId.Create("t1")], seen);
    }

    [Fact]
    public async Task A_batch_without_a_tenant_leaves_the_ambient_tenant_as_it_was()
    {
        await ApprovedAsync(null, ("E1", "TR1", "10", null));
        var seen = new List<TenantId?>();
        _fixture.Handler.Probe = (_, _) => seen.Add(GoldpathAmbientTenant.Current);

        await _fixture.ExecuteAllAsync(Guid.NewGuid());
        using (Ambient("outer"))
        {
            // A replay of an executed row is a no-op; the execute run of a second tenant-less batch shows the flow's own.
            await ApprovedAsync(null, ("F1", "TR1", "10", null));
            await _fixture.ExecuteAllAsync(Guid.NewGuid());
        }

        Assert.Equal([null, TenantId.Create("outer")], seen);
    }

    [Fact]
    public async Task A_replay_runs_the_handler_with_the_batchs_tenant()
    {
        var batch = await ApprovedAsync("t1", ("E1", "TR1", "10", "FAIL"));
        var failure = (await _fixture.ExecuteAllAsync(Guid.NewGuid())).SelectMany(Failures).Single();
        _fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathBulkRow>().Single(r => r.BatchId == batch.Id);
            row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
        });
        TenantId? seen = null;
        _fixture.Handler.Probe = (_, _) => seen = GoldpathAmbientTenant.Current;

        using (var scope = _fixture.Scope())
        {
            await _fixture.Engine.ReplayRowAsync(scope.ServiceProvider, failure.ItemKey, CancellationToken.None);
        }

        Assert.Equal(TenantId.Create("t1"), seen);
        Assert.Null(GoldpathAmbientTenant.Current);
    }

    [Fact]
    public async Task What_a_failing_rows_handler_added_is_not_saved_with_the_chunk_and_its_neighbours_are()
    {
        var batch = await ApprovedAsync("t1", ("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"), ("E3", "TR3", "30", null));
        var ghosts = new List<Guid>();
        _fixture.Handler.Probe = (row, context) =>
        {
            // Every row adds an entity on the engine's own context, the way an adopter's handler adds its instruction.
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var template = db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id);
            var ghost = new GoldpathBulkBatch
            {
                Id = Guid.NewGuid(),
                Definition = $"ghost-{row.EndToEndId}",
                FileId = template.FileId,
                State = GoldpathBulkBatchState.Rejected,
                ReceivedAt = template.ReceivedAt,
            };
            ghosts.Add(ghost.Id);
            db.Add(ghost);
        };

        await _fixture.ExecuteAllAsync(Guid.NewGuid());

        var saved = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking()
            .Where(b => ghosts.Contains(b.Id)).Select(b => b.Definition).OrderBy(d => d).ToList());
        Assert.Equal(["ghost-E1", "ghost-E3"], saved);
    }

    /// <summary>
    /// The verifier's P1 on #587: a single-tenant app's batch label is free-form (AdminTenantScope passes it through), so
    /// it is never parsed as a tenant id there, and the handler sees no ambient tenant — exactly as before #539.
    /// </summary>
    [Theory]
    [InlineData("ACME")]
    [InlineData("agency_1")]
    [InlineData("Acme Corp")]
    [InlineData("agency-1")]
    public async Task A_single_tenant_apps_batch_runs_whatever_its_label_and_pins_no_ambient_tenant(string label)
    {
        using var single = new BulkFixture();
        var batch = await single.IngestValidatedAsync(BulkFixture.Csv(("E1", "TR1", "10", null)), label);
        using (var scope = single.Scope())
        {
            Assert.True((await single.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", null, CancellationToken.None)).Ok);
        }

        var seen = new List<TenantId?>();
        single.Handler.Probe = (_, _) => seen.Add(GoldpathAmbientTenant.Current);

        var failures = (await single.ExecuteAllAsync(Guid.NewGuid())).SelectMany(Failures).ToList();

        Assert.Empty(failures);
        Assert.Equal([null], seen);
        Assert.NotNull(single.Query(db => db.Set<GoldpathBulkRow>().AsNoTracking().Single(r => r.BatchId == batch.Id).ExecutedAt));
    }

    /// <summary>
    /// On a multi-tenant app a stored tenant that is no tenant id fails each row, named, in the repair queue — never an
    /// exception after the claim that files every row "interrupted mid-flight" — and its replay fails with the same reason.
    /// </summary>
    [Fact]
    public async Task A_multi_tenant_batch_whose_stored_tenant_is_no_tenant_id_fails_each_row_by_name_and_its_replay_too()
    {
        await ApprovedAsync("Acme Corp", ("E1", "TR1", "10", null), ("E2", "TR2", "20", null));
        var ran = 0;
        _fixture.Handler.Probe = (_, _) => ran++;

        var failures = (await _fixture.ExecuteAllAsync(Guid.NewGuid())).SelectMany(Failures).ToList();

        Assert.Equal(0, ran);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Contains("'Acme Corp' is not a tenant id", f.Reason, StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.Reason.Contains("interrupted", StringComparison.Ordinal));

        using var scope = _fixture.Scope();
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.Engine.ReplayRowAsync(scope.ServiceProvider, failures[0].ItemKey, CancellationToken.None));
        Assert.Contains("'Acme Corp' is not a tenant id", replay.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scope itself, synchronously (the verifier's M2 on #587: an AsyncLocal write inside the async engine never flows
    /// back to the awaiting test, so only a synchronous fact can see whether Dispose restores the previous value).
    /// </summary>
    [Fact]
    public void The_scope_pins_the_tenant_and_restores_what_was_there_nested_or_not()
    {
        using (Ambient("outer"))
        {
            using (BatchTenant.Use(TenantId.Create("t1")))
            {
                Assert.Equal(TenantId.Create("t1"), GoldpathAmbientTenant.Current);
                using (BatchTenant.Use(TenantId.Create("t2")))
                {
                    Assert.Equal(TenantId.Create("t2"), GoldpathAmbientTenant.Current);
                }

                Assert.Equal(TenantId.Create("t1"), GoldpathAmbientTenant.Current);
                using (BatchTenant.Use(null))
                {
                    Assert.Equal(TenantId.Create("t1"), GoldpathAmbientTenant.Current);   // nothing to pin: left as it was
                }

                Assert.Equal(TenantId.Create("t1"), GoldpathAmbientTenant.Current);
            }

            Assert.Equal(TenantId.Create("outer"), GoldpathAmbientTenant.Current);
        }

        Assert.Null(GoldpathAmbientTenant.Current);
    }

    [Fact]
    public void A_stored_tenant_resolves_to_a_pin_only_on_a_multi_tenant_app()
    {
        using var single = new BulkFixture();
        Assert.Equal(BatchTenant.Pin.None, BatchTenant.Resolve(single.Services, "t1"));
        Assert.Equal(BatchTenant.Pin.None, BatchTenant.Resolve(single.Services, "Acme Corp"));
        Assert.Equal(BatchTenant.Pin.None, BatchTenant.Resolve(_fixture.Services, null));
        Assert.Equal(TenantId.Create("t1"), BatchTenant.Resolve(_fixture.Services, "t1").Tenant);
        var refused = BatchTenant.Resolve(_fixture.Services, new string('x', 500));
        Assert.Null(refused.Tenant);
        Assert.True(refused.Refusal!.Length < 300, "the refusal clips what it echoes");
    }

    /// <summary>An ambient tenant for the test's own flow (what an HTTP request or a consumer would have set).</summary>
    private static Restore Ambient(string tenant)
    {
        var restore = new Restore(GoldpathAmbientTenant.Current);
        GoldpathAmbientTenant.Current = TenantId.Create(tenant);
        return restore;
    }

    private sealed class Restore(TenantId? previous) : IDisposable
    {
        public void Dispose() => GoldpathAmbientTenant.Current = previous;
    }

    private static IReadOnlyList<(string ItemKey, string Reason)> Failures(GoldpathJobChunk chunk)
        => (List<(string, string)>)typeof(GoldpathJobChunk)
            .GetProperty("ItemFailures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(chunk)!;
}
