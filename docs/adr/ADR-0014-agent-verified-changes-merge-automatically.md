# ADR-0014: Agent-verified changes merge automatically — except decisions, releases and the gates

- Status: **proposed** (2026-10-20 — owner decision given in chat the same day; **acceptance
  is the owner merging the pull request that introduces this ADR**, which is itself Tier C
  under the rule below) · Scoped amendment of ADR-0010 (human gates are fixed) for ONE gate
  in ONE repository; composes with ADR-0005 (the tier rule ships with its verifier and its
  self-test), ADR-0007 (the agent layer changes only by human merge) and ADR-0008 (no merge
  while the golden-manifest matrix is red — a required check stays required)

## Context

An overnight agent pipeline maintains this repository: a planner picks work, a worker opens
pull requests labelled `agent-pr`, an **independent verifier** re-checks each one and labels
it `agent-verified` or `changes-requested`, and a morning report tells the owner what
happened. They run as Claude Code cloud routines. The owner has little review time; under
ADR-0010 as written every one of those pull requests waits for a human approval before it
merges, so the pipeline produces a queue, not progress.

## Decision

1. **What is amended.** ADR-0010's *MR approval (developer)* gate, for pull requests in
   **this repository** that carry `agent-pr` and `agent-verified` and fall in Tier A or B
   below: the human approval BEFORE merge is replaced by the independent verifier's verdict
   plus every required status check green, and the human review moves to AFTER merge — the
   morning report lists every auto-merged pull request, and any of them is undone by one
   revert.
2. **What stays exactly as ADR-0010 says.** *Spec approval* (business) and *cutover
   approval* (ops) are untouched. Every agent output still arrives as a pull request.
   **Decisions and releases stay human:** ADRs, RFCs, the release train and the gates are
   Tier C, and a Tier C change is merged by the owner. **Tags → NuGet stay human always** —
   no routine pushes a tag or runs a release. The templates ADR-0010 ships to adopters, and
   every app generated from them, keep ADR-0010 unamended: this ADR governs how Goldpath is
   maintained, not what Goldpath promises its adopters. Extending it there needs its own ADR.
3. **The tiers.** A file takes the highest tier any rule gives it; a pull request takes the
   highest tier of its files. The rules are data, in ONE file — `.github/merge-tiers.yml` —
   and nothing else decides a tier.

   | Tier | What | Merge |
   |---|---|---|
   | **C** | **Decisions:** `docs/adr/**`, `docs/rfc/**` (any RFC change — the practical form of "sets an RFC to ACCEPTED or edits an ACCEPTED RFC's decisions"). **Releases / the train:** `VersionPrefix`/`VersionSuffix` in `Directory.Build.props`, the templates' `Goldpath.*` pins, `docs/upgrades/**`, a `CHANGELOG.md` version heading, `.github/workflows/release.yml`. **The gates themselves:** `.github/**` (workflows, the tier rules, `gm-shapes.yml`, `CODEOWNERS`, `dependabot.yml`), `scripts/*gate*`, `docs-freshness.sh`, `ledger-check.sh`, `schema-honesty.sh`, `skills-parity.sh`, `template-pins.sh`, `stryker/**` configs and thresholds. **Licence policy:** `scripts/license-gate.py`. **Security policy:** `SECURITY.md`. **The agent layer:** `.claude/**`, `templates/*/.claude/**` | **always the owner.** An `agent-pr` here fails `merge-policy` until the owner adds `owner-approved` |
   | **B** | Public API (`packages/**`, `analyzers/**`, `tools/**`, `schemas/**`, `ui/**`, `samples/**`), the templates' code, dependencies (`Directory.Packages.props`, `*.csproj`, `global.json`, `nuget.config`, `.config/**`, `package.json`, lockfiles), migrations (`**/Migrations/**`) | automatically, once `agent-verified` and every required check is green |
   | **A** | Everything no rule names — tests, docs outside the C paths, small fixes | automatically, on the same condition as B. A and B differ only in what the morning report points at |

   The rules file also carries a few **extensions** of the C list, each marked `EXTENSION`
   and each the decision's own principle — *the gates themselves* — applied to something
   the named list did not spell out: every other script a workflow runs (the self-test
   fails if a workflow ever runs a script the rules leave below C), `.editorconfig`, the
   warnings-as-errors and `NoWarn` properties, an added `#pragma warning disable` /
   `SuppressMessage` / skipped test (ADR-0005: suppression without justification is
   forbidden, so a human sees every new one), the skill evals (ADR-0007), the sample's copy
   of the agent layer, every `CLAUDE.md`, and the two `Goldpath.Templates@` pins
   `template-pins.sh` checks in the docs. The owner accepts them by merging; striking one is
   a one-line edit of the rules file, which is Tier C.
4. **Enforcement, in three layers — and why three.**
   1. **Deterministic** — `.github/workflows/merge-policy.yml`, job `merge-policy`, run on
      every pull request event that can change the answer (opened, synchronize, labeled,
      unlabeled, reopened). It computes the tier with `scripts/merge-tier.py` from the
      rules **as the base branch has them** (a pull request never judges itself by the
      rules it proposes), writes the tier and the files that put it there to the job
      summary, sets exactly one `tier:A|B|C` label from a separate job that alone holds a
      write token, and FAILS on Tier C + `agent-pr` without `owner-approved`, and on
      `hold`. An unreadable patch on a content rule, or a file list shorter than the pull
      request's own count, is judged Tier C — it fails closed. The tier computation ships
      with a self-test (fixtures for every rule family, the pass/fail decision table, and
      the every-workflow-script-is-C check), run in `ci.yml` against the rules a pull
      request proposes and in `merge-policy` against the base's.
   2. **The verifier's rules** — on a Tier C pull request the verifier's verdict is advice
      to the owner, never a merge; it never adds `owner-approved` or removes `hold`, never
      merges through a red or missing `merge-policy`, and never edits its own rules (the
      agent layer is Tier C).
   3. **The morning report's audit** — every auto-merged pull request (number, tier, squash
      commit, the verifier's verdict), every Tier C pull request waiting for the owner, and
      every `owner-approved` or `hold` label event that happened while a routine was
      running — the one window in which the owner did not set it.
5. **The identity caveat, recorded as fact.** The agents act with the **owner's** GitHub
   identity — their pull requests are authored by the owner's account — so GitHub cannot
   tell a label or an approval the agent set from one the owner set. Branch protection
   cannot express "the owner, but not the agent". Tier C is therefore enforced by the three
   layers above, **not by identity**: an agent that disregarded its rules could add
   `owner-approved` itself and the check would pass. Layer 3 is what would catch it, after
   the fact. The hardening that closes this — a separate GitHub App identity for the agents
   plus CODEOWNERS with *require review from code owners* on the Tier C paths — is open
   thread **T38**; until it lands, ADR-0010's "gate bypassing is technically closed off"
   is TRUE for Tier A/B (the required checks) and **procedural** for Tier C.
6. **Revert.** Agent pull requests merge as ONE squash commit, so any of them is undone by
   `git revert <squash-sha>` on a branch and a pull request (the revert's own tier follows
   its files — reverting a Tier C merge is Tier C). The morning report gives the squash SHA
   of every auto-merged pull request, so the owner never has to reconstruct it.
7. **Suspension.** Per pull request: add the label **`hold`** — `merge-policy` fails while it
   is on, so nothing merges it. All of it: **disable or delete the routine** — no new agent
   pull requests and no merges. Removing `merge-policy` from the required checks does NOT
   suspend the policy; it removes the only deterministic guard, and is itself a gate change.

## Rationale

ADR-0010 is right about the question an auditor asks — "who approved this?" — and that
question has to keep a human answer for the things that ARE decisions: what the platform
is (ADRs), what a module will be (RFCs), what ships under a version number (releases), and
what counts as proof (the gates). Most of an overnight pipeline's output is none of those:
a test, a doc fix, a defect fix inside a package. For those, the honest answer to "who
approved this?" becomes "an independent verifier and every gate, under the owner's standing
decision ADR-0014, reviewed the next morning, reversible by one commit" — and that answer
is written in git: the pull request, its labels, its check runs, the squash commit, the
report. Requiring a human click before each one would not make it safer; it would make the
pipeline wait for a reviewer who is not there. The line is drawn by PATH because a path is
something a script can decide without judgement — the judgement stays with the owner, who
drew the line once, here.

## Consequences

- `merge-policy` is only a guard once it is **required**: after this merges, the owner adds
  it — together with `ci`'s `gates`, `integration` and `kit` jobs — to `main`'s required
  status checks. Measured 2026-10-20: `main` requires no status checks at all today (it does
  enforce the rules on administrators, which matters because the agents act as one). Until
  that setting is made, "every required check green" is an empty condition and no routine
  should merge anything.
- The pipeline's merge step merges with `--squash` and only when `agent-verified`, every
  required check and `merge-policy` are green; it never uses an admin bypass.
- A new gate, a new decision folder or a new release surface is a one-line edit of
  `.github/merge-tiers.yml` — and that edit is Tier C, so the owner sees every change to
  the line itself.
- Documented for operators in [docs/ops/agent-pipeline.md](../ops/agent-pipeline.md); the
  hardening is [open-threads](../strategy/open-threads.md) T38.
