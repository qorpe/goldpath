using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Goldpath;

/// <summary>
/// A batch handler on Wolverine (RFC goldpath-messaging-engines §4.1 row 12): deliveries the listener holds in
/// flight are gathered into a batch that closes at <see cref="GoldpathBatchOptions.MessageLimit"/> or once
/// <see cref="GoldpathBatchOptions.TimeLimit"/> has passed since its first delivery, whichever comes first; at
/// most <see cref="GoldpathBatchOptions.ConcurrencyLimit"/> batches run at once. A delivery's handling completes
/// only when its batch has run (the listener acknowledges after handling), and a throw fails every delivery in
/// the batch into Wolverine's retry ladder.
/// </summary>
internal abstract class BatchSeamHandler : SeamHandler
{
    public static BatchSeamHandler For(GoldpathBatchHandlerRegistration registration)
        => (BatchSeamHandler)Activator.CreateInstance(
            typeof(BatchSeamHandler<,>).MakeGenericType(registration.Event, registration.Handler), registration.Options)!;
}

internal sealed class BatchSeamHandler<TEvent, THandler>(GoldpathBatchOptions options) : BatchSeamHandler
    where TEvent : class, IIntegrationEvent
    where THandler : class, IIntegrationEventBatchHandler<TEvent>
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _running = new(options.ConcurrencyLimit, options.ConcurrencyLimit);
    private List<(IntegrationEvent<TEvent> Item, TaskCompletionSource Done)>? _open;

    public override Type MessageType => typeof(TEvent);

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope ?? throw new InvalidOperationException("A message arrived without its envelope.");
        var message = envelope.Message as TEvent ?? throw new InvalidOperationException($"The message is not a '{typeof(TEvent).Name}'.");
        var (headers, tenant, correlationId) = Read(envelope);
        var item = new IntegrationEvent<TEvent>(message, new IntegrationEventContext(envelope.Id, correlationId, tenant, Math.Max(0, envelope.Attempts - 1), headers));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        List<(IntegrationEvent<TEvent> Item, TaskCompletionSource Done)>? full = null;
        lock (_gate)
        {
            if (_open is null)
            {
                var opened = new List<(IntegrationEvent<TEvent>, TaskCompletionSource)>();
                _open = opened;
                _ = CloseAfterAsync(opened, context.Runtime.Services);
            }

            _open.Add((item, done));
            if (_open.Count >= options.MessageLimit)
            {
                full = _open;
                _open = null;
            }
        }

        if (full is not null)
        {
            _ = RunAsync(full, context.Runtime.Services);
        }

        await done.Task.WaitAsync(cancellation);
    }

    private async Task CloseAfterAsync(List<(IntegrationEvent<TEvent> Item, TaskCompletionSource Done)> batch, IServiceProvider services)
    {
        await Task.Delay(options.TimeLimit);
        lock (_gate)
        {
            if (!ReferenceEquals(_open, batch))
            {
                return;   // it filled up and is already running
            }

            _open = null;
        }

        await RunAsync(batch, services);
    }

    private async Task RunAsync(List<(IntegrationEvent<TEvent> Item, TaskCompletionSource Done)> batch, IServiceProvider services)
    {
        await _running.WaitAsync();
        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync([.. batch.Select(b => b.Item)], CancellationToken.None);
            batch.ForEach(b => b.Done.TrySetResult());
        }
        catch (Exception e)
        {
            batch.ForEach(b => b.Done.TrySetException(e));
        }
        finally
        {
            _running.Release();
        }
    }
}
