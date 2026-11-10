namespace Goldpath;

/// <summary>
/// Tuning surface of the message-path floor. Bound from configuration section
/// <c>Goldpath:Messaging</c>, then the code callback applies.
/// </summary>
public sealed class GoldpathMessagingOptions
{
    /// <summary>Consumer resilience defaults (RFC goldpath-messaging D4). Messages are never silently dropped.</summary>
    public RetryOptions Retry { get; } = new();

    /// <summary>
    /// The largest message body, in bytes, this message path publishes (#458). The default is RabbitMQ 4's own
    /// <c>max_message_size</c>, 16 MiB: a body above the broker's limit is refused by the broker on every attempt, and an
    /// outbox row the broker refuses is retried for ever, holding back every row committed after it. So a publish above
    /// this limit is refused at the seam, before it is committed, and a committed outbox row above it (written before the
    /// limit was lowered, or by another build) is marked failed in place and skipped, never retried. Set it to the
    /// broker's limit when that is not RabbitMQ's default; it must be positive.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Retry tuning: immediate attempts, then delayed redelivery, then the error queue.</summary>
    public sealed class RetryOptions
    {
        /// <summary>Immediate in-process retry attempts before redelivery.</summary>
        public int ImmediateCount { get; set; } = 3;

        /// <summary>
        /// Delayed redelivery intervals (requires a transport/scheduler that supports delay);
        /// an empty list disables the redelivery stage.
        /// </summary>
        public IList<TimeSpan> RedeliveryIntervals { get; } =
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];
    }
}
