# packages/

25 packages. The NuGet train is 24 of them (Abstractions → ServiceDefaults → ApiDefaults → Data →
Messaging → Ring B/C modules + providers, among them the optional Locking providers `Goldpath.Locking.SqlServer`
and `Goldpath.Locking.Oracle`), published as `0.1.0-preview.*` with OIDC trusted publishing.
The 25th, `Goldpath.Messaging.Wolverine`, is the second messaging engine's adapter (GA gate G5): it is
built and runs the bus conformance suite in CI, but it is not published (`IsPackable=false`) until its
switch proofs S1–S4 and S6 are linked in open-threads T18 (ADR-0015 §2).
Per-module RFCs and status: `docs/rfc/README.md`. Live capability ledger:
`docs/strategy/coverage-matrix.md`.
