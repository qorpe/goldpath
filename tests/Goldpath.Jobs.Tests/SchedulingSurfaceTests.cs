using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Goldpath.Jobs.Tests;

/// <summary>
/// The scheduling surface (admin contract R2), against a real in-memory Quartz scheduler
/// rather than a mock: the questions here — "does a simple trigger report its repeat
/// count?", "does an undeclared job get refused?" — are exactly the ones a mock would
/// answer by construction.
///
/// The RUN LIST is deliberately NOT here. Its filters and keyset walk order by
/// DateTimeOffset, which SQLite cannot translate at all, and Goldpath does not ship on
/// SQLite — so proving it against this fixture would prove it on a store no adopter runs.
/// It lives in Goldpath.IntegrationTests/JobsRunListTests.cs, on real PostgreSQL.
/// </summary>
public class SchedulingSurfaceTests
{
    private static GoldpathJobDefinition Define<TJob>()
        where TJob : class, IGoldpathJob
    {
        var options = new GoldpathJobsOptions();
        options.AddJob<TJob>();
        return options.Jobs[0];
    }

    // ── what put the run on the schedule (R2.3) ─────────────────────────────────

    [Fact]
    public async Task An_UNSTAMPED_fire_is_recorded_as_Scheduled()
    {
        using var fixture = new RunnerFixture();
        var job = new ScriptedJob { TotalItems = 2, ChunkSize = 2 };

        await fixture.Runner.RunAsync(job, Define<ScriptedJob>(), fixture.Fire(), default);

        // Only the admin verbs stamp, so the absence of a stamp IS the scheduler firing —
        // the default may never invent an operator who was not there.
        Assert.Equal(GoldpathJobTriggeredBy.Scheduled, fixture.Query(db => db.Set<GoldpathJobRun>().Single().TriggeredBy));
    }

    [Theory]
    [InlineData(GoldpathJobTriggeredBy.Manual)]
    [InlineData(GoldpathJobTriggeredBy.Rerun)]
    [InlineData(GoldpathJobTriggeredBy.Replay)]
    public async Task A_STAMPED_fire_carries_the_operator_into_the_run(string stamp)
    {
        using var fixture = new RunnerFixture();
        var job = new ScriptedJob { TotalItems = 2, ChunkSize = 2 };
        var fire = fixture.Fire() with { TriggeredBy = stamp };

        await fixture.Runner.RunAsync(job, Define<ScriptedJob>(), fire, default);

        Assert.Equal(stamp, fixture.Query(db => db.Set<GoldpathJobRun>().Single().TriggeredBy));
    }

    // ── the trigger surface, against a REAL scheduler ───────────────────────────

    [Fact]
    public async Task A_cron_trigger_reports_the_facts_a_cron_STRING_cannot_explain()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly", ("window", "eod"));
        await scheduler.ScheduleJob(
            TriggerBuilder.Create()
                .WithIdentity("nightly-cron", GoldpathJobsExtensions.JobGroup)
                .ForJob(new JobKey("nightly", GoldpathJobsExtensions.JobGroup))
                .WithPriority(7)
                .WithCronSchedule("0 0 3 * * ?", s => s.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")))
                .Build());

        var trigger = (await admin.GetJobsAsync("fleet", default)).Single().Triggers.Single();

        Assert.Equal("cron", trigger.Type);
        Assert.Equal("0 0 3 * * ?", trigger.CronExpression);
        // The timezone is the fact that makes "it did not run at 03:00" answerable: the
        // cron string reads the same in every zone and means a different hour in each.
        Assert.Equal("Europe/Istanbul", trigger.TimeZoneId);
        Assert.Equal(7, trigger.Priority);
        Assert.NotNull(trigger.StartAt);
    }

    [Fact]
    public async Task A_simple_trigger_reports_its_interval_and_repeats_rather_than_an_empty_cron()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "poller");
        await scheduler.ScheduleJob(
            TriggerBuilder.Create()
                .WithIdentity("poller-simple", GoldpathJobsExtensions.JobGroup)
                .ForJob(new JobKey("poller", GoldpathJobsExtensions.JobGroup))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(15)).WithRepeatCount(4))
                .Build());

        var trigger = (await admin.GetJobsAsync("fleet", default)).Single().Triggers.Single();

        // Reading "type: simple" beats inferring it from which fields came back null.
        Assert.Equal("simple", trigger.Type);
        Assert.Null(trigger.CronExpression);
        Assert.Equal(TimeSpan.FromMinutes(15), trigger.RepeatInterval);
        Assert.Equal(4, trigger.RepeatCount);
        Assert.Equal(0, trigger.TimesTriggered);
    }

    [Fact]
    public async Task The_job_data_map_is_READ_only_and_visible()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly", ("window", "eod"), ("batchSize", "500"));

        var job = (await admin.GetJobsAsync("fleet", default)).Single();

        // Diagnosis needs to SEE the parameters a run was given; there is deliberately no
        // verb that changes them (ADR-0001 — that is the code's job, not an operator's).
        Assert.Equal("eod", job.DataMap!["window"]);
        Assert.Equal("500", job.DataMap["batchSize"]);
        Assert.DoesNotContain(
            typeof(GoldpathJobsAdminService<JobsTestContext>).GetMethods().Select(m => m.Name),
            name => name.Contains("DataMap", StringComparison.Ordinal) && !name.StartsWith("get_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_trigger_can_be_added_to_a_DECLARED_job_and_removed_again()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");

        var added = await admin.AddTriggerAsync("fleet", "nightly", "month-end",
            new GoldpathTriggerSpec("0 0 2 L * ?", null, null, null, null), "ops@acme", default);
        var afterAdd = (await admin.GetJobsAsync("fleet", default)).Single().Triggers.Count;
        var removed = await admin.RemoveTriggerAsync("fleet", "nightly", "month-end", "ops@acme", default);
        var afterRemove = (await admin.GetJobsAsync("fleet", default)).Single().Triggers.Count;

        Assert.True(added.Ok, added.Message);
        Assert.Equal(1, afterAdd);
        Assert.True(removed.Ok, removed.Message);
        Assert.Equal(0, afterRemove);
        // The JOB survives its last trigger: unscheduling is not deleting.
        Assert.True(await scheduler.CheckExists(new JobKey("nightly", GoldpathJobsExtensions.JobGroup), default));
        // Both crossings are on the record — iron rule 2 — and each names who made it.
        var audit = fixture.Query(db => db.Set<GoldpathJobAdminAudit>()
            .Where(a => a.Action == "add-trigger" || a.Action == "remove-trigger")
            .Select(a => new { a.Action, a.Actor }).ToList());
        Assert.Contains(audit, a => a.Action == "add-trigger");
        Assert.Contains(audit, a => a.Action == "remove-trigger");
        Assert.All(audit, a => Assert.Equal("ops@acme", a.Actor));
    }

    [Fact]
    public async Task Scheduling_a_job_that_was_never_DECLARED_is_refused_with_the_reason()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);

        var result = await admin.AddTriggerAsync("fleet", "invented", "whenever",
            new GoldpathTriggerSpec("0 0 3 * * ?", null, null, null, null), "ops@acme", default);

        // This refusal IS the constitution at runtime (ADR-0001): a trigger schedules a job
        // that the manifest and the code already declare. It cannot bring one into being.
        Assert.False(result.Ok);
        Assert.Contains("does not create one", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trigger_must_be_ONE_kind_and_a_bad_cron_never_reaches_the_store()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");

        var both = await admin.AddTriggerAsync("fleet", "nightly", "t1",
            new GoldpathTriggerSpec("0 0 3 * * ?", null, TimeSpan.FromMinutes(5), null, null), "ops@acme", default);
        var neither = await admin.AddTriggerAsync("fleet", "nightly", "t2",
            new GoldpathTriggerSpec(null, null, null, null, null), "ops@acme", default);
        var nonsense = await admin.AddTriggerAsync("fleet", "nightly", "t3",
            new GoldpathTriggerSpec("every other tuesday", null, null, null, null), "ops@acme", default);

        Assert.False(both.Ok);
        Assert.False(neither.Ok);
        Assert.False(nonsense.Ok);
        // A refused verb leaves nothing behind — not even the trigger it was going to make.
        Assert.Empty((await admin.GetJobsAsync("fleet", default)).Single().Triggers);
    }

    [Fact]
    public async Task An_unknown_TIMEZONE_is_refused_by_both_scheduling_verbs_never_thrown()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");

        var added = await admin.AddTriggerAsync("fleet", "nightly", "bad-zone",
            new GoldpathTriggerSpec("0 0 3 * * ?", "Mars/Olympus", null, null, null), "ops@acme", default);
        var rescheduled = await admin.RescheduleAsync("fleet", "nightly", "0 0 3 * * ?", "Mars/Olympus", "ops@acme", default);

        // A typo in a form field is CALLER error: it answers 400 with the reason, like
        // every other refusal on this surface. Letting TimeZoneNotFoundException escape
        // would render as a 500 with nothing an operator could act on — and `reschedule`
        // has been frozen with that hole since it shipped (found by the R2 review).
        Assert.False(added.Ok);
        Assert.Contains("Mars/Olympus", added.Message, StringComparison.Ordinal);
        Assert.False(rescheduled.Ok);
        Assert.Contains("not a timezone this host knows", rescheduled.Message, StringComparison.Ordinal);
        // And nothing was scheduled on the way out.
        Assert.Empty((await admin.GetJobsAsync("fleet", default)).Single().Triggers);
    }

    [Fact]
    public async Task Removing_a_trigger_THROUGH_THE_WRONG_JOB_is_refused()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");
        await DeclareJobAsync(scheduler, "hourly");
        await admin.AddTriggerAsync("fleet", "nightly", "shared-name",
            new GoldpathTriggerSpec("0 0 3 * * ?", null, null, null, null), "ops@acme", default);

        var result = await admin.RemoveTriggerAsync("fleet", "hourly", "shared-name", "ops@acme", default);

        // Trigger names are unique per fleet, not per job: without this check a typo in the
        // job segment would unschedule something else entirely, and answer 200.
        Assert.False(result.Ok);
        Assert.Contains("belongs to job 'nightly'", result.Message, StringComparison.Ordinal);
        Assert.Single((await admin.GetJobsAsync("fleet", default)).Single(j => j.Name == "nightly").Triggers);
    }

    [Fact]
    public async Task The_fleet_reports_the_STORE_s_truth_and_names_the_connection_separately()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");

        var status = await admin.GetFleetStatusAsync("fleet", default);
        var unknown = await admin.GetFleetStatusAsync("no-such-fleet", default);

        Assert.NotNull(status);
        // Cluster facts come from the registry's view of the store...
        Assert.Equal("fleet", status!.SchedulerName);
        Assert.Single(status.Nodes);
        Assert.False(status.IsPaused);
        // ...while the per-instance numbers are named for the member that answered. On a
        // management-mode head those read as standby with an idle pool, and reporting
        // them as the FLEET's told an operator their running fleet was holding fires
        // (found by the console smoke on this PR).
        Assert.NotNull(status.Connection);
        Assert.True(status.Connection.ThreadPoolSize > 0);

        // A fleet nobody registered is absent, not an empty status that reads as healthy.
        Assert.Null(unknown);
    }

    [Fact]
    public async Task A_fleet_stopped_by_pause_all_reports_ITSELF_as_paused()
    {
        using var fixture = new RunnerFixture();
        var scheduler = await InMemorySchedulerAsync();
        var admin = AdminOver(fixture, scheduler);
        await DeclareJobAsync(scheduler, "nightly");
        await admin.AddTriggerAsync("fleet", "nightly", "nightly-cron",
            new GoldpathTriggerSpec("0 0 3 * * ?", null, null, null, null), "ops@acme", default);

        var before = (await admin.GetFleetStatusAsync("fleet", default))!.IsPaused;
        await admin.SetFleetPausedAsync("fleet", paused: true, "ops@acme", default);
        var after = (await admin.GetFleetStatusAsync("fleet", default))!.IsPaused;
        await admin.SetFleetPausedAsync("fleet", paused: false, "ops@acme", default);
        var resumed = (await admin.GetFleetStatusAsync("fleet", default))!.IsPaused;

        // Read back from the store's own paused trigger groups — durable and cluster-wide,
        // which is exactly what pause-all promises and what a per-instance standby is not.
        Assert.False(before);
        Assert.True(after);
        Assert.False(resumed);
    }

    // ── harness ────────────────────────────────────────────────────────────────

    private static GoldpathJobsAdminService<JobsTestContext> AdminOver(RunnerFixture fixture, IScheduler? scheduler = null)
        => new(
            new FakeRegistry(scheduler),
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

    /// <summary>A real Quartz scheduler with the RAM store: real triggers, no database.</summary>
    private static async Task<IScheduler> InMemorySchedulerAsync()
    {
        var scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"unit-{Guid.NewGuid():N}",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
            ["quartz.threadPool.threadCount"] = "1",
        }).GetScheduler();
        await scheduler.Start();
        return scheduler;
    }

    private static async Task DeclareJobAsync(IScheduler scheduler, string name, params (string Key, string Value)[] data)
    {
        var map = new JobDataMap();
        foreach (var (key, value) in data)
        {
            map[key] = value;
        }

        await scheduler.AddJob(
            JobBuilder.Create<NoopJob>()
                .WithIdentity(name, GoldpathJobsExtensions.JobGroup)
                .SetJobData(map)
                .StoreDurably()
                .Build(),
            replace: true);
    }

    private sealed class NoopJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    /// <summary>
    /// The registry seam, faked — and ONLY the seam: every scheduler fact under test comes
    /// from the real Quartz instance this hands back.
    /// </summary>
    private sealed class FakeRegistry(IScheduler? scheduler) : IGoldpathJobsFleetRegistry
    {
        public Task<IReadOnlyList<GoldpathFleetInfo>> GetFleetsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GoldpathFleetInfo>>(
                scheduler is null ? [] : [new GoldpathFleetInfo("fleet", 1, [new GoldpathFleetNode("node-a", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10))])]);

        public Task<IScheduler> GetSchedulerAsync(string schedulerName, CancellationToken ct)
            => Task.FromResult(scheduler ?? throw new InvalidOperationException("this test declared no scheduler"));
    }
}
