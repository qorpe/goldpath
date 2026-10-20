# Getting started

Every command below has been executed verbatim against the published packages — if a
line here fails for you, that is a bug; please file it.

## Install (once per machine)

```bash
dotnet new install Goldpath.Templates@0.1.0-preview.9   # preview: pin the version
dotnet tool install -g Goldpath.Cli --prerelease         # the `goldpath` command
dotnet tool install -g specdrift                         # the deterministic engine behind check/add
```

Prerequisites: .NET SDK 10, Docker (the generated app's AppHost runs postgres/rabbitmq
containers for you — nothing to install by hand).

## Generate

```bash
dotnet new goldpath-solution -n Acme.Orders --db postgresql --broker rabbitmq --features bulk
cd Acme.Orders
```

What you now have: an Aspire AppHost (containers wired), an API with a walking-skeleton
vertical slice (create + keyset-paginated list + an outboxed integration event round-
tripping the broker), the manifest (`.goldpath/manifest.yaml`) describing exactly what
was composed, the Initial migration, and the first OpenAPI contract already committed
to `specs/`. Already have a solution? `goldpath init` attaches the manifest + schema gate (L2 —
it rewires nothing; gradual adoption starts there).
Prefer questions over flags? Bare `goldpath new` runs the WIZARD: say which modules the
app needs and the infrastructure is derived with its reasons ("no broker needed —
removed"); it prints the equivalent command and generates through the same flow.
Shape choices: `--db postgresql|sqlserver`, `--broker rabbitmq|none`,
`--auth openid|apikey|none`, `--layout vertical-slice|clean-architecture`, and thirteen
`--features` (run `dotnet new goldpath-solution -h`). Grow the solution with
`goldpath new service <Name>` (a service head with its OWN database and manifest) and
`goldpath new gateway` (YARP routing `/{head}/…` over service discovery). Past local:
`goldpath export compose` GENERATES docker-compose.yml (+ per-project Dockerfiles, laid
once) from the AppHost — the two definitions cannot diverge; re-run after AppHost
changes. Dev tier on purpose: fixed credentials, migrations on boot; environments stay
CI-built manifests.

## Run and verify

```bash
goldpath check                                # spec validate + drift + build, one verb
dotnet run --project src/Acme.Orders.AppHost  # containers start, dashboard opens
dotnet test                                   # the smoke drives the REAL AppHost
```

`goldpath check` is the habit to build: it validates the manifest against its schema and
rules, proves the repository still matches what the manifest declares (drift), and
builds. Red check = the app is lying about itself; the finding tells you the fix.

## Grow

```bash
goldpath add feature notification    # manifest line + package + wiring + model + migration step
goldpath add worker reports --trigger jobs
goldpath db add AddInvoices          # owner-aware; unchanged owners are skipped
```

Every `add` ends in an engine round-trip — if the result would drift, ALL files are
restored and the finding explains itself. Nothing lands half-wired.

## Where next

- The six ideas behind all of this: [Concepts](concepts.md).
- A real app built exactly this way: [the CorPay tour](corpay-tour.md).
- Production posture (migrations bundles, admin auth floor, dashboards): each concept
  links its runbook.
