using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// <c>goldpath add feature idempotency</c> against the template's own sample command.
///
/// GP1001 fails the build of any app that composes idempotency while a Mediant command is
/// unmarked — a Warning the generated apps promote to an error, on purpose. The template
/// marks its sample <c>CreateOrderCommand</c> under <c>UseIdempotency</c>; the recipe did not,
/// so an app grown with the verb stopped building at the first <c>dotnet build</c>. The
/// recipes promise every line mirrors what <c>--features X</c> generates, and this one was
/// missing four. Found by the nightly GmGrownRest shape once NU1504 stopped hiding it
/// (2026-09-14); the published preview.8 CLI has the same gap.
/// </summary>
public class AddIdempotencySampleTests
{
    private static int Add(FakeApp app, FakeProcessRunner runner, TextWriter? output = null)
        => CliRunner.Run(["add", "feature", "idempotency", "--path", app.Root], runner, output ?? TextWriter.Null, TextWriter.Null);

    [Fact]
    public void The_sample_command_is_marked_with_exactly_the_templates_lines()
    {
        using var app = new FakeApp(sampleCommand: "api");

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        // Parity with the template itself, not with a copy of it in this test: the block the
        // template emits under UseIdempotency must sit directly above the record.
        var expected = string.Join('\n', TemplateIdempotencyBlock()) + "\npublic record CreateOrderCommand(";
        Assert.Contains(expected, app.Read(app.SampleCommand!), StringComparison.Ordinal);

        // vertical-slice: the command compiles in the Api project, which reaches the attribute
        // through Goldpath.Idempotency — the template adds no reference here, so neither do we.
        Assert.DoesNotContain("Mediant.Behaviors", app.Read(app.ApiProject), StringComparison.Ordinal);
    }

    [Fact]
    public void In_clean_architecture_the_commands_own_project_gets_the_attribute_package_and_its_pin()
    {
        using var app = new FakeApp(sampleCommand: "application");

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        Assert.Contains("[Mediant.Behaviors.Attributes.Idempotent(", app.Read(app.SampleCommand!), StringComparison.Ordinal);

        // The Application project does not reference Goldpath.Idempotency, so the attribute is
        // not there transitively (the template's own clean-architecture fix, #254).
        Assert.Contains("<PackageReference Include=\"Mediant.Behaviors\" />", app.Read(app.ApplicationProject), StringComparison.Ordinal);

        // Central package management: a reference without its PackageVersion is NU1010. The pin
        // follows the app's own Mediant line, read from Mediant.AspNetCore.
        Assert.Contains("<PackageVersion Include=\"Mediant.Behaviors\" Version=\"1.4.1\" />", app.Read(Path.Combine(app.Root, "Directory.Packages.props")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sample_the_team_already_marked_is_left_as_it_is()
    {
        using var app = new FakeApp(sampleCommand: "api");
        var marked = app.Read(app.SampleCommand!).Replace(
            "public record CreateOrderCommand(",
            "[Mediant.Behaviors.Attributes.Idempotent]\npublic record CreateOrderCommand(",
            StringComparison.Ordinal);
        File.WriteAllText(app.SampleCommand!, marked);

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        Assert.Equal(marked, app.Read(app.SampleCommand!));
    }

    [Fact]
    public void Without_the_sample_the_feature_still_lands_and_the_next_step_names_the_build_rule()
    {
        using var app = new FakeApp();
        var output = new StringWriter();

        Assert.Equal(0, Add(app, new FakeProcessRunner(), output));

        Assert.Contains("builder.AddGoldpathIdempotency();", app.Read(app.Program), StringComparison.Ordinal);

        // The team's own commands are the team's decision — but the next step must say that
        // the build refuses until it is made, not read like an optional nicety.
        Assert.Contains("GP1001", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_refusal_restores_the_sample_command_and_its_project_too()
    {
        using var app = new FakeApp(sampleCommand: "application");
        var props = Path.Combine(app.Root, "Directory.Packages.props");
        var before = new[] { app.SampleCommand!, app.ApplicationProject, props }.ToDictionary(p => p, app.Read);
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["--repo"] = 1;   // fails only the drift call

        Assert.Equal(1, Add(app, runner));

        foreach (var (path, content) in before)
        {
            Assert.Equal(content, app.Read(path));
        }
    }

    /// <summary>The lines the solution template emits between <c>#if (UseIdempotency)</c> and <c>#endif</c> above the sample record.</summary>
    private static string[] TemplateIdempotencyBlock()
    {
        var template = File.ReadAllLines(Path.Combine(
            RepoRoot(), "templates", "goldpath-solution", "src", "GoldpathTemplate.Api", "Orders", "Features", "CreateOrder.cs"));
        var start = Array.IndexOf(template, "#if (UseIdempotency)");
        var end = Array.IndexOf(template, "#endif", start);
        Assert.True(start >= 0 && end > start, "the template's UseIdempotency block above the sample record was not found");
        return template[(start + 1)..end];
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
}
