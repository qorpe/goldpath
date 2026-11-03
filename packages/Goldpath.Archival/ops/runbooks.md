# Goldpath.Archival — runbooks (archival RFC §8: runbook or it didn't happen)

Lifecycle verbs live at `/goldpath/admin/archival`; RUN views (archive/purge/verify progress)
live in the JOBS console — the three archival runs are jobs. Hold rows and erasure records
ARE the audit: every verb leaves durable who/when/what.

## 1. Auditor recall (retrieve + prove integrity)

1. `GET /entries/{definition}/{key}` (tenant-scoped when the request is tenant-bound) —
   the document plus its hashes; the p95 budget is 5s at 1M entries (measured: 0.5ms).
2. `POST /definitions/{definition}/verify` — a clean result is the integrity statement:
   link continuity, content hashes, and lawful-erasure divergences all checked.
   - **Before you call a finding tamper on PostgreSQL:** an entry archived before #383's fix
     from a `DueAt` with sub-microsecond ticks fails "document does not match its content hash"
     with nothing changed, because it was hashed at 100 ns and stored at µs. Check whether the
     definition's `DueWhen` date selector reads the clock or does fractional arithmetic, rather
     than reading a column, and whether the entry predates the fix. Remedy: open-threads T58.
3. For a RANGE, hand the auditor the verify output plus the entries — the chain hash makes
   the set self-proving; nothing needs to be trusted about the database operator.

## 2. Legal hold — place / lift

- Place: `POST /entries/{d}/{k}/hold {"caseReference":"LIT-…"}` — the case reference is
  MANDATORY (it justifies the hold later). The hold row records who/when.
- Effect: the entry is exempt from retention purge AND from erasure (D4). A held entry in
  the chain also stops the purge of everything BEHIND it — deliberate: prefix purges keep
  the chain verifiable.
- Lift: `POST /entries/{d}/{k}/lift-hold` — stamps who/when on the same row. The next
  purge run releases the backlog.
- Review: `GET /holds?includeLifted=true` is the litigation-readiness report.

## 3. Erasure request (KVKK/GDPR) — end to end

1. Identify the aggregates for the subject (domain knowledge — the app's index, not ours).
2. Per aggregate: `POST /entries/{d}/{k}/erase {"subjectKey":"…","detail":"ticket …"}`.
   The verb: refuses under an active hold; refuses without the DataProtection module
   (classification is what tells the archive WHAT to redact); redacts every classified
   field INSIDE the document; re-stamps the content hash; marks `erasedAt`; writes the
   evidence row.
3. Answer the authority from `GET /erasures` — the evidence trail is a query, not a search.
4. Integrity after erasure: verification treats content/chain divergence WITH the erasure
   mark as lawful; run `verify` to confirm the chain still stands.

## 4. Tamper alarm (verify failures > 0)

Signal: `goldpath_archival_verify_failures_total` increments, or the verify job's run shows
repair-queue items (`Definition#ChainIndex` + problem).

1. Localize: the finding names the index — `GET /entries/...` around it; the problem text
   distinguishes broken links, content mismatches, and unlawful re-stamps.
2. This is an INCIDENT, not a data fix: preserve the database, involve security. The chain
   tells you WHERE and WHAT KIND; backups + WAL tell you WHEN and WHO.
3. Never "repair" an entry to silence the alarm — the divergence IS the evidence.

## 5. Purge did not shrink / backlog grows

- `GET /definitions` — `dueBacklog` vs `entries`, `purgedThrough` vs `chainHead`.
- A held entry stops the prefix purge at itself (§2) — check `GET /holds` first.
- Row retentions only purge rows passing their `Where` guard (for example: only rolled-up
  detail) — a stalled rollup job starves the purge BY DESIGN; fix the rollup, not the guard.
- Catch-up after downtime is automatic: the runs are Jobs — resumable, chunked, deadline-
  predicted; watch the jobs console.

## 6. Envelope version bump

`schemaVersion` is stamped per entry. A reader newer than the entry reads old versions; a
reader OLDER than an entry must refuse (never guess forward). Bumps arrive with explicit
migration notes in this module's CHANGELOG entry.
