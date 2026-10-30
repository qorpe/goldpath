using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Goldpath.Jobs.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.EntityFrameworkCore.Infrastructure;
using Oracle.ManagedDataAccess.Client;
using Quartz;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The clustered Quartz store on Oracle (G7, slice O-3, #406; RFC goldpath-oracle §2.3, §8): the <c>qrtz_</c> model
/// against Quartz's own <c>tables_oracle.sql</c>, and the cluster's proofs of <see cref="JobsClusterTests"/> on a real
/// Oracle container — exactly one fire across two schedulers, the misfire instruction honoured, and a kill -9 recovered
/// on the other node.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class JobsOracleTests(OracleFixture oracle, ITestOutputHelper output)
{
    // ---- the model against Quartz's own DDL ----

    [Fact]
    public void The_store_model_on_oracle_is_quartz_own_ddl_by_name_and_by_type()
    {
        var quartz = QuartzOracleDdl();
        var model = StoreColumns(OracleModel());

        // Every table and column the model creates is one OracleDelegate names, spelled as its unquoted SQL folds.
        Assert.Equal(quartz.Keys.Order(StringComparer.Ordinal), model.Keys.Order(StringComparer.Ordinal));
        foreach (var (table, columns) in model)
        {
            Assert.All(columns.Keys, column => Assert.Contains(column, quartz[table].Keys));
        }

        // Every column is the type family Quartz declares: the flags are VARCHAR2(1) and the binaries BLOB exactly, as
        // OracleDelegate reads and writes them; a string is national (NVARCHAR2) where Quartz's is VARCHAR2, and a
        // number is NUMBER(p) — both read and bound the same way by ODP.NET.
        foreach (var (table, columns) in model)
        {
            foreach (var (column, type) in columns)
            {
                Assert.True(Family(type) == Family(quartz[table][column]), $"{table}.{column}: {type}, where Quartz declares {quartz[table][column]}");
            }
        }

        // Where the model is NARROWER than Quartz's DDL, each column is listed: the names and groups are 150 on every
        // provider (the jobs model's lengths), and a priority is a .NET int, which NUMBER(10) holds.
        var narrower = model
            .SelectMany(t => t.Value.Where(c => Width(c.Value) < Width(quartz[t.Key][c.Key])).Select(c => $"{t.Key}.{c.Key} {c.Value} < {quartz[t.Key][c.Key]}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var column in narrower)
        {
            output.WriteLine(column);
        }

        Assert.Equal(ExpectedNarrower.Order(StringComparer.Ordinal), narrower);

        // The columns Quartz's DDL carries and the model does not: Quartz 3.x never reads or writes them.
        var missing = quartz
            .SelectMany(t => t.Value.Keys.Where(c => !model[t.Key].ContainsKey(c)).Select(c => $"{t.Key}.{c}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["QRTZ_FIRED_TRIGGERS.EXECUTION_GROUP", "QRTZ_TRIGGERS.EXECUTION_GROUP", "QRTZ_TRIGGERS.MISFIRE_ORIG_FIRE_TIME",
             "QRTZ_TRIGGERS.PREFERRED_NODE", "QRTZ_TRIGGERS.PREFERRED_NODE_AUTO"],
            missing);
    }

    /// <summary>The columns narrower than Quartz's own DDL, each a deliberate length of the jobs model on every provider.</summary>
    private static readonly string[] ExpectedNarrower =
    [
        .. new[] { "QRTZ_BLOB_TRIGGERS", "QRTZ_CRON_TRIGGERS", "QRTZ_SIMPLE_TRIGGERS", "QRTZ_SIMPROP_TRIGGERS", "QRTZ_TRIGGERS", "QRTZ_FIRED_TRIGGERS" }
            .SelectMany(t => new[] { $"{t}.TRIGGER_GROUP NVARCHAR2(150) < VARCHAR2(200)", $"{t}.TRIGGER_NAME NVARCHAR2(150) < VARCHAR2(200)" }),
        .. new[] { "QRTZ_JOB_DETAILS", "QRTZ_TRIGGERS", "QRTZ_FIRED_TRIGGERS" }
            .SelectMany(t => new[] { $"{t}.JOB_GROUP NVARCHAR2(150) < VARCHAR2(200)", $"{t}.JOB_NAME NVARCHAR2(150) < VARCHAR2(200)" }),
        "QRTZ_PAUSED_TRIGGER_GRPS.TRIGGER_GROUP NVARCHAR2(150) < VARCHAR2(200)",
        "QRTZ_TRIGGERS.PRIORITY NUMBER(10) < NUMBER(13)",
        "QRTZ_FIRED_TRIGGERS.PRIORITY NUMBER(10) < NUMBER(13)",
    ];

    private static string Family(string type) => Regex.Match(type, "^[A-Z0-9]+").Value switch
    {
        "VARCHAR2" or "NVARCHAR2" when type.EndsWith("(1)", StringComparison.Ordinal) && !type.StartsWith('N') => "flag",
        "VARCHAR2" or "NVARCHAR2" => "string",
        "NUMBER" or "NUMERIC" or "DECIMAL" => "number",
        var other => other,
    };

    private static int Width(string type)
    {
        var width = Regex.Match(type, @"\((\d+)");
        return width.Success ? int.Parse(width.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
    }

    [Fact]
    public void The_postgresql_store_ddl_is_unchanged_by_the_oracle_shape()
    {
        // ShapeForOracle runs on Oracle only: the PostgreSQL DDL of the qrtz_ tables is byte for byte the one main created
        // before O-3 (the golden was generated from main 463e79f, before this change).
        using var db = new StoreDb(new DbContextOptionsBuilder<StoreDb>().UseNpgsql("Host=unused").Options);
        // Compared statement by statement, ordinally sorted: EF orders the script's index statements by a culture-
        // sensitive name comparison, so under tr-TR "IX_…" and "idx_…" swap places while every statement is the same.
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Quartz", "store.postgres.sql"));
        Assert.Equal(Statements(golden), Statements(QrtzStatements(db.Database.GenerateCreateScript())));
    }

    [Fact]
    public void Only_the_oracle_model_takes_the_oracle_shape()
    {
        using var npgsql = new StoreDb(new DbContextOptionsBuilder<StoreDb>().UseNpgsql("Host=unused").Options);
        Assert.Equal("qrtz_job_details", npgsql.Model.GetEntityTypes().Single(e => e.ClrType.Name == "QrtzJobDetail").GetTableName());
        Assert.Equal("QRTZ_JOB_DETAILS", OracleModel().GetEntityTypes().Single(e => e.ClrType.Name == "QrtzJobDetail").GetTableName());
    }

    // ---- the cluster on a real Oracle ----

    [Fact]
    public async Task Two_schedulers_on_one_oracle_store_fire_a_trigger_exactly_once()
    {
        var connection = await ClusterSchemaAsync("GP_QRTZ_ONCE");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var children = new ConcurrentQueue<string>();

        using var nodeA = StartHostProcess(connection, trigger: true, children);
        await WaitUntilAsync(() => Task.FromResult(children.Any(l => l.Contains("TESTHOST-READY", StringComparison.Ordinal))), timeout.Token, children);
        using var nodeB = StartHostProcess(connection, trigger: false, children);
        try
        {
            await WaitUntilAsync(async () => (await QueryRunAsync(connection, nameof(SlowClusterJob)))?.Status == GoldpathJobRunStatus.Completed, timeout.Token, children);
            await WaitUntilAsync(async () => (await QueryRunAsync(connection, nameof(ChainedProofJob)))?.Status == GoldpathJobRunStatus.Completed, timeout.Token, children);

            var run = (await QueryRunAsync(connection, nameof(SlowClusterJob)))!;
            Assert.Equal(1, run.Executions);   // one trigger, two schedulers: one fire
            var sink = await QuerySinkAsync(connection, nameof(SlowClusterJob));
            Assert.Equal(30, sink.Count);
            Assert.Equal(30, sink.Select(s => s.ChunkIndex).Distinct().Count());
            Assert.Single(await QuerySinkAsync(connection, nameof(ChainedProofJob)));   // the successor, once
        }
        finally
        {
            KillQuietly(nodeA);
            KillQuietly(nodeB);
        }
    }

    [Fact]
    public async Task Kill9_midrun_on_oracle_is_recovered_on_the_other_node_from_the_checkpoint()
    {
        var connection = await ClusterSchemaAsync("GP_QRTZ_KILL");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var children = new ConcurrentQueue<string>();

        using var nodeA = StartHostProcess(connection, trigger: true, children);
        await WaitUntilAsync(() => Task.FromResult(children.Any(l => l.Contains("TESTHOST-READY", StringComparison.Ordinal))), timeout.Token, children);
        using var nodeB = StartHostProcess(connection, trigger: false, children);
        try
        {
            await WaitUntilAsync(async () => (await QuerySinkAsync(connection, nameof(SlowClusterJob))).Count >= 3, timeout.Token, children);
            nodeA.Kill(entireProcessTree: true);
            await nodeA.WaitForExitAsync(timeout.Token);

            await WaitUntilAsync(async () => (await QueryRunAsync(connection, nameof(SlowClusterJob)))?.Status == GoldpathJobRunStatus.Completed, timeout.Token, children);
            var completed = (await QueryRunAsync(connection, nameof(SlowClusterJob)))!;
            Assert.Equal(30, completed.CompletedChunks);
            Assert.True(completed.Executions >= 2, "recovery must have consumed a second fire");

            var sink = await QuerySinkAsync(connection, nameof(SlowClusterJob));
            Assert.Equal(30, sink.Select(s => s.ChunkIndex).Distinct().Count());
            Assert.True(sink.GroupBy(s => s.ChunkIndex).Count(g => g.Count() > 1) <= 1, "at most the in-flight chunk may repeat");
            Assert.Equal(2, sink.Select(s => s.Instance).Distinct().Count());   // both nodes really worked
        }
        finally
        {
            KillQuietly(nodeA);
            KillQuietly(nodeB);
        }
    }

    [Fact]
    public async Task A_misfired_trigger_on_oracle_follows_its_misfire_instruction()
    {
        var connection = await ClusterSchemaAsync("GP_QRTZ_MISFIRE");
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:jobsdb"] = connection;
        builder.Services.AddDbContext<ClusterDb>(o => UseOracle(o, connection));
        builder.AddGoldpathJobs<HostApplicationBuilder, ClusterDb>(jobs =>
        {
            jobs.SchedulerName = "it-misfire";
            jobs.ConnectionName = "jobsdb";
            jobs.Provider = GoldpathJobStoreProvider.Oracle;
        });

        using var host = builder.Build();
        try
        {
            // Both triggers were due ten minutes ago, far past the store's 60-second misfire threshold, before the
            // scheduler started. FireNow must fire once; NextWithRemainingCount on a one-shot trigger must not fire.
            var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
            var due = DateTimeOffset.UtcNow.AddMinutes(-10);
            await scheduler.ScheduleJob(
                JobBuilder.Create<RecordingJob>().WithIdentity("fire-now", "misfire").UsingJobData("key", "fire-now").Build(),
                TriggerBuilder.Create().WithIdentity("fire-now", "misfire").StartAt(due)
                    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow()).Build());
            await scheduler.ScheduleJob(
                JobBuilder.Create<RecordingJob>().WithIdentity("skip", "misfire").UsingJobData("key", "skip").Build(),
                TriggerBuilder.Create().WithIdentity("skip", "misfire").StartAt(due)
                    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionNextWithRemainingCount()).Build());

            await host.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await WaitUntilAsync(() => Task.FromResult(RecordingJob.Fired.Contains("fire-now")), timeout.Token, new ConcurrentQueue<string>());
            await Task.Delay(TimeSpan.FromSeconds(5));   // room for a wrong second fire, or a fire of "skip"

            Assert.Equal(["fire-now"], RecordingJob.Fired.ToArray());
            // Misfired past its only fire: the store keeps the row, with no next fire time — it will never fire.
            var skip = await scheduler.GetTrigger(new TriggerKey("skip", "misfire"));
            Assert.Equal(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount, skip!.MisfireInstruction);
            Assert.Null(skip.GetNextFireTimeUtc());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            QuartzProcessGlobals.Pin();
        }
    }

    /// <summary>A plain Quartz job that records which trigger fired it.</summary>
    public sealed class RecordingJob : IJob
    {
        /// <summary>The keys fired, in order.</summary>
        public static ConcurrentQueue<string> Fired { get; } = new();

        /// <inheritdoc />
        public Task Execute(IJobExecutionContext context)
        {
            Fired.Enqueue(context.MergedJobDataMap.GetString("key")!);
            return Task.CompletedTask;
        }
    }

    // ---- helpers ----

    private sealed class StoreDb(DbContextOptions<StoreDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathJobs();
    }

    private static void UseOracle(DbContextOptionsBuilder options, string connection)
        => options.UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19));

    private static IModel OracleModel()
    {
        var options = new DbContextOptionsBuilder<StoreDb>();
        UseOracle(options, "User Id=unused;Password=unused;Data Source=unused");
        using var db = new StoreDb(options.Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>Table → column → store type of every qrtz_ entity in a model.</summary>
    private static Dictionary<string, Dictionary<string, string>> StoreColumns(IModel model)
        => model.GetEntityTypes()
            .Where(e => e.ClrType.Name.StartsWith("Qrtz", StringComparison.Ordinal))
            .ToDictionary(
                e => e.GetTableName()!,
                e => e.GetProperties().ToDictionary(p => p.GetColumnName(), p => p.GetColumnType().ToUpperInvariant()));

    /// <summary>Table → column → type of every CREATE TABLE in the vendored DDL, folded as Oracle folds unquoted names.</summary>
    private static Dictionary<string, Dictionary<string, string>> QuartzOracleDdl()
    {
        var ddl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Quartz", "tables_oracle.sql"));
        var tables = new Dictionary<string, Dictionary<string, string>>();
        foreach (Match table in Regex.Matches(ddl, @"CREATE TABLE (\w+)\s*\((.*?)\n\);", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var columns = new Dictionary<string, string>();
            foreach (var line in table.Groups[2].Value.Split('\n').Select(l => l.Trim()))
            {
                var column = Regex.Match(line, @"^(\w+)\s+([A-Za-z0-9]+(?:\(\d+(?:,\d+)?\))?)");
                if (column.Success && !line.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("REFERENCES", StringComparison.OrdinalIgnoreCase))
                {
                    columns[column.Groups[1].Value.ToUpperInvariant()] = column.Groups[2].Value.ToUpperInvariant();
                }
            }

            tables[table.Groups[1].Value.ToUpperInvariant()] = columns;
        }

        return tables;
    }

    /// <summary>The CREATE TABLE and CREATE INDEX statements of a script that touch the qrtz_ tables, in order.</summary>
    private static string QrtzStatements(string script)
        => string.Join("\n\n", script.ReplaceLineEndings("\n").Split("\n\n")
            .Where(s => s.Contains("qrtz_", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Trim())) + "\n";

    private static List<string> Statements(string script)
        => [.. script.ReplaceLineEndings("\n").Split("\n\n").Select(s => s.Trim()).Where(s => s.Length > 0).Order(StringComparer.Ordinal)];

    private async Task<string> ClusterSchemaAsync(string name)
    {
        var connection = await oracle.CreateSchemaAsync(name);
        var options = new DbContextOptionsBuilder<ClusterDb>();
        UseOracle(options, connection);
        await using var db = new ClusterDb(options.Options);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static ClusterDb Db(string connection)
    {
        var options = new DbContextOptionsBuilder<ClusterDb>();
        UseOracle(options, connection);
        return new ClusterDb(options.Options);
    }

    private static async Task<GoldpathJobRun?> QueryRunAsync(string connection, string jobName)
    {
        await using var db = Db(connection);
        return await db.Set<GoldpathJobRun>().AsNoTracking().SingleOrDefaultAsync(r => r.JobName == jobName);
    }

    private static async Task<List<SinkEntry>> QuerySinkAsync(string connection, string jobName)
    {
        await using var db = Db(connection);
        return await db.Sink.AsNoTracking().Where(s => s.JobName == jobName).ToListAsync();
    }

    private Process StartHostProcess(string connection, bool trigger, ConcurrentQueue<string> children)
    {
        var hostDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Goldpath.Jobs.TestHost/bin/Debug/net10.0/Goldpath.Jobs.TestHost.dll"));
        Assert.True(File.Exists(hostDll), $"build Goldpath.Jobs.TestHost first (expected at {hostDll})");

        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add(hostDll);
        startInfo.ArgumentList.Add(connection);
        startInfo.ArgumentList.Add("--provider");
        startInfo.ArgumentList.Add("oracle");
        if (trigger)
        {
            startInfo.ArgumentList.Add("--trigger");
        }

        var process = Process.Start(startInfo)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { children.Enqueue($"[out]{e.Data}"); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { children.Enqueue($"[err]{e.Data}"); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token, ConcurrentQueue<string> children)
    {
        try
        {
            while (!await condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine(string.Join('\n', children.TakeLast(80)));
            throw;
        }
    }
}

/// <summary>The run list's filters and keyset walk on a real Oracle (G7, O-3, #406): <see cref="JobsRunListFacts"/>.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class JobsRunListOracleTests(OracleFixture oracle) : JobsRunListFacts
{
    private static int _schemas;

    // xUnit builds one instance per fact, and each takes a schema of its own, as each PostgreSQL fact takes a container.
    protected override Task<string> StartStoreAsync()
        => oracle.CreateSchemaAsync($"GP_QRTZ_RUNS_{Interlocked.Increment(ref _schemas)}");

    protected override Task StopStoreAsync() => Task.CompletedTask;   // the collection's container is the fixture's

    protected override void Use(DbContextOptionsBuilder options, string connection)
        => options.UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19));
}
