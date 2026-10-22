#!/usr/bin/env python3
"""merge-tier — the deterministic half of ADR-0014 (agent-verified changes merge automatically).

It answers two questions and nothing else, with no network and no LLM:

  classify  Which tier (A < B < C) does a pull request's file list put it in, and which
            files put it there? The rules are DATA in .github/merge-tiers.yml; this script
            only reads them. A file takes the highest tier any rule gives it; the pull
            request takes the highest tier of its files; a file no rule names is Tier A.
  decide    Given that tier and the pull request's labels, may the `merge-policy` check
            pass? It fails on `hold`, and on Tier C carrying `agent-pr` without
            `owner-approved` — the one combination an agent may never merge.

  selftest  Runs the fixtures in scripts/merge-tier.fixtures.json against a rules file and
            checks that every script a workflow runs is itself Tier C (a gate added later
            cannot slip in as Tier A), and that merge-policy.yml carries no concurrency
            group (a cancelled run can leave the required check BLOCKED). Red on a planted
            fault; see docs/ops/agent-pipeline.md.

Usage:
  merge-tier.py classify --rules R --files FILES.jsonl [--expected-count N] [--summary MD]
  merge-tier.py decide   --tier T --labels "a,b,c"
  merge-tier.py selftest [--rules R]

FILES.jsonl is one GitHub "pull request file" object per line (filename, previous_filename,
status, patch) — exactly what `gh api --paginate repos/O/R/pulls/N/files --jq '.[]'` emits.
"""
import argparse
import json
import os
import re
import sys

RANK = {"A": 0, "B": 1, "C": 2}
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_RULES = os.path.join(ROOT, ".github", "merge-tiers.yml")
FIXTURES = os.path.join(ROOT, "scripts", "merge-tier.fixtures.json")


class RuleError(Exception):
    pass


# ── rules ────────────────────────────────────────────────────────────────────────────────
# The rules file is a deliberately tiny YAML subset: top-level `C:` / `B:` keys, each a list
# of single-quoted strings, comments anywhere. It is parsed here WITHOUT a YAML library so the
# gate has no dependency to install, and the parser REFUSES any line it does not understand —
# a rules file that half-parses would silently drop a rule, which is the one failure this gate
# exists to prevent. The self-test cross-checks the parse against PyYAML when it is present.

def _strip_comment(rest, lineno):
    rest = rest.strip()
    if rest and not rest.startswith("#"):
        raise RuleError(f"line {lineno}: unexpected text after the value: {rest!r}")


def _single_quoted(text, lineno):
    """Parse a YAML single-quoted scalar at the start of text; return (value, remainder)."""
    if not text.startswith("'"):
        raise RuleError(f"line {lineno}: every rule must be single-quoted: {text!r}")
    out, i = [], 1
    while i < len(text):
        ch = text[i]
        if ch == "'":
            if i + 1 < len(text) and text[i + 1] == "'":
                out.append("'")
                i += 2
                continue
            return "".join(out), text[i + 1:]
        out.append(ch)
        i += 1
    raise RuleError(f"line {lineno}: unterminated quote: {text!r}")


def parse_rules_text(text):
    """Return {"C": [raw, ...], "B": [raw, ...]} in file order."""
    sections, current = {}, None
    for lineno, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        key = re.fullmatch(r"([ABC]):\s*(#.*)?", line.rstrip())
        if key:
            current = key.group(1)
            if current in sections:
                raise RuleError(f"line {lineno}: tier {current} declared twice")
            if current == "A":
                raise RuleError(f"line {lineno}: Tier A is the default — it takes no rules")
            sections[current] = []
            continue
        item = re.fullmatch(r"\s+-\s+(.*)", line)
        if item and current:
            value, rest = _single_quoted(item.group(1), lineno)
            _strip_comment(rest, lineno)
            sections[current].append(value)
            continue
        raise RuleError(f"line {lineno}: not a tier key or a quoted list item: {line!r}")
    if "C" not in sections:
        raise RuleError("the rules declare no Tier C — refusing to classify everything as A/B")
    return sections


def glob_to_regex(glob):
    """`**/` = any directories (or none), `**` = anything, `*` = within one segment, `?` = one char."""
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("".join(out))


class Rule:
    """`'glob'` matches by path; `'glob | regex'` also needs a changed patch line to match."""

    def __init__(self, tier, raw):
        self.tier, self.raw = tier, raw
        glob, sep, content = raw.partition(" | ")
        self.glob = glob.strip()
        self.path = glob_to_regex(self.glob)
        try:
            self.content = re.compile(content.strip()) if sep else None
        except re.error as error:
            raise RuleError(f"rule {raw!r}: bad regex: {error}") from error

    def matches(self, filename, patch):
        """Return None, or a short reason string when this rule claims the file."""
        if not self.path.fullmatch(filename):
            return None
        if self.content is None:
            return f"path {self.glob}"
        if patch is None:
            # Binary, or too large for the API to inline: the content cannot be read, so it
            # is judged as if it matched. Fail closed — a human looks.
            return f"path {self.glob} (no patch to inspect — judged conservatively)"
        for line in patch.splitlines():
            if line.startswith(("+", "-")) and self.content.search(line):
                return f"path {self.glob} with a changed line matching /{self.content.pattern}/"
        return None


def load_rules(path):
    with open(path, encoding="utf-8") as handle:
        sections = parse_rules_text(handle.read())
    return [Rule(tier, raw) for tier in ("C", "B") for raw in sections.get(tier, [])]


# ── classify ─────────────────────────────────────────────────────────────────────────────

def classify_file(rules, filename, patch):
    best = ("A", "no rule names it")
    for rule in rules:
        if RANK[rule.tier] <= RANK[best[0]]:
            continue
        reason = rule.matches(filename, patch)
        if reason:
            best = (rule.tier, reason)
    return best


def classify(rules, files, expected_count=None):
    """files: list of dicts with filename, optional previous_filename, optional patch."""
    rows = []
    for entry in files:
        names = [entry["filename"]]
        if entry.get("previous_filename"):
            names.append(entry["previous_filename"])  # a rename OUT of a C path is still C
        tier, reason, shown = "A", "no rule names it", entry["filename"]
        for name in names:
            t, r = classify_file(rules, name, entry.get("patch"))
            if RANK[t] > RANK[tier]:
                tier, reason, shown = t, r, name
        rows.append({"file": shown, "tier": tier, "reason": reason})
    if expected_count is not None and len(files) != expected_count:
        # The files API stops at 3000 entries; a list that disagrees with the PR's own count
        # cannot be trusted to have shown us every C path.
        rows.append({"file": "(file list)", "tier": "C",
                     "reason": f"listed {len(files)} files but the pull request reports "
                               f"{expected_count} — an incomplete list is judged as Tier C"})
    tier = max((row["tier"] for row in rows), key=RANK.get, default="A")
    return {"tier": tier, "files": len(files),
            "because": [row for row in rows if row["tier"] == tier and tier != "A"],
            "rows": rows}


# ── decide ───────────────────────────────────────────────────────────────────────────────

def decide(tier, labels):
    """Return (passes, message). Pure — the workflow and the self-test call the same code."""
    labels = set(labels)
    if "hold" in labels:
        return False, "`hold` is set: nothing merges until a human removes it"
    if tier == "C" and "agent-pr" in labels and "owner-approved" not in labels:
        return False, ("Tier C pull request opened by the agent pipeline without `owner-approved`: "
                       "decisions, releases, gates, licence/security policy and the agent layer "
                       "are merged by the owner, never by the verifier (ADR-0014)")
    if tier == "C":
        return True, "Tier C: a human merges this (the check passes; branch protection and the owner do the rest)"
    if "agent-pr" in labels:
        return True, f"Tier {tier}: may merge automatically once `agent-verified` and every required check is green"
    return True, f"Tier {tier}: not an agent pull request — ordinary human review"


# ── self-test ────────────────────────────────────────────────────────────────────────────

def workflow_scripts():
    """Every scripts/<name> a workflow runs — each is a gate, so each must be Tier C."""
    found = set()
    folder = os.path.join(ROOT, ".github", "workflows")
    for name in sorted(os.listdir(folder)):
        if name.endswith((".yml", ".yaml")):
            text = open(os.path.join(folder, name), encoding="utf-8").read()
            found.update(re.findall(r"scripts/[A-Za-z0-9_.-]+\.(?:sh|py)", text))
    return sorted(found)


def policy_concurrency():
    """Line numbers of any `concurrency:` key in merge-policy.yml — there must be none.

    A group cancels runs, and a cancelled run can be the newest check suite on the commit,
    the one branch protection reads: `merge-policy` then stays BLOCKED beside a green run
    (#361). Every run reads the labels when it starts, so nothing needs cancelling."""
    path = os.path.join(ROOT, ".github", "workflows", "merge-policy.yml")
    lines = open(path, encoding="utf-8").read().splitlines()
    return [n for n, line in enumerate(lines, 1) if re.match(r"\s*concurrency\s*:", line)]


def selftest(rules_path):
    failures = []
    try:
        rules = load_rules(rules_path)
    except RuleError as error:
        print(f"── merge-tier selftest: the rules do not parse: {error}")
        return 1

    # The parser and PyYAML must agree, so the file means the same thing to a human's
    # YAML-highlighting editor as it does to the gate.
    try:
        import yaml  # optional
        loaded = yaml.safe_load(open(rules_path, encoding="utf-8").read())
        mine = parse_rules_text(open(rules_path, encoding="utf-8").read())
        if loaded != mine:
            failures.append("the rules file parses differently under PyYAML than under the gate")
    except ImportError:
        print("── merge-tier selftest: PyYAML absent — the cross-parse check is skipped")

    fixtures = json.load(open(FIXTURES, encoding="utf-8"))
    for case in fixtures["classify"]:
        result = classify(rules, case["files"], case.get("expected_count"))
        ok = result["tier"] == case["expect"]
        names = sorted(row["file"] for row in result["because"])
        if ok and "because" in case and names != sorted(case["because"]):
            ok = False
        print(f"  {'ok  ' if ok else 'FAIL'} classify  {case['name']}: want {case['expect']}, got {result['tier']}"
              + ("" if ok else f" (because {names})"))
        if not ok:
            failures.append(f"classify: {case['name']}")
    for case in fixtures["decide"]:
        passes, _ = decide(case["tier"], case["labels"])
        ok = passes == case["passes"]
        print(f"  {'ok  ' if ok else 'FAIL'} decide    {case['name']}: want {'pass' if case['passes'] else 'fail'}, "
              f"got {'pass' if passes else 'fail'}")
        if not ok:
            failures.append(f"decide: {case['name']}")
    for script in workflow_scripts():
        tier, _ = classify_file(rules, script, None)
        ok = tier == "C"
        print(f"  {'ok  ' if ok else 'FAIL'} gate      {script} is run by a workflow: want C, got {tier}")
        if not ok:
            failures.append(f"gate script not Tier C: {script}")
    found = policy_concurrency()
    print(f"  {'ok  ' if not found else 'FAIL'} workflow  merge-policy.yml has no concurrency group"
          + ("" if not found else f" (line {', '.join(map(str, found))})"))
    if found:
        failures.append("merge-policy.yml has a concurrency group: a cancelled run can leave the required check BLOCKED")

    if failures:
        print(f"── merge-tier selftest: RED ({len(failures)} failing)")
        for failure in failures:
            print(f"  {failure}")
        return 1
    print("── merge-tier selftest: green")
    return 0


# ── cli ──────────────────────────────────────────────────────────────────────────────────

def write_summary(path, result):
    lines = [f"## merge-policy: Tier {result['tier']}", "",
             f"{result['files']} changed file(s). A file takes the highest tier any rule in "
             "`.github/merge-tiers.yml` gives it; the pull request takes the highest of its files.", ""]
    if result["because"]:
        lines += [f"Files that make it Tier {result['tier']}:", "", "| File | Rule |", "|---|---|"]
        cell = lambda text: text.replace("|", "\\|")  # a regex alternation must not split the table
        lines += [f"| `{cell(row['file'])}` | {cell(row['reason'])} |" for row in result["because"]]
    else:
        lines.append("No rule names any changed file — Tier A.")
    with open(path, "a", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    c = sub.add_parser("classify")
    c.add_argument("--rules", default=DEFAULT_RULES)
    c.add_argument("--files", required=True)
    c.add_argument("--expected-count", type=int)
    c.add_argument("--summary")
    d = sub.add_parser("decide")
    d.add_argument("--tier", required=True, choices=sorted(RANK))
    d.add_argument("--labels", default="")
    s = sub.add_parser("selftest")
    s.add_argument("--rules", default=DEFAULT_RULES)
    args = parser.parse_args(argv)

    if args.command == "selftest":
        return selftest(args.rules)
    if args.command == "decide":
        passes, message = decide(args.tier, [l for l in args.labels.split(",") if l])
        print(message if passes else f"::error title=merge-policy::{message}")
        return 0 if passes else 1

    rules = load_rules(args.rules)
    with open(args.files, encoding="utf-8") as handle:
        files = [json.loads(line) for line in handle if line.strip()]
    result = classify(rules, files, args.expected_count)
    if args.summary:
        write_summary(args.summary, result)
    print(json.dumps({"tier": result["tier"], "because": result["because"]}, indent=2))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except RuleError as error:
        print(f"::error title=merge-policy::the rules file does not parse: {error}")
        sys.exit(2)
