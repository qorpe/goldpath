using Xunit;

namespace Goldpath.Cli.Tests;

public class AddFeatureTests
{
    private static int Add(string feature, FakeApp app, FakeProcessRunner runner)
        => CliRunner.Run(["add", "feature", feature, "--path", app.Root], runner, TextWriter.Null, TextWriter.Null);

    [Fact]
    public void Audittrail_wires_package_registration_model_and_manifest()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();

        Assert.Equal(0, Add("audittrail", app, runner));

        Assert.Contains("<PackageReference Include=\"Goldpath.AuditTrail\" />", app.Read(app.ApiProject), StringComparison.Ordinal);
        Assert.Contains("builder.AddGoldpathAuditTrail<WebApplicationBuilder, ShopDbContext>();", app.Read(app.Program), StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddGoldpathAuditLog();", app.Read(app.Model), StringComparison.Ordinal);
        Assert.Contains("  auditTrail: true", app.Read(app.Manifest), StringComparison.Ordinal);
        // The engine round-trip ran: validate then drift.
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("validate"));
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("drift"));
    }

    [Fact]
    public void Registration_lands_after_the_registrations_anchor()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("softdelete", app, new FakeProcessRunner()));

        var lines = app.Read(app.Program).Split('\n');
        var anchor = Array.FindIndex(lines, l => l.Contains("goldpath:features registrations", StringComparison.Ordinal));
        Assert.Equal("builder.AddGoldpathSoftDelete();", lines[anchor + 1].Trim());
    }

    [Fact]
    public void Multitenancy_adds_middleware_after_the_middleware_anchor()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("multitenancy", app, new FakeProcessRunner()));

        var lines = app.Read(app.Program).Split('\n');
        var anchor = Array.FindIndex(lines, l => l.Contains("goldpath:features middleware", StringComparison.Ordinal));
        Assert.StartsWith("app.UseGoldpathMultiTenancy();", lines[anchor + 1].Trim(), StringComparison.Ordinal);
        Assert.Contains("modelBuilder.ApplyGoldpathMultiTenancy(this);", app.Read(app.Model), StringComparison.Ordinal);
    }

    [Fact]
    public void Caching_provisions_redis_and_retires_the_idempotency_fallback()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();
        Assert.Equal(0, Add("idempotency", app, runner));
        Assert.Contains("AddDistributedMemoryCache", app.Read(app.Program), StringComparison.Ordinal);

        Assert.Equal(0, Add("caching", app, runner));

        var program = app.Read(app.Program);
        Assert.Contains("builder.AddGoldpathCaching();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDistributedMemoryCache", program, StringComparison.Ordinal);
        Assert.Contains("var cache = builder.AddRedis(\"redis\");", app.Read(app.AppHost), StringComparison.Ordinal);
        Assert.Contains(".WithReference(cache).WaitFor(cache)", app.Read(app.AppHost), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Aspire.Hosting.Redis\" />", app.Read(app.AppHostProject), StringComparison.Ordinal);
        Assert.Contains("  distributedCaching: true", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Redis_reference_lands_inside_the_fluent_chain_before_the_health_check()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("caching", app, new FakeProcessRunner()));

        var lines = app.Read(app.AppHost).Split('\n');
        var reference = Array.FindIndex(lines, l => l.Contains(".WithReference(cache)", StringComparison.Ordinal));
        var health = Array.FindIndex(lines, l => l.Contains(".WithHttpHealthCheck", StringComparison.Ordinal));
        Assert.True(reference >= 0 && reference < health, "the cache reference must precede the health check in the chain");
    }

    [Fact]
    public void Idempotency_with_caching_wired_skips_the_memory_fallback()
    {
        using var app = new FakeApp(cachingWired: true);
        Assert.Equal(0, Add("idempotency", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Contains("builder.AddGoldpathIdempotency();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDistributedMemoryCache", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Locking_on_postgres_reuses_the_app_connection_name()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("locking", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Contains("builder.AddGoldpathLocking(o =>", program, StringComparison.Ordinal);
        Assert.Contains("o.ConnectionName = \"shopdb\";", program, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Locking\" />", app.Read(app.ApiProject), StringComparison.Ordinal);
        var manifest = app.Read(app.Manifest);
        Assert.Contains("  distributedLocking:", manifest, StringComparison.Ordinal);
        Assert.Contains("    provider: postgres", manifest, StringComparison.Ordinal);
        Assert.Contains("    connectionName: shopdb", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Locking_on_sqlserver_uses_the_optional_package()
    {
        using var app = new FakeApp(sqlServer: true);
        Assert.Equal(0, Add("locking", app, new FakeProcessRunner()));

        Assert.Contains("builder.AddGoldpathSqlServerLocking(o => o.ConnectionName = \"shopdb\");", app.Read(app.Program), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Locking.SqlServer\" />", app.Read(app.ApiProject), StringComparison.Ordinal);
        Assert.Contains("    provider: sqlserver", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Archival_wires_jobs_and_archival_with_admin_endpoints_after_the_endpoints_anchor()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("archival", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Contains("builder.AddGoldpathJobs<WebApplicationBuilder, ShopDbContext>(jobs =>", program, StringComparison.Ordinal);
        Assert.Contains("jobs.AddGoldpathArchivalJobs<ShopDbContext>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.AddGoldpathArchival<WebApplicationBuilder, ShopDbContext>(archival =>", program, StringComparison.Ordinal);

        var lines = program.Split('\n');
        var anchor = Array.FindIndex(lines, l => l.Contains("goldpath:features endpoints", StringComparison.Ordinal));
        Assert.StartsWith("app.MapGoldpathJobsAdmin<ShopDbContext>(exposeUnsecured: true);", lines[anchor + 1].Trim(), StringComparison.Ordinal);
        Assert.StartsWith("app.MapGoldpathArchivalAdmin<ShopDbContext>(exposeUnsecured: true);", lines[anchor + 2].Trim(), StringComparison.Ordinal);

        var model = app.Read(app.Model);
        Assert.Contains("modelBuilder.AddGoldpathArchiveModel();", model, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddGoldpathJobs();", model, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Archival\" />", app.Read(app.ApiProject), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Jobs\" />", app.Read(app.ApiProject), StringComparison.Ordinal);
        Assert.Contains("  archival: true", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_on_a_fresh_app_opens_the_jobs_composition()
    {
        using var app = new FakeApp();
        Assert.Equal(0, Add("bulk", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Contains("builder.AddGoldpathJobs<WebApplicationBuilder, ShopDbContext>(jobs =>", program, StringComparison.Ordinal);
        Assert.Contains("jobs.AddGoldpathBulkJobs<ShopDbContext>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.AddGoldpathBulk<WebApplicationBuilder, ShopDbContext>(bulk =>", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGoldpathBulkAdmin<ShopDbContext>(exposeUnsecured: true);", program, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddGoldpathBulk();", app.Read(app.Model), StringComparison.Ordinal);
        Assert.Contains("  bulk: true", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_on_a_jobs_wired_app_composes_into_the_existing_scheduler()
    {
        using var app = new FakeApp(jobsWired: true);
        Assert.Equal(0, Add("bulk", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Equal(1, CountOf(program, "builder.AddGoldpathJobs<"));   // ONE scheduler, not two

        var lines = program.Split('\n');
        var connection = Array.FindIndex(lines, l => l.Contains("jobs.ConnectionName", StringComparison.Ordinal));
        Assert.StartsWith("jobs.AddGoldpathBulkJobs<ShopDbContext>();", lines[connection + 1].Trim(), StringComparison.Ordinal);
        Assert.Contains("jobs.AddGoldpathArchivalJobs<ShopDbContext>();", program, StringComparison.Ordinal);   // the neighbor survives
    }

    private static int CountOf(string text, string marker)
        => text.Split(marker).Length - 1;

    [Fact]
    public void Notification_on_a_jobs_wired_app_composes_into_the_existing_scheduler()
    {
        using var app = new FakeApp(jobsWired: true);
        Assert.Equal(0, Add("notification", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Equal(1, CountOf(program, "builder.AddGoldpathJobs<"));   // ONE scheduler, not two
        Assert.Contains("jobs.AddGoldpathNotificationJobs<ShopDbContext>();", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGoldpathNotificationAdmin<ShopDbContext>(exposeUnsecured: true);", program, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddGoldpathNotification();", app.Read(app.Model), StringComparison.Ordinal);
        Assert.Contains("  notification: true", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_on_a_wired_app_rides_scheduler_and_bus()
    {
        using var app = new FakeApp(jobsWired: true, messagingWired: true);
        Assert.Equal(0, Add("campaign", app, new FakeProcessRunner()));

        var program = app.Read(app.Program);
        Assert.Equal(1, CountOf(program, "builder.AddGoldpathJobs<"));        // ONE scheduler, not two
        Assert.Equal(1, CountOf(program, "builder.AddGoldpathMessaging("));   // ONE bus, not two
        Assert.Contains("jobs.AddGoldpathCampaignJobs<ShopDbContext>();", program, StringComparison.Ordinal);
        Assert.Contains("bus.AddGoldpathCampaignConsumers<ShopDbContext>();", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGoldpathCampaignAdmin<ShopDbContext>(exposeUnsecured: true);", program, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddGoldpathCampaign();", app.Read(app.Model), StringComparison.Ordinal);
        Assert.Contains("  campaign: true", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_without_messaging_fails_with_the_broker_rule()
    {
        using var app = new FakeApp(jobsWired: true);
        Assert.NotEqual(0, Add("campaign", app, new FakeProcessRunner()));
        Assert.DoesNotContain("campaign: true", app.Read(app.Manifest), StringComparison.Ordinal);   // nothing half-applied
    }

    [Fact]
    public void Already_enabled_feature_is_a_clean_noop()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();
        Assert.Equal(0, Add("softdelete", app, runner));
        var wired = app.Read(app.Program);
        var engineRuns = runner.Calls.Count;

        Assert.Equal(0, Add("softdelete", app, runner));

        Assert.Equal(wired, app.Read(app.Program));
        Assert.Equal(engineRuns, runner.Calls.Count);   // no second engine round-trip
    }

    [Fact]
    public void Engine_rejection_restores_every_file_byte_identical()
    {
        using var app = new FakeApp();
        var before = new[] { app.Manifest, app.ApiProject, app.AppHostProject, app.Program, app.Model, app.AppHost }
            .ToDictionary(p => p, app.Read);
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["--repo"] = 1;   // fails only the drift call

        Assert.Equal(1, Add("caching", app, runner));

        foreach (var (path, content) in before)
        {
            Assert.Equal(content, app.Read(path));
        }
    }

    [Fact]
    public void Unknown_feature_exits_2()
    {
        using var app = new FakeApp();
        Assert.Equal(2, Add("quantumsafe", app, new FakeProcessRunner()));
    }

    [Fact]
    public void Worker_manifests_are_refused_with_the_owning_solution_message()
    {
        using var app = new FakeApp(kind: "worker");
        var error = new StringWriter();
        var exitCode = CliRunner.Run(["add", "feature", "caching", "--path", app.Root], new FakeProcessRunner(), TextWriter.Null, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("owning SOLUTION", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_anchor_fails_loud_and_leaves_no_partial_edit()
    {
        using var app = new FakeApp();
        File.WriteAllText(app.Program, app.Read(app.Program)
            .Replace("// goldpath:features registrations — the drift profile is the source of these rows", string.Empty, StringComparison.Ordinal));
        var manifestBefore = app.Read(app.Manifest);
        var apiBefore = app.Read(app.ApiProject);

        var error = new StringWriter();
        var exitCode = CliRunner.Run(["add", "feature", "softdelete", "--path", app.Root], new FakeProcessRunner(), TextWriter.Null, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("anchor", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(manifestBefore, app.Read(app.Manifest));
        Assert.Equal(apiBefore, app.Read(app.ApiProject));
    }

    /// <summary>
    /// Six features ride the jobs runtime — archival, approvals, bulk, campaign, fileexchange,
    /// notification — and every one of their recipes asks for Goldpath.Jobs. Adding a second one
    /// to an app that already has the first must not reference the package again: NuGet refuses a
    /// duplicate PackageReference (NU1504), and the generated apps treat warnings as errors, so
    /// the app stops restoring.
    ///
    /// The insertion guard looked only at the FIRST line of a recipe's block. Every jobs recipe
    /// leads with its own package, so the guard never saw that Goldpath.Jobs was already there.
    /// The nightly GmGrownRest shape carried four copies from 2026-09-08 until this test.
    /// </summary>
    [Fact]
    public void A_second_jobs_riding_feature_does_not_reference_jobs_twice()
    {
        using var app = new FakeApp();

        Assert.Equal(0, Add("archival", app, new FakeProcessRunner()));
        Assert.Equal(0, Add("approvals", app, new FakeProcessRunner()));

        var project = app.Read(app.ApiProject);
        var jobs = project.Split('\n').Count(l => l.Contains("<PackageReference Include=\"Goldpath.Jobs\" />", StringComparison.Ordinal));
        Assert.Equal(1, jobs);

        // And the guard did not swallow the second feature's OWN package along with the duplicate.
        Assert.Contains("<PackageReference Include=\"Goldpath.Archival\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Approvals\" />", project, StringComparison.Ordinal);
    }
}
