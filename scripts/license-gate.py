#!/usr/bin/env python3
"""License gate (foundation §5 + the free-only rule): every package in the dependency
graph (transitive included) must carry an allowlisted OSS license. Anything else fails
the build — a commercial-license surprise cannot enter the golden path silently."""
import json
import re
import subprocess
import sys
from pathlib import Path

ALLOW = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "MS-PL", "0BSD", "PostgreSQL"}
# Explicit, justified exceptions (id, reason) — reviewed like any suppression:
# Proprietary-but-FREE exceptions — each entry must say WHY it is acceptable and WHERE it
# can enter the graph. These are NOT open source; they are reviewed policy decisions.
EXCEPTIONS: dict[str, str] = {
    # Microsoft Software License Terms (closed source, free of charge, redistributable).
    # Windows-only native SNI for Microsoft.Data.SqlClient. Reachable ONLY through the
    # OPTIONAL Goldpath.Locking.SqlServer package / the template's explicit sqlserver db choice —
    # i.e. only customers who already run commercially-licensed SQL Server. Approved 2026-07-05.
    "Microsoft.Data.SqlClient.SNI.runtime": "proprietary-free (Microsoft Software License Terms)",
    # Oracle Free Use Terms and Conditions (closed source, free of charge, redistributable).
    # The Oracle EF Core provider and its ODP.NET driver. Reachable ONLY through the template's
    # explicit `--db oracle` / manifest `database: oracle` choice — i.e. only adopters who
    # already run a commercially licensed Oracle Database. No Goldpath package references
    # them; the optional Goldpath.Locking.Oracle package (if O-4 ships it) is the one
    # exception and carries the same scope. Approved 2026-10-23 (goldpath-oracle RFC, #287).
    "Oracle.EntityFrameworkCore": "proprietary-free (Oracle Free Use Terms and Conditions)",
    "Oracle.ManagedDataAccess.Core": "proprietary-free (Oracle Free Use Terms and Conditions)",
}
# licenseUrl-era packages (no SPDX expression in the nuspec); licenses verified by hand.
# Every entry here was checked against its project page — reviewed like any suppression.
KNOWN_LEGACY: dict[str, str] = {
    "CommandLineParser": "MIT",
    "Microsoft.DotNet.PlatformAbstractions": "MIT",
    "Microsoft.NETCore.Platforms": "MIT",
    "Microsoft.VisualStudio.Validation": "MIT",
    "NETStandard.Library": "MIT",
    # SQLitePCLRaw 3.0.5+ ships native builds via this package; its LICENSE.txt reads
    # verbatim "SQLite is Public Domain" (https://sqlite.org/copyright.html). Verified
    # from the 3.53.4 nupkg, 2026-08-27.
    "SQLite": "Public Domain",
    "SourceGear.sqlite3": "Apache-2.0",
    "System.Buffers": "MIT",
    "System.ComponentModel.Composition": "MIT",
    "System.Memory": "MIT",
    "System.Numerics.Vectors": "MIT",
    "System.Security.Cryptography.ProtectedData": "MIT",
    "System.Security.Permissions": "MIT",
    "System.Threading.Tasks.Extensions": "MIT",
    "xunit.abstractions": "Apache-2.0",
}

root = Path(__file__).resolve().parent.parent
out = subprocess.run(
    ["dotnet", "list", str(root / "Goldpath.sln"), "package", "--include-transitive", "--format", "json"],
    capture_output=True, text=True, check=True)
data = json.loads(out.stdout)

packages: set[tuple[str, str]] = set()
for project in data.get("projects", []):
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []) or []:
                packages.add((package["id"], package["resolvedVersion"]))

nuget_root = Path.home() / ".nuget" / "packages"
failures, unknown = [], []
for pkg_id, version in sorted(packages):
    nuspec = nuget_root / pkg_id.lower() / version / f"{pkg_id.lower()}.nuspec"
    if not nuspec.exists():
        unknown.append((pkg_id, version, "nuspec not found"))
        continue
    text = nuspec.read_text(encoding="utf-8", errors="ignore")
    match = re.search(r'<license type="expression">([^<]+)</license>', text)
    license_expr = match.group(1).strip() if match else None
    if license_expr is None:
        if pkg_id in KNOWN_LEGACY:
            continue                       # verified by hand (see KNOWN_LEGACY)
        if pkg_id in EXCEPTIONS:
            print(f"  ALLOW {pkg_id} {version}: {EXCEPTIONS[pkg_id]}")
            continue
        unknown.append((pkg_id, version, "no license expression"))
        continue

    # SPDX semantics: any OR-branch fully allowed is enough; AND requires all terms.
    def branch_ok(branch: str) -> bool:
        terms = {t.strip().strip("()") for t in re.split(r"\bAND\b", branch)}
        return terms <= ALLOW

    allowed = any(branch_ok(b) for b in re.split(r"\bOR\b", license_expr))
    if not allowed and pkg_id not in EXCEPTIONS:
        failures.append((pkg_id, version, license_expr))

print(f"license-gate: {len(packages)} packages checked, allowlist={sorted(ALLOW)}")
for pkg_id, version, expr in failures:
    print(f"  FAIL  {pkg_id} {version}: {expr}")
for pkg_id, version, why in unknown:
    print(f"  CHECK {pkg_id} {version}: {why}")

if failures or unknown:
    sys.exit(1)
print("license-gate: GREEN — the golden path is entirely free/OSS")
