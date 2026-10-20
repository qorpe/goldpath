using System.Text.Json;
using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// Records every question the wizard asks (text, choices, default) and answers from a
/// script — so the prompts themselves are under test, not just the derivation.
/// </summary>
internal sealed class RecordingPrompter(
    string name = "Shop",
    string database = "postgresql",
    string auth = "openid",
    string layout = "vertical-slice",
    IReadOnlyList<string>? features = null,
    bool outbox = false,
    bool generate = true) : IPrompter
{
    public List<string> Inputs { get; } = [];
    public List<(string Question, IReadOnlyList<string> Choices, string Default)> Chooses { get; } = [];
    public List<(string Question, IReadOnlyList<string> Choices)> ChooseManys { get; } = [];
    public List<(string Question, bool Default)> Confirms { get; } = [];

    public string Choose(string question, IReadOnlyList<string> choices, string defaultChoice)
    {
        Chooses.Add((question, choices, defaultChoice));
        // The kind question comes first (after the name); the script answers "solution".
        return Chooses.Count switch { 1 => "solution", 2 => database, 3 => auth, _ => layout };
    }

    public IReadOnlyList<string> ChooseMany(string question, IReadOnlyList<string> choices)
    {
        ChooseManys.Add((question, choices));
        return features ?? [];
    }

    public bool Confirm(string question, bool defaultAnswer)
    {
        Confirms.Add((question, defaultAnswer));
        return Confirms.Count == 1 ? outbox : generate;
    }

    public string Input(string question)
    {
        Inputs.Add(question);
        return name;
    }
}

/// <summary>
/// Mutation-gate tests: every line below pins an EXACT value (argument list, prompt text,
/// output line, exit code) so a mutant that silences, garbles or flips it dies.
/// </summary>
public class WizardMutationTests
{
    private static WizardCommand.Plan Derive(IReadOnlyList<string>? features = null, bool outbox = false,
        string db = "postgresql", string auth = "openid", string layout = "vertical-slice")
        => WizardCommand.Derive(new WizardCommand.Answers("Shop", db, auth, layout, features ?? [], outbox));

    [Fact]
    public void The_walking_skeleton_derives_exactly_db_auth_and_no_broker()
    {
        var plan = Derive();

        Assert.Equal(["--db", "postgresql", "--auth", "openid", "--broker", "none"], plan.Arguments);
        Assert.Equal(
            [
                "database: postgresql — every shape owns one",
                "auth: openid",
                "layout: vertical-slice",
                "no broker needed — removed (nothing you chose publishes through one)",
                "no Redis — removed (only the caching module brings it)",
                "modules: none — the walking skeleton only",
            ],
            plan.Notes);
    }

    [Fact]
    public void Every_answer_maps_to_its_argument_in_a_fixed_order()
    {
        var plan = Derive(features: ["campaign", "caching", "idempotency", "archival"], db: "sqlserver", auth: "none", layout: "clean-architecture");

        Assert.Equal(
            [
                "--db", "sqlserver", "--auth", "none", "--layout", "clean-architecture",
                "--features", "campaign", "--features", "caching", "--features", "idempotency", "--features", "archival",
            ],
            plan.Arguments);
        Assert.Equal(
            [
                "database: sqlserver — every shape owns one",
                "auth: none — admin surfaces opt out VISIBLY; acceptable only behind an authenticating boundary",
                "layout: clean-architecture",
                "broker: rabbitmq — campaign REQUIRES one (the release path IS broker fan-out, RFC D8)",
                "redis joins — caching is its only source (HybridCache L1+L2)",
                "jobs scheduler + the operations console ride the app database (campaign, archival)",
                "modules: campaign, caching, idempotency, archival",
            ],
            plan.Notes);
    }

    [Fact]
    public void The_outbox_keeps_the_broker_and_idempotency_without_caching_learns_its_fallback()
    {
        var plan = Derive(features: ["idempotency"], outbox: true);

        Assert.Equal(["--db", "postgresql", "--auth", "openid", "--features", "idempotency"], plan.Arguments);
        Assert.Equal(
            [
                "database: postgresql — every shape owns one",
                "auth: openid",
                "layout: vertical-slice",
                "broker: rabbitmq — the outbox publishes THROUGH a broker",
                "no Redis — removed (only the caching module brings it)",
                "idempotency stores keys in a memory cache — enable caching for Redis-backed keys",
                "modules: idempotency",
            ],
            plan.Notes);
    }

    [Theory]
    [InlineData("archival")]
    [InlineData("bulk")]
    [InlineData("notification")]
    [InlineData("campaign")]
    [InlineData("approvals")]
    [InlineData("fileexchange")]
    public void Each_jobs_rider_brings_the_scheduler_note(string rider)
    {
        var plan = Derive(features: [rider]);
        Assert.Contains($"jobs scheduler + the operations console ride the app database ({rider})", plan.Notes);
    }

    [Theory]
    [InlineData("multitenancy")]
    [InlineData("caching")]
    [InlineData("softdelete")]
    public void Non_riders_bring_no_scheduler_note(string module)
    {
        var plan = Derive(features: [module]);
        Assert.DoesNotContain(plan.Notes, n => n.StartsWith("jobs scheduler", StringComparison.Ordinal));
        Assert.Contains($"modules: {module}", plan.Notes);
    }

    [Fact]
    public void Duplicate_and_unknown_modules_collapse_before_the_generator()
    {
        var plan = Derive(features: ["bulk", "bulk", "blockchain"]);
        Assert.Equal(["--db", "postgresql", "--auth", "openid", "--broker", "none", "--features", "bulk"], plan.Arguments);
        Assert.Contains("modules: bulk", plan.Notes);
    }

    [Fact]
    public void The_module_menu_is_the_canonical_recipe_list_minus_the_one_the_template_cannot_take()
    {
        Assert.Equal(FeatureRecipes.TemplateFeatures, WizardCommand.Modules);
        Assert.Equal(FeatureRecipes.Names.Where(n => n != "outbox"), WizardCommand.Modules);
        Assert.DoesNotContain("outbox", WizardCommand.Modules);
    }

    [Fact]
    public void The_module_menu_is_exactly_the_solution_templates_features_choice_list()
    {
        // The menu's values become `dotnet new goldpath-solution --features <value>`. A value
        // the template's choice list does not carry generates a command that cannot run —
        // the wizard offered `outbox` until the preview.8 coverage audit (2026-09-05).
        var templateJson = File.ReadAllText(Path.Combine(RepoRoot(), "templates", "goldpath-solution", ".template.config", "template.json"));
        using var document = JsonDocument.Parse(templateJson);
        var choices = document.RootElement
            .GetProperty("symbols").GetProperty("features").GetProperty("choices")
            .EnumerateArray().Select(c => c.GetProperty("choice").GetString()!).ToList();

        Assert.Equal(choices.OrderBy(c => c, StringComparer.Ordinal), WizardCommand.Modules.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void A_module_the_template_cannot_take_never_reaches_the_generated_command()
    {
        // Defence in depth: even handed `outbox` directly, Derive filters it out rather than
        // emitting `--features outbox`.
        var plan = WizardCommand.Derive(new WizardCommand.Answers(
            "Shop", "postgresql", "none", "vertical-slice", ["audittrail", "outbox"], PublishesIntegrationEvents: false));

        Assert.DoesNotContain("outbox", plan.Arguments);
        Assert.Contains("audittrail", plan.Arguments);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "templates")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_wizard_asks_exactly_these_questions_with_these_defaults()
    {
        var prompter = new RecordingPrompter(generate: false);

        WizardCommand.Run(prompter, new FakeProcessRunner(), TextWriter.Null, TextWriter.Null);

        Assert.Equal(["Name (e.g. OrderPlatform, or Billing.Nightly for a worker)"], prompter.Inputs);
        Assert.Equal(4, prompter.Chooses.Count);
        Assert.Equal("What are we building?", prompter.Chooses[0].Question);
        Assert.Equal(["solution", "worker"], prompter.Chooses[0].Choices);
        Assert.Equal("solution", prompter.Chooses[0].Default);
        Assert.Equal("Database", prompter.Chooses[1].Question);
        Assert.Equal(["postgresql", "sqlserver"], prompter.Chooses[1].Choices);
        Assert.Equal("postgresql", prompter.Chooses[1].Default);
        Assert.Equal("Authentication", prompter.Chooses[2].Question);
        Assert.Equal(["openid", "apikey", "none"], prompter.Chooses[2].Choices);
        Assert.Equal("openid", prompter.Chooses[2].Default);
        Assert.Equal("Code layout", prompter.Chooses[3].Question);
        Assert.Equal(["vertical-slice", "clean-architecture"], prompter.Chooses[3].Choices);
        Assert.Equal("vertical-slice", prompter.Chooses[3].Default);
        var modules = Assert.Single(prompter.ChooseManys);
        Assert.Equal("Which modules does this app need?", modules.Question);
        Assert.Equal(WizardCommand.Modules, modules.Choices);
        Assert.Equal(
            [("Will it publish integration events to other systems (outbox)?", false), ("Generate?", true)],
            prompter.Confirms);
    }

    [Fact]
    public void Declining_prints_the_derived_shape_and_the_equivalent_command_verbatim()
    {
        var output = new StringWriter { NewLine = "\n" };
        var runner = new FakeProcessRunner();

        var exit = WizardCommand.Run(new RecordingPrompter(name: "  Shop  ", generate: false), runner, output, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Empty(runner.Calls);
        // The name is trimmed; the command line is what the user would type by hand.
        Assert.Equal(
            "── goldpath new (wizard): say what the app DOES — the infrastructure is derived, with reasons.\n" +
            "\n" +
            "── the derived shape:\n" +
            "   database: postgresql — every shape owns one\n" +
            "   auth: openid\n" +
            "   layout: vertical-slice\n" +
            "   no broker needed — removed (nothing you chose publishes through one)\n" +
            "   no Redis — removed (only the caching module brings it)\n" +
            "   modules: none — the walking skeleton only\n" +
            "\n" +
            "   equivalent command: goldpath new solution -n Shop --db postgresql --auth openid --broker none\n" +
            "── nothing generated.\n",
            output.ToString());
    }

    [Fact]
    public void Accepting_hands_the_exact_argument_list_to_dotnet_new()
    {
        var output = new StringWriter { NewLine = "\n" };
        var runner = new FakeProcessRunner();

        var exit = WizardCommand.Run(
            new RecordingPrompter(name: "Shop", auth: "apikey", layout: "clean-architecture", features: ["caching"], outbox: true),
            runner, output, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Equal("dotnet", runner.Calls[0].FileName);
        Assert.Equal(
            ["new", "goldpath-solution", "-n", "Shop", "--db", "postgresql", "--auth", "apikey", "--layout", "clean-architecture", "--features", "caching"],
            runner.Calls[0].Arguments);
        Assert.Contains("   equivalent command: goldpath new solution -n Shop --db postgresql --auth apikey --layout clean-architecture --features caching\n", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("nothing generated", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_a_usage_error_before_any_other_question(string blank)
    {
        var prompter = new RecordingPrompter(name: blank);

        var exception = Assert.Throws<CliUsageException>(
            () => WizardCommand.Run(prompter, new FakeProcessRunner(), TextWriter.Null, TextWriter.Null));

        Assert.Equal("the wizard needs a name.", exception.Message);
        Assert.Empty(prompter.Chooses);
    }
}

public class DiscoverMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"goldpath-discover-mut-{Guid.NewGuid():N}");

    public DiscoverMutationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Manifest(string relativeSolutionDir, string body)
    {
        var dir = Path.Combine(_root, relativeSolutionDir, ".goldpath");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.yaml");
        File.WriteAllText(path, body);
        return path;
    }

    private (int Exit, string[] Lines, string Error) Run(params string[] rest)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var exit = CliRunner.Run(["discover", .. rest], new FakeProcessRunner(), output, error);
        return (exit, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), error.ToString());
    }

    private static string Rel(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void Lines_are_ordinal_sorted_and_say_exactly_what_each_manifest_declares()
    {
        Manifest(Rel("apps", "zeta"), "kind: solution\nname: Zeta\nproducts:\n  - name: qorpe.apiPortal\n    enabled: true\n  - name: \"qorpe.billing\"\n");
        Manifest(Rel("apps", "alpha"), "kind: worker\nname: 'Alpha'\n");
        Manifest("", "kind: solution\nname: Root\n");

        var (exit, lines, error) = Run("--path", _root);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Equal(
            [
                $".  kind=solution  name=Root",
                $"{Rel("apps", "alpha")}  kind=worker  name=Alpha",
                $"{Rel("apps", "zeta")}  kind=solution  name=Zeta  products=qorpe.apiPortal,qorpe.billing",
                $"── 3 manifest(s) under {_root}",
            ],
            lines);
    }

    [Fact]
    public void A_manifest_that_declares_nothing_reads_as_question_marks()
    {
        Manifest("bare", "");

        var (_, lines, _) = Run("--path", _root);

        Assert.Equal($"bare  kind=?  name=?", lines[0]);
    }

    [Fact]
    public void Products_end_at_the_next_top_level_key_and_accept_bare_name_items()
    {
        // `name:` AFTER the products array is the solution's name, never a product;
        // an un-indented `- name:` item is still an item; a bare `name:` line inside
        // the array (a folded item) still counts.
        Manifest("a", "kind: solution\nproducts:\n- name: one\n- name: two\nname: After\n");
        Manifest("b", "kind: solution\nname: B\nproducts:\n  -\n    name: folded\n");

        var (_, lines, _) = Run("--path", _root);

        Assert.Equal("a  kind=solution  name=After  products=one,two", lines[0]);
        Assert.Equal("b  kind=solution  name=B  products=folded", lines[1]);
    }

    [Fact]
    public void Every_vendor_and_build_directory_is_skipped_by_exact_name()
    {
        foreach (var skipped in new[] { "node_modules", "bin", "obj", ".git", ".vs", "dist", "coverage", "TestResults" })
        {
            Manifest(Rel(skipped, "inside"), $"kind: solution\nname: In{skipped}\n");
        }

        Manifest("keeper", "kind: solution\nname: Keeper\n");

        var (_, lines, _) = Run("--path", _root);

        Assert.Equal(["keeper  kind=solution  name=Keeper", $"── 1 manifest(s) under {_root}"], lines);
    }

    [Fact]
    public void An_empty_tree_names_the_root_it_searched()
    {
        var (exit, lines, _) = Run("--path", _root);

        Assert.Equal(0, exit);
        Assert.Equal([$"── no Goldpath manifests under {_root}"], lines);
    }

    [Fact]
    public void No_path_means_the_current_directory()
    {
        var (exit, lines, _) = Run();

        Assert.Equal(0, exit);
        Assert.EndsWith($" under {Directory.GetCurrentDirectory()}", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_directory_is_a_usage_error_naming_the_full_path()
    {
        var missing = Path.Combine(_root, "nowhere");

        var (exit, lines, error) = Run("--path", missing);

        Assert.Equal(2, exit);
        Assert.Empty(lines);
        Assert.Equal($"goldpath: no such directory: {missing}\n", error);
    }

    [Fact]
    public void An_unreadable_manifest_is_reported_on_stderr_and_still_counted()
    {
        var path = Manifest("locked", "kind: solution\nname: Locked\n");
        // An exclusive handle makes ReadAllText fail with an IOException on every OS .NET
        // enforces FileShare on — the inventory must go on, not die on one bad file.
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var (exit, lines, error) = Run("--path", _root);

        Assert.Equal(0, exit);
        Assert.StartsWith($"goldpath: could not read {path} — ", error, StringComparison.Ordinal);
        Assert.Equal(["locked  kind=?  name=?", $"── 1 manifest(s) under {_root}"], lines);
    }
}

public class CliRunnerMutationTests
{
    private static (int Exit, string Out, string Err) Run(FakeProcessRunner runner, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var exit = CliRunner.Run(args, runner, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static (int Exit, string Out, string Err) Run(params string[] args) => Run(new FakeProcessRunner(), args);

    /// <summary>Runs with an empty console so ConsolePrompter answers every question with its default.</summary>
    private static T WithEmptyConsole<T>(Func<T> body)
    {
        var stdin = Console.In;
        var stdout = Console.Out;
        Console.SetIn(new StringReader(""));
        Console.SetOut(TextWriter.Null);
        try
        {
            return body();
        }
        finally
        {
            Console.SetIn(stdin);
            Console.SetOut(stdout);
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_prints_the_full_usage_on_stdout(string verb)
    {
        var (exit, output, error) = Run(verb);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.StartsWith("goldpath — the Goldpath golden-path CLI (thin and deterministic)\n", output, StringComparison.Ordinal);
        Assert.Contains("  goldpath add feature <name> [--plan] [--path <dir>] wire a Ring B feature into an existing app\n", output, StringComparison.Ordinal);
        Assert.Contains("(--plan: print what it would do and decide, as JSON; write nothing)\n", output, StringComparison.Ordinal);
        Assert.Contains("  goldpath spec ready [--gate <SPEC-nnnn> --author <who>] [--next-id rule|spec] [--path <dir>]\n", output, StringComparison.Ordinal);
        Assert.Contains("  goldpath --help | --version\n", output, StringComparison.Ordinal);
        Assert.EndsWith("features: multitenancy, audittrail, softdelete, idempotency, dataprotection, caching, locking, approvals, fileexchange, archival, bulk, notification, campaign\nadd feature also accepts: outbox (births the bus in an app generated without one)\n", output, StringComparison.Ordinal);
        // Every name the verb accepts is named in the usage — the 13 template features plus outbox.
        foreach (var feature in FeatureRecipes.Names)
        {
            Assert.Contains(feature, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_is_the_informational_version_without_build_metadata(string verb)
    {
        var expected = typeof(CliRunner).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion.Split('+')[0];

        var (exit, output, error) = Run(verb);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Equal($"{expected}\n", output);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("add")]
    [InlineData("add", "feature")]
    [InlineData("add", "worker")]
    [InlineData("db")]
    [InlineData("export")]
    [InlineData("export", "image")]
    public void An_unknown_shape_prints_usage_on_stderr_and_exits_2(params string[] args)
    {
        var (exit, output, error) = Run(args);

        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.StartsWith("goldpath — the Goldpath golden-path CLI", error, StringComparison.Ordinal);
        Assert.Contains("usage:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Bare_new_is_the_wizard_not_a_usage_error()
    {
        // ConsolePrompter on an empty stdin yields no name: the WIZARD refuses, with its
        // own message — proof the verb reached it rather than falling through to usage.
        var (exit, output, error) = WithEmptyConsole(() => Run("new"));

        Assert.Equal(2, exit);
        Assert.StartsWith("── goldpath new (wizard): say what the app DOES", output, StringComparison.Ordinal);
        Assert.Equal("goldpath: the wizard needs a name.\n", error);
    }

    [Fact]
    public void Init_reaches_the_init_command_and_attaches_with_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"goldpath-init-mut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Legacy.sln"), "");
        try
        {
            var runner = new FakeProcessRunner();
            var (exit, _, error) = WithEmptyConsole(() => Run(runner, "init", "--path", root));

            Assert.Equal(0, exit);
            Assert.Empty(error);
            Assert.True(File.Exists(Path.Combine(root, ".goldpath", "manifest.yaml")));
            Assert.Contains(runner.Calls, c => c.Arguments.Contains("validate"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("init")]
    [InlineData("check")]
    [InlineData("discover")]
    [InlineData("export", "compose")]
    [InlineData("add", "feature", "caching")]
    [InlineData("db", "status")]
    public void Only_path_is_understood_after_these_verbs(params string[] verb)
    {
        var (exit, output, error) = Run([.. verb, "--frobnicate", "x"]);

        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.Equal("goldpath: unexpected arguments: --frobnicate x (only --path <dir> is understood here)\n", error);
    }

    [Fact]
    public void No_path_means_the_current_directory_for_add_feature()
    {
        var (exit, _, error) = Run("add", "feature", "caching");

        Assert.Equal(1, exit);
        Assert.Equal(
            $"goldpath: no manifest at {Path.Combine(".", ".goldpath", "manifest.yaml")} — goldpath add runs inside a Goldpath-generated app (or pass --path).\n",
            error);
    }

    [Fact]
    public void Add_feature_dispatches_the_name_and_path_verbatim()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();

        var (exit, output, _) = Run(runner, "add", "feature", "softdelete", "--path", app.Root);

        Assert.Equal(0, exit);
        Assert.Contains("goldpath: 'softdelete' wired", output, StringComparison.Ordinal);
        Assert.All(runner.Calls, c => Assert.Equal(app.Root, c.WorkingDirectory));
    }

    [Fact]
    public void Db_add_and_bundle_take_a_name_before_path()
    {
        using var app = new FakeApp();
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["has-pending-model-changes"] = 1;   // a model change is pending

        var (exit, output, _) = Run(runner, "db", "add", "add-thing", "--path", app.Root);

        Assert.Equal(0, exit);
        Assert.Contains("── goldpath db add: 'AddThing' for ", output, StringComparison.Ordinal);
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("AddThing"));

        var bundleRunner = new FakeProcessRunner();
        var (bundleExit, _, bundleError) = Run(bundleRunner, "db", "bundle", "out", "--path", app.Root);

        Assert.Equal(0, bundleExit);
        Assert.Empty(bundleError);
        Assert.Contains(bundleRunner.Calls, c => c.Arguments.Contains("bundle"));
    }

    [Fact]
    public void Db_add_without_a_name_teaches_the_shape()
    {
        using var app = new FakeApp();

        var (exit, _, error) = Run("db", "add", "--path", app.Root);

        Assert.Equal(2, exit);
        Assert.Equal("goldpath: goldpath db add needs a name: goldpath db add <migration-name>\n", error);
    }

    [Fact]
    public void Db_init_and_status_never_swallow_a_stray_token_as_a_name()
    {
        using var app = new FakeApp();

        foreach (var verb in new[] { "init", "status" })
        {
            var (exit, _, error) = Run("db", verb, "stray", "--path", app.Root);

            Assert.Equal(2, exit);
            Assert.Equal($"goldpath: unexpected arguments: stray --path {app.Root} (only --path <dir> is understood here)\n", error);
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("bundle")]
    public void Db_add_and_bundle_with_nothing_after_them_run_against_the_current_directory(string verb)
    {
        // The test process runs from its bin directory: no migration owner there, so the
        // command teaches — the point is that an EMPTY rest never indexes rest[0].
        var (exit, _, error) = Run("db", verb);

        Assert.Equal(1, exit);
        Assert.StartsWith("goldpath: no migration owner found", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_worker_defaults_to_a_queue_trigger()
    {
        using var app = new FakeApp(messagingWired: true);

        var (exit, _, error) = Run("add", "worker", "payments", "--path", app.Root);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.True(File.Exists(Path.Combine(app.Root, "src", "Shop.PaymentsWorker", "WorkItems", "WorkItemQueuedHandler.cs")));
    }

    [Fact]
    public void Add_worker_defaults_to_the_current_directory()
    {
        var (exit, _, error) = Run("add", "worker", "payments", "--trigger", "schedule");

        Assert.Equal(1, exit);
        Assert.Equal(
            $"goldpath: no manifest at {Path.Combine(".", ".goldpath", "manifest.yaml")} — goldpath add runs inside a Goldpath-generated app (or pass --path).\n",
            error);
    }

    [Fact]
    public void Add_worker_reads_trigger_and_path_in_either_order()
    {
        using var app = new FakeApp(jobsWired: true);

        var (exit, _, error) = Run("add", "worker", "eod-report", "--path", app.Root, "--trigger", "jobs");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.True(File.Exists(Path.Combine(app.Root, "src", "Shop.EodReportWorker", "Reports", "NightlyReportJob.cs")));
    }

    [Theory]
    [InlineData("--trigger")]
    [InlineData("--path")]
    [InlineData("--frobnicate", "x")]
    [InlineData("--trigger", "jobs", "--path")]
    public void Add_worker_refuses_a_dangling_or_unknown_flag(params string[] rest)
    {
        var (exit, output, error) = Run(["add", "worker", "payments", .. rest]);

        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.Equal($"goldpath: unexpected arguments: {string.Join(' ', rest)} (only --trigger <t> and --path <dir> are understood here)\n", error);
    }

    [Fact]
    public void Add_worker_with_an_unknown_trigger_exits_2()
    {
        var (exit, _, error) = Run("add", "worker", "payments", "--trigger", "cron");

        Assert.Equal(2, exit);
        Assert.Equal("goldpath: unknown trigger 'cron' — one of: queue, schedule, jobs\n", error);
    }

    [Fact]
    public void New_passes_every_template_argument_through_in_order()
    {
        var runner = new FakeProcessRunner();
        var outDir = Path.Combine(Path.GetTempPath(), $"goldpath-new-mut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);   // db init scans -o for owners; keep it empty and ours
        try
        {
            var (exit, _, _) = Run(runner, "new", "worker", "-n", "Billing.Nightly", "--trigger", "schedule", "-o", outDir);

            Assert.Equal(0, exit);
            Assert.Equal(["new", "goldpath-worker", "-n", "Billing.Nightly", "--trigger", "schedule", "-o", outDir], runner.Calls[0].Arguments);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void A_failure_exception_exits_1_with_the_message_prefixed()
    {
        var (exit, output, error) = Run("export", "compose", "--path", Path.GetTempPath());

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.StartsWith("goldpath: ", error, StringComparison.Ordinal);
        Assert.EndsWith("\n", error, StringComparison.Ordinal);
    }
}
