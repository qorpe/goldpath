using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Goldpath.Messaging.Conformance;

/// <summary>One delivery as the handler saw it (messaging-exit §7.1 S3): which endpoint, which message, which attempt, which headers.</summary>
/// <param name="Sequence">The order the deliveries reached their handlers in, across the whole host.</param>
/// <param name="Endpoint">The handler's endpoint: its queue, named as the seam names it.</param>
/// <param name="Key">The message's business identity: a number the fact chose, or an id the fact generated.</param>
/// <param name="KeyIsStable">True when <paramref name="Key"/> is the same on every run (a number the fact chose), false for a generated id.</param>
/// <param name="MessageId">The transport's message id the context carried.</param>
/// <param name="RetryAttempt">The attempt the context reported.</param>
/// <param name="Tenant">The tenant the context restored.</param>
/// <param name="CorrelationId">The correlation the context carried.</param>
/// <param name="Headers">Every header the handler was handed, as delivered.</param>
public sealed record Delivery(
    long Sequence, string Endpoint, string Key, bool KeyIsStable, Guid? MessageId, int RetryAttempt, string? Tenant, string? CorrelationId,
    IReadOnlyDictionary<string, object?> Headers);

/// <summary>
/// What every conformance handler reports, so a fact's observable outcome can be compared across engines (messaging-exit
/// §7.1 S3) without the fact asserting it. One per <see cref="ConformanceContainers"/>, registered in every host the
/// harness builds; it records and never changes what a handler does.
/// </summary>
public sealed class DeliveryRecorder
{
    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private long _sequence;

    /// <summary>Records one delivery of a message whose business key the fact generated.</summary>
    public void Record(Type handler, Guid key, IntegrationEventContext context) => Record(handler, key.ToString("D"), false, context);

    /// <summary>Records one delivery of a message whose business key the fact chose.</summary>
    public void Record(Type handler, int key, IntegrationEventContext context) => Record(handler, key.ToString(CultureInfo.InvariantCulture), true, context);

    /// <summary>Everything recorded so far, in delivery order.</summary>
    public IReadOnlyList<Delivery> Snapshot() => [.. _deliveries.OrderBy(d => d.Sequence)];

    /// <summary>Forgets what was recorded: the differential starts each engine's run of a fact from nothing.</summary>
    public void Clear() => _deliveries.Clear();

    private void Record(Type handler, string key, bool stable, IntegrationEventContext context)
        => _deliveries.Enqueue(new Delivery(
            Interlocked.Increment(ref _sequence), GoldpathHandlerEndpoints.NameOf(handler), key, stable, context.MessageId, context.RetryAttempt,
            context.Tenant?.Value, context.CorrelationId, new Dictionary<string, object?>(context.Headers, StringComparer.Ordinal)));
}

/// <summary>One message parked in an error queue, read from the broker by its management API.</summary>
/// <param name="Queue">The error queue.</param>
/// <param name="HeaderKeys">The AMQP header names the message carries.</param>
/// <param name="MessageTypes">The message types its envelope names.</param>
public sealed record ParkedMessage(string Queue, IReadOnlyList<string> HeaderKeys, IReadOnlyList<string> MessageTypes);

/// <summary>The outbox and inbox tables at the end of a fact, by their own columns.</summary>
/// <param name="InboxRows">Rows in <c>InboxState</c>.</param>
/// <param name="InboxConsumed">Inbox rows whose handler ran (<c>Consumed</c> set).</param>
/// <param name="InboxDelivered">Inbox rows the engine finished with (<c>Delivered</c> set).</param>
/// <param name="InboxReceiveCounts">Each inbox row's <c>ReceiveCount</c>, sorted.</param>
/// <param name="OutboxMessages">Rows in <c>OutboxMessage</c>: published, not yet relayed or not yet removed.</param>
/// <param name="OutboxStates">Rows in <c>OutboxState</c>.</param>
/// <param name="OutboxStatesDelivered">Outbox state rows whose messages were relayed (<c>Delivered</c> set).</param>
public sealed record StoreState(
    int InboxRows, int InboxConsumed, int InboxDelivered, IReadOnlyList<int> InboxReceiveCounts, int OutboxMessages, int OutboxStates,
    int OutboxStatesDelivered);

/// <summary>
/// One engine's observable outcome of one fact, flattened to <c>path → value</c> so two engines' records compare line by
/// line and an allow-list entry can name the line it excuses (messaging-exit §7.1 S3).
/// </summary>
public static class OutcomeRecord
{
    /// <summary>
    /// Flattens what a fact left behind. Generated ids differ on every run, so a delivery is identified by how often its
    /// message arrived and which attempts it was given, never by the id itself; a delivery ORDER is recorded only where
    /// <paramref name="ordered"/> says the seam promises one (§7.1: one publisher, one endpoint, a concurrency limit of 1).
    /// </summary>
    public static SortedDictionary<string, string> Of(
        IReadOnlyList<Delivery> deliveries, IReadOnlyList<ParkedMessage> parked, StoreState store, bool ordered)
    {
        var record = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in deliveries.GroupBy(d => d.Endpoint))
        {
            var p = $"deliveries.{endpoint.Key}";
            var all = endpoint.ToList();
            record[$"{p}.count"] = Number(all.Count);
            record[$"{p}.distinct-keys"] = Number(all.Select(d => d.Key).Distinct().Count());
            Both(record, $"{p}.per-key", [.. all.GroupBy(d => d.Key).Select(g => Number(g.Count()))]);
            record[$"{p}.distinct-message-ids"] = Number(all.Select(d => d.MessageId).Distinct().Count());
            Both(record, $"{p}.per-message-id", [.. all.GroupBy(d => d.MessageId).Select(g => Number(g.Count()))]);
            record[$"{p}.message-id-missing"] = Number(all.Count(d => d.MessageId is null));
            Both(record, $"{p}.attempts", [.. all.GroupBy(d => d.Key).Select(g => "[" + string.Join(",", g.Select(d => d.RetryAttempt)) + "]")]);
            if (all.All(d => d.KeyIsStable))
            {
                record[$"{p}.keys"] = string.Join(",", all.Select(d => int.Parse(d.Key, CultureInfo.InvariantCulture)).Distinct().Order());
                if (ordered)
                {
                    record[$"{p}.order"] = string.Join(",", all.Select(d => d.Key));
                }
            }

            record[$"{p}.context.tenant"] = Values(all.Select(d => d.Tenant ?? "(none)"));
            record[$"{p}.context.correlation"] = Values(all.Select(d => d.CorrelationId ?? "(none)"));
            foreach (var key in all.SelectMany(d => d.Headers.Keys).Distinct(StringComparer.Ordinal))
            {
                var seen = all.Count(d => d.Headers.ContainsKey(key));
                record[$"{p}.headers.{key}"] = seen == all.Count ? "every delivery" : $"{Number(seen)} of {Number(all.Count)} deliveries";
            }

            foreach (var key in new[] { GoldpathHeaders.TenantId, GoldpathHeaders.CorrelationId })
            {
                record[$"{p}.header-value.{key}"] = Values(all.Select(d => d.Headers.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "(null)" : "(absent)"));
            }
        }

        foreach (var queue in parked.GroupBy(m => m.Queue))
        {
            var p = $"error-queue.{queue.Key}";
            record[$"{p}.count"] = Number(queue.Count());
            // One line per type an envelope names, counting the parked messages that name it: an entry can then excuse one
            // URN without excusing the event's own type, an empty list, "(no envelope)" or "(not json)" beside it.
            foreach (var type in queue.SelectMany(m => m.MessageTypes).Distinct(StringComparer.Ordinal))
            {
                record[$"{p}.message-type.{type}"] = Number(queue.Count(m => m.MessageTypes.Contains(type, StringComparer.Ordinal)));
            }

            record[$"{p}.message-type-missing"] = Number(queue.Count(m => m.MessageTypes.Count == 0));
            foreach (var key in queue.SelectMany(m => m.HeaderKeys).Distinct(StringComparer.Ordinal))
            {
                record[$"{p}.headers.{key}"] = Number(queue.Count(m => m.HeaderKeys.Contains(key)));
            }
        }

        record["store.inbox.rows"] = Number(store.InboxRows);
        record["store.inbox.consumed"] = Number(store.InboxConsumed);
        record["store.inbox.delivered"] = Number(store.InboxDelivered);
        Both(record, "store.inbox.receive-counts", [.. store.InboxReceiveCounts.Select(Number)]);
        record["store.outbox-message.rows"] = Number(store.OutboxMessages);
        record["store.outbox-state.rows"] = Number(store.OutboxStates);
        record["store.outbox-state.delivered"] = Number(store.OutboxStatesDelivered);
        return record;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A multiset as each value and how often it occurs, "{1×80}": readable at any size, and a fact whose size is the
    // engine's own (S2.8 grows its backlog to the engine's window) can be excused on the count without excusing the values.
    private static string Multiset(IEnumerable<string> values)
        => "{" + string.Join(" ", values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{Number(g.Count())}")) + "}";

    private static string Values(IEnumerable<string> values) => "{" + string.Join(" ", values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "}";

    // Both lines of a multiset: the values with their counts, and the values alone.
    private static void Both(SortedDictionary<string, string> record, string path, IReadOnlyList<string> values)
    {
        record[path] = Multiset(values);
        record[path + ".values"] = Values(values);
    }
}

/// <summary>An excused difference between the oracle's record and the candidate's: which fact, which line, and why the seam does not promise it.</summary>
/// <param name="Fact">The fact's method name, where <c>*</c> matches any run of characters (<c>*</c> alone: every fact).</param>
/// <param name="Path">The record line, where <c>*</c> matches any run of characters.</param>
/// <param name="Reason">Why the difference is not a divergence: what the seam does and does not promise, with its source.</param>
public sealed record AllowedDifference(string Fact, string Path, string Reason)
{
    /// <summary>Whether this entry excuses <paramref name="path"/> of <paramref name="fact"/>.</summary>
    public bool Matches(string fact, string path) => Glob(Fact).IsMatch(fact) && Glob(Path).IsMatch(path);

    private static Regex Glob(string pattern) => new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
}

/// <summary>One line on which two records disagree.</summary>
/// <param name="Path">The record line.</param>
/// <param name="Oracle">The oracle's value, or null when the oracle's record has no such line.</param>
/// <param name="Candidate">The candidate's value, or null when the candidate's record has no such line.</param>
/// <param name="AllowedBy">The allow-list entry that excuses it, or null when nothing does — a divergence.</param>
public sealed record Difference(string Path, string? Oracle, string? Candidate, AllowedDifference? AllowedBy);

/// <summary>The comparator (messaging-exit §7.1 S3): the candidate's record must equal the oracle's, line for line, but for what the allow-list excuses.</summary>
public static class OutcomeComparator
{
    /// <summary>Every line the two records disagree on, each with the entry that excuses it or none.</summary>
    public static IReadOnlyList<Difference> Compare(
        string fact, IReadOnlyDictionary<string, string> oracle, IReadOnlyDictionary<string, string> candidate, IReadOnlyList<AllowedDifference> allowed)
    {
        var differences = new List<Difference>();
        foreach (var path in oracle.Keys.Union(candidate.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var left = oracle.TryGetValue(path, out var l) ? l : null;
            var right = candidate.TryGetValue(path, out var r) ? r : null;
            if (left != right)
            {
                differences.Add(new Difference(path, left, right, allowed.FirstOrDefault(entry => entry.Matches(fact, path))));
            }
        }

        return differences;
    }

    /// <summary>The reviewed allow-list, read from <c>differential-allowlist.json</c> beside the suite.</summary>
    public static IReadOnlyList<AllowedDifference> LoadAllowList(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<AllowedDifference>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"'{path}' holds no allow-list.");
    }
}

/// <summary>Reads, and resets, what a fact leaves on the broker and in the database.</summary>
public static class OutcomeProbes
{
    private static readonly string[] StoreTables = ["InboxState", "OutboxMessage", "OutboxState"];

    /// <summary>The messages every <c>*_error</c> queue holds, read without consuming them.</summary>
    public static async Task<IReadOnlyList<ParkedMessage>> ParkedAsync(ConformanceContainers containers)
    {
        using var http = containers.ManagementClient();
        var parked = new List<ParkedMessage>();
        foreach (var queue in await ErrorQueuesAsync(http))
        {
            using var request = new StringContent("""{"count":1000,"ackmode":"ack_requeue_true","encoding":"auto","truncate":65536}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}/get", request);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var message in document.RootElement.EnumerateArray())
            {
                var headers = message.GetProperty("properties").TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
                    ? [.. h.EnumerateObject().Select(o => o.Name).Order(StringComparer.Ordinal)]
                    : new List<string>();
                parked.Add(new ParkedMessage(queue, headers, MessageTypesOf(message.GetProperty("payload").GetString())));
            }
        }

        return parked;
    }

    /// <summary>Empties every <c>*_error</c> queue, so a fact's error-queue contents are its own.</summary>
    public static async Task PurgeErrorQueuesAsync(ConformanceContainers containers)
    {
        using var http = containers.ManagementClient();
        var queues = await ErrorQueuesAsync(http);
        foreach (var queue in queues)
        {
            using var response = await http.DeleteAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}/contents");
            response.EnsureSuccessStatusCode();
        }

        // The management API's message count is a statistic the broker refreshes every few seconds. A fact that reads
        // the depth before and after (S2.18) must not read the count from before the purge, so wait until it is zero.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        foreach (var queue in queues)
        {
            while (await containers.QueueDepthAsync(queue) != 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"'{queue}' still reports messages 30 s after it was purged.");
                }

                await Task.Delay(250);
            }
        }
    }

    /// <summary>The outbox and inbox tables as a fact left them; all zero before the first host created them.</summary>
    public static async Task<StoreState> StoreAsync(ConformanceContainers containers)
    {
        await using var connection = new NpgsqlConnection(containers.Postgres.GetConnectionString());
        await connection.OpenAsync();
        if (!await TablesExistAsync(connection))
        {
            return new StoreState(0, 0, 0, [], 0, 0, 0);
        }

        await using var inbox = new NpgsqlCommand(
            """SELECT count(*)::int, count("Consumed")::int, count("Delivered")::int, coalesce(array_agg("ReceiveCount" ORDER BY "ReceiveCount"), '{}') FROM "InboxState" """, connection);
        await using var reader = await inbox.ExecuteReaderAsync();
        await reader.ReadAsync();
        var (rows, consumed, delivered, receives) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), (int[])reader.GetValue(3));
        await reader.CloseAsync();
        await using var outbox = new NpgsqlCommand(
            """SELECT (SELECT count(*)::int FROM "OutboxMessage"), (SELECT count(*)::int FROM "OutboxState"), (SELECT count("Delivered")::int FROM "OutboxState")""", connection);
        await using var outboxReader = await outbox.ExecuteReaderAsync();
        await outboxReader.ReadAsync();
        return new StoreState(rows, consumed, delivered, receives, outboxReader.GetInt32(0), outboxReader.GetInt32(1), outboxReader.GetInt32(2));
    }

    /// <summary>Empties the outbox and inbox tables, so a fact's rows are its own.</summary>
    public static async Task TruncateStoreAsync(ConformanceContainers containers)
    {
        await using var connection = new NpgsqlConnection(containers.Postgres.GetConnectionString());
        await connection.OpenAsync();
        if (await TablesExistAsync(connection))
        {
            await using var truncate = new NpgsqlCommand("""TRUNCATE "InboxState", "OutboxMessage", "OutboxState" """, connection);
            await truncate.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> TablesExistAsync(NpgsqlConnection connection)
    {
        await using var exists = new NpgsqlCommand(
            "SELECT count(*)::int FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = ANY(@names)", connection);
        exists.Parameters.AddWithValue("names", StoreTables);
        return (int)(await exists.ExecuteScalarAsync())! == StoreTables.Length;
    }

    private static async Task<IReadOnlyList<string>> ErrorQueuesAsync(HttpClient http)
    {
        using var response = await http.GetAsync("api/queues/%2F?columns=name");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.EnumerateArray().Select(q => q.GetProperty("name").GetString()!).Where(n => n.EndsWith("_error", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }

    // The 8.x envelope names its message types; a body that is not an envelope names none, and the record says so.
    private static List<string> MessageTypesOf(string? payload)
    {
        try
        {
            using var body = JsonDocument.Parse(payload ?? "");
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("messageType", out var types) && types.ValueKind == JsonValueKind.Array
                ? [.. types.EnumerateArray().Select(t => t.GetString() ?? "(null)")]
                : ["(no envelope)"];
        }
        catch (JsonException)
        {
            return ["(not json)"];
        }
    }
}
