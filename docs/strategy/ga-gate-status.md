# GA gate status — what is true today, gate by gate

The ledger for [goldpath-ga-readiness](../rfc/goldpath-ga-readiness.md) §7. The RFC holds the
**decisions** for each gate (the criterion, the proof that closes it, the owner, the effort, the
dependency); this file holds **today's status, with evidence**, and nothing else.

It was column (b) of the RFC's gate tables until 2026-10-23. It moved here because recording
progress is not a decision: under ADR-0014 every `docs/rfc/**` change is Tier C, so a code PR
that only updated its gate's status row needed the owner to merge it. A row here changes in the
same pull request that changes reality, like every other ledger (CLAUDE.md, open-threads,
ai-sdlc-status, coverage-matrix). Changing a gate's criterion, proof or owner is still an RFC
change.

## 7.1 Contract

| # | Gate | Today (evidence) |
|---|---|---|
| G1 | **Engine-free public API** (ADR-0013 S1 at the API level) | **11** shipped signatures name MassTransit: `Goldpath.Messaging` 10 (`AddGoldpathMessaging(Action<IBusRegistrationConfigurator>)`, `AddGoldpathOutbox`, `ConfigureGoldpathEndpoints`, both filters, handler registration, the adapter consumer) and `Goldpath.Campaign` 1 (`AddGoldpathCampaignConsumers`, kept for the templates until Part 4/4 and now registering the seam's handlers); `engine-gate.sh`'s baseline and ceiling are 11. Campaign's two consumer lines left with #328 (Part 3/4): campaign consumes through the seam (`AddGoldpathCampaign<TContext>()`, an item handler and an outcome batch handler on the queue the fleet already drains), the batch contract (`IIntegrationEventBatchHandler<T>`) is proven by conformance facts S2.9–S2.11 on real RabbitMQ, and a reflection test holds Campaign's public types and signatures free of engine types. The publish seam (GP0404) and consume seam (GP0405, Warning since preview.8) shipped. Since #327 (Part 2/4) the outbox/inbox configuration and the test harness exist in Goldpath vocabulary and the conformance suite names no engine; the 11 lines stay until Part 4/4 removes the old overloads |
| G2 | **PublicApiAnalyzers 5.x** (T24) | [#246](https://github.com/qorpe/goldpath/pull/246) (3.3.4 → 5.6.0) is open, merge state DIRTY, 2 failing checks. T24 holds the Roslyn pair on the SDK floor (compiler 5.0.0 in SDK 10.0.101). Whether 5.6 loads on that floor is the first thing to measure |
| G3 | **Manifest schema honest at 1.0** | Five values annotated (`oracle`, `kafka`, `inmemory`, `prometheus`, `eventstream`), enforced by `schema-honesty.sh`. Measured 2026-10-23: `specdrift validate` answered "clean — no findings" for each. Since #336 `goldpath check` warns after validate (not failing), naming the value and quoting the annotation that owns it, derived from the `$comment` so a new annotation needs no code; corpus rows `m5`–`m9` with `# expect-warning:` prove it (`RoadmapValuesTests`). Open: the engine half (T45) and a service's unannotated `oracle` (T46) |
| G4 | **1.0 public-API review and freeze** | Never done. The surface grew train by train, and Campaign R2 ([#277](https://github.com/qorpe/goldpath/pull/277)) adds to it in preview.10 |

## 7.2 Dependencies and runtime

| # | Gate | Today (evidence) |
|---|---|---|
| G5 | **MassTransit exit executed before the v8 window closes** (T18/T24) | Seam: publish + consume shipped. Suite: 8 facts, green on 8.5.10 → 8.5.11 ([#268](https://github.com/qorpe/goldpath/pull/268)). The outbox/harness vocabulary line is open. No family-bus RFC or repository exists in `qorpe` (2026-10-20). D (Wolverine) is the RFC's measured fallback, which the RFC sizes at about a one-week adapter. **Part 1/5 (#330, 2026-10-25):** `Goldpath.Messaging.Wolverine` (WolverineFx 6.53.0, `IsPackable=false`) runs the whole conformance class, S2.1–S2.12, green on real RabbitMQ + PostgreSQL in the same CI job as 8.5.11, each concrete class naming its engine by its manifest value; the engine is found through the new `IGoldpathMessagingEngine` contract. A planted fault (the tenant header dropped) turns S2.2 red on Wolverine only. Still open: the suite gaps (Part 2/5), the differential report (3/5), S4 (4/5), S1/S6 (5/5), and what the adapter refuses today (open-threads T51). `wolverine` is not a manifest value until those proofs are linked in T18 (RFC goldpath-messaging-engines §3) |
| G6 | **Console OIDC login** (T10) | Not built, by deliberate decision ([console RFC](../rfc/goldpath-console.md) §3 auth, case 3). The console relies on the app's own auth floor |
| G7 | **Oracle provider — IN** (T16; owner decision 2026-10-21, Q3) | SCHEMA-ONLY ([golden-manifests-v1](../strategy/golden-manifests-v1.md)). The GM-2 banking persona names it. qorpe.coexist (private) already targets Oracle (owner, 2026-10-20), so the family's first Oracle exposure exists. Q3 answered IN |

## 7.3 Proof and quality

| # | Gate | Today (evidence) |
|---|---|---|
| G8 | **GM at PR time (T30) + branch protection with required checks** | **Met.** [#273](https://github.com/qorpe/goldpath/pull/273) is merged. `main` protection lists required checks (gates, build/test, security audit, licence gate, `golden manifests (PR subset)`) and merges go through a PR. A planted red PR cannot merge **MET 2026-10-21.** [#273](https://github.com/qorpe/goldpath/pull/273) merged 2026-10-20 (T30 closed with a planted red and its green). Since 2026-10-21 `main` requires a PR and five checks — `merge-policy`, `gates`, `integration`, `kit`, `golden manifests (PR subset)` — with administrators included and no required review (`gh api repos/qorpe/goldpath/branches/main/protection`, Q12). The planted-red half of the proof is T30's run; a red required check now blocks the merge button |
| G9 | **Mutation: every gated package above break with margin** | With [#275](https://github.com/qorpe/goldpath/pull/275) + [#278](https://github.com/qorpe/goldpath/pull/278) (merged 2026-10-20): Caching 69.57 → **100**, Auth 70.65 → **97.83**, Bulk 70.09 → **88.38**. Still under a 5-point margin: MultiTenancy 72.09, ServiceDefaults 72.15, Jobs 72.52, Analyzers 72.55, Idempotency 73.58, Archival 74.49, Approvals 74.84 (#278's [`stryker/README.md`](../../stryker/README.md) table, nightly [39420913374](https://github.com/qorpe/goldpath/actions/runs/39420913374)). T33: the EF cached-model blind spot in Jobs/Archival |
| G10 | **Scenario campaign: all seven seeded scenarios evidenced** | **3 of 7** (S-FIN-01; S-FIN-02 and S-OPS-01 since 2026-10-21, #323) |
| G10a | S-FLEET-03 (GlobalTps pair), S-FIN-02 (hold vs erasure), S-OPS-01 (pause-all survives restart + audit), S-OPS-02 (refusals from the screen, zero 500s) | **3 of 4.** S-FIN-02 and S-OPS-01 evidenced 2026-10-21 ([#323](https://github.com/qorpe/goldpath/issues/323): `ScenarioFin02Tests`, `ScenarioOps01Tests`). S-OPS-02 evidenced 2026-10-25 ([#324](https://github.com/qorpe/goldpath/issues/324): `ui/console/e2e/refusals.spec.ts` in the console smoke, four mistakes refused from the screen, every verb a 400, no 5xx). S-FLEET-03 ([#292](https://github.com/qorpe/goldpath/issues/292)) is not written |
| G10b | S-FLEET-01 (2.3M devices, five working days, honest clock compression) | not run |
| G10c | S-FLEET-02 (30M, 2,000 TPS, flat for 60 min) incl. **the 30M scale rig** | rig spec unwritten ("SPEC TO BE WRITTEN WITH THE OWNER"). The enumeration leg is measured off the rig on PostgreSQL (#288, 2026-10-24): 35M targets through the pacer in 37.3 min, 15,625 rows/s, flat leader memory, on the agent sandbox profile ([campaign-benchmarks.md](../../packages/Goldpath.Campaign/ops/campaign-benchmarks.md)). The Oracle Cloud Always Free VM was considered and dropped: its A1 allowance is reported halved to 2 OCPU / 12 GB (enforced from 2026-08-18), too small for this run, and no cloud account is opened (owner, 2026-10-21). The rate leg's minute-boundary dip is gone ([#284](https://github.com/qorpe/goldpath/issues/284), 2026-10-26): the default leadership slice equals the default cron period, so leadership is continuous, and `CampaignContinuousLeadershipTests` holds every 10-second window within ±5% across two hand-overs (a measured hand-over under 0.6 s at 50 TPS); the release path's own ceiling (about 600/s inside a slice at 1000 TPS) is Part 3 of that gap |
| G11 | **T1: the console driven against CorPay** | CorPay's nightly asserts the console is SERVED and inherits the floor. It drives nothing ([ai-sdlc-status](../strategy/ai-sdlc-status.md) Operations row) |
| G12 | **Samples: Insurance + Telco; no empty core cell** | CorPay has four 🟡 (SoftDelete, Caching, a Contracts-idiom worker consumer, Archival). Insurance and Telco are not started (master plan step 8, "LAST, as the full-set exam"). The Factoring sample left the public exam on 2026-10-25 (RFC Q6 amendment) |
| G13 | **Performance baselines published** | Five tables (Jobs, Archival, Bulk, Notification, Campaign), last measured 2026-07-07…13 (e.g. [campaign-benchmarks](../../packages/Goldpath.Campaign/ops/campaign-benchmarks.md)). The monthly `bench.yml` is green ([36843126511](https://github.com/qorpe/goldpath/actions/runs/36843126511), 2026-10-01) but its numbers stay in a run artifact. R2 changes the release path |
| G14 | **Air-gapped install proven** | Claimed (SECURITY.md hardening notes, ADR-0008, foundation §11 *"the offline/proxy scenario is part of Template CI"*). The freshness gates skip honestly offline, and the [migrations runbook](../ops/migrations-runbook.md) §6 covers bundles. **No CI job runs anything offline** (no offline/mirror step in the workflows or `validate-gm.sh`) |

## 7.4 Security

| # | Gate | Today (evidence) |
|---|---|---|
| G15 | **Threat model per module with an auth surface** | **Part 1/2 in place, Part 2/2 open.** Scans: since [#366](https://github.com/qorpe/goldpath/pull/366) (2026-10-23) `codeql.yml` runs CodeQL (C#, JS/TS) on every PR, and the nightly `zap-baseline` job runs a passive ZAP baseline against a generated app and its served console. Every alert is settled in `scripts/zap-baseline.conf`, and the open header findings are [T43](open-threads.md). Threat models: **4 of 10 surfaces** in [docs/security/threat-model.md](../security/threat-model.md) (2026-10-25; admin routes, served console, Campaign R2.8 callbacks, Bulk upload + gate), each threat mapped to its test, an issue (#452, #453, #454) or an accepted risk; six accepted risks (A1–A6), signed by the owner 2026-10-26 on #455. Still to model: Approvals, FileExchange, Notification, DataProtection, Idempotency, MultiTenancy ([#340](https://github.com/qorpe/goldpath/issues/340) stays open) |
| G16 | **Dependency/CVE gate** (exists) **+ a patch-train path** | Gate: **exists**. Path: the 2026-10-13 advisories broke every app generated from published preview.8. `main` was fixed 2026-10-19 ([#269](https://github.com/qorpe/goldpath/pull/269)), and adopters are still waiting on [#271](https://github.com/qorpe/goldpath/pull/271) on 2026-10-20 |
| G17 | **SBOM + provenance** (exists) | **Exists** since preview.7 (F2, [#156](https://github.com/qorpe/goldpath/pull/156)): syft CycloneDX, `attest-build-provenance`, OIDC trusted publishing. The stale `preview.6` lines in SECURITY.md are fixed in this RFC's PR |

## 7.5 Documentation

| # | Gate | Today (evidence) |
|---|---|---|
| G18 | **Docs: getting-started → production; upgrade guides; support policy page** | [getting-started](../guide/getting-started.md) stops at a production pointer. Upgrade guides preview.1–9 exist. The support policy is spread over SECURITY.md and the versioning RFC. The `upgrade` skill is NOT BUILT (ai-sdlc §2) and stays optional |

## 7.6 Business-owned

| # | Gate | Today (evidence) |
|---|---|---|
| G19 | **At least one external adopter in production** (foundation §12, Phase 4 gate) | **None.** api-portal and coexist are first-party: ADR-0012 calls a product module "the platform's permanent first adopter", not an external one. The factoring-class engagement is before its PoC (T20's rehearsal has not run) |
| G20 | **Maintainer continuity / bus factor** | Solo. Versioning D3 says *"honest for a solo-maintained OSS asset"*, and SECURITY.md lists one human contact. Foundation §11's antidote to "Orphaning" (*"dedicated platform team"*) is not met |

## Summary

| Area | Gates | Exists today |
|---|---|---|
| Contract | G1–G4 | ledgers + roll, schema-honesty |
| Runtime | G5–G7 | seam + 8-fact suite |
| Proof | G8–G14 | **G8 met**, GM nightly + PR subset, mutation gate, 3/7 scenarios, 5 bench tables |
| Security | G15–G17 | CVE gate, SBOM, provenance, CodeQL + ZAP baseline |
| Docs | G18 | getting-started, upgrade guides |
| Business | G19–G20 | — |
