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
