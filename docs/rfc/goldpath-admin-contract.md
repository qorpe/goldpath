# Goldpath admin API — the frozen contract (H8)

Status: **FROZEN** (2026-07-12). This document is the UI phase's input: the single run
console is written ONCE against this surface. Any change below this line is a breaking
change and needs this document updated in the same PR (and, after NuGet release, a
versioning note per H7).

## Conventions (all five surfaces)

- **Mount**: `Map<Module>Admin<TContext>(prefix, exposeUnsecured)` on the management head;
  default prefix `/goldpath/admin/<module>`. Fail-closed: every endpoint demands
  `GoldpathPolicies.Ops` unless `exposeUnsecured: true` (visible opt-out + startup warning).
- **Verbs are POSTs with kebab-case names** (`trigger`, `replay-items`, `lift-hold`,
  `pause-all`). No PATCH; `PUT`/`DELETE` only for true upserts/removals (calendars).
- **Verb envelope**: every mutating verb returns `GoldpathAdminResult { ok, message }` —
  `200` when `ok`, `400` with the same envelope when refused (the message is the reason;
  never a silent 200). Unexpected exceptions stay with the platform's problem+json.
  Missing entities on GET answer `404` with no body.
- **Actor**: taken from the authenticated identity (`anonymous` fallback); every mutating
  verb writes an audit row server-side — the UI never supplies the actor.
- **Paging**: list endpoints take `?take=` (defaults 50–200 per endpoint) and clamp to
  `[1, 500]` (`AdminPaging.Clamp`, compile-linked into all five modules). Large ordered
  sets offer keyset paging (`afterRow` on the bulk validation report).
- **Failure nouns**: execution failures answer to **`failures`** everywhere
  (campaign `/{id}/failures`, notification `/failures`; bulk execution failures live in
  the JOBS repair queue via the run detail). Bulk's `/batches/{id}/errors` is the
  **validation report** — a different concept, deliberately a different noun.
- **Audit reads are a capability, not a baseline**: jobs exposes a global `/audit`,
  campaign a per-entity `/{id}/audit`; the other modules' evidence lives in their domain
  rows (decisions on the batch, erasure receipts). The console shows an audit tab only
  where the capability exists.

## Route inventory (frozen)

### jobs — `/goldpath/admin/jobs`
| Method | Route | Returns |
|---|---|---|
| GET | `/fleets` | fleet list |
| GET | `/fleets/{fleet}/jobs` | `GoldpathJobInfo[]` |
| GET | `/fleets/{fleet}/runs?job=&take=` | run list |
| GET | `/runs/{runId}` | `GoldpathRunDetail` (chunks by status + open failures) |
| POST | `/fleets/{fleet}/jobs/{job}/trigger` · `/pause` · `/resume` · `/reschedule` | `GoldpathAdminResult` |
| POST | `/fleets/{fleet}/pause-all` · `/resume-all` | `GoldpathAdminResult` |
| POST | `/runs/{runId}/rerun` · `/replay-items` | `GoldpathAdminResult` |
| GET/PUT/DELETE | `/fleets/{fleet}/calendars[/{name}]` | calendar CRUD |
| GET | `/audit?take=` | admin audit trail |

### archival — `/goldpath/admin/archival`
| Method | Route | Returns |
|---|---|---|
| GET | `/definitions` | definition list |
| GET | `/entries/{definition}/{key}` | entry detail (natural key) |
| POST | `/entries/{definition}/{key}/hold` · `/lift-hold` · `/erase` | `GoldpathAdminResult` |
| GET | `/holds?includeLifted=&take=` · `/erasures?take=` | evidence lists |
| POST | `/definitions/{definition}/verify` | `GoldpathAdminResult` |

### bulk — `/goldpath/admin/bulk`
| Method | Route | Returns |
|---|---|---|
| GET | `/definitions` | intake numbers per definition |
| POST | `/batches/{definition}` (body = file) | `GoldpathBulkBatchInfo` |
| GET | `/batches?definition=&state=&take=` · `/batches/{batchId}` | batch list / detail |
| GET | `/batches/{batchId}/errors?afterRow=&take=` | VALIDATION report (keyset) |
| POST | `/batches/{batchId}/approve` · `/reject` | `GoldpathAdminResult` |

### notification — `/goldpath/admin/notification` (read-only)
| Method | Route | Returns |
|---|---|---|
| GET | `/templates` | registered templates |
| GET | `/notifications?state=&template=&tenant=&take=` · `/notifications/{id}` | evidence list / detail |
| GET | `/suppressions?take=` · `/failures?take=` | filtered evidence views |

### campaign — `/goldpath/admin/campaign`
| Method | Route | Returns |
|---|---|---|
| GET | `/?state=&take=` · `/{id}` | campaign list / detail |
| GET | `/{id}/failures?take=` | failed items (item replay stays with jobs `replay-items`) |
| GET | `/{id}/audit?take=` | per-campaign audit |
| POST | `/` (create) · `/{id}/pause` · `/resume` · `/abort` · `/throttle` | `GoldpathAdminResult` |

### approvals — `/goldpath/admin/approvals` (T21 console federation, 2026-08-28)
| Method | Route | Returns |
|---|---|---|
| GET | `/requests?status=&ladder=&take=` · `/requests/{id}` | recent requests (R3 repeats) / detail with trail + signatures |
| POST | `/requests/{id}/approve` · `/reject` (body: role, reason) | `GoldpathAdminResult` — a refusal's message is the ENGINE rule's name (`FourEyesViolation`, `ReasonRequired`, …) |

Decisions run the engine unchanged: the caller's principal is the decider, so four-eyes,
distinct-eyes, the rung's role and the mandatory rejection reason all hold on this surface
exactly as anywhere else. The store is not tenant-scoped (the approvals model predates R1's
scoping and carries no tenant column), so no `?tenant=` parameter exists to lie with.

### fileexchange — `/goldpath/admin/fileexchange` (T22 console federation, 2026-09-03; read-only)
| Method | Route | Returns |
|---|---|---|
| GET | `/rails` | every declared rail with live counts (files archived, quarantine depth, last archive) — the probe root answers `[]`-shaped even before a file arrives |
| GET | `/files?rail=&take=` | recent files, newest archive first (in-flight files first); R3 repeats on `rail` |
| GET | `/quarantine?rail=&file=&take=` | quarantined rows, oldest first, with the reason and when they FIRST quarantined (an upsert on reprocess keeps the age); R3 repeats on both. File names carry dots and slashes, so they ride the QUERY, never the path |

READ-ONLY by construction, like notification: a file arrives through the composed transport
and its pick-up job, and re-delivering it IS the reprocess (the engine dedups on the
`(rail, file, line)` key) — a console verb that "reprocessed" without the file's bytes would
only pretend. The reads ride a separate `IGoldpathFileLedgerQueries` seam both shipped ledgers
implement; a custom ledger without it makes `MapGoldpathFileExchangeAdmin` refuse at STARTUP
with the teaching text. Not tenant-scoped (the ledger carries no tenant column), so no
`?tenant=` parameter exists to lie with.

## Freeze mechanics

- The campaign surface carries a route-freeze test (`RouteContractTests`) — the pattern
  any surface adopts when the UI starts depending on it. The approvals and fileexchange
  surfaces freeze through their endpoint tests over real HTTP (`ApprovalAdminEndpointsTests`,
  `FileExchangeAdminTests`).
- Breaking fixes applied at freeze time (cheap now, expensive after the UI):
  campaign `/{id}/failed-items` → `/{id}/failures`; `take` clamp made uniform across all
  five modules (was jobs/archival-only).

## Revision R1 — tenant scoping (ACCEPTED 2026-07-24, lands at the preview.3 boundary)

**Finding (independent audit, A1 — CRITICAL):** the data layer is fail-closed, the admin
surfaces are not. No admin endpoint reads `ITenantContext`; where tenant filtering exists
at all (bulk, archival, notification) it is a CLIENT-SUPPLIED `?tenant=` query parameter,
and campaign has none. In a deployment with per-tenant operators, `goldpath-ops` alone
lets any operator read every tenant's batches, evidence and archives.

### The rule (all five surfaces, uniform)

1. **Ambient tenant is the scope.** When `multiTenancy` is on, every admin read and verb
   is scoped to the ambient `ITenantContext` — resolved exactly like the business
   endpoints (header/subdomain per the manifest). No ambient tenant on a multi-tenant
   app → `400` with a teaching envelope (fail-closed, never "all tenants").
2. **Cross-tenant is a second, explicit privilege.** The `?tenant=` override (and the
   implicit "all tenants" view) demands `GoldpathPolicies.OpsAllTenants`
   (`goldpath-ops-all` role) ON TOP of `GoldpathPolicies.Ops`. Without it: `403`.
   With it, `?tenant=` narrows and omitting it means "all" — today's semantics, now
   privilege-gated — and every crossing is logged with the actor, the requested tenant
   and the ambient one (structured warning on `Goldpath.AdminSurface`).
3. **Single-tenant apps are untouched.** `multiTenancy: false` keeps today's behavior
   byte-for-byte; the scoping layer compiles to a pass-through. The signal is the marker
   `AddGoldpathMultiTenancy` registers (`GoldpathMultiTenancyMarker`) — NOT the presence
   of an `ITenantContext`, which other modules register for their own flow (messaging
   propagates the tenant of a consumed message). Amended 2026-07-27 after the console
   gate caught a single-tenant app whose admin surfaces began refusing the moment a
   broker joined the composition.
4. **One shared seam.** The scoping lives in ONE shared-source file
   (`packages/shared/AdminTenantScope.cs`, compile-linked like the auth floor) that the
   endpoints call — not per-module reimplementations. The ADR-0005 companion analyzer
   rule (`GP0904`: an admin endpoint taking a tenant parameter without the seam — 0902 was taken) follows
   in the same preview.3 train.
5. **Surfaces without tenant-stamped rows (campaign)** are inherently cross-tenant on a
   multi-tenant app: the WHOLE surface demands `GoldpathPolicies.OpsAllTenants` (an
   endpoint filter on the group). Single-tenant apps see no change.
6. **The same logic per-endpoint where a single surface mixes both kinds** (review-agent
   findings on the R1 PR, all accepted): archival's hold/lift-hold/erase verbs and its
   holds/erasures lists operate on rows keyed WITHOUT a tenant column — on a multi-tenant
   app they demand the privilege outright; bulk's `/errors` scopes through its batch's
   tenant (a foreign batch id answers like an absent one); notification's
   `/suppressions` + `/failures` scope like its other lists.

### Why this is a contract REVISION, not a break-and-hope

Routes, nouns, envelopes and paging are unchanged. What changes is authorization
semantics (`?tenant=` becomes privilege-gated) and default scope (ambient, not "all") —
a behavioral break permitted at a preview boundary per the H7 versioning promise, shipped
with an upgrade-guide entry. The UI phase (U2+) is written against THIS revision.

## Revision R2 — the scheduling surface (PROPOSED 2026-07-28)

**Finding.** The console covers what the five modules *do*, but only half of what the
fleet *is*. An operator can trigger a job and read its runs; they cannot see why a job
will fire at 03:00, which node ran the last one, whether a run was scheduled or triggered
by hand, or what a trigger's misfire policy is — and they cannot answer "show me
yesterday's failures" without walking a take-bounded list. A comparable Quartz management
screen carries all of it, and an adopter who has seen one will read the gaps as missing
capability rather than deliberate scope.

The gap splits three ways, and only the middle one is a contract change.

### 1. Already frozen, never put on screen (no contract change — U5 work)

`pause-all` / `resume-all`, `reschedule`, the calendar CRUD, and the global `/audit` are
all in the inventory above and none of them has a screen. `pause-all` is the one that
matters at 03:00: it is the single verb an operator reaches for during an incident, and
today the console cannot reach it. Booked as **open-threads T13**; the screens land in
U5 with the routes below.

### 2. Facts the store holds and the contract does not carry (ADDITIVE — this revision)

Additive only: no route is renamed, no envelope changes shape, and every field below is
*added* to a payload the console already reads. A client written against R1 keeps working.

| # | Addition | Why an operator needs it |
|---|---|---|
| R2.1 | `GET /fleets/{fleet}/status` → scheduler state: `runningSince`, `threadPoolSize`, `jobsExecuted`, `isShutdown`, plus the `nodes` already on `GoldpathFleetInfo` | "Is this fleet alive, and how big is it?" is the first question of an incident, and today it is answered by inference from whether runs appear |
| R2.2 | `GoldpathTriggerInfo` widens: `type` (cron\|simple), `priority`, `misfireInstruction`, `timeZoneId`, `startAt`, `endAt`, `timesTriggered`, `repeatInterval`, `repeatCount` | A cron string alone does not explain a fire time. Timezone and misfire policy are the two fields that make a "why did it not run?" answerable |
| R2.3 | `GoldpathJobRun` gains `triggeredBy` (`Scheduled`\|`Manual`\|`Rerun`\|`Replay`) | Who started this run. **Cost: a migration** (one nullable column) — the only schema change in R2. *Amended during implementation on both counts: the instance that ran it was already there as `StartedBy` and only needed showing, and `Replay` earned its own value — labelling a repair-queue redrive as a plain `Rerun` would have been the sort of small lie this column exists to prevent.* |
| R2.4 | `GET /fleets/{fleet}/runs` gains `?status=`, `?from=`, `?to=`, and keyset `?afterId=` | "Yesterday's failures" must not be a scroll. Keyset follows the bulk validation report's precedent (`afterRow`), not offset paging |
| R2.5 | `POST /fleets/{fleet}/jobs/{job}/triggers` (add) · `DELETE /fleets/{fleet}/jobs/{job}/triggers/{trigger}` (remove) | A declared job may legitimately need a second schedule (month-end as well as nightly). `reschedule` stays the frozen shorthand for the 90% case: changing the one cron a job has |
| R2.6 | Job detail exposes its **job data map, read-only** | Diagnosis needs to see the parameters a run was given. Editing them is drift (see §3) |

Derived facts stay derived: a job's health rollup ("healthy / failing / mixed") and its
"last run 3 minutes ago" are computed by the console from runs it already reads. The
contract gains no aggregate endpoint it would then have to keep true.

### 3. Deliberately REFUSED — runtime authoring (the constitution decides this)

A comparable screen lets an operator pick a job class from a server-provided list, fill a
data map, and create a job; and delete one. **Goldpath will not**, at any layer:

- **Creating or deleting a JOB is a manifest change** (ADR-0001: the manifest is the
  single source of truth; composition is compile-time). A job created at runtime is a
  production behaviour that exists in no manifest, no review and no repository — the exact
  drift the constitution exists to prevent. There is no `available-classes` endpoint,
  because reflecting the assembly's job types into a picker is the first half of that
  feature.
- **Changing a job's data map at runtime is the same drift, quietly**: the job would then
  behave differently from the code that declares it. Read-only, per R2.6.
- **SCHEDULING is not authoring, and stays open**: triggers, calendars, pause/resume and
  reschedule are configuration about *when* a declared job runs. They are already audited
  server-side (iron rule 2), they survive restart in the Quartz store, and an operator
  who cannot move a run out of a maintenance window will move it by editing the database.

Owner decision, 2026-07-28: adopt this three-way split as written.

### Rejected, with the reason recorded

- **Quartz `standby`/`start` at the scheduler level.** It looks like the natural "stop
  everything" button and is a footgun in a cluster: standby applies to the node that
  received the call, so an operator who "stopped the fleet" has stopped one instance while
  the others keep firing — and it is lost on restart. `pause-all` is durable, cluster-wide
  and already frozen. One way to stop a fleet, and it is the one that works.
- **Offset paging on runs.** Ordered sets that grow while being read are exactly where
  offset paging skips and duplicates rows; keyset is the house rule.

### Test plan (the DoD for the implementation step)

1. **Unit**: the widened DTO carries every Quartz fact for both a cron and a simple
   trigger; `triggeredBy` is stamped `Manual` by the trigger verb, `Rerun` by rerun,
   `Replay` by the repair redrive and `Scheduled` by the scheduler path.
   *(The run list moved to integration: it orders by `DateTimeOffset`, which SQLite cannot
   translate at all — proving it on the package's own sqlite fixture would have proven it
   on a store Goldpath does not ship on.)*
2. **Integration (real Postgres + real Quartz)**: two-instance fleet — a run stamped with
   the instance that executed it; `?from`/`?to`/`?status` against a seeded window;
   `afterId` walks the whole set with no gap and no repeat; a second trigger added to a
   declared job fires and is removed again; the migration applies to a database written by
   the previous train.
3. **Refusal proofs**: there is no route that creates a job (the absence is asserted, so
   nobody adds one quietly); the scheduling verbs stay behind the ops floor and write
   audit rows; on a multi-tenant app they obey R1's scoping.
4. **Console (U5)**: every route above is driven in `scripts/console-smoke.sh` against a
   real fleet, and the axe gate stays green.

### Why this is a revision, not a break

Additive fields on existing payloads, new routes alongside the frozen ones, and one
migration that adds two nullable columns. R1 clients keep working unchanged. Shipped in
preview.5 (the scheduling train), with an upgrade-guide entry for the migration.

## Revision R3 (2026-08-02) — repeatable filters

Additive; nothing an R2 client sends changes meaning.

- The list filters `?status=` (jobs runs), `?state=` (bulk batches, campaigns,
  notifications), `?template=` (notifications) and `?definition=` (bulk batches —
  joined 2026-08-03 with T8/#72, before the revision ships) may now REPEAT:
  `?state=Failed&state=Suppressed`. Several values of one filter are OR'd; different
  filters still AND together. A single value behaves exactly as R2 did; an absent
  filter still means "all". Unknown values are ignored rather than refused — a
  filter that matches nothing returns an empty list, the same claim R2 made.
- Motivation: the console's facet filters are multi-select (ui-standard v1.2 §8.13),
  and the console refuses to fake OR by merging take-bounded pages client-side —
  the server is the only honest place to widen a filter.

## Revision R4 — a read role, failures by code with a cursor, an outcomes aggregate, a versioned throttle, the ceiling routes (PROPOSED 2026-10-25; merging the PR that adds this section is its acceptance)

**Finding.** A coverage analysis on 2026-10-20 (a device-fleet adopter, the same one that
produced Campaign R3) found three things the frozen surface cannot say:

1. **Whoever can see can also act.** Every admin route demands one policy,
   `GoldpathPolicies.Ops` (`AdminSurfaceGuard.Apply` puts it on the whole group), so the person
   who should only watch a campaign can also abort it ([#297](https://github.com/qorpe/goldpath/issues/297)).
2. **The campaign governor cannot answer "what is failing, and why".**
   `GET /campaign/{id}/failures` is capped at `AdminPaging.MaxTake` (500) with no cursor. Its row,
   `GoldpathCampaignFailedItem(Seq, Error, CompletedAt)`, leaves out the item's `ErrorCode`, which
   the store has kept since Campaign R2.1. Nothing splits the outcomes by code
   ([#296](https://github.com/qorpe/goldpath/issues/296)).
3. **Two operators' throttles race and the last one silently wins.** Campaign R3.5 gives the
   campaign a `PolicyVersion` and the service a conditional throttle. The HTTP shape is this
   revision's to write ([#298](https://github.com/qorpe/goldpath/issues/298)).

Campaign R3.2's live ceilings also need routes that the frozen surface does not have. Campaign R3
Q5 put them here, so the contract is revised and frozen once.

**Owner decisions.**
- **2026-10-20** (on #296, #297 and #298): admin-contract R4 is approved. It covers the
  `/failures` cursor and `ErrorCode`, the `/outcomes` aggregate, and an optional `If-Match` /
  `ETag` on throttle. The additive read role `goldpath-ops-read` is approved too, and
  `goldpath-ops` also satisfies it. This deliberately reverses Campaign R2's "finer roles than
  the ops floor" non-goal; the campaign RFC records the reversal.
- **2026-10-21** (Campaign R3 Q5): the ceiling routes fold into R4.

This text comes first, docs-only ([#304](https://github.com/qorpe/goldpath/issues/304)). Each code
slice below updates the route inventory above in the same PR that maps its route. That is the
freeze rule, and it is unchanged.

| # | Addition | The sentence for it |
|---|---|---|
| R4.1 | A read policy, `GoldpathPolicies.OpsRead` (role `goldpath-ops-read` by default), that `goldpath-ops` also satisfies. Every admin `GET` demands it; every other method still demands `goldpath-ops`. A verb refused for want of the role answers `403` with the verb envelope, in words | "the duty manager watches the push; only the operator can stop it" |
| R4.2 | `GET /campaign/{id}/failures` returns each item's `errorCode` and walks the whole set by keyset (`?afterSeq=`) | "show me every device that failed, not the first five hundred" |
| R4.3 | `GET /campaign/{id}/outcomes`: the campaign's items counted by outcome, and its failures counted by error code | "four thousand failed — three thousand five hundred of them `ACK_TIMEOUT`" |
| R4.4 | The campaign detail carries its `PolicyVersion` as a strong `ETag`; `throttle` accepts an optional `If-Match`. A stale one answers `412` with the verb envelope | "my TPS cut must not be undone by a quota change made from a stale screen" |
| R4.5 | The ceiling routes of Campaign R3.2: `GET /campaign/ceilings`, `POST /campaign/ceilings/global/throttle`, `POST /campaign/ceilings/target-systems/{name}/throttle`, `GET /campaign/ceilings/audit` | "the device core takes two hundred a second, and tonight it takes a hundred" |

### R4.1 — the read role ([#297](https://github.com/qorpe/goldpath/issues/297))

**Rule — the HTTP method decides, with no list of exceptions.** `GET` is a read (so would `HEAD`
be, should a surface ever map one). `POST`, `PUT` and `DELETE` are verbs. This holds on all
seven surfaces (jobs, archival, bulk, notification, campaign, approvals, fileexchange) and on the
served console (`MapGoldpathConsole`, every route a `GET`).
- **A viewer can open the console and read every screen.**
- **A viewer reads archived documents in full** (owner decision 2026-10-25, on this revision's
  pull request). `GET /archival/entries/{definition}/{key}` returns `GoldpathArchiveEntry` as
  stored, `Document` included: the archived aggregate, unmasked, within the caller's tenant
  scope (R1). It is a `GET`, so the method rule makes it a read, and it stays one.
  - **Why:** answering a retrieval request ("show me what we held on this customer") is
    exactly the duty the read role exists for, and it needs the content, not its metadata.
  - **What it means for an adopter:** granting `goldpath-ops-read` grants the content of every
    archived document in the holder's scope — where personal data sits under legal hold and
    erasure. Where that content is sensitive, withhold the role. R4-S1 must put this in the
    upgrade guide (see *Compatibility*).
  - **Not built:** a viewer that sees an entry's metadata but not its document. It would be a
    masking reader like Notification's `MaskedRecipient`, not a route exception; open-threads
    T50 carries it with its trigger.
- **Every verb stays an operator's.** That includes archival's `verify`, a `POST` that changes no
  row: it walks a whole hash chain, and spending that cost is an operator's call. A rule that
  depends on reading each handler is a rule a new route can quietly break. The method is what the
  route-table test can see.

**Policies, and how they compose.**

| Policy | Satisfied by (default roles, `Goldpath:Auth`) | Demanded by |
|---|---|---|
| `GoldpathPolicies.OpsRead` = `"goldpath-ops-read"` (new) | `OpsReadRole` (`goldpath-ops-read`, new option) **or** `OpsRole` (`goldpath-ops`) | every admin `GET`, and the console's page and `console.config.json` |
| `GoldpathPolicies.Ops` = `"goldpath-ops"` (unchanged) | `OpsRole` | every admin `POST` / `PUT` / `DELETE` |
| `GoldpathPolicies.OpsAllTenants` = `"goldpath-ops-all-tenants"` (unchanged) | `OpsAllTenantsRole` | R1's cross-tenant widening, on top of whichever of the two above the route demands |

- **`goldpath-ops` satisfies the read policy.** So no existing operator loses a route, and an
  identity provider that grants only today's role sees no change. That is what makes the
  revision additive.
- **R1 composes unchanged.** On a multi-tenant app, a cross-tenant viewer holds
  `goldpath-ops-read` + `goldpath-ops-all-tenants`, and a cross-tenant operator holds
  `goldpath-ops` + `goldpath-ops-all-tenants`.
- **Campaign's surface stays whole-group cross-tenant** (R1 rule 5). A viewer of it on a
  multi-tenant app therefore needs both roles, exactly as an operator does today.

**Where it is applied: still ONE file.**
- `AdminSurfaceGuard.Apply` puts `OpsRead` on the group instead of `Ops`.
- It also adds, to every endpoint in the group whose method is not `GET`/`HEAD`, an endpoint
  filter that authorizes `Ops` through `IAuthorizationService`.
- The filter, not a second `RequireAuthorization`, is what lets the refusal speak.
  - **What the refusal looks like:** `403` with `GoldpathAdminResult { ok: false, message }`,
    whose message names the missing role: *"this verb needs the goldpath-ops role; this identity
    can read this surface but not act on it"*.
  - **Why authorization metadata cannot do it:** the authorization middleware would answer the
    same `403` with an empty body, and the console could only say that the call failed.
- An unauthenticated caller still meets the group's policy first and gets today's `401`
  challenge.
- **The order, said plainly:** endpoint filters run after model binding. A viewer's verb body is
  therefore parsed, but never acted on. A body that does not bind answers the binder's `400`
  before the role check can answer `403`. Nothing reaches a handler either way.
- `exposeUnsecured: true` keeps meaning "no policy at all", and it keeps its warning.

**Registration.**
- `AddGoldpathAuth` registers `OpsRead` as `RequireRole(OpsReadRole, OpsRole)` (any one of the
  two), in both branches: the authenticated strategies, and `Strategy = None`, where audit A4
  already registers the ops policies so that a refusal is a `403`, never a `500`.
- **An app that registers its own policies without `Goldpath.Auth`** keeps working: it may never
  define `goldpath-ops-read`.
  - When `AuthorizationOptions.GetPolicy(OpsRead)` is null at map time, the guard demands `Ops`
    for reads too, which is R3's behaviour exactly.
  - It logs one information line on `Goldpath.AdminSurface` naming the policy to register.
  - An unregistered policy is never allowed to become a `500` at request time.

**Public API (additions only).**
```
const Goldpath.GoldpathPolicies.OpsRead = "goldpath-ops-read" -> string!
Goldpath.GoldpathAuthOptions.OpsReadRole.get -> string!
Goldpath.GoldpathAuthOptions.OpsReadRole.set -> void
```
`AdminSurfaceGuard.Apply`'s signature does not change. Its behaviour changes for `GET`s
(additively) and for the body of a role refusal on verbs: an empty `403` becomes an envelope
`403`.

**The console.**
- `AdminClient.send` reads the envelope on `403` (and on `412`, R4.4) as well as on `200`/`400`.
  Today any status other than those two throws, and the verb is reported as "did not reach the
  server".
- A viewer who presses *pause* therefore reads the server's sentence as a refusal (S-OPS-02).
- The console does not hide verbs from a viewer: it has no way to know the caller's roles
  without asking, and a hidden button explains nothing. The refusal is the explanation.

**Proof.**
- **A route-table test** (a new file, as #297 asks) maps all seven surfaces and the console on
  one host, enumerates every endpoint under their prefixes, and asserts: `OpsRead` on every
  `GET`, and the `Ops` filter on every other method.
  - It is red today, because every `GET` demands `Ops`.
  - A new route cannot ship without falling into one of the two buckets.
- **Over real HTTP:**
  - a `goldpath-ops-read` identity reads every surface (`200`) and is refused every verb
    (`403` + envelope naming the role);
  - that identity's `GET /archival/entries/{definition}/{key}` returns the entry's `Document`
    (the disclosure above, pinned so that a later masking change is a decision, not a drift);
  - a `goldpath-ops` identity does both;
  - no identity gets `401`;
  - an app that never registers `OpsRead` gets R3's behaviour and the log line.
- **Console smoke (S-OPS-02):** a viewer opens the governor, presses *pause*, and reads the
  refusal. Axe stays green.

### R4.2 — failures by code, with a cursor ([#296](https://github.com/qorpe/goldpath/issues/296))

| Method | Route | Returns |
|---|---|---|
| GET | `/{id}/failures?afterSeq=&take=` | failed items in `Seq` order, after `afterSeq` (exclusive); each row `{ seq, error, errorCode, completedAt }` |

- **Keyset, by the item's own key.** `Seq` is unique within a campaign, part of the primary key
  `(CampaignId, Seq)`, and never reused.
  - `afterSeq` absent or `0` starts from the beginning, as today. A full page means "ask again
    after its last `seq`"; a short page is the end.
  - This follows the bulk report's `afterRow` and the jobs runs' `afterId`, not an offset. The set
    grows while it is read (R2's reason).
- **`take` keeps its default (100) and the uniform `[1, 500]` clamp.**
- **`errorCode`** is `GoldpathCampaignItem.ErrorCode`, at most 64 characters. It holds one of:
  - the code a handler gave `Failed(code, …)` (R2.1);
  - `HANDLER_EXCEPTION` for a throw;
  - `ACK_TIMEOUT` for an acknowledgement nobody sent;
  - a callback's own code.

  It is `null` for an item that failed under R1, which stored no code.
- **Additive on the wire.** An R3 client that sends no `afterSeq` gets today's first page,
  ignores the new field, and keeps working.
- **In the API,** `GoldpathCampaignFailedItem` is a shipped positional record, so the code
  joins as an init-only property, not a fourth positional parameter.
- **The service** gains an overload with the cursor. The shipped
  `GetFailedItemsAsync(id, take, ct)` calls it with `afterSeq: 0`.

```
Goldpath.GoldpathCampaignFailedItem.ErrorCode.get -> string?
Goldpath.GoldpathCampaignFailedItem.ErrorCode.init -> void
Goldpath.GoldpathCampaignAdminService<TContext>.GetFailedItemsAsync(System.Guid id, long afterSeq, int take, System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Goldpath.GoldpathCampaignFailedItem!>!>!
```

**Proof.** A campaign with 1,203 failed items is walked at `take=500` in three pages, with no gap
and no repeat. Every row carries the code its handler or callback gave. A page asked for after
the last `seq` is empty. On the route-freeze test the route text is unchanged, because a query
parameter is not part of the pattern; the contract table above gains `afterSeq` in the slice's PR.

### R4.3 — the outcomes aggregate ([#296](https://github.com/qorpe/goldpath/issues/296))

| Method | Route | Returns |
|---|---|---|
| GET | `/{id}/outcomes` | `{ byState: { "<item state>": count, … }, failedByCode: [ { code, count }, … ], failedCodesTruncated }` |

- **`byState` counts the campaign's items by item state.**
  - The keys are the `GoldpathCampaignItemState` names, `Pending` through the newest. A state
    with no items is omitted.
  - It answers from one `GROUP BY State` over the items of one campaign, which the
    `(CampaignId, State, …)` indexes serve.
- **`failedByCode` counts the `Failed` items by `errorCode`.**
  - It is ordered by count (descending), then by code (ordinal), with `code: null` last among
    equal counts. The order is fixed in the query, not left to the database: PostgreSQL and
    SQL Server sort nulls at opposite ends.
  - At most 20 entries; `failedCodesTruncated: true` says when more codes exist.
  - Items with no code are counted under `code: null`, never dropped.
- **The first aggregate route, by owner decision.** R2 said the contract "gains no aggregate
  endpoint it would then have to keep true". The owner approved this one on 2026-10-20 (#296),
  because counting thirty million rows by code is not something a client can do from take-bounded
  pages. The rest of R2's rule still holds:
  - the route counts rows and derives nothing;
  - it has no ratio, ETA or "health" field. The console computes those from these counts and the
    detail it already reads.
- **Cost, said plainly.** This is the first admin read that scans more than one page. On a
  30-million-item campaign it is two grouped counts per call.
  - The console asks for it when the governor opens and on the governor's poll (#296, every 5 s
    while the tab is visible).
  - The slice measures the call on a 1M-item campaign (the size `campaign-benchmarks.md`
    already enumerates) and publishes the number there, with profile, date and commit.
  - If it exceeds the governor's poll interval, the console polls this route less often than the
    detail. No new index is added for it; Campaign R3 holds the train to one migration.

**Public API (additions only).**
```
Goldpath.GoldpathCampaignOutcomes
Goldpath.GoldpathCampaignOutcomes.GoldpathCampaignOutcomes(System.Collections.Generic.IReadOnlyDictionary<string!, long>! ByState, System.Collections.Generic.IReadOnlyList<Goldpath.GoldpathCampaignCodeCount!>! FailedByCode, bool FailedCodesTruncated) -> void
Goldpath.GoldpathCampaignOutcomes.ByState.get -> System.Collections.Generic.IReadOnlyDictionary<string!, long>!
Goldpath.GoldpathCampaignOutcomes.ByState.init -> void
Goldpath.GoldpathCampaignOutcomes.FailedByCode.get -> System.Collections.Generic.IReadOnlyList<Goldpath.GoldpathCampaignCodeCount!>!
Goldpath.GoldpathCampaignOutcomes.FailedByCode.init -> void
Goldpath.GoldpathCampaignOutcomes.FailedCodesTruncated.get -> bool
Goldpath.GoldpathCampaignOutcomes.FailedCodesTruncated.init -> void
Goldpath.GoldpathCampaignCodeCount
Goldpath.GoldpathCampaignCodeCount.GoldpathCampaignCodeCount(string? Code, long Count) -> void
Goldpath.GoldpathCampaignCodeCount.Code.get -> string?
Goldpath.GoldpathCampaignCodeCount.Code.init -> void
Goldpath.GoldpathCampaignCodeCount.Count.get -> long
Goldpath.GoldpathCampaignCodeCount.Count.init -> void
Goldpath.GoldpathCampaignAdminService<TContext>.GetOutcomesAsync(System.Guid id, System.Threading.CancellationToken ct) -> System.Threading.Tasks.Task<Goldpath.GoldpathCampaignOutcomes?>!
```
An unknown campaign id answers `404` with no body (the convention for a missing entity).

**Proof.**
- A seeded campaign with items in five states and failures under three codes plus one with no
  code answers exactly those counts.
- 25 distinct codes answer 20 entries with `failedCodesTruncated: true`, and their order is
  stable.
- An unknown id answers `404`.
- The route-freeze test gains the line `GET /goldpath/admin/campaign/{id:guid}/outcomes`.

### R4.4 — a versioned throttle: `ETag`, `If-Match`, `412` ([#298](https://github.com/qorpe/goldpath/issues/298), the HTTP half of Campaign R3.5)

- **`GET /{id}`** answers with `ETag: "<PolicyVersion>"`: a strong entity tag, the decimal
  version in quotes. The body also carries `policyVersion` (R3.5), so a client that cannot read
  headers still has it.
- **`POST /{id}/throttle`** accepts an optional `If-Match` (RFC 9110 §13.1.1, strong
  comparison).

  | `If-Match` | Meaning | Answer |
  |---|---|---|
  | absent | last writer wins (R3, scripts) | `200` / `400` as today; the version moves once per applied throttle |
  | `*` | "any current representation" (RFC 9110) | the same as absent |
  | one strong tag equal to the current version | the policy did not move since the read | `200` / `400` as today; the version moves by one |
  | one strong tag not equal to it, or a weak tag (`W/"4"`, which strong comparison never matches) | the policy moved since the read | **`412`** with `GoldpathAdminResult { ok: false, message }`: R3.5's teaching text, naming the current version, who changed it and when. Audited as `throttle-refused` with both versions |
  | not an entity tag at all (`4` without quotes, `"abc"`, a list of several tags) | a malformed precondition | `400` with the envelope, saying what the header must look like. A list is refused rather than matched, so "any of these versions" is never accepted by accident |

- **The `412` and the `400` carry the same envelope as every refusal.** The console reads both
  (R4.1's client change).
  - The console always sends the version it read.
  - On a `412` it re-reads the campaign and keeps the operator's draft, so the retry is one
    click.
- **The response to an applied throttle carries the new `ETag`.** The console's next throttle
  from the same screen does not need a re-read to stay current.

**Proof.**
- Over real HTTP:
  - two throttles carry the same `If-Match`: the first `200`, the second `412`, and the audit
    shows one `throttle` and one `throttle-refused`;
  - two throttles without one both apply, and the version moves by two;
  - `*`, a weak tag, a bare number and a list each answer as the table says.
- Console smoke: a stale form is refused, re-reads, and the operator's retry succeeds (Campaign
  R3.5's console proof).
- The route-freeze test is unchanged: no route is added.

### R4.5 — the ceiling routes (Campaign R3.2, owner decision Q5)

| Method | Route | Returns |
|---|---|---|
| GET | `/ceilings` | `GoldpathCampaignCeilingInfo[]`: the global row and every target system, each with its live `maxTps`, the code value (`codeMaxTps`), the types that name it, and who changed it last |
| POST | `/ceilings/global/throttle` (body: `{ maxTps?, clearMaxTps? }`) | `GoldpathAdminResult` |
| POST | `/ceilings/target-systems/{name}/throttle` (same body) | `GoldpathAdminResult`. Clearing is refused here: a target system without a ceiling is a typo, and the refusal says to set a high number instead (R3.2) |
| GET | `/ceilings/audit?take=` | the ceiling changes, newest first (`GoldpathCampaignAudit` rows with `CampaignId = Guid.Empty`, action `ceiling`) |

- The routes sit under the campaign mount, so they inherit everything below unchanged:
  - R1 rule 5's whole-group all-tenants filter;
  - R4.1's split: a viewer reads the ceilings, and an operator changes them;
  - the verb envelope.
- `{id:guid}` keeps the detail routes from ever matching `ceilings`.
- `{name}` is the declared target-system name. An unknown name answers `400`, naming the
  declared ones; it is not a `404`, because the verb's subject is a name and not an entity the
  caller looked up.
- The public API is Campaign R3.2's block. R4 adds no type of its own here.

**Proof.** Campaign R3.2's (R3-S9, [#318](https://github.com/qorpe/goldpath/issues/318)):
- a change through the verb bites within one leader tick, and the audit row carries the actor;
- a viewer reads `/ceilings` and is refused the verb;
- the route-freeze test gains the four lines.

### Compatibility — why this is a revision, not a break

- **No route is renamed or removed. No envelope changes shape. No existing field changes
  meaning.**
- An R3 client keeps working against an R4 server:
  - it sends no `afterSeq` and no `If-Match`, and it ignores `errorCode`, `policyVersion`,
    `ETag` and the new routes;
  - an operator it authenticates as `goldpath-ops` reads and acts exactly as before.
- **Two behaviours change, both for the better and both said in the upgrade guide:**
  - a role refusal on a verb now has a body (an envelope `403` instead of an empty one);
  - a `GET` that refused `goldpath-ops-read` with `403` now answers it.
- **One disclosure the upgrade guide states in words** (owner decision 2026-10-25): granting
  `goldpath-ops-read` grants archive retrieval, every archived document's full content in the
  holder's tenant scope (R4.1). An adopter whose archive holds sensitive content withholds the
  role, or grants it only where that read is intended.
- **No migration.** `PolicyVersion` and the ceilings table are Campaign R3-S1's (#314), already
  on `main`. `ErrorCode` has been on the items table since R2.1.
- Ships with whichever train carries its slices. Each slice's PR moves its routes into the
  inventory above and adds its upgrade-guide line.

### Rejected, with the reason recorded

- **A per-route list of read and mutate.** Every new route would need its author to remember
  the list, and a forgotten entry fails open or closed depending on its default. The method rule
  is checkable by a test that needs no list.
- **Hiding verbs from a viewer in the console.** The console would need to know the caller's
  roles, which means either a new "who am I" route or a token the console holds (§3 case 3 of
  the console RFC, not built). The refusal in words already tells the viewer what they lack.
- **`IAuthorizationMiddlewareResultHandler` for the worded `403`.** It is a global service: it
  would exist only where `Goldpath.Auth` registered it, and it would reshape every `403` in the
  app, not the admin surfaces'. The guard's endpoint filter is scoped to the routes it guards
  and works under any authentication an adopter composes.
- **The version in the throttle body instead of `If-Match`.** The owner chose the HTTP
  precondition (2026-10-20). It is also the shape gateways, caches and generic clients already
  understand.
- **Offset paging on failures, or a larger `take`.** Offset paging skips and repeats rows in a
  set that grows while it is read. A larger clamp only moves the wall.

### Implementation slices (after acceptance; each one issue, each ≤ ~3 hours of agent time)

In dependency order. Each slice carries:
- its tests, red first;
- its PublicAPI lines;
- the contract inventory rows it adds, in the same PR;
- the README and runbook text;
- the upgrade-guide line.

| Slice | R4 item | Issue | Depends on | Scope |
|---|---|---|---|---|
| R4-S1 | R4.1 | [#297](https://github.com/qorpe/goldpath/issues/297) | acceptance | `GoldpathPolicies.OpsRead`, `OpsReadRole`, the registration in both strategy branches, `AdminSurfaceGuard`'s split and its worded `403` filter, the unregistered-policy fallback, the cross-surface route-table test, the real-HTTP role matrix (the viewer's archive retrieval included), the *Conventions* mount line above ("every endpoint demands `GoldpathPolicies.Ops`") rewritten to the split, and the upgrade-guide line that granting `goldpath-ops-read` grants archive retrieval |
| R4-S2 | R4.1 | filed on acceptance (#297's console half) | S1 | `AdminClient.send` reads `403`/`412` envelopes; the viewer's refused verb on every verb button; console smoke S-OPS-02's viewer journey; axe |
| R4-S3 | R4.2 | [#296](https://github.com/qorpe/goldpath/issues/296) (part 1) | acceptance | `afterSeq`, `ErrorCode` on the row, the overload, the 1,203-row walk |
| R4-S4 | R4.3 | filed on acceptance (#296 part 2) | acceptance | `/outcomes`, its two records, the route-freeze line, the measured cost on the 1M-item bench campaign |
| R4-S5 | R4.4 | filed on acceptance (#298's HTTP half) | R3-S4 ([#298](https://github.com/qorpe/goldpath/issues/298)) | `ETag` on the detail and the throttle response, `If-Match` parsing and its five cases, the `412`, the console's re-read on refusal, the stale-form smoke |
| R4-S6 | R4.5 | [#318](https://github.com/qorpe/goldpath/issues/318) (R3-S9) | R3-S8 ([#290](https://github.com/qorpe/goldpath/issues/290)) | as Campaign R3-S9 states; its routes enter the inventory under this revision |
| R4-S7 | R4.2 + R4.3 | filed on acceptance (#296's console half) | S2, S3, S4, [#294](https://github.com/qorpe/goldpath/issues/294) | the governor's 5-second poll while the tab is visible, the binding constraint (#294), the outcomes by code, the failures walk with codes; the console's 97% floors hold |

S1, S3 and S4 depend on nothing but this revision's merge, and can land in any order. The R4
row in `open-threads.md` (T50) carries what this revision deliberately leaves out.

### Deliberately NOT in this revision

- **An admin-only tier above `goldpath-ops`** (for example, only an administrator may *abort*, or
  may change a ceiling). Trigger and proof: open-threads T50.
- **Binding the approver role outside Approvals.** The approver stays the rung's role inside a
  ladder. Four-eyes on a campaign start is Campaign R3.7 through `Goldpath.Approvals`, not a
  fourth admin role. Trigger and proof: T50.
- **Push updates (SSE / SignalR) for the governor:** P2 (#296's out of scope). R4.3's cost is
  measured against polling.
- **Exports of item outcomes:** unchanged from Campaign R2. The failures walk and R4.3's counts
  are what an operator reads; a file is the app's own reporting.
