import { AdminClient, AdminHttpError, MODULES } from "./adminClient";

type Route = string;
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

function fakeFetch(routes: Record<Route, Response | (() => Response)>) {
  const calls: { url: string; init?: RequestInit }[] = [];
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.push({ url, init });
    const key = Object.keys(routes).find((route) => url.endsWith(route) || url.includes(route));
    if (!key) return new Response("not found", { status: 404 });
    const entry = routes[key];
    return typeof entry === "function" ? entry() : entry.clone();
  }) as typeof fetch;
  return { fetcher, calls };
}

describe("the admin client (the console's only door — the FROZEN contract)", () => {
  it("discovers capabilities from the contract's own list roots", async () => {
    const { fetcher } = fakeFetch({
      "/goldpath/admin/jobs/fleets": json([]),
      "/goldpath/admin/bulk/definitions": json([]),
      "/goldpath/admin/campaign/": json([]),
      // archival + notification are simply not composed into this app.
    });

    const capabilities = await new AdminClient({ fetcher }).discoverCapabilities();

    expect(capabilities.jobs).toEqual({ kind: "present" });
    expect(capabilities.bulk).toEqual({ kind: "present" });
    expect(capabilities.campaign).toEqual({ kind: "present" });
    expect(capabilities.archival).toEqual({ kind: "absent" });        // 404 = never composed
    expect(capabilities.notification).toEqual({ kind: "absent" });
  });

  it("tells 'forbidden' apart from 'absent' — the panel exists, this operator may not see it", async () => {
    const { fetcher } = fakeFetch({
      "/goldpath/admin/jobs/fleets": new Response("", { status: 403 }),
      "/goldpath/admin/archival/definitions": new Response("", { status: 401 }),
    });

    const capabilities = await new AdminClient({ fetcher }).discoverCapabilities();

    expect(capabilities.jobs.kind).toBe("forbidden");
    expect(capabilities.archival.kind).toBe("forbidden");
    expect(capabilities.bulk).toEqual({ kind: "absent" });
  });

  it("a 400 is REFUSED, not absent — and carries the server's own reason", async () => {
    // What a multi-tenant app answers when the call cannot be scoped (R1): the module is
    // composed and reachable; only this request is impossible. Tenant resolution answers
    // ProblemDetails, the admin seam answers the Goldpath envelope — both are read.
    const { fetcher } = fakeFetch({
      "/goldpath/admin/jobs/fleets": new Response(
        JSON.stringify({ title: "Tenant could not be resolved.", status: 400 }),
        { status: 400, headers: { "content-type": "application/json" } },
      ),
      "/goldpath/admin/bulk/definitions": new Response(
        JSON.stringify({ ok: false, message: "no ambient tenant on a multi-tenant app" }),
        { status: 400, headers: { "content-type": "application/json" } },
      ),
    });

    const capabilities = await new AdminClient({ fetcher }).discoverCapabilities();

    expect(capabilities.jobs).toEqual({ kind: "refused", message: "Tenant could not be resolved." });
    expect(capabilities.bulk).toEqual({ kind: "refused", message: "no ambient tenant on a multi-tenant app" });
    expect(capabilities.campaign).toEqual({ kind: "absent" });
  });

  it("a refusal with a body the console cannot read still refuses — it never invents words", async () => {
    const { fetcher } = fakeFetch({ "/goldpath/admin/jobs/fleets": new Response("<html>gateway</html>", { status: 400 }) });

    const capabilities = await new AdminClient({ fetcher }).discoverCapabilities();

    expect(capabilities.jobs).toEqual({ kind: "refused", message: undefined });
  });

  it("a service that never ANSWERS is unreachable, not module-less", async () => {
    // A dead service and a cross-origin one whose CORS refuses this console look identical
    // from here — fetch reports nothing more. Both are "we could not ask", which is a
    // different sentence from "the app does not have this module", and the console must
    // not collapse the two: only one of them means stop looking.
    const fetcher = (async () => {
      throw new TypeError("network down");
    }) as typeof fetch;

    const capabilities = await new AdminClient({ fetcher }).discoverCapabilities();

    expect(MODULES.every((module) => capabilities[module].kind === "unreachable")).toBe(true);
  });

  it("clamps take to the contract's [1,500] before the request leaves the browser", async () => {
    const { fetcher, calls } = fakeFetch({ "/runs?": json([]) });
    const client = new AdminClient({ fetcher });

    await client.runs("it-cluster", { take: 10_000 });
    await client.runs("it-cluster", { take: 0 });

    expect(calls[0].url).toContain("take=500");
    expect(calls[1].url).toContain("take=1");
  });

  it("encodes fleet and job names — a name with a slash never forges a route", async () => {
    const { fetcher, calls } = fakeFetch({ "/jobs/": json([]) });

    await new AdminClient({ fetcher }).jobs("eod/nightly");

    expect(calls[0].url).toContain("fleets/eod%2Fnightly/jobs");
    expect(calls[0].url).not.toContain("fleets/eod/nightly/jobs");
  });

  it("returns the verb envelope for BOTH 200 and 400 — refusals are data, not errors", async () => {
    const { fetcher } = fakeFetch({
      "/trigger": json({ ok: true, message: "run 42 scheduled" }),
      "/pause": json({ ok: false, message: "the job is already paused" }, 400),
    });
    const client = new AdminClient({ fetcher });

    expect(await client.triggerJob("it", "eod")).toEqual({ ok: true, message: "run 42 scheduled" });
    expect(await client.pauseJob("it", "eod")).toEqual({ ok: false, message: "the job is already paused" });
  });

  it("throws on an unexpected status so the UI can say something honest", async () => {
    const { fetcher } = fakeFetch({ "/trigger": new Response("", { status: 503 }) });

    await expect(new AdminClient({ fetcher }).triggerJob("it", "eod")).rejects.toBeInstanceOf(AdminHttpError);
  });

  it("carries credentials on every call — the ops floor is the same for the console", async () => {
    const { fetcher, calls } = fakeFetch({ "/fleets": json([]) });

    await new AdminClient({ fetcher }).fleets();

    expect(calls[0].init?.credentials).toBe("include");
  });

  it("every campaign verb posts ITS frozen route — including the ones the panel shows rarely", async () => {
    const { fetcher, calls } = fakeFetch({ "/campaign/": json({ ok: true, message: "done" }) });
    const client = new AdminClient({ fetcher });

    await client.resumeCampaign("c-1");
    await client.pauseCampaign("c-1");

    expect(calls[0].url).toContain("/goldpath/admin/campaign/c-1/resume");
    expect(calls[1].url).toContain("/goldpath/admin/campaign/c-1/pause");
  });

  it("R4.5: the ceiling routes — the global row, a target system by its encoded name, and the audit", async () => {
    const { fetcher, calls } = fakeFetch({ "/campaign/": json({ ok: true, message: "done" }) });
    const client = new AdminClient({ fetcher });

    await client.throttleCampaignCeiling({ global: true }, { clearMaxTps: true });
    await client.throttleCampaignCeiling({ targetSystem: "device/core x" }, { maxTps: 40 });
    await client.campaignCeilings();
    await client.campaignCeilingAudit(9_999);

    expect(calls[0].url).toContain("/goldpath/admin/campaign/ceilings/global/throttle");
    expect(calls[0].init?.body).toBe(JSON.stringify({ clearMaxTps: true }));
    expect(calls[1].url).toContain("/goldpath/admin/campaign/ceilings/target-systems/device%2Fcore%20x/throttle");
    expect(calls[1].init?.body).toBe(JSON.stringify({ maxTps: 40 }));
    expect(calls[2].url).toContain("/goldpath/admin/campaign/ceilings");
    expect(calls[3].url).toContain("/goldpath/admin/campaign/ceilings/audit?take=500");
  });

  it("ids with slashes and spaces are ENCODED — a key is data, never part of the path", async () => {
    const { fetcher, calls } = fakeFetch({ "/goldpath/admin/": json({}) });
    const client = new AdminClient({ fetcher });

    await client.notification("a/b c");
    await client.archiveEntry("policies", "P/42 x");

    expect(calls[0].url).toContain("/notifications/a%2Fb%20c");
    expect(calls[1].url).toContain("/entries/policies/P%2F42%20x");
  });

  it("rerun posts the run's own route — the repair verbs are all one shape", async () => {
    const { fetcher, calls } = fakeFetch({ "/jobs/runs/": json({ ok: true, message: "rerun queued" }) });

    const result = await new AdminClient({ fetcher }).rerun("run-9f21");

    expect(calls[0].url).toContain("/goldpath/admin/jobs/runs/run-9f21/rerun");
    expect(result).toEqual({ ok: true, message: "rerun queued" });
  });

  it("a verb answered with an unexpected status THROWS — it is never read as an outcome", async () => {
    const { fetcher } = fakeFetch({ "/jobs/runs/": new Response("", { status: 503 }) });

    await expect(new AdminClient({ fetcher }).rerun("run-9f21")).rejects.toThrow(/503/);
  });
});
