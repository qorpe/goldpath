# The 1.0 API review — verdict tables

One TSV per package that has a public-API ledger (`packages/<Package>/PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`). These are the data of the 1.0 API review. The narrative, the method and
the policies are in [`docs/rfc/goldpath-1.0-api-review.md`](../../rfc/goldpath-1.0-api-review.md),
and the owner signs the whole review once, in API-13 (#402). These files are Tier A (owner decision
2026-10-24 on #390), so the review parts #391–#396 merge after the verifier.

## Format

The first line is the header, tab-separated:

```
line	verdict	target	reason
```

Each following line is one verdict for one ledger line:

| Column | Holds |
|---|---|
| `line` | The ledger line exactly as the ledger writes it. One leading `[ID]` prefix is ignored on both sides, because PublicApiAnalyzers 5.x may prefix an experimental member |
| `verdict` | `keep`, `rename`, `remove` or `experimental` |
| `target` | For `rename`, the full new ledger line. For `experimental`, the diagnostic id (`GPEXP001` for Approvals, `GPEXP002` for FileExchange). Empty otherwise |
| `reason` | Mandatory for every verdict but `keep`. For `remove`, it also says what replaces the line, or why no adopter needs it |

A row is a promise about the line, not a change to it. Renames and removals are applied only by
API-14 (#403), in one train, with no compatibility shims.

## The counter

`scripts/api-review-gate.sh` is the only source of every number the record quotes:

```bash
./scripts/api-review-gate.sh                          # covered/total and verdict counts per package; fails on a malformed table
./scripts/api-review-gate.sh --require-complete       # also fails on any live ledger line without a row
./scripts/api-review-gate.sh --require-complete --package Goldpath.Jobs   # one package (a review part's own bar)
./scripts/api-review-gate.sh --list-non-keep          # the owner's reading list for API-13
./scripts/api-review-gate.sh --require-applied        # after API-14: every verdict is applied
./scripts/api-review-gate.sh --selftest               # each fault planted in a scratch copy goes red
```

A table fails the gate on:
- a stale row: a `keep` or `experimental` whose line is no longer live, or a `rename` whose source and
  target are both not live (a `remove` whose line has gone, or a `rename` whose target is live, is an
  applied verdict, not a stale one);
- an unknown verdict;
- a non-keep row without a reason;
- a `rename` or `experimental` row without a target;
- a duplicate row;
- a `keep` in an experimental package (Approvals and FileExchange: GA readiness Q4);
- an `experimental` in Approvals or FileExchange naming another id than its package's (`GPEXP001` /
  `GPEXP002`, compared exactly), or an `experimental` in any other package naming one of those two ids;
- a missing or extra file, or a wrong header.

## Findings for API-13

What a review part found that binds more than its own table. The owner reads these beside
`--list-non-keep` and the record's P1–P3 when signing (API-13, #402). A part adds a finding
here and never answers another part's lines.

- **API-2 (#391), `GoldpathAdminResult` placement (P2).** Answered as P2 proposes: the 14 Jobs lines are
  `remove` rows whose reason names the move to `Goldpath.Abstractions` as the same line, same name and namespace.
  A same-text `rename` cannot express a move between ledgers: the gate reads a rename whose target is live as
  already applied. API-4 (#393) adds the lines to the Abstractions table, and API-14 (#403) moves the type.
- **API-2 (#391), the `ct` parameter name (method Q3), proposed as a policy.** Jobs' admin service names its
  token `ct` on 18 methods, while every other public Jobs signature says `cancellationToken`. The Jobs rows rename
  them. The same name is on 13 Archival, 10 Campaign, 7 Bulk and 5 Notification live lines, Shipped and Unshipped
  (measured at 9513a76, unchanged at fd5d37f).
  **Proposed:** every part renames them the same way. The rename is binary-compatible, and it breaks only a caller
  that passes the token by name.
- **API-2 (#391), a job cannot be unit-tested from outside.** `GoldpathJobContext` and `GoldpathJobChunk` have no
  public constructor, so an adopter's test of `IGoldpathJob.ExecuteChunkAsync` cannot build either. Goldpath's own
  Campaign tests use reflection. The runner path (`GoldpathJobRunner<TContext>`, kept public for this) runs a whole
  job, not one chunk. A test factory would be new API, out of scope for a review part. It is listed here for the owner.
- **API-3 (#392), Archive or Archival.** Answered as one rule: **Archival names the module, Archive names the thing it
  keeps.** The module's entry points already say Archival (the package, `AddGoldpathArchival`, `AddGoldpathArchivalJobs`,
  `GoldpathArchivalOptions`, `MapGoldpathArchivalAdmin`). One archive's builder, definition, entry, envelope, chain state and
  engine say Archive. The one module-level type that broke the rule, `GoldpathArchiveAdminService<TContext>`, is renamed
  `GoldpathArchivalAdminService<TContext>`, so it pairs with `MapGoldpathArchivalAdmin` as every sibling's admin service pairs
  with its `Map…Admin`. Two names stay although the rule alone would change them:
  - **The three jobs** (`GoldpathArchiveJob`, `GoldpathArchiveVerifyJob`, `GoldpathRetentionPurgeJob`), because a job's key is
    its type name (`GoldpathJobDefinition.Name = jobType.Name`). Operators trigger by it, `GoldpathJobExecution.JobName` stores
    it and the `goldpath.job` span tag carries it, so a rename would orphan schedules and run history.
  - **`AddGoldpathArchiveModel`**, which waits for the model-extension finding below.
- **API-3 (#392), public metrics types, proposed as a policy.** Five packages ship their metrics class as public surface:
  `GoldpathBulkMetrics`, `GoldpathNotificationMetrics`, `GoldpathCampaignMetrics`, `GoldpathApprovalsMetrics` and
  `GoldpathFileExchangeMetrics`. `GoldpathArchivalMetrics` and `GoldpathJobsMetrics` are already internal (measured at 543747a).
  **Proposed:** every metrics class is internal, and the contract is the meter and instrument names that API-8 (#397)
  inventories. Bulk's public member is a writer, `SetIntakeSnapshot`, so any caller can overwrite the awaiting-approval gauge the
  human-in-the-loop alert reads; Notification's `SetQueueSnapshot` has the same shape. The Bulk rows answer it as `remove`.
  API-4 (#393), API-5 (#394) and API-6 (#395) answer their own lines.
- **API-3 (#392), the ModelBuilder extension has several spellings.** `AddGoldpath<X>` on `Goldpath<X>Model` (Bulk, Campaign,
  Notification), `AddGoldpath<X>Model` on `Goldpath<X>ModelExtensions` (Approvals, Archival, FileExchange), and three more
  (`AddGoldpathJobs`, `AddGoldpathAuditLog` and `AddGoldpathOutboxEntities`, each on a `…ModelExtensions` class). Bulk's and
  Archival's lines are kept as written. Every generated DbContext calls them and `MigrationsAnalyzers.cs` names them, so a
  rename is a template and analyzer change in API-14 (#403) as well as a ledger line. If the owner picks one spelling, the two
  rows follow it.
- **API-3 (#392), engine methods go internal; the engine types stay public.** `GoldpathBulkEngine<TContext>`,
  `GoldpathArchiveEngine<TContext>` and `GoldpathBulkFileStore<TContext>` stay public by construction: DI builds them by type,
  and the public constructors of the jobs and admin services take them, so an internal type would be inconsistent
  accessibility (CS0051). Their methods are `remove` rows, for two reasons:
  - Every adopter-facing verb is on the admin service, which adds what the engine does not. Bulk's `UploadAsync` refuses a
    blank file name (#476) and triggers validation; Archival's `RetrieveAsync` scopes by tenant and records the retrieval metric.
  - The rest are the jobs' own steps. `ExecuteRangeAsync` takes a `GoldpathJobChunk`, which no adopter can construct (API-2's
    finding above).

  Goldpath's own tests call these methods, so API-14 (#403) adds `InternalsVisibleTo` for `Goldpath.IntegrationTests` (both
  packages) and `Goldpath.Archival.Tests`. Bulk already grants it to `Goldpath.Bulk.Tests`.
- **API-3 (#392), three things the documents promise and the API does not deliver.** The lines are kept or removed as
  their rows say, and nothing is added, because adding API is out of scope for a review part.
  - The bulk RFC names `IGoldpathBulkFileStore` as the file-storage seam (`docs/rfc/goldpath-bulk.md` lines 42 and 57). No
    such interface exists: the engine takes the concrete `GoldpathBulkFileStore<TContext>`.
  - `IGoldpathBulkFormat`, the RFC's D3 format seam, is public, but no builder method accepts an implementation (`Csv()` is
    the only one). An adopter can implement it but cannot plug it in.
  - The bulk RFC says the stored raw file "is retrievable only through the admin API" (`docs/rfc/goldpath-bulk.md` line 83).
    No admin route returns it: `MapGoldpathBulkAdmin` maps definitions, upload, batches, one batch, its errors, approve and
    reject. The only public way to the raw bytes is `GoldpathBulkFileStore<TContext>.OpenRead`, which the Bulk table removes.
    The owner decides whether the RFC sentence or a route is the fix.
- **API-4 (#393), P1 applied to the unprefixed types.** P1 keeps the flat `Goldpath` namespace *because* every name
  in it is module-prefixed. Ten public types in this group are not, and API-4 renames them with the prefix their
  siblings carry: in Abstractions `TenantId` → `GoldpathTenantId`, `ITenantContext` → `IGoldpathTenantContext`,
  `IUserContext` → `IGoldpathUserContext`, `IMultiTenant` → `IGoldpathMultiTenant`, `ISoftDeletable` →
  `IGoldpathSoftDeletable`, `IAuditedEntity` → `IGoldpathAuditedEntity`, `IAuditLogged` → `IGoldpathAuditLogged` and
  `IIntegrationEvent` → `IGoldpathIntegrationEvent`; in Idempotency `IdempotencyConflictBehavior` and
  `IdempotencyFingerprintMode` gain `Goldpath`. Two more unprefixed types, `StoredHttpResponse` and
  `SoftDeleteContributor`, need no public name at all and are `remove` rows (internal).
  - **The cost is the widest of the review**, and it is stated so the owner weighs it rather than meets it in API-14.
    Measured at 2782d3c: the Abstractions types appear on 11 live lines of other packages' ledgers (`TenantId` on
    Goldpath.Data, Goldpath.Messaging and Goldpath.MultiTenancy lines, `ITenantContext` on one Goldpath.Locking line),
    which API-7 (#396) renames the same way; and every generated entity, the template docs (`IIntegrationEvent` is in
    the generated CLAUDE.md's GP0401/GP0402 rule) and the analyzers that recognise these interfaces name them. The
    rename is a template, analyzer and upgrade-guide change in API-14 (#403), not only a ledger line.
  - **If the owner keeps the unprefixed names instead,** P1's reason has to change ("names are module-prefixed" is not
    true of them), and the 38 Abstractions, 1 Caching and 8 Idempotency `rename` rows flip to `keep` with that reason.
    The two `remove` rows do not depend on this: they go internal because nothing outside their package needs them.
- **API-4 (#393), P2 confirmed: `GoldpathAdminResult` joins the Abstractions table when API-14 moves it.** No
  Abstractions row is written for it now: its 14 lines (the type, its members and those its record synthesizes, all
  `remove` rows in the Jobs table) are not live in the Abstractions ledger yet, and the gate
  reads a `keep` for a line that is not live as stale. API-14 (#403) adds the 14 rows as `keep` in the same change
  that moves the type.
- **API-4 (#393), the idempotent-replay header moves to `GoldpathHeaders`.** `GoldpathIdempotencyMiddleware` goes
  internal (AddGoldpathIdempotency adds it through its own startup filter, and its constructor puts Mediant's
  `IIdempotentOperationCoordinator` into Goldpath's surface). Its `ReplayHeader` constant is the one part an adopter
  needs, so API-14 adds `const Goldpath.GoldpathHeaders.IdempotentReplay = "Goldpath-Idempotent-Replay" -> string!`
  to the Abstractions ledger beside `IdempotencyKey`, with a `keep` row. The header's value does not change.
- **API-4 (#393), the shared policies applied.** Notification's five `ct` parameters are renamed to
  `cancellationToken` (API-2's ct policy), and `GoldpathNotificationMetrics` with its writer `SetQueueSnapshot` is a
  `remove` (API-3's metrics policy): `SetQueueSnapshot` sets the gauge the notification runbook pages on. Notification's
  model extension `AddGoldpathNotification` is kept as written, waiting on API-3's model-extension spelling finding.
- **API-5 (#394), the experimental packages answered: every line `experimental`, `remove` or (one record) `rename`.**
  Measured at c9d23ae: 376 Approvals lines and 259 FileExchange lines, 635 in all (the issue's 462 was taken at 0186ca3,
  before #479 and #480 grew Approvals). 588 are `experimental` under `GPEXP001`/`GPEXP002`, 24 are `remove` and 23 are `rename`. The
  issue's four questions, answered:
  - **The EF stores go internal.** `GoldpathEfApprovalStore<TContext>` and `GoldpathEfFileLedger<TContext>` are
    registered only by `AddGoldpathApprovals<TBuilder, TContext>` and `AddGoldpathFileExchange<TBuilder, TContext>`,
    behind `IGoldpathApprovalStore` and `IGoldpathFileLedger`. No other public line and no template or sample names
    them, so unlike Bulk's `GoldpathBulkFileStore<TContext>` (which a public constructor takes) nothing forces them
    public. API-14 (#403) adds `InternalsVisibleTo` for `Goldpath.Approvals.Tests`, `Goldpath.FileExchange.Tests` and
    `Goldpath.IntegrationTests`, which construct them.
  - **The persistence rows stay public** (`experimental`): `GoldpathApprovalDelegationRow`,
    `GoldpathApprovalSignatureRow`, `GoldpathFileProcessedRow`, `GoldpathFileQuarantineRow`, `GoldpathFileArchiveRow`.
    They are EF entities of the module's model, and the adopter's migrations name them, which is why Bulk's and
    Archival's entities are kept. Whether every module's entities should go internal is one cross-module question, not
    this part's: it would also have to answer EF compiled models, whose generated code in the adopter's assembly names
    the entity types.
  - **The admin services stay non-generic.** `GoldpathApprovalsAdminService` and `GoldpathFileExchangeAdminService`
    read through their store interfaces, not the app's context, so a `TContext` parameter (method Q7) would bind
    nothing.
  - **Singular or plural:** the Archival rule applies (API-3): the module's entry points say `Approvals` /
    `FileExchange`, and one request, ladder, rung, file or rail says `Approval` / `File`. The one module-level name that
    breaks it, `GoldpathApprovalModelExtensions.AddGoldpathApprovalModel`, is the ModelBuilder extension, which waits on
    API-3's spelling finding, so it is `experimental` as written. `GoldpathApprovalEscalationJob` keeps its name
    because a job's type name is its key.
  - **One rename, for the owner to sign or overrule: `GoldpathQuarantinedRow` → `GoldpathFileQuarantinedLine`.**
    FileExchange has three types for one quarantined line: `GoldpathFileQuarantineRow` (the EF entity),
    `GoldpathQuarantinedRow` (the record `IGoldpathFileLedger.GetQuarantineAsync` and `GoldpathFileRailResult.Quarantined`
    return) and `GoldpathFileQuarantineInfo` (the admin DTO). The record alone lacks the `File` prefix every other
    FileExchange data type carries, and it takes the `…Row` suffix this review uses for EF entities, so it reads as a
    second entity. The 23 rows are its 18 own lines and the 5 lines whose signatures name it (`GoldpathFileRailResult`'s
    constructor, `Quarantined` and `Deconstruct`, `IGoldpathFileLedger.GetQuarantineAsync` and the in-memory ledger's; the EF ledger's is a
    `remove`). Renaming it now is cheap; after the T22 lift it is a major.
  - **The metrics policy applied:** `GoldpathApprovalsMetrics` and `GoldpathFileExchangeMetrics` are `remove`. Both
    are public static classes with no public member at all.
  - **Two kept shapes, said so the owner can overrule them:** `GoldpathApprovalRequest`'s five `set` accessors stay
    `set` (the engine moves the state on the request it read, and a custom store outside the assembly materializes
    it, which neither `init` nor an internal setter serves); and no `ct` parameter exists in either package (measured),
    so API-2's ct policy changes nothing here.
