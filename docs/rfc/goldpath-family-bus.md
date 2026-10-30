# RFC: the Goldpath-family bus — scope, wire contract, clean-room rule and the path into Goldpath

- Status: **PROPOSED** (2026-10-30). Merging the pull request that adds this file is the owner's
  acceptance of §9's answers; the text is written to the recommendations
- Date: 2026-10-30 (written in November's place on the owner's 2026-10-24 decision on #352, so that the
  owner's review has December to itself)
- Composes with [ADR-0013](../adr/ADR-0013-messaging-bus-is-a-swappable-engine.md) §2 and §4 (its own
  qorpe repository, clean-room, MassTransit 8.x as the declared behaviour reference),
  [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) (S1–S6 bind it; opt-in in a 1.x
  minor; default only in 2.0), [ADR-0012](../adr/ADR-0012-goldpath-is-a-platform.md) (bound as a
  published package, never as source), [goldpath-messaging-exit](goldpath-messaging-exit.md) §4 option
  F(3) and §7.1 (the switch proofs, defined there and not restated here) and
  [goldpath-messaging-engines](goldpath-messaging-engines.md) (the adapter contract it implements and
  the wire it speaks, §4.4)
- Ledger: open thread T40 (`../strategy/open-threads.md`); issue
  [#352](https://github.com/qorpe/goldpath/issues/352); the repository itself is
  [#353](https://github.com/qorpe/goldpath/issues/353), an owner action

This file is Goldpath's **composition record** for the family bus: what Goldpath needs from it, the
contract it must keep on the wire, and how it enters this repository. The library's own design —
its internal types, its pipeline, its transport code — lives in its own repository once that exists
(ADR-0013 §2), the way specdrift's and Mediant's do.

## 1. Scope / Non-Goals

**Scope — exactly messaging-exit §4 F(3), and nothing beside it.** The family bus is a message bus
library for .NET, scoped to what Goldpath consumes:

| # | Capability | Why Goldpath needs it | The behaviour reference |
|---|---|---|---|
| C1 | **RabbitMQ transport** | The one broker every template, CorPay and api-portal composes (`builder.AddRabbitMQ("messaging")` in each AppHost) | MassTransit 8.x's RabbitMQ transport, observed under the conformance suite |
| C2 | **Envelope serialisation, wire-compatible with the 8.x envelope and topology (S4)** | A mixed fleet during a rolling deployment, in both directions, with no drained cutover (ADR-0015 §2) | The 8.x JSON envelope and RabbitMQ topology as messaging-engines §4.4 specifies them |
| C3 | **Consumer pipeline with retry and an error queue** | Immediate retry, delayed redelivery, and a poison message parked in `<queue>_error` with its envelope intact (S2.3, S4.5) | 8.x retry and redelivery as observed, `MT-Redelivery-Count`, the `_error` / `_skipped` queues |
| C4 | **EF Core outbox and inbox, on PostgreSQL and SQL Server** | Publish atomic with the caller's `SaveChanges`, relay after a crash, exactly-once handling (S2.1, S2.6, S2.19, S4.2–S4.4) — on both database providers the templates compose with a broker (`outbox.UseSqlServer()` under `--db sqlserver`, the PostgreSQL form otherwise), because ADR-0015 §2's zero adopter impact holds for every adopter, not only the PostgreSQL ones | The 8.x tables (`OutboxMessage`, `OutboxState`, `InboxState`) as messaging-engines §4.4 adopts them |
| C5 | **Tenant and correlation headers** | `X-Goldpath-Tenant` and `X-Correlation-Id` as plain envelope `headers` (S2.2) | `GoldpathHeaders`; nothing of 8.x's own |
| C6 | **OpenTelemetry** | One trace from publish to consume, across engines, with `goldpath.tenant_id` and `goldpath.correlation_id` (S2.16) | 8.x's `MT-Activity-Id` header for the parent context |
| C7 | **In-memory test transport** | `GoldpathMessagingTestHost`'s default mode (messaging-engines §4.3) | The 8.x in-memory transport's observable behaviour |
| C8 | **Aspire wiring** | The connection name `messaging`, a health check, and resilient start-up the AppHost already expects | `Aspire.RabbitMQ.Client` conventions, composed rather than rewritten (ADR-0003) |

**Non-goals.**
- **No sagas, state machines, routing slips, request/response clients, schedulers as a product, or
  other transports** (Azure Service Bus, Amazon SQS, Kafka). Goldpath consumes none of them;
  messaging-exit §4 F(3) excludes them by name. A request for one is a new RFC, not a slice.
- **No library code in this repository and no code in this pull request.** This is the composition
  record; the code lives in the library's repository (§2) and in one adapter package here (§2, §4).
- **No default flip.** The family bus reaches 1.x adopters as an **opt-in** in a minor release and
  becomes the default only in **2.0** (ADR-0015 §3). MassTransit 8.x stays the default for the whole
  1.x line; Wolverine stays the 1.0 insurance (GA gate G5, T18).
- **Not on the 1.0 critical path.** Nothing in GA readiness waits on it (T40); the G1→G5 chain keeps
  priority over every slice below.
- **Not Mediant's job.** In-process notifications stay Mediant notifications (GP0401/GP0402); the
  bus carries integration events only, through the seam.

## 2. Seam Map — where the pieces live

| Where | What | Licence |
|---|---|---|
| **The library's repository** (proposed `qorpe/bus`, §9 Q1) | The bus: `Qorpe.Bus` (envelope, pipeline, retry, the engine-neutral consumer model), `Qorpe.Bus.RabbitMq` (C1, C2's topology), `Qorpe.Bus.EntityFrameworkCore` (C4), `Qorpe.Bus.Testing` (C7), `Qorpe.Bus.Aspire` (C8). Its own CI, mutation gate (break 70, messaging-exit §7.1 S5), licence gate, README with the clean-room statement (§2.1) | Apache-2.0 (§9 Q2) |
| **This repository** | `Goldpath.Messaging.QorpeBus` (proposed, §9 Q3): the adapter. It implements `IGoldpathMessagingEngine` and declares `[assembly: GoldpathMessagingEngine(typeof(…))]` exactly as the MassTransit and Wolverine adapters do (messaging-engines §2, §4.1 row 5). It references the library **as published, pinned packages** (ADR-0012), never as source or a submodule | Apache-2.0 with the rest of Goldpath |
| `Goldpath.Messaging` | Unchanged. The seam, the builder, the outbox entity mapping (`AddGoldpathOutboxEntities()`, the 8.x tables) and the harness vocabulary are already engine-free | — |
| Application code | Nothing. Selecting the family bus is a manifest value plus a package reference (S1) | — |

The library does **not** reference Goldpath. Its consumer model, headers and options are its own;
the adapter maps the seam onto them. That keeps it a library another .NET team could use without
Goldpath, which is what an open-source qorpe repository is for (ADR-0013 §2).

### 2.1 The clean-room statement (for the library's README)

> **Clean-room.** This library is written from scratch. Its behaviour reference is MassTransit 8.x,
> licensed Apache-2.0: its public documentation, its public issue history, and its observable
> behaviour — the messages, headers, exchanges, queues and database rows it produces — as recorded
> by the Goldpath bus conformance suite against a pinned 8.x release. No source code of MassTransit
> is copied, translated or derived from. **The commercially licensed MassTransit v9 source has never
> been consulted** and is never to be consulted by a contributor to this repository. Wire
> compatibility with the MassTransit 8.x envelope and RabbitMQ topology is a deliberate
> interoperability goal, specified from observed traffic, so that a fleet can run both during a
> rolling deployment. MassTransit is a trademark of its owners; this project is not affiliated with
> or endorsed by them.

Two working rules make the statement checkable rather than a promise:
- **The wire is specified from captures, not from code.** Slice FB-1 records the 8.x traffic the
  conformance harness already produces (envelopes, headers, exchange and queue declarations, the
  three tables' rows) as **fixtures** with the 8.x version that wrote them. The library is written to
  the fixtures.
- **Contributions carry a statement.** The library's `CONTRIBUTING.md` asks every pull request to
  confirm that no MassTransit source (any version) was consulted for it, and the PR template carries
  the checkbox. A finding to the contrary removes the change.

## 3. Manifest Surface

- **Value:** `providers.messagingEngine: qorpebus` (proposed with the package name, §9 Q3), beside
  `masstransit` and — once G5 links its proofs — `wolverine`.
- **When it enters the schema:** in the pull request that links the family bus's S1, S2, S3, S4 and S6
  proofs into T40, exactly as messaging-engines §3 rules for `wolverine`. It is **never** a
  SCHEMA-ONLY value: ADR-0015 §2 says an adapter short of the bar "is not a manifest value".
- **What it drives:** the adapter `PackageReference` that `goldpath new` / `goldpath add` writes, and
  specdrift's drift check. Never a line of C#.
- **Default:** unchanged (`masstransit`) for the whole 1.x line. The default becomes the family bus
  only in 2.0 (ADR-0015 §3), after S5.

## 4. API Surface

**The adapter adds no public API to Goldpath.** `Goldpath.Messaging.QorpeBus` adds no public type: its
assembly attribute names an internal `IGoldpathMessagingEngine` implementation — the same shape as
the Wolverine adapter. `engine-gate.sh` gains the adapter's directory as an
allowed home for the engine's namespace (`Qorpe.Bus.`) and stays at zero everywhere else.

### 4.1 The wire contract the library must keep (S4)

These are **requirements on the library**, taken from messaging-engines §4.4 and the S4 facts already
in the suite. Each is an S4 measurement in both directions against the pinned 8.x (8.5.12 today) and
against Wolverine, because all three speak the same wire:

| # | Requirement | Held by |
|---|---|---|
| W1 | Reads and writes the 8.x JSON envelope (`application/vnd.masstransit+json`): `messageId`, `correlationId`, `conversationId`, `sourceAddress`, `destinationAddress`, `messageType` (`urn:message:<Namespace>:<Type>`, every type the message is published as), `sentTime`, `headers`, `message` | S4.1 both directions; FB-1's captured fixtures |
| W2 | Declares the 8.x RabbitMQ topology and no parallel one: a fanout exchange per message type named `<Namespace>:<Type>`; per endpoint a queue and a same-named exchange bound to the types it consumes; `<queue>_error` and `<queue>_skipped`. Queue names from `GoldpathHandlerEndpoints.NameOf` | S4.1, S4.5; S2.17's exchange assertions |
| W3 | Honours `[GoldpathMessageName]`: a renamed type keeps its exchange and URN on the wire | S2.17 |
| W4 | `X-Goldpath-Tenant` and `X-Correlation-Id` are plain `headers` entries, read and written as such | S2.2, S4.1 |
| W5 | Reads `MT-Redelivery-Count`, so a message redelivered across engines does not restart its ladder; writes it when it redelivers | S2.3 extended to a mixed fleet |
| W6 | A faulted message parked in `<queue>_error` keeps its envelope, message id and headers, so either engine handles it once shovelled back | S4.5 |
| W7 | Outbox: writes `OutboxMessage` rows as the 8.x envelope inside the caller's transaction; relays any pending row whichever engine wrote it, to the row's own type's exchange (the first of the `;`-separated `MessageType`); holds the relay lock per `OutboxState` row (`SELECT … FOR UPDATE` on PostgreSQL, `SELECT … WITH (UPDLOCK, ROWLOCK)` on SQL Server — the statements 8.x's lock statement providers issue) | S2.6, S4.3, on both providers |
| W8 | Inbox: one `InboxState` row per **handler**, keyed (`MessageId`, consumer id) with the consumer id the 8.x adapter consumer derives (`MD5("{consumer}:{message}:{queue}")` as a GUID, SHA-256's first 16 bytes under FIPS), **claimed before it is locked**: the row is inserted so that a concurrent duplicate is absorbed rather than failing the delivery, and only then locked — on PostgreSQL `INSERT … ON CONFLICT DO NOTHING` then `SELECT … FOR UPDATE`; on SQL Server an insert that absorbs the duplicate key (an `INSERT … WHERE NOT EXISTS` under `(UPDLOCK, HOLDLOCK)`, or duplicate-key errors 2601/2627 absorbed — FB-4 chooses by measurement) then `SELECT … WITH (UPDLOCK, ROWLOCK)`; swept by `Delivered` older than the duplicate-detection window | S4.2, S4.4, and S2.19, on both providers — the defect #467 found in the Wolverine adapter, which the family bus is designed not to have |
| W9 | Trace continuity: writes `MT-Activity-Id` (the W3C `traceparent`) into **both** the envelope `headers` and the AMQP message headers, as 8.x does; reads the parent from the AMQP header, falling back to the envelope's; opens its consume span under it. 8.x reads only the AMQP header, which is why an engine that writes only the envelope entry breaks a mixed-fleet trace at an 8.x consumer (measured on #467, T51) | S2.16, and a mixed-fleet S4 trace fact in **both** directions (8.x → family bus and family bus → 8.x): one trace id from publish to consume |

S2.1–S2.12 and S4.1–S4.5 are on `main` today. S2.14–S2.19 and `[GoldpathMessageName]` (W3) arrive with
#467 (G5 Part 2/5), open as this is written. W3, W8's claim and W9 cite those facts, so they bind the
family bus once #467 merges; until then they stand as requirements without a fact.

### 4.2 Delayed redelivery without the delayed-message plugin

8.x schedules delayed redelivery through RabbitMQ's delayed-message exchange plugin. On stock
RabbitMQ that redelivery is refused (`406 … unknown exchange type 'x-delayed-message'`), and the
plugin is unmaintained and cannot run from RabbitMQ 4.3 — measured in #467 (G5 Part 2/5), where
it is recorded as a finding. The family bus therefore **must not depend on the plugin**. Slice FB-3
measures two plugin-free designs on the pinned broker and takes the one that holds W5 and W6 in a
mixed fleet:
- a delay queue per ladder step (`x-message-ttl`) that dead-letters back to the consumer's queue;
- a broker-native delivery delay, if the pinned broker version provides one.

Either way the message returns to the **same** consumer queue with `MT-Redelivery-Count` set, so an
8.x or Wolverine process that receives it counts on rather than restarting. Which design is chosen
is FB-3's measured result, recorded in the library's repository and in T40.

## 5. Analyzer Rules and gates

- **No new Goldpath analyzer.** GP0406 (proposed in messaging-engines §4.2, open in T44) is the ban on
  every engine namespace in application code. The adapter's namespace (`Qorpe.Bus`) joins its list in
  the adapter's pull request, so a family-bus type in application code is flagged exactly as a
  MassTransit or Wolverine type is.
- **`engine-gate.sh`** gains `packages/Goldpath.Messaging.QorpeBus` as the third adapter directory,
  with its selftest planting a `Qorpe.Bus.` type outside it (red) and inside it (green).
- **In the library's repository:** a mutation gate (break 70, messaging-exit §7.1 S5), a licence
  gate (every dependency's licence admitted, the whole tree pinned, no "latest"), and the
  clean-room checkbox (§2.1). Its dependencies are expected to be `RabbitMQ.Client`,
  `Microsoft.EntityFrameworkCore.Relational`, `System.Diagnostics.DiagnosticSource` and the Aspire
  client integration — each pinned and licence-gated before its first use.
  The library takes no database provider package: the adopter's own (`Npgsql.EntityFrameworkCore.PostgreSQL` or `Microsoft.EntityFrameworkCore.SqlServer`)
  is in the graph already, and the library carries the lock and claim statements for both (W7, W8),
  chosen from the provider EF reports, as 8.x's lock statement providers are.

## 6. Ops Package

The adapter ships `ops/` in this repository, as every Goldpath package does:
- **Runbook** rows for the family bus that differ from 8.x: where its delayed redelivery lives
  (§4.2), and how to read its spans and logs. The `_error` shovel procedure is unchanged (W6), so
  the existing runbook line applies to all three engines.
- **The switch, both ways** (S6): the upgrade guide section an adopter follows to opt in (a manifest
  value and a package reference) and to opt back out — no code, no maintenance window.
- **Signals:** the same `goldpath.*` tags and the same queue names as today, so existing dashboards
  keep working across a switch. A signal only the family bus emits is documented in its runbook
  with what an operator does about it.

## 7. Test Plan — S1–S6, in the order they can run

The proofs are messaging-exit §7.1's; this is when each runs for the family bus:

| Proof | Runs | Where |
|---|---|---|
| **S2** conformance | From FB-6, on every adapter pull request: the `BusConformanceTests` class registered with the family-bus engine, unchanged, on real RabbitMQ + PostgreSQL, and the outbox/inbox facts (S2.1, S2.6, S2.19) also on real SQL Server — the harness is PostgreSQL-only today, so FB-6 adds its SQL Server leg, which every engine then runs | this repository's `integration` job |
| **S3** differential | With S2: observable outcomes compared against MassTransit 8.x (8.5.12 today; the pin moves with the oracle) | same |
| **S4** wire | FB-6: `MixedFleetTests` with the pairs (8.x → family bus) and (family bus → 8.x), the outbox/inbox facts (S4.2–S4.4) on both PostgreSQL and SQL Server, and the same pairs with Wolverine once G5 has shipped it; W1–W9 each held by a fact | same |
| **S1** apps compile unchanged | FB-7: every broker-bearing GM shape, CorPay and api-portal built with `messagingEngine: qorpebus` and no source edit | GM matrix + the two reference apps |
| **S6** rollback | FB-7: S1–S4 the other way (family bus → 8.x) | same |
| **S5** shadow lane | After FB-7: **≥4 weeks** nightly on the GM broker shapes, CorPay and api-portal, before the opt-in ships in a 1.x minor | nightly workflow |

The library's own unit and integration tests, its mutation gate and its wire fixtures (FB-1) run in
its repository; this repository runs the proofs that say Goldpath can run on it.

## 8. DoD — the dated slice plan

Dates are targets on T40's written timeline (milestone `family-bus`, 2027-03-31). A month that passes
without its step is a finding at the next train-boundary re-measure, not a quiet slip (T40).

| Slice | When | What lands | Done when |
|---|---|---|---|
| **RFC** | 2026-10-30 written; **December 2026** owner review | This file, PROPOSED | the owner merges it (or answers §9 otherwise and the text changes first) |
| **FB-0** | **December 2026** | The repository (#353, an owner action): licence, README with §2.1, `CONTRIBUTING.md` with the clean-room checkbox, CI with the licence and mutation gates, pinned SDK | an empty solution builds green in its CI |
| **FB-1** | January 2027, weeks 1–2 | The wire captured: 8.x fixtures (envelopes, headers, topology declarations, the three tables' rows) recorded from the conformance harness with their 8.x version, the inbox rows also under `MT_FIPS_ENABLE` so W8's SHA-256 branch is captured too, and the outbox and inbox rows on both PostgreSQL and SQL Server; the envelope reader and writer (W1, W3, W4) | the library round-trips every fixture byte-for-byte where 8.x is deterministic, and field-for-field where it is not (`sentTime`, ids) |
| **FB-2** | January 2027, weeks 2–3 | RabbitMQ transport: the topology (W2), publisher confirms, consumers with concurrency and prefetch limits | a library integration test against a real broker declares exactly the fixtures' topology |
| **FB-3** | January 2027, weeks 3–4 | The pipeline: immediate retry, plugin-free delayed redelivery (§4.2, measured and chosen), the error and skipped queues, `MT-Redelivery-Count` (W5, W6) | the ladder runs on stock RabbitMQ with no plugin, and a parked message keeps its envelope |
| **FB-4** | February 2027, weeks 1–2 | EF outbox and inbox on the 8.x tables (W7, W8), including the claim-before-lock, on PostgreSQL and SQL Server | a library test proves one handling of one message id delivered N times concurrently, on each provider |
| **FB-5** | February 2027, week 2 | Headers, OpenTelemetry (W9), the in-memory transport (C7), Aspire wiring (C8) | the library's own tests for each; the in-memory transport passes the harness's in-memory mode |
| **FB-6** | February 2027, week 3 | `Goldpath.Messaging.QorpeBus` in this repository, binding the published preview packages; the conformance class and `MixedFleetTests` registered with it | S2, S3 and S4 green, linked in T40 |
| **FB-7** | February 2027, week 4 | S1 and S6 across the GM broker shapes, CorPay and api-portal | S1 and S6 linked in T40; the shadow lane starts |
| **S5** | March 2027 (≥4 weeks) | The nightly shadow lane | four green weeks, linked in T40; then the opt-in (`qorpebus` enters the manifest enum) ships in a 1.x minor |
| **2.0** | not before 2.0 | The default flips, with the upgrade guide carrying both directions | ADR-0015 §3 |

- [x] This RFC, PROPOSED and dated, with the 8 sections, the scope exactly as messaging-exit §4 F(3)
      (§1), the clean-room statement and a proposed repository name (§2, §9), the adapter's path into
      this repository (§2–§5, §7), and the dated slice plan with the S5 lane (§8).
- [x] T40 updated to name this RFC and the slice plan.
- [ ] The owner accepts §9 Q1–Q4 by merging.
- [ ] FB-0 … S5 as above, each linked in T40 as it lands.

## 9. Owner decisions this merge accepts

| # | Question | Recommendation |
|---|---|---|
| Q1 | The repository's name | **`qorpe/bus`**, its packages prefixed **`Qorpe.Bus`** (`Qorpe.Bus`, `Qorpe.Bus.RabbitMq`, `Qorpe.Bus.EntityFrameworkCore`, `Qorpe.Bus.Testing`, `Qorpe.Bus.Aspire`). It names what it is, carries no vendor's name, and the organisation prefix cannot collide: `Qorpe.Bus` and `Qorpe.Bus.RabbitMq` are free on nuget.org (checked 2026-10-30). Rejected: a coined product name, because the library's value is being boring and replaceable; any name containing "MassTransit" or "Transit", because of §2.1's non-affiliation |
| Q2 | The library's licence | **Apache-2.0**: the same as its behaviour reference and as Goldpath, with an explicit patent grant an enterprise legal review asks for. Rejected: MIT, which lacks the patent grant |
| Q3 | The adapter package and manifest value | **`Goldpath.Messaging.QorpeBus`** and **`qorpebus`**, following messaging-engines §2's `Goldpath.Messaging.<Engine>` pattern |
| Q4 | Delayed redelivery | **Plugin-free, chosen by measurement in FB-3** (§4.2). Rejected: depending on the delayed-message exchange plugin, which is unmaintained and cannot run from RabbitMQ 4.3 (#467) |
