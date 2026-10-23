#!/usr/bin/env bash
# An audit (2026-08-09) found the manifest schema accepting five values with no code
# behind them (db: oracle, broker: kafka|inmemory, exporters: prometheus, auditTrail.store:
# eventstream). A schema is a PROMISE an adopter reads before they build; accepting a value
# that cannot run is the most expensive kind of documentation error.
#
# The rule: every such value carries a $comment saying SCHEMA-ONLY / RESERVED and naming
# the ledger row. This gate fails when a roadmap value loses its annotation — or when a new
# one is added without one.
#
# The annotation is the clause "`value` is SCHEMA-ONLY" / "`a`, `b` and `c` are RESERVED", read
# with CLAUSE below. `goldpath check` (tools/Goldpath.Cli/RoadmapValues.cs) reads the same comment
# with the SAME pattern, pinned equal by RoadmapValuesTests, so a value this gate accepts as
# annotated is a value the CLI warns about. A marker anywhere else in the comment is not enough.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - <<'PY'
import json, re, sys
CLAUSE = r"((?:`[^`]+`(?:\s*,\s*(?:(?:and|or)\s+)?|\s+and\s+|\s+or\s+)?)+)\s+(?:is|are)\s+(?:SCHEMA-ONLY|RESERVED)\b"
def declared(comment):
    names = set()
    for clause in re.finditer(CLAUSE, comment):
        names |= set(re.findall(r"`([^`]+)`", clause.group(1)))
    return names
schema = json.load(open("schemas/manifest/v1/goldpath-manifest.schema.json"))
defs = schema["$defs"]
expected = {
    ("providersFull", "db", "oracle"),
    ("providersFull", "broker", "kafka"),
    ("providersFull", "broker", "inmemory"),
}
missing = []
for holder, prop, value in expected:
    node = defs[holder]["properties"][prop]
    if value not in node.get("enum", []):
        continue                      # the value was removed — fine, nothing to annotate
    if value not in declared(node.get("$comment", "")):
        missing.append(f"{holder}.{prop} accepts '{value}' with no $comment saying `{value}` is SCHEMA-ONLY")
obs = defs["observability"]["properties"]["exporters"]
if "prometheus" in json.dumps(obs) and "prometheus" not in declared(obs.get("$comment", "")):
    missing.append("observability.exporters accepts 'prometheus' with no $comment saying `prometheus` is SCHEMA-ONLY")
# auditTrail is a oneOf(boolean | object) — the store lives in the object branch.
audit = defs["features"]["properties"]["auditTrail"]
for branch in audit.get("oneOf", [audit]):
    store = branch.get("properties", {}).get("store")
    if not store or "eventstream" not in store.get("enum", []):
        continue
    if "eventstream" not in declared(store.get("$comment", "")):
        missing.append("features.auditTrail.store accepts 'eventstream' with no $comment saying `eventstream` is RESERVED")
if missing:
    print("schema-honesty: values a manifest may declare but no code can run:")
    for m in missing:
        print(f"  - {m}")
    print("Annotate them ($comment) or remove them from the enum.")
    sys.exit(1)
print("schema-honesty: every roadmap-only value is annotated as such.")
PY
