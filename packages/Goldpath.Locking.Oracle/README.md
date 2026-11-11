# Goldpath.Locking.Oracle

The Oracle Database (`DBMS_LOCK`) provider for `Goldpath.Locking` — shipped as a separate package
ON PURPOSE.

## Why separate

The dependency chain (`DistributedLock.Oracle` → `Oracle.ManagedDataAccess.Core`) carries Oracle's
ODP.NET driver under the **Oracle Free Use Terms and Conditions** (FUTC: closed source, free of
charge, redistributable). The Goldpath license gate allows it as a REVIEWED exception: teams that
choose Oracle already run a commercially licensed Oracle Database, and teams that don't must not
carry the driver in their graph. Core `Goldpath.Locking` stays fully open source.

## Getting started

```csharp
builder.AddGoldpathOracleLocking();    // instead of AddGoldpathLocking()
```

Configuration binds from the same `Goldpath:DistributedLocking` section; `ConnectionName`
defaults to `database` (the lock lives in the app database — zero new infrastructure).
Everything else — `GoldpathLockNames`, metrics, usage shape — is identical to the core package.

**One grant is required.** The runtime user needs `EXECUTE` on `SYS.DBMS_LOCK`, which Oracle does
not grant to `PUBLIC`. A DBA runs it once per schema:

```sql
GRANT EXECUTE ON SYS.DBMS_LOCK TO <runtime_user>;
```

Without it the first acquire fails with `PLS-00201: identifier 'SYS.DBMS_LOCK' must be declared`.
See [ops/locking-oracle.md](ops/locking-oracle.md).

**Cancellation waits out the acquire timeout.** `DBMS_LOCK` does not interrupt a server-side wait,
so a cancelled acquire on a held lock returns only when its timeout ends. Keep acquire timeouts shorter
than the host's shutdown timeout, or loop on short `TryAcquireLockAsync` slices while checking the token.
See [ops/locking-oracle.md § Cancellation and shutdown](ops/locking-oracle.md#cancellation-and-shutdown).

## Configuration

See `Goldpath.Locking`. Setting `Provider = Oracle` on `AddGoldpathLocking()` is refused at
registration and points here.

## Providers

This package IS the provider. Redis/Postgres live in `Goldpath.Locking`, SQL Server in
`Goldpath.Locking.SqlServer`.
