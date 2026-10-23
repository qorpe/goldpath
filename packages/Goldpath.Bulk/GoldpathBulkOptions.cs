using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>
/// The app's execution hook: one VALID, APPROVED row. Throwing sends the row to the run's
/// repair queue (the jobs `replay-items` verb retries it); the chunk continues. Handlers
/// calling external systems get at-most-once-or-repair semantics: the engine CLAIMS the
/// row before invoking (MDM constraint 2), so a crash never silently re-sends.
/// </summary>
public interface IGoldpathBulkRowHandler<in TRow>
    where TRow : class
{
    /// <summary>Executes one row.</summary>
    Task ExecuteAsync(TRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken);
}

/// <summary>Ambient facts handed to the row handler.</summary>
public sealed class GoldpathBulkRowContext
{
    internal GoldpathBulkRowContext(Guid batchId, int rowNumber, string? tenant, bool replay, IServiceProvider services)
    {
        BatchId = batchId;
        RowNumber = rowNumber;
        Tenant = tenant;
        Replay = replay;
        Services = services;
    }

    /// <summary>The owning batch.</summary>
    public Guid BatchId { get; }

    /// <summary>The row's 1-based coordinate in the file.</summary>
    public int RowNumber { get; }

    /// <summary>The batch's tenant, when tenant-bound.</summary>
    public string? Tenant { get; }

    /// <summary>True when this invocation is an admin replay of a repair-queue row.</summary>
    public bool Replay { get; }

    /// <summary>Scoped services of the executing chunk.</summary>
    public IServiceProvider Services { get; }
}

/// <summary>Accumulates validation findings for one row (value-free by contract, D6).</summary>
public sealed class GoldpathBulkRowValidationContext
{
    internal GoldpathBulkRowValidationContext(IServiceProvider services) => Services = services;

    /// <summary>Scoped services for validation lookups (reference data, account checks).</summary>
    public IServiceProvider Services { get; }

    internal List<(string Field, string Message)> Findings { get; } = [];

    /// <summary>
    /// Records one finding. The message must not echo the offending VALUE — reports stay
    /// classified-data-free; the raw file is the evidence.
    /// </summary>
    public void Fail(string field, string message) => Findings.Add((field, message));
}

/// <summary>The outcome of validating one raw row (engine-internal transport).</summary>
public sealed class GoldpathBulkRowResult
{
    /// <summary>The typed row as JSON when every check passed; null for invalid rows.</summary>
    public string? Payload { get; init; }

    /// <summary>Findings (empty for valid rows).</summary>
    public IReadOnlyList<(string Field, string Message)> Errors { get; init; } = [];

    /// <summary>The in-file duplicate key, when the definition declares one.</summary>
    public string? RowKey { get; init; }
}

/// <summary>
/// One registered batch shape. The generic builder BAKES typed closures at registration
/// (the archival pattern): the engine and the jobs stay non-generic over row types.
/// </summary>
public sealed class GoldpathBulkDefinition
{
    internal GoldpathBulkDefinition(string name) => Name = name;

    /// <summary>Registration key: the upload route segment and the report label.</summary>
    public string Name { get; }

    /// <summary>The row ceiling — MANDATORY (an unbounded intake is a decision nobody made, GP1501).</summary>
    public int MaxRows { get; internal set; }

    /// <summary>Skips the approval gate (imports, reference data) — analyzer-visible (GP1503).</summary>
    public bool AutoApprove { get; internal set; }

    /// <summary>Allows approving a batch with invalid rows: the valid subset executes, the report is the evidence (D5).</summary>
    public bool TolerateInvalidRows { get; internal set; }

    /// <summary>Retention for the raw file's bytes after the batch reaches a terminal state (D6).</summary>
    public TimeSpan? DeleteFileAfter { get; internal set; }

    /// <summary>The format reader (v1: CSV).</summary>
    public IGoldpathBulkFormat Format { get; internal set; } = new GoldpathCsvFormat(new GoldpathCsvOptions());

    internal Func<GoldpathBulkRawRow, IServiceProvider, GoldpathBulkRowResult> ValidateRow { get; set; } = null!;

    internal Func<string, GoldpathBulkRowContext, CancellationToken, Task> ExecuteRow { get; set; } = null!;

    /// <summary>The row key of a staged payload: the key its row was validated under (in-file duplicates).</summary>
    internal Func<string, string?> KeyOfPayload { get; set; } = _ => null;

    /// <summary>Runs the baked conversion + validation pipeline for one raw row (contract-testable).</summary>
    public GoldpathBulkRowResult Validate(GoldpathBulkRawRow raw, IServiceProvider services)
        => ValidateRow(raw, services);
}

/// <summary>Fluent registration surface for one batch shape.</summary>
public sealed class GoldpathBulkBatchBuilder<TRow>
    where TRow : class, new()
{
    private readonly GoldpathBulkDefinition _definition;
    private GoldpathCsvOptions _csv = new();
    private Func<TRow, string?>? _rowKey;
    private Action<TRow, GoldpathBulkRowValidationContext>? _validate;

    internal GoldpathBulkBatchBuilder(GoldpathBulkDefinition definition) => _definition = definition;

    /// <summary>Configures the v1 CSV format (delimiter, culture).</summary>
    public GoldpathBulkBatchBuilder<TRow> Csv(Action<GoldpathCsvOptions>? configure = null)
    {
        _csv = new GoldpathCsvOptions();
        configure?.Invoke(_csv);
        _definition.Format = new GoldpathCsvFormat(_csv);
        return this;
    }

    /// <summary>Sets the mandatory row ceiling.</summary>
    public GoldpathBulkBatchBuilder<TRow> MaxRows(int maxRows)
    {
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows), "The row ceiling must be positive.");
        }

        _definition.MaxRows = maxRows;
        return this;
    }

    /// <summary>Declares the in-file duplicate key: a repeated key is a validation error on the LATER row.</summary>
    public GoldpathBulkBatchBuilder<TRow> RowKey(Func<TRow, string?> key)
    {
        _rowKey = key;
        return this;
    }

    /// <summary>Registers the domain validator (services available via the context).</summary>
    public GoldpathBulkBatchBuilder<TRow> Validate(Action<TRow, GoldpathBulkRowValidationContext> validate)
    {
        _validate = validate;
        return this;
    }

    /// <summary>Skips the approval gate for this definition (GP1503 makes it visible).</summary>
    public GoldpathBulkBatchBuilder<TRow> AutoApprove()
    {
        _definition.AutoApprove = true;
        return this;
    }

    /// <summary>Allows the gate to approve past invalid rows: only the valid subset executes (D5).</summary>
    public GoldpathBulkBatchBuilder<TRow> TolerateInvalidRows()
    {
        _definition.TolerateInvalidRows = true;
        return this;
    }

    /// <summary>Deletes the raw file's bytes this long after the batch reaches a terminal state.</summary>
    public GoldpathBulkBatchBuilder<TRow> DeleteFileAfter(TimeSpan period)
    {
        _definition.DeleteFileAfter = period;
        return this;
    }

    internal void Bake()
    {
        if (_definition.MaxRows == 0)
        {
            throw new InvalidOperationException(
                $"Bulk definition '{_definition.Name}' has no MaxRows — the row ceiling is a decision, not a default (bulk RFC D7 / GP1501).");
        }

        var culture = _csv.ResolveCulture();
        var properties = typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();
        var rowKey = _rowKey;
        var validate = _validate;

        _definition.ValidateRow = (raw, services) =>
        {
            var row = new TRow();
            var errors = new List<(string Field, string Message)>();
            foreach (var property in properties)
            {
                if (!raw.Fields.TryGetValue(property.Name, out var text))
                {
                    continue;   // absent column: the property keeps its default
                }

                if (!TryConvert(text, property.PropertyType, culture, out var value))
                {
                    errors.Add((property.Name, $"value is not a valid {FriendlyType(property.PropertyType)}"));
                    continue;
                }

                property.SetValue(row, value);
            }

            if (errors.Count == 0 && validate is not null)
            {
                var context = new GoldpathBulkRowValidationContext(services);
                validate(row, context);
                errors.AddRange(context.Findings);
            }

            return new GoldpathBulkRowResult
            {
                Payload = errors.Count == 0 ? JsonSerializer.Serialize(row) : null,
                Errors = errors,
                RowKey = errors.Count == 0 ? rowKey?.Invoke(row) : null,
            };
        };

        _definition.KeyOfPayload = payload => rowKey is null
            ? null
            : rowKey(JsonSerializer.Deserialize<TRow>(payload)
                ?? throw new InvalidOperationException($"Bulk row payload of '{_definition.Name}' deserialized to null."));

        _definition.ExecuteRow = async (payload, context, cancellationToken) =>
        {
            var row = JsonSerializer.Deserialize<TRow>(payload)
                ?? throw new InvalidOperationException($"Bulk row payload of '{_definition.Name}' deserialized to null.");
            var handler = context.Services.GetRequiredService<IGoldpathBulkRowHandler<TRow>>();
            await handler.ExecuteAsync(row, context, cancellationToken);
        };
    }

    private static string FriendlyType(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type).Name;

    private static bool TryConvert(string text, Type target, CultureInfo culture, out object? value)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (text.Length == 0)
        {
            // Empty field: null when the property can hold it; a finding otherwise.
            value = null;
            return underlying is not null || !target.IsValueType || target == typeof(string);
        }

        var type = underlying ?? target;
        try
        {
            value = type switch
            {
                _ when type == typeof(string) => text,
                _ when type == typeof(Guid) => Guid.Parse(text),
                _ when type == typeof(DateTimeOffset) => DateTimeOffset.Parse(text, culture, DateTimeStyles.AssumeUniversal),
                _ when type.IsEnum => Enum.Parse(type, text, ignoreCase: true),
                _ => Convert.ChangeType(text, type, culture),
            };
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            value = null;
            return false;
        }
    }
}

/// <summary>
/// Bulk composition options (bulk RFC §4). Definitions bake their typed closures at
/// registration; the engine, the jobs and the admin surface stay non-generic.
/// </summary>
public sealed class GoldpathBulkOptions
{
    internal List<GoldpathBulkDefinition> BatchList { get; } = [];

    /// <summary>The registered batch definitions.</summary>
    public IReadOnlyList<GoldpathBulkDefinition> Batches => BatchList;

    /// <summary>Valid rows per execute chunk (each chunk is one checkpoint).</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>File-chunk row inserts per SaveChanges during validation (batched writes).</summary>
    public int InsertBatchSize { get; set; } = 1_000;

    /// <summary>Registers one batch shape.</summary>
    public GoldpathBulkOptions AddBatch<TRow>(string name, Action<GoldpathBulkBatchBuilder<TRow>> configure)
        where TRow : class, new()
    {
        if (BatchList.Any(b => b.Name == name))
        {
            throw new InvalidOperationException($"Bulk definition '{name}' is already registered.");
        }

        var definition = new GoldpathBulkDefinition(name);
        var builder = new GoldpathBulkBatchBuilder<TRow>(definition);
        configure(builder);
        builder.Bake();
        BatchList.Add(definition);
        return this;
    }

    /// <summary>Finds a definition or fails with a teaching message.</summary>
    public GoldpathBulkDefinition Definition(string name)
        => BatchList.FirstOrDefault(b => b.Name == name)
            ?? throw new InvalidOperationException(
                $"No bulk definition named '{name}' — registered: {string.Join(", ", BatchList.Select(b => b.Name))}.");
}
