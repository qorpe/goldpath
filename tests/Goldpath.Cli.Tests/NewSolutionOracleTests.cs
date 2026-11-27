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

    [Fact]
    public void A_name_that_reads_oracle_is_not_a_database_choice()
    {
        var runner = new FakeProcessRunner();

        Assert.Equal(0, CliRunner.Run(["new", "solution", "-n", "oracle", "--description", "--db oracle"], runner, TextWriter.Null, TextWriter.Null));
    }
}
