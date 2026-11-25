using System.Diagnostics;
using System.Globalization;
using Goldpath.Approvals.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Approvals' first benchmark table (GA gate G13, #596; scripts/bench-approvals.sh →
/// packages/Goldpath.Approvals/ops/approvals-benchmarks.md). The approvals RFC §6 names per-ladder throughput and
/// oldest-pending as the module's operational signals; these facts put the first numbers behind them. Everything runs
/// through the public <see cref="GoldpathApprovalEngine"/> on the EF store over a real PostgreSQL. Trait-gated: evidence,
/// not regression tests. No accepted RFC declares an Approvals budget, so nothing here asserts a time; each fact asserts
/// only that the work it timed was the work it claims (every request granted, every overdue request moved).
/// </summary>
[Trait("Category", "Bench")]
[Collection(ApprovalsBenchCollection.Name)]
public sealed class ApprovalsBenchTests : IAsyncLifetime
{
    /// <summary>Requests per proof (GOLDPATH_BENCH_APPROVALS_N overrides it for a quick local run).</summary>
    private static readonly int N = int.TryParse(
        Environment.GetEnvironmentVariable("GOLDPATH_BENCH_APPROVALS_N"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
        ? n
        : 10_000;

    private const string Ladder = "credit-limit";
    private const int WorklistSamples = 20;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-24T09:00:00Z", CultureInfo.InvariantCulture));
    private ServiceProvider _provider = null!;
    private GoldpathApprovalEngine _engine = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _provider = new ServiceCollection()
            .AddDbContext<EfApprovalStoreFacts.ApprovalsDbContext>(b => b.UseNpgsql(_postgres.GetConnectionString()))
            .BuildServiceProvider(true);
        using (var scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EfApprovalStoreFacts.ApprovalsDbContext>().Database.EnsureCreatedAsync();
        }

        // Two rungs: the manager rung takes a quorum of two distinct signers (the four-eyes AND distinct-eyes path),
        // and the top rung is where an overdue manager request escalates to.
        _engine = new GoldpathApprovalEngine(
            new GoldpathApprovalsOptions().AddLadder(Ladder, l => l
                .Rung("manager", 1_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
                .TopRung("general-manager", TimeSpan.FromHours(24))),
            new GoldpathEfApprovalStore<EfApprovalStoreFacts.ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
            _time,
            NullLogger<GoldpathApprovalEngine>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<List<Guid>> RequestAllAsync()
    {
        var ids = new List<Guid>(N);
        for (var i = 0; i < N; i++)
        {
            var request = await _engine.RequestAsync(Ladder, $"LIMIT-{i}", 500_000m, $"maker-{i % 50}");
            ids.Add(request.Id);
        }

        return ids;
    }

    [Fact]
    public async Task Bench_request_then_decide_every_request_through_its_quorum()
    {
        var watch = Stopwatch.StartNew();
        var ids = await RequestAllAsync();
        watch.Stop();
        var requestSeconds = watch.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-APPROVALS request n={N} seconds={requestSeconds:F1} per-second={N / requestSeconds:F0} ms-per-request={watch.Elapsed.TotalMilliseconds / N:F2}"));

        // The measured path is the guarded one: the requester deciding their own request is refused, here, on the
        // same engine and store the loop below uses.
        Assert.Equal(GoldpathApprovalDecisionOutcome.FourEyesViolation,
            await _engine.DecideAsync(ids[0], "maker-0", "manager", granted: true, "my own request"));

        watch.Restart();
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
                await _engine.DecideAsync(ids[i], "checker-a", "manager", granted: true, "first signature"));
            Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
                await _engine.DecideAsync(ids[i], "checker-b", "manager", granted: true, "quorum met"));
        }

        watch.Stop();
        var decisions = 2 * N;
        var decideSeconds = watch.Elapsed.TotalSeconds;

        using var scope = _provider.CreateScope();
        var granted = await scope.ServiceProvider.GetRequiredService<EfApprovalStoreFacts.ApprovalsDbContext>()
            .Set<GoldpathApprovalRequest>().CountAsync(r => r.Status == GoldpathApprovalStatus.Granted);
        Assert.Equal(N, granted);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-APPROVALS decide n={N} decisions={decisions} seconds={decideSeconds:F1} decisions-per-second={decisions / decideSeconds:F0} ms-per-decision={watch.Elapsed.TotalMilliseconds / decisions:F2} granted={granted}"));
    }

    [Fact]
    public async Task Bench_worklist_for_one_role_with_every_request_pending()
    {
        await RequestAllAsync();

        // One warm-up call, so the first sample does not carry EF's model build and the first connection.
        Assert.Equal(N, (await _engine.WorklistAsync("checker-a", "manager")).Count);

        var samples = new double[WorklistSamples];
        for (var i = 0; i < WorklistSamples; i++)
        {
            var watch = Stopwatch.StartNew();
            var worklist = await _engine.WorklistAsync("checker-a", "manager");
            watch.Stop();
            Assert.Equal(N, worklist.Count);
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-APPROVALS worklist pending={N} samples={WorklistSamples} p50-ms={Percentile(samples, 0.50):F1} p95-ms={Percentile(samples, 0.95):F1} max-ms={samples[^1]:F1}"));
    }

    [Fact]
    public async Task Bench_one_escalation_sweep_over_every_request_overdue()
    {
        await RequestAllAsync();
        _time.Advance(TimeSpan.FromHours(9));

        var watch = Stopwatch.StartNew();
        var moved = await _engine.EscalateOverdueAsync();
        watch.Stop();

        Assert.Equal(N, moved);
        Assert.Equal(N, (await _engine.WorklistAsync("gm-a", "general-manager")).Count);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-APPROVALS escalate overdue={N} moved={moved} seconds={watch.Elapsed.TotalSeconds:F1} per-second={moved / watch.Elapsed.TotalSeconds:F0}"));
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    private static double Percentile(double[] ascending, double p)
        => ascending[Math.Clamp((int)Math.Ceiling(p * ascending.Length) - 1, 0, ascending.Length - 1)];
}

/// <summary>
/// Runs <see cref="ApprovalsBenchTests"/> alone, like <see cref="CampaignBrokerBenchCollection"/>: the monthly
/// <c>bench.yml</c> runs every <c>Category=Bench</c> class in one process, and a table measured beside another bench's
/// load would not be this module's number.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApprovalsBenchCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "approvals-bench";
}
