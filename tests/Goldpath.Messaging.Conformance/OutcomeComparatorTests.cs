using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The S3 comparator and record, without a broker (messaging-exit §7.1 S3): what counts as a divergence, what the
/// allow-list may excuse, and that the committed allow-list is reviewable — every entry narrow and carrying its reason.
/// </summary>
public sealed class OutcomeComparatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoHeaders = new Dictionary<string, object?>();

    private static IntegrationEventContext Context(Guid messageId, int attempt, string? correlation = "corr")
        => new(messageId, correlation, null, attempt, new Dictionary<string, object?> { [GoldpathHeaders.CorrelationId] = correlation });

    private static Delivery Delivered(long sequence, string key, Guid messageId, int attempt, bool stable = false, IReadOnlyDictionary<string, object?>? headers = null)
        => new(sequence, "poison", key, stable, messageId, attempt, null, "corr", headers ?? new Dictionary<string, object?> { [GoldpathHeaders.CorrelationId] = "corr" });

    private static StoreState Empty => new(0, 0, 0, [], 0, 0, 0);

    private static SortedDictionary<string, string> Ladder(params int[] attempts)
    {
        var message = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        return OutcomeRecord.Of([.. attempts.Select((a, i) => Delivered(i, key, message, a))], [], Empty, ordered: false);
    }

    [Fact]
    public void Two_runs_of_the_same_outcome_with_different_generated_ids_compare_equal()
    {
        Assert.Empty(OutcomeComparator.Compare("S2_3", Ladder(0, 1, 2), Ladder(0, 1, 2), []));
    }

    [Fact]
    public void One_extra_retry_on_the_candidate_is_a_divergence()
    {
        var differences = OutcomeComparator.Compare("S2_3", Ladder(0, 1, 2), Ladder(0, 1, 2, 3), []);

        Assert.Contains(differences, d => d.Path == "deliveries.poison.attempts" && d.Oracle == "{[0,1,2]×1}" && d.Candidate == "{[0,1,2,3]×1}" && d.AllowedBy is null);
        Assert.Contains(differences, d => d.Path == "deliveries.poison.count" && d.Oracle == "3" && d.Candidate == "4");
    }

    [Fact]
    public void A_line_only_one_engine_has_is_a_difference_with_the_other_side_absent()
    {
        var oracle = new SortedDictionary<string, string> { ["a"] = "1" };
        var candidate = new SortedDictionary<string, string> { ["b"] = "1" };

        var differences = OutcomeComparator.Compare("f", oracle, candidate, []);

        Assert.Equal([("a", "1", (string?)null), ("b", null, "1")], differences.Select(d => (d.Path, d.Oracle, d.Candidate)));
    }

    [Fact]
    public void An_entry_excuses_only_the_lines_and_the_facts_it_names()
    {
        var oracle = new SortedDictionary<string, string> { ["deliveries.ping.headers.traceparent"] = "x", ["deliveries.ping.count"] = "1" };
        var candidate = new SortedDictionary<string, string> { ["deliveries.ping.count"] = "2" };
        AllowedDifference[] allowed = [new("S2_2_*", "deliveries.*.headers.traceparent", "why")];

        var onIts = OutcomeComparator.Compare("S2_2_tenant", oracle, candidate, allowed);
        var onAnother = OutcomeComparator.Compare("S2_20_other", oracle, candidate, allowed);

        Assert.Same(allowed[0], onIts.Single(d => d.Path == "deliveries.ping.headers.traceparent").AllowedBy);
        Assert.Null(onIts.Single(d => d.Path == "deliveries.ping.count").AllowedBy);
        Assert.All(onAnother, d => Assert.Null(d.AllowedBy));
    }

    [Fact]
    public void A_glob_matches_a_whole_line_and_treats_every_other_character_literally()
    {
        var entry = new AllowedDifference("*", "store.inbox.receive-counts*", "why");

        Assert.True(entry.Matches("any", "store.inbox.receive-counts"));
        Assert.True(entry.Matches("any", "store.inbox.receive-counts.values"));
        Assert.False(entry.Matches("any", "xstore.inbox.receive-counts"));
        Assert.False(new AllowedDifference("*", "a.b", "why").Matches("any", "aXb"));   // the dot is a dot, not any character
    }

    [Fact]
    public void An_order_is_recorded_only_where_the_seam_promises_one_and_only_for_keys_the_fact_chose()
    {
        Delivery[] reversed = [Delivered(1, "2", Guid.NewGuid(), 0, stable: true), Delivered(2, "1", Guid.NewGuid(), 0, stable: true)];
        Delivery[] generated = [Delivered(1, Guid.NewGuid().ToString(), Guid.NewGuid(), 0)];

        Assert.Equal("2,1", OutcomeRecord.Of(reversed, [], Empty, ordered: true)["deliveries.poison.order"]);
        Assert.Equal("1,2", OutcomeRecord.Of(reversed, [], Empty, ordered: true)["deliveries.poison.keys"]);
        Assert.DoesNotContain("deliveries.poison.order", OutcomeRecord.Of(reversed, [], Empty, ordered: false).Keys);
        Assert.DoesNotContain(OutcomeRecord.Of(generated, [], Empty, ordered: true).Keys, k => k.EndsWith(".order", StringComparison.Ordinal) || k.EndsWith(".keys", StringComparison.Ordinal));
    }

    [Fact]
    public void A_header_seen_on_some_deliveries_only_says_how_many()
    {
        var with = new Dictionary<string, object?> { ["x"] = "1" };
        Delivery[] deliveries = [Delivered(1, "a", Guid.NewGuid(), 0, headers: with), Delivered(2, "b", Guid.NewGuid(), 0, headers: NoHeaders)];

        var record = OutcomeRecord.Of(deliveries, [], Empty, ordered: false);

        Assert.Equal("1 of 2 deliveries", record["deliveries.poison.headers.x"]);
        Assert.Equal("{(absent)}", record[$"deliveries.poison.header-value.{GoldpathHeaders.TenantId}"]);
    }

    [Fact]
    public void A_multiset_keeps_its_values_on_a_line_of_their_own_so_a_size_can_be_excused_without_them()
    {
        var key = Guid.NewGuid().ToString();
        var message = Guid.NewGuid();
        var record = OutcomeRecord.Of([Delivered(1, key, message, 0), Delivered(2, Guid.NewGuid().ToString(), Guid.NewGuid(), 0)], [], Empty, ordered: false);

        Assert.Equal("{1×2}", record["deliveries.poison.per-key"]);
        Assert.Equal("{1}", record["deliveries.poison.per-key.values"]);
        Assert.Equal("{[0]}", record["deliveries.poison.attempts.values"]);
    }

    private const string IntegrationEventUrn = "urn:message:Goldpath:IIntegrationEvent";
    private const string PoisonUrn = "urn:message:Goldpath.Messaging.Conformance:Poison";

    private static SortedDictionary<string, string> Parked(params string[][] messageTypes)
        => OutcomeRecord.Of([], [.. messageTypes.Select(t => new ParkedMessage("poison_error", [], t))], Empty, ordered: false);

    [Fact]
    public void A_parked_envelope_records_each_type_it_names_on_a_line_of_its_own()
    {
        var record = Parked([PoisonUrn, IntegrationEventUrn], [PoisonUrn], []);

        Assert.Equal("3", record["error-queue.poison_error.count"]);
        Assert.Equal("2", record[$"error-queue.poison_error.message-type.{PoisonUrn}"]);
        Assert.Equal("1", record[$"error-queue.poison_error.message-type.{IntegrationEventUrn}"]);
        Assert.Equal("1", record["error-queue.poison_error.message-type-missing"]);
    }

    [Fact]
    public void The_committed_allow_list_excuses_the_missing_interface_urn_and_nothing_else_about_a_parked_envelope()
    {
        var allowed = OutcomeComparator.LoadAllowList(Path.Combine(AppContext.BaseDirectory, "differential-allowlist.json"));
        var oracle = Parked([PoisonUrn, IntegrationEventUrn]);

        // What the reason describes: the candidate lists the event's own type only.
        Assert.All(OutcomeComparator.Compare("S2_3_retry", oracle, Parked([PoisonUrn]), allowed), d => Assert.NotNull(d.AllowedBy));

        // What it must not excuse: the wrong own type, an empty list, an unreadable envelope.
        foreach (var candidate in new[] { Parked(["urn:message:Goldpath.Messaging.Conformance:Ping"]), Parked([]), Parked(["(no envelope)"]), Parked(["(not json)"]) })
        {
            Assert.Contains(OutcomeComparator.Compare("S2_3_retry", oracle, candidate, allowed), d => d.Path.StartsWith("error-queue.poison_error.message-type", StringComparison.Ordinal) && d.AllowedBy is null);
        }
    }

    [Fact]
    public void The_recorder_keeps_delivery_order_and_forgets_on_clear()
    {
        var recorder = new DeliveryRecorder();
        recorder.Record(typeof(PoisonHandler), 7, Context(Guid.NewGuid(), 0));
        recorder.Record(typeof(PingHandler), Guid.Empty, Context(Guid.NewGuid(), 1));

        var snapshot = recorder.Snapshot();
        Assert.Equal(["poison", "ping"], snapshot.Select(d => d.Endpoint));
        Assert.Equal([true, false], snapshot.Select(d => d.KeyIsStable));
        Assert.Equal(1, snapshot[1].RetryAttempt);
        recorder.Clear();
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public void The_committed_allow_list_is_narrow_and_every_entry_says_why()
    {
        var allowed = OutcomeComparator.LoadAllowList(Path.Combine(AppContext.BaseDirectory, "differential-allowlist.json"));

        Assert.NotEmpty(allowed);
        Assert.All(allowed, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Fact));
            Assert.False(string.IsNullOrWhiteSpace(entry.Path));
            Assert.True(entry.Reason.Trim().Length >= 40, $"'{entry.Path}' carries no reason worth reviewing");
            // No entry may excuse a whole record or a whole section: each names the lines it excuses.
            Assert.DoesNotContain(entry.Path, new[] { "*", "deliveries.*", "error-queue.*", "store.*" });
            var itsFact = entry.Fact.Replace("*", "x", StringComparison.Ordinal);
            foreach (var guarded in new[] { "attempts.values", "per-message-id.values", "per-key.values", "context.tenant", "context.correlation", "order" })
            {
                Assert.False(entry.Matches(itsFact, $"deliveries.ping.{guarded}"), $"'{entry.Path}' would excuse deliveries.*.{guarded}");
            }

            // A parked envelope's own type is never excused, on any queue an entry does not name outright.
            foreach (var guarded in new[] { $"message-type.{PoisonUrn}", "message-type.(no envelope)", "message-type.(not json)", "message-type-missing" })
            {
                Assert.False(entry.Matches(itsFact, $"error-queue.poison_error.{guarded}"), $"'{entry.Path}' would excuse error-queue.*.{guarded}");
            }
        });
        Assert.Equal(allowed.Count, allowed.Select(e => (e.Fact, e.Path)).Distinct().Count());
    }
}
