#!/usr/bin/env bash
# GA gate G4, API-10 (#399; docs/rfc/goldpath-1.0-api-review.md §2): the non-.NET surfaces an
# adopter types or writes, frozen additions-only.
#
# One committed snapshot, scripts/surface-freeze-gate.snapshot.txt, holds a line per
#   cli-verb <verb form>                   every verb form the dispatcher knows
#   cli-flag <verb form> <flag>            every flag that verb's parser accepts
#   cli-passthrough <verb form>            a verb that hands its arguments on unread (dotnet new);
#                                          its flags are the template's parameters below
#   cli-global <flag>                      a top-level flag that is a verb of its own (--help, --version)
#   template-param <template> <name>       every `type: parameter` symbol of templates/*/.template.config
#   template-choice <template> <name> <c>  every choice of such a parameter
#   schema-property <JSON pointer>         every property of the manifest schema v1
#   schema-enum <JSON pointer> <value>     every enum value of the manifest schema v1 (JSON-encoded)
#
# The CLI lines come from the PARSER, not from a grep: scripts/surface-freeze-gate.probe.cs drives
# CliRunner.Run with every flag literal of tools/Goldpath.Cli against every verb form and keeps the
# flags the parser does not reject the way it rejects an undeclared one. The CLI has no parse table
# and no per-verb --help, and its one --help text omits --path on four verb forms, so neither could
# be the source (measured on #399).
#
# It fails when a snapshot line disappears (a removal; a rename is a removal plus an addition), and
# on any line the snapshot lacks, telling the author to add it. Versioning D2 counts a removal as
# breaking; an addition is allowed, but only on the record. `$comment`, `description`, `default`,
# `title` and `examples` are never read, so changing them cannot turn the gate red.
#
# Modes:
#   (default)    compare the tree with the snapshot
#   --print      print what the tree declares, in snapshot form (to review an addition)
#   --selftest   plant each fault in a scratch copy of the tree: red for each, green unplanted
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "$@" <<'PY'
import json, pathlib, re, shutil, subprocess, sys, tempfile

SNAPSHOT = pathlib.Path("scripts/surface-freeze-gate.snapshot.txt")
SCHEMA = pathlib.Path("schemas/manifest/v1/goldpath-manifest.schema.json")
# Keywords that carry prose or a value, never a name an adopter's manifest uses.
IGNORED = {"$comment", "description", "default", "title", "examples"}


def cli_verbs(root):
    """Every verb form, read where it is decided: the dispatcher, NewCommand's kinds, DbCommand's verbs."""
    cli = root / "tools" / "Goldpath.Cli"
    dispatch = (cli / "CliRunner.cs").read_text(encoding="utf-8")
    verbs = set()
    for m in re.finditer(r'\["([a-z]+)"(?:, "([a-z]+)")?', dispatch):
        verbs.add(m.group(1) if not m.group(2) else f"{m.group(1)} {m.group(2)}")
    # `new <kind>`: the heads are tested as `kind is "service" or "gateway"`, the templates are the
    # arms of the `"solution" => "goldpath-solution"` switch. (docs-freshness block 3's reading
    # misses the switch arms: open-threads T65.)
    new_command = (cli / "NewCommand.cs").read_text(encoding="utf-8")
    for test in re.findall(r'kind is ("[a-z]+"(?: or "[a-z]+")*)', new_command):
        verbs.update(f"new {kind}" for kind in re.findall(r'"([a-z]+)"', test))
    for kind in re.findall(r'^\s+"([a-z]+)" => "goldpath-', new_command, re.M):
        verbs.add(f"new {kind}")
    for verb in re.findall(r'^\s+"([a-z]+)" => ', (cli / "DbCommand.cs").read_text(encoding="utf-8"), re.M):
        verbs.add(f"db {verb}")
    verbs.discard("db")  # the bare dispatcher entry: its sub-verbs are the surface
    return sorted(verbs)


def cli_flags(root):
    """Every flag-shaped literal in the CLI's source: the candidates the probe asks the parser about."""
    found = set()
    for path in sorted((root / "tools" / "Goldpath.Cli").glob("*.cs")):
        found.update(re.findall(r'"(--?[A-Za-z0-9][A-Za-z0-9-]*)"', path.read_text(encoding="utf-8")))
    return sorted(found)


def cli_lines(root):
    probe = root / "scripts" / "surface-freeze-gate.probe.cs"
    run = subprocess.run(
        ["dotnet", "run", str(probe), "--", *cli_verbs(root), "--", *cli_flags(root)],
        capture_output=True, text=True, encoding="utf-8")
    if run.returncode != 0:
        sys.exit(f"surface-freeze-gate: the CLI probe failed ({run.returncode}):\n{run.stdout}{run.stderr}")
    lines = {line for line in run.stdout.split("\n") if line.startswith("cli-")}
    if not lines:
        sys.exit("surface-freeze-gate: the CLI probe printed no line; refusing to compare an empty surface")
    return lines


def template_lines(root):
    lines = set()
    configs = sorted((root / "templates").glob("*/.template.config/template.json"))
    if not configs:
        sys.exit("surface-freeze-gate: no templates/*/.template.config/template.json found")
    for config in configs:
        template = config.parent.parent.name
        for name, symbol in json.loads(config.read_text(encoding="utf-8")).get("symbols", {}).items():
            if symbol.get("type") != "parameter":
                continue
            lines.add(f"template-param {template} {name}")
            for choice in symbol.get("choices", []):
                lines.add(f"template-choice {template} {name} {choice['choice']}")
    return lines


def schema_lines(root):
    lines = set()

    def escape(key):
        return key.replace("~", "~0").replace("/", "~1")

    def walk(node, pointer):
        if isinstance(node, dict):
            for key, value in node.items():
                if key in IGNORED:
                    continue
                if key == "properties" and isinstance(value, dict):
                    for name, sub in value.items():
                        child = f"{pointer}/properties/{escape(name)}"
                        lines.add(f"schema-property {child}")
                        walk(sub, child)
                elif key == "enum" and isinstance(value, list):
                    for item in value:
                        lines.add(f"schema-enum {pointer or '/'} {json.dumps(item, ensure_ascii=False)}")
                else:
                    walk(value, f"{pointer}/{escape(key)}")
        elif isinstance(node, list):
            for index, item in enumerate(node):
                walk(item, f"{pointer}/{index}")

    walk(json.loads((root / SCHEMA).read_text(encoding="utf-8")), "")
    return lines


def observe(root):
    return cli_lines(root) | template_lines(root) | schema_lines(root)


def read_snapshot(text):
    """The snapshot's lines (a `#` line is a comment) and any line typed twice."""
    lines = [l for l in text.split("\n") if l and not l.startswith("#")]
    return set(lines), sorted({l for l in lines if lines.count(l) > 1})


def compare(observed, snapshot, duplicates):
    problems = [f"duplicate snapshot line: {l}" for l in duplicates]
    for line in sorted(snapshot - observed):
        problems.append(f"REMOVED (or renamed): {line} — a breaking change under versioning D2; "
                        f"restore it, or the 1.0 review records the break and this line leaves the snapshot in that PR")
    for line in sorted(observed - snapshot):
        problems.append(f"NOT IN THE SNAPSHOT: {line} — an addition is allowed, on the record: "
                        f"add this line to {SNAPSHOT}")
    return problems


def counts(lines):
    kinds = {}
    for line in lines:
        kind = line.split(" ", 1)[0]
        kinds[kind] = kinds.get(kind, 0) + 1
    return ", ".join(f"{kinds[k]} {k}" for k in sorted(kinds))


def selftest():
    """Plant each fault in a scratch copy of the inputs; each must go red, the unplanted copy green."""
    real_cli = cli_lines(pathlib.Path("."))
    snapshot, _ = read_snapshot(SNAPSHOT.read_text(encoding="utf-8"))
    failures = []

    def scratch(with_cli_source=False):
        work = pathlib.Path(tempfile.mkdtemp(prefix="surface-freeze-selftest-"))
        for name in ("templates", "schemas"):
            shutil.copytree(name, work / name, ignore=shutil.ignore_patterns("bin", "obj"))
        if with_cli_source:
            for name in ("Directory.Build.props", "Directory.Packages.props", "global.json", "nuget.config", ".editorconfig"):
                if pathlib.Path(name).exists():
                    shutil.copy(name, work / name)
            shutil.copytree("tools/Goldpath.Cli", work / "tools" / "Goldpath.Cli", ignore=shutil.ignore_patterns("bin", "obj"))
            (work / "scripts").mkdir()
            shutil.copy("scripts/surface-freeze-gate.probe.cs", work / "scripts" / "surface-freeze-gate.probe.cs")
        return work

    def edit(path, old, new):
        text = path.read_text(encoding="utf-8")
        if old not in text:
            sys.exit(f"surface-freeze-gate selftest: the plant's anchor is gone from {path}: {old!r}")
        path.write_text(text.replace(old, new, 1), encoding="utf-8")

    def schema_edit(work, change):
        path = work / SCHEMA
        document = json.loads(path.read_text(encoding="utf-8"))
        change(document)
        path.write_text(json.dumps(document, indent=2, ensure_ascii=False), encoding="utf-8")

    def expect(name, work, red, must_name=None):
        # must_name is the exact problem a planted fault has to produce (the text before its " — "):
        # a substring would let "--next-ids" stand in for "--next-id".
        # A scratch copy that carries the CLI's source is probed afresh; the others reuse the real probe's lines.
        cli = cli_lines(work) if (work / "tools").exists() else real_cli
        observed = cli | template_lines(work) | schema_lines(work)
        problems = compare(observed, snapshot, [])
        shutil.rmtree(work, ignore_errors=True)
        if red and not problems:
            failures.append(f"NOT caught: {name}")
        elif red and must_name and not any(p.startswith(f"{must_name} — ") for p in problems):
            failures.append(f"caught for the wrong reason: {name} (no problem names {must_name!r}): {problems}")
        elif not red and problems:
            failures.append(f"red on a green control: {name}: {problems}")
        else:
            print(f"surface-freeze-gate selftest: {'red' if red else 'green'} as it should be — {name}")

    # Green controls: the unplanted tree, and a change to every ignored keyword.
    expect("the unplanted tree", scratch(), red=False)
    work = scratch()

    def prose(document):
        document["$comment"] = "planted prose"
        document["description"] = "planted description"
        first = next(iter(document["properties"].values()))
        first["description"] = "planted"
        first["default"] = "planted"
    schema_edit(work, prose)
    expect("a $comment, description and default change", work, red=False)

    # The CLI, planted in its SOURCE and read back through the probe.
    work = scratch(with_cli_source=True)
    edit(work / "tools/Goldpath.Cli/CliRunner.cs", 'case "--next-id":', 'case "--next-ids":')
    expect("a renamed flag (spec ready --next-id → --next-ids, in CliRunner.cs)", work, red=True,
           must_name="REMOVED (or renamed): cli-flag spec ready --next-id")
    # The literal "--trigger" stays in WizardCommand.cs, so a grep would still see the flag; only the
    # parser can tell that `add worker` no longer takes it.
    work = scratch(with_cli_source=True)
    edit(work / "tools/Goldpath.Cli/CliRunner.cs",
         'case "--trigger" when i + 1 < rest.Count:\n                    trigger = rest[i + 1];\n                    break;\n', "")
    expect("a flag one verb stops parsing while its literal stays in the source (add worker --trigger)", work, red=True,
           must_name="REMOVED (or renamed): cli-flag add worker --trigger")
    work = scratch(with_cli_source=True)
    edit(work / "tools/Goldpath.Cli/CliRunner.cs",
         '["discover", .. var rest] => DiscoverCommand.Run(ParsePath(rest), output, error),\n', "")
    expect("a removed verb (discover, from the dispatcher)", work, red=True, must_name="REMOVED (or renamed): cli-verb discover")

    # The templates.
    work = scratch()
    edit(work / "templates/goldpath-solution/.template.config/template.json", '"choice": "sqlserver"', '"choice": "mssql"')
    expect("a renamed template choice (goldpath-solution db sqlserver → mssql)", work, red=True,
           must_name="REMOVED (or renamed): template-choice goldpath-solution db sqlserver")
    work = scratch()
    config = work / "templates/goldpath-worker/.template.config/template.json"
    document = json.loads(config.read_text(encoding="utf-8"))
    document["symbols"]["trigger"]["choices"] = [c for c in document["symbols"]["trigger"]["choices"] if c["choice"] != "jobs"]
    config.write_text(json.dumps(document, indent=2, ensure_ascii=False), encoding="utf-8")
    expect("a removed template choice (goldpath-worker trigger jobs)", work, red=True,
           must_name="REMOVED (or renamed): template-choice goldpath-worker trigger jobs")

    # The manifest schema.
    work = scratch()
    schema_edit(work, lambda d: d["properties"].pop("owner"))
    expect("a removed schema property (/properties/owner)", work, red=True, must_name="REMOVED (or renamed): schema-property /properties/owner")
    work = scratch()
    schema_edit(work, lambda d: d["properties"]["kind"]["enum"].remove("module"))
    expect('a removed enum value (/properties/kind "module")', work, red=True, must_name='REMOVED (or renamed): schema-enum /properties/kind "module"')
    work = scratch()
    schema_edit(work, lambda d: d["properties"]["kind"]["enum"].append("planted"))
    expect('an added enum value without its snapshot line (/properties/kind "planted")', work, red=True,
           must_name='NOT IN THE SNAPSHOT: schema-enum /properties/kind "planted"')

    # And the snapshot side: a line typed twice.
    _, duplicates = read_snapshot("\n".join(sorted(snapshot) + [sorted(snapshot)[0]]))
    if duplicates:
        print("surface-freeze-gate selftest: red as it should be — a duplicate snapshot line")
    else:
        failures.append("NOT caught: a duplicate snapshot line")

    if failures:
        print("surface-freeze-gate selftest FAILED:")
        for failure in failures:
            print(f"  {failure}")
        sys.exit(1)
    print("surface-freeze-gate selftest: green unplanted and on ignored keywords; every planted fault red.")


mode = sys.argv[1] if len(sys.argv) > 1 else ""
if mode == "--selftest":
    selftest()
elif mode == "--print":
    print("\n".join(sorted(observe(pathlib.Path(".")))))
elif mode == "":
    observed = observe(pathlib.Path("."))
    snapshot, duplicates = read_snapshot(SNAPSHOT.read_text(encoding="utf-8"))
    problems = compare(observed, snapshot, duplicates)
    if problems:
        print("surface-freeze-gate FAILED:")
        for problem in problems:
            print(f"  {problem}")
        sys.exit(1)
    print(f"── surface freeze gate: {counts(observed)} — every snapshot line in the tree, every line in the snapshot")
else:
    sys.exit(f"usage: {sys.argv[0]} [--print | --selftest]")
PY
