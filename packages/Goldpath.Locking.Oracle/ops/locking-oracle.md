# Locking.Oracle — Ops Runbook

The Oracle provider for `Goldpath.Locking` (Medallion's `DistributedLock.Oracle`: `DBMS_LOCK` user
locks on the app database — zero new infrastructure). The metrics and the dashboard are the Locking
module's (`goldpath_lock_acquire_total{outcome}`, `goldpath_lock_wait_seconds`,
`Goldpath.Locking/ops/grafana-locking-dashboard.json`); this runbook is the provider's half: the grant
it needs, what a held lock looks like from the server, and what goes wrong with it.

## The grant (required)
`DBMS_LOCK` is not granted to `PUBLIC`. Run once, as a DBA, for the user the application connects as:
```sql
GRANT EXECUTE ON SYS.DBMS_LOCK TO <runtime_user>;
```
Without it, the first acquire fails with `PLS-00201: identifier 'SYS.DBMS_LOCK' must be declared`.
The lock contract test grants it exactly this way (`LockContractOracleTests`).

## Inspecting a held lock
A user lock is lock type `UL`. `DBMS_LOCK` maps the lock's name to a numeric id, and
`DBA_LOCKS` names the holder's session:
```sql
SELECT l.session_id, l.lock_id1, l.mode_held, l.mode_requested, l.last_convert AS seconds_in_mode
FROM dba_locks l
WHERE l.lock_type = 'PL/SQL User Lock';
```
To see which Goldpath lock name an id is, join `SYS.DBMS_LOCK_ALLOCATED` on `lockid = lock_id1`
(the provider allocates its names there). That column holds 128 BYTES: a name that is ASCII and fits
reads as the Goldpath lock name itself; any other name — longer, or with non-ASCII characters such as
Turkish ones — is stored by Goldpath (`OracleLockNames`, before the library sees it) as its leading
ASCII, `#`, and the hex SHA-256 of its UTF-8. To find such a lock, compute the same digest of the name. `V$LOCK` with `type = 'UL'` shows the same rows by `sid`; rows with `request > 0` are the
contention the timeout counter is about to record. Reading `DBA_LOCKS` and `V$LOCK` needs
`SELECT_CATALOG_ROLE` (or a grant on the views) for the inspection queries only.

## "Job didn't run anywhere" (lock leaked vs held)
A user lock taken by this provider is SESSION-owned: it is held on a connection the provider keeps
open, and it dies with that session, crash included — a "leaked" lock is a session that is still
alive. Find it (`V$SESSION` on the `sid` above); if the holder is a hung process, kill the SESSION
(`ALTER SYSTEM KILL SESSION 'sid,serial#'`), never the lock. A held lock with a live holder is not a
leak — the paused-holder hazard applies: locks are mutual exclusion, not correctness fences. Design
the handler idempotent (the Idempotency module) rather than shortening the timeout.

## Timeouts climbing
`goldpath_lock_acquire_total{outcome="timeout"}` rising on one lock name = contention on that
resource. Before scaling, check the holder's duration (`seconds_in_mode` above): a handler holding
the lock across a slow dependency call serializes the whole fleet behind that dependency.

## Cancellation and shutdown
On Oracle, cancelling a waiting acquire does NOT return it early. `DBMS_LOCK.REQUEST` is a
server-side wait that `DistributedLock.Oracle` does not interrupt, so the cancellation token is
observed only when that wait ends: a cancelled `TryAcquireLockAsync` / `AcquireLockAsync` on a held
lock returns (`OperationCanceledException`) only after up to its full acquire timeout. Goldpath passes
the token down unchanged; this is the library's behaviour, pinned by
`LockContractOracleTests.A_cancelled_acquire_on_a_held_lock_waits_out_its_timeout_before_it_observes_the_token`
so an upgrade that changes it is noticed. Redis and Postgres are not affected.

What this means for a host:
- A host shutting down, or a request whose client went away, stays blocked on a held lock for the
  remaining acquire timeout. Keep every acquire timeout SHORTER than the host's shutdown timeout
  (`HostOptions.ShutdownTimeout`) and the pod's `terminationGracePeriodSeconds`, or graceful shutdown
  overruns and the pod is killed mid-work.
- A guard that must wait long (minutes, for a nightly job) should loop on `TryAcquireLockAsync` with a
  short timeout (a few seconds) and check the token between attempts, rather than make one long wait.
  The cost is bounded by the slice, not by the overall wait.

## Which database holds the lock
The lock lives in the database (the PDB) named by `ConnectionName` — the app database by default.
User locks are scoped to the database instance: two applications that connect to DIFFERENT pluggable
databases never contend, and two that share one contend on the same name — which is what the
tenant-scoped names of `GoldpathLockNames` are for.

## Connection pool
Each held lock holds one pooled connection for as long as it is held. Size the pool
(`Max Pool Size` on the ODP.NET connection string) for the locks the fleet holds at once plus the
application's own work; a pool exhausted by holders turns every acquire into a pool timeout.
