# Goldpath.Sdk

The module SDK seam (platform RFC D1, ADR-0012): the admin-surface contract every
Goldpath module composes from — first-party modules in the Goldpath monorepo and product
modules in their own repos alike. Before this package the seam was compile-linked shared
source, which cannot leave the monorepo; a product module binds HERE the way Goldpath
binds to Mediant.

## What it carries

- **`AdminSurfaceGuard`** — the H2 auth floor in one place: every admin mapper applies
  it, fail-closed, and the HTTP method decides (admin-contract R4.1). A `GET` demands the
  read policy (`goldpath-ops-read` or `goldpath-ops`). Any other method also demands
  `goldpath-ops`, refused with a `403` envelope naming the role. Without a registered read
  policy, every route demands `goldpath-ops` as before. `exposeUnsecured` is a visible,
  warning-logged opt-out.
- **`AdminTenantScope`** — admin-contract revision R1: on a multi-tenant app every admin
  read/verb scopes to the ambient tenant; crossing the fence demands
  `GoldpathPolicies.OpsAllTenants` and is logged with the actor.
- **`AdminPaging`** — the frozen contract's paging clamp (`[1, 500]`); an absurd `take`
  can never become an unbounded query.
- **`TraceLink`** — stored W3C traceparent → span links, for spans born off-request
  (Quartz threads) that must point at the trace that caused the work.

## Who uses it

Every module with an admin surface (Jobs, Bulk, Archival, Notification, Campaign) and
the console package. A product module's admin endpoints call the same four types — that
is what makes its surface answer, refuse, page and trace exactly like the family's.
