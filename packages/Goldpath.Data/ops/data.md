# Data — Ops Runbook

The floor's persistence: EF Core on the app's own DbContext with the Goldpath conventions
(UTC `DateTimeOffset`, model defaults), keyset pagination (`ToPageAsync`), and the migration
discipline (Development walks the SAME migrations production applies from the CI bundle —
migrations RFC D2; `EnsureCreated` is analyzer-flagged, GP0302).

## Pending-migration detection
- A host that starts against a database BEHIND its model fails loudly on the first query
  that touches the missing column — never silently. Before that: `goldpath db status`
  (the CLI verb) lists pending migrations against the app's connection; run it in the
  deploy pipeline as a gate.
- `__EFMigrationsHistory` is the truth: `SELECT "MigrationId" FROM "__EFMigrationsHistory"
  ORDER BY 1 DESC LIMIT 3;` — compare with the newest file under `Migrations/`.
- Two replicas migrating at once race into "relation already exists": the template
  serializes Development migrations behind a database lock (`pg_advisory_lock` /
  `sp_getapplock`). Production applies the bundle ONCE, from CI, never from the app.

## Keyset pagination troubleshooting ("rows are skipped / repeated")
`ToPageAsync` walks a cursor over an ORDER BY that must be total and stable:
1. The order-by must end in a UNIQUE key (the id); ordering by a non-unique column alone
   skips ties across pages.
2. The cursor encodes the LAST row's key values — a client that mutates the cursor gets
   `400` (cursor-invalid); a spike there is a client bug, not data loss.
3. `Skip/Take` is analyzer-flagged (GP0301): if a list drifts under concurrent inserts,
   someone bypassed the page helper.

## Oracle

Measured in G7 slice O-2 (#405), on `gvenzl/oracle-free:23.26.3-slim-faststart` with `Oracle.EntityFrameworkCore`
10.23.26301. The proofs are `tests/Goldpath.IntegrationTests/OracleDataTests.cs` (`Category=Oracle`).

- **Cost of the container.**
  - Agent sandbox, image already pulled: 27.6 s and 32.6 s to accept connections, measured by the lane fixture's
    `StartUpTime`.
  - GitHub-hosted runner (ci run 40725896363, job 122948933921, 2026-10-28, the O-1 fact alone): 54 s from test
    discovery to the result. That covers the image pull and the start; CI's logger does not print the fixture's
    own number.
- **The 19c form must be asked for.** The provider's default SQL form is 23ai's, which maps `bool` to `BOOLEAN`, a
  column type 19c does not have. To run on 19c, use
  `UseOracle(cs, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))`; `bool` is then
  `NUMBER(1)`. The golden in `tests/Goldpath.IntegrationTests/Oracle/composed-model.golden.txt` is that form.
- **`DateOnly` and `TimeOnly` are strings.** They are `NVARCHAR2(10)` and `NVARCHAR2(48)`, not `DATE` and
  `INTERVAL`. They round-trip, including `23:59:59.9999999` and a DST date, and range filters order correctly. Read
  them as text when you query from SQL*Plus.
- **Document columns are capped at 2,000 characters** (`HasMaxLength(-1)` → `NVARCHAR2(2000)`), and a NULL in one
  fails in ODP.NET (`OracleParameter.set_Size`) before it is sent. Until #475 lands, a job execution that succeeds
  (its `Error` is NULL) cannot be recorded on Oracle.
- **`''` is NULL.** Every NOT NULL string column refuses an empty string with `ORA-01400`. The known writers of
  `""` are #384 (the global ceiling row) and #476 (three admin verbs that accept a blank value). A LINQ filter
  `x == ""` is translated as a NULL test, so on Oracle it matches the rows whose value is NULL: a query ported from
  PostgreSQL returns different rows, with no error.
- **Keyset has no Oracle branch, and it is correct only in a session that sorts BINARY.** `ToPageAsync` emits
  `FETCH FIRST … ROWS ONLY`, and orders as the database session does:
  - a string key in BINARY order, which is by code point (`A` before `a`; dotted `İ` and dotless `ı` by code point);
  - a `Guid` key by its `RAW(16)` bytes, which is stable but not the order rows were created in.

  ODP.NET decides a session's NLS settings from a .NET culture, and which culture depends on pooling:
  - a **pooled** connection takes the `CurrentCulture` of the thread that **creates the pool**, and every session
    in that pool keeps it for the pool's life, whatever culture later requests run under;
  - an **unpooled** connection takes the culture the process **started** under; a culture set in code is ignored.

  A Turkish culture opens the session with `NLS_SORT=TURKISH` and `NLS_COMP=BINARY`, so the page's `ORDER BY`
  sorts linguistically while its `key > cursor` compares by code point, and a walk over a string key **silently
  skips rows** (measured: 4 of 12 at a page size of 1, 6 of 12 at 5). Until Goldpath's Oracle composition pins the
  session's sort ([#496](https://github.com/qorpe/goldpath/issues/496)), **pin it on every connection**: run
  `ALTER SESSION SET NLS_SORT=BINARY NLS_COMP=BINARY` when a connection opens (an EF Core connection interceptor's
  `ConnectionOpened`); `A_string_key_walk_in_a_turkish_process_walks_every_row_once_the_session_sort_is_pinned`
  proves it walks 12 of 12 on a pool created under `tr-TR`. Setting an invariant culture in code is not
  enough: it does not reach unpooled sessions, and with per-request localization the pool's sort is decided by
  whichever request opened it first. Only a process that **starts** non-Turkish (a non-Turkish `LANG`, or
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) and never creates a pool on a Turkish thread avoids it without the pin.
- **Identifiers fit.** Every table, column, key, index and constraint name in the composed model is at most
  76 bytes, against Oracle's 128-byte limit.

## Connection-pool exhaustion
Symptoms: p95 climbs, then `TimeoutException` acquiring a connection. Causes in order of
likelihood: a DbContext captured in a singleton (a scope leak — the EF meter's
`active_dbcontexts` never returns to baseline), long transactions holding connections
(the outbox commits WITH the business row on purpose — keep handlers short), or a pool
sized below replica count × concurrency. Raise `Maximum Pool Size` only after the leak is
ruled out.

## Signals
EF Core's meter (`Microsoft.EntityFrameworkCore`: queries, saved changes, active contexts,
compiled-query cache hits/misses, execution-strategy failures, optimistic-concurrency
failures) reaches the collector through `Goldpath.ServiceDefaults` since 2026-09-03; the
Npgsql meter (`Npgsql`: connection pool usage, open/idle/busy) rides with it. SQL Server's
client exposes EventCounters, not a Meter — pool signals there come from the server
(`sys.dm_exec_connections`) until the client ships one.

## Dashboard
`grafana-data-dashboard.json` — query and save-changes rates, active contexts (the leak
line), compiled-query cache misses (a climbing line is dynamic LINQ defeating the cache),
concurrency and execution-strategy failures, Npgsql pool usage.
