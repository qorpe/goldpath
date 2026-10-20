# RFC: Goldpath Skills v1 — the agentic half meets the deterministic half

> Status: v1.0 accepted — D1–D6 approved by Ömer (2026-07-06): the four-skill set · home =
> the template · engine consultation as a written hard step · diet-in-skill + eval-asserted
> independence · outcome-only acceptance · the goldpath-feature eval run end to end as this
> RFC's proof. The last piece of the Phase 2 gate
> ("business sentence → MR in one session"). Effort M.
> Constitution grounding: ADR-0004 (skills call the ENGINE via MCP, never re-derive what it
> answers), foundation §5 ("a skill without golden-manifest→expected-output evals does not
> get merged", "no skill may bypass the gates"), foundation §8.2 (test independence: the
> test-gen skill NEVER sees the implementation; a separate breaker agent exists to break it).

## 1. What ships

Skills are versioned PROMPT ARTIFACTS (`SKILL.md` + support files) that ride the template —
every generated app is agent-ready on day one, with the deterministic engine already
registered as an MCP server (`.mcp.json` → `specdrift mcp`).

| Skill | Job | Hard steps it cannot skip |
|---|---|---|
| **goldpath-feature** | business sentence → vertical slice (endpoint + handler + entity deltas + tests) → gates → MR-ready | read the manifest FIRST · new/changed contract goes to the spec, code follows it · `spec_validate`+`spec_drift` before declaring done · full local gate run (build, tests, format) · never invent infra — compose the Goldpath primitives the manifest enables |
| **goldpath-manifest** | authoring wizard: enable/disable features, explain trade-offs, keep truth | every edit round-trips `spec_validate` with the profile rules · feature toggles must name their consequences (packages that appear/disappear) · drift check after wiring |
| **goldpath-test-gen** | spec-derived tests | context DIET enforced in the skill: may read specs/, .goldpath/, conventions — MUST NOT open the feature implementation folder · derives cases from the spec's example tables + the edge-case catalog · property-based where the surface is algorithmic |
| **breaker** (agent) | adversarial: succeeds the day it finds something | reads spec + public surface only · produces failing scenarios as tests, not opinions · runs AFTER test-gen, separate context |

**Non-goals (v1, written not silent):** deterministic codegen inside skills (the skill
COMPOSES existing primitives; boundary-layer codegen is Spec Engine v2 — a skill that
hand-writes a DTO the engine could generate is a review flag), a hosted agent runner
(skills run in whatever agent the team uses; Claude Code is the reference), auto-merge
(human review is a fixed gate, ADR-0010), analyst-facing NL spec authoring (needs the
AsyncAPI/data-model authoring work — the wizard covers the manifest only).

## 2. Evals — the foundation §5 hard rule

Every skill ships `evals/<skill>/`: a fixture (golden manifest + input sentence/spec) and
an ACCEPTANCE SCRIPT that checks machine-verifiable outcomes only — builds green, gates
green, expected artifacts exist, forbidden context untouched (test-gen's diet is checked by
asserting the eval transcript never opened `Features/`). Prompt wording is never asserted;
OUTCOMES are.

- The **goldpath-feature eval IS the Phase 2 gate demo**: generated app + "a customer can cancel
  an unshipped order" → the acceptance script requires: new endpoint present in the exported
  OpenAPI, handler + tests exist, specdrift clean, full suite green.
  **Honesty note (2026-10-20,
  [goldpath-authoring](goldpath-authoring.md) D9):** the gate this eval met is
  **sentence → code**. The input sentence was already decomposed by a human, and the same agent
  wrote the contract it then implemented; no business-approved spec existed, so ADR-0010's
  gate #1 had nothing to approve. The CHANGELOG's "the Phase 2 gate is MET" stays as the history
  it is. The spec-gated form foundation §3.2 and §7 describe (sentence → approved spec → code)
  is NOT met, and the authoring RFC is the plan for it.
- Execution: locally/manually now (an agent session drives it; the script judges);
  joins CI when an agent-in-CI story exists — recorded as the deferral it is.

## 3. Decision Points (Ömer)

- **D1 — v1 set = the four above.** **Widened 2026-09-08** by
  [goldpath-delivery-cycle-v1](goldpath-delivery-cycle-v1.md): a fifth skill (`goldpath-defect`)
  and a per-repository change skill join the set, and the layer reaches the LIBRARY repositories
  as well as generated apps. The four below are unchanged; the set is no longer closed. The review-agent (MR reviewer) and analyst-authoring
  skills are their own tracks (review-agent-v1.md already exists as strategy).
  **Recommendation: these four.**
- **D2 — Home = the template** (`.claude/skills/`, `.claude/agents/breaker.md`, `.mcp.json`).
  Generated apps are agent-ready day one; the goldpath repo carries only the RFC + evals design.
  **Recommendation: template.**
- **D3 — Engine consultation is a hard step, in writing, in every skill** — "done" without
  a clean `spec_validate` + `spec_drift` is not done. This is ADR-0004 made procedural.
  **Recommendation: yes.**
- **D4 — Test independence mechanics:** the diet lives IN the skill text (what may be read),
  the breaker is a SEPARATE agent file with its own context, and the eval asserts the diet
  held. No tooling-level sandbox in v1 (recorded; an enforcement hook is a later hardening).
  **Recommendation: as described.**
- **D5 — Eval acceptance = outcomes only** (build/gates/artifacts/diet), never prompt text.
  **Recommendation: yes.**
- **D6 — The proof for THIS RFC:** I run the goldpath-feature eval end to end on a generated app
  and the MR carries the acceptance script's output — the Phase 2 gate demo, witnessed.
  **Recommendation: accept as the DoD proof.**

## 4. DoD
- [x] D1–D6 locked · four skills + breaker agent + `.mcp.json` ship in the template
- [x] `evals/skills/` with scenarios + outcome-only acceptance scripts for all four;
      **the goldpath-feature eval executed end to end — 9/9 PASS** (business sentence →
      CancelOrder slice: contract in the committed OpenAPI, slice-shape match, tests +
      full suite green incl. the real-container smoke, manifest untouched, format clean,
      spec_validate + spec_drift clean). Output recorded in the MR. Bonus: the eval's
      FIRST run caught a doc-vs-reality drift (conventions said folder-per-feature, the
      template ships file-per-feature) — docs aligned to reality, in this MR.
- [x] GM shape still green with the skills-bearing template (GmSkills: spec-lint clean ×2,
      smoke pass) · CHANGELOG · module plan Phase 2 gate row marked
