#!/usr/bin/env bash
# GA gate G4, part API-8 (#397; docs/rfc/goldpath-1.0-api-review.md §2): the diagnostic contract and the
# Goldpath-owned table names, frozen by one inventory.
#
# Versioning D2 counts a renamed meter, instrument or span, and a renamed Goldpath-owned table, as a
# breaking change. Until this gate there was no list to break against. The inventory
# (scripts/diagnostic-names-gate.inventory.txt) holds one name per line, by kind:
#
#   meter       <meter>                    a System.Diagnostics.Metrics.Meter a package creates
#   source      <activity source>          a System.Diagnostics.ActivitySource a package creates
#   instrument  <meter> <instrument>       an instrument, with the meter that creates it
#   span        <source> <span name>       a span name, with its source (an interpolated name is kept as written)
#   table       <package> <table>          a table a package's model maps (ToTable — required on every entity)
#   external    <meter or source>          a name ServiceDefaults subscribes to that Goldpath does NOT own
#
# The gate scans packages/**/*.cs and fails on:
#   - an inventory name that no longer exists in code (a rename or a removal: a breaking change, so the
#     inventory line changes in the same PR, deliberately);
#   - a name in code that the inventory does not hold (the list stays complete, the way RS0016 keeps
#     PublicAPI complete);
#   - a name it cannot read: a meter, source, instrument, span or table created from an expression
#     that is neither a string literal nor a const string in the same file. Make it one.
#
# Every entity a package maps with modelBuilder.Entity<T>(…) must carry a ToTable (in its configuration, or
# chained): a table EF names by convention follows the class name, and would be a Goldpath-owned table outside
# the inventory. Any other way to declare a table (IEntityTypeConfiguration, [Table], DbSet, Entity(typeof(…)))
# is refused, so the rule cannot be walked around.
#
# `diagnostic-names-gate.sh --selftest` plants a renamed meter, a renamed span, a renamed table, an
# un-inventoried instrument, an unreadable name, an un-inventoried factory meter (MeterOptions), an entity
# mapped without ToTable, a table declared by attribute, a removed external subscription and a name kept
# alive only by a comment in a scratch copy, each red, and the unplanted copy green.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "$@" <<'PY'
import os, re, shutil, sys, tempfile

INVENTORY = "scripts/diagnostic-names-gate.inventory.txt"
SKIP_DIRS = {".git", "bin", "obj", "node_modules"}
KINDS = {"meter": 1, "source": 1, "instrument": 2, "span": 2, "table": 2, "external": 1}

STRING = r'(?:\$?"(?:[^"\\]|\\.)*")'
CONST = re.compile(r'\bconst\s+string\s+(\w+)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;')
METER = re.compile(r'(?:new\s+Meter(?:Options)?\s*\(|\bMeter(?:Options)?\s+\w+\s*=\s*new\s*\()\s*([^,)]*)')   # MeterOptions: IMeterFactory.Create
SOURCE = re.compile(r'(?:new\s+ActivitySource\s*\(|\bActivitySource\s+\w+\s*=\s*new\s*\()\s*([^,)]*)')
INSTRUMENT = re.compile(r'\.Create(?:Counter|Histogram|UpDownCounter|ObservableCounter|ObservableGauge|ObservableUpDownCounter|Gauge)\s*(?:<[^>()]*>)?\s*\(\s*([^,)]*)')
SPAN = re.compile(r'(\w+)\s*\.\s*StartActivity\s*\(\s*([^,)]*)')
TABLE = re.compile(r'\.ToTable\s*\(\s*([^,)]*)')
EXTERNAL = re.compile(r'\.Add(?:Meter|Source)\s*\(\s*"([^"]+)"\s*\)')
# A table EF names by convention (no ToTable) is a Goldpath-owned table the TABLE pattern cannot see, so every
# entity a package maps must carry its ToTable, and every other way to declare one is refused (fail closed).
ENTITY = re.compile(r'\.Entity\s*<\s*([\w.]+)\s*>\s*\(')
CHAINED_TABLE = re.compile(r'\s*\.ToTable\s*\(')
OTHER_MAPPING = re.compile(r'IEntityTypeConfiguration\s*<|\[\s*Table\s*\(|\bDbSet\s*<|\.Entity\s*\(\s*typeof\s*\(|\.SharedTypeEntity\s*<')


def cs_files(root):
    base = os.path.join(root, "packages")
    for top, dirs, names in os.walk(base):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
        for name in sorted(names):
            if name.endswith(".cs"):
                path = os.path.join(top, name)
                yield os.path.relpath(path, root).replace(os.sep, "/")


def strip_comments(text):
    # Comments may quote names (a doc comment naming a meter); only code counts. Strings are kept intact.
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif c == "'":
            # A char literal ('"' or '\\'') is kept whole, so a quote inside it never opens a string.
            j = i + 1
            if j < n and text[j] == "\\":
                j += 2
            while j < n and text[j] != "'":
                j += 1
            out.append(text[i:j + 1])
            i = j + 1
        elif c == '"' or (c == "$" and i + 1 < n and text[i + 1] == '"') or (c == "@" and i + 1 < n and text[i + 1] == '"'):
            j = i + (2 if c in "$@" else 1)
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" and c != "@" else 1
            out.append(text[i:j + 1])
            i = j + 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def call_span(code, start):
    """The text of a call's argument list, from the '(' at code[start - 1] to its matching ')', and the index after it."""
    depth, i, n = 1, start, len(code)
    while i < n and depth:
        c = code[i]
        if c == '"':
            i += 1
            while i < n and code[i] != '"':
                i += 2 if code[i] == "\\" else 1
        elif c == "'":
            i += 1
            while i < n and code[i] != "'":
                i += 2 if code[i] == "\\" else 1
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        i += 1
    return code[start:i - 1], i


def resolve(expr, consts):
    """The name an argument expression denotes: a literal, a const, or const + literal. None = unreadable."""
    parts = [p.strip() for p in expr.strip().split("+")]
    value = ""
    for part in parts:
        if re.fullmatch(STRING, part):
            literal = part[2:-1] if part.startswith("$") else part[1:-1]
            value += literal
        elif part in consts:
            value += consts[part]
        else:
            return None
    return value or None


def package_of(path):
    return path.split("/")[1]


def scan(root):
    found, unreadable, sources_by_package = set(), [], {}
    spans = []
    entities = {}   # (package, entity type) -> [path, has a ToTable]
    for path in cs_files(root):
        with open(os.path.join(root, path), encoding="utf-8") as f:
            code = strip_comments(f.read())
        consts = dict(CONST.findall(code))
        package = package_of(path)
        meters = []
        for m in METER.finditer(code):
            name = resolve(m.group(1), consts)
            (meters.append(name), found.add(("meter", name))) if name else unreadable.append(f"{path}: meter {m.group(1).strip()}")
        for m in SOURCE.finditer(code):
            name = resolve(m.group(1), consts)
            if name:
                found.add(("source", name))
                sources_by_package.setdefault(package, set()).add(name)
            else:
                unreadable.append(f"{path}: activity source {m.group(1).strip()}")
        for m in INSTRUMENT.finditer(code):
            name = resolve(m.group(1), consts)
            if not name:
                unreadable.append(f"{path}: instrument {m.group(1).strip()}")
            elif len(set(meters)) != 1:
                unreadable.append(f"{path}: instrument {name} — its file must create exactly one meter to attribute it")
            else:
                found.add(("instrument", meters[0], name))
        for m in SPAN.finditer(code):
            name = resolve(m.group(2), consts)
            spans.append((path, package, name, m.group(2).strip()))
        for m in TABLE.finditer(code):
            name = resolve(m.group(1), consts)
            (found.add(("table", package, name))) if name else unreadable.append(f"{path}: table {m.group(1).strip()}")
        for m in ENTITY.finditer(code):
            args, after = call_span(code, m.end())
            mapped = ".ToTable(" in re.sub(r"\s+", "", args) or CHAINED_TABLE.match(code, after) is not None
            entry = entities.setdefault((package, m.group(1)), [path, False])
            entry[1] = entry[1] or mapped
        for m in OTHER_MAPPING.finditer(code):
            unreadable.append(f"{path}: entity mapping '{m.group(0).strip()}' — map a Goldpath table with modelBuilder.Entity<T>(e => e.ToTable(\"…\"))")
        if package == "Goldpath.ServiceDefaults":
            for name in EXTERNAL.findall(code):
                if not name.startswith("Goldpath."):
                    found.add(("external", name))
    for (package, entity), (path, mapped) in sorted(entities.items()):
        if not mapped:
            unreadable.append(f"{path}: entity {entity} has no ToTable — map it with ToTable so its name is frozen (a convention name follows the class)")
    for path, package, name, expr in spans:
        owned = sources_by_package.get(package, set())
        if not name:
            unreadable.append(f"{path}: span {expr}")
        elif len(owned) != 1:
            unreadable.append(f"{path}: span {name} — its package must create exactly one activity source to attribute it")
        else:
            found.add(("span", next(iter(owned)), name))
    return found, unreadable


def read_inventory(root):
    entries, problems = set(), []
    with open(os.path.join(root, INVENTORY), encoding="utf-8") as f:
        for number, raw in enumerate(f, 1):
            line = raw.rstrip("\n")
            if not line.strip() or line.startswith("#"):
                continue
            body = line.split("  #", 1)[0].rstrip()   # a trailing note after two spaces and '#'
            kind, _, rest = body.partition(" ")
            if kind not in KINDS:
                problems.append(f"{INVENTORY}:{number}: unknown kind '{kind}'")
                continue
            # The last field of a span may contain spaces (an interpolated name such as "{endpoint} consume").
            fields = rest.split(" ", KINDS[kind] - 1) if KINDS[kind] > 1 else [rest]
            if len(fields) != KINDS[kind] or not all(fields):
                problems.append(f"{INVENTORY}:{number}: '{kind}' takes {KINDS[kind]} field(s): {line}")
                continue
            entry = (kind, *fields)
            if entry in entries:
                problems.append(f"{INVENTORY}:{number}: duplicate line: {line}")
            entries.add(entry)
    return entries, problems


def check(root):
    found, problems = scan(root)
    problems = [f"unreadable name (make it a literal or a const string in the same file): {u}" for u in problems]
    inventory, inventory_problems = read_inventory(root)
    problems += inventory_problems
    for entry in sorted(inventory - found):
        problems.append(f"in the inventory, gone from the code (a rename or removal — breaking, Versioning D2): {' '.join(entry)}")
    for entry in sorted(found - inventory):
        problems.append(f"in the code, missing from the inventory (add the line in the same PR): {' '.join(entry)}")
    return problems, inventory


def report(root):
    problems, inventory = check(root)
    if problems:
        print("── diagnostic names gate: FAILED")
        for problem in problems:
            print(f"  ✗ {problem}")
        return 1
    counts = {kind: sum(1 for e in inventory if e[0] == kind) for kind in KINDS}
    print("── diagnostic names gate: " + ", ".join(f"{counts[k]} {k}{'s' if counts[k] != 1 else ''}" for k in KINDS)
          + " — every name in the inventory, every inventory line in the code")
    return 0


def selftest(root):
    def scratch():
        tmp = tempfile.mkdtemp(prefix="diag-gate-")
        for path in cs_files(root):
            os.makedirs(os.path.dirname(os.path.join(tmp, path)), exist_ok=True)
            shutil.copy(os.path.join(root, path), os.path.join(tmp, path))
        os.makedirs(os.path.join(tmp, "scripts"))
        shutil.copy(os.path.join(root, INVENTORY), os.path.join(tmp, INVENTORY))
        return tmp

    def plant(tmp, path, old, new):
        full = os.path.join(tmp, path)
        with open(full, encoding="utf-8") as f:
            text = f.read()
        if text.count(old) != 1:
            raise SystemExit(f"selftest: '{old}' is not exactly once in {path}; update the selftest")
        with open(full, "w", encoding="utf-8") as f:
            f.write(text.replace(old, new))

    faults = {
        "a renamed meter": ("packages/Goldpath.Locking/GoldpathLockingExtensions.cs", 'new("Goldpath.Locking")', 'new("Goldpath.Locks")',
                            "gone from the code (a rename or removal — breaking, Versioning D2): meter Goldpath.Locking"),
        "a renamed span": ("packages/Goldpath.Jobs/GoldpathJobRunner.cs", 'StartActivity("goldpath.job.chunk")', 'StartActivity("goldpath.job.chunks")',
                           "gone from the code (a rename or removal — breaking, Versioning D2): span Goldpath.Jobs goldpath.job.chunk"),
        "a renamed table": ("packages/Goldpath.Notification/NotificationModel.cs", 'ToTable("GoldpathNotifications")', 'ToTable("GoldpathNotification")',
                            "gone from the code (a rename or removal — breaking, Versioning D2): table Goldpath.Notification GoldpathNotifications"),
        "an un-inventoried instrument": ("packages/Goldpath.Locking/GoldpathLockingExtensions.cs", 's_meter.CreateCounter<long>("goldpath_lock_acquire_total");',
                                         's_meter.CreateCounter<long>("goldpath_lock_acquire_total"); static readonly object s_planted = s_meter.CreateCounter<long>("goldpath_lock_planted_total");',
                                         "missing from the inventory (add the line in the same PR): instrument Goldpath.Locking goldpath_lock_planted_total"),
        "an unreadable instrument name": ("packages/Goldpath.Auth/GoldpathAuthCore.cs", 's_meter.CreateCounter<long>("goldpath_auth_failures_total");',
                                          's_meter.CreateCounter<long>(PlantedName);',
                                          "unreadable name (make it a literal or a const string in the same file): packages/Goldpath.Auth/GoldpathAuthCore.cs: instrument PlantedName"),
        "a factory-made meter missing from the inventory": ("packages/Goldpath.Locking/GoldpathLockingExtensions.cs", 'new("Goldpath.Locking")',
                                                            'new("Goldpath.Locking"); static readonly object s_factory = new MeterOptions("Goldpath.Planted")',
                                                            "missing from the inventory (add the line in the same PR): meter Goldpath.Planted"),
        "an entity mapped by convention, without ToTable": ("packages/Goldpath.Notification/NotificationModel.cs", 'modelBuilder.Entity<GoldpathNotificationAttachment>(attachment =>',
                                                            'modelBuilder.Entity<ZzPlanted>(e => { e.HasKey(x => x.Id); }); modelBuilder.Entity<GoldpathNotificationAttachment>(attachment =>',
                                                            "entity ZzPlanted has no ToTable"),
        "a table declared by attribute": ("packages/Goldpath.Notification/NotificationModel.cs", 'public class GoldpathNotification\n',
                                          '[Table("Planted")] public class GoldpathNotification\n',
                                          "entity mapping '[Table('"),
        "a dropped external subscription": ("packages/Goldpath.ServiceDefaults/GoldpathServiceDefaultsExtensions.cs", '.AddMeter("Npgsql")', '',
                                            "gone from the code (a rename or removal — breaking, Versioning D2): external Npgsql"),
    }
    failures = []
    tmp = scratch()
    try:
        clean, _ = check(tmp)
        if clean:
            failures.append("the unplanted copy is red:\n    " + "\n    ".join(clean))
    finally:
        shutil.rmtree(tmp)
    for label, (path, old, new, expected) in faults.items():
        tmp = scratch()
        try:
            plant(tmp, path, old, new)
            problems, _ = check(tmp)
            if not any(expected in p for p in problems):
                failures.append(f"{label}: not caught (expected '{expected}'; got {problems or 'green'})")
        finally:
            shutil.rmtree(tmp)
    # A name quoted only in a comment is not code: removing the real one is still caught.
    tmp = scratch()
    try:
        plant(tmp, "packages/Goldpath.Jobs/GoldpathJobRunner.cs", 'StartActivity("goldpath.job.chunk")', '/* StartActivity("goldpath.job.chunk") */ StartActivity("goldpath.job.chunks")')
        problems, _ = check(tmp)
        if not any("span Goldpath.Jobs goldpath.job.chunk" in p and "gone from the code" in p for p in problems):
            failures.append("a name kept alive by a comment was not caught")
    finally:
        shutil.rmtree(tmp)
    if failures:
        print("── diagnostic names gate selftest: FAILED")
        for failure in failures:
            print(f"  ✗ {failure}")
        return 1
    print(f"── diagnostic names gate selftest: {len(faults) + 1} planted faults each red, the unplanted copy green")
    return 0


root = os.getcwd()
args = sys.argv[1:]
if args == ["--selftest"]:
    sys.exit(selftest(root))
if args:
    print("usage: diagnostic-names-gate.sh [--selftest]")
    sys.exit(2)
sys.exit(report(root))
PY
