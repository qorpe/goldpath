# Goldpath Golden Manifest Matrix v1 (Working Draft)

> The concrete form of Foundation 8.4 / ADR-0008: the set of combinations that proves the
> "runs with one click" claim on every commit. Whenever the template or any core package
> changes, CI runs for every golden manifest: **generate → build → spin up → smoke → tear down**.
> Status: v0.1 draft (2026-07-03)

---

## 0. Selection Principles

1. **A full factorial is impossible and unnecessary** (3 architectures × 2 codeOrg × 3 db × …
   × 2^8 features). The approach: **pairwise coverage + realistic personas**. Every enum value
   appears in at least one GM; every Ring B feature is enabled in at least one GM; the
   combinations are not made up — they are target customer profiles (sales demo = golden
   manifest).
2. **GM count discipline:** 6 in v1. Adding a new GM requires an RFC (CI time is a budget).
3. **What is not covered is not hidden:** values not covered in v1 are explicitly listed in §4
   (no silent gaps).
4. **Every combination failure coming from the field** either updates an existing GM or is
   added as a regression manifest (a permanent lesson).

---

## 1. The Six Golden Manifests (personas)

| ID | Persona | Architecture | codeOrg | db / cache / broker / auth | Features | Extras |
|---|---|---|---|---|---|---|
| **GM-1** `minimal-default` | "The 5-line manifest" — the golden path itself | modular-monolith | vertical-slice | postgres / redis / rabbitmq / openid (all defaults) | — | Fastest smoke; demo opener |
| **GM-2** `banking-core` | Cheque/promissory-note style banking transformation | microservice | clean-architecture | oracle / redis / kafka / ldap | idempotency, outbox, auditTrail(db), softDelete, dataProtection(catalog), distributedLocking | 2 services + SOAP externalSystem (mock: record-replay) + full nfr |
| **GM-3** `telco-tenant` | Multi-tenant telco self-service | modular-monolith | vertical-slice | sqlserver / redis / rabbitmq / openid | multiTenancy(header, shared-db), distributedCaching(l1+l2), idempotency | worker: consumer-rabbitmq |
| **GM-4** `simple-internal` | Small internal app / SME — the narrowest footprint | monolith | clean-architecture | sqlserver / inmemory / **none** / apikey | — | Proof of rule V1 (outbox is rejected when there is no broker — negative test pair) |
| **GM-5** `worker-heavy` | Batch/processing-heavy back office | modular-monolith | vertical-slice | postgres / redis / kafka / openid | outbox, idempotency | workers: scheduler-quartz + consumer-kafka + batch |
| **GM-6** `gateway-front` | API productization / platform facing the outside world | microservice | vertical-slice | postgres / redis / inmemory-broker / saml | idempotency | gateway (autoRegisterServices) + 2 services + REST externalSystem (mock: stub) |

## 2. Coverage Check (every enum value ≥1 GM)

> **Reality note (2026-08-05):** this table is the TARGET, not the present. The
> generator now produces TWO layouts — vertical-slice and `--layout clean-architecture`
> (Domain/Application/Infrastructure/Api; the nightly GmFourClean row proves GM-4's
> codeOrg dimension end to end, real containers included). The microservice half is
> real too (2026-08-05): `goldpath new service` (own db, own `kind: service` manifest)
> and `goldpath new gateway` (YARP over service discovery) — the nightly GmSixGateway
> row proves generate → verbs → build → ROUTED smoke end to end. GM-2/5/6 as WRITTEN
> remain roadmap: oracle/kafka are schema-only and ldap/saml are not in the auth enum
> (their triggers are ledgered). Treat unchecked cells as roadmap, never as coverage. The bulk-only
> adopter question ("we will use ONLY bulk — what do we run?") has its own row since
> 2026-08-05: GmBulkOnly proves bulk + jobs + console on NOTHING but the app database
> (`docs/guide/modules-and-infrastructure.md` is the adopter-facing matrix).

**Read the third column first.** The GM numbers in the middle column are the WRITTEN
personas; they say which persona would exercise a value, not that anything runs. The
2026-09-05 coverage audit found the table read as coverage, and two of its rows named
values (`ldap`, `saml`) the manifest schema does not even accept — so the invariant in the
heading was inverted. The third column is the only column that is a fact.

| Dimension | Value → GM (the written personas) | Actually proven, by a nightly shape |
|---|---|---|
| deploymentModel | monolith→4 · modular-monolith→1,3,5 · microservice→2,6 | monolith, modular-monolith (most shapes) · microservice (GmSixGateway) |
| codeOrg | clean→2,4 · vertical-slice→1,3,5,6 | both (GmFourClean, GmOneClean · every other shape) |
| db | postgres→1,5,6 · sqlserver→3,4 · oracle→2 | postgres, sqlserver. **oracle is SCHEMA-ONLY** — in the enum, no EF provider, no Quartz store, no keyset translation (T16) |
| cache | redis→1,2,3,5,6 · inmemory→4 | both (GmEverything composes redis through the caching recipe; inmemory is the default) |
| broker | rabbitmq→1,3 · kafka→2,5 · none→4 · inmemory→6 | rabbitmq, none. **kafka is SCHEMA-ONLY** — in the enum, no rider |
| auth | openid→1,3,5 · ldap→2 · apikey→4 · saml→6 | openid, apikey (GmApiKey, 2026-09-05), none. **ldap and saml are NOT IN THE SCHEMA AT ALL** — the auth enum is `openid \| apikey \| none`; these two rows describe personas, not gaps in coverage (issue #11) |
| features (Ring B) | idempotency→2,3,5,6 · outbox→2,5 · auditTrail→2 · softDelete→2 · multiTenancy→3 · dataProtection→2 · distributedLocking→2 · distributedCaching→3 | all thirteen as template flags (GmEverything) and all fourteen recipes as `add feature` (GmGrown + GmGrownRest; the fourteenth, `notification`, only since 2026-10-20 — the cell said fourteen while the shapes ran thirteen) |
| worker | consumer-rabbitmq→3 · scheduler-quartz→5 · consumer-kafka→5 · batch→5 | consumer-rabbitmq (GmWorkerQueue), scheduler-quartz (GmWorkerJobs), batch (GmWorkerSchedule). **consumer-kafka is unbuildable** while kafka is schema-only |
| gateway / externalSystems / nfr | 6 / 2(soap,record-replay)+6(rest,stub) / 2 | gateway (GmSixGateway). **externalSystems and nfr are proven by no shape** |

## 3. Execution Model

- **Every pull request that touches a template, the CLI, an analyzer or a core package:** the
  covering subset of the shapes (§6a) × [generate → build → spec-lint → `dotnet test` (smoke:
  live end-to-end flow + health) → tear down], before merge. Every shape runs nightly (§6).
  Containers come from the corporate registry mirror (the offline scenario is deliberately
  tested).
- **Nightly:** plus `goldpath check` for every GM — which is `specdrift validate` + `drift` + db
  status + build in one verb (the Spec Engine also audits its own
  output), and a package version bump rehearsal (Renovate simulation: are the GMs still green
  with the latest core version).
- **Negative corpus:** invalid manifests (started with today's m3/m4) are part of the schema
  test suite; every new V-rule adds at least one negative example.

## 4. Deliberately Uncovered in v1 (no silent gaps)

- worker: `bulk` (its module comes with the execution ladder L3; added to GM-5 with its RFC).
  `scheduler-hangfire` was REMOVED from the schema entirely (2026-07-06, jobs RFC D1):
  Hangfire is LGPL-3.0 — the license gate rejects it, so it is an impossibility, not a gap
- db-per-tenant isolation (multiTenancy's heavyweight mode — a GM-3 variant in the module RFC)
- Ring C modules other than `modules: [yarpGateway]` (the catalog vision; every module RFC
  MUST state which GM it will enter — RFC template §7 "golden manifest impact")

## 5. Evolution Rules

1. A new module RFC → enters at least one GM (otherwise it is not merged).
2. A combination that blows up in the field → a GM update or a regression manifest (never deleted).
3. A GM change demands a review as serious as a schema/template change (these are the product's contract).
4. Every GM is also an example project usable in sales/demos — narrated by its persona name.

## 6. The matrix today (docs-freshness gate keeps this table honest)

Every shape below runs nightly via `scripts/validate-gm.sh` — pack → generate → build →
spec-lint → smoke with real containers (GmOffline through `scripts/validate-gm-offline.sh`, which
runs it a second time with the network denied); the shapes marked **PR** also run on every pull
request that touches what a generated app is built from (§6a). The list lives in
[`.github/gm-shapes.yml`](../../.github/gm-shapes.yml), which `gm.yml` reads for both lanes;
a shape there and a row here may not drift apart, nor may the Runs column and the file's
`pr: true` marks — the gate fails on any direction.

| Shape | Arguments | What it proves | Runs |
|---|---|---|---|
| GmOneAuthDefault | (defaults) | the default shape, auth floor up | nightly |
| GmOneOpenFlow | `--auth none` | open shape drives the full order flow | nightly |
| GmFourSimple | `--db sqlserver --broker none --auth none` | the SqlServer/no-broker quadrant | nightly |
| GmFourClean | `--layout clean-architecture …` | four-project split, migrations in Infrastructure | PR + nightly |
| GmOneClean | `--layout clean-architecture --features fileexchange` (defaults: postgres, broker, auth) | the clean layout on the default providers: the seam-consuming handler lives in Application, the authed FileExchange admin surface, and the NU1903 lift on postgres (found by the preview.8 release, 2026-09-05) | nightly |
| GmCleanFeatures | `--broker none --layout clean-architecture --features audittrail --features idempotency --features dataprotection --features locking` | the clean layout carrying the features whose CODE THE LAYOUT MOVES. Neither clean shape above reaches them: GmFourClean takes no features, and GmOneClean keeps the default broker, which supplies `Mediant.Behaviors` to Application by accident through `Goldpath.Messaging` — hence broker-less here. Added 2026-09-09 after qorpe/coexist generated this shape as its first adopter and it did not build, twice: the entity partials and the command attribute land in Domain and Application, but only the Api csproj carried the feature-conditional package references. `--features audittrail` joined at the preview.9 release: this lane builds against the SOURCE (its local feed shadows nuget.org), so it never had to wait for the train that publishes the `IAuditLogged` move | PR + nightly |
| GmSixGateway | `--auth none` + `new service` + `new gateway` | multi-head by the adopter's own verbs; routed probe | PR + nightly |
| GmWorkerInSolution | `--features audittrail,softdelete,multitenancy,bulk` (auth openid) + `add worker eod --trigger jobs;add worker ingest --trigger queue;add worker tick --trigger schedule;db add Workers` | the in-solution worker inherits the solution's auth floor and features (T25 parity by inheritance): all three workers build, migrate, boot behind the AppHost and pass drift; bulk is the jobs rider whose tables the jobs worker's fleet shares (the verb refuses a jobs worker without one) | PR + nightly |
| GmGrown | `--auth none --broker none` + `add feature audittrail;softdelete;locking;bulk;outbox` + `db add Grown` (green 2026-09-05) | born lean, grown by the CLI's own recipes — the `goldpath add feature` verb proven end to end (plain, provider-gated, jobs-rider-with-console, and the bus-birthing outbox recipe) | PR + nightly |
| GmOneViaCli | `goldpath new solution` (defaults) | the CLI's OWN generation verb end to end — every other shape calls `dotnet new`, so the command the guides teach had no e2e proof until the preview.8 coverage audit (2026-09-05) | nightly |
| GmWorkerViaCli | `goldpath new worker --trigger schedule` | the worker generation verb, same reason | nightly |
| GmApiKey | `--auth apikey --features bulk` | the api-key floor: in the template's choices, the CLI's flag and the wizard, and in no shape until 2026-09-05 | PR + nightly |
| GmGrownRest | `--auth openid` + `add feature multitenancy;idempotency;dataprotection;caching;archival;approvals;fileexchange;campaign;notification` + `db add GrownRest` | the nine recipes GmGrown does not run, grown on ONE app BEHIND an auth floor — `add feature` had fourteen recipes and six e2e proofs. `notification` joined 2026-10-20: choosing the PR subset by coverage found the two grown shapes ran thirteen recipes while this doc said fourteen | PR + nightly |
| GmBulkOnly | `--features bulk --broker none --auth none` | operational module with ONLY the app database | nightly |
| Gm.Dotted | `--broker none --auth none` | dotted solution names (issue #24 regression) | nightly |
| GmConsole | `--features bulk --auth none` | the console SERVES with its own bundle | nightly |
| GmConsoleAuthed | `--features bulk` | the console's 401 floor branch | nightly |
| GmEverything | all thirteen features (`--auth none`) | maximal composition compiles, boots, smokes | PR + nightly |
| GmWorkerQueue | worker template | queue-triggered worker shape | nightly |
| GmWorkerSchedule | worker `--trigger schedule` | schedule-triggered worker shape | PR + nightly |
| GmWorkerJobs | worker `--trigger jobs` | jobs-profile worker shape | nightly |
| GmWorkerJobsAuthed | worker `--trigger jobs --db sqlserver --auth openid --features audittrail,softdelete,locking,fileexchange` | worker concept parity (T25): the management head's auth floor UP — admin surface and console answer 401; the jobs store provider flip on SQL Server | PR + nightly |
| GmWorkerQueueFeatures | worker `--features multitenancy,audittrail,softdelete,dataprotection,locking,notification --auth none` | worker concept parity (T25): the tenant rides the message header, the riders bring the jobs runtime next to the inbox, the console SERVES from the worker's head | PR + nightly |
| GmOffline | `--auth none` (postgres, rabbitmq), run by `validate-gm-offline.sh` | the air-gapped install (GA gate G14): the shape once connected, to fill a local NuGet feed with the closure the app, its `dotnet-ef` tool and the CLI restore and a local registry with every image its containers ran; then again with outbound network denied (nuget.org and Docker Hub checked unreachable first), restoring into an empty packages folder from that feed alone, through `goldpath db bundle`, the bundle applied twice to a real PostgreSQL, and the smoke. Steps: [air-gapped install](../ops/air-gapped-install.md) | nightly |

## 6a. At PR time — the covering subset (open-threads T30)

ADR-0008 asks for the matrix "on every template or core package change"; until 2026-10-20 it
ran only nightly, which is how preview.8 shipped GP2102 and how pull requests kept merging
for days while the nightly was red. `ci.yml` now runs the eleven **PR** shapes above, in
parallel, on every pull request whose files touch what a generated app is built from —
`templates/`, `tools/Goldpath.Cli/`, `analyzers/`, `packages/` (the lane packs the SOURCE
into a feed that shadows nuget.org, so a package change reaches every generated app),
`ui/console/` (its bundle ships inside `Goldpath.Console`), `schemas/manifest/` (spec-lint
validates against it), the root `Directory.*.props`, `global.json`, `nuget.config`, and the
lane itself. The check `golden manifests (PR subset)` always reports, so it can be required.
Wall-clock is one shape's (about three minutes on a hosted runner), not eleven.

The subset is chosen by COVERAGE: every template flag value and every CLI verb that writes
into a generated app runs in at least one PR shape.

| What must be exercised | Values | PR shape(s) |
|---|---|---|
| solution `--db` | postgresql · sqlserver | GmEverything · GmFourClean |
| solution `--broker` | rabbitmq · none | GmEverything · GmCleanFeatures, GmFourClean, GmGrown |
| solution `--auth` | openid · apikey · none | GmCleanFeatures, GmGrownRest, GmWorkerInSolution · GmApiKey · GmEverything |
| solution `--layout` | vertical-slice · clean-architecture | GmEverything · GmCleanFeatures (with the features the layout moves), GmFourClean (on sqlserver) |
| solution `--features` | all thirteen | GmEverything |
| worker `--trigger` | queue · schedule · jobs | GmWorkerQueueFeatures · GmWorkerSchedule · GmWorkerJobsAuthed |
| worker `--db` | postgresql · sqlserver | GmWorkerQueueFeatures · GmWorkerJobsAuthed |
| worker `--auth` | openid · none · apikey | GmWorkerJobsAuthed · GmWorkerQueueFeatures · **no shape in either lane** (below) |
| worker `--features` | all seven | GmWorkerJobsAuthed (audittrail, softdelete, locking, fileexchange) + GmWorkerQueueFeatures (multitenancy, audittrail, softdelete, dataprotection, locking, notification) |
| `goldpath add feature` | all fourteen recipes | GmGrown (5) + GmGrownRest (9) |
| `goldpath add worker` | jobs · queue · schedule | GmWorkerInSolution |
| `goldpath new service` / `new gateway` | — | GmSixGateway |
| `goldpath db init` / `db add` | — | every shape / GmGrown, GmGrownRest, GmWorkerInSolution |

Left to the nightly, each because its values are already in the subset and what it adds is
a combination, not a code path a PR-time shape misses:

- **GmOneAuthDefault** — the defaults; GmGrownRest is born as exactly this shape before it grows.
- **GmOneOpenFlow**, **Gm.Dotted**, **GmBulkOnly** — `--auth none` and `--broker none`
  variants of shapes above; Gm.Dotted guards one naming regression (#24).
- **GmFourSimple** — sqlserver on vertical-slice; sqlserver is proven by GmFourClean.
- **GmOneClean** — clean on the default broker; the clean layout is proven twice above.
- **GmConsole**, **GmConsoleAuthed** — the console serves (GmEverything, GmWorkerQueueFeatures)
  and answers 401 (GmWorkerInSolution — openid with bulk, a superset of GmConsoleAuthed — and GmWorkerJobsAuthed) in the subset.
- **GmOneViaCli**, **GmWorkerViaCli** — `goldpath new solution|worker` is a strict passthrough
  to the same `dotnet new` call every other shape makes, followed by `db init`.
- **GmWorkerQueue**, **GmWorkerJobs** — the bare queue and jobs workers; the subset runs both
  triggers with their features.

**Proven by no shape, in either lane:** the worker template's `--auth apikey`. It is a
template choice (the `UseApiKey` branch) that GmApiKey proves only for the solution.
