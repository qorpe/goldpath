# RFC: Goldpath Console — the InfraOps surface (UI phase)

Status: **ACCEPTED** (2026-07-14) — D1 (React+Tailwind, dist-shipped; supersedes the
RCL wording), D2 (triage-first home) and D3 (component tests + the GmConsole nightly
shape) approved by the owner. U1 (kit) is live.
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
  3. **A token the console holds is NOT built.** A browser-side OIDC client would put an
     identity library in the dist and a per-adopter authority/clientId in config, which is
     a different product decision from "the console is a client of the app's floor". Until
     an adopter needs it, the honest answer is (1) and (2) — and the console says so when
     a service refuses: a call blocked by CORS is reported as UNREACHABLE, never as an
     absent module, because "we could not ask" and "they do not have it" are different
     sentences.
- **External products**: Mockifyr (mocks) and other products appear as configured LINK
  tiles — their UI is theirs.

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
  do not.
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
