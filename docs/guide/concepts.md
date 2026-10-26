# Concepts — the six ideas everything hangs on

## 1. The manifest is the single source of truth

`.goldpath/manifest.yaml` says what your app IS: providers (db/cache/broker/auth) and
features. A disabled feature does not exist in the codebase at all — composition is
compile-time, not runtime flags. The deterministic engine (`specdrift`) enforces both
directions: `spec_validate` rejects impossible manifests (e.g. campaign without a
broker) BEFORE code exists, and `spec_drift` catches a repository that stopped matching
its manifest. `goldpath check` runs both plus the build. Constitution: ADR-0001/0004/0005.

## 2. Compose, never rebuild

Goldpath adds enterprise opinion ON TOP of Microsoft and Mediant — never wrappers.
EF is EF (plus conventions, keyset pagination, a save-contributor seam); MassTransit is
MassTransit (plus the outbox/inbox wiring and header propagation); Quartz is Quartz
(plus the run model below). If you know the underlying library, you already know 90%.
The analyzers (GP####) are the opinions made executable — treat a diagnostic as a design
instruction with a teaching message, not noise. Constitution: ADR-0003.

## 3. One run model carries all heavy work

Jobs, archival sweeps, bulk executions and campaign fan-outs all ride ONE engine:
chunked plans, a persisted checkpoint after every chunk, per-item repair queues, live
progress with predicted-finish (alerts fire BEFORE a deadline breaks), and kill-9
recovery — another node resumes from the checkpoint, proven by tests that really kill
processes. Operate everything through one console surface (`/goldpath/admin/jobs`):
trigger, pause, reschedule, rerun, replay-items. Depth: `packages/Goldpath.Jobs/README`
+ its ops pack; the proof story: [no double payment under kill -9](../stories/kill9-no-double-payment.md).

## 4. Migrations are the app's, and production applies bundles

Migrations live in YOUR app (the CLI generates them against the just-composed model);
Development auto-migrates; production applies an EF **bundle** built by CI — the app
never carries DDL rights. One table set has ONE owner (GP1801 enforces it); added
workers own only their private tables and MAP shared ones read-only
(`ExcludeFromMigrations`). Verbs: `goldpath db init|add|status|bundle`. Depth:
[the migrations runbook](../ops/migrations-runbook.md).

## 5. The admin surface is a frozen, fail-closed contract

Every `Map*Admin` is fail-closed out of the box (opting out is visible and warned), and
the HTTP method decides who passes:
- **Reads:** every `GET` demands the read policy, which `goldpath-ops-read` (a viewer) and
  `goldpath-ops` (an operator) both satisfy.
- **Verbs:** every other method also demands `goldpath-ops`. A viewer's verb is refused with
  `403` and a sentence naming the missing role.
- **What a viewer can see:** the read role reads everything, including archived documents in
  full ([admin contract R4.1](../rfc/goldpath-admin-contract.md)). Routes, envelopes and paging are FROZEN — the UI and your scripts
are written once against [the contract](../rfc/goldpath-admin-contract.md); changing it
is a breaking change under [the versioning contract](../rfc/goldpath-versioning.md).
Every mutating verb writes an audit row with the actor, server-side.

## 6. Claims are proofs

Nothing here asks for trust: exactly-once is a broker round-trip test, recovery is a
real `kill -9`, performance numbers come from a pinned CI profile you can rent
(4 vCPU/16 GB — each module's `ops/*-benchmarks.md`), correlation is a span chain you
can walk in Tempo ([guide](../ops/trace-correlation.md)), and the golden-manifest
matrix regenerates every app shape nightly against real containers — and the covering
subset on every pull request that could change a generated app, before it merges. When a claim and a proof
disagree, the proof wins and the claim gets fixed — in public
([the CorPay gap ledger](../../samples/GAP-LEDGER.md)).
