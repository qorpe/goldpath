using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign RFC R3.5 (#298) on a real database: the throttle is one conditional <c>ExecuteUpdate</c> per attempt, so
/// every policy column it sets has to translate on the provider — the string-stored <c>DateOnly</c>/<c>TimeOnly</c>
/// and the converted <c>DateTimeOffset</c> on Oracle included. The unit suite proves the rule on SQLite; this proves
/// the statement where adopters run it.
/// </summary>
internal static class CampaignPolicyVersionProof
{
    public static async Task RunAsync<TContext>(Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddDbContext<TContext>(configure);
        var options = new GoldpathCampaignOptions();
        await using var provider = services.BuildServiceProvider();
        var engine = new GoldpathCampaignEngine<TContext>(options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<TContext>>.Instance);
        var admin = new GoldpathCampaignAdminService<TContext>(options, engine, provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<GoldpathCampaignAdminService<TContext>>.Instance);

        var id = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            await db.Database.EnsureCreatedAsync();
            db.Set<GoldpathCampaign>().Add(new GoldpathCampaign
            {
                Id = id,
                Type = "fleet",
                Name = "policy-version",
                ParametersJson = "{}",
                Tps = 100,
                MaxInFlight = 10,
                TimeZoneId = "UTC",
                CreatedBy = "it",
                CreatedAt = DateTimeOffset.UtcNow,
                State = GoldpathCampaignState.Ready,
            });
            await db.SaveChangesAsync();
        }

        // Every column the conditional update sets, with a value that differs from the row's.
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var applied = await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(
            Tps: 40, DailyQuota: 900, MaxInFlight: 5, WindowStart: new TimeOnly(9, 0), WindowEnd: new TimeOnly(18, 30),
            TimeZoneId: "Europe/Berlin", ExcludedDays: ["Sunday"], EndDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            MaxAttempts: 3, Priority: GoldpathCampaignPriority.High)
        { StartAt = start }, expectedPolicyVersion: 0, "alice", CancellationToken.None);
        Assert.True(applied.Ok, applied.Message);
        Assert.Equal(1, applied.PolicyVersion);

        var stale = await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(Tps: 7), expectedPolicyVersion: 0, "bob", CancellationToken.None);
        Assert.False(stale.Ok);
        Assert.True(stale.Stale);
        Assert.Contains("alice", stale.Message, StringComparison.Ordinal);

        var lastWriter = await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(MaxInFlight: 6), expectedPolicyVersion: null, "carol", CancellationToken.None);
        Assert.Equal(2, lastWriter.PolicyVersion);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var row = await db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.Equal((40, 900, 6, 3, GoldpathCampaignPriority.High), (row.Tps, row.DailyQuota, row.MaxInFlight, row.MaxAttempts, row.Priority));
            Assert.Equal((new TimeOnly(9, 0), new TimeOnly(18, 30)), (row.WindowStart!.Value, row.WindowEnd!.Value));
            Assert.Equal("Europe/Berlin", row.TimeZoneId);
            Assert.Equal("Sunday", row.ExcludedDays);
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), row.EndDate);
            Assert.Equal(start.ToUnixTimeMilliseconds(), row.StartAt!.Value.ToUnixTimeMilliseconds());
            Assert.Equal(GoldpathCampaignState.Ready, row.State);   // the throttle never writes State
            Assert.Equal(2, row.PolicyVersion);
            Assert.Equal(["throttle", "throttle-refused", "throttle"], await db.Set<GoldpathCampaignAudit>().AsNoTracking()
                .Where(a => a.CampaignId == id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        }
    }
}

/// <summary>R3.5's conditional throttle on PostgreSQL (#298).</summary>
public sealed class CampaignPolicyVersionPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public Task TheConditionalThrottle_WritesEveryPolicyColumnAndTheVersion_AndRefusesAStaleOne()
        => CampaignPolicyVersionProof.RunAsync<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
}
