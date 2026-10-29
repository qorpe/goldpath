#!/usr/bin/env bash
# GA gate G4's counter (docs/rfc/goldpath-1.0-api-review.md §5, §7; #341, #390).
#
# The 1.0 API review marks every LIVE public-API ledger line keep / rename / remove /
# experimental. A live line is a PublicAPI.Shipped.txt line the sibling Unshipped file does not
# mark *REMOVED*, plus every Unshipped addition. The verdicts are data, one TSV per package in
# docs/strategy/api-review-1.0/ (header `line<TAB>verdict<TAB>target<TAB>reason`), so this
# script, not a hand-typed table, is the source of every number the record quotes.
#
# A line is matched after stripping ONE leading `[ID]` prefix, on both sides, because
# PublicApiAnalyzers 5.x may prefix experimental members (whether it does is measured in
# API-11, #400).
#
# Modes:
#   (default)                          report covered/total and the verdict counts per package; fail
#                                      on a malformed table: a stale row (a keep/experimental whose line
#                                      is no longer live, or a rename whose source and target are both
#                                      not live), an unknown verdict, a non-keep without a reason, a
#                                      rename or experimental without a target, an experimental naming
#                                      another id than its package's, a duplicate row, a
#                                      `keep` in an experimental package, a missing or extra TSV, a
#                                      wrong header
#   --require-complete [--package P …] also fail on any live line that is neither a row nor a rename's
#                                      target (all packages, or P)
#   --require-applied                  after API-14 (#403): every live line is covered by keep or
#                                      experimental, or is the target of a rename; no rename/remove
#                                      source line is still live (a rename applied as a plain deletion
#                                      is a stale row in every mode: neither its source nor its target
#                                      is live)
#
# The modes compose on an applied review: once API-14 has removed a rename's or a removal's source
# and added the rename's target, report and --require-complete stay green (an applied source is
# not stale, a rename's target is covered by its row), so `gates` runs all three after API-14.
# A keep or experimental row whose line has gone is stale in EVERY mode: a member the review
# signed off cannot disappear without a verdict that says so.
#   --list-non-keep                    the owner's reading list for API-13 (#402)
#   --selftest                         plant each fault in a scratch copy; red for each, green unplanted
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "$@" <<'PY'
import os, re, shutil, sys, tempfile

TABLES = "docs/strategy/api-review-1.0"
HEADER = ["line", "verdict", "target", "reason"]
VERDICTS = ("keep", "rename", "remove", "experimental")
# Q4 of the GA readiness RFC: these ship [Experimental] at 1.0, so none of their lines may be `keep`.
# The value is the package's proposed diagnostic id (record §4): an `experimental` row there must name it.
EXPERIMENTAL_PACKAGES = {"Goldpath.Approvals": "GPEXP001", "Goldpath.FileExchange": "GPEXP002"}
PREFIX = re.compile(r"^\[[A-Za-z0-9_]+\]")


def bare(line):
    """One leading `[ID]` prefix stripped (an experimental member's mark under PublicApiAnalyzers 5.x)."""
    return PREFIX.sub("", line, count=1)


def read_lines(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return [raw.rstrip("\n").rstrip("\r") for raw in f]


def ledgers(root):
    """{package: set of live bare lines} for every packages/<P> with a PublicAPI ledger."""
    out = {}
    base = os.path.join(root, "packages")
    for package in sorted(os.listdir(base)):
        shipped = os.path.join(base, package, "PublicAPI.Shipped.txt")
        unshipped = os.path.join(base, package, "PublicAPI.Unshipped.txt")
        if not (os.path.exists(shipped) or os.path.exists(unshipped)):
            continue
        removed, added = set(), []
        for line in read_lines(unshipped):
            if not line.strip() or line.startswith("#"):
                continue
            if line.startswith("*REMOVED*"):
                removed.add(bare(line[len("*REMOVED*"):]))
            else:
                added.append(bare(line))
        live = set()
        for line in read_lines(shipped):
            if line.strip() and not line.startswith("#") and bare(line) not in removed:
                live.add(bare(line))
        live.update(added)
        out[package] = live
    return out


def tables(root, findings):
    """{package: [row dict]}; malformed files become findings."""
    out = {}
    directory = os.path.join(root, TABLES)
    for name in sorted(os.listdir(directory)) if os.path.isdir(directory) else []:
        if not name.endswith(".tsv"):
            continue
        package = name[: -len(".tsv")]
        lines = read_lines(os.path.join(directory, name))
        if not lines or lines[0].split("\t") != HEADER:
            findings.append(f"{TABLES}/{name}: the first line must be the header {'<TAB>'.join(HEADER)}")
            out[package] = []
            continue
        rows = []
        for number, raw in enumerate(lines[1:], 2):
            if not raw.strip():
                continue
            cells = raw.split("\t")
            if len(cells) != 4:
                findings.append(f"{TABLES}/{name}:{number}: a row has four tab-separated cells, this one has {len(cells)}")
                continue
            line, verdict, target, reason = (cell.strip() for cell in cells)
            rows.append({"line": bare(line), "verdict": verdict, "target": target, "reason": reason, "at": f"{TABLES}/{name}:{number}"})
        out[package] = rows
    return out


def check(root, require_complete=False, only=None, require_applied=False):
    findings = []
    live = ledgers(root)
    rows = tables(root, findings)
    for package in sorted(set(live) - set(rows)):
        findings.append(f"{package} has a ledger and no {TABLES}/{package}.tsv")
    for package in sorted(set(rows) - set(live)):
        findings.append(f"{TABLES}/{package}.tsv names a package with no ledger")

    summary = {}
    for package in sorted(live):
        table = rows.get(package, [])
        seen = {}
        for row in table:
            where, verdict = row["at"], row["verdict"]
            if row["line"] in seen:
                findings.append(f"{where}: duplicate row (first at {seen[row['line']]}): {row['line']}")
            seen.setdefault(row["line"], where)
            if verdict not in VERDICTS:
                findings.append(f"{where}: unknown verdict '{verdict}' (one of {', '.join(VERDICTS)})")
                continue
            if verdict != "keep" and not row["reason"]:
                findings.append(f"{where}: a '{verdict}' needs a reason")
            if verdict in ("rename", "experimental") and not row["target"]:
                findings.append(f"{where}: a '{verdict}' needs a target ({'the full new ledger line' if verdict == 'rename' else 'the diagnostic id'})")
            if verdict == "experimental" and row["target"] and package in EXPERIMENTAL_PACKAGES and row["target"] != EXPERIMENTAL_PACKAGES[package]:
                findings.append(f"{where}: an 'experimental' in {package} names '{row['target']}', not its diagnostic id {EXPERIMENTAL_PACKAGES[package]}")
            if verdict == "keep" and package in EXPERIMENTAL_PACKAGES:
                findings.append(f"{where}: {package} ships [Experimental] at 1.0 (GA readiness Q4): 'keep' is not a verdict there")
            if row["line"] not in live[package]:
                if verdict in ("keep", "experimental"):
                    findings.append(f"{where}: stale row, a '{verdict}' line is no longer live in {package} (a signed-off member left without a verdict): {row['line']}")
                elif verdict == "rename" and bare(row["target"]) not in live[package]:
                    findings.append(f"{where}: stale row, neither the rename's source nor its target is live in {package}: {row['line']}")
                # A remove whose line has gone, or a rename whose target is live, is an APPLIED verdict.

        targets = {bare(row["target"]) for row in table if row["verdict"] == "rename" and row["target"]}
        covered = live[package] & (set(seen) | targets)
        counts = {verdict: sum(1 for row in table if row["verdict"] == verdict) for verdict in VERDICTS}
        summary[package] = (len(covered), len(live[package]), counts)
        if require_complete and (only is None or package in only):
            for line in sorted(live[package] - set(seen) - targets):
                findings.append(f"{package}: no verdict for a live line: {line}")
        if require_applied:
            kept = {row["line"] for row in table if row["verdict"] in ("keep", "experimental")}
            for row in table:
                if row["verdict"] in ("rename", "remove") and row["line"] in live[package]:
                    findings.append(f"{row['at']}: a '{row['verdict']}' is not applied, the line is still live: {row['line']}")
            for line in sorted(live[package] - kept - targets):
                findings.append(f"{package}: a live line is neither kept, experimental nor a rename's target: {line}")
    if only:
        for package in sorted(set(only) - set(live)):
            findings.append(f"--package {package}: no such package with a ledger")
    return findings, summary


def report(findings, summary, mode):
    total = sum(t for _, t, _ in summary.values())
    covered = sum(c for c, _, _ in summary.values())
    print(f"api-review-gate ({mode}): {covered} of {total} live ledger lines carry a verdict, over {len(summary)} packages")
    print(f"  {'package':<28} {'covered':>8} {'total':>6}  keep rename remove experimental")
    for package, (c, t, counts) in summary.items():
        print(f"  {package:<28} {c:>8} {t:>6}  {counts['keep']:>4} {counts['rename']:>6} {counts['remove']:>6} {counts['experimental']:>12}")
    if findings:
        print(f"api-review-gate: {len(findings)} finding(s):")
        for finding in findings[:200]:
            print(f"  - {finding}")
        if len(findings) > 200:
            print(f"  … and {len(findings) - 200} more")
        return 1
    print("api-review-gate: green.")
    return 0


def list_non_keep(root):
    findings = []
    rows = tables(root, findings)
    for package, table in sorted(rows.items()):
        for row in table:
            if row["verdict"] != "keep":
                target = f" -> {row['target']}" if row["target"] else ""
                print(f"{package}\t{row['verdict']}\t{row['line']}{target}\t{row['reason']}")
    return 0


def selftest():
    def scratch():
        tmp = tempfile.mkdtemp(prefix="api-review-gate-")
        for package in os.listdir("packages"):
            for name in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
                src = os.path.join("packages", package, name)
                if os.path.exists(src):
                    os.makedirs(os.path.join(tmp, "packages", package), exist_ok=True)
                    shutil.copy(src, os.path.join(tmp, "packages", package, name))
        shutil.copytree(TABLES, os.path.join(tmp, TABLES))
        return tmp

    def add_row(root, package, line, verdict="keep", target="", reason=""):
        with open(os.path.join(root, TABLES, f"{package}.tsv"), "a", encoding="utf-8") as f:
            f.write(f"{line}\t{verdict}\t{target}\t{reason}\n")

    def first_live(root, package):
        return sorted(ledgers(root)[package])[0]

    def ledger_append(root, package, line, shipped=False):
        name = "PublicAPI.Shipped.txt" if shipped else "PublicAPI.Unshipped.txt"
        with open(os.path.join(root, "packages", package, name), "a", encoding="utf-8") as f:
            f.write(line + "\n")

    P = "Goldpath.Caching"
    X = "Goldpath.FileExchange"

    def apply_review(root, add_target=True, drop_kept=False):
        """P fully reviewed (one rename, one remove, the rest keep), then applied as API-14 would apply it."""
        lines = sorted(ledgers(root)[P])
        renamed_from, removed_line, rest = lines[0], lines[1], lines[2:]
        gone = {renamed_from, removed_line} | ({rest[0]} if drop_kept else set())
        path = os.path.join(root, "packages", P)
        for name in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
            full = os.path.join(path, name)
            if os.path.exists(full):
                kept_lines = [l for l in read_lines(full) if bare(l) not in gone]
                open(full, "w", encoding="utf-8").write("\n".join(kept_lines) + "\n")
        if add_target:
            ledger_append(root, P, "Goldpath.Renamed.Target() -> void")
        add_row(root, P, renamed_from, "rename", "Goldpath.Renamed.Target() -> void", "naming")
        add_row(root, P, removed_line, "remove", "", "no adopter needs it")
        for l in rest:
            add_row(root, P, l)

    def only_p(findings):
        return [f for f in findings if f.startswith(f"{TABLES}/{P}.tsv") or f.startswith(P + ":")]

    planted = [
        ("a stale row (its line is in no ledger)", {}, lambda r: add_row(r, P, "Goldpath.Nothing.Here() -> void")),
        ("an unknown verdict", {}, lambda r: add_row(r, P, first_live(r, P), "maybe", "", "x")),
        ("a non-keep without a reason", {}, lambda r: add_row(r, P, first_live(r, P), "remove")),
        ("a rename without a target", {}, lambda r: add_row(r, P, first_live(r, P), "rename", "", "consistency")),
        ("an experimental without a target", {}, lambda r: add_row(r, X, first_live(r, X), "experimental", "", "Q4")),
        ("a duplicate row", {}, lambda r: (add_row(r, P, first_live(r, P)), add_row(r, P, first_live(r, P)))),
        ("a keep in an experimental package", {}, lambda r: add_row(r, X, first_live(r, X))),
        ("an experimental naming another package's diagnostic id", {}, lambda r: add_row(r, X, first_live(r, X), "experimental", "GPEXP999", "Q4")),
        ("a package's TSV missing", {}, lambda r: os.remove(os.path.join(r, TABLES, f"{P}.tsv"))),
        ("a TSV for a package with no ledger", {}, lambda r: shutil.copy(os.path.join(r, TABLES, f"{P}.tsv"), os.path.join(r, TABLES, "Goldpath.Ghost.tsv"))),
        ("a wrong header", {}, lambda r: open(os.path.join(r, TABLES, f"{P}.tsv"), "w", encoding="utf-8").write("line\tverdict\n")),
        ("a row with three cells", {}, lambda r: open(os.path.join(r, TABLES, f"{P}.tsv"), "a", encoding="utf-8").write("a\tkeep\tb\n")),
        ("an uncovered live line under --require-complete", {"require_complete": True}, lambda r: None),
        ("an uncovered line in the one package --package names", {"require_complete": True, "only": {P}}, lambda r: None),
        ("--package naming a package with no ledger", {"require_complete": True, "only": {"Goldpath.Ghost"}},
         lambda r: [add_row(r, P, line) for line in sorted(ledgers(r)[P])]),
        ("a rename not applied (its source still live) under --require-applied", {"require_applied": True},
         lambda r: [add_row(r, P, line, "rename" if i == 0 else "keep", "Goldpath.Renamed() -> void" if i == 0 else "", "naming" if i == 0 else "")
                    for i, line in enumerate(sorted(ledgers(r)[P]))]),
        ("a removal not applied under --require-applied", {"require_applied": True},
         lambda r: [add_row(r, P, line, "remove" if i == 0 else "keep", "", "unused" if i == 0 else "") for i, line in enumerate(sorted(ledgers(r)[P]))]),
        ("a live line with no verdict under --require-applied", {"require_applied": True}, lambda r: None),
        ("a rename applied as a deletion (its target never added) under --require-applied", {"require_applied": True},
         lambda r: apply_review(r, add_target=False)),
        ("a rename applied as a deletion, in report mode", {}, lambda r: apply_review(r, add_target=False)),
        ("a kept line deleted from the ledger under --require-applied", {"require_applied": True},
         lambda r: apply_review(r, drop_kept=True)),
        ("a kept line deleted from the ledger, in report mode", {}, lambda r: apply_review(r, drop_kept=True)),
        ("a prefixed ledger line with no verdict, every other line covered, under --require-complete",
         {"require_complete": True, "only": {P}},
         lambda r: ([add_row(r, P, line) for line in sorted(ledgers(r)[P])],
                    ledger_append(r, P, "[GPEXP999]Goldpath.Prefixed.Uncovered() -> void"))),
    ]

    failures = []
    clean = scratch()
    if check(clean)[0]:
        failures.append(f"the unplanted copy is red in report mode: {check(clean)[0][:3]}")
    if not check(clean, require_complete=True)[0]:
        failures.append("--require-complete is green on a tree with no verdicts")

    # A prefixed ledger line is covered by its bare row, and a prefixed row covers a bare line.
    prefixed = scratch()
    line = first_live(prefixed, P)
    ledger_append(prefixed, P, "[GPEXP999]Goldpath.Prefixed.Member() -> void")
    add_row(prefixed, P, "Goldpath.Prefixed.Member() -> void")
    add_row(prefixed, P, f"[GPEXP999]{line}")
    if check(prefixed)[0]:
        failures.append(f"a line matched across one [ID] prefix went red: {check(prefixed)[0][:3]}")

    # An experimental package reviewed line by line under its own diagnostic id is complete and green.
    experimental = scratch()
    for line in sorted(ledgers(experimental)[X]):
        add_row(experimental, X, line, "experimental", EXPERIMENTAL_PACKAGES[X], "Q4")
    if check(experimental, require_complete=True, only={X})[0]:
        failures.append(f"an experimental package under its own id went red: {check(experimental, require_complete=True, only={X})[0][:3]}")

    # An applied review (one renamed: source gone, target live; one removed: gone; the rest kept) is green in
    # EVERY mode `gates` runs after API-14 (record §5): report, --require-complete and --require-applied.
    applied = scratch()
    apply_review(applied)
    for mode, kwargs in (("report", {}), ("--require-complete --package", {"require_complete": True, "only": {P}}),
                         ("--require-applied", {"require_applied": True})):
        findings_applied = only_p(check(applied, **kwargs)[0])
        if findings_applied:
            failures.append(f"an applied review went red in {mode}: {findings_applied[:3]}")

    for name, kwargs, plant in planted:
        root = scratch()
        plant(root)
        found = check(root, **kwargs)[0]
        if kwargs.get("require_applied"):
            found = only_p(found)   # every other package has no verdicts, so it is red in this mode anyway
        if not found:
            failures.append(f"planted fault NOT caught: {name}")
        else:
            print(f"api-review-gate selftest: red as it should be — {name}")
        shutil.rmtree(root)

    for root in (clean, prefixed, experimental, applied):
        shutil.rmtree(root)
    if failures:
        print("api-review-gate selftest FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("api-review-gate selftest: green unplanted, across one [ID] prefix, on an experimental package under its own id and on an applied review in every mode; every planted fault red.")
    return 0


args = sys.argv[1:]
if args[:1] == ["--selftest"]:
    sys.exit(selftest())
if args[:1] == ["--list-non-keep"]:
    sys.exit(list_non_keep("."))
if args[:1] == ["--require-applied"]:
    findings, summary = check(".", require_applied=True)
    sys.exit(report(findings, summary, "require-applied"))
if args[:1] == ["--require-complete"]:
    only = set()
    rest = args[1:]
    while rest:
        if rest[0] != "--package" or len(rest) < 2:
            raise SystemExit("usage: api-review-gate.sh --require-complete [--package Goldpath.X …]")
        only.add(rest[1])
        rest = rest[2:]
    findings, summary = check(".", require_complete=True, only=only or None)
    sys.exit(report(findings, summary, "require-complete"))
if args:
    raise SystemExit("usage: api-review-gate.sh [--require-complete [--package P …] | --require-applied | --list-non-keep | --selftest]")
findings, summary = check(".")
sys.exit(report(findings, summary, "report"))
PY
