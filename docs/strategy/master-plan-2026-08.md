# Master plan — 2026-08-03 (owner-approved order)

The single ordered list of everything open, set with the owner on 2026-08-03 after a
repo-wide sweep (code, RFCs, ledgers, schema-vs-generator, samples, CI). Nothing is
open outside this list and the trigger-parked set at the bottom. Each step lands with
the standing discipline: tests → PR → review findings answered BEFORE merge → CI
green → merge → ledgers updated in the same PR.

## The order

1. **Campaign R1 implementation** — ExcludedDays, EndDate→ExpiredIncomplete, the retry
   ladder (30s → 2m → repair queue), GlobalTps; schema + template + CLI recipe +
   console fields + tests. The one accepted RFC with zero code; blocks S-FLEET-01/03.
2. **Housekeeping slice** (one group) — **DONE 2026-08-03**: T14 four `.Produces<T>` (+
   the response-side export test; CorPay's re-export proof rides preview.6) · T8 bulk
   `?definition=` filter (server + facet + smoke past the page bound) · ops runbooks for
   Idempotency/AuditTrail/SoftDelete · analyzers GP1001/1002/1004 (batch 4) · T4 review
   agent as a CI step + output-contract hardening (waits only on the `ANTHROPIC_API_KEY`
   secret — owner decision) · the monthly bench cadence (bench.yml, 1st of the month) ·
   the GM coverage-table reality note and the stale doc lines (2026-08-03, PR #131).
3. **preview.6 train** — **DONE 2026-08-05**: `v0.1.0-preview.6` on nuget.org (R3 +
   console U6–U9 + Campaign R1 + the housekeeping set; GM matrix + Campaign mutation
   green before the tag, ADR-0008). T12 closed (every CorPay head reaches ready — and
   fixing it surfaced the payments worker's D3-violating migration). The pre-flight
   also found and fixed: five silently red nightlies (the console build never installed
   the kit), mutation-heavy's missing tool restore, GP1001 catching the TEMPLATE's own
   unmarked command (the Mediant attribute surface is `KeyProperty`, not the RFC's
   `KeyExpression` sketch), and console-smoke's pg probe racing initdb. CorPay pins on
   preview.6 with its four commands `[Idempotent]`-marked; its re-export regained the
   four T14 schemas and `goldpath check` stays green — T14 closed. T1 unblocked.
4. **ADR-0012 + Platform/Module SDK RFC** — **DONE 2026-08-05 (ACCEPTED)**: Goldpath is
   a PLATFORM; product modules live in their OWN repos, bind to the published train
   like adopters (never source), declare under namespaced `products` keys, ship their
   own kit-composed consoles federated by the registry, and may be closed on the open
   core. D1 `Goldpath.Sdk` + D2 manifest surface + D6 `export compose` ride step 5;
   D5 `@qorpe/ui` extraction fires at consumer #3 (step 6). The acceptance carries the
   §2b condition: every risk has a written antidote AND its enforcing mechanism; the
   two honest GAPs (product-repo train-freshness step, GP20xx no-source-leak analyzer)
   are the pilot's DoD.
5. **Shapes + CLI package** — **DONE 2026-08-05**, six slices, each PR'd with an
   end-to-end proof: `Goldpath.Sdk` + the manifest `products` surface (S1, non-breaking
   — the shared types were internal everywhere) · `--layout clean-architecture` with
   GmFourClean real (S2) · `goldpath new service|gateway` — db-per-service manifests,
   YARP over service discovery, the ROUTED smoke probe, GmSixGateway real (S3) · the
   bare-`goldpath new` WIZARD deriving infrastructure with reasons ("no broker needed —
   removed"), pure-function tested (S4) · `goldpath export compose` GENERATED from the
   AppHost, proven by a real `docker compose up` answering business traffic (S5) ·
   GmBulkOnly (the bulk-only customer runs ONLY Postgres) + the module×infrastructure
   matrix (`docs/guide/modules-and-infrastructure.md`) + `goldpath init` L2 attach
   (manifest + schema gate; rewiring stays the transformation pack's — RFC D5 scope
   honored) (S6). The plain-monolith variant is declarative (GM-4's no-features shape);
   `new module` was renamed by ADR-0012: product modules live in their OWN repos.
6. **Pilot product module** — the API Portal (chosen 2026-08-06; analysis private).
   **6.0 DONE 2026-08-07**: @qorpe/ui extracted (RFC qorpe-ui accepted; repo
   github.com/qorpe/ui, gates G1–G6 live, 0.1.0 on npm with the B1–B9
   standardization series) and THIS console now composes from the PUBLISHED kit —
   ui/kit deleted, the kit-freshness gate holds the pin honest.
   **6.1 — the pilot, IN PROGRESS (recorded 2026-09-03)**: qorpe.apiPortal (private
   repo) stands on the published train — 69 merged PRs by 2026-09-03, on
   0.1.0-preview.7 the day it shipped, the two §2b riders in its CI (train-freshness,
   product-guard/GP2001) plus the npm kit-freshness twin, its admin console composed
   from @qorpe/ui (consumer #3), and Mockifyr on the published kit too. The pilot has
   already fed the platform four fixes it found live: Approvals v2's ladder rules
   (#185), the service-discovery core (#197), the document-column widening (#198),
   and the console federation of Approvals (#200). What 6.1 still owes: the STC
   customer's M1 answers (the BRD baseline, the fee-collection boundary, NFR numbers)
   decide the pilot's next investment.
7. **Scenario campaign** — starts after step 3 and runs alongside: S-FIN-01 first,
   the S-FLEET set once R1 ships, the 30M rig spec co-written before any scale claim.
   All seven seeded scenarios get evidence. **2026-10-21 (GA readiness Q11):** zero budget —
   the 30M run uses the full dataset, never a shrunk one, overnight on the owner's Mac in a
   dedicated Lima VM, with GitHub-hosted free runners as the repeatable lane.
8. **The samples — LAST, as the full-set exam** — Insurance and Telco from scratch. A
   Factoring sample was added on 2026-10-21 and moved out of this public repository on
   2026-10-25, both by the owner ([GA readiness](../rfc/goldpath-ga-readiness.md) Q6/G12): it is
   built in a private qorpe repository under the RFC's provenance rule. T23's industry pack is
   unaffected
   (with the step-5 wizard, proving it in anger) plus CorPay's open cells (SoftDelete,
   Caching, a real Contracts-idiom worker consumer, the Archival slice, SPEC-GAPS
   G3–G7, GAP-LEDGER #32/#33/#34). `add feature`, `db bundle`, the manifest skill —
   all proven in real use; no empty core cell remains in the coverage matrix.

## The finalize set (F1–F3) — DONE 2026-08-09

Run before the pilot, after two audits found the engineering ahead of the paperwork:

- **F1 — the doc truth sweep** (#155): the 42 analyzer rules moved to the SHIPPED ledger
  (they were shipping while the file read empty); SPEC-GAPS reconciled against the real
  export — G1/G2 genuinely closed, G3–G7 genuinely open, and G7 (no `security` requirement
  on any payment operation) flagged as the one a buyer's gateway review hits first; three
  floor RFCs stopped promising ops packs that do not exist (recorded as T17 with a trigger);
  the recipe count corrected. Preceded by #154, which reconciled five ledgers with GitHub in
  BOTH directions and added `ledger-check.sh` + `schema-honesty.sh` so neither drift returns.
- **F2 — supply chain** (#156): a CycloneDX SBOM (203 libraries, 21 ours) and signed SLSA
  provenance ride with every train, both generated BEFORE the push so a failure leaves the
  train unpublished rather than published without evidence; `SECURITY.md` carries the
  disclosure path, response targets and the honest support window. CRA Art. 14 makes these
  legal obligations from 2026-09-11.
- **F3 — the messaging dependency** (#157): [goldpath-messaging-exit](../rfc/goldpath-messaging-exit.md)
  measures the exposure instead of assuming it, and corrects §9 D1's claim that a move would
  not touch consumer code — the template's handlers, our FROZEN public API and CorPay's 79
  references say otherwise. Recommendation: stay pinned on 8.x; a move is a MAJOR version.
  **Owner decision pending** (the RFC's own DoD item 1).

## Open, with an owner — not parked

An audit (2026-08-09) found three items with an accepted decision, a FIRED trigger and no
line in this plan. A capability that has an ADR, an RFC and no owner in the ordered list is
how an asset acquires a surprise:

9. **`Goldpath.Ai`** (ADR-0011 + RFC accepted 2026-07-26, open thread T7) — the trigger was
   "after the UI phase"; the UI phase is complete, so the trigger has fired and the module
   is unbuilt. Scope stays as the RFC set it: model gateway, admin tool registry as MCP,
   AI decision record, confidence gate. **It belongs after the pilot, not before** — the
   pilot is what will say which of the four an adopter actually reaches for.
10. **The MassTransit exit RFC** — DONE 2026-08-09 (F3, PR #157: `docs/rfc/goldpath-messaging-exit.md`,
    the measured exit). The decision itself was taken in two steps: option F on 2026-09-03
    (ADR-0013), then on 2026-10-21 Wolverine as the 1.0 insurance by 2026-12-31 and the
    family bus on its own committed timeline ([GA readiness](../rfc/goldpath-ga-readiness.md)
    Q1/Q2). Both live on the open-threads ledger (T18, T40), not here. Original text kept for
    the record:
    (issue #11, owner-prioritized) — MassTransit v9 has gone
    commercial (`docs/rfc/goldpath-messaging.md` §strategy). The options recorded there are
    a v8 fork, Wolverine, or growing Mediant toward transport. This is a **licensing
    exposure in the core path**, and until now it lived only in an issue comment: an
    enterprise procurement review asks about third-party licensing on day one. The RFC is
    owed before any client engagement that includes messaging.
11. **T1 — the console driven against CorPay** — T12 closed on 2026-08-03, which fired this
    trigger; the adopter-shaped console proof has not been run since.

## The saga / rules-engine decision (made 2026-08-09, previously an undecided park)

These two sat in the parked list with a trigger that could never fire ("decided inside the
step-4 RFC" — step 4 completed without deciding). An undecided park is not a deferral, it
is a hole. The decision:

- **Saga / process manager: NOT Goldpath's to build.** The .NET ecosystem already has
  mature, battle-tested answers (MassTransit state machines, NServiceBus sagas, Dapr
  Workflow, Temporal). ADR-0003 says what Microsoft/the ecosystem provides is COMPOSED,
  never rewritten — an accelerator that ships its own saga engine is rewriting the thing
  its own constitution forbids. What Goldpath owes instead is the **seam**: the outbox is
  already atomic with the transaction, the run model already survives kill-9, and idempotency
  already makes retries safe — those are the three properties a saga library needs from its
  host. If an adopter mandates orchestration, we compose their choice and document the wiring.
  **Written as a non-goal, not a roadmap item.**
  **Honesty rider (2026-08-10):** that last sentence is a claim we have never RUN. No sample
  composes a MassTransit state machine (or any orchestrator) on top of Goldpath, so "it
  composes cleanly" is a design argument, not a proof. Recorded as thread **T19** with the
  proof it owes; until that runs, do not tell a client orchestration is proven — tell them
  it is unproven and cheap to prove.
- **Rules engine: same verdict, different reason.** Business rules are DOMAIN, and the one
  thing this accelerator refuses to do is guess a domain. NRules/Microsoft RulesEngine exist
  for teams that want a rules DSL; most enterprise "rules" turn out to be either
  configuration (the config registry pattern) or a decision table the domain owns. **Non-goal
  until an adopter's requirement names one** — and then it composes, like the saga.

Both are recorded here rather than in the parked list so nobody mistakes "we chose not to"
for "we forgot".

## Parked on written triggers (deliberately open)

*Moved out 2026-10-21:* the Oracle provider (T16 — its trigger fired on 2026-10-20 and the owner
put it IN for 1.0, GA gate G7) and the console OIDC login (T10 — a 1.0 gate, G6) are scheduled
work now, not parks.

`upgrade` skill (first real preview→preview migration) · console URL routes / tenant
picker (T9/T11) · ⌘K entity-id search (needs a cross-module lookup endpoint) ·
stat-card trends (no time series in the contract) · TS mutation for the ui kit (T3) ·
public demos, one subdomain per product (T41 — the owner has an always-on machine; parked
2026-10-21).

## The transformation park fired early (owner decision, 2026-08-16)

The Phase 3 transformation package sat above with the trigger "the phase after the
samples". A factoring engagement at a bank moved it — exactly the §5.1 timing rule's
shape: the toolchain is born as the by-product of a real deliverable with a named first
customer, not as a third front. The deterministic core now lives OUTSIDE this repo as
[qorpe/specanchor](https://github.com/qorpe/specanchor) (Apache-2.0, the OSS line), and the
decision record is [specanchor-composition](../rfc/specanchor-composition.md): §9 keeps the
method, specanchor keeps the machinery, and **until the rehearsal (fake legacy → Goldpath
migration) completes, Goldpath owes specanchor exactly the paperwork in that RFC** — the
pilot product module and the samples keep their order above. Thread T20 carries the proof
this composition owes.
