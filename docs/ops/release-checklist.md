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
- [ ] **CorPay's `Goldpath.*` pins move to the new train in this PR**
      (`samples/corpay/Directory.Packages.props`), not after the tag. When they moved
      afterwards, the publish turned `train-freshness` red on `main` and on every open PR
      until a follow-up caught up (v0.1.0-preview.9, #307). The gate holds this:
      - While this repository's train is not on nuget.org yet (the release PR, and `main`
        between merge and publish), it requires CorPay to pin exactly that train.
      - A release PR that leaves CorPay behind fails here, before the tag rather than after.
      - Adopters' runs of the script are unaffected.

      Before the publish, nuget.org cannot restore the new pins. To build CorPay on the
      release PR, restore it from a locally packed feed, the way `validate-gm.sh` builds a
      generated app:
      `dotnet pack Goldpath.sln -c Release -o /tmp/feed && dotnet build samples/corpay/CorPay.sln --source /tmp/feed --source https://api.nuget.org/v3/index.json`.
      The nightly's `sample-corpay` job restores from nuget.org only, so it stays red on the
      release head until the publish. Teaching it the packed feed is
      [#378](https://github.com/qorpe/goldpath/issues/378), triggered by the next release PR.
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
      - **preview.11 — the message path in Goldpath vocabulary (#327, G1 Part 2/4).** The two
        solution layouts, the worker template, the CLI's worker skeleton and outbox recipe, and
        CorPay (when it takes the train) move from `AddGoldpathMessaging(bus => …)` to
        `AddGoldpathMessaging(messaging => …)` with `UseRabbitMq()`, and their `DbContext`s from
        the three `modelBuilder.Add…Entity()` lines to `AddGoldpathOutboxEntities()`. Proof: every
        broker GM shape green, and `goldpath db add` on one of them yields an empty migration
        (RFC goldpath-messaging-engines §4.2). The upgrade guide's steps 1, 2 and 4 (§8 there).
      - **preview.11 — campaign on the seam (#328, G1 Part 3/4).** The solution
        layouts' `Program.cs` (`bus.AddGoldpathCampaignConsumers<OrdersDbContext>();`) and the
        CLI's campaign recipe (`FeatureRecipes.cs`, with its golden in `RecipeGoldenTests`/
        `AddFeatureTests`) move to `messaging.AddGoldpathCampaign<TContext>();` inside the new
        block. Proof: the campaign GM shape green, and its broker queue still named after the
        context. `AddGoldpathCampaignConsumers` itself goes in Part 4/4, with Messaging's
        MassTransit-typed overloads. Upgrade guide step 3, drafted: *"Campaign: replace
        `bus.AddGoldpathCampaignConsumers<YourDbContext>()` with
        `messaging.AddGoldpathCampaign<YourDbContext>()` in the `AddGoldpathMessaging(messaging => …)` block
        — campaign's builder line needs that form (step 1); the old line keeps working on the `bus => …`
        block until Part 4/4 removes it. Queue
        names and bindings do not change — both handlers still drain the queue named after your
        context — so hosts may be rolled one at a time. If you referenced
        `GoldpathCampaignItemConsumer<T>` or `GoldpathCampaignOutcomeSink<T>` (for example to tune a
        consumer), they are now `GoldpathCampaignItemHandler<T>` and `GoldpathCampaignOutcomeHandler<T>`;
        a test double of MassTransit's `IPublishEndpoint` for the campaign engine becomes one of
        `IIntegrationEventPublisher`."*

## After the merge

- [ ] Tag `v<version>` on the merge commit.
- [ ] NuGet push via `release.yml` (OIDC trusted publishing — tag-gated; shipped with issue #10).
- [ ] GitHub release notes = the CHANGELOG entry + a link to the upgrade guide.
