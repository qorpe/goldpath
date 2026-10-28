# RFC: the messaging dependency — exposure, and the exit that is not thin

- Status: **ACCEPTED** — option **A** (stay pinned on the 8.x line), owner-decided 2026-08-10,
  with **D** as the named fallback and the triggers below as the only things that move us
- Date: 2026-08-09 (decided and §5 implemented 2026-08-10)
- Amended 2026-10-21 by owner decision, recorded with the acceptance of
  [goldpath-ga-readiness](goldpath-ga-readiness.md) §8.3 (Q1, Q2): **S4 wire compatibility is
  mandatory** for any engine switch (the drained-cutover escape is removed); **Wolverine** (D) is
  the 1.0 insurance and must pass S1–S4 and S6 by 2026-12-31 (GA gate G5); the **family bus**
  is committed on a written timeline (§7.1, T40) and becomes the default only in 2.0. The
  constitution records it as [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md),
  a scoped amendment of ADR-0013
- Supersedes the recommendation in [goldpath-messaging](goldpath-messaging.md) §9 D1
- Ledger: issue [#11](https://github.com/qorpe/goldpath/issues/11) (owner-prioritized),
  master plan "Open, with an owner" item 10

## 1. Scope / Non-Goals

**Scope.** Decide how Goldpath responds to MassTransit's licence change, with the real
exposure measured rather than assumed. Define what an exit would cost, what would have to
be true before we take one, and what we do NOW so the cost stops growing.

**Non-goals.** This RFC **moves no dependency**: MassTransit 8.5.10 stays pinned exactly
where it was. It does not choose a replacement library — that is a second RFC, opened only
if a trigger below fires — and it does not change the manifest.

*(As drafted this section also said the RFC ships no code. §5 was then decided and
implemented, so it ships exactly one thing: the publish seam. Corrected here rather than
left to read as a promise we broke.)*

## 2. Seam Map — where MassTransit actually is (measured 2026-08-09)

§9 D1 of the messaging RFC recorded a comforting claim:

> "the Goldpath surface (`AddGoldpathMessaging`, filters, conventions) is our own thin
> layer, so a future move … changes the composition, **not consumer code**."

**That claim is false, and it is the most important thing in this RFC.** Three greps:

| Surface | What is there | Why it matters |
|---|---|---|
| **The template** — every generated app | `CreateOrderHandler(IOrdersDbContext db, IPublishEndpoint publisher)`; `OrderPlacedConsumer : IConsumer<OrderPlaced>` with `Consume(ConsumeContext<OrderPlaced>)` | This IS consumer code. A generated app's own handlers name MassTransit types on day one. |
| **Our frozen public API** | `PublicAPI.Shipped.txt` of `Goldpath.Campaign` exposes `Consume(MassTransit.ConsumeContext<…>)` and `AddGoldpathCampaignConsumers(this MassTransit.IBusRegistrationConfigurator)`; `Goldpath.Messaging` exposes `GoldpathConsumeFilter<T>.Send(MassTransit.ConsumeContext<T>, MassTransit.IPipe<…>)` | These are SHIPPED public signatures. Changing them is a major version under our own versioning contract, not an internal swap. |
| **The reference app** | 79 MassTransit/`IPublishEndpoint`/`IConsumer` references in `samples/corpay/src` | The proof that the template's shape is what adopters actually write. |

The coupling is not an accident: ADR-0003 says what the ecosystem provides is **composed,
not rewritten**, and composing means naming the library's types. That was the right call and
it has a bill; this RFC is the bill arriving, not the decision being wrong.

**What is genuinely ours** (would survive any move unchanged): the `IIntegrationEvent`
boundary marker, the tenant/correlation header contract, the outbox's atomicity guarantee,
the analyzer rules GP0401-0403, and the manifest's `providers.broker` vocabulary.

## 3. Manifest Surface

Unchanged. `providers.broker` keeps `rabbitmq | kafka | inmemory | none` with today's
honesty annotations (kafka and inmemory are SCHEMA-ONLY). A future transport change is a
composition detail behind the same manifest key — that part of D1's claim IS true.

## 4. API Surface — the options, with what each actually costs

| # | Option | What it costs | When it is right |
|---|---|---|---|
| **A** | **Stay on 8.x, pinned** (today's position: 8.5.10, Apache-2.0) | Nothing now. Accepts that fixes stop when the v8 maintenance window closes (~end 2026 per the vendor's own statements) | Until a trigger fires. **This is the recommendation.** |
| **B** | **Buy v9** | Unknown — no list price is published anywhere, which is itself a procurement finding: an enterprise buyer cannot budget a dependency whose cost they cannot look up | Only if an adopter already holds a licence |
| **C** | **Fork v8** | We become the maintainer of a message bus. Directly against ADR-0003 (do not rewrite what the ecosystem provides) and against our own staffing reality | Never, on today's team |
| **D** | **Move to Wolverine** (MIT; JasperFx has publicly declined to commercialize it) | A MAJOR version: our public API changes, the template's handlers change, every adopter's consumers change. Needs a migration guide and the `upgrade` skill that does not exist yet | If v8 stops being safe to ship |
| **E** | **Grow Mediant toward transport** (issue #11's GO/NO-GO) | The largest: we would own a transport. Same ADR-0003 objection as C unless the scope stays tiny (in-process + one broker) | Only with real usage data showing how little of MassTransit we consume |
| **F** | **Seam + conformance suite now; a Goldpath-family bus as its own open-source library, swapped in only when the suite proves it** (added 2026-09-03) | Three bills, in order. (1) The CONSUME seam — a Goldpath-owned handler contract, registration, outbox/inbox configuration and test harness, plus an analyzer banning the library's namespaces from application code: 3–4 days, and a MAJOR version once (the same major D would cost — paid once, for every future engine). (2) The **bus conformance suite** — 60–100 transport-agnostic scenarios with MassTransit 8.5.10 as the oracle, mined from the library's public issue history and our own edge cases: 1–2 weeks. (3) The library itself, scoped to what Goldpath consumes (RabbitMQ transport, envelope serialisation, consumer pipeline with retry + error queue, EF outbox/inbox, tenant/correlation headers, OTel, in-memory test transport, Aspire wiring — NOT sagas, routing slips or other transports): 4–6 weeks to a first working adapter with Claude Code, then a shadow lane of ≥4 weeks before any default flips. Written clean-room with the 8.x line as the declared behaviour reference (ADR-0013) | **Chosen 2026-09-03.** (1) and (2) are needed by EVERY exit and by option A itself (they make the seam honest); (3) runs beside the sync product, never on the accelerator's critical path, and flips the default only through §7's switch proofs |

**Recommendation as of 2026-08-10: A now, with D as the named fallback** — and the honest
sentence a client must hear is *"a migration would be a major version, not a patch"*,
because the measurement above says so.

**Decision 2026-09-03 — option F, recorded as [ADR-0013](../adr/ADR-0013-messaging-bus-is-a-swappable-engine.md).**
The owner's reasoning: the platform should own its runtime compatibility rather than
track a vendor's licensing calendar, and "as good as the library we replace" must be a
scenario corpus both engines pass, not an opinion. The staged shape is what keeps that
honest — the seam and the suite come first because they are needed whatever the engine
turns out to be; D (Wolverine) stays the measured fallback if the family bus stalls, and
the suite makes trying it a one-week adapter, not a research project. A stays the shipped
position until the switch proofs pass.

## 5. What we do NOW so the bill stops growing

One rule, no code today, and it is deliberately small:

> **New adopter-facing code does not take a NEW hard dependency on the messaging library
> where a Goldpath seam already exists.**

Concretely, the publish side is the cheap half: a handler that needs to publish could depend
on a Goldpath-owned `IIntegrationEventPublisher` instead of `IPublishEndpoint`, and the
consume side (which genuinely needs the library's pipeline) could stay as it is. That single
narrowing would take the template's per-app exposure from "every command handler" to "only
consumers".

**DECIDED and implemented 2026-08-10** — the owner's argument was the one that settles it:
we have **zero adopters today**, so moving the template's publish line costs nothing; with N
adopters it costs N migrations plus a guide plus the `upgrade` skill that does not exist.
The asymmetry is the whole argument.

What shipped:

- `IIntegrationEventPublisher` in `Goldpath.Messaging`, registered by `AddGoldpathMessaging`,
  implemented by a one-line delegation to `IPublishEndpoint`. **Not** a bus abstraction: no
  retry policy, no topology, no routing of our own — ADR-0003 stands. Everything Goldpath
  adds to the publish path already lives in the pipeline filters, where it applies to every
  publish regardless of caller.
- Both template layouts publish through it; `using MassTransit` is gone from the command
  handlers of a generated app.
- **GP0404** (warning) flags application code injecting `IPublishEndpoint`, exempting the
  Goldpath namespace that owns the delegation — the seam has an enforcing mechanism, per
  ADR-0005, so it cannot erode by habit.

**The consume side deliberately stayed on the library's types until 2026-09-05.** A consumer
genuinely uses the pipeline (headers, retry, redelivery, tenant restore); wrapping that was
judged a leaky abstraction — until ADR-0013 made the engine swappable and the seam
necessary. **Shipped 2026-09-05:** `IIntegrationEventHandler<TEvent>` +
`IntegrationEventContext` (message id, correlation, tenant, retry attempt, headers) +
`AddGoldpathHandler`/`AddGoldpathHandlers`, adapted onto ONE library consumer per pair;
the handler's queue is named exactly as the consumer's was (S4: no wire change). Analyzer
GP0405 steers adopters (Info on preview.7; Warning since preview.8, when the templates,
the CLI's worker skeleton and CorPay move their consumers — the published-API rule keeps
them on preview.7 until then; the release checklist carries the lines). The honest promise
is now: *if the engine changes, neither your command handlers nor your handlers move; your
Program.cs transport block does — once, in the upgrade guide.*

**Sequencing, learned by building it:** CorPay binds to the PUBLISHED train, so it cannot
take the seam until preview.7 carries it — the attempt failed to compile against preview.6,
which is the adopter discipline working exactly as designed. CorPay migrates its four publish
sites when it takes the next train, in the same slice, the way it took preview.6.

## 6. Ops Package

None — this RFC changes no runtime behaviour. If an exit ever happens, its ops obligation is
the migration guide plus the `upgrade` skill (`ai-sdlc-status.md` records it as NOT BUILT),
because a major version without a migration path is how an accelerator loses the adopter it
was supposed to accelerate.

## 7. Test Plan — what an exit would have to prove

Recorded now so the cost is not rediscovered later. A move is not done until:

1. The **outbox atomicity** proof passes on the new transport (the existing Testcontainers
   Postgres test, unchanged in intent).
2. The **tenant + correlation headers** still propagate end to end (H4's trace-correlation
   proof).
3. **GP0401-0403** still mean something — the boundary between in-process notifications and
   integration events survives, or the analyzers are rewritten with it.
4. The **golden-manifest matrix** stays green on every broker-bearing shape.
5. CorPay migrates with a written guide, and the guide is what an adopter would follow.

### 7.1 The switch proofs (added 2026-09-03 with option F)

The owner's question was *"will the switch really be a flip, with nothing breaking?"* It is
— **only** if all six of these have run; short of that the honest word is "migration", not
"flip". Recorded so the day the flip is proposed, the bar is already written.

| # | Proof | What it rules out |
|---|---|---|
| S1 | **Seam completeness** — the analyzer bans the library's namespaces in application code; a generated app (every GM broker-bearing shape), CorPay and api-portal compile UNCHANGED against either adapter | a "flip" that is secretly a code change in every consumer |
| S2 | **Conformance suite green on both adapters** — publish/consume, retry + error queue, outbox atomicity INCLUDING crash between commit and publish, inbox de-duplication, tenant/correlation headers, per-queue ordering, concurrency/prefetch, poison messages, broker restart recovery, graceful shutdown drain, large payloads, message versioning (added property, renamed type), OTel spans | the long tail the library's decade of fixes covers and a rewrite forgets |
| S3 | **Differential run** — the same scenarios on both adapters with observable outcomes compared, mock-engine style (oracle = MassTransit 8.5.10) | "passes our tests" meaning "passes tests we wrote around our own behaviour" |
| S4 | **Mixed-fleet wire compatibility — MANDATORY** — old publisher → new consumer and the reverse, on the 8.x envelope and topology; outbox rows written under the old adapter delivered by the new. An adapter that is not wire-compatible does not ship: there is no drained-cutover alternative (owner decision 2026-10-21 — this row as written on 2026-09-03 allowed a drained cutover with a maintenance window; that escape is removed, because zero adopter impact is a hard requirement for any engine switch) | a rolling deployment that strands in-flight messages, and a "switch" that needs a maintenance window |
| S5 | **Shadow lane** — the GM broker shapes, CorPay and api-portal run nightly on the new adapter for ≥4 weeks before the default flips; the library carries its own mutation gate (break 70, like every package) | a default flip on a green afternoon |
| S6 | **Rollback proven** — the flip back to the MassTransit adapter passes S1–S4 the other way, and the upgrade guide carries both directions | a one-way door presented as a switch |

The seam (S1) and the suite (S2/S3) are September 2026 work on the accelerator; S4–S6 are
the library's own DoD and cannot start before it exists.

**2026-10-21 — owner decision: zero adopter impact, for every engine**
([ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md)). S1 (apps compile
unchanged), S4 (wire compatibility, now mandatory) and S6 (rollback both ways) bind ANY engine
switch, not only the default flip. Two adapters are now on a written clock:

| Adapter | Role | Must pass | By |
|---|---|---|---|
| **Wolverine** (option D, MIT) | The 1.0 insurance against the 8.x window ([GA readiness](goldpath-ga-readiness.md) G5) | S1, S2 (widened, incl. the four gaps below), S3 against MassTransit 8.5.11, **S4**, S6. It is not the default in 1.x, so S5 and a flip are not owed | **2026-12-31** (train 0.1.0-preview.11) |
| **Goldpath-family bus** (option F) | The committed long-term engine (T40) | S1–S6 in full | RFC + qorpe repository **December 2026** → first adapter **January–February 2027** → ≥4-week shadow lane (S5) → **opt-in in a 1.x minor** → **default only in 2.0** (D2 counts a default flip as breaking, so 1.0 locks the default for 1.x) |

S4 for the family bus means the clean-room library reads and writes the 8.x envelope and
topology on the wire; that is a requirement on its RFC, not a later discovery.

**2026-09-05 — the suite exists:** `tests/Goldpath.Messaging.Conformance` runs in the CI
integration job on real RabbitMQ + PostgreSQL, THROUGH the seam (handlers, the publisher,
the context — no engine type in a scenario). First eight facts, numbered after the S2
list: exactly-once on a re-delivered message id (S2.1), tenant + correlation across the
broker into the context (S2.2), the poison ladder then the fault (S2.3), a 1 MiB payload
(S2.4), a wider producer tolerated and a narrower one defaulted (S2.5), a commit before
the bus exists delivered once it starts — the crash between commit and publish (S2.6), a
paused broker delaying but losing nothing (S2.7), a graceful stop mid-stream draining
across two processes without duplicates (S2.8). Not yet in the suite, said plainly:
delayed redelivery (needs the scheduler plugin the in-memory ladder skips), per-queue
ordering (the seam promises none; a second engine may), OTel spans (needs an exporter
in the harness), consumer concurrency/prefetch limits. Every fact is written against the
seam, so S3 is "register a second engine's adapter in the harness and run the class".

**2026-10-26 — S4 in the suite (G5 Part 4/5, #333).** `MixedFleetTests` runs two hosts, one per
engine, on one broker and one database, once per direction (MassTransit 8.5.11 → Wolverine and
Wolverine → MassTransit), through the seam only: S4.1 what one engine publishes (committed
through the outbox, and raw with its message id set) the other handles, with tenant,
correlation and message id intact; S4.2 a message handled under one engine and redelivered
under the other is not handled again; S4.3 an outbox row one engine committed and never
relayed is delivered by the other after the restart; S4.4 a handler the new release adds runs
for that redelivery while the one already there is skipped (one inbox row per handler); S4.5
a message one engine faulted into `<queue>_error`, shovelled back as the runbook says, is
handled by the other. Without Wolverine's MassTransit interop on the listener, S4.1 goes red.
Writing the facts found three defects in the Wolverine adapter, each red first and fixed:
its inbox keyed a consumer by the endpoint alone, so every cross-engine redelivery ran its
handler twice (S4.2, S4.4, both ways); its relay published an 8.x outbox row to an exchange
named after the whole `;`-separated `MessageType` column, where nothing is bound, and then
deleted the row, a silent loss (S4.3); and its inbox sweep read `ExpirationTime`, which 8.x
never writes, so the rows the old engine left would never be forgotten.

**Retry and the error queue in a mixed fleet.** Immediate retry is measured by S2.3 on each
engine, and the error queue by S4.5 across them:

- *Immediate retries* run in the process that received the delivery, on its engine, counted by
  that engine (`IntegrationEventContext.RetryAttempt` starts at 0 on each); no attempt count
  crosses the wire, as goldpath-messaging-engines §4.4 designs.
- *The error queue* is the same `<queue>_error` for both engines. A message either one parks
  there keeps its envelope, its message id and its `X-Goldpath-Tenant` / `X-Correlation-Id`
  headers, so once the consumer is fixed the operator shovels it back and **either** engine
  handles it, exactly once (a delivery that faulted was never recorded as consumed).
- *Delayed redelivery* is the one stage a mixed fleet does not share. The Wolverine adapter
  refuses it at registration (open-threads T51), so a fleet with a Wolverine process runs with
  `Goldpath:Messaging:Retry:RedeliveryIntervals` empty on that process, and its exhausted
  messages go straight to `<queue>_error`. A MassTransit process that keeps the stage schedules
  its redelivery onto the shared queue, where a Wolverine process may receive it. Read from the
  adapter's code, not measured (the suite's broker has no delayed-exchange plugin): that process
  does not read `MT-Redelivery-Count`, runs a fresh immediate ladder, then parks the message, so
  the remaining delayed stages are skipped and nothing is lost. This is T51's to close, and it
  stays open until delayed redelivery is durable on Wolverine.

## 8. DoD — closed 2026-08-10

- [x] Owner decides A–E → **A** (stay pinned on 8.x), fallback **D**. Decided 2026-08-10.
- [x] The false claim in `goldpath-messaging.md` §9 D1 is corrected in place, pointing here.
- [x] The triggers that would move us off A are written into
      [open-threads.md](../strategy/open-threads.md) as **T18**, each with the proof that
      must run before the thread closes.
- [x] The narrowing candidate in §5 is scoped and **shipped** — `IIntegrationEventPublisher`
      plus GP0404, in the same train as this decision (PR #159).

**What is deliberately NOT closed by this RFC**, so nobody reads A as safety: the 8.x
maintenance window is finite, and A is a decision to WATCH, not a decision to relax. T18 is
where that watching lives; if it goes stale, this decision has quietly expired.

**2026-09-03 addendum — the watch became a plan.** Option F chosen (ADR-0013). New DoD
lines, open until each proof runs:

- [x] The consume seam ships — handler contract, context, registration (2026-09-05, this
      repo's `Goldpath.Messaging`) with analyzer GP0405 (Info → Warning at the preview.8 boundary, done).
- [x] The two template layouts, the worker template and the CLI's worker skeleton consume
      through it (preview.8, with the upgrade guide; GP0405 is a Warning from that train).
- [x] CorPay consumes through it (2026-09-07, on the day preview.8 published: three
      handlers, same queue names, smoke green on real containers).
      api-portal has no consumers of its own.
- [x] The outbox/inbox configuration and the test harness in Goldpath vocabulary (#327, G1
      Part 2/4, as designed in [goldpath-messaging-engines](goldpath-messaging-engines.md)
      §4.1–§4.3): `GoldpathMessagingBuilder.AddOutbox<TContext>` with `GoldpathOutboxOptions`,
      `AddGoldpathOutboxEntities()` and `GoldpathMessagingTestHost`. The conformance suite and
      the outbox and tenant-square integration tests name no engine. The MassTransit-typed
      overloads stay until Part 4/4; what Part 2 leaves is open-threads T44.
- [x] The bus conformance suite exists in this repo (2026-09-05, `tests/Goldpath.Messaging.Conformance`,
      eight facts through the seam, green against MassTransit 8.5.10 on real containers, in the
      CI integration job) — its scenario list grows with §7.1's gaps; owner review pending.
- [ ] The Wolverine adapter passes §7.1 S1, S2, S3, S4 and S6 by **2026-12-31** (GA gate G5,
      owner decision 2026-10-21, ADR-0015); SECURITY.md states the engine posture.
      **2026-10-25 (G5 Part 1/5, #330):** `Goldpath.Messaging.Wolverine` runs the suite's current
      class, S2.1–S2.12, green beside 8.5.11 in the same CI job. S2 is not yet widened with the four
      gaps above (Part 2/5), and S1, S3, S4 and S6 are open; what the adapter refuses is open-threads T51.
      **2026-10-26 (G5 Part 4/5, #333):** S4's facts, S4.1–S4.5, run in the CI integration job in both
      directions, on the 8.x envelope, topology and tables; delayed redelivery is the stage a mixed
      fleet does not share (above, T51).
- [ ] The family bus has its own RFC and repository (qorpe organisation, open source,
      clean-room per ADR-0013) in **December 2026**; its first adapter enters the suite in
      January–February 2027 (T40, milestone `family-bus`).
- [ ] §7.1 S1–S6 pass for the family bus; it ships opt-in in a 1.x minor and becomes the
      default only at 2.0, at a train boundary; T18 and T40 close.
