# Goldpath.Approvals

Human approval workflows as a composable Ring B module: amount-laddered authority chains
**declared as data**, four-eyes and maker-checker enforcement, bounded delegation, deadline
escalation, and the worklist — with every lifecycle step audited and published as
integration events. This module replaces the e-mail approval thread, not the humans in it.

```csharp
builder.AddGoldpathApprovals(approvals => approvals
    .AddLadder("credit-limit", ladder => ladder
        .Rung("expert", upToInclusive: 1_000_000m, escalateAfter: TimeSpan.FromHours(8))
        .Rung("deputy-manager", 5_000_000m, TimeSpan.FromHours(8))
        .Rung("manager", 15_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
        .TopRung("general-manager", TimeSpan.FromHours(24))));
```

- **Request** — `engine.RequestAsync("credit-limit", subject, amount, requestedBy)` routes
  the amount to its rung and starts the trail.
- **Decide** — `engine.DecideAsync(id, decidedBy, deciderRole, granted, reason)` enforces
  four-eyes (the requester may never decide their own request) and the rung's role.
  Refusals are values (`GoldpathApprovalDecisionOutcome`), not exceptions.
- **Quorum** — a rung may demand N DISTINCT grants (`requiredApprovals`); each grant is a
  signature toward the rung, and distinct-eyes holds across the WHOLE chain: one identity
  signs at most once per request, even after escalation (`AlreadySigned`).
- **Concurrent decisions** — two decisions at once never both apply (#480). On the built-in
  stores the request row is version-checked, and a signature is written with the request in
  one transaction under a unique `(RequestId, SignedBy)` key. The decision that loses reads
  again: the same identity answers `AlreadySigned`, and a request the other decision closed
  answers `NotPending`, with no event. A custom store keeps its own guarantees.
- **Rejection** — terminal at any point, and its reason is MANDATORY: a blank reason is
  refused (`ReasonRequired`) — a bare "no" teaches the requester nothing.
- **Withdraw** — `WithdrawAsync(id, requestedBy)` takes back a pending request; only its
  requester may (`NotRequester` otherwise), and the step lands in the trail.
- **Resubmit** — `ResubmitAsync(rejectedId, requestedBy)` reworks a rejected request as a
  fresh one, linked by `SupersedesId` with both trails cross-referenced — the audit reads
  request → reject → rework as one story.
- **Delegate** — `DelegateRoleAsync(from, fromRole, to, window)`: the delegate decides the
  delegator's rung (`fromRole`, taken from the authenticated delegator's claims, never from a
  request body) and no other; bounded window, depth one (a delegate cannot re-delegate; the cycle
  guard is structural). The role-less `DelegateAsync` refuses since preview.10 (#479).
- **Escalate** — `EscalateOverdueAsync()` moves overdue requests one rung up; overdue at
  the top rung expires. Schedule it with `jobs.AddGoldpathApprovalsJobs()` (a five-minute
  sweep by default — rung deadlines are measured in hours, so the granularity never moves
  an SLA), or call it from your own loop.
- **Worklist** — `WorklistAsync(identity, role)`: what this person may decide, oldest first.

Events (`GoldpathApprovalRequested/Granted/Rejected/Escalated/Expired`) carry the
`IIntegrationEvent` marker and publish through the messaging seam when a broker is
composed — and stay silent when not.

State lives behind `IGoldpathApprovalStore`; the in-memory store ships for tests and
single-node hosts, a database-backed store composes through the seam.

- **Console** — `MapGoldpathApprovalsAdmin()` mounts the admin surface
  (`/goldpath/admin/approvals`, contract §7.1) and the operations console federates on
  it: the worklist with the quorum said as a number, the trail, and decide verbs that run
  the ENGINE unchanged — a refusal answers the rule's name.
- **Metrics** — the `Goldpath.Approvals` meter counts every lifecycle step per ladder;
  the Grafana board ships in `ops/grafana-approvals-dashboard.json`.

This is a saga's counterpart, not its competitor: a compensating flow orchestrates
SYSTEMS and may request an approval as one of its steps; Approvals never drives system
steps itself (RFC D3).
