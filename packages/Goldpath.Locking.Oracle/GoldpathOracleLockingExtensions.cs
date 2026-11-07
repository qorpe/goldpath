using Medallion.Threading;
using Medallion.Threading.Oracle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// Oracle locking registration — the optional-package counterpart of
/// <c>AddGoldpathLocking()</c> (see the package description for WHY it is separate).
/// </summary>
public static class GoldpathOracleLockingExtensions
{
    /// <summary>
    /// Adds the <c>DBMS_LOCK</c>-backed provider and the <see cref="GoldpathLockNames"/> helper. The runtime
    /// user needs <c>EXECUTE ON SYS.DBMS_LOCK</c> (the package's <c>ops/locking-oracle.md</c>).
    /// </summary>
    public static TBuilder AddGoldpathOracleLocking<TBuilder>(this TBuilder builder, Action<GoldpathLockingOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new GoldpathLockingOptions { Provider = GoldpathLockProvider.Oracle, ConnectionName = "database" };
        builder.Configuration.GetSection("Goldpath:DistributedLocking").Bind(options);
        configure?.Invoke(options);
        if (options.Provider != GoldpathLockProvider.Oracle)
        {
            // Configuration binds over the default: a Provider left in appsettings for another store would
            // otherwise describe a lock this method does not build.
            throw new InvalidOperationException(
                $"AddGoldpathOracleLocking() builds the Oracle provider, but Goldpath:DistributedLocking:Provider says '{options.Provider}' — "
                + "remove the setting, set it to Oracle, or call the registration for that provider instead.");
        }

        builder.Services.AddSingleton(options);

        var configuration = builder.Configuration;
        builder.Services.AddSingleton<IDistributedLockProvider>(_ =>
        {
            var connectionString = configuration.GetConnectionString(options.ConnectionName)
                ?? throw new InvalidOperationException(
                    $"Goldpath:DistributedLocking (Oracle) needs the '{options.ConnectionName}' connection string.");
            // Names are made safe for DBMS_LOCK's 128-byte column BEFORE the library sees them (OracleLockNames).
            return new GoldpathMeteredLockProvider(new OracleLockNames(new OracleDistributedSynchronizationProvider(connectionString)));
        });

        builder.Services.AddScoped<GoldpathLockNames>();
        return builder;
    }
}
