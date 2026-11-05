using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// S3, the differential run (messaging-exit RFC §7.1, GA gate G5 Part 3/5): every conformance fact is run on the oracle
/// and then on the candidate, and what each left behind is RECORDED rather than asserted — how often each message
/// arrived, at which attempts, with which headers, in which order where the seam promises one, what the error queues
/// hold and the state of the outbox and inbox rows. The candidate's record must equal the oracle's line for line, but
/// for the entries of the reviewed allow-list, each carrying the reason the seam does not promise that line. A fact can
/// pass on both engines and still fail here: that is the point of S3, which rules out "passes our tests" meaning
/// "passes tests we wrote around our own behaviour".
/// </summary>
[Collection("conformance")]
[Trait("Category", "Differential")]
public sealed class BusDifferentialTests(ConformanceContainers containers)
{
    private const string Oracle = "masstransit";
    private const string Candidate = "wolverine";

    // Where the seam promises an order (§7.1: one publisher's messages, on one endpoint at a concurrency limit of 1, in
    // one process), the record carries it and the comparator compares it; everywhere else an order is the engine's.
    private static readonly HashSet<string> OrderedFacts =
        [nameof(BusConformanceTests.S2_14_with_a_concurrency_limit_of_one_an_endpoint_handles_its_queue_in_publish_order)];

    private static readonly Lazy<IReadOnlyList<AllowedDifference>> AllowList =
        new(() => OutcomeComparator.LoadAllowList(Path.Combine(AppContext.BaseDirectory, "differential-allowlist.json")));

    /// <summary>Every fact of both conformance classes, found by reflection, so a fact added to either joins S3 unasked.</summary>
    public static TheoryData<string> Facts()
    {
        var data = new TheoryData<string>();
        foreach (var fact in FactMethods())
        {
            data.Add(fact.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Facts))]
    public async Task S3_the_candidates_record_of_a_fact_equals_the_oracles(string fact)
    {
        var oracle = await RecordAsync(Oracle, fact);
        var candidate = await RecordAsync(Candidate, fact);
        var differences = OutcomeComparator.Compare(fact, oracle, candidate, AllowList.Value);
        DifferentialReport.Write(fact, oracle, candidate, differences);

        var divergences = differences.Where(d => d.AllowedBy is null).ToList();
        Assert.True(
            divergences.Count == 0,
            $"{Candidate} diverges from the oracle on {fact}:\n" + string.Join("\n", divergences.Select(d => $"  {d.Path}: oracle={d.Oracle ?? "(absent)"} {Candidate}={d.Candidate ?? "(absent)"}")));
    }

    [Fact]
    public void Every_conformance_fact_is_in_the_differential()
    {
        // By construction (reflection), and held here so a fact moved out of the two classes is noticed.
        var facts = FactMethods().Select(m => m.Name).ToList();
        Assert.Equal(facts.Count, facts.Distinct().Count());
        Assert.Contains(nameof(BusConformanceTests.S2_1_exactly_once_the_same_message_delivered_twice_handles_once), facts);
        Assert.Contains(nameof(BatchConformanceTests.S2_12_what_a_batch_handler_publishes_is_delivered), facts);
        Assert.All(OrderedFacts, ordered => Assert.Contains(ordered, facts));
        Assert.Equal(typeof(BusConformanceTests).GetMethods().Count(IsFact) + typeof(BatchConformanceTests).GetMethods().Count(IsFact), facts.Count);
    }

    private async Task<SortedDictionary<string, string>> RecordAsync(string engine, string fact)
    {
        // Each engine's run of a fact starts from nothing the other left: no parked message, no outbox or inbox row,
        // no recorded delivery. Every fact stops its hosts before it returns, so what is read after is final.
        await OutcomeProbes.PurgeErrorQueuesAsync(containers);
        await OutcomeProbes.TruncateStoreAsync(containers);
        containers.Recorder.Clear();

        var method = FactMethods().Single(m => m.Name == fact);
        object suite = method.DeclaringType == typeof(BusConformanceTests)
            ? engine == Oracle ? new MassTransitBusConformance(containers) : new WolverineBusConformance(containers)
            : engine == Oracle ? new MassTransitBatchConformance(containers) : new WolverineBatchConformance(containers);
        await (Task)method.Invoke(suite, null)!;

        return OutcomeRecord.Of(
            containers.Recorder.Snapshot(), await OutcomeProbes.ParkedAsync(containers), await OutcomeProbes.StoreAsync(containers),
            OrderedFacts.Contains(fact));
    }

    private static IEnumerable<MethodInfo> FactMethods()
        => typeof(BusConformanceTests).GetMethods().Where(IsFact).Concat(typeof(BatchConformanceTests).GetMethods().Where(IsFact)).OrderBy(m => m.Name, StringComparer.Ordinal);

    private static bool IsFact(MethodInfo method) => method.GetCustomAttribute<FactAttribute>() is not null;
}

/// <summary>
/// The comparison report (messaging-exit §7.1 S3): per fact, both engines' records and every line they disagree on with
/// the allow-list entry that excuses it; and a summary of every fact run so far. Written where
/// <c>GOLDPATH_DIFFERENTIAL_OUT</c> points (CI uploads it as an artifact), or beside the test assembly.
/// </summary>
public static class DifferentialReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The directory the report is written to.</summary>
    public static string Directory
        => Environment.GetEnvironmentVariable("GOLDPATH_DIFFERENTIAL_OUT") is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "differential");

    /// <summary>Writes one fact's comparison, then rewrites the summary from every fact's comparison on disk.</summary>
    public static void Write(string fact, IReadOnlyDictionary<string, string> oracle, IReadOnlyDictionary<string, string> candidate, IReadOnlyList<Difference> differences)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var comparison = new FactComparison(fact, oracle, candidate, differences);
        File.WriteAllText(Path.Combine(Directory, fact + ".json"), JsonSerializer.Serialize(comparison, Json));

        var summary = new StringBuilder()
            .AppendLine("# S3 differential report")
            .AppendLine()
            .AppendLine("Oracle: `masstransit`. Candidate: `wolverine`. Each fact's records and differences are in `<fact>.json` beside this file.")
            .AppendLine()
            .AppendLine("| Fact | Lines | Differences | Allowed | Divergences |")
            .AppendLine("|---|---|---|---|---|");
        var divergent = new List<(string Fact, Difference Difference)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal))
        {
            var each = JsonSerializer.Deserialize<FactComparison>(File.ReadAllText(path), Json)!;
            var open = each.Differences.Where(d => d.AllowedBy is null).ToList();
            summary.AppendLine($"| `{each.Fact}` | {each.Oracle.Count} | {each.Differences.Count} | {each.Differences.Count - open.Count} | {(open.Count == 0 ? "0" : $"**{open.Count}**")} |");
            divergent.AddRange(open.Select(d => (each.Fact, d)));
        }

        summary.AppendLine().AppendLine("## Divergences").AppendLine();
        if (divergent.Count == 0)
        {
            summary.AppendLine("None.");
        }

        foreach (var (each, d) in divergent)
        {
            summary.AppendLine($"- `{each}` `{d.Path}`: oracle `{d.Oracle ?? "(absent)"}`, candidate `{d.Candidate ?? "(absent)"}`");
        }

        File.WriteAllText(Path.Combine(Directory, "summary.md"), summary.ToString());
    }

    /// <summary>One fact's comparison, as the report stores it.</summary>
    public sealed record FactComparison(
        string Fact, IReadOnlyDictionary<string, string> Oracle, IReadOnlyDictionary<string, string> Candidate, IReadOnlyList<Difference> Differences);
}
