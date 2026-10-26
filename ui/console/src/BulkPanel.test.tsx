import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type BulkBatchInfo, type BulkDefinitionStatus, type BulkRowError } from "./adminClient";
import { BulkPanel } from "./BulkPanel";

const batch = (over: Partial<BulkBatchInfo> = {}): BulkBatchInfo => ({
  id: "3f7a0c9e-0000-4000-8000-000000000001",
  definition: "payments",
  state: "Validated",
  tenant: "acme",
  totalRows: 120,
  validRows: 118,
  invalidRows: 2,
  executedRows: 0,
  failedRows: 0,
  runId: null,
  receivedAt: "2026-07-27T09:00:00Z",
  validatedAt: "2026-07-27T09:01:00Z",
  ...over,
});

const definition = (over: Partial<BulkDefinitionStatus> = {}): BulkDefinitionStatus => ({
  name: "payments",
  batchesByState: { Validated: 1, Completed: 4 },
  awaitingApproval: 1,
  oldestAwaitingApprovalSeconds: 7200,
  ...over,
});

const finding = (row: number): BulkRowError => ({
  id: row,
  batchId: batch().id,
  rowNumber: row,
  field: "Amount",
  message: "amount must be greater than zero",
});

interface Routes {
  batchStatus?: number;
  definitions?: unknown;
  batches?: unknown;
  batch?: unknown;
  errors?: (afterRow: number) => BulkRowError[];
  verb?: { status: number; body: unknown };
  upload?: { status: number; body: unknown };
}

function client(routes: Routes = {}) {
  const posted: { url: string; body: BodyInit | null | undefined }[] = [];
  const fetched: string[] = [];
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const json = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

    if (init?.method === "POST") {
      posted.push({ url, body: init.body });
      if (/\/bulk\/batches\/[^/?]+\?/.test(url)) {
        const uploaded = routes.upload ?? { status: 200, body: batch({ id: "new-batch", state: "Received" }) };
        return json(uploaded.body, uploaded.status);
      }

      const verb = routes.verb ?? { status: 200, body: { ok: true, message: "approved" } };
      return json(verb.body, verb.status);
    }

    fetched.push(url);
    if (url.includes("/errors?")) {
      const afterRow = Number(new URL(url, "http://x").searchParams.get("afterRow") ?? 0);
      return json(routes.errors ? routes.errors(afterRow) : [finding(4), finding(9)]);
    }

    if (url.includes("/bulk/batches?")) return json(routes.batches ?? [batch()]);
    if (url.includes("/bulk/batches/")) {
      if (routes.batchStatus) return json({ title: "not found" }, routes.batchStatus);
      return json(routes.batch ?? batch());
    }
    if (url.endsWith("/bulk/definitions")) return json(routes.definitions ?? [definition()]);
    return new Response("not found", { status: 404 });
  }) as typeof fetch;

  return { client: new AdminClient({ fetcher }), posted, fetched };
}

const openBatch = async (id = batch().id) => userEvent.click(await screen.findByRole("button", { name: id }));

describe("the bulk intake panel (upload → report → four-eyes gate)", () => {
  it("leads with what is waiting at the gate, and how long it has waited", async () => {
    const { client: api } = client();
    render(<BulkPanel client={api} />);

    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent("payments: 1 awaiting approval (oldest 2h)");
  });

  it("says nothing about the gate when nothing waits there", async () => {
    const { client: api } = client({
      definitions: [definition({ awaitingApproval: 0, oldestAwaitingApprovalSeconds: null })],
    });
    render(<BulkPanel client={api} />);

    await screen.findByRole("heading", { name: "Definitions" });
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("§7.9: the batch's run id LINKS to the run console when one exists to land on", async () => {
    const opened: string[] = [];
    const { client: api } = client({ batch: batch({ state: "Executed", runId: "r-42" }) });
    render(<BulkPanel client={api} onOpenRun={(id) => opened.push(id)} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "run r-42" }));
    expect(opened).toEqual(["r-42"]);
  });

  it("§7.9: without a run console to land on, the run id stays a plain FACT", async () => {
    const { client: api } = client({ batch: batch({ state: "Executed", runId: "r-42" }) });
    render(<BulkPanel client={api} />);

    await openBatch();
    expect(await screen.findByText("run r-42")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "run r-42" })).not.toBeInTheDocument();
  });

  it("§8.12: a committed batch ID reads THAT batch from the server", async () => {
    const wanted = batch({ id: "11111111-2222-4333-8444-555555555555", state: "Approved" });
    const { client: api, fetched } = client({ batch: wanted, batches: [batch()] });
    render(<BulkPanel client={api} />);

    const search = await screen.findByRole("textbox", { name: "Search by batch id" });
    await userEvent.type(search, "11111111-2222-4333-8444-555555555555{Enter}");

    // The lookup went to the single-batch route, and the table shows only that batch.
    await waitFor(() => expect(fetched.some((url) => url.includes("/bulk/batches/11111111"))).toBe(true));
    const section = screen.getByTestId("batches");
    expect(await within(section).findByText("11111111-2222-4333-8444-555555555555")).toBeInTheDocument();
    expect(within(section).queryByText(batch().id)).not.toBeInTheDocument();
  });

  it("§8.12: a search that HITS a broken service surfaces the failure — never an empty list", async () => {
    const { client: api } = client({ batchStatus: 500 });
    render(<BulkPanel client={api} />);
    await userEvent.type(await screen.findByRole("textbox", { name: "Search by batch id" }), "any{Enter}");
    // The keyset table's own error path answers, not a quiet "no batches".
    expect(await within(screen.getByTestId("batches")).findByText(/could not be loaded/)).toBeInTheDocument();
    expect(within(screen.getByTestId("batches")).queryByText(/No batches/)).not.toBeInTheDocument();
  });

  it("§8.12: an ID nobody has answers as an empty list, not an error", async () => {
    const { client: api } = client({ batchStatus: 404 });
    render(<BulkPanel client={api} />);
    await userEvent.type(await screen.findByRole("textbox", { name: "Search by batch id" }), "nope{Enter}");
    expect(await within(screen.getByTestId("batches")).findByText(/No batches/)).toBeInTheDocument();
  });

  it("R3: TWO states travel as repeated params — OR on the server, never merged here", async () => {
    const { client: api, fetched } = client();
    render(<BulkPanel client={api} />);
    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Validated/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Approved/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(fetched.some((url) => url.includes("state=Validated") && url.includes("state=Approved"))).toBe(true),
    );
  });

  it("filters batches by state, and RE-toggling the active state clears it", async () => {
    const { client: api, fetched } = client();
    render(<BulkPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Validated/ }));
    await userEvent.keyboard("{Escape}");

    await waitFor(() => expect(fetched.some((url) => url.includes("state=Validated"))).toBe(true));

    // The single-commit contract's other half: RE-toggling the active state clears it —
    // the next batch fetch carries no state filter at all.
    const before = fetched.length;
    await userEvent.click(screen.getByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Validated/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() => {
      const since = fetched.slice(before).filter((url) => url.includes("/batches?"));
      expect(since.length).toBeGreaterThan(0);
      expect(since.every((url) => !url.includes("state="))).toBe(true);
    });
  });

  it("R3 (#72): definitions travel as repeated ?definition= params to the SERVER, ANDed with state", async () => {
    const { client: api, fetched } = client({
      definitions: [definition(), definition({ name: "refunds", batchesByState: {}, awaitingApproval: 0 })],
    });
    render(<BulkPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: /Definition/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /payments/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /refunds/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(
        fetched.some((url) => url.includes("definition=payments") && url.includes("definition=refunds")),
      ).toBe(true),
    );

    // The filter narrows the QUERY the server answers — never the page client-side.
    await userEvent.click(screen.getByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Validated/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(
        fetched.some((url) => url.includes("definition=refunds") && url.includes("state=Validated")),
      ).toBe(true),
    );
  });

  it("opens a batch and shows the row ledger the server reported", async () => {
    const { client: api } = client();
    render(<BulkPanel client={api} />);

    await openBatch();

    const detail = await screen.findByTestId("batch-detail");
    expect(detail).toHaveTextContent("120");
    expect(detail).toHaveTextContent("118");
    // Two findings, same teaching message — the report lists both rows, not one.
    expect(within(detail).getAllByText("amount must be greater than zero")).toHaveLength(2);
  });

  it("walks the validation report by keyset — afterRow is the last row number seen", async () => {
    const pages: Record<number, BulkRowError[]> = {
      0: Array.from({ length: 100 }, (_, index) => finding(index + 1)),
      100: [finding(101)],
    };
    const { client: api, fetched } = client({ errors: (afterRow) => pages[afterRow] ?? [] });
    render(<BulkPanel client={api} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: /more/i }));

    await waitFor(() => expect(fetched.some((url) => url.includes("afterRow=100"))).toBe(true));
  });

  it("the gate appears only on a validated batch", async () => {
    const { client: api } = client({ batch: batch({ state: "Completed", invalidRows: 0 }), batches: [batch({ state: "Completed" })] });
    render(<BulkPanel client={api} />);

    await openBatch();
    await screen.findByTestId("batch-detail");

    expect(screen.queryByRole("button", { name: "approve" })).toBeNull();
    expect(screen.queryByRole("button", { name: "reject" })).toBeNull();
  });

  it("approving posts the frozen route through the confirm gate", async () => {
    const { client: api, posted } = client();
    render(<BulkPanel client={api} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "approve" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm approve" });
    await userEvent.click(within(dialog).getByRole("button", { name: "approve" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/bulk/batches/${batch().id}/approve`);
  });

  it("rejecting refuses to fire without the evidence note the contract demands", async () => {
    const { client: api, posted } = client();
    render(<BulkPanel client={api} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "reject" }));

    const dialog = screen.getByRole("alertdialog", { name: "confirm reject" });
    const confirm = within(dialog).getByRole("button", { name: "reject" });
    expect(confirm).toBeDisabled();

    await userEvent.type(within(dialog).getByLabelText("reason (required)"), "duplicate file");
    await userEvent.click(confirm);

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain(`/goldpath/admin/bulk/batches/${batch().id}/reject`);
    expect(JSON.parse(String(posted[0].body))).toEqual({ note: "duplicate file" });
  });

  it("a refusal from the engine surfaces verbatim — the console never paraphrases", async () => {
    const { client: api } = client({
      verb: { status: 400, body: { ok: false, message: "the batch is not awaiting approval" } },
    });
    render(<BulkPanel client={api} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "approve" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm approve" });
    await userEvent.click(within(dialog).getByRole("button", { name: "approve" }));

    expect(await screen.findByText("the batch is not awaiting approval")).toBeInTheDocument();
  });

  it("uploads the file as a RAW body to the definition's route", async () => {
    const { client: api, posted } = client();
    render(<BulkPanel client={api} />);

    await userEvent.upload(
      await screen.findByLabelText("batch file"),
      new File(["id,amount\n1,10"], "payments.csv", { type: "text/csv" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "upload" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm upload" });
    expect(dialog).toHaveTextContent("Upload payments.csv into payments?");
    await userEvent.click(within(dialog).getByRole("button", { name: "upload" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain("/goldpath/admin/bulk/batches/payments?fileName=payments.csv");
    expect(posted[0].body).toBeInstanceOf(File);
  });

  it("a failed upload never reads as success", async () => {
    const { client: api } = client({ upload: { status: 500, body: {} } });
    render(<BulkPanel client={api} />);

    await userEvent.upload(
      await screen.findByLabelText("batch file"),
      new File(["broken"], "payments.csv", { type: "text/csv" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "upload" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "upload" }));

    expect(await screen.findByText(/did not reach the server/)).toBeInTheDocument();
  });

  it("a definition list that will not load says so instead of showing an empty panel", async () => {
    const fetcher = (async () => new Response("", { status: 503 })) as typeof fetch;
    render(<BulkPanel client={new AdminClient({ fetcher })} />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/bulk definitions could not be loaded/i);
  });

  it("the gate's outcome OUTLIVES the gate — approving unmounts the buttons, not the message", async () => {
    // The server's truth changes under the panel: the batch leaves the gated state, so
    // the approve/reject controls disappear on the refresh that follows the verb.
    let state = "Validated";
    const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (init?.method === "POST") {
        state = "Approved";
        return json({ ok: true, message: "approved" });
      }

      if (url.includes("/errors?")) return json([]);
      if (url.includes("/bulk/batches?")) return json([batch({ state })]);
      if (url.includes("/bulk/batches/")) return json(batch({ state }));
      return json([definition()]);
    }) as typeof fetch;

    render(<BulkPanel client={new AdminClient({ fetcher })} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "approve" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "approve" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "approve" })).toBeNull());
    expect(screen.getByText("approved")).toBeInTheDocument();
  });

  it("a decision that never reached the server is NOT reported as recorded", async () => {
    // The verb envelope never came back — 503 is neither an ok nor a refusal, and the
    // operator must not walk away believing the gate moved.
    const { client: api } = client({ verb: { status: 503, body: {} } });
    render(<BulkPanel client={api} />);

    await openBatch();
    await userEvent.click(await screen.findByRole("button", { name: "approve" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "approve" }));

    expect(await screen.findByText(/did not reach the server — it may not have been recorded/)).toBeInTheDocument();
  });

  it("a decided batch carries its evidence: who, when, and why", async () => {
    const decided = batch({
      state: "Rejected",
      decidedAt: "2026-07-27T10:00:00Z",
      decidedBy: "ops@example.com",
      decisionNote: "line 3 is malformed",
    });
    const { client: api } = client({ batch: decided, batches: [decided] });
    render(<BulkPanel client={api} />);

    await openBatch();
    const detail = await screen.findByTestId("batch-detail");
    expect(detail).toHaveTextContent("Rejected by ops@example.com at 2026-07-27T10:00:00Z");
    expect(detail).toHaveTextContent("line 3 is malformed");
  });

  it("a definition with nothing in it says so, instead of showing a bare name", async () => {
    const { client: api } = client({ definitions: [definition({ batchesByState: {}, awaitingApproval: 0 })] });
    render(<BulkPanel client={api} />);

    expect(await screen.findByText("no batches yet")).toBeInTheDocument();
  });
});

describe("the bulk intake panel — the remaining doors", () => {
  it("'choose file' opens the hidden file input — the styled button is only its face", async () => {
    const { client: api } = client();
    render(<BulkPanel client={api} />);

    const input = (await screen.findByLabelText("batch file")) as HTMLInputElement;
    const picked = vi.spyOn(input, "click");
    await userEvent.click(screen.getByRole("button", { name: "choose file" }));
    expect(picked).toHaveBeenCalledTimes(1);
  });

  it("clearing the state facet re-reads the batches with no state at all", async () => {
    const { client: api, fetched } = client();
    render(<BulkPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Validated/ }));
    const lists = () => fetched.filter((url) => url.includes("/batches?"));
    await waitFor(() => expect(lists().at(-1)).toContain("state=Validated"));
    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("state="));
  });

  it("unticking a definition and clearing the facet each narrow the query back", async () => {
    const { client: api, fetched } = client({
      definitions: [definition(), definition({ name: "refunds", batchesByState: {}, awaitingApproval: 0 })],
    });
    render(<BulkPanel client={api} />);
    const lists = () => fetched.filter((url) => url.includes("/batches?"));

    await userEvent.click(await screen.findByRole("button", { name: /Definition/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /payments/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /refunds/ }));
    await waitFor(() => expect(lists().at(-1)).toContain("definition=refunds"));

    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: /payments/ }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("definition=payments"));
    expect(lists().at(-1)).toContain("definition=refunds");

    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("definition="));
  });

  it("closing the sheet lets go of the batch", async () => {
    const { client: api } = client();
    render(<BulkPanel client={api} />);

    await openBatch();
    await screen.findByTestId("batch-detail");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("batch-detail")).toBeNull());
  });

  it("a batch that will not open says which one", async () => {
    const { client: api } = client({ batchStatus: 500 });
    render(<BulkPanel client={api} />);

    await openBatch();
    expect(await screen.findByText(`batch ${batch().id} could not be opened`)).toBeInTheDocument();
    expect(screen.queryByTestId("batch-detail")).toBeNull();
  });
});
