# Goldpath.Bulk — measured performance (bulk RFC §6)

Reference profile: local dev machine (Apple Silicon), PostgreSQL 17 in Testcontainers,
`scripts/bench-bulk.sh` (tests: `BulkBenchTests`, Trait `Category=Bench`). Re-run and
update this file whenever the intake or execution path changes.

| Proof | Budget | Measured (2026-07-07) | Verdict |
|---|---|---|---|
| 10k-row CSV: upload → parse → validate → report | < 5 min (the finance card) | **0.39 s** (upload 0.04 s) | ~770× headroom |
| 100k-row execute, no-op handler (chunk 500) | — (baseline) | **17.1 s ≈ 5,840 rows/s** | claim+stamp+counter overhead only |

## Reading the numbers

- **Intake is not the bottleneck and never will be** — the card's 5-minute budget was
  written for end-to-end operator experience; the engine spends it 770× under. Real
  intakes add domain-validator lookups; budget accordingly, the pipeline itself is free.
- **Execute throughput is the FLOOR, not the ceiling:** 5,840 rows/s is what the
  engine's honesty costs — the persisted CLAIM before any side effect, the per-row stamp,
  the batched counter updates, one checkpoint per 500 rows. A real handler's external
  call will dominate; the module's own overhead is ~0.17 ms/row.
- Upload streams and hashes in 256 KB chunks — the file never materializes as one array;
  memory stays flat regardless of file size.

## Reference profile (CI): ubuntu-latest, 4 vCPU / 16 GB — 2026-07-13

The PINNED profile adopters can rent and budget against (`bench.yml` dispatch,
run 29228081113; the dev-machine numbers above/below are the fast point, not the promise).

| Proof | Budget | CI measured | Verdict |
|---|---|---|---|
| 10k-row CSV: upload → parse → validate → report | < 5 min | **0.85 s** (upload 0.03 s) | ~350× headroom |
| 100k-row execute, no-op handler (chunk 500) | — (baseline) | **69.0 s ≈ 1,450 rows/s** | ~0.7 ms/row module overhead on 4 vCPU |

## 10M-row device list: upload → validate → report (#300, 2026-10-24)

`BulkIntakeBenchTests` via `scripts/bench-bulk.sh intake`: a 10,000,000-row, 162 MiB CSV of
device identifiers (`dev-` + 12 digits) on Testcontainers PostgreSQL 17, a definition with
`MaxRows(10_000_000)` and `RowKey(r => r.DeviceId)`, the default `InsertBatchSize` (1,000).
Every 100th row repeats the device of the row 4,999 rows above it — 99,951 repeats, each one
confirmed against a row that is already written, the expensive path. Peak managed heap is
sampled every 100 ms during the run; the working set is the process's peak.

**Profile:** the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker
Engine 29, everything co-located. Not the CI reference profile. The code column names the
engine's commit; both runs used this change's bench test. **Run id: none** — local runs, one each. The CI profile was not run: the next `bench.yml` dispatch records it here.

| Duplicate check | Code | Upload | Validate | Rows/s | Peak managed heap | Peak working set | Database |
|---|---|---|---|---|---|---|---|
| a 64-bit hash per key, exact confirmation (this change) | `aeec739` | 9.6 s | 818.3 s | **12,079** | **541 MiB** | **584 MiB** | 1,239 MiB |
| every key as a string (before) | `e5c9a24` | 6.9 s | 670.0 s | 14,773 | 1,226 MiB | 2,199 MiB | 1,239 MiB |

Both runs reported the same: 9,900,049 valid, 99,951 invalid, 99,951 "duplicate of row N
within the file" findings.

### Reading the numbers

- **Memory is the point of the change.** The duplicate check keeps a 64-bit hash and a row
  number per distinct key, never the key: about 40 bytes per key (measured 42.2 bytes per extra
  17-character key in `DuplicateKeyMemoryTests`, against 98.2 before). That is a FIXED cost per
  key, independent of the key's length — not constant memory: 10M distinct keys still hold
  roughly 400 MiB, which is most of the 541 MiB peak. Open-threads T49 carries what a
  constant-memory check would take.
- **The price is one read per duplicate whose first row is already written.** A hash hit is
  confirmed by re-deriving the first row's key from its staged payload, from the flush buffer
  when it is still there and by primary key otherwise. On this file every one of the 99,951
  repeats takes the read, and the run is 151 s slower than the string table; that matches
  about 1.5 ms per read on this co-located profile, but it was not measured separately. A
  file with no duplicates pays nothing, and a repeat within the same 1,000-row flush reads
  memory, not the database. T49 also tracks batching those reads.
- **A key its payload cannot give back keeps the old cost.** The confirmation needs the key
  to survive the row's JSON round trip. A definition whose key does not (a `[JsonIgnore]`
  property, a private setter) is detected on the first keyed row of each pass, logged as a
  warning, and validated with every key kept in full — the string table's memory, with the
  same report. A key lost by some rows only passes that check; its first repeat is caught
  and the pass is validated again from the start keeping keys in full, so such a file pays
  for the rows read before that repeat twice. The bench's row type is not such a definition.
- **Validation dominates, upload does not.** The 162 MiB file uploaded in under 10 s; the
  ~12,000 rows/s of validation is the per-row staging write (10M rows → 1.2 GiB of database).

## Oracle column (G7 Oracle 8/12, #556): 2026-11-26

`GOLDPATH_BENCH_PROVIDER=oracle scripts/bench-bulk.sh` runs the same facts on Oracle: `BulkBenchOracleTests` (the 10k
intake and the 100k execute) and `BulkIntakeBenchOracleTests` (set `GOLDPATH_BENCH_BULK_INTAKE_ROWS=1000000` for the
1M-row intake). Each fact ran alone, one after the other, PostgreSQL first, on the same machine.

**Profile:**
- Machine: the overnight agent's cloud sandbox, Linux x64, 4 logical CPUs / 15 GB, Docker Engine 29.9.3, with the bench
  process and the database co-located. This is not the CI reference profile.
- Build: **Debug**, as `scripts/bench-bulk.sh` runs it.
- Oracle: Database Free `gvenzl/oracle-free:23.26.3-slim-faststart` (`OracleFixture.Image`), ODP.NET 23.26.301, Oracle EF
  10.23.26301, in the 19c SQL form.
- PostgreSQL: `postgres:17-alpine`.
- Code: `main` at `3403c2c`, with #559's `BLOB` file chunk. No package change.
- **Run id: none.** These are local runs, one each, with each fact selected by its own `--filter` (the same filters
  `scripts/bench-bulk.sh` composes).

| Proof | Budget (§6) | PostgreSQL 17 | Oracle Free 23.26.3 |
|---|---|---|---|
| 10k-row CSV: upload → parse → validate → report | < 5 min | **1.84 s** (upload 0.67 s) | **3.23 s** (upload 1.25 s) |
| 1M-row device list: upload → validate → report (`BulkIntakeBenchTests`, 9,951 repeats) | — | upload 1.1 s, validate 47.8 s, **20,485 rows/s**; peak managed heap 296 MiB, working set 460 MiB, database 131 MiB | upload 1.6 s, validate 81.9 s, **11,981 rows/s**; peak managed heap 207 MiB, working set 398 MiB, schema segments 164 MiB |
| 100k-row execute, no-op handler (chunk 500) | — (baseline) | **not measured**: see below | **not measured**: see below |

Both intakes reported the same: 990,049 valid, 9,951 invalid, 9,951 "duplicate of row N within the file" findings.

**The 100k execute is not measured on either provider.** On `3403c2c` it does not finish: 60,000 of 100,000 rows after
21 minutes on PostgreSQL, at about 25 rows/s and slowing, with the test process at one full core and the database idle.
The same fact on `5411bd9` (the commit before #587) ran in 35.4 s (2,827 rows/s) on this machine. #587 snapshots the
change tracker for every row, which costs O(tracked entities) per row, and the bench runs every chunk in one context.
That is [#605](https://github.com/qorpe/goldpath/issues/605). An Oracle number taken now would measure that defect, not
the provider, so the column waits for #605 (open-threads T68).

**#605 fixed (2026-11-27): the 100k execute on PostgreSQL is 29.8 s ≈ 3,360 rows/s** again, on the same machine and
profile as above (Debug, co-located, `postgres:17-alpine`), `scripts/bench-bulk.sh card` on branch
`agent/605-bulk-tracked-per-row` over `main@fe67a79`. The 10k intake in the same run took 0.48 s. Run id: none (one
local run). That is the same order as before #587 on this machine (35.4 s on `5411bd9`). The engine now records what a
row tracks as EF raises it, instead of snapshotting the tracker per row. The Oracle column is still open-threads T68's:
it is measured once this is on `main`.

### Reading the numbers (oracle RFC §2.6)

- **The intake stays within the same order of magnitude on Oracle.** At 1M rows, validation runs at 0.58× PostgreSQL's
  rate (11,981 against 20,485 rows/s). The engine writes intake rows through EF batches and `ExecuteUpdate`, with no
  `COPY` or `SqlBulkCopy` on either provider. On Oracle, EF batches the inserts with ODP.NET array binding, and that is
  what keeps the gap under 2×. The per-row staging write still dominates, as it does on PostgreSQL. Oracle-specific
  tuning (direct-path loads, partitioning) is out of scope (#287).
- **The 10k card is 1.8× PostgreSQL's** (3.23 s against 1.84 s), of which upload is 1.25 s against 0.67 s; the file's
  chunks are a `BLOB` on Oracle and `bytea` on PostgreSQL (#559). Oracle's is still about 90× inside the 5-minute
  budget.
- **Memory is the duplicate check's, not the provider's.** The managed peaks are the same order on both, and are set by
  the 64-bit hash per key (see the 10M section). Oracle's lower peak is within run-to-run noise for a single run and is
  not a claim. "Schema segments" is `USER_SEGMENTS` for the bench's schema, the nearest Oracle reading to PostgreSQL's
  database size, and is not the same quantity.
- **No budget is promised on Oracle** (oracle RFC §6: measured, not promised). The facts assert correctness only: the
  same valid, invalid and duplicate counts on both providers.
