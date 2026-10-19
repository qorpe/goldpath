# Platform RFC: the Module SDK — how product modules ride the core

> Status: **ACCEPTED** (2026-08-05 — D1–D7 approved by Ömer). The approval carries one
> governance condition, honored in §2b: every operating risk of this model is listed
> WITH its antidote AND the mechanism that enforces the antidote — nothing in this
> model may drift outside our control on discipline alone (ADR-0005: a rule without a
> verifier does not count). Companion: ADR-0012. This is a PLATFORM RFC like the
> console's — the eight module sections apply where they fit; the center of gravity is
> the decision set.

## 1. Scope / Non-Goals

**Scope:** everything a first-party product module (Sync, API Portal, …) needs to be
built OUTSIDE this monorepo yet feel inside the family: the SDK surface it binds to, the
manifest key it declares, the console it ships, the feed it ships FROM, the kit its UI
composes, the container story it inherits, and the auth story that federates it beside
the core app.

**Non-goals:** no product module is designed here (each gets its own RFC — foundation
§10 intake); no runtime plugin system (contradicts §5.0's toggle rule); no change to the
L1/L2/L3 non-imposition guarantee (§1 — immutable); no third-party marketplace story
(first-party products first; the marketplace question has no adopter asking).

## 2. The decisions

### D1 — The SDK surface: `packages/shared` becomes `Goldpath.Sdk`

Today the admin seam (`AdminSurfaceGuard`, `AdminTenantScope`, `AdminPaging`,
`TraceLink` — 149 lines) is `<Compile Include>`-linked into six packages. Inside the
monorepo that was the right call (H2: one file, no drift); outside it is a wall — an
out-of-repo module has no honest way to build an admin surface today except copying the
files, which is exactly the submodule/source-copy shape foundation §10 forbids for
Goldpath's OWN upstream dependencies; ADR-0012 extends that same discipline downstream
(a product module binds to packages the way Goldpath binds to Mediant).

**Decision:** a real `Goldpath.Sdk` package carrying the admin seam (guard, tenant
scope, paging clamp, trace links) plus the module-authoring surface that today only
convention carries (the `Map<Module>Admin` shape, the `GoldpathAdminResult` envelope).
It rides the D1 lockstep train with its own PublicAPI ledgers.
(Implementation note, 2026-08-05: the migration turned out NON-breaking — the shared
types were `internal` in every consumer, so no adopter could ever see them and no
consumer ledger moves; the types go public for the first time in the Sdk's OWN ledger.
The six in-repo packages swapped compile-links for the package reference in one step;
`packages/shared` is gone.)

### D2 — Namespaced product declarations in the manifest

The v1 schema is closed by design: `features` enumerates exactly 12 keys,
`additionalProperties: false` throughout, and the Ring C shelf is a one-item enum
(`modules: [yarpGateway]`). A product cannot declare itself without editing the schema —
which is correct for CATALOG modules and wrong for products.

**Decision:** a new top-level `products` surface with NAMESPACED ids
(`^[a-z][a-z0-9-]*\.[a-zA-Z][a-zA-Z0-9]*$` — `qorpe.coexist`, `qorpe.apiPortal`), each
entry a toggle-plus-options object the product's own RFC schema-fragments into place.
(Implementation note, 2026-08-05: shipped as an ARRAY of `{name, enabled, …}` entries
rather than a map — the engine's never-guess schema vocabulary has no
`patternProperties`, and an unvalidatable key is worse than a different shape; the
namespace guarantee rides `pattern` on `name`, mechanically enforced, corpus-proven.
One consequence is a DECISION, not an accident (review R5): duplicate names cannot be
expressed as invalid in that vocabulary — `uniqueItems` compares whole objects — so the
refusal belongs to the COMPOSITION layer: the CLI's product wiring rejects a duplicate
name at generation, mechanical with the pilot; recorded in the schema description.)
The core `features` enumeration STAYS closed; namespacing means a future core feature
can never collide with a vendor key. Additive schema change (v1 stays v1), corpus rows
(valid + invalid: bad namespace, unknown flat key) land with the schema, and no product
ships before a golden-manifest shape exercises its declaration (ADR-0008; the GM matrix
currently exercises no second Ring C module — the first product fixes that too).

### D3 — The console contribution model: per-product consoles, one registry

§5.0 forbids the obvious answer: runtime panel loading is assembly-scanning magic, and
the embedded dist is one artifact per train. The console's own code already names the
alternative ("a future surface for a DIFFERENT audience becomes its own group") and the
registry already federates services.

**Decision:** a product module ships ITS OWN console app — composed from the kit (D5),
served by its own management head behind its own floor (`MapGoldpathConsole` pattern),
conformant to ui-standard (one family, provably: axe + the family checklist). Operators
reach it beside the core console through the cross-service REGISTRY — a product's
console is one more service entry, not a plugin panel. The core console's five-module
surface stays frozen; new CATALOG modules keep joining it at compile time as today.
Per-product deep links ride T9 (client-side routes) when its trigger fires.

**Clarification (owner, 2026-10-19 — #257, [goldpath-console-link-tiles.md](goldpath-console-link-tiles.md)).**
"One more service entry" reads as "one more REGISTRY entry": a **link** (`AddLink`) when the
product has no core admin surface, and a **service plus a link** when it has one. A link is
never probed, so a product with no Goldpath admin surface joins the operators' view without a
false UNREACHABLE row. This clarifies the decision; it does not reverse it.

### D4 — Private distribution: closed modules on the open core

**Decision:** the core stays Apache-2.0 on nuget.org, one train (versioning D1). A
product module lives in its OWN private repo, binds to the published train like an
adopter (pins, upgrade guides, `goldpath check` — the CorPay discipline verbatim), and
ships from an INTERNAL NuGet feed (`nuget.config`'s mirror override is the existing
mechanism; air-gapped is first-class, foundation §10). Product versioning is the
product's own semver PLUS a declared "built against 0.x" train pin — D1's lockstep is
scoped to `Goldpath.*` from this monorepo, and D3's latest-only window applies to the
pin. Licensing of product modules is per-product (open core, amended in ADR-0012).
Supersedes the packaging half of T6 for first-party products; T6's brownfield-adopter
trigger stays for third parties.

### D5 — `@qorpe/ui`: the kit ships when the third consumer arrives

`@goldpath/kit` is `private: true`, `main: src/index.ts`, consumed by `link:` — a source
folder wearing a manifest. The written trigger stands: two consumers copy, three justify
a package — and the pilot product module (master plan step 6) is the third. The console
RFC already ASSUMES a published kit for the custom-pages path; today that is a promise
without a package.

**Decision (shape now, execution at the trigger):** extract the family as **`@qorpe/ui`**
(the kit's tokens + primitives + Goldpath composites that are family-generic; the
console-contract-specific pieces stay in the console app). Real packaging: build step,
`exports` map, `.d.ts`, `files` allowlist, its own npm semver — an npm artifact cannot
ride `Directory.Build.props`, so the train relationship is a PIN (each Goldpath train
records the `@qorpe/ui` version its console was built with; the console RFC's
"versioned with the train" line is corrected to say exactly that). mockifyr migrates to
the package on its own schedule; until the trigger fires, `link:` stays.

### D6 — The container story: `goldpath export compose`, generated

Nothing exists today (zero Dockerfiles, no export, no deploy doc) — but the design is
already written: foundation §10, execution-model blindness — AppHost is local comfort;
`goldpath export compose` is GENERATED from the AppHost definition so the two can never
diverge; K8s/OpenShift manifests come from CI.

**Decision:** keep that design, schedule it: `goldpath export compose` joins master plan
step 5's CLI scope (it is the microservice layout's missing deploy half), K8s manifests
stay CI-side patterns (migrations-runbook's sketch grows into the doc). Product modules
inherit the story for free — they are ordinary Goldpath apps.

### D7 — Federation auth: staged, and T10 keeps its trigger

A customer running the core app + two product consoles is exactly the console RFC's §3
map: same boundary (case 1/2) works TODAY — every head behind one floor (or CORS with
credentials), the registry lists them, refusals stay honest. What does NOT work today is
services that cannot share a boundary — and that is T10's written trigger, not a new
thread.

**Decision:** stage 1 (now): federation = one auth boundary + the registry; the
products' RFCs must keep their surfaces behind `GoldpathPolicies.Ops` so the story holds
by construction. Stage 2 (when T10 fires — and a multi-product deployment is its
likeliest firer): the browser-side OIDC client is built ONCE in the kit, so every
product console inherits sign-in-once; T10's proof line is unchanged. Token–tenant
binding (auth RFC) already crosses products because it is claim-based, not origin-based.

## 2b. Risks & antidotes (the acceptance condition)

Every risk of operating this model, with its antidote AND the mechanism that enforces
the antidote. The rule for this table is the constitution's own (ADR-0005): an antidote
enforced only by discipline is listed as a GAP until its mechanism ships — nothing here
is allowed to rest on "we will remember".

| # | Risk | Antidote | Enforced by (mechanism) |
|---|---|---|---|
| R-1 | **Version skew** — a product repo silently falls trains behind the core | D3's latest-only support window: a product is supported on the CURRENT train only; falling behind is visible, never ambient | The product repo's CI runs the CorPay lane verbatim — nightly build + `goldpath check` against its PINNED train, plus `scripts/train-freshness.sh`, which goes RED when nuget.org carries a newer train than the pin — **SHIPPED 2026-08-10**, running in CI against CorPay (the adopter-shaped rehearsal) and skipping honestly only on genuine connectivity failure, never on a broken request |
| R-2 | **Source leakage** — a product copies core source instead of binding to packages (the fork that cannot receive updates, foundation §10) | Products bind to published `Goldpath.*` + `Goldpath.Sdk` only; no compile-links leave this monorepo | **SHIPPED 2026-08-10** in two halves, deliberately: **GP2001** (analyzer) flags a non-Goldpath assembly declaring types in the `Goldpath` namespace — the copy-a-file-keep-its-namespace move; **`scripts/product-guard.sh`** flags a `.csproj` compile-linking source from outside the repo. The second half is a script, not a Roslyn rule, because "does this path leave the repo?" is a question MSBuild answers exactly and an analyzer could only approximate. Both proven against CorPay: clean today, red on a planted leak |
| R-3 | **Family drift** — a product console "resembles" the family instead of BEING it | D5: every console composes `@qorpe/ui`; conformance is composition, not imitation | The product console's CI runs the same gates as the core console: axe + the ui-standard family checklist + kit-version pin recorded per train; a console not importing the kit cannot pass the checklist's component-vocabulary rows |
| R-4 | **Kit breaking changes ripple** to every consumer | The kit gets the NuGet discipline on npm: semver + changelog + upgrade notes; each Goldpath train RECORDS the kit version it was built with | `template-pins.sh` grows a kit-pin row (the same script that already fails the build on a stale Goldpath pin); the kit's publish workflow refuses a version without a changelog entry (mirrors release.yml's awk gate) |
| R-5 | **Cross-repo change cost** — one seam change = two PRs | The SDK surface is deliberately SMALL and frozen-by-ledger; products feel churn only when the ledger changes | `Goldpath.Sdk` carries PublicAPI Shipped/Unshipped like every package (RS0016/17 build-red on undeclared change) + versioning D2: a ledger removal is a MINOR train with an upgrade-guide entry — mechanical, not memorial |
| R-6 | **Visibility loss** — separate repos hide what exists and what runs where | The manifest stays the unit of truth (ADR-0001): `goldpath discover` finds manifests, the console registry lists running services — nobody inventories repos by hand | The namespaced `products` key makes every product self-declaring; corpus + GM shape reject an undeclared or malformed product (ADR-0008: no product ships while its GM row is red) |
| R-7 | **Private feed becomes a second-class citizen** (stale mirror, broken air-gap) | The internal feed is the SAME mechanism the air-gapped scenario already treats as first-class (`nuget.config` override) — one path, not a special case | The product CI restores EXCLUSIVELY from the internal feed (no nuget.org fallback in its `nuget.config`) — a stale mirror fails the product's own nightly loudly, on our side of the fence |
| R-8 | **Governance drift** — a product ships without an RFC, ops pack, or conformance pass | Foundation §10 intake holds for products exactly as for modules: RFC → constitution conformance check → build; "no runbook = no module" unchanged | The RFC index's completeness rule (every file has a row) + the review agent's R1 class on the product repo (same skill, same script — the product template ships `.claude/` like CorPay's) |

Two rows carry honest GAPs until the pilot (R-2's analyzer, R-1's freshness step) — both
are the pilot's DoD items in step 6, listed there so they cannot be silently skipped.

## 3. Manifest surface

D2 above; the schema fragment ships with the first product's RFC, the `products` map and
its corpus rows ship with THIS RFC's implementation PR.

## 4. API surface

`Goldpath.Sdk` (D1) — the only new package. Its Shipped ledger starts from the four
shared files' current public surface, unchanged; the authoring helpers arrive with the
pilot product's needs, never speculatively (§11: the lure of 54 modules).

## 5. Analyzer rules

None in this RFC. Candidate for the pilot: a GP20xx that flags a product assembly
referencing `Goldpath.*` internals or re-implementing the SDK seam — deferred until
there is a product to flag (ADR-0005 wants a verifier per standard; the standard here
lands with the first consumer).

## 6. Ops package

The SDK inherits the platform's ops posture; a product module's RFC owes its own `ops/`
(no runbook = no module — unchanged).

## 7. Test plan

- Corpus: `products` map valid/invalid rows (bad namespace, flat unknown key still
  rejected).
- GM: a shape declaring a namespaced product (can stub the product package) — the
  matrix's first second-shelf row.
- SDK migration train: the six in-repo consumers build from the package; PublicAPI
  ledgers carry the move; the admin-surface behavior suites (auth floor, tenant scope,
  clamp) run unchanged — they ARE the seam's tests.
- Pilot product (step 6) is the end-to-end proof: private repo, pinned train, own
  console from `@qorpe/ui`, registry federation, `goldpath check` green in ITS CI.

## 8. DoD

- [x] D1–D7 approved by the owner (2026-08-05; ADR-0012 accepted; §2b's risk table is
      the approval's written condition — the two GAP rows are the pilot's DoD)
- [x] `Goldpath.Sdk` package + in-repo migration (2026-08-05 — non-breaking: the shared
      types were internal everywhere; no upgrade step needed)
- [x] Manifest `products` surface + corpus (m4 valid, m6/m7 invalid) + schema docs
      (2026-08-05)
- [x] `@qorpe/ui` extraction plan recorded (RFC qorpe-ui, accepted 2026-08-06) and
      EXECUTED (step 6.0, 2026-08-07); consumer #3 — the api-portal admin console — starts
      from the published package
- [x] `goldpath export compose` SHIPPED (2026-08-05, step 5 slice 5): compose + laid
      Dockerfiles GENERATED from the AppHost; proven by a real `docker compose up` — the
      api answered health AND a business endpoint through the stack
- [x] rfc/README + adr/README indexes current (verified by the 2026-09-01 repo audit:
      every RFC and ADR indexed, every link resolving; docs-freshness gates the links)
