using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R3.6 (#299, R3-S10) on real PostgreSQL: the statements SQLite cannot vouch
/// for — the window counters riding the sink's campaign update, the suspension's conditional flip on
/// a <c>double precision</c> ratio, the bounded error-code sample, and the window restart stamping a
/// <c>timestamptz</c> (Npgsql refuses a non-zero offset). The engine and the pacer are driven
/// directly; S-OPS-03 on a real broker is R3-S11 (#319).
/// </summary>
public sealed class CampaignSuspendProof : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public Task A_crossed_ratio_suspends_with_its_reason_and_resume_restarts_the_window_on_PostgreSQL()
        => CampaignSuspendScenario<CampaignTests.CampDb>.RunAsync(
            services => services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString())));
}

/// <summary>The same R3.6 scenario on Oracle (the RFC's 19c SQL form), in the Oracle lane.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class CampaignSuspendOracleProof(OracleFixture oracle)
{
    [Fact]
    public async Task A_crossed_ratio_suspends_with_its_reason_and_resume_restarts_the_window_on_Oracle()
    {
        var connection = await oracle.CreateSchemaAsync("GP_SUSPEND");
        await CampaignSuspendScenario<OracleComposedDb>.RunAsync(services => services.AddScoped(_ => OracleComposedDb.Create(connection)));
    }
}

/// <summary>
/// One campaign through the engine, the pacer and the admin service on a real provider: ten releases,
/// five failures in one flush, a suspension on the next slice, and a resume that restarts the window.
/// </summary>
internal sealed class CampaignSuspendScenario<TContext> : IAsyncDisposable
    where TContext : DbContext
{
    private ServiceProvider _services = null!;
    private GoldpathCampaignOptions _options = null!;
    private GoldpathCampaignEngine<TContext> _engine = null!;
    private GoldpathCampaignAdminService<TContext> _admin = null!;

    public static async Task RunAsync(Action<IServiceCollection> addContext)
    {
        await using var scenario = new CampaignSuspendScenario<TContext>();
        await scenario.InitializeAsync(addContext);
        await scenario.RunAsync();
    }

    private async Task InitializeAsync(Action<IServiceCollection> addContext)
    {
        _options = new GoldpathCampaignOptions
        {
            LeaderTick = TimeSpan.FromMilliseconds(100),
            LeadershipSlice = TimeSpan.FromMilliseconds(500),
        };
        _options.AddCampaign<CampaignBenchTests.BenchTarget>("fleet", c => c
            .MaxTargets(1_000)
            .Targets((_, _) => Targets(10)));
        _engine = new GoldpathCampaignEngine<TContext>(
            _options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<TContext>>.Instance);
        var services = new ServiceCollection();
        addContext(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_options);
        services.AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher());
        services.AddSingleton(_engine);
        services.AddLogging();
        _services = services.BuildServiceProvider();
        _admin = new GoldpathCampaignAdminService<TContext>(
            _options, _engine, _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<GoldpathCampaignAdminService<TContext>>.Instance);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private static async IAsyncEnumerable<CampaignBenchTests.BenchTarget> Targets(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new CampaignBenchTests.BenchTarget(i);
        }

        await Task.CompletedTask;
    }

    private async Task SliceAsync()
    {
        using var scope = _services.CreateScope();
        var pacer = new GoldpathCampaignPacerJob<TContext>(
            _engine, _options, TimeProvider.System, NullLogger<GoldpathCampaignPacerJob<TContext>>.Instance);
        await pacer.ExecuteChunkAsync(CampaignFixtureLike.MakeChunk(0, "lead"), CampaignFixtureLike.CreateContext(scope.ServiceProvider), CancellationToken.None);
    }

    private async Task<T> QueryAsync<T>(Func<TContext, Task<T>> query)
    {
        using var scope = _services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TContext>());
    }

    private async Task RunAsync()
    {
        var created = await _admin.CreateAsync("fleet", "suspend proof", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 10_000, MaxInFlight: 1_000) { SuspendOnFailureRatio = 0.3, SuspendWindow = 100, SuspendMinSample = 4 },
            tenant: null, actor: "op", CancellationToken.None);
        Assert.True(created.Ok, created.Message);
        var id = await QueryAsync(db => db.Set<GoldpathCampaign>().Select(c => c.Id).SingleAsync());
        await SliceAsync();
        Assert.Equal(10, (await QueryAsync(db => db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id))).ReleasedThrough);

        // The fleet: ten claims, one flush; five time out at the device core.
        var outcomes = new List<GoldpathCampaignOutcomeMessage>();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            for (long seq = 1; seq <= 10; seq++)
            {
                var item = await _engine.ClaimAsync(db, id, seq, CancellationToken.None);
                Assert.NotNull(item);
                outcomes.Add(seq <= 5
                    ? new GoldpathCampaignOutcomeMessage(id, seq, false, "timed out") { ErrorCode = "TIMEOUT", Attempt = 1 }
                    : new GoldpathCampaignOutcomeMessage(id, seq, true, null) { Attempt = 1 });
            }

            await _engine.ApplyOutcomesAsync(db, id, outcomes, CancellationToken.None);
        }

        var flushed = await QueryAsync(db => db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal((10L, 5L), (flushed.WindowOutcomes, flushed.WindowFailures));

        await SliceAsync();

        var suspended = await QueryAsync(db => db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(GoldpathCampaignState.Suspended, suspended.State);
        Assert.Equal(
            "failure ratio 0.5 ≥ 0.3 over 10 outcomes since the campaign began (minimum sample 4); most frequent code TIMEOUT (100%)",
            suspended.SuspendedReason);
        var audit = await QueryAsync(db => db.Set<GoldpathCampaignAudit>().AsNoTracking().SingleAsync(a => a.CampaignId == id && a.Action == "suspend"));
        Assert.Equal("system", audit.Actor);

        var resumed = await _admin.ResumeAsync(id, "op", CancellationToken.None);
        Assert.True(resumed.Ok, resumed.Message);
        var running = await QueryAsync(db => db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.NotEqual(GoldpathCampaignState.Suspended, running.State);
        Assert.Null(running.SuspendedReason);
        Assert.Equal((0L, 0L), (running.WindowOutcomes, running.WindowFailures));
        Assert.NotNull(running.WindowStartedAt);
        Assert.Equal(TimeSpan.Zero, running.WindowStartedAt!.Value.Offset);
        Assert.True(running.WindowStartedAt > audit.At, string.Create(CultureInfo.InvariantCulture, $"{running.WindowStartedAt:O} vs {audit.At:O}"));
    }
}
