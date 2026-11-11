namespace Goldpath;

/// <summary>
/// The publish seam's batch member (#285; owner decision 2026-11-09): how a Goldpath package publishes many events of
/// one type without one awaited broker round trip per event. It is INTERNAL on purpose — reached by the packages that
/// need it through <c>InternalsVisibleTo</c>, never by an adopter — so it adds no line to any <c>PublicAPI.*.txt</c>
/// (ADR-0013, GA readiness G1) and an engine switch still changes nothing an adopter wrote (ADR-0015).
///
/// <para>Each engine's publisher implements it. With no outbox composed, the batch goes to the broker with bounded
/// concurrency (<see cref="GoldpathBatchPublishing.MaxConcurrency"/> publishes outstanding at once). With an outbox,
/// every publish is a row added to the ONE scoped <c>DbContext</c>, which is not safe for concurrent use, so the rows
/// are added in order — a local write with no round trip, which is already fast. A publisher that does not implement
/// it (a decorator, a third engine) is published through one item at a time, so the behaviour is never weaker than
/// <see cref="IIntegrationEventPublisher.PublishAsync{TEvent}"/>.</para>
///
/// <para>The contract a caller relies on is the single publish's, per event: when the returned task completes, every
/// event was handed to the broker (or added to the outbox). When it faults, any subset may have been published — the
/// caller must treat the whole batch as unpublished-and-maybe-delivered, exactly as a crash between publish and mark,
/// which is what the campaign's state-guarded claim already makes safe.</para>
/// </summary>
internal interface IGoldpathBatchPublisher
{
    /// <summary>Publishes every event in <paramref name="integrationEvents"/>; see the type's remarks for ordering and failure.</summary>
    Task PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> integrationEvents, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent;
}

/// <summary>The entry point a package calls: the engine's batch member when it has one, one at a time otherwise.</summary>
internal static class GoldpathBatchPublishing
{
    /// <summary>
    /// The most publishes a batch keeps outstanding at once on the wire. Bounded so one batch can neither flood the
    /// client's confirm tracking nor starve other publishers sharing the connection; 64 keeps a 250-item tick at four
    /// confirm round trips instead of 250.
    /// </summary>
    internal const int MaxConcurrency = 64;

    /// <summary>Publishes <paramref name="integrationEvents"/> through <paramref name="publisher"/>.</summary>
    internal static Task PublishBatchAsync<TEvent>(
        this IIntegrationEventPublisher publisher, IReadOnlyList<TEvent> integrationEvents, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(integrationEvents);
        if (integrationEvents.Count == 0)
        {
            return Task.CompletedTask;
        }

        return publisher is IGoldpathBatchPublisher batch
            ? batch.PublishBatchAsync(integrationEvents, cancellationToken)
            : OneAtATimeAsync(publisher, integrationEvents, cancellationToken);
    }

    /// <summary>The single publish, in order: the fallback, and the outbox path of each engine.</summary>
    internal static async Task OneAtATimeAsync<TEvent>(
        IIntegrationEventPublisher publisher, IReadOnlyList<TEvent> integrationEvents, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
    {
        foreach (var integrationEvent in integrationEvents)
        {
            await publisher.PublishAsync(integrationEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="publish"/> for every item with at most <see cref="MaxConcurrency"/> outstanding, and
    /// completes when all have; the first failure is rethrown once every started publish has settled, so nothing is
    /// left running against a scope the caller is about to dispose.
    /// </summary>
    internal static async Task BoundedAsync<T>(IReadOnlyList<T> items, Func<T, CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        for (var start = 0; start < items.Count; start += MaxConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(start + MaxConcurrency, items.Count);
            var window = new Task[end - start];
            for (var i = start; i < end; i++)
            {
                window[i - start] = StartAsync(publish, items[i], cancellationToken);
            }

            await Task.WhenAll(window);
        }
    }

    // A publish that throws before its first await becomes a faulted task here, so the window's other publishes are
    // still awaited rather than abandoned.
    private static async Task StartAsync<T>(Func<T, CancellationToken, Task> publish, T item, CancellationToken cancellationToken)
        => await publish(item, cancellationToken);
}
