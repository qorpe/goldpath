using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// #539: Bulk on a MULTI-TENANT app, on real PostgreSQL with the real jobs runner. A batch uploaded under a tenant
/// executes every row with that tenant as the ambient tenant, around the row handler AND around the chunk's save, so
/// the tenant-owned rows a handler adds are stored under the batch's tenant; a handler that throws is recorded on its
/// own row with its own message (never "interrupted"), and what it added before throwing is not saved; a replayed
/// repair item runs under the batch's tenant too. The handler here adds and does NOT save, as an adopter's handler
/// does: the chunk's save is the one the multi-tenancy write guard checks.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class BulkTenantTests : IAsyncLifetime
{
    private const string Tenant = "t1";

    public sealed class InstructionRow
    {
        public string EndToEndId { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>A tenant-owned entity the handler adds: what the downstream "payment" leaves behind.</summary>
    public sealed class PaymentInstruction : IMultiTenant
    {
        public Guid Id { get; set; }
        public TenantId TenantId { get; set; }
        public string EndToEndId { get; set; } = "";
        public int RowNumber { get; set; }
    }

    public sealed class TenantBulkDb(DbContextOptions<TenantBulkDb> options) : DbContext(options)
    {
        public DbSet<PaymentInstruction> Instructions => Set<PaymentInstruction>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyGoldpathMultiTenancy(this);
            modelBuilder.AddGoldpathBulk();
            modelBuilder.AddGoldpathJobs();
        }
    }

    /// <summary>Adds the instruction and leaves the save to the engine; a "FAIL" row throws AFTER it added.</summary>
    public sealed class InstructionHandler(TenantBulkDb db) : IGoldpathBulkRowHandler<InstructionRow>
    {
        public Task ExecuteAsync(InstructionRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken)
        {
            db.Instructions.Add(new PaymentInstruction { Id = Guid.NewGuid(), EndToEndId = row.EndToEndId, RowNumber = context.RowNumber });
            if (row.Note == "FAIL")
            {
                throw new InvalidOperationException("core banking refused the instruction");
            }

            return Task.CompletedTask;
        }
    }

    private readonly SuiteStore _store = SuiteStore.PostgreSql();
    private readonly string _fleet = $"bulkt-{Guid.NewGuid():N}"[..16];
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _store.StartAsync<TenantBulkDb>(options => new(options));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:bulkdb"] = _store.ConnectionString;
        builder.AddGoldpathMultiTenancy();
        builder.AddGoldpathData<HostApplicationBuilder, TenantBulkDb>(_store.Use);   // the interceptors: the tenant stamp and the write guard
        builder.Services.AddScoped<IGoldpathBulkRowHandler<InstructionRow>, InstructionHandler>();
        builder.AddGoldpathBulk<HostApplicationBuilder, TenantBulkDb>(bulk =>
        {
            bulk.ChunkSize = 10;   // both rows in ONE chunk: a failing row must not take its neighbour down
            bulk.AddBatch<InstructionRow>("instructions", b => b.MaxRows(1_000).RowKey(r => r.EndToEndId));
        });
        builder.AddGoldpathJobs<HostApplicationBuilder, TenantBulkDb>(jobs =>
        {
            jobs.ConnectionName = "bulkdb";
            jobs.Provider = _store.JobsProvider;
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathBulkJobs<TenantBulkDb>(validateCron: "0 0 0 1 1 ? 2099", executeCron: "0 0 0 1 1 ? 2099");
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();
        await _store.StopAsync();
    }

    private GoldpathJobsAdminService<TenantBulkDb> Admin => _host.Services.GetRequiredService<GoldpathJobsAdminService<TenantBulkDb>>();

    private GoldpathBulkAdminService<TenantBulkDb> BulkAdmin => _host.Services.GetRequiredService<GoldpathBulkAdminService<TenantBulkDb>>();

    private T Query<T>(Func<TenantBulkDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        using var bypass = GoldpathTenant.Bypass();   // every tenant's rows, and the tenant-less ones
        return query(scope.ServiceProvider.GetRequiredService<TenantBulkDb>());
    }

    private static MemoryStream Csv(params string[] notes)
    {
        var text = new StringBuilder("EndToEndId,Amount,Note\n");
        for (var i = 0; i < notes.Length; i++)
        {
            text.Append($"E{i + 1},{(i + 1) * 10},{notes[i]}\n");
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private async Task<GoldpathBulkBatch> RunAsync(MemoryStream csv, GoldpathBulkBatchState expected, CancellationToken token)
    {
        while ((await Admin.GetJobsAsync(_fleet, token)).Count < 2)
        {
            await Task.Delay(250, token);
        }

        var batch = await BulkAdmin.UploadAsync("instructions", csv, "instructions.csv", Tenant, "it-operator", token);
        await WaitForStateAsync(batch.Id, GoldpathBulkBatchState.Validated, token);
        Assert.True((await BulkAdmin.ApproveAsync(batch.Id, "treasurer", "four-eyes done", token)).Ok);
        Assert.True((await Admin.TriggerAsync(_fleet, "GoldpathBulkExecuteJob`1", dryRun: false, "it-operator", token)).Ok);
        return await WaitForStateAsync(batch.Id, expected, token);
    }

    /// <summary>Waits for <paramref name="state"/>; a batch that ends in the other terminal state fails at once, with why.</summary>
    private async Task<GoldpathBulkBatch> WaitForStateAsync(Guid batchId, GoldpathBulkBatchState state, CancellationToken token, bool failOnOtherEnd = true)
    {
        while (true)
        {
            var batch = Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batchId));
            if (batch.State == state)
            {
                return batch;
            }

            if (failOnOtherEnd && batch.State is GoldpathBulkBatchState.Completed or GoldpathBulkBatchState.CompletedWithFailures)
            {
                var run = batch.RunId is { } id ? await Admin.GetRunAsync(id, token) : null;
                Assert.Fail($"the batch ended {batch.State} ({batch.ExecutedRows} executed, {batch.FailedRows} failed), expected {state}: "
                    + string.Join(" | ", run?.OpenFailures.Select(f => f.Reason) ?? [])
                    + " || chunks: " + string.Join(" | ", Query(db => db.Set<GoldpathJobRunChunk>().AsNoTracking().Where(c => c.LastError != null).Select(c => c.LastError).ToList())));
            }

            await Task.Delay(250, token);
        }
    }

    [Fact]
    public async Task A_batch_uploaded_under_a_tenant_executes_every_row_as_that_tenant()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var done = await RunAsync(Csv("", ""), GoldpathBulkBatchState.Completed, timeout.Token);

        Assert.Equal(2, done.ExecutedRows);
        Assert.Equal(0, done.FailedRows);
        var stored = Query(db => db.Instructions.AsNoTracking().OrderBy(i => i.RowNumber).ToList());
        Assert.Equal(["E1", "E2"], stored.Select(i => i.EndToEndId));
        Assert.All(stored, i => Assert.Equal(TenantId.Create(Tenant), i.TenantId));
    }

    [Fact]
    public async Task A_row_whose_handler_throws_is_recorded_on_that_row_with_its_message_and_leaves_nothing_behind()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var partial = await RunAsync(Csv("", "FAIL"), GoldpathBulkBatchState.CompletedWithFailures, timeout.Token);

        Assert.Equal(1, partial.ExecutedRows);
        Assert.Equal(1, partial.FailedRows);
        var failure = Assert.Single((await Admin.GetRunAsync(partial.RunId!.Value, timeout.Token))!.OpenFailures);
        Assert.Contains("core banking refused the instruction", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("interrupted", failure.Reason, StringComparison.Ordinal);

        // Row 1 is stored as the tenant; what row 2's handler added before it threw is not.
        var stored = Assert.Single(Query(db => db.Instructions.AsNoTracking().ToList()));
        Assert.Equal(("E1", TenantId.Create(Tenant)), (stored.EndToEndId, stored.TenantId));
    }

    [Fact]
    public async Task A_replayed_repair_item_runs_under_its_batchs_tenant()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var partial = await RunAsync(Csv("", "FAIL"), GoldpathBulkBatchState.CompletedWithFailures, timeout.Token);

        Query(db =>
        {
            foreach (var row in db.Set<GoldpathBulkRow>().Where(r => r.BatchId == partial.Id && r.FailedAt != null))
            {
                row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
            }

            return db.SaveChanges();
        });
        Assert.True((await Admin.ReplayItemsAsync(partial.RunId!.Value, "it-operator", timeout.Token)).Ok);

        // The batch starts this wait CompletedWithFailures: only Completed (or the timeout) ends it.
        var healed = await WaitForStateAsync(partial.Id, GoldpathBulkBatchState.Completed, timeout.Token, failOnOtherEnd: false);
        Assert.Equal(2, healed.ExecutedRows);
        var stored = Query(db => db.Instructions.AsNoTracking().OrderBy(i => i.RowNumber).ToList());
        Assert.Equal(["E1", "E2"], stored.Select(i => i.EndToEndId));
        Assert.All(stored, i => Assert.Equal(TenantId.Create(Tenant), i.TenantId));
    }
}
