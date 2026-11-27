using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// ORA-9 (#412): until ORA-10 (#413) teaches them Oracle, the verbs that infer the app's
/// provider REFUSE an Oracle app in words. Before this guard each of them read "not
/// PostgreSQL" as SQL Server (or "not SQL Server" as PostgreSQL) and would have written that
/// provider's code into an app generated with <c>--db oracle</c>. A refusal touches nothing:
/// every file is byte-identical afterwards and no process (dotnet, specdrift) was started.
/// </summary>
public class OracleAppRefusalTests
{
    public static TheoryData<string[]> Verbs()
    {
        var data = new TheoryData<string[]>();
        foreach (var feature in FeatureRecipes.Names)
        {
            data.Add(["add", "feature", feature]);
        }

        data.Add(["add", "worker", "eod", "--trigger", "jobs"]);
        data.Add(["add", "worker", "ingest", "--trigger", "queue"]);
        data.Add(["add", "worker", "tick", "--trigger", "schedule"]);
        data.Add(["new", "service", "Billing"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void A_verb_that_infers_the_provider_refuses_an_oracle_app_and_writes_nothing(string[] verb)
    {
        using var app = new FakeApp(oracle: true, jobsWired: true, messagingWired: true, lockingWired: true);
        var before = Snapshot(app.Root);
        var runner = new FakeProcessRunner();
        var error = new StringWriter();

        var exit = CliRunner.Run([.. verb, "--path", app.Root], runner, TextWriter.Null, error);

        Assert.NotEqual(0, exit);
        Assert.Contains("Oracle", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("#413", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.Equal(before, Snapshot(app.Root));
        Assert.DoesNotContain("SqlServer", AllText(app.Root), StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", AllText(app.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_reader_names_an_oracle_app_oracle()
    {
        using var app = new FakeApp(oracle: true);

        Assert.Equal("oracle", AppFacts.Read(AppFiles.Locate(app.Root)).DatabaseProvider);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("none")]
    public void The_providers_the_verbs_write_pass_the_guard(string provider)
        => Facts(provider).RequireWritableProvider("add feature");

    [Theory]
    [InlineData("oracle")]
    [InlineData("mysql")]
    [InlineData("")]
    public void Any_other_provider_is_refused_not_routed_to_an_else_branch(string provider)
    {
        var refusal = Assert.Throws<CliFailureException>(() => Facts(provider).RequireWritableProvider("add worker"));

        Assert.Contains("goldpath add worker", refusal.Message, StringComparison.Ordinal);
    }

    private static AppFacts Facts(string provider) => new()
    {
        DbContextName = "ShopDbContext",
        DatabaseProvider = provider,
        ConnectionName = "shopdb",
        CachingWired = false,
        JobsWired = false,
        MessagingWired = false,
        AuthWired = false,
    };

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(root, path)] = File.ReadAllText(path);
        }

        return files;
    }

    private static string AllText(string root) => string.Concat(Snapshot(root).Values);
}
