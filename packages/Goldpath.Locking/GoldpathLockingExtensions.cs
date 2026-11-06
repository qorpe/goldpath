using System.Diagnostics;
using System.Diagnostics.Metrics;
using Medallion.Threading;
using Medallion.Threading.Postgres;
using Medallion.Threading.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Goldpath;

/// <summary>Lock store (RFC decision D2) — db providers need zero new infrastructure.</summary>
public enum GoldpathLockProvider
{
    /// <summary>Redis (RedLock) — reuses the caching L2 connection by default.</summary>
    Redis,

    /// <summary>PostgreSQL advisory locks — the lock lives in the database you already run.</summary>
    Postgres,

    /// <summary>
    /// SQL Server <c>sp_getapplock</c> — ships in the OPTIONAL <c>Goldpath.Locking.SqlServer</c>
    /// package, because its dependency chain carries Microsoft's proprietary-but-free
    /// SqlClient native bits; only SQL Server choosers take that on (license-gate exception,
    /// reviewed). Use its <c>AddGoldpathSqlServerLocking()</c> instead of <c>AddGoldpathLocking()</c>.
    /// </summary>
    SqlServer,

    /// <summary>
    /// Oracle <c>DBMS_LOCK</c> — ships in the OPTIONAL <c>Goldpath.Locking.Oracle</c> package, because
    /// its dependency chain carries Oracle's ODP.NET driver under the Oracle Free Use Terms and
    /// Conditions; only Oracle choosers take that on (license-gate exception, reviewed). Use its
    /// <c>AddGoldpathOracleLocking()</c> instead of <c>AddGoldpathLocking()</c>.
    /// </summary>
    Oracle,
}

/// <summary>Tuning surface — bound from <c>Goldpath:DistributedLocking</c>.</summary>
public sealed class GoldpathLockingOptions
{
    /// <summary>Which store holds the locks.</summary>
    public GoldpathLockProvider Provider { get; set; } = GoldpathLockProvider.Redis;

    /// <summary>Connection-string name (redis default: the caching L2; db providers: the app db).</summary>
    public string ConnectionName { get; set; } = "redis";
}

/// <summary>
/// Registers Ring B distributed locking: Medallion's <see cref="IDistributedLockProvider"/>
/// (composed, not wrapped — ADR-0003) behind manifest-driven provider selection, metered
/// through a decorator over the SAME interface, plus the tenant-scoped name helper.
/// </summary>
public static class GoldpathLockingExtensions
{
    /// <summary>Adds the lock provider per manifest and the <see cref="GoldpathLockNames"/> helper.</summary>
    public static TBuilder AddGoldpathLocking<TBuilder>(this TBuilder builder, Action<GoldpathLockingOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new GoldpathLockingOptions();
        builder.Configuration.GetSection("Goldpath:DistributedLocking").Bind(options);
        configure?.Invoke(options);

        // Loud at registration, not at first use: the SqlServer and Oracle providers live in
        // optional packages so the pure-OSS dependency graph stays pure for everyone else. Any
        // value this method does not build is refused here, never mistaken for Postgres below.
        switch (options.Provider)
        {
            case GoldpathLockProvider.Redis or GoldpathLockProvider.Postgres:
                break;
            case GoldpathLockProvider.SqlServer:
                throw new InvalidOperationException(
                    "SqlServer locking ships in the optional Goldpath.Locking.SqlServer package — "
                    + "reference it and call AddGoldpathSqlServerLocking() instead of AddGoldpathLocking().");
            case GoldpathLockProvider.Oracle:
                throw new InvalidOperationException(
                    "Oracle locking ships in the optional Goldpath.Locking.Oracle package — "
                    + "reference it and call AddGoldpathOracleLocking() instead of AddGoldpathLocking().");
            default:
                throw new InvalidOperationException(
                    $"Goldpath:DistributedLocking:Provider '{options.Provider}' is not a lock provider — use Redis or Postgres here, "
                    + "or the optional package's own registration for SqlServer or Oracle.");
        }

        builder.Services.AddSingleton(options);

        var configuration = builder.Configuration;
        builder.Services.AddSingleton<IDistributedLockProvider>(_ =>
        {
            // Resolved lazily so docgen/build-time paths never need a live store.
            var connectionString = configuration.GetConnectionString(options.ConnectionName)
                ?? throw new InvalidOperationException(
                    $"Goldpath:DistributedLocking ({options.Provider}) needs the '{options.ConnectionName}' connection string.");

            IDistributedLockProvider inner = options.Provider == GoldpathLockProvider.Redis
                ? CreateRedisProvider(connectionString)
                : new PostgresDistributedSynchronizationProvider(connectionString);
            return new GoldpathMeteredLockProvider(inner);
        });

        builder.Services.AddScoped<GoldpathLockNames>();
        return builder;
    }

    // Connects EAGERLY — unit tests cannot execute this without a live Redis, so the method
    // is a named mutation-gate ignore (see stryker/Goldpath.Locking.json); behavior is covered by
    // LockContractTests against a real container.
    private static IDistributedLockProvider CreateRedisProvider(string connectionString)
        => new RedisDistributedSynchronizationProvider(
            ConnectionMultiplexer.Connect(connectionString).GetDatabase());
}

/// <summary>Module meters — flow into the Ring A OTel pipeline.</summary>
internal static class GoldpathLockingMetrics
{
    private static readonly Meter s_meter = new("Goldpath.Locking");

    public static readonly Counter<long> Acquires =
        s_meter.CreateCounter<long>("goldpath_lock_acquire_total");

    public static readonly Histogram<double> WaitSeconds =
        s_meter.CreateHistogram<double>("goldpath_lock_wait_seconds");

    public static void Record(bool acquired, long startTimestamp)
    {
        Acquires.Add(1, new KeyValuePair<string, object?>("outcome", acquired ? "acquired" : "timeout"));
        WaitSeconds.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
    }
}

/// <summary>
/// Metrics decorator over Medallion's own interface (not a new abstraction — RFC D1):
/// counts acquire outcomes and observes wait time on every path.
/// </summary>
public sealed class GoldpathMeteredLockProvider : IDistributedLockProvider
{
    private readonly IDistributedLockProvider _inner;

    /// <summary>Wraps the real provider.</summary>
    public GoldpathMeteredLockProvider(IDistributedLockProvider inner) => _inner = inner;

    /// <inheritdoc />
    public IDistributedLock CreateLock(string name) => new MeteredLock(_inner.CreateLock(name));

    private sealed class MeteredLock(IDistributedLock inner) : IDistributedLock
    {
        public string Name => inner.Name;

        public IDistributedSynchronizationHandle Acquire(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var handle = inner.Acquire(timeout, cancellationToken);
                GoldpathLockingMetrics.Record(acquired: true, start);
                return handle;
            }
            catch (TimeoutException)
            {
                GoldpathLockingMetrics.Record(acquired: false, start);
                throw;
            }
        }

        public async ValueTask<IDistributedSynchronizationHandle> AcquireAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var handle = await inner.AcquireAsync(timeout, cancellationToken).ConfigureAwait(false);
                GoldpathLockingMetrics.Record(acquired: true, start);
                return handle;
            }
            catch (TimeoutException)
            {
                GoldpathLockingMetrics.Record(acquired: false, start);
                throw;
            }
        }

        public IDistributedSynchronizationHandle? TryAcquire(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            var handle = inner.TryAcquire(timeout, cancellationToken);
            GoldpathLockingMetrics.Record(acquired: handle is not null, start);
            return handle;
        }

        public async ValueTask<IDistributedSynchronizationHandle?> TryAcquireAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            var handle = await inner.TryAcquireAsync(timeout, cancellationToken).ConfigureAwait(false);
            GoldpathLockingMetrics.Record(acquired: handle is not null, start);
            return handle;
        }
    }
}
