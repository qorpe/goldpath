# Mutation gates — what is scored, what is excluded, and why

One config per package (`Goldpath.<Package>.json`), all at the same thresholds: high 85,
low 75, **break 70** — `scripts/mutation-gate.sh <Package>` runs one, the nightly matrix
runs all of them, `mutation-heavy.yml` re-runs the six long-running ones on demand — all six by
default, or one through its `package` input (`gh workflow run mutation-heavy.yml -f
package=Goldpath.Campaign`; a name outside the six is refused). Stryker JSON
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
number in brackets. The other seventeen rows, all but Console and Campaign (below), stand at
f459fe2 — their code and tests did not change, and the same nightly re-scored them within the
noise band below.

**Messaging was re-measured 2026-10-22**, a full run of `mutation-gate.sh Goldpath.Messaging` at
da89aa4 on the #327 branch (G1 Part 2/4: the builder, the outbox options, the test host): 80.72 % →
81.53 % (151 mutants scored). Minutes are the local sandbox's, which another repository's Stryker
run shared, not the CI profile's. Survivors left: the test host's dispose branches and its
`WaitUntilStarted` line, which the in-memory transport cannot tell apart. The argument guards
that also survived are pinned by a test added after the run.

**Messaging was re-measured 2026-10-24**, a full run at 421f47d on the #328 branch (G1 Part 3/4: the
batch seam, named endpoints, the engine's batch composition): 81.53 % → 86.09 % (222 mutants tested;
the agent sandbox's minutes). Of the new code's survivors, four are pinned by tests added after the
run — the duplicate-batch check's `&&`, the explicit endpoint name, a `MessageLimit` of exactly 1, and
the refusal naming the in-flight product — and so is the engine test hook's registration callback.
Left without coverage: the hook's RabbitMQ transport callback, which only the broker bench reaches.

**Messaging was re-measured 2026-10-25**, a full run at 2a97413 on the #330 branch (G5 Part 1/5: the
engine contract, discovery, and the conformance class run once per engine, so the Wolverine facts
also exercise Messaging's code): 86.09 % → 82.70 % (275 mutants tested; 99.8 minutes in the agent
sandbox while other test runs shared it, not the CI profile's). The drop is the new code's survivors. Pinned by tests added after the run, each checked by
planting its mutant: the refusal's separator and its order (the order moved from discovery into the
decision, so it holds however the declarations are found), an engine with an internal constructor, the
engine publish's four argument guards, an application not waiting for the bus while the test host
does, and the message-scope tenant registered when the application registers none. Removed rather
than tested, because they could not change behaviour: a null check after `Activator.CreateInstance`
(null only for a `Nullable<T>`, which the type check refuses), the floor assembly added to a scan that
always contains it, and a `Directory.Exists` the catch already covers. Left: loading an adapter found
beside the application but not loaded yet (no test process here starts without the adapter loaded),
the dynamic-assembly filter, the scan's catch branches, the kebab formatter on the builder path (the
builder names every endpoint), the test host's dispose branches, and its body guard, which the engine's
own guard now makes equivalent.

**Archival, Bulk and Notification in diff mode, 2026-11-09** (#409, `GOLDPATH_MUTATE_SINCE=origin/main`, the local
sandbox; diff mode mutates the whole changed file, and the full run stays the authoritative score, so the table below
is unchanged): Archival 83.17 % (97 mutants), Bulk 84.75 %, Notification 81.82 %. Every mutant on the lines the
change touched (the three `ChunkedContains` filters and their guards) is killed or does not compile.
- **One survivor on those lines, now killed:** Notification's `template is { Length: > 0 }` → `>= 0`. An empty
  `template` array would have filtered every row out. Bulk's suite already pinned its twin. `AdminServiceTests` now
  asserts that an empty filter is no filter (79.55 % → 81.82 %).
- The ChunkedContains call itself yields no mutant. Dropping the archived-keys filter is proven on a real database
  instead: the PostgreSQL `BatchSize = 600` fact goes red on the unique index.

**Data was re-measured 2026-11-08**, a full run of `mutation-gate.sh Goldpath.Data` on the #408 branch
(`GoldpathQueryableExtensions.ChunkedContains`, RFC goldpath-oracle §2.5), on the local sandbox: 77.38 % → 80.00 %
(103 mutants tested, 3.8 minutes, the sandbox's, not the CI profile's).
- **Two survivors on the new lines, both equivalent:**
  - Removing `ThrowIfNull(source)`: `Queryable.Where` then refuses the null itself, with the same `ArgumentNullException`
    and the same parameter name, `source`.
  - `values.Count <= size` → `< size`: at exactly 1,000 values the list goes through `Chunk(1000)` instead. That
    yields the same single chunk.
- Every other mutant on the new lines is killed: the chunk size, the provider match (ordinal, `Oracle`), the `OR`
  join, and the `Contains` overload lookup.
- No threshold moved and nothing was excluded. Campaign's three call sites changed too; its diff-mode run on the
  same branch is reported in the #408 pull request.

**Approvals was re-measured 2026-11-03**, a full run of `mutation-gate.sh Goldpath.Approvals` on the #480 branch
(concurrent decisions: a version on the request row, a signature written with the request in one transaction, a
unique signature key), on the local sandbox: 77.78 % → 77.87 % (2.8 minutes, the sandbox's, not the CI profile's).
- **The first run scored 76.75 %.** The new code's survivors were the custom-store two-call path and the in-memory
  store's stale check on that path, both without coverage, and the EF store's conflict branch, reached only by the
  PostgreSQL proof, which is outside the mutated test project. A clock that lands a reject between a grant's read and
  its write now reaches all three in the unit suite.
- **Survivors left on the new lines:**
  - The version moving by −1 instead of +1, in either store. Equivalent: any change of the token refuses the stale
    write, and the value never returns to one already read.
  - The in-memory atomic path's duplicate-signature check. Through the engine it is unreachable: every signature
    write also moves the version, which refuses first. It mirrors the database's unique key in the same write.
  - The EF store's duplicate-signature probe, `&&` → `||`, and its unique-violation branch. Only a provider error
    other than the unique key tells them apart, and the proof for that branch is the PostgreSQL concurrency fact.
- No threshold moved and nothing was excluded.

**Archival was re-measured 2026-11-02**, a full run of `mutation-gate.sh Goldpath.Archival` on the
#383 branch (an entry's `DueAt` is hashed and stored at the same microsecond), on the local sandbox:
79.55 % → 79.90 % (382 mutants tested, 7.9 minutes, the sandbox's, not the CI profile's). Every
mutant on the new lines is killed: the sign and the `%` of the truncation, and the entry's
initializer. No threshold moved and nothing was excluded.

**Bulk was re-measured 2026-10-24**, a full run of `mutation-gate.sh Goldpath.Bulk` at 6b97bc3 on
the #300 branch (the duplicate check keeps hashes, not keys), at concurrency 1 on the local
sandbox: 88.38 % → 88.37 % (577 mutants tested). The new `GoldpathBulkDuplicateKeys` leaves one
survivor, `unit >> 8` → `unit >>> 8` in the hash, which is equivalent: a `char` is unsigned, so the
two shifts agree. The other survivors are in lines the change did not touch. Minutes are the
sandbox's, not the CI profile's.

**Console was re-measured 2026-10-19**, a full run on the #272 branch, after the product
console link tiles (#257) put the package's first real decision logic in it — the link URL
rule and the registry's links-only shape. 75.00 % → 91.46 % (75 killed, 6 survived, 1 without
coverage, of 82 scored mutants); the rule's branches are each pinned by a fact that a planted
fault turns red. That run predates #275's scope fix, but Console scored the same in both modes
at f459fe2, so its row carries it (minutes not recorded).

| Package | Score | Margin over break (70) | Minutes | Solution mode (score, minutes) |
|---|---:|---:|---:|---|
| Caching | 100.00 % (69.57) | 30.00 | 2.3 | 69.57 %, 33.4 (RED there too) |
| Auth | 97.83 % (70.65) | 27.83 | 1.8 | 70.65 %, 32.3 |
| Abstractions | 95.24 % | 25.24 | 0.9 | 95.24 %, 30.0 |
| SoftDelete | 91.67 % | 21.67 | 1.4 | 91.67 %, 3.7 |
| Locking | 91.67 % | 21.67 | 7.1 | 91.67 %, 59.5 |
| Cli | 91.48 % | 21.48 | 7.6 | 91.44 %, 8.6 |
| Console | 91.46 % (75.00) | 21.46 | — | 75.00 %, 24.3 |
| Bulk | 88.37 % (88.38) | 18.37 | 19.7 | never finished (timeout) |
| Idempotency | 84.91 % (73.58) | 14.91 | 1.8 | 73.58 %, 3.3 |
| Campaign | 79.22 % (83.95) | 9.22 | 104 (contended) | never finished (OOM) |
| Messaging | 82.70 % (86.09) | 12.70 | 99.8 | 80.72 %, 69.4 |
| AuditTrail | 81.97 % | 11.97 | 5.1 | 81.97 %, 5.7 |
| MultiTenancy | 81.40 % (72.09) | 11.40 | 6.1 | 75.58 %, 38.5 |
| Notification | 81.23 % | 11.23 | 5.9 | never finished (OOM) |
| DataProtection | 78.95 % | 8.95 | 1.5 | 78.95 %, 33.0 |
| ServiceDefaults | 78.48 % (72.15) | 8.48 | 1.4 | 72.15 %, 4.3 |
| Approvals | 77.87 % (74.84) | 7.87 | 2.8 | 74.84 %, 217.7 |
| Data | 80.00 % | 10.00 | 3.8 | 77.38 %, 38.0 |
| FileExchange | 76.92 % | 6.92 | 3.6 | 76.92 %, 31.8 |
| Archival | 79.90 % (74.49) | 9.90 | 7.9 | never finished (OOM) |
| Analyzers | 77.37 % (72.55) | 7.37 | 3.4 | 72.55 %, 5.4 |
| Jobs | 79.39 % (72.52) | 9.39 | 4.2 | never finished (OOM) |

**Analyzers, Jobs and Archival were re-measured 2026-10-31** (#337, GA gate G9 part 2): full local
runs of `scripts/mutation-gate.sh` at concurrency 4 on the agent sandbox (minutes are the sandbox's,
not the CI profile's), before and after this PR's tests on the #337 branch. No threshold
moved and nothing was excluded.
- **Analyzers 72.71 → 77.37 %.** The invocation branch of `AuthSurfaceAnalyzer` (the fluent
  `AllowAnonymous()`, `UseHmacRedaction("…")`, `ApiKeys.Add(…)`) had no test at all, nor did GP0701's
  Mediant `[SensitiveData]` path, GP0301's `ISoftDeletable`/`IMultiTenant` markers and GP0302's three
  other method names. Writing the `ApiKeys.Add` fact found a false positive, fixed in the same PR:
  a literal client NAME beside a key read from a store was flagged; only the value is a secret now,
  as on the indexer form.
- **Jobs 72.90 → 79.39 %** and **Archival 75.25 → 79.55 %** (the "before" is the same sandbox on
  `main`'s tests; the nightly had 72.52 and 74.49). T33's cure: both model-contract suites build their
  model FRESH from `ModelBuilder`, as Bulk's does, instead of reading a context's cached model. Mapping
  survivors fell from 18 to 1 in `JobsModel.cs` and from 20 to 2 in `ArchiveModel.cs`. Of those left,
  `ToTable("GoldpathJobAdminAudit")` and `ToTable("GoldpathArchiveChainState")` are equivalent under a
  fresh `ModelBuilder`: without them EF names the table after the class, which is the same string. The
  legal hold's `Definition` default was pinned by a fact added after the run.

The hosted confirmation is G9's own proof, two consecutive nightlies at or above 75 %.

**MultiTenancy, Idempotency, ServiceDefaults and Approvals were re-measured 2026-10-21** (#322,
GA gate G9 part 1): full local runs of `scripts/mutation-gate.sh` (a container, not the hosted
runner; minutes are local) after tests that kill named survivors by asserting an observable
outcome. No threshold moved and nothing was excluded. Approvals' starting point measured 74.18 %
locally against the nightly's 74.84 %, so a local number carries roughly that much noise; each
of the four clears 75 by more than it. The hosted confirmation is G9's own proof: two
consecutive nightlies at or above 75 %.

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

**Campaign after revision R2 (PR #277), 2026-10-20.** The row above carries the R2 engine's
score; the bracketed 75.13 % is the pre-R2 engine at f459fe2. R2 grew the package (typed
results, signed callbacks, parking, the carried fair share, the drain sweep: 1,053 mutants
tested, 191 tests), and the first run of it scored 71.47 %
([run 39432932577](https://github.com/qorpe/goldpath/actions/runs/39432932577/job/118662399665)),
with the new callback surface weakest at 48.8 %. Twelve tests then pinned what each callback
answer says, the body limit, the timestamp window and the fair share's edges:
[run 39454180800](https://github.com/qorpe/goldpath/actions/runs/39454180800/job/118736572505),
74.77 % at concurrency 1 (minutes as measured there). The pacer (`GoldpathCampaignJobs.cs`,
59.2 % in the first run) is the next place to add facts.

**Campaign on the seam (#328, G1 Part 3/4), 2026-10-24.** A full local run of
`mutation-gate.sh Goldpath.Campaign` at 5c59165 on the #328 branch: 74.77 % → 76.30 % (1,081
mutants tested, 211 tests; minutes are the agent sandbox's, not the CI profile's). The MassTransit
consumers file, excluded as adapter plumbing, is gone (the MassTransit-typed registration kept for the
templates until G1 Part 4/4 sits in the excluded composition file); its successor `GoldpathCampaignHandlers.cs`
(the item and outcome handlers, the queue name) is scored, with unit tests of its own: 17 killed,
2 survived. One survivor (`replay: false` → `true`) is pinned by a test added after the run; the
other (`Length > 1000` → `>= 1000`) is equivalent, since a 1,000-character message cut at 1,000 is
unchanged.

**Campaign after R3-S2, the scheduled start (#289), 2026-10-28.** A full local run of
`mutation-gate.sh Goldpath.Campaign` at 1c77033 on the #289 branch: 76.30 % → 83.95 % (1,148
mutants tested, 237 tests; 43.4 minutes, the agent sandbox's, not the CI profile's). The rise is not
this slice's alone: 211 → 237 tests includes R3-S2's eighteen and the Campaign facts merged since
5c59165, and no run isolated the two. On the lines R3-S2 changed: 70 killed, 7 survived, 1 without
coverage.
- **Killed afterwards by two facts:**
  - the start's conditional update with `&&` turned into `||` (`(Id || Ready) && StartAt`) started a
    campaign an operator had just paused, and is killed by
    `APauseMadeAfterTheTickLoadedTheCampaign_WinsOverThePacersStart`;
  - create without a policy always applying a patch is killed by
    `ACreateWithoutAPolicy_TakesTheTypesDefaults_WhichCarryNoStart`.
- **Equivalent:** the start gate's `refusal is not null` → `is null`, because the gate list ships empty
  (until amendment R3.7 adds a gate), and removing the `ChangeTracker.Clear()` after the start's
  audit row, because the tick's scope and its tracker are discarded at the end of the tick.
- **Not R3-S2's:** the rest sit on the rewritten audit-text line (`Describe`), on its pre-existing
  `excludedDays` and `endDate` terms.

**Campaign after R3-S5, `MaxAwaitingAck` (#293), 2026-10-30.** A full local run of
`mutation-gate.sh Goldpath.Campaign` at 4831cbb on the #293 branch: 83.95 % → **82.31 %**
(1,191 mutants tested, 261 tests; 48 minutes, the agent sandbox's, not the CI profile's), still
green against the break at 70. The run is honest about the drop: the per-state settle split and the
re-count added mutants, and the package's older survivors (callback prose, `ChangeTracker.Clear()`
hygiene, the pre-existing throttle positivity checks for the other fields) still stand. On the
lines R3-S5 changed, one survivor was a real gap: a failure straight from `Processing` counted as
leaving `AwaitingAck` (`leftAck += moved` survived). `AFailureStraightFromProcessing_MovesNoAcknowledgement`
was added after the run and kills it (planted, red); the score above predates that test. This run predates the merge of R3-S3, R3-S4, R3-S7 and R3-S10 into the branch, so the table above keeps R3-S4's measured row; the merged code has not been re-measured.

**Campaign after R3-S4, the policy version (#298), 2026-11-01.** A full local run of
`mutation-gate.sh Goldpath.Campaign` at d84a0d2 on the #298 branch: **79.22 %** — 1,277 mutants
tested (1,030 killed, 11 timeouts, 236 survived, 37 without coverage), 326 tests. The run took
104 minutes in the agent sandbox, and for 40 of them a second Stryker job shared its four cores, so
the time is not a profile number. The drop from 83.95 % is not this slice's to explain alone: R3-S3
(#315) and R3-S7 (#317) merged since 1c77033 and grew the scored code from 1,148 to 1,277 tested
mutants, and no run isolated the three slices. By file, the pacer (`GoldpathCampaignJobs.cs`, 65.0 %)
is still the weakest.

On the lines R3-S4 rewrote (the throttle, from `ThrottleAsync` through the stale refusal): 70
killed, 16 survived, 3 without coverage.
- **Killed afterwards by four facts in `R3PolicyVersionTests`, each red against its mutant:**
  - the retry bound (`<` → `<=`), by `AThrottleThatLosesEveryRace_StopsAfterEightAttempts_…`. The
    `attempt++` → `attempt--` mutant loops forever under the same fact and dies by timeout; that one
    is reasoned, not re-run.
  - the stale refusal's "who moved it" lookup, `OrderByDescending` → `OrderBy` and `&&` → `||`, by
    `AStaleRefusal_NamesTheNewestThrottleOfThisCampaign_…`;
  - the terminal-state pattern (`or` → `and`), by `EveryTerminalState_RefusesTheThrottle_…`;
  - the three mutants on the excluded-days column (`null` ↔ `""`, and the separator), by
    `ClearedExcludedDays_AreStoredAsNoValue_…`.
- **Not R3-S4's:** the other eight survivors and three without coverage sit in the patch validation
  (the positive-values check, the unknown-day and seven-day messages) and the "already started"
  message. Those lines moved verbatim from the shipped throttle into `ThrottleRefusal` and
  `ApplyThrottle`, and the shipped tests assert only `Ok = false` on them.

**Campaign after R3-S10, auto-suspend (#299), 2026-11-02.** A full local run of
`mutation-gate.sh Goldpath.Campaign` at ba32e9a on the #299 branch scored **78.87 %**: 1,409 mutants tested,
343 tests, 112.5 minutes. The minutes are the agent sandbox's, with a container run beside it, not the CI
profile's. The fall from 83.95 % (R3-S2) is not this slice's alone: `main` gained R3-S7 and other Campaign
work since 1c77033, and no run isolated the two. The table row above is unchanged until a CI run confirms
the number.

On the lines R3-S10 changed: 153 killed, 37 survived, 4 without coverage (78.9 %). The survivors:
- **Single-campaign blind spots:** the window restart's and the suspension flip's `Id == …` turned into
  `||`. Every fact runs one campaign, so a guard that also matches another campaign's row is not seen.
- **Return-value and in-memory bookkeeping:** `EvaluateFailureRatioAsync`'s booleans, the tick's `-1`, and
  the in-memory counter subtraction after a restart. The row, not the loaded copy, is what the next tick reads.
- **Validation boundaries:** the throttle's `SuspendWindow`/`SuspendMinSample` `<= 0` against `< 0`, and
  `SuspendRefusal`'s ordering against `StartRefusal`. The theory accepts either refusal text on the
  throttle, so a boundary that falls through to the other refusal still passes.
- **Audit text:** `ShowsSuspend`'s terms and `Bound`'s `>` against `>=`. The second is equivalent: a text of
  exactly the column's length is unchanged either way.
- **No coverage at the run above; covered since:** the sample's `CompletedAt >= WindowStartedAt` filter.
  `TheReasonsCode_IsReadFromTheCurrentWindowOnly_NotFromTheFailuresBeforeItRestarted` (added after the run)
  suspends a campaign after its window restarted; replacing the filter with `true` turns it red ("B (71%)"
  instead of "B (100%)"). The score above was not re-run for it.

These are the next facts to add for this package (T39's proof for R3-S10).

**Campaign after R3-S8, target-system ceilings (#290), 2026-11-05.**
- **The run:** a diff-mode run, `GOLDPATH_MUTATE_SINCE=origin/main mutation-gate.sh Goldpath.Campaign`, on the #290 branch at 8997b6c. It mutated the four changed files whole: 1,298 mutants, 487 tests, 113.6 minutes in the agent sandbox, scoring **79.40 %** (gate green). This is diff mode, so the full run stays the authoritative score, and the table row above is unchanged.
- **On the lines R3-S8 added:** 72 killed, 23 survived, 1 without coverage, 43 compile errors.
- **Killed since the run:** five of the survivors are killed by facts added after the run, each planted by hand and seen red:
  - the follow's guard with `&&` turned into `||` (`OfTwoSystemsSeededAlike_OnlyTheOneWhoseCodeMoved_Follows`);
  - the `continue` for an unmoved code value (`ACodeValueThatDidNotMove_…`);
  - the reconcile's and the read's row match with `&&` turned into `||` (`ASystemNamedLikeTheGlobalRow_IsStillItsOwnRow`);
  - the missing-global-row fallback (`WithNoRowsYet_TheLiveCeilingsAreTheCodeSeeds`).
- **The rest, read one by one:**
  - **Equivalent:** the two-pass loop's bounds (`<=`, `||`, `--`). Once `desired` is empty a further pass changes nothing.
  - **Equivalent:** the `continue` after the disagreement warning. The guarded update that follows matches no row.
  - **Equivalent within one conflict:** the insert's `true`/`false` and its `finally` detach. A second pass re-reads the row, and no later `SaveChanges` runs on that context.
  - **Equivalent:** the global bucket's reset while off. The charge guard alone already prevents the debt (planted: both together go red, either alone does not).
  - **Unreachable through a host that passed the startup check:** the target step's `continue` for an undeclared system.
  - **A collision only with a system named `global`:** the `"target:"` ledger prefix.
  - **Log text only:** the `moved == 1` log and the message strings.

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
