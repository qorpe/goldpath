using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// <c>goldpath add feature X --plan</c> (goldpath-authoring RFC §3, Slice 0): authoring runs it for
/// every feature it proposes and turns each printed decision into a question or a sourced
/// answer. So the plan must say EXACTLY what the real run would tell the team, and it must
/// touch nothing — an authoring session that wrote the manifest could not end its own turn.
/// </summary>
public class AddFeaturePlanTests
{
    public static TheoryData<string> Features => [.. FeatureRecipes.Names];

    private static (int ExitCode, JsonObject Json) Plan(FakeApp app, string feature, FakeProcessRunner? runner = null)
    {
        var output = new StringWriter();
        var exitCode = CliRunner.Run(["add", "feature", feature, "--plan", "--path", app.Root], runner ?? new FakeProcessRunner(), output, TextWriter.Null);
        return (exitCode, exitCode == 0 ? JsonNode.Parse(output.ToString())!.AsObject() : []);
    }

    private static List<string> Decisions(JsonObject plan) => [.. plan["decisions"]!.AsArray().Select(d => d!.GetValue<string>())];

    [Theory]
    [MemberData(nameof(Features))]
    public void The_plan_lists_word_for_word_the_decisions_the_real_run_prints(string feature)
    {
        // Twin apps from the same fixture: one is planned, the other really grown. The real
        // run's "→" lines are the recipe's text as the team reads it — the plan is pinned to
        // THAT, not to a copy of the recipe kept in this test.
        using var planned = new FakeApp(messagingWired: true);
        using var grown = new FakeApp(messagingWired: true);

        var (exitCode, plan) = Plan(planned, feature);
        var output = new StringWriter();
        Assert.Equal(0, CliRunner.Run(["add", "feature", feature, "--path", grown.Root], new FakeProcessRunner(), output, TextWriter.Null));

        Assert.Equal(0, exitCode);
        var printed = output.ToString().Split('\n')
            .Where(line => line.StartsWith("  → ", StringComparison.Ordinal))
            .Select(line => line["  → ".Length..].TrimEnd('\r'))
            .ToList();
        Assert.NotEmpty(printed);
        Assert.Equal(printed, Decisions(plan));
    }

    [Fact]
    public void The_plan_carries_the_recipe_and_its_migration_decision()
    {
        using var app = new FakeApp();

        var (exitCode, plan) = Plan(app, "audittrail");

        Assert.Equal(0, exitCode);
        Assert.Equal("audittrail", plan["feature"]!.GetValue<string>());
        Assert.Equal("auditTrail", plan["manifestKey"]!.GetValue<string>());
        Assert.False(plan["alreadyEnabled"]!.GetValue<bool>());
        Assert.True(plan["modelGrows"]!.GetValue<bool>());
        Assert.Contains("  auditTrail: true", plan["manifestLines"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Contains("Goldpath.AuditTrail", plan["packages"]!.AsArray().Select(p => p!.GetValue<string>()));
        var decisions = Decisions(plan);
        Assert.StartsWith("mark audited entities: partial class X : IAuditLogged", decisions[0], StringComparison.Ordinal);
        Assert.StartsWith("the model grew: run `goldpath db add AddAudittrail`", decisions[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_names_the_provider_edit_a_born_bus_makes()
    {
        using var app = new FakeApp();

        var (_, plan) = Plan(app, "outbox");

        Assert.Equal(["providers.broker: rabbitmq"], plan["providerEdits"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public void The_plan_prints_the_recipe_text_unescaped_so_it_can_be_copied_verbatim()
    {
        using var app = new FakeApp();
        var output = new StringWriter();

        CliRunner.Run(["add", "feature", "idempotency", "--plan", "--path", app.Root], new FakeProcessRunner(), output, TextWriter.Null);

        // decisions.yaml carries the opt-in verbatim: "—" and "'" must not arrive as — / '.
        Assert.Contains("GP1001 fails the build until each one is (the template's sample CreateOrderCommand is marked for you)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_writes_nothing_and_runs_no_engine()
    {
        using var app = new FakeApp(messagingWired: true, sampleCommand: "api", smokeTest: true);
        var before = Snapshot(app.Root);
        var runner = new FakeProcessRunner();

        foreach (var feature in FeatureRecipes.Names)
        {
            Assert.Equal(0, Plan(app, feature, runner).ExitCode);
        }

        Assert.Equal(before, Snapshot(app.Root));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void An_enabled_feature_is_reported_as_such_and_still_lists_its_decisions()
    {
        // "Already on — used as follows" (RFC §3): relying on a feature still means deciding
        // its opt-ins for the NEW entity, so the decisions are printed either way.
        using var app = new FakeApp();
        Assert.Equal(0, CliRunner.Run(["add", "feature", "softdelete", "--path", app.Root], new FakeProcessRunner(), TextWriter.Null, TextWriter.Null));

        var (exitCode, plan) = Plan(app, "softdelete");

        Assert.Equal(0, exitCode);
        Assert.True(plan["alreadyEnabled"]!.GetValue<bool>());
        Assert.StartsWith("mark soft-deletable entities", Decisions(plan)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_may_come_before_or_after_the_path()
    {
        using var app = new FakeApp();
        var output = new StringWriter();

        Assert.Equal(0, CliRunner.Run(["add", "feature", "caching", "--path", app.Root, "--plan"], new FakeProcessRunner(), output, TextWriter.Null));
        Assert.Equal("distributedCaching", JsonNode.Parse(output.ToString())!["manifestKey"]!.GetValue<string>());
        Assert.DoesNotContain("distributedCaching", app.Read(app.Manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_refuses_what_the_real_run_refuses()
    {
        using var worker = new FakeApp(kind: "worker");
        using var app = new FakeApp();

        Assert.Equal(1, CliRunner.Run(["add", "feature", "caching", "--plan", "--path", worker.Root], new FakeProcessRunner(), TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, CliRunner.Run(["add", "feature", "sagas", "--plan", "--path", app.Root], new FakeProcessRunner(), TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, CliRunner.Run(["add", "feature", "caching", "--plan", "--verbose", "--path", app.Root], new FakeProcessRunner(), TextWriter.Null, TextWriter.Null));
    }

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);
}
