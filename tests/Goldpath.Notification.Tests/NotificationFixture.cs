using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Goldpath.Notification.Tests;

/// <summary>A channel that records messages and fails on script ("FAIL" in the body).</summary>
public sealed class RecordingChannel : IGoldpathNotificationChannel
{
    public List<GoldpathNotificationMessage> Accepted { get; } = [];

    public int Throws { get; set; }

    /// <summary>The refusal's text: the provider's own words, as a real channel's exception carries them.</summary>
    public string FailureMessage { get; set; } = "the gateway refused";

    public string Name => "recording";

    public Task SendAsync(GoldpathNotificationMessage message, CancellationToken cancellationToken)
    {
        if (message.Body.Contains("FAIL", StringComparison.Ordinal) || Throws-- > 0)
        {
            throw new InvalidOperationException(FailureMessage);
        }

        Accepted.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class NotificationTestContext(DbContextOptions<NotificationTestContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddGoldpathNotification();
        modelBuilder.AddGoldpathJobs();
    }
}

public sealed class NotificationFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public NotificationFixture(Action<GoldpathNotificationOptions>? configure = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new GoldpathNotificationOptions { RetryDelay = TimeSpan.Zero };
        Options.AddTemplate("policy-renewal", t => t
            .Channel("recording", c => c
                .Subject("tr", "Poliçeniz {{PolicyNo}} yenilenmek üzere")
                .Body("tr", "Sayın {{Name}}, poliçeniz {{RenewalDate}} tarihinde yenilenecektir.")
                .Subject("", "Your policy {{PolicyNo}} is up for renewal")
                .Body("", "Dear {{Name}}, your policy renews on {{RenewalDate}}."))
            .DeleteBodyAfter(TimeSpan.FromDays(90)));
        configure?.Invoke(Options);

        Channel = new RecordingChannel();
        var services = new ServiceCollection();
        services.AddDbContext<NotificationTestContext>(o => o.UseSqlite(_connection));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options);
        services.AddSingleton<IGoldpathNotificationChannel>(Channel);
        services.AddScoped<IGoldpathNotifier, GoldpathNotifier<NotificationTestContext>>();
        services.AddLogging();
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NotificationTestContext>().Database.EnsureCreated();
    }

    public GoldpathNotificationOptions Options { get; }

    public RecordingChannel Channel { get; }

    public ServiceProvider Services { get; }

    public GoldpathNotificationSendJob<NotificationTestContext> SendJob()
        => new(Options, [Channel], TimeProvider.System, NullLogger<GoldpathNotificationSendJob<NotificationTestContext>>.Instance);

    public GoldpathNotificationRetentionJob<NotificationTestContext> RetentionJob()
        => new(Options, TimeProvider.System);

    public static GoldpathNotificationRequest Renewal(string dedupKey, string name = "Ömer", string? culture = "tr")
        => new("policy-renewal", "recording", "omer@example.test", culture ?? "tr",
            new Dictionary<string, string> { ["Name"] = name, ["PolicyNo"] = "P-42", ["RenewalDate"] = "2026-08-01" },
            dedupKey);

    public async Task<GoldpathNotification> RequestAsync(GoldpathNotificationRequest request)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IGoldpathNotifier>().RequestAsync(request, CancellationToken.None);
    }

    /// <summary>Runs one full send pass (plan + every chunk) through the job class.</summary>
    public async Task<List<(string ItemKey, string Reason)>> RunSendPassAsync()
    {
        using var scope = Services.CreateScope();
        var job = SendJob();
        var context = CreateContext(scope.ServiceProvider);
        var plan = await job.PlanAsync(context, CancellationToken.None);
        var failures = new List<(string, string)>();
        for (var i = 0; i < plan.ChunkPayloads.Count; i++)
        {
            var chunk = MakeChunk(i, plan.ChunkPayloads[i]);
            await job.ExecuteChunkAsync(chunk, context, CancellationToken.None);
            failures.AddRange(Failures(chunk));
        }

        return failures;
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

    public T Query<T>(Func<NotificationTestContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<NotificationTestContext>());
    }

    public void Mutate(Action<NotificationTestContext> mutate)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestContext>();
        mutate(db);
        db.SaveChanges();
    }

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}
