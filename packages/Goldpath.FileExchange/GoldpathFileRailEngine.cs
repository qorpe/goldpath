using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>Where rail progress lives: processed row keys, quarantine records, archive
/// marks. The in-memory ledger ships for tests and single-node hosts; a database-backed
/// ledger composes through this seam.</summary>
public interface IGoldpathFileLedger
{
    /// <summary>Whether a row already applied — the (rail, file, line) idempotency key.</summary>
    Task<bool> IsProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default);

    /// <summary>Marks a row applied.</summary>
    Task MarkProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default);

    /// <summary>Records a quarantined row with its reason (upserts on reprocess).</summary>
    Task QuarantineAsync(string rail, string file, int line, string reason, CancellationToken cancellationToken = default);

    /// <summary>Clears a row's quarantine record (it applied on reprocess).</summary>
    Task ReleaseQuarantineAsync(string rail, string file, int line, CancellationToken cancellationToken = default);

    /// <summary>The current quarantine list for a file.</summary>
    Task<IReadOnlyList<GoldpathQuarantinedRow>> GetQuarantineAsync(string rail, string file, CancellationToken cancellationToken = default);

    /// <summary>Marks the file archived (retention is the Archival module's business).</summary>
    Task MarkArchivedAsync(string rail, string file, CancellationToken cancellationToken = default);

    /// <summary>
    /// The content hash a processed row was marked with (#482): <c>null</c> when the row is not processed, and <c>""</c>
    /// when it is processed but its hash is unknown — a row marked before the upgrade, or by a ledger that keeps no hash,
    /// which the engine counts as matching. The default answers from <see cref="IsProcessedAsync"/>, so a ledger that
    /// keeps no hash still compiles and keeps today's behaviour.
    /// </summary>
    async Task<string?> GetProcessedHashAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
        => await IsProcessedAsync(rail, file, line, cancellationToken).ConfigureAwait(false) ? "" : null;

    /// <summary>
    /// Marks a row applied with the SHA-256 hex of its content (#482), so a different file delivered later under the same
    /// name is refused rather than skipped as a duplicate. The default drops the hash and marks the row as
    /// <see cref="MarkProcessedAsync(string, string, int, CancellationToken)"/> does. Named apart from that member rather than
    /// overloading it: the public-API analyzers refuse a second overload with an optional token (RS0026/RS0027).
    /// </summary>
    Task MarkProcessedWithHashAsync(string rail, string file, int line, string contentHash, CancellationToken cancellationToken = default)
        => MarkProcessedAsync(rail, file, line, cancellationToken);
}

/// <summary>One quarantined row: where and why.</summary>
public sealed record GoldpathQuarantinedRow(string Rail, string File, int Line, string Reason);

/// <summary>The outcome of one rail run over one file.</summary>
public sealed record GoldpathFileRailResult(
    string Rail,
    string File,
    string? FileRejectedReason,
    int Processed,
    int SkippedAsDuplicate,
    IReadOnlyList<GoldpathQuarantinedRow> Quarantined);

/// <summary>A file arrived on a rail.</summary>
public sealed record GoldpathFileReceived(string Rail, string File, int DataRows) : IIntegrationEvent;

/// <summary>A rail run finished ingesting a file.</summary>
public sealed record GoldpathFileIngested(string Rail, string File, int Processed, int SkippedAsDuplicate, int Quarantined) : IIntegrationEvent;

/// <summary>A rail run quarantined rows — the batch CONTINUED (that is the point).</summary>
public sealed record GoldpathRowsQuarantined(string Rail, string File, int Count) : IIntegrationEvent;

/// <summary>A file failed its file-level contract and ingested NOTHING.</summary>
public sealed record GoldpathFileRejected(string Rail, string File, string Reason) : IIntegrationEvent;

/// <summary>
/// The rail engine: validate the file, then per row — dedup on (rail, file, line),
/// parse/validate/handle, quarantine on failure WITHOUT stopping the batch. Re-running
/// the same file (redelivery or reprocess) applies zero duplicates and retries only what
/// quarantined. A DIFFERENT file under a name already processed — a line whose content no
/// longer matches the line processed under that number — is refused whole, never skipped
/// (#482). Scheduled pick-up rides the Jobs module; this engine owns one run.
/// </summary>
public sealed class GoldpathFileRailEngine
{
    private readonly GoldpathFileExchangeOptions _options;
    private readonly IGoldpathFileLedger _ledger;
    private readonly ILogger<GoldpathFileRailEngine> _logger;
    private readonly IIntegrationEventPublisher? _publisher;

    /// <summary>Creates the engine (the publisher is optional — no broker, no events).</summary>
    public GoldpathFileRailEngine(
        GoldpathFileExchangeOptions options,
        IGoldpathFileLedger ledger,
        ILogger<GoldpathFileRailEngine> logger,
        IIntegrationEventPublisher? publisher = null)
    {
        _options = options;
        _ledger = ledger;
        _logger = logger;
        _publisher = publisher;
    }

    /// <summary>Runs one file through its rail. Idempotent per (rail, file, line).</summary>
    public async Task<GoldpathFileRailResult> ProcessAsync(string railName, string file, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        if (!_options.Rails.TryGetValue(railName, out var rail))
        {
            throw new InvalidOperationException($"Rail '{railName}' is not declared — files ingest on declared rails only.");
        }

        if (rail.ValidateFileCore(lines) is { } fileReason)
        {
            _logger.LogWarning("Rail {Rail} rejected file {File}: {Reason}", rail.Name, file, fileReason);
            GoldpathFileExchangeMetrics.CountFileRejected(rail.Name);
            await PublishAsync(new GoldpathFileRejected(rail.Name, file, fileReason), cancellationToken);
            return new GoldpathFileRailResult(rail.Name, file, fileReason, 0, 0, []);
        }

        // #482, before ANY line applies: every line already processed under this (rail, file) must carry the content it was
        // processed with. A different file under a name already ingested would otherwise have its first lines skipped as
        // "duplicates" — silent loss that looks like idempotence. So the whole file is refused, naming the first line that
        // differs, and nothing of it applies. A stored hash that is unknown ("": marked before the upgrade, or by a ledger
        // that keeps none) counts as matching, so an upgrade does not refuse every redelivery. The same read tells the loop
        // below which lines are processed, so a run reads the ledger once per line, as it always has. A line is hashed only
        // where a hash is needed — to compare with a stored one here, or to be stored once it applies — and the comparison
        // runs on the stack, so a line nobody processed, or one quarantined, costs no hash, and the run keeps one flag per
        // line rather than any string (verifier, PR #603: hashing every line had made a 1M-line replay allocate 473 MiB).
        var alreadyProcessed = new bool[lines.Count];
        for (var i = rail.HeaderLines; i < lines.Count; i++)
        {
            var lineNo = i + 1;   // 1-based, matches what operators see in the file
            if (await _ledger.GetProcessedHashAsync(rail.Name, file, lineNo, cancellationToken) is not { } known)
            {
                continue;
            }

            alreadyProcessed[i] = true;
            if (known.Length > 0 && !HashMatches(lines[i], known))
            {
                var changed = $"Line {lineNo} differs from the line already processed under this file name: this is a different file "
                    + "delivered under a name the rail has already ingested, so nothing of it was applied. Deliver it under a new name.";
                _logger.LogWarning("Rail {Rail} rejected file {File}: {Reason}", rail.Name, file, changed);
                GoldpathFileExchangeMetrics.CountFileRejected(rail.Name);
                await PublishAsync(new GoldpathFileRejected(rail.Name, file, changed), cancellationToken);
                return new GoldpathFileRailResult(rail.Name, file, changed, 0, 0, []);
            }
        }

        var dataRows = lines.Count - rail.HeaderLines;
        GoldpathFileExchangeMetrics.CountFileReceived(rail.Name);
        await PublishAsync(new GoldpathFileReceived(rail.Name, file, dataRows), cancellationToken);

        var processed = 0;
        var skipped = 0;
        var quarantined = new List<GoldpathQuarantinedRow>();
        for (var i = rail.HeaderLines; i < lines.Count; i++)
        {
            var lineNo = i + 1;   // 1-based, matches what operators see in the file
            if (alreadyProcessed[i])
            {
                skipped++;
                continue;
            }

            if (await rail.ProcessRowCore(lines[i], cancellationToken) is { } reason)
            {
                await _ledger.QuarantineAsync(rail.Name, file, lineNo, reason, cancellationToken);
                quarantined.Add(new GoldpathQuarantinedRow(rail.Name, file, lineNo, reason));
                continue;   // the batch does NOT stop — quarantine is per row
            }

            await _ledger.MarkProcessedWithHashAsync(rail.Name, file, lineNo, HashOf(lines[i]), cancellationToken);
            await _ledger.ReleaseQuarantineAsync(rail.Name, file, lineNo, cancellationToken);
            processed++;
        }

        if (quarantined.Count > 0)
        {
            await PublishAsync(new GoldpathRowsQuarantined(rail.Name, file, quarantined.Count), cancellationToken);
        }

        await _ledger.MarkArchivedAsync(rail.Name, file, cancellationToken);
        GoldpathFileExchangeMetrics.CountRows(rail.Name, processed, quarantined.Count, skipped);
        await PublishAsync(new GoldpathFileIngested(rail.Name, file, processed, skipped, quarantined.Count), cancellationToken);
        return new GoldpathFileRailResult(rail.Name, file, null, processed, skipped, quarantined);
    }

    /// <summary>
    /// The content hash a processed row is marked with: SHA-256 of the line's UTF-8 bytes, lower-case hex (64 characters).
    /// A line that is not well-formed text (a lone surrogate) has no exact UTF-8 form — the lenient encoder would turn every
    /// such character into the same U+FFFD, so two different lines would hash alike — and is hashed instead by its exact
    /// UTF-16 code units, behind a prefix no UTF-8 encoding begins with, so it can collide with nothing.
    /// </summary>
    internal static string HashOf(string line)
    {
        Span<byte> hash = stackalloc byte[HashBytes];
        HashInto(line, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="line"/> hashes to <paramref name="storedHex"/> (hex, either case) — compared on the stack, with
    /// no string built. Anything that is not 64 hex digits matches no line: an unreadable stored hash refuses, never passes.
    /// </summary>
    internal static bool HashMatches(string line, string storedHex)
    {
        if (storedHex.Length != HashBytes * 2)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[HashBytes];
        HashInto(line, hash);
        for (var i = 0; i < HashBytes; i++)
        {
            if (HexValue(storedHex[2 * i]) != hash[i] >> 4 || HexValue(storedHex[(2 * i) + 1]) != (hash[i] & 0xF))
            {
                return false;
            }
        }

        return true;
    }

    private static void HashInto(string line, Span<byte> hash)
    {
        var max = StrictUtf8.GetMaxByteCount(line.Length);
        byte[]? rented = null;
        Span<byte> buffer = max <= StackLimit ? stackalloc byte[StackLimit] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var written = StrictUtf8.GetBytes(line, buffer);
            SHA256.HashData(buffer[..written], hash);
        }
        catch (EncoderFallbackException)
        {
            byte[] bytes = [0xFF, .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(line.AsSpan())];
            SHA256.HashData(bytes, hash);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private const int HashBytes = 32;
    private const int StackLimit = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
        => _publisher?.PublishAsync(integrationEvent, cancellationToken) ?? Task.CompletedTask;
}

/// <summary>In-memory ledger: tests and single-node hosts; database ledgers compose via the seam.</summary>
public sealed class GoldpathInMemoryFileLedger : IGoldpathFileLedger, IGoldpathFileLedgerQueries
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    // The processed rows and the content hash each was marked with ("" when marked without one).
    // The stored hash is kept as the completed task the read answers with, so a replay — one read per line — allocates
    // nothing per line (verifier, PR #603).
    private readonly Dictionary<(string Rail, string File, int Line), Task<string?>> _processed = [];
    private static readonly Task<string?> NoHash = Task.FromResult<string?>("");
    private static readonly Task<string?> NotProcessed = Task.FromResult<string?>(null);
    private readonly Dictionary<(string Rail, string File, int Line), (string Reason, DateTimeOffset At)> _quarantine = [];
    private readonly Dictionary<(string Rail, string File), DateTimeOffset> _archived = [];

    /// <summary>Creates the ledger on the system clock.</summary>
    public GoldpathInMemoryFileLedger() : this(TimeProvider.System)
    {
    }

    /// <summary>Creates the ledger on <paramref name="clock"/> (tests pin the quarantine age).</summary>
    public GoldpathInMemoryFileLedger(TimeProvider clock) => _clock = clock;

    /// <inheritdoc />
    public Task<bool> IsProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_processed.ContainsKey((rail, file, line)));
        }
    }

    /// <inheritdoc />
    public Task MarkProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _processed.TryAdd((rail, file, line), NoHash);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetProcessedHashAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _processed.TryGetValue((rail, file, line), out var hash) ? hash : NotProcessed;
        }
    }

    /// <inheritdoc />
    public Task MarkProcessedWithHashAsync(string rail, string file, int line, string contentHash, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // A row is marked once, as in the database ledger (its key refuses a second insert): the first hash stands.
            _processed.TryAdd((rail, file, line), Task.FromResult<string?>(contentHash));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task QuarantineAsync(string rail, string file, int line, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Upsert keeps the FIRST quarantine time: the age an operator triages is how long
            // the row has been waiting, not how recently the same failure repeated.
            var at = _quarantine.TryGetValue((rail, file, line), out var existing) ? existing.At : _clock.GetUtcNow();
            _quarantine[(rail, file, line)] = (reason, at);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseQuarantineAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _quarantine.Remove((rail, file, line));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathQuarantinedRow>> GetQuarantineAsync(string rail, string file, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathQuarantinedRow> rows = _quarantine
                .Where(kv => kv.Key.Rail == rail && kv.Key.File == file)
                .OrderBy(kv => kv.Key.Line)
                .Select(kv => new GoldpathQuarantinedRow(kv.Key.Rail, kv.Key.File, kv.Key.Line, kv.Value.Reason))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public Task MarkArchivedAsync(string rail, string file, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _archived.TryAdd((rail, file), _clock.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathFileInfo>> ListFilesAsync(string? rail, int take, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var keys = _archived.Keys
                .Concat(_quarantine.Keys.Select(k => (k.Rail, k.File)))
                .Concat(_processed.Keys.Select(k => (k.Rail, k.File)))
                .Where(k => rail is null || string.Equals(k.Rail, rail, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            IReadOnlyList<GoldpathFileInfo> files = keys
                .Select(k => new GoldpathFileInfo(
                    k.Rail,
                    k.File,
                    _processed.Keys.Count(p => p.Rail == k.Rail && p.File == k.File),
                    _quarantine.Keys.Count(q => q.Rail == k.Rail && q.File == k.File),
                    _archived.ContainsKey(k),
                    _archived.TryGetValue(k, out var at) ? at : null))
                .OrderByDescending(f => f.ArchivedAt ?? DateTimeOffset.MaxValue)   // in-flight (unarchived) files first, then newest archive
                .ThenBy(f => f.Rail, StringComparer.Ordinal).ThenBy(f => f.File, StringComparer.Ordinal)
                .Take(take)
                .ToList();
            return Task.FromResult(files);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathFileQuarantineInfo>> ListQuarantineAsync(string? rail, int take, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathFileQuarantineInfo> rows = _quarantine
                .Where(kv => rail is null || string.Equals(kv.Key.Rail, rail, StringComparison.OrdinalIgnoreCase))
                // Same tie-break as the database ledger: age, then rail, file, line — one
                // order whichever ledger the app composed.
                .OrderBy(kv => kv.Value.At).ThenBy(kv => kv.Key.Rail, StringComparer.Ordinal).ThenBy(kv => kv.Key.File, StringComparer.Ordinal).ThenBy(kv => kv.Key.Line)
                .Take(take)
                .Select(kv => new GoldpathFileQuarantineInfo(kv.Key.Rail, kv.Key.File, kv.Key.Line, kv.Value.Reason, kv.Value.At))
                .ToList();
            return Task.FromResult(rows);
        }
    }
}
