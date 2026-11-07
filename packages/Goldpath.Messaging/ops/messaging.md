# Messaging — Ops Runbook

The floor's bus: MassTransit over RabbitMQ with the transactional outbox/inbox in the app
database and the Goldpath filters (tenant + correlation headers on every message). Every
signal below is real today: MassTransit's own meter (`MassTransit`) reaches the collector
through `Goldpath.ServiceDefaults`, and the outbox is a table you can query.

## "The event never arrived" triage
1. **Did it leave the outbox?** The publish is committed WITH the business row (that is the
   point); delivery is the outbox delivery service's job. A row still in `OutboxMessage`
   after a minute means the delivery loop is not running (host not started, bus stopped) or
   the broker is unreachable — check `/health/ready` and the broker connection before the
   consumer.
   ```sql
   SELECT COUNT(*), MIN("SentTime") FROM "OutboxMessage";   -- backlog + its age
   ```
2. **Did the consumer receive it?** `masstransit_receive` (per endpoint) climbs; if it does
   not, the queue binding is wrong — `ConfigureGoldpathEndpoints` names queues after the
   consumer (kebab-case, minus "Consumer"); a renamed consumer is a NEW queue and the old
   one keeps the old messages.
3. **Did the consumer fault?** `masstransit_consume_fault` + the `_error` queue in the broker.
   The inbox dedups by MessageId, so a redelivered message that faulted again is NOT a
   duplicate — it is the same failure; fix the consumer, then move the message back from
   the error queue (shovel) and it applies exactly once.

## Poison messages
A message that faults on every retry lands in `<queue>_error`, provided the delayed stage can run: with a
redelivery ladder configured on a broker without the delayed-message exchange plugin, the redelivery is refused
and the message stays on its own queue (open-threads T52), so clear the ladder there. Inspect it there (headers
carry `X-Goldpath-Tenant` and the correlation id — the trace is one search away), fix or
park, then shovel it back. Never purge an error queue in production without exporting it:
it is the only copy of what the producer promised.

## Outbox backlog
- Backlog rising, broker healthy → the delivery service is starved (one host, many
  messages) or a consumer is slow and the broker is flow-controlling. Scale the host that
  owns the outbox, not the consumers.
- Backlog rising, broker down → expected; the outbox IS the buffer. Alarm on age, not count.
- Backlog flat, `masstransit_publish` zero → nothing is publishing; check that handlers call
  `IIntegrationEventPublisher` (GP0404 makes direct `IPublishEndpoint` injection an error).

## An outbox row marked failed
`goldpath.messaging.outbox.oversized{stage="outbox"}` rose, or the log carries "Outbox message … is N bytes, above
Goldpath:Messaging:MaxMessageBytes". A committed row was larger than the broker accepts. RabbitMQ refuses it on every
attempt (`PRECONDITION_FAILED - message size … is larger than configured max size`), so the sweep marked it failed
in place, and the rows behind it moved on (#458). Nothing was sent, and nothing is lost: the row is still in the table.
```sql
SELECT "SequenceNumber", "MessageId", "MessageType", octet_length("Body") AS bytes, "Headers"
FROM "OutboxMessage" WHERE "Headers" LIKE '%Goldpath-Outbox-Failed%';
```
The SQL is PostgreSQL's. On SQL Server, `LEN("Body")` gives characters, a lower bound of the bytes, and the header edit is `JSON_MODIFY`.
- **Decide first, with its producer.** The fix is on the producer's side: a body that size should be a reference
  (a claim check) or several events. Usually the right recovery is to republish the corrected event from the
  producer, then delete the marked row.
- **Or deliver it as it is,** if the broker can be given a larger `max_message_size`: raise the broker's limit AND
  `Goldpath:Messaging:MaxMessageBytes` above the row's `bytes`, restore the outbox it came from
  (`Goldpath-Outbox-Failed-OutboxId` in its headers), and remove the three `Goldpath-Outbox-Failed*` headers:
  ```sql
  INSERT INTO "OutboxState" ("OutboxId", "LockId", "Created")
  VALUES ('<Goldpath-Outbox-Failed-OutboxId>', gen_random_uuid(), now() AT TIME ZONE 'utc') ON CONFLICT DO NOTHING;
  UPDATE "OutboxMessage" SET "OutboxId" = '<Goldpath-Outbox-Failed-OutboxId>',
         "Headers" = ("Headers"::jsonb - 'Goldpath-Outbox-Failed' - 'Goldpath-Outbox-Failed-OutboxId' - 'Goldpath-Outbox-Failed-Inbox')::text
  WHERE "SequenceNumber" = <n>;
  ```
  The delivery loop takes it on its next pass. A row a consumer published (`Goldpath-Outbox-Failed-Inbox`) was
  part of that consumer's inbox delivery, which has finished. Republish it from the producer rather than reattach it.
- `stage="publish"` counts refusals at the seam: an `InvalidOperationException` reached the caller, and nothing was
  committed. There is nothing to recover, only a producer to fix.

## Redelivery storms
`masstransit_consume_retry` climbing on one endpoint = one consumer failing repeatedly under
retry. The retry policy is the floor's (immediate + delayed); a storm means a DOWNSTREAM
dependency is down — pause the endpoint (RabbitMQ management → consumer cancel, or scale
the consumer host to zero) rather than letting retries hammer the dependency.

## Tenant on the wire
Every published message carries `X-Goldpath-Tenant` from the ambient tenant, and every
consumer restores it. A consumer that writes tenant-scoped rows without a tenant header
trips the write guard (`goldpath_tenant_write_guard_trips_total`, the tenancy board) —
the fix is on the PRODUCER side (publish inside a tenant scope), never a guard bypass.

## Dashboard
`grafana-messaging-dashboard.json` — receive/consume/fault rates per endpoint, consume
duration p95, retries, publish rate. The outbox backlog has NO meter (it is a table): the
SQL above is the panel until the exit RFC's conformance suite adds one (ADR-0013 §3). The one outbox meter Goldpath
emits is `goldpath.messaging.outbox.oversized` (meter `Goldpath.Messaging`, #458): a body above the size limit,
refused at publish or marked failed in place ("An outbox row marked failed" above).

## Exit posture
The bus is a swappable engine behind a Goldpath-owned seam (ADR-0013). Application code
publishes through `IIntegrationEventPublisher`; consumers still name MassTransit types
until the consume seam ships (September 2026). Nothing in this runbook changes then —
the meters do (the conformance suite pins the signals a bus must emit).
