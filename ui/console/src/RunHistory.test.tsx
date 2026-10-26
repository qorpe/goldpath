import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type RunDetail, type RunSummary } from "./adminClient";
import { RunHistory } from "./RunHistory";

const run = (over: Partial<RunSummary> = {}): RunSummary => ({
  id: "run-9f21",
  jobName: "eod-reconciliation",
  status: "Completed",
  startedAt: "2026-07-26T10:00:00Z",
  finishedAt: "2026-07-26T10:05:00Z",
  totalChunks: 10,
  completedChunks: 10,
  failedChunks: 0,
  totalItems: 1000,
  itemFailures: 0,
  startedBy: "node-a",
  triggeredBy: "Scheduled",
  ...over,
});

const detail = (over: Partial<RunDetail> = {}): RunDetail => ({
  run: run(),
  chunksByStatus: { Completed: 9, Failed: 1 },
  openFailures: [
    { id: 1, runId: "run-9f21", chunkIndex: 1, itemKey: "ORD-77", reason: "bank refused", failedAt: "2026-07-26T10:04:00Z" },
  ],
  ...over,
});

function api(options: { runs?: (url: URL) => RunSummary[]; run?: RunDetail } = {}) {
  const asked: URL[] = [];
  const sent: { url: string; body: BodyInit | null | undefined }[] = [];
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
    if (init?.method && init.method !== "GET") {
      sent.push({ url, body: init.body });
      return json({ ok: true, message: "done" });
    }

    if (url.includes("/jobs/runs/")) return json(options.run ?? detail());
    if (url.includes("/runs?")) {
      const parsed = new URL(url, "http://service.local");
      asked.push(parsed);
      return json(options.runs ? options.runs(parsed) : [run()]);
    }

    return new Response("not found", { status: 404 });
  }) as typeof fetch;
  return { client: new AdminClient({ fetcher }), asked, sent };
}

describe("the run history (contract R2.4)", () => {
  it("shows WHO put each run on the schedule and WHICH node ran it", async () => {
    const { client } = api({ runs: () => [run({ triggeredBy: "Manual", startedBy: "node-b" })] });
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    // The two questions a run list is asked the morning after.
    expect(await screen.findByText(/Manual/)).toBeInTheDocument();
    expect(screen.getByText(/node-b/)).toBeInTheDocument();
  });

  it("a run from before the column exists reads as NOT RECORDED, never as Scheduled", async () => {
    const { client } = api({ runs: () => [run({ triggeredBy: null })] });
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    // Guessing "Scheduled" would invent the absence of an operator we cannot vouch for.
    expect(await screen.findByText(/not recorded/)).toBeInTheDocument();
  });

  it("the state filter travels to the server — the console never narrows a page locally", async () => {
    const user = userEvent.setup();
    const { client, asked } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");

    await user.click(screen.getByRole("button", { name: /State/ }));
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));

    // Filtering client-side would narrow ONE take-bounded page and read as "no failures"
    // while more sat behind it.
    await waitFor(() => expect(asked.at(-1)!.searchParams.get("status")).toBe("Failed"));

    // R3: a second selection APPENDS — repeated params, OR on the server.
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Running/ }));
    await waitFor(() => expect(asked.at(-1)!.searchParams.getAll("status")).toEqual(["Failed", "Running"]));
  });

  it("a date window covers the WHOLE of the last day, not its first instant", async () => {
    const user = userEvent.setup();
    const { client, asked } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");

    await user.type(screen.getByLabelText("From"), "2026-07-27");
    await user.type(screen.getByLabelText("To"), "2026-07-27");

    await waitFor(() => {
      const last = asked.at(-1)!;
      expect(last.searchParams.get("from")).toBe("2026-07-27T00:00:00Z");
      // "from the 27th to the 27th" must not return an empty list.
      expect(last.searchParams.get("to")).toBe("2026-07-27T23:59:59Z");
    });
  });

  it("walks with a KEYSET cursor: the next page continues after the last row seen", async () => {
    const user = userEvent.setup();
    // A FULL page is what keeps the walk going: a short one is the end of the list, so
    // the first page has to fill `take` for there to be a second at all.
    const { client, asked } = api({
      runs: (url) => {
        const take = Number(url.searchParams.get("take"));
        if (url.searchParams.get("afterId") === null) {
          return Array.from({ length: take }, (_, index) => run({ id: `r-${take - index}` }));
        }

        return [run({ id: "r-tail" })];
      },
    });
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    const take = Number(asked[0].searchParams.get("take"));
    await screen.findByRole("button", { name: "r-1" });

    await user.click(screen.getByRole("button", { name: /load more/i }));

    // The cursor is the LAST row we were handed, not a page number.
    await waitFor(() => expect(asked.at(-1)!.searchParams.get("afterId")).toBe("r-1"));
    expect(await screen.findByRole("button", { name: "r-tail" })).toBeInTheDocument();
    expect(take).toBeGreaterThan(0);
  });

  it("clearing the filters asks again without them", async () => {
    const user = userEvent.setup();
    const { client, asked } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");
    await user.click(screen.getByRole("button", { name: /State/ }));
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));
    await waitFor(() => expect(asked.at(-1)!.searchParams.get("status")).toBe("Failed"));

    await user.click(screen.getByRole("button", { name: "clear filters" }));

    await waitFor(() => expect(asked.at(-1)!.searchParams.get("status")).toBeNull());
  });

  it("an empty result says whether the FILTERS emptied it", async () => {
    const user = userEvent.setup();
    const { client } = api({ runs: (url) => (url.searchParams.get("status") ? [] : [run()]) });
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");

    await user.click(screen.getByRole("button", { name: /State/ }));
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));

    expect(await screen.findByText(/No run matches these filters/)).toBeInTheDocument();
  });

  it("opening a run shows its chunk breakdown and repair queue", async () => {
    const user = userEvent.setup();
    const { client } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} now={new Date("2026-07-26T10:06:00Z")} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));

    const panel = await screen.findByTestId("run-detail");
    expect(panel).toHaveTextContent("Completed: 9");
    expect(panel).toHaveTextContent("ORD-77");
    expect(panel).toHaveTextContent("bank refused");
  });

  it("replaying posts the frozen route with NO body — the server scopes it, not the UI", async () => {
    const user = userEvent.setup();
    const { client, sent } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));
    await user.click(await screen.findByRole("button", { name: "replay-items" }));

    const dialog = screen.getByRole("alertdialog");
    expect(dialog).toHaveTextContent(/Replay all open repair items/);
    await user.click(dialog.querySelector("button")!);

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0].url).toContain("/goldpath/admin/jobs/runs/run-9f21/replay-items");
    expect(sent[0].body).toBeUndefined();
  });

  it("replay-items appears only when there is something to replay", async () => {
    const user = userEvent.setup();
    const { client } = api({ run: detail({ openFailures: [] }) });
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));
    await screen.findByTestId("run-detail");

    expect(screen.queryByRole("button", { name: "replay-items" })).toBeNull();
    expect(screen.getByText(/Repair queue — empty/)).toBeInTheDocument();
  });

  it("a run that cannot be opened says so instead of showing a blank panel", async () => {
    const user = userEvent.setup();
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/jobs/runs/")) return new Response("", { status: 503 });
      return new Response(JSON.stringify([run()]), { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch;
    render(<RunHistory client={new AdminClient({ fetcher })} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));

    expect(await screen.findByText(/could not be opened/)).toBeInTheDocument();
  });

  it("the job search commits to the SERVER through the contract's ?job=", async () => {
    const user = userEvent.setup();
    const { client, asked } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");

    await user.type(screen.getByLabelText("Search by job"), "eod{Enter}");

    // A client-side narrow of one loaded page would read as "no runs" while more sat
    // behind the take bound — the search is a FILTER, so it travels.
    await waitFor(() => expect(asked.at(-1)!.searchParams.get("job")).toBe("eod"));
  });
});

describe("the run history — the remaining doors", () => {
  it("rerun posts the frozen route for THAT run, through the confirm", async () => {
    const user = userEvent.setup();
    const changed = vi.fn();
    const { client, sent } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={changed} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));
    await user.click(await screen.findByRole("button", { name: "rerun" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm rerun" });
    expect(dialog).toHaveTextContent("Rerun run-9f21?");
    await user.click(within(dialog).getByRole("button", { name: "rerun" }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0].url).toContain("/goldpath/admin/jobs/runs/run-9f21/rerun");
    await waitFor(() => expect(changed).toHaveBeenCalled());
  });

  it("unticking a state and clearing the facet each re-ask without it", async () => {
    const user = userEvent.setup();
    const { client, asked } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);
    await screen.findByText("eod-reconciliation");

    await user.click(screen.getByRole("button", { name: /State/ }));
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));
    await user.click(await screen.findByRole("menuitemcheckbox", { name: /Running/ }));
    await waitFor(() => expect(asked.at(-1)!.searchParams.getAll("status")).toEqual(["Failed", "Running"]));

    await user.click(screen.getByRole("menuitemcheckbox", { name: /Failed/ }));
    await waitFor(() => expect(asked.at(-1)!.searchParams.getAll("status")).toEqual(["Running"]));

    await user.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(asked.at(-1)!.searchParams.getAll("status")).toEqual([]));
  });

  it("closing the sheet lets go of the run", async () => {
    const user = userEvent.setup();
    const { client } = api();
    render(<RunHistory client={client} fleet="it-cluster" refreshToken={0} onChanged={() => {}} />);

    await user.click(await screen.findByRole("button", { name: "run-9f21" }));
    await screen.findByTestId("run-detail");
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("run-detail")).toBeNull());
  });
});
