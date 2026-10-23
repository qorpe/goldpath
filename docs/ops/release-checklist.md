# Release checklist (the D4 gate)

One release PR per train version, containing ALL of the following — the tag happens
only after that PR merges green.

## Before the release PR

- [ ] Local full gates green: build, unit loop, `dotnet format --verify-no-changes`,
      license gate, integration suite.
- [ ] Golden-manifest matrix green **on the release PR's own head** — a fresh `nightly.yml`
      dispatch with `--ref` the release branch (ADR-0008: no release while GM is red). A
      nightly that ran on an earlier commit does NOT count: preview.8 was tagged against a
      green nightly that ran on the commit BEFORE its own template changes, and every app
      generated with `fileexchange` on that train failed its first build (GP2102).
- [ ] Mutation scores current for every package whose engine paths changed since the
      last release. The nightly matrix scores every gated package — twenty-two packages since
      2026-10-20, when the big six (Jobs, Archival, Bulk, Notification, Campaign, Caching)
      joined it; `mutation-heavy.yml` re-scores those six on demand, or one of them with its
      `package` input (`-f package=Goldpath.Campaign`). A local run is
      `scripts/mutation-gate.sh Goldpath.<Package>` — the FULL config name; the script refuses a
      name that matches no config, because `mutation-gate.sh Jobs` once ran nothing and printed
      GREEN.
- [ ] Bench reference numbers re-measured (`bench.yml` dispatch) IF any engine path
      changed; ops docs updated from the run.

## In the release PR

- [ ] Version bump: the single train version (D1 — every `Goldpath.*` package, the CLI
      and the template pack) — INCLUDING the templates' own `Goldpath.*` pins, which
      decide which train a generated app restores. `scripts/template-pins.sh` gates this
      on every PR; it exists because the pins sat four trains behind while the golden
      manifest matrix quietly validated the old one.
- [ ] PublicAPI roll: each package's `PublicAPI.Unshipped.txt` content moves to
      `PublicAPI.Shipped.txt` (empty Unshipped after the roll).
- [ ] `CHANGELOG.md`: the train's entry, newest on top.
- [ ] `docs/upgrades/<version>.md`: a step for every breaking change (PublicAPI diff,
      admin-contract change, `[schema]` migration step, default flip) — or the single
      line "No breaking changes — take it blind."
- [ ] Admin contract check: if any route/envelope changed, `goldpath-admin-contract.md`
      was updated in the SAME PR that changed it (the route-freeze test forces this).
- [ ] **Mutation scores recorded** — every score you ran goes in `stryker/README.md`'s
      measured-scores table with its date. Before 2026-09-05 the checklist asked for
      "current" scores and nothing wrote one down, so the line could not be audited.
- [ ] **Next-train adoptions** — API that landed on main AFTER the last train and that the
      templates and CLI recipes may only consume once the pins move (the templates must
      generate apps on the PUBLISHED train, `template-pins.sh`; a CLI recipe writes into an
      adopter's app on that train too). Wire each in the release PR, then delete its line:
      - *(none open — preview.9's one adoption is wired: `GmCleanFeatures` carries `audittrail`.)*

## After the merge

- [ ] Tag `v<version>` on the merge commit.
- [ ] NuGet push via `release.yml` (OIDC trusted publishing — tag-gated; shipped with issue #10).
- [ ] GitHub release notes = the CHANGELOG entry + a link to the upgrade guide.
