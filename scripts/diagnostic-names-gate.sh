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
# Every table must be named by a ToTable, because a table EF names by convention follows the class name and would
# be a Goldpath-owned table outside the inventory. EF adds an entity, and so a table, in three ways, and the gate
# reads all three from source:
#   - an explicit modelBuilder.Entity<T>(…): it must carry its ToTable (in its configuration, or chained);
#   - a relationship to a type, HasOne<T>/HasMany<T>/OwnsMany<T>: T must be an entity the package maps with
#     ToTable (OwnsMany<T> may carry its own; OwnsOne<T> shares the owner's table and adds none);
#   - a navigation: every public property of a mapped entity class (and of a base class declared under packages/)
#     must be a scalar EF maps to a column, an enum, a collection of either, or a mapped entity — or be converted
#     (Property(x => x.P)…HasConversion) or ignored (Ignore(x => x.P), [NotMapped]) in that entity's configuration.
#     A type the gate does not know is refused, not guessed: map it, convert it, ignore it, or teach the gate.
# The other ways to declare a table it knows (IEntityTypeConfiguration, [Table], DbSet, Entity(typeof(…)),
# Entity("…"), SharedTypeEntity, AddEntityType, ApplyConfigurationsFromAssembly, a HasOne/HasMany/Owns* naming
# its type by string or typeof) are refused. Reading source is not reading EF's model, so the gate also checks
# against EF itself: every table in the Oracle composed model's golden
# (tests/Goldpath.IntegrationTests/Oracle/composed-model.golden.txt, which OracleDataTests compares with the model
# EF builds, failing in the oracle job — not a required check — when they differ) must be in the inventory,
# unless it is named below as not Goldpath-owned. The Quartz tables are not in that golden; the three source rules above hold them.
#
# `diagnostic-names-gate.sh --selftest` plants a renamed meter, a renamed span, a renamed table, an
# un-inventoried instrument, an unreadable name, an un-inventoried factory meter (MeterOptions), an entity
# mapped without ToTable, a table declared by attribute, a navigation to an unmapped class, a HasOne<T> to an
# unmapped class, a golden table missing from the inventory, a removed external subscription and a name kept
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
OTHER_MAPPING = re.compile(r'IEntityTypeConfiguration\s*<|\[\s*Table\s*\(|\bDbSet\s*<|\.Entity\s*\(\s*(?:typeof\s*\(|")|\.SharedTypeEntity\s*<'
                           r'|\.AddEntityType\s*\(|\.ApplyConfigurationsFromAssembly\s*\(|\.(?:HasOne|HasMany|OwnsOne|OwnsMany)\s*\(\s*(?:typeof\s*\(|")')
# A relationship to a TYPE adds that type as an entity: it must be one the package maps (with ToTable).
RELATED = re.compile(r'\.(HasOne|HasMany|OwnsOne|OwnsMany)\s*<\s*([\w.]+)\s*>\s*\(')
# A navigation adds its type as an entity: every public property of a mapped class must be something EF maps
# to a column, or a mapped entity, or be converted or ignored.
CLASS = re.compile(r'\b(?:class|record|struct)\s+(\w+)(?:\s*<[^>{]*>)?(?:\s*\(([^)]*)\))?\s*(?::\s*([^{]+?))?\s*(?:where\b[^{]*)?\{')
ENUM = re.compile(r'\benum\s+(\w+)')
PROPERTY = re.compile(r'((?:\[[^\]]*\]\s*)*)\bpublic\s+((?:(?:required|virtual|override|new|sealed|static|abstract)\s+)*)'
                      r'([\w.]+(?:\s*<[^{};=]*?>)?(?:\s*\[\s*\])?\??)\s+(\w+)\s*\{\s*(?:get|set|init|private|protected|internal)\b')
CONVERTED = re.compile(r'\.Property\s*(?:<[^>()]*>)?\s*\(\s*\w+\s*=>\s*\w+\.(\w+)\s*\)[^;]*?\.HasConversion\b')
IGNORED = re.compile(r'\.Ignore\s*\(\s*\w+\s*=>\s*\w+\.(\w+)\s*\)')
SCALARS = {"string", "bool", "char", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double",
           "decimal", "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "byte[]"}
COLLECTIONS = {"List", "IList", "ICollection", "IEnumerable", "HashSet", "ISet", "IReadOnlyList", "IReadOnlyCollection", "Collection"}
UNFROZEN = "a table whose name nothing freezes (EF would name it by convention): "
# Tables in the composed-model golden that Goldpath does not own, each with its reason.
COMPOSED_GOLDEN = "tests/Goldpath.IntegrationTests/Oracle/composed-model.golden.txt"
NOT_GOLDPATH_TABLES = {
    "OracleDataRows": "the golden's own test entity (OracleComposedDb), not a package's",
    "MediantAuditEntries": "Mediant's audit table, composed by AuditTrail; Mediant owns its name",
}


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


def block(code, start):
    """The text of a brace block, from the '{' at code[start - 1] to its matching '}'."""
    depth, i, n = 1, start, len(code)
    while i < n and depth:
        c = code[i]
        if c in "\"'":
            i += 1
            while i < n and code[i] != c:
                i += 2 if code[i] == "\\" else 1
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        i += 1
    return code[start:i - 1]


def element(type_name):
    """A property's type without its nullable mark, and a collection's element type; None for another generic."""
    t = re.sub(r"\s+", "", type_name).rstrip("?")
    m = re.fullmatch(r"(?:System\.Collections\.Generic\.)?(\w+)<(.+)>", t)
    if m:
        return m.group(2).rstrip("?") if m.group(1) in COLLECTIONS and "," not in m.group(2) else None
    if t.endswith("[]") and t != "byte[]":
        return t[:-2].rstrip("?")
    return t


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
    configs = {}    # (package, entity type) -> the text of its configurations
    related = []    # (path, package, method, type, carries its own ToTable)
    classes = {}    # class name -> [(path, base list, body)]; a positional record's parameters are properties too
    enums = set()
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
            configs[(package, m.group(1))] = configs.get((package, m.group(1)), "") + args
        for m in RELATED.finditer(code):
            args, _ = call_span(code, m.end())
            related.append((path, package, m.group(1), m.group(2), ".ToTable(" in re.sub(r"\s+", "", args)))
        enums.update(ENUM.findall(code))
        for m in CLASS.finditer(code):
            positional = "".join(f"public {p.strip().split('=')[0].strip()} {{ get; }}\n" for p in (m.group(2) or "").split(",") if p.strip())
            classes.setdefault(m.group(1), []).append((path, m.group(3) or "", positional + block(code, m.end())))
        for m in OTHER_MAPPING.finditer(code):
            unreadable.append(UNFROZEN + f"{path}: entity mapping '{m.group(0).strip()}' — map a Goldpath table with modelBuilder.Entity<T>(e => e.ToTable(\"…\"))")
        if package == "Goldpath.ServiceDefaults":
            for name in EXTERNAL.findall(code):
                if not name.startswith("Goldpath."):
                    found.add(("external", name))
    for (package, entity), (path, mapped) in sorted(entities.items()):
        if not mapped:
            unreadable.append(UNFROZEN + f"{path}: entity {entity} has no ToTable — map it with ToTable so its name is frozen (a convention name follows the class)")
    mapped_by_package = {}
    for (package, entity), (_, mapped) in entities.items():
        if mapped:
            mapped_by_package.setdefault(package, set()).add(entity.split(".")[-1])
    for path, package, method, type_name, own_table in related:
        short = type_name.split(".")[-1]
        if short in mapped_by_package.get(package, set()) or method == "OwnsOne" or (method == "OwnsMany" and own_table):
            continue
        unreadable.append(UNFROZEN + f"{path}: {method}<{type_name}> adds {type_name} as an entity named by convention — map it with "
                          "modelBuilder.Entity<T>(e => e.ToTable(\"…\")) in this package")
    for (package, entity), (path, mapped) in sorted(entities.items()):
        short = entity.split(".")[-1]
        known = mapped_by_package.get(package, set())
        config = configs.get((package, entity), "")
        exempt = set(CONVERTED.findall(config)) | set(IGNORED.findall(config))
        pending, seen, declared = [short], set(), False
        while pending:
            name = pending.pop()
            if name in seen:
                continue
            seen.add(name)
            for _, bases, body in classes.get(name, []):
                declared = True
                pending += [b.strip().split("<")[0].split(".")[-1] for b in bases.split(",") if b.strip().split("<")[0].split(".")[-1] in classes]
                for p in PROPERTY.finditer(body):
                    attributes, modifiers, type_name, prop = p.groups()
                    if "static" in modifiers or prop in exempt or re.search(r"\bNotMapped\b", attributes):
                        continue
                    target = element(type_name)
                    if target is not None and (target in SCALARS or target.split(".")[-1] in enums or target.split(".")[-1] in known):
                        continue
                    unreadable.append(UNFROZEN + f"{path}: entity {entity} property {prop} of type {type_name.strip()} — EF would map it as an entity "
                                      "named by convention; map that type with ToTable, convert the property (HasConversion) or ignore it")
        if not declared:
            unreadable.append(UNFROZEN + f"{path}: entity {entity} — its class is not declared under packages/, so its navigations cannot be read")
    golden_tables = None   # None: the golden is missing, and the check against EF's own model cannot run
    golden = os.path.join(root, COMPOSED_GOLDEN)
    if os.path.exists(golden):
        with open(golden, encoding="utf-8") as f:
            golden_tables = sorted({line.split(".", 1)[0] for line in f if "." in line} - set(NOT_GOLDPATH_TABLES))
    for path, package, name, expr in spans:
        owned = sources_by_package.get(package, set())
        if not name:
            unreadable.append(f"{path}: span {expr}")
        elif len(owned) != 1:
            unreadable.append(f"{path}: span {name} — its package must create exactly one activity source to attribute it")
        else:
            found.add(("span", next(iter(owned)), name))
    return found, unreadable, golden_tables


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
    found, problems, golden_tables = scan(root)
    problems = [u if u.startswith(UNFROZEN) else f"unreadable name (make it a literal or a const string in the same file): {u}" for u in problems]
    inventory, inventory_problems = read_inventory(root)
    problems += inventory_problems
    for entry in sorted(inventory - found):
        problems.append(f"in the inventory, gone from the code (a rename or removal — breaking, Versioning D2): {' '.join(entry)}")
    for entry in sorted(found - inventory):
        problems.append(f"in the code, missing from the inventory (add the line in the same PR): {' '.join(entry)}")
    inventoried_tables = {e[2] for e in inventory if e[0] == "table"}
    if golden_tables is None:
        problems.append(f"{COMPOSED_GOLDEN} is missing: the check against EF's own model cannot run")
    for table in golden_tables or []:
        if table not in inventoried_tables:
            problems.append(f"in EF's composed model ({COMPOSED_GOLDEN}), missing from the inventory: table {table} — "
                            "map it with ToTable and add its line, or name it in NOT_GOLDPATH_TABLES with the reason")
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
        os.makedirs(os.path.dirname(os.path.join(tmp, COMPOSED_GOLDEN)))
        shutil.copy(os.path.join(root, COMPOSED_GOLDEN), os.path.join(tmp, COMPOSED_GOLDEN))
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
        "a navigation to a class no ToTable maps": ("packages/Goldpath.Notification/NotificationModel.cs", 'public string Recipient { get; set; } = "";',
                                                     'public string Recipient { get; set; } = ""; public List<ZzNav> ZzNavs { get; set; } = new();',
                                                     "entity GoldpathNotification property ZzNavs of type List<ZzNav>"),
        "a HasOne<T> to a class no ToTable maps": ("packages/Goldpath.Jobs/QuartzStoreModel.cs", "e.HasOne<QrtzJobDetail>().WithMany()",
                                                   "e.HasOne<ZzPrincipal>().WithMany()",
                                                   "HasOne<ZzPrincipal> adds ZzPrincipal as an entity named by convention"),
        "a table in EF's composed model missing from the inventory": (COMPOSED_GOLDEN, "GoldpathNotifications.Body:",
                                                                      "ZzConvention.Id: RAW(16) not null\nGoldpathNotifications.Body:",
                                                                      "missing from the inventory: table ZzConvention"),
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
