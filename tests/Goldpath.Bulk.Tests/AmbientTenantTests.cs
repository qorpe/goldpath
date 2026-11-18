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
    private readonly BulkFixture _fixture = new();

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
                Id = Guid.NewGuid(), Definition = $"ghost-{row.EndToEndId}", FileId = template.FileId,
                State = GoldpathBulkBatchState.Rejected, ReceivedAt = template.ReceivedAt,
            };
            ghosts.Add(ghost.Id);
            db.Add(ghost);
        };

        await _fixture.ExecuteAllAsync(Guid.NewGuid());

        var saved = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking()
            .Where(b => ghosts.Contains(b.Id)).Select(b => b.Definition).OrderBy(d => d).ToList());
        Assert.Equal(["ghost-E1", "ghost-E3"], saved);
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
