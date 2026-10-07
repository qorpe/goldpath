using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Goldpath.Campaign.Tests;

/// <summary>The test campaign's target payload.</summary>
public sealed record TestTarget(int Id, string Email);

/// <summary>Records what the engine publishes; can be scripted to crash mid-batch.</summary>
public sealed class RecordingPublisher : IPublishEndpoint
{
    public List<object> Published { get; } = [];

    /// <summary>When positive, the publisher throws after this many more publishes.</summary>
    public int ThrowAfter { get; set; } = -1;

    public Task Publish<T>(T message, CancellationToken cancellationToken = default)
        where T : class
    {
        if (ThrowAfter == 0)
        {
            throw new InvalidOperationException("the broker connection dropped");
        }

        if (ThrowAfter > 0)
        {
            ThrowAfter--;
        }

        Published.Add(message);
        return Task.CompletedTask;
    }

    public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish(object message, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task Publish<T>(object values, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer)
        => throw new NotSupportedException();
}

/// <summary>Executes targets; fails the ids scripted into <see cref="FailIds"/>.</summary>
public sealed class TestHandler(CampaignFixture fixture) : IGoldpathCampaignItemHandler<TestTarget>
{
    public Task ExecuteAsync(TestTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
    {
        if (fixture.FailIds.Contains(target.Id))
        {
            throw new InvalidOperationException($"the provider refused target {target.Id}");
        }

        fixture.Executed.Add((target, context));
        return Task.CompletedTask;
    }
}

public sealed class CampaignTestContext(DbContextOptions<CampaignTestContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddGoldpathCampaign();
        modelBuilder.AddGoldpathJobs();
    }
}

public sealed class CampaignFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public CampaignFixture(Action<GoldpathCampaignOptions>? configure = null, int sourceSize = 10, Action<IServiceCollection>? extraServices = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Source = [.. Enumerable.Range(1, sourceSize).Select(i => new TestTarget(i, $"user{i}@example.test"))];
        Options = new GoldpathCampaignOptions();
        Options.AddCampaign<TestTarget>("winback", c => c
            .MaxTargets(1_000)
            .Targets((_, _) => Stream(Source)));
        configure?.Invoke(Options);

        Publisher = new RecordingPublisher();
        var services = new ServiceCollection();
        services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(_connection));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options);
        services.AddSingleton<IPublishEndpoint>(Publisher);
        services.AddSingleton<GoldpathCampaignEngine<CampaignTestContext>>(
            _ => new GoldpathCampaignEngine<CampaignTestContext>(
                Options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance));
        services.AddScoped<IGoldpathCampaignItemHandler<TestTarget>>(_ => new TestHandler(this));
        services.AddLogging();
        extraServices?.Invoke(services);
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CampaignTestContext>().Database.EnsureCreated();
    }

    public List<TestTarget> Source { get; }

    public HashSet<int> FailIds { get; } = [];

    public List<(TestTarget Target, GoldpathCampaignItemContext Context)> Executed { get; } = [];

    public GoldpathCampaignOptions Options { get; }

    public RecordingPublisher Publisher { get; }

    public ServiceProvider Services { get; }

    public GoldpathCampaignEngine<CampaignTestContext> Engine
        => Services.GetRequiredService<GoldpathCampaignEngine<CampaignTestContext>>();

    public GoldpathCampaignPacerJob<CampaignTestContext> Pacer()
        => new(Engine, Options, TimeProvider.System,
            NullLogger<GoldpathCampaignPacerJob<CampaignTestContext>>.Instance);

    public GoldpathCampaignAdminService<CampaignTestContext> Admin()
        => new(Options, Engine, Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<GoldpathCampaignAdminService<CampaignTestContext>>.Instance);

    public async Task<GoldpathCampaign> CreateAsync(GoldpathCampaignPolicy? policy = null)
    {
        using var scope = Services.CreateScope();
        return await Engine.CreateAsync(scope.ServiceProvider, "winback", "test run",
            new Dictionary<string, string>(), policy, tenant: null, actor: "tester", CancellationToken.None);
    }

    /// <summary>Streams enumeration to the end (or to a ceiling pause) through the engine.</summary>
    public async Task EnumerateAllAsync(GoldpathCampaign campaign)
    {
        using var scope = Services.CreateScope();
        var stream = await Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
        try
        {
            while (!campaign.EnumerationComplete && campaign.State != GoldpathCampaignState.Paused)
            {
                await Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    /// <summary>Acts as the consumer fleet for every published coordinate: claim → execute → outcome batch.</summary>
    public async Task ConsumeAllPublishedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        var outcomes = new List<GoldpathCampaignOutcomeMessage>();
        foreach (var message in Publisher.Published.OfType<GoldpathCampaignItemMessage>().ToList())
        {
            var item = await Engine.ClaimAsync(db, message.CampaignId, message.Seq, CancellationToken.None);
            if (item is null)
            {
                continue;   // duplicate delivery — the guard dropped it
            }

            try
            {
                var result = await Engine.ExecuteItemAsync(scope.ServiceProvider, message.Type,
                    message.CampaignId, message.Seq, item.TargetJson, null, attempt: item.Attempts + 1, replay: false, CancellationToken.None);
                outcomes.Add(Engine.OutcomeFor(message.CampaignId, message.Seq, result));
            }
            catch (Exception e)
            {
                outcomes.Add(new GoldpathCampaignOutcomeMessage(message.CampaignId, message.Seq, false, e.Message));
            }
        }

        foreach (var group in outcomes.GroupBy(o => o.CampaignId))
        {
            await Engine.ApplyOutcomesAsync(db, group.Key, [.. group], CancellationToken.None);
        }
    }

    /// <summary>Runs one pacer leadership slice through the job class; returns reported item failures.</summary>
    public async Task<IReadOnlyList<(string ItemKey, string Reason)>> RunPacerSliceAsync()
    {
        using var scope = Services.CreateScope();
        var job = Pacer();
        var context = CreateContext(scope.ServiceProvider);
        var plan = await job.PlanAsync(context, CancellationToken.None);
        var chunk = MakeChunk(0, plan.ChunkPayloads[0]);
        await job.ExecuteChunkAsync(chunk, context, CancellationToken.None);
        return Failures(chunk);
    }

    public static GoldpathJobContext CreateContext(IServiceProvider services)
        => (GoldpathJobContext)Activator.CreateInstance(typeof(GoldpathJobContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [Guid.NewGuid(), "test", "node", "job", false, null, services], null)!;

    public static GoldpathJobChunk MakeChunk(int index, string payload)
        => (GoldpathJobChunk)Activator.CreateInstance(typeof(GoldpathJobChunk),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [index, payload], null)!;

    public static IReadOnlyList<(string ItemKey, string Reason)> Failures(GoldpathJobChunk chunk)
        => (List<(string, string)>)typeof(GoldpathJobChunk)
            .GetProperty("ItemFailures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(chunk)!;

    public T Query<T>(Func<CampaignTestContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTestContext>());
    }

    public void Mutate(Action<CampaignTestContext> mutate)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        mutate(db);
        db.SaveChanges();
    }

    public GoldpathCampaign Reload(Guid id)
        => Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == id));

    private static async IAsyncEnumerable<TestTarget> Stream(IEnumerable<TestTarget> source)
    {
        foreach (var target in source)
        {
            yield return target;
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}
