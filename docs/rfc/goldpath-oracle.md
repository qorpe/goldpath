# RFC: Oracle as a first-class database provider (a Data revision)

- Status: **PROPOSED** (2026-10-22). Asked for by [#305](https://github.com/qorpe/goldpath/issues/305)
  after the owner's decision on [#287](https://github.com/qorpe/goldpath/issues/287) (2026-10-20):
  **Oracle is IN for 1.0**. The decision was confirmed on 2026-10-21 as Q3 of
  [goldpath-ga-readiness](goldpath-ga-readiness.md), where it is gate **G7**. **Merging this RFC
  is its acceptance.** Documents only: nothing here moves code, a dependency or a default.
- Revises: [goldpath-data](goldpath-data.md), which says the provider "stays the caller's
  choice", by naming a third provider the golden path ships and proves. It also revises
  [goldpath-jobs](goldpath-jobs.md) §11, whose Oracle trigger has fired.
- Composes with: [ADR-0003](../adr/ADR-0003-configure-not-wrap-microsoft.md) (compose what the
  provider ships, never rewrite it), [ADR-0005](../adr/ADR-0005-executable-standards.md) (every
  standard ships with its verifier), [ADR-0008](../adr/ADR-0008-first-click-run-is-proven.md) (the
  first-click run is proven, and no merge while the golden-manifest matrix is red), [ADR-0013](../adr/ADR-0013-messaging-bus-is-a-swappable-engine.md)
  and [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) (the outbox belongs to the
  messaging engine, §2.9). Contradicts no ADR.
- Ledger: [open-threads.md](../strategy/open-threads.md) **T16** carries this RFC until its proof
  runs.

## 1. Scope / Non-Goals

**Scope.** Everything needed for `database: oracle` in a manifest, or `--db oracle` on the CLI,
to generate an application that builds, migrates, runs and passes its smoke test on a real
Oracle database. That means the same promise `postgresql` and `sqlserver` carry today:
- the EF Core provider and the ADO.NET driver, pinned;
- the licence-gate exception they need;
- every place in the packages where SQL or a provider name is spelled out, each given its Oracle
  form;
- the Quartz job store;
- the template, CLI, manifest and Aspire surface;
- the golden-manifest shapes;
- the proofs, on a real Oracle container;
- an ordered slice plan.

**Non-goals.**
- **Oracle-only features** beyond provider parity: no Oracle Advanced Queuing, no edition-based
  redefinition, no RAC-specific tuning. Partitioning appears only as guidance (§2.8).
- **Oracle versions below 19c.** The supported floor is **Oracle Database 19c**, the oldest
  release still in Premier/Extended Support in the market that asked. Proofs run on **Oracle
  Database Free 23.26** (the `gvenzl/oracle-free` image). Where newer syntax would be easier
  (`BOOLEAN`, `IF NOT EXISTS`), the 19c form is what ships.
- **Making Oracle the default.** PostgreSQL stays the golden-path default, and SQL Server stays
  the second shipped choice.
- **Any licence change for adopters who never choose Oracle.** The Oracle packages enter a
  generated app's graph only when `database: oracle` is selected (§3, §2.1).

## 2. Seam Map: every provider-specific point and its Oracle form

The Data module has one seam: the caller configures the provider inside
`AddGoldpathData(...)`. Everything else that is provider-specific was found by searching the
packages for `Npgsql`, `SqlServer` and raw SQL. Each row below is one place; the slice that
lands it is in §8.

### 2.1 Packages and the licence gate

| Package | Version (to pin) | Licence |
|---|---|---|
| `Oracle.EntityFrameworkCore` | **10.23.26301** (EF Core 10 line, published 2026-09-08) | Oracle Free Use Terms and Conditions (FUTC), `license type="file"` |
| `Oracle.ManagedDataAccess.Core` | **23.26.301** (published 2026-09-08; the EF provider's floor is `[23.26.301, 24)` on that line) | FUTC, `license type="file"` |

**Why `.26301` and not the newest `.26400`.** `.26400` was published on 2026-10-20, two days
before this RFC. The pin is the newest release at least two weeks old when slice O-1 lands. If
`.26400` is two weeks old by then, O-1 takes it and records the date.

**Transitive pins.** ODP.NET Core's `net8.0` group drags `System.Security.Cryptography.Pkcs
8.0.1`, `System.Formats.Asn1 8.0.1` and `System.DirectoryServices.Protocols 8.0.2`.
`Directory.Packages.props` already lifts `Pkcs` to `10.0.13` (GHSA-846f-f764-76mx,
GHSA-2fmv-rv2c-59x7). O-1 checks each of the three against the advisory database. Any that is
vulnerable gets a central lift with its GHSA id in the comment, exactly like the Pkcs precedent,
and the generated app's `Directory.Packages.props` carries the same lift for the Oracle shape.

**The licence-gate exception** (text for `scripts/license-gate.py`, scoped to these two package
ids and nothing else):

```python
    # Oracle Free Use Terms and Conditions (closed source, free of charge, redistributable).
    # The Oracle EF Core provider and its ODP.NET driver. Reachable ONLY through the template's
    # explicit `--db oracle` / manifest `database: oracle` choice — i.e. only adopters who
    # already run a commercially licensed Oracle Database. No Goldpath package references
    # them; the optional Goldpath.Locking.Oracle package (if O-4 ships it) is the one
    # exception and carries the same scope. Approved <date of acceptance> (goldpath-oracle RFC, #287).
    "Oracle.EntityFrameworkCore": "proprietary-free (Oracle Free Use Terms and Conditions)",
    "Oracle.ManagedDataAccess.Core": "proprietary-free (Oracle Free Use Terms and Conditions)",
```

The precedent is the SqlClient SNI runtime entry of 2026-07-05: closed source, free of charge,
reachable only through an explicit provider choice by an adopter who already licenses that
database. The exception is justified the same way and is no wider. The gate keeps failing on
**any other** FUTC package.

**Where they enter the graph.** Only the generated application's database-owning project
(`Api` in vertical-slice, `Infrastructure` in clean-architecture) references
`Oracle.EntityFrameworkCore`, and only under the template's `db == "oracle"` condition. No
`Goldpath.*` package references either id. So the packages' own licence surface is unchanged,
and `license-gate.py` over `Goldpath.sln` sees them only through the Oracle GM shape and the
Oracle test projects.

### 2.2 Migrations (D7)

- **Generation.** `goldpath db add <Name>` runs `dotnet ef migrations add` against the
  configured provider, so an Oracle app generates Oracle migrations. Nothing changes in the verb.
- **Identifier length.** Oracle 12.2 and later allow 128 bytes; 12.1 and older allowed 30. The
  floor is 19c, so the limit is **128**. Goldpath's names are far below it: for example,
  `IX_GoldpathCampaignItems_CampaignId_State_ReleasedAt` is 52 characters. O-2
  adds a model-contract test that fails when any table, column, index or constraint name in the
  Goldpath model exceeds 128 bytes, so a future name cannot break Oracle silently.
- **Case.** The EF provider quotes identifiers, so `"GoldpathCampaignItems"` keeps its case
  and nothing folds to upper case. Hand-written SQL in the packages must quote identifiers in
  the same case (§2.7). An adopter querying from SQL*Plus must quote too; the adopter guide
  says so.
- **Types.** EF's Oracle mappings are used as shipped, with no Goldpath value converters:
  - `bool` maps to `NUMBER(1)` (19c has no `BOOLEAN` column);
  - `Guid` maps to `RAW(16)`;
  - `DateTimeOffset` maps to `TIMESTAMP(7) WITH TIME ZONE`;
  - `string` without a max length maps to `NVARCHAR2(2000)`, or to `NCLOB` past the limit.

  O-2's model-contract goldens pin every Goldpath column's Oracle store type, so a provider
  upgrade that changes a mapping is red rather than silent.
- **`DateOnly` / `TimeOnly`** (Campaign `EndDate`, `WindowStart`, `WindowEnd`). The expectation
  is that the Oracle provider maps `DateOnly` to `DATE` and `TimeOnly` to an `INTERVAL DAY TO
  SECOND`. This is not verified here; O-2 proves both round-trip, including `TimeOnly` past noon and a
  `DateOnly` at a DST boundary. If either does not, the fallback is a Goldpath-owned
  `ValueConverter` registered by `ApplyGoldpathConventions()`, only when the provider is Oracle.
- **Sequences and identity.** Goldpath entities use `Guid` keys or composite natural keys, plus
  the campaign item's `(CampaignId, Seq)`, where `Seq` is assigned by the engine. None relies on
  database identity, so Oracle `IDENTITY` columns and sequences are not exercised by Goldpath's
  own model. An adopter's `int` key gets the provider's `GENERATED BY DEFAULT ON NULL AS
  IDENTITY`, as shipped.
- **Empty string.** Oracle stores `''` as `NULL`. No Goldpath column distinguishes an empty
  string from null, which O-2 asserts across the model. The adopter guide states it, because an
  adopter's `required` string column will refuse `""`.

### 2.3 The Quartz job store (Jobs §11)

Quartz.NET ships `OracleDelegate` and the Oracle `tables_oracle.sql` DDL under Apache-2.0.
- **`GoldpathJobStoreProvider`** gains `Oracle`. `GoldpathJobsExtensions` maps it to
  `store.UsePersistentStore(...)` with Quartz's Oracle provider (`UseOracle(...)`, present in
  the pinned Quartz 3.22.5 assembly). `GoldpathJobsFleetRegistry`'s raw properties gain
  `driverDelegateType = Quartz.Impl.AdoJobStore.OracleDelegate, Quartz` and
  `dataSource.default.provider = OracleODPManaged`.
- **The DDL.** `QuartzStoreModel` (the EF-managed `qrtz_*` tables) already writes unquoted
  identifiers. It does so because PostgreSQL folds them to lower case and Quartz's delegate SQL
  is unquoted. **Oracle folds unquoted identifiers to UPPER case**, and Quartz's Oracle delegate
  queries them unquoted, so they resolve. O-3 proves the model's table and column names match
  `tables_oracle.sql` once folded. Oracle's DDL uses `NUMBER(13)` for timestamps, `VARCHAR2`,
  and `BLOB` for job data; the model takes those store types under Oracle.
- **Clustering.** Oracle row locks (`SELECT … FOR UPDATE` on `QRTZ_LOCKS`) are what
  `OracleDelegate` uses. Nothing else is needed.

### 2.4 Keyset pagination

`ToPageAsync` (`Goldpath.Data/KeysetPagination.cs`) builds the keyset predicate as an
expression tree: `key > last` for one key, and `(k1 > a) OR (k1 = a AND k2 > b)` for two. EF
translates that portably, and `Take(n + 1)` becomes `FETCH FIRST n ROWS ONLY` on Oracle
(12c+). **No Oracle-specific code is expected.** The proof is the existing keyset suite run
against Oracle (O-2), including:
- `Guid` keys, where `RAW(16)` ordering is byte-wise, as on SQL Server's `uniqueidentifier`. The
  suite already asserts "stable, not chronological" for Guids.
- `string` keys, where Oracle's comparison is binary under the default `NLS_SORT=BINARY`. The
  adopter guide says a linguistic `NLS_SORT` on the session changes keyset order and is
  unsupported.

### 2.5 IN-lists above 1,000 elements

Oracle refuses an `IN (…)` list longer than 1,000 literals (ORA-01795). EF's Oracle provider
parameterizes primitive collections, but how it translates `Contains` over a large local list
is the risk. The engine's own `Contains` uses are:
- the campaign's `due.Contains(i.Seq)`, `orphans.Contains(i.Seq)` and `seqs.Contains(i.Seq)`
  (`GoldpathCampaignEngine.cs`);
- the outcome sink's batch.

Their sizes are bounded by `ReleaseBatchSize` and the sink batch (200), but a configured
`ReleaseBatchSize` above 1,000 would cross the limit.

**The decision:** a Goldpath helper, `ChunkedContains`, splits any local list above 1,000 into
`OR`-joined chunks of at most 1,000 when the provider is Oracle, and is a no-op otherwise. It
is used at every engine `Contains` site whose list size is configurable. O-5 proves it with a
`ReleaseBatchSize = 5,000` campaign on Oracle. The alternative was capping `ReleaseBatchSize`
at 1,000 under Oracle. It is rejected because a configuration value would then behave
differently by provider, and nobody would see why.

### 2.6 `SKIP LOCKED` claims and bulk insert

- **Claims.** Goldpath's claims are conditional `ExecuteUpdate`s (`ClaimAsync`: update where
  state allows; zero rows affected means someone else owns it). They are not `SELECT … FOR UPDATE
  SKIP LOCKED`, so they translate to Oracle `UPDATE … WHERE` unchanged. Nothing in the packages
  spells out `SKIP LOCKED` today. If a future engine needs it, Oracle supports `FOR UPDATE SKIP
  LOCKED` natively.
- **Bulk** (`Goldpath.Bulk`). The engine writes intake rows through EF batches and
  `ExecuteUpdate`, with no `COPY` or `SqlBulkCopy`, so it runs on Oracle as is. EF's Oracle
  batching uses array binding. O-5 measures the 1M-row intake on Oracle and records it in
  `ops/bulk-benchmarks.md`, with no budget promised until it is measured.

### 2.7 Hand-written SQL in the packages

| Where | Today | Oracle form (O-5) |
|---|---|---|
| `GoldpathCampaignMigrations.GoldpathCampaignR2Backfill` | Npgsql and SqlServer branches; anything else **throws** `NotSupportedException` | An Oracle branch: `UPDATE "GoldpathCampaignItems" SET "NextAttemptAt" = COALESCE("CompletedAt", SYSTIMESTAMP) + CASE WHEN "Attempts" <= 1 THEN INTERVAL '30' SECOND ELSE INTERVAL '2' MINUTE END WHERE "State" = 6 AND "NextAttemptAt" IS NULL`, with the schema-qualified form `"<schema>"."GoldpathCampaignItems"`. The provider is detected by `ActiveProvider` containing `Oracle`. The R2 test that executes the backfill on PostgreSQL gains an Oracle twin |
| `ServiceDefaults` metrics | `.AddMeter("Npgsql")` | ODP.NET publishes OpenTelemetry traces through `Oracle.ManagedDataAccess.OpenTelemetry`, a separate FUTC package. **Not adopted**: the EF Core meters already cover command duration. The adopter guide names the package as an opt-in |
| `Goldpath.Locking` | Redis, Postgres advisory locks, SQL Server `sp_getapplock` (optional package) | §2.9 |

O-5 also adds a test that fails if a new `migrationBuilder.Sql(` or `ExecuteSqlRaw` call appears
in `packages/` without an Oracle branch. It is a source scan in the style of the existing
architecture tests, so the next piece of hand-written SQL cannot forget the third provider.

### 2.8 Partitioning guidance

Goldpath does not partition, on any provider. The campaign items table at 30M rows is the one
table where an adopter will ask. Guidance only, in `docs/guides/oracle.md` (O-6):
- interval partitioning on `CampaignId` is not useful, because the key is a Guid;
- **list partitioning by campaign is the adopter's operation**, done after `goldpath db
  script` and before deployment, using Oracle's `ALTER TABLE … MODIFY PARTITION BY` (19c+,
  online);
- Goldpath's queries all lead with `CampaignId`, so partition pruning applies.

No code path depends on partitioning, and no proof claims it.

### 2.9 What is not the Data module's

- **Distributed locking.** `DistributedLock.Oracle` (MIT, depends on ODP.NET) gives
  `DBMS_LOCK`-based locks. Following the SQL Server precedent, an **optional
  `Goldpath.Locking.Oracle`** package (O-4) composes it, so that only Oracle choosers carry the
  FUTC driver through locking. The template wires it when `db == oracle` and `locking` is on.
  The alternative was the Redis lock provider for Oracle apps. It is rejected as the default
  because a generated app should not need Redis just because its database is Oracle. It stays
  available.
- **The transactional outbox.** It belongs to the messaging engine (ADR-0013/0015). The
  MassTransit EF outbox's lock statements have SQL Server, PostgreSQL and MySQL providers. Its
  Oracle support is **unverified**, and the engine is being replaced by 2026-12-31 (G1–G5). So
  the Oracle shape in this RFC ships **`broker: none` first** (GmOracle). The broker-on Oracle
  shape (GmOracleCampaign) waits for the engine switch, and its outbox is that engine's adapter
  question. Open-threads T16 records this as a named deferral, with its trigger: *the default
  engine after G5 publishes an Oracle outbox store*.

## 3. Manifest Surface

- `providers.db: oracle` stops being SCHEMA-ONLY in the slice that makes the Oracle GM shape
  green (O-6), not earlier.
  - The schema's `$comment` loses the SCHEMA-ONLY clause.
  - `scripts/schema-honesty.sh` drops `("providersFull", "db", "oracle")` from its annotated list.
  - `golden-manifests-v1.md` gains the shape.
  - All three happen in the same PR, as T16's proof requires.
- Because of §2.9, `db: oracle` with `broker: rabbitmq` gets a validation-time finding that names
  T16, until the broker-on shape is green. This extends GA readiness gate G3's "selected an
  annotated value" finding to one combination. The rule lives in specdrift and its id is assigned
  in O-6. `db: oracle` with `broker: none` is valid.
- No new manifest field: the provider choice is the existing `providers.db`.

## 4. API Surface

Additions only, in `PublicAPI.Unshipped.txt`:
- `Goldpath.Jobs.GoldpathJobStoreProvider.Oracle = 2`. It is appended, so no stored value moves.
- `Goldpath.Data.GoldpathQueryableExtensions.ChunkedContains<T>(…)` (§2.5). Its shape is set in
  O-5.
- The `Goldpath.Locking.Oracle` package (O-4): `AddGoldpathOracleLocking()`, mirroring
  `AddGoldpathSqlServerLocking()`. `GoldpathLockProvider.Oracle` is appended, and selecting it
  without the optional package fails loudly at registration, as SqlServer does today.

**The CLI and templates.**
- `goldpath new solution --db oracle`; the template's `db` choice gains
  `{ "choice": "oracle", "description": "Oracle Database 19c+ (FUTC-licensed driver)" }`.
- `goldpath-worker` gains the same choice.
- The AppHost uses `Aspire.Hosting.Oracle` **13.4.6**, the same line as the template's other
  Aspire packages. Its container image is pinned to **`gvenzl/oracle-free:23.26.3-slim-faststart`**,
  never `23`, `slim` or `latest`.
- The generated `Directory.Packages.props` pins `Oracle.EntityFrameworkCore` (§2.1) under the
  `db == oracle` condition.
- `goldpath db script` and `goldpath db bundle` work unchanged; both are EF verbs.

## 5. Analyzer Rules

None new. The existing rules are provider-agnostic. The one Oracle-specific hazard a rule could
catch is a 1,000+ `Contains`, and it is not statically decidable, so it is handled at runtime by
§2.5 and kept honest by O-5's source scan. GP numbers stay free.

## 6. Ops Package

- `docs/guides/oracle.md` (O-6), the adopter guide:
  - the supported floor (19c), the proof version (Oracle Database Free 23.26), and the licence statement (FUTC,
    reachable only by choice);
  - the quoting and case rule; `''` as null; `NLS_SORT=BINARY` for keyset order;
  - partitioning guidance (§2.8);
  - grants for the migrations user, and the separate runtime user without DDL rights (the D7
    two-user pattern, in Oracle terms: `CREATE SESSION` + object grants);
  - `goldpath db bundle` against an air-gapped Oracle;
  - the optional OpenTelemetry package (§2.7).
- **Runbooks.** The Jobs runbook gains Oracle's `QRTZ_LOCKS` contention signature. The Campaign
  runbook gains ORA-01795 ("you raised `ReleaseBatchSize` past 1,000 on a build without
  `ChunkedContains`").
- **Benchmarks.** O-5's Oracle numbers go into the existing `ops/*-benchmarks.md` of Bulk,
  Campaign and Jobs, as a third profile column. They are measured, not promised.

## 7. Test Plan (including golden-manifest impact)

**T16's proof, as the gate.**
- The golden-manifest matrix gains an Oracle shape.
- The D7 migration proofs pass on a real Oracle container.
- The Quartz store and keyset translation pass there too.
- The licence-gate exception is scoped to the Oracle packages only.
- `oracle` leaves the SCHEMA-ONLY list in the same PR.

**Suites on Oracle.** A Testcontainers fixture (`OracleBuilder`, the same pinned image) and the
`Category=Oracle` trait. The following run against it:
- **Data:** model-contract goldens (store types, identifier lengths, `''`/null), keyset (all
  key shapes), `DateOnly`/`TimeOnly` round trip, D7 migrate up, down and idempotent.
- **Jobs:** the clustered store, two schedulers and one fire; misfire; the fleet registry.
- **Campaign:**
  - the R1 and R2 broker-less suites (engine, pacer, sink) on Oracle;
  - the R2 backfill's Oracle branch executed on a real Oracle;
  - a 5,000-item `ReleaseBatchSize` release (§2.5).
- **Locking:** the `DistributedLock.Oracle` composition, with two holders and one winner.

**Golden-manifest shapes** (`.github/gm-shapes.yml`):

| Shape | Args | Lane | Why |
|---|---|---|---|
| **GmOracle** | `--db oracle --broker none --auth none` | **PR** (`pr: true`) | The provider is a template condition. A template change that breaks it must be red at PR time, not the next night (T30's lesson) |
| **GmOracleCampaign** | `--db oracle --features campaign --features jobs --features locking` (broker on) | **nightly**; added when §2.9's trigger fires | Pulls the most provider-specific code (Quartz store, backfill, chunked `Contains`, Oracle locking). Nightly because an Oracle container plus a broker exceeds the PR lane's budget |

**Cost.** Oracle Free's slim-faststart image starts in about 20 to 40 seconds on the CI
reference profile; this is to be confirmed in O-2 and written down. The PR lane gains one shape.
The nightly gains the Oracle suites.

## 8. DoD and the ordered slice plan

**DoD.**
- Every row of §2 has landed or has a named deferral in T16 with its trigger.
- GmOracle is green on PR and nightly lanes.
- The suites in §7 are green on Oracle.
- `license-gate.py` is green with exactly the two exceptions of §2.1.
- `oracle` is off the SCHEMA-ONLY list.
- The adopter guide is published.
- G7 in the GA readiness table points at the evidence.

**Slices.** Each is at most about 3 hours of agent work, red-first, in this order. Where a slice
needs real Oracle, its tests are `Category=Oracle` and run in their own CI lane.

| # | Slice | Depends on | Train |
|---|---|---|---|
| **O-1** | Pins and the licence gate. The two packages in central `Directory.Packages.props` (test projects only), the transitive advisory check (§2.1), the `license-gate.py` exception with its date, and the Oracle Testcontainers fixture with the pinned image and a "container starts" test | acceptance | preview.10 |
| **O-2** | Data on Oracle. Model-contract goldens for store types, identifier ≤128 and `''`/null; keyset suite; `DateOnly`/`TimeOnly`; D7 migrate up, down and idempotent | O-1 | preview.10 |
| **O-3** | Jobs on Oracle. `GoldpathJobStoreProvider.Oracle`, `OracleDelegate` and the fleet-registry properties, the `qrtz_*` model against `tables_oracle.sql`, clustered-store tests | O-1 | preview.10 |
| **O-4** | Locking on Oracle. The optional `Goldpath.Locking.Oracle` package, `GoldpathLockProvider.Oracle`, two-holder test, licence-gate scope note | O-1 | preview.11 |
| **O-5** | Campaign and Bulk on Oracle. R2 backfill Oracle branch, `ChunkedContains` at every configurable `Contains`, the 5,000-batch release proof, the hand-written-SQL source scan, Bulk and Campaign Oracle bench columns | O-2, O-3 | preview.11 |
| **O-6** | The adopter surface. Template `--db oracle` (solution and worker), AppHost with `Aspire.Hosting.Oracle`, the generated pins, **GmOracle** (`pr: true`), the oracle-with-broker finding (§3), schema `$comment` and `schema-honesty.sh`, `golden-manifests-v1.md`, `docs/guides/oracle.md`, G7 evidence. **`oracle` leaves SCHEMA-ONLY here** | O-2 to O-5 | preview.11 |
| **O-7** | GmOracleCampaign (broker on), nightly | §2.9's trigger: the post-G5 engine publishes an Oracle outbox store | after G5 |

O-1 to O-6 land before G4 freezes the public API (GA readiness RFC), because O-3 and O-4 append
enum values.

## Open questions for the owner

1. **The 19c floor.** Recommended: 19c. Is any committed adopter on 12.2 or 18c? If so, the
   identifier, `FETCH FIRST` and `DateOnly` proofs need an older container, and none is freely
   available.
2. **Broker-on Oracle before G5.** Recommended: no; wait for the post-G5 engine (§2.9).
   Supporting the MassTransit EF outbox on Oracle now would be work thrown away on 2026-12-31.
3. **`Goldpath.Locking.Oracle` as a separate package**, following the SQL Server precedent.
   Recommended: yes. The alternative is Redis-only locking for Oracle apps.
