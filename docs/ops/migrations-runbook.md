# Migrations runbook (migrations RFC §6)

The schema lifecycle of a Goldpath app, end to end. The invariants behind every section:
migrations live in the APP (packages contribute MODEL — RFC D1); Development migrates,
real environments apply the BUNDLE (D2/D4); the app process never holds DDL rights; one
table set has ONE migration owner (D3, GP1801 guards the same-assembly case).

## 1. First deploy (day one)

1. `goldpath new solution -n Shop` — generation ends with `goldpath db init`: the Initial
   migration is committed WITH the app's first commit.
2. CI (the generated workflow) builds the **migration bundles** per owner project and
   uploads them as the `migration-bundles` artifact.
3. Deployment order, forever: **bundle first, app second.** Run the bundle as an
   init-container / pre-deploy hook with a DDL-capable connection string; start the new
   app version only after it exits 0. The app's own connection has NO DDL rights.

```yaml
# k8s sketch — the pattern, not a prescription
initContainers:
  - name: migrate
    image: <artifact image carrying Shop.Api-migrations>
    command: ["./Shop.Api-migrations", "--connection", "$(DDL_CONNECTION)"]
```

## 2. Enabling a feature on a LIVE system

1. `goldpath add feature campaign` — the recipe wires code + manifest, and its NextSteps
   end with the exact migration line.
2. `goldpath db add add-campaign` — the new package tables become ONE reviewed migration.
3. PR → CI rebuilds the bundle → deploy (bundle first). Old rows are untouched: package
   contributions only ADD their own tables/indexes; `goldpath check` (db status inside)
   goes red if the model and migrations ever drift apart.

## 3. Upgrading Goldpath packages

- A release whose packages change a model contribution carries a `[schema]` marker on its
  CHANGELOG entry (RFC D6). After updating: `goldpath db add goldpath-<version>` →
  review the generated migration like any other → PR → bundle → deploy.
- No marker = no schema effect = update and go. `goldpath db status` is the safety net
  either way — a forgotten migration cannot pass `goldpath check`.

## 4. Rollback stance (said honestly)

Bundles are **forward-only**. The rollback story is: restore the database backup taken
BEFORE the bundle ran, then roll forward with a fixed migration. Down-migrations against
enterprise data are a fiction we refuse to sell — plan backups into the deploy pipeline
(the bundle step is the natural pre-hook for a snapshot).

## 5. Multi-head solutions (api + workers)

- The API's context OWNS the shared package tables. A jobs-trigger worker maps the same
  tables with `excludeFromMigrations: true` — `goldpath add worker` GENERATES this; the
  worker's bundle only ever carries its PRIVATE tables.
- GP1801 (warning) flags a second owning map inside one assembly. Across projects the
  guard is the generated exclusion — if you hand-write a second head, keep it.

## 6. Air-gapped delivery

`goldpath db bundle` produces the same artifacts locally; ship them with the release
package. Nothing in the apply path needs the internet — the bundle is self-contained. The
nightly GmOffline shape bundles and applies with the network denied, as part of the whole
[air-gapped install](air-gapped-install.md).

## 7. Triage

| Symptom | First look |
|---|---|
| `goldpath check` red on db status | a model change without a migration — `goldpath db add <name>`, commit the migration |
| Bundle fails mid-apply | it stops at the failing migration; the history table says exactly where — fix forward, restore only if data was corrupted |
| "relation already exists" on first deploy | the database was born via EnsureCreated (pre-migration era) — one-time: baseline it by inserting the Initial row into `__EFMigrationsHistory`, then bundles apply cleanly |
| Worker bundle tries to create qrtz/shared tables | the exclusion was removed — restore `excludeFromMigrations: true` on the worker's shared contributions (GP1801 would have said so in-assembly) |
