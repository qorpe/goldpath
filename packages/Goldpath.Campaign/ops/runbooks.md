# Goldpath.Campaign — runbooks (campaign RFC §8: runbook or it didn't happen)

Verbs live at `/goldpath/admin/campaign` (create/pause/resume/abort/throttle — EVERY verb is
audited to `GoldpathCampaignAudit`). Item replay lives in the JOBS console (`replay-items`);
the pacer's run health lives on the goldpath-jobs dashboard. This board owns the GOVERNOR.

## 1. Release rate ≠ configured TPS

- The `released/s` line should track the campaign's `Tps` within a leader tick or two.
- Rate is ZERO: `GET /{id}` — `State` Ready? The campaign is **waiting for its start, not
  stuck** (R3.1): the snapshot is complete and `startAt` (UTC) is still ahead; it starts on
  its own (§11 to move it). `WindowOpenNow` false? Then the window is doing its job
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
  resume when the provider recovers. Pause means now (R2.6): an item message still on the
  queue reaches a consumer that claims nothing, so only the calls already in flight finish
  — at most one per consumer slot. Measured on a real broker (S-FLEET-04, two hosts, eight
  slots, a 600 ms device): no claim after the pause committed, the fleet quiet within 423 ms,
  against a 3-second acceptance bar ([campaign-benchmarks.md](campaign-benchmarks.md)).

## 4. "The campaign isn't sending" (but the window is closed)

- `GET /{id}` — `WindowOpenNow` false + `window_closed_ticks` climbing = CORRECT
  behaviour: the send window (e.g. 09:00–18:00 Europe/Istanbul) is closed.
- Sending outside the window is a policy change: `POST /{id}/throttle` with
  `"clearWindow": true` (audited) — or wait for the window; the campaign's budget does not refill
  while its window is closed, and no bucket holds more than one tick's refill plus the fraction
  it carries (#292), so the morning does not open with a burst. (The ceiling holds for every
  second of ticks when `LeaderTick` divides one second, as the default 250 ms does; any other tick
  lets a second release up to one more tick's refill.)

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
  `MapGoldpathCampaignCallbacks`. Check the surface first: reachability, then what it
  answers the target system —
  - `401` — the signature is missing, stale or wrong (`goldpath_campaign_callbacks_rejected_total`
    climbs with it): the sender's secret is not one of the type's active secrets (a rotation
    half done — §10), it signs a different path than it sends (a proxy that rewrites the
    prefix), or its clock is more than `CallbackTimestampTolerance` (5 minutes) off;
  - `404` — the campaign in the path is not of that type, or no item of it carries the
    correlation id and none is in flight that could;
  - `202` — published, or PARKED: the answer outran its acceptance and settles when the
    acceptance lands (`goldpath_campaign_callbacks_parked_total`). Parks nobody claims expire
    after `StaleClaimAfter`;
  - `200` — the item already settled; the repeat changed nothing.
- Nothing is lost while you look: the sweep turns every missed deadline into a RETRYABLE
  failure (`ACK_TIMEOUT`), the ladder re-releases it, and exhaustion lands in the repair
  queue — never in a silent success. On a campaign the pacer no longer releases (aborted,
  expired, finished — a replay) the timeout is terminal at once and filed for repair. A
  second attempt is a new acceptance with a new correlation id.
- A campaign that stalls with `AwaitingAck` at `MaxInFlight`: every acceptance holds an
  in-flight slot until its answer or its deadline. Raise `MaxInFlight` toward Tps × the ack
  timeout the handler declares, or shorten the timeout.

## 9. Orphaned releases being published again (R2.6)

- `goldpath_campaign_orphans_rereleased_total` climbing with no pause or resume in the audit
  trail — consumers are refusing claims or the broker is losing messages: a half-stopped
  host whose consumers still hold prefetched messages, a purged queue, a consumer fleet
  scaled to zero. The sweep keeps the campaign moving under its own allowance; the number
  says a host needs looking at.
- After a resume the counter jumps once by design: every Released row is marked due so the
  messages consumers refused during the pause come back on the next tick.

## 10. Rotating a callback secret (R2.8)

The secret is per campaign type and comes from configuration. Rotation never drops a
callback, because for its duration two secrets verify:

1. Generate the next secret (`openssl rand -base64 32`) and store it beside the current one.
2. Deploy with `.SignedCallbacks(next, previousSecret: current)` — both verify.
3. Move the target system to `next`. Watch `goldpath_campaign_callbacks_rejected_total`: it
   must stay flat.
4. Deploy with `.SignedCallbacks(next)` — the old secret stops verifying.

For a LEAKED secret, skip the overlap: deploy `.SignedCallbacks(next)` alone and move the
target system to it at once. Callbacks still signed with the leaked secret are `401` from
that deploy on, and the items they would have settled time out into the ladder rather than
settle on a forged word.

## 11. Moving a scheduled start (R3.1)

- `GET /{id}` — `startAt` is the instant in UTC; the policy's `timeZoneId` is how to read it.
  Convert the wall-clock time you were given in that zone yourself: on a DST Sunday, 02:30 in
  Europe/Berlin does not exist in spring and happens twice in autumn, so ask which one is meant.
- `POST /{id}/throttle {"startAt": "2026-11-09T07:00:00Z"}` moves it; `{"clearStartAt": true}`
  starts the campaign as soon as its snapshot is ready (a `Ready` one on the next tick). The
  old → new policy lands in `LastVerb` and the audit trail with your name on it.
- Refused "has already started": the campaign is `Running` or released something. A start
  cannot be taken back; pause it, or lower its `tps`.
- Refused "after the end date": the start would fall after `endDate`'s local day, so the
  campaign would expire before it started. Move the end date in the same or an earlier call.
- A start in the past means now. The start itself is audited `start` with the actor `system`.
