namespace Goldpath;

/// <summary>
/// One released item on the wire (the pacer publishes, competing consumers receive).
/// Carries COORDINATES only — the target payload stays in the durable item row; a broker
/// message is delivery plumbing, not a second source of truth.
/// </summary>
public sealed record GoldpathCampaignItemMessage(Guid CampaignId, long Seq, string Type) : IIntegrationEvent;

/// <summary>
/// One item's outcome (consumers and the callback surface publish; the batching SINK flushes
/// durable truth). R2 widened it without touching the positional shape: a failure now says
/// whether a retry can help and names its code; an ACCEPTANCE (<see cref="Accepted"/>) parks
/// the item until a callback or the ack deadline settles it.
/// </summary>
public sealed record GoldpathCampaignOutcomeMessage(Guid CampaignId, long Seq, bool Succeeded, string? Error) : IIntegrationEvent
{
    /// <summary>The provider's short, stable error code (reports group by it); null when unknown.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>For a failure: whether the ladder applies. Default true keeps R1's behaviour for legacy handlers.</summary>
    public bool Retryable { get; init; } = true;

    /// <summary>True when the target system accepted the request and will answer later (R2.1).</summary>
    public bool Accepted { get; init; }

    /// <summary>The id the target system's callback will name (acceptances only).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The instant after which an unanswered acceptance is a retryable failure.</summary>
    public DateTimeOffset? AckDeadline { get; init; }
}
