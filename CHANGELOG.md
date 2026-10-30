# Changelog

All notable changes to the Goldpath packages are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) · Versioning: SemVer.

## [Unreleased]
### Added
- **The Data module, proven on Oracle (GA gate G7, slice O-2, #405).** Tests only: no package changes.
  `OracleDataTests` (`Category=Oracle`) runs on the O-1 lane's container, one schema per fact, against a model that
  composes Jobs, Campaign, Bulk and Archival in the 19c SQL form:
  - every column's Oracle store type is pinned in a golden;
  - every identifier fits in 128 bytes;
  - every NOT NULL string column refuses `''`;
  - the keyset suite passes with no Oracle branch, in a session that sorts BINARY;
  - `DateOnly`/`TimeOnly` round-trip;
  - D7 migrate up, down, up, and a second migrate changes nothing.

  It found three things, filed as issues:
  - the document columns are `NVARCHAR2(2000)` and refuse a NULL in the client (#475);
  - three admin verbs can write `''` into a NOT NULL column (#476);
  - the global ceiling row's empty name (#384, already open).

  It also found that under a `tr-TR` culture ODP.NET opens the session with `NLS_SORT=TURKISH`, so a keyset walk
  over a string key silently skips rows. A fact pins it, #496 holds the fix (pin the session's sort in the Oracle
  composition), and `ops/data.md` gives the per-connection pin as the workaround until then.

  `packages/Goldpath.Data/ops/data.md` gains an "Oracle" section: the container's cost, the 19c compatibility
  setting, and the type mappings.
- **Jobs: the clustered Quartz store runs on Oracle (G7, slice O-3, #406).**
  - **API (additive):** `GoldpathJobStoreProvider.Oracle = 2`. It is appended, so no stored value
    moves. The executor composes `UseOracle`, and the management head's never-started schedulers use
    `OracleDelegate` and `OracleODPManaged`. The application references ODP.NET; `Goldpath.Jobs`
    takes no Oracle dependency.
  - **An unknown provider is refused** with the value named, on both the executor and the management
    head. Before this change, any value other than `SqlServer` silently became a PostgreSQL store.
  - **The `qrtz_` tables take Quartz's own Oracle shape on Oracle only:**
    - **Names:** upper-case table, column and index names. EF quotes what it creates, and
      `OracleDelegate`'s unquoted SQL folds to upper case. Without this, the store's first query fails
      with `ORA-00942: table or view "…"."QRTZ_JOB_DETAILS" does not exist`.
    - **Flags:** `VARCHAR2(1)` holding `'0'`/`'1'`.
    - **Binary data:** job data, calendars and blob triggers are `BLOB`, where EF's default
      `RAW(2000)` would cap a job's data at 2,000 bytes.
    - The PostgreSQL DDL is byte-for-byte unchanged (a golden generated from `main` before the
      change).
  - **Proof** (`JobsOracleTests` and `JobsRunListOracleTests`, `Category=Oracle`, on the pinned
    Oracle Free container):
    - the model against Quartz.NET's own `tables_oracle.sql`, vendored from tag v3.22.9;
    - two schedulers fire one trigger exactly once;
    - a misfired trigger follows its stored instruction;
    - a kill -9 mid-run is recovered on the other node from the checkpoint;
    - the run list's filters and keyset walk hold.
  - **Runbook:** §9 covers `QRTZ_LOCKS` row-lock contention.
  - **Still open:** `GoldpathJobExecutions` loses the history row of a fire with no error on Oracle.
    The `Error` document column refuses a NULL in ODP.NET (#475).
- **Campaign: a scheduled start and the `Ready` state (campaign RFC R3.1, slice R3-S2, #289).**
  - **API (additive, init-only):** `GoldpathCampaignPolicy.StartAt` and `IsBeforeStart(utcNow)`,
    `GoldpathCampaignThrottle.StartAt` and `ClearStartAt`, `GoldpathCampaignInfo.StartAt`. No
    positional constructor changes. `StartAt` is stored in UTC.
  - **Engine and pacer:** a finished snapshot goes to `Ready` while the start is ahead, and to
    `Running` otherwise, as before. The pacer loads `Ready`, starts it once the start has passed
    through one internal start gate that ships with no gates (amendment R3.7 plugs in there),
    audits it `start` with the actor `system`, and releases in the same tick. A `Ready`
    campaign past its end date expires.
  - **Verbs:** `create` takes `StartAt`. `throttle` moves or clears it until the campaign has
    started (`Running`, or anything released) and refuses after that. A start in the past
    means now, and a start after the end date's local day is refused. `pause` accepts `Ready`;
    `resume` returns to `Ready` while the start is ahead. The audit and `LastVerb` add
    `start=…` to the policy text only when a start is involved, so existing text is unchanged.
  - **Proof:** `R3StartTests` (20 facts, a stepped clock), red against planted faults,
    including the Berlin DST pair: a 03:30 start on spring-forward Sunday releases nothing at
    01:29:59Z and releases at 01:30Z, and of the two 02:30s on fall-back Sunday only the
    chosen one starts. The pacer's start is guarded on the state and the start it read, so an
    operator's move or pause made during the tick wins. Mutation gate: 83.95 % (`stryker/README.md`).
  - **On a real broker and in the console (R3-S3, #315):**
    - `CampaignR3Tests` runs on RabbitMQ + PostgreSQL. A campaign created twenty seconds out reports
      `Ready` and then `Running`. No item is claimed before its start, and every item completes once.
    - The console governor lists, filters and counts `Ready` as live.
    - It shows the start in the policy time zone, and moves or clears it through `throttle`.
    - A local start in the spring-forward gap is refused before anything is sent. One in the repeated
      hour asks which of its two instants is meant. A date the calendar does not have (`2026-02-30`,
      `24:00`) is refused, never rolled over.
    - A policy whose time zone is a Windows id the server accepts (`Turkey Standard Time`) is shown as
      the instant alone, and moving its start says that the console reads IANA ids, naming the zone.
    - A start still in the draft when a refresh shows the campaign running is not sent.
    - The console smoke drives the move end to end, and checks that the verb log records it.
- **Admin surfaces: a read role, `goldpath-ops-read` (admin contract R4.1, slice R4-S1, #297).**
  - **What it is:** `GoldpathPolicies.OpsRead` (Abstractions) and `GoldpathAuthOptions.OpsReadRole`
    (Auth, default `goldpath-ops-read`). `AddGoldpathAuth` registers the policy in both strategy
    branches as "the read role or the ops role", so no operator loses a route.
  - **How the guard applies it:** `AdminSurfaceGuard` (Sdk) applies the HTTP method rule on all
    seven surfaces and the console. A `GET` demands `OpsRead`. Every other method also demands
    `Ops` through an endpoint filter, which refuses with `403` and the admin envelope naming the
    configured role ("this verb needs the goldpath-ops role; this identity can read this surface
    but not act on it"), where it used to answer an empty `403`.
  - **Apps without the read policy:** an app that registers no `OpsRead` policy keeps `Ops` on
    every route and gets one information log line.
  - **Proof:** a cross-surface route-table test holds every route in one of the two buckets. A
    real-HTTP role matrix on PostgreSQL covers every surface:
    - a viewer reads every parameterless `GET` and is refused every verb in words;
    - the viewer retrieves an archived document in full;
    - an operator does both;
    - nobody is challenged with `401`.
  - **Disclosure:** granting the role grants archive retrieval. The preview.10 upgrade guide says
    so; T50 carries the masking viewer.
- **The Oracle test lane (GA gate G7, slice O-1, #404).** Repository and CI only: no package
  changes, and no `Goldpath.*` package references Oracle.
  - **Pins:** `Oracle.EntityFrameworkCore` 10.23.26301, `Oracle.ManagedDataAccess.Core` 23.26.301
    (the newest releases at least two weeks old on 2026-10-26) and `Testcontainers.Oracle` 4.16.0,
    referenced by `Goldpath.IntegrationTests` only.
  - **Licence gate:** `scripts/license-gate.py` gains exactly the two scoped Oracle Free Use Terms
    entries of RFC goldpath-oracle §2.1. Any other FUTC package still fails the gate.
  - **Fixture:** `OracleFixture` runs one `gvenzl/oracle-free:23.26.3-slim-faststart` container per
    collection.
  - **CI:** `ci.yml` gains an `oracle` job (`Category=Oracle&Category!=Bench`), and the
    `integration` job excludes `Category=Oracle`. `nightly.yml` runs the same filter.
  - **Advisories:** of ODP.NET's six transitive packages only `System.Security.Cryptography.Pkcs`
    8.0.1 carries advisories. It resolves to the existing central lift, 10.0.13.
- **The second messaging engine runs the conformance suite: a Wolverine adapter, and the engine contract
  it plugs into (GA gate G5 Part 1/5, #330).**
  - **`Goldpath.Messaging`:** the engine contract of RFC goldpath-messaging-engines §4.1 row 5, built
    ahead of Part 4/4 by owner decision (2026-10-25). `IGoldpathMessagingEngine` (`Name`, `Compose`,
    `PublishAsync`), declared by an adapter assembly with `[assembly: GoldpathMessagingEngine(typeof(…))]`
    and handed a read-only `GoldpathMessagingComposition`; `GoldpathMessagingTransport` becomes public.
    `AddGoldpathMessaging(messaging => …)` finds the engine: an adapter package beside the application
    wins over the MassTransit engine `Goldpath.Messaging` still carries, and two adapter packages fail
    the start, naming both. An application that references no adapter (every one today) composes
    exactly as before. Additions only; the MassTransit-typed overloads are unchanged.
  - **`Goldpath.Messaging.Wolverine`** (new, not published: `IsPackable=false` until the switch proofs
    are linked in open-threads T18). WolverineFx 6.53.0 and its transitive family pinned exactly, all
    MIT; the licence gate passes with no new exception, and `dotnet list package --vulnerable` is clean.
    Wolverine receives (a listener per endpoint, the immediate retry ladder, the move to
    `<queue>_error`); the adapter writes the 8.x envelope, declares the 8.x topology and keeps the
    outbox and inbox in the 8.x tables on PostgreSQL itself (RFC §4.4, §9 Q4). It refuses in words
    what it does not do yet, among them delayed redelivery, which the default options enable
    (open-threads T51).
  - **The conformance suite runs once per engine:** S2.1–S2.12 green on MassTransit 8.5.11 and on
    Wolverine, on real RabbitMQ + PostgreSQL; dropping the tenant header in the adapter turns S2.2
    red on Wolverine only. S2.12 (what a batch handler publishes is delivered) was red on the
    adapter's first cut, which discarded a batch's outbox rows with its scope, and is green on both
    now. The adapter's own decisions the class cannot reach (the commit without the inbox, two
    handlers on one endpoint, the inbox sweep) have Wolverine-only facts beside it.
- **A threat model, for the first four surfaces with an auth boundary (GA gate G15 Part 2/2,
  #340).** Documents and one test assertion; no package change.
  `docs/security/threat-model.md` covers four surfaces: the admin routes, the served console,
  Campaign's signed callbacks, and Bulk upload with its gate.
  - It is STRIDE per surface, and every threat names what holds it: a test, by class and method;
    an analyzer; an issue; or an accepted risk.
  - Three threats had no holder and are now issues: #452 (CSRF on body-less verbs behind a
    gateway cookie), #453 (a bulk batch does not record its uploader), and #454 (no test pins
    that the console refuses traversal-shaped asset paths).
  - Six accepted risks (A1–A6) await the owner's signature. A6 is that archival retrieval
    returns an archived document unredacted to both ops roles, the viewer role included under
    admin-contract R4.1 (the regulatory record; erasure is what masks it).
  - Bulk upload is the one mutating admin verb that records no actor (#453); every other
    surface's audit evidence is named per surface. The Jobs holder's unit test now asserts
    each audit row's actor, not only its action.
  - `SECURITY.md` links the model, and `ga-gate-status.md` G15 records 4 of 10 surfaces. The
    other six (Approvals, FileExchange, Notification, DataProtection, Idempotency,
    MultiTenancy) remain the issue's checklist.
- **S-OPS-02 runs in the console smoke: four operator mistakes refused from the screen, zero
  5xx (GA gate G10a, #324).** Test-only; no package change.
  - **The four mistakes:** `ui/console/e2e/refusals.spec.ts` attempts each one through the real
    console against a real app: a bad cron and an unknown timezone in the reschedule form, a
    second signature by the same approver, and an erase while a legal hold stands.
  - **What it asserts:** each verb answers 400, and the console shows the server's own sentence.
    Any 5xx, or any request the browser could not read, fails the run. The platform's
    exception page carries no CORS headers, so a 500 to the cross-origin console arrives as a
    failed request.
  - **The smoke host** files a second approval request at startup, so this spec never races
    the approvals journey for one quorum.
- **Security scans in CI (GA gate G15 Part 1/2, #339).** Repository CI, no package changes.
  `.github/workflows/codeql.yml` runs CodeQL (`security-extended`) on the C# and the console's
  TypeScript on every pull request, on `main` and weekly, with its actions pinned to commits.
  A nightly `zap-baseline` job runs `scripts/zap-baseline.sh`: a passive OWASP ZAP baseline,
  image pinned by digest, against a generated app (`GmBulkOnly`), its OpenAPI operations and
  the console it serves, in Production posture. An alert that `scripts/zap-baseline.conf`
  does not list fails the night. The first run's open findings are carried as INFO with
  their ledger entries: #377 (an unknown Bulk definition answers 500) and open-threads T43
  (three missing security headers).
- **`Goldpath.Messaging`: the message path in Goldpath's own vocabulary (GA gate G1 Part 2/4,
  #327).** `builder.AddGoldpathMessaging(messaging => …)` takes a `GoldpathMessagingBuilder`:
  `AddHandler<TEvent, THandler>()`, `AddHandlers(assemblies)`, `AddOutbox<TContext>(…)` with
  `GoldpathOutboxOptions` (`DuplicateDetectionWindow` 30 min, `Inbox` on, `DeliveryPollInterval`
  the engine's default unless set), `UseRabbitMq(connectionName = "messaging")` and `Options`.
  The outbox's row-lock statements follow the context's EF provider, so the
  `UsePostgres()`/`UseSqlServer()` line goes away, and an outbox on any other provider fails the
  host's start in words instead of the delivery loop later; a non-positive outbox interval is
  refused at registration. The pipeline is applied to every endpoint
  without a `ConfigureGoldpathEndpoints` call. `modelBuilder.AddGoldpathOutboxEntities()` maps
  the same three tables and columns as the engine-named calls (no migration).
  `GoldpathMessagingTestHost` starts a host on the in-memory transport (or the one the block
  selects), returns once the bus is up, and publishes as an upstream service would
  (`PublishAsync` with a `GoldpathOutgoingMessage` stamp, `PublishAsAsync<TEvent>(shape)`). The
  conformance suite and the outbox and tenant-square integration tests now use only this
  vocabulary. **Additive:** the MassTransit-typed overloads stay until Part 4/4, so
  `engine-gate.sh` is unchanged at 13. `Goldpath.Messaging` now references
  `MassTransit.RabbitMQ` 8.5.11 (the transport `UseRabbitMq()` composes) and
  `Microsoft.Extensions.Hosting` 10.0.11 (the test host). What Part 2 leaves is open-threads T44.
  A present but blank `ConnectionStrings:messaging` is refused at the bus's start in words (an
  absent one still means the engine's local default), and the test host refuses a tenant stamp
  outside `TenantId`'s alphabet, which the consume filter would otherwise drop silently.
- **`Goldpath.Messaging`: the batch half of the consume seam (GA gate G1 Part 3/4, #328).**
  `IIntegrationEventBatchHandler<TEvent>` takes deliveries in groups
  (`HandleAsync(IReadOnlyList<IntegrationEvent<TEvent>>, …)`, each `IntegrationEvent<TEvent>`
  pairing the event with its own `IntegrationEventContext`: message id, correlation, tenant and
  headers per delivery, so one batch may carry several tenants). It is registered with
  `messaging.AddBatchHandler<TEvent, THandler>(GoldpathBatchOptions)`: `MessageLimit` (10),
  `TimeLimit` (1 s) and `ConcurrencyLimit` (1), the engine's defaults; a limit that is not
  positive, a `MessageLimit × ConcurrencyLimit` above 65,535 (a broker's 16-bit prefetch window)
  or a `TimeLimit` beyond a timer's ~24.8 days is refused at registration, naming the handler. A batch is delivered when either
  limit is reached, at most `ConcurrencyLimit` batches run at once per endpoint, and a throw
  fails the whole batch into the retry ladder. Conformance facts S2.9–S2.11 prove the size
  bound, the time bound and one batch in flight on real RabbitMQ. `AddHandler<TEvent,
  THandler>(endpointName)` and `AddBatchHandler`'s optional `endpointName` register a handler
  on a queue that must keep the name a running fleet already drains; handlers given one name
  share one queue, and moving a handler to a second name is refused.
- **`goldpath check` warns a manifest that selects a roadmap-only value (GA gate G3, #336).**
  After `specdrift validate` passes, every value the schema accepts but no code runs
  (`broker: kafka|inmemory`, `exporters: prometheus`, `auditTrail.store: eventstream`, and
  `db: oracle` until G7 lands) is reported as a `WARNING` naming its place in the manifest and
  quoting the schema `$comment` that owns it. The check still passes: the schema accepts these
  values on purpose. The list is derived from the annotation `schema-honesty.sh` already holds
  (`` `x` is/are SCHEMA-ONLY|RESERVED ``), walked with the manifest so a `then` applies only when
  its `if` holds, so a new roadmap value needs a `$comment` and no code. `schema-honesty.sh` now
  reads that annotation with the same pattern (pinned equal by a test), so a value the gate
  accepts as annotated is one the CLI warns about: a marker elsewhere in the comment, or in lower
  case, no longer satisfies the gate. Five manifest corpus rows
  carry `# expect-warning:` lines that the CLI tests hold the finding to. `specdrift validate`
  itself does not warn yet (open-threads T45), and a service's `providers.db: oracle` has no
  annotation to derive from (T46).
- **Campaign: pause proven within three seconds on a real broker (S-FLEET-04, #286).** Test and
  documentation only, no package change. `ScenarioFleet04Tests` runs two hosts on Testcontainers
  RabbitMQ + PostgreSQL, four consumer slots each and a 600 ms target, pauses through the admin
  verb with the queue at least ten times deeper than the slots, and asserts no claim later than
  the pause + 3 s, no more calls in flight than slots, and every item exactly once after resume.
  It is red on the claim guard before R2.6. The measured worst case (no claim after the pause,
  the fleet quiet within 423 ms) is in `ops/campaign-benchmarks.md` and the runbook's pause text.
  Writing it found that a bus-level consumer limit does not reach the campaign's item endpoint,
  which it shares with the outcome sink (open-threads T48).
- **Campaign: a real-broker bench at 1000 TPS (S-FLEET-02a, #283).** Measurement only, no
  package change. `CampaignBrokerBenchTests` drives one campaign through the whole release path
  on Testcontainers RabbitMQ + PostgreSQL with the package defaults, records published item
  messages per one-second window and reconciles exactly-once. `scripts/bench-campaign.sh broker`
  runs it for 15 minutes, `smoke` for 2. Today's baseline is in
  `packages/Goldpath.Campaign/ops/campaign-benchmarks.md`; it is below the threshold, as
  expected before Parts 2 and 3.
- **The engine gate (GA gate G1, #321).** `scripts/engine-gate.sh` fails CI when a
  `PublicAPI.*.txt` outside a bus-engine adapter package names an engine type (`MassTransit.`,
  `Wolverine.`) that is not in its committed baseline, `scripts/engine-gate.baseline.txt`. The
  baseline holds the 13 lines shipped on 2026-10-20 and may only shrink, and `--selftest`
  proves five planted faults red. The design that empties it is
  [goldpath-messaging-engines](docs/rfc/goldpath-messaging-engines.md) (PROPOSED). No package
  changes.
- **Merge tiers for agent pull requests (ADR-0014, proposed).** Repository governance, no
  package changes. `.github/workflows/merge-policy.yml` computes a pull request's tier from
  its changed files with the rules in `.github/merge-tiers.yml` (read from the base branch,
  applied by `scripts/merge-tier.py`), writes the tier and the files behind it to the job
  summary, sets one `tier:A|B|C` label, and fails the `merge-policy` check on Tier C
  (decisions, releases, the gates, licence/security policy, the agent layer) carrying
  `agent-pr` without `owner-approved`, and on `hold`. The tier computation ships with a
  self-test — fixtures for every rule family, the decision table, and a check that every
  script a workflow runs is Tier C — run in `ci.yml`. The agents act with the owner's GitHub
  identity, so Tier C is enforced by the check, the verifier's rules and the morning report,
  not by identity; the hardening is open-threads T38. Operator page:
  [docs/ops/agent-pipeline.md](docs/ops/agent-pipeline.md).

- **`mutation-heavy.yml` re-scores one package on demand (#311).** Repository CI, no package
  changes. A `package` dispatch input (default `all`) names one of the six heavy packages, and a
  `plan` job turns it into the matrix, refusing a name outside the six rather than running an
  empty matrix that passes. `stryker/README.md` and the release checklist name it.

### Changed
- **Dependencies: the nuget minor/patch group of 2026-10-27.** MassTransit (and its RabbitMQ and EF Core
  packages) 8.5.11 → 8.5.12, so the conformance suite's oracle is 8.5.12. The templates, CorPay and the CLI's
  outbox recipe pin the same version, and the templates and CorPay move `System.Security.Cryptography.Pkcs` to
  10.0.14 with it, because a generated app pinning 8.5.11 against a Goldpath.Messaging
  built on 8.5.12 fails restore with NU1109. Also: MailKit 4.18.2, Microsoft.OpenApi 2.13.0, the
  `Microsoft.Extensions.*` 10.0.14 line, OpenTelemetry 1.20.1 and Quartz 3.22.9. The Oracle pair stays at
  23.26.301 / 10.23.26301 until 23.26.400 is two weeks old (2026-11-03).
- **`Goldpath.Campaign`: leadership is continuous by default (#284).** `LeadershipSlice` goes
  from 50 s to 60 s, the period of the default pacer cron (`pacerCron`, unchanged). The next fire
  is already due when a slice returns, and the jobs runner, which never runs the pacer twice at
  once, starts it then.
  - **Before:** every minute had about 10 s with no leader, so a campaign averaged at most 5/6
    of its configured rate.
  - **Proof:** `CampaignContinuousLeadershipTests` runs on the package defaults, with Quartz on a
    real PostgreSQL and a real RabbitMQ. It was red on the 50 s default (two 10-second windows
    per minute at half rate, with a 10 s gap between releases). It is green on 60 s: every
    10-second window across two hand-overs is within ±5%, with a measured hand-over of under 0.6 s (the longest gap between two releases at 50 TPS with a 250 ms tick: 583 ms and 459 ms on two runs).
  - **What does not change:** no public API line; a slice still starts with empty token
    buckets, so a fire's first tick releases at most one tick's worth.
  - **Upgrade guide:** preview.10, "A default changed: the pacer's leadership slice".
- **Admin-contract revision R4 is written (#304), PROPOSED; merging it is its acceptance.**
  Documents only, no package change. `docs/rfc/goldpath-admin-contract.md` gains R4, the shape of
  the owner's 2026-10-20 approvals:
  - **R4.1:** a read role, `goldpath-ops-read`, which `goldpath-ops` also satisfies. Every admin
    `GET` and the served console demand it, and every verb keeps `goldpath-ops`. A verb refused
    for want of the role answers `403` with the verb envelope. The role reads archived
    documents in full (`GET /archival/entries/{definition}/{key}` returns the `Document`), by
    owner decision of 2026-10-25; R4.1 states it, and R4-S1 must state it in the upgrade guide.
  - **R4.2:** campaign `/failures` walks by `afterSeq` and returns each item's `errorCode`.
  - **R4.3:** campaign `/{id}/outcomes` counts the items by state and the failures by code.
  - **R4.4:** the campaign detail's `ETag` is its `PolicyVersion`, and `throttle` takes an
    optional `If-Match`; a stale one answers `412`.
  - **R4.5:** Campaign R3.2's four ceiling routes.

  The revision also states its compatibility case (additive, no migration), the rejected
  alternatives, and seven ordered slices. Alongside it:
  - the auth RFC (§10) and the console RFC (§3 auth, item 4) say how the three policies compose;
  - the campaign RFC records that R2's "finer roles" non-goal is reversed for reads;
  - open-threads T50 carries what R4 leaves out: an admin-only tier, an approver outside
    Approvals, and a viewer that reads archive metadata without the document.
- **`Goldpath.Campaign`: campaign consumes through the seam (GA gate G1 Part 3/4, #328).**
  `messaging.AddGoldpathCampaign<TContext>()` on the `GoldpathMessagingBuilder` registers
  `GoldpathCampaignItemHandler<TContext>` (an `IIntegrationEventHandler<GoldpathCampaignItemMessage>`)
  and `GoldpathCampaignOutcomeHandler<TContext>` (an `IIntegrationEventBatchHandler`, still 200
  outcomes or 500 ms, one batch at a time). Claim-before-execute, the outcome shapes and the
  set-based outcome write are unchanged. **No queue moves:** both handlers drain the queue the
  previous consumers drained, named after the context (`OrdersDbContext` drains
  `orders-db-context`), with the same exchange bindings, so a fleet can roll host by host; a test
  reads the queue and bindings from the broker, and the same test passes against the previous
  registration. `bus.AddGoldpathCampaignConsumers<TContext>()` on the MassTransit-typed block keeps
  working and now registers the same two handlers on the same queue; it leaves with Messaging's
  MassTransit-typed overloads in Part 4/4, and the templates and the CLI recipe move to the builder
  line at the preview.11 release PR. **BREAKING for code that names the consumer types:**
  `GoldpathCampaignItemConsumer<TContext>`, `GoldpathCampaignOutcomeSink<TContext>` and
  `GoldpathCampaignOutcomeSinkDefinition<TContext>` are removed (use the handlers above). The
  engine, the pacer and the callback endpoint publish through `IIntegrationEventPublisher` instead
  of MassTransit's `IPublishEndpoint`, so a test double of the campaign engine implements the seam.
  `scripts/engine-gate.sh`'s baseline drops from 13 lines to 11 and its ceiling with it; the gate
  now treats a Shipped line that the sibling Unshipped file marks `*REMOVED*` as gone, and dropping
  the marker fails it.
- **The public 1.0 sample exam is Insurance + Telco (GA readiness Q6/G12, amended 2026-10-25).**
  Documents only, no package change. The Factoring sample the owner added on 2026-10-21 moves
  out of this public repository. It is built from scratch in a private qorpe repository as a
  generic, clean-room sample, under a provenance rule: public domain sources only, cited; nothing
  from any engagement; its agents never read an engagement repository. It is not a public 1.0
  gate. The RFC's G12 row, the rc.2 row and Q6, `ga-gate-status.md` G12, the coverage matrix,
  the master plan's step 8 and CLAUDE.md say so. The ten planned Factoring parts (#438–#447)
  are closed here with that reason.

### Fixed
- **Campaign: no second of pacer ticks releases more than its ceiling, even after an idle stretch (#292,
  S-FLEET-03).** Every hard-ceiling bucket (a campaign's `Tps`, a type's `MaxTps`, the `GlobalTps`)
  banked up to a whole second of tokens. What it saved while nothing released (a closed window, a full
  in-flight cap, a pause, a spent quota) was spent in one tick when release resumed, so one second could
  release the ceiling plus all but one tick of refill: 1.75 times the ceiling at the default 250 ms tick.
  - **Now:** a bucket holds one tick's refill, plus the largest fraction a refill can leave behind (none
    when the refill is a whole number of units). When `LeaderTick` divides one second (the default
    250 ms does), the ticks of any one second therefore release at most the ceiling. A tick that does not
    divide a second fits one more tick into some seconds, so such a second can release up to one more
    tick's refill (56 against 50 at a 140 ms tick); that was already so before this change, and
    `LeaderTick`'s documentation now says so. The steady rate is unchanged: 2.5 a tick still spends 2,
    then 3. Each refill is snapped to the exact grid of its fraction, so floating point never costs a
    unit at the cap: ten refills of 0.1 are exactly one, where 0.9999999999999999 would have released
    2/s at a 50 ms tick as 1.82/s. No public API, option or migration changes.
  - **Proof:** `BurstControlTests` counts each tick's releases exactly through the pacer's own tick scope.
    After 30 idle ticks, the three buckets each released 100 in one tick and 195 in one second against a
    ceiling of 100; now 5 a tick. S-FLEET-03 at its own numbers (GlobalTps 1000, two campaigns at 800):
    1950 in one second before, at most 1000 now. `CampaignR1Tests` on RabbitMQ and PostgreSQL drops its
    +50% tolerance and holds every second of ticks of the run to `GlobalTps` itself.
- **`Goldpath.Messaging.Wolverine` (not published) is wire-compatible with a MassTransit fleet:
  S4, mixed-fleet compatibility, in the conformance suite (GA gate G5 Part 4/5, #333).**
  `MixedFleetTests` runs a MassTransit 8.5.11 process and a Wolverine process on one RabbitMQ and one
  PostgreSQL, in both directions, through the seam only (S4.1–S4.5). It covers:
  - what one engine publishes, the other handles with tenant, correlation and message id intact;
  - a redelivery handled under one engine is not handled again under the other;
  - an outbox row one engine committed and never relayed is delivered by the other;
  - a message one engine parked in `<queue>_error` is handled by the other once shovelled back.

  The facts were red first and found three adapter defects:
  - **The inbox keyed a consumer by the endpoint alone.** A message the default engine had handled ran
    its handler again on Wolverine, and the reverse. It now keys each handler by the id 8.x derives for
    its adapter consumer (one `InboxState` row per handler, MD5 or FIPS SHA-256 as 8.x chooses), pinned
    against a row 8.5.11 wrote.
  - **The relay lost a MassTransit-written row.** It published the row to an exchange named after the
    whole `;`-separated `MessageType` column, which nothing is bound to, then deleted the row. It now
    publishes to the row's own type's exchange.
  - **The inbox sweep missed MassTransit's rows.** It read `ExpirationTime`, which 8.x leaves empty, so
    rows the old engine wrote were never forgotten. It now forgets by the 8.x rule: `Delivered` older
    than the duplicate-detection window.

  Removing the listener's MassTransit interop turns S4.1 red. Mixed-fleet retry and error-queue
  semantics are written in RFC goldpath-messaging-exit §7.1. `Goldpath.Messaging` and every
  published package are unchanged.
- **The console's npm advisories are closed: vitest 2 → 5, with the 97% coverage floor re-earned
  (#356).** Console test tooling only; the shipped bundle is byte-identical to the one before
  (the same `pnpm build` output hashes) and every package is unchanged. `ui/console` takes
  `vitest` and `@vitest/coverage-v8` at 5.0.5 (exact pins, one family as Dependabot groups it;
  supersedes #375), and `pnpm update source-map-js` lifts the last stale `source-map-js`
  (under the `@tailwindcss/vite` dev plugin) to 1.2.2, with no other resolution re-chosen.
  `pnpm audit` goes from 26 advisories (3 critical, 11 high: `vitest`, `tinypool`, `vite` 5,
  `esbuild`, `source-map-js`, `brace-expansion`) to none, with no override. vitest ≥4 maps v8
  coverage back to the source AST-aware, so the same 274 tests measured 91.16% statements and
  94.22% lines against floors of 97. The floors stay at 97 (open-threads T24). 35 new component
  tests cover what the old counter credited and the new one does not: the throttle form's
  every dial, resume and abort on a paused campaign, rerun, unticking and clearing each facet,
  closing each sheet, the error line of every detail that fails to load, the erasure table's
  columns, and the cross-section hop from a batch's run id to that run. Under vitest 5 the
  console now measures 97.93% statements, 90.59% branches, 97.6% functions, 98.99% lines.
- **`Goldpath.Bulk`: in-file duplicate detection no longer keeps every key (#300).** Validation
  kept each distinct `RowKey` as a string, so a 10M-row device list held them all: 1,226 MiB
  peak managed heap on a 10M-row run. It now keeps a 64-bit hash and the first row per key
  (about 40 bytes, whatever the key's length; 541 MiB on the same run) and confirms a hash hit
  exactly by re-deriving the first row's key from its staged payload, so two keys that share a
  hash are never reported as duplicates. The report, its first-row numbers and the counts are
  unchanged, including for a row key that does not survive the payload's JSON round trip (a
  `[JsonIgnore]` property or a private setter): such a definition is detected on the first
  keyed row of a pass, logged as a warning, and keeps every key in full for that pass, as
  before. A key that only SOME rows lose (a conditional `[JsonIgnore]` over a non-default
  initializer) passes that first-row check and is caught at its first repeat; the pass is
  then taken again from the start with every key kept in full, with the same warning, so the
  report is still the string table's and the batch never stays `Validating`. The
  confirmation costs one read per duplicate whose first row is already written:
  the 10M run with 99,951 such repeats validated at 12,079 rows/s against 14,773 (open-threads
  T49). No public API, schema or dependency change. `scripts/bench-bulk.sh intake` runs the
  10M bench; the numbers are in `packages/Goldpath.Bulk/ops/bulk-benchmarks.md`.
- **`Goldpath.Campaign`: a snapshot is no longer bound to the leader tick (#288).** The pacer
  materialized one `EnumerationBatchSize` batch per campaign per tick, so at the defaults (2,000
  rows, 250 ms) a snapshot ran at about 8,000 rows/s. 35M targets took over 70 minutes before
  the first release. Each tick now spends the time it would otherwise sleep enumerating further,
  after its releases and while the slowest step seen in that tick still fits before the tick
  ends. Several snapshots share that time in a rotation that carries across ticks, so when a
  tick has room for fewer extra steps than there are snapshots, the oldest does not take them
  all. A running campaign's releases keep their cadence. A pause or abort that lands mid-tick
  stops the snapshot at its last durable batch, and resume continues from there with nothing
  lost or doubled. No public API or option changes. `scripts/bench-campaign.sh enum` measures
  enumeration through the pacer (`CampaignPacerEnumerationBenchTests`), and
  `ops/campaign-benchmarks.md` now says which path each enumeration number measured.
- **The nightly ZAP baseline no longer changes its rules overnight.** Repository CI, no package
  changes. `scripts/zap-baseline.sh` pinned the ZAP image by digest, but the packaged scans
  start ZAP with `-addonupdate`, install `pscanrulesBeta` from the internet and check ZAP's
  own version online unless `-silent` is passed. So the rule set moved with no change here:
  2026-10-24's nightly ran more rules than the PR's own run had (PASS 104 → 111 on the API leg,
  53 → 58 on the console leg) and failed on four alerts that are in no rules-file line. Both
  scans now pass `-z -silent`: the image's own add-ons, no unsolicited request, the same rules
  every night, and a run an air-gapped network can repeat. The G15 row of
  `docs/strategy/ga-gate-status.md` now records the CodeQL and ZAP jobs from #366.
- **Recording a gate's progress no longer needs the owner's merge.** Repository governance, no
  package changes. Column (b), "Today (evidence)", of the GA-readiness RFC's gate tables, and the
  "Exists today" column of its summary, move to a ledger, `docs/strategy/ga-gate-status.md`. The
  RFC keeps each gate's criterion, proof, owner, effort and dependency. Under ADR-0014 every
  `docs/rfc/**` change is Tier C, so a code PR that only updated its gate's status row (#362,
  #379) waited for the owner. The same holds for `stryker/README.md`: the tier rule
  `stryker/**` narrows to `stryker/*.json`, the configs that enforce the thresholds and the
  exclusions, which is what ADR-0014 names ("`stryker/**` configs and thresholds"). The score
  ledger beside them is now Tier A. The new ledger is in `ledger-check.sh`'s list, and four
  merge-tier fixtures pin both moves (the README fixture is red under the old rule).
- **`train-freshness.sh` holds a release PR that leaves CorPay behind (#312).** Run from
  this repository while its own train is not on nuget.org yet (the release PR, and `main`
  between merge and publish), the gate now requires the product to pin exactly that train.
  Before, it accepted a stale pin there and refused the new one, so CorPay's pins could only
  move after the tag, and `main` went red until they did (v0.1.0-preview.9, #307). An
  adopter's copy of the script behaves as before. The release checklist moves CorPay's pins
  into the release PR. No package changes.
- **`Goldpath.Messaging`: assembly scanning skips an open generic handler (#327).**
  `AddGoldpathHandlers(assemblies)` (and the new `AddHandlers`) kept every non-abstract class,
  so an open generic `IIntegrationEventHandler<T>` implementation in a scanned assembly failed the
  host's composition with an opaque reflection error ("Late bound operations cannot be performed
  on types … ContainsGenericParameters"). A generic definition cannot be an endpoint, so it is
  now skipped; closed handlers, including one deriving from it, are registered as before.
- **Dependabot moves vitest and its `@vitest/*` plugins together.** Repository governance,
  no package changes. The console's catch-all group takes minor/patch only, so a major
  arrived per package: #359 bumped `@vitest/coverage-v8` to 5.0.5 alone, and against
  vitest 2 the console tests could not even load ("does not provide an export named
  'BaseCoverageProvider'"). A `vitest` group (`vitest`, `@vitest/*`, every update type),
  listed before the catch-all, makes each family update one pull request; the family's
  major stays a deliberate decision (open-threads T24).
- **A label burst no longer leaves `merge-policy` BLOCKED beside a green run.** Repository
  governance, no package changes. On #361 a push and two label edits in one second started
  three runs; the workflow's `cancel-in-progress` group cancelled two, one of them the newest
  check suite on the commit, which is the one branch protection reads (runs 39734763532,
  39734763872, 39734763819). `gh pr merge` was refused until the cancelled runs were re-run
  one at a time, and re-running two at once cancelled one again. `merge-policy.yml` now has
  no concurrency group: every run reads the labels when it starts, so the newest run judges
  the newest label state (the ADR-0014 answer) as long as nothing cancels it, and a
  ten-second read-only job costs nothing to finish. `tier-label` skips a run whose commit the
  pull request has moved past, so a slow older run cannot overwrite the newer tier. The
  `merge-tier.py` self-test fails if a concurrency group comes back (proven red with the old
  block and with `cancel-in-progress: false`, which still cancels a pending run when a third
  arrives).
- **The golden-manifest lane builds the generated app before `goldpath db init` and before
  every `db` post-generation verb** (#279), so a compile or analyzer error fails with the
  compiler's own diagnostic instead of `dotnet ef`'s bare "Build failed". In the T30 proof
  (#273) a planted defect — GP2102 dropped from the template's `WarningsNotAsErrors` — turned
  two shapes red and neither log named GP2102: GmEverything failed at `db init`, GmGrownRest
  (whose FileExchange only arrives through `add feature fileexchange`) at its later
  `db add GrownRest`. Replanted, `validate-gm.sh` now stops at `error GP2102` in both. The
  build after the post verbs stays, since they and the migrations change the app; on a warm
  green run the extra step costs about six seconds (56.8 s → 62.1 s for one shape).
- **Every mutation gate ran the whole solution's tests, whatever its config said.** The gate
  started Stryker at the repository root, where it finds `Goldpath.sln` and switches to
  solution mode — and solution mode never reads `test-projects`: it runs every test project
  whose build references the package. Abstractions was mutated against 702 tests and the
  integration host instead of its own 18; Approvals took 3.6 hours a night; the six heavy
  packages ran out of memory on hosted runners (issue #20's exit 143). The gate now runs
  Stryker from the package's own directory (`test-projects` written `../../tests/...`; four
  configs pointed at paths that never existed and nobody noticed, because the setting was
  never read), and it fails a run whose log shows solution mode or a test-project count other
  than the config's. Suites that legitimately score another package are named in that
  package's config with a row in `stryker/README.md` — Data, MultiTenancy, Messaging, Locking
  and Caching borrow them; a borrowed integration class is admitted by `test-case-filter`, not
  the whole container-backed suite — and `docs-freshness.sh` fails a missing test project or
  an unrowed borrow. Measured on hosted runners 2026-10-20: the sixteen nightly packages from
  10.1 hours of job time to 1.3, the slowest from 218 minutes to 27, all six heavy packages
  finishing in under 30 minutes, every score unchanged except MultiTenancy (75.58 → 72.09 %:
  three mutants only an unrelated benchmark killed). Caching scores 69.57 %, below the break,
  in both modes — open thread T32. The gate also streams the scope lines it used to discard
  (`tail -4`), and both mutation workflows keep the full log and Stryker's JSON report.
- **Caching sat below its mutation break, and Bulk and Auth cleared theirs inside the noise.**
  The honest scores from the scope fix put Caching at 69.57 % (open thread T32), Bulk at
  70.09 % and Auth at 70.65 % — the last two within the ±1 point a same-commit rerun moves.
  Tests, not a lower break and not an exclusion, moved them, each citing the clause it proves:
  Caching to **100.00 %** (the tiers, the configuration section, the default TTL and the
  injected key helper, all asserted from the container an app resolves), Auth to **97.83 %**
  (the ops roles, the audience guard's exact edges, HTTPS-only metadata, the failure meter, the
  binding's typed 403, and `UseGoldpathAuth` authenticating on a bare pipeline) and Bulk to
  **88.38 %**. Bulk's largest gap was a test reading a CACHED model: EF builds a context type's
  model once per process, so in Stryker's long-lived test host the model contract tests never
  saw `AddGoldpathBulk` run under a mutant, and 23 mutants survived assertions written to kill
  them; the rest were an engine never observed mid-batch, where the terminal recount hides a
  wrong counter. The same cached-model blind spot sits in the Jobs and Archival model suites
  (open thread T33). Measured in the nightly and on the heavy lane alike, same scores.
- **Six packages were mutation-tested only when somebody remembered to dispatch them.** Jobs,
  Archival, Bulk, Notification, Campaign and Caching ran in the dispatch-only
  `mutation-heavy.yml`, which is how Caching stayed red for seven weeks unseen. With their
  scope fixed they finish in under 15 minutes at the nightly's concurrency, so the nightly
  matrix now scores all twenty-two gated packages; `mutation-heavy.yml` stays for re-scoring
  the six on demand.

### Added
- **The deterministic floor under authoring** (Slice 0 of the
  [goldpath-authoring](docs/rfc/goldpath-authoring.md) RFC, which landed PROPOSED; no LLM
  anywhere in it). Before a business sentence can become an approved spec, the artefacts that
  carry it need a shape, a check and a place to live:
  - **specanchor's rule, ledger-term and spec schemas are pinned** in `schemas/specanchor/v1/`
    as byte copies of an immutable upstream commit. specanchor has published no release yet, so
    there is no tag to pin to. `scripts/specanchor-freshness.sh` (CI) proves three things: the
    copies were not edited in place, the recorded commit really serves those bytes, and upstream
    has not moved past them. It also turns red the day specanchor publishes a tag while the pin
    is still a commit. Air-gapped runs keep the offline check and skip the rest honestly.
  - **`schemas/authoring/v1/decisions.schema.json`** is the one new artefact: what a proposal
    decides about the manifest, the source of each decision, and every domain opt-in resolved
    into a question or a sourced answer. There is no `disable`. Providers, architecture,
    external systems and products are questions only.
  - **`corpus-check.sh` gates three schema families** (manifest, specanchor, decisions) with 34
    entries. It is also stricter. An invalid entry now counts as rejected only when specdrift
    returns findings (exit 1). Before, any non-zero exit counted, so a schema keyword the engine
    cannot evaluate (exit 2) made the whole negative half pass. That was proven by planting
    one. Each YAML invalid entry names its planted defect on a `# expect:` line, and the
    rejection must be for that reason.
  - **`goldpath spec ready`**: V1, every card's shape through `specdrift validate` against the
    schemas the CLI now carries. V2, every `manifest.proposed.yaml` against the app's own
    rules; an outbox proposed with `broker: none` gets SPEC0101. V3, ids unique across the
    repository, sources that resolve to existing lines inside it, spec→rule links, declared
    questions, and an honest open-bracket count. `--gate SPEC-nnnn --author <who>` adds
    ADR-0010's gate #1, which refuses an unapproved spec, a spec with open brackets, or a spec
    approved by its own author. `--next-id rule|spec` allocates ids, so nobody picks them by
    hand. The CLI reads the cards with the engine's own YAML parser and version (YamlDotNet
    18.1.0, now pinned here), so the two cannot read a file differently.
  - **`goldpath add feature <x> --plan`** prints the recipe as JSON: manifest lines, packages,
    provider edits, whether the model grows, and the decisions the team must take. It writes
    nothing and runs no engine. The decisions are word for word what the real run prints after
    it succeeds, because both now come from one method. A test pins this for all fourteen
    recipes against a twin app really grown.
  - **An empty `docs/domain/` skeleton** in the solution template and CorPay, held identical by
    `skills-parity.sh`. This changes what `dotnet new goldpath-solution` generates, so it rides
    the next train.
- **The golden-manifest matrix runs at PR time** (open-threads T30). ADR-0008 asks for it on
  every template or core package change; CI ran it only nightly, which is how preview.8 shipped
  GP2102 and how pull requests kept merging for days while the nightly was red. A pull request
  that touches what a generated app is built from — the templates, the CLI, the analyzers, the
  packages (the lane packs the source into a feed that shadows nuget.org), the console bundle,
  the manifest schema, the root build files or the lane itself — now runs eleven of the
  twenty-three shapes, in parallel, before merge, behind one check that always reports and so
  can be required: `golden manifests (PR subset)`. The eleven are chosen by coverage — every
  template flag value, every `add feature` recipe and every CLI verb that writes into an app
  runs in at least one of them; `golden-manifests-v1.md` §6a argues each choice and each shape
  left to the nightly. The shapes moved out of `nightly.yml` into `.github/gm-shapes.yml`, read
  by one reusable `gm.yml` for both lanes (and dispatchable on any branch), and docs-freshness
  holds the doc's table to the file's `pr: true` marks in both directions. Proven the way the
  thread demanded: a planted GP2102 regression in the solution template turned the PR red in
  `GmEverything` and `GmGrownRest` while the repository build stayed green, and its revert
  turned it green again.
- **Product console link tiles in the console registry (#257).** The console RFC promised that
  products "appear as configured LINK tiles", and the registry had nowhere to put a link: its one
  entry kind is a service the console PROBES, so a product with no Goldpath admin surface (the
  RFC's own Mockifyr example) could only have joined as a false UNREACHABLE row. Coexist, which
  mounts its own console beside the core one, was reachable only by typing its address.
  `GoldpathConsoleOptions.AddLink(name, url)` (and `Links`, `GoldpathConsoleLink`) adds a `links`
  array beside `services` in `console.config.json`; Today draws one native-link tile per product,
  and the ⌘K palette offers "Open {name}". A link is never probed — a tile asserts an address,
  never health. A links-only app OMITS `services` instead of sending an empty list, because the
  console reads an empty list as a broken registry. The URL is a root-relative path or an
  absolute http(s) address without credentials; anything else (`javascript:`, `data:`, `file:`,
  `//host`, a path-relative address, a control character) stops the app at startup, and the
  console re-checks a hand-written file. Additive: eight new PublicAPI lines, nothing shipped
  changes — take it blind. Decision record: `docs/rfc/goldpath-console-link-tiles.md`.

### Fixed
- **The `add feature notification` recipe had never run end to end.** The golden-manifests doc
  said the two grown shapes ran all fourteen recipes; they ran thirteen. `GmGrownRest` now
  grows `notification` too — found while choosing the PR subset by coverage.

### Added
- **Campaign revision R2 — asynchronous targets, shared ceilings, keyset takeover**
  ([RFC](docs/rfc/goldpath-campaign.md), revision R2 with its 2026-10-20 amendments;
  [upgrade guide](docs/upgrades/0.1.0-preview.10.md)). A design review for a device-fleet
  adopter found four places where the module and the adopter's architecture disagreed and
  the architecture was right. Typed handler results (`IGoldpathCampaignActionHandler<T>`:
  Succeeded / Failed with a code and a retryable flag / Accepted with a correlation id and an
  ack deadline), an `AwaitingAck` item state, an idempotent HTTP callback surface
  (`MapGoldpathCampaignCallbacks`, `POST {prefix}/{type}/{campaignId}/{correlationId}`) and an
  ack sweep that turns a missed deadline into a retryable failure; an idempotency key per
  attempt; a configurable, jittered retry ladder with the ripe instant persisted per item; a
  per-type `MaxTps` ceiling; a `Priority` (High 3 · Normal 2 · Low 1) that splits a contended
  shared ceiling by weighted max-min fair share, the fractional share carried tick to tick so
  nobody starves; a claim guard that reads the campaign state (pause means now) plus orphaned
  releases published again after `OrphanReleaseAfter`; and a keyset selector (`TargetsAfter`)
  so a takeover reopens the stream after the last key instead of re-reading every row. The R1
  handler contract is untouched.
- **Signed campaign callbacks** (campaign RFC R2.8). A target system authenticates each
  callback with an HMAC-SHA256 signature keyed by the campaign type's secret
  (`SignedCallbacks(secret, previousSecret)` — two active during a rotation), over the
  timestamp, method, path and body, inside `CallbackTimestampTolerance`; it needs no ops
  credential, and an unsigned callback is not possible. A callback that arrives before its
  acceptance is visible is parked and settles when the acceptance lands. New rule **GP1704**
  (Error): the callback surface mapped while no campaign type signs.
- **[schema]** Campaign R2: two columns on `GoldpathCampaigns`, five nullable columns on
  `GoldpathCampaignItems`, the `GoldpathCampaignParkedCallbacks` table, and the items'
  `(CampaignId, State)` index replaced by three. `goldpath db add CampaignR2R3` (one migration
  for R2 and R3 — see the next entry), then one line —
  `migrationBuilder.GoldpathCampaignR2Backfill()` — stamps the retries R1 left waiting. The
  three `CREATE INDEX` statements block writes on a large items table: apply between
  campaigns, or build them concurrently (the upgrade guide has both providers' statements).
- **[schema]** Campaign R3's schema, no behaviour yet (campaign RFC R3, slice R3-S1, #314).
  Eleven columns on `GoldpathCampaigns` (`StartAt`, `MaxAwaitingAck`, `AwaitingAckCount`,
  `PolicyVersion`, `SuspendOnFailureRatio`, `SuspendWindow`, `SuspendMinSample`,
  `WindowOutcomes`, `WindowFailures`, `WindowStartedAt`, `SuspendedReason`), the
  `GoldpathCampaignCeilings` table (`GoldpathCampaignCeiling`, `GoldpathCampaignCeilingKind`;
  primary key `(Kind, Name)`), and two appended campaign states, `Ready = 8` and
  `Suspended = 9`. Nothing changes on `GoldpathCampaignItems`, and there is no data step:
  every default is R2's behaviour, so the columns can be applied while campaigns run. R2 is
  unpublished, so preview.9 adopters generate ONE migration for both:
  `goldpath db add CampaignR2R3` (owner decision Q1). Nothing reads or writes the new columns
  until the behaviour slices R3-S2..S11 land.

### Changed
- **Three removed public members** in `Goldpath.Campaign`, all shipped in preview.8:
  `GoldpathCampaignEngine<TContext>.ExecuteItemAsync` takes the attempt number and returns
  the typed result (source- and binary-breaking); the positional `GoldpathCampaignInfo`
  constructor gains `Priority` before `WindowOpenNow` (source- and binary-breaking); the
  `GoldpathCampaignThrottle` constructor gains an optional trailing `Priority`
  (source-compatible, **binary-breaking** — recompile). Only code that drives the engine or
  builds the admin records directly is affected.
- **The campaign retry ladder's default changed**: 30 s, 2 min, then **10 min** for every
  later attempt (R1: 2 min), with **±20% jitter** on every rung (R1: exact). Pin R1 with
  `RetryBackoff = [30s, 2m]` and `RetryJitter = 0`.

### Fixed
- A retry re-released on a real broker could be dropped by the consumer's claim guard: the
  leader publishes before it marks (crash safety), and the retry's message reached a
  consumer while the row still said `AwaitingRetry`. A ripe `AwaitingRetry` row now claims;
  an unripe one still refuses. R1's ladder had unit proof only; the R2 real-broker
  integration is what found it.
- Aborting a campaign left its retries waiting on a rung forever (`AwaitingRetry`, never
  released again); abort now stamps them `Aborted` with the rest of the unsent work.
- A consumer that died mid-call on a paused, closed-window, aborted, expired or finished
  campaign stayed `Processing` forever — the stale-claim sweep visited only campaigns
  releasing that tick. It now reaches every campaign with work in flight.

## [0.1.0-preview.9] - 2026-10-19

The repair train — and the security train. preview.8 shipped four ways for a generated or grown
app to fail — `fileexchange` on its first build, a second jobs feature added with the CLI, `add
feature idempotency`, and `add feature multitenancy` failing the app's own smoke test — and the
clean-architecture layout did not build with `audittrail` or `idempotency`. All five are fixed,
and the release checklist now demands the matrix green on the release's own head. Four .NET
advisories published 2026-10-13/15 made every app generated on preview.8 fail its first restore;
this train carries the lifts in both templates. One type moved between packages: recompile. Read
the [upgrade guide](docs/upgrades/0.1.0-preview.9.md) — each section says whether it touches
you.

### Fixed
- **Every build and every generated app failed restore on four .NET advisories published
  2026-10-13/15.** `System.Security.Cryptography.Pkcs` (GHSA-846f-f764-76mx, GHSA-2fmv-
  rv2c-59x7) and `System.Security.Cryptography.Xml` (GHSA-c399-9853-6jmj, GHSA-7hqp-7c5m-4vq6)
  arrive transitively through SqlClient, MimeKit, Roslyn workspaces and Aspire; `MessagePack`
  (GHSA-qhrr-8q5h-9q3h) through Aspire's StreamJsonRpc. Under TreatWarningsAsErrors
  NU1902/NU1903 is a restore failure, so the nightly (every GM shape, every mutation scope,
  corpay, the console smoke) and every pull request went red at once. The repository lifts Pkcs
  to 10.0.13 on both target frameworks (MimeKit 4.18 already demands >= 10.0.0 on net8.0, so an
  8.0.x pin is a downgrade) with a direct reference in the three projects whose graphs carry it,
  the pattern the solution template already used for Xml; a repository-wide transitive pin was
  tried and rejected, because it turns every central version into a ceiling and collides with
  the deliberate per-TFM EF Core pins. The two templates and CorPay enable
  `CentralPackageTransitivePinningEnabled` and pin Pkcs 10.0.13, Xml 10.0.13 and MessagePack
  2.5.307 — a generated app is one TFM, so the pin is the lift wherever the package lands,
  including the projects an adopter adds later. An app already generated on preview.8 needs the
  same three pins until it regenerates.
- **`goldpath add feature multitenancy` failed the app's own smoke test** (#261). Tenancy is
  fail-closed and refuses a request without the tenant header before auth runs; the template's
  smoke sends it under `--features multitenancy`, and the recipe left the smoke alone. The
  recipe now adds the template's line, its `using` and the `Goldpath.Abstractions` reference.
- **`goldpath add feature idempotency` left an app that did not build** (#260). GP1001 fails
  the build while idempotency is composed and a command is unmarked; the template marks its
  sample `CreateOrderCommand` under `--features idempotency` and the recipe did not. It now
  emits the template's four lines (a test reads them from the template), and in
  clean-architecture adds `Mediant.Behaviors` with its pin to the project that compiles the
  sample. The published preview.8 CLI has the gap; the upgrade guide gives the one-line fix.
- **Every app generated with `fileexchange` failed its first build on preview.8** (#259).
  FileExchange is composed with no rail by design, GP2102 says so, and the generated apps
  promote warnings to errors. The templates keep GP2102 a warning (`WarningsNotAsErrors`); it
  still shows until the first `AddRail`.
- **A second jobs-riding `add feature` referenced `Goldpath.Jobs` twice** (#258) — NU1504,
  and the app stopped restoring. References are now checked per package against the whole
  project file.
- **The clean-architecture layout did not build with `audittrail` or `idempotency`** (#254).
  `IAuditLogged` moved to `Goldpath.Abstractions` (see Changed), and the `Application` project
  references `Mediant.Behaviors` under `UseIdempotency`. Found by `qorpe/coexist`; the nightly
  `GmCleanFeatures` shape now generates exactly that layout with those features.
- **The migration runbook's verify step could never pass** (#245): it demanded no occurrence
  of the old repository slug, which the runbook itself and `SECURITY.md` name on purpose. It
  now says what a clean result looks like.
- **`docs-freshness.sh` reported two of its three checks and then forgave them.** The script
  had `set -uo pipefail` but no `-e` and two separate blocks, so only the LAST block's exit
  code survived: a broken relative link and a retired tool name were printed and the gate
  exited 0 — while the file's own header claimed a broken link fails CI. Both checks now
  gate, and both were verified to fail on planted drift. Found by the maintainer stop-gate on
  the day it was written, which is the argument for the hook in one sentence.

### Changed
- **`IAuditLogged` moved from `Goldpath.AuditTrail` to `Goldpath.Abstractions`** (#254),
  beside the markers it belongs with. Same namespace — recompile, no source edits. A binary
  compiled against preview.8's `Goldpath.AuditTrail` must be rebuilt; the upgrade guide says
  why a type forward was withdrawn.
- **Every repository URL points at `qorpe/goldpath`** (#240), including the two that ship
  inside packages: `HelpBase`, the help link of all forty-nine analyzer rules, and
  `RepositoryUrl`, the Source Link target and nuget.org source link.
- **The release checklist requires the golden-manifest matrix green on the release PR's own
  head.** preview.8 was tagged against a green nightly that ran on the commit before its own
  template changes, which is how GP2102 shipped. The nightly `GmCleanFeatures` shape also gains
  `audittrail`, now that the templates consume `IAuditLogged` from the train.
- **The analyzer release ledger is true.** GP1901, GP1902, GP2101 and GP2102 shipped on
  preview.8 but were still listed as unshipped; they now sit in the shipped ledger.
- **T27 closes for the library repositories and T28 opens** (#253): the delivery cycle reached
  every named repository, and the sweep's one gap has its own thread.
- **The cycle's sixth step is stack-neutral**, because it was not. It named `spec_validate`,
  `spec_drift` and `specs/` — true for a generated .NET application and meaningless for an npm
  library, which is the next repository due to carry this text. The step now says: run THIS
  repository's own contract check, whatever proves that what is committed still equals what is
  built, and get it clean. Each repository's skill names its own; the sequence does not change
  between them. `skills-parity.sh` now also holds goldpath's own copy of `cycle.md` identical
  to the one the templates ship, while leaving its maintainer-shaped skills and hook alone.
- **The migration product module is `qorpe.coexist`, not `qorpe.sync`.** Two reasons, and the
  first decided it: an internal asset of the same name was under review in the same week by the
  same reviewer who owns this module, and a module sharing its name would have been read as a
  rename of it. The second is that "sync" named the MECHANISM — keeping two stores equal is how
  this works, not what it is for. The new name says the one thing true of every engagement, that
  both systems are live at once; the migration frame and the source's eventual retirement, usual
  but not guaranteed, live in the description. Thirteen documents, the manifest schema's example
  and two corpus fixtures moved; nothing had been published under the old name, so this is the
  cheapest the rename would ever have been.
- **CorPay consumes through the seam.** The sample took 0.1.0-preview.8 the day it
  published: three handlers instead of `IConsumer<T>` (`OrderPlacedHandler`,
  `PaymentExecutedHandler`, `WorkItemQueuedHandler`), twenty-one pins moved, queue names
  unchanged. It was the last line under the release checklist's next-train adoptions, and
  the messaging-exit RFC's CorPay box closes with it.

### Added
- **goldpath runs the discipline it ships** (delivery-cycle RFC D4). The repository carries
  `.claude/cycle.md` byte-identical to the templates', a `goldpath-change` skill for the
  maintainer's path, and a MAINTAINER-shaped stop gate: it builds only the projects whose
  files changed and runs this repository's own gates in place of the drift check an app would
  run. Copying the app-shaped hook would have meant a 23-package build on every turn end,
  which is slow enough that the hook would be deleted — and a deleted gate is worse than an
  absent one.
- **One delivery cycle, shipped with the templates** ([RFC](docs/rfc/goldpath-delivery-cycle-v1.md)).
  `.claude/cycle.md` carries nine steps in which a defect and a feature differ only in the
  first, and it deliberately holds no rules of its own — every rule stays in the document that
  owns it. New skill `goldpath-defect` (prove the cause with evidence, then prove the test
  fails before fixing); `goldpath-feature` gains the three steps it never had: proving the test
  goes red, running it for real and measuring, and watching the as-is.
- **The worker template gains the skills it was missing.** A worker was born with ONE skill;
  it now carries the same set in its own shape — no OpenAPI artefact, contracts are its
  integration events, and drift is between the manifest's trigger and what the host composes.
- **`scripts/skills-parity.sh`** — the agent layer is copied to three places and the claim that
  the copies match had nothing behind it. Five deliberate divergences are listed with their
  reason; anything else is red. Verified against a planted drift.
- The `goldpath-defect` eval, whose acceptance **reverts the fix and requires the new test to
  go red** — the cycle's own rule, checked rather than trusted.

### Dependencies
- .NET 10.0.12 patch line across the Microsoft.Extensions and EF Core packages, Quartz 3.21.0
  (via 3.20.1), Testcontainers 4.15.0 (#244, #255); `Microsoft.Extensions.Http.Resilience`
  10.9.0 (#247) and `Microsoft.Extensions.ServiceDiscovery` 10.9.0 (#256), both off the 9.x line;
  console dev dependencies (#241). 33 NuGet updates (#268) — among them MailKit 4.18.1 and
  **MassTransit 8.5.11**, a patch on the 8.x OSS line the messaging-exit RFC keeps; the CLI's
  `KnownVersions`, both templates and CorPay move with it — and 13 console dependencies (#270).

## [0.1.0-preview.8] - 2026-09-07

The seam-and-proof train. The consume seam reaches the generated apps, and a coverage
audit of every CLI verb, flag value and feature against what proves it closes four real
defects, gives six verbs their first end-to-end proof, gives the two newest modules the
analyzer rules and console journey they shipped without, and makes five ledger claims
true. Adopters: read the upgrade guide first — GP0405 becomes a Warning and GP1901 is a
new ERROR.

### Closed by the coverage audit (2026-09-05)

### Changed
- **Ledger honesty pass** (the coverage audit's fourth finding). Five claims the ledgers
  could not support are corrected in place, each saying what was wrong and when:
  the two registers disagreed about T1 (the CorPay nightly asserts the console is SERVED
  and inherits the floor; it does not DRIVE it, so the thread stays open and the
  overstating row is withdrawn); T7's trigger has fired and the thread now reads as
  unscheduled rather than blocked; the golden-manifest coverage table listed GM coverage
  for `ldap` and `saml`, values the manifest schema does not accept, so it now carries a
  third column saying what actually runs; an RFC row called `Goldpath.Sdk` unpublished
  though it shipped on preview.7; and a specanchor DoD box sat unchecked with all three
  of its sub-clauses satisfied.
- **`Goldpath.Analyzers` has a mutation gate.** The package that enforces forty-nine
  executable standards was the one with no ungameable metric over it, and the exclusion
  ledger carried no row explaining why. It scores 72.67 % and joins the nightly matrix,
  which is sixteen packages now. Its single exclusion (`Descriptors.cs`, forty-nine
  descriptor initialisers with no branching) is justified in the ledger.
- **Mutation scores are recorded.** The release checklist asked for "current" scores and
  nothing wrote one down, so the gate could be audited only by whoever ran it.
  `stryker/README.md` gains a measured-scores table and the checklist gains the line that
  fills it.
### Fixed
- **The wizard offered a module the template cannot take.** `goldpath new`'s module menu was
  the recipe list, which carries `outbox` — a recipe with no `--features` value, so choosing
  it printed and ran `dotnet new goldpath-solution --features outbox` and the template refused.
  The menu is now the template's own choice list, pinned against `template.json` by a test.
- **`goldpath add feature approvals` left the console rail over nothing.** The recipe wired the
  module and the jobs block but never emitted `app.MapGoldpathApprovalsAdmin(...)`, so a
  CLI-grown app showed an Approvals section with no endpoint behind it. Every admin-bearing
  recipe now has that invariant under test.
- **`goldpath new service|gateway` swallowed flags in silence.** `new service Billing --db
  sqlserver` ignored `--db` and generated a head on the solution's shape without a word; the
  two verbs now refuse any flag but `--path`, naming it.
- The usage text names `outbox` as an `add feature`-only recipe (it listed thirteen of fourteen).
### Added
- **The CLI has its own proof lane** (`scripts/validate-cli.sh`, nightly job `cli-proofs`).
  The golden-manifest lane proves the TEMPLATES — it calls `dotnet new` directly — so six
  verbs an adopter types had unit tests and nothing else: the wizard, `init`, `export
  compose`, `discover`, `db status` and `check` (which ran only in the CorPay job, against
  the PUBLISHED tool, never the working tree). The lane drives every one of them on a real
  generated app and asserts what each must produce, including `docker compose config`
  accepting the exported file and `check` naming each of its four steps.
- **Four nightly shapes for values that had none**: `GmOneViaCli` and `GmWorkerViaCli`
  generate through `goldpath new solution|worker` instead of `dotnet new` (the verb the
  guides teach had no end-to-end proof); `GmApiKey` proves the api-key floor, which was in
  the template's choices, the CLI's flag and the wizard and in no shape; `GmGrownRest` runs
  the eight `add feature` recipes `GmGrown` does not, behind an auth floor. The in-solution
  fleet gains a third worker so `add worker --trigger schedule` is proven too.
### Changed
- Mutation ledger honesty: the nine unrowed exclusions (Archival, Bulk and CLI) now carry their
  justification in `stryker/README.md`, and `ignore-methods: Log*` — which the ledger claimed
  for every package — is declared in all twenty-one configs instead of ten.
- The hosted-fit mutation matrix is called fifteen packages everywhere (it was ten, thirteen and
  fifteen in three places). `docs-freshness.sh` gained two gates: the spelled-out count must
  match the matrix, and every stryker exclusion must have a ledger row.


### Changed (this train, at the boundary)
- **The templates, the worker template and `goldpath add worker` consume through the seam** —
  `OrderPlacedHandler` / `WorkItemQueuedHandler : IIntegrationEventHandler<T>` registered with
  `bus.AddGoldpathHandler<TEvent, THandler>()`; queue names unchanged. **GP0405 is a Warning**
  from this train (was Info). Both templates and `goldpath add feature fileexchange` map
  `MapGoldpathFileExchangeAdmin()` behind the auth floor. CorPay moves its three consumers
  when it takes this train (it binds to the published packages).
- **Clean layout on postgres built red (NU1903)** — the Infrastructure class library lifted
  EF Design's vulnerable `System.Security.Cryptography.Xml` only under the SqlServer
  provider; the lift is now unconditional and the nightly gains `GmOneClean` (clean layout
  on the default providers + fileexchange), the shape that was missing.

### Changed
- **Kit-freshness gate tightened** — the console may now lag `@qorpe/ui` by at most ONE minor
  or 14 days (was two minors / 30 days); the three consumers had drifted three minors apart
  in August under the old window. Same change in mockifyr's copy.

### Added
- **The bus conformance suite** (ADR-0013 §3) — `tests/Goldpath.Messaging.Conformance`: eight
  facts a bus must hold for Goldpath, proven on real RabbitMQ + PostgreSQL through the seam
  (exactly-once on a re-delivered id, tenant + correlation across the broker, the poison
  ladder then the fault, a 1 MiB payload, version tolerance, the crash between commit and
  publish, a broker outage, a graceful stop draining across processes). The oracle is
  MassTransit 8.5.10; a second engine runs the same class. In the CI integration job.
- **`goldpath add worker --trigger jobs` refuses a solution without a jobs rider** — the
  in-solution fleet shares the app database's jobs tables, which the Api's context owns;
  without a rider it booted against nothing (found by the `GmWorkerInSolution` nightly).
  The refusal names the alternatives. The nightly shapes that grow an app by verbs now run
  `goldpath db add` after the recipes, as the verbs' next steps tell an adopter — the
  `GmGrown` shape's first nightly hung on a model without its migration.
- **`goldpath add worker` inherits the solution's floor and features** (the in-solution
  half of T25's worker parity): a worker added to an authed solution is authed — its fleet's
  admin surface sits behind the SAME ops policy instead of the hard-coded `exposeUnsecured`
  — and it composes the solution's cross-cutting features on its own context (audit rows,
  soft-delete filter, tenant isolation, data protection, the app-database lock; table-owning
  ones only where the trigger owns a context). The `GmWorkerInSolution` nightly shape proves
  it with an authed, three-feature solution growing a jobs worker and a queue worker.
- **The consume seam** (ADR-0013, messaging-exit RFC §5) — `IIntegrationEventHandler<TEvent>`,
  `IntegrationEventContext` (message id, correlation, tenant, retry attempt, headers) and
  `bus.AddGoldpathHandler<TEvent, THandler>()` / `AddGoldpathHandlers(assembly)`: an adopter's
  handler names no bus type, drains the SAME queue the consumer of that name drained, and
  rides the pipeline's retry, inbox and tenant restoration. Analyzer **GP0405** steers
  `IConsumer<T>` implementations toward it — Info in this train; Warning at the next boundary,
  when the templates, the CLI's worker skeleton and CorPay move (release checklist). With the
  publish seam of preview.7 this completes the application-side vocabulary the swappable
  engine needs.
- **The `GmGrown` nightly shape** — a lean app (`--auth none --broker none`) grown by five
  `goldpath add feature` recipes (audittrail, softdelete, locking, bulk, outbox), then
  built, drift-checked and smoked: the CLI verb's end-to-end proof (it had shipped
  UNPROVEN). Its first local runs found FOUR recipe bugs, fixed below.
- **`Goldpath.FileExchange` admin surface + the console's seventh module** (open-threads T22)
  — `MapGoldpathFileExchangeAdmin()` mounts `/goldpath/admin/fileexchange` (contract §7.1,
  READ-ONLY: `/rails` with live counts, `/files` newest-archive-first, `/quarantine` with each
  row's reason and age; R3 repeats; file names ride the query). The console gains "File rails"
  (rails, files, the quarantine sheet), a Today card ("Rows in quarantine") and triage rows;
  the `Goldpath.FileExchange` meter (files received/rejected, rows applied/quarantined/
  duplicate, per rail) and its Grafana board ship with the package. The reads ride a new
  `IGoldpathFileLedgerQueries` seam both shipped ledgers implement; a custom ledger without it
  is refused at startup in words. The templates and `goldpath add feature fileexchange` map
  the surface at the next train boundary (they may only consume PUBLISHED API — the
  release checklist carries the line); the console smoke host proves it against source now.
  **[schema]** `GoldpathFileQuarantine.QuarantinedAt` and `GoldpathFileArchive.ArchivedAt`
  columns join the ledger model — one additive migration for adopters on the EF ledger.
- **Ops packs for the seven ops-less packages** (open-threads T17 closed) — Messaging
  (runbook + board over the MassTransit meter), Data (runbook + board over EF Core's and
  Npgsql's meters), ApiDefaults (runbook: deprecated-version traffic, sunset procedure,
  cursor-invalid triage), Console (runbook + board over the admin routes), Sdk (the guard's
  two log lines as alerts), Locking.SqlServer (the applock half of the Locking pack),
  Abstractions (N/A by construction, written down). `Goldpath.ServiceDefaults` now
  subscribes to the `Microsoft.EntityFrameworkCore` and `Npgsql` meters — they never
  reached the collector before, whatever the RFC note said.
- **Mutation gates for the three thin floor suites** — `Goldpath.Console` (75%),
  `Goldpath.Messaging` (80%) and `Goldpath.ServiceDefaults` (70%) join the nightly matrix
  with the family thresholds (break 70); the corners the first runs left alive got tests
  (the sampler ladder, the correlation id's exact boundary, live/ready tag split, the
  guard's queue, the framework's own Fault messages passing the boundary guard, options
  binding).
- **Worker template concept parity** (open-threads T25, decided 2026-09-03) — `goldpath new
  worker` gains `--auth openid|apikey|none` for the MANAGEMENT head (the jobs admin surface
  and the console now sit behind the ops floor, or carry the visible `exposeUnsecured`
  opt-out under `none`) and `--features multitenancy|audittrail|softdelete|dataprotection|
  locking|notification|fileexchange` — the features whose concept exists in a process
  without business HTTP. The riders (notification, fileexchange) bring the jobs runtime and
  the console to a queue worker; the jobs worker on SQL Server now sets the jobs store
  provider (it silently defaulted to the postgres delegate before). The manifest schema's
  worker branch accepts `providers.auth` and the seven feature keys; the worker's drift
  profile carries the same feature⇄package⇄call pairs the solution has; the worker ships the
  `.claude/` guardrails (stop gate, format hook, `goldpath-manifest`, `breaker`) and
  `ops/grafana-worker-dashboard.json`. The wizard asks the worker the same questions and
  refuses table-owning features on a schedule worker with teaching text. Two nightly shapes
  prove it: `GmWorkerJobsAuthed` (401 on the admin surface and the console, sqlserver) and
  `GmWorkerQueueFeatures` (tenant on the message header, riders next to the inbox, console served).
- **`goldpath add feature outbox`** — the fourteenth recipe, the one schema key the CLI could
  not compose: births the bus with its RabbitMQ resource when the app has none, joins the
  existing bus when it has one; the three MassTransit tables join the model; the `using`
  the template hides behind a preprocessor symbol is written for real.
- **The console rides the FIRST jobs feature through the CLI** — `goldpath add feature
  archival|bulk|notification|campaign|approvals|fileexchange` now maps `MapGoldpathConsole`
  (secured, or the visible opt-out) exactly as the template does; a CLI-grown app no longer
  ends up with the admin API and no screen over it.
- **`goldpath new` (wizard) births workers too** — kind, trigger and database asked; the
  equivalent `goldpath new worker` command printed and delegated to.

### Fixed
- **Messaging: the correlation header was never stamped from inside a real request.** The
  publish filter read `goldpath.correlation_id` off `Activity.Current`, but by the time it
  runs MassTransit has started its own child activity for the send, so the tag (on the
  request activity) was one level up and the header stayed empty. The filter now walks the
  activity chain. Found by the new Messaging mutation gate.
- **`goldpath add feature outbox` on a `--broker none` app** — two bugs the GmGrown shape
  found: the recipe left `providers.broker: none` in the manifest, so the engine's own
  SPEC0101 refused the result it had just produced (the recipe now flips the broker, and
  `ManifestEditor.SetProviderScalar` exists for the next recipe that births infrastructure);
  and the born bus brought `PackageReference`s for packages the template pins only under
  `UseBroker`, so the restore failed with NU1010 (the recipe now adds the central pins —
  `Goldpath.Messaging` on the app's train, `Aspire.Hosting.RabbitMQ` on its Aspire line,
  `MassTransit.RabbitMQ` from `KnownVersions`, which a test keeps equal to the repo pin).
- **`goldpath add feature` wrote `using MassTransit;` into a method body** — `EnsureUsing`
  took `using var scope = …` (the template's migration block) for a using directive and
  inserted after it (CS1001). It now recognises directives only: column 0, a namespace,
  a semicolon.
- **The outbox recipe's model calls arrived without their using** — the three MassTransit
  table calls landed in `OnModelCreating` while the model file's `using MassTransit;`
  sits behind `UseBroker` in the template (CS1061). Recipes now carry model usings.
- **`goldpath new` without `-o`** resolves the app root to the template's name directory,
  so the post-generation `db init` and the first-contract commit run instead of silently
  skipping (they searched `<cwd>/src` before).

## [0.1.0-preview.7] - 2026-09-01

The platform train. Goldpath stops being only a set of packages and becomes a PLATFORM
(ADR-0012): `Goldpath.Sdk` ships, products declare themselves, and the CLI grows the
verbs an adopter actually starts with — `goldpath new` (service, gateway, bare),
`goldpath init`, `goldpath discover`, `goldpath export compose`. Two new Ring B modules
reach nuget for the first time (Approvals, FileExchange), the console federates its
sixth module, and every train from this one on carries an SBOM and signed provenance.
Upgrade guide: `docs/upgrades/0.1.0-preview.7.md`.

### Added
- **Goldpath.Approvals** (new Ring B module, core landed 2026-08-18): human approval
  workflows — amount-laddered authority chains declared as data, four-eyes/maker-checker
  enforcement, bounded delegation (depth one), deadline escalation with top-rung expiry,
  the worklist, and the full audit trail; lifecycle published as integration events.
  17 deterministic tests; `features.approvals` + `goldpath add feature approvals`.
- **Goldpath.FileExchange** (new Ring B module, core landed 2026-08-18): file-based
  integration rails as a unit — rails declared as data with baked closures, file-level
  contracts, idempotent `(file, line)` ingestion, per-row quarantine that never stops the
  batch, zero-duplicate replay/reprocess, archive marks; lifecycle published as
  integration events. 7-test planted-fault rig; `features.fileExchange` +
  `goldpath add feature fileexchange`.

  Both modules ship database-backed stores on the app's own DbContext
  (`AddGoldpathApprovalModel` / `AddGoldpathFileExchangeModel`, in-memory fallbacks behind
  the same seams) and full composition: manifest keys, `goldpath add feature`, and
  `dotnet new goldpath-solution --features approvals/fileexchange`.
- **Approvals v2 — the six product-proven ladder rules (RFC accepted, #185).** Rung
  quorums (`RequiredApprovals`), distinct eyes across the whole chain (`AlreadySigned`),
  mandatory rejection reasons, withdraw + resubmit with the audit chain
  (`SupersedesId`), the signatures store seam both stores implement, and the SCOPED
  engine consuming the scoped outbox publisher; `AddGoldpathApprovalsJobs` adds the
  5-minute escalation sweep as a standard `IGoldpathJob`.
- **Approvals joins the console (T21, #200).** The sixth federated module: worklist with
  quorum numbers, detail sheet with signatures and the numbered trail, decide verbs
  through the ENGINE under the caller's principal, triage rows and the Awaiting-decision
  Today card. Admin surface at `/goldpath/admin/approvals` (contract §7.1/R3, frozen verb
  envelope), six `goldpath_approvals_*` counters tagged by ladder, and the Grafana
  dashboard JSON under `ops/`.
- **`Goldpath.Sdk` — Goldpath is a platform (ADR-0012, RFC D1+D2, #142).** Products
  declare themselves; the ops floor (`AdminSurfaceGuard`, `AdminTenantScope`,
  `AdminPaging`) ships as the SDK every admin surface builds on.
- **The CLI an adopter starts with.** `goldpath new service|gateway` (#144) and
  `goldpath new` bare — the wizard that derives infrastructure from intent (#145),
  `goldpath init` (#147), `goldpath export compose` — the AppHost stays the only
  definition (#146), `goldpath discover`, `--help`/`--version` and the verb reference
  (#153), `--layout clean-architecture` (#143), and the closeout that takes
  approvals/fileexchange through every jobs rider end to end (#191).
- **Supply chain on every train (#156).** An SBOM (syft) and signed SLSA provenance ride
  with every published package; `SECURITY.md` says how to report.
- **Platform hardening from the product's scars (#177, #186).** Template dev-init
  advisory lock (two replicas boot once), a PR-time CVE gate, and the dependabot policy:
  majors are deliberate, never grouped.
- **Ledger honesty gates (#154).** `ledger-check.sh` compares every claimed issue/PR
  state against GitHub LIVE; `schema-honesty.sh` gates the schema claims.
- **GP0404 — the publish seam (#159, `Goldpath.Analyzers`).** Application code injecting
  MassTransit's `IPublishEndpoint` is an ERROR; publish through
  `IIntegrationEventPublisher` so a transport change never edits a handler (the
  messaging-exit RFC's first step). The one analyzer on this train that can turn an
  adopter's build red — the swap is one line per handler; see the upgrade guide.

### Changed
- **ServiceDefaults registers the discovery core the per-client handler needs (#197).**
  `AddServiceDiscovery()` rides the defaults, so a generated app's typed clients resolve
  without the adopter composing the core by hand (the api-portal pilot's
  delete-when-fixed workaround dies with this train).
- **Package document columns declare their own shape (#198).** Ten columns that hold
  DOCUMENTS (payloads, baselines, audit values, erasure evidence) say `HasMaxLength(-1)`
  instead of inheriting the conventions' 256 default, which refused or silently truncated
  real content on every conventions-shaped host — found live by the qorpe.apiPortal
  pilot as a 22001. Bounded human-text columns say their bound on purpose. One
  migration: `goldpath db add PackageDocumentColumns`.
- **The console composes from the published `@qorpe/ui` (#150, #151)** — the family kit
  on npm, both consoles on the same tokens.
- Dependency refresh (#192): Mediant 1.4.1, EF Core 10.0.x line, 42 pinned updates —
  and the templates follow the train's pins (the GM matrix enforces the agreement).

### Fixed
- **Jobs: the terminal flip recounts the ledger from row stamps (#152)** — a chunk
  landing during the flip can no longer strand a run in `Running`.

## [0.1.0-preview.6] - 2026-08-03

The operations train. Campaign revision R1 gives long-running campaigns their calendar
(excluded days, an end date that expires the remainder into a report, a per-item retry
ladder, one process-wide TPS gate); the admin contract reaches Revision R3 (repeatable
OR filters); and the console lands its full visual family (U6–U9) plus the housekeeping
set — typed responses, the bulk definition filter, three ops runbooks, analyzer batch 4.
Upgrade guide: `docs/upgrades/0.1.0-preview.6.md` (one migration; GP1002 may newly fail
a build that composes consumers without the EF inbox — deliberate, downgradeable).

### Added
- **Campaign revision R1 (`Goldpath.Campaign`).** `ExcludedDays` (policy-timezone day
  set — a paused calendar day, not a paused campaign), `EndDate` → the new
  `ExpiredIncomplete` terminal state (the remainder is a report, never a surprise send),
  a per-item retry ladder (`MaxAttempts`, 30s → 2m rungs, the new `AwaitingRetry` item
  state counts as in-flight and blocks completion; exhaustion goes to the repair queue
  with the LAST error), and opt-in `GoldpathCampaignOptions.GlobalTps` — one per-process
  bucket over every running campaign, capping allowance only. All live-adjustable
  through the throttle verb; refusals teach (excluding all seven days says "pause the
  campaign instead"). One migration: `goldpath db add CampaignR1`.
- **Admin contract Revision R3 — repeatable filters.** `?status=`, `?state=`,
  `?template=` and `?definition=` may repeat: values OR within a filter, filters AND
  together; a single value behaves exactly as R2 did. The console's facet filters are
  truly multi-select on the server's answer, never a client-side merge.
- **Bulk `?definition=` on `/batches` (#72)** — server-side, riding the existing
  `(Definition, State)` index; the console batch list grows the matching facet.
- **Typed responses for the four tenant-wrapped admin lists (#98)** — archival
  holds/erasures and bulk batches/errors declare their 200 schemas; a response-side
  export test pins the regression class.
- **Analyzer batch 4 (`Goldpath.Analyzers`, now 42 rules).** GP1001 (command without
  `[Idempotent]` while `AddGoldpathIdempotency()` is composed, warn), GP1002 (broker
  consumers composed without the EF inbox `AddGoldpathOutbox()`, **error**), GP1004
  (`[Idempotent]` with no declared key and no natural-key property, warn).
- **Ops runbooks for Idempotency, AuditTrail and SoftDelete** — every shipped module now
  carries `ops/`; honest-signals posture (no unshipped metric is cited).

### Changed
- **The console's visual family (U6–U9, `Goldpath.Console`).** One component vocabulary
  in the kit's single token file, swept across every panel; toolbars live INSIDE the
  table card; facet filters are multi-select; icon actions with tooltips; a density
  toggle; one hand-rolled Select; add/edit flows in modals; row detail in sheets; the
  bare console prefix and the bare port both land Home and the brand mark goes there
  too. The axe gate and the 28-journey console smoke ran green through the whole sweep.
- `GoldpathBulkAdminService.GetBatchesAsync` gains the `definition` filter parameter
  (the one shipped-signature change this train; the admin HTTP surface is additive).

## [0.1.0-preview.5] - 2026-07-28

The scheduling train. The admin contract's Revision R2 gives the console the other half
of what a fleet IS — its scheduler, its triggers, its calendars, its history — and the
console grows the screens to drive it, `pause-all` included. Plus two fixes every
single-app adopter feels on their first screen. Upgrade guide:
`docs/upgrades/0.1.0-preview.5.md` (one migration, no breaking API changes).

### Fixed
- **`Goldpath.Console`: an app that configures no service no longer warns its operator
  about a missing one.** `console.config.json` answered `{"services":[]}` for an
  unconfigured registry; the console reads an empty registry as a BROKEN one, so every
  single-app adopter — the common case — met “the service registry lists no service with a
  name” on their first screen. The endpoint now answers **404** (no registry), which the
  console has always read as “this service only”, silently. No configuration changes.
- **A paused job looked exactly like a running one.** The console read `job.paused` and
  `job.nextFireTime`, which `GoldpathJobInfo` has never carried. Both are now derived from
  the job's triggers, where the truth lives.

### Added
- **The scheduling surface (`Goldpath.Jobs`, admin contract revision R2).** The console
  could drive what the modules DO but only half of what a fleet IS. Now on the contract:
  `GET /fleets/{fleet}/status` — the fleet as the STORE sees it (job count, cluster
  members with their check-ins, and whether `pause-all` has stopped it), plus the instance
  the caller is connected THROUGH, named separately because Quartz's metadata is
  per-instance and a management head reports standby while the executors run; a trigger
  payload that carries what a cron string cannot
  explain (type, timezone, misfire instruction, priority, start/end, and a simple
  trigger's interval, repeat count and times-triggered); runs filterable by `?status=`,
  `?from=`, `?to=` with keyset paging via `?afterId=`; `POST`/`DELETE` on
  `/fleets/{fleet}/jobs/{job}/triggers[/{name}]` so a declared job can carry a second
  schedule; and the job's data map, read-only.
- **`GoldpathJobRun.TriggeredBy`** — `Scheduled`, `Manual`, `Rerun` or `Replay`. An
  unstamped fire is `Scheduled` by definition, so the column never invents an operator who
  was not there.
- **The console's scheduling surface (U5).** The Runs section opens into four: the fleet
  (state, cluster members, and the durable `pause-all`), its jobs with the triggers that
  decide when they run (add, remove, reschedule, and a read-only job data map), its
  calendars, and a run history filterable by state and date over a keyset walk. Reaching
  `pause-all` from a screen is new — it is the verb an operator wants at 03:00 and the
  console could not send it before.

### Migration required
One nullable column (`TriggeredBy` on the runs table). Generate it with
`goldpath db add AddJobTriggeredBy` after taking this train; existing rows keep `null`,
which the console reads as "not recorded" rather than guessing.

### Refused, deliberately
No route creates or deletes a JOB, and there is no endpoint that lists job classes —
composition is the manifest's and the code's (ADR-0001). The refusal is asserted by a test
over the route table, not just written down.

## [0.1.0-preview.4] - 2026-07-27

The console train. Goldpath ships an operations console — served by the adopter's own
management head, driving the frozen admin contract with the operator's own credentials —
plus the tenancy fix that the console's own gate uncovered. Upgrade guide:
`docs/upgrades/0.1.0-preview.4.md` (no breaking changes).

### Added
- **`Goldpath.Console` — the operations console, mapped by your app.**
  `app.MapGoldpathConsole()` serves the built single page from embedded assets, behind the
  SAME ops floor as the admin surfaces. Adopters never run Node: the dist is built by
  Goldpath's CI and shipped inside the package, which refuses to pack without it. The
  cross-service registry is configuration (`AddService(name, adminBaseUrl)`), not a file
  beside the dist.
- **The console itself** (`ui/console`, `ui/kit`): a TODAY triage screen across every
  configured service, then panels for all five modules — runs (trigger/pause/rerun/replay),
  bulk intake (upload → the engine's validation report → the four-eyes gate), the campaign
  governor (pacer numbers + live throttle + pause/resume/abort), notification evidence
  (masked recipients, three lenses, no verbs by contract) and archival (chain verification,
  keyed retrieval, hold/lift/erase). Capability-lit: a module the app never composed has no
  panel, and one that refuses says why in the SERVER's words.

### Changed
- **Mediant floor is 1.4.0** (`Goldpath.ApiDefaults`, `Goldpath.Auth`). Apps that pin
  Mediant themselves must bump; apps that do not are carried by the transitive reference.

### Fixed
- **Multi-tenancy is a MARKER, not "some `ITenantContext` exists".** Composing a broker
  registers an `ITenantContext` for message-scoped propagation, and the admin seam read
  that as "this app is multi-tenant" — so a SINGLE-tenant app that merely added messaging
  had its admin surfaces refuse: 400 on the tenant-scoped ones, 403 on campaign. Shipped in
  preview.3; invisible because the reference app composes multitenancy. `AddGoldpathMultiTenancy`
  now registers `GoldpathMultiTenancyMarker` and the seam asks for that. Multi-tenant
  behaviour is unchanged.

## [0.1.0-preview.3] - 2026-07-25

The security train — every actionable finding of the 2026-07-21 independent audit's
"security & correctness" block, closed. Upgrade guide: `docs/upgrades/0.1.0-preview.3.md`
(three behavioral breaks, each with its written opt-out/migration).

### Security
- **Admin surfaces are tenant-scoped (admin-contract revision R1, audit A1 — CRITICAL).**
  On multi-tenant apps every admin read/verb scopes to the ambient tenant; crossing the
  fence demands the new `goldpath-ops-all-tenants` policy and is logged with the actor.
  Surfaces whose rows carry no tenant column (campaign; archival holds/erasures) demand
  the privilege outright. Single-tenant apps unchanged. New analyzer `GP0904` flags an
  endpoint taking a caller-supplied tenant without the `AdminTenantScope` seam.
- **Erasure keeps the sealed hash (audit A2).** `PreErasureContentHash` preserves the
  chain-sealed content hash through redaction — chain verification never skips erased
  rows, and post-erasure tampering is now detectable.
- **Two fail-open defaults closed (audit A3).** OpenId with an authority but no audience
  refuses to start (`AllowAnyAudience=true` is the visible opt-out); SMTP is secure by
  default (`UseSsl=true`; plaintext needs `AllowInsecureTransport=true`).

### Fixed
- **`Strategy=None` + guarded admin route answered 500 (audit A4)** — the None path now
  registers the ops policies and a deny-only scheme; the admin floor refuses with an
  honest 401/403.
- `goldpath db init` commits the first contract; `goldpath db add` skips empty
  migrations; `add worker` generates `launchSettings.json` (Aspire endpoint inference);
  CWD-independent `db` verbs. (Merged after preview.2; first shipped here.)

### Added
- Guardrail hooks in the template (`.claude/settings.json`): post-edit whitespace format
  and a stop gate — the agent cannot end a turn on a red build.
- The edge-case checklist v0 inside `goldpath-test-gen` + the `breaker` agent.
- Event-contracts idiom (per-app `<Name>.Contracts` classlib) taught by `add worker`.

## [0.1.0-preview.2] - 2026-07-13

Upgrade guide: `docs/upgrades/0.1.0-preview.2.md` (no breaking changes).

### Fixed
- **The bulk ledger survives a kill between stamp and counter** — a process dying
  between a chunk's row-stamp save and its counter increment left
  `ExecutedRows + FailedRows` one short forever (money right, books wrong). The
  terminal flip now rewrites the counters from the row stamps — the rows are the
  truth, the counters their cache. Found by the two-executor kill-9 proof on CI
  hardware; pinned by an engine unit test.
- **Dotted solution names work end to end** (#24) — `dotnet new goldpath-solution -n
  Acme.Orders` now builds: `Projects.*` identifiers ride a derived safe-name symbol,
  the manifest schema accepts dotted PascalCase segments, and the owner slug derives
  kebab-clean. Proven by two new golden-manifest shapes (solution + worker) and pinned
  by the nightly matrix.
- **The first OpenAPI contract ships with generation** (#12) — `goldpath new`'s
  post-step commits the build-time export into `specs/`, so the first
  `goldpath add feature` passes its engine round-trip out of the box.

## [0.1.0-preview.1] - 2026-07-13

Upgrade guide: `docs/upgrades/0.1.0-preview.1.md` (first release — nothing to upgrade from).

### Added
- **Release hardening (H1–H8) — the enterprise-ready pass before first publish**:
  - *Migrations story (H1)*: migrations live in the APP; Development auto-migrates,
    production is EF bundle-first; `goldpath db init|add|status|bundle` (owner-aware);
    one table set has ONE owner (GP1801); three end-to-end proofs on real PostgreSQL
    including data-preserving column adds; the migrations runbook.
  - *Fail-closed admin surfaces (H2)*: every `Map*Admin` demands the `goldpath-ops`
    policy out of the box; opting out is visible (`exposeUnsecured: true`) and warned.
  - *End-to-end trace correlation (H4)*: run/chunk/replay spans; the operator's request
    traceparent crosses the Quartz boundary via the job data map; bulk batches pin the
    upload trace and every later span links back — one trace id per instruction,
    through the repair path; `docs/ops/trace-correlation.md`.
  - *Frozen admin API contract (H8)*: `docs/rfc/goldpath-admin-contract.md` — envelope,
    paging (`take` clamped [1,500] everywhere), failure nouns, full route inventory,
    with a route-freeze test.
  - *Two-executor bulk kill-9 proof + CI reference benches (H6)*: the winning executor
    killed mid-batch recovers on the second node with NO double payment; all bench
    numbers re-measured on pinned CI hardware into the ops docs.
  - *Versioning & support contract (H7)*: `docs/rfc/goldpath-versioning.md` — lockstep
    train, pre-1.0 rules, support window, the D4 release gate this entry satisfies.
  - Heavy proof gates moved to GitHub Actions: PR gates + integration, nightly GM
    matrix (7 shapes) + migrations proofs + mutation matrix, dispatchable benches.
- **Goldpath.Campaign S3 — campaign is a manifest capability (module COMPLETE — the execution
  ladder L1–L4 closes)** — `features.campaign` joins the manifest schema WITH the first
  cross-field rule: a solution enabling campaign cannot pick `broker: none` (the release
  path IS broker fan-out; the invalid corpus proves the rejection). `dotnet new
  goldpath-solution --features campaign` generates the order-winback sample (type keys as CODE
  constants; a `#error` guard says the broker rule at build time) wired into the SHARED
  jobs block (four riders, still ONE scheduler) AND the app's bus; `goldpath add feature
  campaign` mirrors it — the new `BusLines` seam registers consumers on THE bus, never a
  second one, and refuses a messaging-less app with the D8 teaching text. Drift guards a
  TRIANGLE (campaign + jobs + messaging); GmEverything grew to ELEVEN features.
- **Goldpath.Campaign S2 — the ops surface (audited verbs, live throttle, governor board)** —
  create/pause/resume/abort/throttle on `/goldpath/admin/campaign`, EVERY mutating verb
  audited (`GoldpathCampaignAudit`); the LIVE throttle patches the row and the pacer obeys
  within one tick (bench: exact); abort requires a reason and drains claimed items
  gracefully; GP1701–1703; Grafana governor board + 7 runbooks + measured performance
  (1M enumeration 65k rows/s, sink 57k outcomes/s).
- **Goldpath.Campaign S1 — governed mass-execution, L4 of the ladder** — durable target plan
  with watermarks + LIVE policy on the row, a LONG-LIVED single-leader pacer on Goldpath.Jobs
  (takeover from durable watermarks), publish-then-mark release + state-guarded consumer
  claim (double-send structurally impossible), batching outcome sink (30M items never
  mean 30M writes), stale-claim sweep + completion-flip repair filing, replay heals.
- **Goldpath.Notification S3 — notification is a manifest capability (module COMPLETE)** —
  `features.notification` joins the manifest schema; `dotnet new goldpath-solution --features
  notification` generates the order-confirmed sample (template keys and dedup keys as
  CODE constants — template keys are wire contracts, name them like APIs) wired into the
  SHARED jobs block (now composing three riders: archival, bulk, notification — still
  ONE scheduler); `goldpath add feature notification` mirrors it composition-aware through
  the `JobsOptionsLines` seam; the drift guard pair; GmEverything grew to TEN features.
- **Goldpath.Notification S2 — the read-only ops surface (queue health, evidence views)** —
  the admin API (`MapGoldpathNotificationAdmin`, `/goldpath/admin/notification`) is READ-ONLY BY
  DESIGN: requesting belongs to the app (the notifier), re-sending to the jobs console
  (`replay-items`) — an admin verb that could inject messages would be an evidence hole.
  Recipients are MASKED on every surface (`o***@e***`). Templates view with live state
  counts, template hash, retention window and the oldest-requested age; filtered
  notification queries (tenant fail-closed); suppression and failure reports. Queue
  gauges (`goldpath_notification_queue`, `goldpath_notification_oldest_requested_age_seconds` —
  a stuck channel pages BEFORE customers call) publish from both the templates view and
  the ~30-second send run; Grafana panel + six runbooks. Analyzers **GP1601** (a direct
  `SmtpClient` while Goldpath.Notification is referenced is an evidence hole — warning) and
  **GP1602** (a template without `DeleteBodyAfter` keeps personal data forever — info).
- **Goldpath.Notification S1 — one event becomes one message, with evidence** — the
  transactional-notification core: requests are EVIDENCE ROWS in the app's own database
  (a REQUIRED unique dedup key makes a retry storm land once — proven on the pg unique
  index; rendering happens AT REQUEST TIME so a missing token throws into the app's
  transaction and a bad notice never persists; the TEMPLATE HASH stamped into every row
  proves what was sent even after body retention nulls the content). `Sent` means
  **accepted by the channel** — named honestly; delivery-status feedback is deferred
  with a written trigger. Claim-before-send with a stale-claim sweep: an interrupted
  send repairs, NEVER silently re-sends; bounded in-attempt retry, exhausted → the jobs
  repair queue, `replay-items` is the human-confirmed re-send. `MaySend` suppression is
  evidence too; `NotBefore` is the quiet-hours field. Channels: email (MailKit, MIT)
  and webhook ship with ATTACHMENTS in the contract from day one; SMS is a documented
  seam. Proven on PostgreSQL with a REAL SMTP server (smtp4dev): the renewal mail
  actually lands — subject asserted through the server's API — and a dead webhook
  exhausts, repairs and replays into a live listener. The FOURTH jobs-riding feature.
- **Goldpath.Bulk S3 — bulk is a manifest capability (module COMPLETE)** — `features.bulk`
  joins the manifest schema; `dotnet new goldpath-solution --features bulk` generates the full
  composition (an OrderImport sample — row type + handler as a conditional file — wired
  into a SHARED `AddGoldpathJobs` block with archival: one scheduler however many jobs-riding
  features are enabled); `goldpath add feature bulk` mirrors it with a composition-aware
  recipe — on a jobs-wired app it inserts `AddGoldpathBulkJobs` INTO the existing scheduler
  configuration instead of opening a second one (a new `JobsOptionsLines` insertion seam
  every future jobs-riding feature reuses); TWO drift rows guard the pair; GmEverything
  grew to NINE features.
- **Goldpath.Bulk S2 — the intake ops surface (upload, report, gate)** — the admin API
  (`MapGoldpathBulkAdmin`, `/goldpath/admin/bulk`): octet-stream upload (`curl --data-binary
  @payments.csv` is the whole client story — no multipart ceremony) that fires the
  validate run IMMEDIATELY with the cron as safety net, a definitions view with live
  state counts and the awaiting-approval age, tenant-fail-closed batch queries, the
  value-free error report paged by row number, and approve/reject verbs stamping
  actor + note — the state machine's rows ARE the audit, no separate audit table.
  Intake gauges (`goldpath_bulk_batches` by state, `goldpath_bulk_awaiting_approval_age_seconds` —
  the human-in-the-loop alert) publish from both the definitions view and the
  minute-cron validate run; the Grafana panel pages past the gate SLA; six runbooks.
  Analyzers **GP1501–1503**: a ceiling-less definition is an error (an unbounded
  intake is a decision nobody made), `SaveChanges` inside a row handler is a warning
  (the engine batches per chunk), `AutoApprove` is visible as info.
- **Goldpath.Bulk S1 — file intake becomes a validated, approved, resumable run (L3)** — the
  intake half of the execution ladder's third rung: a content-addressed, streamed file
  store in the app's own database (identical bytes return the SAME batch — a client retry
  storm cannot double-pay; a REJECTED file may be resubmitted deliberately), RFC-4180 CSV
  v1 behind the `IGoldpathBulkFormat` seam (columns map by HEADER NAME — a reordered export
  cannot shift money between fields), typed per-row validation with in-file duplicate
  keys and VALUE-FREE error reports (row+field+message, never the data), a mandatory row
  ceiling refused whole, and an audited approve/reject gate (invalid rows block by
  default; a rejection needs a reason). Execution composes Goldpath.Jobs UNCHANGED: chunks
  over row-number space, the persisted CLAIM lands before any side effect (a row
  interrupted mid-flight goes to the repair queue instead of being silently re-sent —
  MDM constraint 2, proven), row failures ride THE repair queue and the jobs
  `replay-items` verb heals them (the last cleared failure flips the batch to
  Completed). Measured: 10k-row intake 0.39 s against the finance card's 5-minute
  budget; engine overhead ~0.17 ms/row. Proven on PostgreSQL with the real runner:
  trigger → validate run → gate → execute run → poisoned rows repaired → exactly-once
  sink reconciliation.
- **Goldpath.Archival S3 — archival is a manifest capability (module COMPLETE)** —
  `features.archival` joins the manifest schema (the schema-rejects-unimplemented rule
  held to the end: the key lands WITH the capability); `dotnet new goldpath-solution --features
  archival` generates the full composition (Goldpath.Archival + Goldpath.Jobs, an Order sample
  lifecycle, archive/jobs model contributions, both admin APIs mounted on the new
  `goldpath:features endpoints` anchor); `goldpath add feature archival` mirrors it into existing
  apps (provider- and connection-name-aware); TWO drift rows guard the pair — a manifest
  claiming archival without the jobs runtime is a finding. Admin-endpoint service
  parameters are now explicit `[FromServices]` (jobs + archival): build-time OpenAPI
  export runs with no connection string, and inference must never read a GET's service
  as a request body.
- **Goldpath.Archival S2 — the lifecycle ops surface (hold, erasure, verify)** — the admin API
  (`MapGoldpathArchivalAdmin`, `/goldpath/admin/archival`): definitions view with live numbers,
  tenant-scoped retrieval (fail-closed), legal hold place/lift (the hold row IS the audit —
  who/when/case; an active hold exempts purge AND erasure), and the KVKK/GDPR verb:
  **erasure redacts every `[GoldpathPersonalData]`-classified field INSIDE the stored document
  through the real DataProtection catalog**, re-stamps the content hash, marks the entry
  and writes the evidence row — the chain hash never changes, so verification reads the
  divergence WITH the mark as lawful and WITHOUT it as tamper (proven on PostgreSQL with
  the real module wired). Erasure is idempotent evidence: only CHANGED values count, a
  second request reports "nothing left". Plus the `Goldpath.Archival` meter (backlog, appended/
  purged, erasures, verify failures — the tamper alert pages on ANY, retrieval p95 against
  the 5s budget), the Grafana panel, six runbooks, and analyzers **GP1401–1403** (an
  archive that cannot honor erasure is a liability — error; guardless row retention —
  warning; lifecycle-less archive — info).
- **Goldpath.Archival S1 — the data lifecycle after "hot" (L2 composes itself)** — declarative
  GRAPH archives (EF include-paths; whole claim files as one tamper-evident document) and
  guarded ROW retention, executed as `IGoldpathJob`s on the Jobs module: chunked, checkpointed,
  resumable, visible in the same console. Integrity model: every entry seals a **ChainHash**
  at append (immutable; the chain links through it) plus a **ContentHash** of the current
  document — the audited erasure path (S2) will re-stamp content WITHOUT breaking the
  chain, and a divergence with no erasure record is tamper. The VERIFY job files findings
  straight into the jobs repair queue — tamper alarms ride the existing metric. Retention
  purges remove only a contiguous chain PREFIX; an active legal hold stops the purge at
  itself (and the chain still verifies via the purged-head anchor). Proven on PostgreSQL:
  archive-moves-graph, indexed retrieval, a raw-SQL tamper caught, hold blocking the purge,
  guarded row purge; CsCheck properties: any single-character corruption detected, graph
  round-trip is identity.
- **Goldpath.Jobs S3 — the jobs worker is a template choice (module COMPLETE)** — `dotnet new
  goldpath-worker --trigger jobs`: a chunked `NightlyReportJob` sample (plan by count, checkpoint
  per chunk, deadline set — GP1302 enforces the SLA in generated code), `workerType:
  scheduler-quartz` manifest with its cron, the SPEC0113 invariant (a quartz scheduler
  declares its cron), scheduler-quartz-gated drift rows, and the audited admin API mounted
  on the worker. GmWorkerJobs joined the GM matrix and runs the whole story: admin trigger →
  6/6 chunks → audit trail. The GM proof caught two REAL bugs before merge: the sample's
  int day-key silently became an identity column (EF convention; `ValueGeneratedNever` now,
  and the trap is documented), and a poisoned CHECKPOINT save could wedge a chunk as
  Claimed forever — the runner now retreats through a fresh scope (regression-tested).
  Interactive proof recorded: point-read p95 0.4→0.3ms under a full-tilt run (budget <10%).
- **Goldpath.Jobs S2 — the ops surface (§7.1's first full exemplar)** — the admin API
  (`MapGoldpathJobsAdmin`, `/goldpath/admin/jobs`): fleets DISCOVERED from the store (deploying a new
  worker kind just appears — D9), jobs with live trigger state, runs/run detail with the
  repair queue, and the verb set: trigger (+dry-run), pause/resume, fleet-wide
  pause-all/resume-all, reschedule (runtime cron override, D7), rerun (double-run-guarded),
  replay-items (through the job's `IGoldpathItemReplay` hook ON an executor — the type lives
  there), calendar CRUD (holiday/weekly/cron + which-triggers-ride-it). EVERY mutating verb
  writes an audit row (iron rule 2). Management heads now run NO scheduler at all —
  on-demand, never-started schedulers per fleet (zero cluster noise). Plus: the `Goldpath.Jobs`
  meter (§7 vocabulary — progress, items/s, checkpoint age, predicted-overrun-BEFORE-the-
  deadline, repair depth, duration), the Grafana dashboard template and seven runbooks in
  `ops/`, and analyzers GP1301–1303 (renumbered from 05xx: ids are wire contracts). Proven
  by the two-process integration suite driving every verb and asserting the audit trail.
- **Goldpath.Jobs S1 — L2 of the execution ladder lands (clustered)** — scheduled and
  long-running work on a CLUSTERED Quartz persistent store (exactly-once firing, misfire,
  `RequestsRecovery` failover) with the Goldpath run model on top: chunked/checkpointed/
  RESUMABLE runs, per-item repair queue, live progress + deadline prediction, completion
  chaining (`StartAfter<T>`), input version pinning, business-day calendars. The `qrtz_`
  schema ships as EF model contributions (`modelBuilder.AddGoldpathJobs()`) — the store never
  escapes migration discipline. Two modes: `AddGoldpathJobs` (executor; one cluster per worker
  kind) and `AddGoldpathJobsManagement` (API head, thread pool 0 — verbs everything, executes
  nothing). PROOF: real kill-9 (child processes) — node A killed mid-run, node B recovers
  the fire and RESUMES from the checkpoint, every chunk exactly once except at most the
  in-flight one; management member triggers a stored job it cannot execute; bench 100k
  items in 0.4s with 2.1ms/chunk checkpoint cost (budgets: 5min/25ms). Mutation 73.8%.
- **The `goldpath` CLI exists (Slice C — template completion DONE, Phase A closes)** — a dotnet
  tool (`Goldpath.Cli`, command `goldpath`) that WRAPS what exists, no new semantics: `goldpath new
  solution|worker` (template passthrough), `goldpath add feature <name>` (the drift profile's
  row applied to an existing app: manifest line + package + registration + model call +
  feature extras, landing on the `goldpath:features` anchors exactly as generation would), and
  `goldpath check` (specdrift validate + drift + build, one verb). Every `add` ends in an engine
  round-trip; a red engine restores every touched file byte-identical and fails loudly.
  Context-aware where the template was: audit registration infers the DbContext type,
  locking detects provider + connection name, idempotency composes with caching (and
  caching retires the memory fallback). The CLI embeds the manifest schema it was built
  from, so tool and contract cannot drift apart. Templates gained middleware/resources/
  references anchors for the same reason. Proof: a plain generated app transformed
  feature-by-feature into the everything-on shape by `goldpath add` alone — engine clean at
  every step, then the REAL smoke suite green on the CLI-composed app.
- **Template: the WORKER kind exists (Slice B of template completion)** — `dotnet new
  goldpath-worker --trigger queue|schedule`: a probe-carrying host (readiness/liveness are the
  worker's deployment contract too). `queue` generates an inbox-guarded MassTransit
  consumer (exactly-once: dedup rides the same DbContext transaction as the work) over
  postgres/sqlserver; `schedule` generates a BCL PeriodicTimer batch skeleton (RFC D3 — the
  Ring C Jobs module's landing pad), zero infrastructure. `kind: worker` manifests are lean
  by schema design (workerType + trigger; providers live in the owning solution) and get
  their own specdrift profile: workerType-gated wiring rows plus two teaching rules
  (SPEC0111 consumer-names-its-queue, SPEC0112 batch-consumes-nothing). GM matrix gains
  GmWorkerQueue + GmWorkerSchedule; the queue smoke publishes into the real broker twice
  with one MessageId and proves exactly-once end to end.
- **Template: Ring B features are now GENERATION choices (Slice A of template completion)** —
  `dotnet new goldpath-solution --features multitenancy --features audittrail ...` (7 choices):
  each selection generates its manifest line, package reference, `AddGoldpath*` registration and
  model call — exactly the drift profile's rows, so specdrift is the acceptance test.
  Feature deltas ship as conditional PARTIAL entity files; caching provisions a Redis
  resource in the AppHost; locking lives in the app database (postgres/sqlserver aware);
  multi-tenant smoke sends the tenant header. Proven: a 5-combination matrix
  (generate+validate+build+drift) and the everything-on shape through the full GM pipeline
  (GmEverything, real containers, 27s — now in the CI gm-matrix). `goldpath:features` anchors
  ship for the upcoming CLI's textual `add`.

### Changed
- **Mediant 1.1.0 → 1.2.0 (the composition loop closes)** — all three issues this asset
  filed shipped: #129 (GET positional-record binding), #130 (HybridCache-backed query
  caching — [Cacheable] hits now get L1+L2), #131 (a real default ICacheInvalidator —
  [InvalidatesCache] works, tag-based). Goldpath.Caching now registers AddMediantHybridCaching:
  both cache surfaces ride ONE HybridCache with one tag vocabulary. The deliberately-pinned
  no-op test broke on the version bump exactly as designed and flipped to the real
  semantics. Template pins bumped alongside.

### Added
- **Goldpath Skills v1 — the Phase 2 gate is MET** — the template now ships the agentic layer:
  `goldpath-feature` (business sentence → merge-ready slice; contract-first, engine-checked),
  `goldpath-manifest` (toggles with named consequences; manifest+wiring change together),
  `goldpath-test-gen` (spec-derived tests with a written context DIET — never reads
  implementations, foundation §8.2), the `breaker` agent (falsification as executable
  tests), and `.mcp.json` registering specdrift — "done" without clean
  spec_validate+spec_drift is not done (ADR-0004 made procedural). Every skill carries an
  outcome-only eval (`evals/skills/`, foundation §5's hard rule); the goldpath-feature eval ran
  end to end: 9/9 acceptance on a generated app. Its first run also caught a
  doc-vs-reality drift (folder-per-feature vs file-per-feature) — docs aligned.

### Added
- **Spec Engine v1 COMPLETE (M4 — goldpath integration)** — the template now ships the goldpath
  specdrift profile: 5 cross-field invariant rules (outbox⇒broker, l2⇒cache provider,
  redis-locking⇒cache provider, subdomain+apikey warning, hmac secret-store discipline) and
  the full Ring B wiring table (feature ⇄ package ⇄ registration call, value-gated where
  strategies differ) plus the committed-vs-built OpenAPI pair. validate-gm.sh runs
  spec-lint (validate + drift) on every generated shape; the CI pipeline gains the
  spec-lint job (valid corpus passes WITH rules, invalid corpus must fail). The deferred
  analyzer ledger was revisited against the engine: GP0403 moves up a layer (drift/AsyncAPI,
  engine v2); the rest recorded per-rule. Engine pinned at v0.4.0.

### Fixed
- **Two rots caught by specdrift's first run against this repo** (Spec Engine M1 proof):
  the corpus manifest `m2-service-full.json` still used `dataProtection.piiFields`, an
  option the schema dropped when the module shipped — refreshed to the current options;
  and the template's source manifest hardcoded `auth: openid` instead of carrying the
  `--auth` choice — now a `GOLDPATH-AUTH` replacement token, so the generated manifest tells
  the truth about the generated app.

### Added
- **CI gates authored end-to-end** — every
  local gate is now a pipeline job: build (PublicAPI/audit), format, full test suite, the
  Testcontainers integration suite over DinD, the mutation gate fanned out one job per
  package, the license gate (self-provisioning python3), the GM matrix with the three
  locally-proven shapes (authed default / open flow / sqlserver+no-broker), benchmarks and
  pack. Pipelines stay manual-only until the runner exists; the runner runbook makes the fix
  one step (requirements, registration commands, sizing, DinD alternatives) and the ENABLE
  edit at the top of the file is the single switch that turns local discipline into a
  machine gate (ADR-0005/0010). Definition is server-lint clean.

### Added
- **Goldpath.Analyzers batch 3 — the full Ring B rule backlog (12 rules, 23 total)** —
  GP0701/0702 (classified PII on integration events; classification without the
  DataProtection module), GP0801/0802/0803 (cache attribute misuse; raw cache keys),
  GP0901/0902/0903 (multi-tenancy wiring, manual tenant writes, filter-dodging without a
  visible Bypass), GP1101/1102 (raw lock names; leaked lock handles), GP1201/1202
  (anonymous-surface inventory; literal secrets on the Goldpath auth surfaces). All rules stay
  severity-configurable and suppressible with justification (ADR-0005); heuristic rules say
  so in their docs. The analyzer backlog is now EMPTY except the recorded manifest-dependent
  deferrals (GP0101/0103/0203/0403/1001/1002/1004).

### Added
- **Test hardening (foundation §8 alignment)** — the mutation gate now covers EVERY package
  (`scripts/mutation-gate.sh`: per-package Stryker configs under `stryker/`, break-at 70 —
  below the threshold, no merge), closing the drift where only Goldpath.Data was mutation-scored.
  Property-based coverage (CsCheck) added to the algorithmic surfaces that shipped
  example-based: the TenantId grammar (any invalid character anywhere rejects; boundary
  exact), the cache-key convention (distinct tenants never collide for any area/key), and
  the lock-name convention (distinct tenants never contend; global/tenant namespaces
  disjoint). Goldpath.Locking's metered decorator gained direct unit coverage (all four acquire
  shapes, contended and not). Gate scores at introduction: Abstractions 95.2 · SoftDelete 91.7
  · Locking 91.7 · AuditTrail 80.4 · DataProtection 79.0 · Data 76.9 · Auth 76.4 ·
  Idempotency 76.0 · Caching 73.9 · MultiTenancy 72.6 — every package ≥ break 70. The gate
  run itself drove 30+ new tests (metric emission per acquire path, guard messages, option
  branches, the audit schema contract, claim fallback chains) and one justified named ignore
  (Redis's eager-connect factory — container-tested, unit-unreachable).

### Added
- **Goldpath.Auth (Ring B companion — the last cross-cutting gap)** — the manifest's
  `providers.auth` gets its implementation: OIDC/JWT bearer composed against any IdP
  (openid, default) or a minimal named-client API-key handler; secure-by-default fallback
  policy (anonymous is an explicit `[AllowAnonymous]`; probes exempted in
  MapGoldpathDefaultEndpoints); token–tenant binding with MultiTenancy (mismatch = 403 +
  `goldpath_auth_tenant_binding_rejects_total`); Mediant `[Authorize]` composed on the same
  principal for command-level checks. Template gains `--auth openid|apikey|none`; the
  authed golden-manifest shape is GREEN (secure-by-default 401 + green probes is the
  first-click contract). saml/ldap are schema-rejected strategic deferrals.

### Added
- **Goldpath.Locking (Phase 2, item 14 — Ring B COMPLETE)** — Medallion DistributedLock composed
  (MIT): Redis / Postgres advisory locks / SQL Server sp_getapplock behind manifest-driven
  provider selection; application code injects Medallion's own `IDistributedLockProvider`
  (no wrapper; metrics via a decorator over the same interface —
  `goldpath_lock_acquire_total{outcome}`, `goldpath_lock_wait_seconds`). Tenant-scoped lock names
  (`GoldpathLockNames`, fail-closed without an ambient tenant; explicit `Global()` escape).
  The same contract test proven on real Redis AND Postgres. Fencing tokens honestly out of
  scope: locks reduce duplicate work, idempotency guarantees correctness — the modules compose.
  The SQL Server provider ships as the OPTIONAL `Goldpath.Locking.SqlServer` package: the license
  gate caught that its chain carries Microsoft's proprietary-but-free SqlClient SNI runtime —
  scoped exception recorded in the gate itself; the core Goldpath.Locking graph stays fully OSS.

### Added
- **Goldpath.MultiTenancy (Phase 2, item 13)** — the tenant becomes ambient truth on every seam:
  header/subdomain resolution (fail-closed 400 with exempt paths), a live tenant query filter
  on every `IMultiTenant` entity (context-rooted — the only shape EF re-evaluates per query),
  automatic stamping, and a cross-tenant write guard that throws and counts a security metric
  (`goldpath_tenant_write_guard_trips_total`). Explicit `GoldpathTenant.Bypass()`/`Use()` scopes.
  THE FULL SQUARE proven on real Postgres+RabbitMQ: HTTP tenant survives middleware → EF
  stamp → outbox → broker → consume restore → consumer-side stamp. Supporting changes:
  Abstractions gains `GoldpathAmbientTenant` (flow carrier), Data gains `GoldpathQueryFilters`
  (AND-composition; SoftDelete migrated — two modules' filters no longer erase each other),
  Messaging's consume filter restores the ambient tenant. Strategic deferrals per approval
  condition (path-prefix, db-per-tenant): tracked in the module plan, REJECTED by the
  manifest schema until implemented.

### Added
- **Goldpath.Caching (Phase 2, item 12)** — Microsoft HybridCache as the app surface (L1 + Redis
  L2 per manifest, stampede-protected, tag invalidation) and Mediant
  `[Cacheable]`/`[InvalidatesCache]` as the query surface, composed without wrappers.
  Tenant-scoped key convention `goldpath:{tenant}:{area}:{key}` (`GoldpathCacheKeys`) baked in ahead of
  MultiTenancy. L2 proven on real Redis (Testcontainers): cross-host L2 read, tenant isolation.
  Known gap recorded: Mediant 1.1.0 ships no `ICacheInvalidator`, so query-path eviction is
  TTL-only — filed as mediant#131 (pinned by a deliberately-failing-when-fixed test);
  query-path L1 filed as mediant#130.

### Added
- **Goldpath.DataProtection (Phase 2, item 11)** — classify a property ONCE (`[GoldpathPersonalData]`/
  `[GoldpathSensitiveData]` attributes or the type-safe code catalog for untouchable entities) and
  every sink masks it consistently: audit change rows (per-property masking replaces the
  all-or-nothing `namesOnly` as the recommended posture), MEL log redaction (the Goldpath
  attributes are Microsoft `DataClassificationAttribute`s — `EnableRedaction()` +
  `[LogProperties]` works natively), and Mediant audit patterns (catalog names feed
  `SensitivePatterns`; Mediant `[SensitiveData]` recognized by name). Redaction composed from
  Microsoft.Extensions.Compliance: `***` erasure default, HMAC pseudonymization opt-in.
  Goldpath.Abstractions gains the classification contracts and its single micro-dependency
  (Microsoft.Extensions.Compliance.Abstractions).

### Added
- **Goldpath.Analyzers batch 2 — Ring B module guards** — GP0501 (`IAuditLogged` entity with a
  DbContext present but no `AddGoldpathAuditLog()` call, error), GP0502 (manual writes to audit
  stamp fields from application code, warn; save contributors exempt), GP0601
  (`ISoftDeletable` entity with a DbContext present but no `ApplyGoldpathSoftDelete()` call, error),
  GP1003 (`[Idempotent]` on a Mediant query — a no-op, info). Entity-only assemblies are
  exempt from the wiring checks; all rules remain severity-configurable and suppressible with
  justification (ADR-0005).

### Added
- **Goldpath.SoftDelete (Phase 2, item 10)** — deletes of `ISoftDeletable` entities become stamped
  updates touching exactly three columns; a global `!IsDeleted` filter hides them everywhere
  (`ApplyGoldpathSoftDelete()`); hard deletion is the explicit `GoldpathSoftDelete.Suppress()` scope
  (right-to-erasure flows). AuditTrail interplay proven: a soft delete audits as the
  `IsDeleted false→true` change — one story, same transaction. Data seam gained contributor
  ordering (`IEntitySaveContributor.Order`, additive default member; SoftDelete runs at −100).

### Added
- **Goldpath.AuditTrail (Phase 2, item 9)** — two audit levels, one correlated story: Mediant
  `[Auditable]` command audit (EF store composed) + Goldpath entity audit via the Data
  save-contributor seam — stamps auto-filled and old→new change rows written in the SAME
  transaction as the change (rollback leaves no audit rows — proven). New `IAuditLogged`
  marker (change rows) alongside `IAuditedEntity` (stamps). `IUserContext` added to
  Abstractions (HTTP-claims implementation included); `GoldpathSaveContext` carries `User`;
  the Data interceptor now snapshots entries so contributors can add rows safely.

### Added
- **Goldpath.Idempotency — Ring B opens (Phase 2, item 8)**: the HTTP `Idempotency-Key` middleware
  composed on Mediant 1.1.0's idempotent-operation coordinator (#114/#115 — one store, one
  semantics across HTTP and `[Idempotent]` commands). Behavior locked per the RFC: replay is
  byte-for-byte (`Goldpath-Idempotent-Replay: true`), concurrent duplicate → 409 (or Wait mode:
  serialize + replay), key reuse with a different payload → 422 (SHA-256 fingerprint),
  key scope `http:{tenant}:{method}:{path}:{key}`, only 2xx stored. Manifest schema gained
  the idempotency options object.

### Added
- **Phase 1 gate items (7c part 2)** — build-time OpenAPI export in the template (the
  deterministic Spec Engine `drift` input; config made docgen-tolerant), the GM matrix as a
  CI job (manual until the DinD spike passes on company runners), and **pipelines re-enabled**
  (MR/main). Architecture shapes proposed as 7d parallel to early Phase 2 (decision pending).

### Added
- **Ops decisions + gates (7c part 1)** — foundation §7.1: the three-surface ops rule
  (Observe=dashboards, Operate=InfraOps portal with "no screen without an Admin API /
  no action without audit", Configure=manifest) and the central-logging decision
  (MEL+OTel+Collector; deliberately not Serilog — sink choice is environment config, never
  code; reference collector config in the ServiceDefaults ops package). **License gate live:**
  scripts/license-gate.py — 217 packages (transitive included) all free/OSS; SPDX OR/AND
  semantics; PostgreSQL license allowlisted; verified legacy list. CI: license-gate (blocking),
  benchmark and DinD-spike jobs (manual until pipelines resume).

### Added
- **Template shape choices (7b part 2)** — the selection story begins, with the rule
  "a choice is only offered once its combination is proven": `--db postgresql|sqlserver`,
  `--broker rabbitmq|none`. Conditional composition throughout (packages, AppHost resources,
  Program.cs, outbox lock provider, manifest); `broker=none` generates zero messaging code.
  `scripts/validate-gm.sh` generalizes the local proof per shape (GM-1 and the GM-4 shape green).

### Added
- **Integration suite (7b)** — real-container proofs (Testcontainers, postgres:17 + rabbitmq:4):
  THE outbox atomicity proof (a rolled-back transaction publishes nothing; a committed one
  delivers exactly once) and Postgres keyset parity (composite DateTimeOffset walk with
  duplicate-key pressure, Guid descending walk, member-init projection). Designing the proof
  surfaced a real gap: `AddGoldpathOutbox` now auto-registers the consumer-side EF inbox on every
  receive endpoint (previously only the publish side was outboxed).

### Added
- **Goldpath.Templates (7a)** — `dotnet new goldpath-solution` scaffolds the GM-1 golden-path shape:
  Aspire AppHost (postgres+rabbitmq containers), Ring A floor, a Mediant vertical-slice
  walking skeleton (idempotency-ready POST, keyset-paginated GET, outboxed integration event
  + consumer), manifest, CLAUDE.md family, pinned everything, and a smoke suite that drives
  the REAL AppHost. `scripts/validate-gm1.sh` = the local ADR-0008 proof (pack → install →
  generate → build → smoke). The walking skeleton immediately earned its keep: it surfaced
  the Mediant positional-record GET-binding gap (mediant#129) and the Goldpath.Data projection
  rule (member-init projections only — now documented in the Data README).

### Added
- **Goldpath.Analyzers** — executable standards (ADR-0005) as a Roslyn analyzer package
  (netstandard2.0, `analyzers/dotnet/cs`): GP0102 (new HttpClient), GP0202 (Skip/Take
  offset pagination), GP0301 (DateTime on marked entities), GP0302 (unguarded runtime
  Migrate/EnsureCreated), GP0303 (interpolated raw SQL — error), GP0401 (publishing
  without IIntegrationEvent — error), GP0402 (notification cross-marked — error).
  Rules match types by metadata name (zero hard dependencies; inert when the seam is absent —
  L1 à-la-carte safe). Deferred rules (0101/0103/0203/0403) recorded in the RFC.

### Added
- **Goldpath.Messaging** — the message-path floor (`net8.0;net10.0`, transport-neutral, pinned to
  the **MassTransit 8.x OSS line** — v9 went commercial; exit strategy in the RFC): the
  Mediant/MassTransit event boundary with a runtime `IIntegrationEvent` guard (GP0401 until
  the analyzer ships), kebab-case topology, tenant/correlation header propagation with
  consume-side restoration (`GoldpathMessageTenantContext`), retry defaults (immediate ×3 +
  delayed redelivery 5m/15m/30m → error queue), and `AddGoldpathOutbox<TContext>` composing
  MassTransit's EF transactional outbox (30m dedup window).

### Added
- **Test hardening + repo CI**: repo CI pipeline (build/test/mutation/pack on every MR),
  Stryker.NET mutation testing on Goldpath.Data (score 65.67% → 81.16% after targeted hardening;
  break threshold ratcheted to 70), CsCheck property-based tests (cursor codec fuzz +
  keyset walk invariant over random datasets), BenchmarkDotNet baseline for the cursor codec.
  The hardening pass caught and fixed a real defect: the enum-to-string convention silently
  overrode explicit `HasConversion<int>()` provider-type conversions.

### Added
- **Goldpath.Data** — the data-path floor (`net8.0` EF8 / `net10.0` EF10, provider-neutral):
  keyset cursor-pagination executor (`ToPageAsync`, 1-2 keys with per-key direction, self-ordering,
  size+1 last-page detection, never OFFSET; invalid cursor → `GoldpathInvalidCursorException`),
  the save-contributor seam (`IEntitySaveContributor` + single interceptor with clock/tenant
  context — Ring B modules plug in here), and model conventions (string 256, decimal 18,4,
  enum-as-string, `TenantId` conversion). Migration policy per RFC D1: dev auto-migrate,
  production bundle-only. NuGet audit caught the vulnerable SQLitePCLRaw native lib
  (GHSA-2m69-gcr7-jv3q, test-only dep) — lifted to patched bundle 3.0.3. Third catch of the gate.
- **Goldpath.ApiDefaults** — API surface conventions (`net8.0;net10.0`): URL-segment versioning
  (default v1, versions reported), the keyset cursor-pagination wire contract
  (`PageRequest`/`Page<T>`/`GoldpathCursor` — opaque base64url cursors, no total count by design),
  JSON wire defaults (camelCase, enums as strings, null-writes ignored), and deterministic
  OpenAPI generation with a Development-only interactive endpoint (net10-only pillar).
  NuGet audit blocked the vulnerable transitive `Microsoft.OpenApi` 2.0.0 (GHSA-v5pm-xwqc-g5wc);
  lifted to patched 2.9.0 via a direct pinned reference.
- **Goldpath.ServiceDefaults** — the Ring A floor (`net8.0;net10.0`): OpenTelemetry
  (traces/metrics/logs, profile-driven sampling, OTLP + dev console exporter), health
  endpoints (`/health/live`, `/health/ready` — always mapped, masked), RFC 9457
  ProblemDetails with `correlationId`/`traceId` extensions, correlation middleware
  (`X-Correlation-Id`), HTTP resilience + service discovery client defaults, and a global
  concurrency guard (429 as ProblemDetails). One call: `AddGoldpathServiceDefaults()` +
  `MapGoldpathDefaultEndpoints()`. Ops baseline (dashboard/alerts/runbook) included.
  Note: NuGet audit caught known CVEs in OpenTelemetry 1.11.x during development —
  pinned to patched 1.16.0 (the supply-chain gate working as designed).
- **Goldpath.Abstractions** — zero-dependency contract layer (`net8.0;net10.0`):
  `TenantId` (validated value type) + `ITenantContext`; entity capability markers
  `IAuditedEntity`, `ISoftDeletable`, `IMultiTenant`; `IIntegrationEvent` marker
  (broker-bound events, per the Messaging boundary); `GoldpathHeaders` canonical header names.
  Public API locked with PublicApiAnalyzers.
- Solution infrastructure: pinned SDK (`global.json`), central package management
  (`Directory.Packages.props`, everything pinned), warnings-as-errors + XML docs enforced
  (`Directory.Build.props`), single explicit NuGet feed (`nuget.config`).
