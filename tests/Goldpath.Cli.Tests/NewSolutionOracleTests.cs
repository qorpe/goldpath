using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// <c>goldpath new solution --db oracle</c> (ORA-9, #412; owner decision 2026-11-25): an Oracle
/// app ships broker-less until T16's trigger fires, and the template's default broker is
/// <c>rabbitmq</c>. Without <c>--broker none</c> the CLI refuses in words and runs nothing, in
/// every spelling <c>dotnet new</c> accepts; with it, <c>db</c> passes through untouched.
/// </summary>
public class NewSolutionOracleTests
{
    public static TheoryData<string[]> RefusedShapes() => new()
    {
        new[] { "--db", "oracle" },
        new[] { "--db", "oracle", "--broker", "rabbitmq" },
        new[] { "--db=oracle" },
        new[] { "--db:oracle", "--broker:rabbitmq" },
        new[] { "-db", "oracle" },
        new[] { "-db=oracle", "-b", "rabbitmq" },
        new[] { "--db", "ORACLE" },
        new[] { "--db", "Oracle", "--broker", "none", "--broker", "rabbitmq" },
        new[] { "--db", "oracle", "--broker" },
        new[] { "--db", "oracle", "--broker", "" },
        new[] { "--db", "oracle", "--auth", "none", "--features", "bulk" },
    };

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public void Oracle_without_broker_none_is_refused_and_nothing_runs(string[] flags)
    {
        var runner = new FakeProcessRunner();
        var error = new StringWriter();

        var exit = CliRunner.Run(["new", "solution", "-n", "Shop", .. flags], runner, TextWriter.Null, error);

        Assert.Equal(2, exit);
        Assert.Empty(runner.Calls);
        Assert.Contains("--broker none", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("T16", error.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string[]> AcceptedShapes() => new()
    {
        new[] { "--db", "oracle", "--broker", "none" },
        new[] { "--broker", "none", "--db", "oracle" },
        new[] { "--db=oracle", "--broker=none" },
        new[] { "--db:oracle", "-b", "NONE" },
        new[] { "-db", "oracle", "-b:none" },
        new[] { "--db", "oracle", "--broker", "none", "--auth", "none", "--features", "bulk", "--features", "archival" },
    };

    [Theory]
    [MemberData(nameof(AcceptedShapes))]
    public void Oracle_with_broker_none_passes_through_to_dotnet_new_untouched(string[] flags)
    {
        var runner = new FakeProcessRunner();

        var exit = CliRunner.Run(["new", "solution", "-n", "Shop", .. flags], runner, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Equal(["new", "goldpath-solution", "-n", "Shop", .. flags], runner.Calls[0].Arguments);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void The_other_databases_keep_the_default_broker(string db)
    {
        var runner = new FakeProcessRunner();

        Assert.Equal(0, CliRunner.Run(["new", "solution", "-n", "Shop", "--db", db], runner, TextWriter.Null, TextWriter.Null));
        Assert.Equal(["new", "goldpath-solution", "-n", "Shop", "--db", db], runner.Calls[0].Arguments);
    }

    /// <summary>
    /// <c>dotnet new</c> expands any token starting with <c>@</c> into a response file's contents,
    /// wherever it stands, and the guard does not read files: so every such token is refused
    /// before anything runs, whatever the database. That is fail-closed rather than "refuse an rsp
    /// that says oracle", so a file the guard never opens cannot carry the combination past it.
    /// </summary>
    public static TheoryData<string[]> ResponseFileShapes() => new()
    {
        new[] { "@a.rsp" },
        new[] { "--db", "oracle", "--broker", "none", "@a.rsp" },
        new[] { "--db", "postgresql", "@a.rsp" },
        new[] { "--description", "@notes.rsp" },
        new[] { "@" },
        new[] { "@/tmp/" + new string('x', 5000) + ".rsp" },
    };

    [Theory]
    [MemberData(nameof(ResponseFileShapes))]
    public void A_response_file_is_refused_and_nothing_runs(string[] flags)
    {
        var runner = new FakeProcessRunner();
        var error = new StringWriter();

        var exit = CliRunner.Run(["new", "solution", "-n", "Shop", .. flags], runner, TextWriter.Null, error);

        Assert.Equal(2, exit);
        Assert.Empty(runner.Calls);
        Assert.Contains("does not read a response file", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--db and --broker on the command line", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 65), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_file_that_says_oracle_is_refused_even_though_it_exists()
    {
        // The verifier's probe P1: a real file holding "--db oracle" (dotnet new would expand it
        // into an Oracle app on the default rabbitmq broker). Refused, never opened, nothing runs.
        var directory = Directory.CreateTempSubdirectory("goldpath-rsp-");
        try
        {
            var rsp = Path.Combine(directory.FullName, "a.rsp");
            File.WriteAllText(rsp, "--db\noracle\n");
            var runner = new FakeProcessRunner();

            var exit = CliRunner.Run(["new", "solution", "-n", "Shop", "@" + rsp], runner, TextWriter.Null, TextWriter.Null);

            Assert.Equal(2, exit);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_name_that_reads_oracle_is_not_a_database_choice()
    {
        var runner = new FakeProcessRunner();

        Assert.Equal(0, CliRunner.Run(["new", "solution", "-n", "oracle", "--description", "--db oracle"], runner, TextWriter.Null, TextWriter.Null));
    }
}
