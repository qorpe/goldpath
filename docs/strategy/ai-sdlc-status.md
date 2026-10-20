# AI-assisted SDLC — coverage & status

Status: LIVING DOCUMENT (updated in the same PR whenever a row's status changes — the
same discipline as `coverage-matrix.md`). First cut 2026-07-23.

`foundation.md` §6–§8 defines the target: a command-driven lifecycle where design,
analysis, development, test, DB work, release and operations are all AI-assisted and
**machine-validated** — zero-trust toward AI output, every claim gated by a proof.
This document tracks where reality is against that target, honestly. Statuses:

- **SHIPPED-PROVEN** — exists and has been exercised end-to-end with recorded evidence.
- **SHIPPED-UNPROVEN** — ships today, but no recorded end-to-end run exists yet.
- **PARTIAL** — some of the target exists; the rest has a listed gap.
- **NOT BUILT** — nothing exists; phase/trigger noted where the deferral is written.

A row may only move to SHIPPED-PROVEN with a pointer to its evidence (transcript,
nightly job, or test). Claiming above the real status is the one failure mode this
document exists to prevent.

## 1. Lifecycle stages vs. reality

| Stage | Target (foundation) | Today | Deterministic gate today | Gap → next artifact |
|---|---|---|---|---|
| Design / analysis | `authoring` + `spec-review` skills: business language → manifest + spec (§6.1) | **NOT BUILT** — manifests are hand-authored. The plan is [goldpath-authoring](../rfc/goldpath-authoring.md), **PROPOSED** 2026-10-20; Slices 1–3 wait for acceptance. **Slice 0, the no-LLM floor, LANDED** the same week: specanchor's rule/ledger-term/spec schemas pinned with a freshness gate, the `decisions` schema, corpora for all four, `goldpath spec ready` and `goldpath add feature --plan`. That is the deterministic half of the stage, with nothing yet to drive it. See the honesty note under §2 | `specdrift validate` / `spec_drift` (schema + rules); `goldpath spec ready` (artefact shape, sources, ids, gate #1) + `corpus-check.sh` + `specanchor-freshness.sh` for the authoring artefacts | `authoring` skill + `spec-review` agent — the RFC's Slices 1–2 (open-threads T34) |
| Composition | manifest as the single source of truth | `goldpath-manifest` skill **SHIPPED-UNPROVEN**; CLI + schema + GM matrix **SHIPPED-PROVEN** (every shape nightly; since 2026-10-20 the covering subset also runs on every PR that touches what a generated app is built from — T30) | GM matrix (PR subset + nightly) + specdrift | drive the skill in a recorded run |
| Development | `new-service` / `add-feature`: spec → code with tests (§7) | `goldpath new`/`add worker` deterministic scaffolding **SHIPPED-PROVEN** (GM nightly); `goldpath-feature` skill **SHIPPED-PROVEN** — it drove the five CorPay S2 slices (`coverage-matrix.md` tooling table); the `goldpath add feature` CLI verb is **SHIPPED-PROVEN** by the nightly `GmGrown` shape (a lean app grown by five recipes — audittrail, softdelete, locking, bulk, outbox — then migrated, built, drift-checked and smoked; GREEN on the 2026-09-05 dispatch after the four recipe bugs it found were fixed); the Insurance sample remains its field proof | build + analyzers (42 rules) + PublicAPI ledger + `dotnet format` | field the CLI verb (coverage-matrix plans it for the Insurance sample) |
| DB | migrations discipline driven by CLI verbs | `goldpath db` verbs + D7 proofs (`validate-migrations.sh` on real pg) **SHIPPED-PROVEN** | migration bundle CI step + GP1801 | — |
| Test | `test-gen` (never sees the implementation, §8.2) + breaker + property-based + mutation | `goldpath-test-gen` skill **SHIPPED-PROVEN** — fielded on CorPay 2026-07-23: 8 spec-derived paging/tenancy/state tests + a 7-item gap report (`samples/corpay/tests/SPEC-GAPS.md`), diet held (no `Features/` reads; public seams via compile probes); `breaker` agent **SHIPPED-PROVEN** — same run, own context: 15 `Breaker_` tests, 14 attacks repelled, **1 genuine finding** (sub-cent amount accepted — fixed in the same PR; `samples/corpay/tests/BREAKER-VERDICT.md`), breaker eval 3/3 PASS; mutation gates **SHIPPED-PROVEN** (all 22 gated packages nightly since 2026-10-20, each scored by its own suites; `mutation-heavy.yml` re-scores the six long-running ones on demand); property-based **PARTIAL** (CsCheck present, not yet the §8.3 catalog-driven norm) | Stryker break=70 + test projects in CI | edge-case catalog is Phase 2 (domain memory) |
| Review / validation chain | §6.4: schema → build → analyzers → arch tests → contracts → tests → mutation → review agent → human | Chain **SHIPPED-PROVEN** up to mutation; review agent **SHIPPED-PROVEN as a manual script** (`scripts/review-agent.sh` — three hard findings caught and fixed on the R1/A3 PRs alone); in-loop gating **SHIPPED** (hooks, 2026-07-23): the agent cannot end a turn on a red build, and since specdrift 0.4.2 the drift leg gates at warn; the CI step **LANDED 2026-08-03** (`review-agent.yml` gates on hard-stop findings, contract-hardened runner) | CI gates on PR + nightly + in-loop hooks | the CI step waits on the `ANTHROPIC_API_KEY` secret (owner decision — T4's remainder) |
| Skill quality (evals) | eval set per skill; a skill that fails evals is not released (§6.2) | **PARTIAL** — as of 2026-07-24 the 4 acceptance runners are portable (published specdrift tool, no local-checkout assumption; failures print their output) and the deterministic half runs nightly (`evals-acceptance`: runner syntax + breaker vs CorPay). The LLM half — running the skills themselves per fixture — stays deferred until an agent-in-CI story exists (P2) | nightly `evals-acceptance` job | agent-in-CI story (P2) unlocks full skill-run evals |
| Model proficiency matrix | skill × model → pass-rate matrix (§6.2) | **NOT BUILT** — Phase 2 (needs the eval lane first) | — | after the eval lane |
| Release / DevOps | release train + delivery telemetry (§6.5) | Train **SHIPPED-PROVEN** (OIDC trusted publishing, license gate, roll script); delivery report **NOT BUILT** — Phase 2 | release workflow + license gate | — |
| Operations | admin APIs + ops packs + console (§7.1) | Admin APIs + dashboards + runbooks **SHIPPED-PROVEN** per module (every shipped module carries `ops/` since 2026-08-03 — the Idempotency/AuditTrail/SoftDelete runbooks closed the last three gaps, honest-signals posture: no unshipped metric is cited); console **SHIPPED-PROVEN** (U1–U9 met 2026-08-03: cross-service registry, TODAY triage with stat cards, the full family standards ui-standard v1.3 §7–§9, contract R3 OR filters, `MapGoldpathConsole()` with canonical-root redirect — 44 smoke tests against three real apps — the journey, served-console, screenshot and axe specs, counted by the run itself (2026-09-05: the approval worklist journey and the Approvals/File rails axe scans joined; the panel count the axe spec walks is eight) and the app-SERVED console; the OIDC login remains ledgered as T10; **T1 is still OPEN**: CorPay's nightly asserts that the console is SERVED and inherits the floor (401 on the secured head, 200 on the internal head's visible opt-out) — it does not DRIVE it. This row claimed the proof was riding since 2026-09-03; the coverage audit of 2026-09-05 found the two ledgers disagreeing and this is the one that was wrong); **product-console link tiles (#257): on `main`, unpublished** (2026-10-19 — `AddLink` + a `links` array beside `services`, tiles on Today; the smoke clicks a tile to arrival, proves a links-only app shows no registry warning, and axe-scans the SERVED Today with tiles; the adopter proof waits for the train: open-threads T31) | admin contract frozen + integration proofs | console smoke 44 tests + axe |
| Pipeline (chained skills + human gates) | §6.1 level 3 | **NOT BUILT** — Phase 2 by written deferral (§12). Maintainer side only (not the adopter pipeline this row tracks): an overnight routine pipeline (planner → worker → independent verifier → morning report) opens `agent-pr` pull requests in THIS repository; its merge rule is [ADR-0014](../adr/ADR-0014-agent-verified-changes-merge-automatically.md) (**PROPOSED** 2026-10-20) — the routines live outside the repository and no agent pull request has run through `merge-policy` yet, so that leg is SHIPPED-UNPROVEN | `merge-policy` (tier from paths; fails Tier C + `agent-pr` without `owner-approved`) + its self-test in `ci.yml` | an agent pull request merged under the policy and listed in a morning report; Tier C by identity — open-threads T38 |
| Domain memory | project knowledge that grows with every run (§6.3) | **NOT BUILT** — `domain-memory-v1.md` is a v0.1 draft. The solution template and CorPay now carry an empty `docs/domain/` skeleton (authoring Slice 0, held identical by `skills-parity.sh`); nothing fills it yet | `goldpath spec ready` checks any card placed there | the first cards: authoring Slice 1 on CorPay (open-threads T34) |

## 2. The §6.1 skill set, name by name

| Skill (foundation §6.1) | Status | Where |
|---|---|---|
| `authoring` | NOT BUILT (Phase 2) — RFC PROPOSED 2026-10-20 | [goldpath-authoring](../rfc/goldpath-authoring.md) (Slice 1) |
| `spec-review` | NOT BUILT (Phase 2) — RFC PROPOSED 2026-10-20 | [goldpath-authoring](../rfc/goldpath-authoring.md) (Slice 2) |
| `new-service` | covered deterministically | `goldpath new` (CLI — no LLM needed, by design) |
| `add-feature` | **SHIPPED-PROVEN** (drove CorPay S2) | `.claude/skills/goldpath-feature` |
| `test-gen` | **SHIPPED-PROVEN** (fielded on CorPay 2026-07-23 — `samples/corpay/tests/SPEC-GAPS.md`) | `.claude/skills/goldpath-test-gen` |
| `breaker` | **SHIPPED-PROVEN** (same run, 1 genuine finding fixed — `samples/corpay/tests/BREAKER-VERDICT.md`) | `.claude/agents/breaker.md` |
| `reverse-engineer` | NOT BUILT (transformation package, §9) | — |
| `differential-test` | NOT BUILT (transformation package, §9) | — |
| `docs-sync` | NOT BUILT | — |
| `upgrade` | NOT BUILT (first real consumer: a preview→preview migration) | — |
| *(extra, not in §6.1)* `goldpath-manifest` | SHIPPED-UNPROVEN | `.claude/skills/goldpath-manifest` |

3 skills + 1 agent ship in the template pack today. Three are now fielded in anger:
`goldpath-feature` drove the five CorPay S2 slices; `goldpath-test-gen` and `breaker`
ran on CorPay 2026-07-23 (spec-derived tests + gap report; adversarial run with one
genuine finding, fixed). `goldpath-manifest` remains unfielded (`coverage-matrix.md`
plans it for the Insurance sample).

**Honesty note (2026-10-20, [goldpath-authoring](../rfc/goldpath-authoring.md) D9).** The
CHANGELOG entry for Skills v1 says "the Phase 2 gate is MET". What the `goldpath-feature` eval
proved is **sentence → code**: its input sentence was already decomposed by a human, the same
agent wrote the contract it then implemented, and no business-approved spec existed for
ADR-0010's gate #1 to approve. The spec-gated form that foundation §3.2 and §7 describe
(sentence → approved spec → code) is **not met**. The CHANGELOG stays as written, because it is
history; this row, the skills-v1 RFC and the authoring RFC carry the correction.

## 3. Mechanism inventory — what carries the AI layer

| Mechanism | Role | Status |
|---|---|---|
| CLAUDE.md family + `conventions.md` | passive context | SHIPPED (template) |
| Skills | active recipes | PARTIAL — three fielded, one not (see §2) |
| MCP (`specdrift mcp`) | deterministic tools in the AI's hand | SHIPPED (`spec_validate`, `spec_drift`) |
| Hooks | unskippable in-loop gates | **SHIPPED** (template + CorPay, 2026-07-23): post-edit whitespace format on touched `.cs`; Stop gate blocks a red `dotnet build` and error-level `specdrift drift` findings. Since specdrift 0.4.2 the drift leg gates at WARN (`--fail-on warn`) — the OR-semantics fix killed the false-positive noise that made that impossible |
| Evals | skill regression tests | PARTIAL — runners portable + nightly deterministic lane; full skill-run evals await agent-in-CI (P2) |
| Plugin packaging | install the layer into an *existing* app | NOT BUILT (template-only distribution today) |

## 4. The near-term path (ordered)

1. **Proof runs** — DONE for `goldpath-test-gen` + `breaker` (CorPay, 2026-07-23; see
   the Test row). Remaining: `goldpath-manifest` and the `add feature` CLI verb
   distribute to the next samples per `coverage-matrix.md`. Follow-ups the run itself
   surfaced: contract-export completeness is FIXED at the seam (Mediant 1.4.0 stamps
   request metadata + Accepts; ApiDefaults projects query properties into documented
   parameters — `ContractExportTests`); CorPay's re-export rides the next train. And the CorPay
   SPEC0203 noise is DIAGNOSED as wiring-row OR-semantics (issue #11's written trigger
   fired 2026-07-24); LANDED in specdrift 0.4.2 (OR-semantics + `--fail-on` + embedded schema).
2. **Hook set in the template** — DONE 2026-07-23 (`.claude/settings.json` +
   `.claude/hooks/` in the template and CorPay): post-edit whitespace format; stop-gate
   `dotnet build` + `specdrift drift`. The §6.4 chain now runs *inside* the AI's turn —
   an agent cannot end its turn on a red build. The 0.4.2 follow-up LANDED: the stop-gate drift leg now gates at warn.
3. **Portable evals + nightly lane** — DONE 2026-07-24: `accept.sh` runners use the
   published specdrift tool (no `$HOME` checkout assumption) and print failing output;
   nightly `evals-acceptance` runs the deterministic half (syntax + breaker vs CorPay).
   Same PR closes the other half of the audit's Y10: `validate-gm.sh` now honors its
   pin (v0.4.1) — a local checkout is used only via an explicit
   `GOLDPATH_SPECDRIFT_SRC`, and announces itself when it is.
4. **CLI verbs as MCP tools** — typed `goldpath_*` tools next to specdrift's, so skills
   stop shelling out. Shaped by what the proof run shows is actually needed.
5. **Plugin packaging** — one installable unit (skills + MCP + hooks) for existing apps.
   Only after 1–3 mature; packaging unproven content distributes the wrong thing.

Phase 2 items (pipeline, authoring, delivery report, model matrix, domain memory) keep
their written gates in `foundation.md` §12 — this document does not re-plan them, it
only tracks when their triggers fire.
