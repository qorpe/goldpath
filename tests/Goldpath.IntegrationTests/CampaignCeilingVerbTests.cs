using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-FLEET-03 extended (campaign RFC R3.2, slice R3-S9, #318) on real PostgreSQL + RabbitMQ: two campaigns of
/// DIFFERENT types that name one target system never jointly exceed its ceiling in any one-second window of pacer
/// ticks, and an operator lowering that ceiling through the admin verb mid-run binds every tick that starts one
/// leader tick after the change commits, with no restart; the change is audited with the operator's name. The strict
/// per-second check is #292's: no tolerance.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignCeilingVerbTests : IAsyncLifetime
{
    private const string System = "device-core";
    private const int SeedTps = 20;
    private const int LoweredTps = 8;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);   // WorstSecondOfTicks reads ticks of this length

    private readonly SuiteStore _store = SuiteStore.PostgreSql();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly string _fleet = $"ccv-{Guid.NewGuid():N}"[..16];   // Quartz's SchedulerRepository is process-global
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        CampaignTests.WinbackHandler.Executions.Clear();
        CampaignTests.WinbackHandler.Poisoned.Clear();
        await Task.WhenAll(_store.StartAsync(), _rabbit.StartAsync());

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:campdb"] = _store.ConnectionString;
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(_store.Use);
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<CampaignTests.CustomerTarget>, CampaignTests.WinbackHandler>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(4);
            campaign.LeaderTick = Tick;
            campaign.EnumerationBatchSize = 500;
            campaign.AddTargetSystem(System, SeedTps);
            foreach (var type in new[] { "config-push", "firmware-push" })
            {
                campaign.AddCampaign<CampaignTests.CustomerTarget>(type, c => c
                    .MaxTargets(10_000)
                    .TargetSystem(System)
                    .Targets((services, _) => services.GetRequiredService<CampaignTests.CampDb>()
                        .Customers.AsNoTracking()
                        .OrderBy(x => x.Id)
                        .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                        .AsAsyncEnumerable()));
            }
        });
        builder.Services.WaitForTheBusOnStart();
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddGoldpathCampaign<CampaignTests.CampDb>();
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        });
        builder.AddGoldpathJobs<HostApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.Provider = _store.JobsProvider;
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_store.StopAsync(), _rabbit.DisposeAsync().AsTask());
    }

    private T Query<T>(Func<CampaignTests.CampDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    [Fact]
    public async Task TwoTypesOnOneTargetSystem_NeverJointlyExceedItsCeiling_AndTheVerbsChangeBitesWithinOneTick()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Customers.AddRange(Enumerable.Range(1, 400).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = $"device{i}@example.test",
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);

            // Each generous alone and of a different type: only the target system's shared bucket can explain a joint ceiling.
            var engine = scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>();
            var generous = new GoldpathCampaignPolicy(1_000, null, 5_000, null, null, "UTC");
            await engine.CreateAsync(scope.ServiceProvider, "config-push", "config", new Dictionary<string, string>(), generous, tenant: null, actor: "it", token);
            await engine.CreateAsync(scope.ServiceProvider, "firmware-push", "firmware", new Dictionary<string, string>(), generous, tenant: null, actor: "it", token);
        }

        List<DateTimeOffset> Stamps() => Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.ReleasedAt != null).Select(i => i.ReleasedAt!.Value).ToList());

        // Both release hungry at the seed for a stretch that spans a slice boundary (an idle gap: the burst case).
        while (Stamps().Count == 0)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(200, token);
        }

        await Task.Delay(TimeSpan.FromSeconds(6), token);

        // Tonight the device core takes eight: through the verb, not the row.
        DateTimeOffset committed;
        using (var scope = _host.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>();
            var result = await admin.ThrottleCeilingAsync(GoldpathCampaignCeilingKind.TargetSystem, System,
                new GoldpathCampaignCeilingPatch(MaxTps: LoweredTps), "ops@example.test", token);
            committed = DateTimeOffset.UtcNow;
            Assert.True(result.Ok, result.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(8), token);
        var stamps = Stamps();
        var before = stamps.Where(s => s < committed).ToList();
        var after = stamps.Where(s => s >= committed + Tick).ToList();

        // Both types actually released on both sides of the change (so the joint bound is a shared bucket, not one idle type).
        var types = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.ReleasedAt != null)
            .Join(db.Set<GoldpathCampaign>().AsNoTracking(), i => i.CampaignId, c => c.Id, (i, c) => c.Type).Distinct().ToList());
        Assert.Equal(2, types.Count);
        var worstBefore = CampaignR1Tests.WorstSecondOfTicks(before);
        Assert.True(worstBefore.Released <= SeedTps,
            $"before the change one second of ticks (from {worstBefore.From:HH:mm:ss.fff}) released {worstBefore.Released} across both types > {SeedTps}");
        var worstAfter = CampaignR1Tests.WorstSecondOfTicks(after);
        Assert.True(worstAfter.Released <= LoweredTps,
            $"from one tick after the change one second of ticks (from {worstAfter.From:HH:mm:ss.fff}) released {worstAfter.Released} across both types > {LoweredTps}");

        // Enough seconds on both sides of the change for the bounds above to have been tested at all.
        Assert.True(before.Count >= SeedTps * 2, $"only {before.Count} releases before the change — too few seconds were sampled");
        Assert.True(after.Count >= LoweredTps * 3, $"only {after.Count} releases after the change — too few seconds were sampled");

        // The change is audited with the operator's name, and the row carries it.
        var audit = Assert.Single(Query(db => db.Set<GoldpathCampaignAudit>().AsNoTracking().Where(a => a.CampaignId == Guid.Empty).ToList()));
        Assert.Equal(("ceiling", "ops@example.test", $"target system '{System}': MaxTps {SeedTps}/s -> {LoweredTps}/s"),
            (audit.Action, audit.Actor, audit.Detail));
        var row = Query(db => db.Set<GoldpathCampaignCeiling>().AsNoTracking().Single(c => c.Kind == GoldpathCampaignCeilingKind.TargetSystem));
        Assert.Equal((LoweredTps, SeedTps, "ops@example.test"), (row.MaxTps, row.SeedMaxTps, row.UpdatedBy));
    }
}
