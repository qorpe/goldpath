using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.FileExchange.Tests;

/// <summary>
/// The planted-fault rig (fileexchange RFC §7): a registry-style CSV rail with a header,
/// a trailer count, and rows "reference;amount". Every planted fault — bad row, duplicate
/// file, truncated file, replay — must be caught, quarantined, or deduplicated exactly as
/// declared, and the batch must never stop for a row.
/// </summary>
public class FileRailEngineTests
{
    private sealed record RegistryRow(string Reference, decimal Amount);

    private static GoldpathFileExchangeOptions Options() => new GoldpathFileExchangeOptions()
        .AddRail<RegistryRow>("registry-daily", rail => rail
            .Header(1)
            .ValidateFile(lines =>
            {
                // Trailer contract: the header's declared row count must match reality.
                if (lines.Count == 0 || !lines[0].StartsWith("H;", StringComparison.Ordinal))
                {
                    return "missing header";
                }

                var declared = int.Parse(lines[0].Split(';')[1]);
                return declared == lines.Count - 1 ? null : $"truncated: header declares {declared} rows, file has {lines.Count - 1}";
            })
            .ParseLine(line =>
            {
                var parts = line.Split(';');
                return new RegistryRow(parts[0], decimal.Parse(parts[1]));
            })
            .ValidateRow(row => row.Amount > 0 ? null : $"non-positive amount for {row.Reference}")
            .Handle((row, _) =>
            {
                Applied.Add(row.Reference);
                return Task.CompletedTask;
            }));

    private static readonly List<string> Applied = [];

    private static (GoldpathFileRailEngine Engine, GoldpathInMemoryFileLedger Ledger, RecordingPublisher Events) Build()
    {
        Applied.Clear();
        var ledger = new GoldpathInMemoryFileLedger();
        var events = new RecordingPublisher();
        var engine = new GoldpathFileRailEngine(Options(), ledger, NullLogger<GoldpathFileRailEngine>.Instance, events);
        return (engine, ledger, events);
    }

    private static readonly string[] CleanFile = ["H;3", "A-1;100.50", "A-2;200.00", "A-3;50.25"];

    [Fact]
    public async Task Clean_file_ingests_every_row_once()
    {
        var (engine, _, events) = Build();
        var result = await engine.ProcessAsync("registry-daily", "reg-0817.csv", CleanFile);

        Assert.Null(result.FileRejectedReason);
        Assert.Equal(3, result.Processed);
        Assert.Empty(result.Quarantined);
        Assert.Equal(["A-1", "A-2", "A-3"], Applied);
        Assert.Contains(events.Published, e => e is GoldpathFileIngested { Processed: 3, Quarantined: 0 });
    }

    [Fact]
    public async Task Bad_rows_quarantine_and_the_batch_continues()
    {
        var (engine, ledger, events) = Build();
        string[] file = ["H;4", "A-1;100.50", "garbage-line", "A-3;-5", "A-4;70.00"];
        var result = await engine.ProcessAsync("registry-daily", "reg-0818.csv", file);

        Assert.Equal(2, result.Processed);                       // the batch did NOT stop
        Assert.Equal([3, 4], result.Quarantined.Select(q => q.Line));
        Assert.StartsWith("parse:", result.Quarantined[0].Reason);
        Assert.Equal("non-positive amount for A-3", result.Quarantined[1].Reason);
        Assert.Equal(["A-1", "A-4"], Applied);

        var quarantine = await ledger.GetQuarantineAsync("registry-daily", "reg-0818.csv");
        Assert.Equal(2, quarantine.Count);
        Assert.Contains(events.Published, e => e is GoldpathRowsQuarantined { Count: 2 });
    }

    [Fact]
    public async Task Replaying_the_same_file_applies_zero_duplicates()
    {
        var (engine, _, _) = Build();
        await engine.ProcessAsync("registry-daily", "reg-0817.csv", CleanFile);
        var replay = await engine.ProcessAsync("registry-daily", "reg-0817.csv", CleanFile);

        Assert.Equal(0, replay.Processed);
        Assert.Equal(3, replay.SkippedAsDuplicate);
        Assert.Equal(["A-1", "A-2", "A-3"], Applied);   // still exactly once
    }

    [Fact]
    public async Task Truncated_file_is_rejected_whole_and_ingests_nothing()
    {
        var (engine, _, events) = Build();
        string[] truncated = ["H;3", "A-1;100.50", "A-2;200.00"];   // header says 3, file has 2
        var result = await engine.ProcessAsync("registry-daily", "reg-0819.csv", truncated);

        Assert.NotNull(result.FileRejectedReason);
        Assert.Equal(0, result.Processed);
        Assert.Empty(Applied);
        Assert.Contains(events.Published, e => e is GoldpathFileRejected);
        Assert.DoesNotContain(events.Published, e => e is GoldpathFileIngested);
    }

    [Fact]
    public async Task Reprocess_after_fix_retries_only_the_quarantined_row()
    {
        var (engine, ledger, _) = Build();
        string[] broken = ["H;2", "A-1;100.50", "A-2;-1"];
        await engine.ProcessAsync("registry-daily", "reg-0820.csv", broken);
        Assert.Equal(["A-1"], Applied);

        // The counterparty resends the file with the row fixed. The good row dedups,
        // the fixed row applies, and its quarantine record clears.
        string[] fixedFile = ["H;2", "A-1;100.50", "A-2;75.00"];
        var result = await engine.ProcessAsync("registry-daily", "reg-0820.csv", fixedFile);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.SkippedAsDuplicate);
        Assert.Equal(["A-1", "A-2"], Applied);
        Assert.Empty(await ledger.GetQuarantineAsync("registry-daily", "reg-0820.csv"));
    }

    [Fact]
    public async Task An_undeclared_rail_is_refused()
    {
        var (engine, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ProcessAsync("no-such-rail", "x.csv", CleanFile));
    }

    [Fact]
    public void A_rail_without_parse_or_handle_is_rejected_at_declaration()
    {
        Assert.Throws<InvalidOperationException>(() => new GoldpathFileExchangeOptions()
            .AddRail<RegistryRow>("broken", rail => rail.Header(1)));
    }

    private sealed class RecordingPublisher : IIntegrationEventPublisher
    {
        public List<IIntegrationEvent> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Published.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
    /// <summary>
    /// #482 (threat model T-FXI-04): a DIFFERENT file delivered under a name already processed is refused whole, in words,
    /// rather than having its lines skipped as "duplicates". Nothing of it applies, nothing quarantines, no ingest is
    /// announced, and the first file's rows are untouched — so the file can be delivered again under a new name.
    /// </summary>
    [Fact]
    public async Task A_different_file_under_a_processed_name_is_rejected_whole_in_words()
    {
        var (engine, ledger, events) = Build();
        await engine.ProcessAsync("registry-daily", "daily.csv", CleanFile);
        events.Published.Clear();

        string[] nextDay = ["H;3", "A-1;100.50", "B-2;999.00", "B-3;12.00"];   // same name, line 3 onwards is a new day
        var result = await engine.ProcessAsync("registry-daily", "daily.csv", nextDay);

        Assert.Equal("Line 3 differs from the line already processed under this file name: this is a different file delivered "
            + "under a name the rail has already ingested, so nothing of it was applied. Deliver it under a new name.", result.FileRejectedReason);
        Assert.Equal((0, 0), (result.Processed, result.SkippedAsDuplicate));
        Assert.Empty(result.Quarantined);
        Assert.Equal(["A-1", "A-2", "A-3"], Applied);   // the first day only, once
        Assert.Equal(result.FileRejectedReason, Assert.IsType<GoldpathFileRejected>(Assert.Single(events.Published)).Reason);
        Assert.Empty(await ledger.GetQuarantineAsync("registry-daily", "daily.csv"));

        var renamed = await engine.ProcessAsync("registry-daily", "daily-2.csv", nextDay);
        Assert.Equal(3, renamed.Processed);
    }

    /// <summary>
    /// The class is "any byte that differs", so the rule is the content's hash, not a comparison of what looks the same:
    /// the first line, the last line, a trailing space, a carriage return, a change of case, another Unicode
    /// normalisation of the same text, a zero-width character and a byte-order mark each refuse the file (an ill-formed
    /// character: the next fact).
    /// </summary>
    [Theory]
    [InlineData(1, "A-1;100.50", "A-1;100.51")]
    [InlineData(3, "A-3;50.25", "A-3;50.26")]
    [InlineData(2, "A-2;200.00", "A-2;200.00 ")]
    [InlineData(2, "A-2;200.00", "A-2;200.00\r")]
    [InlineData(2, "A-2;200.00", "a-2;200.00")]
    [InlineData(2, "Caf\u00E9-2;200.00", "Cafe\u0301-2;200.00")]
    [InlineData(2, "A-2;200.00", "A\u200B-2;200.00")]
    [InlineData(1, "A-1;100.50", "\uFEFFA-1;100.50")]
    public async Task Every_byte_difference_in_a_processed_line_refuses_the_file(int dataLine, string original, string changed)
    {
        var (engine, _, _) = Build();
        var first = (string[])CleanFile.Clone();
        first[dataLine] = original;
        Assert.Equal(3, (await engine.ProcessAsync("registry-daily", "daily.csv", first)).Processed);

        var second = (string[])first.Clone();
        second[dataLine] = changed;
        var result = await engine.ProcessAsync("registry-daily", "daily.csv", second);

        Assert.StartsWith($"Line {dataLine + 1} differs", result.FileRejectedReason, StringComparison.Ordinal);
        Assert.Equal(0, result.Processed);
    }

    /// <summary>
    /// The same class through an ill-formed line: two lines that differ only in a lone surrogate, which a lenient UTF-8
    /// encoder turns into the same U+FFFD, and a lone surrogate against U+FFFD itself. Built in code, not as theory
    /// data, because the test runner's serialization of theory data would turn both lone surrogates into U+FFFD first.
    /// </summary>
    [Fact]
    public async Task Lines_that_differ_only_in_an_ill_formed_character_refuse_the_file()
    {
        foreach (var (original, changed) in new[] { ("A" + '\uD800' + "-2;200.00", "A" + '\uDBFF' + "-2;200.00"), ("A\uFFFD-2;200.00", "A" + '\uD800' + "-2;200.00") })
        {
            var (engine, _, _) = Build();
            string[] first = ["H;3", "A-1;100.50", original, "A-3;50.25"];
            Assert.Equal(3, (await engine.ProcessAsync("registry-daily", "daily.csv", first)).Processed);

            var result = await engine.ProcessAsync("registry-daily", "daily.csv", ["H;3", "A-1;100.50", changed, "A-3;50.25"]);

            Assert.StartsWith("Line 3 differs", result.FileRejectedReason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What still goes through, as the decision on #482 says: the same bytes dedup (the replay above), a quarantined line
    /// that was fixed applies because it was never processed, and lines appended after the processed ones apply.
    /// </summary>
    [Fact]
    public async Task A_fixed_quarantined_line_and_new_trailing_lines_still_apply_under_the_same_name()
    {
        var (engine, _, _) = Build();
        await engine.ProcessAsync("registry-daily", "daily.csv", ["H;2", "A-1;100.50", "A-2;-1"]);   // line 3 quarantines

        var fixedAndLonger = await engine.ProcessAsync("registry-daily", "daily.csv", ["H;3", "A-1;100.50", "A-2;20.00", "A-3;7.00"]);

        Assert.Null(fixedAndLonger.FileRejectedReason);
        Assert.Equal((2, 1), (fixedAndLonger.Processed, fixedAndLonger.SkippedAsDuplicate));
        Assert.Equal(["A-1", "A-2", "A-3"], Applied);
    }

    /// <summary>
    /// A row marked without a hash — before the upgrade, or by a ledger that keeps none — counts as matching, so an
    /// upgrade does not refuse every redelivery; the guarantee starts with rows marked after it (the upgrade guide says so).
    /// </summary>
    [Fact]
    public async Task A_row_marked_without_a_hash_counts_as_matching()
    {
        var (engine, ledger, _) = Build();
        await ledger.MarkProcessedAsync("registry-daily", "daily.csv", 2);   // the pre-#482 overload: no hash
        Assert.Equal("", await ledger.GetProcessedHashAsync("registry-daily", "daily.csv", 2));
        Assert.Null(await ledger.GetProcessedHashAsync("registry-daily", "daily.csv", 3));

        var result = await engine.ProcessAsync("registry-daily", "daily.csv", ["H;2", "Z-9;1.00", "A-2;2.00"]);

        Assert.Null(result.FileRejectedReason);
        Assert.Equal((1, 1), (result.Processed, result.SkippedAsDuplicate));
        // The hash the row is marked with is SHA-256 of the line's UTF-8 bytes, lower-case hex — computed here independently.
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("A-2;2.00"))).ToLowerInvariant();
        Assert.Equal(expected, await ledger.GetProcessedHashAsync("registry-daily", "daily.csv", 3));
    }

    /// <summary>
    /// A custom ledger written against the shipped interface — the six original members, none of #482's — still compiles
    /// and still dedups: the default members answer "processed, hash unknown", which counts as matching.
    /// </summary>
    [Fact]
    public async Task A_ledger_that_implements_only_the_shipped_members_keeps_todays_behaviour()
    {
        Applied.Clear();
        var engine = new GoldpathFileRailEngine(Options(), new ShippedMembersOnly(), NullLogger<GoldpathFileRailEngine>.Instance);
        await engine.ProcessAsync("registry-daily", "daily.csv", CleanFile);
        var replay = await engine.ProcessAsync("registry-daily", "daily.csv", CleanFile);

        Assert.Equal((0, 3), (replay.Processed, replay.SkippedAsDuplicate));
        Assert.Equal(["A-1", "A-2", "A-3"], Applied);
    }

    private sealed class ShippedMembersOnly : IGoldpathFileLedger
    {
        private readonly GoldpathInMemoryFileLedger _inner = new();

        public Task<bool> IsProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default) => _inner.IsProcessedAsync(rail, file, line, cancellationToken);

        public Task MarkProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default) => _inner.MarkProcessedAsync(rail, file, line, cancellationToken);

        public Task QuarantineAsync(string rail, string file, int line, string reason, CancellationToken cancellationToken = default) => _inner.QuarantineAsync(rail, file, line, reason, cancellationToken);

        public Task ReleaseQuarantineAsync(string rail, string file, int line, CancellationToken cancellationToken = default) => _inner.ReleaseQuarantineAsync(rail, file, line, cancellationToken);

        public Task<IReadOnlyList<GoldpathQuarantinedRow>> GetQuarantineAsync(string rail, string file, CancellationToken cancellationToken = default) => _inner.GetQuarantineAsync(rail, file, cancellationToken);

        public Task MarkArchivedAsync(string rail, string file, CancellationToken cancellationToken = default) => _inner.MarkArchivedAsync(rail, file, cancellationToken);
    }
}
