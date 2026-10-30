# RFC: Goldpath Console — the InfraOps surface (UI phase)

Status: **ACCEPTED** (2026-07-14) — D1 (React+Tailwind, dist-shipped; supersedes the
RCL wording), D2 (triage-first home) and D3 (component tests + the GmConsole nightly
shape) approved by the owner. U1 (kit) is live. **Revision OIDC-1** (the console signs in by OIDC,
GA gate G6) is PROPOSED 2026-10-29 at the end of this document.
Visual contract: `docs/strategy/ui-standard-v1.md` (adopted). Locked antecedents:
ONE run console; the UI knows CAPABILITIES, not levels; products (such as Mockifyr)
own their UI — the console links, never embeds; UI is written ONCE against the full,
sample-proven capability set.

## 1. What the console is

The shipped InfraOps surface of every Goldpath app: ALL module screens out of the box,
riding the FROZEN admin contract (`goldpath-admin-contract.md`) and nothing else — the
console is a client of the same API adopters script, which is why the contract froze
first. It is also the asset's extension point: adopters get a dashboard they can
custom-develop ON, with the same kit, the same way they add features to the backend.

## 2. Architecture — three layers, mirroring backend composition

1. **`ui/kit`** — the token system + primitives of ui-standard-v1 (npm package,
   versioned with the train). Everything an adopter's custom screen needs; nothing
   Goldpath-schema-specific.
2. **`Goldpath.Console`** — the screens, capability-driven:
   - **Run console** (the core): fleets → jobs → runs → chunk breakdown → repair queue;
     verbs trigger/pause/resume/reschedule/rerun/replay-items with confirm + audit hints.
   - **Module panels**, lit by capability discovery: bulk intake (upload/report/
     four-eyes gate), campaign governor (pacer + LIVE throttle), notification evidence
     views (masked), archival (holds/erasure/verify).
   - **Capability discovery**: the console probes each registered service's admin
     surfaces (the frozen route roots); a 404 root = capability absent = the panel does
     not exist. No manifest upload, no config drift — the API is the truth here too.
3. **The adopter's console app** — references `Goldpath.Console`, adds custom pages
   with the kit. Extension is composition: a route table contribution, not a fork.

## 3. N services, one console — the service registry

- **Within one app**: nothing to configure — n workers share the app database and the
  fleet registry is store-discovered (jobs D9); the API's admin surface already speaks
  for every fleet. CorPay proves it: api + payments + eod under one console today.
- **Across services**: a registry of entries `{ name, adminBaseUrl }` — config-file or
  Aspire service discovery. Each service contributes its capability panels under its
  name; cross-service home aggregates them. SHIPPED (U4, 2026-07-27): the console reads
  `console.config.json` from where it is served — adding a service is a config change,
  never a rebuild — with `?base=` as the dev override and same-origin as the default.
  Switching service re-runs discovery from scratch: one service's panels must never
  appear under another's name. A registry that fails to load is ANNOUNCED, because an
  operator who configured four services and silently sees one is looking at the wrong
  console.
- **Auth** (settled in U4, 2026-07-27 — and deliberately NOT an IdP client of our own):

  1. **Same origin — the shape we ship.** The console is served by the app it drives
     (`MapGoldpathConsole`), behind the SAME ops floor as the admin surfaces. The browser
     is challenged for the console's own document, so an unauthenticated operator never
     reaches a screen at all; every subsequent call rides whatever the app's floor already
     established (cookie, or a gateway's header). Nothing to configure, and no way to ship
     an unauthenticated console by accident — proven in the smoke: the console mapped on
     the auth-floored host answers 401 for its own page.
  1b. **On a MULTI-TENANT app.** The console's own page is exempt from tenant resolution
     (it is a static document, and a browser cannot put a tenant header on one — a
     multi-tenant app served a bare 400 where the console should be until preview.4). The
     admin CALLS it makes stay tenant-scoped exactly as R1 says, so an app resolving the
     tenant by SUBDOMAIN works end to end, while a header-resolving app has the console
     report its surfaces as refused, in the server's words. A tenant PICKER for operators
     holding the all-tenants privilege is open-threads T11.
  2. **Cross-service.** The console sends its calls with credentials, so each additional
     service must (a) sit under the same auth as the console's origin, or (b) allow that
     origin explicitly in CORS *with* credentials. There is no third option that does not
     involve a token the console would have to hold.
  3. **A token the console holds is NOT built** (superseded once [Revision OIDC-1](#revision-oidc-1--the-console-signs-in-oidc-proposed-2026-10-29-merging-the-pr-that-adds-this-section-is-its-acceptance) is accepted: the console signs in by OIDC as an opt-in, and this case stays true for a console with no IdP configured). A browser-side OIDC client would put an
     identity library in the dist and a per-adopter authority/clientId in config, which is
     a different product decision from "the console is a client of the app's floor". Until
     an adopter needs it, the honest answer is (1) and (2) — and the console says so when
     a service refuses: a call blocked by CORS is reported as UNREACHABLE, never as an
     absent module, because "we could not ask" and "they do not have it" are different
     sentences.
  4. **Read and act are two roles** (admin-contract [R4.1](goldpath-admin-contract.md), PROPOSED
     2026-10-25; owner decision 2026-10-20 on #297).
     - The console's page, its config and every admin `GET` demand `goldpath-ops-read`, and
       `goldpath-ops` satisfies it. Every verb demands `goldpath-ops`.
     - A viewer opens every screen. A verb they press is refused with `403` and the verb
       envelope, and the console shows the server's sentence. It does not hide the button: it
       cannot know the caller's roles without asking, and the refusal is the explanation.
     - `AdminClient` reads the envelope on `403` and `412` as well as `200`/`400` (R4-S2).
- **External products**: Mockifyr (mocks) and other products appear as configured LINK
  tiles — their UI is theirs. SHIPPED on `main` (2026-10-19, #257; decision record
  [goldpath-console-link-tiles.md](goldpath-console-link-tiles.md)), published with the next
  train: `AddLink(name, url)` adds a `links` array beside `services` in `console.config.json`
  (a links-only app OMITS `services`, because an empty list reads as a broken registry), and
  Today draws one native-link tile per product, plus "Open {name}" in the palette. A link is
  NEVER probed — a tile asserts an address, not health. The URL is a root-relative path or an
  absolute http(s) address without credentials; anything else is refused in `AddLink`, again
  at `MapGoldpathConsole` startup, and once more by the console's reader.

## 4. Decisions

- **D1 — Stack: React + Tailwind, dist-shipped (supersedes the earlier RCL wording).**
  One design system and one stack across the product family (the Mockifyr lineage);
  adopters NEVER run Node — CI builds the dist, `Goldpath.Console` ships it as embedded
  static assets served by `MapGoldpathConsole()` on the management head. SHIPPED (U4,
  2026-07-27): the package refuses to PACK without a built console; the registry comes
  from the app's own configuration (`AddService`), not a JSON file someone has to copy
  beside the dist; the console sits behind the same ops floor as the admin surfaces; a
  missing asset 404s rather than being answered with the page (a blank screen with a green
  status code is the worst failure a console can have); and an unknown path 404s too,
  because the console has no client-side routes YET — when it gains them, the server must
  also inject a `<base href>` (open-threads T9). The "no Node
  in generated apps" principle holds by construction. Custom pages: the kit npm package
  for teams that build UI, and a config-driven link/iframe-free tile row for those that
  do not — the product console tiles of §3 (`AddLink`, 2026-10-19).
- **D2 — The operator's first screen: cross-service TRIAGE ("today").**
  Red/overrun-predicted runs, repair-queue depths, gates awaiting four-eyes, DLQ depth —
  each row deep-links into its panel. Fleet browsing is one click away, never the
  landing page: operators open consoles to answer "is anything wrong", not to browse.
  SHIPPED (U4, 2026-07-27). Three properties it must keep, because each was a decision:
  (a) every number comes from the contract's own take-bounded lists — the console invents
  no aggregate the API does not expose — so the screen PRINTS its scope instead of
  implying completeness; (b) a surface the console cannot read (no ops role, or a call it
  cannot scope) is itself a row: blindness during an incident is the most important thing
  an operator can be told, and it is grouped per service so it cannot bury the estate's
  real problems; (c) a surface that dies mid-read is a row too — triage never drops a
  service quietly.
- **D3 — Proof bar (UI is claims-are-proofs too).**
  Kit: component tests (vitest) on the composites (keyset table paging, verb button's
  refusal surface, state mapping), under a COVERAGE FLOOR that CI enforces (kit 95/90,
  console 97/85 statements/branches) — the floor exists because the gaps that hid real
  bugs here were branches nobody had thought to exercise, not tests anyone had deleted.
  ACCESSIBILITY is part of the bar, not a later polish pass: every panel and the confirm
  dialog are checked with axe (WCAG 2.1 A/AA, serious + critical) in the same smoke. An
  ops console is used at 3am over a remote session, keyboard-only, on whatever screen is
  to hand — its first run found the secondary text below the contrast threshold and a
  confirm dialog that Escape could not close. Console: the smoke drives THREE real apps — one open,
  one behind the auth floor, one tenant-scoped — because how the console behaves when it
  is REFUSED is as much a claim as how it behaves when it is welcome; plus a service that
  dies mid-session. Runnable on demand (`.github/workflows/console-smoke.yml`), not only
  nightly. Playwright smoke against a REAL generated
  app: SHIPPED (U4, 2026-07-27) as TWO golden-manifest shapes, because one could not say
  it — `GmConsole` (a module, no auth) proves the generated app serves the console AND its
  own bundle, and `GmConsoleAuthed` (the same shape with the floor up) proves the page
  answers 401. The matrix had no shape combining auth with an operational module, so the
  guarded branch would have shipped unexecuted. DRIVING that generated console in a
  browser stays with the console smoke, which owns Playwright. No screenshot-diff theater;
  behavior only.

## 5. Phases

| Phase | Delivers | Exit gate |
|---|---|---|
| U1 | `ui/kit`: tokens + primitives + composites | component tests green; kit gallery page |
| U2 | Run console over the registry (single-app path) | **MET** (2026-07-26): `scripts/console-smoke.sh` drives the real console against a real Goldpath app (Postgres + Quartz + the frozen surface) — capability discovery, confirm gate, run reaching terminal, repair queue replayed; runs nightly |
| U3 | Module panels (bulk/campaign/notification/archival), capability-lit | **MET** (2026-07-27): all four panels ship, and the console smoke drives every module's real verbs against one app — bulk's four-eyes gate (including the engine's refusal on invalid rows), the campaign governor over a real broker (release, throttle, pause, resume, abort), the notification evidence in all three kinds (sent, failed, suppressed, recipients masked), and archival's chain verification, keyed retrieval, legal hold, the hold's refusal of an erasure, and the erasure itself — after which the chain still verifies |
| U4 | Cross-service registry + triage home + auth story + `MapGoldpathConsole()` + GmConsole in nightly | the full D3 bar; the console driven against CorPay and its screenshots into the README (open-threads T1) |
| U5 | The scheduling surface: the Runs section opens into fleet status, jobs (with their triggers), calendars and a filterable run history | **MET** (2026-07-28): four sections behind an ARIA tab strip, every one driven in `scripts/console-smoke.sh` against a real fleet — `pause-all`/`resume-all` (T13's 03:00 verb, now reachable), a second trigger added to a declared job and removed again, a calendar created and deleted, reschedule, and a history filtered by state and date over a real keyset walk. Triggers live INSIDE their job rather than in a list of their own: a trigger without the job it fires is a name and a cron string. Job authoring stays refused (ADR-0001) and the absence is asserted, in the route table and on screen. The smoke found three things unit tests could not: a fleet status that reported the console's own management connection instead of the cluster, a form that swallowed its success message when it closed, and a quiet button whose outcome nobody rendered |
| U6 | The styling pass — one sweep over spacing, density, filter layout, panel rhythm | **MET** (2026-07-28): a component vocabulary (`card`, `row-card`, `control`, `control-label`, `btn-quiet`, `chip`, `link-action`, `section-title`) now lives in the kit's ONE token file — composed from existing tokens, no new hex (§6) — and every panel plus the kit's own primitives swept onto it, so the next re-skin stays a one-file change. Density: timestamps quieted to wall-clock seconds by STRING surgery (`shortStamp` — never through `Date`, which would shift the server's clock into the viewer's), instance names truncate with the full value in `title`. Proven the only way cosmetics can be: the console smoke 30/30 with the axe gate green, both unit suites untouched, and the README pictures re-captured from the restyled console |
| U7 | The FAMILY pass — owner feedback batch v1.1 (ui-standard-v1 §7): lucide icons, grouped sidebar with left-border active + persistent icon-rail collapse, ⌘K, kit Table + right-Sheet row detail, facet filters, pill tabs, stat cards, page headers, cross-screen links | in batches, kit-first so every screen inherits; axe + smoke green per batch; screenshots re-captured at the end | scenario campaign (phase 2) starts when the OWNER calls U7 done |

## 6. Non-goals

No CRUD generator for business entities (vertical slices own their screens); no
embedded Grafana re-implementation (panels link/embed the boards the modules already
ship); no theme marketplace — one standard, one accent swap.

## Revision OIDC-1 — the console signs in (OIDC) (PROPOSED 2026-10-29; merging the PR that adds this section is its acceptance)

**Why now.** Owner decision 2026-10-21 (GA readiness RFC Q7): console OIDC is a 1.0 gate, **G6**
(open-threads T10), because "a split-origin enterprise deployment is the common case". §3's auth
case 3 said a token the console holds is NOT built. This revision supersedes case 3 and keeps
cases 1, 1b, 2 and 4 exactly as they are. Nothing in it is built yet: each slice below carries its
own red → green proof.

### OIDC-1.1 What changes, and what does not

- **Opt-in, per console.** A console whose configuration names no identity provider behaves
  byte-for-byte as today:
  - it is served behind the app's own floor (case 1);
  - its calls carry `credentials: "include"`;
  - it holds no token.

  The served console with no IdP at all is the default, and it stays so. The existing
  `scripts/console-smoke.sh` journeys and the `GmConsole`/`GmConsoleAuthed` shapes are the
  regression proof that this stays true.
- **With an IdP configured**, the console signs the operator in once. It then sends
  `Authorization: Bearer <token>` with `credentials: "omit"` to every service the registry names.
  The services are the apps' own admin surfaces under the existing `openid` strategy
  (`Goldpath.Auth`, JwtBearer). The token is validated against each service's own
  `Goldpath:Auth:Authority` and `Audience` (audit A3 already refuses a missing audience).
- **The admin contract is unchanged.** It keeps its routes, envelopes, admin-contract R1 tenant scoping
  and admin-contract R4.1's read and act roles. A bearer token is one more way to carry the principal those rules
  already judge.

### OIDC-1.2 The flow

**Authorization code with PKCE, in the browser, as a public client.** There is no client secret:
a browser cannot keep one.
- **Library:** the console composes **`oidc-client-ts`**, pinned exactly to `3.5.0`
  (Apache-2.0, published 2026-03-13; its one dependency is `jwt-decode` ^4, MIT, resolved by the lockfile). The flow is
  not hand-rolled (ADR-0003).
- **Licence gate:** the pin is gated in slice C-1 before the library enters the graph (OIDC-1.7).
- **Sign-in:** the console redirects to the authority, returns to its own `…/console/` path,
  exchanges the code, and removes the `code` and `state` parameters from the address bar.
- **The token lives in memory only.** The console uses `InMemoryWebStorage` for the user store,
  never `localStorage` or `sessionStorage`, so a script injected into the page cannot read a
  token that outlives the tab.
- **The sign-in state is tab-scoped, short-lived and token-free.** A redirect flow must keep the
  PKCE `code_verifier` and `nonce` across the navigation to the IdP and back, so they cannot live
  in memory. `oidc-client-ts` 3.5.0 defaults its `stateStore` to `localStorage`, which outlives the
  tab and is shared by every tab of the origin, so the console sets it explicitly:
  `stateStore: new WebStorageStateStore({ store: window.sessionStorage })`. The callback removes
  the entry it redeems; `staleStateAgeInSeconds` bounds an abandoned one, and the console calls
  `clearStaleState()` at start-up. The state never holds a token.
- **A reload loses the token.** The console then redirects once more. The IdP's own session
  makes that round trip silent for an operator who is still signed in there.
- **Nothing the console writes outlives the tab.** The only sign-in artefact that does is the
  IdP's session cookie, on the IdP's origin. `localStorage` stays empty.

### OIDC-1.3 Where the authority and the client id come from (no per-adopter rebuild)

The dist is built once by CI and shipped embedded in `Goldpath.Console` (D1). Configuration
therefore travels the way the service registry already does: the app that serves the console
writes it at runtime.
- **`GoldpathConsoleOptions.UseOidc(authority, clientId, scopes)`**, or configuration under
  `Goldpath:Console:Oidc` (`Authority`, `ClientId`, `Scopes`). The console options bind no
  configuration today, so this section is new. Configuration wins over code, so each
  environment can point at its own IdP without a rebuild.
- **A new anonymous route, `…/console/console.auth.json`.** It answers
  `{ "authority", "clientId", "scopes" }`, values that are public by the nature of a public
  client. The console must read them before anyone is signed in. When no IdP is configured, the
  route answers `404`, and the console takes that as "no sign-in, case 1".
- **`console.config.json`, which holds the service registry, stays behind the floor.** With OIDC
  on, it is fetched with the bearer token like any admin `GET`. Internal service addresses are
  never served to an anonymous caller.
- **The page shell (HTML, JS, CSS) is served anonymously when OIDC is configured.** It carries no
  data, and a browser cannot attach a bearer token to a document navigation. It stays behind the
  floor when OIDC is off, unchanged. This is **Q1** below.
- **Each registry entry may name the token its service accepts:**
  `{ name, adminBaseUrl, audience?, scopes? }`, with `AddService(name, url, audience?, scopes?)`.
  An entry without them uses the console's default token.

### OIDC-1.4 One sign-in, several services: the token's audience

Services validate the token's `aud` against their own `Audience`. Two IdP behaviours are
common, and the console supports both:
1. **One token, many audiences.** A Keycloak audience mapper, or any IdP that mints a
   multi-audience token. Each service accepts the same token, and the console holds one.
2. **One token per resource.** Microsoft Entra ID mints one resource per access token. The
   console then redeems the one refresh token once per distinct `scopes` set, on a path it owns:
   - **Not `UserManager`.** In 3.5.0, `UserManager.signinSilent` takes the refresh-token path
     with the user's original scope (it forwards `resource` and `extraTokenParams`, never a
     scope), and stores the one `User` it gets back, replacing the console's default token.
   - **`OidcClient.useRefreshToken({ state, extraTokenParams: { scope } })` instead.** 3.5.0
     spreads `extraTokenParams` after its own `scope`, so the console's set is the one sent.
     `state` is an object of `RefreshState`'s shape built from the current `UserManager` user
     (the class is typed, not exported at runtime). This call never reaches
     `UserManager.storeUser`.
   - **A console-owned in-memory map** holds each access token, keyed by the sorted scope set
     the console **requested**, never by the response's `scope`: when the IdP omits `scope`,
     3.5.0 reports the original scope there, which would file B's token under A's key. The
     default token stays the `UserManager` user's.
   - **Rotation.** When the IdP rotates refresh tokens, the new one is written back onto the
     `UserManager` user, so the next redemption, for any service, uses it.
   - **One queue for every redemption, the default renew included.** 3.5.0's
     `automaticSilentRenew` defaults to `true`, and that renew runs `signinSilent` on the
     library's own timer, outside any queue the console keeps, and stores a new `User`. So the
     console sets `automaticSilentRenew: false` and drives the default token's renew itself
     (OIDC-1.5), through the same single-flight queue as the per-scope redemptions. One refresh
     token is then never presented twice at once (under rotation with reuse detection, a second
     presentation can revoke the whole token family and sign the operator out), and no
     redemption's write-back overwrites another's rotated refresh token.

   A service whose token cannot be obtained becomes a triage row ("signed in, but not for this
   service"), never a silent omission (D2(b)).

The proof drives case 1 against a real IdP container (OIDC-1.6). Case 2 is proven at unit level
against the published 3.5.0 code paths named above (`OidcClient.useRefreshToken` and the token
request it sends, never a hand-written stand-in for the library), and slice C-5 carries its red
proof. Whether a hosted Entra tenant joins the nightly is **Q3**.

### OIDC-1.5 CORS, expiry and the honest refusals

- **CORS (slice C-3).** A service the console reaches from another origin must answer the
  preflight and allow that origin with `Authorization` and `Content-Type`, **without**
  `Allow-Credentials`, because the console sends none.
  - `Goldpath.Sdk` gains an opt-in `AddGoldpathConsoleOrigins(params string[] origins)` and the
    `Goldpath:Console:AllowedOrigins` configuration, both applied to the admin groups only.
  - A preflight is answered before the auth floor, as the ASP.NET CORS middleware does, and a
    test pins it.
  - Every actual call still meets the floor.
  - Wildcards are refused at startup: `*` with an auth header is a mistake, not a posture.
- **Bearer tokens close the CSRF gap for this mode.** The console sends no cookie, so a foreign
  page's form has nothing to ride. That is threat-model T-ADM-07 / #452 for OIDC deployments.
  Gateway-cookie deployments keep #452 open.
- **Expiry.** The console renews before expiry with the refresh token (rotating, in memory). It
  drives that renew itself (`automaticSilentRenew: false`), through the one single-flight queue
  OIDC-1.4 case 2 describes, so the library's timer never redeems the refresh token beside a
  console redemption.
  - A `401` from a service triggers one silent renew and one retry of a **read**.
  - A **verb** is never retried automatically. It answers "your sign-in expired — sign in and
    run it again", because a verb run twice is not a read run twice.
  - A second `401`, or a failed renew, shows "signed out" with a sign-in button, and the triage
    home lists every service as unreadable for that reason (D2(b)).
- **`401` is never shown as `403`.** A `401` means "who are you" and asks for sign-in. A `403`
  stays the server's own sentence: admin-contract R4.1's role refusal, admin-contract R1's
  tenant refusal. The console does not
  hide verbs from a viewer; the refusal is the explanation (case 4).
- **The IdP is unreachable.** The console says so, names the authority, and offers a retry. It
  does not fall back to cookie mode, because a silent mode switch would hide a misconfiguration.
- **Sign-out** ends the console's tokens and calls the IdP's end-session endpoint when the
  discovery document names one.

### OIDC-1.6 The proof plan (G6's bar)

The console smoke gains a second mode, `scripts/console-smoke.sh oidc`, and a job in
`console-smoke.yml` and the nightly.
- **A real IdP container: Keycloak, pinned by exact tag.** The realm is imported from a committed
  file with three users: an operator (`goldpath-ops`), a viewer (`goldpath-ops-read`) and a user
  with neither role. It has a public client with PKCE required and an audience mapper for both
  services. The image is pre-pulled, and the realm file is local, so the mode runs air-gapped
  (CLAUDE.md invariant).
- **Two services on two origins.** Two `Goldpath.Jobs.TestHost` instances under the `openid`
  strategy, on different ports, each with its own `Audience`, and the console allowed by
  `AddGoldpathConsoleOrigins`. The console itself is served from a third origin by Vite, the
  split-origin case.
- **Journeys, in Playwright, through Keycloak's real login form:**
  - the operator signs in once and reads both services, and a verb on each is applied;
  - the viewer reads both, and every verb shows the server's `403` sentence;
  - the no-role user sees both services as triage rows ("refused");
  - a token expired in the IdP renews silently for a read, and the next verb asks to sign in
    again;
  - with the IdP stopped, sign-in says so;
  - sign-out returns to the sign-in screen with the tokens gone from memory.
- **axe stays green on every screen**, including the signed-out and IdP-unreachable states.
- **Regression:** the existing smoke (served console, no IdP) runs unchanged and green in the
  same job.
- **Red proofs**, one per slice. Examples:
  - a token sent to the wrong audience is refused by the service;
  - a console that put the token in `sessionStorage` fails the unit fact asserting no token is in
    web storage;
  - a console that left `stateStore` at the library's default fails the fact asserting
    `localStorage` stays empty through a full sign-in;
  - a wildcard origin is refused at startup.

### OIDC-1.7 Slices (each ≤ ~3 hours of agent time, in order)

| Slice | Delivers | Red → green proof |
|---|---|---|
| C-1 | The npm licence gate. `scripts/license-gate.py`, or a sibling, reads `ui/console`'s production dependency graph from `pnpm-lock.yaml`, applies the same allowlist, and runs in `gates`. Then `oidc-client-ts@3.5.0` (exact) enters `dependencies` | A planted GPL dependency turns the gate red. The current graph is green |
| C-2 | Server configuration: `UseOidc`, `Goldpath:Console:Oidc`, the anonymous `console.auth.json` (404 when off), the anonymous shell only when OIDC is on, and `AddService(…, audience, scopes)` | Console route tests: the auth route is anonymous and minimal; the registry stays behind the floor; with OIDC off, every route is byte-identical to today |
| C-3 | `AddGoldpathConsoleOrigins` with preflight-before-floor, no credentials, and wildcards refused | Sdk tests: the preflight answers; the actual call still gets `401` without a token; a wildcard refuses at startup |
| C-4 | The console signs in: `UserManager` with PKCE, in-memory user store, a `sessionStorage` state store with stale-state cleanup, redirect handling; `AdminClient` sends the bearer token with `credentials: "omit"` per service | Vitest against the **real** `oidc-client-ts` 3.5.0 `UserManager`, with only the IdP's endpoints faked: the bearer goes per service; no access or refresh token is in `localStorage` or `sessionStorage` at any point; `localStorage` stays empty through a full sign-in; the sign-in state is gone from `sessionStorage` after the callback; with no `auth.json` the console runs in case-1 mode. Leaving `stateStore` at its default turns the `localStorage` fact red. Coverage floors held |
| C-5 | Expiry and refusals: silent renew, the one retry for reads only, a verb never retried, `401` vs `403` wording, IdP-unreachable and signed-out states, sign-out. Case 2 of OIDC-1.4: per-scope-set redemption through `OidcClient.useRefreshToken`, the console-owned token map keyed by the requested set, the rotated refresh token written back, one single-flight queue for every redemption including the default renew (`automaticSilentRenew: false`) | Vitest on each branch; a planted retry of a verb turns its fact red. Case 2, against the real `oidc-client-ts` 3.5.0 with only the token endpoint faked: refreshing for service B leaves service A's token **and** the shared refresh token usable, with rotation on. Routing the redemption through `UserManager.signinSilent` turns it red (B's token arrives with A's scope, and the default user is replaced). With rotation on, a default renew and a redemption for B issued together leave A's token, B's token and the shared refresh token usable; leaving `automaticSilentRenew` at its default `true` turns it red, deterministically: the fact uses fake timers advanced to the library's expiring notification (`expires_in` − `accessTokenExpiringNotificationTimeInSeconds`), so the library's own renew fires inside the fact rather than whenever the clock allows. An IdP that omits `scope` in the response still files B's token under B's requested set |
| C-6 | `console-smoke.sh oidc` with Keycloak pinned, the realm file, two origins, the OIDC-1.6 journeys and axe, in `console-smoke.yml` and the nightly | The full mode green. A wrong audience on one service turns its journey red (CI link) |
| C-7 | Docs: Console README, `ops/` runbook (Keycloak and Entra setup, the two audience shapes, CORS), upgrade guide, T10 closed, `ga-gate-status` G6 | `docs-freshness`; T10's proof is C-6's green run |

### OIDC-1.8 Questions for the owner (each with a recommendation)

- **Q1 — The anonymous shell.** With OIDC on, may the console's static shell and
  `console.auth.json` be served without the floor? **Recommended: yes.** The shell carries no
  data; the registry and every admin call stay behind the floor; and a browser cannot put a
  bearer token on a document request. The alternative, keeping the shell behind a gateway cookie,
  makes OIDC useless exactly where it is needed.
- **Q2 — The library.** **`oidc-client-ts` 3.5.0, recommended.** It is maintained (3.5.0, March 2026),
  Apache-2.0, has one MIT dependency, and no framework coupling. The
  alternative is a hand-rolled PKCE flow, which ADR-0003 argues against.
- **Q3 — Entra in CI.** **Recommended: no hosted tenant in CI** (no secrets, no cloud account,
  and the air-gapped rule). The Entra shape, one token per resource, is proven at unit level and
  documented in the runbook. A hosted proof waits for an adopter on Entra (a T10 follow-up row).
- **Q4 — Token renewal.** **Recommended:** refresh tokens with rotation, held in memory.
  Hidden-iframe silent renew is not used: third-party-cookie blocking in current browsers breaks
  it.
