# Goldpath guide — the adopter's path

Start here; go deep via the links. The deep layers (RFCs, runbooks, ops packs) already
exist per module — this guide is the map, not a copy.

1. **[Getting started](getting-started.md)** — install to running app in five minutes.
2. **[Concepts](concepts.md)** — the six ideas everything else hangs on: manifest,
   composition, the run model, migrations discipline, the frozen admin contract, proofs.
3. **[Modules × infrastructure](modules-and-infrastructure.md)** — what each capability
   actually costs to run, verified against the composition (the wizard derives from it).
4. **[The CorPay tour](corpay-tour.md)** — a real finance app built ONLY with the public
   verbs, feature by feature, with the bugs the build itself found.
5. **[CLI reference](cli-reference.md)** — every verb the tool has, what it does, and
   what it deliberately does not.
6. **[Integration events](integration-events.md)** — every event the packages publish:
   fields, the moment each fires, what a consumer may rely on.
7. **[Support policy](support-policy.md)** — which versions get fixes, the security
   response targets, the engines and databases supported today, the `[Experimental]`
   surfaces, and how to ask for a backport; every fact linked to the document that owns it.
8. **Proof stories** — why the claims hold: [No double payment under kill -9](../stories/kill9-no-double-payment.md).

Operating depth (already written, linked from the concepts): the
[migrations runbook](../ops/migrations-runbook.md), the
[trace-correlation guide](../ops/trace-correlation.md), the
[frozen admin contract](../rfc/goldpath-admin-contract.md), the
[versioning & support contract](../rfc/goldpath-versioning.md), and every module's
`ops/` pack (Grafana board + runbooks + measured performance).
