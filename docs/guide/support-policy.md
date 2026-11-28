# Support policy — what Goldpath supports, and where each fact is decided

This page is the one place an adopter reads to know what is supported. It makes no decision
of its own: every line links to the document that owns the fact, and a number that can move
(a window, a target, a version) is quoted from its owner or linked, never written here a
second time. When this page and its source disagree, the source is right and this page is a
defect.

## Which versions receive fixes

The window is the versioning contract's
[D3 — Support window](../rfc/goldpath-versioning.md#d3--support-window-honest-for-a-solo-maintained-oss-asset).
Quoted from it:

> - **Pre-1.0:** the latest release only. Older 0.x releases get no backports; the
>   upgrade guides are the path forward.
> - **Post-1.0:** the latest minor of the current major gets fixes; the previous major
>   gets **security fixes only, for 6 months** after the new major ships. Nothing wider
>   is promised, so the promise can always be kept.

- **Which release is the latest.** The top released entry of [CHANGELOG.md](../../CHANGELOG.md),
  which is the same version as on nuget.org. [SECURITY.md § Supported versions](../../SECURITY.md#supported-versions)
  says the same and deliberately names no number.
- **What may break between trains.** The contract's
  [D2](../rfc/goldpath-versioning.md#d2--semver-with-the-pre-10-rules-spelled-out) lists what
  counts as breaking, and every break ships with a step-by-step guide in
  [docs/upgrades/](../upgrades/).
- **What 1.0 must prove before it ships.** The gates are decided in
  [goldpath-ga-readiness](../rfc/goldpath-ga-readiness.md), and their status today is in
  [ga-gate-status](../strategy/ga-gate-status.md).

## Security response targets

The targets (acknowledgement, first assessment, fix or written mitigation, coordinated
disclosure) are the table in
[SECURITY.md § Reporting a vulnerability](../../SECURITY.md#reporting-a-vulnerability), and
that table is the only copy. Report privately through the route that section gives, never in
a public issue.

## Engines and providers today

What a generated application can run on **today**. Each row names its evidence; a row whose
evidence changes changes here in the same pull request.

| Choice | Status today | Evidence |
|---|---|---|
| Messaging engine: **MassTransit 8.x** | **The default and the only selectable engine.** Locked as the default for the whole 1.x line | [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) (the engine table and its rule 3); [messaging-engines RFC §3](../rfc/goldpath-messaging-engines.md#3-manifest-surface) (`masstransit` only until another adapter passes the bar) |
| Messaging engine: **Wolverine** | **Proven by the conformance suite, not selectable.** It is not a manifest value until its switch proofs are linked in open thread T18; it is opt-in and never the default in 1.x | [ga-gate-status G5](../strategy/ga-gate-status.md#72-dependencies-and-runtime) (the suite's parts and what is still open); [open-threads T18](../strategy/open-threads.md) (the proofs that make it a value); [messaging-engines RFC §3](../rfc/goldpath-messaging-engines.md#3-manifest-surface) |
| Database: **PostgreSQL** | **Supported.** The default provider | [golden-manifests-v1 §2](../strategy/golden-manifests-v1.md#2-coverage-check-every-enum-value-1-gm) (the `db` row's "actually proven" column) |
| Database: **SQL Server** | **Supported** | [golden-manifests-v1 §2](../strategy/golden-manifests-v1.md#2-coverage-check-every-enum-value-1-gm) (the `db` row); [§6](../strategy/golden-manifests-v1.md#6-the-matrix-today-docs-freshness-gate-keeps-this-table-honest) (the solution and worker `--db` rows: GmFourClean and GmWorkerJobsAuthed generate on it in CI) |
| Database: **Oracle** | **SCHEMA-ONLY.** The manifest accepts it so a manifest can declare intent; declaring it does not make a generated application run on it. The modules' own suites run on a real Oracle in CI on the way to that | [ga-gate-status G7](../strategy/ga-gate-status.md#72-dependencies-and-runtime) (what is proven on Oracle so far); [the manifest schema](../../schemas/manifest/v1/goldpath-manifest.schema.json) (the `oracle` value's own comment); [golden-manifests-v1 §2](../strategy/golden-manifests-v1.md#2-coverage-check-every-enum-value-1-gm) (the `db` row) |

The other providers a manifest can name (cache, broker, auth) and what each one costs to run
are in [Modules × infrastructure](modules-and-infrastructure.md) and in the same
[golden-manifests table](../strategy/golden-manifests-v1.md#2-coverage-check-every-enum-value-1-gm); a value marked SCHEMA-ONLY there is not supported either.

## `[Experimental]` surfaces

**What `[Experimental]` promises.** A surface marked with the BCL's
`System.Diagnostics.CodeAnalysis.ExperimentalAttribute` is outside the SemVer promise — a
1.x minor may change it — and it says so at compile time: using it raises its diagnostic,
which a consumer suppresses to opt in knowingly. The decision is the
[GA readiness RFC § 4](../rfc/goldpath-ga-readiness.md#4-api-surface--what-ga-promises-an-enterprise-adopter)
("API stability") and its owner decision Q4 in
[§ 8.3](../rfc/goldpath-ga-readiness.md#83-owner-decisions-2026-10-21).

**What is marked today: nothing.** No shipped surface carries `[Experimental]` yet. Q4 decides
that `Goldpath.Approvals` and `Goldpath.FileExchange` ship in 1.0 with their surface marked,
lifted in a 1.x minor once their adopter proofs run; the marking, with a documented
diagnostic id per package, is [#400](https://github.com/qorpe/goldpath/issues/400). Until it
lands, those two packages are on the preview window above like every other package, and this
section lists them when they are marked.

## Asking for a backport

A backport to an older train is not promised by the window above, but it is a decision that
can be made — not one that can be guessed. If you cannot move trains quickly, say so in an
issue on this repository, as [SECURITY.md § Supported versions](../../SECURITY.md#supported-versions)
asks. A security problem is never that issue: report it privately as
[§ Security response targets](#security-response-targets) says.
