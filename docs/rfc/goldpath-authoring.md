# RFC: Goldpath Authoring — a business sentence becomes an approved spec before it becomes code

> Status: **PROPOSED** (2026-10-20). The owner decided the following the same day (§9, "Owner
> decisions"): land as PROPOSED, **start Slice 0 now** (the deterministic floor, no LLM), D3 as
> warn-then-refuse, holdout fixtures in a private repository, and D9's honesty note. Slices 1–3
> wait for this RFC's acceptance. D1, D2, D4–D8 and the remaining open questions (§11) are
> still open. Tracked as `open-threads.md` T34.
> Constitution grounding: ADR-0001 (the manifest is truth: authoring PROPOSES a manifest
> change, it never half-applies one), ADR-0002 (OpenAPI 3.1 fragments and specanchor's JSON
> Schemas, no new spec DSL), ADR-0004 (the LLM proposes, the deterministic engine validates;
> §2 states the side of every step, as the ADR's consequence requires), ADR-0005 (§5 and §7
> are the verifiers), ADR-0007 (no eval, no release), ADR-0010 (this RFC places gate #1, it
> does not move it). Composes with [goldpath-skills-v1](goldpath-skills-v1.md) (the skill
> layer this joins) and [goldpath-delivery-cycle-v1](goldpath-delivery-cycle-v1.md) §5/D6
> (the rule/term/spec format this writes).
> Phase: foundation §12 Phase 2: "a feature flows from a business sentence to an MR in a
> single session".

---

## 1. Scope / Non-Goals

### 1.1 The gap, with evidence

| Claim | Evidence |
|---|---|
| `authoring` and `spec-review` do not exist | `docs/strategy/ai-sdlc-status.md` §1 Design row ("NOT BUILT — manifests are hand-authored") and §2 |
| The Phase 2 gate was declared MET in a narrowed form | `CHANGELOG.md:790` ("the Phase 2 gate is MET") rests on the `goldpath-feature` eval (`goldpath-skills-v1.md` §2, DoD 9/9). That same RFC lists "analyst-facing NL spec authoring" as a written non-goal (§1). The gate met was **sentence → code**. The **sentence → approved spec → code** chain that foundation §3.2 and §7 describe was not met |
| The implementing agent interprets the sentence AND writes the contract it then implements | `goldpath-feature/SKILL.md` step 2 ("contract before code", but the same agent writes the contract). Foundation §8.2's "AI cannot grade its own homework" is enforced for tests (test-gen diet, breaker) and nowhere at the spec level |
| Gate #1 (spec approval, business) has nothing to approve | ADR-0010 names the gate. Foundation §3.2 says "the generate skill does not run without `approved`". No skill checks for an approved spec, because no approved-spec artefact exists |
| Business rules leave no durable record, so the tester hits a dead end | `samples/corpay/tests/SPEC-GAPS.md` G6 ("state machine is implicit … whether Rejected is terminal, whether re-submit after reject is legal"), G3/G7 (the manifest enables idempotency/auth and the contract says nothing). `goldpath-delivery-cycle-v1.md` §5 calls the rule layer "the one we do not have" |
| `goldpath-manifest` is not authoring | `goldpath-manifest/SKILL.md` takes a TECHNICAL instruction ("Enable soft delete" — `evals/skills/goldpath-manifest/scenario.md`), names consequences and wires the change. It never derives a decision from business language. It is unfielded (`coverage-matrix.md:60`), and the deterministic `goldpath add feature` verb already does its wiring with an engine round-trip and full rollback (`tools/Goldpath.Cli/AddFeatureCommand.cs`) |
| Domain memory is absent | ai-sdlc-status §1 Domain-memory row (NOT BUILT). No `docs/domain/` in the template or CorPay. The D6 rule/term/spec chain is not staged yet (`open-threads.md` T27, "Remaining: … then D6's spec chain staged") |

**Where a human does the translation today.** The owner writes the scenario cards by hand
(`docs/scenarios/*.md`). From them the owner decides the manifest (wizard / `goldpath new`
flags / `goldpath add feature`) and rewrites the business story as a developer-ready sentence.
The `goldpath-feature` eval input is that rewrite: "A customer can cancel an order that has not
been confirmed yet. Cancelling a confirmed order must be rejected…" is already decomposed. It
has states, a transition and a rejection rule. Authoring is that decomposition, made explicit,
checkable and approvable.

### 1.2 Scope

1. **`goldpath-authoring` skill.** It reads a business sentence or a scenario-card slice plus the
   app's manifest, glossary and approved rules. It emits a **change proposal**: rule cards,
   proposed glossary terms, one spec card (acceptance criteria), a proposed manifest, a
   decision record and an OpenAPI 3.1 fragment. Every uncertainty becomes a question, never a
   guess.
2. **`spec-review` agent.** It is adversarial and runs in its own context (the breaker
   pattern). It reads only the proposal and its sources, and it succeeds the day it finds a
   guess, a contradiction or a missing edge.
3. **The deterministic support both stand on** (§5): pinned artefact schemas, a `spec-ready`
   check, a recipe `--plan` dry-run, and a contract-containment check.
4. **The eval lane** for both (§7), and a **step 0 for `goldpath-feature`**: it starts from an
   approved spec.

### 1.3 Non-goals (written, not silent)

- **No code, no generated artefacts.** Authoring never writes handlers, DTOs, migrations or
  the committed `specs/<App>.Api.json`. That is `goldpath-feature` plus the build's export
  (SPEC0212).
- **No manifest wiring.** Authoring proposes and does not apply. Applying is `goldpath add
  feature` (deterministic) or `goldpath-manifest`, after gate #1.
- **No replacement of a human gate.** spec-review is a checker, not an approver. Gate #1
  stays a human MR approval (ADR-0010).
- **No new spec format.** The behaviour layer is specanchor's `rule`/`ledger-term`/`spec`
  (delivery-cycle D6). The contract layer is OpenAPI 3.1. The one new artefact
  (`decisions.yaml`) is a decision record about the binding layer. It is not a spec of
  behaviour (D4).
- **v1 does not author events (AsyncAPI) or worker triggers.** spec-engine-v1 §1 defers
  AsyncAPI. Workers' contracts are C# records (`goldpath-event-contracts`). A sentence that
  needs an event yields a QUESTION in v1.
- **No new apps.** Card → `goldpath new` via the wizard's `Derive` answers
  (`WizardCommand.cs:148`) is a v2 trigger (§9). v1 changes an existing app.
- **v1 does not decide providers or external systems.** They change infrastructure. v1 raises
  them as questions only (D5).
- **No reverse engineering.** That is specanchor's discovery chain (foundation §9, T20).
- **No runtime AI.** This is the dev layer (ADR-0006). `Goldpath.Ai` (ADR-0011) is unrelated.

## 2. Seam Map — where authoring sits, and which side of ADR-0004 each step is on

```
sentence/card ─► goldpath-authoring ─► proposal ─► spec-review ─► (revise ≤2 rounds) ─► spec MR
                 [agentic]              │          [agentic, own ctx]                    │
                                        ▼                                                ▼
                    specdrift validate + spec-ready [deterministic]       GATE #1 analyst approves
                                                                                         │
 implementation MR ◄─ containment ◄─ build export ◄─ goldpath-feature ◄─ goldpath add feature
   GATE #2           [deterministic]  [deterministic]  [agentic, step 0]   [deterministic]
```

| Step | Side | What it may touch |
|---|---|---|
| Interpret sentence → rules, terms, criteria, manifest decisions, contract fragment | **agentic** (authoring) | `docs/domain/<bc>/**`, `specs/proposals/<SPEC-id>/**` only |
| Allocate `RULE-`/`SPEC-` ids | **deterministic** (`spec-ready --next-id`). An LLM never picks an id | — |
| Artefact shape, manifest-proposal validity, open-bracket count, id uniqueness | **deterministic** (specdrift `validate` with pinned schemas + rules; `spec-ready`) | read-only |
| Domain opt-ins a feature drags in (authority chains, rails, retention…) | **deterministic** (`goldpath add feature <x> --plan` prints the recipe's `NextSteps`, `FeatureRecipes.cs:106`). Authoring turns each into a question | read-only |
| Find guesses, contradictions, missing edges | **agentic** (spec-review, own context) | `SPEC-REVIEW.md` in the proposal |
| Approve | **human** (gate #1) | MR approval + `approved_by` |
| Apply the manifest decision | **deterministic** (`goldpath add feature`, engine round-trip, rollback on findings) | csproj/Program.cs/manifest |
| Implement | **agentic** (`goldpath-feature`) | code + tests; tests cite `RULE-` ids (cycle §4) |
| Did the build implement what was approved? | **deterministic** (containment: built OpenAPI ⊇ approved fragment, §5 V4) | read-only |

**Why proposals live outside the drift pair.** The template's stop gate runs `specdrift drift
--fail-on warn` (`templates/goldpath-solution/.claude/hooks/stop-gate.sh:29`). A manifest feature enabled without its wiring
is a drift error. A committed `specs/*.json` edited ahead of the build trips SPEC0212. An
authoring session that wrote either would fail to end its own turn, which is correct. So the
proposal sits in `specs/proposals/`, which drift does not read. The real manifest and the real
contract change only in the implementation MR, by the deterministic verbs and the export.

## 3. Manifest Surface — what authoring may propose

No schema change. The proposal carries `manifest.proposed.yaml`: the WHOLE file after the
change. There is no patch format (ADR-0002), the diff is git's, and specdrift validates it with
the app's own schema + `.specdrift/rules.yaml` exactly as it validates the real one.

| Manifest area | v1 authoring may | Why |
|---|---|---|
| `features.*` (the 14 keys, schema `$defs/features`) | propose enable, or "already on — used as follows" | there is a deterministic applier (`goldpath add feature`) and a recipe plan to mine for opt-ins |
| `nfr` | propose values **only when the sentence states them** ("within 300 ms"); otherwise a question | pure data, no wiring; foundation §8.4: a load test without targets is forbidden |
| `providers`, `architecture`, `externalSystems`, `products` | **questions only** | infrastructure or ownership decisions with no deterministic applier in scope |
| disable anything | **never** — a removal surfaced by the sentence is a question | toggle-off has data-lifecycle consequences (`goldpath-manifest/SKILL.md`, "What NOT to do") |

**"Never guesses domain opt-ins", made mechanical.** The CLI already prints "Your decisions
(goldpath never guesses domain opt-ins)" (`AddFeatureCommand.cs:75`). Authoring runs `--plan`
for every feature it proposes and must turn each printed opt-in into either a question or an
answer cited from the sentence. The eval checks that the questions exist (§7, H5).

## 4. API Surface — the artefacts and the two agent contracts

**The proposal** (one folder per change; the rule and term cards outlive it):

```
docs/domain/<bc>/terms/<term>.yaml        ledger-term   (specanchor schema, status: proposed)
docs/domain/<bc>/rules/RULE-nnnn.yaml     rule          (confidence: inferred until a test pins it)
specs/proposals/SPEC-nnnn/spec.yaml       spec          (notation: gherkin|ears; open_brackets; approved_by)
specs/proposals/SPEC-nnnn/manifest.proposed.yaml
specs/proposals/SPEC-nnnn/decisions.yaml  one entry per manifest path touched or relied on: path, change
                                          (enable|set|rely|ask), source (file+lines or verbatim quote), opt-ins → question ids
specs/proposals/SPEC-nnnn/openapi.fragment.yaml   OpenAPI 3.1: only the new/changed paths + components
specs/proposals/SPEC-nnnn/SPEC-REVIEW.md  spec-review's verdict (written by the agent, never by authoring)
specs/proposals/SPEC-nnnn/run.json        telemetry-v1 `skill-run` shape (skill, model, outcome, rounds, questions)
```

**The forward adaptation, stated** (delivery-cycle §5): `source_ref` uses specanchor's
existing `{file, line_start, line_end}` branch (`rule.schema.v1.json` `$defs/sourceRef`),
pointing at the scenario card or at a committed `docs/requests/<date>-<slug>.md` that holds the
verbatim sentence. The schema stays unmodified. `confidence: inferred` until a test pins the rule.
`evidenced` then means the test that pins it, not a characterization test. `disposition`
stays human-only.

**Status mapping** (foundation §3.2 ↔ specanchor): draft = proposal on a branch · review =
`spec-ready` green and spec-review run · **approved = `approved_by` set AND `open_brackets: 0`
AND the spec MR merged by a CODEOWNER** · implemented = containment green on main.

**`goldpath-authoring` hard steps** (the skill restates no rule; it defers to `cycle.md`
steps 1–2 and to the documents named here):
1. Read the manifest, the glossary (`docs/domain/<bc>/terms`) and the approved rules first.
   A word missing from the glossary becomes a `proposed` term with an open question. The skill
   does not coin a code name outside the glossary without saying so.
2. One rule per behavioural claim in the sentence. Every rule carries a `source_ref` into the
   input. A rule without one is an invention and must not be written.
3. Whatever the sentence does not settle goes to `open_questions` (rule) or `[OPEN …]`
   (spec). Never into the statement.
4. For every manifest decision, run `--plan` and record the opt-ins (§3). Validate
   `manifest.proposed.yaml` with `spec_validate`.
5. Write the contract fragment from `conventions.md`'s error and status conventions, not
   from taste. Mark uncovered status codes as open.
6. Run `spec-ready`. Hand off to spec-review. Revise for at most 2 rounds. Findings still
   unresolved become `open_questions`; they are not dropped.

**`spec-review` agent** (`.claude/agents/spec-review.md`, own context). *May read*: the
proposal, its `source_ref` targets, the glossary, approved rules, the edge-case checklist
(`goldpath-test-gen/SKILL.md`, the v0 §8.3 catalog), `conventions.md`, the manifest. *Must not
read*: authoring's transcript or reasoning, or `src/**`. It checks the artefact against the
source, never the author's intent. Finding classes: **S1 invented** (a claim with no span in the
source) · **S2 folded ambiguity** (a statement asserts what the source leaves open) · **S3
guessed opt-in** (a manifest decision or opt-in answer with no source) · **S4 contradiction**
(with an approved rule or an existing contract) · **S5 missing edge** (an applicable checklist
anchor with no criterion and no "n/a because") · **S6 glossary** (a code name outside the
ledger) · **S7 scope** (touches a decided non-goal, e.g. saga/rules engine, `master-plan-2026-08`).
Each finding names the `RULE-`/`SPEC-` id or the decisions path. "None found" lists what was
attacked, as the breaker's verdict does.

**`goldpath-feature` step 0** (D3): when the change carries a business rule, the skill starts
from an approved `SPEC-` id, implements against its fragment, and cites `RULE-` ids in tests.
A raw sentence that carries a rule is routed to authoring first. **Decided 2026-10-20:** in v1
the skill WARNS when a rule-carrying change has no approved spec and proceeds; in v2 it
REFUSES.

## 5. Verifiers (ADR-0005) — the deterministic gates, with ids

| Id | Gate | Where | Proof it can fail |
|---|---|---|---|
| **V1** | Artefact shape: every `rule`/`ledger-term`/`spec` validates against specanchor's v1 schemas, **pinned** into `schemas/specanchor/v1/` with a freshness gate (the `train-freshness`/`kit-freshness` pattern). `decisions.yaml` validates against `schemas/authoring/v1/`. specdrift `validate` is the engine. The three specanchor schemas use only keywords inside specdrift's evaluator subset (checked: `format`/`exclusiveMinimum` appear only in `comparison-policy`) | app CI + `spec-ready` | a corpus `valid/`+`invalid/` pair per schema, as `schemas/manifest/v1/corpus` does |
| **V2** | `manifest.proposed.yaml` passes `spec_validate` (schema + rules) | `spec-ready` | a proposal enabling `outbox` with broker `none` → SPEC0101 |
| **V3** | `spec-ready`: ids unique repo-wide (catches two branches allocating the same `RULE-`), every `source_ref` resolves to existing lines, `rule_ids` in specs exist, every `decisions.yaml` entry cites a source, and, **for gate #1 only**, `open_brackets == 0` and `approved_by` ≠ the MR author | app CI on the spec MR and on main | planted: duplicate id, dangling source line, self-approval |
| **V4** | Containment: the built OpenAPI ⊇ the approved fragment (paths, methods, status codes, schema properties), using semantic comparison like drift(b) | specdrift (D7) or an interim app script | an implementation that drops an approved 409 fails |
| **V5** | Traceability for NEW rules (delivery-cycle D6 staging): a rule no test cites, or a test citing an absent rule, is red | app CI | planted uncited rule |

V4 closes the chain: today nothing proves that the MR implements what the business approved.
SPEC0212 proves only that committed equals built.

## 6. Operations — telemetry, ledgers, cost

- **Telemetry.** `run.json` uses the `skill-run` event shape (`telemetry-v1.md:34`), so the
  Delivery Report (NOT BUILT, ai-sdlc-status §1 Release row) can read history when it exists.
  The measures that matter here are questions raised, questions the analyst answered by changing
  the rule, and spec-review rounds.
- **Cost.** The owner's no-extra-spend constraint (T4, re-confirmed 2026-09-03) means the
  LLM half runs under the subscription, by hand, with recorded run reports (the specanchor
  `evals/rule-extractor/runs/` pattern). CI runs only the deterministic half, which is the
  existing `evals-acceptance` lane.
- **Ledger rows** move in the PR that changes reality (CLAUDE.md status rule). The table is
  in §8.

## 7. Test Plan — the eval lane (the verifier for the agentic half)

**Shape** (house pattern: `evals/skills/<name>/scenario.md` + outcome-only `accept.sh`, skills
RFC D5): `evals/skills/goldpath-authoring/fixtures/<id>/{input.md, expected.yaml}`, where
`expected.yaml` is the **answer key, written by a human before any run** (specanchor's
`TRAPS.md` discipline).

**Dev fixtures** (finance: CorPay and a lean generated app):

| Id | Input (from `finance-payments.md`) | Planted trap → required outcome |
|---|---|---|
| F1 | beneficiary save/remove; "payments referencing them stay intact" (coverage row softDelete) | manifest: **rely** on softDelete/multiTenancy/dataProtection/auditTrail, **enable nothing**; open questions: can a removed beneficiary be restored, is the IBAN masked in lists |
| F2 | slice 3 "four-eyes on high amounts" | `features.approvals` is OFF in CorPay → must be **ask**, not enable (S3 bait); the threshold is an `[OPEN]` bracket; G6's two questions (Rejected terminal? re-submit legal?) surface |
| F3 | lean app: "a resubmitted instruction must never pay twice" | **enable** `idempotency`; `--plan` opt-ins become questions |
| F4 | "if execution fails, compensate the ledger across services" | S7: saga is a decided non-goal → no module proposed, an out-of-scope note + question |

**Hard criteria** (machine-checked; any failure fails the fixture):
- H1: V1–V3 green, except the gate-#1-only clauses.
- H2: the decision set equals the key exactly. Features are an enum, so this is exact.
- H3: no forbidden enable or disable.
- H4: the files touched are inside the allowed paths. No code, no real manifest, no `specs/*.json`.
- H5: the opt-in question count ≥ the `--plan` count for each enabled feature.
- H6: each planted ambiguity's anchor carries ≥1 open question or bracket, AND the rule
  statement there contains none of the key's forbidden assertions. The forbidden phrases are a
  short list per trap. They are deliberately crude, and H6 is backed by R1.

**Rubric** (a human scores it and the score goes in the run report, like goldpath-defect's item 6
and specanchor E1–E7): R1 the questions are the RIGHT questions · R2 the statements are domain
language a business reader can approve · R3 the fragment follows `conventions.md`.

**spec-review eval** (the goldpath-defect `plant.sh` pattern): a known-good proposal plus
`plant.sh`, which injects one defect per class S1–S5. Acceptance requires each planted defect to
be named in `SPEC-REVIEW.md` by its id or path, and requires ≤1 hard finding on the clean,
unplanted proposal (the false-positive budget, the same idea as the review agent's
dismiss-rate rule, ADR-0007).

**Against overfitting:**
1. **The holdout is the unbuilt sectors.** Insurance and Telco cards (`docs/scenarios/`) have
   no sample yet (master-plan step 8). Their fixtures are written by a different session after
   the skill version freezes, and they score release decisions. A holdout fixture used for a
   release is promoted to dev, and a new one is written. **Custody, decided 2026-10-20:** the
   holdout fixtures live in a PRIVATE repository, the way the private briefs already keep
   customer names out of this one (`scenario-campaign.md`). That repository is created in
   Slice 1, when the first holdout fixture is written, and not before.
2. **Paraphrase invariance.** Each fixture has 3 paraphrases, one in Turkish
   (domain-memory-v1 §4.3, the TR↔EN bridge). H2 must hold across all of them. A decision that
   flips with wording is a failure, whatever the individual scores.
3. **Outcomes only.** Prompt wording is never asserted (skills RFC D5). Keys assert decisions
   and anchors, not phrasing.
4. **k = 3 runs per fixture.** The skill is non-deterministic, so a fixture passes the hard
   criteria only if all 3 runs pass.

**Release thresholds** (D8, recommended): hard criteria **3/3 on every dev fixture AND every
holdout fixture**, with zero S3-class misses (a guessed opt-in is a hard stop, never averaged).
Rubric ≥ 2/3 per fixture, and ≥ 80% of fixtures at full rubric. spec-review: 5/5 planted
classes found, false-positive budget held.

**Model proficiency matrix (§6.2).** It is NOT BUILT (ai-sdlc-status §1). The eval is
model-agnostic by construction: SKILL.md + MCP, a scorer that never reads the transcript's
prose, and `run.json` recording the model. The first column is the reference model under the
subscription. Each further column, including an on-prem model for the bank reality (foundation
§6.2), is the same fixture set under the same thresholds. The promise stays "proficiency is
proven per model". For a weak model, the floor is the guardrail chain (V1–V5 + spec-review +
gate #1): it fails more often and ships nothing wrong. H6 and S3 are where a weak model is most
likely to slip past, which is why both are hard stops.

## 8. DoD — slices, each with its proof

**Slice 0 — the deterministic floor (no LLM).** Pin the specanchor schemas plus the freshness
gate. Add `schemas/authoring/v1/decisions` with a corpus. Add `spec-ready` (V2/V3 +
`--next-id`), `goldpath add feature --plan` (prints the plan and `NextSteps` as JSON, touches
nothing), and a `docs/domain/` skeleton in the template, mirrored to CorPay (`skills-parity.sh`).
*Done =* each corpus is red/green as planted; `--plan` leaves `git status` clean (unit test);
`spec-ready` fails on all three planted defects; the GM shape stays green with the skeleton. This
slice IS delivery-cycle D6's "schemas land now". It closes that half of the delivery-cycle DoD
line; the line's other half, the traceability gate for new rules, is V5 and lands in Slice 3.

*Slice 0 placement (2026-10-20):* `spec-ready` is a verb of the goldpath CLI,
`goldpath spec ready`, and not a specdrift feature. Its V1 and V2 legs call the engine
(`specdrift validate` with the pinned schemas, exactly as `goldpath check` does), so nothing
the engine owns is re-implemented. Its V3 leg checks gate #1, which is ADR-0010's Goldpath
process and not a property of a manifest. specanchor has published no release or tag, so the
schemas are pinned to an immutable commit of its default branch, and the freshness gate
re-points to the newest tag the day one exists.

**Slice 1 — `goldpath-authoring` on CorPay (F1–F4).** Add the skill in the template plus the
CorPay mirror, the eval with F1–F4, and the `goldpath-change` lane. *Done =* F1–F4 hard criteria
3/3, with the run report and rubric recorded under `evals/skills/goldpath-authoring/runs/`. The
F1 proposal lands on CorPay as a real spec MR, which seeds the Payments glossary (foundation
§6.3: authoring fills memory as a project's first task).

**Slice 2 — `spec-review`.** Add the agent, `plant.sh`, and the eval. *Done =* 5/5 planted classes
named and the false-positive budget held. The authoring↔review loop runs on F2, and the S3
bait (approvals) is caught or never taken. The run is recorded.

**Slice 3 — the chain, in one session.** Add `goldpath-feature` step 0, V4 and V5. Then run the
whole flow on CorPay Beneficiary: sentence → authoring → spec-review → **the analyst approves
the spec MR (a real platform approval)** → `goldpath add feature` if decided → goldpath-feature
→ test-gen derives from the rules → V4 green → implementation MR → gate #2. *Done =* both MRs
merged with evidence, the transcript recorded, the holdout set scored at threshold, and the
first matrix column published. This is foundation §12's Phase 2 gate in the form §3.2/§7
describe.

**Ledger rows (same PR as the reality they describe):**

| Ledger | Row | Moves to | When |
|---|---|---|---|
| ai-sdlc-status §1 | Design/analysis | PARTIAL | slice 1 |
| ai-sdlc-status §1 | Design/analysis | SHIPPED-PROVEN | slice 3, with transcript |
| ai-sdlc-status §2 | `authoring` | SHIPPED-PROVEN (dev-fixture scope stated) | slice 1 |
| ai-sdlc-status §2 | `spec-review` | SHIPPED-PROVEN | slice 2 |
| ai-sdlc-status §1 | Domain memory | PARTIAL (CorPay Payments glossary + rules) | slice 1 |
| ai-sdlc-status §1 | Model proficiency | PARTIAL (one column) | slice 3 |
| ai-sdlc-status §1 | Pipeline | PARTIAL (one chain, human-gated) | slice 3 |
| skills-v1 RFC §2 + ai-sdlc-status | honesty note: "the Phase 2 gate was met as sentence→code; the spec-gated form is this RFC" | — | with this RFC (D9, decided 2026-10-20) |
| open-threads | **T34**: this RFC's Slices 1–3, plus its two v2 triggers (authoring for workers/events: the first worker-shaped sentence; card → new app via the wizard: the Insurance/Telco sample start). The triggers split into their own rows when the RFC is accepted | opened | RFC landing |
| open-threads | **T5** gains authoring's LLM-half | — | slice 1 |
| open-threads | **T6**: authoring's `--plan` is the first concrete CLI-as-MCP demand | — | slice 1 |
| coverage-matrix tooling | Skills: authoring / spec-review | rows added | slice 1 |
| SPEC-GAPS | G6 | answered (or explicitly still open) by F2's approved rules | slice 2 |

## 9. Decisions (owner)

### Owner decisions (2026-10-20)

- **Landing.** The RFC lands as **PROPOSED**. **Slice 0 is approved to start now**; it calls no
  LLM (ADR-0004) and every rule it adds ships with its verifier (ADR-0005). Slices 1–3 wait
  for the RFC's acceptance.
- **Open question 2 (D3's strength):** `goldpath-feature` **WARNS in v1** and **REFUSES in v2**
  when a rule-carrying change has no approved spec.
- **Open question 5 (holdout custody):** holdout fixtures live in a **private repository**,
  created in Slice 1 and not before (§7).
- **Open question 9 (sequencing):** start now with Slice 0. Authoring does not wait for the
  Insurance sample.
- **D9:** yes. The CHANGELOG is not rewritten. The honesty note goes to `ai-sdlc-status.md` and
  to the skills-v1 RFC in the same PR as this RFC.
- Everything else (D1, D2, D4–D8, open questions 1, 3, 4, 6, 7, 8, 10) stays open.

### The decisions as proposed

- **D1 — Two artefacts, two agents.** The `goldpath-authoring` skill and the `spec-review` agent
  are separate contexts, mirroring test-gen/breaker. *Recommend: yes.*
- **D2 — Format.** Adopt specanchor's rule/term/spec (delivery-cycle D6, ACCEPTED) at
  domain-memory-v1's location (`docs/domain/<bc>/`). domain-memory-v1's BR markdown cards and
  `language.md` table are **superseded**. Its keepers are the location, the TR body + EN anchors,
  the edge-case catalog and integrations notes. *Recommend: yes, with a superseding note on
  domain-memory-v1.*
- **D3 — `goldpath-feature` requires an approved spec when the change carries a business rule**
  (foundation §3.2). Purely technical changes (no rule) keep today's lane. *Recommend: yes.* It
  changes the goldpath-feature eval fixture, which gains an approved SPEC. *Its strength is
  decided (2026-10-20): warn in v1, refuse in v2.*
- **D4 — `decisions.yaml` is the one new artefact.** specanchor's schemas are
  `additionalProperties: false`, and JSON Patch records the what without the why or the source.
  *Recommend: yes, schema in `schemas/authoring/v1/`.*
- **D5 — v1 manifest reach = `features` (enable/rely) + stated `nfr`.** Everything else is a
  question. *Recommend: yes.*
- **D6 — Gate #1 shape: two stacked MRs** (spec MR approved by a CODEOWNER of `docs/domain/**` +
  `specs/proposals/**`, then the implementation MR). The alternative is one MR with two approval
  rules. *Recommend: two.* The spec approval then stands alone in git history (ADR-0010
  traceability).
- **D7 — V4 lives in specdrift** as the first cut of the matrix's `diff` cell (foundation §5.1,
  v1.5), through its own specdrift RFC. Until then an interim app-side script can stand in.
  *Recommend: specdrift; the interim script only if slice 3 would otherwise wait.*
- **D8 — Thresholds as in §7.** *Recommend: as written.*
- **D9 — Annotate, don't rewrite, the "Phase 2 gate MET" claim.** The CHANGELOG is history. The
  skills-v1 RFC and ai-sdlc-status get one honesty line. *Recommend: yes, in slice 0.*
  **Decided 2026-10-20: yes.**

## 10. Risks & antidotes (each with its enforcing mechanism — the ADR-0012 governance condition)

| Risk | Antidote | Enforced by |
|---|---|---|
| Authoring folds an ambiguity into a confident statement | questions are first-class; spec-review S2 | eval H6 (hard) + V3 `open_brackets==0` at gate #1 |
| A guessed domain opt-in reaches code | `--plan` turns opt-ins into questions; S3 | H5 + zero-S3 hard stop + decisions need sources (V3) |
| Gate #1 becomes a rubber stamp (solo repo, or the author approves their own) | approval is a CODEOWNER MR approval | V3 `approved_by ≠ author` + branch protection. In a solo-owner repo the gate degenerates, and the run report says so rather than hiding it |
| The eval overfits the four finance fixtures | holdout from unbuilt sectors, rotation, paraphrase invariance | release blocked without a holdout score (§7) |
| Two formats for domain memory | D2 supersedes the BR markdown | pinned schemas + freshness gate (V1) |
| specanchor (pre-1.0, reverse-shaped) changes its schema | pin + freshness gate; forward use rides the unmodified file+line branch | V1 corpus + freshness gate |
| Ceremony kills small changes | the technical lane stays (D3) | goldpath-feature step 0 is conditional on "carries a rule" |
| Cost stalls the matrix | subscription runs by hand, recorded | ledger shows PARTIAL until columns exist, never above |

## 11. Open questions for the owner

1. **D2:** supersede domain-memory-v1's BR markdown with specanchor artefacts, keeping its location and language decisions?
2. ~~**D3:** must `goldpath-feature` refuse a rule-carrying change without an approved spec, or only warn in v1?~~ **Decided 2026-10-20:** v1 warns, v2 refuses.
3. **D6, gate #1 in this repo's reality:** the owner is the only human. For the slice-3 demo, who is the "business" approver? Is the owner's approval of an agent-authored spec MR an acceptable gate #1, if the run report states it plainly?
4. **D7:** is containment (V4) a specdrift 0.5 feature, with its own RFC in that repo, or a goldpath-side script for v1?
5. **Holdout custody:** this is a public repo, so a holdout cannot be hidden. Is "written by a different session after freeze, then rotated" enough, or should holdout fixtures live in a private repo, as the private briefs already do (`scenario-campaign.md`: customer names stay outside)? **Decided 2026-10-20:** a private repository, created in Slice 1.
6. **Language:** may the eval carry Turkish paraphrases, given CLAUDE.md's English rule for this repo and domain-memory-v1 §4.3's Turkish body text for customer repos? (Proposal: allowed as fixture DATA only.)
7. **Cost cadence:** k=3 × (4 dev + holdout) × 3 paraphrases is about 60+ subscription runs per skill release. Is that acceptable, or should the rule be k=1 for dev iterations and k=3 only at release?
8. **Second matrix column:** which on-prem model (the bank reality, foundation §6.2), and who runs it? Without one, the matrix stays a single column.
9. **Sequencing:** slices 0–3 compete with step 6.1 (the pilot) and the scenario campaign (master-plan steps 6–7). Is authoring the next track, or does it wait for the Insurance sample (master-plan step 8), which would give it real holdout pressure? **Decided 2026-10-20:** start now with Slice 0.
10. **Naming:** `goldpath-authoring` (skill) and `spec-review` (agent), or match foundation §6.1's bare `authoring`?
