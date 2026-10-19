# Coverage matrix — capability × sample (LIVING document)

Rule of the house (owner directive, 2026-07-06): the CORE set — Ring B (all eight) +
auth + outbox + worker + jobs + archival — appears in EVERY sector sample with a real
business use; the matrix accepts no empty core cell. The ladder-top three (bulk,
notification, campaign) are CARD-TRIGGERED: they appear where the sector's most popular
scenario genuinely demands them. This file is updated as samples land — cells move from
planned to proven with the PR that proves them.

Legend: ✅ proven today · 🔷 defined on the card, lands with its sample · 🟡 gap/decision
for planning · — deliberately absent (the card does not trigger it).

## Core set (every sample)

| Capability | CorPay (finance) | Insurance | Telco |
|---|---|---|---|
| MultiTenancy | ✅ corporate fence (tested) | 🔷 agency book + explicit ELEVATED back-office scope | 🔷 dealer/MVNO |
| Auth (openid) | ✅ 401 floor; four-eyes distinct identity | 🔷 adjuster-only decision → 403 (role gate) | 🔷 billing-ops role on cycle verbs |
| AuditTrail | ✅ instruction states + approver | 🔷 endorsements + decisions | 🔷 tariff/subscriber changes |
| SoftDelete | 🟡 no story yet (candidate: beneficiary/template removal) | 🔷 cancelled policies/withdrawn claims | 🔷 deactivated subs (win-back reads) |
| Idempotency | ✅ header + DB-enforced unique reference | 🔷 duplicate FNOL/disbursement | 🔷 gateway callback ×N deliveries |
| DataProtection | ✅ IBANs masked | 🔷 health data (special category) + national id | 🔷 traffic/location + authorized projection |
| Caching | 🟡 card names FX/fees; slice missing | 🔷 tariff catalog + tag invalidation | 🔷 plan catalog + balance p95<100ms |
| Locking | ✅ EOD singleton (postgres) | 🔷 claim-finalize + renewal singleton | 🔷 port-in machine + competing chunks |
| Outbox+Broker | ✅ PaymentExecuted → ledger feed | 🔷 PolicyIssued/ClaimApproved | 🔷 UsageRecorded/InvoiceIssued |
| Worker (queue) | ✅ scaffolded; 🟡 real consumer story via the Contracts idiom | 🔷 partner FNOL consumer | 🔷 CDR ingest ≥500 msg/s dedup-ON |
| Jobs (schedule) | ✅ EOD: calendar+singleton+deadline+tenant chunks | 🔷 renewal→payment CHAINING + input-version pin | 🔷 500k subs <4h + pause/drain + watermark |
| Archival | 🟡 manifest-on, no slice (candidate: 10y instruction archive) | 🔷 PRIMARY customer: claim-file graph + legal hold + erasure composition | 🔷 CDR tiering/rollup + legal-grade invoices |

## Card-triggered set

| Capability | CorPay | Insurance | Telco |
|---|---|---|---|
| Bulk | ✅ batch payment file (the triggering card) | — | — (usage is streaming) |
| Notification | — | 🔷 TRIGGERS: renewal notices (job→event→send) | 🔷 reinforces: invoice + campaign notices |
| Campaign | — | 🟡 decision: accept single-sector proof or add winback (card revision) | 🔷 the home: CRM-triggered dispatch |

## Shape/variant coverage (GM-proven, sample placement is planning)

| Variant | Today | Suggested home |
|---|---|---|
| db sqlserver | GM only | 🟡 Telco |
| auth apikey | GM only | 🟡 Telco partner/gateway surface |
| tenancy subdomain | package tests | 🟡 Telco MVNO |
| locking redis / sqlserver | package tests | 🟡 Telco |
| auth none / broker none / dotted names | GM shapes | stays GM-only by design |

## Tooling coverage

| Tool | CorPay | Plan |
|---|---|---|
| goldpath new (+first contract) | ✅ | every sample starts with it |
| add feature | 🟢 PROVEN by the nightly `GmGrown` shape (five recipes on a lean app; green 2026-09-05) — not yet in a SAMPLE | Insurance generates LEAN, then `add feature notification` — the verb proven in anger |
| add worker (queue/jobs) | ✅ both | FNOL / CDR workers |
| db init/add/status | ✅ | — |
| db bundle (prod story) | 🟡 no sample story | Telco: bundle-first deploy slice with runbook |
| goldpath check | ✅ every slice | — |
| Skills: goldpath-feature | ✅ drove S2 | — |
| Skills: test-gen / breaker | ✅ both fielded on CorPay 2026-07-23 — `tests/SPEC-GAPS.md` (8 spec-derived tests + a 7-item gap report) and `tests/BREAKER-VERDICT.md` (15 attacks, 1 genuine finding, fixed) | widen: test-gen→Insurance NFRs; breaker→Telco callback chaos |
| Skills: manifest | 🟡 never fielded | manifest→Insurance toggle |
| specdrift CLI path | ✅ | — |
| specdrift MCP path (from skills) | 🟡 not exercised in a sample flow | Insurance build runs it |

## Ring C modules (matrix entry pending their sample cards)

| Capability | CorPay (finance) | Insurance | Telco |
|---|---|---|---|
| Approvals | 🟡 no sample story yet (the api-portal product proves the pattern end to end) | 🔷 endorsement four-eyes candidate | 🔷 tariff-change four-eyes candidate |
| FileExchange | 🟡 no sample story yet | 🔷 bordereaux import candidate | 🔷 CDR drop candidate |

## Foundational packages (no matrix row — they underpin EVERY cell)

Abstractions · Sdk · ServiceDefaults · ApiDefaults · Data · Messaging — composed by every
sample and every generated shape; their proof is the golden-manifest matrix (nightly, plus
its PR-time subset), not a sector story. Named here so the docs-freshness inventory can hold
this file to the full package list.

## Console panels (post-UI; eyes-on + Playwright per sample)

| Panel | CorPay | Insurance | Telco |
|---|---|---|---|
| Triage home | 🔷 EOD reds | 🔷 renewal overrun | 🔷 billing ETA + consumer lag |
| Run console + repair | 🔷 EOD replay | 🔷 poisoned claim | 🔷 chunk takeover |
| Bulk gate | 🔷 four-eyes via UI | — | — |
| Campaign governor | — | — | 🔷 live throttle via UI |
| Notification evidence | — | 🔷 | 🔷 |
| Archival holds/erasure | 🟡 (with the archive slice) | 🔷 litigation hold | 🔷 tier view |
| Tenant-switcher / login-gate | 🔷 | 🔷 elevated scope | 🔷 MVNO |
| Product console link tiles (#257) | 🟡 pending the train (CorPay binds published packages; the smoke host proves it on `main` ✅) | 🟡 pending the train | 🟡 pending the train |

## Open planning decisions (the 🟡 owners)

1. CorPay completion slice: SoftDelete story + Caching slice + Archival slice + the real
   worker-consumer via the Contracts idiom.
2. Insurance is built LEAN then grown by `add feature` — the verb's field proof.
3. `goldpath-manifest` is the one unfielded skill left; it distributes per the tooling table.
4. Telco doubles as the VARIANT sample (sqlserver/apikey/subdomain/redis-lock).
5. Campaign single-sector proof: accept, or revise the insurance card with winback.
6. `db bundle` production story lands in Telco.
