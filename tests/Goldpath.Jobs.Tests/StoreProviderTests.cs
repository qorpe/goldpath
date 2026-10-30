using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Jobs.Tests;

/// <summary>
/// Which Quartz store each provider runs on (G7, O-3, #406): the executing members' <c>UsePostgres</c> /
/// <c>UseSqlServer</c> / <c>UseOracle</c> and the management head's properties must name the same delegate and ADO
/// provider, and a provider this module does not know is refused rather than read as PostgreSQL.
/// </summary>
public class StoreProviderTests
{
    [Theory]
    [InlineData(GoldpathJobStoreProvider.Postgres, "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz", "Npgsql")]
    [InlineData(GoldpathJobStoreProvider.SqlServer, "Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz", "SqlServer")]
    [InlineData(GoldpathJobStoreProvider.Oracle, "Quartz.Impl.AdoJobStore.OracleDelegate, Quartz", "OracleODPManaged")]
    public void Each_provider_names_its_quartz_delegate_and_ado_provider(GoldpathJobStoreProvider provider, string driverDelegate, string adoProvider)
        => Assert.Equal((driverDelegate, adoProvider), QuartzStoreProviders.StoreOf(provider));

    [Fact]
    public void Each_named_delegate_and_ado_provider_is_one_quartz_ships()
    {
        foreach (var provider in Enum.GetValues<GoldpathJobStoreProvider>())
        {
            var (driverDelegate, adoProvider) = QuartzStoreProviders.StoreOf(provider);
            Assert.NotNull(Type.GetType(driverDelegate, throwOnError: false));
            Assert.Contains(adoProvider, new[] { "Npgsql", "SqlServer", "OracleODPManaged" });
        }
    }

    [Fact]
    public void Oracle_keeps_its_stored_value()
        => Assert.Equal(2, (int)GoldpathJobStoreProvider.Oracle);   // appended: no stored Postgres/SqlServer value moves

    [Fact]
    public void An_unknown_provider_is_refused_by_the_management_head_and_by_the_executor()
    {
        var unknown = (GoldpathJobStoreProvider)99;
        Assert.Throws<InvalidOperationException>(() => QuartzStoreProviders.StoreOf(unknown));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:jobs"] = "Host=unused";
        var refused = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathJobs<HostApplicationBuilder, JobsTestContext>(jobs => jobs.Provider = unknown));
        Assert.Contains("'99' is not a store this module knows", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_on_any_other_provider_keeps_the_lower_case_store()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        using var context = new StoreContext(new DbContextOptionsBuilder<StoreContext>().UseSqlite(connection).Options);
        var details = context.Model.GetEntityTypes().Single(e => e.ClrType == typeof(QrtzJobDetail));
        Assert.Equal("qrtz_job_details", details.GetTableName());
        Assert.Equal("is_durable", details.FindProperty(nameof(QrtzJobDetail.IsDurable))!.GetColumnName());
    }

    private sealed class StoreContext(DbContextOptions<StoreContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathJobs();
    }
}
