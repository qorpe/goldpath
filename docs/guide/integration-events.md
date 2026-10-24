# Integration events the packages publish

Every event a Goldpath package puts on the bus, in one place — the wire contract an
adopter's consumers bind to. Package events live IN their package (the record IS the
contract; MassTransit matches by type identity), so a consumer references the package
and the record, never a copy. Events an ADOPTER'S app publishes across processes follow
the [event-contracts decision](../rfc/goldpath-event-contracts.md) instead — a
`<Name>.Contracts` library, earned by the first cross-process consumer.

Every publish below rides the messaging seam (`IIntegrationEventPublisher`): with the
outbox composed it leaves in the SAME transaction as the write; without a broker the
seam is absent and the engine stays silent — no event is ever "best effort".

## Goldpath.Approvals — the authority ladder speaks

| Event | Fields | Fires when |
|---|---|---|
| `GoldpathApprovalRequested` | `ApprovalId, Ladder, Subject, Amount, PendingRole` | A request enters the ladder (`RequestAsync`) — and AGAIN on resubmit after a rejection (`ResubmitAsync`, the chain carries `SupersedesId` on the row, not on the wire). |
| `GoldpathApprovalGranted` | `ApprovalId, Ladder, Subject, DecidedBy` | The rung's quorum is complete — the request is decided in favour. `DecidedBy` is the LAST signature; the full signature list stays on the trail. |
| `GoldpathApprovalRejected` | `ApprovalId, Ladder, Subject, DecidedBy, Reason` | Any rung says no. `Reason` is mandatory by engine rule (`ReasonRequired`) — a consumer may rely on it being non-empty. |
| `GoldpathApprovalEscalated` | `ApprovalId, Ladder, Subject, FromRole, ToRole` | The escalation sweep (`AddGoldpathApprovalsJobs`, 5-minute cadence) lifts an overdue request one rung up. |
| `GoldpathApprovalExpired` | `ApprovalId, Ladder, Subject, AtRole` | The sweep finds the TOP rung overdue — nowhere left to escalate; the request expires. |

Withdrawals publish nothing: the initiator took the request back before any decision,
and downstream systems that reacted to `Requested` learn nothing they must undo.

## Goldpath.FileExchange — one rail run, four moments

| Event | Fields | Fires when |
|---|---|---|
| `GoldpathFileRejected` | `Rail, File, Reason` | The FILE-level contract refused the whole file (truncation, trailer mismatch). Nothing was ingested; no other event follows for this file. |
| `GoldpathFileReceived` | `Rail, File, DataRows` | The file passed its contract and ingestion starts. `DataRows` counts lines after the declared header. |
| `GoldpathRowsQuarantined` | `Rail, File, Count` | At least one row quarantined during the run — published ONCE per run, after the loop; the batch did NOT stop (that is the point). Per-row reasons live in the ledger, not on the wire. |
| `GoldpathFileIngested` | `Rail, File, Processed, SkippedAsDuplicate, Quarantined` | The run finished. A replay of the same file publishes this again with `Processed: 0, SkippedAsDuplicate: n` — consumers treat it as a receipt, never as "new rows". |

## Goldpath.Campaign — delivery plumbing, not a second source of truth

| Event | Fields | Fires when |
|---|---|---|
| `GoldpathCampaignItemMessage` | `CampaignId, Seq, Type` | The pacer RELEASES an item: coordinates only — the target payload stays in the durable item row (a message is delivery plumbing). Competing consumers claim-before-execute; one of them runs the item. |
| `GoldpathCampaignOutcomeMessage` | `CampaignId, Seq, Succeeded, Error` | The executing consumer reports back — success, or the handler's failure with its error text. The batching outcome sink folds these into the campaign's counters and the repair queue. |

These two are the campaign's INTERNAL rails: an adopter's app composes their handlers
(`messaging.AddGoldpathCampaign<TContext>()`, or `bus.AddGoldpathCampaignConsumers<TContext>()` on the
MassTransit-typed block until G1 Part 4/4) and never publishes them by hand.

## The other packages

Jobs, Archival, Bulk, Notification, Caching, Idempotency, Locking, MultiTenancy,
AuditTrail, SoftDelete, DataProtection, Auth publish NO integration events: their
outcomes are rows, metrics and admin verbs (the frozen admin contract), by design — a
scheduler tick or an archived row is state, not news.

## Consuming

An adopter handles an event through the consume seam — `IIntegrationEventHandler<TEvent>`
registered with `bus.AddGoldpathHandler<TEvent, THandler>()` (or `AddGoldpathHandlers`
over an assembly). The handler receives the event and an `IntegrationEventContext`
(message id, correlation, tenant, retry attempt, headers) and names no bus type; its queue
is named after it exactly as a consumer's would be, so the wire is the same (ADR-0013;
messaging-exit RFC §5). The packages' own consumers (campaign) stay on the library — they
ARE the engine's side of the seam.

## Keeping this true

The catalog is verified against the source: every `IIntegrationEvent` record under
`packages/` appears above with its fields (`docs-freshness.sh` counts them). Adding an
event to a package means adding its row here in the same change.
