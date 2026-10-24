#!/usr/bin/env bash
# GA gate G1's ratchet (docs/rfc/goldpath-messaging-engines.md §5, ADR-0013 §1, ADR-0015).
#
# A shipped public signature that names a bus engine's type is a promise 1.0 would freeze:
# changing it afterwards is a major version for every adopter. On 2026-10-20 thirteen such
# lines existed (ten in Goldpath.Messaging, three in Goldpath.Campaign); the engine-free API
# that replaces them lands on 0.1.0-preview.11. Until then the number may only go DOWN.
#
# The rule: no PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt outside an engine's own adapter
# package may contain an engine type (MassTransit.*, Wolverine.*), except a line held in the
# committed baseline (scripts/engine-gate.baseline.txt). The baseline may only shrink:
#   - a new engine-typed line anywhere outside an adapter package fails;
#   - a baseline line that no longer exists in its file fails (take it out of the baseline in
#     the same change — that is the ratchet clicking);
#   - a baseline holding more lines than the ceiling below fails, so the 13 cannot be traded
#     for 13 different ones by editing the baseline alone.
#
# A Shipped line that the sibling PublicAPI.Unshipped.txt marks `*REMOVED*` is gone from the
# surface (the analyzers' own convention; roll-shipped.sh deletes it at the train boundary), so
# it no longer counts — and dropping the marker brings it back, which fails like any new line.
#
# `engine-gate.sh --selftest` plants each of those faults in a scratch copy and proves the
# check goes red for each, and green on an unplanted copy.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "$@" <<'PY'
import os, re, shutil, sys, tempfile

# An engine adapter package may name its own engine; nothing else may. The names are the
# ones ADR-0015 §1 proposes and the engines RFC §2 confirms.
ADAPTER_DIRS = {
    "packages/Goldpath.Messaging.MassTransit",
    "packages/Goldpath.Messaging.Wolverine",
}
ENGINE_TYPE = re.compile(r"(?<![A-Za-z0-9_.])(MassTransit|Wolverine)\.")
BASELINE = "scripts/engine-gate.baseline.txt"
CEILING = 10            # 13 on 2026-10-20 (GA readiness §7.1 G1); 10 since Campaign's three left (#328). Lower it, never raise it.
SKIP_DIRS = {".git", "bin", "obj", "node_modules"}


def public_api_files(root):
    for base, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in names:
            if name in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
                yield os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/")


def in_adapter(path):
    return any(path == d or path.startswith(d + "/") for d in ADAPTER_DIRS)


def read_baseline(root):
    entries = []
    with open(os.path.join(root, BASELINE), encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if not line.strip() or line.startswith("#"):
                continue
            path, sep, signature = line.partition(" | ")
            if not sep:
                raise SystemExit(f"engine-gate: malformed baseline line (want 'path | signature'): {line}")
            entries.append((path, signature))
    return entries


def removed_from(root, path):
    """The Shipped lines the sibling Unshipped file marks *REMOVED*; empty for any other file."""
    if not path.endswith("PublicAPI.Shipped.txt"):
        return set()
    unshipped = os.path.join(root, path[: -len("Shipped.txt")] + "Unshipped.txt")
    if not os.path.exists(unshipped):
        return set()
    with open(unshipped, encoding="utf-8") as f:
        return {raw.rstrip("\n")[len("*REMOVED*"):] for raw in f if raw.startswith("*REMOVED*")}


def check(root):
    """Returns the list of findings; empty means green."""
    findings = []
    baseline = read_baseline(root)
    if len(baseline) > CEILING:
        findings.append(f"the baseline holds {len(baseline)} lines; its ceiling is {CEILING} and it may only shrink")

    allowed = {}
    for entry in baseline:
        allowed[entry] = allowed.get(entry, 0) + 1

    found = {}
    for path in sorted(public_api_files(root)):
        if in_adapter(path):
            continue
        removed = removed_from(root, path)
        with open(os.path.join(root, path), encoding="utf-8") as f:
            for number, raw in enumerate(f, 1):
                line = raw.rstrip("\n")
                if line.startswith("*REMOVED*") or not ENGINE_TYPE.search(line):
                    continue          # a removal is the ratchet working, not a new promise
                if line in removed:
                    continue          # shipped, and removed by this train: no longer on the surface
                key = (path, line)
                found[key] = found.get(key, 0) + 1
                if found[key] > allowed.get(key, 0):
                    findings.append(f"{path}:{number} names an engine type outside an adapter package: {line}")

    for (path, signature), count in sorted(allowed.items()):
        if found.get((path, signature), 0) < count:
            findings.append(
                f"baseline line no longer exists in {path} — remove it from {BASELINE}: {signature}")
    return findings


def report(findings):
    if findings:
        print("engine-gate: a public signature names a bus engine outside its adapter package (GA gate G1):")
        for finding in findings:
            print(f"  - {finding}")
        print("Express it in Goldpath vocabulary (docs/rfc/goldpath-messaging-engines.md §4), or move it into the")
        print("engine's adapter package. The baseline only ever shrinks.")
        return 1
    count = len(read_baseline("."))
    print(f"engine-gate: no new engine type in a public signature; {count} baseline line(s) left of {CEILING}.")
    return 0


def selftest():
    """Plants each fault in a scratch copy of the PublicAPI files and proves it goes red."""
    def scratch():
        tmp = tempfile.mkdtemp(prefix="engine-gate-")
        for path in public_api_files("."):
            os.makedirs(os.path.join(tmp, os.path.dirname(path)), exist_ok=True)
            shutil.copy(path, os.path.join(tmp, path))
        os.makedirs(os.path.join(tmp, "scripts"), exist_ok=True)
        shutil.copy(BASELINE, os.path.join(tmp, BASELINE))
        return tmp

    def append(root, path, line):
        with open(os.path.join(root, path), "a", encoding="utf-8") as f:
            f.write(line + "\n")

    def drop_first_baseline_line_from_api(root):
        path, signature = read_baseline(root)[0]
        full = os.path.join(root, path)
        lines = open(full, encoding="utf-8").read().split("\n")
        lines.remove(signature)
        open(full, "w", encoding="utf-8").write("\n".join(lines))

    messaging = "packages/Goldpath.Messaging/PublicAPI.Unshipped.txt"
    gone = "Goldpath.Gone.Old(MassTransit.ConsumeContext<Goldpath.X!>! context) -> void"

    def ship_and_remove(root):
        append(root, "packages/Goldpath.Messaging/PublicAPI.Shipped.txt", gone)
        append(root, messaging, "*REMOVED*" + gone)

    def drop_removal_marker(root):
        ship_and_remove(root)
        full = os.path.join(root, messaging)
        lines = open(full, encoding="utf-8").read().split("\n")
        lines.remove("*REMOVED*" + gone)
        open(full, "w", encoding="utf-8").write("\n".join(lines))

    planted = [
        ("a removed engine line brought back (its *REMOVED* marker dropped)", drop_removal_marker),
        ("a 14th MassTransit line in an Unshipped file",
         lambda r: append(r, messaging, "static Goldpath.X.Y(this MassTransit.IBusRegistrationConfigurator! c) -> void")),
        ("a Wolverine type outside its adapter",
         lambda r: append(r, messaging, "static Goldpath.X.Z(Wolverine.IMessageBus! bus) -> void")),
        ("a baseline line deleted from the API but kept in the baseline", drop_first_baseline_line_from_api),
        ("a baseline padded past its ceiling",
         lambda r: append(r, BASELINE, "packages/Goldpath.Messaging/PublicAPI.Shipped.txt | Goldpath.Padding(MassTransit.X! x) -> void")),
        ("a baseline signature re-declared in a second file",
         lambda r: append(r, messaging, read_baseline(r)[0][1])),
    ]

    failures = []
    clean = scratch()
    if check(clean):
        failures.append(f"the unplanted copy is red: {check(clean)}")

    adapter = scratch()
    os.makedirs(os.path.join(adapter, "packages/Goldpath.Messaging.MassTransit"), exist_ok=True)
    append(adapter, "packages/Goldpath.Messaging.MassTransit/PublicAPI.Unshipped.txt",
           "static Goldpath.MassTransitAdapter.Use(MassTransit.IBusRegistrationConfigurator! c) -> void")
    if check(adapter):
        failures.append(f"an engine type INSIDE its adapter package went red: {check(adapter)}")
    removal = scratch()
    ship_and_remove(removal)
    if check(removal):
        failures.append(f"a shipped engine line marked *REMOVED* went red: {check(removal)}")

    for name, plant in planted:
        root = scratch()
        plant(root)
        if not check(root):
            failures.append(f"planted fault NOT caught: {name}")
        else:
            print(f"engine-gate selftest: red as it should be — {name}")
        shutil.rmtree(root)

    for root in (clean, adapter, removal):
        shutil.rmtree(root)
    if failures:
        print("engine-gate selftest FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("engine-gate selftest: green on the real tree, inside an adapter package and on a removed line; every planted fault red.")
    return 0


if len(sys.argv) > 1 and sys.argv[1] == "--selftest":
    sys.exit(selftest())
sys.exit(report(check(".")))
PY
