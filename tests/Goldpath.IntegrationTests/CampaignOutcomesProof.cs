using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Oracle.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Admin-contract R4.2 and R4.3 (#296) on real databases. The outcomes aggregate's two grouped
/// counts and the failures cursor must translate on every provider, and the order of
/// <c>failedByCode</c> must be the contract's on each: count descending, code ORDINAL, no code last
/// among equal counts. The service fixes that order rather than the database: a linguistic collation
/// (ICU or glibc <c>en-US</c>) puts <c>a-lower</c> before <c>B-UPPER</c>, a bytewise one does not, and
/// SQLite (the unit suite) sorts NULL first where PostgreSQL and Oracle sort it last. The PostgreSQL
/// proof runs on an ICU <c>en-US</c> default collation and the Oracle proof in a linguistic session
/// (<c>NLS_SORT = GENERIC_M</c>), where the database's own order disagrees with the contract's — and
/// a second campaign puts that disagreement ACROSS the twentieth place, where re-sorting the rows the
/// database returned cannot repair it: only a bytewise tie-break in the query fetches the right ones.
/// </summary>
public static class CampaignOutcomesProof
{
    /// <summary>Seeds a fixed set of items, then asks the service for the outcomes and the failures walk.</summary>
    public static async Task ProveAsync<TContext>(IServiceProvider services, GoldpathCampaignOptions options)
        where TContext : DbContext
    {
        var engine = services.GetRequiredService<GoldpathCampaignEngine<TContext>>();
        var id = await SeedAsync<TContext>(services, engine, "outcomes proof", add =>
        {
            // Equal counts under a lower-case and an upper-case code, and under no code: a linguistic
            // collation puts "a-lower" first, ordinal puts "B-UPPER" first, and NULL goes last.
            add(3, GoldpathCampaignItemState.Failed, "a-lower");
            add(3, GoldpathCampaignItemState.Failed, null);
            add(3, GoldpathCampaignItemState.Failed, "B-UPPER");
            add(5, GoldpathCampaignItemState.Failed, "ACK_TIMEOUT");
            add(4, GoldpathCampaignItemState.Succeeded, null);
            add(2, GoldpathCampaignItemState.Pending, null);
            for (var n = 0; n < 21; n++)
            {
                add(1, GoldpathCampaignItemState.Failed, $"Z{n:00}");   // 25 distinct codes in all: truncated at 20
            }
        });

        // Nineteen codes ten times each, then three codes once each that a linguistic collation and
        // ordinal order differently: the twentieth place belongs to "B-UPPER" by the contract, while
        // a linguistic ORDER BY would bring "a-lower" and "a-second" and leave "B-UPPER" unfetched.
        var boundary = await SeedAsync<TContext>(services, engine, "boundary tie", add =>
        {
            for (var n = 0; n < 19; n++)
            {
                add(10, GoldpathCampaignItemState.Failed, $"C{n:00}");
            }

            add(1, GoldpathCampaignItemState.Failed, "a-lower");
            add(1, GoldpathCampaignItemState.Failed, "a-second");
            add(1, GoldpathCampaignItemState.Failed, "B-UPPER");
        });

        var admin = new GoldpathCampaignAdminService<TContext>(options, engine, services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<GoldpathCampaignAdminService<TContext>>.Instance);

        var outcomes = await admin.GetOutcomesAsync(id, CancellationToken.None);
        Assert.NotNull(outcomes);
        Assert.Equal(new Dictionary<string, long> { ["Pending"] = 2, ["Succeeded"] = 4, ["Failed"] = 35 }, outcomes.ByState);
        Assert.True(outcomes.FailedCodesTruncated);
        Assert.Equal(20, outcomes.FailedByCode.Count);
        Assert.Equal(["ACK_TIMEOUT", "B-UPPER", "a-lower", null, "Z00", "Z01"], outcomes.FailedByCode.Take(6).Select(c => c.Code));
        Assert.Equal([5L, 3L, 3L, 3L, 1L, 1L], outcomes.FailedByCode.Take(6).Select(c => c.Count));
        Assert.Equal("Z15", outcomes.FailedByCode[^1].Code);
        Assert.Null(await admin.GetOutcomesAsync(Guid.NewGuid(), CancellationToken.None));

        var tie = await admin.GetOutcomesAsync(boundary, CancellationToken.None);
        Assert.NotNull(tie);
        Assert.True(tie.FailedCodesTruncated);
        Assert.Equal(20, tie.FailedByCode.Count);
        Assert.Equal("C18", tie.FailedByCode[18].Code);
        Assert.Equal(new GoldpathCampaignCodeCount("B-UPPER", 1), tie.FailedByCode[19]);

        // The failures walk: 35 failures at take=16 in three pages, every row with its code.
        var walked = new List<GoldpathCampaignFailedItem>();
        long after = 0;
        for (var page = 0; page < 5; page++)
        {
            var rows = await admin.GetFailedItemsAsync(id, after, 16, CancellationToken.None);
            walked.AddRange(rows);
            if (rows.Count < 16)
            {
                break;
            }

            after = rows[^1].Seq;
        }

        Assert.Equal(35, walked.Count);
        Assert.Equal(walked.Select(w => w.Seq).Distinct().Order(), walked.Select(w => w.Seq));
        Assert.Equal(["a-lower", "a-lower", "a-lower", null, null, null, "B-UPPER"], walked.Take(7).Select(w => w.ErrorCode));
    }

    /// <summary>Creates a campaign and seeds its items through <paramref name="seed"/>: (count, state, error code).</summary>
    private static async Task<Guid> SeedAsync<TContext>(IServiceProvider services, GoldpathCampaignEngine<TContext> engine, string name,
        Action<Action<int, GoldpathCampaignItemState, string?>> seed)
        where TContext : DbContext
    {
        using var scope = services.CreateScope();
        var campaign = await engine.CreateAsync(scope.ServiceProvider, "fleet", name, new Dictionary<string, string>(),
            new GoldpathCampaignPolicy(1, null, 1, null, null, "UTC"), tenant: null, actor: "it", CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var seq = 0L;
        seed((count, state, code) =>
        {
            for (var n = 0; n < count; n++)
            {
                db.Set<GoldpathCampaignItem>().Add(new GoldpathCampaignItem
                {
                    CampaignId = campaign.Id,
                    Seq = ++seq,
                    TargetJson = "{}",
                    State = state,
                    ErrorCode = code,
                    Error = state == GoldpathCampaignItemState.Failed ? "refused" : null,
                });
            }
        });
        await db.SaveChangesAsync();
        return campaign.Id;
    }

    /// <summary>The proof's campaign type; it never enumerates, the items are seeded.</summary>
    public static GoldpathCampaignOptions Options()
    {
        var options = new GoldpathCampaignOptions();
        options.AddCampaign<CampaignBenchTests.BenchTarget>("fleet", c => c.MaxTargets(100)
            .Targets((_, _) => AsyncEnumerable.Empty<CampaignBenchTests.BenchTarget>()));
        return options;
    }
}

/// <summary>R4.2 and R4.3 on PostgreSQL.</summary>
public sealed class CampaignOutcomesPostgresTests : IAsyncLifetime
{
    // A linguistic default collation, so ORDER BY in the database would answer "a-lower" before "B-UPPER".
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithEnvironment("POSTGRES_INITDB_ARGS", "--locale-provider=icu --icu-locale=en-US")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Outcomes_and_the_failures_walk_answer_the_contracts_order_on_PostgreSQL()
    {
        var options = CampaignOutcomesProof.Options();
        await using var services = new ServiceCollection()
            .AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(options)
            .AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher())
            .AddSingleton(new GoldpathCampaignEngine<CampaignTests.CampDb>(options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<CampaignTests.CampDb>>.Instance))
            .AddLogging()
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>().Database.EnsureCreatedAsync();
        }

        await CampaignOutcomesProof.ProveAsync<CampaignTests.CampDb>(services, options);
    }
}

/// <summary>
/// R4.2 and R4.3 on Oracle, in a LINGUISTIC session: Oracle's ORDER BY follows the session's
/// <c>NLS_SORT</c>, which a client's language settings can make linguistic, so the proof sets one
/// rather than relying on the BINARY default.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class CampaignOutcomesOracleTests(OracleFixture oracle)
{
    [Fact]
    public async Task Outcomes_and_the_failures_walk_answer_the_contracts_order_on_Oracle()
    {
        var connection = await oracle.CreateSchemaAsync("GP_CAMP_OUTCOMES");
        var options = CampaignOutcomesProof.Options();
        await using var services = new ServiceCollection()
            .AddScoped(_ => new OracleComposedDb(new DbContextOptionsBuilder<OracleComposedDb>()
                .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))
                .AddInterceptors(new LinguisticSession())
                .EnableServiceProviderCaching(false)
                .Options))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(options)
            .AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher())
            .AddSingleton(new GoldpathCampaignEngine<OracleComposedDb>(options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<OracleComposedDb>>.Instance))
            .AddLogging()
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OracleComposedDb>().Database.EnsureCreatedAsync();
        }

        await CampaignOutcomesProof.ProveAsync<OracleComposedDb>(services, options);
    }
}

/// <summary>Sets a linguistic sort on every connection the context opens.</summary>
internal sealed class LinguisticSession : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER SESSION SET NLS_SORT = GENERIC_M";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER SESSION SET NLS_SORT = GENERIC_M";
        command.ExecuteNonQuery();
    }
}
