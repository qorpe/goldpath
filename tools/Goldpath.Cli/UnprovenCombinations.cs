using System.Text.Json.Nodes;

namespace Goldpath.Cli;

/// <summary>
/// GA gate G3, one level up from <see cref="RoadmapValues"/>: two values that each run, chosen
/// TOGETHER, where the combination does not. Today there is exactly one (Oracle RFC §3): an
/// Oracle app with a broker. The messaging engine's outbox has no proven Oracle store, so
/// Oracle ships with <c>broker: none</c> and the broker-on shape waits for open-threads T16's
/// trigger (the post-G5 default engine publishes an Oracle outbox store).
///
/// The RFC places this rule in specdrift; specdrift is a separate repository, so it is
/// implemented here first, as the T45 precedent did, and runs where the roadmap warning runs:
/// after validate, as a warning that does not fail the check.
/// </summary>
public static class UnprovenCombinations
{
    /// <summary>The id of the Oracle-with-a-broker finding.</summary>
    public const string OracleWithBroker = "GPC0001";

    /// <summary>One selected combination no shape proves: its id, where it sits, the values, and what to do.</summary>
    public sealed record Finding(string Id, string Path, string Value, string Message);

    /// <summary>
    /// Every unproven combination <paramref name="manifest"/> selects. Fail-closed on the broker: an
    /// ABSENT broker is the schema's default, <c>rabbitmq</c>, and any broker other than
    /// <c>none</c> (a roadmap one included, which is warned about on its own too) is broker-on.
    /// A service manifest is not judged: its <c>providers</c> may override only the database,
    /// and the broker is the owning solution's, whose manifest carries the finding.
    /// </summary>
    public static IReadOnlyList<Finding> Find(JsonNode? manifest)
    {
        if (manifest is not JsonObject root
            || Scalar(root["kind"]) == "service"
            || root["providers"] is not JsonObject providers
            || Scalar(providers["db"]) != "oracle")
        {
            return [];
        }

        var broker = providers.ContainsKey("broker") ? Scalar(providers["broker"]) : "rabbitmq";
        if (broker == "none")
        {
            return [];
        }

        return
        [
            new Finding(
                OracleWithBroker,
                "providers.db+providers.broker",
                $"oracle+{broker ?? "<not a name>"}",
                "an Oracle app ships with broker: none. No golden-manifest shape proves Oracle with a broker, because the messaging engine's outbox has no proven Oracle store (open-threads T16). Set providers.broker: none."),
        ];
    }

    private static string? Scalar(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
