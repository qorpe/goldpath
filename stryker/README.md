# Mutation gates — what is scored, what is excluded, and why

One config per package (`Goldpath.<Package>.json`), all at the same thresholds: high 85,
low 75, **break 70** — `scripts/mutation-gate.sh <Package>` runs one, the nightly matrix
runs all of them, `mutation-heavy.yml` re-runs the six long-running ones on demand. Stryker JSON
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
| MultiTenancy | `Goldpath.IntegrationTests` | `Http_tenant_survives_middleware_stamp_outbox_broker_and_consumer_stamp` is the tenant's journey through the outbox and a real broker, which no unit suite can host (2 mutants). The rest of the suite is filtered out: under solution mode it also killed 3 header-name string mutants INCIDENTALLY, through the campaign pacer bench (75.58 %); the deliberate tests alone score 72.09 %. Borrowing the whole suite cost 130 minutes (nightly 39388961885) and did not even kill those 3 — the bench fails under Stryker's instrumentation. |
| Messaging | `Goldpath.Messaging.Conformance` | The seam's own conformance suite (ADR-0013): `S2_1_exactly_once_…` is the outbox's exactly-once contract (1 mutant). |
| Messaging | `Goldpath.IntegrationTests` | `Committed_transaction_delivers_exactly_once` and `Rolled_back_transaction_publishes_nothing` prove the outbox registration against real Postgres + RabbitMQ (3 mutants). |
| Locking | `Goldpath.IntegrationTests` | `Exactly_one_competitor_holds_the_lock_and_release_hands_it_over` and `Tenant_scoped_names_do_not_contend_across_tenants` contend for a real lock; the unit suite has no second holder (1 mutant). |
| Caching | `Goldpath.IntegrationTests` | `A_second_host_with_a_cold_l1_reads_the_value_from_redis_l2` is the only proof of the Redis L2 wiring (3 mutants). With it the package sat BELOW the break until `CachingCompositionTests` (2026-10-20) — see the measured scores. |

## Measured scores

The release checklist requires "mutation scores current for every package whose engine
paths changed"; until 2026-09-05 no score was written down anywhere, so the gate could be
checked only by the person who ran it. This table is that record. A score here is a FULL
run (`scripts/mutation-gate.sh <Package>`), never a `--since` diff run.

**Measured 2026-10-20**, full runs on GitHub's hosted runners (ubuntu-latest, 4 vCPU / 16 GB)
at commit f459fe2 — the first scores taken with each package's OWN scope (see "Which tests
score a package"): the sixteen in [nightly 39410985645](https://github.com/qorpe/goldpath/actions/runs/39410985645)
at concurrency 2, the heavy six in [mutation-heavy 39410990059](https://github.com/qorpe/goldpath/actions/runs/39410990059)
at concurrency 1. "Solution mode" is the same package on main, where every referencing test
project ran ([nightly 39297981494](https://github.com/qorpe/goldpath/actions/runs/39297981494),
2026-10-19; of the heavy six only Caching ever finished a hosted run in that mode — red — in
[mutation-heavy 39378794363](https://github.com/qorpe/goldpath/actions/runs/39378794363)).

**Caching, Auth and Bulk were re-measured the same day** at commit fe968b5, after the tests of
#278 — once in the first [nightly 39420913374](https://github.com/qorpe/goldpath/actions/runs/39420913374)
that scores all twenty-two packages (the long-running six at concurrency 2) and once in
[mutation-heavy 39420917618](https://github.com/qorpe/goldpath/actions/runs/39420917618) at
concurrency 1. Both runs gave the same three scores; their rows below carry them, the old
number in brackets. The other nineteen rows stand at f459fe2 — their code and tests did not
change, and the same nightly re-scored them within the noise band below.

| Package | Score | Margin over break (70) | Minutes | Solution mode (score, minutes) |
|---|---:|---:|---:|---|
| Caching | 100.00 % (69.57) | 30.00 | 2.3 | 69.57 %, 33.4 (RED there too) |
| Auth | 97.83 % (70.65) | 27.83 | 1.8 | 70.65 %, 32.3 |
| Abstractions | 95.24 % | 25.24 | 0.9 | 95.24 %, 30.0 |
| SoftDelete | 91.67 % | 21.67 | 1.4 | 91.67 %, 3.7 |
| Locking | 91.67 % | 21.67 | 7.1 | 91.67 %, 59.5 |
| Cli | 91.48 % | 21.48 | 7.6 | 91.44 %, 8.6 |
| Bulk | 88.38 % (70.09) | 18.38 | 15.2 | never finished (timeout) |
| AuditTrail | 81.97 % | 11.97 | 5.1 | 81.97 %, 5.7 |
| Notification | 81.23 % | 11.23 | 5.9 | never finished (OOM) |
| Messaging | 80.72 % | 10.72 | 26.9 | 80.72 %, 69.4 |
| DataProtection | 78.95 % | 8.95 | 1.5 | 78.95 %, 33.0 |
| Data | 77.38 % | 7.38 | 2.3 | 77.38 %, 38.0 |
| FileExchange | 76.92 % | 6.92 | 3.6 | 76.92 %, 31.8 |
| Campaign | 75.13 % | 5.13 | 28.5 | never finished (OOM) |
| Console | 75.00 % | 5.00 | 1.1 | 75.00 %, 24.3 |
| Approvals | 74.84 % | 4.84 | 3.5 | 74.84 %, 217.7 |
| Archival | 74.49 % | 4.49 | 10.6 | never finished (OOM) |
| Idempotency | 73.58 % | 3.58 | 2.2 | 73.58 %, 3.3 |
| Analyzers | 72.55 % | 2.55 | 3.9 | 72.55 %, 5.4 |
| Jobs | 72.52 % | 2.52 | 9.1 | never finished (OOM) |
| ServiceDefaults | 72.15 % | 2.15 | 2.2 | 72.15 %, 4.3 |
| MultiTenancy | 72.09 % | 2.09 | 6.2 | 75.58 %, 38.5 |

Read the margin column, not the score. At f459fe2 **Caching was below the break** (red in
solution mode as well, unseen because the heavy lane never finished — open thread T32) and
**Bulk cleared it by 0.09 and Auth by 0.65**. The break was not lowered and nothing was
excluded: tests were added (#278). Caching's seven survivors were all observable from the
container; Auth's were options, guards and pipeline order nobody asserted; Bulk's model
contract tests had been reading a CACHED EF model — EF builds a context type's model once per
process, so in Stryker's long-lived test host `AddGoldpathBulk` never ran under a mutant —
and its engine had never been observed mid-batch, where the terminal recount cannot hide a
wrong counter. Two Auth survivors remain and are equivalent (the none-branch
`UseAuthentication`, and binding wired for the api-key strategy, whose principal never
carries a tenant claim). MultiTenancy, ServiceDefaults, Jobs and Analyzers still clear the
break by under three points. Same-commit reruns on these runners move a score by up to ±1
point (Approvals 73.86–74.84, Bulk 70.09–71.28 before #278, Jobs 72.14–72.90, Cli
91.13–91.87: tests that time out under instrumentation on one run and kill on the next).
Treat a margin under 2 as a standing invitation to add facts, not as a passing grade.

MultiTenancy is the one package whose score moved with the scope: 75.58 % → 72.09 %. Stryker's
JSON report from the solution-mode run names the difference — three header-name string
mutants in `MultiTenancy.cs` that only the campaign pacer bench killed, incidentally (row
above). Every other package scores the same under its own scope, because the suites that
killed its mutants are now named in its config.

The 2026-09-14 table that stood here (self-hosted runner, nightly 34890001568, solution
mode — itself the successor of a 2026-09-05 local run) is superseded, not merged; both are
in git history.

## Excluded files

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
