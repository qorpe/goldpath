#!/usr/bin/env bash
# Alert rules are code that runs at 3 a.m.: a rule that does not parse, never fires, or pages the
# wrong person is found by the incident it missed. Every package that ships a rule file
# (packages/<Package>/ops/alerts.yaml, #295) is held here, offline:
#
#   1. `promtool check rules` — the file parses and every expression is valid PromQL;
#   2. `promtool test rules` on the package's alerts.test.yaml — a rule file without its cases is
#      refused, because a rule nobody has seen fire is a guess;
#   3. every rule carries `severity` from the family's scheme (critical | major | minor —
#      packages/Goldpath.ServiceDefaults/ops/dashboard-and-alerts.md) and a `runbook_url` whose
#      file exists in this repository and whose anchor is one of that file's headings.
#
# promtool comes from a PINNED Prometheus release whose archive is checked against the SHA-256
# below before it runs. Air-gapped (a first-class scenario): put the verified archive at
# $PROMETHEUS_ARCHIVE, or point $PROMTOOL at a binary already provisioned — its version must be
# exactly PROMETHEUS_VERSION. Nothing here reaches the network once either is set.
# The download path fetches the linux-amd64 archive (the CI runner's); on any other platform set
# $PROMTOOL.
set -euo pipefail
cd "$(dirname "$0")/.."

PROMETHEUS_VERSION="3.5.0"
PROMETHEUS_SHA256="e811827af26d822afb09a4f28314f61b618b12cff5369835a67f674d8b46f39a"   # prometheus-3.5.0.linux-amd64.tar.gz
ARCHIVE_NAME="prometheus-${PROMETHEUS_VERSION}.linux-amd64.tar.gz"

if [[ -z "${PROMTOOL:-}" ]]; then
  cache="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/goldpath-promtool-${PROMETHEUS_VERSION}"
  PROMTOOL="$cache/prometheus-${PROMETHEUS_VERSION}.linux-amd64/promtool"
  if [[ ! -x "$PROMTOOL" ]]; then
    mkdir -p "$cache"
    archive="${PROMETHEUS_ARCHIVE:-$cache/$ARCHIVE_NAME}"
    if [[ ! -f "$archive" ]]; then
      curl -fsSL --retry 3 -o "$archive" \
        "https://github.com/prometheus/prometheus/releases/download/v${PROMETHEUS_VERSION}/${ARCHIVE_NAME}"
    fi
    echo "${PROMETHEUS_SHA256}  ${archive}" | sha256sum -c --quiet - \
      || { echo "alert-rules: $archive does not match the pinned SHA-256 — refusing to run it" >&2; exit 1; }
    tar -xzf "$archive" -C "$cache" "prometheus-${PROMETHEUS_VERSION}.linux-amd64/promtool"
  fi
fi

actual="$("$PROMTOOL" --version 2>&1 | head -1)"
[[ "$actual" == "promtool, version ${PROMETHEUS_VERSION} "* ]] \
  || { echo "alert-rules: expected promtool ${PROMETHEUS_VERSION}, got: $actual" >&2; exit 1; }

shopt -s nullglob
files=(packages/*/ops/alerts.yaml)
if (( ${#files[@]} == 0 )); then
  echo "alert-rules: no packages/*/ops/alerts.yaml found — the gate has nothing to hold" >&2
  exit 1
fi

for rules in "${files[@]}"; do
  dir="$(dirname "$rules")"
  [[ -f "$dir/alerts.test.yaml" ]] \
    || { echo "alert-rules: $rules has no $dir/alerts.test.yaml — every rule file ships with its cases" >&2; exit 1; }
  "$PROMTOOL" check rules "$rules"
  (cd "$dir" && "$PROMTOOL" test rules alerts.test.yaml)
done

python3 - "${files[@]}" <<'PY'
import os, re, sys, unicodedata
SEVERITIES = {"critical", "major", "minor"}
PREFIX = "https://github.com/qorpe/goldpath/blob/main/"

def anchors(path):
    """GitHub's heading anchors: lower-case, punctuation dropped, spaces to hyphens, repeats numbered."""
    seen, out = {}, set()
    for line in open(path, encoding="utf-8"):
        m = re.match(r"^#{1,6}\s+(.*?)\s*#*\s*$", line)
        if not m:
            continue
        text = re.sub(r"[`*_]|\[([^\]]*)\]\([^)]*\)", r"\1", m.group(1)).strip().lower()
        slug = "".join(c for c in text if c in " -" or unicodedata.category(c)[0] in "LN").replace(" ", "-")
        n = seen.get(slug, 0)
        seen[slug] = n + 1
        out.add(slug if n == 0 else f"{slug}-{n}")
    return out

fail = []
for rules in sys.argv[1:]:
    # The rule file is plain YAML in a known shape; reading it line by line keeps this gate free of a
    # YAML library (promtool has already proven it parses).
    current = None
    found = {}
    for line in open(rules, encoding="utf-8"):
        if m := re.match(r"^\s*- alert:\s*(\S+)", line):
            current = m.group(1)
            found[current] = {}
        elif current and (m := re.match(r"^\s+(severity|runbook_url):\s*(\S+)\s*$", line)):
            found[current][m.group(1)] = m.group(2).strip("'\"")
    if not found:
        fail.append(f"{rules}: no alert rules")
    for alert, fields in found.items():
        severity = fields.get("severity")
        if severity not in SEVERITIES:
            fail.append(f"{rules}: {alert} has severity {severity!r}; the scheme is critical | major | minor")
        url = fields.get("runbook_url", "")
        if not url.startswith(PREFIX) or "#" not in url:
            fail.append(f"{rules}: {alert} needs a runbook_url {PREFIX}<file>#<heading>, got {url!r}")
            continue
        target, anchor = url[len(PREFIX):].split("#", 1)
        if not os.path.isfile(target):
            fail.append(f"{rules}: {alert} links to {target}, which does not exist")
        elif anchor not in anchors(target):
            fail.append(f"{rules}: {alert} links to {target}#{anchor}, which is not one of its headings")
if fail:
    print("\n".join(f"alert-rules: {f}" for f in fail), file=sys.stderr)
    sys.exit(1)
print(f"alert-rules: {sum(1 for _ in sys.argv[1:])} rule file(s): severities and runbook links hold")
PY
