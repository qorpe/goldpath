using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace Goldpath;

/// <summary>
/// Composition entry points (jobs RFC D2/D9): a worker registers EXECUTOR mode (its own
/// cluster, thread pool on); the solution's API head registers MANAGEMENT mode (same
/// store, thread pool 0 — can verb everything, can execute nothing). Call
/// <c>modelBuilder.AddGoldpathJobs()</c> in the context's <c>OnModelCreating</c> so the store
/// and the run model ride normal migrations.
/// </summary>
public static class GoldpathJobsExtensions
{
    /// <summary>The Quartz group every Goldpath job/trigger lives under.</summary>
    public const string JobGroup = "goldpath";

    /// <summary>Data-map key marking a fire as an admin replay of a run's repair items.</summary>
    public const string ReplayRunKey = "goldpath:replay-run";

    /// <summary>
    /// Data-map key carrying the W3C traceparent of the request that caused a fire — the
    /// run span links to it, tying the operator's trace to the run's trace.
    /// </summary>
    public const string TraceParentKey = "goldpath:traceparent";

    /// <summary>
    /// Data-map key carrying WHO put this fire on the schedule (R2.3). Stamped by the
    /// admin verbs; its absence is the definition of a scheduled fire, so the scheduler
    /// path needs no stamp of its own.
    /// </summary>
    public const string TriggeredByKey = "goldpath:triggered-by";

    /// <summary>
    /// Executor mode: registers the clustered scheduler, the run engine, the history
    /// listener and every configured job with its schedule.
    /// </summary>
    public static TBuilder AddGoldpathJobs<TBuilder, TContext>(this TBuilder builder, Action<GoldpathJobsOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
        where TContext : DbContext
        => AddCore<TBuilder, TContext>(builder, configure, executor: true);

    /// <summary>
    /// Management mode for the solution's API head: joins the store with thread pool 0 —
    /// full visibility and verbs over every worker fleet, zero execution. Fleets are
    /// DISCOVERED from the store; deploying a new worker kind changes nothing here.
    /// </summary>
    public static TBuilder AddGoldpathJobsManagement<TBuilder, TContext>(this TBuilder builder, Action<GoldpathJobsOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
        where TContext : DbContext
        => AddCore<TBuilder, TContext>(builder, configure, executor: false);

    private static TBuilder AddCore<TBuilder, TContext>(TBuilder builder, Action<GoldpathJobsOptions>? configure, bool executor)
        where TBuilder : IHostApplicationBuilder
        where TContext : DbContext
    {
        var options = new GoldpathJobsOptions();
        builder.Configuration.GetSection("Goldpath:Jobs").Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IGoldpathJobRunner, GoldpathJobRunner<TContext>>();
        builder.Services.AddSingleton<GoldpathJobHistoryListener<TContext>>();

        // Connection strings come from the AppHost; under build-time tooling (OpenAPI
        // export) they are absent — stay tolerant, the scheduler simply does not start.
        var connectionString = builder.Configuration.GetConnectionString(
            options.ConnectionName.Length > 0 ? options.ConnectionName : "jobs");
        if (connectionString is null)
        {
            return builder;
        }

        // The management surface exists in BOTH modes: a worker manages its own fleet, the
        // API head manages every fleet — same registry, same store-driven discovery (D9).
        builder.Services.TryAddSingleton<IGoldpathJobsFleetRegistry>(sp => new GoldpathJobsFleetRegistry<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(), connectionString, options.Provider));
        builder.Services.TryAddSingleton<GoldpathJobsAdminService<TContext>>();

        if (!executor)
        {
            // Management members run NO scheduler at all: fleets are discovered from the
            // store, verbs go through on-demand never-started schedulers — zero cluster
            // noise, zero risk of competing for fires.
            return builder;
        }

        foreach (var definition in options.Jobs)
        {
            builder.Services.TryAddScoped(definition.JobType);
        }

        builder.Services.AddQuartz(quartz =>
        {
            quartz.SchedulerName = options.SchedulerName;
            quartz.SchedulerId = "AUTO";

            quartz.UseDefaultThreadPool(tp => tp.MaxConcurrency = Math.Max(1, options.MaxConcurrency));

            quartz.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.RetryInterval = TimeSpan.FromSeconds(15);
                // Every provider is its own case: an unknown value must never quietly become a PostgreSQL store.
                switch (options.Provider)
                {
                    case GoldpathJobStoreProvider.Postgres:
                        store.UsePostgres(pg => pg.ConnectionString = connectionString);
                        break;
                    case GoldpathJobStoreProvider.SqlServer:
                        store.UseSqlServer(sql => sql.ConnectionString = connectionString);
                        break;
                    case GoldpathJobStoreProvider.Oracle:
                        store.UseOracle(oracle => oracle.ConnectionString = connectionString);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Goldpath:Jobs:Provider '{options.Provider}' is not a store this module knows (Postgres, SqlServer, Oracle).");
                }

                store.UseSystemTextJsonSerializer();
                store.UseClustering(cluster =>
                {
                    cluster.CheckinInterval = options.CheckinInterval;
                    cluster.CheckinMisfireThreshold = options.CheckinMisfireThreshold;
                });
            });

            quartz.MisfireThreshold = options.MisfireThreshold;
            quartz.AddJobListener<GoldpathJobHistoryListener<TContext>>();

            foreach (var definition in options.Jobs)
            {
                var jobKey = new JobKey(definition.Name, JobGroup);
                var adapterType = typeof(GoldpathQuartzAdapter<>).MakeGenericType(definition.JobType);
                quartz.AddJob(adapterType, jobKey, job => job
                    .WithIdentity(jobKey)
                    .StoreDurably()
                    .RequestRecovery());   // a dead node's fire re-fires elsewhere; the runner RESUMES

                if (definition.Cron is { } cron)
                {
                    quartz.AddTrigger(trigger =>
                    {
                        trigger.ForJob(jobKey)
                            .WithIdentity($"{definition.Name}-cron", JobGroup)
                            .WithCronSchedule(cron, schedule =>
                            {
                                if (definition.TimeZoneId is { } timeZone)
                                {
                                    schedule.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(timeZone));
                                }
                            });
                        if (definition.CalendarName is { } calendar)
                        {
                            trigger.ModifiedByCalendar(calendar);
                        }
                    });
                }
            }

            foreach (var (name, calendar) in options.Calendars)
            {
                quartz.AddCalendar(name, calendar, replace: true, updateTriggers: true);
            }
        });

        builder.Services.AddQuartzHostedService(hosted =>
        {
            hosted.WaitForJobsToComplete = true;   // drain: finish the chunk, checkpoint, hand over
            hosted.AwaitApplicationStarted = true;
        });

        return builder;
    }
}
