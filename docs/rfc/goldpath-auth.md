# Module RFC: Goldpath.Auth

> Status: v1.0 accepted — D1–D5 approved by Ömer (2026-07-05): openid+apikey+none with
> saml/ldap as schema-rejected strategic deferrals · secure-by-default fallback policy ·
> token–tenant binding on by default · minimal apikey · Mediant [Authorize] composed.
> Ring B companion (closes the last
> cross-cutting gap found in the 2026-07-05 inventory review) · Effort M
> Dependencies: Goldpath.Abstractions (headers, tenant context), Mediant.Behaviors (composed:
> `[Authorize]` AuthorizationBehavior — fail-closed, "no context = Unauthorized"),
> ASP.NET authentication (composed: JwtBearer/OIDC — ADR-0003; we do NOT write an identity server)

## 1. Scope / Non-Goals

**Scope — the manifest's `providers.auth` finally gets its implementation:**

- **`openid` (default):** OIDC discovery + JWT bearer validation for APIs — the enterprise
  standard (Keycloak/Entra/Ping; every bank already runs an IdP). Authority + audience from
  config; composed `AddAuthentication().AddJwtBearer(...)`, nothing rewritten.
- **`apikey`:** a thin authentication handler for internal/legacy callers — key from the
  `X-Goldpath-Api-Key` header, validated against configured keys (values live in the secret
  store; constant-time comparison; no key-management UI — that's the Ring C portal).
- **Secure by default (fail-closed, the house style):** a fallback authorization policy
  requires an authenticated principal on EVERY endpoint; anonymous access is an explicit,
  greppable `[AllowAnonymous]`; health/openapi exempt via the same path-list pattern as
  MultiTenancy.
- **Token–tenant binding (with MultiTenancy):** when the JWT carries the tenant claim
  (default `goldpath_tenant`, configurable), a mismatch against the resolved ambient tenant is a
  403 — a stolen acme token cannot ride a globex header. Claim absent → binding not enforced
  (gateway-injected-header deployments stay valid).
- **Command-level authorization composed from Mediant:** `[Authorize]` on commands via
  `AddMediantAuthorization` — role/policy names map to ASP.NET policies; one vocabulary,
  both levels.
- **OpenAPI:** security scheme wired per strategy (bearer/apiKey) so generated clients and
  the docs UI carry auth correctly.
- **Template:** `--auth openid|apikey|none` choice generates the wiring (`none` = internal
  services behind mTLS/gateway; generates NOTHING — compile-time composition).
- **`IUserContext`:** already reads claims (AuditTrail's HttpClaimsUserContext) — with Auth
  enabled, audit rows carry real identities. No new code, just the proof test.

**Non-Goals (v1, written not silent):** identity server / user store (compose the
customer's IdP), `saml` and `ldap` (STRATEGIC DEFERRALS per the standing condition —
SAML is the SSO/portal layer's concern and APIs speak JWT via token exchange; LDAP sits
behind the IdP in every modern setup; both tracked with trigger = first project demand,
and the manifest schema REJECTS them until implemented), fine-grained/ReBAC authorization
engines (OpenFGA-class — Ring C candidate if a project demands), session/cookie flows
(API-first golden path), key rotation UI (Ring C portal).

## 2. Seam Map

| Seam | Touch |
|---|---|
| HTTP middleware | `UseAuthentication`/`UseAuthorization` ordering contract with `UseGoldpathMultiTenancy` (resolution → authN → binding check → authZ); fallback policy |
| Mediant pipeline | `AddMediantAuthorization` composed — `[Authorize]` on commands, fail-closed |
| EF interceptor | none (identity flows into audit via the existing IUserContext) |
| MassTransit filter | none in v1 — message-level auth is transport/broker concern (recorded) |

## 3. Manifest Surface
```yaml
providers:
  auth: openid              # openid | apikey | none  (saml/ldap: strategic deferrals, schema-rejected)

# options (Goldpath:Auth):
#   openid:  authority, audience, tenantClaim ("goldpath_tenant"), requireHttpsMetadata (default true)
#   apikey:  headerName ("X-Goldpath-Api-Key"), keys (from secret store)
```

## 4. API Surface
```csharp
builder.AddGoldpathAuth();                          // strategy per manifest; fail-closed fallback policy
app.UseGoldpathAuth();                              // authN + tenant-binding + authZ, ordered correctly

[AllowAnonymous]                               // the explicit, greppable exception
app.MapGet("/public/rates", ...);

[Authorize(Roles = "loan-officer")]            // Mediant command-level, same vocabulary
public sealed record ApproveLoanCommand : ICommand<Result>;
```

## 5. Analyzer Rules (SHIPPED — analyzer batch 3, 2026-07-06)
| ID | Rule | Severity |
|---|---|---|
| GP1201 | `[AllowAnonymous]` on a non-exempt endpoint (review flag — anonymous surface is inventory) | info |
| GP1202 | API key or JWT signing material as a string literal in code | error |

## 6. Ops Package ("no runbook = no module")
- **Metrics:** `goldpath_auth_failures_total` (by reason: expired/invalid-signature/missing),
  `goldpath_auth_tenant_binding_rejects_total` (**security signal — alert on any non-zero**, the
  write-guard pattern)
- **Runbook:** "everything is 401" triage (authority reachability → clock skew → audience);
  token-binding reject investigation (stolen-token suspicion procedure); apikey rotation
  (dual-key overlap window)
- **Dashboard:** 401/403 rate by endpoint; binding rejects; token expiry distribution

## 7. Test Plan
- openid (in-proc IdP stub signing real JWTs): valid token → 200; expired/bad-audience/bad
  signature → 401; missing → 401; `[AllowAnonymous]` + exempt paths pass; fallback policy
  covers unattributed endpoints (secure-by-default proof)
- Token–tenant binding: claim==header → 200; mismatch → 403 + metric; claim absent → pass
- apikey: valid header → 200 with principal; wrong/missing → 401; constant-time comparison
- Mediant `[Authorize]`: role present → handled; missing → fail-closed (composed proof)
- IUserContext interplay: audit row carries the token's subject
- License gate GREEN (ASP.NET auth is part of the framework — zero new packages beyond
  Microsoft.AspNetCore.Authentication.JwtBearer, MIT)

## 8. DoD
- [x] Decisions locked (D1–D5) · package + tests green (9: secure-by-default 401/200,
      expired/wrong-audience/bad-signature via the REAL validation path against an in-proc
      signing IdP stub, AllowAnonymous escape, token–tenant binding match/mismatch/absent,
      apikey named-client + wrong/missing, Mediant [Authorize] same-principal both outcomes)
      · PublicAPI locked
- [x] README (4 sections) + ops runbook + CHANGELOG · GP1201/1202 to backlog
- [x] Template gains `--auth openid|apikey|none` (default openid) · schema enum trimmed to
      implemented values (+ MapGoldpathDefaultEndpoints marks probes AllowAnonymous explicitly)
      · saml/ldap deferrals recorded with trigger
- [x] GM PROVEN, not just tracked: authed default shape GREEN in 28s (secure-by-default
      401 + green probes IS the first-click contract for authed shapes; full-flow smoke
      stays in --auth none, GREEN 26s). Two infra fixes recorded in validate-gm.sh:
      -m:1 (net10 StaticWebAssets parallel cache race) and a Goldpath global-cache purge
      (same-version repacks poisoned restores with stale assemblies — TypeLoadException).
- Note (claim-mapping): the Mediant authorization context reads mapped AND raw claim types
  (role/roles/sub) so authorization never depends on a JWT handler mapping flag. Mediant
  fails closed by THROWING; bare hosts see the exception, Ring A ProblemDetails shapes it.

## 9. Decision Points (Ömer)

- **D1 — v1 strategies: `openid` + `apikey` + `none`; `saml`/`ldap` strategic deferrals.**
  Schema enum trimmed to implemented values (the standing condition: the manifest must not
  accept what does not exist). **Recommendation: approve the trim.**
- **D2 — Secure by default:** fallback policy denies anonymous everywhere; `[AllowAnonymous]`
  is the explicit escape; health/openapi exempt. Fail-open auth is how internal APIs end up
  on the internet. **Recommendation: yes.**
- **D3 — Token–tenant binding on by default** when both modules are enabled and the claim is
  present; mismatch = 403 + alarmed metric. Configurable claim name, disableable for
  gateway-owns-tenancy topologies. **Recommendation: on by default.**
- **D4 — ApiKey stays deliberately minimal:** config-sourced keys (secret store), constant-time
  compare, no self-service management (Ring C portal). It exists for internal/legacy callers,
  not as the primary strategy. **Recommendation: minimal.**
- **D5 — Command-level authorization = Mediant's `[Authorize]`, composed** — no Goldpath
  authorization layer of our own; policy vocabulary lives in ASP.NET policies both levels
  share. **Recommendation: compose.**

## 10. The read role — admin-contract R4 (PROPOSED 2026-10-25, [#304](https://github.com/qorpe/goldpath/issues/304))

**Owner decision, 2026-10-20 (#297):** the admin floor splits into read and act, additively.
Admin-contract [revision R4.1](goldpath-admin-contract.md) is the normative text. This module's
share of it:

- **A third policy.** `GoldpathPolicies.OpsRead` = `"goldpath-ops-read"`, registered as
  `RequireRole(OpsReadRole, OpsRole)`: either role passes. The new option
  `Goldpath:Auth:OpsReadRole` defaults to `goldpath-ops-read`.
  - It is registered in both branches that register the ops policies today: the authenticated
    strategies, and `Strategy = None`, where audit A4 keeps a refusal a `403` rather than a
    `500`.
- **How the three compose.**
  - Reads demand `OpsRead`, and verbs demand `Ops`.
  - R1's `OpsAllTenants` stacks on top of either one when a call crosses tenants.
  - `goldpath-ops` satisfies `OpsRead`, so an identity provider that grants only today's role
    sees no change.
- **Which routes are which:** the HTTP method decides. `GET` is a read; `POST`, `PUT` and
  `DELETE` are verbs, on every admin surface and the served console. There is no per-route
  list (R4.1 says why).
- **No new authorization layer** (D5 still holds). The policies are ASP.NET policies. The
  worded `403` on a refused verb comes from the admin guard's endpoint filter in `Goldpath.Sdk`,
  not from this module, so it holds under any authentication an adopter composes.
- **An app that composes its own authentication without this module** and never registers
  `goldpath-ops-read` keeps R3's behaviour: reads demand `Ops`, with one startup log line naming
  the policy to register.

```
const Goldpath.GoldpathPolicies.OpsRead = "goldpath-ops-read" -> string!   (Goldpath.Abstractions)
Goldpath.GoldpathAuthOptions.OpsReadRole.get -> string!
Goldpath.GoldpathAuthOptions.OpsReadRole.set -> void
```

What R4 leaves out — an admin-only tier above `goldpath-ops`, and an approver role outside
Approvals — is open-threads T50.
