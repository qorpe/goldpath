# Goldpath.Campaign — runbooks (campaign RFC §8: runbook or it didn't happen)

Verbs live at `/goldpath/admin/campaign` (create/pause/resume/abort/throttle — EVERY verb is
audited to `GoldpathCampaignAudit`). Item replay lives in the JOBS console (`replay-items`);
the pacer's run health lives on the goldpath-jobs dashboard. This board owns the GOVERNOR.

## 1. Release rate ≠ configured TPS

- The `released/s` line should track the campaign's `Tps` within a leader tick or two.
- Rate is ZERO: `GET /{id}` — `WindowOpenNow` false? Then the window is doing its job
  (§4 if that surprises you). `State` Paused? Read `LastVerb` — either an operator or
  the TARGET CEILING paused it (the verb text says which; resume or abort is a decision,
  not a default). Quota exhausted? `ReleasedToday` vs `DailyQuota` on the same view.
- Rate is LOW but nonzero: in-flight is pinned (§2) or the leader is starving — check
  the pacer run on the JOBS dashboard (a dead fleet means no leader; the next fire
  takes over from the watermarks, nothing is lost).

## 2. In-flight pinned at MaxInFlight

- The pacer is deliberately holding releases: consumers terminal-stamp slower than the
  release rate. This is the BROKER-PROTECTION governor working, not a bug.
- Scale the consumer fleet, or accept the pace. Raising `MaxInFlight` via
  `POST /{id}/throttle` moves the pressure to the broker/provider — a decision, so it
  lands in the audit with your name on it.

## 3. Gateway screaming / provider rate-limits mid-campaign

- `POST /{id}/throttle {"tps": <lower>}` — the LIVE row is re-read every leader tick;
  the new ceiling takes effect within ~250ms, no restart, no redeploy (D6).
- The old→new policy lands in `LastVerb` AND the audit trail; the released items already
  on the wire drain through the sink.
- Provider hard-down: `POST /{id}/pause` — in-flight items drain, nothing new releases;
  resume when the provider recovers.

## 4. "The campaign isn't sending" (but the window is closed)

- `GET /{id}` — `WindowOpenNow` false + `window_closed_ticks` climbing = CORRECT
  behaviour: the send window (e.g. 09:00–18:00 Europe/Istanbul) is closed.
- Sending outside the window is a policy change: `POST /{id}/throttle` with
  `"clearWindow": true` (audited) — or wait for the window; the budget does not bank
  beyond one second, so the morning does not open with a burst.

## 5. Failed items / poisoned targets

- `GET /{id}/failures` — Seq + the handler's error text. The same items sit in the
  JOBS repair queue (filed by the completing slice, or immediately when a claim went
  stale): replay from THERE (`replay-items`) — one repair discipline for the whole asset.
- "interrupted mid-flight" errors are the claim-repair story: the consumer died between
  claim and outcome — the provider MAY have received it. Confirm on the provider side
  before replaying; that is exactly why it did not silently re-send.
- Replay success heals the campaign counters (FailedCount decrements); the campaign
  state stays `CompletedWithFailures` — the completion evidence is history, not a lie.

## 6. Abort discipline

- `POST /{id}/abort {"reason": "..."}` — the reason is REQUIRED (it becomes evidence).
  Unreleased/unclaimed items terminal-stamp `Aborted`; items a consumer already CLAIMED
  finish their in-flight call and drain through the sink — an abort never yanks a
  half-made external call.
- After the drain, item states answer precisely: how many went out, how many succeeded,
  how many were never attempted.

## 7. Ceiling breach (campaign paused by the guard)

- `State=Paused` + `LastVerb` starting with "goldpath: target ceiling" — enumeration found
  MORE targets than the type's `MaxTargets`. At L4 scale that is an outage guard, not
  an inconvenience.
- Narrow the selector parameters (create a new campaign) or raise the type's ceiling in
  CODE (PR review — the ceiling is a decision) — then resume or abort this instance.

## 8. Acceptances piling up (R2.1)

- `AwaitingAck` growing on the items drill-down, `goldpath_campaign_ack_timeouts_total`
  climbing — the target system accepted and never answered, or its answers cannot reach
  `MapGoldpathCampaignCallbacks`. Check the surface first (reachability, the policy or the
  mTLS boundary it sits behind, the correlation id the callback names — `404` means the id
  is unknown to the type, `200` means the item already settled).
- Nothing is lost while you look: the sweep turns every missed deadline into a RETRYABLE
  failure (`ACK_TIMEOUT`), the ladder re-releases it, and exhaustion lands in the repair
  queue — never in a silent success. A second attempt gets a fresh correlation id.

## 9. Orphaned releases being published again (R2.6)

- `goldpath_campaign_orphans_rereleased_total` climbing with no pause or resume in the audit
  trail — consumers are refusing claims or the broker is losing messages: a half-stopped
  host whose consumers still hold prefetched messages, a purged queue, a consumer fleet
  scaled to zero. The sweep keeps the campaign moving under its own allowance; the number
  says a host needs looking at.
- After a resume the counter jumps once by design: every Released row is marked due so the
  messages consumers refused during the pause come back on the next tick.
