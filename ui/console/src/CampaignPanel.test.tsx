import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type CampaignInfo } from "./adminClient";
import { CampaignPanel, describeThrottle, formatInZone, resolveStart, startProblem, toThrottle } from "./CampaignPanel";

const emptyDraft = {
  tps: "", dailyQuota: "", maxInFlight: "", windowStart: "", windowEnd: "", timeZoneId: "",
  clearDailyQuota: false, clearWindow: false,
  excludedDays: [] as string[], endDate: "", maxAttempts: "", clearExcludedDays: false, clearEndDate: false, priority: "",
  startLocal: "", startChoice: "" as "" | "earlier" | "later", clearStartAt: false,
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

describe("R3.1: a scheduled start, read in the policy time zone", () => {
  const draft = { ...emptyDraft };

  it("one local time is usually one instant", () => {
    expect(resolveStart("2026-11-02T09:00", "Europe/Istanbul")).toEqual({
      kind: "ok",
      instant: { utc: "2026-11-02T06:00:00.000Z", offset: "UTC+03:00" },
    });
  });

  it("the spring-forward gap does not exist, so it is refused", () => {
    // Berlin skips 02:00–03:00 on 2026-03-29.
    expect(resolveStart("2026-03-29T02:30", "Europe/Berlin")).toEqual({ kind: "gap" });
    expect(startProblem({ ...draft, startLocal: "2026-03-29T02:30" }, "Europe/Berlin"))
      .toBe("02:30 on 2026-03-29 does not exist in Europe/Berlin: the clocks skip that hour. Choose another time.");
    expect(toThrottle({ ...draft, startLocal: "2026-03-29T02:30" }, "Europe/Berlin")).toEqual({});
  });

  it("the repeated hour is two instants, and the operator says which", () => {
    // Berlin repeats 02:00–03:00 on 2026-10-25: first at +02:00, then at +01:00.
    expect(resolveStart("2026-10-25T02:30", "Europe/Berlin")).toEqual({
      kind: "ambiguous",
      earlier: { utc: "2026-10-25T00:30:00.000Z", offset: "UTC+02:00" },
      later: { utc: "2026-10-25T01:30:00.000Z", offset: "UTC+01:00" },
    });
    const ambiguous = { ...draft, startLocal: "2026-10-25T02:30" };
    expect(startProblem(ambiguous, "Europe/Berlin")).toBe("02:30 on 2026-10-25 happens twice in Europe/Berlin: say which one.");
    expect(toThrottle(ambiguous, "Europe/Berlin")).toEqual({});
    expect(toThrottle({ ...ambiguous, startChoice: "later" }, "Europe/Berlin")).toEqual({ startAt: "2026-10-25T01:30:00.000Z" });
    expect(startProblem({ ...ambiguous, startChoice: "earlier" }, "Europe/Berlin")).toBeNull();
  });

  it("a zone changed in the same patch is the one the start is read in", () => {
    expect(toThrottle({ ...draft, startLocal: "2026-11-02T09:00", timeZoneId: "Europe/Berlin" }, "Europe/Istanbul"))
      .toEqual({ timeZoneId: "Europe/Berlin", startAt: "2026-11-02T08:00:00.000Z" });
  });

  it("an unreadable time or an unknown zone is refused, never guessed", () => {
    expect(resolveStart("tomorrow", "Europe/Berlin")).toEqual({ kind: "invalid" });
    expect(resolveStart("2026-11-02T09:00", "Mars/Olympus")).toEqual({ kind: "invalid" });
    expect(startProblem({ ...draft, startLocal: "2026-11-02T09:00" }, "Mars/Olympus")).toBe("2026-11-02T09:00 cannot be read in Mars/Olympus.");
    expect(formatInZone("2026-11-02T06:00:00Z", "Mars/Olympus")).toBe("2026-11-02T06:00:00Z");
  });

  it("clearing the start wins over a typed one, and the confirm line names both", () => {
    expect(toThrottle({ ...draft, startLocal: "2026-11-02T09:00", clearStartAt: true }, "Europe/Istanbul")).toEqual({ clearStartAt: true });
    expect(startProblem({ ...draft, startLocal: "2026-03-29T02:30", clearStartAt: true }, "Europe/Berlin")).toBeNull();
    expect(describeThrottle({ clearStartAt: true })).toBe("no scheduled start (it starts as soon as it is ready)");
    expect(describeThrottle({ startAt: "2026-11-02T06:00:00.000Z" }, "Europe/Istanbul"))
      .toBe("start 2026-11-02 09:00 Europe/Istanbul (2026-11-02T06:00:00.000Z)");
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

  it("R3.1: Ready is a state to filter by", async () => {
    const seen: string[] = [];
    const fetcher = (async (input: RequestInfo | URL) => {
      seen.push(String(input));
      return new Response(JSON.stringify([]), { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch;
    render(<CampaignPanel client={new AdminClient({ fetcher })} />);

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Ready/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(seen.some((url) => url.includes("state=Ready"))).toBe(true));
  });

  it("R3.1: a Ready campaign shows its start in the policy time zone, is live, and can have the start moved", async () => {
    const ready = campaign({ state: "Ready", releasedThrough: 0, startAt: "2026-11-02T06:00:00Z" });
    const { client: api, posted } = client({ list: [ready], detail: ready, verb: { status: 200, body: { ok: true, message: "policy updated" } } });
    render(<CampaignPanel client={api} />);

    await open();
    const detail = await screen.findByTestId("campaign-detail");
    expect(detail).toHaveTextContent("Start2026-11-02 09:00 Europe/Istanbul (2026-11-02T06:00:00Z)");
    expect(screen.getByRole("button", { name: "pause" })).toBeInTheDocument();   // Ready is live

    await userEvent.type(screen.getByLabelText("start"), "2026-11-03T10:30");
    await userEvent.click(screen.getByRole("button", { name: "throttle" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm throttle" });
    expect(dialog).toHaveTextContent("start 2026-11-03 10:30 Europe/Istanbul (2026-11-03T07:30:00.000Z)");
    await userEvent.click(within(dialog).getByRole("button", { name: "throttle" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(JSON.parse(String(posted[0].body))).toEqual({ startAt: "2026-11-03T07:30:00.000Z" });
  });

  it("R3.1: clearing the start posts the explicit clear", async () => {
    const ready = campaign({ state: "Ready", releasedThrough: 0, startAt: "2026-11-02T06:00:00Z" });
    const { client: api, posted } = client({ list: [ready], detail: ready });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("checkbox", { name: "clear start" }));
    await userEvent.click(screen.getByRole("button", { name: "throttle" }));
    await userEvent.click(within(screen.getByRole("alertdialog", { name: "confirm throttle" })).getByRole("button", { name: "throttle" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(JSON.parse(String(posted[0].body))).toEqual({ clearStartAt: true });
  });

  it("R3.1: a start in the spring-forward gap is refused before anything is sent", async () => {
    const ready = campaign({ state: "Ready", releasedThrough: 0, timeZoneId: "Europe/Berlin", startAt: null });
    const { client: api, posted } = client({ list: [ready], detail: ready });
    render(<CampaignPanel client={api} />);

    await open();
    expect(await screen.findByTestId("campaign-detail")).toHaveTextContent("Startas soon as it is ready");
    expect(screen.queryByRole("checkbox", { name: "clear start" })).toBeNull();   // nothing to clear
    await userEvent.type(screen.getByLabelText("start"), "2026-03-29T02:30");
    expect(screen.getByRole("alert")).toHaveTextContent("02:30 on 2026-03-29 does not exist in Europe/Berlin");
    expect(screen.queryByRole("button", { name: "throttle" })).toBeNull();
    expect(posted).toHaveLength(0);
  });

  it("R3.1: a start in the repeated hour asks which one, and sends the one chosen", async () => {
    const ready = campaign({ state: "Ready", releasedThrough: 0, timeZoneId: "Europe/Berlin", startAt: null });
    const { client: api, posted } = client({ list: [ready], detail: ready });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.type(await screen.findByLabelText("start"), "2026-10-25T02:30");
    expect(screen.getByRole("alert")).toHaveTextContent("02:30 on 2026-10-25 happens twice in Europe/Berlin: say which one.");
    expect(screen.queryByRole("button", { name: "throttle" })).toBeNull();

    await userEvent.click(screen.getByRole("radio", { name: "the second (UTC+01:00)" }));
    expect(screen.queryByRole("alert")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "throttle" }));
    await userEvent.click(within(screen.getByRole("alertdialog", { name: "confirm throttle" })).getByRole("button", { name: "throttle" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(JSON.parse(String(posted[0].body))).toEqual({ startAt: "2026-10-25T01:30:00.000Z" });
  });

  it("R3.1: once running, the start is shown but can no longer be moved", async () => {
    const running = campaign({ startAt: "2026-07-27T06:00:00Z" });
    const { client: api } = client({ list: [running], detail: running });
    render(<CampaignPanel client={api} />);

    await open();
    expect(await screen.findByTestId("campaign-detail")).toHaveTextContent("Start2026-07-27 09:00 Europe/Istanbul");
    expect(screen.queryByLabelText("start")).toBeNull();
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

describe("the campaign governor's remaining doors", () => {
  it("a paused campaign resumes through the frozen route", async () => {
    const paused = campaign({ state: "Paused" });
    const { client: api, posted } = client({ detail: paused, list: [paused], verb: { status: 200, body: { ok: true, message: "resumed" } } });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("button", { name: "resume" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm resume" });
    expect(dialog).toHaveTextContent("Release continues under the policy below");
    await userEvent.click(within(dialog).getByRole("button", { name: "resume" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/campaign/${paused.id}/resume`);
    expect(await screen.findByText("resumed")).toBeInTheDocument();
  });

  it("a paused campaign can still be aborted, and the reason travels", async () => {
    const paused = campaign({ state: "Paused" });
    const { client: api, posted } = client({ detail: paused, list: [paused] });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.click(await screen.findByRole("button", { name: "abort" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm abort" });
    await userEvent.type(within(dialog).getByLabelText("reason (required)"), "budget pulled");
    await userEvent.click(within(dialog).getByRole("button", { name: "abort" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/campaign/${paused.id}/abort`);
    expect(JSON.parse(String(posted[0].body))).toEqual({ reason: "budget pulled" });
  });

  it("every dial of the throttle form reaches the confirm line and the body — and a cleared form resets", async () => {
    const { client: api, posted } = client({ verb: { status: 200, body: { ok: true, message: "policy updated" } } });
    render(<CampaignPanel client={api} />);

    await open();
    await userEvent.type(await screen.findByLabelText("daily quota"), "900");
    await userEvent.type(screen.getByLabelText("max in-flight"), "12");
    await userEvent.type(screen.getByLabelText("max attempts"), "3");
    await userEvent.type(screen.getByLabelText("window start"), "08:30");
    await userEvent.type(screen.getByLabelText("window end"), "18:00");
    await userEvent.type(screen.getByLabelText("time zone"), "Europe/Berlin");
    await userEvent.type(screen.getByLabelText("end date"), "2026-12-31");
    // A day ticked and unticked again is NOT excluded; only what stays ticked travels.
    await userEvent.click(screen.getByLabelText("Sat"));
    await userEvent.click(screen.getByLabelText("Sun"));
    await userEvent.click(screen.getByLabelText("Sat"));
    await userEvent.click(screen.getByLabelText("clear window"));
    await userEvent.click(screen.getByLabelText("clear excluded days"));
    await userEvent.click(screen.getByLabelText("clear end date"));

    await userEvent.click(screen.getByRole("button", { name: "throttle" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm throttle" });
    expect(dialog).toHaveTextContent(
      "daily quota 900, max in-flight 12, window 08:30–18:00, time zone Europe/Berlin, no window (release around the clock), " +
        "excluded days Sunday, end date 2026-12-31, 3 attempts per item, no excluded days, no end date",
    );
    await userEvent.click(within(dialog).getByRole("button", { name: "throttle" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(JSON.parse(String(posted[0].body))).toEqual({
      dailyQuota: 900,
      maxInFlight: 12,
      maxAttempts: 3,
      windowStart: "08:30",
      windowEnd: "18:00",
      timeZoneId: "Europe/Berlin",
      clearWindow: true,
      excludedDays: ["Sunday"],
      endDate: "2026-12-31",
      clearExcludedDays: true,
      clearEndDate: true,
    });
    // Applied: the draft empties, so nothing is left to throttle.
    await waitFor(() => expect(screen.queryByRole("button", { name: "throttle" })).toBeNull());
    expect(screen.getByLabelText("daily quota")).toHaveValue(null);
  });

  it("the confirm line marks the open end of a half-given window", () => {
    expect(describeThrottle({ windowStart: "08:00" })).toBe("window 08:00–…");
    expect(describeThrottle({ windowEnd: "17:00" })).toBe("window …–17:00");
  });

  it("unticking a state and clearing the facet each re-read the list without it", async () => {
    const seen: string[] = [];
    const fetcher = (async (input: RequestInfo | URL) => {
      seen.push(String(input));
      return new Response(JSON.stringify([]), { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch;
    render(<CampaignPanel client={new AdminClient({ fetcher })} />);
    const lists = () => seen.filter((url) => url.includes("/campaign/?"));

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Running/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Paused/ }));
    await waitFor(() => expect(lists().at(-1)).toContain("state=Paused"));

    // Unticked, Running leaves the query; Paused stays.
    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: /Running/ }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("state=Running"));
    expect(lists().at(-1)).toContain("state=Paused");

    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("state="));
  });

  it("closing the sheet lets go of the campaign", async () => {
    const { client: api } = client();
    render(<CampaignPanel client={api} />);

    await open();
    await screen.findByTestId("campaign-detail");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("sheet")).toBeNull());
  });

  it("a campaign that will not open says which one, instead of an empty sheet", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/campaign/?")) {
        return new Response(JSON.stringify([campaign()]), { status: 200, headers: { "content-type": "application/json" } });
      }
      return new Response("", { status: 500 });
    }) as typeof fetch;
    render(<CampaignPanel client={new AdminClient({ fetcher })} />);

    await open();
    expect(await screen.findByText(`campaign ${campaign().id} could not be opened`)).toBeInTheDocument();
    expect(screen.queryByTestId("campaign-detail")).toBeNull();
  });
});
