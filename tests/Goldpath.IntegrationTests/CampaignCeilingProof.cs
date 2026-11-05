using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R3.2, part a (#290) on real PostgreSQL: the statements SQLite cannot vouch for —
/// the seed rows inserted by eight leaders at once (exactly one row per key, and no leader fails), the
/// guarded follow of a changed code value including the global row's <c>NULL</c> seed (<c>MaxTps =
/// SeedMaxTps</c> with both null must translate to <c>IS NULL</c>), the operator's value standing, and the
/// pacer binding two types to the row's live ceiling rather than the code seed. Whether the eight actually
/// collide on a key varies by run; the conflict path itself is proven deterministically, with an
/// interceptor, by <c>R3TargetSystemTests.AnInsertThatMeetsAConcurrentRow_ReReadsIt_AndAppliesTheSeedRuleToIt</c>.
/// </summary>
public sealed class CampaignCeilingProof : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public Task Target_system_ceilings_seed_follow_code_stand_for_the_operator_and_bind_the_pacer_on_PostgreSQL()
        => CampaignCeilingScenario<CampaignTests.CampDb>.RunAsync(
            services => services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString())));
}

/// <summary>The same R3.2 scenario on Oracle (the RFC's 19c SQL form), in the Oracle lane.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class CampaignCeilingOracleProof(OracleFixture oracle)
{
    [Fact]
    public async Task Target_system_ceilings_seed_follow_code_stand_for_the_operator_and_bind_the_pacer_on_Oracle()
    {
        var connection = await oracle.CreateSchemaAsync("GP_CEILING");
        await CampaignCeilingScenario<OracleComposedDb>.RunAsync(services => services.AddScoped(_ => OracleComposedDb.Create(connection)));
    }
}

/// <summary>
/// Eight leaders seed one empty table at once; a release changes the code values; an operator moves the
/// target system's row; and one pacer slice over two types naming the system runs at the operator's value.
/// </summary>
internal sealed class CampaignCeilingScenario<TContext> : IAsyncDisposable
    where TContext : DbContext
{
    private const string System = "device-core";
    private ServiceProvider _services = null!;
    private GoldpathCampaignOptions _options = null!;
    private GoldpathCampaignEngine<TContext> _engine = null!;

    public static async Task RunAsync(Action<IServiceCollection> addContext)
    {
        await using var scenario = new CampaignCeilingScenario<TContext>();
        await scenario.InitializeAsync(addContext);
        await scenario.RunAsync();
    }

    private async Task InitializeAsync(Action<IServiceCollection> addContext)
    {
        _options = Options(systemTps: 200, globalTps: null);
        _engine = Engine(_options);
        var services = new ServiceCollection();
        addContext(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_options);
        services.AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher());
        services.AddSingleton(_engine);
        services.AddLogging();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private async Task RunAsync()
    {
        // 1) Eight leaders seed an empty table at once: one row per key, and none of them fails.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ReconcileAsync(systemTps: 200, globalTps: null))));
        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        var global = rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.Global);
        Assert.Equal((GoldpathCampaignCeiling.GlobalName, (int?)null, (int?)null), (global.Name, global.MaxTps, global.SeedMaxTps));
        var system = rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((System, (int?)200, (int?)200, "system"), (system.Name, system.MaxTps, system.SeedMaxTps, system.UpdatedBy));

        // 2) The next release seeds 300 and turns GlobalTps on at 5,000: nobody moved either row, so both follow —
        //    the global row's guard compares NULL with NULL.
        await ReconcileAsync(systemTps: 300, globalTps: 5_000);
        rows = await RowsAsync();
        Assert.Equal((5_000, 5_000), Values(rows, GoldpathCampaignCeilingKind.Global));
        Assert.Equal((300, 300), Values(rows, GoldpathCampaignCeilingKind.TargetSystem));

        // 3) Tonight the device core takes forty: an operator moves its row. The release after seeds 400; the row stands.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var row = await db.Set<GoldpathCampaignCeiling>().SingleAsync(c => c.Kind == GoldpathCampaignCeilingKind.TargetSystem && c.Name == System);
            row.MaxTps = 40;
            row.UpdatedBy = "ops@example.test";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await ReconcileAsync(systemTps: 400, globalTps: 5_000);
        var running = Options(systemTps: 400, globalTps: 5_000);
        var engine = Engine(running);
        Assert.Equal((40, 300), Values(await RowsAsync(), GoldpathCampaignCeilingKind.TargetSystem));

        // 4) One pacer slice over two types naming the system: they share the row's forty a second, not code's 400.
        Guid first, second;
        using (var scope = _services.CreateScope())
        {
            var policy = new GoldpathCampaignPolicy(10_000, null, 100_000, null, null, "UTC");
            first = (await engine.CreateAsync(scope.ServiceProvider, "config-push", "a", new Dictionary<string, string>(), policy, null, "tester", CancellationToken.None)).Id;
            second = (await engine.CreateAsync(scope.ServiceProvider, "firmware-push", "b", new Dictionary<string, string>(), policy, null, "tester", CancellationToken.None)).Id;
        }

        await SliceAsync(engine, running);   // the first slice enumerates the 500 targets each and starts both
        var before = await ReleasedAsync(first, second);
        var started = DateTimeOffset.UtcNow;
        await SliceAsync(engine, running);
        var elapsed = DateTimeOffset.UtcNow - started;
        var released = await ReleasedAsync(first, second) - before;
        // 40/s for a 2 s slice is 80, plus one 250 ms tick's bank (10); at code's 400/s it would be about 800.
        Assert.True(released is > 0 and <= 90, $"released {released} in {elapsed.TotalSeconds:F1} s against a ceiling of 40/s");
    }

    private static GoldpathCampaignOptions Options(int systemTps, int? globalTps, TimeSpan? slice = null)
    {
        var options = new GoldpathCampaignOptions
        {
            LeaderTick = TimeSpan.FromMilliseconds(250),
            LeadershipSlice = slice ?? TimeSpan.FromSeconds(2),
            GlobalTps = globalTps,
        };
        options.AddTargetSystem(System, systemTps);
        options.AddCampaign<CampaignBenchTests.BenchTarget>("config-push", c => c.MaxTargets(1_000).TargetSystem(System).Targets((_, _) => Targets(500)));
        options.AddCampaign<CampaignBenchTests.BenchTarget>("firmware-push", c => c.MaxTargets(1_000).TargetSystem(System).Targets((_, _) => Targets(500)));
        return options;
    }

    private static GoldpathCampaignEngine<TContext> Engine(GoldpathCampaignOptions options)
        => new(options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<TContext>>.Instance);

    /// <summary>A leader's slice start, through the public job: a slice of zero length reconciles the rows and leads no tick.</summary>
    private Task ReconcileAsync(int systemTps, int? globalTps)
    {
        var options = Options(systemTps, globalTps, slice: TimeSpan.Zero);
        return SliceAsync(Engine(options), options);
    }

    private async Task<List<GoldpathCampaignCeiling>> RowsAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TContext>().Set<GoldpathCampaignCeiling>().AsNoTracking().ToListAsync();
    }

    private static (int?, int?) Values(List<GoldpathCampaignCeiling> rows, GoldpathCampaignCeilingKind kind)
    {
        var row = rows.Single(r => r.Kind == kind);
        return (row.MaxTps, row.SeedMaxTps);
    }

    private async Task<long> ReleasedAsync(Guid first, Guid second)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathCampaign>().AsNoTracking().Where(c => c.Id == first || c.Id == second).SumAsync(c => c.ReleasedThrough);
    }

    private async Task SliceAsync(GoldpathCampaignEngine<TContext> engine, GoldpathCampaignOptions options)
    {
        using var scope = _services.CreateScope();
        var pacer = new GoldpathCampaignPacerJob<TContext>(engine, options, TimeProvider.System, NullLogger<GoldpathCampaignPacerJob<TContext>>.Instance);
        await pacer.ExecuteChunkAsync(CampaignFixtureLike.MakeChunk(0, "lead"), CampaignFixtureLike.CreateContext(scope.ServiceProvider), CancellationToken.None);
    }

    private static async IAsyncEnumerable<CampaignBenchTests.BenchTarget> Targets(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new CampaignBenchTests.BenchTarget(i);
        }

        await Task.CompletedTask;
    }
}
