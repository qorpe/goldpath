# Mutation gates — what is scored, what is excluded, and why

One config per package (`Goldpath.<Package>.json`), all at the same thresholds: high 85,
low 75, **break 70** — `scripts/mutation-gate.sh <Package>` runs one, the nightly matrix
runs the hosted-fit set, `mutation-heavy.yml` the six long-running ones. Stryker JSON
cannot carry comments, so every `mutate` exclusion and every `ignore-methods` entry is
justified HERE (ADR-0005: suppression is visible and justified at every layer). An
exclusion without a row below is a finding.

## Which tests score a package

A package is scored by the test projects its config names in `test-projects` — and by
nothing else. That sentence was false until 2026-10-20: the gate ran Stryker from the
repository root, where it finds `Goldpath.sln` and switches to **solution mode**, and solution
mode ignores `test-projects` and runs every test project whose build references the package.
Abstractions was scored by 702 tests, the integration host among them, instead of its own
suite; every package was scored the same way. The gate now runs Stryker from the package's
own directory (so `test-projects` are written `../../tests/...`), and it fails a run whose log
shows solution mode or a test-project count other than the config's. `docs-freshness.sh`
checks that every listed test project exists.

A test project other than the package's own raises the package's score, so each one is
justified here the way an exclusion is. A borrowed suite without a row is a finding.
Where the borrowed project is `Goldpath.IntegrationTests`, the config's `test-case-filter`
admits only the named class (`FullyQualifiedName!~Goldpath.Tests.Integration.|FullyQualifiedName~Goldpath.Tests.Integration.<Class>.`):
the row names the tests that assert the package's contract, and the filter makes the config
borrow exactly those — not every container-backed story that happens to pass through.

| Package | Borrowed test project | Why it counts |
|---|---|---|
| AuditTrail | `Goldpath.DataProtection.Tests` | `AuditMaskingTests` drives the audit interceptor with masked properties: what the audit log RECORDS for a protected column is AuditTrail behavior, asserted from the DataProtection side. |
| AuditTrail | `Goldpath.SoftDelete.Tests` | The soft-delete/audit interplay (`Audit_sees_the_soft_delete_as_exactly_three_modified_fields`) asserts the audit entry a soft delete produces — AuditTrail's change capture under a second interceptor. |
| SoftDelete | `Goldpath.MultiTenancy.Tests` | `SoftDelete_and_tenant_filters_combine_on_one_entity` and the contributor ordering assert SoftDelete's query filter composed with the tenant filter on one entity. |
| Data | `Goldpath.Jobs.Tests` | `ModelContractTests` (`A_secondary_head_maps_the_tables_but_never_owns_their_migrations`) is the only test of `ModelConventions`' secondary-head rule; Data's own suite never reaches that file (5 mutants, measured 2026-10-20). |
| Data | `Goldpath.SoftDelete.Tests` | Asserts the stamped-update rewrite through `QueryFilters` and the contributor ordering in `SaveContributors` — Data's composition seam, exercised by the module that plugs into it (2 mutants). |
| Data | `Goldpath.MultiTenancy.Tests` | The tenant guard's ordering (`Guard_messages_teach_the_fix_and_the_contributor_runs_first`) asserts `SaveContributors` and the filter composition in `QueryFilters` (2 mutants). |
| MultiTenancy | `Goldpath.Auth.Tests` | `Binding_can_be_disabled_for_gateway_owned_tenancy` / `Binding_rejection_body_names_the_problem` assert the tenant-to-principal binding MultiTenancy registers (1 mutant). |
| MultiTenancy | `Goldpath.IntegrationTests` | `Http_tenant_survives_middleware_stamp_outbox_broker_and_consumer_stamp` is the tenant's journey through the outbox and a real broker, which no unit suite can host (2 mutants). The rest of the suite is filtered out: under solution mode it also killed 3 header-name string mutants INCIDENTALLY, through the campaign pacer bench (75.58 %); the deliberate tests alone score 72.09 %, and a whole-suite borrow cost 130 min a night for those 3. |
| Messaging | `Goldpath.Messaging.Conformance` | The seam's own conformance suite (ADR-0013): `S2_1_exactly_once_…` is the outbox's exactly-once contract (1 mutant). |
| Messaging | `Goldpath.IntegrationTests` | `Committed_transaction_delivers_exactly_once` and `Rolled_back_transaction_publishes_nothing` prove the outbox registration against real Postgres + RabbitMQ (3 mutants). |
| Locking | `Goldpath.IntegrationTests` | `Exactly_one_competitor_holds_the_lock_and_release_hands_it_over` and `Tenant_scoped_names_do_not_contend_across_tenants` contend for a real lock; the unit suite has no second holder (1 mutant). |
| Caching | `Goldpath.IntegrationTests` | `A_second_host_with_a_cold_l1_reads_the_value_from_redis_l2` is the only proof of the Redis L2 wiring (3 mutants). The package stays BELOW the break with it — see the measured scores. |

## Excluded files

## Measured scores

The release checklist requires "mutation scores current for every package whose engine
paths changed"; until 2026-09-05 no score was written down anywhere, so the gate could be
checked only by the person who ran it. This table is that record. A score here is a FULL
run (`scripts/mutation-gate.sh <Package>`), never a `--since` diff run.

**Measured 2026-09-14**, full runs (`scripts/mutation-gate.sh`) on the self-hosted runner, in
nightly run 34890001568 on the preview.9 release candidate — all four of its CLI fixes in. The
2026-09-05 local measurements this replaces are in git history.

| Package | Score | Margin over break (70) |
|---|---:|---:|
| Abstractions | 95.24 % | 25.24 |
| SoftDelete | 91.67 % | 21.67 |
| Locking | 91.67 % | 21.67 |
| Cli | 91.09 % | 21.09 |
| AuditTrail | 81.97 % | 11.97 |
| Messaging | 80.72 % | 10.72 |
| DataProtection | 78.95 % | 8.95 |
| Data | 77.38 % | 7.38 |
| FileExchange | 76.92 % | 6.92 |
| Approvals | 76.47 % | 6.47 |
| Console | 75.00 % | 5.00 |
| Idempotency | 73.58 % | 3.58 |
| Analyzers | 72.55 % | 2.55 |
| ServiceDefaults | 72.15 % | 2.15 |
| MultiTenancy | 72.09 % | 2.09 |
| Auth | 70.65 % | **0.65** |

Read the margin column, not the score. Five packages clear the break by less than four
points — **Auth by 0.65**, MultiTenancy by 2.09, ServiceDefaults by 2.15, Analyzers by 2.55 and
Idempotency by 3.58 — so in those five a single deleted or weakened test turns the gate red. That
fact was invisible until this table existed: the checklist asked for scores and nothing recorded
one. Treat a margin under 2 as a standing invitation to add facts, not as a passing grade.

Against 2026-09-05: Cli fell 2.10 to 91.09 — new recipe code in preview.9, still 21 points clear.
Messaging fell 3.62 — it gained the consume seam (ADR-0013) in the same window, so its scored
code is not the code measured on 2026-09-05. MultiTenancy fell 2.33. The two measurements ran on
different machines, so those drops are recorded here, not explained.

The six long-running packages (Jobs, Archival, Bulk, Notification, Campaign, Caching) run
in `mutation-heavy.yml` (dispatch-only) and are measured locally before a release; their
rows join this table at the next such run.

| Package | Excluded file | Why it is not scored |
|---|---|---|
| Jobs | `GoldpathJobsExtensions.cs` | DI composition — registrations and option binding; behavior lives in the registered types, which ARE scored. |
| Jobs | `QuartzStoreModel.cs` | EF model mapping for Quartz's own tables — declarative; a mutated column name fails the migration proofs, not a unit test. |
| Jobs | `GoldpathQuartzAdapter.cs` | The Quartz adapter — exercised only with a live scheduler (the integration suite and the console smoke), which Stryker's unit loop does not host. |
| Jobs | `GoldpathJobHistoryListener.cs` | Quartz listener callbacks — same live-scheduler dependency. |
| Jobs | `GoldpathJobsFleetRegistry.cs` | Cluster-member registry over Quartz metadata — live-scheduler dependency. |
| Jobs | `GoldpathJobsAdminEndpoints.cs` | Minimal-API route table over the admin service; the contract is pinned by the route-freeze test and the console smoke. |
| Jobs | `GoldpathJobsAdminService.cs` | The admin verbs (trigger/pause/reschedule/calendars/rerun/replay) run through the LIVE scheduler and the fleet registry — the same dependency that excludes the adapter and the registry. MEASURED 2026-09-03: scoring it drops the package below the break (the unit loop cannot host the scheduler, so its 738 lines survive by construction). It is proven where the scheduler exists: `JobsClusterTests`, `JobsRunListTests`, `BulkClusterTests` and the console smoke's journeys drive every verb against real Postgres + Quartz. |
| FileExchange | `GoldpathFileExchangeMetrics.cs` | Meter declarations (see Campaign/Notification); joined 2026-09-03 with the admin surface. |
| Archival | `GoldpathArchivalExtensions.cs` | DI composition (see Jobs) — the archival engine, policy and store ARE scored. |
| Archival | `GoldpathArchivalAdminEndpoints.cs` | Route table (see Jobs); the contract is pinned by the admin-contract check and the console smoke. |
| Archival | `GoldpathArchivalMetrics.cs` | Meter declarations — names pinned by the dashboard's queries, not by mutants. |
| Bulk | `GoldpathBulkExtensions.cs` | DI composition (see Jobs) — the batch engine, row handlers and approval gate ARE scored. |
| Bulk | `GoldpathBulkAdminEndpoints.cs` | Route table (see Jobs). |
| Bulk | `GoldpathBulkMetrics.cs` | Meter declarations. |
| Cli | `Program.cs` | The process entry point — three lines that hand argv to `CliRunner.Run`, which IS scored end to end. |
| Cli | `ConsoleProcessRunner.cs` | The real `IProcessRunner` — it starts `dotnet`/`specdrift` and returns the exit code; every command under test drives the fake, and the real one is exercised by the nightly golden-manifest shapes and `validate-migrations.sh`. |
| Analyzers | `Descriptors.cs` | 49 `DiagnosticDescriptor` initialisers — id, title, message format, severity and help link, with no branching. A mutant here changes a STRING, and the strings are pinned where they are read: every rule's id and message are asserted by `Goldpath.Analyzers.Tests`, and the release-tracking analyzer (RS2000/RS2007) fails the build if an id leaves the release files. Mutating them would only manufacture survivors the tests already cover from the other side. |
| Cli | `Prompter.cs` | The interactive shell (`Console.ReadLine` + menu rendering) behind `IPrompter` — a unit loop cannot answer a prompt. The wizard's QUESTIONS, defaults and multi-select parsing are pinned against a recording fake in `WizardDiscoverRunnerMutationTests`; the derivation it feeds is a pure function under full mutation. **Weakest exclusion in this ledger** (open-threads: no e2e run drives the real prompter either). |
| Campaign | `GoldpathCampaignExtensions.cs` | DI composition (see Jobs). |
| Campaign | `GoldpathCampaignMetrics.cs` | Meter/counter declarations — names are pinned by the dashboard's queries, not by mutants. |
| Campaign | `GoldpathCampaignConsumers.cs` | MassTransit consumers — exercised against a real broker in the integration suite. |
| Campaign | `GoldpathCampaignAdminEndpoints.cs` | Route table (see Jobs). |
| Notification | `GoldpathNotificationExtensions.cs` | DI composition. |
| Notification | `GoldpathNotificationMetrics.cs` | Meter declarations. |
| Notification | `GoldpathNotificationChannels.cs` | SMTP/channel transports — exercised against a real Mailpit in the integration suite. |
| Notification | `GoldpathNotificationAdminEndpoints.cs` | Route table. |

Everything else in a scored package is mutated — including the admin services of the
modules whose engines are in-process (Approvals, FileExchange, Notification's reads); the
Jobs admin service is the exception above, and the reason is measured, not assumed.

## Ignored methods

| Package | Method pattern | Why |
|---|---|---|
| all | `Log*` | Logging calls carry no behavior a test can observe; mutating them only manufactures survivors. Declared in EVERY config since 2026-09-05 — eleven of them carried the ledger's word without the setting until the preview.8 coverage audit. |
| Locking | `CreateRedisProvider` | Builds the Redis lock provider — needs a live Redis, which the unit loop does not host; the Redis path is proven by the integration suite. |

## Packages without a gate

`ApiDefaults`, `Locking.SqlServer`, `Sdk` — composition and adapter shells with little
branching logic; each has a unit suite. A gate joins the moment one of them grows an engine
path (the 2026-09-01 audit's rule). `Console`, `Messaging` and `ServiceDefaults` gained
gates on 2026-09-03 (the thin-suites PR): each carries real branching (asset resolution,
the publish/consume filters, correlation + concurrency guard) that a unit suite can score.
