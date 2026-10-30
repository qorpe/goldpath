using Quartz;

namespace Goldpath;

/// <summary>Where the clustered store lives.</summary>
public enum GoldpathJobStoreProvider
{
    /// <summary>PostgreSQL (golden-path default).</summary>
    Postgres,

    /// <summary>SQL Server.</summary>
    SqlServer,

    /// <summary>
    /// Oracle (G7). Quartz's <c>OracleDelegate</c> over ODP.NET managed: the application references
    /// <c>Oracle.ManagedDataAccess.Core</c>, which Quartz loads by name; this package takes no Oracle dependency.
    /// </summary>
    Oracle = 2,
}

/// <summary>
/// Module options (bound from <c>Goldpath:Jobs</c>). The scheduler name IS the cluster identity:
/// one scheduler per worker KIND (jobs RFC D9) — instances sharing the name form the
/// failover cluster; different kinds share the tables, separated by name.
/// </summary>
public sealed class GoldpathJobsOptions
{
    /// <summary>Cluster identity. Default: the entry assembly's name.</summary>
    public string SchedulerName { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "goldpath-jobs";

    /// <summary>The app connection the store rides (the app database — zero new infra).</summary>
    public string ConnectionName { get; set; } = "";

    /// <summary>Store provider (golden path: postgres or sqlserver — jobs RFC D10).</summary>
    public GoldpathJobStoreProvider Provider { get; set; } = GoldpathJobStoreProvider.Postgres;

    /// <summary>Executor thread count. Management mode forces 0 (can verb, cannot execute).</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Cluster check-in cadence — bounds failover detection time.</summary>
    public TimeSpan CheckinInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How stale a check-in must be before the node counts as dead.</summary>
    public TimeSpan CheckinMisfireThreshold { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Misfire threshold for triggers (see the runbook for per-policy semantics).</summary>
    public TimeSpan MisfireThreshold { get; set; } = TimeSpan.FromSeconds(60);

    internal List<GoldpathJobDefinition> JobList { get; } = [];
    internal Dictionary<string, ICalendar> CalendarMap { get; } = new(StringComparer.Ordinal);

    /// <summary>The registered job definitions (the management surface reads these).</summary>
    public IReadOnlyList<GoldpathJobDefinition> Jobs => JobList;

    /// <summary>The registered calendars by name.</summary>
    public IReadOnlyDictionary<string, ICalendar> Calendars => CalendarMap;

    /// <summary>Registers a job with its schedule and run shape.</summary>
    public GoldpathJobsOptions AddJob<TJob>(Action<GoldpathJobBuilder<TJob>>? configure = null)
        where TJob : class, IGoldpathJob
    {
        var builder = new GoldpathJobBuilder<TJob>();
        configure?.Invoke(builder);
        JobList.Add(builder.Definition);
        return this;
    }

    /// <summary>Registers a named Quartz calendar (see <see cref="GoldpathCalendars"/> factories).</summary>
    public GoldpathJobsOptions AddCalendar(string name, ICalendar calendar)
    {
        CalendarMap[name] = calendar;
        return this;
    }
}

/// <summary>Everything the runner knows about one registered job.</summary>
public sealed class GoldpathJobDefinition
{
    internal GoldpathJobDefinition(Type jobType)
    {
        JobType = jobType;
        Name = jobType.Name;
    }

    /// <summary>The authored job type.</summary>
    public Type JobType { get; }

    /// <summary>Job name (defaults to the type name; also the Quartz job key).</summary>
    public string Name { get; internal set; }

    /// <summary>Quartz cron expression; null = ad-hoc only (triggered by verb or chain).</summary>
    public string? Cron { get; internal set; }

    /// <summary>IANA/Windows timezone for the cron schedule (null = UTC).</summary>
    public string? TimeZoneId { get; internal set; }

    /// <summary>Named calendar excluding days/times (holidays, business days).</summary>
    public string? CalendarName { get; internal set; }

    /// <summary>SLA for one run — prediction compares against it (GP0502 warns when absent).</summary>
    public TimeSpan? Deadline { get; internal set; }

    /// <summary>Parallel chunk claims within the executing node.</summary>
    public int MaxParallelChunks { get; internal set; } = 1;

    /// <summary>Attempts per chunk before it is isolated as failed.</summary>
    public int MaxChunkAttempts { get; internal set; } = 3;

    /// <summary>Optional pause between chunk claims — the crude-but-honest throttle.</summary>
    public TimeSpan? InterChunkDelay { get; internal set; }

    /// <summary>Predecessor jobs whose COMPLETED runs trigger this job (chaining v1 — jobs RFC D6).</summary>
    public List<string> StartAfterJobs { get; } = [];

    /// <summary>Captures the pinned input version at run start (mid-run deploys never mix inputs).</summary>
    public Func<IServiceProvider, string>? InputVersionFactory { get; internal set; }
}

/// <summary>Fluent shape for one job registration.</summary>
public sealed class GoldpathJobBuilder<TJob>
    where TJob : class, IGoldpathJob
{
    internal GoldpathJobDefinition Definition { get; } = new(typeof(TJob));

    /// <summary>Quartz cron expression (e.g. <c>"0 30 1 * * ?"</c>).</summary>
    public string? Cron
    {
        get => Definition.Cron;
        set => Definition.Cron = value;
    }

    /// <summary>IANA/Windows timezone id for the schedule.</summary>
    public string? TimeZoneId
    {
        get => Definition.TimeZoneId;
        set => Definition.TimeZoneId = value;
    }

    /// <summary>Named calendar registered via <c>AddCalendar</c>.</summary>
    public string? Calendar
    {
        get => Definition.CalendarName;
        set => Definition.CalendarName = value;
    }

    /// <summary>SLA for one run (predicted overrun alerts before the deadline hits).</summary>
    public TimeSpan? Deadline
    {
        get => Definition.Deadline;
        set => Definition.Deadline = value;
    }

    /// <summary>Parallel chunk claims within the node.</summary>
    public int MaxParallelChunks
    {
        get => Definition.MaxParallelChunks;
        set => Definition.MaxParallelChunks = value;
    }

    /// <summary>Attempts per chunk before isolation.</summary>
    public int MaxChunkAttempts
    {
        get => Definition.MaxChunkAttempts;
        set => Definition.MaxChunkAttempts = value;
    }

    /// <summary>Pause between chunk claims (interactive-path protection).</summary>
    public TimeSpan? InterChunkDelay
    {
        get => Definition.InterChunkDelay;
        set => Definition.InterChunkDelay = value;
    }

    /// <summary>Starts this job when a run of <typeparamref name="TPredecessor"/> completes.</summary>
    public GoldpathJobBuilder<TJob> StartAfter<TPredecessor>()
        where TPredecessor : class, IGoldpathJob
    {
        Definition.StartAfterJobs.Add(typeof(TPredecessor).Name);
        return this;
    }

    /// <summary>Pins an input version at run start; jobs read it via the run context.</summary>
    public GoldpathJobBuilder<TJob> PinInput(Func<IServiceProvider, string> versionFactory)
    {
        Definition.InputVersionFactory = versionFactory;
        return this;
    }
}
