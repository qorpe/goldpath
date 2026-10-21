#!/usr/bin/env bash
# The agent layer is COPIED to three places in this repository, and the copies are supposed to
# be identical except where a shape genuinely differs. Until 2026-09-08 that was a claim in
# skills/README.md with nothing behind it — and api-portal's stop-gate.sh had already drifted
# from the template's without anyone noticing.
#
# This gate makes the claim checkable: files that must match, match; files that differ, differ
# for a REASON that is written here rather than remembered.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
python3 - "$ROOT" <<'PY'
import os, sys, hashlib
root = sys.argv[1]
SOLUTION = "templates/goldpath-solution/.claude"
COPIES = {
    "worker template": "templates/goldpath-worker/.claude",
    "CorPay": "samples/corpay/.claude",
    # goldpath's own maintainer layer shares the CYCLE with what it ships; its skills and
    # hooks are deliberately different shapes (D2 and D4), so only the shared files are
    # compared — see SHARED_ONLY below.
    "goldpath itself": ".claude",
}
# The root layer is the MAINTAINER shape: same nine steps, different skills and a different
# hook. Only the files that are supposed to be identical everywhere are compared for it.
SHARED_ONLY = {"goldpath itself": {"cycle.md"}}

# Deliberate divergences, each with the reason it is not a defect. A file listed here is NOT
# compared; a file NOT listed here must be byte-identical wherever it exists.
EXCEPTIONS = {
    ("worker template", "conventions.md"): "worker conventions: no HTTP surface, no vertical slice",
    ("worker template", "agents/breaker.md"): "the worker's attack surface is its triggers, not an HTTP contract",
    ("worker template", "skills/goldpath-feature/SKILL.md"): "a worker has no OpenAPI artefact; its contracts are its integration events",
    ("worker template", "skills/goldpath-manifest/SKILL.md"): "the worker manifest has a trigger and a narrower feature set",
    ("CorPay", "conventions.md"): "the sample's own domain conventions ride on top of the template's",
}

# The domain-memory skeleton (goldpath-authoring RFC, Slice 0) is copied the same way: the
# template generates it, CorPay carries the same files. The worker template does not — a worker
# has no business-facing specs of its own — and goldpath's own root is not an application.
DOMAIN_SOURCE = "templates/goldpath-solution/docs/domain"
DOMAIN_COPIES = {"CorPay": "samples/corpay/docs/domain"}
def digest(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()

source_root = os.path.join(root, SOLUTION)
if not os.path.isdir(source_root):
    print(f"── skills-parity: {SOLUTION} does not exist — nothing to compare"); sys.exit(1)

sources = {}
for base, dirs, names in os.walk(source_root):
    dirs[:] = [d for d in dirs if d not in {"bin", "obj"}]
    for n in names:
        rel = os.path.relpath(os.path.join(base, n), source_root)
        sources[rel] = digest(os.path.join(base, n))

fail, compared, excused = [], 0, 0
for label, copy in COPIES.items():
    copy_root = os.path.join(root, copy)
    if not os.path.isdir(copy_root):
        fail.append(f"{label}: {copy} is missing entirely")
        continue
    for rel, want in sources.items():
        if label in SHARED_ONLY and rel not in SHARED_ONLY[label]:
            continue
        if (label, rel) in EXCEPTIONS:
            excused += 1
            target = os.path.join(copy_root, rel)
            if not os.path.exists(target):
                fail.append(f"{label}: {rel} is excused from matching but does not exist at all")
            continue
        target = os.path.join(copy_root, rel)
        if not os.path.exists(target):
            fail.append(f"{label}: {rel} exists in the template and is missing here")
            continue
        compared += 1
        if digest(target) != want:
            fail.append(f"{label}: {rel} has drifted from the template")

# An exception that no longer applies is stale bookkeeping — it would hide a real drift.
for (label, rel) in EXCEPTIONS:
    if rel not in sources:
        fail.append(f"exception ({label}, {rel}) names a file the template no longer has")

domain_root = os.path.join(root, DOMAIN_SOURCE)
if not os.path.isdir(domain_root):
    fail.append(f"{DOMAIN_SOURCE} does not exist — the template generates no domain-memory skeleton")
else:
    for label, copy in DOMAIN_COPIES.items():
        for base, dirs, names in os.walk(domain_root):
            for n in names:
                rel = os.path.relpath(os.path.join(base, n), domain_root)
                target = os.path.join(root, copy, rel)
                if not os.path.exists(target):
                    fail.append(f"{label}: docs/domain/{rel} exists in the template and is missing here")
                    continue
                compared += 1
                if digest(target) != digest(os.path.join(base, n)):
                    fail.append(f"{label}: docs/domain/{rel} has drifted from the template")
if fail:
    print("── skills-parity: the agent layer has drifted:")
    for f in fail:
        print(f"  {f}")
    sys.exit(1)
print(f"── skills-parity: {compared} files identical across {len(COPIES)} copies, {excused} deliberate divergences accounted for")
PY
