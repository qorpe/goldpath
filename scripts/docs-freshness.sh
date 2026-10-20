#!/usr/bin/env bash
# The freshness gate (ADR-0009, curated class): a relative markdown link to a file that
# does not exist fails CI. This is the structural answer to the module-plan-v1.md class
# of rot — a doc may not reference what is not there.
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Both checks run so a contributor sees everything wrong in one pass, and EITHER failing
# fails the gate. Until 2026-09-08 there was no `set -e` and no status collection, so only
# the LAST block's exit code survived: broken links and retired names were printed and then
# silently forgiven — while this file's own header claimed a broken link fails CI. Found by
# the maintainer stop-gate on the day it was written.
STATUS=0
python3 - "$ROOT" <<'PY'
import os, re, sys
root = sys.argv[1]
link = re.compile(r"\]\(([^)#?\s]+)(?:#[^)]*)?\)")
scopes = ["README.md", "CLAUDE.md", "docs", "samples", "schemas", "evals",
          "packages", "analyzers", "rulesets", "skills", "tools", "ui"]
broken = []
for scope in scopes:
    full = os.path.join(root, scope)
    files = []
    if os.path.isfile(full):
        files = [full]
    else:
        skip = {"node_modules", "bin", "obj", "dist", ".pnpm"}
        for base, dirs, names in os.walk(full):
            dirs[:] = [d for d in dirs if d not in skip]
            files += [os.path.join(base, n) for n in names if n.endswith(".md")]
    for f in files:
        text = open(f, encoding="utf-8", errors="replace").read()
        for m in link.finditer(text):
            target = m.group(1)
            if target.startswith(("http://", "https://", "mailto:", "/")):
                continue
            resolved = os.path.normpath(os.path.join(os.path.dirname(f), target))
            if not os.path.exists(resolved):
                broken.append(f"{os.path.relpath(f, root)}: -> {target}")
if broken:
    print("── docs freshness: BROKEN relative links:")
    for b in broken:
        print(f"  {b}")
    sys.exit(1)
print("── docs freshness: all relative links resolve")

# RETIRED NAMES. A tool we no longer use may not quietly reappear in the docs, the
# schema or the templates: the strategy documents carried "WireMock" for four months
# after Mockifyr had overtaken it on every axis we care about, and every reader of
# foundation.md was told to reach for it. Removing a name once is a cleanup; keeping it
# gone is a gate. Add a name here the day a decision retires it.
retired = {
    "wiremock": "Mockifyr is the mock system (foundation.md 5.1, decided 2026-07-28)",
}
exts = {".md", ".json", ".yaml", ".yml", ".cs", ".ts", ".tsx", ".sh", ".props", ".csproj"}
skip = {"node_modules", "bin", "obj", "dist", ".pnpm", ".git", ".next"}
offences = []
for base, dirs, names in os.walk(root):
    dirs[:] = [d for d in dirs if d not in skip]
    for name in names:
        path = os.path.join(base, name)
        if os.path.splitext(name)[1] not in exts or os.path.samefile(path, os.path.join(root, "scripts/docs-freshness.sh")):
            continue
        text = open(path, encoding="utf-8", errors="replace").read().lower()
        for word, why in retired.items():
            if word in text:
                offences.append(f"{os.path.relpath(path, root)}: '{word}' is retired — {why}")
if offences:
    print("── retired names: a name we stopped using is back:")
    for o in offences:
        print(f"  {o}")
    sys.exit(1)
print("── retired names: none of the retired tools are mentioned")
PY
STATUS=$(( STATUS + $? ))

# ── INVENTORY SYNC (#174): every public surface is NAMED where readers look for it, and
#    every numeric claim about a surface matches reality. Same contract as the tests:
#    "the docs are stale" is a RED state, never a silent one.
python3 - "$ROOT" <<'PY'
import os, re, sys
root = sys.argv[1]
fail = []
def read(*parts):
    path = os.path.join(root, *parts)
    return open(path, encoding="utf-8", errors="replace").read() if os.path.exists(path) else ""

# 1. Every package is on the live capability ledger.
ledger = read("docs", "strategy", "coverage-matrix.md")
packages = sorted(d for d in os.listdir(os.path.join(root, "packages"))
                  if d.startswith("Goldpath.") and os.path.isdir(os.path.join(root, "packages", d)))
# The matrix speaks capability SHORT names ("Idempotency"); providers ride their base
# capability's row ("Locking" covers Locking.SqlServer). Either spelling satisfies.
for package in packages:
    short = package.removeprefix("Goldpath.")
    base = short.split(".")[0]
    if package not in ledger and short not in ledger and base not in ledger:
        fail.append(f"package {package} missing from docs/strategy/coverage-matrix.md")

# 2. A doc that COUNTS packages must count them correctly (packages/README said 19 while
#    23 existed — found by this gate's first run).
readme = read("packages", "README.md")
for claim in re.findall(r"(\d+)\s+packages", readme):
    if int(claim) != len(packages):
        fail.append(f"packages/README.md claims {claim} packages; there are {len(packages)}")

# 3. Every CLI verb the dispatcher knows is in the CLI reference.
dispatch = read("tools", "Goldpath.Cli", "CliRunner.cs")
verbs = set()
for m in re.finditer(r'\["([a-z]+)"(?:, "([a-z]+)")?', dispatch):
    verbs.add(m.group(1) if not m.group(2) else f"{m.group(1)} {m.group(2)}")
verbs -= {"new"} if "new " in " ".join(verbs) else set()
# The dispatcher hands `new <kind>` and `db <verb>` to their commands as VARIABLES, so the
# regex above never saw the kinds and the sub-verbs — a new kind or a new db verb could
# ship undocumented (closure audit 2026-09-05). Read them where they are decided.
new_command = read("tools", "Goldpath.Cli", "NewCommand.cs")
for kind in re.findall(r'(?:kind is|") ?"([a-z]+)"(?: =>)', new_command):
    verbs.add(f"new {kind}")
for kind in re.findall(r'kind is "([a-z]+)"', new_command):
    verbs.add(f"new {kind}")
db_command = read("tools", "Goldpath.Cli", "DbCommand.cs")
for verb in re.findall(r'^\s+"([a-z]+)" => ', db_command, re.M):
    verbs.add(f"db {verb}")
verbs.discard("db")   # the bare dispatcher entry — its sub-verbs are the documented surface
reference = read("docs", "guide", "cli-reference.md")
for verb in sorted(verbs):
    if f"goldpath {verb}" not in reference:
        fail.append(f"CLI verb 'goldpath {verb}' missing from docs/guide/cli-reference.md")

# 4. Every golden-manifest shape (.github/gm-shapes.yml — the nightly runs them all) is a
#    row of the golden-manifests doc's §6 table, every row there is a shape, and the doc's
#    "Runs" column says PR exactly for the shapes marked `pr: true` (open-threads T30: the
#    PR-time subset is chosen by coverage, and the doc is where the choice is argued).
shapes_text = read(".github", "gm-shapes.yml")
gm_doc = read("docs", "strategy", "golden-manifests-v1.md")
shapes = {}
for block in re.split(r"\n  - name: ", shapes_text)[1:]:
    name = block.split("\n", 1)[0].strip()
    shapes[name] = bool(re.search(r"^    pr: true\s*$", block, re.M))
doc_rows = {}
for m in re.finditer(r"^\| (Gm[A-Za-z.]+) \|(.*)\|\s*$", gm_doc, re.M):
    doc_rows[m.group(1)] = "PR" in m.group(2).rsplit("|", 1)[-1]
for shape, pr in shapes.items():
    if shape not in doc_rows:
        fail.append(f"shape {shape} (gm-shapes.yml) has no row in docs/strategy/golden-manifests-v1.md §6")
    elif doc_rows[shape] != pr:
        fail.append(f"shape {shape}: gm-shapes.yml says pr={pr}, the doc's Runs column says {'PR' if doc_rows[shape] else 'nightly only'}")
for shape in doc_rows:
    if shape not in shapes:
        fail.append(f"docs/strategy/golden-manifests-v1.md §6 lists {shape}, which .github/gm-shapes.yml does not run")
if not any(shapes.values()):
    fail.append(".github/gm-shapes.yml marks no shape `pr: true` — the PR-time lane would run nothing")

# 4b. Every integration event a PACKAGE publishes is catalogued with its fields in the
#     events guide — the wire contract a consumer binds to may not be undocumented
#     (four of eleven were, found by the 2026-09-01 audit).
events_doc = read("docs", "guide", "integration-events.md")
event_record = re.compile(r"record\s+(Goldpath\w+)\s*\(([^)]*)\)\s*:\s*IIntegrationEvent")
for base, dirs, names in os.walk(os.path.join(root, "packages")):
    dirs[:] = [d for d in dirs if d not in {"bin", "obj"}]
    for name in names:
        if not name.endswith(".cs"):
            continue
        source = open(os.path.join(base, name), encoding="utf-8", errors="replace").read()
        for m in event_record.finditer(source):
            event = m.group(1)
            if f"`{event}`" not in events_doc:
                fail.append(f"integration event {event} ({name}) missing from docs/guide/integration-events.md")
                continue
            for field in [p.strip().split()[-1] for p in m.group(2).split(",") if p.strip()]:
                if field not in events_doc:
                    fail.append(f"integration event {event}: field {field} not named in docs/guide/integration-events.md")

# 5. Every script is referenced SOMEWHERE a reader can find it.
corpus = ""
for scope in ["README.md", "CLAUDE.md"]:
    corpus += read(scope)
for base, dirs, names in os.walk(os.path.join(root, "docs")):
    for n in names:
        if n.endswith(".md"):
            corpus += read(os.path.relpath(os.path.join(base, n), root))
for n in os.listdir(os.path.join(root, ".github", "workflows")):
    corpus += read(".github", "workflows", n)
corpus += "".join(read("scripts", n) for n in os.listdir(os.path.join(root, "scripts")))
for script in sorted(os.listdir(os.path.join(root, "scripts"))):
    if script.endswith((".sh", ".py")) and script not in corpus.replace("docs-freshness.sh", script, 0):
        others = corpus
        if script not in others:
            fail.append(f"scripts/{script} is referenced nowhere (README, CLAUDE.md, docs, workflows, other scripts)")

# 6. Every template is in templates/README.md.
templates_doc = read("templates", "README.md")
for template in sorted(d for d in os.listdir(os.path.join(root, "templates"))
                       if d.startswith("goldpath-") and os.path.isdir(os.path.join(root, "templates", d))):
    if template not in templates_doc:
        fail.append(f"template {template} missing from templates/README.md")

# 7. The mutation ledgers agree on HOW MANY packages the hosted matrix scores. Three places
#    said ten, thirteen and fifteen at once (preview.8 coverage audit, 2026-09-05): the
#    nightly's own comment, the release checklist's parenthetical, and the matrix itself.
WORDS = {10: "ten", 11: "eleven", 12: "twelve", 13: "thirteen", 14: "fourteen", 15: "fifteen",
         16: "sixteen", 17: "seventeen", 18: "eighteen", 19: "nineteen", 20: "twenty"}
nightly_text = read(".github", "workflows", "nightly.yml")
matrix = re.search(r"package: \[(.*?)\]", nightly_text, re.S)
if matrix:
    scored = [p.strip() for p in matrix.group(1).split(",") if p.strip()]
    spelled = WORDS.get(len(scored))
    if spelled is None:
        fail.append(f"mutation matrix has {len(scored)} packages — teach docs-freshness that number word")
    else:
        if f"hosted-fit {spelled}" not in nightly_text:
            fail.append(f"nightly.yml scores {len(scored)} packages but its comment does not say 'hosted-fit {spelled}'")
        checklist = read("docs", "ops", "release-checklist.md")
        if f"{spelled} packages" not in checklist:
            fail.append(f"the mutation matrix scores {len(scored)} packages; release-checklist.md does not say '{spelled} packages'")
else:
    fail.append("nightly.yml has no mutation package matrix — docs-freshness cannot check the count")

# 8. Every stryker exclusion carries a row in the exclusion ledger (stryker/README.md's own
#    rule: "An exclusion without a row below is a finding"). Nine were unrowed until 2026-09-05.
stryker_doc = read("stryker", "README.md")
for config in sorted(n for n in os.listdir(os.path.join(root, "stryker")) if n.endswith(".json")):
    package = config[:-5].replace("Goldpath.", "")
    for line in read("stryker", config).splitlines():
        line = line.strip().strip(",").strip('"')
        if not line.startswith("!"):
            continue
        excluded = line.rsplit("/", 1)[-1]
        if f"`{excluded}`" not in stryker_doc:
            fail.append(f"stryker/{config} excludes {excluded} with no row in stryker/README.md")

if fail:
    print("── inventory sync: the docs stopped telling the truth:")
    for f in fail:
        print(f"  {f}")
    sys.exit(1)
print(f"── inventory sync: {len(packages)} packages, {len(verbs)} CLI verbs, golden-manifest shapes (and their PR subset), scripts and templates all documented")
PY
STATUS=$(( STATUS + $? ))
exit $(( STATUS > 0 ? 1 : 0 ))
