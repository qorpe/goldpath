using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// <c>goldpath add feature multitenancy</c> against the template's own smoke test.
///
/// Tenancy is fail-closed: a request without the tenant header is refused with 400 before
/// authentication runs. The template's smoke test sends the header under
/// <c>UseMultiTenancy</c>; the recipe did not touch it, so an app grown with the verb failed
/// its own smoke — an authed shape expecting 401 got 400. Found by the nightly GmGrownRest
/// shape once it built (2026-09-14), the first time that shape ever reached its smoke.
/// </summary>
public class AddMultiTenancySmokeTests
{
    private static int Add(FakeApp app, FakeProcessRunner runner)
        => CliRunner.Run(["add", "feature", "multitenancy", "--path", app.Root], runner, TextWriter.Null, TextWriter.Null);

    [Fact]
    public void The_smoke_sends_the_tenant_header_with_exactly_the_templates_line()
    {
        using var app = new FakeApp(smokeTest: true);

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        // Parity with the template itself: its UseMultiTenancy line, directly after the client.
        var expected = "        var client = app.CreateHttpClient(\"api\");\n" + TemplateTenantLine() + "\n";
        Assert.Contains(expected, app.Read(app.SmokeTest), StringComparison.Ordinal);
    }

    [Fact]
    public void The_smoke_project_can_compile_the_header_line()
    {
        using var app = new FakeApp(smokeTest: true);

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        // GoldpathHeaders lives in Goldpath.Abstractions under namespace Goldpath — the template
        // adds both under the same symbol, so the grown app must have both.
        Assert.Contains("using Goldpath;", app.Read(app.SmokeTest), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Goldpath.Abstractions\" />", app.Read(app.SmokeProject), StringComparison.Ordinal);
    }

    [Fact]
    public void A_smoke_that_already_sends_the_header_is_left_as_it_is()
    {
        using var app = new FakeApp(smokeTest: true);
        var sending = app.Read(app.SmokeTest).Replace(
            "        var client = app.CreateHttpClient(\"api\");\n",
            "        var client = app.CreateHttpClient(\"api\");\n        client.DefaultRequestHeaders.Add(Goldpath.GoldpathHeaders.TenantId, \"t1\");\n",
            StringComparison.Ordinal);
        File.WriteAllText(app.SmokeTest, sending);

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        Assert.Equal(sending, app.Read(app.SmokeTest));
    }

    [Fact]
    public void Without_a_smoke_test_the_feature_still_lands()
    {
        using var app = new FakeApp();

        Assert.Equal(0, Add(app, new FakeProcessRunner()));

        Assert.Contains("app.UseGoldpathMultiTenancy();", app.Read(app.Program), StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_refusal_restores_the_smoke_test_and_its_project_too()
    {
        using var app = new FakeApp(smokeTest: true);
        var before = new[] { app.SmokeTest, app.SmokeProject }.ToDictionary(p => p, app.Read);
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["--repo"] = 1;   // fails only the drift call

        Assert.Equal(1, Add(app, runner));

        foreach (var (path, content) in before)
        {
            Assert.Equal(content, app.Read(path));
        }
    }

    /// <summary>The line the solution template's smoke test emits under <c>UseMultiTenancy</c>, right after the client.</summary>
    private static string TemplateTenantLine()
    {
        var template = File.ReadAllLines(Path.Combine(
            RepoRoot(), "templates", "goldpath-solution", "tests", "GoldpathTemplate.SmokeTests", "SmokeTests.cs"));
        var client = Array.FindIndex(template, l => l.Contains("app.CreateHttpClient(\"api\");", StringComparison.Ordinal));
        Assert.True(client >= 0, "the template smoke test's client line was not found");
        Assert.Equal("//#if (UseMultiTenancy)", template[client + 1]);
        Assert.Equal("//#endif", template[client + 3]);
        return template[client + 2];
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
