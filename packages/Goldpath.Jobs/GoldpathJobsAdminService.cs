using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

namespace Goldpath;

/// <summary>
/// One registered job as the store sees it, with its live triggers and — read-only —
/// its data map (R2.6). Diagnosis needs to see the parameters a run was given; editing
/// them at runtime would make the job behave differently from the code that declares it,
/// which is the drift ADR-0001 exists to prevent.
/// </summary>
public sealed record GoldpathJobInfo(
    string Name,
    string? Description,
    bool RequestsRecovery,
    IReadOnlyList<GoldpathTriggerInfo> Triggers,
    IReadOnlyDictionary<string, string>? DataMap = null);

/// <summary>
/// One trigger's live state (R2.2). A cron string alone does not explain a fire time —
/// the timezone it is read in and the misfire policy are the two facts that make "why did
/// it not run last night?" answerable, so they travel with it.
/// </summary>
public sealed record GoldpathTriggerInfo(
    string Name,
    string State,
    string? CronExpression,
    string? CalendarName,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? PreviousFireAt,
    string Type = "unknown",
    int Priority = 0,
    int MisfireInstruction = 0,
    string? TimeZoneId = null,
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    int? TimesTriggered = null,
    TimeSpan? RepeatInterval = null,
    int? RepeatCount = null);

/// <summary>
/// The instance THIS console is talking through, and nothing more.
/// <para>
/// Quartz's metadata is per-instance: asked through a management-mode member it reports
/// that member — standby, an empty thread pool, zero jobs executed — while the executors
/// are firing normally. Reporting those numbers as the fleet's told an operator their
/// fleet was holding fires when it was not (found by the console smoke on the R2 PR), so
/// they are named for what they are and kept out of the fleet's own answer.
/// </para>
/// </summary>
public sealed record GoldpathFleetConnection(
    string InstanceId,
    DateTimeOffset? RunningSince,
    int ThreadPoolSize,
    int JobsExecuted,
    bool IsShutdown,
    bool InStandbyMode);

/// <summary>
/// A fleet as the STORE sees it (R2.1) — the first question of an incident is whether the
/// thing is alive, how big it is, and whether somebody stopped it. All three are facts
/// about the cluster, not about whichever member answered. <c>IsPaused</c> is what
/// <c>pause-all</c> leaves behind: durable, cluster-wide, and read back from the store's
/// own paused trigger groups.
/// </summary>
public sealed record GoldpathFleetStatus(
    string SchedulerName,
    int JobCount,
    bool IsPaused,
    IReadOnlyList<GoldpathFleetNode> Nodes,
    GoldpathFleetConnection Connection);

/// <summary>A trigger to add to a DECLARED job (R2.5): cron or simple, never a new job.</summary>
public sealed record GoldpathTriggerSpec(string? Cron, string? TimeZoneId, TimeSpan? Interval, int? RepeatCount, string? CalendarName, int Priority = 5);

/// <summary>A run with its chunk breakdown and open repair items.</summary>
public sealed record GoldpathRunDetail(GoldpathJobRun Run, IReadOnlyDictionary<string, int> ChunksByStatus, IReadOnlyList<GoldpathJobItemFailure> OpenFailures);

/// <summary>Verb outcome — failures carry the reason, never a silent 200.</summary>
public sealed record GoldpathAdminResult(bool Ok, string Message);

/// <summary>A calendar over the wire: exactly one shape per type.</summary>
public sealed record GoldpathCalendarSpec(string Type, string? Description, IReadOnlyList<DateTime>? ExcludedDates, IReadOnlyList<DayOfWeek>? ExcludedDays, string? CronExpression);

/// <summary>A named calendar and the triggers currently riding it.</summary>
public sealed record GoldpathCalendarInfo(string Name, string? Description, IReadOnlyList<string> UsedByTriggers);

/// <summary>
/// The admin verbs behind <c>MapGoldpathJobsAdmin</c> (§7.1: the API is the contract, every
/// screen is its skin, agents script the same surface). EVERY mutating verb writes a
/// <see cref="GoldpathJobAdminAudit"/> row — iron rule 2 lives here, not in each caller.
/// </summary>
public sealed class GoldpathJobsAdminService<TContext>
    where TContext : DbContext
{
    private readonly IGoldpathJobsFleetRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    /// <summary>Registered by AddGoldpathJobs/AddGoldpathJobsManagement.</summary>
    public GoldpathJobsAdminService(IGoldpathJobsFleetRegistry registry, IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _time = time;
    }

    /// <summary>Every fleet in the store with its live cluster nodes.</summary>
    public Task<IReadOnlyList<GoldpathFleetInfo>> GetFleetsAsync(CancellationToken ct)
        => _registry.GetFleetsAsync(ct);

    /// <summary>The fleet's jobs with their live trigger states.</summary>
    public async Task<IReadOnlyList<GoldpathJobInfo>> GetJobsAsync(string fleet, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(GoldpathJobsExtensions.JobGroup), ct);
        var jobs = new List<GoldpathJobInfo>();
        foreach (var key in keys.OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var detail = await scheduler.GetJobDetail(key, ct);
            var triggers = new List<GoldpathTriggerInfo>();
            foreach (var trigger in await scheduler.GetTriggersOfJob(key, ct))
            {
                var state = await scheduler.GetTriggerState(trigger.Key, ct);
                triggers.Add(Describe(trigger, state));
            }

            // ToString() rather than the raw object: a data map value may be any type the
            // job put there, and the console renders text. Values are declared in code
            // (ADR-0001), so this is a window onto the composition, never a lever on it.
            var data = detail?.JobDataMap.Count > 0
                ? detail.JobDataMap.ToDictionary(entry => entry.Key, entry => entry.Value?.ToString() ?? "", StringComparer.Ordinal)
                : null;
            jobs.Add(new GoldpathJobInfo(key.Name, detail?.Description, detail?.RequestsRecovery ?? false, triggers, data));
        }

        return jobs;
    }

    /// <summary>
    /// One trigger, as the store holds it. Cron and simple triggers answer different
    /// halves of this shape — a simple trigger has no cron string, a cron trigger no
    /// repeat count — and the reader is told WHICH by <c>Type</c> rather than having to
    /// infer it from which fields came back null.
    /// </summary>
    private static GoldpathTriggerInfo Describe(ITrigger trigger, TriggerState state)
    {
        var cron = trigger as ICronTrigger;
        var simple = trigger as ISimpleTrigger;
        return new GoldpathTriggerInfo(
            trigger.Key.Name,
            state.ToString(),
            cron?.CronExpressionString,
            trigger.CalendarName,
            trigger.GetNextFireTimeUtc(),
            trigger.GetPreviousFireTimeUtc(),
            cron is not null ? "cron" : simple is not null ? "simple" : "unknown",
            trigger.Priority,
            trigger.MisfireInstruction,
            cron?.TimeZone?.Id,
            trigger.StartTimeUtc,
            trigger.EndTimeUtc,
            simple?.TimesTriggered,
            simple?.RepeatInterval,
            simple?.RepeatCount);
    }

    /// <summary>
    /// Resolves an operator-supplied timezone id. Unknown and malformed ids are CALLER
    /// error, so they answer like every other refusal — 400 with the reason — instead of
    /// escaping as an unhandled <see cref="TimeZoneNotFoundException"/> that the platform
    /// renders as a 500 with nothing an operator can act on. (Found on the R2 PR; the
    /// already-frozen `reschedule` verb had the same hole since it shipped.)
    /// </summary>
    private static bool TryResolveZone(string? timeZoneId, out TimeZoneInfo? zone, out string? refusal)
    {
        zone = null;
        refusal = null;
        if (timeZoneId is null)
        {
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            refusal = $"'{timeZoneId}' is not a timezone this host knows";
            return false;
        }
    }

    /// <summary>The fleet's scheduler as it sees itself, with its live cluster nodes (R2.1).</summary>
    public async Task<GoldpathFleetStatus?> GetFleetStatusAsync(string fleet, CancellationToken ct)
    {
        var fleets = await _registry.GetFleetsAsync(ct);
        var known = fleets.FirstOrDefault(f => f.SchedulerName == fleet);
        if (known is null)
        {
            return null;
        }

        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var meta = await scheduler.GetMetaData(ct);
        // The paused state is read from the STORE's trigger groups, which is where
        // pause-all wrote it — durable, cluster-wide, and true no matter which member
        // answers this call.
        var paused = (await scheduler.GetPausedTriggerGroups(ct)).Contains(GoldpathJobsExtensions.JobGroup);
        return new GoldpathFleetStatus(
            known.SchedulerName,
            known.JobCount,
            paused,
            known.Nodes,
            new GoldpathFleetConnection(
                scheduler.SchedulerInstanceId,
                meta.RunningSince,
                meta.ThreadPoolSize,
                meta.NumberOfJobsExecuted,
                meta.Shutdown,
                meta.InStandbyMode));
    }

    /// <summary>
    /// Adds a trigger to a DECLARED job (R2.5). Scheduling is not authoring: this cannot
    /// bring a job into existence — an unknown job name is refused, which is the whole
    /// point (ADR-0001 keeps job definitions in the manifest and the code).
    /// </summary>
    public async Task<GoldpathAdminResult> AddTriggerAsync(string fleet, string job, string name, GoldpathTriggerSpec spec, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GoldpathAdminResult(false, "a trigger needs a name");
        }

        if ((spec.Cron is null) == (spec.Interval is null))
        {
            return new GoldpathAdminResult(false, "give exactly one of cron or interval — a trigger is one kind or the other");
        }

        if (spec.Cron is { } cron && !CronExpression.IsValidExpression(cron))
        {
            return new GoldpathAdminResult(false, $"'{cron}' is not a valid Quartz cron expression");
        }

        if (!TryResolveZone(spec.TimeZoneId, out var zone, out var zoneRefusal))
        {
            return new GoldpathAdminResult(false, zoneRefusal!);
        }

        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var jobKey = new JobKey(job, GoldpathJobsExtensions.JobGroup);
        if (!await scheduler.CheckExists(jobKey, ct))
        {
            return new GoldpathAdminResult(false, $"no job '{job}' in fleet '{fleet}' — a trigger schedules a DECLARED job, it does not create one");
        }

        var triggerKey = new TriggerKey(name, GoldpathJobsExtensions.JobGroup);
        if (await scheduler.CheckExists(triggerKey, ct))
        {
            return new GoldpathAdminResult(false, $"trigger '{name}' already exists — remove it or reschedule it");
        }

        var builder = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey).WithPriority(spec.Priority);
        if (spec.Cron is { } expression)
        {
            builder = builder.WithCronSchedule(expression, schedule =>
            {
                if (zone is not null)
                {
                    schedule.InTimeZone(zone);
                }
            });
        }
        else
        {
            builder = builder.WithSimpleSchedule(schedule =>
            {
                schedule.WithInterval(spec.Interval!.Value);
                // No repeat count means "forever" — the Quartz default an operator
                // expects from an interval trigger.
                if (spec.RepeatCount is { } repeats)
                {
                    schedule.WithRepeatCount(repeats);
                }
                else
                {
                    schedule.RepeatForever();
                }
            });
        }

        if (spec.CalendarName is { } calendar)
        {
            builder = builder.ModifiedByCalendar(calendar);
        }

        await scheduler.ScheduleJob(builder.Build(), ct);
        await AuditAsync(actor, "add-trigger", fleet, job, $"{name}: {spec.Cron ?? spec.Interval?.ToString()}", ct);
        return new GoldpathAdminResult(true, $"trigger '{name}' scheduled");
    }

    /// <summary>Removes one trigger from a declared job (R2.5). The JOB is untouched.</summary>
    public async Task<GoldpathAdminResult> RemoveTriggerAsync(string fleet, string job, string name, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var triggerKey = new TriggerKey(name, GoldpathJobsExtensions.JobGroup);
        var trigger = await scheduler.GetTrigger(triggerKey, ct);
        if (trigger is null)
        {
            return new GoldpathAdminResult(false, $"no trigger '{name}' in fleet '{fleet}'");
        }

        // A trigger names its job; removing one through another job's route would let a
        // typo unschedule something else entirely.
        if (!string.Equals(trigger.JobKey.Name, job, StringComparison.Ordinal))
        {
            return new GoldpathAdminResult(false, $"trigger '{name}' belongs to job '{trigger.JobKey.Name}', not '{job}'");
        }

        await scheduler.UnscheduleJob(triggerKey, ct);
        await AuditAsync(actor, "remove-trigger", fleet, job, name, ct);
        return new GoldpathAdminResult(true, $"trigger '{name}' removed — the job itself is untouched");
    }

    /// <summary>
    /// Runs of a fleet, newest first (R2.4). Filters answer the questions an operator
    /// actually asks — "yesterday's failures", "this job since 06:00" — instead of making
    /// them scroll a take-bounded list until the rows run out.
    /// <para>
    /// Paging is KEYSET, not offset: runs are inserted at the head while the list is being
    /// read, and every offset page after the first would then skip or repeat rows.
    /// <paramref name="afterId"/> names the last row the caller saw; the walk continues
    /// strictly after it in (StartedAt desc, Id desc) order — the tuple, not StartedAt
    /// alone, because two runs of a fleet can start in the same instant and a single-column
    /// cursor would drop one of them for good.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GoldpathJobRun>> GetRunsAsync(
        string fleet,
        string? job,
        int take,
        CancellationToken ct,
        string[]? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? afterId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var runs = db.Set<GoldpathJobRun>().AsNoTracking()
            .Where(r => r.SchedulerName == fleet && (job == null || r.JobName == job));

        if (status is { Length: > 0 })
        {
            // Several states are OR'd (contract R3); one state behaves exactly as R2 did.
            runs = runs.Where(r => status.Contains(r.Status));
        }

        if (from is { } start)
        {
            runs = runs.Where(r => r.StartedAt >= start);
        }

        if (to is { } end)
        {
            runs = runs.Where(r => r.StartedAt <= end);
        }

        if (afterId is { } cursor)
        {
            var anchor = await db.Set<GoldpathJobRun>().AsNoTracking()
                .Where(r => r.Id == cursor)
                .Select(r => new { r.StartedAt, r.Id })
                .FirstOrDefaultAsync(ct);
            // A cursor that names no row would otherwise silently return page one again,
            // which reads as "the list restarted" — an empty page says the walk is over.
            if (anchor is null)
            {
                return [];
            }

            runs = runs.Where(r => r.StartedAt < anchor.StartedAt
                || (r.StartedAt == anchor.StartedAt && r.Id.CompareTo(anchor.Id) < 0));
        }

        return await runs
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    /// <summary>One run with chunk breakdown and its OPEN repair queue.</summary>
    public async Task<GoldpathRunDetail?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var run = await db.Set<GoldpathJobRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return null;
        }

        var chunks = await db.Set<GoldpathJobRunChunk>().AsNoTracking()
            .Where(c => c.RunId == runId)
            .GroupBy(c => c.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, StringComparer.Ordinal, ct);
        var failures = await db.Set<GoldpathJobItemFailure>().AsNoTracking()
            .Where(f => f.RunId == runId && f.RedrivenAt == null)
            .OrderBy(f => f.Id)
            .Take(200)
            .ToListAsync(ct);
        return new GoldpathRunDetail(run, chunks, failures);
    }

    /// <summary>Fires the job now; dry-run reports what WOULD happen without firing.</summary>
    public async Task<GoldpathAdminResult> TriggerAsync(string fleet, string job, bool dryRun, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var key = new JobKey(job, GoldpathJobsExtensions.JobGroup);
        if (!await scheduler.CheckExists(key, ct))
        {
            return new GoldpathAdminResult(false, $"no job '{job}' in fleet '{fleet}'");
        }

        if (dryRun)
        {
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var next = triggers.Select(t => t.GetNextFireTimeUtc()).Where(t => t.HasValue).Min();
            return new GoldpathAdminResult(true, next is { } n
                ? $"dry-run: would fire now; next scheduled fire {n:O}"
                : "dry-run: would fire now; no scheduled trigger (ad-hoc job)");
        }

        await scheduler.TriggerJob(key, StampTraceParent(new JobDataMap
        {
            [GoldpathJobsExtensions.TriggeredByKey] = GoldpathJobTriggeredBy.Manual,
        }), ct);
        await AuditAsync(actor, "trigger", fleet, job, null, ct);
        return new GoldpathAdminResult(true, "triggered");
    }

    /// <summary>Pauses every trigger of one job (cluster-wide, via the store).</summary>
    public async Task<GoldpathAdminResult> PauseJobAsync(string fleet, string job, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var key = new JobKey(job, GoldpathJobsExtensions.JobGroup);
        if (!await scheduler.CheckExists(key, ct))
        {
            return new GoldpathAdminResult(false, $"no job '{job}' in fleet '{fleet}'");
        }

        await scheduler.PauseJob(key, ct);
        await AuditAsync(actor, "pause", fleet, job, null, ct);
        return new GoldpathAdminResult(true, "paused");
    }

    /// <summary>Resumes a paused job.</summary>
    public async Task<GoldpathAdminResult> ResumeJobAsync(string fleet, string job, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var key = new JobKey(job, GoldpathJobsExtensions.JobGroup);
        if (!await scheduler.CheckExists(key, ct))
        {
            return new GoldpathAdminResult(false, $"no job '{job}' in fleet '{fleet}'");
        }

        await scheduler.ResumeJob(key, ct);
        await AuditAsync(actor, "resume", fleet, job, null, ct);
        return new GoldpathAdminResult(true, "resumed");
    }

    /// <summary>Fleet-wide stop/go: pauses or resumes EVERY trigger in the fleet (audited).</summary>
    public async Task<GoldpathAdminResult> SetFleetPausedAsync(string fleet, bool paused, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        if (paused)
        {
            await scheduler.PauseAll(ct);
        }
        else
        {
            await scheduler.ResumeAll(ct);
        }

        await AuditAsync(actor, paused ? "pause-all" : "resume-all", fleet, "*", null, ct);
        return new GoldpathAdminResult(true, paused ? "fleet paused" : "fleet resumed");
    }

    /// <summary>
    /// Runtime schedule override (RFC D7): the DEFINITION stays in code, the CRON is an
    /// audited ops decision — "run at 03:00 tonight" never waits for an MR.
    /// </summary>
    public async Task<GoldpathAdminResult> RescheduleAsync(string fleet, string job, string cron, string? timeZoneId, string actor, CancellationToken ct)
    {
        if (!CronExpression.IsValidExpression(cron))
        {
            return new GoldpathAdminResult(false, $"'{cron}' is not a valid Quartz cron expression");
        }

        if (!TryResolveZone(timeZoneId, out var zone, out var zoneRefusal))
        {
            return new GoldpathAdminResult(false, zoneRefusal!);
        }

        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        var jobKey = new JobKey(job, GoldpathJobsExtensions.JobGroup);
        if (!await scheduler.CheckExists(jobKey, ct))
        {
            return new GoldpathAdminResult(false, $"no job '{job}' in fleet '{fleet}'");
        }

        var triggerKey = new TriggerKey($"{job}-cron", GoldpathJobsExtensions.JobGroup);
        var existing = await scheduler.GetTrigger(triggerKey, ct);
        var builder = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, s =>
            {
                if (zone is not null)
                {
                    s.InTimeZone(zone);
                }
            });
        if (existing?.CalendarName is { } calendar)
        {
            builder = builder.ModifiedByCalendar(calendar);
        }

        var trigger = builder.Build();
        if (existing is null)
        {
            await scheduler.ScheduleJob(trigger, ct);
        }
        else
        {
            await scheduler.RescheduleJob(triggerKey, trigger, ct);
        }

        var oldCron = (existing as ICronTrigger)?.CronExpressionString ?? "<none>";
        await AuditAsync(actor, "reschedule", fleet, job, $"{oldCron} -> {cron}", ct);
        return new GoldpathAdminResult(true, $"rescheduled: {oldCron} -> {cron}");
    }

    /// <summary>Re-fires a TERMINAL run's job; refuses while a run is open (never a double-run).</summary>
    public async Task<GoldpathAdminResult> RerunAsync(Guid runId, string actor, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var run = await db.Set<GoldpathJobRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return new GoldpathAdminResult(false, "no such run");
        }

        if (run.Status == GoldpathJobRunStatus.Running)
        {
            return new GoldpathAdminResult(false, "the run is still open — resume happens on the next fire, not through rerun");
        }

        var openRun = await db.Set<GoldpathJobRun>().AsNoTracking().AnyAsync(r =>
            r.SchedulerName == run.SchedulerName && r.JobName == run.JobName && r.Status == GoldpathJobRunStatus.Running, ct);
        if (openRun)
        {
            return new GoldpathAdminResult(false, "another run of this job is open — rerun would double-run");
        }

        var scheduler = await _registry.GetSchedulerAsync(run.SchedulerName, ct);
        await scheduler.TriggerJob(new JobKey(run.JobName, GoldpathJobsExtensions.JobGroup), StampTraceParent(new JobDataMap
        {
            [GoldpathJobsExtensions.TriggeredByKey] = GoldpathJobTriggeredBy.Rerun,
        }), ct);
        await AuditAsync(actor, "rerun", run.SchedulerName, run.JobName, $"after run {runId}", ct);
        return new GoldpathAdminResult(true, "triggered a fresh run");
    }

    /// <summary>
    /// Redrives OPEN repair items through the job's <see cref="IGoldpathItemReplay"/> hook on an
    /// EXECUTOR (the type lives there, not here): a one-off fire carries the item keys in
    /// its data map. Items without the hook fail loudly on the executor, never silently.
    /// </summary>
    public async Task<GoldpathAdminResult> ReplayItemsAsync(Guid runId, string actor, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var run = await db.Set<GoldpathJobRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return new GoldpathAdminResult(false, "no such run");
        }

        var openItems = await db.Set<GoldpathJobItemFailure>().AsNoTracking()
            .Where(f => f.RunId == runId && f.RedrivenAt == null)
            .Select(f => f.ItemKey)
            .Take(500)
            .ToListAsync(ct);
        if (openItems.Count == 0)
        {
            return new GoldpathAdminResult(false, "the repair queue of this run is empty");
        }

        var scheduler = await _registry.GetSchedulerAsync(run.SchedulerName, ct);
        var data = StampTraceParent(new JobDataMap
        {
            [GoldpathJobsExtensions.ReplayRunKey] = runId.ToString(),
            [GoldpathJobsExtensions.TriggeredByKey] = GoldpathJobTriggeredBy.Replay,
        });
        await scheduler.TriggerJob(new JobKey(run.JobName, GoldpathJobsExtensions.JobGroup), data, ct);
        await AuditAsync(actor, "replay-items", run.SchedulerName, run.JobName, $"{openItems.Count} items of run {runId}", ct);
        return new GoldpathAdminResult(true, $"replay fire queued for {openItems.Count} open items");
    }

    /// <summary>
    /// Stamps the caller's W3C traceparent into the fire's data map — the only vehicle
    /// that crosses the Quartz store, so the run span can link back to the request.
    /// </summary>
    private static JobDataMap StampTraceParent(JobDataMap data)
    {
        if (Activity.Current?.Id is { } traceParent)
        {
            data[GoldpathJobsExtensions.TraceParentKey] = traceParent;
        }

        return data;
    }

    /// <summary>The fleet's calendars with the triggers riding them.</summary>
    public async Task<IReadOnlyList<GoldpathCalendarInfo>> GetCalendarsAsync(string fleet, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var usage = await db.Set<QrtzTrigger>().AsNoTracking()
            .Where(t => t.SchedName == fleet && t.CalendarName != null)
            .Select(t => new { t.CalendarName, t.TriggerName })
            .ToListAsync(ct);

        var calendars = new List<GoldpathCalendarInfo>();
        foreach (var name in await scheduler.GetCalendarNames(ct))
        {
            var calendar = await scheduler.GetCalendar(name, ct);
            calendars.Add(new GoldpathCalendarInfo(name, calendar?.Description,
                usage.Where(u => u.CalendarName == name).Select(u => u.TriggerName).ToList()));
        }

        return calendars;
    }

    /// <summary>Creates or replaces a calendar (holiday | weekly | cron), updating riding triggers.</summary>
    public async Task<GoldpathAdminResult> PutCalendarAsync(string fleet, string name, GoldpathCalendarSpec spec, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // The name keys a NOT NULL qrtz_calendars row, and '' is NULL on Oracle (#476); a blank one names nothing anyway.
            return new GoldpathAdminResult(false, "a calendar needs a name — triggers refer to it by that name");
        }

        Quartz.ICalendar calendar;
        switch (spec.Type.ToUpperInvariant())
        {
            case "HOLIDAY":
                var holiday = new Quartz.Impl.Calendar.HolidayCalendar { Description = spec.Description };
                foreach (var date in spec.ExcludedDates ?? [])
                {
                    holiday.AddExcludedDate(date.Date);
                }

                calendar = holiday;
                break;
            case "WEEKLY":
                var weekly = new Quartz.Impl.Calendar.WeeklyCalendar { Description = spec.Description };
                foreach (var day in spec.ExcludedDays ?? [])
                {
                    weekly.SetDayExcluded(day, true);
                }

                calendar = weekly;
                break;
            case "CRON" when spec.CronExpression is not null && CronExpression.IsValidExpression(spec.CronExpression):
                calendar = new Quartz.Impl.Calendar.CronCalendar(spec.CronExpression) { Description = spec.Description };
                break;
            default:
                return new GoldpathAdminResult(false, "calendar type must be holiday|weekly|cron (cron needs a valid expression)");
        }

        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        await scheduler.AddCalendar(name, calendar, replace: true, updateTriggers: true, ct);
        await AuditAsync(actor, "calendar-put", fleet, name, spec.Type, ct);
        return new GoldpathAdminResult(true, "calendar stored");
    }

    /// <summary>Deletes a calendar (refused by the store while triggers ride it).</summary>
    public async Task<GoldpathAdminResult> DeleteCalendarAsync(string fleet, string name, string actor, CancellationToken ct)
    {
        var scheduler = await _registry.GetSchedulerAsync(fleet, ct);
        try
        {
            if (!await scheduler.DeleteCalendar(name, ct))
            {
                return new GoldpathAdminResult(false, $"no calendar '{name}' in fleet '{fleet}'");
            }
        }
        catch (SchedulerException exception)
        {
            return new GoldpathAdminResult(false, exception.Message);   // e.g. "calendar is referenced by a trigger"
        }

        await AuditAsync(actor, "calendar-delete", fleet, name, null, ct);
        return new GoldpathAdminResult(true, "calendar deleted");
    }

    /// <summary>The audit trail of admin verbs, newest first.</summary>
    public async Task<IReadOnlyList<GoldpathJobAdminAudit>> GetAuditAsync(int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathJobAdminAudit>().AsNoTracking()
            .OrderByDescending(a => a.At)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    private async Task AuditAsync(string actor, string action, string fleet, string target, string? detail, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(new GoldpathJobAdminAudit
        {
            At = _time.GetUtcNow(),
            Actor = actor,
            Action = action,
            Fleet = fleet,
            Target = target,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
