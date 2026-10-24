# Goldpath.Messaging

Message-path floor of the Goldpath enterprise asset, composed on the **MassTransit 8.x OSS line**
(v9 went commercial — the golden path carries no commercial-license surprise; exit strategy
documented in `docs/rfc/goldpath-messaging.md` D1).

## Getting started

```csharp
builder.AddGoldpathMessaging(bus =>
{
    bus.AddConsumer<OrderConfirmedConsumer>();
    bus.AddGoldpathOutbox<OrdersDbContext>(o => o.UsePostgres());   // features.outbox — template wires the lock provider

    bus.UsingRabbitMq((context, cfg) =>                        // transport from manifest providers.broker
    {
        cfg.Host(connectionString);
        cfg.ConfigureGoldpathEndpoints(context);                    // guard + propagation + retry + conventions
    });
});
```

## In Goldpath's vocabulary (G1, no engine type)

The same path, declared without naming the engine (RFC `goldpath-messaging-engines` §4.1). The
templates move to it at the preview.11 train; the block above keeps working until then.

```csharp
builder.AddGoldpathMessaging(messaging =>
{
    messaging.AddHandler<OrderConfirmed, OrderConfirmedHandler>();   // or messaging.AddHandlers(typeof(Program).Assembly)
    messaging.AddOutbox<OrdersDbContext>();                          // lock statements follow the context's EF provider
    messaging.UseRabbitMq();                                         // ConnectionStrings:messaging; the pipeline is applied to every endpoint
});

// OrdersDbContext.OnModelCreating — the same three tables as before, so no migration:
modelBuilder.AddGoldpathOutboxEntities();
```

`AddOutbox` takes `GoldpathOutboxOptions`: `DuplicateDetectionWindow` (30 min), `Inbox` (on) and
`DeliveryPollInterval` (the engine's default unless set).

**Testing:** `GoldpathMessagingTestHost` runs the same registrations on the in-memory transport
(or the one the block selects), returns from its start once the bus is up, and publishes the way
an upstream service would:

```csharp
await using var host = await GoldpathMessagingTestHost.StartAsync(
    messaging => messaging.AddHandler<OrderConfirmed, OrderConfirmedHandler>(),
    builder => builder.Services.AddSingleton(probe));
await host.PublishAsync(new OrderConfirmed(id), message => { message.Tenant = "acme"; message.MessageId = messageId; });
```

## Consuming through the seam (ADR-0013)

```csharp
public sealed class OrderConfirmedHandler(OrdersDbContext db) : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed e, IntegrationEventContext context, CancellationToken ct)
    {
        // context: MessageId (the inbox key), CorrelationId, Tenant, RetryAttempt, Headers — no library type.
        ...
    }
}

builder.AddGoldpathMessaging(bus =>
{
    bus.AddGoldpathHandler<OrderConfirmed, OrderConfirmedHandler>();   // or bus.AddGoldpathHandlers(typeof(Program).Assembly)
    ...
});
```

A handler drains the SAME queue the consumer of that name drained (`OrderConfirmedHandler`
and `OrderConfirmedConsumer` → `order-confirmed`), so moving to the seam changes no wire.
Retry, redelivery, the inbox and tenant restoration are the pipeline's — a throw is a
retry, then the error queue. `IConsumer<T>` still works (analyzer GP0405 says so, Info
until the templates adopt the seam at the next train; Warning from then on).

### In batches

A handler whose work is set-based takes deliveries in groups, each with its own context:

```csharp
public sealed class ReadingSink(MeterDbContext db) : IIntegrationEventBatchHandler<MeterRead>
{
    public async Task HandleAsync(IReadOnlyList<IntegrationEvent<MeterRead>> batch, CancellationToken ct)
    {
        // batch[i].Message, batch[i].Context (MessageId, CorrelationId, Tenant, Headers): one write for the lot.
        ...
    }
}

builder.AddGoldpathMessaging(messaging => messaging
    .AddBatchHandler<MeterRead, ReadingSink>(new GoldpathBatchOptions { MessageLimit = 200, TimeLimit = TimeSpan.FromMilliseconds(500) })
    .UseRabbitMq());
```

A batch is delivered when `MessageLimit` (10) or `TimeLimit` (1 s) is reached, whichever is
first (`MessageLimit × ConcurrencyLimit` at most 65,535, a broker's prefetch window); at most `ConcurrencyLimit` (1) batches run at once per endpoint; a throw fails the whole
batch into the retry ladder, so the sink must be idempotent per message. A queue that must keep
a name a running fleet already drains is named explicitly — `AddHandler<TEvent, THandler>("orders-db")`,
or `AddBatchHandler`'s `endpointName` — and handlers given one name share one queue.

## Configuration

Bound from `Goldpath:Messaging`:

```json
{ "Goldpath": { "Messaging": { "Retry": { "ImmediateCount": 3 } } } }
```

Redelivery defaults: 5m/15m/30m, then the error queue — messages are never silently dropped.

## Advanced

**The event boundary (the rule everything hangs on):**

```csharp
public record OrderConfirmed(Guid OrderId) : IIntegrationEvent;   // broker-bound — marker REQUIRED
public record OrderTotalsChanged(...) : INotification;            // in-process — Mediant, never the bus
```

Publishing an unmarked type throws (runtime guard; analyzer GP0401 will catch it at build).
A Mediant notification must never carry `IIntegrationEvent` (GP0402) — one world only.

- **Outbox** (`AddGoldpathOutbox<TContext>`): publish commits atomically with business data;
  consumer-side inbox dedup included; 30m duplicate-detection window default.
- **Propagation**: `X-Goldpath-Tenant` and `X-Correlation-Id` flow through publish/consume
  automatically; on consume the tenant is restored into the message-scoped
  `GoldpathMessageTenantContext` (`ITenantContext` in golden-path services).
- Kebab-case endpoint naming; OTel tracing flows via MassTransit's built-in instrumentation.

## Providers

Transport chosen in the manifest (`providers.broker`): `rabbitmq`, `kafka`, `inmemory`
(tests/dev). Transport packages and the outbox lock provider are wired by the template;
this package references only MassTransit core + EF outbox.
