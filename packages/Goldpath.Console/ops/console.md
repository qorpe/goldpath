# Console — Ops Runbook

`MapGoldpathConsole()` serves the operations console from the package's embedded assets on
the app's own management head (`/goldpath/console/`), behind the ops floor
(`GoldpathPolicies.Ops`) or with the VISIBLE `exposeUnsecured: true` opt-out. The console is
a client of the frozen admin contract (`/goldpath/admin/*`) — it has no data of its own.

## "The page is blank / 500"
The page references its bundle relatively (`./assets/…`). A blank page with a green status
means the assets did not ship — a `Goldpath.Console` package built without its `dist`
(the GM lane fails on exactly this). Verify: `GET /goldpath/console/assets/<bundle>.js`
must answer 200; the served-console smoke asserts it on every train.

## "Every panel says refused / forbidden"
- `401` on the page itself: the ops floor is up and the operator has no principal. Sign in
  against the app's IdP; the console carries the browser's credentials (`credentials:
  include`), it holds no token of its own (open-threads T10).
- `403` on the calls: a principal without the `goldpath-ops` role. Grant the role, not an
  exemption.
- `400` "cannot be scoped": a multi-tenant app resolving tenants by HEADER; the console's
  calls carry none (T11). Subdomain resolution works today; `?tenant=` for holders of the
  all-tenants role.
- `absent` everywhere on a service that has modules: CORS. A cross-origin service must
  allow the console's ORIGIN (named, never reflected — CWE-942) with credentials.

## The registry (`console.config.json`)
Served next to the console; adding a service is a config change. A registry that cannot be
read falls back to same-origin AND says so in the shell — an operator who configured four
services and sees one has found the outage, not a bug in the console.

### Product console links (`AddLink`, `links`)
`AddLink(name, url)` puts a tile on Today (and "Open {name}" in the ⌘K palette) that leads to a
product's OWN console. A tile asserts an ADDRESS, never health: the console never probes a link,
needs no CORS for it and never makes it a triage row — whether the product is well is shown on
its own console.

| Configured | `console.config.json` answers |
|---|---|
| nothing | `404` — "this service only", silently |
| services only | `{"services":[…]}` |
| services and links | `{"services":[…],"links":[…]}` |
| links only | `{"links":[…]}` — **no `services` key**: an empty list would read as a broken registry |

**The app refuses to start on a bad link** (`InvalidOperationException`, thrown by `AddLink`
and again by `MapGoldpathConsole` over the whole list). The message names the link, never its
address (a refused one may carry a credential):
- `has no name` — give the tile the operator's word for the product.
- `is configured twice` — two names that differ only in case or spaces; name each product once.
- `its address is empty` / `contains whitespace or a control character` / `contains a backslash`.
- `is protocol-relative (//host)` — write the scheme: `https://host/`.
- `is neither root-relative nor an absolute http(s) address` — `javascript:`, `data:`, `file:`,
  `console/` and `../x` all land here. Use `/product/console/` (same origin) or
  `https://product.internal/` (another origin).
- `carries credentials (user:password@)` — a credential never rides a link.

**"The tile is missing" + a warning banner naming "a product console"**: the registry file
was written by hand (a dist hosted elsewhere) and the console's own re-check dropped an entry
— no name, a repeated name, or an address it will not open. Fix the file; the banner counts
what was skipped.

**"The tile leads to 401 / a sign-in page"**: that is the PRODUCT's floor, not the console's —
a link grants nothing, and a cross-origin console asks for its own sign-in (a token the
console would carry across is open-threads T10). Same-origin products ride the session the
console already has.

The registry sits behind the ops floor, so link addresses (often internal hostnames) are
disclosed only to principals who passed it.

## Signals
- `LogWarning "{Prefix} is mapped WITHOUT the ops policy"` at startup: the opt-out is in
  effect on that prefix (`Goldpath.Sdk` guard). Acceptable only behind an authenticating
  boundary; in a log search this line is the inventory of unguarded heads.
- Request rate and 401/403 on `/goldpath/console/*` and `/goldpath/admin/*`
  (`http.server.request.duration` by `http_route`) — the console's traffic IS the operators'
  activity; a 401 spike is a token expiry wave or an attack.

## Dashboard
`grafana-console-dashboard.json` — admin-surface traffic and refusals by route, page serves,
latency of the admin reads (the console's own responsiveness ceiling).
