# RFC: Product console link tiles in the console registry (#257)

> Status: **ACCEPTED** (owner, 2026-10-19). Implemented on `main` the same day, for the next
> train; the issue stays open until a train publishes it and an adopter proves it in a browser
> (`../strategy/open-threads.md` T31).
>
> **Owner decisions, 2026-10-19:**
> 1. **Option B** — a sibling `sealed record GoldpathConsoleLink(string Name, string Url)`,
>    `GoldpathConsoleOptions.Links`, `AddLink(name, url)`, and a `links` array beside
>    `services` in `console.config.json` — together with the platform-SDK RFC D3 wording
>    clarification ("one more REGISTRY entry", §8 below; applied in
>    [goldpath-platform-sdk.md](goldpath-platform-sdk.md) D3).
> 2. **Tiles on Today only**, plus "Open {name}" in the command palette. **No rail "Products"
>    group**: the pinned ONE-Modules-group rule (ui-standard v1.1 §7.2 as amended) stays.
> 3. **A′** (`ConsoleUrl` on a service) is **not booked**. It stays additive and possible if a
>    later need (T9 per-service deep links) asks for it; nobody is waiting for it.
>
> This is a SURFACE decision on an existing package, not a new module, so it is the proposal
> kept as the decision record — the eight-section template with §0 (problem) and §2 (options)
> in front, as the approvals-v2 RFC keeps its deltas beside the module's own RFC. Evidence
> line numbers in §0 were read against `origin/main` @ `b7bb1f1`, before the change.
> Touches: `Goldpath.Console` (options, the URL rule, the registry endpoint), `ui/console`
> (reader, Today, palette), the console smoke, docs and ledgers. Templates: none on this train.

## 0. Problem, with evidence

The console RFC says products appear as link tiles. The shipped registry had nowhere to put
a link, and nothing in the console drew one.

| Claim | Evidence (as read before the change) |
|---|---|
| The promise | [goldpath-console.md](goldpath-console.md) §3: "Mockifyr (mocks) and other products appear as configured LINK tiles — their UI is theirs." Its header: "the console links, never embeds". D1: "a config-driven link/iframe-free tile row". |
| The platform depends on it | ADR-0012: a product's console is "its own app composed from the kit, joining operators' view through the cross-service registry". Platform-SDK RFC D3: "a product's console is one more service entry, not a plugin panel". The §7 test plan's pilot proof includes "registry federation". |
| The registry had only one shape | `GoldpathConsoleService(string Name, string AdminBaseUrl)` — a **positional sealed record**, shipped API (`PublicAPI.Shipped.txt` lists the type, its primary ctor and `get`/`init` for both properties). The only builder was `AddService(name, adminBaseUrl)`. |
| What the endpoint served | 404 when no service is configured (a deliberate seam fix) and otherwise `{ services: [{ name, adminBaseUrl }] }`. |
| Every entry becomes a probed AdminClient | `ConsoleApp.tsx` builds one `AdminClient` per entry and probes every one. A probe that throws (a cross-origin host with no CORS) becomes `unreachable`, which Today reports as UNREACHABLE. |
| The reader knew only services | `registry.ts` reads an empty `services` list as a BROKEN registry, with a warning banner and a fallback to same-origin. |
| Nothing rendered a link | No component in `ui/console/src` drew an external link. The kit already supports one: `@qorpe/ui@0.6.0` `ShellNavItem.href` renders a real `<a>`. |
| The adopter that hit it | Coexist maps `MapGoldpathConsole()` with no registry and its own `MapCoexistConsole()` at `/coexist/console/` on the **same app**. Its `open-threads.md` S24 waits for "a published Goldpath train whose registry entry can name a service's own console", with the proof "driven in a browser, not inferred from the configuration". |

**Consequence.** An operator could reach a product's console only by typing its address. The
pilot proof's "registry federation" line could not be shown in a browser.

## 1. Scope / Non-goals

**Scope.** The registry can name a product console's address. The core console renders it as a
link tile on Today. The change is additive to shipped API and lands on `main` for the next train.

**Non-goals:**
- **No embedding.** No iframe and no runtime panel (console RFC locked antecedent; ADR-0012
  "never a runtime plugin"; platform-SDK RFC D3).
- **No health on the tile.** A tile asserts an address, not a status. A green dot the console
  never measured would be the false claim D2 exists to prevent.
- **No discovery from the manifest `products` key.** The registry stays config, not discovery
  (console RFC §3).
- **No sign-in carry-over.** A cross-origin product console sits behind its own floor; a second
  sign-in is T10's subject (browser-side OIDC), which stays unchanged.
- **No deep links into a product's screens.** Those ride T9 (client-side routes).
- **No icons, descriptions, ordering weights or groups per link.** A name and an address; more
  can be added later, additively.
- **No rail entry** (owner decision 2).

## 2. Options considered

Two kinds of target must both be served: **(a)** a product console on a Goldpath app (Coexist:
same origin, with core admin surfaces of its own), and **(b)** a product with no Goldpath admin
surface at all (Mockifyr, the RFC's own example, usually cross-origin).

| | **A. `ConsoleUrl` as a new positional parameter** | **A′. `ConsoleUrl` as an init-only property on the existing record** | **B. Sibling entry kind: `AddLink(name, url)`** | **C. Separate link registry** |
|---|---|---|---|---|
| Shipped-API compatibility | **Breaking**: the shipped ctor is removed (RS0017); binaries compiled against the previous train fail with `MissingMethodException` — exactly where ADR-0012's train-pinned product packages live. `Deconstruct` grows to 3 outs. | Additive (2 lines + an overload). | Additive: 8 Unshipped lines; nothing shipped changes. | Additive, but the largest new surface. |
| Kind (a): Coexist | Works | Works | Works: `AddLink("Coexist", "/coexist/console/")` | Works |
| Kind (b): link-only product | **Fails**: it needs a fake `adminBaseUrl`, which the console probes, and cross-origin without CORS that is a false UNREACHABLE row — the blindness signal D2(b) reserves for real blindness. | **Fails** the same way. | Works: links are never probed and need no CORS (navigation is not `fetch`). | Works |
| Registry JSON | `consoleUrl` per service | `consoleUrl` per service | a `links` array beside `services` | a second document with its own 404/empty/broken semantics |
| Fit to the written design | D3 "one more service entry" | D3 | console §3 "configured LINK tiles", D1 "tile row"; D3 needs the §8 clarification | contradicts §3's ONE registry and ADR-0012 |
| Verdict | Rejected | Viable for (a) only — not booked (owner decision 3) | **ACCEPTED** | Rejected |

**Why B over A′.** A′ ties a console to an entry that is always probed, which makes the RFC's own
example (Mockifyr) impossible to show without a false alarm. B gives a link exactly the
semantics of a link: no probe, no CORS, no capability discovery and no triage rows.

## 3. Manifest surface

**None.** The registry is runtime deployment config — where running things are, not what is
composed. ADR-0001 is untouched: no schema change, no corpus rows, no GM shape.

## 4. API surface

### C# (`Goldpath.Console`, namespace `Goldpath`)

```csharp
public sealed record GoldpathConsoleLink(string Name, string Url);

public sealed class GoldpathConsoleOptions
{
    public IList<GoldpathConsoleService> Services { get; } = [];   // unchanged
    public IList<GoldpathConsoleLink> Links { get; } = [];
    public GoldpathConsoleOptions AddService(string name, string adminBaseUrl);   // unchanged
    public GoldpathConsoleOptions AddLink(string name, string url);   // validates eagerly
}
```

`PublicAPI.Unshipped.txt` gains exactly eight lines (the record type, its ctor, `get`/`init` of
both properties, `AddLink`, `Links.get`). PublicApiAnalyzers 3.3.4 does not demand
record-synthesized members, matching how the existing record is ledgered.

```csharp
app.MapGoldpathConsole(configure: console => console.AddLink("Coexist", "/coexist/console/"));
```

### Registry document (`GET {prefix}/console.config.json`)

| Configured | Answer |
|---|---|
| nothing | **404, unchanged** ("this service only", silently) |
| services only | `{"services":[…]}` — unchanged, no `links` key |
| services and links | `{"services":[…],"links":[{"name","url"}]}` |
| links only | `{"links":[…]}`, with **no `services` key at all** |

The `services` key is omitted, not emptied, because the reader treats `services: []` as a
BROKEN registry — the exact seam that once put a false warning on every single-app operator's
first screen. The reader keeps "present but empty means broken" and adds "absent, with links
present, means same-origin".

**Admin contract: no revision.** `console.config.json` is not in the admin contract's route
inventory; it is served under the console prefix, not `/goldpath/admin/*`. It is still an
adopter-facing format (a dist hosted elsewhere reads a hand-written file), so it is
versioned-additive: old keys keep their meaning and an older reader ignores `links`. The
versioning RFC classes this as a new opt-in capability, **not breaking**; the 404→200 change
happens only when an adopter configures a link.

### Console (`ui/console`)

- **`registry.ts`.** `Registry` gains `links: LinkEntry[]`. A nameless link, a repeated name, or
  one whose URL fails the client rule (§5) is dropped **and counted in the banner**, which names
  what is missing ("a product console", "a service", or both). Links-only resolves to
  `services: [SAME_ORIGIN]` with no problem. `?base=` stays a one-service dev override and
  returns no links.
- **Today: one tile row** below the triage content, so D2's "is anything wrong" stays first.
  `<nav aria-label="Product consoles">` holding a `<ul>` of native `<a href>` tiles in the kit's
  card vocabulary (no new tokens, no new hex). Each tile shows the name and, quieter, the
  destination (the path when same-origin, the host otherwise), with an `aria-hidden` lucide
  `ExternalLink`. **Same tab**, the kit's `ShellNavItem.href` precedent. No links, no section.
- **Command palette.** A "Consoles" group with "Open {name}", which navigates to the address the
  reader already resolved and approved (v1.1 §7.14: commands GO places).
- **Rail.** Unchanged (owner decision 2).

## 5. Verifiers: the URL rule, security and the ops floor (ADR-0005)

**One URL rule in the server**, `ConsoleLinkRules`, run twice: eagerly in `AddLink` (the stack
trace points at the adopter's line), and again over the whole `Links` list in
`MapGoldpathConsole`, so a `Links.Add(new(...))` that bypassed `AddLink` is still refused **at
startup**. It throws `InvalidOperationException` naming the link and the two accepted forms —
and never echoing the URL, since a refused one may carry a credential into a startup log.

1. `Name` must not be blank; names are unique (ordinal, case-insensitive, trimmed).
2. `Url` is accepted in exactly two forms:
   - **Root-relative**: starts with `/`, the second character is not `/`. It resolves against
     the console's origin (the Coexist shape). Path-relative forms (`console/`, `../x`) are
     refused: the console prefix is configurable, so they would resolve differently per adopter.
   - **Absolute http/https**: it must literally start with `http://` or `https://`, parse with
     `Uri.TryCreate(UriKind.Absolute)` with a host, and carry **no `UserInfo`**. Plain `http` is
     allowed because air-gapped and internal ops networks are first-class.
3. Refused before either form is considered: an empty address, any whitespace or control
   character (browsers strip some: `java<TAB>script:`), and any backslash (a browser reads
   `/\host` as `//host`). Everything else falls outside both forms and is refused:
   `javascript:`, `data:`, `vbscript:`, `file:`, `blob:`, `//host`, `https:host`.
4. **Order matters.** On Linux and macOS, `Uri.TryCreate("/coexist/console/", UriKind.Absolute)`
   succeeds as `file:///coexist/console/`. The root-relative decision runs first; a test pins
   the quirk on the OS the suite runs on, and planting the absolute parse first turns it red.

**The client re-checks** (`resolveLink`, defence in depth for hand-written files): the same two
forms, then `new URL(url, document.baseURI)`, keeping only `http:`/`https:` without credentials.
Tiles and palette commands only ever receive the resolved address. React's handling of
`javascript:` hrefs is not relied on.

**Open redirect: no surface.** The server never redirects to a link; links come only from
server configuration, never from the request. The served smoke asserts that `?link=` and
`?base=` put no tile on the page.

**Ops floor.** `console.config.json` sits inside the guarded group, so link addresses — often
internal hostnames — are disclosed only to principals who passed the floor (unit-tested: 401
anonymous). A link grants nothing; the target applies its own floor (platform-SDK D7).
**Referrer:** tiles set `referrerPolicy="strict-origin-when-cross-origin"` (the browser default,
made explicit so it is never loosened).

## 6. Ops package

- `packages/Goldpath.Console/ops/console.md`, "The registry": `links`, the no-probe semantics,
  the 404 / links-only rule, the startup refusals and what each means, and "a link that 401s on
  arrival is the product's floor, not the console's" (T10).
- `packages/Goldpath.Console/README.md`: the `AddLink` example in both shapes.
- **Dashboard: no new signal.** Navigation leaves the app; there is nothing to meter honestly.

## 7. Test plan (as landed)

| Layer | What it proves | Where |
|---|---|---|
| .NET unit | Links served beside services in order; links-only answers **200 with `links` and no `services` property** (read with `JsonDocument`, not a typed bind); services-only carries no `links` key; nothing configured is still 404 (existing test). | `tests/Goldpath.Console.Tests/ConsoleLinkTests.cs` |
| .NET unit (rule) | Acceptances and a theory of refusals (every form in §5); WHICH rule each refusal names; blank and duplicate names; the refusal never echoes a credential; the Unix `file://` quirk pinned; a link added straight to `Links` refused at `MapGoldpathConsole`. | same |
| .NET unit (floor) | `console.config.json` with links answers 401 on the secured host. | same |
| PublicAPI | RS0016/RS0017 green with only the eight additive Unshipped lines. | build gate |
| Mutation | `Goldpath.Console` holds its break (70); the score is recorded with its date in `stryker/README.md`. | `scripts/mutation-gate.sh Goldpath.Console` |
| Vitest: reader | Links parsed and resolved; links-only gives `[SAME_ORIGIN]` with **no** problem; `services: []` plus links still warns; dropped links reported (singular, plural, beside a dropped service); `?base=` carries no links; `resolveLink` accepts and refuses the §5 forms and judges the RESOLVED scheme. | `ui/console/src/registry.test.ts` |
| Vitest: app | Today renders `nav[aria-label="Product consoles"]` with one link per entry, the right `href`, name plus destination as accessible name, same tab and the referrer policy; links are never probed; no links means no section; the banner names a missing product console; the palette's "Consoles" group navigates to the approved address; the rail grows no Products group; the existing "ONE Modules group" test is unchanged and green. | `ui/console/src/ConsoleApp.test.tsx` |
| Playwright: served | The smoke host composes links from `GOLDPATH_CONSOLE_LINKS` (the sibling of `GOLDPATH_CONSOLE_SERVICES`) and maps a stand-in product console at `/product/console/` behind the same floor. The test **clicks the tile and asserts arrival** (coexist S24's proof standard). The absolute link to the auth-floored app shows its host. A links-only console (`GOLDPATH_LINKS_ONLY_CONSOLE`, a second prefix `/linked/console/` on the open app — the tenant-scoped app cannot host it, since only `/goldpath/console` is exempt from tenant resolution) shows its tile and **no registry warning**, and its registry answers with the single key `links`. `?link=`/`?base=` inject no tile. | `ui/console/e2e/served.spec.ts` |
| Playwright: axe and keyboard | An axe scan (WCAG 2.1 A/AA, serious and critical) of the **served** Today with tiles present — the path `?base=` skips. Tab alone reaches the first tile, Tab moves to the next, and Enter on a tile arrives. | `ui/console/e2e/a11y.spec.ts` |
| GM matrix | **Unchanged** (ADR-0008). Templates do not call `AddLink`, and must not before a train publishes it. | nightly |

## 8. DoD: train boundary, docs and ledgers

**Train boundary.** `CHANGELOG.md` `[Unreleased] → Added`; it ships on the first train cut after
it merges (the 0.1.0-preview.9 release PR was already open, so this lands after it). The upgrade
guide line is "No breaking changes — take it blind". Templates and CLI recipes consume nothing
new, so there is no next-train-adoption line in `docs/ops/release-checklist.md`. CorPay binds
published packages and is not part of this DoD. Coexist adopts on its next pin move and closes
its S24 with its own browser proof.

**Platform-SDK RFC D3 clarification** (owner sign-off: decision 1). "One more service entry" is
read as "one more REGISTRY entry": a **link** when the product has no core admin surface, and a
**service plus a link** when it has one. This clarifies D3 and does not reverse it.

**Ledgers, in the same PR:** `open-threads.md` **T31** (the issue stays open: trigger = the next
train publishes `GoldpathConsoleLink`; proof = an adopter reaches its product console from the
core console by the tile, driven in a browser on the published train); `ai-sdlc-status.md`
Operations row ("product-console link tiles: on main, unpublished"); `coverage-matrix.md`
"Console panels" gains a link-tile row.

## 9. ADR constraints

- **ADR-0012** — products join "through the cross-service registry, never a runtime plugin":
  rules out C and any embed; products bind published packages, which forces the train boundary.
- **ADR-0003** — compose, do not rewrite: `System.Uri` and the platform `URL` for parsing, the
  kit's card vocabulary, the SDK's `AdminSurfaceGuard`. No URL-sanitizer dependency.
- **ADR-0001** — the manifest stays the truth for composition; links are deployment addresses.
- **ADR-0005** — the URL rule ships with its verifier: startup refusal plus tests, no suppression.
- **ADR-0008** — the GM matrix stays green; no template change on this train.
- **ADR-0009** — no runbook, no feature: `ops/console.md` is updated.
- **ADR-0010** — owner approval before code (2026-10-19); merge only through the human gate.
