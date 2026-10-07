# Changelog

All notable changes to the Goldpath packages are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) · Versioning: SemVer.

## [Unreleased]

### Added
- **Campaign revision R2 — asynchronous targets, shared ceilings, keyset takeover**
  ([RFC](docs/rfc/goldpath-campaign.md), revision R2). A design review for a device-fleet
  adopter found four places where the module and the adopter's architecture disagreed and
  the architecture was right. Typed handler results (`IGoldpathCampaignActionHandler<T>`:
  Succeeded / Failed with a code and a retryable flag / Accepted with a correlation id and an
  ack deadline), an `AwaitingAck` item state, an idempotent HTTP callback surface
  (`MapGoldpathCampaignCallbacks`) and an ack sweep that turns a missed deadline into a
  retryable failure; an idempotency key per attempt; a configurable, jittered retry ladder
  with the ripe instant persisted per item; a per-type `MaxTps` ceiling; a `Priority`
  (High 3 · Normal 2 · Low 1) that splits a contended shared ceiling by weighted max-min
  fair share; a claim guard that reads the campaign state (pause means now) plus orphaned
  releases published again after `OrphanReleaseAfter`; and a keyset selector
  (`TargetsAfter`) so a takeover reopens the stream after the last key instead of
  re-reading every row. One migration: `goldpath db add CampaignR2`. The R1 handler
  contract is untouched.

### Changed
- `GoldpathCampaignEngine<TContext>.ExecuteItemAsync` takes the attempt number and returns
  the typed result; `GoldpathCampaignInfo` carries `Priority`; `GoldpathCampaignThrottle`
  accepts an optional `Priority`. Source-level only, for code that calls the engine or
  constructs the admin records directly.

### Fixed
- A retry re-released on a real broker could be dropped by the consumer's claim guard: the
  leader publishes before it marks (crash safety), and the retry's message reached a
  consumer while the row still said `AwaitingRetry`. A ripe `AwaitingRetry` row now claims;
  an unripe one still refuses. R1's ladder had unit proof only; the R2 real-broker
  integration is what found it.

### Changed
- **The cycle's sixth step is stack-neutral**, because it was not. It named `spec_validate`,
  `spec_drift` and `specs/` — true for a generated .NET application and meaningless for an npm
  library, which is the next repository due to carry this text. The step now says: run THIS
  repository's own contract check, whatever proves that what is committed still equals what is
  built, and get it clean. Each repository's skill names its own; the sequence does not change
  between them. `skills-parity.sh` now also holds goldpath's own copy of `cycle.md` identical
  to the one the templates ship, while leaving its maintainer-shaped skills and hook alone.


### Changed
- **The migration product module is `qorpe.coexist`, not `qorpe.sync`.** Two reasons, and the
  first decided it: an internal asset of the same name was under review in the same week by the
  same reviewer who owns this module, and a module sharing its name would have been read as a
  rename of it. The second is that "sync" named the MECHANISM — keeping two stores equal is how
  this works, not what it is for. The new name says the one thing true of every engagement, that
  both systems are live at once; the migration frame and the source's eventual retirement, usual
  but not guaranteed, live in the description. Thirteen documents, the manifest schema's example
  and two corpus fixtures moved; nothing had been published under the old name, so this is the
  cheapest the rename would ever have been.

### Fixed
- **`docs-freshness.sh` reported two of its three checks and then forgave them.** The script
  had `set -uo pipefail` but no `-e` and two separate blocks, so only the LAST block's exit
  code survived: a broken relative link and a retired tool name were printed and the gate
  exited 0 — while the file's own header claimed a broken link fails CI. Both checks now
  gate, and both were verified to fail on planted drift. Found by the maintainer stop-gate on
  the day it was written, which is the argument for the hook in one sentence.

### Added
- **goldpath runs the discipline it ships** (delivery-cycle RFC D4). The repository carries
  `.claude/cycle.md` byte-identical to the templates', a `goldpath-change` skill for the
  maintainer's path, and a MAINTAINER-shaped stop gate: it builds only the projects whose
  files changed and runs this repository's own gates in place of the drift check an app would
  run. Copying the app-shaped hook would have meant a 23-package build on every turn end,
  which is slow enough that the hook would be deleted — and a deleted gate is worse than an
  absent one.


### Added
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


### Changed
- **CorPay consumes through the seam.** The sample took 0.1.0-preview.8 the day it
  published: three handlers instead of `IConsumer<T>` (`OrderPlacedHandler`,
  `PaymentExecutedHandler`, `WorkItemQueuedHandler`), twenty-one pins moved, queue names
  unchanged. It was the last line under the release checklist's next-train adoptions, and
  the messaging-exit RFC's CorPay box closes with it.


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
