# RFC Index

Every module/significant component is born through an RFC before implementation begins.
Template = the eight fixed sections of [goldpath-idempotency.md](goldpath-idempotency.md):

1. Scope / Non-Goals · 2. Seam Map · 3. Manifest Surface · 4. API Surface ·
5. Analyzer Rules · 6. Ops Package · 7. Test Plan (including golden manifest impact) · 8. DoD

This table is COMPLETE by rule: every file in this folder has a row, and the row tells the
truth (the freshness gate keeps links alive; the review agent's R1 class polices claims).
All "implemented" modules are published on nuget.org at `0.1.0-preview.6` (the current train).

| RFC | Module / Topic | Status |
|---|---|---|
| [goldpath-abstractions](goldpath-abstractions.md) | Goldpath.Abstractions (foundational) | implemented |
| [goldpath-servicedefaults](goldpath-servicedefaults.md) | Goldpath.ServiceDefaults (Ring A) | implemented |
| [goldpath-apidefaults](goldpath-apidefaults.md) | Goldpath.ApiDefaults (golden-path core) | implemented |
| [goldpath-data](goldpath-data.md) | Goldpath.Data (golden-path core) | implemented |
| [goldpath-messaging](goldpath-messaging.md) | Goldpath.Messaging (golden-path core) | implemented |
| [goldpath-analyzers](goldpath-analyzers.md) | Goldpath.Analyzers (executable standards) | implemented |
| [goldpath-idempotency](goldpath-idempotency.md) | Goldpath.Idempotency (Ring B) | implemented (ops runbook + GP1001/1002/1004 shipped 2026-08-03) |
| [goldpath-audittrail](goldpath-audittrail.md) | Goldpath.AuditTrail (Ring B) | implemented (ops runbook shipped 2026-08-03) |
| [goldpath-softdelete](goldpath-softdelete.md) | Goldpath.SoftDelete (Ring B) | implemented (ops runbook shipped 2026-08-03) |
| [goldpath-auth](goldpath-auth.md) | Goldpath.Auth (Ring B) | implemented |
| [goldpath-multitenancy](goldpath-multitenancy.md) | Goldpath.MultiTenancy (Ring B) | implemented |
| [goldpath-caching](goldpath-caching.md) | Goldpath.Caching (Ring B) | implemented |
| [goldpath-locking](goldpath-locking.md) | Goldpath.Locking (Ring B) | implemented |
| [goldpath-dataprotection](goldpath-dataprotection.md) | Goldpath.DataProtection (Ring B) | implemented |
| [goldpath-jobs](goldpath-jobs.md) | Goldpath.Jobs (the run engine) | implemented |
| [goldpath-archival](goldpath-archival.md) | Goldpath.Archival | implemented |
| [goldpath-bulk](goldpath-bulk.md) | Goldpath.Bulk (ladder L3) | implemented |
| [goldpath-notification](goldpath-notification.md) | Goldpath.Notification | implemented |
| [goldpath-campaign](goldpath-campaign.md) | Goldpath.Campaign (ladder L4) | implemented |
| [goldpath-template](goldpath-template.md) | `dotnet new` solution pack | implemented |
| [goldpath-template-completion](goldpath-template-completion.md) | template completion set | implemented |
| [goldpath-migrations](goldpath-migrations.md) | migrations discipline + `goldpath db` + bundle | implemented (H1) |
| [goldpath-admin-contract](goldpath-admin-contract.md) | the admin API contract | **FROZEN** + revisions R1 (tenant scoping, 2026-07-24), R2 (fleet status, widened trigger DTO, run filters), R3 (repeatable OR filters) — all ACCEPTED & implemented |
| [goldpath-versioning](goldpath-versioning.md) | SemVer & support promise (H7) | accepted (binding) |
| [goldpath-event-contracts](goldpath-event-contracts.md) | event contracts idiom (per-app `<Name>.Contracts`) | accepted (2026-07-14) |
| [goldpath-delivery-cycle-v1](goldpath-delivery-cycle-v1.md) | one delivery cycle for defects and features, for adopters AND library maintainers | **PROPOSED** (2026-09-08) — widens the skills RFC's D1; the measured gap is that five library repositories carry no cycle at all, and the accelerator does not run the hooks it ships |
| [goldpath-skills-v1](goldpath-skills-v1.md) | the AI skill layer v1 | implemented — ships inside the template; field status: `../strategy/ai-sdlc-status.md` §2 |
| [goldpath-authoring](goldpath-authoring.md) | Goldpath Authoring: a business sentence becomes an approved spec (specanchor rule/term/spec + a decision record + an OpenAPI fragment) before it becomes code; the `goldpath-authoring` skill, the adversarial `spec-review` agent, and the deterministic floor under both | **PROPOSED** (2026-10-20) — Slice 0 (the no-LLM floor: pinned specanchor schemas + freshness gate, the `decisions` schema, `goldpath spec ready`, `add feature --plan`, the `docs/domain/` skeleton) approved to start the same day and **landed**; Slices 1–3 wait for acceptance. Owner decisions recorded in its §9; tracked as `../strategy/open-threads.md` T34 |
| [goldpath-console](goldpath-console.md) | Goldpath.Console (the UI phase) | implemented — U1–U9 met (family standards: `../strategy/ui-standard-v1.md` §7–§9); admin contract at Revision R3 |
| [goldpath-platform-sdk](goldpath-platform-sdk.md) | the Module SDK (platform RFC, ADR-0012 companion) | **ACCEPTED** (2026-08-05, D1–D7 + the §2b risk/antidote condition) — D1/D2/D5/D6 implemented in step 5 (`Goldpath.Sdk` shipped on the 0.1.0-preview.7 train — this row said "NOT yet in a published train" until the 2026-09-05 coverage audit); the two §2b DoD gaps are **CLOSED** 2026-08-10 (`train-freshness.sh` + GP2001/`product-guard.sh`, both running in CI against CorPay); the pilot product module is the remaining open item |
| [goldpath-ai](goldpath-ai.md) | Goldpath.Ai — opt-in runtime AI (gateway · tool registry · decision record · confidence gate) | **ACCEPTED** (2026-07-26, D1–D4 as recommended) — implementation opens after the UI phase |
| [goldpath-messaging-exit](goldpath-messaging-exit.md) | the messaging dependency: measured exposure, options, and the triggers that would move us | **ACCEPTED** (2026-08-10) — option **A**: stay pinned on 8.x, fallback D; a move would be a MAJOR version, not an internal swap. §5 implemented (the publish seam + GP0404); the watch lives as `open-threads.md` T18 |
| [spec-engine-v1](spec-engine-v1.md) | specdrift (separate repo) | implemented — 0.4.2 published (NuGet tool + MCP + Docker + Action) |
| [qorpe-ui](qorpe-ui.md) | @qorpe/ui — the family UI kit extraction (platform-sdk D5) | **implemented** (step 6.0, 2026-08-07): the kit lives in [qorpe/ui](https://github.com/qorpe/ui), publishes to npm via OIDC trusted publishing, and BOTH consumers run on the published package — `ui/kit` is deleted from this repo |
| [qorpe-coexist](qorpe-coexist.md) | qorpe.coexist (separate private repo) — the migration & co-existence product module (ADR-0012's own named example): CDC capture, ordered replayable stream, adapter-based versioned mapping, event-id reconciliation composing db-compare | **accepted** (owner, 2026-08-18) — v0 scope is engagement-shaped (§5.1 by-product rule); build scheduled, two-store rig first; the second-product-repo ordering is an owner decision recorded in RFC D5 |
| [goldpath-approvals](goldpath-approvals.md) | Goldpath.Approvals — human approval workflows: maker-checker, four-eyes, amount-laddered authority, delegation, escalation, worklist; definitions as versioned data | **accepted** (owner, 2026-08-18) — build started same day by owner decision; core + 20 tests + manifest/CLI/template wiring + EF store landed; console federation and the adopter proof stay open (T21) |
| [goldpath-approvals-v2](goldpath-approvals-v2.md) | The product-proven ladder deltas: withdraw, quorum rungs, distinct-eyes, mandatory rejection reason, reject→resubmit chain — additive, non-breaking | **accepted** (owner, 2026-08-26) — implemented 2026-08-27: withdraw, quorum rungs, chain-wide distinct-eyes, mandatory rejection reason, resubmit chain; 17 new facts + the package's own mutation gate |
| [goldpath-fileexchange](goldpath-fileexchange.md) | Goldpath.FileExchange — file-based integration rails as a unit: scheduled pick-up/drop, format contracts, idempotent `(file,line)` ingestion, quarantine, reprocessing, archival, outbound | **accepted** (owner, 2026-08-18) — build started same day by owner decision; core + 9 tests (planted-fault rig + EF ledger) + manifest/CLI/template wiring landed; transports, console federation and the adopter proof stay open (T22) |
| [specanchor-composition](specanchor-composition.md) | specanchor (separate repo) — the transformation package's deterministic core, composed per foundation §9/§5.1 | **ACCEPTED** (2026-08-16, #165) — the toolchain is real ([qorpe/specanchor](https://github.com/qorpe/specanchor), Apache-2.0; 74 tests, mutation 94.8%, first skill eval 7/7); the composition proof it owes is `open-threads.md` T20 (the rehearsal) |
