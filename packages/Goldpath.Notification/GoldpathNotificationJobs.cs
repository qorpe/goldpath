using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The send run (notification RFC D3): claims a batch of due Requested rows (claim
/// persisted BEFORE any channel call — MDM constraint 2), sends with a bounded in-attempt
/// retry, stamps evidence, and files exhausted or interrupted rows into the REPAIR QUEUE.
/// The jobs `replay-items` verb is the re-send verb (a human confirms the provider first).
/// </summary>
public sealed class GoldpathNotificationSendJob<TContext> : IGoldpathJob, IGoldpathItemReplay
    where TContext : DbContext
{
    private readonly GoldpathNotificationOptions _options;
    private readonly IEnumerable<IGoldpathNotificationChannel> _channels;
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathNotificationSendJob<TContext>> _logger;

    /// <summary>Resolved per fire.</summary>
    public GoldpathNotificationSendJob(
        GoldpathNotificationOptions options, IEnumerable<IGoldpathNotificationChannel> channels,
        TimeProvider time, ILogger<GoldpathNotificationSendJob<TContext>> logger)
    {
        _options = options;
        _channels = channels;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var due = await db.Set<GoldpathNotification>()
            .CountAsync(n => n.State == GoldpathNotificationState.Requested
                && (n.NotBefore == null || n.NotBefore <= now), cancellationToken);
        var chunks = (int)Math.Ceiling(due / (double)_options.ChunkSize);
        var payloads = new List<string>();
        for (var i = 0; i < chunks; i++)
        {
            payloads.Add(string.Create(CultureInfo.InvariantCulture, $"send:{i}"));
        }

        // This run fires every ~30s — publish the queue gauges from here so the
        // stuck-channel alert stays live unattended.
        foreach (var template in _options.Templates)
        {
            var counts = await db.Set<GoldpathNotification>().AsNoTracking()
                .Where(n => n.Template == template.Key)
                .GroupBy(n => n.State)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            var oldestRequested = await db.Set<GoldpathNotification>().AsNoTracking()
                .Where(n => n.Template == template.Key && n.State == GoldpathNotificationState.Requested)
                .OrderBy(n => n.RequestedAt)
                .Select(n => (DateTimeOffset?)n.RequestedAt)
                .FirstOrDefaultAsync(cancellationToken);
            GoldpathNotificationMetrics.SetQueueSnapshot(
                template.Key,
                counts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal),
                oldestRequested is { } t ? (now - t).TotalSeconds : 0);
        }

        payloads.Add("interrupted");   // the crash sweep rides every run; a no-op costs nothing
        return new GoldpathJobPlan(payloads, due);
    }

    /// <inheritdoc />
    public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();

        if (chunk.Payload == "interrupted")
        {
            // Claimed before THIS run started, never stamped: the send was interrupted
            // mid-flight. Repair, never silently re-send (the provider may have accepted).
            var interrupted = await db.Set<GoldpathNotification>()
                .Where(n => n.State == GoldpathNotificationState.Requested && n.ClaimedAt != null && n.ClaimedAt < now - _options.StaleClaimAfter)
                .ToListAsync(cancellationToken);
            foreach (var row in interrupted)
            {
                row.State = GoldpathNotificationState.Failed;
                row.FailedAt = now;
                row.Detail = "interrupted mid-flight on a previous attempt — confirm with the provider, then replay";
                chunk.ReportItemFailure(row.Id.ToString("N"), row.Detail);
            }

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // THE claim: persisted before any channel call.
        var batch = await db.Set<GoldpathNotification>()
            .Where(n => n.State == GoldpathNotificationState.Requested && n.ClaimedAt == null
                && (n.NotBefore == null || n.NotBefore <= now))
            .OrderBy(n => n.RequestedAt)
            .Take(_options.ChunkSize)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return;
        }

        foreach (var row in batch)
        {
            row.ClaimedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var row in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendOneAsync(db, row, chunk, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReplayItemAsync(string itemKey, GoldpathJobContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<TContext>();
        var id = Guid.ParseExact(itemKey, "N");
        var row = await db.Set<GoldpathNotification>().FirstOrDefaultAsync(n => n.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"No notification for repair item '{itemKey}'.");
        if (row.State == GoldpathNotificationState.Sent)
        {
            return;   // already accepted; replay is idempotent evidence, not a re-send
        }

        row.State = GoldpathNotificationState.Requested;   // a HUMAN confirmed the provider — one more attempt
        row.Attempts = 0;
        await SendOneAsync(db, row, chunk: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (row.State != GoldpathNotificationState.Sent)
        {
            throw new InvalidOperationException(row.Detail ?? "the channel refused again — the item stays open");
        }
    }

    private async Task SendOneAsync(TContext db, GoldpathNotification row, GoldpathJobChunk? chunk, CancellationToken cancellationToken)
    {
        var channel = _channels.FirstOrDefault(c => c.Name == row.Channel);
        var start = _time.GetTimestamp();
        if (channel is null)
        {
            row.State = GoldpathNotificationState.Failed;
            row.FailedAt = _time.GetUtcNow();
            row.Detail = $"no channel named '{row.Channel}' is registered — registered: {string.Join(", ", _channels.Select(c => c.Name))}";
            chunk?.ReportItemFailure(row.Id.ToString("N"), row.Detail);
            return;
        }

        var attachments = await db.Set<GoldpathNotificationAttachment>().AsNoTracking()
            .Where(a => a.NotificationId == row.Id && a.Content != null)
            .Select(a => new GoldpathNotificationAttachmentContent(a.Name, a.ContentType, a.Content!))
            .ToListAsync(cancellationToken);
        var message = new GoldpathNotificationMessage(
            row.Id, row.Recipient, row.Subject, row.Body ?? "", attachments, row.Template, row.Tenant, row.CorrelationId);

        while (true)
        {
            try
            {
                row.Attempts++;
                await channel.SendAsync(message, cancellationToken);
                row.State = GoldpathNotificationState.Sent;
                row.SentAt = _time.GetUtcNow();
                row.Detail = null;
                GoldpathNotificationMetrics.Sent(row.Template, row.Channel, _time.GetElapsedTime(start));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // shutdown: the claim stays; the next run's sweep repairs it honestly
            }
            catch (Exception e)
            {
                if (row.Attempts >= _options.MaxAttempts)
                {
                    // #532: the provider's words name who they refused. Masked once, before ANY surface: the row's Detail
                    // (the admin views), the jobs console's repair reason and the log. The log carries the exception's
                    // type and the masked text, not the exception object, whose message and inner messages are unmasked.
                    var detail = GoldpathNotificationFailureText.Of(e, row.Recipient);
                    row.State = GoldpathNotificationState.Failed;
                    row.FailedAt = _time.GetUtcNow();
                    row.Detail = detail;
                    GoldpathNotificationMetrics.Failed(row.Template, row.Channel);
                    chunk?.ReportItemFailure(row.Id.ToString("N"), detail);
                    _logger.LogWarning(
                        "Notification {NotificationId} exhausted its {Attempts} attempts: {FailureType}: {Detail}",
                        row.Id, row.Attempts, e.GetType().FullName, detail);
                    return;
                }

                await Task.Delay(_options.RetryDelay, _time, cancellationToken);
            }
        }
    }
}

/// <summary>
/// Body retention (notification RFC D5): nulls rendered subject/body and attachment
/// CONTENT past each template's window. The evidence row — and the template hash proving
/// WHAT was sent — survives forever; attachment NAMES survive too.
/// </summary>
public sealed class GoldpathNotificationRetentionJob<TContext> : IGoldpathJob
    where TContext : DbContext
{
    private readonly GoldpathNotificationOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Resolved per fire.</summary>
    public GoldpathNotificationRetentionJob(GoldpathNotificationOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
        => Task.FromResult(new GoldpathJobPlan(
            [.. _options.Templates.Where(t => t.DeleteBodyAfter is not null).Select(t => t.Key)]));

    /// <inheritdoc />
    public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<TContext>();
        var template = _options.Template(chunk.Payload);
        var cutoff = _time.GetUtcNow() - template.DeleteBodyAfter!.Value;
        var now = _time.GetUtcNow();

        var expired = db.Set<GoldpathNotification>()
            .Where(n => n.Template == template.Key && n.BodyDeletedAt == null && n.Body != null)
            .Where(n => (n.SentAt != null && n.SentAt < cutoff)
                || (n.FailedAt != null && n.FailedAt < cutoff)
                || (n.State == GoldpathNotificationState.Suppressed && n.RequestedAt < cutoff));

        await db.Set<GoldpathNotificationAttachment>()
            .Where(a => expired.Select(n => n.Id).Contains(a.NotificationId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Content, (byte[]?)null), cancellationToken);
        await expired.ExecuteUpdateAsync(s => s
            .SetProperty(n => n.Subject, (string?)null)
            .SetProperty(n => n.Body, (string?)null)
            .SetProperty(n => n.BodyDeletedAt, now), cancellationToken);
    }
}
