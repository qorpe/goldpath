# RFC: messaging engines — the engine-free API, the adapter packages, and wire compatibility by design

- Status: **ACCEPTED** (2026-10-22, owner decision; the merge of #361 records it). §9's five
  choices are accepted as recommended — the text is written to them
- Date: 2026-10-21
- Opens GA gate **G1** ([goldpath-ga-readiness](goldpath-ga-readiness.md) §7.1 and §8.1:
  "the engine-adapter RFC revision and the ratcheting PublicAPI engine check" on preview.10)
- Composes with [ADR-0013](../adr/ADR-0013-messaging-bus-is-a-swappable-engine.md) (the seam),
  [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) (more than two adapters,
  zero adopter impact, the default locked for 1.x) and [goldpath-messaging-exit](goldpath-messaging-exit.md)
  (§7.1's switch proofs S1–S6 stay defined there; this RFC does not restate them, it designs
  for them)
- Ledger: open thread T18 (`../strategy/open-threads.md`); issue
  [#321](https://github.com/qorpe/goldpath/issues/321) (G1 Part 1/4)

## 1. Scope / Non-Goals

**Scope.** ADR-0013 says registration, outbox/inbox configuration and the test harness become
Goldpath types, and that the manifest gains an engine choice "the RFC defines". This is that
RFC. It defines:

1. the adapter package names (§2);
2. the manifest's engine choice (§3);
3. the engine-free public API that replaces each of the thirteen shipped signatures naming
   MassTransit, one row per line (§4.1), plus the engine references the PublicAPI files do
   not show (§4.2);
4. the test-harness vocabulary the conformance suite and adopters' tests use (§4.3);
5. how S4 wire compatibility holds **by design**: the envelope and topology on the wire, the
   header mapping, and how outbox rows written under one adapter are delivered by the other,
   with no drained cutover (§4.4);
6. the verifier that ships with it, `scripts/engine-gate.sh` (§5), and the preview.11 upgrade
   guide outline and train-boundary adoption (§8).

**Non-goals.** No API is implemented here (Part 2/4, issue G1). Campaign's move to the seam is
Part 3/4 and the extraction of the MassTransit adapter package is Part 4/4. No Wolverine code
is written (G5). The default engine does not change: MassTransit 8.x is the default for every
preview and for the whole 1.x line (ADR-0015 §3). The family bus has its own RFC (T40).

## 2. Seam Map — packages after G1

| Package | Holds | References an engine? |
|---|---|---|
| `Goldpath.Messaging` | The seam: `IIntegrationEventPublisher`, `IIntegrationEventHandler<T>`, `IntegrationEventContext`, `GoldpathMessageTenantContext`, `GoldpathMessagingOptions`, `GoldpathHandlerEndpoints`, the registration builder (§4.1), the outbox options and entity mapping (§4.1, §4.2), the batch-handler contract (§4.1), the engine contract adapters implement (`IGoldpathMessagingEngine`, §4.1 row 5) | **No.** After Part 4/4 it carries no `PackageReference` to any engine; `engine-gate.sh` keeps its PublicAPI free of engine types |
| `Goldpath.Messaging.MassTransit` | **Proposed name.** Everything that today composes MassTransit: the publish and consume filters (internal), the per-handler adapter consumer (internal), endpoint conventions, retry/redelivery wiring, the EF outbox/inbox composition, the RabbitMQ transport binding, the in-memory test transport | Yes, MassTransit 8.x, pinned (8.5.11 today). The default engine |
| `Goldpath.Messaging.Wolverine` | **Proposed name.** The same contract on Wolverine, pinned. Built under G5; published as selectable only once S1, S2, S3, S4 and S6 are linked in T18 (ADR-0015 §2) | Yes, Wolverine, pinned |
| `Goldpath.Campaign` | Its item consumer and outcome sink, as seam handlers (§4.1 rows 11–13) | **No** |

An application references `Goldpath.Messaging` and **exactly one** adapter package — the one its
manifest selects (ADR-0001: the others do not exist in it). A fourth adapter needs ADR-0015's
bar, not a new ADR, and a row here.

## 3. Manifest Surface

```yaml
providers:
  broker: rabbitmq          # unchanged: rabbitmq | kafka | inmemory | none
  messagingEngine: masstransit
```

- **Key:** `providers.messagingEngine`, solution-wide like `broker` (`providersServiceOverride`
  does not admit it: one fleet, one wire, and S4 is about the fleet).
- **Values:** `masstransit` only, until a second adapter passes ADR-0015's bar. ADR-0015 §2 says
  an adapter short of the bar "is not a manifest value", so **`wolverine` enters the enum in the
  same pull request that links its S1–S4 and S6 proofs into T18**. It is never a SCHEMA-ONLY
  value, so `schema-honesty.sh` gains no row for it. (The issue suggested a SCHEMA-ONLY
  annotation for not-yet-wired values. The ADR is stricter, and the ADR wins.)
- **Default:** `masstransit`, locked for 1.x (ADR-0015 §3). A manifest that omits the key keeps
  today's behaviour byte for byte.
- **Rule:** the key is meaningful only when `broker` is not `none`. A manifest that sets it with
  `broker: none` gets a validation finding rather than silently carrying a dead value. This is a
  schema `if/then` and a corpus row, landing with Part 2.
- **What the key drives:** the adapter `PackageReference` that `goldpath new` / `goldpath add`
  writes into the host projects, and `specdrift`'s drift check that the referenced adapter
  matches the manifest. It never drives a line of C#. That is S1: switching engines is a manifest
  value plus a package reference (ADR-0015 §2).

## 4. API Surface

### 4.1 The thirteen lines, one row each

Line numbers are those of `PublicAPI.Shipped.txt` on 2026-10-20, and they match
`scripts/engine-gate.baseline.txt` line for line.

| # | Shipped line today | Becomes | Where |
|---|---|---|---|
| 1 | `GoldpathConsumeFilter<T>.Probe(MassTransit.ProbeContext!)` (Messaging:4) | An adapter internal. The behaviour it carries (restore the tenant from `X-Goldpath-Tenant`, tag `goldpath.correlation_id` from `X-Correlation-Id`, restore the ambient tenant afterwards) becomes a **written pipeline contract** every adapter implements. Conformance S2.2 proves it on each engine | `Goldpath.Messaging.MassTransit`, `internal` |
| 2 | `GoldpathConsumeFilter<T>.Send(MassTransit.ConsumeContext<T>, MassTransit.IPipe<…>)` (Messaging:5) | Same as row 1; the type stops being public | same |
| 3 | `GoldpathPublishFilter<T>.Probe(MassTransit.ProbeContext!)` (Messaging:21) | An adapter internal. Its behaviour (refuse a non-`IIntegrationEvent` publish at runtime, the GP0401 floor; stamp tenant and correlation) is in the same pipeline contract | `Goldpath.Messaging.MassTransit`, `internal` |
| 4 | `GoldpathPublishFilter<T>.Send(MassTransit.PublishContext<T>, MassTransit.IPipe<…>)` (Messaging:22) | Same as row 3 | same |
| 5 | `AddGoldpathMessaging<TBuilder>(this TBuilder, Action<MassTransit.IBusRegistrationConfigurator>, Action<GoldpathMessagingOptions>?)` (Messaging:23) | `AddGoldpathMessaging<TBuilder>(this TBuilder builder, Action<GoldpathMessagingBuilder> configure) -> TBuilder`. `GoldpathMessagingBuilder` is a Goldpath type with `AddHandler<TEvent, THandler>()`, `AddHandlers(params Assembly[])`, `AddBatchHandler<TEvent, THandler>(GoldpathBatchOptions)`, `AddOutbox<TContext>(Action<GoldpathOutboxOptions>?)`, `UseRabbitMq(string connectionName = "messaging")`, and `Options` (`GoldpathMessagingOptions`, still bound from `Goldpath:Messaging`). The **engine** is found by an assembly attribute, not named: each adapter declares `[assembly: GoldpathMessagingEngine(typeof(…))]`, and `AddGoldpathMessaging` fails at startup if it finds no engine ("reference the adapter your manifest selects") or more than one (it names both). Adapters implement `IGoldpathMessagingEngine`, a Goldpath contract that takes the builder's recorded registrations and composes them | `Goldpath.Messaging` |
| 6 | `AddGoldpathOutbox<TContext>(this MassTransit.IBusRegistrationConfigurator, Action<MassTransit.IEntityFrameworkOutboxConfigurator>?)` (Messaging:24) | `GoldpathMessagingBuilder.AddOutbox<TContext>(Action<GoldpathOutboxOptions>? configure = null) where TContext : DbContext`. `GoldpathOutboxOptions` has `DuplicateDetectionWindow` (default 30 min, as today), `Inbox` (default `true`: the consumer-side dedup is on, as today) and `DeliveryPollInterval`. **The lock provider (`UsePostgres()` / `UseSqlServer()`) is derived from the context's EF provider.** That removes the line the template writes under `#if (UsePostgres)`, and the adopter's provider choice stays in the manifest where it already is | `Goldpath.Messaging` (options), adapter (composition) |
| 7 | `ConfigureGoldpathEndpoints<TEndpoint>(this MassTransit.IBusFactoryConfigurator<TEndpoint>, MassTransit.IBusRegistrationContext)` (Messaging:25) | **Removed without a replacement.** The pipeline (filters, retry, redelivery, endpoint conventions) is applied by the adapter for every endpoint it creates, so the adopter does not have to remember the call. Today's `UsingRabbitMq((ctx, cfg) => { cfg.Host(…); cfg.ConfigureGoldpathEndpoints(ctx); })` block becomes `messaging.UseRabbitMq()` | adapter |
| 8 | `AddGoldpathHandler<TEvent, THandler>(this MassTransit.IBusRegistrationConfigurator)` (Messaging:31) | `GoldpathMessagingBuilder.AddHandler<TEvent, THandler>()`, returning the builder. The queue is named by `GoldpathHandlerEndpoints.NameOf(THandler)` exactly as today (S4: no queue moves) | `Goldpath.Messaging` |
| 9 | `AddGoldpathHandlers(this MassTransit.IBusRegistrationConfigurator, params Assembly[])` (Messaging:32) | `GoldpathMessagingBuilder.AddHandlers(params Assembly[])`, same scan rule | `Goldpath.Messaging` |
| 10 | `GoldpathIntegrationEventConsumer<TEvent, THandler>.Consume(MassTransit.ConsumeContext<TEvent>)` (Messaging:34) | An adapter internal. It was public only because MassTransit constructs it through DI, and no adopter names it | `Goldpath.Messaging.MassTransit`, `internal` |
| 11 | `GoldpathCampaignItemConsumer<TContext>.Consume(MassTransit.ConsumeContext<GoldpathCampaignItemMessage>)` (Campaign:181) | `GoldpathCampaignItemHandler<TContext> : IIntegrationEventHandler<GoldpathCampaignItemMessage>`. The claim-before-execute logic is unchanged and reads the message id from `IntegrationEventContext.MessageId`. **Its queue keeps today's name.** Part 3 reads the name the 8.x formatter gives the generic consumer on a running bus today and registers the handler under exactly that name. It does not trust `NameOf` to land on the same one for a generic type. A mixed fleet drains one queue | `Goldpath.Campaign` |
| 12 | `GoldpathCampaignOutcomeSink<TContext>.Consume(MassTransit.ConsumeContext<MassTransit.Batch<GoldpathCampaignOutcomeMessage>>)` (Campaign:233) | A new seam contract, **`IIntegrationEventBatchHandler<TEvent>`** with `HandleAsync(IReadOnlyList<IntegrationEvent<TEvent>> batch, CancellationToken)`, where `IntegrationEvent<TEvent>` pairs each message with its `IntegrationEventContext`. It is registered with `AddBatchHandler<TEvent, THandler>(new GoldpathBatchOptions { MessageLimit = 200, TimeLimit = 500 ms, ConcurrencyLimit = 1 })`, today's numbers. Semantics both engines must meet: delivery in batches bounded by size **or** time, one batch in flight per endpoint when `ConcurrencyLimit = 1`, and a throw fails the whole batch into retry. Conformance gains a fact for each. `GoldpathCampaignOutcomeSinkDefinition<TContext>` (Campaign:235–236), a `ConsumerDefinition` subclass whose PublicAPI line does not show its MassTransit base type, is **removed** with it (§4.2) | `Goldpath.Messaging` (contract), `Goldpath.Campaign` (handler) |
| 13 | `AddGoldpathCampaignConsumers<TContext>(this MassTransit.IBusRegistrationConfigurator)` (Campaign:296) | `AddGoldpathCampaign<TContext>(this GoldpathMessagingBuilder)`, which registers the item handler and the outcome batch handler | `Goldpath.Campaign` |

After Parts 2–4 the gate's baseline is empty and its ceiling is zero. That is G1's exit
(GA readiness §8.1: "G1 check at zero outside the adapter packages").

### 4.2 Engine references the PublicAPI files do not show

The gate reads signatures. Four couplings survive a clean gate, so this RFC names them; each is
closed in the Part that touches it:

| Where | What | Becomes |
|---|---|---|
| `Goldpath.Campaign` | `GoldpathCampaignOutcomeSinkDefinition<TContext> : ConsumerDefinition<…>`. A PublicAPI line records a type's name, not its base type | Removed (row 12). Part 3 adds a reflection test asserting that no public type in a non-adapter package derives from, or implements, an engine type. This is the check the text gate cannot make |
| Every generated app's `DbContext` | `modelBuilder.AddInboxStateEntity(); AddOutboxMessageEntity(); AddOutboxStateEntity();` and `using MassTransit;` (both template layouts, the worker template, CorPay) | `modelBuilder.AddGoldpathOutboxEntities()` in `Goldpath.Messaging`, mapping **the same tables and columns** (§4.4, outbox). No migration is generated for an existing app. The proof is `goldpath db add` producing an empty migration on a GM shape after the swap |
| Generated apps' smoke tests | `Bus.Factory.CreateUsingRabbitMq(…)` to publish "as an upstream service would", with `ctx.Headers.Set(GoldpathHeaders.TenantId, …)` | The harness's external publisher (§4.3) |
| Application code at large | GP0404 bans `IPublishEndpoint` and GP0405 bans `IConsumer<T>`; neither is the namespace ban ADR-0013 §1 asks for. CorPay still carries `using MassTransit;` in six files outside its two `Program.cs` files | **GP0406** (proposed id): any engine namespace (`MassTransit`, `Wolverine`) referenced from application code is a Warning. Info on preview.11, Warning on rc.1, the cadence GP0405 used. It exempts the `Goldpath.Messaging.*` adapter assemblies, and nothing else |

### 4.3 The test-harness vocabulary

The conformance suite and an adopter's tests speak one vocabulary, in `Goldpath.Messaging`.
The engine is again found from the referenced adapter:

| Goldpath type / member | Replaces | Notes |
|---|---|---|
| `GoldpathMessagingTestHost` (`await using var host = await GoldpathMessagingTestHost.StartAsync(builder => …)`) | `AddMassTransitTestHarness`, `ITestHarness`, a hand-built `Bus.Factory` | Starts the configured engine on the **in-memory** transport by default, or on RabbitMQ when given a connection string (the conformance suite's mode) |
| `host.PublishAsync<TEvent>(TEvent, Action<GoldpathOutgoingMessage>? stamp)` | `bus.Publish(msg, ctx => { ctx.MessageId = …; ctx.Headers.Set(…); })` | `GoldpathOutgoingMessage` exposes `MessageId`, `Tenant`, `CorrelationId` and `Headers`: the fields the S2 facts and the smoke tests set |
| `host.Handled<TEvent>()`: `Task<IntegrationEventContext> FirstAsync(TimeSpan)` and `IReadOnlyList<…> All` | `harness.Consumed.Any<T>()` | What a handler received, including its context |
| `host.Faulted<TEvent>()`: `Task<GoldpathFault> FirstAsync(TimeSpan)` | `IConsumer<Fault<T>>` in the suite (`PoisonFaultConsumer`) | Proves a poison message left through the pipeline's error door, whatever each engine calls it |
| `host.Published<TEvent>()` | `harness.Published.Any<T>()` | For the outbox facts |

The suite's `PoisonFaultConsumer : IConsumer<Fault<Poison>>` and `ConformanceDb`'s three
`modelBuilder.Add…Entity()` calls are its last engine references. With them gone, a second
engine runs the class unchanged, which messaging-exit §7 already says S3 needs ("register a
second engine's adapter in the harness and run the class").

### 4.4 S4 — wire compatibility by design

S4 is mandatory (ADR-0015 §2). Wire compatibility therefore has to be designed in now. Testing
it after an adapter exists would be too late. A mixed fleet means process A on one engine and
process B on the other, sharing a broker and a database, during a rolling deployment, in both
directions.

**The envelope is the MassTransit 8.x JSON envelope** (`application/vnd.masstransit+json`):
`messageId`, `correlationId`, `conversationId`, `sourceAddress`, `destinationAddress`,
`messageType` (`urn:message:<Namespace>:<Type>`), `sentTime`, `headers`, `message`. This is the
envelope every deployed Goldpath fleet carries today, so making it the contract strands no
in-flight message. Every adapter **writes** it and **reads** it. An adapter whose engine has a
native envelope of its own still serialises this one on the RabbitMQ transport. Wolverine
documents a MassTransit interoperability mode for RabbitMQ endpoints. Whether that mode carries
every field and header above is the first S4 measurement under G5, not an assumption this RFC
makes.

**The topology is the 8.x RabbitMQ topology.** There is one fanout exchange per message type,
named `<Namespace>:<Type>`. Each endpoint has a queue plus an exchange of the same name, bound to
the message-type exchanges it consumes. Queue names come from `GoldpathHandlerEndpoints.NameOf`,
which is already engine-free and public. Error and skipped queues are `<queue>_error` and
`<queue>_skipped`. An adapter declares this topology. It never declares a parallel one that the
other engine cannot see.

**Headers.** Tenant travels as `X-Goldpath-Tenant` and correlation as `X-Correlation-Id`
(`GoldpathHeaders`). Both are plain envelope `headers` entries, never an engine-specific
property. The retry attempt is **not** a wire header that adapters exchange: each engine counts
its own attempts for a delivery. The 8.x redelivery header (`MT-Redelivery-Count`) is read by
every adapter, so a message redelivered across engines does not reset its ladder. The message
id is the envelope's `messageId`, the inbox's dedup key on both sides.

**The outbox and inbox: one schema on disk, the 8.x one.** The tables already in every
adopter's migrations (`OutboxMessage`, `OutboxState`, `InboxState`, with their 8.x columns)
become **the Goldpath outbox schema**. `AddGoldpathOutboxEntities()` maps exactly them (§4.2).
Every adapter:

- **writes** its outgoing messages into `OutboxMessage` as the 8.x envelope, inside the caller's
  `SaveChanges` transaction (the atomicity proof, messaging-exit §7 item 1);
- **relays** any undelivered `OutboxMessage` row, whichever engine wrote it. A row written by
  process A and left pending when A stopped is delivered by process B on the other engine. That
  is S4's "outbox rows written under the old adapter delivered by the new", with no drain;
- **deduplicates** against `InboxState` keyed by (`MessageId`, consumer endpoint). A message
  delivered once under one engine and redelivered under the other is not handled twice;
- holds the relay lock per `OutboxState` row the same way, so two engines never deliver one
  row twice.

The rejected alternatives: a **new Goldpath-owned schema** would make every adopter migrate the
day it ships, which is adopter impact, and would cost a cutover from the 8.x tables anyway. A
**per-pair relay of the other engine's native tables** grows quadratically with adapters, and S6
needs every pair both ways. Choosing the 8.x schema means adopting schema and wire together:
each new adapter speaks what the default already writes. §9 Q4 records the choice for the owner.

**Rollback (S6)** is the same design read backwards. MassTransit reads the envelope, topology,
headers and tables a second adapter wrote, because they are its own.

## 5. Analyzer Rules and gates

- **`scripts/engine-gate.sh`** (this RFC's verifier, ADR-0005; it ships in the same pull request).
  It fails when a `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt` outside
  `packages/Goldpath.Messaging.MassTransit` and `packages/Goldpath.Messaging.Wolverine` contains
  an engine type (`MassTransit.` or `Wolverine.`), except a line held in
  `scripts/engine-gate.baseline.txt`. That baseline holds exactly today's thirteen lines. The
  baseline **only shrinks**: a baseline line missing from its file fails ("remove it from the
  baseline"), and a baseline longer than the ceiling (13, lowered as Parts 2–3 land, never
  raised) fails. Removals (`*REMOVED*` lines) are the ratchet working and never fail.
  `--selftest` plants five faults in a scratch copy and proves each one red: a 14th line, a
  Wolverine type, a deleted-but-baselined line, a padded baseline, and a baselined signature
  re-declared in a second file. It also proves the gate green on the real tree and on an engine
  type inside its adapter package. Both modes run in `ci.yml`'s `gates` job. The script and its
  baseline match `.github/merge-tiers.yml`'s Tier C rule `scripts/*gate*`, so loosening either
  one is the owner's merge.
- **GP0406** (proposed, §4.2): the engine-namespace ban in application code, which ADR-0013 §1
  requires and GP0404/GP0405 only approximate.
- **The reflection check** (proposed, §4.2): no public type outside an adapter package derives
  from, or implements, an engine type.

## 6. Ops Package

No runtime behaviour changes until Part 2. When it does, the adapter's ops notes move from
`packages/Goldpath.Messaging/ops/` into the adapter package unchanged. Adopters reach them
through the same runbook links. SECURITY.md's engine posture is G5's to write, not this RFC's.

## 7. Test Plan

- **This PR:** `engine-gate.sh --selftest` (five planted faults red, two clean trees green) and
  `engine-gate.sh` on the real tree (green at 13 of 13). The red run on CI is linked from the
  pull request, as #321's proof asks.
- **Part 2 (API):** every existing conformance fact (S2.1–S2.8) passes unchanged through the new
  registration on MassTransit. Each GM broker shape regenerates with the new `Program.cs` block
  and `goldpath db add` yields an **empty** migration, which proves §4.2's table mapping. The
  baseline drops to 3.
- **Part 3 (Campaign):** three new conformance facts for the batch contract (size bound, time
  bound, one in flight) pass on MassTransit. Campaign's existing integration tests pass through
  the seam. The baseline drops to 0, and so does the ceiling.
- **Part 4 (extraction):** `Goldpath.Messaging` has no engine `PackageReference`. The reflection
  check is green. CorPay and api-portal compile **unchanged** against the extracted adapter. That
  is S1 for the default engine, the precondition for running S1 against any other.
- **G5 (Wolverine):** S1–S4 and S6 per messaging-exit §7.1. §4.4's design gives S4 three
  measurable claims, each a conformance fact run in a two-process mixed fleet in both
  directions: envelope round-trip, a pending outbox row relayed across engines, and inbox dedup
  across engines.
- **Golden-manifest impact:** none in this PR. Part 2 changes every broker-bearing shape's
  `Program.cs` and `DbContext`, and the GM matrix proves them (ADR-0008).

## 8. DoD

- [x] This RFC, PROPOSED and dated, with one row per shipped line (§4.1) and the couplings the
      gate cannot see (§4.2).
- [x] `scripts/engine-gate.sh` + baseline, run in CI's `gates` job (self-test and real tree).
- [ ] Owner accepts §9 Q1–Q5 by merging (or answers otherwise, and this text changes first).
- [ ] Part 2/4: the registration builder, the outbox options and entity mapping, the harness,
      and GP0406 as Info. Baseline 13 → 3. **Landed additively (#327):** the builder, the outbox
      options and entity mapping, and the test host's entry point and publish; the conformance
      suite and the outbox and tenant-square integration tests use only them. Issue #327 keeps the
      MassTransit-typed overloads until Part 4/4, so the baseline stays at 13 until then. GP0406,
      the `providers.messagingEngine` key, engine discovery by attribute with a public
      `IGoldpathMessagingEngine`, `ConfigureEngine`, `AddBatchHandler` (Part 3) and the harness's
      `Handled`/`Faulted`/`Published` are open-threads T44.
- [x] Part 3/4: Campaign through the seam, with the batch contract. **Landed (#328):**
      `IIntegrationEventBatchHandler<TEvent>`, `IntegrationEvent<TEvent>`, `GoldpathBatchOptions` and
      `AddBatchHandler`; conformance facts S2.9–S2.11 (size bound, time bound, one in flight);
      `AddGoldpathCampaign<TContext>(this GoldpathMessagingBuilder)` with the item and outcome handlers;
      the §4.2 reflection check over Campaign's public surface. Row 11's "registers the handler under
      exactly that name" is carried by `AddHandler<TEvent, THandler>(endpointName)` and `AddBatchHandler`'s
      `endpointName`: both handlers share one queue named the way the 8.x kebab formatter names a
      consumer generic over the context, proven against the formatter for a corpus of context names and
      on a real broker. The two consumer lines left; `AddGoldpathCampaignConsumers` stays, now registering
      the same handlers, because the templates write it until the preview.11 release and the GM matrix
      builds them against this repository's packages — the reason Part 2 kept Messaging's old overloads
      (#327). So the baseline went 13 → 11, not 3 → 0; Part 4/4 takes it to 0.
- [ ] Part 4/4: `Goldpath.Messaging.MassTransit` extracted; `Goldpath.Messaging` engine-free.
- [x] messaging-exit §8's open line ("the outbox/inbox configuration and the test harness in
      Goldpath vocabulary") is ticked by Part 2 (#327), pointing here.

**The preview.11 upgrade guide, outlined** (`docs/upgrades/0.1.0-preview.11.md`, written by the
release PR):

1. *Messaging registration*: the `AddGoldpathMessaging(bus => …)` block before and after, plus
   one `PackageReference` added (`Goldpath.Messaging.MassTransit`). Every generated shape's diff
   is shown verbatim.
2. *Outbox entities*: three `modelBuilder.Add…Entity()` lines become
   `AddGoldpathOutboxEntities()`. **No migration**, and the guide says how to prove it
   (`goldpath db add` yields an empty one).
3. *Campaign*: `AddGoldpathCampaignConsumers<T>()` becomes `AddGoldpathCampaign<T>()`. Queue
   names are unchanged, so the rollout can be rolling.
4. *Tests*: the harness swap, with the smoke test's raw-bus publish shown before and after.
5. *What does not move*: command handlers (`IIntegrationEventPublisher`), event handlers
   (`IIntegrationEventHandler<T>`), queue names, the wire, the tables. This is the sentence an
   operator plans a rolling deployment on.

**Train-boundary adoption.** Templates consume published API only (`scripts/template-pins.sh`).
The two solution layouts, the worker template and the CLI's worker skeleton move to the new
block in the **release PR of preview.11**, not when Part 2 lands. CorPay moves when it takes
preview.11. `docs/ops/release-checklist.md` carries these lines from Part 2 onwards, the same
way GP0405's adoption was carried.

## 9. Owner decisions this merge accepts

| # | Question | Recommendation (the text above is written to it) |
|---|---|---|
| Q1 | Adapter package names | `Goldpath.Messaging.MassTransit`, `Goldpath.Messaging.Wolverine`, as ADR-0015 §1 proposes |
| Q2 | How the engine is selected without application code | An assembly attribute in the adapter package, discovered by `AddGoldpathMessaging`, with startup failure on zero or several. The manifest drives the `PackageReference`. Rejected: an engine-named call in `Program.cs`, because a switch would then edit code, which breaks S1 |
| Q3 | Is there an escape hatch for engine-specific tuning? | **Yes, in the adapter package only**: `GoldpathMessagingBuilder.ConfigureEngine<TEngineConfigurator>(Action<…>)`, whose use GP0406 flags so the adopter sees the line that would move on a switch. With no hatch, an enterprise adopter who needs a prefetch number has no way through except dropping the floor |
| Q4 | Which schema does the outbox speak across adapters? | The 8.x tables as the Goldpath outbox schema (§4.4). This means no migration for any adopter and one relay design for N engines |
| Q5 | The manifest key | `providers.messagingEngine`, solution-wide, `masstransit` only until an adapter passes the bar (§3) |
