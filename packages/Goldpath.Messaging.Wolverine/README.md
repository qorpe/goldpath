# Goldpath.Messaging.Wolverine

The Wolverine engine adapter behind the Goldpath message-path seam (ADR-0013, ADR-0015, RFC
`goldpath-messaging-engines` §2): the 1.0 insurance against the MassTransit 8.x maintenance window
(GA gate G5). **Not published, and not a manifest value yet:** it becomes selectable only once the
switch proofs S1–S4 and S6 are linked in open-threads T18 (ADR-0015 §2, RFC §3).

## What it is

An application never names it. Referencing this package is the whole switch: `AddGoldpathMessaging`
finds the engine the package declares, and the messaging block, the handlers
(`IIntegrationEventHandler<T>`, `IIntegrationEventBatchHandler<T>`) and the publisher
(`IIntegrationEventPublisher`) stay as they are.

- **Wolverine receives.** One RabbitMQ listener per endpoint (the queue `GoldpathHandlerEndpoints.NameOf`
  names, or the name given at registration), a hand-written handler per endpoint and event (no code is
  generated, so no runtime compiler ships), the immediate retry ladder
  (`Goldpath:Messaging:Retry:ImmediateCount`), then the move to `<queue>_error`. A batch endpoint
  gathers in-flight deliveries by `MessageLimit` or `TimeLimit`, runs at most `ConcurrencyLimit`
  batches at once; a delivery's handling completes only when its batch has run, and the listener
  acknowledges a delivery after its handling (Wolverine's native acknowledgement), so an unrun batch
  leaves its deliveries on the queue.
- **The adapter writes what every adapter shares** (RFC §4.4): the MassTransit 8.x JSON envelope, the
  8.x RabbitMQ topology (one fanout exchange per message type named `Namespace:Type`; per endpoint a
  queue and a same-named exchange; `<queue>_error`), and the outbox and inbox in the 8.x tables
  (`OutboxMessage`, `OutboxState`, `InboxState`, mapped by `AddGoldpathOutboxEntities()`). A publish is a
  row committed with the caller's `SaveChanges`; a relay delivers it with broker confirms, locking with
  `FOR UPDATE SKIP LOCKED`, so two processes never deliver one row. A delivery is recorded in
  `InboxState` in the handler's own transaction, so a redelivered message runs once.
- **Tenant and correlation** travel as `X-Goldpath-Tenant` and `X-Correlation-Id`, restored into the
  message scope and the ambient tenant, exactly as the default engine restores them.
- **Tracing.** A publish carries its span as `MT-Activity-Id`, and a delivery's consume span continues that
  trace, tagged `goldpath.tenant_id` and `goldpath.correlation_id`. Listen to the ActivitySource
  `Goldpath.Messaging.Wolverine` (or `*`) to export it. The header is written into the envelope only, so across
  engines in a mixed fleet the trace is not continuous yet (T51).
- **A concurrency limit** (`GoldpathHandlerOptions.ConcurrencyLimit`) runs the endpoint's listener in parallel,
  at most that many deliveries at once and that many prefetched, each acknowledged once handled. Without one,
  the listener handles on the transport's callback, as before. A `[GoldpathMessageName]` names the exchange
  and the URN the adapter writes.

## What it refuses today

At registration, in words (open-threads T51): delayed redelivery (the default options enable it;
clear `Goldpath:Messaging:Retry:RedeliveryIntervals`), the in-memory transport, an endpoint shared by
several event types, an endpoint drained both singly and in batches, generic integration events, and
an outbox on SQL Server. A batch endpoint runs without the inbox.

## Proof

`tests/Goldpath.Messaging.Conformance` runs the whole fact class, S2.1–S2.12 and S2.14–S2.19 (S2.13, delayed
redelivery, is reserved: open-threads T52), on this engine and on
MassTransit 8.5.11, against real RabbitMQ and PostgreSQL, in CI's integration job. `WolverineAdapterTests`
beside it holds what the class cannot reach: a handler's publish committed without the inbox, two
handlers of one endpoint both run, and the inbox swept only past its window.
