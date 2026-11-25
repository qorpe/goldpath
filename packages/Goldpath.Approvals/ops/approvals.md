# Approvals — Ops Runbook

## "Why is this request still pending" triage
1. Read the request's TRAIL (`GetAsync` / the admin surface): every routing, escalation and
   delegation step is in it with its actor and timestamp — the answer is usually the last line.
2. Pending age counts from `PendingSince` (it RESETS on escalation — a rung's deadline is that
   rung's, not the request's lifetime). Compare against the ladder's `EscalateAfter` per rung.
3. If nothing escalates at all: the sweep is a scheduled job — check the Jobs console for the
   escalation job's last run before suspecting the engine.

## Decision refused — outcome decoding
- `FourEyesViolation` — the requester tried to decide their own request. Not a bug, the rule.
- `WrongRole` — decider holds neither the pending rung's role nor an active delegation.
  Check delegation expiry (`Until` is absolute UTC) before re-granting roles.
- `NotPending` — someone else decided first, or it expired. The trail says which.

## Escalation storms
A spike of `GoldpathApprovalEscalated` events means a rung stopped deciding (vacation,
role change, worklist not being watched). Delegate that rung's holder or shorten nothing —
fix the staffing; the ladder's deadlines are the SLA you declared.

## Expiries
`GoldpathApprovalExpired` at the top rung is a governance signal, never noise: the request
needed the highest authority and did not get it in time. Alarm on expiry count > 0.

## Store
The in-memory store loses state on restart — it is for tests and single-node demos.
Compose a database-backed `IGoldpathApprovalStore` before production.

## The console panel and the admin surface
`MapGoldpathApprovalsAdmin()` mounts `/goldpath/admin/approvals` (contract §7.1); the
operations console federates on it — the worklist with quorum said as `n/m`, the trail,
and decide verbs that run the ENGINE unchanged (the caller's principal is the decider, so
four-eyes holds on this surface too). A refusal answers the rule's name verbatim.

## Metrics and the dashboard
The meter `Goldpath.Approvals` counts requested/granted/rejected/escalated/expired/
withdrawn per ladder; `grafana-approvals-dashboard.json` beside this file reads them.
§2 rule of thumb: alert on the escalated/expired pair, not on queue length — the queue is
the console's job, the PAIR is the staffing signal.

## Alerts (the thresholds this module is worth waking someone for)

Written 2026-09-05, for the same reason as the FileExchange table: the runbook decoded
outcomes and named no threshold. Every series carries a `ladder` tag — alert per ladder,
because a ladder's deadlines are the SLA that ladder declared.

| alert | expression (Prometheus shape) | why this and not a lower bar |
|---|---|---|
| Expiries at the top rung | `increase(goldpath_approvals_expired_total[1h]) > 0` | An expiry means the whole authority chain ran out of time. There is no rung above it, so nothing else will catch this. Page on the first one. |
| Escalation storm | `increase(goldpath_approvals_escalated_total[1h]) > 3 * <ladder's normal hourly rate>` | A rung stopped deciding (vacation, role change, unwatched worklist). Escalation is working as designed and the staffing is not. |
| Sweep stopped | `increase(goldpath_approvals_escalated_total[6h]) == 0` while requests are pending | Deadlines are enforced by a scheduled job, not by the read path (GP1902). A dead sweep looks exactly like a quiet day. Cross-check the Jobs console before paging. |
| Decision drought | `rate(goldpath_approvals_granted_total[1h]) + rate(goldpath_approvals_rejected_total[1h]) == 0` while requested > 0 for 4h | Requests arriving and nothing being decided: the worklist is not being watched, and escalation will start firing in an hour anyway. |

Do NOT alert on rejection RATE: a ladder that rejects is a ladder that works.

## Measured performance
Request, decide, worklist and escalation-sweep numbers on PostgreSQL, with the profile, the command and how
to read them: [approvals-benchmarks.md](approvals-benchmarks.md) (`scripts/bench-approvals.sh`).
