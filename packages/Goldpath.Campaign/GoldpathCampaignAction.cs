namespace Goldpath;

/// <summary>
/// What one claimed item's execution came to (campaign RFC R2.1). A handler returns one of
/// the three shapes instead of "return = success, throw = failure": a TERMINAL answer
/// (<see cref="Succeeded"/>), a failure that says whether a retry can help
/// (<see cref="Failed"/>), or an ACCEPTANCE by a target system that will answer later
/// through the callback surface (<see cref="Accepted"/>) — OMA-DM sessions, SMS delivery
/// receipts and every other asynchronous target live on that third shape.
/// </summary>
public abstract record GoldpathCampaignActionResult
{
    private protected GoldpathCampaignActionResult()
    {
    }

    /// <summary>The action is done and it worked. Terminal.</summary>
    public static GoldpathCampaignActionResult Succeeded() => new GoldpathCampaignActionSucceeded();

    /// <summary>
    /// The action failed. <paramref name="retryable"/> decides the item's path: a transient
    /// refusal (timeout, 503, throttled) walks the retry ladder; a permanent one (unsupported
    /// device, invalid target) is terminal at once — no ladder, no human before the report.
    /// </summary>
    public static GoldpathCampaignActionResult Failed(string errorCode, string message, bool retryable)
        => new GoldpathCampaignActionFailed(errorCode, message, retryable);

    /// <summary>
    /// The target system accepted the request and will report the outcome later. The item
    /// waits as <see cref="GoldpathCampaignItemState.AwaitingAck"/> until a callback names
    /// <paramref name="correlationId"/> or <paramref name="ackTimeout"/> passes — then it is
    /// a retryable failure, never a silent success.
    /// </summary>
    public static GoldpathCampaignActionResult Accepted(string correlationId, TimeSpan ackTimeout)
        => new GoldpathCampaignActionAccepted(correlationId, ackTimeout);
}

/// <summary>The action is done and it worked.</summary>
public sealed record GoldpathCampaignActionSucceeded : GoldpathCampaignActionResult;

/// <summary>The action failed, with the provider's code, its words, and whether a retry can help.</summary>
public sealed record GoldpathCampaignActionFailed : GoldpathCampaignActionResult
{
    /// <summary>Creates a failure.</summary>
    public GoldpathCampaignActionFailed(string errorCode, string message, bool retryable)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("A failure needs an error code — the report groups by it.", nameof(errorCode));
        }

        ErrorCode = errorCode;
        Message = message ?? "";
        Retryable = retryable;
    }

    /// <summary>A short, stable code (`MDM_UNAVAILABLE`, `UNSUPPORTED_DEVICE`) — reports group by it.</summary>
    public string ErrorCode { get; }

    /// <summary>The provider's own words (bounded when stored).</summary>
    public string Message { get; }

    /// <summary>True when a later attempt can succeed (the ladder applies); false is terminal.</summary>
    public bool Retryable { get; }
}

/// <summary>The target system accepted the request; the outcome arrives by callback or times out.</summary>
public sealed record GoldpathCampaignActionAccepted : GoldpathCampaignActionResult
{
    /// <summary>Creates an acceptance.</summary>
    public GoldpathCampaignActionAccepted(string correlationId, TimeSpan ackTimeout)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("An acceptance needs the correlation id the callback will name.", nameof(correlationId));
        }

        if (ackTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ackTimeout), "The ack timeout must be positive — an acceptance that never expires is a silent loss.");
        }

        CorrelationId = correlationId;
        AckTimeout = ackTimeout;
    }

    /// <summary>The id the target system will name in its callback.</summary>
    public string CorrelationId { get; }

    /// <summary>How long to wait for that callback before treating the item as a retryable failure.</summary>
    public TimeSpan AckTimeout { get; }
}

/// <summary>
/// The R2 execution hook: one claimed target, one typed result. Prefer this over
/// <see cref="IGoldpathCampaignItemHandler{TTarget}"/> whenever the target system can refuse
/// permanently or answer asynchronously — the result carries what the ladder and the report
/// need. The item was CLAIMED before this runs (constraint 2); the context's
/// <see cref="GoldpathCampaignItemContext.IdempotencyKey"/> names the attempt so the target
/// system can recognize a redelivered request. Do NOT call <c>SaveChanges</c> here (GP1702).
/// </summary>
public interface IGoldpathCampaignActionHandler<in TTarget>
    where TTarget : class
{
    /// <summary>Executes one target and says what came of it.</summary>
    Task<GoldpathCampaignActionResult> ExecuteAsync(TTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken);
}
