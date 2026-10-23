using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-OPS-01 (scenario campaign): "The 03:00 incident: stop the world, fix, resume, answer for
/// it." A real jobs fleet on real PostgreSQL ticks every second. The night operator stops it
/// with the admin verb pause-all; NOTHING fires; the host is RESTARTED while paused (the stop
/// must be durable and cluster-wide, not a flag in one process's memory) and still nothing
/// fires; the morning operator resumes; work flows again; and the admin audit answers "who
/// stopped the night" with both verbs and both actors.
/// Replay: dotnet test --filter FullyQualifiedName~ScenarioOps01.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class ScenarioOps01Tests : IAsyncLifetime
{
    public sealed class OpsDb(DbContextOptions<OpsDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathJobs();
    }

    /// <summary>One tiny chunk per fire — every fire leaves exactly one run row behind.</summary>
    public sealed class NightlyTickJob : IGoldpathJob
    {
        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
            => Task.FromResult(new GoldpathJobPlan(["tick"], 1));

        public Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    // Long enough that a fire which slipped past the pause would certainly show up: the
    // trigger fires every second, so a broken stop produces ~6 runs in this window.
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(6);

    private readonly string _fleet = $"ops01-{Guid.NewGuid():N}"[..16];   // Quartz's SchedulerRepository is process-global
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = new OpsDb(new DbContextOptionsBuilder<OpsDb>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Pause_all_survives_a_restart_and_the_audit_answers_who_stopped_the_night()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;

        // GIVEN a fleet mid-run: the trigger is firing and runs are landing.
        var night = await StartHostAsync(token);
        try
        {
            await WaitUntilAsync(async () => await CountRunsAsync(night) >= 2, token);

            // WHEN the night operator stops the world.
            var paused = await Admin(night).SetFleetPausedAsync(_fleet, paused: true, "night-operator", token);
            Assert.True(paused.Ok, paused.Message);

            // A fire already in flight at the verb may still finish; after that, silence.
            await WaitUntilAsync(async () => await CountRunningAsync(night) == 0, token);
            var atPause = await CountRunsAsync(night);
            await Task.Delay(Quiet, token);
            Assert.Equal(atPause, await CountRunsAsync(night));
            Assert.True((await Admin(night).GetFleetStatusAsync(_fleet, token))!.IsPaused);
        }
        finally
        {
            await StopAsync(night);
        }

        // AND the host restarts while paused: a new process-equivalent, the same store.
        var morning = await StartHostAsync(token);
        try
        {
            var beforeRestartQuiet = await CountRunsAsync(morning);
            await Task.Delay(Quiet, token);
            Assert.Equal(beforeRestartQuiet, await CountRunsAsync(morning));
            Assert.True((await Admin(morning).GetFleetStatusAsync(_fleet, token))!.IsPaused,
                "the paused state must be read back from the store after a restart");

            // THEN the morning operator resumes, and the fleet works again.
            var resumed = await Admin(morning).SetFleetPausedAsync(_fleet, paused: false, "morning-operator", token);
            Assert.True(resumed.Ok, resumed.Message);
            await WaitUntilAsync(async () => await CountRunsAsync(morning) > beforeRestartQuiet, token);
            Assert.False((await Admin(morning).GetFleetStatusAsync(_fleet, token))!.IsPaused);

            // AND the audit answers "who stopped the night" — and who started the morning.
            var audit = (await Admin(morning).GetAuditAsync(50, token)).Where(a => a.Fleet == _fleet).ToList();
            var stop = Assert.Single(audit, a => a.Action == "pause-all");
            var go = Assert.Single(audit, a => a.Action == "resume-all");
            Assert.Equal("night-operator", stop.Actor);
            Assert.Equal("morning-operator", go.Actor);
            Assert.True(stop.At < go.At);
        }
        finally
        {
            await StopAsync(morning);
        }
    }

    private async Task<IHost> StartHostAsync(CancellationToken token)
    {
        var connection = _postgres.GetConnectionString();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:opsdb"] = connection;
        builder.Services.AddDbContext<OpsDb>(o => o.UseNpgsql(connection));
        builder.AddGoldpathJobs<HostApplicationBuilder, OpsDb>(jobs =>
        {
            jobs.ConnectionName = "opsdb";
            jobs.SchedulerName = _fleet;
            jobs.AddJob<NightlyTickJob>(j => j.Cron = "* * * * * ?");
        });
        var host = builder.Build();
        await host.StartAsync(token);

        // Connection warm-up on a container that JUST reported ready is transient.
        await WaitUntilAsync(async () =>
        {
            try
            {
                return (await Admin(host).GetJobsAsync(_fleet, token)).Count >= 1;
            }
            catch (Quartz.JobPersistenceException)
            {
                return false;
            }
        }, token);
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
        QuartzProcessGlobals.Pin();
    }

    private static GoldpathJobsAdminService<OpsDb> Admin(IHost host)
        => host.Services.GetRequiredService<GoldpathJobsAdminService<OpsDb>>();

    private Task<int> CountRunsAsync(IHost host) => QueryAsync(host, db => db.Set<GoldpathJobRun>()
        .CountAsync(r => r.SchedulerName == _fleet && r.JobName == nameof(NightlyTickJob)));

    private Task<int> CountRunningAsync(IHost host) => QueryAsync(host, db => db.Set<GoldpathJobRun>()
        .CountAsync(r => r.SchedulerName == _fleet && r.Status == GoldpathJobRunStatus.Running));

    private static async Task<int> QueryAsync(IHost host, Func<OpsDb, Task<int>> query)
    {
        using var scope = host.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OpsDb>());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }
}
