# Agent pipeline and merge tiers

How agent pull requests reach `main` in this repository. The decision is
[ADR-0014](../adr/ADR-0014-agent-verified-changes-merge-automatically.md) (proposed until the
owner merges the pull request that introduces it); this page is the operator's view of it.

## The pipeline

An overnight run of Claude Code cloud routines, in four roles:

| Role | Does | Leaves behind |
|---|---|---|
| **planner** | picks the night's work from the ledgers and open issues | a plan the worker follows |
| **worker** | makes one change per pull request, through `.claude/cycle.md` | a pull request labelled **`agent-pr`** |
| **verifier** | an independent pass over that pull request — it re-reads the diff and the checks, it did not write the change | **`agent-verified`** or **`changes-requested`** |
| **morning report** | tells the owner what happened overnight | the audit below |

## The tiers

`.github/merge-tiers.yml` holds every rule, with a comment on each; nothing else decides a
tier. A file takes the highest tier any rule gives it (a renamed file is also judged by its
old name); a pull request takes the highest tier of its files.

| Tier | In short | How it merges |
|---|---|---|
| **C** | decisions (`docs/adr/**`, `docs/rfc/**`), releases (version, template pins, upgrade guides, CHANGELOG version headings, `release.yml`), the gates (`.github/**`, the gate scripts, the mutation configs `stryker/*.json`, suppressions), licence and security policy, the agent layer (`.claude/**`, `CLAUDE.md`) | **the owner merges.** An `agent-pr` here fails `merge-policy` until the owner adds `owner-approved` |
| **B** | packages, analyzers, the CLI, schemas, the console, samples, templates' code, dependencies, migrations | automatically, once `agent-verified` and every required check is green |
| **A** | anything else — tests, docs outside the C paths, small fixes | same as B |

Releases are never automatic: no routine pushes a tag or dispatches `release.yml`.

## The `merge-policy` check

[`.github/workflows/merge-policy.yml`](../../.github/workflows/merge-policy.yml) runs on every
pull request when it is opened, pushed to, reopened or relabelled:

- **job `merge-policy`** (read-only token) — reads the rules and `scripts/merge-tier.py` from
  the **base** branch, lists the changed files through the API, writes the tier and the files
  that put it there to the job summary, and decides. It **fails** on:
  - Tier C + `agent-pr` without `owner-approved`;
  - `hold`, on any pull request.

  It passes otherwise — including Tier C opened by a human, which the human then merges.
  A file list shorter than the pull request's own count, or a content rule with no patch to
  read, is judged Tier C.
- **job `tier-label`** (the only job with a write token; runs no repository code) — sets
  exactly one of `tier:A`, `tier:B`, `tier:C`, creating the labels the first time. A run for
  a commit the pull request has since moved past leaves the label to the newer run.

**No run is ever cancelled** — the workflow has no concurrency group, and the self-test keeps
it that way. Several events in one second (a push plus two label edits) start one run each;
on #361 a shared group cancelled the newest of them, the one branch protection reads, and
`merge-policy` stayed BLOCKED beside a green run on the same commit until the cancelled runs
were re-run one at a time. Every run reads the labels when it starts, so the newest run
judges the newest label state.

It guards only once it is a **required** status check on `main`, beside `ci`'s `gates`,
`integration` and `kit` jobs (the owner's setting — `main` required none on 2026-10-20).

### The self-test

```bash
python3 scripts/merge-tier.py selftest                    # the repository's rules
python3 scripts/merge-tier.py selftest --rules /tmp/x.yml # a candidate rules file
```

It runs the fixtures in `scripts/merge-tier.fixtures.json` (an ADR change is C, a workflow
change is C, a package change is B, a tests-only change is A, a mixed change takes the
highest tier, plus every content rule, renames, missing patches and a truncated list), the
pass/fail decision table, and two structural checks: **every script a workflow runs must be
Tier C**, so a gate added later cannot arrive as Tier A; and **`merge-policy.yml` carries no
concurrency group** (proven red by putting the old `cancel-in-progress: true` block back, and
again with `cancel-in-progress: false`). It runs in `ci.yml` against the
rules a pull request proposes, and inside `merge-policy` against the base's. It was proven
red by deleting the `.github/**` rule from a copy of the rules file: the workflow fixture
and the rules-file fixture turned Tier A and the run exited 1.

## Labels

| Label | Set by | Means |
|---|---|---|
| `agent-pr` | worker | opened by the pipeline |
| `agent-verified` / `changes-requested` | verifier | the independent verdict |
| `tier:A` / `tier:B` / `tier:C` | `merge-policy` | the computed tier — never set by hand |
| `owner-approved` | **the owner only** | lets an agent Tier C pull request pass `merge-policy`; the owner merges it |
| `hold` | anyone | `merge-policy` fails while it is on — nothing merges |

## The identity caveat

The agents act with the owner's GitHub identity, so GitHub cannot distinguish a label or an
approval the agent set from one the owner set. Tier C is enforced by the deterministic check,
the verifier's rules and the morning report's audit — **not by identity**. The hardening (a
GitHub App identity for the agents and code-owner review on the Tier C paths) is
[open-threads](../strategy/open-threads.md) T38.

## The morning report

Lists, for the night: every auto-merged pull request (number, title, tier, squash SHA, the
verifier's verdict); every Tier C pull request waiting for the owner; every pull request left
at `changes-requested`; and every `owner-approved` or `hold` label event that happened while a
routine was running — the window in which the owner did not set it.

## Undo and stop

- **Undo a merge:** `git revert <squash-sha>` on a branch, then a pull request. One pull
  request is one squash commit, and the report gives the SHA. The revert's tier follows its
  files.
- **Stop one pull request:** add `hold`.
- **Stop everything:** disable or delete the routine. Do not remove `merge-policy` from the
  required checks to "pause" it — that removes the guard, not the pipeline.
