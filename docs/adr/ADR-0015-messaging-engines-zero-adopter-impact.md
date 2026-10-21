# ADR-0015: Messaging engines — more than two adapters, zero adopter impact for any switch, the default locked for 1.x

- Status: **accepted** (2026-10-21 — owner decision in chat, recorded the same day in
  [goldpath-ga-readiness](../rfc/goldpath-ga-readiness.md) §8.3 Q1/Q2 and
  [goldpath-messaging-exit](../rfc/goldpath-messaging-exit.md) §7.1; the owner's merge of the
  pull request that introduces this ADR records it) · Scoped amendment of ADR-0013 (the message
  bus is a swappable engine) for THREE of its sentences, listed below; composes with ADR-0001
  (only the selected engine is compiled in), ADR-0005 (the rule ships with its verifiers: the
  analyzer and the switch proofs), ADR-0008 (no engine becomes selectable or default while the
  golden-manifest matrix is red) and ADR-0012 (the family bus is bound as a published package)

## Context

ADR-0013 made the bus engine an adapter behind a Goldpath-owned seam and named exactly two
adapters: the MassTransit 8.x one we ship and a Goldpath-family bus still to be written. On
2026-10-21 the owner answered the GA readiness RFC's Q1 and Q2. Three things in those answers
are not in ADR-0013 as written: a THIRD adapter (Wolverine, the 1.0 insurance, because the
free 8.x line's maintenance window closes around 2026-12-31 and no family-bus RFC or repository
exists yet); a hard rule that ANY engine switch costs an adopter nothing, which removes the
drained-cutover escape the messaging-exit RFC's §7.1 S4 allowed on 2026-09-03; and a default
engine that 1.0 locks for the whole 1.x line. A decision of that weight belongs in the
constitution, not only in an RFC's answer table.

## Decision

1. **The seam admits more than two engine adapters.** Each adapter is its own package; no other
   Goldpath package and no application code names an engine type. Three are named today:

   | Adapter | Package (proposed) | Role | Its bar, and when |
   |---|---|---|---|
   | MassTransit 8.x (Apache-2.0, pinned) | `Goldpath.Messaging.MassTransit` | **The default engine** — shipped today, the default through the previews and the whole 1.x line | It is the behaviour reference the others are measured against |
   | Wolverine (MIT, pinned) | `Goldpath.Messaging.Wolverine` | **The 1.0 insurance** against the 8.x window. Opt-in, never the default in 1.x | S1, S2, S3, S4 and S6 green **by 2026-12-31** (GA gate G5, train `0.1.0-preview.11`). S5 is not owed while it is not the default |
   | The Goldpath-family bus | named by its own RFC | **The committed long-term engine**, clean-room and its own qorpe repository exactly as ADR-0013 §2 and §4 say | Its own timeline (open thread T40, milestone `family-bus`): RFC + repository **December 2026** → first adapter **January–February 2027** → a ≥4-week shadow lane (S5) → **opt-in in a 1.x minor** → **the default only in 2.0**. It is committed: not dropped, not silently deferred — a month that passes without its step is a finding at the next train-boundary re-measure |

   The package names are proposed here; the engine-adapter RFC revision that opens GA gate G1
   confirms them, and the owner accepts them by merging it. A fourth adapter needs no new ADR —
   it needs this ADR's bar.
2. **Zero adopter impact is a hard rule for ANY engine switch or addition** — opting in to an
   engine, opting back out, and every default change. Before an adapter ships as selectable,
   all of these have run green (the proofs are messaging-exit §7.1's, which stays their
   definition):
   - **S1 — apps compile unchanged.** No engine type in application code, analyzer-enforced
     (GP0404, GP0405 and the namespace ban ADR-0013 §1 requires, widened to every adapter's
     engine). Every broker-bearing golden-manifest shape, CorPay and api-portal compile
     unchanged against the adapter. Selecting an engine is a manifest value plus a package
     reference, never a line of application code.
   - **S2 + S3 — conformance and the differential run.** The bus conformance suite is green on
     the adapter and its observable outcomes match the oracle's.
   - **S4 — wire compatibility, MANDATORY.** In a mixed fleet, a message published under one
     engine is consumed under the other in both directions, on the 8.x envelope and topology,
     and outbox rows written under one engine are delivered by the other. **The
     drained-cutover-with-a-maintenance-window escape is not allowed**: an adapter that cannot
     interoperate on the wire does not ship as selectable.
   - **S6 — rollback proven both ways.** Switching back passes S1–S4 the other way, and the
     upgrade guide carries both directions.

   An adapter that cannot meet this bar is not a manifest value and is not published as a
   package an adopter can select; it may be built and measured in the repository until it
   does. **S5** — the ≥4-week shadow lane on the GM broker shapes, CorPay and api-portal —
   is owed before an engine becomes the default, and the family bus runs it before its
   opt-in as well (its timeline above).
3. **The default engine is locked for the 1.x line.** A new engine reaches 1.x adopters as an
   opt-in in a minor release (additive under the versioning contract). Changing the default
   is a **2.0** change, at a train boundary, with an upgrade guide that carries both directions,
   after S1–S6 have all passed and while the golden-manifest matrix is green.
4. **What of ADR-0013 stops applying, and what stays.** ADR-0013's file is not edited; these
   three sentences are read as amended here:
   - §2: *"The library behind that seam is an **adapter**, and there are two of them: the
     MassTransit 8.x adapter (…) and a **Goldpath-family bus** (…)"* — the words "and there are
     two of them" stop applying; the adapter set is §1 above. The rest of §2 — the family bus
     as its own open-source qorpe repository, never on the accelerator's critical path —
     stands.
   - §3: *"The default adapter flips only when the suite, the differential run and the shadow
     lane in the RFC's §7 have all passed; the flip is reversible by the same proof run the
     other way."* — replaced by §2 and §3 above: the bar binds every switch, not only the
     default flip, it adds S1 and a mandatory S4, and a default flip is a 2.0 change.
   - Consequences: *"the default flip is a train boundary with an upgrade guide and the rollback
     proven"* — it is a **major** train boundary (2.0), under §3 above.

   Everything else in ADR-0013 stands unchanged: the Goldpath-owned seam and its analyzer (§1),
   the conformance suite as the definition of "a bus that Goldpath can run on" (§3's first
   sentence — its oracle is the pinned 8.x patch, 8.5.11 since 2026-10-19), the clean-room rule
   for the family bus (§4), and its Rationale. This is **not a second exception to ADR-0003**:
   it widens the same one concern ADR-0013 carved out — the bus engine — and nothing else.

## Rationale

ADR-0013 exists so that a vendor's licensing calendar never becomes our adopters' migration.
A swap that compiles unchanged but needs a maintenance window to drain every queue, or that
cannot be rolled back, is still a migration — just one the adopter's operations team pays for
instead of their developers. "Zero adopter impact" is the only reading of ADR-0013's promise an
enterprise operator can plan around, so it becomes the bar for every engine rather than a hope
for the last one. Naming more than two adapters is the honest consequence of the calendar: the
8.x window closes around the end of 2026, the family bus cannot exist and finish a shadow lane
before then, and Wolverine — MIT-licensed, sized by the messaging-exit RFC at about a one-week
adapter once the suite exists — is the insurance that makes the window survivable without
dropping the long-term engine. Locking the default for 1.x follows from the versioning
contract, which counts a default flip as breaking: an adopter who pins 1.x gets the engine they
installed until they choose otherwise.

## Consequences

- `Goldpath.Messaging` keeps the seam; each engine lives in its own adapter package, and an
  application references the one its manifest selects — the others do not exist in it
  (ADR-0001). GA gate G1's check (no engine type in any `PublicAPI.*.txt` outside its own
  adapter package) applies to every adapter, not only MassTransit.
- The one-time move of the registration, outbox/inbox configuration and test harness to
  Goldpath vocabulary (G1, the breaking `0.1.0-preview.11` train, with its upgrade guide) is the
  price of completing the seam, paid once before 1.0. It is not an engine switch; after it, no
  switch may ask an adopter to change a line of code.
- Every adapter is pinned (no "latest") and passes the licence gate with its full dependency
  tree; air-gapped restore stays a first-class path.
- The Wolverine proofs are linked in open thread T18 and close it; the family bus's S1–S6 belong
  to T40. GA gate G5 is the Wolverine bar above; the family bus is off the 1.0 critical path.
- Because the default is locked, the answer an unpatched 8.x CVE after the window can get in
  1.x is the Wolverine opt-in — which this rule makes a manifest change, rolled back the same
  way; the posture itself is SECURITY.md's to state (G5).
- A switch proposed without S1, S2, S3, S4 and S6 linked is refused on this ADR, whatever the
  calendar says. Relaxing the rule — reinstating a drained cutover, or a default flip inside
  1.x — needs its own ADR.
