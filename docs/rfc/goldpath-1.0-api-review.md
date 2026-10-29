# RFC: The 1.0 public-API review — the record (GA gate G4)

Status: **DRAFT** (2026-10-29, API-1 #390). This file is the narrative record of the 1.0 API review,
and the owner merges it (Tier C). The verdicts themselves are data under
[`docs/strategy/api-review-1.0/`](../strategy/api-review-1.0/README.md) (Tier A), filled by the review
parts API-2 … API-7 (#391–#396). The owner signs the whole review once, in API-13 (#402), which fills §8.
Owner decision on #390, 2026-10-24: option (a), the record here and the tables there.

## 1. Scope / Non-Goals

**In scope:**
- **Every live public-API ledger line at the review commit.** A live line is a
  `packages/*/PublicAPI.Shipped.txt` line that the sibling `PublicAPI.Unshipped.txt` does not mark
  `*REMOVED*`, plus every `Unshipped.txt` addition.
- **Every line added after that until rc.1.** A later line is reviewed in the PR that adds it, because
  `--require-complete` fails on an uncovered one once the record is complete (§5).
- **The non-.NET surfaces** listed in §2, each with its own freeze gate.

At this record's commit (`main` 92ad7a0) the gate counts **2,648 live lines in 23 packages**. The count
moves before the review runs, for three reasons:
- #335 (PublicApiAnalyzers 5.x) regenerates every ledger and adds record-synthesized members;
- #329 moves MassTransit-typed lines into an adapter package;
- G7 appends Oracle enum values and may add `Goldpath.Locking.Oracle`.

The counter, not a hand-typed table, is the source of every number (§7).

**Out of scope:**
- Any verdict: those are API-2 … API-7.
- Applying a rename or a removal: that is API-14 (#403), in one train, with **no compatibility shims**
  (versioning RFC Non-goals: "No compatibility shims/facades for renamed APIs pre-1.0").
- The additions-only freeze gate: that is API-12 (#401).

## 2. Seam Map — the surfaces the review covers

| Surface | Freeze gate | Owner of the rows |
|---|---|---|
| Public .NET API (the 23 ledgers) | `scripts/api-review-gate.sh` (this record), then API-12's additions-only gate (#401) | API-2 … API-7 |
| Meter, span and table names | an inventory gate | API-8 (#397) |
| Admin routes and the signed callback route | a route-contract test per module | API-9 (#398) |
| CLI verbs and flags, template parameters, the manifest schema | an additions-only snapshot gate | API-10 (#399) |
| `[Experimental]` marks on Approvals and FileExchange | the diagnostic ids of §4 | API-11 (#400) |

## 3. Manifest Surface

None. The review changes no manifest value. The manifest schema's freeze is API-10's row in §2.

## 4. API Surface — the verdicts, the method and the cross-cutting policies

**The verdict vocabulary.** One row per live line, in its package's TSV:

| Verdict | Means | `target` | `reason` |
|---|---|---|---|
| `keep` | Frozen at 1.0 as written | — | optional |
| `rename` | Replaced by the line in `target` | the full new ledger line | mandatory |
| `remove` | Deleted at rc.1 | — | mandatory: what replaces it, or why no adopter needs it |
| `experimental` | Ships under `[Experimental]` (outside the 1.x promise) | the diagnostic id | mandatory |

- Renames and removals are applied only by API-14 (#403), in one train, with no shims.
- In the experimental packages (Approvals and FileExchange, GA readiness Q4), every line is
  `experimental`, `rename` or `remove`. `keep` is refused there by the gate.

**The proposed diagnostic ids** for `[Experimental]`:
- **`GPEXP001`** for `Goldpath.Approvals`;
- **`GPEXP002`** for `Goldpath.FileExchange`.

There is one id per package so an adopter opts in to each separately. Neither collides: the analyzer
release files (`AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md`) hold 50 `GP…` ids
today and no `GPEXP` id. API-11 (#400) applies them.

**The method.** Each review part asks every line the same questions, and writes the answer only when
the verdict is not `keep`:
1. Does this need to be public? An adopter must be able to name it in a composition, a handler or a
   test.
2. Is it named like its siblings across modules: `Add*` / `Map*Admin` / `*Options` / `*Extensions` /
   `*AdminService` / `*Info` / `*Request`?
3. Is `CancellationToken` the last parameter, and named `cancellationToken`?
4. Do the nullable annotations say what the code does (`?`, `!`)?
5. Should this be `init` rather than `set`? Mutable configuration stays `set`.
6. Should a data shape be a `record`, and is a positional record's constructor a binary promise worth
   making?
7. Is the generic `TContext` shape (`where TContext : DbContext`) consistent across the modules that
   bind the app's context?
8. Does the line name a bus-engine type? That is G1's business: `scripts/engine-gate.sh`.

**Cross-cutting policies, proposed once here so no group argues them six times:**
- **P1, the flat `Goldpath` namespace.** Every live line is in `Goldpath` (measured: zero lines outside
  it). **Proposed: keep.**
  - One `using Goldpath;` serves every module. Names are already module-prefixed (`GoldpathCampaign…`,
    `GoldpathBulk…`), so collisions do not arise.
  - Splitting namespaces at 1.0 would rename every type for every adopter for no behaviour.
- **P2, `GoldpathAdminResult`.** It is declared in `Goldpath.Jobs` (`GoldpathJobsAdminService.cs`, and
  the Jobs ledger), and every admin surface returns it. **Proposed: `rename` to the same name in
  `Goldpath.Abstractions`.** The source does not change, because the type name and namespace stay. An
  adopter recompiles, and no module needs Jobs to answer a verb. API-2 writes the Jobs row; API-4
  confirms the Abstractions target.
- **P3, records with positional constructors.** A positional parameter list is a binary promise.
  **Proposed:** positional records stay where the shape is the contract (requests, results). Later
  additions are init-only members, as Campaign R3 did, so that no 1.x addition breaks a constructor.

**The package groups**, the gate's own output at this record's commit, summed by review part:

| Review part | Packages (live lines) | Lines |
|---|---|---:|
| API-2 (#391) | Goldpath.Jobs 423 | 423 |
| API-3 (#392) | Goldpath.Bulk 253, Goldpath.Archival 197 | 450 |
| API-4 (#393) | Goldpath.Notification 247, Goldpath.Abstractions 61, Goldpath.Idempotency 29, Goldpath.DataProtection 29, Goldpath.Caching 14, Goldpath.SoftDelete 10 | 390 |
| API-5 (#394) | Goldpath.Approvals 275, Goldpath.FileExchange 187 | 462 |
| API-6 (#395) | Goldpath.Campaign 510 | 510 |
| API-7 (#396) | Goldpath.Messaging 140, Goldpath.Data 33, Goldpath.Locking 19, Goldpath.Locking.SqlServer 2, Goldpath.AuditTrail 44, Goldpath.Auth 45, Goldpath.MultiTenancy 36, Goldpath.ApiDefaults 25, Goldpath.ServiceDefaults 32, Goldpath.Sdk 17, Goldpath.Console 20 | 413 |
| **All** | 23 packages | **2,648** |

Each part re-measures at its own commit with the gate. These numbers are this record's snapshot, not
a promise.

## 5. Analyzer Rules / Gates

`scripts/api-review-gate.sh`, run by the `gates` job in report mode and `--selftest`:

| Mode | Fails on |
|---|---|
| (default) report | a stale row (a `keep`/`experimental` whose line is no longer live, or a `rename` whose source and target are both not live), an unknown verdict, a non-keep without a reason, a `rename`/`experimental` without a target, a duplicate row, a `keep` in an experimental package, a missing or extra TSV, a wrong header or a malformed row |
| `--require-complete [--package P …]` | the above, plus any live line that is neither a row nor a `rename`'s target (all packages, or the named ones) |
| `--require-applied` | the report mode's checks, plus, after API-14: a `rename`/`remove` whose source line is still live, or a live line that is neither kept, experimental nor a rename's target |
| `--list-non-keep` | nothing: it prints the owner's reading list for API-13 |
| `--selftest` | the gate itself: each fault is planted in a scratch copy and must go red, while the unplanted copy, a match across one `[ID]` prefix and an applied review stay green; the applied review is checked in every mode `gates` runs after API-14 |

- **Matching** strips one leading `[ID]` prefix on both sides, because PublicApiAnalyzers 5.x may
  prefix experimental members. Whether it does is measured in API-11 (#400).
- **Each review part's bar** is `--require-complete --package <its packages>` green at its commit.
- **API-13 adds** `--require-complete` (all packages) to `gates`.
- **API-14 adds** `--require-applied`; report and `--require-complete` stay in `gates` beside it.
- **The modes compose on an applied review.** Once API-14 removes a `rename`'s or a `remove`'s source
  and adds the `rename`'s target, the applied source is not stale and the target counts as covered by
  its `rename` row, so all three modes are green together. What stays red in every mode is a member
  leaving without a verdict that says so: a `keep` or `experimental` line that is no longer live, and a
  `rename` applied as a plain deletion (neither its source nor its target live).

## 6. Ops Package

None. The record is a build-time artefact: no runtime surface, metric or runbook.

## 7. Test Plan — the coverage proof

- **The proof that the review covers 100% of lines** is `./scripts/api-review-gate.sh
  --require-complete` green at the review's commit. Today it is red, with 0 of 2,648 covered, by
  design.
- **The gate's own proof** is `--selftest`. It plants 18 faults, each red, against three green
  controls. Each check of the checker can be removed to show the selftest go red; for example,
  disabling the experimental-package `keep` rule turns its planted fault "NOT caught".
- **The numbers in §4** are the gate's output at the PR's commit (§1), never typed by hand.

## 8. Sign-off

*Filled by API-13 (#402).* The owner reads `--list-non-keep` and the cross-cutting policies P1–P3,
and signs, or sends verdicts back to their part. Until then this record is a DRAFT, and no verdict
is applied.
