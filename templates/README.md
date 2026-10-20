# Goldpath.Templates

Golden-path templates. Current: `goldpath-solution` (GM-1 shape defaults: modular-monolith,
vertical-slice, PostgreSQL, RabbitMQ) — with `--layout clean-architecture` generating the
four-project split (Domain / Application / Infrastructure / Api; the packages anchor rides
Infrastructure, which also owns migrations). Also current: `goldpath-worker` — the
headless worker shape (queue / schedule / jobs triggers; probes instead of business
contracts, no OpenAPI artifact) with **concept parity** to the solution (2026-09-03,
open-threads T25): `--auth openid|apikey|none` guards its MANAGEMENT head (the jobs admin
surface and the console it serves; `none` keeps the visible `exposeUnsecured` opt-out) and
`--features` composes the Ring B features whose concept exists in a process without business
HTTP — multitenancy, audittrail, softdelete, dataprotection, locking, notification,
fileexchange. Deliberately NOT offered on the worker: `layout` (meaningless in one project),
`idempotency` (request-shaped; a worker's replay protection is the inbox), `caching`,
`archival`, `bulk`, `campaign`, `approvals` (their verbs are solution-head admin APIs — they
join the worker head the day a worker-only scenario needs them). The table-owning features
need the worker's database, so `--trigger schedule` refuses them at build with teaching
text. Ships the same `.claude/` guardrails (stop gate, format hook, `goldpath-manifest`
skill, `breaker` agent) and a day-one Grafana board (`ops/grafana-worker-dashboard.json`). Further shapes arrive per the template RFC phasing
(`docs/rfc/goldpath-template.md`, decision D1).

```
dotnet new install Goldpath.Templates
dotnet new goldpath-solution -n MyPlatform
cd MyPlatform && dotnet run --project src/MyPlatform.AppHost   # F5 experience: containers up, dashboard on
dotnet test                                                     # smoke: POST → event consumed → paginated GET
```

Local validation without any feed: `scripts/validate-gm.sh <Name> [--db sqlserver --broker none]` packs the repo, installs
the template from source, generates the requested shape, builds it against the local feed,
and runs the smoke suite. The proven shapes are the golden-manifest matrix's (`.github/gm-shapes.yml`: all of them
nightly, the covering subset on every pull request that touches a template), listed with what each one exists to prove in
`docs/strategy/golden-manifests-v1.md` §6 — a count restated here goes stale the day a
shape is added, and this sentence claimed two long after there were twenty-two
(preview.8 coverage audit, 2026-09-05).
