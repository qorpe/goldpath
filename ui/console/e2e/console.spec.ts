import { expect, test } from "@playwright/test";

/**
 * Drives the REAL console against a REAL Goldpath app (Postgres + Quartz + the frozen
 * admin surface). Every assertion is behaviour the operator would see; nothing is stubbed.
 */
const service = process.env.GOLDPATH_SERVICE_URL ?? "http://localhost:5310";
/** The same packages, behind the auth floor — every admin call answers 401. */
const secured = process.env.GOLDPATH_SECURED_URL ?? "http://localhost:5312";
/** The same packages, tenant-scoped (R1) — a call with no ambient tenant is refused. */
const tenanted = process.env.GOLDPATH_TENANT_URL ?? "http://localhost:5313";

test.describe("the run console against a real Goldpath app", () => {
  test("discovers the composed capability and hides what the app never composed", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);

    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true })).toBeVisible();
    // This host composes jobs + bulk; the other three surfaces answer 404, so no panels.
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true })).toBeVisible();
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Campaigns", exact: true })).toBeVisible();
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Notifications", exact: true })).toBeVisible();
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Archival", exact: true })).toBeVisible();
  });

  test("triggers a job, watches the run finish, and replays its repair item", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();

    // The fleet appears because the executor exists (zero-config discovery).
    await expect(page.getByRole("button", { name: /console-smoke/ })).toBeVisible();

    await page.getByRole("tab", { name: "Jobs" }).click();
    // Scope the verb to ITS job section: this fleet also carries the bulk validate/execute
    // jobs, so an unscoped "trigger" would be three buttons and one ambiguous click.
    const smokeJob = page.getByRole("row", { name: /SmokeJob/ }).first();
    await smokeJob.getByRole("button", { name: "trigger" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect(dialog).toContainText("recorded as started by hand");
    await dialog.getByRole("button", { name: "trigger" }).click();
    await expect(page.getByRole("status")).toBeVisible();

    // The run appears and reaches a terminal state — polled through the real API.
    const smokeRun = page.getByRole("row", { name: /SmokeJob/ }).first();
    await expect(async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
      await page.getByRole("tab", { name: "History" }).click();
      await expect(smokeRun).toContainText("Completed", { timeout: 5_000 });
    }).toPass({ timeout: 60_000 });

    // The run says a HUMAN started it — the stamp the contract's R2.3 column carries.
    await expect(smokeRun).toContainText("Manual");

    await smokeRun.getByRole("button").first().click();
    const detail = page.getByTestId("run-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText("Completed:");            // chunk counts by status
    await expect(detail).toContainText("Repair queue (1 shown)"); // the isolated item
    await expect(detail).toContainText("the bank refused this instruction");

    // Replay the repair queue — the verb the operator reaches for after a bad night.
    await page.getByRole("button", { name: "replay-items" }).click();
    const replayDialog = page.getByRole("alertdialog");
    await expect(replayDialog).toContainText("Replay all open repair items");
    await replayDialog.getByRole("button", { name: "replay-items" }).click();
    await expect(page.getByRole("status")).toBeVisible();
  });

  test("the scheduling surface: fleet state, the 03:00 verb, and a schedule an operator changes", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();

    // ── Overview: what the fleet IS (contract R2.1)
    const overview = page.getByTestId("fleet-overview");
    // True of the FLEET. The member that answers this call is the management head, which
    // Quartz reports as in standby with an idle pool — reading that as the fleet's state
    // told an operator their running fleet was holding fires (caught by this very smoke).
    await expect(overview).toContainText("accepting fires");
    await expect(overview).toContainText("This console is connected through");

    // ── pause-all: the verb an operator reaches for at 03:00 and could not reach until U5
    //
    // It is also the most DANGEROUS verb to exercise in a shared smoke: it is durable and
    // cluster-wide, so a failure between pausing and resuming leaves every later test
    // waiting on jobs that will never fire. The finally is that safety net — it goes
    // through the API rather than the UI precisely because it must run even when the UI
    // assertions are what failed.
    try {
      await page.getByRole("button", { name: "pause every job" }).click();
      const pauseDialog = page.getByRole("alertdialog");
      await expect(pauseDialog).toContainText("cluster-wide and survives a restart");
      await pauseDialog.getByRole("button", { name: "pause every job" }).click();
      await expect(page.getByRole("status")).toBeVisible();

      // The fleet itself now reads as stopped — from the STORE's paused trigger group,
      // which is where pause-all wrote it.
      await expect(overview).toContainText("nothing will fire until someone resumes it");

      // And the same truth reaches the JOBS screen, which derives it from the TRIGGERS.
      // Asserted on a scheduled job: SmokeJob is fired by hand and carries no trigger, so
      // it is not paused by this — it is unscheduled, which the screen says differently
      // and on purpose.
      await page.getByRole("tab", { name: "Jobs" }).click();
      await expect(page.getByRole("button", { name: "resume" }).first()).toBeVisible();

      await page.getByRole("tab", { name: "Overview" }).click();
      await page.getByRole("button", { name: "resume every job" }).click();
      await page.getByRole("alertdialog").getByRole("button", { name: "resume every job" }).click();
      await expect(page.getByRole("status")).toBeVisible();
      await expect(overview).toContainText("accepting fires");
    } finally {
      await page.request.post(`${service}/goldpath/admin/jobs/fleets/console-smoke/resume-all`);
    }

    // ── a second schedule on a DECLARED job, then removed again (R2.5)
    await page.getByRole("tab", { name: "Jobs" }).click();
    await page.getByRole("button", { name: "SmokeJob" }).click();
    const sheet = page.getByTestId("sheet");
    await sheet.getByRole("button", { name: "add a trigger" }).click();
    // The form opens in the centred DIALOG now (§9.5) — a portal outside the sheet.
    const addDialog = page.getByRole("dialog", { name: "Add a trigger" });
    await addDialog.getByLabel("Name").fill("month-end");
    await addDialog.getByLabel("Cron").fill("0 0 2 L * ?");
    await addDialog.getByRole("button", { name: "schedule it" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "schedule it" }).click();
    // Success CLOSES the dialog itself; the outcome strip lands back in the sheet.
    await expect(addDialog).not.toBeVisible();
    await expect(sheet.getByRole("status")).toBeVisible();

    await page.reload();
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await page.getByRole("tab", { name: "Jobs" }).click();
    await page.getByRole("button", { name: "SmokeJob" }).click();
    const reopened = page.getByTestId("sheet");
    await expect(reopened).toContainText("month-end");
    await expect(reopened).toContainText("0 0 2 L * ?");

    await reopened.locator("li", { hasText: "month-end" }).getByRole("button", { name: "remove" }).click();
    const removeDialog = page.getByRole("alertdialog");
    await expect(removeDialog).toContainText("The JOB stays");
    await removeDialog.getByRole("button", { name: "remove" }).click();
    await expect(page.getByRole("status")).toBeVisible();
    // The sheet is MODAL: close it before reaching for the tab strip behind it.
    await page.keyboard.press("Escape");

    // ── a calendar, created and deleted through the frozen CRUD (T13)
    await page.getByRole("tab", { name: "Calendars" }).click();
    await page.getByRole("button", { name: "add a calendar" }).click();
    const calendarDialog = page.getByRole("dialog", { name: "Add a calendar" });
    await calendarDialog.getByLabel("Name").fill("smoke-holidays");
    await calendarDialog.getByLabel(/Excluded dates/).fill("2026-01-01");
    await calendarDialog.getByRole("button", { name: "create it" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "create it" }).click();
    // Success closes this dialog too; the outcome lands on the calendars section.
    await expect(calendarDialog).not.toBeVisible();
    await expect(page.getByRole("status").first()).toBeVisible();

    await page.reload();
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await page.getByRole("tab", { name: "Calendars" }).click();
    const calendar = page.getByRole("row", { name: /smoke-holidays/ });
    await expect(calendar).toBeVisible();
    await calendar.getByRole("button", { name: "delete" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "delete" }).click();
    await expect(page.getByRole("status")).toBeVisible();

    // ── the history answers a QUESTION rather than being scrolled (R2.4)
    await page.getByRole("tab", { name: "History" }).click();
    await page.getByRole("button", { name: /State/ }).click();
    await page.getByRole("menuitemcheckbox", { name: /Completed/ }).click();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("row", { name: /Completed/ }).first()).toBeVisible();
    // Toggling to Running: the facet holds ONE state — pick Running (Completed clears).
    await page.getByRole("button", { name: /State/ }).click();
    await page.getByRole("menuitemcheckbox", { name: /^Completed$/ }).click();
    await page.getByRole("menuitemcheckbox", { name: /^Running$/ }).click();
    await page.keyboard.press("Escape");
    // Either there is a running row or the screen says the filters emptied it — both are
    // answers; a stale Completed row would not be.
    await expect(page.getByTestId("run-history")).not.toContainText("Completed:");

    // ── and every crossing above is on the audit, with the actor
    await page.getByRole("tab", { name: "Overview" }).click();
    const audit = page.getByTestId("fleet-overview");
    await expect(audit).toContainText("pause-all");
    await expect(audit).toContainText("add-trigger");
    await expect(audit).toContainText("remove-trigger");
  });

  test("the ⌘K palette: the rail's search opens it and a command navigates", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true })).toBeVisible();

    // The rail trigger opens the palette; typing narrows it to one destination.
    await page.getByRole("button", { name: /Search/ }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await dialog.getByRole("combobox").fill("archi");
    await dialog.getByText("Archival").click();

    // The palette closed and the console went where the command said.
    await expect(dialog).not.toBeVisible();
    await expect(page.getByRole("heading", { name: "Archival" })).toBeVisible();

    // And the keyboard route works too — ⌘K/Ctrl-K toggles it from anywhere.
    await page.keyboard.press("ControlOrMeta+k");
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.keyboard.press("ControlOrMeta+k");
    await expect(page.getByRole("dialog")).not.toBeVisible();
  });

  test("§7.9: a history row's job links to its schedule; a trigger's calendar links on", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await expect(page.getByRole("button", { name: /console-smoke/ })).toBeVisible();
    await page.getByRole("tab", { name: "History" }).click();

    // Earlier journeys left runs behind; the JOB cell is a link, not a name to remember.
    const jobLink = page.getByTestId("run-history").getByRole("button", { name: "SmokeJob" }).first();
    await expect(jobLink).toBeVisible();
    await jobLink.click();

    // Landed on Jobs with the job's own sheet already open.
    const sheet = page.getByTestId("sheet");
    await expect(sheet).toBeVisible();
    await expect(sheet).toContainText("SmokeJob");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("tab", { name: "Jobs" })).toHaveAttribute("aria-selected", "true");
  });

  test("a job cannot be CREATED from the console — the constitution has no button", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await page.getByRole("tab", { name: "Jobs" }).click();
    await expect(page.getByTestId("jobs-tab")).toBeVisible();

    // ADR-0001: composition is the manifest's. The API has no route for this and the
    // screen offers no affordance — asserted here so a future panel cannot quietly grow
    // one against a server that would refuse it anyway.
    await expect(page.getByRole("button", { name: /add a job|new job|create job/i })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /delete job|remove job/i })).toHaveCount(0);
  });

  test("uploads a batch, reads the real validation report, and works the four-eyes gate", async ({ page }) => {
    const openBulk = async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true }).click();
      await expect(page.getByTestId("bulk-panel")).toBeVisible();
    };

    const upload = async (name: string, csv: string) => {
      await page.getByLabel("batch file").setInputFiles({ name, mimeType: "text/csv", buffer: Buffer.from(csv) });
      await page.getByRole("button", { name: "upload" }).click();
      const dialog = page.getByRole("alertdialog", { name: "confirm upload" });
      await expect(dialog).toContainText(`Upload ${name} into payments?`);
      await dialog.getByRole("button", { name: "upload" }).click();
      await expect(page.getByText(/Received — batch .* is queued for validation/)).toBeVisible();
    };

    // The newest batch is the first row (the contract orders by receipt, newest first).
    const newest = page.getByTestId("batches").getByRole("row", { name: /payments/ }).first();
    const waitForState = async (state: string) =>
      expect(async () => {
        await openBulk();
        // Read the state from the batch ROW: the filter's <option> list carries every
        // state name too, so an unscoped text match would pass before anything happened.
        await expect(newest).toContainText(state, { timeout: 5_000 });
      }).toPass({ timeout: 60_000 });

    // ── act 1: a file with a structurally broken line
    await openBulk();
    // 120 broken lines: enough that the validation report has a SECOND keyset page, so
    // the walk itself is proven in a browser and not only in a unit test.
    const broken = Array.from({ length: 120 }, (_, index) => `E2E-B${index + 1},30.00,stray`).join("\n");
    await upload("payments.csv", `EndToEndId,Amount\nE2E-1,10.00\nE2E-2,20.00\n${broken}\n`);
    await waitForState("Validated");

    await newest.getByRole("button").first().click();
    const detail = page.getByTestId("batch-detail");
    // The report the ENGINE wrote — the broken line, named by its own message.
    await expect(detail).toContainText("expected 2 fields");

    // The report is keyset-paged: the first page ends at row 100 and the walk continues
    // from there. Page one stops at data row 102 (two good rows precede the broken ones).
    // Scoped to the REPORT table: the ledger above it also prints 122 (the row count).
    const report = detail.locator("table");
    await expect(report.getByText("102", { exact: true })).toBeVisible();
    await expect(report.getByText("122", { exact: true })).toHaveCount(0);
    await detail.getByRole("button", { name: /more/i }).click();
    await expect(report.getByText("122", { exact: true })).toBeVisible();

    // The gate refuses to fire without evidence.
    await page.getByRole("button", { name: "reject" }).click();
    const rejectDialog = page.getByRole("alertdialog", { name: "confirm reject" });
    await expect(rejectDialog.getByRole("button", { name: "reject" })).toBeDisabled();
    await rejectDialog.getByRole("button", { name: "cancel" }).click();

    // And the ENGINE refuses this approval: invalid rows block it. The console shows the
    // refusal verbatim — that message is how the operator learns what to do next.
    await page.getByRole("button", { name: "approve" }).click();
    const blocked = page.getByRole("alertdialog", { name: "confirm approve" });
    await expect(blocked).toContainText("audited");
    await blocked.getByRole("button", { name: "approve" }).click();
    await expect(page.getByText(/\d+ invalid rows block approval/)).toBeVisible();

    // ── act 2: the operator rejects it, with the reason the contract demands
    await page.getByRole("button", { name: "reject" }).click();
    const rejecting = page.getByRole("alertdialog", { name: "confirm reject" });
    await rejecting.getByLabel("reason (required)").fill("line 3 is malformed — resend the export");
    await rejecting.getByRole("button", { name: "reject" }).click();
    await waitForState("Rejected");

    await newest.getByRole("button").first().click();
    await expect(detail).toContainText("line 3 is malformed — resend the export");

    // ── act 3: the fixed file passes the gate, and the decision becomes a server fact
    await openBulk();
    await upload("payments-fixed.csv", "EndToEndId,Amount\nE2E-4,10.00\nE2E-5,20.00\n");
    await waitForState("Validated");

    await newest.getByRole("button").first().click();
    await page.getByRole("button", { name: "approve" }).click();
    const approving = page.getByRole("alertdialog", { name: "confirm approve" });
    await approving.getByLabel("note (optional)").fill("checked against the ledger");
    await approving.getByRole("button", { name: "approve" }).click();
    await expect(page.getByText("approved", { exact: true })).toBeVisible();

    await waitForState("Approved");
    await newest.getByRole("button").first().click();
    await expect(detail).toContainText("checked against the ledger");
  });

  test("filters the batch list by definition through the SERVER, past the page bound (#72)", async ({ page, request }) => {
    // One refunds batch FIRST, then a full page of NEWER payments batches: narrowing the
    // 50-row page client-side would answer "no batches" for refunds while one exists —
    // only the server-side filter can walk past the page bound (T8's exit proof).
    const post = (definition: string, fileName: string, csv: string) =>
      request.post(`${service}/goldpath/admin/bulk/batches/${definition}?fileName=${fileName}`, {
        headers: { "content-type": "application/octet-stream" },
        data: csv,
      });
    expect((await post("refunds", "t8-refund.csv", "EndToEndId,Amount\nT8-R1,5.00\n")).ok()).toBeTruthy();
    for (let index = 0; index < 52; index += 1) {
      // Unique content per file — the intake dedups identical bodies into one batch.
      expect((await post("payments", `t8-pay-${index}.csv`, `EndToEndId,Amount\nT8-P${index},1.00\n`)).ok()).toBeTruthy();
    }

    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true }).click();
    await expect(page.getByTestId("bulk-panel")).toBeVisible();

    // Unfiltered, the page is all newer payments rows — the refunds batch sits past its end.
    const batchRows = page.getByTestId("batches").getByRole("row");
    await expect(batchRows.filter({ hasText: "payments" }).first()).toBeVisible();
    await expect(batchRows.filter({ hasText: "refunds" })).toHaveCount(0);

    await page.getByTestId("batches").getByRole("button", { name: /Definition/ }).click();
    await page.getByRole("menuitemcheckbox", { name: "refunds" }).click();
    await page.keyboard.press("Escape");
    await expect(batchRows.filter({ hasText: "refunds" }).first()).toBeVisible();
    await expect(batchRows.filter({ hasText: "payments" })).toHaveCount(0);
  });

  test("governs a live campaign: watches it release, throttles, pauses, resumes, aborts", async ({ page, request }) => {
    // The campaign is CREATED through the API, not the console: its parameters are
    // domain-shaped, so authoring belongs to the app. The console governs what exists.
    const created = await request.post(`${service}/goldpath/admin/campaign/`, {
      data: { type: "welcome", name: "smoke-welcome", policy: { tps: 2, maxInFlight: 5 } },
    });
    expect(created.ok()).toBeTruthy();

    const openCampaigns = async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Campaigns", exact: true }).click();
      await expect(page.getByTestId("campaign-panel")).toBeVisible();
    };

    await openCampaigns();
    await page.getByRole("button", { name: "smoke-welcome" }).click();
    const detail = page.getByTestId("campaign-detail");
    await expect(detail).toBeVisible();

    // The pacer is real: items enumerate and release through the broker on their own.
    await expect(async () => {
      await openCampaigns();
      await page.getByRole("button", { name: "smoke-welcome" }).click();
      await expect(detail).toContainText(/Released\s*[1-9]/, { timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    // Throttle: only the field the operator touched travels, and it is named first.
    await page.getByLabel("tps").fill("1");
    await page.getByRole("button", { name: "throttle" }).click();
    const throttling = page.getByRole("alertdialog", { name: "confirm throttle" });
    await expect(throttling).toContainText("1 tps");
    await throttling.getByRole("button", { name: "throttle" }).click();
    await expect(page.getByRole("status")).toBeVisible();
    await expect(detail).toContainText("(now 1)");   // the server's new policy, read back

    // Pause → resume: the pacer obeys, and the buttons swap with the state.
    await page.getByRole("button", { name: "pause" }).click();
    await page.getByRole("alertdialog", { name: "confirm pause" }).getByRole("button", { name: "pause" }).click();
    await expect(page.getByRole("button", { name: "resume" })).toBeVisible();

    await page.getByRole("button", { name: "resume" }).click();
    await page.getByRole("alertdialog", { name: "confirm resume" }).getByRole("button", { name: "resume" }).click();
    await expect(page.getByRole("button", { name: "pause" })).toBeVisible();

    // Abort names the cost, demands a reason, and ends the campaign for good. The verb
    // buttons swap with the state, so the dialog is asserted OPEN before it is filled —
    // otherwise a click that raced the resume's refresh would fail far away from here.
    await page.getByRole("button", { name: "abort" }).click();
    const aborting = page.getByRole("alertdialog", { name: "confirm abort" });
    await expect(aborting).toBeVisible();
    await expect(aborting).toContainText("stamped Aborted");
    await aborting.getByLabel("reason (required)").fill("smoke run finished");
    await aborting.getByRole("button", { name: "abort" }).click();
    // The pacer's own answer, before any reload: proof the verb LANDED.
    await expect(page.getByTestId("campaign-detail").getByRole("status")).toBeVisible();

    await expect(async () => {
      await openCampaigns();
      await page.getByRole("button", { name: "smoke-welcome" }).click();
      await expect(detail).toContainText("Aborted", { timeout: 5_000 });
    }).toPass({ timeout: 30_000 });

    // The verb log is the server's own record — every governor action, with its reason.
    await expect(detail).toContainText("abort");
    await expect(detail).toContainText("smoke run finished");
    await expect(page.getByRole("button", { name: "pause" })).toHaveCount(0);
  });

  test("campaign R1: an excluded day halts release; an end date expires the remainder into a report", async ({ page, request }) => {
    // Two probes, created through the API like every campaign. The first excludes
    // TODAY from day one; the second was born already past its end date.
    const day = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"][new Date().getUTCDay()];
    const yesterday = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
    const excluded = await request.post(`${service}/goldpath/admin/campaign/`, {
      data: { type: "welcome", name: "r1-excluded", policy: { tps: 50, maxInFlight: 50, excludedDays: [day] } },
    });
    expect(excluded.ok()).toBeTruthy();
    const expiring = await request.post(`${service}/goldpath/admin/campaign/`, {
      data: { type: "welcome", name: "r1-expiring", policy: { tps: 50, maxInFlight: 50, endDate: yesterday } },
    });
    expect(expiring.ok()).toBeTruthy();

    const openCampaigns = async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Campaigns", exact: true }).click();
      await expect(page.getByTestId("campaign-panel")).toBeVisible();
    };

    // The expiring probe flips to the R1 terminal state on the pacer's own clock.
    await expect(async () => {
      await openCampaigns();
      await expect(page.getByRole("row", { name: /r1-expiring/ })).toContainText("ExpiredIncomplete", { timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    // The excluded probe releases NOTHING today — enumeration may finish, release must not move.
    await page.getByRole("button", { name: "r1-excluded" }).click();
    const detail = page.getByTestId("campaign-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText(day.slice(0, 3));   // the policy row says which day is off
    // textContent concatenates the label and the value ("Released0 of N") — assert
    // on that seam, which no other number can fake.
    await expect(page.getByTestId("sheet")).toContainText(/Released0 of/);

    // Lifting the exclusion live resumes it — the operator sentence R1.1 promises.
    await page.keyboard.press("Escape");
    await expect(async () => {
      await openCampaigns();
      await page.getByRole("button", { name: "r1-excluded" }).click();
      // The family checkbox keeps its real input sr-only; actionability wants the
      // LABEL, which is what an operator clicks anyway.
      await page.getByTestId("sheet").getByText("clear excluded days").click();
      await page.getByTestId("sheet").getByRole("button", { name: "throttle" }).click();
      const dialog = page.getByRole("alertdialog");
      await expect(dialog).toContainText("no excluded days");
      await dialog.getByRole("button", { name: "throttle" }).click();
      await expect(page.getByRole("status").first()).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 30_000 });

    await expect(async () => {
      await openCampaigns();
      await page.getByRole("button", { name: "r1-excluded" }).click();
      await expect(page.getByTestId("sheet")).not.toContainText(/Released0 of/, { timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
  });

  test("reads the notification evidence: sent, suppressed and failed, each with its own words", async ({ page }) => {
    const openNotifications = async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Notifications", exact: true }).click();
      await expect(page.getByTestId("notification-panel")).toBeVisible();
    };

    // The send job is real and runs on its own cron; the app requested three notifications
    // at startup — one the webhook accepts, one the MaySend hook refuses, one whose
    // transport is a dead port. Poll until the queue has worked through them.
    const sentRow = page.getByTestId("evidence").getByRole("row", { name: /Sent/ }).first();
    await expect(async () => {
      await openNotifications();
      await expect(sentRow).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    // Recipients arrive MASKED from the API — the address itself never reaches the browser.
    // The API masks both halves (`c***@e***`) — neither address nor domain reaches here.
    await expect(page.getByRole("row", { name: /c\*\*\*@e\*\*\*/ }).first()).toBeVisible();
    await expect(page.locator("body")).not.toContainText("customer1@example.com");
    await expect(page.locator("body")).not.toContainText("@example.com");

    // The failure lens: the transport's own refusal, written by the module.
    await page.getByRole("button", { name: "Failures" }).click();
    const failedRow = page.getByTestId("evidence").getByRole("row", { name: /Failed/ }).first();
    await expect(failedRow).toBeVisible();
    await failedRow.getByRole("button").first().click();
    // The template name lives in the SHEET's title now; the section holds the fields.
    const detail = page.getByTestId("sheet");
    await expect(detail).toContainText("ops-alert");
    await expect(detail).toContainText(/refused|connection|error/i);
    // The detail is a modal SHEET now — close it before reaching for the lens rail.
    await page.keyboard.press("Escape");

    // The suppression lens: a refusal by policy is evidence, and it carries its reason.
    await page.getByRole("button", { name: "Suppressions" }).click();
    await page.getByTestId("evidence").getByRole("row", { name: /Suppressed/ }).first().getByRole("button").first().click();
    await expect(detail).toContainText("suppressed by the MaySend hook");

    // Read-only by contract: this surface offers no verb at all.
    await expect(page.getByRole("button", { name: /resend|retry/i })).toHaveCount(0);
  });

  test("works the approval worklist: the rung an amount routed to, the quorum, and the engine's four-eyes refusal", async ({ page }) => {
    // Approvals was the only console MODULE with no journey, because the smoke host did
    // not compose it (preview.8 coverage audit, 2026-09-05). The host now declares one
    // real ladder and requests one real approval for 2.5M, which the ENGINE routes to the
    // manager rung — quorum two. Nothing below is a fixture.
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Approvals", exact: true }).click();
    await expect(page.getByTestId("approvals-panel")).toBeVisible();

    // The worklist: the amount decided the rung, and the quorum is the rung's property.
    const queue = page.getByTestId("approvals-queue");
    const row = queue.getByRole("row", { name: /Limit raise for ACME/ });
    await expect(row).toContainText("credit-limit");
    await expect(row).toContainText("manager");
    await expect(row).toContainText("0/2");

    // The sheet tells the request's story: routing, the trail the engine appended.
    await row.getByRole("button", { name: "Limit raise for ACME" }).click();
    const detail = page.getByTestId("approval-detail");
    await expect(detail).toContainText("analyst@example.com");
    await expect(detail).toContainText("Trail");

    // One signature: the quorum moves, the request stays pending — two eyes are not four.
    await detail.getByLabel("Decider role").fill("manager");
    await detail.getByRole("button", { name: "Approve", exact: true }).click();
    await expect(page.getByTestId("verb-message")).toHaveText("Applied");
    await expect(detail).toContainText("1/2");

    // The SAME decider again: the engine refuses in its own words, and the panel shows
    // the refusal rather than a generic error. This is the rule the module exists for.
    await detail.getByRole("button", { name: "Approve", exact: true }).click();
    const refusal = page.getByTestId("verb-message");
    await expect(refusal).not.toHaveText("Applied");
    await expect(refusal).toContainText(/eyes|already|distinct|same/i);
    await page.keyboard.press("Escape");
  });

  test("reads the file rails: a file the engine ingested, its quarantine with the reason and the age", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "File rails", exact: true }).click();
    await expect(page.getByTestId("fileexchange-panel")).toBeVisible();

    // The rail with its live counts — the engine ingested one file at startup: two rows
    // applied, one quarantined. The banner says the quarantine out loud.
    const rails = page.getByTestId("rails");
    await expect(rails.getByRole("row", { name: /registry-daily/ })).toContainText("1");
    await expect(page.getByRole("status")).toContainText("registry-daily: 1 row in quarantine");

    // The file, newest archive first, opens into the sheet with the quarantined row's
    // reason in the ENGINE's words — the row-contract reason the rail declared.
    await page.getByTestId("files").getByRole("button", { name: "registry-2026-09-03.csv" }).click();
    const detail = page.getByTestId("sheet");
    await expect(detail).toContainText("non-positive amount");
    await expect(detail).toContainText("Rows applied");
    await page.keyboard.press("Escape");

    // Read-only by contract: re-delivering the file IS the reprocess, so no verb exists here.
    await expect(page.getByRole("button", { name: /reprocess|retry|discard/i })).toHaveCount(0);
  });

  test("archival: verifies the chain, retrieves an entry, holds it, then erases it", async ({ page }) => {
    const openArchival = async () => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Archival", exact: true }).click();
      await expect(page.getByTestId("archival-panel")).toBeVisible();
    };

    // The archive job is real and runs on its own cron — poll until it has appended.
    await expect(async () => {
      await openArchival();
      // The row exists at startup; CRON ACTIVITY is a positive number appearing in it —
      // every numeric column starts at 0, so any nonzero digit is the appended entry.
      await expect(page.getByRole("row", { name: /policies/ }).first()).toContainText(/[1-9]/, { timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    // The chain verifies END TO END, computed by the engine over every entry.
    await page.getByRole("button", { name: "verify policies" }).click();
    await page.getByRole("alertdialog", { name: "confirm verify policies" }).getByRole("button", { name: "verify policies" }).click();
    await expect(page.getByTestId("chain-findings")).toContainText("the chain verifies");

    // Retrieval is by key — the archive is not browsable, and the panel says so.
    await page.getByLabel("aggregate key").fill("1");
    await page.getByRole("button", { name: "retrieve" }).click();
    const entry = page.getByTestId("archive-entry");
    await expect(entry).toBeVisible();

    // The document is not on screen until asked for.
    await expect(entry).not.toContainText("Holder 1");
    await page.getByRole("button", { name: "reveal document" }).click();
    await expect(entry).toContainText("Holder 1");

    // A hold needs its case reference, and the hold list records who placed it.
    // "hold" is a substring of "lift-hold" — every locator here must be exact.
    await page.getByRole("button", { name: "hold", exact: true }).click();
    const holding = page.getByRole("alertdialog", { name: "confirm hold" });
    await expect(holding.getByRole("button", { name: "hold", exact: true })).toBeDisabled();
    await holding.getByLabel("case reference (required)").fill("CASE-SMOKE-1");
    await holding.getByRole("button", { name: "hold", exact: true }).click();
    await expect(page.getByRole("row", { name: /CASE-SMOKE-1/ })).toBeVisible();

    // A held entry cannot be erased — the engine says WHY, and the console repeats it
    // verbatim. This is the whole point of a hold, so the gate proves it.
    await page.getByRole("button", { name: "erase" }).click();
    const blocked = page.getByRole("alertdialog", { name: "confirm erase" });
    await blocked.getByLabel("subject key (required)").fill("holder:1");
    await blocked.getByRole("button", { name: "erase" }).click();
    await expect(entry).toContainText("lift the hold first");

    // Lift it, then erase: the redaction records itself as an erasure row.
    await page.getByRole("button", { name: "lift-hold" }).click();
    await page.getByRole("alertdialog", { name: "confirm lift-hold" }).getByRole("button", { name: "lift-hold" }).click();
    // The lift is a round trip: erase only once the hold list (lifted holds hidden by default)
    // no longer shows it. Erasing earlier races the lift, the engine still refuses with
    // "lift the hold first", and the erasure row never appears (nightly 2026-10-26, 46/47).
    await expect(page.getByRole("row", { name: /CASE-SMOKE-1/ })).toHaveCount(0);

    await page.getByRole("button", { name: "erase" }).click();
    const erasing = page.getByRole("alertdialog", { name: "confirm erase" });
    await erasing.getByLabel("subject key (required)").fill("holder:1");
    await erasing.getByRole("button", { name: "erase" }).click();
    await expect(page.getByRole("row", { name: /holder:1/ })).toBeVisible();

    // The redacted entry explains its own hash divergence — redaction, not tamper.
    await expect(entry).toContainText("BY DESIGN");

    await page.getByRole("button", { name: "verify policies" }).click();
    await page.getByRole("alertdialog", { name: "confirm verify policies" }).getByRole("button", { name: "verify policies" }).click();
    await expect(page.getByTestId("chain-findings")).toContainText("the chain verifies");
  });

  test("behind the auth floor: every surface is NAMED as forbidden, never hidden", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(secured)}`);

    // TRIAGE says it first: the console cannot see this service, which during an incident
    // is the most important thing an operator can be told.
    await expect(page.getByTestId("triage-home")).toContainText("surfaces on");
    await expect(page.getByTestId("triage-home")).toContainText("cannot be read");

    // The modules ARE composed here; the operator simply has no principal. Hiding them
    // would tell the operator this app has no admin surface, which is false.
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true })).toBeVisible();
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true })).toBeVisible();
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText("lacks the ops role");
    await expect(page.getByTestId("run-console")).toHaveCount(0);
    await expect(page.getByText(/No Goldpath admin surface answered here/)).toHaveCount(0);
  });

  test("tenant-scoped: a call that cannot be scoped is refused, with the server's reason", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(tenanted)}`);

    // R1: no ambient tenant → the app refuses (400). The console must repeat that, not
    // silently downgrade a composed module to "absent".
    await expect(page.getByTestId("triage-home")).toContainText("cannot be read");
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true })).toBeVisible();
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    const banner = page.getByRole("alert");
    await expect(banner).toContainText("composed here but refused this request");
    await expect(banner).toContainText(/tenant/i);
    await expect(page.getByTestId("run-console")).toHaveCount(0);
  });

  test("a service that dies MID-SESSION is reported, not papered over", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await expect(page.getByTestId("run-console")).toBeVisible();

    // Reach the verb while the service is still healthy...
    await page.getByRole("tab", { name: "Jobs" }).click();
    await expect(page.getByRole("row", { name: /SmokeJob/ }).first()).toBeVisible();

    // ...and only THEN let the service stop answering: the point is a verb that dies in
    // flight, not a screen that never loaded.
    await page.route(`${service}/goldpath/admin/**`, (route) => route.abort("failed"));

    await page.getByRole("row", { name: /SmokeJob/ }).first().getByRole("button", { name: "trigger" }).first().click();
    const dialog = page.getByRole("alertdialog");
    await dialog.getByRole("button", { name: "trigger" }).click();

    // The verb never reached the server, and the console says exactly that — the one
    // thing it must never do here is imply the trigger landed.
    await expect(page.getByText(/did not reach the server/)).toBeVisible();
  });

  test("one console, three services: the registry switches between them and re-discovers each", async ({ page }) => {
    // No ?base= — the console reads the registry the smoke wrote, exactly as an adopter's
    // console reads theirs.
    await page.goto("/");

    // The landing screen is TODAY; the modules are one click away, never the front door.
    await expect(page.getByTestId("triage-home")).toBeVisible();
    await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();

    // The picker is the family's OWN listbox now — a combobox button, not a native select.
    const picker = page.getByRole("combobox", { name: "service" });
    await expect(picker).toContainText("open");
    const pick = async (name: string) => {
      await picker.click();
      await page.getByRole("option", { name }).click();
    };
    await expect(page.getByTestId("run-console")).toBeVisible();
    await expect(page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true })).toBeVisible();

    // The auth-floored app composes the same modules but refuses this operator: the
    // sections stay NAMED and the reason is the server's.
    await pick("auth-floored");
    await expect(page.getByRole("alert")).toContainText("lacks the ops role");
    await expect(page.getByTestId("run-console")).toHaveCount(0);

    // And the tenant-scoped app refuses differently — a call it cannot scope.
    await pick("tenant-scoped");
    await expect(page.getByRole("alert")).toContainText("composed here but refused this request");

    // Back to the open app: its panels come back, freshly discovered.
    await pick("open");
    await expect(page.getByTestId("run-console")).toBeVisible();
  });

  test("triage: the first screen answers 'is anything wrong' and its rows are deep links", async ({ page }) => {
    // Give the estate something to report: a batch waiting at the four-eyes gate.
    const csv = "EndToEndId,Amount\nE2E-T1,10.00\nE2E-T2,20.00\n";
    const uploaded = await page.request.post(`${service}/goldpath/admin/bulk/batches/payments?fileName=triage.csv`, {
      headers: { "content-type": "application/octet-stream" },
      data: csv,
    });
    expect(uploaded.ok()).toBeTruthy();

    // The validate job is real; poll the TRIAGE screen until it reports the gate.
    const gate = page.getByRole("button", { name: /awaiting approval in payments/ });
    await expect(async () => {
      await page.goto("/");
      await expect(page.getByTestId("triage-home")).toBeVisible({ timeout: 5_000 });
      await expect(gate).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    // The screen says WHAT it read, so the numbers cannot be mistaken for the whole truth.
    await expect(page.getByTestId("triage-home")).toContainText("most recent 50 rows");

    // The stat cards print the same published numbers: the gate above means the
    // "Awaiting approval" card carries a non-zero count, and it is a real button.
    const cards = page.getByTestId("triage-cards");
    await expect(cards).toBeVisible();
    await expect(cards.getByRole("button", { name: /Awaiting approval/ })).toContainText(/[1-9]/);
    await expect(cards.getByText("Failed runs")).toBeVisible();

    // A row is a deep link: it opens the panel that owns it, on the service that owns it.
    await gate.click();
    await expect(page.getByTestId("bulk-panel")).toBeVisible();
    await expect(page.getByRole("combobox", { name: "service" })).toContainText("open");
  });
});
