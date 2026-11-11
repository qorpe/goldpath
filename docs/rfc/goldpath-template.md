# Module RFC: Goldpath Template Pack + Template CI

> Status: v1.0 accepted — D1-D5 approved by Ömer (2026-07-04). Phase 7a SHIPPED: GM-1 proven
> green end-to-end locally (generate → build → real AppHost with postgres+rabbitmq → smoke).
> Phase 1 item 7 (the finale)
> This is where ADR-0001 (manifest), ADR-0008 ("runs with one click" is proven) and the
> deferred-work ledger all land. Multi-MR delivery inside one RFC (see D1).

## 1. Scope / Non-Goals

**Scope:**

| Pillar | What it is |
|---|---|
| **`goldpath-solution` template** | `dotnet new goldpath-solution` → a runnable solution born from manifest-shaped parameters: Aspire AppHost (deps as containers), ServiceDefaults wired, a walking-skeleton service (Mediant vertical slice + ApiDefaults + Data), seed data + a live end-to-end flow, smoke-test project, `.goldpath/manifest.yaml`, CLAUDE.md family, a CI pipeline definition, pinned tool manifest |
| **Template CI (the GM matrix)** | For each golden manifest: generate → build → start → smoke → down. Red matrix = no merge (ADR-0008). Grows persona-by-persona with D1 phasing |
| **Deferred-ledger burn-down** | Testcontainers real-provider tests (Postgres keyset walk, outbox ATOMICITY proof, RabbitMQ round-trip), license gate (free-only allowlist), benchmark job, OpenAPI export artifact (Spec Engine drift input) |
| **Pipelines back on** | `workflow.rules` restored to MR/main triggers once the matrix is green (exit of this item) |

**Non-Goals:** no `goldpath` CLI yet (`goldpath add/init` arrive with Spec Engine — template params
cover generation until then); no `goldpath-module`/`goldpath-worker`/`goldpath-gateway` sub-templates in the
first cut (D1 phases them); no template for sector content (knowledge layers stay empty scaffolds).

## 2. Template Content (walking skeleton — GM-1 shape first)

```
MyPlatform/
├── .goldpath/manifest.yaml                  # generated from template parameters — single source of truth
├── global.json · Directory.Build.props · Directory.Packages.props · nuget.config · .config/dotnet-tools.json
├── ci pipeline                         # build/test/gates
├── CLAUDE.md · .claude/{conventions,architecture,tech-stack}.md · docs/{adr,domain}/ (skeletons)
├── src/
│   ├── MyPlatform.AppHost/             # Aspire: postgres+rabbitmq containers, dashboard
│   ├── MyPlatform.ServiceDefaults?     # NO — Goldpath.ServiceDefaults NuGet (ADR: package, not copied project)
│   └── Orders/                         # walking skeleton (vertical slice)
│       ├── Features/CreateOrder/       # [Idempotent-ready] Mediant command + [HttpEndpoint]
│       ├── Features/GetOrders/         # keyset-paginated query (ToPageAsync)
│       ├── OrderConfirmed.cs           # IIntegrationEvent → outbox → consumer (in-solution)
│       └── OrdersDbContext.cs          # Goldpath conventions + migrations + seed
└── tests/MyPlatform.SmokeTests/        # end-to-end: POST → event consumed → paginated GET
```

The skeleton IS the demo: F5 → dashboard shows the flow; `dotnet test` proves it.

## 3. Manifest Surface
Template parameters ⇄ manifest fields (deterministic both ways): `--db postgresql|sqlserver`,
`--broker rabbitmq|none`, `--outbox`, feature toggles. The generated manifest validates against
`schemas/manifest/v1` (corpus gains every generated shape).

## 4. Test Plan (this item IS the test plan)
- Template CI job per GM: `dotnet new … → build → AppHost start (containers) → smoke → down`
- Testcontainers suite (Data/Messaging): Postgres keyset walk parity with SQLite results;
  **outbox atomicity**: rollback publishes nothing, commit publishes exactly once (inbox dedup)
- License gate: allowlist MIT/Apache-2.0/BSD; anything else fails the build
- Benchmark job (manual/nightly): cursor codec baseline regression

## 5. DoD (item exit = Phase 1 gate)
- [x] 7a: template core + GM-1 green locally (scripts/validate-gm.sh); found and fixed two
      real integration issues on the way — Mediant GET binding (→ mediant#129) and the
      Goldpath.Data projection rule (positional-record projections don't translate; documented)
- [x] 7b (part 2): template conditional composition shipped — db {postgresql,sqlserver} ×
      broker {rabbitmq,none} choices; a choice is only OFFERED once its combination is green
      (GM-1 25s + GM-4 shape 65s, both proven via scripts/validate-gm.sh). Broker=none
      generates ZERO messaging code (compile-time composition at the template level)
- [x] GM personas green in CI per the D1 phasing — OVERTAKEN: the nightly matrix runs
      THIRTEEN shapes (six personas + clean-architecture, gateway, bulk-only, dotted,
      console/authed, three worker triggers), green on the 2026-09-01 dispatch
- [x] 7b (part 1): Testcontainers suite green on REAL postgres+rabbitmq — outbox ATOMICITY
      proven (rollback publishes nothing / commit delivers exactly once) + Postgres keyset
      parity (composite DateTimeOffset walk, Guid descending, member-init projection).
      AddGoldpathOutbox now auto-wires the consumer-side inbox (gap found while designing the proof)
- [x] 7c (part 1): license gate LIVE and green (217 packages, all free/OSS; SPDX OR/AND
      semantics, verified legacy list, justified-exception mechanism) + benchmark job +
      DinD spike job defined (validate when pipelines resume) + central-logging reference
      (collector config) + ops-surface principles recorded (foundation §7.1)
- [x] 7c (part 2): OpenAPI export artifact live (build-time, docgen-tolerant config; asserted
      in validate-gm) · GM matrix in CI (GmOneDefault + GmFourSimple; manual+allow_failure
      until the DinD spike passes, then promoted to blocking) · pipelines attempted ON, reverted pending runner fix (see below)
- [x] 7d — architecture shapes — RESOLVED BY SHIPPING (2026-08-05): `--layout
      clean-architecture` (#143), the microservice multi-project by verbs (`goldpath new
      service|gateway`, #144), the vertical-slice single project as the default monolith;
      GM-2/6 joined the matrix as GmFourClean and GmSixGateway. (Original text: decision
      for Ömer, proposed as 7d parallel to early Phase 2; Ring B modules do not depend on
      template layout, so deferring kept Phase 1 closable.)
- [x] Pipelines re-enabled — OVERTAKEN 2026-08-03: the GitLab-runner problem died with the GitHub move; `.github/workflows/ci.yml` runs on every PR and push to main, nightly runs the GM matrix. The original stall never needed fixing.
- [x] Phase 1 gate ritual: generate a solution, run it, walk the dashboard — recorded as
      the adopter's own walk: `docs/guide/getting-started.md` (five minutes to a running
      app) and `docs/guide/corpay-tour.md` (the real app, verb by verb)

## 9. Decision Points (Ömer)
- **D1 — Phased delivery inside the item (multi-MR):** 7a = template core + GM-1 in CI ·
  7b = provider wiring (Postgres/RabbitMQ via Aspire) + Testcontainers suite + GM-3/GM-5 ·
  7c = remaining shapes (clean-architecture, microservice, monolith/none-broker) + GM-2/4/6 +
  pipelines back on. **Recommendation: yes** — one giant MR would be unreviewable.
- **D2 — First shape = golden-path defaults:** modular-monolith + vertical-slice + postgres +
  rabbitmq (exactly the manifest defaults; GM-1). **Recommendation: yes.**
- **D3 — Walking skeleton domain:** minimal "Orders" (one entity, one command, one query,
  one integration event) — doubles as the smoke flow and the future OrderPlatform seed.
  **Recommendation: yes.**
- **D4 — Testcontainers in company CI:** docker-executor runners need working DinD/socket for
  Testcontainers; unknown until tried. **Recommendation:** local-first (suite runs on dev
  machines), a spike CI job early in 7b; if DinD is blocked, the suite stays a local+release
  gate and we say so in writing (no silent gap).
- **D5 — Aspire version:** pin 13.4.6 (current stable line). **Recommendation: yes.**
  _Since 2026-11-11 the pin is **13.6.5**: GHSA-f7vh-h5g3-7jrh affects every `Aspire.Hosting` below it._
