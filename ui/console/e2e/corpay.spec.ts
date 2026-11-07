import { type APIRequestContext, type Browser, type BrowserContext, expect, request as playwrightRequest, test } from "@playwright/test";

/**
 * GA gate G11 (open-threads T1, #325): the console DRIVES the adopter-shaped sample.
 *
 * `scripts/corpay-console-smoke.sh` brings CorPay up on its PUBLISHED train (the API head and the EOD
 * worker, real Postgres, RabbitMQ and Redis) and a pinned Keycloak with a committed realm. Nothing
 * here is a purpose-built host: every page is the console CorPay itself serves, from the package's
 * embedded assets, behind CorPay's own auth floor.
 *
 * Identity (owner decision 2026-11-04): a token per actor from Keycloak's token endpoint (direct
 * grant), carried as `Authorization: Bearer …` on every request of that actor's browser context —
 * the console RFC's "a gateway's header" case. The console itself holds no token.
 */
const api = process.env.CORPAY_API_URL ?? "http://localhost:5340";
const eod = process.env.CORPAY_EOD_URL ?? "http://localhost:5341";
const tokenUrl = process.env.CORPAY_TOKEN_URL ?? "http://localhost:58080/realms/corpay/protocol/openid-connect/token";
const tenant = process.env.CORPAY_TENANT ?? "corpay-smoke";
/** The proof's planted fault (#325 "a deliberately wrong expectation fails the run"): the approver the audit must name. */
const expectedApprover = process.env.CORPAY_EXPECTED_APPROVER ?? "approver";

const passwords: Record<string, string> = {
  maker: "maker-smoke-only",
  approver: "approver-smoke-only",
  viewer: "viewer-smoke-only",
};

async function tokenFor(user: string): Promise<string> {
  const http: APIRequestContext = await playwrightRequest.newContext();
  try {
    const response = await http.post(tokenUrl, {
      form: { grant_type: "password", client_id: "corpay-console-smoke", username: user, password: passwords[user] },
    });
    expect(response.ok(), `Keycloak refused a token for ${user}: ${response.status()}`).toBeTruthy();
    return (await response.json()).access_token as string;
  } finally {
    await http.dispose();
  }
}

/** One browser context per actor: every request it makes carries that actor's token and the tenant. */
async function actor(browser: Browser, user: string): Promise<BrowserContext> {
  return browser.newContext({
    extraHTTPHeaders: { Authorization: `Bearer ${await tokenFor(user)}`, "X-Goldpath-Tenant": tenant },
  });
}

test.describe("CorPay's own console, driven end to end", () => {
  test("the API head's console holds its floor: no token is 401, a token without the ops role is 403", async ({ browser }) => {
    // Read through each context's own request client: it carries the same headers its pages would,
    // and a browser renders an empty 401/403 as a navigation error rather than a status.
    const anonymous = await browser.newContext();
    expect((await anonymous.request.get(`${api}/goldpath/console/`)).status()).toBe(401);
    await anonymous.close();

    const viewer = await actor(browser, "viewer");
    expect((await viewer.request.get(`${api}/goldpath/console/`)).status()).toBe(403);
    await viewer.close();

    // And the operator's token opens it: the same page, the same floor.
    const operator = await actor(browser, "maker");
    expect((await operator.request.get(`${api}/goldpath/console/`)).status()).toBe(200);
    await operator.close();
  });

  test("the EOD job on the internal head: run it, watch it in the history, pause and resume it", async ({ page }) => {
    const openRuns = async () => {
      await page.goto(`${eod}/goldpath/console/`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
      await page.getByRole("button", { name: /corpay-eodworker/ }).click();
    };

    // How many EOD runs exist before this one: the history must show a NEW run, not an older one.
    const eodRuns = async () =>
      ((await (await page.request.get(`${eod}/goldpath/admin/jobs/fleets/corpay-eodworker/runs?take=200`)).json()) as { jobName: string; status: string }[])
        .filter((run) => run.jobName === "EodReconciliationJob");
    const before = (await eodRuns()).length;

    await openRuns();
    await page.getByRole("tab", { name: "Jobs" }).click();
    const eodJob = page.getByRole("row", { name: /EodReconciliationJob/ }).first();
    await eodJob.getByRole("button", { name: "trigger" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect(dialog).toContainText("recorded as started by hand");
    await dialog.getByRole("button", { name: "trigger" }).click();
    await expect(page.getByRole("status")).toBeVisible();

    // The run reaches a terminal state, polled through CorPay's own admin surface.
    await expect.poll(async () => (await eodRuns()).length, { timeout: 30_000 }).toBe(before + 1);
    const eodRun = page.getByRole("row", { name: /EodReconciliationJob/ }).first();
    await expect(async () => {
      await openRuns();
      await page.getByRole("tab", { name: "History" }).click();
      await expect(eodRun).toContainText("Completed", { timeout: 5_000 });
    }).toPass({ timeout: 90_000 });
    await expect(eodRun).toContainText("Manual");

    // Pause the job and resume it: the store's trigger state moves (the job reads Suppressed, its verb
    // becomes resume), and after a reload the screen still says so — it is the store's state, not the page's.
    await page.getByRole("tab", { name: "Jobs" }).click();
    const job = page.getByRole("row", { name: /EodReconciliationJob/ }).first();
    await job.getByRole("button", { name: "pause" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "pause" }).click();
    await expect(job.getByRole("button", { name: "resume" })).toBeVisible();
    await openRuns();
    await page.getByRole("tab", { name: "Jobs" }).click();
    await expect(job).toContainText("Suppressed");
    await expect(job.getByRole("button", { name: "pause" })).toHaveCount(0);

    await job.getByRole("button", { name: "resume" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "resume" }).click();
    await expect(job.getByRole("button", { name: "pause" })).toBeVisible();
    await openRuns();
    await page.getByRole("tab", { name: "Jobs" }).click();
    await expect(job).not.toContainText("Suppressed");
    await expect(job.getByRole("button", { name: "pause" })).toBeVisible();

    // Every crossing is on the fleet's admin audit, newest first: resume, pause, trigger. This head is
    // exposeUnsecured (its visible choice), so its actor is "anonymous".
    await page.getByRole("tab", { name: "Overview" }).click();
    const audit = page.getByTestId("fleet-overview").getByRole("row", { name: /EodReconciliationJob/ });
    await expect(audit.nth(0)).toContainText("resume");
    await expect(audit.nth(1)).toContainText("pause");
    await expect(audit.nth(2)).toContainText("trigger");
  });

  test("a payment file through the four-eyes gate: the maker uploads, the approver approves, each is on the record", async ({ browser }) => {
    // Two actors, validation and execution each a real job run: longer than the suite's 60 s default.
    test.setTimeout(240_000);
    const reference = `SMOKE-${Date.now()}`;
    const csv = [
      "Reference,DebtorIban,CreditorIban,Amount,Currency",
      `${reference}-1,TR330006100519786457841326,TR320010009999901234567890,125.00,TRY`,
      `${reference}-2,DE89370400440532013000,FR1420041010050500013M02606,42.50,EUR`,
      "",
    ].join("\n");

    const openBulk = async (page: import("@playwright/test").Page) => {
      await page.goto(`${api}/goldpath/console/`);
      await page.getByTestId("shell-rail").getByRole("button", { name: "Bulk intake", exact: true }).click();
      await expect(page.getByTestId("bulk-panel")).toBeVisible();
    };
    const newest = (page: import("@playwright/test").Page) =>
      page.getByTestId("batches").getByRole("row", { name: /payment-instructions/ }).first();
    const waitForState = async (page: import("@playwright/test").Page, state: string | RegExp) =>
      expect(async () => {
        await openBulk(page);
        await expect(newest(page)).toContainText(state, { timeout: 5_000 });
      }).toPass({ timeout: 90_000 });

    // ── the maker
    const maker = await actor(browser, "maker");
    const makerPage = await maker.newPage();
    await openBulk(makerPage);
    await makerPage.getByLabel("definition").click();
    await makerPage.getByRole("option", { name: "payment-instructions" }).click();
    await makerPage.getByLabel("batch file").setInputFiles({ name: "payments.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
    await makerPage.getByRole("button", { name: "upload" }).click();
    const confirm = makerPage.getByRole("alertdialog", { name: "confirm upload" });
    await expect(confirm).toContainText("Upload payments.csv into payment-instructions?");
    await confirm.getByRole("button", { name: "upload" }).click();
    await expect(makerPage.getByText(/Received — batch .* is queued for validation/)).toBeVisible();
    await waitForState(makerPage, "Validated");
    await maker.close();

    // ── the approver, a different principal in a different context
    const approver = await actor(browser, "approver");
    const approverPage = await approver.newPage();
    await waitForState(approverPage, "Validated");
    await newest(approverPage).getByRole("button").first().click();
    await approverPage.getByRole("button", { name: "approve" }).click();
    const approving = approverPage.getByRole("alertdialog", { name: "confirm approve" });
    await approving.getByLabel("note (optional)").fill("four-eyes: checked against the payment register");
    await approving.getByRole("button", { name: "approve" }).click();
    // The engine's answer first: navigating away before it lands would cancel the call (a 499).
    await expect(approverPage.getByText("approved", { exact: true })).toBeVisible();
    // Approval starts the execute run at once, so the row may already have moved past Approved.
    await waitForState(approverPage, /Approved|Executing|Completed/);

    // The batch records WHO decided — the approver's own principal, from the token.
    await newest(approverPage).getByRole("button").first().click();
    const detail = approverPage.getByTestId("batch-detail");
    await expect(detail).toContainText(`Approved by ${expectedApprover}`);
    await expect(detail).toContainText("four-eyes: checked against the payment register");

    // ── the maker is on the jobs admin audit: the upload verb started the validate run in the maker's name.
    // The approval writes no admin-audit row of its own (Goldpath.Bulk records it on the batch, above):
    // open-threads T63 carries that gap, so this line does not claim more than the audit holds.
    await approverPage.keyboard.press("Escape");
    await approverPage.getByTestId("shell-rail").getByRole("button", { name: "Runs", exact: true }).click();
    await approverPage.getByRole("button", { name: /CorPay\.Api/ }).click();
    await approverPage.getByRole("tab", { name: "Overview" }).click();
    const audit = approverPage.getByTestId("fleet-overview");
    await expect(audit.getByRole("row", { name: /GoldpathBulkValidateJob/ }).first()).toContainText("maker");
    await approver.close();
  });

  test("the approved payment file executes: the payment lands (fails today: #539)", async ({ browser }) => {
    // Driving CorPay found it: Goldpath.Bulk's execute run calls the row handler with no ambient tenant,
    // so on a multi-tenant app every row's SaveChanges is refused ("Adding 'PaymentInstruction' requires
    // an ambient tenant") and the retried chunk files each row as "interrupted mid-flight". Marked
    // test.fail() so the night stays honest without hiding it: the day the defect is fixed this test
    // PASSES, Playwright reports it as an unexpected pass, and the marker has to come off (#539, open-threads T62).
    test.fail();
    test.setTimeout(180_000);
    const reference = `EXEC-${Date.now()}`;
    const csv = `Reference,DebtorIban,CreditorIban,Amount,Currency\n${reference}-1,TR330006100519786457841326,TR320010009999901234567890,10.00,TRY\n`;

    const maker = await actor(browser, "maker");
    const uploaded = await maker.request.post(`${api}/goldpath/admin/bulk/batches/payment-instructions?fileName=exec.csv`, {
      data: csv, headers: { "Content-Type": "text/csv" },
    });
    expect(uploaded.ok(), `upload refused: ${uploaded.status()}`).toBeTruthy();
    const batchId = (await uploaded.json()).id as string;
    await maker.close();

    const approver = await actor(browser, "approver");
    const state = async () => (await (await approver.request.get(`${api}/goldpath/admin/bulk/batches/${batchId}`)).json()).state as string;
    await expect.poll(state, { timeout: 60_000 }).toBe("Validated");
    expect((await approver.request.post(`${api}/goldpath/admin/bulk/batches/${batchId}/approve`, { data: { note: "execution proof" } })).ok()).toBeTruthy();
    await expect.poll(state, { timeout: 120_000 }).toMatch(/^Completed/);
    const batch = await (await approver.request.get(`${api}/goldpath/admin/bulk/batches/${batchId}`)).json();
    await approver.close();
    expect({ state: batch.state, executed: batch.executedRows, failed: batch.failedRows }).toEqual({ state: "Completed", executed: 1, failed: 0 });
  });
});
