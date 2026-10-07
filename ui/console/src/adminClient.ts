/**
 * The console's ONLY door to a service: the FROZEN admin contract
 * (docs/rfc/goldpath-admin-contract.md). Every call carries the operator's credentials —
 * the console is a well-dressed client of the same API adopters script, nothing more.
 */
export interface AdminClientOptions {
  /** Service root, e.g. "" for same-origin or "https://payments.internal". */
  baseUrl?: string;
  fetcher?: typeof fetch;
}

/** The seven module surfaces of the frozen contract. */
export const MODULES = ["jobs", "archival", "bulk", "notification", "campaign", "approvals", "fileexchange"] as const;
export type ModuleName = (typeof MODULES)[number];

/**
 * The probe route per module — the contract's own list root. A 404 means the module was
 * never composed into this app, so the panel does not exist (console RFC §2: no manifest
 * upload, no config drift — the API is the truth here too).
 */
const PROBE: Record<ModuleName, string> = {
  jobs: "/goldpath/admin/jobs/fleets",
  archival: "/goldpath/admin/archival/definitions",
  bulk: "/goldpath/admin/bulk/definitions",
  notification: "/goldpath/admin/notification/templates",
  campaign: "/goldpath/admin/campaign/",
  approvals: "/goldpath/admin/approvals/requests",
  fileexchange: "/goldpath/admin/fileexchange/rails",
};

/**
 * What a probe learned about one module. A refusal carries the SERVER's words: the
 * console repeats them rather than inventing its own explanation.
 */
export type Capability =
  | { kind: "present" }
  | { kind: "absent" }
  | { kind: "forbidden"; message?: string }
  | { kind: "refused"; message?: string }
  /**
   * The probe never got an answer: the service is down, the network is in the way, or the
   * browser blocked the call (a cross-origin service whose CORS does not allow this
   * console's origin looks EXACTLY like this from here — fetch reports nothing more).
   * Distinct from `absent` on purpose: "the app does not have this module" and "we could
   * not ask" are different sentences, and only one of them is a reason to stop looking.
   */
  | { kind: "unreachable" };

/**
 * Pulls the human sentence out of a refusal. Goldpath's own envelope says `message`;
 * ASP.NET's ProblemDetails (what tenant resolution answers with) says `title`/`detail`.
 * Neither is guessed at — an unreadable body simply yields nothing.
 */
async function refusalMessage(response: Response): Promise<string | undefined> {
  try {
    const body = (await response.clone().json()) as { message?: string; detail?: string; title?: string };
    return body.message ?? body.detail ?? body.title;
  } catch {
    return undefined;
  }
}

/** One cluster member's heartbeat, exactly as `GoldpathFleetNode` sends it. */
export interface FleetNode {
  instanceName: string;
  lastCheckin: string;
  checkinInterval: string;
}

export interface FleetInfo {
  schedulerName: string;
  jobCount: number;
  nodes: FleetNode[];
}

/**
 * The instance this console talks THROUGH — not the fleet. Quartz metadata is
 * per-instance, and a management-mode member reports standby with an idle thread pool
 * while the executors fire normally.
 */
export interface FleetConnection {
  instanceId: string;
  runningSince: string | null;
  threadPoolSize: number;
  jobsExecuted: number;
  isShutdown: boolean;
  inStandbyMode: boolean;
}

/** The fleet as the STORE sees it (contract R2.1) — cluster facts, not one member's. */
export interface FleetStatus {
  schedulerName: string;
  jobCount: number;
  /** What `pause-all` leaves behind: durable and cluster-wide. */
  isPaused: boolean;
  nodes: FleetNode[];
  connection: FleetConnection;
}

/** One trigger's live state (contract R2.2). */
export interface TriggerInfo {
  name: string;
  state: string;
  cronExpression: string | null;
  calendarName: string | null;
  nextFireAt: string | null;
  previousFireAt: string | null;
  type: string;
  priority: number;
  misfireInstruction: number;
  timeZoneId: string | null;
  startAt: string | null;
  endAt: string | null;
  timesTriggered: number | null;
  repeatInterval: string | null;
  repeatCount: number | null;
}

/**
 * One job, as `GoldpathJobInfo` actually sends it.
 *
 * This type used to carry `paused` and `nextFireTime`, which the contract has never
 * returned — so the console's paused badge could not light no matter what an operator
 * did, and "next fire" never appeared. The unit test agreed, because its fixture was
 * shaped from this type rather than from the payload. Whether a job is paused is a fact
 * about its TRIGGERS, and that is where it is read from now (`isPaused` below).
 */
export interface JobInfo {
  name: string;
  description?: string | null;
  requestsRecovery?: boolean;
  triggers: TriggerInfo[];
  dataMap?: Record<string, string> | null;
}

/**
 * A job is paused when every trigger it has is paused — one live trigger means it still
 * fires. A job with NO trigger is not paused; it is unscheduled, which the screen says in
 * its own words rather than dressing as a pause.
 */
export function isPaused(job: JobInfo): boolean {
  return job.triggers.length > 0 && job.triggers.every((trigger) => trigger.state === "Paused");
}

/** The soonest a job will fire, or null when nothing is scheduled to. */
export function nextFireAt(job: JobInfo): string | null {
  const times = job.triggers.map((trigger) => trigger.nextFireAt).filter((at): at is string => at !== null);
  return times.length === 0 ? null : times.reduce((soonest, at) => (at < soonest ? at : soonest));
}

/** A calendar as the contract holds it (frozen route, first screened in U5). */
export interface CalendarInfo {
  name: string;
  description: string | null;
  usedByTriggers: string[];
}

/** The four calendar shapes the contract accepts; exactly one shape per type. */
export interface CalendarSpec {
  type: string;
  description?: string | null;
  excludedDates?: string[] | null;
  excludedDays?: number[] | null;
  cronExpression?: string | null;
}

/** One admin crossing, as the audit read returns it. */
export interface AdminAuditRow {
  id: number;
  at: string;
  actor: string;
  action: string;
  fleet: string;
  /** The job, calendar or run the verb targeted — the row's own word is `target`. */
  target: string;
  detail: string | null;
}

/** A trigger an operator adds to a DECLARED job (contract R2.5). */
export interface AddTriggerRequest {
  name: string;
  cron?: string | null;
  timeZoneId?: string | null;
  interval?: string | null;
  repeatCount?: number | null;
  calendarName?: string | null;
  priority?: number | null;
}

export interface RunSummary {
  id: string;
  jobName: string;
  status: string;
  startedAt: string;
  finishedAt?: string | null;
  deadlineAt?: string | null;
  predictedFinishAt?: string | null;
  totalChunks: number;
  completedChunks: number;
  failedChunks: number;
  totalItems?: number | null;
  itemFailures: number;
  /** The instance that STARTED the run — it was always on the payload, never on screen. */
  startedBy?: string | null;
  /** Scheduled | Manual | Rerun | Replay; null on runs written before the column (R2.3). */
  triggeredBy?: string | null;
}

/**
 * The run detail as the contract actually returns it — a NESTED record
 * (`GoldpathRunDetail(Run, ChunksByStatus, OpenFailures)`), not a flattened row:
 * chunks arrive as counts per status, and open failures are capped at 200 server-side.
 */
export interface RunDetail {
  run: RunSummary;
  chunksByStatus: Record<string, number>;
  openFailures: {
    id: number;
    runId: string;
    chunkIndex: number;
    itemKey: string;
    reason: string;
    failedAt: string;
    redrivenAt?: string | null;
  }[];
}

/** Intake numbers per definition — the panel's headline row. */
export interface BulkDefinitionStatus {
  name: string;
  batchesByState: Record<string, number>;
  awaitingApproval: number;
  oldestAwaitingApprovalSeconds?: number | null;
}

/** One batch over the wire: the state machine's public face. */
export interface BulkBatchInfo {
  id: string;
  definition: string;
  state: string;
  tenant?: string | null;
  totalRows: number;
  validRows: number;
  invalidRows: number;
  executedRows: number;
  failedRows: number;
  runId?: string | null;
  receivedAt: string;
  validatedAt?: string | null;
  decidedAt?: string | null;
  decidedBy?: string | null;
  decisionNote?: string | null;
  completedAt?: string | null;
}

/** One validation finding — teaching text, value-free by contract. */
export interface BulkRowError {
  id: number;
  batchId: string;
  rowNumber: number;
  field: string;
  message: string;
}

/** A campaign as the governor sees it: pacer counters + the live policy in force. */
export interface CampaignInfo {
  id: string;
  type: string;
  name: string;
  state: string;
  enumeratedThrough: number;
  enumerationComplete: boolean;
  releasedThrough: number;
  succeededCount: number;
  failedCount: number;
  inFlight: number;
  remaining: number;
  tps: number;
  dailyQuota?: number | null;
  releasedToday: number;
  maxInFlight: number;
  windowStart?: string | null;
  windowEnd?: string | null;
  timeZoneId: string;
  excludedDays: string[];
  endDate?: string | null;
  maxAttempts: number;
  /** R2.5: "Normal" | "High" | "Low" — the share of a contended shared ceiling (GlobalTps, a type's MaxTps). */
  priority: string;
  windowOpenNow: boolean;
  etaSecondsAtCurrentTps?: number | null;
  createdAt: string;
  createdBy: string;
  completedAt?: string | null;
  lastVerb?: string | null;
  tenant?: string | null;
}

/** One failed item — the drill-down; REPLAY belongs to the jobs console. */
export interface CampaignFailedItem {
  seq: number;
  error?: string | null;
  completedAt?: string | null;
}

/** One audited verb against a campaign (who did what, newest first). */
export interface CampaignAuditEntry {
  id: number;
  at: string;
  actor: string;
  action: string;
  campaignId: string;
  detail?: string | null;
}

/**
 * The LIVE policy patch: every field is optional and a null one KEEPS its current value,
 * so the console sends only what the operator actually changed. Clearing is explicit —
 * `clearDailyQuota` / `clearWindow` exist because "no quota" cannot be said with a null.
 */
export interface CampaignThrottle {
  tps?: number;
  dailyQuota?: number;
  maxInFlight?: number;
  windowStart?: string;
  windowEnd?: string;
  timeZoneId?: string;
  clearDailyQuota?: boolean;
  clearWindow?: boolean;
  /** R1.1: English day names ("Saturday"); omitted = keep, clearExcludedDays wipes. */
  excludedDays?: string[];
  /** R1.2: yyyy-MM-dd in the policy timezone; omitted = keep, clearEndDate wipes. */
  endDate?: string;
  /** R1.3: attempts per item before the repair queue (1 = no auto-retry). */
  maxAttempts?: number;
  clearExcludedDays?: boolean;
  clearEndDate?: boolean;
  /** R2.5: the share of a contended shared ceiling (High 3 · Normal 2 · Low 1); omitted = keep. */
  priority?: "Normal" | "High" | "Low";
}

/** One template with its live queue numbers and its retention promise. */
export interface NotificationTemplateStatus {
  key: string;
  hash: string;
  /** ISO-8601 duration or null — how long the rendered body survives (D4 retention). */
  deleteBodyAfter?: string | null;
  byState: Record<string, number>;
  oldestRequestedSeconds?: number | null;
}

/** One approval request as the admin surface returns it — quorum numbers included. */
export interface ApprovalRequestInfo {
  id: string;
  ladder: string;
  subject: string;
  amount: number;
  requestedBy: string;
  requestedAt: string;
  pendingRole: string;
  pendingSince: string;
  status: string;
  decidedBy?: string | null;
  reason?: string | null;
  supersedesId?: string | null;
  signatureCount: number;
  requiredApprovals: number;
}

/** One audited lifecycle step of a request. */
export interface ApprovalTrailEntry {
  at: string;
  actor: string;
  action: string;
  detail: string;
}

/** One collected quorum signature. */
export interface ApprovalSignature {
  requestId: string;
  signedBy: string;
  role: string;
  at: string;
}

/** A request's full story: the row plus its trail and signatures. */
export interface ApprovalRequestDetail {
  request: ApprovalRequestInfo;
  trail: ApprovalTrailEntry[];
  signatures: ApprovalSignature[];
}

/** One declared file rail with its live counts (the file-exchange landing view). */
export interface FileRailInfo {
  name: string;
  headerLines: number;
  filesArchived: number;
  quarantineDepth: number;
  lastArchivedAt?: string | null;
}

/** One file's run outcome: rows applied, rows waiting in quarantine, the archive mark. */
export interface FileInfo {
  rail: string;
  file: string;
  processedRows: number;
  quarantinedRows: number;
  archived: boolean;
  archivedAt?: string | null;
}

/** One quarantined row with its reason and when it FIRST quarantined (age survives reprocess). */
export interface FileQuarantineInfo {
  rail: string;
  file: string;
  line: number;
  reason: string;
  quarantinedAt: string;
}

/**
 * One notification as the ADMIN surface returns it. The recipient arrives MASKED from the
 * server (first character + domain hint); the console never has the full address and must
 * never present this field as if it were one.
 */
export interface NotificationInfo {
  id: string;
  dedupKey: string;
  template: string;
  templateHash: string;
  channel: string;
  maskedRecipient: string;
  culture: string;
  state: string;
  attempts: number;
  detail?: string | null;
  requestedAt: string;
  notBefore?: string | null;
  claimedAt?: string | null;
  sentAt?: string | null;
  failedAt?: string | null;
  bodyDeletedAt?: string | null;
  tenant?: string | null;
  correlationId?: string | null;
}

/** One archive definition with the numbers that decide whether it is healthy. */
export interface ArchiveDefinitionStatus {
  name: string;
  entries: number;
  dueBacklog: number;
  activeHolds: number;
  chainHead: number;
  purgedThrough: number;
}

/** One archived entry: the document plus the tamper-evidence around it. */
export interface ArchiveEntry {
  id: number;
  definition: string;
  aggregateKey: string;
  tenant?: string | null;
  document: string;
  schemaVersion: number;
  dueAt: string;
  archivedAt: string;
  chainIndex: number;
  contentHash: string;
  chainHash: string;
  previousHash: string;
  erasedAt?: string | null;
  preErasureContentHash?: string | null;
}

/** A legal hold — the row IS its own audit. */
export interface LegalHold {
  id: number;
  definition: string;
  aggregateKey: string;
  caseReference: string;
  placedBy: string;
  placedAt: string;
  liftedBy?: string | null;
  liftedAt?: string | null;
}

/** An erasure record — the row IS the answer to the subject's request. */
export interface ErasureRecord {
  id: number;
  subjectKey: string;
  requestedBy: string;
  requestedAt: string;
  entriesAffected: number;
  detail?: string | null;
}

/** One thing the chain verifier found wrong. An empty list is the good news. */
export interface ChainFinding {
  definition: string;
  chainIndex: number;
  aggregateKey: string;
  problem: string;
}

export interface AdminResult {
  ok: boolean;
  message: string;
}

/** Thrown for transport/status failures the caller must surface, never swallow. */
export class AdminHttpError extends Error {
  constructor(readonly status: number, readonly route: string) {
    super(`${route} answered ${status}`);
    this.name = "AdminHttpError";
  }
}

export class AdminClient {
  private readonly baseUrl: string;
  private readonly fetcher: typeof fetch;

  constructor(options: AdminClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? "").replace(/\/$/, "");
    // BIND the default: a bare `fetch` stored on an object and called as `this.fetcher(...)`
    // loses its receiver and throws "Illegal invocation" in a real browser — which the
    // discovery catch would have silently turned into "no capabilities" (found by the U2
    // Playwright gate; jsdom does not reproduce it, so the unit tests were green).
    this.fetcher = options.fetcher ?? ((input, init) => globalThis.fetch(input, init));
  }

  private async get<T>(route: string): Promise<T> {
    const response = await this.fetcher(`${this.baseUrl}${route}`, {
      headers: { accept: "application/json" },
      credentials: "include",
    });
    if (!response.ok) throw new AdminHttpError(response.status, route);
    return (await response.json()) as T;
  }

  /**
   * Capability discovery: probe each module's list root ONCE.
   *
   * - 404 → `absent`: the module was never composed into this app.
   * - 401/403 → `forbidden`: it exists, this operator may not see it.
   * - 400 → `refused`: it exists and answered, but the request could not be SCOPED — a
   *   multi-tenant app refuses an admin call that carries no ambient tenant (contract
   *   revision R1). Reading that as "absent" would tell the operator the module is not
   *   composed, which is a lie the console must never tell.
   * - anything else non-ok → `absent`, honestly: nothing usable answered.
   * - a call that THREW → `unreachable`: down, blocked, or cross-origin without CORS.
   */
  async discoverCapabilities(): Promise<Record<ModuleName, Capability>> {
    const entries = await Promise.all(
      MODULES.map(async (module) => {
        try {
          const response = await this.fetcher(`${this.baseUrl}${PROBE[module]}`, {
            headers: { accept: "application/json" },
            credentials: "include",
          });
          if (response.status === 404) return [module, { kind: "absent" } as Capability] as const;
          if (response.status === 401 || response.status === 403) {
            return [module, { kind: "forbidden", message: await refusalMessage(response) } as Capability] as const;
          }

          if (response.status === 400) {
            return [module, { kind: "refused", message: await refusalMessage(response) } as Capability] as const;
          }

          return [module, { kind: response.ok ? "present" : "absent" } as Capability] as const;
        } catch {
          return [module, { kind: "unreachable" } as Capability] as const;   // asked, never answered
        }
      }),
    );
    return Object.fromEntries(entries) as Record<ModuleName, Capability>;
  }

  fleets(): Promise<FleetInfo[]> {
    return this.get<FleetInfo[]>("/goldpath/admin/jobs/fleets");
  }

  jobs(fleet: string): Promise<JobInfo[]> {
    return this.get<JobInfo[]>(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs`);
  }

  /** The fleet's own state (R2.1) — absent fleets 404, which surfaces as an error. */
  fleetStatus(fleet: string): Promise<FleetStatus> {
    return this.get<FleetStatus>(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/status`);
  }

  /**
   * Runs, filtered and keyset-paged (R2.4). `afterId` names the last row the caller saw;
   * the server continues strictly after it, so a run inserted at the head mid-walk cannot
   * shift the page under the reader.
   */
  runs(
    fleet: string,
    options: { job?: string; take?: number; status?: string[]; from?: string; to?: string; afterId?: string } = {},
  ): Promise<RunSummary[]> {
    const query = new URLSearchParams();
    if (options.job) query.set("job", options.job);
    for (const status of options.status ?? []) query.append("status", status);
    if (options.from) query.set("from", options.from);
    if (options.to) query.set("to", options.to);
    if (options.afterId) query.set("afterId", options.afterId);
    // The contract clamps take to [1,500]; the console never asks for more.
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<RunSummary[]>(
      `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/runs?${query.toString()}`,
    );
  }

  /** Calendars of a fleet (frozen route — first screened in U5). */
  calendars(fleet: string): Promise<CalendarInfo[]> {
    return this.get<CalendarInfo[]>(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/calendars`);
  }

  /** The admin crossings of this service, newest first (frozen route). */
  jobsAudit(take = 100): Promise<AdminAuditRow[]> {
    return this.get<AdminAuditRow[]>(`/goldpath/admin/jobs/audit?take=${Math.min(500, Math.max(1, take))}`);
  }

  run(runId: string): Promise<RunDetail> {
    return this.get<RunDetail>(`/goldpath/admin/jobs/runs/${encodeURIComponent(runId)}`);
  }

  /** Every mutating verb answers the frozen envelope — 200 ok, 400 refusal, both typed. */
  verb(route: string, body?: unknown): Promise<AdminResult> {
    return this.send("POST", route, body);
  }

  /**
   * The envelope is the same whichever method carries it: verbs are POSTs, but the
   * contract reserves PUT and DELETE for true upserts and removals (calendars, triggers),
   * and those answer `GoldpathAdminResult` exactly like a POST does.
   */
  async send(method: "POST" | "PUT" | "DELETE", route: string, body?: unknown): Promise<AdminResult> {
    const response = await this.fetcher(`${this.baseUrl}${route}`, {
      method,
      credentials: "include",
      headers: body === undefined ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (response.status === 200 || response.status === 400) {
      return (await response.json()) as AdminResult;
    }

    throw new AdminHttpError(response.status, route);
  }

  triggerJob(fleet: string, job: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/trigger`);
  }

  pauseJob(fleet: string, job: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/pause`);
  }

  resumeJob(fleet: string, job: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/resume`);
  }

  /**
   * Fleet-wide stop and go. This is the verb an operator reaches for at 03:00, and until
   * U5 the console had no way to send it (open-threads T13). It is durable and
   * cluster-wide — every trigger in the store, not just this node's.
   */
  pauseFleet(fleet: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/pause-all`);
  }

  resumeFleet(fleet: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/resume-all`);
  }

  /** The audited schedule override (frozen D7 verb): the DEFINITION stays in code. */
  reschedule(fleet: string, job: string, cron: string, timeZoneId?: string | null): Promise<AdminResult> {
    return this.verb(
      `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/reschedule`,
      { cron, timeZoneId: timeZoneId ?? null },
    );
  }

  /** Adds a trigger to a DECLARED job (R2.5) — it cannot create a job. */
  addTrigger(fleet: string, job: string, request: AddTriggerRequest): Promise<AdminResult> {
    return this.verb(
      `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/triggers`,
      request,
    );
  }

  /** Removes one trigger; the JOB is untouched (R2.5). */
  removeTrigger(fleet: string, job: string, name: string): Promise<AdminResult> {
    return this.send(
      "DELETE",
      `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/jobs/${encodeURIComponent(job)}/triggers/${encodeURIComponent(name)}`,
    );
  }

  /** Creates or replaces a calendar (frozen PUT route). */
  putCalendar(fleet: string, name: string, spec: CalendarSpec): Promise<AdminResult> {
    return this.send("PUT", `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/calendars/${encodeURIComponent(name)}`, spec);
  }

  /** Deletes a calendar (frozen DELETE route). */
  deleteCalendar(fleet: string, name: string): Promise<AdminResult> {
    return this.send("DELETE", `/goldpath/admin/jobs/fleets/${encodeURIComponent(fleet)}/calendars/${encodeURIComponent(name)}`);
  }

  rerun(runId: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/runs/${encodeURIComponent(runId)}/rerun`);
  }

  /**
   * Redrives EVERY open repair item of the run — the frozen verb takes no body and
   * scopes itself server-side (`RedrivenAt == null`). The console must not pretend to
   * select items it cannot select (review R1 on the U2 slice-1 PR).
   */
  replayItems(runId: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/jobs/runs/${encodeURIComponent(runId)}/replay-items`);
  }

  bulkDefinitions(): Promise<BulkDefinitionStatus[]> {
    return this.get<BulkDefinitionStatus[]>("/goldpath/admin/bulk/definitions");
  }

  /**
   * The batch list. State and definition filter on the SERVER (#72 closed the definition
   * gap) — the panel never narrows the take-bounded page client-side, which would read as
   * "no batches" while plenty exist past the page. R3: repeated values OR within a
   * filter, the filters AND together.
   */
  bulkBatches(options: { state?: string[]; definition?: string[]; take?: number } = {}): Promise<BulkBatchInfo[]> {
    const query = new URLSearchParams();
    for (const state of options.state ?? []) query.append("state", state);
    for (const definition of options.definition ?? []) query.append("definition", definition);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<BulkBatchInfo[]>(`/goldpath/admin/bulk/batches?${query.toString()}`);
  }

  /** The VALIDATION report — keyset-paged by row number (the contract's own cursor). */
  bulkErrors(batchId: string, options: { afterRow?: number; take?: number } = {}): Promise<BulkRowError[]> {
    const query = new URLSearchParams();
    query.set("afterRow", String(options.afterRow ?? 0));
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 100))));
    return this.get<BulkRowError[]>(
      `/goldpath/admin/bulk/batches/${encodeURIComponent(batchId)}/errors?${query.toString()}`,
    );
  }

  approveBatch(batchId: string, note?: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/bulk/batches/${encodeURIComponent(batchId)}/approve`, { note: note ?? null });
  }

  rejectBatch(batchId: string, note: string): Promise<AdminResult> {
    // The contract makes the note MANDATORY on reject — the refusal must teach later readers.
    return this.verb(`/goldpath/admin/bulk/batches/${encodeURIComponent(batchId)}/reject`, { note });
  }

  /**
   * Intake upload. The contract takes a RAW body on purpose (`curl --data-binary @file.csv`)
   * — no multipart, no antiforgery coupling — so the console posts the file bytes as-is.
   */
  async uploadBatch(definition: string, file: File, tenant?: string): Promise<BulkBatchInfo> {
    const query = new URLSearchParams({ fileName: file.name });
    if (tenant) query.set("tenant", tenant);
    const route = `/goldpath/admin/bulk/batches/${encodeURIComponent(definition)}?${query.toString()}`;
    const response = await this.fetcher(`${this.baseUrl}${route}`, {
      method: "POST",
      credentials: "include",
      headers: { accept: "application/json", "content-type": "application/octet-stream" },
      body: file,
    });
    if (!response.ok) throw new AdminHttpError(response.status, route);
    return (await response.json()) as BulkBatchInfo;
  }

  batch(batchId: string): Promise<BulkBatchInfo> {
    return this.get<BulkBatchInfo>(`/goldpath/admin/bulk/batches/${encodeURIComponent(batchId)}`);
  }

  campaigns(options: { state?: string[]; take?: number } = {}): Promise<CampaignInfo[]> {
    const query = new URLSearchParams();
    for (const state of options.state ?? []) query.append("state", state);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<CampaignInfo[]>(`/goldpath/admin/campaign/?${query.toString()}`);
  }

  campaign(id: string): Promise<CampaignInfo> {
    return this.get<CampaignInfo>(`/goldpath/admin/campaign/${encodeURIComponent(id)}`);
  }

  /** Execution failures — one noun across modules (bulk's `/errors` is validation). */
  campaignFailures(id: string, take = 100): Promise<CampaignFailedItem[]> {
    return this.get<CampaignFailedItem[]>(
      `/goldpath/admin/campaign/${encodeURIComponent(id)}/failures?take=${Math.min(500, Math.max(1, take))}`,
    );
  }

  campaignAudit(id: string, take = 100): Promise<CampaignAuditEntry[]> {
    return this.get<CampaignAuditEntry[]>(
      `/goldpath/admin/campaign/${encodeURIComponent(id)}/audit?take=${Math.min(500, Math.max(1, take))}`,
    );
  }

  pauseCampaign(id: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/campaign/${encodeURIComponent(id)}/pause`, {});
  }

  resumeCampaign(id: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/campaign/${encodeURIComponent(id)}/resume`, {});
  }

  abortCampaign(id: string, reason: string): Promise<AdminResult> {
    // The reason is the evidence — the contract binds the body, so it is never optional.
    return this.verb(`/goldpath/admin/campaign/${encodeURIComponent(id)}/abort`, { reason });
  }

  throttleCampaign(id: string, patch: CampaignThrottle): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/campaign/${encodeURIComponent(id)}/throttle`, patch);
  }

  /** Recent approval requests, newest first (R3: repeated ?status=/?ladder= OR within, AND across). */
  approvals(options: { status?: string[]; ladder?: string[]; take?: number } = {}): Promise<ApprovalRequestInfo[]> {
    const query = new URLSearchParams();
    for (const value of options.status ?? []) query.append("status", value);
    for (const value of options.ladder ?? []) query.append("ladder", value);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<ApprovalRequestInfo[]>(`/goldpath/admin/approvals/requests?${query.toString()}`);
  }

  /** One request's full story: the row, its trail, and the collected signatures. */
  approval(id: string): Promise<ApprovalRequestDetail> {
    return this.get<ApprovalRequestDetail>(`/goldpath/admin/approvals/requests/${encodeURIComponent(id)}`);
  }

  /** A grant under the ENGINE's rules — the console is one more decider, not a bypass. */
  approveApproval(id: string, role: string, reason?: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/approvals/requests/${encodeURIComponent(id)}/approve`, { role, reason });
  }

  /** A rejection — the reason is mandatory and the ENGINE enforces it, not this client. */
  rejectApproval(id: string, role: string, reason: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/approvals/requests/${encodeURIComponent(id)}/reject`, { role, reason });
  }

  /** Every declared rail with its counts — the probe root, so an app with rails and no files still answers. */
  fileRails(): Promise<FileRailInfo[]> {
    return this.get<FileRailInfo[]>("/goldpath/admin/fileexchange/rails");
  }

  /** Recent files, newest archive first (R3: repeated ?rail= OR within the filter). */
  files(options: { rail?: string[]; take?: number } = {}): Promise<FileInfo[]> {
    const query = new URLSearchParams();
    for (const value of options.rail ?? []) query.append("rail", value);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<FileInfo[]>(`/goldpath/admin/fileexchange/files?${query.toString()}`);
  }

  /** The quarantine, oldest first (R3: ?rail= and ?file= repeat — OR within, AND across). File names ride the query, never the path. */
  fileQuarantine(options: { rail?: string[]; file?: string[]; take?: number } = {}): Promise<FileQuarantineInfo[]> {
    const query = new URLSearchParams();
    for (const value of options.rail ?? []) query.append("rail", value);
    for (const value of options.file ?? []) query.append("file", value);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 200))));
    return this.get<FileQuarantineInfo[]>(`/goldpath/admin/fileexchange/quarantine?${query.toString()}`);
  }

  notificationTemplates(): Promise<NotificationTemplateStatus[]> {
    return this.get<NotificationTemplateStatus[]>("/goldpath/admin/notification/templates");
  }

  notifications(options: { state?: string[]; template?: string[]; take?: number } = {}): Promise<NotificationInfo[]> {
    const query = new URLSearchParams();
    for (const state of options.state ?? []) query.append("state", state);
    for (const template of options.template ?? []) query.append("template", template);
    query.set("take", String(Math.min(500, Math.max(1, options.take ?? 50))));
    return this.get<NotificationInfo[]>(`/goldpath/admin/notification/notifications?${query.toString()}`);
  }

  notification(id: string): Promise<NotificationInfo> {
    return this.get<NotificationInfo>(`/goldpath/admin/notification/notifications/${encodeURIComponent(id)}`);
  }

  /** The two focused lists the contract exposes in their own right. */
  notificationSuppressions(take = 100): Promise<NotificationInfo[]> {
    return this.get<NotificationInfo[]>(`/goldpath/admin/notification/suppressions?take=${Math.min(500, Math.max(1, take))}`);
  }

  notificationFailures(take = 100): Promise<NotificationInfo[]> {
    return this.get<NotificationInfo[]>(`/goldpath/admin/notification/failures?take=${Math.min(500, Math.max(1, take))}`);
  }

  archiveDefinitions(): Promise<ArchiveDefinitionStatus[]> {
    return this.get<ArchiveDefinitionStatus[]>("/goldpath/admin/archival/definitions");
  }

  /** Retrieval is by (definition, key) — the archive has no browse route by design. */
  async archiveEntry(definition: string, key: string): Promise<ArchiveEntry | null> {
    const route = `/goldpath/admin/archival/entries/${encodeURIComponent(definition)}/${encodeURIComponent(key)}`;
    const response = await this.fetcher(`${this.baseUrl}${route}`, {
      headers: { accept: "application/json" },
      credentials: "include",
    });
    if (response.status === 404) return null;   // "no such entry" is an answer, not a failure
    if (!response.ok) throw new AdminHttpError(response.status, route);
    return (await response.json()) as ArchiveEntry;
  }

  holds(includeLifted = false, take = 100): Promise<LegalHold[]> {
    const query = new URLSearchParams({ includeLifted: String(includeLifted), take: String(Math.min(500, Math.max(1, take))) });
    return this.get<LegalHold[]>(`/goldpath/admin/archival/holds?${query.toString()}`);
  }

  erasures(take = 100): Promise<ErasureRecord[]> {
    return this.get<ErasureRecord[]>(`/goldpath/admin/archival/erasures?take=${Math.min(500, Math.max(1, take))}`);
  }

  placeHold(definition: string, key: string, caseReference: string): Promise<AdminResult> {
    // The case reference is mandatory: a hold that cannot be justified later is not a hold.
    return this.verb(`/goldpath/admin/archival/entries/${encodeURIComponent(definition)}/${encodeURIComponent(key)}/hold`, { caseReference });
  }

  liftHold(definition: string, key: string): Promise<AdminResult> {
    return this.verb(`/goldpath/admin/archival/entries/${encodeURIComponent(definition)}/${encodeURIComponent(key)}/lift-hold`, {});
  }

  erase(definition: string, key: string, subjectKey: string, detail?: string): Promise<AdminResult> {
    return this.verb(
      `/goldpath/admin/archival/entries/${encodeURIComponent(definition)}/${encodeURIComponent(key)}/erase`,
      { subjectKey, detail: detail ?? null },
    );
  }

  /**
   * Chain verification. This POST does NOT answer the verb envelope — it returns the
   * FINDINGS themselves, and an empty list is the good news.
   */
  async verifyChain(definition: string): Promise<ChainFinding[]> {
    const route = `/goldpath/admin/archival/definitions/${encodeURIComponent(definition)}/verify`;
    const response = await this.fetcher(`${this.baseUrl}${route}`, {
      method: "POST",
      credentials: "include",
      headers: { accept: "application/json" },
    });
    if (!response.ok) throw new AdminHttpError(response.status, route);
    return (await response.json()) as ChainFinding[];
  }
}
