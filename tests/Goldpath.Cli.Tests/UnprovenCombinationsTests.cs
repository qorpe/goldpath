using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// GPC0001 (Oracle RFC §3, #412): <c>db: oracle</c> with a broker is the one combination no
/// golden-manifest shape proves (open-threads T16). Fail-closed on the broker: absent is the
/// schema's default <c>rabbitmq</c>, and only <c>none</c> is clean.
/// </summary>
public class UnprovenCombinationsTests
{
    [Theory]
    [InlineData("providers:\n  db: oracle\n", "oracle+rabbitmq")]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: rabbitmq\n", "oracle+rabbitmq")]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: kafka\n", "oracle+kafka")]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: inmemory\n", "oracle+inmemory")]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: None\n", "oracle+None")]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: [none]\n", "oracle+<not a name>")]
    [InlineData("kind: worker\nproviders:\n  db: oracle\n", "oracle+rabbitmq")]
    public void Oracle_with_any_broker_but_none_is_reported_and_names_T16(string manifest, string value)
    {
        var finding = Assert.Single(UnprovenCombinations.Find(ArtefactYaml.Parse(manifest)));

        Assert.Equal("GPC0001", finding.Id);
        Assert.Equal("providers.db+providers.broker", finding.Path);
        Assert.Equal(value, finding.Value);
        Assert.Contains("T16", finding.Message, StringComparison.Ordinal);
        Assert.Contains("broker: none", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: solution\nproviders:\n  db: oracle\n  broker: none\n")]
    [InlineData("kind: solution\nproviders:\n  db: postgresql\n")]
    [InlineData("kind: solution\nproviders:\n  db: sqlserver\n  broker: rabbitmq\n")]
    [InlineData("kind: solution\nproviders:\n  broker: rabbitmq\n")]
    [InlineData("kind: service\nproviders:\n  db: oracle\n")]          // the broker is the solution's; its manifest is judged
    [InlineData("kind: solution\n")]
    [InlineData("kind: solution\nproviders: oracle\n")]
    [InlineData("- db: oracle\n")]
    [InlineData("")]
    public void Everything_else_is_clean(string manifest)
        => Assert.Empty(UnprovenCombinations.Find(ArtefactYaml.Parse(manifest)));
}
