using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// The classes that touch <c>goldpath_campaign_tuning_reload_refused_total</c>, a process-wide counter
/// with no tag to tell one test's measurement from another's: <see cref="R3TuningReloadTests"/> asserts
/// exact counts of it and <see cref="MetricsExportTests"/> emits one, so they must never run at once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TuningReloadCounterCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "tuning-reload-counter";
}
