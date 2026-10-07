import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type CampaignInfo } from "./adminClient";
import { CampaignPanel, describeThrottle, toThrottle } from "./CampaignPanel";

const emptyDraft = {
  tps: "", dailyQuota: "", maxInFlight: "", windowStart: "", windowEnd: "", timeZoneId: "",
  clearDailyQuota: false, clearWindow: false,
  excludedDays: [] as string[], endDate: "", maxAttempts: "", clearExcludedDays: false, clearEndDate: false, priority: "",
};

const campaign = (over: Partial<CampaignInfo> = {}): CampaignInfo => ({
  id: "c1a2b3c4-0000-4000-8000-000000000001",
  type: "welcome-sms",
  name: "june-welcome",
  state: "Running",
  enumeratedThrough: 10_000,
  enumerationComplete: true,
  releasedThrough: 2_500,
  succeededCount: 2_400,
  failedCount: 40,
  inFlight: 60,
  remaining: 7_500,
  tps: 25,
  dailyQuota: 5_000,
  releasedToday: 2_500,
  maxInFlight: 100,
  windowStart: "09:00:00",
  windowEnd: "20:00:00",
  timeZoneId: "Europe/Istanbul",
  excludedDays: [],
  endDate: null,
  maxAttempts: 1,
  priority: "Normal",
  windowOpenNow: true,
  etaSecondsAtCurrentTps: 300,
  createdAt: "2026-07-27T06:00:00Z",
  createdBy: "ops@example.com",
  lastVerb: "resume",
  ...over,
});

interface Routes {
  list?: unknown;
  detail?: unknown;
  failures?: unknown;
  audit?: unknown;
  verb?: { status: number; body: unknown };
}

function client(routes: Routes = {}) {
  const posted: { url: string; body: BodyInit | null | undefined }[] = [];
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const json = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

    if (init?.method === "POST") {
      posted.push({ url, body: init.body });
      const verb = routes.verb ?? { status: 200, body: { ok: true, message: "paused" } };
      return json(verb.body, verb.status);
    }

    if (url.includes("/failures")) return json(routes.failures ?? []);
    if (url.includes("/audit")) return json(routes.audit ?? []);
    if (url.includes("/campaign/?")) return json(routes.list ?? [campaign()]);
    if (url.includes("/campaign/")) return json(routes.detail ?? campaign());
    return new Response("not found", { status: 404 });
  }) as typeof fetch;

  return { client: new AdminClient({ fetcher }), posted };
}

const open = async (name = "june-welcome") => userEvent.click(await screen.findByRole("button", { name }));

describe("the throttle patch (a null field KEEPS the server's current value)", () => {
  const draft = { ...emptyDraft };

  it("R1: the new dials travel only when touched — and clears are explicit", () => {
    expect(toThrottle({ ...draft, excludedDays: ["Saturday", "Sunday"], endDate: "2026-08-10", maxAttempts: "3" }))
      .toEqual({ excludedDays: ["Saturday", "Sunday"], endDate: "2026-08-10", maxAttempts: 3 });
    expect(toThrottle({ ...draft, clearExcludedDays: true, clearEndDate: true }))
      .toEqual({ clearExcludedDays: true, clearEndDate: true });
  });

  it("sends nothing for an untouched form", () => {
    expect(toThrottle(draft)).toEqual({});
  });

  it("R2.5: priority travels only when chosen, and the confirm line names it", () => {
    expect(toThrottle({ ...draft, priority: "High" })).toEqual({ priority: "High" });
    expect(describeThrottle({ priority: "Low", tps: 5 })).toBe("5 tps, priority Low");
  });

  it("sends only what the operator typed", () => {
    expect(toThrottle({ ...draft, tps: "40", timeZoneId: " Europe/Istanbul " })).toEqual({
      tps: 40,
      timeZoneId: "Europe/Istanbul",
    });
  });

  it("keeps 0 — a zero rate is a real instruction, not an empty box", () => {
    expect(toThrottle({ ...draft, tps: "0" })).toEqual({ tps: 0 });
  });

  it("clearing is explicit: a blank box cannot say 'no quota'", () => {
    expect(toThrottle({ ...draft, clearDailyQuota: true, clearWindow: true })).toEqual({
      clearDailyQuota: true,
      clearWindow: true,
    });
  });

  it("describes the change in the operator's words", () => {
    expect(describeThrottle({ tps: 40, clearWindow: true })).toBe("40 tps, no window (release around the clock)");
  });
});

describe("the campaign governor panel", () => {
  it("lists campaigns with the pacer's numbers", async () => {
    const { client: api } = client();
    render(<CampaignPanel client={api} />);

    expect(await screen.findByRole("button", { name: "june-welcome" })).toBeInTheDocument();
    const row = screen.getByRole("row", { name: /june-welcome/ });
    expect(row).toHaveTextContent("2500");   // released
    expect(row).toHaveTextContent("7500");   // remaining
  });

  it("filters by state through the contract's own parameter", async () => {
    const seen: string[] = [];
    const fetcher = (async (input: RequestInfo | URL) => {
      seen.push(String(input));
      return new Response(JSON.stringify([]), { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch;
    render(<CampaignPanel client={new AdminClient({ fetcher })} />);

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Paused/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Running/ }));
    await userEvent.keyboard("{Escape}");
    // R3: both selections travel as REPEATED params — OR on the server.
    await waitFor(() => expect(seen.some((url) => url.includes("state=Paused") && url.includes("state=Running"))).toBe(true));
  });

  it("shows the policy in force, including what the quota and window mean right now", async () => {
    const { client: api } = client();
    render(<CampaignPanel client={api} />);

    await open();
    const detail = await screen.findByTestId("campaign-detail");
    expect(detail).toHaveTextContent("2500 of 5000 today");
    expect(detail).toHaveTextContent("09:00:00–20:00:00 Europe/Istanbul");
    expect(detail).toHaveTextContent("open now");
    // The ETA is arithmetic at the CURRENT rate — labelled as such, never as a promise.
    expect(detail).toHaveTextContent("~5m at 25 tps");
  });

  it("says when the window is closed — that is policy, not a failure", async () => {
    const { client: api } = client({ detail: campaign({ windowOpenNow: false }), list: [campaign({ windowOpenNow: false })] });
    render(<CampaignPanel client={api} />);

    await open();
    expect(await screen.findByText("outside the release window")).toBeInTheDocument();
  });

  it("a running campaign offers pause and abort — never resume", async () => {
    const { client: api } = client();
    render(<CampaignPanel client={api} />);

    await open();
    await screen.findByTestId("campaign-detail");
    expect(screen.getByRole("button", { name: "pause" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "abort" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "resume" })).toBeNull();
  });

  it("a paused campaign offers resume, and a finished one offers nothing", async () => {
    const paused = client({ detail: campaign({ state: "Paused" }), list: [campaign({ state: "Paused" })] });
    const { unmount } = render(<CampaignPanel client={paused.client} />);
    await open();
    expect(await screen.findByRole("button", { name: "resume" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "pause" })).toBeNull();
    unmount();

    const done = client({ detail: campaign({ state: "Completed" }), list: [campaign({ state: "Completed" })] });
    render(<CampaignPanel client={done.client} />);
    await open();
    await screen.findByTestId("campaign-detail");
    expect(screen.queryByRole("button", { name: "abort" })).toBeNull();
    expect(screen.queryByRole("button", { name: "throttle" })).toBeNull();
  });

  it("aborting names the cost, demands a reason, and posts the frozen route", async () => {
    const { client: api, posted } = client();
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("button", { name: "abort" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm abort" });
    expect(dialog).toHaveTextContent("7500 remaining items are stamped Aborted");
    expect(within(dialog).getByRole("button", { name: "abort" })).toBeDisabled();

    await userEvent.type(within(dialog).getByLabelText("reason (required)"), "wrong audience");
    await userEvent.click(within(dialog).getByRole("button", { name: "abort" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/campaign/${campaign().id}/abort`);
    expect(JSON.parse(String(posted[0].body))).toEqual({ reason: "wrong audience" });
  });

  it("throttling posts ONLY the changed fields, after naming them in the confirm", async () => {
    const { client: api, posted } = client({ verb: { status: 200, body: { ok: true, message: "policy updated" } } });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.type(await screen.findByLabelText("tps"), "40");
    await userEvent.click(screen.getByLabelText("clear daily quota"));
    await userEvent.selectOptions(screen.getByLabelText("priority"), "High");

    await userEvent.click(screen.getByRole("button", { name: "throttle" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm throttle" });
    expect(dialog).toHaveTextContent("40 tps, no daily quota, priority High");
    await userEvent.click(within(dialog).getByRole("button", { name: "throttle" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/campaign/${campaign().id}/throttle`);
    // maxInFlight and the window are untouched, so they are ABSENT — the server keeps them.
    expect(JSON.parse(String(posted[0].body))).toEqual({ tps: 40, clearDailyQuota: true, priority: "High" });
  });

  it("the verb's answer OUTLIVES the buttons that produced it", async () => {
    // Pausing moves the campaign out of the live set, so pause/abort unmount on refresh.
    let state = "Running";
    const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (init?.method === "POST") {
        state = "Paused";
        return json({ ok: true, message: "paused — in-flight items drain" });
      }

      if (url.includes("/failures") || url.includes("/audit")) return json([]);
      if (url.includes("/campaign/?")) return json([campaign({ state })]);
      return json(campaign({ state }));
    }) as typeof fetch;

    render(<CampaignPanel client={new AdminClient({ fetcher })} />);
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "pause" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "pause" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "pause" })).toBeNull());
    expect(screen.getByText("paused — in-flight items drain")).toBeInTheDocument();
  });

  it("a refusal from the pacer surfaces verbatim", async () => {
    const { client: api } = client({
      verb: { status: 400, body: { ok: false, message: "the campaign is already Completed — nothing to pause" } },
    });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("button", { name: "pause" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "pause" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("already Completed");
  });

  it("failed items point at the ONE repair home instead of growing a second one", async () => {
    const { client: api } = client({ failures: [{ seq: 42, error: "carrier rejected", completedAt: "2026-07-27T08:00:00Z" }] });
    render(<CampaignPanel client={api} />);

    await open();
    const detail = await screen.findByTestId("campaign-detail");
    expect(detail).toHaveTextContent("carrier rejected");
    expect(detail).toHaveTextContent("replay these items from the run console");
    expect(screen.queryByRole("button", { name: /replay/ })).toBeNull();
  });

  it("shows the verb log the server recorded", async () => {
    const { client: api } = client({
      audit: [{ id: 1, at: "2026-07-27T07:00:00Z", actor: "ops@example.com", action: "throttle", campaignId: campaign().id, detail: "tps 10 → 25" }],
    });
    render(<CampaignPanel client={api} />);

    await open();
    const detail = await screen.findByTestId("campaign-detail");
    expect(detail).toHaveTextContent("throttle");
    expect(detail).toHaveTextContent("tps 10 → 25");
  });

  it("a list that will not load says so instead of showing an empty governor", async () => {
    const fetcher = (async () => new Response("", { status: 503 })) as typeof fetch;
    render(<CampaignPanel client={new AdminClient({ fetcher })} />);

    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });

  it("refresh re-reads the list AND the open campaign — numbers move under a governor", async () => {
    let released = 100;
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (url.includes("/failures") || url.includes("/audit")) return json([]);
      released += 50;
      if (url.includes("/campaign/?")) return json([campaign({ releasedThrough: released })]);
      return json(campaign({ releasedThrough: released }));
    }) as typeof fetch;

    render(<CampaignPanel client={new AdminClient({ fetcher })} />);
    await open();
    await screen.findByTestId("campaign-detail");
    const before = screen.getByTestId("campaign-detail").textContent;

    await userEvent.click(screen.getByRole("button", { name: "Refresh" }));

    await waitFor(() => expect(screen.getByTestId("campaign-detail").textContent).not.toBe(before));
  });

  it("a verb that never reached the server is NOT reported as applied", async () => {
    const { client: api } = client({ verb: { status: 503, body: {} } });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("button", { name: "pause" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "pause" }));

    expect(await screen.findByText(/did not reach the server — it may not have been applied/)).toBeInTheDocument();
  });

  it("a campaign with no window releases around the clock, and says exactly that", async () => {
    const always = campaign({ windowStart: null, windowEnd: null, dailyQuota: null });
    const { client: api } = client({ detail: always, list: [always] });
    render(<CampaignPanel client={api} />);

    await open();
    const detail = await screen.findByTestId("campaign-detail");
    expect(detail).toHaveTextContent("around the clock");
    expect(detail).toHaveTextContent("Daily quotanone");
  });
});
