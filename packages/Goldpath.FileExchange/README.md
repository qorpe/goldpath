# Goldpath.FileExchange

File-based integration rails as a **unit**: rails declared as data with baked,
compile-checked closures — file-level contracts, idempotent `(file, line)` ingestion,
per-row quarantine that never stops the batch, zero-duplicate replay/reprocess, and
archive marks — with the lifecycle published as integration events. Jobs+Bulk cover the
pieces; the recurring hand-written glue around them (validate → dedup → quarantine →
archive → reprocess) is this module.

```csharp
builder.AddGoldpathFileExchange(files => files
    .AddRail<RegistryRow>("registry-daily", rail => rail
        .Header(1)
        .ValidateFile(lines => TrailerCountMatches(lines) ? null : "truncated")
        .ParseLine(line => RegistryRow.Parse(line))
        .ValidateRow(row => row.Amount > 0 ? null : "non-positive amount")
        .Handle((row, ct) => ApplyAsync(row, ct))));
```

- **File contract** — `ValidateFile` rejects the WHOLE file (truncation, trailer
  mismatch); a rejected file ingests nothing and publishes `GoldpathFileRejected`.
- **Idempotent ingestion** — the `(rail, file, line)` key: a re-delivered or replayed
  file applies zero duplicates. Each processed line is marked with the SHA-256 of its
  content, so a DIFFERENT file delivered under a name already processed is refused whole
  (`GoldpathFileRejected`, naming the first line that differs) rather than skipped (#482).
- **Quarantine** — a bad row (parse throw, row-contract failure, handler throw)
  quarantines with its reason; the batch CONTINUES. `GoldpathRowsQuarantined` fires once
  per run.
- **Reprocess** — run the file again: good rows dedup, fixed rows apply and their
  quarantine records clear.
- **Archive mark** — every completed run marks the file archived; retention/purge is the
  Archival module's business.

Progress lives behind `IGoldpathFileLedger`; the in-memory ledger ships for tests and
single-node hosts, a database-backed ledger composes through the seam. Scheduled pick-up
and drop ride the Jobs module; transports (SFTP, share, object store) are composed
adapters, never rewritten (RFC §1).

**Console** — `MapGoldpathFileExchangeAdmin()` mounts the read-only admin surface
(`/goldpath/admin/fileexchange`, contract §7.1: rails with live counts, files newest-first,
the quarantine with each row's reason and age) and the operations console federates on it
as its seventh module. Behind the ops floor by default; `exposeUnsecured: true` is the
visible opt-out. There are no verbs: re-delivering a file IS the reprocess. The reads need
a ledger that implements `IGoldpathFileLedgerQueries` (both shipped ledgers do).

Boundary with qorpe.coexist: Sync migrates and reconciles STORES during a transformation;
FileExchange is the PERMANENT integration surface with counterparties (RFC D4).
