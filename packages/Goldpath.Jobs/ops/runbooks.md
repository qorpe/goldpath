# Goldpath.Jobs — runbooks (jobs RFC §8: runbook or it didn't happen)

Every procedure below is scriptable through the admin API (`/goldpath/admin/jobs`) — the
dashboard and the AI skills use the SAME verbs, and every verb writes an audit row.

## 1. Stuck run (progress stopped)

**Signal:** `goldpath_jobs_checkpoint_age_seconds` grows; run detail shows chunks `Claimed` and
none completing.

1. `GET /fleets` — is the claiming node still checking in? (`nodes[].lastCheckin`)
2. **Node dead** (last check-in older than the misfire threshold): do NOTHING — Quartz
   clustering re-fires with recovery on a healthy node within the threshold and the runner
   resumes from the checkpoint; stale claims reset automatically. Verify: run detail shows
   progress again; `run.executions` incremented.
3. **Node alive but the chunk hangs** (bug/downstream outage): the chunk's claim holds
   until the node restarts. Fix the downstream, then restart the worker POD (graceful:
   the current chunk drains or times out; the next fire resumes). Never delete run rows.
4. If the schedule window passed meanwhile, see §4 (misfire).

## 2. Poisoned item / failed chunk (repair queue)

**Signal:** `goldpath_jobs_item_failures_total` climbs or a run ends `Failed` with
`failedChunks > 0`; the run continued past them BY DESIGN.

1. `GET /runs/{id}` — read `openFailures[].reason`; fix the data/root cause.
2. Item-level: `POST /runs/{id}/replay-items` — replays open items through the job's
   `IGoldpathItemReplay` hook on an executor. A job without the hook fails the replay LOUDLY:
   add the hook, redeploy, replay again. Replayed items get `redrivenAt` stamped.
3. Chunk-level (run `Failed`): `POST /runs/{id}/rerun` starts a FRESH run over the full
   plan — safe only because job work must be idempotent per item (the platform rule);
   refuse the urge to hand-edit chunk rows.

## 3. Mid-run deploy / input version pinning

A run captures `inputVersion` at start (`PinInput`). Deploying a new tariff/config during
a run does NOT change the running run's inputs — chunks read the pinned version from
`context.InputVersion`. After the deploy: let the run finish, then `rerun` if the new
input must apply retroactively. The run row shows which version it used — the audit answer
to "which tariff billed this?".

## 4. Missed schedule window (misfire)

Quartz applies the trigger's misfire policy after `MisfireThreshold` (default 60s):
cron triggers default to FIRE-ONCE-NOW then continue on schedule. If the window matters
more than the execution (a 01:30 EOD that must NOT run at 09:00), pause the job before
maintenance (`POST .../pause`) and `resume` + `trigger` deliberately after. Check
`GET /fleets/{f}/jobs` → `triggers[].nextFireAt` to confirm the schedule re-armed.

## 5. Counter / cache reconciliation (MDM constraint #3)

Fast counters (an L4 policy engine's TPS/quota) are CACHE; the run tables are TRUTH.
After a cache loss: recompute from `GoldpathJobRuns`/`GoldpathJobRunChunks` (completed chunk × plan
density) before resuming paced work — never trust a freshly-warmed counter that says 0.

## 6. Fleet-wide stop (incident brake)

`POST /fleets/{f}/pause-all` pauses EVERY trigger in the fleet through the store — all
nodes obey on their next poll; running chunks drain and checkpoint. `resume-all` re-arms.
Both audited; both visible in `GET /audit`.

## 7. Graceful shutdown / deploy of a worker

SIGTERM → the hosted service waits for the running chunk, checkpoints, releases the fire.
The run stays OPEN and the next fire (schedule, recovery, or a manual `trigger`) resumes
it. Proven by the kill/resume integration tests — a deploy during a run costs at most one
in-flight chunk's repetition.

## 8. First deploy of a brand-new store (cold-boot contention)

Multiple executor nodes booting SIMULTANEOUSLY against an EMPTY `qrtz_` schema can contend
on the store's own lock bootstrap and stall before "scheduler started". Rolling deploys
never hit this (nodes join one at a time — Kubernetes' default maxSurge behaves); for a
first-ever deploy, bring ONE node up before scaling out. Once the store has lived a single
check-in cycle, concurrent (re)starts are ordinary cluster behavior. Found by the
kill-9 integration proof and pinned by its staggered-start arrangement.

## 9. Oracle: contention on `QRTZ_LOCKS` (G7, O-3, #406)

**What it looks like.** On an Oracle store (`Provider = Oracle`) the cluster serialises trigger
acquisition, misfire handling and check-in through row locks: `SELECT … FROM QRTZ_LOCKS WHERE
SCHED_NAME = :name AND LOCK_NAME = :lock FOR UPDATE`, one row per lock (`TRIGGER_ACCESS`,
`STATE_ACCESS`). Contention shows as:
- scheduled fires starting late on every node at once (a run's `StartedAt` behind its trigger's
  time), while `goldpath_jobs_run_duration_seconds` is unchanged: the work is no slower, getting
  to it is;
- sessions waiting on `enq: TX - row lock contention` in `V$SESSION` / ASH, with the blocking
  statement on `QRTZ_LOCKS`;
- at Debug level, Quartz logging `Lock 'TRIGGER_ACCESS' is desired by …` far more often than it
  logs the lock obtained.

**What to do.**
1. Find the blocker: `SELECT sid, blocking_session, event, sql_id FROM v$session WHERE event
   LIKE 'enq: TX%'`. A blocker that is not a Quartz session (a DBA script, a long transaction
   that touched `QRTZ_LOCKS`) is ended first; nothing outside Quartz should ever lock that table.
2. A blocker that IS a node: a node holding `TRIGGER_ACCESS` for seconds is usually starved
   itself (pool exhausted, a GC pause, a network stall to the database). Check that node's
   connection pool and the database round-trip before anything else.
3. If the fleet is simply too many nodes polling one store, fewer executors per scheduler name
   (one cluster per worker kind, jobs RFC D9) cuts acquisitions; the check-in interval is the
   next lever (`CheckinInterval`), at the cost of slower failover.
4. Never delete or truncate `QRTZ_LOCKS` rows on a running cluster: Quartz recreates them, but a
   node mid-acquisition loses its lock and the cluster can fire a trigger twice.
