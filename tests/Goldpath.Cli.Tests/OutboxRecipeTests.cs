using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// The outbox recipe (the fourteenth — the ONE schema key the CLI could not compose): the
/// bus is born with the broker resource when absent, and joined when present; the model
/// gains the three MassTransit tables; the using the template hides behind a preprocessor
/// symbol is written for real. Plus the console riding the FIRST jobs feature.
/// </summary>
public class OutboxRecipeTests
{
    private static AppFacts Facts(bool messaging, string provider = "postgres", bool console = false) => new()
    {
        DbContextName = "ShopDbContext",
        DatabaseProvider = provider,
        ConnectionName = "shopdb",
        CachingWired = false,
        JobsWired = false,
        MessagingWired = messaging,
        AuthWired = true,
        ConsoleWired = console,
        AspireVersion = "13.4.6",
        TrainVersion = "0.1.0-preview.7",
    };

    [Fact]
    public void Without_a_bus_the_outbox_recipe_births_one_with_the_broker_resource()
    {
        var plan = FeatureRecipes.Build("outbox", Facts(messaging: false));

        Assert.Equal("outbox", plan.ManifestKey);
        Assert.Equal(["Goldpath.Messaging", "MassTransit.RabbitMQ"], plan.ApiPackages);
        Assert.Equal(["Aspire.Hosting.RabbitMQ"], plan.AppHostPackages);
        Assert.Equal(["MassTransit"], plan.Usings);
        Assert.Equal(["var messaging = builder.AddRabbitMQ(\"messaging\");"], plan.Resources);
        Assert.Equal(["    .WithReference(messaging).WaitFor(messaging)"], plan.References);
        Assert.Equal("builder.AddGoldpathMessaging(bus =>", plan.Registrations[0]);
        Assert.Contains("    // goldpath:features consumers — bus-riding features register here", plan.Registrations);
        Assert.Contains("    bus.AddGoldpathOutbox<ShopDbContext>(outbox =>", plan.Registrations);
        Assert.Contains("        outbox.UsePostgres();", plan.Registrations);
        Assert.Contains("        cfg.ConfigureGoldpathEndpoints(context);", plan.Registrations);
        Assert.Empty(plan.BusLines);
        Assert.Equal(
            [
                "        // Transactional outbox/inbox tables (features.outbox in the manifest).",
                "        modelBuilder.AddInboxStateEntity();",
                "        modelBuilder.AddOutboxMessageEntity();",
                "        modelBuilder.AddOutboxStateEntity();",
            ],
            plan.ModelCalls);
        Assert.Equal(["  outbox: true"], plan.ManifestLines);
        Assert.Equal(["MassTransit"], plan.ModelUsings);
        // The manifest must say broker: rabbitmq too, or SPEC0101 refuses the recipe's own
        // result (found by the GmGrown shape, 2026-09-04).
        Assert.Equal([("broker", "rabbitmq")], plan.ProviderEdits);
        // ...and the pins the template only writes under UseBroker (NU1010 otherwise).
        Assert.Equal([("Goldpath.Messaging", "0.1.0-preview.7"), ("MassTransit.RabbitMQ", KnownVersions.MassTransitRabbitMq), ("Aspire.Hosting.RabbitMQ", "13.4.6")], plan.PackageVersions);
        Assert.Equal(3, plan.NextSteps.Count);
    }

    [Fact]
    public void With_a_bus_the_outbox_joins_it_and_brings_no_broker()
    {
        var plan = FeatureRecipes.Build("outbox", Facts(messaging: true, provider: "sqlserver"));

        Assert.Equal(["Goldpath.Messaging"], plan.ApiPackages);
        Assert.Empty(plan.AppHostPackages);
        Assert.Empty(plan.Resources);
        Assert.Empty(plan.Registrations);
        Assert.Equal(
            [
                "    bus.AddGoldpathOutbox<ShopDbContext>(outbox =>",
                "    {",
                "        outbox.UseSqlServer();",
                "    });",
            ],
            plan.BusLines);
    }

    [Fact]
    public void No_database_provider_means_no_outbox_said_plainly()
    {
        var refusal = Assert.Throws<CliFailureException>(() => FeatureRecipes.Build("outbox", Facts(messaging: false, provider: "none")));
        Assert.Contains("outbox tables live in the app database", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outbox_lands_end_to_end_using_included()
    {
        using var app = new FakeApp();   // no messaging wired
        var runner = new FakeProcessRunner();

        Assert.Equal(0, CliRunner.Run(["add", "feature", "outbox", "--path", app.Root], runner, TextWriter.Null, TextWriter.Null));

        var program = app.Read(app.Program);
        Assert.StartsWith("using MassTransit;", program, StringComparison.Ordinal);
        Assert.Contains("builder.AddGoldpathMessaging(bus =>", program, StringComparison.Ordinal);
        Assert.Contains("bus.AddGoldpathOutbox<ShopDbContext>(outbox =>", program, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.AddOutboxMessageEntity();", app.Read(app.Model), StringComparison.Ordinal);
        // The model file needs the using too — the template's sits behind UseBroker (CS1061 otherwise).
        Assert.StartsWith("using MassTransit;", app.Read(app.Model), StringComparison.Ordinal);
        Assert.Contains("var messaging = builder.AddRabbitMQ(\"messaging\");", app.Read(app.AppHost), StringComparison.Ordinal);
        Assert.Contains(".WithReference(messaging).WaitFor(messaging)", app.Read(app.AppHost), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Aspire.Hosting.RabbitMQ\" />", app.Read(app.AppHostProject), StringComparison.Ordinal);
        Assert.Contains("  outbox: true", app.Read(app.Manifest), StringComparison.Ordinal);
        Assert.Contains("  broker: rabbitmq", app.Read(app.Manifest), StringComparison.Ordinal);
        var props = File.ReadAllText(Path.Combine(app.Root, "Directory.Packages.props"));
        Assert.Contains("<PackageVersion Include=\"MassTransit.RabbitMQ\" Version=\"8.5.12\" />", props, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Goldpath.Messaging\" Version=\"0.1.0-preview.7\" />", props, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Aspire.Hosting.RabbitMQ\" Version=\"13.4.6\" />", props, StringComparison.Ordinal);

        // Idempotent: a second run writes nothing twice — one using, one bus.
        Assert.Equal(0, CliRunner.Run(["add", "feature", "outbox", "--path", app.Root], runner, TextWriter.Null, TextWriter.Null));
        var again = app.Read(app.Program);
        Assert.Equal(1, again.Split("using MassTransit;").Length - 1);
        Assert.Equal(1, again.Split("builder.AddGoldpathMessaging(bus =>").Length - 1);
    }

    [Fact]
    public void The_first_jobs_rider_brings_the_console_the_next_one_finds_it()
    {
        var first = FeatureRecipes.Build("archival", Facts(messaging: true, console: false));
        Assert.Contains("Goldpath.Console", first.ApiPackages);
        Assert.Equal("app.MapGoldpathConsole();                           // behind the SAME ops floor as the surfaces", first.Endpoints[^1]);

        var next = FeatureRecipes.Build("bulk", Facts(messaging: true, console: true));
        Assert.DoesNotContain("Goldpath.Console", next.ApiPackages);
        Assert.DoesNotContain(next.Endpoints, line => line.Contains("MapGoldpathConsole", StringComparison.Ordinal));

        // No auth strategy: the opt-out is WRITTEN, exactly as the template does it.
        var open = FeatureRecipes.Build("bulk", new AppFacts
        {
            DbContextName = "ShopDbContext",
            DatabaseProvider = "postgres",
            ConnectionName = "shopdb",
            CachingWired = false,
            JobsWired = false,
            MessagingWired = true,
            AuthWired = false,
            ConsoleWired = false,
        });
        Assert.StartsWith("app.MapGoldpathConsole(exposeUnsecured: true);", open.Endpoints[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_ring_b_feature_never_drags_the_console_in()
    {
        var plan = FeatureRecipes.Build("audittrail", Facts(messaging: true));
        Assert.DoesNotContain("Goldpath.Console", plan.ApiPackages);
        Assert.Empty(plan.Endpoints);
    }

    [Theory]
    [InlineData("kind: solution\nproviders:\n  db: postgresql\n  broker: none\n  auth: none\nfeatures:\n  outbox: true\n", "kind: solution\nproviders:\n  db: postgresql\n  broker: rabbitmq\n  auth: none\nfeatures:\n  outbox: true\n")]
    [InlineData("kind: solution\nproviders:\n  db: postgresql\nfeatures:\n  outbox: true\n", "kind: solution\nproviders:\n  broker: rabbitmq\n  db: postgresql\nfeatures:\n  outbox: true\n")]
    [InlineData("kind: solution\nname: X\nfeatures:\n  outbox: true\n", "kind: solution\nname: X\nproviders:\n  broker: rabbitmq\nfeatures:\n  outbox: true\n")]
    [InlineData("kind: solution\nname: X", "kind: solution\nname: X\nproviders:\n  broker: rabbitmq")]
    public void The_manifest_editor_sets_a_provider_scalar_in_every_shape_a_manifest_can_have(string before, string after)
        => Assert.Equal(after, ManifestEditor.SetProviderScalar(before, "broker", "rabbitmq"));

    [Fact]
    public void With_a_bus_the_outbox_recipe_flips_no_provider()
    {
        var plan = FeatureRecipes.Build("outbox", Facts(messaging: true, provider: "sqlserver"));
        Assert.Empty(plan.ProviderEdits);
    }

    [Fact]
    public void The_known_MassTransit_version_matches_the_repo_pin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Goldpath.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var props = File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Packages.props"));
        Assert.Contains("<PackageVersion Include=\"MassTransit.RabbitMQ\" Version=\"" + KnownVersions.MassTransitRabbitMq + "\" />", props, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_pins_nothing_twice()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();
        Assert.Equal(0, CliRunner.Run(["add", "feature", "outbox", "--path", app.Root], runner, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, CliRunner.Run(["add", "feature", "outbox", "--path", app.Root], runner, TextWriter.Null, TextWriter.Null));
        var props = File.ReadAllText(Path.Combine(app.Root, "Directory.Packages.props"));
        Assert.Equal(1, props.Split("Include=\"MassTransit.RabbitMQ\"").Length - 1);
    }

    [Fact]
    public void Without_central_pins_the_born_bus_refuses_in_words()
    {
        using var app = new FakeApp();
        File.Delete(Path.Combine(app.Root, "Directory.Packages.props"));
        var files = AppFiles.Locate(app.Root);
        var refusal = Assert.Throws<CliFailureException>(() => FeatureRecipes.Build("outbox", AppFacts.Read(files)));
        Assert.Contains("Directory.Packages.props", refusal.Message, StringComparison.Ordinal);
    }
}
