import { defineConfig } from "@playwright/test";

/**
 * The U2 exit gate (console RFC D3): behaviour against a REAL app — no screenshot-diff
 * theatre. `scripts/console-smoke.sh` brings up Postgres + a real Goldpath web app + the
 * console dev server, then runs this suite against them.
 */
export default defineConfig({
  testDir: "./e2e",
  // The screenshot capture is a DOCUMENT, not a gate: it writes the README's pictures from
  // a real console and is run on demand by scripts/console-screenshots.sh. A gate that
  // fails on pixels gets muted, and muting it would cost the behaviour proofs beside it.
  // The CorPay drive (#325) runs only from scripts/corpay-console-smoke.sh, which sets CORPAY_API_URL:
  // it needs CorPay and a Keycloak, not the purpose-built hosts the rest of this suite drives.
  testIgnore: [
    ...(process.env.GOLDPATH_SHOT_DIR ? [] : ["**/screenshots.spec.ts"]),
    ...(process.env.CORPAY_API_URL ? [] : ["**/corpay.spec.ts"]),
  ],
  timeout: 60_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.GOLDPATH_CONSOLE_URL ?? "http://localhost:5200",
    trace: "retain-on-failure",
  },
});
