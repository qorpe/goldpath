using System.Collections.Concurrent;
using System.Collections.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl;

namespace Goldpath;

/// <summary>One discovered fleet: a worker kind's cluster as the store sees it.</summary>
public sealed record GoldpathFleetInfo(string SchedulerName, int JobCount, IReadOnlyList<GoldpathFleetNode> Nodes);

/// <summary>One cluster member's heartbeat state.</summary>
public sealed record GoldpathFleetNode(string InstanceName, DateTimeOffset LastCheckin, TimeSpan CheckinInterval);

/// <summary>The management seam: fleets discovered from the store, verbs through Quartz.</summary>
public interface IGoldpathJobsFleetRegistry
{
    /// <summary>Every fleet in the store — deploying a new worker kind just APPEARS here (D9).</summary>
    Task<IReadOnlyList<GoldpathFleetInfo>> GetFleetsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A management-only Quartz scheduler bound to the fleet's store data (never started,
    /// thread pool 1-but-idle: it can verb everything, it executes nothing).
    /// </summary>
    Task<IScheduler> GetSchedulerAsync(string schedulerName, CancellationToken cancellationToken);
}

/// <summary>
/// Store-driven fleet discovery (jobs RFC D9): scheduler names come from the Quartz tables
/// the executors already maintain; management schedulers are materialized ON DEMAND per
/// fleet and cached. Nothing is configured twice — the store is the registry.
/// </summary>
public sealed class GoldpathJobsFleetRegistry<TContext> : IGoldpathJobsFleetRegistry
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _connectionString;
    private readonly GoldpathJobStoreProvider _provider;
    private readonly ConcurrentDictionary<string, Task<IScheduler>> _schedulers = new(StringComparer.Ordinal);

    /// <summary>Created by <c>AddGoldpathJobsManagement</c>.</summary>
    public GoldpathJobsFleetRegistry(IServiceScopeFactory scopeFactory, string connectionString, GoldpathJobStoreProvider provider)
    {
        _scopeFactory = scopeFactory;
        _connectionString = connectionString;
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathFleetInfo>> GetFleetsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        var jobCounts = await db.Set<QrtzJobDetail>().AsNoTracking()
            .GroupBy(j => j.SchedName)
            .Select(g => new { Scheduler = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var nodes = await db.Set<QrtzSchedulerState>().AsNoTracking().ToListAsync(cancellationToken);

        return jobCounts.Select(f => new GoldpathFleetInfo(
                f.Scheduler,
                f.Count,
                nodes.Where(n => n.SchedName == f.Scheduler)
                    .Select(n => new GoldpathFleetNode(
                        n.InstanceName,
                        // Quartz's ADO store persists check-in times as .NET TICKS.
                        new DateTimeOffset(n.LastCheckinTime, TimeSpan.Zero),
                        TimeSpan.FromMilliseconds(n.CheckinInterval)))
                    .ToList()))
            .OrderBy(f => f.SchedulerName, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IScheduler> GetSchedulerAsync(string schedulerName, CancellationToken cancellationToken)
    {
        var task = _schedulers.GetOrAdd(schedulerName, CreateSchedulerAsync);
        try
        {
            return await task;
        }
        catch
        {
            // A faulted creation (DB down at the FIRST admin call) must not poison the
            // cache until restart (audit A7) — evict so the next call retries fresh.
            _schedulers.TryRemove(new KeyValuePair<string, Task<IScheduler>>(schedulerName, task));
            throw;
        }
    }

    private async Task<IScheduler> CreateSchedulerAsync(string schedulerName)
    {
        // A NEVER-STARTED scheduler: store verbs (trigger/pause/reschedule/calendar CRUD)
        // are persistent-store writes the executing cluster picks up on its next poll;
        // starting would make this member compete for fires, which is exactly what a
        // management head must not do.
        var properties = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = schedulerName,
            ["quartz.scheduler.instanceId"] = "AUTO",
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.driverDelegateType"] = QuartzStoreProviders.StoreOf(_provider).DriverDelegateType,
            ["quartz.jobStore.tablePrefix"] = "qrtz_",
            ["quartz.jobStore.useProperties"] = "true",
            ["quartz.jobStore.clustered"] = "true",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.dataSource.default.connectionString"] = _connectionString,
            ["quartz.dataSource.default.provider"] = QuartzStoreProviders.StoreOf(_provider).DataSourceProvider,
            ["quartz.serializer.type"] = "stj",
        };

        var factory = new StdSchedulerFactory(properties);
        return await factory.GetScheduler();
    }
}

/// <summary>The Quartz ADO settings each store provider runs on.</summary>
internal static class QuartzStoreProviders
{
    /// <summary>
    /// Quartz's driver delegate and ADO provider name for a store: the same pair <c>UsePostgres</c>,
    /// <c>UseSqlServer</c> and <c>UseOracle</c> set on the executing members, so a management head reads the
    /// store the way the cluster writes it. An unknown provider is refused, never read as PostgreSQL.
    /// </summary>
    internal static (string DriverDelegateType, string DataSourceProvider) StoreOf(GoldpathJobStoreProvider provider) => provider switch
    {
        GoldpathJobStoreProvider.Postgres => ("Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz", "Npgsql"),
        GoldpathJobStoreProvider.SqlServer => ("Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz", "SqlServer"),
        GoldpathJobStoreProvider.Oracle => ("Quartz.Impl.AdoJobStore.OracleDelegate, Quartz", "OracleODPManaged"),
        _ => throw new InvalidOperationException(
            $"Goldpath:Jobs:Provider '{provider}' is not a store this module knows (Postgres, SqlServer, Oracle)."),
    };
}
