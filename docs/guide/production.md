# Production — from a generated app to a running environment

[Getting started](getting-started.md) ends with an app running under its AppHost on your
machine. This page is the next step: what a generated app needs before it serves real
traffic. It documents what the app ships **today** and builds nothing. Where a piece is not
built, the page says so and names the open thread that tracks it.

Every command below was run once against an app generated on the published train
(`0.1.0-preview.9`, PostgreSQL + RabbitMQ + OpenID shape). The transcript is on the pull
request that added this page.

> **Before you start, on `0.1.0-preview.9`:** an app generated from that train pins Aspire
> at 13.4.6, which carries GHSA-f7vh-h5g3-7jrh (high). Under warnings-as-errors the AppHost
> and the smoke tests fail restore with NU1903. Lift every `13.4.6` in
> `Directory.Packages.props` and the `Aspire.AppHost.Sdk` line of the AppHost project to
> **13.6.5**, as the [CHANGELOG](../../CHANGELOG.md) entry for the fix says. The next
> train's templates pin 13.6.5 already.

## 1. Deploy shape

Goldpath has three tiers ([foundation §10](../strategy/foundation.md#10-repo-distribution-governance)):

| Tier | What runs it | Status |
|---|---|---|
| Local | the Aspire AppHost (`dotnet run --project src/<App>.AppHost`) | built |
| Alternative | `goldpath export compose` → `docker-compose.yml` + a `Dockerfile` per project | built — **development tier only** |
| Environments | Kubernetes/OpenShift manifests built by CI | **not built** — open thread T73 |

```bash
goldpath export compose      # writes docker-compose.yml from the AppHost; a Dockerfile per project, once
docker compose up -d
```

The compose file is an **export** of the AppHost and is never edited by hand: change the
AppHost and run the verb again ([CLI reference](cli-reference.md#export)). It is the
development tier on purpose, and the generated file says so in its header:

- fixed credentials (`POSTGRES_PASSWORD: goldpath-dev`, `guest:guest` for RabbitMQ);
- one node per service;
- `ASPNETCORE_ENVIRONMENT: Development`, so the API migrates its own database on boot.

**Do not promote the compose file to an environment.** A real environment runs the app with
`ASPNETCORE_ENVIRONMENT` set to anything other than `Development`, connection strings from
your secret store (§3), and the migration bundle applied before the app starts (§2). The
`Dockerfile` the verb lays (SDK 10 build stage, `aspnet:10.0` runtime stage, port 8080) is
yours to keep and is a reasonable image for any tier.

What the repository does **not** ship: a Helm chart, Kubernetes manifests, an OpenShift
template, or an Aspire publish profile. The one Kubernetes fragment is the init-container
sketch in the [migrations runbook](../ops/migrations-runbook.md#1-first-deploy-day-one), which
is "the pattern, not a prescription". Until T73 lands, your platform team writes the
Deployment. The facts it needs are §2's ordering, §4's probes and §6's scale-out rules.

## 2. Migration bundles

Outside Development the app process **never** migrates its database. The template's
`MigrateAsync` call sits behind `app.Environment.IsDevelopment()` (analyzer GP0302 guards the
`EnsureCreated` shortcut). Real environments apply a **bundle**: one executable per
migration-owning project, built by CI or locally.

```bash
goldpath db init         # once, on a fresh app: the Initial migration (generation leaves it migration-ready)
goldpath db status       # green when every owner's migrations match its model
goldpath db bundle       # writes artifacts/migrations/<Owner>-migrations
```

The generated CI workflow (`.github/workflows/ci.yml`) builds the same bundles and uploads
them as the `migration-bundles` artifact.

Apply it **once per release, before the new app version starts**, with a connection that is
allowed to run DDL. The app's own connection should not hold DDL rights
([migrations RFC D4](../rfc/goldpath-migrations.md)).

```bash
./artifacts/migrations/Acme.Orders.Api-migrations --connection "<DDL-capable connection string>"
```

The bundle walks `__EFMigrationsHistory`, so running it again on the same release is a
no-op. The second apply on the walk printed `No migrations were applied. The database is
already up to date.` and exited 0. This makes a retried deploy step safe. It does not make
two **concurrent** applies safe: run the bundle from one place, an init job or a pre-deploy
hook, never from every replica.

The bundle is **framework-dependent**. It needs the ASP.NET Core 10 runtime on the image that
runs it: `mcr.microsoft.com/dotnet/aspnet:10.0`, the same base as the app's runtime stage.
On a plain `ubuntu:24.04` image it exits 131 ("You must install .NET"), and on
`dotnet/runtime:10.0` it exits 150 (no `Microsoft.AspNetCore.App`). The RFC's D4 says CI
builds `--self-contained`. Neither the CLI nor the generated workflow passes that flag
today: open thread T74.

Bundles are forward-only. A failed release is repaired by restoring the backup taken before
the bundle ran and rolling forward (§5). Enabling a feature on a live system, upgrading
packages, multi-head solutions and triage are in the
[migrations runbook](../ops/migrations-runbook.md).

## 3. The auth floor and secrets

**The floor is fail-closed.** With `Goldpath.Auth` composed:

- Every endpoint requires an authenticated user unless it opts out. The fallback policy is
  `RequireAuthenticatedUser()`.
- The admin surfaces under `/goldpath/admin/*` demand the ops policies: `goldpath-ops-read`
  for reads and `goldpath-ops` for everything else
  ([admin contract](../rfc/goldpath-admin-contract.md), [SECURITY.md](../../SECURITY.md#hardening-notes-for-adopters)).
  With no auth configured they refuse rather than serve. The only way to map one unsecured
  is an explicit `exposeUnsecured: true`, which logs a warning on every start. The template
  writes it only in the `--auth none` shape.
- The two health endpoints are the deliberate exceptions (`AllowAnonymous`), because probes
  must work under the floor (§4).

Configuration lives under `Goldpath:Auth` (environment variables `Goldpath__Auth__…`):

| Key | Meaning |
|---|---|
| `Strategy` | `OpenId` (default), `ApiKey` or `None` |
| `Authority`, `Audience` | the OpenID issuer and the audience this app accepts |
| `AllowAnyAudience` | the explicit opt-out from the audience check; off by default |
| `OpsRole`, `OpsReadRole`, `OpsAllTenantsRole` | the role names behind the three ops policies (default `goldpath-ops`, …) |
| `ApiKeyHeader`, `ApiKeys` | the ApiKey strategy; the keys belong in the secret store |

An `Authority` without an `Audience` **refuses to start**. On the walk, the published API
under `ASPNETCORE_ENVIRONMENT=Production` with only `Goldpath__Auth__Authority` set aborted
with `Goldpath:Auth:Authority is set but Goldpath:Auth:Audience is not.` With both set, it
started: `/health/live` and `/health/ready` answered 200 and `/orders` answered 401.

**Secrets never live in a file in the repository.** The generated app reads its connection
strings by name (`ConnectionStrings:ordersdb`, `ConnectionStrings:messaging`). Under the
AppHost, Aspire injects them; in an environment you supply them as
`ConnectionStrings__ordersdb` and so on, from your platform's secret store (a Kubernetes
secret, a vault agent, Key Vault; see the [threat model](../security/threat-model.md), A10).
The template ships no `appsettings*.json` and no user-secrets. Analyzer **GP1202** fails the
build on an auth secret written as a string literal in C# (`ApiKeys`, the data-protection
HMAC key). It does not scan configuration files, so keep those free of secrets by review.

Module secrets follow the same rule. Two examples: the notification mail relay
(`Goldpath:Notification:Email:User`/`Password`) and the data-protection key
(`Goldpath:DataProtection:HmacKey`, [ops page](../../packages/Goldpath.DataProtection/ops/data-protection.md)).

## 4. Observability

`builder.AddGoldpathServiceDefaults()` is already in the generated `Program.cs`. It emits:

- **Traces**: ASP.NET Core, HttpClient, every `Goldpath.*` source and MassTransit. The
  sampler is always-on in Development. Elsewhere it is set by the profile (Minimal 1%,
  Standard 10% by default, Full 100%) or by an explicit ratio, under
  `Goldpath:ServiceDefaults`.
- **Metrics**: runtime, ASP.NET Core, HttpClient, every `Goldpath.*` meter, MassTransit,
  EF Core and Npgsql.
- **Logs**: OpenTelemetry logging with scopes, so every line carries the correlation id
  ([trace-correlation guide](../ops/trace-correlation.md)).

**Nothing leaves the process until you point it somewhere.** Set
`OTEL_EXPORTER_OTLP_ENDPOINT` and every signal goes over OTLP to your collector. Without the
variable, an environment exports nothing; only Development has a console exporter. The
reference collector configuration is
[central-logging.md](../../packages/Goldpath.ServiceDefaults/ops/central-logging.md).

**Probes** are mapped in every environment and need no credentials:

| Path | Checks | Use it as |
|---|---|---|
| `/health/live` | the process itself | liveness: failing means restart |
| `/health/ready` | every registered check | readiness: failing means take it out of the load balancer |

The body is plain text and never lists check details. Triage, the 429 concurrency guard
(`Goldpath:ServiceDefaults:RateLimiting`) and telemetry gaps are in the
[ServiceDefaults runbook](../../packages/Goldpath.ServiceDefaults/ops/runbook.md).

**Dashboards and alerts.** The app ships `ops/grafana-app-dashboard.json`, and every
composed module ships its own board and runbook under `packages/Goldpath.<Module>/ops/`. Alert **rule
files** exist for Campaign only (`packages/Goldpath.Campaign/ops/alerts.yaml`). For every
other package, including the four baseline alerts in
[dashboard-and-alerts.md](../../packages/Goldpath.ServiceDefaults/ops/dashboard-and-alerts.md),
the alerts are still prose: open thread T60.

## 5. Backup, restore and DR — what state lives where

Goldpath keeps its durable state in **your** stores. It has no store of its own:

| State | Where it lives | Must a restore include it? |
|---|---|---|
| Your entities, the migration history and every module's tables (Approvals, Archival, AuditTrail, Bulk, Campaign, FileExchange, Jobs' run tables, Notification) | the app's database | **yes**: this is the system of record |
| The outbox and inbox (`OutboxMessage`, `OutboxState`, `InboxState`) | the app's database, in the same transaction as the change | **yes**, in the same backup. A message not yet delivered exists only here |
| The Quartz store (`qrtz_*` tables) | the app's database, as a model contribution of `Goldpath.Jobs` | **yes**: schedules, triggers and the cluster's locks |
| Queues in flight, and the `<queue>_error` queues | the broker | the outbox re-delivers what was not yet sent, and the inbox drops duplicates by message id. An **error queue is the only copy of a failed message**: export it before you purge or rebuild a broker ([messaging runbook](../../packages/Goldpath.Messaging/ops/messaging.md)) |
| The L2 cache, idempotency keys | Redis or whatever `IDistributedCache` you compose | no. A cold cache is refilled. A lost idempotency key reopens its window (`TtlHours`, 24 by default) ([idempotency](../../packages/Goldpath.Idempotency/ops/idempotency.md)) |

Since everything that matters is in one database, **one consistent backup of that database
is the restore unit.** Take it before every bundle runs; the migrations runbook's rollback
stance depends on it ([§4 there](../ops/migrations-runbook.md#4-rollback-stance-said-honestly)).
After a restore:

- Never delete Jobs run rows: the run tables are the truth and the counters are a cache
  ([Jobs runbook](../../packages/Goldpath.Jobs/ops/runbooks.md)).
- Never truncate `qrtz_locks` on a running cluster.
- Archived and legal-hold data have their own retention rules
  ([Archival runbook](../../packages/Goldpath.Archival/ops/runbooks.md)). The archive is
  queryable history; it is not a backup.

The in-memory stores of Approvals and FileExchange lose everything on restart and are for
tests only. The template composes the database-backed store whenever it composes the module
([approvals](../../packages/Goldpath.Approvals/ops/approvals.md),
[file exchange](../../packages/Goldpath.FileExchange/ops/fileexchange.md)).

**Not written yet:** a recovery-point and recovery-time objective, and a restore drill that
proves a backup taken mid-traffic comes back with no lost or duplicated message. Open thread
T75.

## 6. Worker scale-out — what is safe on N instances

| Module | On several instances | Source |
|---|---|---|
| API heads (stateless endpoints) | safe. The concurrency guard counts per process | [ServiceDefaults runbook](../../packages/Goldpath.ServiceDefaults/ops/runbook.md) |
| Jobs | safe: a clustered Quartz store, exactly-once firing, a dead node's runs re-fired elsewhere. **First-ever deploy: start ONE node** against the empty `qrtz_` schema, then scale | [Jobs runbook §8](../../packages/Goldpath.Jobs/ops/runbooks.md#8-first-deploy-of-a-brand-new-store-cold-boot-contention) |
| Bulk, Archival, Notification, Approvals' escalation | safe: they are Jobs runs and inherit its clustering. Bulk and Notification claim rows before any side effect | [Bulk](../../packages/Goldpath.Bulk/ops/runbooks.md), [Archival](../../packages/Goldpath.Archival/ops/runbooks.md), [Notification](../../packages/Goldpath.Notification/ops/runbooks.md) |
| Campaign | **leader-elected.** One pacer leads at a time; a one-minute cron guarantees a leader exists, and hand-over is sub-second. Items go to competing broker consumers, which scale freely | [Campaign runbook](../../packages/Goldpath.Campaign/ops/runbooks.md) |
| Messaging consumers | safe: competing consumers, inbox de-duplication. A growing outbox backlog is solved by scaling **the host that owns the outbox**, not the consumers | [messaging runbook](../../packages/Goldpath.Messaging/ops/messaging.md) |
| Locking | a lock is for **efficiency**: a paused holder can lose it while a second instance acquires it legitimately. Keep the operation itself replay-safe | [locking](../../packages/Goldpath.Locking/ops/locking.md) |
| Idempotency | safe; replicas can race on a cold key, and the store's last write wins | [idempotency](../../packages/Goldpath.Idempotency/README.md) |
| Caching | safe; L1 differs between instances within `LocalCacheExpiration` | [caching](../../packages/Goldpath.Caching/ops/caching.md) |
| Migrations | **never** on N instances in an environment: the bundle runs once (§2). Development's on-boot migration is serialized behind a database lock | [data](../../packages/Goldpath.Data/ops/data.md) |

Size each database pool for *replicas × concurrency*
([data](../../packages/Goldpath.Data/ops/data.md)).

## 7. Air-gapped environments

Everything above works with no route to nuget.org or Docker Hub. The connected side fills a
feed and a registry, and the air-gapped side installs, bundles, migrates and boots from them
alone. The nightly GmOffline shape proves it: [air-gapped install](../ops/air-gapped-install.md).

## Where next

- [Support policy and versioning](../rfc/goldpath-versioning.md): what a version promises.
- [Upgrade guides](../upgrades/): one per train.
- [Release checklist](../ops/release-checklist.md): how a train is cut, if you mirror it.
