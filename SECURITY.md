# Security policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.** Use GitHub's private reporting —
[Security → Report a vulnerability](https://github.com/qorpe/goldpath/security/advisories/new)
— which opens a private advisory only the maintainer can see.

If that path is unavailable to you, write to **omer@omercelik.dev** with `GOLDPATH-SECURITY`
in the subject.

What to include: the package and version, what an attacker gains, and the smallest
reproduction you have. A manifest plus a failing request usually beats a paragraph.

What to expect:

| Stage | Target |
|---|---|
| Acknowledgement that a human has read it | 3 working days |
| First assessment (is it real, how severe, does it affect a published train) | 10 working days |
| Fix or a written mitigation for a confirmed high/critical | 30 days |
| Coordinated disclosure | with you, after the fix ships — credit given unless you decline |

This is a coordinated-disclosure policy: please give the fix a chance to reach adopters
before publishing details.

## What is in scope

The published `Goldpath.*` packages, the `Goldpath.Cli` tool, the solution/worker templates,
and this repository's build and release pipeline. Findings in a dependency belong upstream —
tell us anyway if a Goldpath default makes it reachable, because the default is ours.

Out of scope: an adopter's own application code, and the infrastructure an adopter runs
(their broker, database, gateway or cluster).

## Supported versions

Goldpath is pre-1.0 and ships as `0.1.0-preview.N` trains. The newest train is the top
released entry of [CHANGELOG.md](CHANGELOG.md), and the same version is on nuget.org. This line
deliberately names no version number, because a pinned number goes stale one train later.
**While the version is below 1.0, only the newest preview train receives fixes.** The
versioning contract says so ([docs/rfc/goldpath-versioning.md](docs/rfc/goldpath-versioning.md)),
and the upgrade notes in `docs/upgrades/` carry every train-to-train change. At 1.0 the support
window becomes the one stated in that contract. What 1.0 must prove before it ships is
defined in [docs/rfc/goldpath-ga-readiness.md](docs/rfc/goldpath-ga-readiness.md) (accepted 2026-10-21).

An adopter who cannot move trains quickly should say so in an issue: a backport is a
decision we can make, but not one we can guess.

## What ships with each release, so you can verify it yourself

- **An SBOM** (CycloneDX, `goldpath-sbom.cdx.json`, attached to the GitHub release) — the
  resolved dependency graph of the release build, so "are we affected by CVE-X?" is an
  artifact lookup rather than an investigation.
- **Signed build provenance** (SLSA, keyless via GitHub OIDC) for every `.nupkg`. It states
  which commit, which workflow and which run produced the package. Verify with:

  ```bash
  gh attestation verify Goldpath.Data.<version>.nupkg --repo qorpe/goldpath
  ```

  A package that fails this check did not come from this pipeline, whatever the registry
  says. **Packages published before the repository moved to the `qorpe` organisation carry
  the old source in their attestation** — verify those with `--repo omercelikdev/goldpath`.
  The move happened between 0.1.0-preview.8 and the train after it; an attestation names
  the repository that built it, and that is a fact about the past, not a mistake to correct.
- **Publishing without a long-lived key**: the train is pushed through NuGet Trusted
  Publishing (OIDC), so there is no API key in this repository to leak. The same is true of
  the family UI kit on npm.

## How we test for security issues

There is no paid penetration test today. That was an owner decision on 2026-10-21 (GA gate
G15): a paid test happens when a paying customer funds one. In its place, two free scans run
in CI. Neither one is a substitute for a threat model, which G15's second part adds:
[docs/security/threat-model.md](docs/security/threat-model.md). It is STRIDE per surface with an
auth boundary, and every threat names the test that holds it, the issue that will, or an
accepted risk for the owner to sign. Four surfaces are modelled so far (the admin routes, the
served console, Campaign's signed callbacks, Bulk upload and its gate). The other six are its
checklist.

- **CodeQL** (static analysis, [`.github/workflows/codeql.yml`](.github/workflows/codeql.yml)):
  - Covers the C# and the console's TypeScript with the `security-extended` query suite.
  - Runs on every pull request, on `main`, and weekly, so new queries also reach code that
    has not changed.
  - Findings land in the repository's code-scanning view.
- **OWASP ZAP baseline** (dynamic, passive, [`scripts/zap-baseline.sh`](scripts/zap-baseline.sh)):
  - Runs nightly against a **generated** app (the golden-manifest shape `GmBulkOnly`). It
    covers the API root and the console that API serves, in Production posture.
  - Every alert listed in [`scripts/zap-baseline.conf`](scripts/zap-baseline.conf) carries
    its reason, and is one of two kinds. **IGNORE** means it is not a weakness here. **INFO**
    means it is a real open finding: it is reported in every run but does not fail the night
    until its fix lands or the owner signs it as an accepted risk in G15's second part. Each
    INFO line names its ledger entry: [#377](https://github.com/qorpe/goldpath/issues/377)
    (a Bulk defect) and open-threads
    [T43](docs/strategy/open-threads.md) (three missing security headers).
  - An alert that is not listed fails the night. The report is kept as a workflow artifact.

Both scanners are pinned, so a red result comes from a change in Goldpath, not in the
scanner. CodeQL's actions are pinned to a commit and the ZAP image to a digest.

## Hardening notes for adopters

- The admin surface (`/goldpath/admin/*`) is **fail-closed** by default: with no auth
  configured it refuses rather than serves ([docs/rfc/goldpath-admin-contract.md](docs/rfc/goldpath-admin-contract.md)).
- Classified fields are masked at the boundary, and the console masks them again on render —
  defence in depth, deliberately duplicated.
- Air-gapped installation is a first-class scenario: nothing in a generated app phones home,
  and every dependency is pinned. There is no runtime licence check anywhere in Goldpath. The
  nightly GmOffline shape installs, migrates and smokes a generated app with outbound network
  denied ([docs/ops/air-gapped-install.md](docs/ops/air-gapped-install.md)).
