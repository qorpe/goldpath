# RFC: 1.0 / GA readiness — "enterprise ready" as a gate, not a feeling

- Status: **ACCEPTED** (2026-10-21) — PROPOSED 2026-10-20, asked for by the owner the same day:
  *"enterprise ready" must become a measurable gate*. The owner answered Q1–Q12 in chat on
  2026-10-21; §8.3 records every answer and the gate table and §8.1 carry them. Documents only;
  nothing here moves code, a dependency or a default. Every gate below closes on a proof that can
  be re-run, never on a judgement.
- Composes with: [goldpath-versioning](goldpath-versioning.md) (the SemVer and support contract
  this RFC activates), [SECURITY.md](../../SECURITY.md) (the response targets),
  [goldpath-messaging-exit](goldpath-messaging-exit.md) + [ADR-0013](../adr/ADR-0013-messaging-bus-is-a-swappable-engine.md)
  as amended by [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) (the calendar-bound item), [foundation](../strategy/foundation.md) §12 (Phase 4's gate: *the
  first external customer project*), and the three live ledgers. Contradicts no ADR; it makes
  one ADR consequence true that is not true today (ADR-0010, gate G8). Its Q1/Q2 answers widen
  ADR-0013 — a third engine adapter, zero adopter impact for any switch, the default locked for
  1.x — and that amendment is recorded as [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md).
- Ledger: [open-threads.md](../strategy/open-threads.md) **T35** carries this gate set until 1.0
  ships; each gate keeps its own thread where one exists (T1, T10, T16, T18, T24, T33). The
  Goldpath-family bus, committed by Q1 but off the 1.0 path, has its own row, **T40**; the deferred public demos have **T41**. GitHub
  milestones `0.1.0-preview.10`, `0.1.0-preview.11`, `1.0.0-rc.1`, `1.0.0-rc.2`, `1.0.0` and
  `family-bus` carry the issues, train by train (§8.1).

## 1. Scope / Non-Goals

**Scope.** Define (a) what a `1.0.0` of the Goldpath train promises an enterprise adopter, (b) the
GA gate table — every criterion with the proof that closes it, today's status with evidence, an
owner, a rough effort and an order — (c) what 1.0 deliberately does not include, and (d) the
train sequence with its critical path. The decisions this RFC could not make were §8.3's
questions for the owner; all twelve were answered on 2026-10-21 and §8.3 records the answers.

**Why now.** Three facts measured on 2026-10-20 make "are we enterprise ready?" answerable only
with a table:

1. The SemVer promise starts at 1.0 ([versioning](goldpath-versioning.md) D2). Whatever is in
   a `PublicAPI.Shipped.txt` on that day is frozen for the 1.x line, and today **13 shipped
   signatures name MassTransit types** (10 in `Goldpath.Messaging`, 3 in `Goldpath.Campaign`).
   Freezing them would make every future engine change a 2.0, which is the exact cost
   ADR-0013 exists to avoid.
2. The free MassTransit 8.x line's maintenance window closes **around the end of 2026**
   ([messaging-exit](goldpath-messaging-exit.md) §4 A). That is about ten weeks away, and no
   family-bus RFC or repository exists yet (the `qorpe` organisation's repository list, 2026-10-20).
3. `main` requires a pull request (no approvals, administrators included) but lists **no
   required status checks** (`gh api repos/qorpe/goldpath/branches/main/protection`, 2026-10-20;
   earlier the same day it answered 404 "Branch not protected"). ADR-0010's consequence says *"gate bypassing is
   also technically closed off (branch protection + pipeline rules)"*. Today every CI gate in this
   repository reports a result, but none of them can block a merge. **Closed 2026-10-21** (Q12,
   G8): five checks are now required, administrators included.

**Non-goals for 1.0.** Accepted with the RFC on 2026-10-21. Anything here may come back as a
1.x minor because it is additive:

| Item | Why it can wait | Where it lives |
|---|---|---|
| `Goldpath.Ai` (ADR-0011) | Opt-in and additive: a 1.x minor can ship it without breaking anyone. The pilot is what says which of its four parts an adopter reaches for ([master plan](../strategy/master-plan-2026-08.md) item 9) | T7 |
| Authoring Slices 1–3 (`goldpath-authoring` skill, `spec-review` agent, V4/V5) | They belong to the development layer (ADR-0006), not to the library's SemVer surface. Slice 0, the deterministic floor, lands earlier ([#276](https://github.com/qorpe/goldpath/pull/276)) | T34 ([#274](https://github.com/qorpe/goldpath/pull/274)) |
| The family-bus **default flip** (messaging-exit §7.1 S4–S6; ADR-0015 §3) | Needs a ≥4-week shadow lane after a library that does not exist yet. 1.0 needs a *proven exit* (G5), not the flip. D2 counts a default flip as breaking, so the owner decided (Q1, Q2): the family bus is **committed on its own timeline** — RFC + qorpe repository December 2026, first adapter January–February 2027, ≥4-week shadow lane (S5), opt-in in a 1.x minor, **default only in 2.0**. It is off the 1.0 critical path, and it is not dropped | T18, T40 |
| A paid external penetration test | Zero budget (Q9, Q11). 1.0 carries free CodeQL + OWASP ZAP in CI and the G15 threat models instead. A paid test happens only when a paying customer funds it | — |
| Public demos (one subdomain per product through a free Cloudflare Tunnel) | Zero budget and no always-on machine (Q11). Deferred to a written trigger, the owner getting an always-on machine | T41 |
| A commercial support offering | Not intended for now (Q9). The support window is the versioning contract's, carried by G20's zero-cost continuity path | — |
| Transformation package / specanchor composition, industry packs | Phase 3 has its own gate. The GA promise is about the library train, not the method | T20, T23 |
| CLI-as-MCP, plugin packaging, TS mutation, review agent with a stored key, tenant picker, console routes, cross-repo cycle parity | Each one has a written trigger that has not fired | T6, T3, T4, T11, T9, T28 |
| LTS designations | Versioning RFC non-goal: not before production adopters ask for one | — |
| The other SCHEMA-ONLY values (`kafka`, `inmemory`, `prometheus`, `eventstream`) | They stay annotated in the 1.0 schema, and a manifest that selects one is warned (§3, G3). None of them is built for 1.0 | — |

## 2. Seam Map — the surfaces 1.0 freezes

Versioning D2 already defines what counts as breaking. GA means each of those surfaces has a
**mechanical** freeze, which follows ADR-0005: a promise without a verifier is not a promise.

| Surface | Size / state today | Freeze mechanism today | Gap |
|---|---|---|---|
| Public .NET API | **≈2,370 lines** across 23 packages' `PublicAPI.Shipped.txt`. 81% of them sit in seven modules: Jobs 423, Campaign 339, Approvals 275, Bulk 253, Notification 247, Archival 197, FileExchange 187 | PublicApiAnalyzers 3.3.4 (RS0016/RS0017) and the `roll-shipped.sh` release roll | 13 MassTransit-typed signatures (G1). The analyzer is two majors behind ([#246](https://github.com/qorpe/goldpath/pull/246), G2). No review has ever read the surface as a whole (G4) |
| Admin HTTP contract | **FROZEN**, Revision R3 ([admin contract](goldpath-admin-contract.md)) | route-freeze test and the console smoke (39 tests) | Campaign R2 adds the signed callback route ([#277](https://github.com/qorpe/goldpath/pull/277)). It must be frozen with the rest |
| Manifest schema v1 | `schemas/manifest/v1/`, corpus + `schema-honesty.sh` | corpus-check, schema-honesty (both in CI) | Five values are SCHEMA-ONLY or RESERVED. They are annotated, but nothing warns at validation time (§3, G3) |
| Goldpath-owned tables | `[schema]` discipline + D7 proofs (`validate-migrations.sh`) | migrations runbook + GP1801 | — |
| Diagnostic contract (span/meter names, analyzer IDs) | Analyzer IDs are release-tracked (RS2000/RS2007). Tracing is pinned in places (`TracingTests`, `HandlerSeamTests`, `ServiceDefaultsTests`) | partial | No single inventory of meter and span names exists to freeze (part of G4) |
| CLI verbs + template parameters | docs-freshness checks that every verb is documented | documentation check only | Nothing fails when a flag is renamed (part of G4) |
| Bus engine behaviour | conformance suite, 8 facts through the seam | CI integration job | Below the RFC's 60–100 scenarios. No second adapter exists (G5) |

## 3. Manifest Surface — the schema at 1.0

No new keys. The 1.0 schema must be **honest**: every enum value either runs end to end or tells
the adopter it does not before they build on it. Today five values cannot run: `db: oracle`
(T16, decided by G7: **IN** — owner decision 2026-10-21, Q3), `broker: kafka|inmemory`,
`exporters: prometheus` and `auditTrail.store: eventstream`. `oracle` leaves the list when G7's
proofs run; the other four stay annotated and warned at 1.0. Each one carries a SCHEMA-ONLY / RESERVED `$comment`, and
`schema-honesty.sh` fails CI if an annotation goes missing. A `$comment` is written for the
person reading the schema, though, and a validator does not act on it. G3 asks that a manifest
which *selects* such a value gets told so at validation time, with the ledger row named.

This RFC recommends **keep + annotate + warn**, not removal. Removing an enum value is itself a
breaking change for any manifest that names it, and the GM-2 `banking-core` persona names `oracle`
([golden-manifests-v1](../strategy/golden-manifests-v1.md)).

## 4. API Surface — what GA promises an enterprise adopter

| Promise | What it means in practice | Source it activates | Gate(s) |
|---|---|---|---|
| **API stability (SemVer from 1.0)** | In 1.x, breaking changes happen only in a major. Minors add and patches fix. "Breaking" is the D2 list: PublicAPI, admin contract, owned schemas, defaults, diagnostic names. APIs marked `[Experimental]` (the BCL attribute, composed per ADR-0003) are outside the promise and say so at compile time. At 1.0 that covers the `Goldpath.Approvals` and `Goldpath.FileExchange` surfaces (Q4), lifted in a 1.x minor once T21/T22's proofs run | versioning D2 | G1, G2, G4 |
| **Support window** | The latest 1.x minor gets fixes. When 2.0 ships, 1.x gets **security fixes only, for 6 months**. One lockstep train (D1) covers every package, the CLI and the templates. The default bus engine is locked for 1.x; a new engine is opt-in in 1.x and default only in 2.0 (Q2) | versioning D1/D3 | G20 (someone has to be able to keep it) |
| **Security response** | Acknowledgement in 3 working days, assessment in 10, a fix or written mitigation for high/critical in 30. Coordinated disclosure. CVE gate on every PR. Static (CodeQL) and dynamic (OWASP ZAP) scans in CI; no paid pentest (Q9) | [SECURITY.md](../../SECURITY.md) | G15, G16 |
| **Upgrade path** | Every train has a step-by-step guide (`docs/upgrades/`, preview.1–9 exist today), plus one consolidated `0.x → 1.0` guide. From rc.1 on, the PublicAPI diff contains no removals | versioning D4, release checklist | G4, G18 |
| **Air-gapped install** | Generate, restore, build, migrate and run with no internet, from a local feed and registry. Nothing phones home and there is no licence check | ADR-0008, foundation §10/§11 | G14 |
| **Supply chain** | CycloneDX SBOM + signed SLSA provenance on every train. OIDC trusted publishing, so no long-lived key exists. Licence gate (free/OSS only). Every dependency pinned | SECURITY.md, F2 ([#156](https://github.com/qorpe/goldpath/pull/156)) | G16, G17 |
| **Proven composition** | Every manifest shape in the matrix is generated, built, run and smoked. A PR that breaks a shape cannot merge | ADR-0008, ADR-0010 | G8 |
| **Runtime independence from a vendor calendar** | Application code names no bus library type. A CVE in the shipped engine after its maintenance window has a measured exit: one train, not a project. **Any engine switch has zero adopter impact** (owner decision 2026-10-21): apps compile unchanged (S1), a mixed fleet interoperates on the wire (S4, mandatory — no drained cutover), and the switch rolls back both ways (S6) | ADR-0013 + ADR-0015, [messaging-exit](goldpath-messaging-exit.md) §7.1 | G1, G5 |
| **Measured behaviour** | Every scale claim has a bench report with a profile, a date and a commit. The seven seeded business scenarios each have a replayable run | foundation §8, [scenario campaign](../strategy/scenario-campaign.md) | G10, G13 |

## 5. Analyzer Rules — the mechanical gates that keep the promise

No new analyzer. The gates that already exist and that GA leans on: PublicApiAnalyzers
(RS0016/RS0017), GP0404/GP0405 (the bus seam), the security audit and licence gate in `ci.yml`,
`template-pins.sh`, `train-freshness.sh`, `schema-honesty.sh`, `ledger-check.sh`,
`docs-freshness.sh`, `skills-parity.sh`, the mutation gate (break 70), and the release checklist's
D4 trio.

New mechanisms the gate table asks for. Each one is small, and each one is a verifier rather than
a document:

- A **PublicAPI engine check** for G1: CI fails if `MassTransit.` appears in any
  `PublicAPI.*.txt` outside the named engine-adapter package.
- **Branch protection** with required checks for G8. It is a repository setting, not code.
  **On since 2026-10-21**: `merge-policy`, `gates`, `integration`, `kit` and
  `golden manifests (PR subset)` are required, administrators included, no required review.
- **CodeQL and OWASP ZAP** in CI for G15 (Q9: the free substitute for a paid pentest). CodeQL
  scans the C# and TypeScript sources on every PR; ZAP's baseline scan runs against a running
  GM shape's API and served console.
- An **offline GM shape** for G14.
- **Verify-after-publish** in `release.yml` for G17: `gh attestation verify` runs on the pushed
  packages.

## 6. Ops Package — support, response, continuity

GA adds no runtime behaviour. It adds operating obligations, which G18–G20 make concrete:

- A **production runbook**. Today `getting-started.md` ends at a pointer ("Production posture
  … each concept", line 70).
- A **support policy page** in `docs/guide/`, joining versioning D3, the SECURITY.md targets
  and the engine/provider matrix in one place.
- A **patch-train path** for restore-breaking advisories (G16). On 2026-10-13 four .NET
  advisories turned NU1902/NU1903 into restore failures for every app generated from the
  published preview.8 templates. The fix reached `main` on 2026-10-19
  ([#269](https://github.com/qorpe/goldpath/pull/269)) and reaches adopters only when
  [#271](https://github.com/qorpe/goldpath/pull/271) ships.
- A **continuity answer**. A support window promised by one person depends on that one person.
  Accepted 2026-10-21 (Q9): the zero-cost path — Apache-2.0, a second trusted owner on the
  GitHub organisation, nuget.org and npm, a written successor/transfer pledge, and a release
  runbook proven by someone other than the owner (G20).
- **Public demos are deferred, not a 1.0 obligation** (Q11). One subdomain per product behind a
  free Cloudflare Tunnel needs an always-on machine, and the owner has none today; open-threads
  **T41** carries the trigger and the proof.

## 7. Test Plan — the GA gate table

Columns: **(a)** the proof that closes the gate · **(b)** today's status, with evidence ·
**(c)** owner: **A** = agent-doable, **O** = owner decision, **B** = business-owned · **(d)** rough
effort (ad = agent-days) · **(e)** dependency / target train.

### 7.1 Contract

| # | Criterion | (a) Proof that closes it | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G1 | **Engine-free public API** (ADR-0013 S1 at the API level) | No `MassTransit.` in any `PublicAPI.*.txt` outside one named adapter package, enforced by a CI check. Campaign's consumers run through `IIntegrationEventHandler<T>`. Outbox/inbox configuration and the test harness use Goldpath vocabulary (messaging-exit §8, open DoD line). GM broker shapes and CorPay compile unchanged | **13** shipped signatures name MassTransit: `Goldpath.Messaging` 10 (`AddGoldpathMessaging(Action<IBusRegistrationConfigurator>)`, `AddGoldpathOutbox`, `ConfigureGoldpathEndpoints`, both filters, handler registration, the adapter consumer) and `Goldpath.Campaign` 3 (two consumers + `AddGoldpathCampaignConsumers`). The publish seam (GP0404) and consume seam (GP0405, Warning since preview.8) shipped. Since #327 (Part 2/4) the outbox/inbox configuration and the test harness exist in Goldpath vocabulary and the conformance suite names no engine; the 13 lines stay until Part 4/4 removes the old overloads | A. The adapter package names are proposed by the engine-adapter RFC revision that opens G1; the owner accepts them by merging it | 5–10 ad | First. The RFC revision and a ratcheting engine check in preview.10 (so the 13 lines cannot grow while the API is being emptied), the breaking change in preview.11 with a guide. Before G4 and G5 |
| G2 | **PublicApiAnalyzers 5.x** (T24) | The analyzer runs at 5.x on `main`. Every ledger is regenerated, including record-synthesized members, with no RS0016/RS0017 suppression and all gates green | [#246](https://github.com/qorpe/goldpath/pull/246) (3.3.4 → 5.6.0) is open, merge state DIRTY, 2 failing checks. T24 holds the Roslyn pair on the SDK floor (compiler 5.0.0 in SDK 10.0.101). Whether 5.6 loads on that floor is the first thing to measure | A | 1–3 ad | Before G4. Freezing ledgers under an analyzer two majors old means re-litigating them after 1.0 |
| G3 | **Manifest schema honest at 1.0** | Every enum value is GM-proven or SCHEMA-ONLY-annotated (already gated). A manifest that SELECTS an annotated value gets a validation-time finding (`specdrift validate` / `goldpath check`) naming the ledger row. A corpus row proves it | Five values annotated (`oracle`, `kafka`, `inmemory`, `prometheus`, `eventstream`), enforced by `schema-honesty.sh`. Nobody has checked whether validation says anything to an adopter who selects one; that check is this gate's first step | A | 1–3 ad | G7 decided `oracle` IN (2026-10-21), so the finding covers the other four values. preview.11. Before G4 |
| G4 | **1.0 public-API review and freeze** | A review record covers every Shipped line (≈2,370) and marks each one keep / rename / remove / `[Experimental]`. It also lists the §2 non-.NET surfaces, each with its freeze gate. The Approvals and FileExchange surfaces are marked `[Experimental]` (Q4). The owner signs it. The changes land in ONE train with a guide. From rc.1 the ledger diff is additions-only | Never done. The surface grew train by train, and Campaign R2 ([#277](https://github.com/qorpe/goldpath/pull/277)) adds to it in preview.10 | A drafts, O decides | 5 ad + 1–2 owner days | After G1, G2, G7, and after #277 lands. It defines rc.1 |

### 7.2 Dependencies and runtime

| # | Criterion | (a) Proof | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G5 | **MassTransit exit executed before the v8 window closes** (T18/T24) | **By 2026-12-31**, the **Wolverine** adapter (owner decision 2026-10-21, Q1: the 1.0 insurance) passes the conformance suite (S2), widened toward the RFC's 60–100 scenarios including its four named gaps (delayed redelivery, ordering, OTel spans, prefetch), and the differential run (S3) against 8.5.11 as the oracle. Because any engine switch must have **zero adopter impact**, the same adapter also proves S1 (GM broker shapes and CorPay compile unchanged against it), **S4 wire compatibility, now mandatory** (MassTransit ↔ Wolverine in a mixed fleet, both directions, outbox rows written under one delivered by the other — no drained cutover) and S6 (rollback both ways). SECURITY.md then states the engine posture: what an 8.x CVE after the window triggers. The default does not change in 1.x (Q2), so S5's shadow lane and the flip are not 1.0 gates | Seam: publish + consume shipped. Suite: 8 facts, green on 8.5.10 → 8.5.11 ([#268](https://github.com/qorpe/goldpath/pull/268)). The outbox/harness vocabulary line is open. No family-bus RFC or repository exists in `qorpe` (2026-10-20). D (Wolverine) is the RFC's measured fallback, which the RFC sizes at about a one-week adapter | A (the owner picked Wolverine, Q1) | suite 5–10 ad. Wolverine adapter ~5 ad, plus S4/S6 ~3–5 ad | **CALENDAR-BOUND: 2026-12-31.** Needs G1 first. Critical path, preview.11. The family bus runs on its own timeline (T40) |
| G6 | **Console OIDC login** (T10) | T10's proof: the console signs in once and carries the token to every service with honest refusals. The console smoke drives it against a real IdP container across ≥2 services on different origins, and axe stays green | Not built, by deliberate decision ([console RFC](goldpath-console.md) §3 auth, case 3). The console relies on the app's own auth floor | A | 5–10 ad | rc.1. Independent of the others. **A 1.0 gate** (owner decision 2026-10-21, Q7) |
| G7 | **Oracle provider — IN** (T16; owner decision 2026-10-21, Q3) | **IN** (decided 2026-10-20 on [#287](https://github.com/qorpe/goldpath/issues/287), confirmed with this RFC: adopters in the Turkish enterprise market run Oracle). The proof: a scoped licence-gate exception for the FUTC-licensed provider ([jobs RFC](goldpath-jobs.md) §11 names the precedent), a GM Oracle shape, and D7 migration + Quartz store + keyset-translation proofs on a real Oracle container. The RFC is [#305](https://github.com/qorpe/goldpath/issues/305); the build is [#287](https://github.com/qorpe/goldpath/issues/287) | SCHEMA-ONLY ([golden-manifests-v1](../strategy/golden-manifests-v1.md)). The GM-2 banking persona names it. qorpe.coexist (private) already targets Oracle (owner, 2026-10-20), so the family's first Oracle exposure exists. Q3 answered IN | A | 10–15 ad | RFC preview.10, build preview.10 → preview.11. Before G4 (the enum freezes with it) |

### 7.3 Proof and quality

| # | Criterion | (a) Proof | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G8 | **GM at PR time (T30) + branch protection with required checks** | [#273](https://github.com/qorpe/goldpath/pull/273) is merged. `main` protection lists required checks (gates, build/test, security audit, licence gate, `golden manifests (PR subset)`) and merges go through a PR. A planted red PR cannot merge **MET 2026-10-21.** [#273](https://github.com/qorpe/goldpath/pull/273) merged 2026-10-20 (T30 closed with a planted red and its green). Since 2026-10-21 `main` requires a PR and five checks — `merge-policy`, `gates`, `integration`, `kit`, `golden manifests (PR subset)` — with administrators included and no required review (`gh api repos/qorpe/goldpath/branches/main/protection`, Q12). The planted-red half of the proof is T30's run; a red required check now blocks the merge button | A (#273) + O (setting) | done | done |
| G9 | **Mutation: every gated package above break with margin** | Every gated package scores **≥ 75%** (break 70 + a 5-point margin; owner decision 2026-10-21, Q5: no lowered break, no unledgered exclusion) on two consecutive nightlies with the heavy six in the nightly. T33 is closed. ApiDefaults / Locking.SqlServer / Sdk keep their written no-gate reason | With [#275](https://github.com/qorpe/goldpath/pull/275) + [#278](https://github.com/qorpe/goldpath/pull/278) (merged 2026-10-20): Caching 69.57 → **100**, Auth 70.65 → **97.83**, Bulk 70.09 → **88.38**. Still under a 5-point margin: MultiTenancy 72.09, ServiceDefaults 72.15, Jobs 72.52, Analyzers 72.55, Idempotency 73.58, Archival 74.49, Approvals 74.84 (#278's [`stryker/README.md`](../../stryker/README.md) table, nightly [39420913374](https://github.com/qorpe/goldpath/actions/runs/39420913374)). T33: the EF cached-model blind spot in Jobs/Archival | A | 5–10 ad | preview.10 → preview.11 |
| G10 | **Scenario campaign: all seven seeded scenarios evidenced** | Each scenario's `evidence:` names a replayable run ([scenario-campaign.md](../strategy/scenario-campaign.md)) | **3 of 7** (S-FIN-01; S-FIN-02 and S-OPS-01 since 2026-10-21, #323) | — | — | — |
| G10a | S-FLEET-03 (GlobalTps pair), S-FIN-02 (hold vs erasure), S-OPS-01 (pause-all survives restart + audit), S-OPS-02 (refusals from the screen, zero 500s) | each one a test or e2e run, linked | **2 of 4.** S-FIN-02 and S-OPS-01 evidenced 2026-10-21 ([#323](https://github.com/qorpe/goldpath/issues/323): `ScenarioFin02Tests`, `ScenarioOps01Tests`). S-FLEET-03 ([#292](https://github.com/qorpe/goldpath/issues/292)) and S-OPS-02 (#323 Part 2/2) are not written | A | 1–2 ad each | preview.10. The FLEET scenario runs on the R2 train |
| G10b | S-FLEET-01 (2.3M devices, five working days, honest clock compression) | run + clock policy written beside it | not run | A | 3–5 ad | rc.1, on Campaign R2 |
| G10c | S-FLEET-02 (30M, 2,000 TPS, flat for 60 min) incl. **the 30M scale rig** | the rig spec co-written with the owner (hardware, dataset, clock policy, thresholds) *before* the run, then numbers published like the H6 benches. **Zero budget** (Q11): the run uses the **full 30M dataset, never a shrunk one**, on free hardware — primary: the owner's Mac, overnight, in a dedicated Lima VM `bench` (separate from the `ci` runner VM); repeatable lane: GitHub-hosted free runners (public repository, 4 vCPU / 16 GB, 6 h job cap), also on the full 30M dataset. If the hardware cannot sustain 2,000 TPS, the published result states the hardware and the measured ceiling (TPS per core) honestly; the dataset is not the variable | rig spec unwritten ("SPEC TO BE WRITTEN WITH THE OWNER"). The Oracle Cloud Always Free VM was considered and dropped: its A1 allowance is reported halved to 2 OCPU / 12 GB (enforced from 2026-08-18), too small for this run, and no cloud account is opened (owner, 2026-10-21) | O (thresholds; leaves the Mac on for a run), A writes the spec and runs | 5 ad once the spec is signed | spec preview.10, run rc.2 at the latest |
| G11 | **T1: the console driven against CorPay** | T1's proof: the console smoke (or its CorPay twin) DRIVES CorPay's admin surface end to end, including the console its internal head serves | CorPay's nightly asserts the console is SERVED and inherits the floor. It drives nothing ([ai-sdlc-status](../strategy/ai-sdlc-status.md) Operations row) | A | 2–3 ad | preview.10 |
| G12 | **Samples: Insurance + Telco + Factoring; no empty core cell** | The [coverage matrix](../strategy/coverage-matrix.md) core table has no 🟡 or 🔷. Each sample is built from scratch and nightly-proven against the PUBLISHED train the way CorPay is (owner decision 2026-10-21, Q6). The **Factoring** sample is a generic, clean-room domain sample in this public repository: public domain knowledge only, no client, institution or engagement names or details. It is not T23's industry pack, which stays on its own trigger | CorPay has four 🟡 (SoftDelete, Caching, a Contracts-idiom worker consumer, Archival). Insurance, Telco and Factoring are not started (master plan step 8, "LAST, as the full-set exam") | A | CorPay cells 5 ad. Each sector 15–20 ad | CorPay cells rc.1, the three sectors rc.2. The exam that may still force an rc |
| G13 | **Performance baselines published** | Every engine module's `ops/*-benchmarks.md` is re-measured on the rc train, on the CI reference profile, with date + commit + run id. Messaging, Approvals and FileExchange each get a first table | Five tables (Jobs, Archival, Bulk, Notification, Campaign), last measured 2026-07-07…13 (e.g. [campaign-benchmarks](../../packages/Goldpath.Campaign/ops/campaign-benchmarks.md)). The monthly `bench.yml` is green ([36843126511](https://github.com/qorpe/goldpath/actions/runs/36843126511), 2026-10-01) but its numbers stay in a run artifact. R2 changes the release path | A | 3–5 ad | rc.1 |
| G14 | **Air-gapped install proven** | One GM shape runs generate → restore → build → `db bundle` → smoke with outbound network denied. It is fed only by a local NuGet feed of the release's packages and a local container registry. The steps become a runbook section | Claimed (SECURITY.md hardening notes, ADR-0008, foundation §11 *"the offline/proxy scenario is part of Template CI"*). The freshness gates skip honestly offline, and the [migrations runbook](../ops/migrations-runbook.md) §6 covers bundles. **No CI job runs anything offline** (no offline/mirror step in the workflows or `validate-gm.sh`) | A | 3–5 ad | preview.11 |

### 7.4 Security

| # | Criterion | (a) Proof | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G15 | **Threat model per module with an auth surface** | One STRIDE-shaped section per surface. Each threat maps to the test or analyzer that holds it, or to an accepted-risk line the owner signed. Surfaces: admin routes (fail-closed floor, R1 tenant scoping), the served console, **Campaign R2.8 signed callbacks** (HMAC, staleness window, [#277](https://github.com/qorpe/goldpath/pull/277)), Bulk upload + four-eyes, Approvals decisions (distinct-eyes, self-approval), FileExchange ingress (path, format, quarantine), Notification channels (credentials, template injection), DataProtection keys, Idempotency key scope, MultiTenancy isolation | **None exists.** No threat model in `docs/` or any `ops/` pack. The defences exist piecemeal (fail-closed admin, boundary masking + console re-masking, R2.8 signing). No CodeQL or ZAP job exists in `.github/workflows/` | A drafts, O reviews. **No paid pentest** (Q9): CodeQL + an OWASP ZAP baseline scan in CI, both green, with every finding fixed or carried as a signed accepted risk. A paid pentest only when a paying customer funds it | 5–10 ad + 1 owner day | preview.11 → rc.1 |
| G16 | **Dependency/CVE gate** (exists) **+ a patch-train path** | Exists: `ci.yml` runs `dotnet list package --vulnerable --include-transitive` and fails the PR, NU1902/NU1903 fail restore under TreatWarningsAsErrors, and the licence gate runs. To add: a documented patch-train path that gets a restore-breaking advisory to adopters within SECURITY.md's target, rehearsed once | Gate: **exists**. Path: the 2026-10-13 advisories broke every app generated from published preview.8. `main` was fixed 2026-10-19 ([#269](https://github.com/qorpe/goldpath/pull/269)), and adopters are still waiting on [#271](https://github.com/qorpe/goldpath/pull/271) on 2026-10-20 | A + O | 2 ad | rc.1 |
| G17 | **SBOM + provenance** (exists) | `release.yml` verifies its own attestations after publishing. SECURITY.md is version-neutral | **Exists** since preview.7 (F2, [#156](https://github.com/qorpe/goldpath/pull/156)): syft CycloneDX, `attest-build-provenance`, OIDC trusted publishing. The stale `preview.6` lines in SECURITY.md are fixed in this RFC's PR | A | 0.5 ad | rc.1 |

### 7.5 Documentation

| # | Criterion | (a) Proof | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G18 | **Docs: getting-started → production; upgrade guides; support policy page** | (1) A production runbook: deploy shape (`export compose` / K8s), bundles, auth floor and secrets, observability, backup/restore, worker scale-out, DR. Someone other than its author walks it once (ideally G19's adopter). (2) Every train's guide plus a consolidated `0.x → 1.0` guide. (3) A support policy page linked from the README. A public demo is **not** part of this gate: it is deferred to open-threads T41 (Q11) | [getting-started](../guide/getting-started.md) stops at a production pointer. Upgrade guides preview.1–9 exist. The support policy is spread over SECURITY.md and the versioning RFC. The `upgrade` skill is NOT BUILT (ai-sdlc §2) and stays optional | A + O review | 5–7 ad | rc.1 |

### 7.6 Business-owned

| # | Criterion | (a) Proof | (b) Today (evidence) | (c) | (d) | (e) |
|---|---|---|---|---|---|---|
| G19 | **At least one external adopter in production** (foundation §12, Phase 4 gate) | An organisation outside qorpe runs a Goldpath service in production for ≥4 weeks on a pinned train. It has taken ≥1 train upgrade through the published guide. A written reference exists; **an anonymous written reference is enough** (owner decision 2026-10-21, Q8) | **None.** api-portal and coexist are first-party: ADR-0012 calls a product module "the platform's permanent first adopter", not an external one. The factoring-class engagement is before its PoC (T20's rehearsal has not run) | **B** | unknown. Sales cycle, not engineering | rc.1 must exist to adopt. This is the longest pole |
| G20 | **Maintainer continuity / bus factor** | **Accepted 2026-10-21 (Q9): the zero-cost path, (a)-lite + (c)** — Apache-2.0 (already source escrow); a **second trusted owner** on the GitHub organisation, nuget.org and npm; a **written successor/transfer pledge** published in the repository; a **release runbook proven by someone other than the owner** (one train cut end to end; OIDC trusted publishing means there is no key to hand over). No commercial support offering and no paid partner for now. The options as proposed: **(a) Co-maintainer:** merge + release rights, with ≥2 owners on the GitHub org, nuget.org and npm. A second person cuts one train end to end (OIDC trusted publishing means there is no key to hand over). **(b) Support partner:** a written SLA that carries SECURITY.md's targets. **(c) Escrow-like guarantees:** Apache-2.0 already acts as source escrow. Add ≥2 registry owners, a published successor/transfer pledge, and a release runbook proven by someone other than the owner. (a)+(c) is the strongest | Solo. Versioning D3 says *"honest for a solo-maintained OSS asset"*, and SECURITY.md lists one human contact. Foundation §11's antidote to "Orphaning" (*"dedicated platform team"*) is not met | **B** | — | Before 1.0. A procurement review asks this on day one |

### 7.7 Summary

| Area | Gates | Exists today | Agent work | Owner / business |
|---|---|---|---|---|
| Contract | G1–G4 | ledgers + roll, schema-honesty | G1, G2, G3, G4 draft | G4 sign-off, accepting the adapter-package names (by merging the RFC revision) |
| Runtime | G5–G7 | seam + 8-fact suite | G5 (Wolverine) build + S1–S4/S6, G6, G7 build | — (Q1, Q3, Q7 decided) |
| Proof | G8–G14 | **G8 met**, GM nightly + PR subset, mutation gate, 3/7 scenarios, 5 bench tables | all except the rig thresholds | rig thresholds, the Mac left on for the overnight run |
| Security | G15–G17 | CVE gate, SBOM, provenance | G15 draft + CodeQL/ZAP, G16 path, G17 verify | G15 review and accepted-risk sign-off |
| Docs | G18 | getting-started, upgrade guides | G18 | review |
| Business | G19–G20 | — | G20 drafts (pledge, runbook) | **both** |

Rough agent total: **≈ 100–170 agent-days** of serial work, with large parallel slices (samples,
scenarios, threat models). The two business gates have no engineering estimate. Budget: **zero**
(Q11) — every gate closes on free tooling and free hardware, and where free hardware caps a
number, the number says so.

## 8. DoD — sequencing, critical path, owner decisions

### 8.1 Accepted train sequence

Accepted 2026-10-21 with the owner's answers. preview.9 shipped 2026-10-20 ([#271](https://github.com/qorpe/goldpath/pull/271));
#272/#273/#275/#276/#277/#278 are on `main` for preview.10. Each train's GitHub milestone carries
its issues and repeats this mapping in its description. There is no external date (Q10): the
gates govern, and the milestone dates are targets.

| Train (milestone, target) | Content (gates) | Exit |
|---|---|---|
| **preview.9** | The repair train and the security lift (#269), MassTransit 8.5.11 | shipped 2026-10-20 |
| **preview.10** (`0.1.0-preview.10`, 2026-11-10) | Campaign R2 hardened (#277), GM at PR time (#273) **+ branch protection (G8, met 2026-10-21)**, mutation scope + margins (#275/#278, then G9 part 1), console link tiles (#272), authoring Slice 0 (#276), local scenarios (G10a), T1 (G11), the Oracle RFC and first build parts (G7, #305/#287). **The critical path starts here:** the engine-adapter RFC revision and the ratcheting PublicAPI engine check (G1, opening). The 30M rig spec co-written with the owner (G10c spec). **Owner decisions Q1–Q12: recorded 2026-10-21** | GM green at PR time; protection on; the engine check holds the 13 lines from growing |
| **preview.11** (`0.1.0-preview.11`, 2026-12-01) — *the breaking train* | Engine-free API (G1, with an upgrade guide), PublicApiAnalyzers 5.x (G2), Oracle IN (G7), schema warnings (G3), **the Wolverine adapter through S1, S2, S3, S4 and S6 (G5, by 2026-12-31 at the latest)**, offline GM shape (G14), CodeQL + ZAP and the threat models (G15), remaining mutation margins (G9) | G1 check at zero outside the adapter packages; G5 proofs linked in T18. If the train slips past 2026-12-01, G5's date does not move |
| **1.0.0-rc.1** (`1.0.0-rc.1`, 2027-01-15) | The API review applied (G4: renames, removals, `[Experimental]` on Approvals + FileExchange), console OIDC (G6), docs set (G18), bench re-baseline (G13), S-FLEET-01 (G10b), CorPay core cells (G12 part), patch-train rehearsal (G16), verify-after-publish (G17). **From here PublicAPI is additions-only** | every A-owned gate closed except G12's sectors and G10c's run |
| **1.0.0-rc.2** (`1.0.0-rc.2`, 2027-02-28) | Insurance + Telco + Factoring (G12) as the exam, S-FLEET-02 on the zero-cost rig (G10c), and whatever they force | no open high/critical security finding; GM green |
| **1.0.0** (`1.0.0`, no date) | G19 and G20 met. The rc soaked ≥4 weeks at the external adopter. G19 is a business clock, so this milestone carries no due date | the T35 row closes |
| **family bus** (`family-bus`, 2027-03-31) — *off the 1.0 path* | The committed Goldpath-family bus timeline (T40): RFC + qorpe repository December 2026 → first adapter January–February 2027 → ≥4-week shadow lane (S5) → opt-in in a 1.x minor → default only in 2.0. S1, S4 and S6 bind it exactly as they bind Wolverine | S1–S4 + S6 green and the shadow lane run; the opt-in ships in the first 1.x minor after it |

### 8.2 Critical path

1. **G8 first** — done 2026-10-21. Every later gate is now enforced by a required check.
2. **G1 → G5, against the calendar.** The engine-free API is the prerequisite for S1, so it goes
   first: its RFC revision and the ratcheting check in preview.10, the break in preview.11. The
   Wolverine adapter then has to pass S2/S3 **and** S1/S4/S6 **before the 8.x window closes
   (2026-12-31)**. That leaves about ten weeks, which fits Wolverine (≈1-week adapter, plus the
   wire-compatibility and rollback proofs). The family bus no longer competes for this window:
   it has its own timeline (T40) and reaches adopters only as an opt-in.
3. **G1 + G2 + G7 → G3 → G4 → rc.1.** Nothing can be frozen until the engine types are out,
   the analyzer is current, and the manifest enum is decided (Oracle IN) and honest.
4. **rc.1 → G19 soak → 1.0.** The external adopter is the longest pole, and it is not an
   engineering task. Engineering makes rc.1 available early so the business clock can start.

### 8.3 Owner decisions (2026-10-21)

The owner answered every question in chat on 2026-10-21. The recommendation column is kept, so
a reader can see where the decision followed it and where it went further.

| Q | Question | Recommendation (2026-10-20) | Owner decision 2026-10-21 |
|---|---|---|---|
| Q1 | Which second adapter must pass S2/S3 by 2026-12-31: the family bus (option F's intent) or Wolverine (option D, the measured fallback)? | **Wolverine as the 1.0 insurance**, with the family bus continuing on its own timeline, off the 1.0 path. ADR-0013 allows both; the suite makes the adapter cheap to try | **Wolverine** is the 1.0 insurance and must pass S2/S3 by 2026-12-31 (G5). The Goldpath-family bus is **also committed** and must not be dropped or silently deferred: family-bus RFC + qorpe repository December 2026 → first adapter January–February 2027 → ≥4-week shadow lane (S5) → opt-in in a 1.x minor → default only in 2.0. It has its own ledger row (T40) and milestone (`family-bus`). **Zero adopter impact is a hard requirement for ANY engine switch**: S1 (apps compile unchanged), S4 wire compatibility — **now mandatory; the drained-cutover escape in [messaging-exit](goldpath-messaging-exit.md) §7.1 S4 is removed** — and S6 (rollback both ways). The same holds for the Wolverine adapter. Recorded in the constitution as [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) |
| Q2 | Does 1.0 lock the **default** engine for 1.x? D2 counts a default flip as breaking, so the family bus would become the default in 2.0, or ship as opt-in in a 1.x minor | Accept: opt-in in 1.x, default in 2.0 | **Yes.** 1.0 locks the default engine for 1.x; a new engine is opt-in in 1.x and the default in 2.0. Recorded as [ADR-0015](../adr/ADR-0015-messaging-engines-zero-adopter-impact.md) §3 |
| Q3 | Is Oracle in or out of 1.0 (G7)? Does coexist's Oracle work fire T16's trigger, and is the FUTC licence-gate exception acceptable? | Decide before preview.11. Without a committed external Oracle adopter: **OUT**, annotated | **IN**, as a first-class provider (decided 2026-10-20: Turkish enterprise adopters run Oracle). G7 = the scoped licence-gate exception, the GM Oracle shape, and D7 migration + Quartz store + keyset proofs on a real Oracle container. The RFC is [#305](https://github.com/qorpe/goldpath/issues/305) |
| Q4 | Modules without their adopter proof (Approvals T21, FileExchange T22): freeze them at 1.0, mark their surface `[Experimental]`, or hold them off the train? D1 lockstep rules out per-package versions | **`[Experimental]`**, lifted in a 1.x minor once T21/T22's proofs run | **As recommended:** both ship in 1.0 with their surface `[Experimental]`, lifted in a 1.x minor once T21/T22's proofs run |
| Q5 | Mutation margin for GA: ≥5 points (75%), or the current break 70 plus "margin under 2 is an invitation"? | 75% for GA, with no lowered break and no unledgered exclusion | **As recommended:** 75% (break 70 + 5), no lowered break, no unledgered exclusion |
| Q6 | Must both Insurance **and** Telco exist before 1.0, or Insurance + CorPay cells, with Telco (the variant sample: sqlserver/apikey/subdomain/redis-lock) in 1.x? | Both, per the master plan's "full-set exam". If the calendar binds, Telco's variant cells move to GM-only proofs and Telco to 1.1 | **Insurance + Telco + Factoring** before 1.0, each from scratch and nightly-proven against the published train (G12). The Factoring sample is a **generic clean-room domain sample** in this public repository — no client, institution or engagement names or details |
| Q7 | Is console OIDC (T10) a 1.0 gate, given that its trigger (an adopter with split auth boundaries) has not fired? | Keep it a gate. A split-origin enterprise deployment is the common case, not the exception | **As recommended:** a 1.0 gate (G6) |
| Q8 | What counts as G19's external adopter: an engagement client's production service? How long a soak, and must the reference be named? | ≥4 weeks, ≥1 upgrade taken, an anonymous written reference is enough | **As recommended:** an organisation outside qorpe in production ≥4 weeks on a pinned train, ≥1 upgrade taken through the published guide; an anonymous written reference is enough |
| Q9 | Which continuity option (G20)? Is a commercial support offering intended, given that foundation §10 says "included in project fee"? Is an external pentest budgeted? | (a) + (c). Decide the support offering before rc.1 | **The zero-cost path:** Apache-2.0 + a second trusted owner on the GitHub organisation, nuget.org and npm + a written successor/transfer pledge + a release runbook proven by someone other than the owner ((a)-lite + (c)). **No paid pentest now:** free CodeQL + OWASP ZAP in CI + the G15 threat models; a paid pentest only when a paying customer funds it. **No commercial support offering** for now |
| Q10 | Version path: straight from `0.1.0-preview.N` to `1.0.0-rc.1`, or 0.2/0.3 minors first? Is there an external date (a client's procurement) to plan backwards from? | Straight to rc.1. Previews already carry guides | **As recommended:** straight from `0.1.0-preview.N` to `1.0.0-rc.1`; no external date — the gates govern |
| Q11 | The S-FLEET-02 rig: who funds the hardware, and what thresholds count as a pass? | Co-write the spec in preview.10 so the hardware lead time does not land on the critical path | **Zero budget.** The rig runs the **full 30M dataset** — no down-scaled run. Hardware: the owner's Mac, overnight, in a dedicated Lima VM `bench` (separate from the `ci` runner VM; the owner leaves the Mac on when a slice is ready to measure) for the full 30M run; GitHub-hosted free runners (public repository, 4 vCPU / 16 GB, 6 h job cap) as the repeatable lane, also on the full 30M dataset. If free hardware cannot sustain 2,000 TPS, the published result states the hardware and the measured ceiling honestly (TPS per core). Thresholds are co-written with the owner before the run. **No Oracle Cloud account** (its Always Free A1 allowance is reported halved to 2 OCPU / 12 GB from 2026-08-18) and **no public demo subdomains for now**: the demos — one subdomain per product through a free Cloudflare Tunnel, qorpe.com being on Cloudflare — are deferred to a written trigger, the owner getting an always-on machine (for example a Mac mini), when the demos and the rig move there (T41). Not a 1.0 gate |
| Q12 | Branch protection for a solo maintainer: require PR + checks with "include administrators", but no required review? | Yes. Checks bind the owner too, and reviews stay the local `review-agent.sh` path (T4) | **Done 2026-10-21:** `main` requires a PR and the checks `merge-policy`, `gates`, `integration`, `kit` and `golden manifests (PR subset)`, administrators included, no required review (G8 met) |

**Why the public demos are a deferred thread and not a gate.** A demo is an adoption aid, not a
property of the library an adopter installs: nothing in §4's promises depends on it, and an
always-on public endpoint on hardware that sleeps would promise what it cannot keep. G18's
production runbook still gets its independent walk (item 1); the demos wait for an always-on
machine (T41).

### 8.4 This RFC's own DoD

- [x] Owner reviews; Q1–Q12 answered; status → ACCEPTED (2026-10-21).
- [x] Each accepted gate is mirrored in its own open-threads row where one exists, and T35 points
      here (T16, T18 and T35 updated 2026-10-21; the family bus has its own row, T40).
- [x] G8 on (protection + #273) — the first gate, and the one that makes the rest enforceable.
      #273 merged 2026-10-20; the five required checks are on since 2026-10-21.
- [ ] The gate table is re-measured at every train boundary until 1.0; a gate leaves it only when its proof has run.
