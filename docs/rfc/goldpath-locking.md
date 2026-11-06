# Module RFC: Goldpath.Locking

> Status: v1.0 accepted — D1–D4 approved by Ömer (2026-07-05): compose Medallion, no wrapper
> interface (a metrics DECORATOR over the same interface is composition, not wrapping) ·
> three providers · tenant-scoped names with explicit Global() · fencing honestly out of
> scope. Ring B (Phase 2, item 14 — the LAST Ring B module) · Effort S
> Dependencies: Goldpath.Abstractions (tenant context for name scoping),
> DistributedLock (Medallion; MIT) — composed, ADR-0003

## 1. Scope / Non-Goals

**Scope — one lock primitive, provider-neutral, tenant-aware names:**

- **Compose `DistributedLock` (Medallion)** — the de-facto .NET distributed-locking library
  (MIT): battle-tested Redis (RedLock), Postgres (advisory locks) and SQL Server
  (sp_getapplock) providers with a single abstraction, `IDistributedLockProvider`.
  Application code injects **Medallion's own interface** — no Goldpath wrapper (the HybridCache
  precedent, D1).
- **Manifest-driven provider selection:** `redis | postgres | sqlserver` — matches the
  template's existing db/broker choices; postgres/sqlserver mean ZERO new infrastructure
  (the lock lives in the database you already run; redis reuses the caching L2 connection).
- **Name convention:** `goldpath:{tenant}:lock:{name}` via `GoldpathLockNames` (the GoldpathCacheKeys
  pattern) — two tenants' "nightly-report" locks never collide; infrastructure-wide locks
  use the explicit `GoldpathLockNames.Global("name")`.
- **Usage shape** (Medallion's own API, documented not wrapped):
  acquire-or-wait, try-acquire-with-timeout, async disposal — all from the library.

**Non-Goals (v1, written not silent):** leader election / long-lived leases (Ring C —
arrives with the Jobs module that needs it; Medallion has primitives when we do),
fencing tokens (needs store support; documented as a known limit of lock-based mutual
exclusion — runbook covers the "lock holder paused" hazard), in-process-only locking
(`SemaphoreSlim` is not our business), ZooKeeper/etcd providers (no current demand).

## 2. Seam Map

| Seam | Touch |
|---|---|
| HTTP middleware | none |
| Mediant pipeline | none in v1 — a `[Locked("name")]` behavior is a candidate WITH the Jobs module (recorded) |
| EF interceptor | none (Postgres/SqlServer providers use their own connections) |
| MassTransit filter | none |

## 3. Manifest Surface
```yaml
distributedLocking:
  provider: redis           # redis | postgres | sqlserver
  connectionName: "redis"   # connection-string name (redis default: the caching L2; db providers: the app db)
```

## 4. API Surface
```csharp
builder.AddGoldpathLocking();                       // provider per manifest → IDistributedLockProvider

public sealed class SettlementJob(IDistributedLockProvider locks, GoldpathLockNames names)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var handle = await locks.TryAcquireLockAsync(
            names.For("settlement"), TimeSpan.FromSeconds(5), ct);
        if (handle is null) return;            // another instance holds it — that's the point
        // ... exactly one instance runs this ...
    }
}

GoldpathLockNames.Global("schema-migration");       // deliberately tenant-free
```

## 5. Analyzer Rules (SHIPPED — analyzer batch 3, 2026-07-06)
| ID | Rule | Severity |
|---|---|---|
| GP1101 | Raw string lock name not built through `GoldpathLockNames` (tenant-collision risk) | warn |
| GP1102 | Lock handle not disposed (acquire without `await using`/`using`) | warn |

## 6. Ops Package ("no runbook = no module")
- **Metrics:** `goldpath_lock_acquire_total` (by outcome: acquired/timeout), `goldpath_lock_wait_seconds`
- **Runbook:** "job didn't run anywhere" triage (lock leaked vs held); provider-specific
  inspection (Redis: the key; Postgres: `pg_locks`; SqlServer: `sys.dm_tran_locks`);
  the paused-holder hazard (locks are mutual exclusion, NOT correctness fences — design
  handlers idempotent, which is why the Idempotency module exists)
- **Dashboard:** acquire timeout rate (contention signal)

## 7. Test Plan (foundation §8 — real containers, no proxies)
- Redis (Testcontainers): two competing workers → exactly one acquires; second acquires
  after release; try-acquire timeout returns null (no throw); dispose releases
- Postgres (Testcontainers): the same contract test against advisory locks — proves the
  provider swap is behavior-neutral
- Name convention: tenant scoping, Global() escape, empty-name loud failure
- License gate stays GREEN (DistributedLock is MIT)

## 8. DoD
- [x] Decisions locked (D1–D4) · package + tests green (8: name convention with fail-closed
      no-tenant throw + Global escape + cross-tenant distinctness, and the SAME contract
      test on real Redis AND real Postgres via Testcontainers — one winner under
      contention, null-on-timeout, release hands over, tenant-scoped names don't contend)
      · PublicAPI locked
- [x] README (4 sections) + ops runbook (paused-holder hazard included) + CHANGELOG
      · GP1101/1102 to backlog · manifest schema gains provider/connectionName
- [x] GM wiring tracked · **Ring B module set COMPLETE (items 8–14)**
- **D2 amendment (found by the license gate, recorded not silent):**
  `Microsoft.Data.SqlClient.SNI.runtime` (transitive of DistributedLock.SqlServer) is
  proprietary-but-free (Microsoft Software License Terms — closed source, free of charge).
  A hard reference would have pushed those bits into EVERY consumer's graph, including
  redis/postgres users. Resolution: the SqlServer provider ships as the OPTIONAL
  `Goldpath.Locking.SqlServer` package; the core stays fully OSS; the license gate carries a
  reviewed, package-scoped EXCEPTION with the justification inline (choosing SQL Server
  already implies commercially-licensed Microsoft infrastructure — the same chain the
  template's `--db sqlserver` choice pulls via EF). Core `AddGoldpathLocking` fails LOUDLY at
  registration if the manifest says sqlserver without the optional package.
- **Oracle (G7, O-4, #407, 2026-11-06), the same shape:** `DistributedLock.Oracle` reaches ODP.NET (Oracle Free
  Use Terms and Conditions), so the Oracle provider ships as the OPTIONAL `Goldpath.Locking.Oracle` package
  (`AddGoldpathOracleLocking()`, `DBMS_LOCK`), under the FUTC exception the Oracle RFC (§2.1) already scopes to
  it. `AddGoldpathLocking` refuses `Provider = Oracle` at registration and names the package, and refuses any value
  that names no provider rather than building PostgreSQL for it.

## 9. Decision Points (Ömer)

- **D1 — Compose Medallion `DistributedLock`, expose ITS `IDistributedLockProvider`.**
  Writing RedLock/advisory-lock plumbing ourselves is exactly what ADR-0003 forbids; the
  library is MIT, mature, and its abstraction is already provider-neutral. No Goldpath wrapper
  interface (HybridCache precedent). **Recommendation: compose.**
- **D2 — Providers v1 = redis + postgres + sqlserver** (all three are thin package
  references and match the template's existing choices; db providers need zero new infra).
  ZooKeeper/etcd deferred — no demand. **Recommendation: all three.**
- **D3 — Tenant-scoped names by default** (`goldpath:{tenant}:lock:{name}`), `Global()` as the
  explicit, greppable escape for infrastructure locks. Same reasoning as cache keys:
  cross-tenant lock collision is a correctness bug. **Recommendation: yes.**
- **D4 — Fencing tokens acknowledged as out of scope, in writing.** Lock-based mutual
  exclusion cannot be made airtight against paused holders without store cooperation;
  the honest posture is "locks reduce duplicate work; idempotency guarantees correctness"
  (the modules compose). Runbook documents the hazard. **Recommendation: accept + document.**
