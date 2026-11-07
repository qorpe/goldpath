using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// Registration coverage from the mutation-gate findings: the wiring branches the container
/// contract tests can't see (Postgres constructs lazily, so this is genuinely unit-testable;
/// Redis connects eagerly and stays container-tested).
/// </summary>
public sealed class LockingRegistrationTests
{
    [Fact]
    public void Postgres_provider_wires_through_the_metered_decorator()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:locks"] = "Host=localhost;Database=x;Username=u;Password=p";
        builder.AddGoldpathLocking(o =>
        {
            o.Provider = GoldpathLockProvider.Postgres;
            o.ConnectionName = "locks";
        });
        using var host = builder.Build();

        var provider = host.Services.GetRequiredService<IDistributedLockProvider>();
        Assert.IsType<GoldpathMeteredLockProvider>(provider);
        Assert.NotNull(provider.CreateLock(GoldpathLockNames.Global("wiring")));   // no connection until acquire
    }

    [Fact]
    public void Options_bind_from_the_manifest_section()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Goldpath:DistributedLocking:Provider"] = "Postgres";
        builder.Configuration["Goldpath:DistributedLocking:ConnectionName"] = "appdb";
        builder.Configuration["ConnectionStrings:appdb"] = "Host=localhost;Database=x;Username=u;Password=p";
        builder.AddGoldpathLocking();
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<GoldpathLockingOptions>();
        Assert.Equal(GoldpathLockProvider.Postgres, options.Provider);
        Assert.Equal("appdb", options.ConnectionName);
        Assert.NotNull(host.Services.GetRequiredService<IDistributedLockProvider>().CreateLock("x"));
    }

    [Fact]
    public void SqlServer_points_at_the_optional_package_loudly_at_registration()
    {
        var builder = Host.CreateApplicationBuilder();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathLocking(o => o.Provider = GoldpathLockProvider.SqlServer));

        Assert.Contains("Goldpath.Locking.SqlServer", ex.Message);
        Assert.Contains("AddGoldpathSqlServerLocking", ex.Message);
    }

    [Fact]
    public void Oracle_points_at_the_optional_package_loudly_at_registration()
    {
        var builder = Host.CreateApplicationBuilder();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathLocking(o => o.Provider = GoldpathLockProvider.Oracle));

        Assert.Contains("Goldpath.Locking.Oracle", ex.Message);
        Assert.Contains("AddGoldpathOracleLocking", ex.Message);
    }

    [Fact]
    public void Oracle_set_through_configuration_is_refused_the_same_way()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Goldpath:DistributedLocking:Provider"] = "Oracle";

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddGoldpathLocking());
        Assert.Contains("AddGoldpathOracleLocking", ex.Message);
    }

    [Fact]
    public void A_value_that_names_no_provider_is_refused_never_built_as_postgres()
    {
        var builder = Host.CreateApplicationBuilder();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathLocking(o => o.Provider = (GoldpathLockProvider)42));

        Assert.Contains("'42' is not a lock provider", ex.Message);
        Assert.Contains("the optional package's own registration for SqlServer or Oracle", ex.Message);   // where to go instead
    }

    [Fact]
    public void Missing_connection_string_fails_at_first_resolve_with_the_fix_in_the_message()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathLocking(o => o.Provider = GoldpathLockProvider.Postgres);   // no conn string at all
        using var host = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IDistributedLockProvider>());
        Assert.Contains("redis", ex.Message);                                 // the default connection name
        Assert.Contains("Postgres", ex.Message);
    }

    [Fact]
    public void Default_connection_name_is_the_caching_l2()
        => Assert.Equal("redis", new GoldpathLockingOptions().ConnectionName);
}
