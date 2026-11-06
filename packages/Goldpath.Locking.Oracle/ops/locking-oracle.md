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
(the provider allocates its names there; a name longer than Oracle's limit is stored hashed by the
library). `V$LOCK` with `type = 'UL'` shows the same rows by `sid`; rows with `request > 0` are the
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

## Which database holds the lock
The lock lives in the database (the PDB) named by `ConnectionName` — the app database by default.
User locks are scoped to the database instance: two applications that connect to DIFFERENT pluggable
databases never contend, and two that share one contend on the same name — which is what the
tenant-scoped names of `GoldpathLockNames` are for.

## Connection pool
Each held lock holds one pooled connection for as long as it is held. Size the pool
(`Max Pool Size` on the ODP.NET connection string) for the locks the fleet holds at once plus the
application's own work; a pool exhausted by holders turns every acquire into a pool timeout.
