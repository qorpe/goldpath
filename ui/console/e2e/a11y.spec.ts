import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

/**
 * The accessibility gate. An operations console is used at 3am by whoever is on call,
 * often over a remote session with a keyboard and no mouse — so this is a correctness
 * property, not a nicety.
 *
 * WCAG 2.1 A + AA, serious and critical violations only: the ramp of "moderate" findings
 * (landmark preferences, heading order in a dense panel) is advice, and a gate that fails
 * on advice gets muted, which costs the real findings too.
 */
const service = process.env.GOLDPATH_SERVICE_URL ?? "http://localhost:5310";
const served = process.env.GOLDPATH_SERVED_CONSOLE_URL ?? `${service}/goldpath/console/`;

const PANELS = [
  // The landing screen is checked too: it is the one an operator sees first, at 3am.
  { nav: "Today", ready: "triage-home" },
  { nav: "Runs", ready: "run-console" },
  { nav: "Bulk intake", ready: "bulk-panel" },
  { nav: "Campaigns", ready: "campaign-panel" },
  { nav: "Notifications", ready: "notification-panel" },
  { nav: "Archival", ready: "archival-panel" },
  // The two newest modules were never scanned — both panels shipped without an axe run
  // (preview.8 coverage audit, 2026-09-05).
  { nav: "Approvals", ready: "approvals-panel" },
  { nav: "File rails", ready: "fileexchange-panel" },
] as const;

async function violations(page: import("@playwright/test").Page) {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  return result.violations
    .filter((violation) => violation.impact === "serious" || violation.impact === "critical")
    .map((violation) => `${violation.id} (${violation.impact}) — ${violation.nodes.length} node(s): ${violation.help}`);
}

test.describe("the console is operable without a mouse or a perfect screen", () => {
  for (const panel of PANELS) {
    test(`${panel.nav} has no serious accessibility violation`, async ({ page }) => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      // EXACT: Today's "Failed runs" stat card is a button too, and a substring match on
      // "Runs" hits both whenever the cards render before the click (seen red 2026-10-19).
      await page.getByRole("button", { name: panel.nav, exact: true }).click();
      await expect(page.getByTestId(panel.ready)).toBeVisible();

      expect(await violations(page)).toEqual([]);
    });
  }

  // Each SECTION of the scheduling surface is its own screen behind a tab; scanning only
  // whichever one happens to be active would leave three unchecked.
  for (const tab of ["Overview", "Jobs", "Calendars", "History"]) {
    test(`the ${tab} section of Runs has no serious accessibility violation`, async ({ page }) => {
      await page.goto(`/?base=${encodeURIComponent(service)}`);
      await page.getByRole("button", { name: "Runs" }).click();
      await page.getByRole("tab", { name: tab }).click();
      await expect(page.getByRole("tabpanel")).toBeVisible();

      expect(await violations(page)).toEqual([]);
    });
  }

  // Product console link tiles (#257). The scans above drive `?base=`, which skips the
  // registry — so they never see a tile. This one scans the page the APP serves, registry
  // and tiles included.
  test("the SERVED Today, product console tiles included, has no serious accessibility violation", async ({ page }) => {
    await page.goto(served);
    await expect(page.getByRole("navigation", { name: "Product consoles" }).getByRole("link")).toHaveCount(2);
    await expect(page.getByTestId("triage-home")).toBeVisible();

    expect(await violations(page)).toEqual([]);
  });

  test("the tiles are reached by Tab, in order, and Enter goes where the tile says", async ({ page }) => {
    await page.goto(served);
    const tiles = page.getByRole("navigation", { name: "Product consoles" }).getByRole("link");
    await expect(tiles).toHaveCount(2);

    // From the top of the page, by Tab alone — no focus() shortcut onto the tile.
    let reached = false;
    for (let press = 0; press < 80 && !reached; press++) {
      await page.keyboard.press("Tab");
      reached = await tiles.first().evaluate((tile) => tile === document.activeElement);
    }
    expect(reached, "Tab never reached the first product console tile").toBe(true);

    await page.keyboard.press("Tab");
    await expect(tiles.nth(1)).toBeFocused();

    await page.keyboard.press("Shift+Tab");
    await expect(tiles.first()).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { name: "Product console" })).toBeVisible();
  });

  test("the tab strip is walkable by arrow keys, with focus following the panel", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByRole("button", { name: "Runs" }).click();
    await page.getByRole("tab", { name: "Overview" }).focus();

    await page.keyboard.press("ArrowRight");

    // Six presses of Tab to reach the last section is the failure this pattern prevents.
    await expect(page.getByRole("tab", { name: "Jobs" })).toBeFocused();
    await expect(page.getByTestId("jobs-tab")).toBeVisible();
  });

  test("a confirm dialog is opened, dismissed and handed back by keyboard alone", async ({ page }) => {
    await page.goto(`/?base=${encodeURIComponent(service)}`);
    await page.getByRole("button", { name: "Runs" }).click();
    await expect(page.getByTestId("run-console")).toBeVisible();

    // Opened from the KEYBOARD — the mouse is not assumed anywhere in this journey.
    await page.getByRole("tab", { name: "Jobs" }).click();
    const trigger = page.getByRole("row", { name: /SmokeJob/ }).first().getByRole("button", { name: "trigger" }).first();
    await trigger.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("alertdialog");
    await expect(dialog).toBeVisible();

    // The dialog itself must be clean — this is where a destructive verb is confirmed.
    expect(await violations(page)).toEqual([]);

    // And it must be dismissible from the keyboard: an operator who opened it by mistake
    // should not have to hunt for the mouse. (The dialog is inline, not a modal overlay,
    // so there is no focus TRAP to assert — the page behind it stays reachable by design.)
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);

    // And focus lands back on the button that opened it: the operator keeps their place.
    await expect(trigger).toBeFocused();
  });
});
