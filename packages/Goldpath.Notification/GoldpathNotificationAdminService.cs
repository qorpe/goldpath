using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>One template's live queue numbers (the console's landing view).</summary>
public sealed record GoldpathNotificationTemplateStatus(
    string Key,
    string Hash,
    TimeSpan? DeleteBodyAfter,
    IReadOnlyDictionary<string, int> ByState,
    double? OldestRequestedSeconds);

/// <summary>
/// One notification over the wire. The RECIPIENT IS MASKED on this broad surface by
/// construction (first character + domain hint survive) — the full address lives only in
/// the store, behind whatever the operator's database access already implies.
/// </summary>
public sealed record GoldpathNotificationInfo(
    Guid Id,
    string DedupKey,
    string Template,
    string TemplateHash,
    string Channel,
    string MaskedRecipient,
    string Culture,
    string State,
    int Attempts,
    string? Detail,
    DateTimeOffset RequestedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? BodyDeletedAt,
    string? Tenant,
    string? CorrelationId);

/// <summary>
/// The notification admin views (§7.1: the API is the contract). READ-ONLY on purpose:
/// requesting belongs to the APP (the notifier), re-sending belongs to the JOBS console
/// (`replay-items`) — an admin surface that could inject messages would be an evidence
/// hole. The rows ARE the audit.
/// </summary>
public sealed class GoldpathNotificationAdminService<TContext>
    where TContext : DbContext
{
    private readonly GoldpathNotificationOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    /// <summary>Registered by <c>AddGoldpathNotification</c>.</summary>
    public GoldpathNotificationAdminService(
        GoldpathNotificationOptions options, IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _time = time;
    }

    /// <summary>Every template with its live numbers; feeds the queue gauges.</summary>
    public async Task<IReadOnlyList<GoldpathNotificationTemplateStatus>> GetTemplatesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var result = new List<GoldpathNotificationTemplateStatus>();
        foreach (var template in _options.Templates)
        {
            var counts = await db.Set<GoldpathNotification>().AsNoTracking()
                .Where(n => n.Template == template.Key)
                .GroupBy(n => n.State)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var oldestRequested = await db.Set<GoldpathNotification>().AsNoTracking()
                .Where(n => n.Template == template.Key && n.State == GoldpathNotificationState.Requested)
                .OrderBy(n => n.RequestedAt)
                .Select(n => (DateTimeOffset?)n.RequestedAt)
                .FirstOrDefaultAsync(ct);

            var byState = counts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal);
            double? oldestSeconds = oldestRequested is { } t ? (now - t).TotalSeconds : null;
            result.Add(new GoldpathNotificationTemplateStatus(
                template.Key, template.Hash, template.DeleteBodyAfter, byState, oldestSeconds));
            GoldpathNotificationMetrics.SetQueueSnapshot(template.Key, byState, oldestSeconds ?? 0);
        }

        return result;
    }

    /// <summary>Recent notifications, newest first (filters optional; tenant fail-closed).</summary>
    public async Task<IReadOnlyList<GoldpathNotificationInfo>> GetNotificationsAsync(
        string[]? state, string[]? template, string? tenant, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var query = db.Set<GoldpathNotification>().AsNoTracking();
        var states = state is null
            ? new List<GoldpathNotificationState>()
            : [.. state.Select(value => Enum.TryParse<GoldpathNotificationState>(value, ignoreCase: true, out var parsed) ? parsed : (GoldpathNotificationState?)null)
                       .Where(parsed => parsed is not null)
                       .Select(parsed => parsed!.Value)];
        if (states.Count > 0)
        {
            // Values OR within a filter (contract R3); one value behaves exactly as R2 did.
            query = query.Where(n => states.Contains(n.State));
        }

        if (template is { Length: > 0 })
        {
            // Bounded only by the request line, so it can pass Oracle's 1,000-element IN list (RFC goldpath-oracle §2.5).
            query = query.ChunkedContains(n => n.Template, template, db.Database.ProviderName);
        }

        if (tenant is not null)
        {
            query = query.Where(n => n.Tenant == tenant);
        }

        var rows = await query.OrderByDescending(n => n.RequestedAt).Take(AdminPaging.Clamp(take)).ToListAsync(ct);
        return [.. rows.Select(ToInfo)];
    }

    /// <summary>One notification's full story (tenant fail-closed; recipient still masked).</summary>
    public async Task<GoldpathNotificationInfo?> GetNotificationAsync(Guid id, string? tenant, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var row = await db.Set<GoldpathNotification>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        return row is null || (tenant is not null && row.Tenant != tenant) ? null : ToInfo(row);
    }

    /// <summary>The suppression report — every MaySend refusal, with its when (evidence, not logs).</summary>
    public Task<IReadOnlyList<GoldpathNotificationInfo>> GetSuppressionsAsync(string? tenant, int take, CancellationToken ct)
        => GetNotificationsAsync([nameof(GoldpathNotificationState.Suppressed)], null, tenant, take, ct);

    /// <summary>The failure list — repair/replay itself lives in the JOBS console.</summary>
    public Task<IReadOnlyList<GoldpathNotificationInfo>> GetFailuresAsync(string? tenant, int take, CancellationToken ct)
        => GetNotificationsAsync([nameof(GoldpathNotificationState.Failed)], null, tenant, take, ct);

    private static GoldpathNotificationInfo ToInfo(GoldpathNotification n) => new(
        n.Id, n.DedupKey, n.Template, n.TemplateHash, n.Channel,
        MaskRecipient(n.Recipient), n.Culture, n.State.ToString(), n.Attempts, n.Detail,
        n.RequestedAt, n.NotBefore, n.ClaimedAt, n.SentAt, n.FailedAt, n.BodyDeletedAt,
        n.Tenant, n.CorrelationId);

    /// <summary>
    /// Masks a recipient for the admin surface: first character + domain hint survive
    /// (enough to triage "wrong customer?", never enough to copy an address off a screen).
    /// </summary>
    public static string MaskRecipient(string recipient)
    {
        if (recipient.Length == 0)
        {
            return "";
        }

        var at = recipient.IndexOf('@');
        if (at > 0 && at < recipient.Length - 1)
        {
            var domain = recipient[(at + 1)..];
            return $"{recipient[0]}***@{domain[0]}***";
        }

        return $"{recipient[0]}***";
    }
}
