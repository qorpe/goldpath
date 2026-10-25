import { expect, test, type Page } from "@playwright/test";

/**
 * S-OPS-02 (GA gate G10a, docs/strategy/scenario-campaign.md): a mid-flight operator
 * mistake is refused in the SERVER's words, attempted from the screen, and nothing in the
 * run answers 5xx. Four mistakes: a bad cron, an unknown timezone, a second signature by
 * the same approver, and an erase while a legal hold stands.
 *
 * Each mistake works on state no other spec touches. The approval is its own request,
 * which the smoke host files at startup next to the one console.spec.ts signs. The
 * archive entry is policy 3, not policy 1. And a refused reschedule changes nothing. So
 * this file can run beside the others in any order.
 */
const service = process.env.GOLDPATH_SERVICE_URL ?? "http://localhost:5310";

/**
 * Every call to the service in the run, so a 5xx anywhere fails the test it happened in.
 * A failed request counts too: the platform's exception page carries no CORS headers, so
 * a 500 to the cross-origin console reaches the browser as a failed request, not a status.
 */
function watchForServerErrors(page: Page): string[] {
  const failures: string[] = [];
  page.on("response", (response) => {
    if (response.status() >= 500) failures.push(`${response.status()} ${response.request().method()} ${response.url()}`);
  });
  page.on("requestfailed", (request) => {
    if (request.url().startsWith(service)) failures.push(`failed ${request.method()} ${request.url()}: ${request.failure()?.errorText}`);
  });
  return failures;
}

/**
 * Confirms a verb and returns the status the server answered it with. A refusal is a
 * 400 carrying the verb envelope. A 5xx is the failure this scenario exists to catch, so
 * it is asserted here, before any text, and the test names the status it got.
 */
async function confirmAndStatus(page: Page, dialog: string, verb: string, route: RegExp): Promise<number> {
  const status = answerTo(page, route);
  await page.getByRole("alertdialog", { name: dialog }).getByRole("button", { name: verb, exact: true }).click();
  return expectRefusal(await status, verb);
}

/** The status the next POST to <route> answered with; 0 when the browser could not read one. */
function answerTo(page: Page, route: RegExp): Promise<number> {
  return Promise.race([
    page.waitForResponse((response) => route.test(response.url()) && response.request().method() === "POST").then((response) => response.status()),
    page.waitForEvent("requestfailed", (request) => route.test(request.url())).then(() => 0),
  ]);
}

function expectRefusal(status: number, verb: string): number {
  expect(status, `${verb} answered ${status === 0 ? "nothing the browser could read (a 5xx without CORS headers)" : status}: a mistake is a 400 in words, never a 5xx`).toBe(400);
  return status;
}

const open = async (page: Page, section: string) => {
  await page.goto(`/?base=${encodeURIComponent(service)}`);
  await page.getByTestId("shell-rail").getByRole("button", { name: section, exact: true }).click();
};

test.describe("S-OPS-02 — every operator mistake is refused in the server's words, zero 5xx", () => {
  let serverErrors: string[] = [];

  test.beforeEach(({ page }) => {
    serverErrors = watchForServerErrors(page);
  });

  test.afterEach(() => {
    expect(serverErrors, "no request in this journey may answer 5xx").toEqual([]);
  });

  test("a bad cron and an unknown timezone are refused where the operator typed them", async ({ page }) => {
    await open(page, "Runs");
    await page.getByRole("tab", { name: "Jobs" }).click();
    await page.getByRole("button", { name: "SmokeJob" }).click();
    const sheet = page.getByTestId("sheet");
    await sheet.getByRole("button", { name: "change the schedule" }).click();
    const form = page.getByRole("dialog", { name: "Change the schedule" });

    // A cron Quartz cannot parse: the refusal names the expression, inside the form.
    await form.getByLabel("Cron").fill("every night at three");
    await form.getByRole("button", { name: "reschedule" }).click();
    await confirmAndStatus(page, "confirm reschedule", "reschedule", /\/reschedule$/);
    await expect(form.getByRole("alert")).toHaveText("'every night at three' is not a valid Quartz cron expression");

    // A valid cron in a timezone this host does not know: refused, never a 500 from an
    // unhandled TimeZoneNotFoundException.
    await form.getByLabel("Cron").fill("0 0 3 * * ?");
    await form.getByLabel("Timezone").fill("Mars/Olympus_Mons");
    await form.getByRole("button", { name: "reschedule" }).click();
    await confirmAndStatus(page, "confirm reschedule", "reschedule", /\/reschedule$/);
    await expect(form.getByRole("alert")).toHaveText("'Mars/Olympus_Mons' is not a timezone this host knows");

    // Both were refusals, so the form stays open with the operator's input in it.
    await expect(form.getByLabel("Timezone")).toHaveValue("Mars/Olympus_Mons");
  });

  test("the same approver's second signature is refused by the engine's rule, by name", async ({ page }) => {
    await open(page, "Approvals");
    const row = page.getByTestId("approvals-queue").getByRole("row", { name: /Limit raise for S-OPS-02/ });
    await expect(row).toContainText("0/2");
    await row.getByRole("button", { name: "Limit raise for S-OPS-02" }).click();
    const detail = page.getByTestId("approval-detail");

    await detail.getByLabel("Decider role").fill("manager");
    await detail.getByRole("button", { name: "Approve", exact: true }).click();
    await expect(page.getByTestId("verb-message")).toHaveText("Applied");
    await expect(detail).toContainText("1/2");

    // The double approve: the engine refuses it, and the panel shows the rule's own name.
    const answered = answerTo(page, /\/approve$/);
    await detail.getByRole("button", { name: "Approve", exact: true }).click();
    expectRefusal(await answered, "a second approve");
    await expect(page.getByTestId("verb-message")).toHaveText("AlreadySigned");
    await expect(detail).toContainText("1/2");
  });

  test("an erase under a legal hold is refused with the reason, and the hold stands", async ({ page }) => {
    const entry = page.getByTestId("archive-entry");
    // The archive job is real and runs every 5 s: retry until it has appended policy 3.
    await expect(async () => {
      await open(page, "Archival");
      await page.getByLabel("aggregate key").fill("3");
      await page.getByRole("button", { name: "retrieve" }).click();
      await expect(entry).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 90_000 });

    await page.getByRole("button", { name: "hold", exact: true }).click();
    const holding = page.getByRole("alertdialog", { name: "confirm hold" });
    await holding.getByLabel("case reference (required)").fill("CASE-S-OPS-02");
    await holding.getByRole("button", { name: "hold", exact: true }).click();
    await expect(page.getByRole("row", { name: /CASE-S-OPS-02/ })).toBeVisible();

    try {
      await page.getByRole("button", { name: "erase" }).click();
      const erasing = page.getByRole("alertdialog", { name: "confirm erase" });
      await erasing.getByLabel("subject key (required)").fill("holder:3");
      const answered = answerTo(page, /\/erase$/);
      await erasing.getByRole("button", { name: "erase" }).click();
      expectRefusal(await answered, "an erase under a hold");
      await expect(entry).toContainText("an active legal hold exempts this entry from erasure (D4) — lift the hold first, with counsel");
      // Refused means nothing was redacted, so no erasure row exists for this subject.
      await expect(page.getByRole("row", { name: /holder:3/ })).toHaveCount(0);
    } finally {
      // Leave the store as this spec found it. The hold is lifted through the API, so the
      // cleanup runs even when an assertion above is what failed.
      await page.request.post(`${service}/goldpath/admin/archival/entries/policies/3/lift-hold`);
    }
  });
});
