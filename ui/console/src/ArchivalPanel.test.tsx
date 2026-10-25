import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type ArchiveEntry, type ChainFinding, type LegalHold } from "./adminClient";
import { ArchivalPanel } from "./ArchivalPanel";

const entry = (over: Partial<ArchiveEntry> = {}): ArchiveEntry => ({
  id: 1,
  definition: "policies",
  aggregateKey: "P-42",
  tenant: "acme",
  document: '{"policyNo":"P-42","holder":"Ada Lovelace"}',
  schemaVersion: 1,
  dueAt: "2026-01-01T00:00:00Z",
  archivedAt: "2026-01-02T00:00:00Z",
  chainIndex: 17,
  contentHash: "cccc1111",
  chainHash: "hhhh2222",
  previousHash: "pppp3333",
  ...over,
});

const hold = (over: Partial<LegalHold> = {}): LegalHold => ({
  id: 1,
  definition: "policies",
  aggregateKey: "P-42",
  caseReference: "CASE-2026-7",
  placedBy: "legal@example.com",
  placedAt: "2026-07-27T04:00:00Z",
  ...over,
});

interface Routes {
  definitions?: unknown;
  entry?: { status: number; body?: unknown };
  holds?: unknown;
  erasures?: unknown;
  verb?: { status: number; body: unknown };
  verify?: ChainFinding[];
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
      if (url.includes("/verify")) return json(routes.verify ?? []);
      const verb = routes.verb ?? { status: 200, body: { ok: true, message: "hold placed" } };
      return json(verb.body, verb.status);
    }

    fetched.push(url);
    if (url.includes("/holds")) return json(routes.holds ?? [hold()]);
    if (url.includes("/erasures")) return json(routes.erasures ?? []);
    if (url.includes("/entries/")) {
      const answer = routes.entry ?? { status: 200, body: entry() };
      return answer.status === 404 ? new Response("", { status: 404 }) : json(answer.body, answer.status);
    }

    if (url.includes("/archival/definitions")) {
      return json(routes.definitions ?? [
        { name: "policies", entries: 1200, dueBacklog: 3, activeHolds: 1, chainHead: 1200, purgedThrough: 400 },
      ]);
    }

    return new Response("not found", { status: 404 });
  }) as typeof fetch;

  return { client: new AdminClient({ fetcher }), posted, fetched };
}

const retrieve = async (key = "P-42") => {
  await userEvent.type(await screen.findByLabelText("aggregate key"), key);
  await userEvent.click(screen.getByRole("button", { name: "retrieve" }));
};

const NOW = new Date("2026-07-27T06:00:00Z");

describe("the archival panel (chain health, retrieval, lifecycle)", () => {
  it("shows what each archive holds and how much of it is still provable", async () => {
    const { client: api } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    // The definitions are a standard TABLE now (v1.1 §7.4): numbers live in their own
    // labelled columns, so the words moved to the headers.
    const row = (await screen.findAllByText("policies"))[0].closest("tr")!;
    expect(row).toHaveTextContent("1200");
    expect(row).toHaveTextContent("3");
    expect(row).toHaveTextContent("head 1200 · purged through 400");
  });

  it("verification with no findings is stated as the good news it is", async () => {
    const { client: api, posted } = client({ verify: [] });
    render(<ArchivalPanel client={api} now={NOW} />);

    await userEvent.click(await screen.findByRole("button", { name: "verify policies" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "verify policies" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain("/goldpath/admin/archival/definitions/policies/verify");
    expect(await screen.findByTestId("chain-findings")).toHaveTextContent("the chain verifies");
  });

  it("a broken chain names every finding — never a bare 'failed'", async () => {
    const { client: api } = client({
      verify: [{ definition: "policies", chainIndex: 88, aggregateKey: "P-88", problem: "content hash does not match the sealed chain hash" }],
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    await userEvent.click(await screen.findByRole("button", { name: "verify policies" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "verify policies" }));

    const findings = await screen.findByTestId("chain-findings");
    expect(findings).toHaveTextContent("does NOT verify");
    expect(findings).toHaveTextContent("content hash does not match the sealed chain hash");
    expect(findings).toHaveTextContent("P-88");
  });

  it("retrieves by key and shows the tamper evidence around the entry", async () => {
    const { client: api, fetched } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();

    const panel = await screen.findByTestId("archive-entry");
    expect(fetched.some((url) => url.includes("/entries/policies/P-42"))).toBe(true);
    expect(panel).toHaveTextContent("hhhh2222");   // sealed chain hash
    expect(panel).toHaveTextContent("pppp3333");   // previous hash
    expect(panel).toHaveTextContent("17");         // chain index
  });

  it("a missing key is an ANSWER, not an error", async () => {
    const { client: api } = client({ entry: { status: 404 } });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve("P-does-not-exist");

    expect(await screen.findByText(/No entry for/)).toHaveTextContent("it may never have been archived, or it may have been purged");
    expect(screen.queryByTestId("archive-entry")).toBeNull();
  });

  it("the archived document is hidden until the operator asks for it", async () => {
    const { client: api } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await screen.findByTestId("archive-entry");
    expect(screen.queryByText(/Ada Lovelace/)).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "reveal document" }));
    expect(screen.getByText(/Ada Lovelace/)).toBeInTheDocument();
  });

  it("a hold demands the case reference the contract binds", async () => {
    const { client: api, posted } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await userEvent.click(await screen.findByRole("button", { name: "hold" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm hold" });
    expect(within(dialog).getByRole("button", { name: "hold" })).toBeDisabled();

    await userEvent.type(within(dialog).getByLabelText("case reference (required)"), "CASE-2026-7");
    await userEvent.click(within(dialog).getByRole("button", { name: "hold" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain("/goldpath/admin/archival/entries/policies/P-42/hold");
    expect(JSON.parse(String(posted[0].body))).toEqual({ caseReference: "CASE-2026-7" });
  });

  it("erasure names what it does and cannot fire without the subject", async () => {
    const { client: api, posted } = client({ verb: { status: 200, body: { ok: true, message: "erased 1 entry" } } });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await userEvent.click(await screen.findByRole("button", { name: "erase" }));
    const dialog = screen.getByRole("alertdialog", { name: "confirm erase" });
    expect(dialog).toHaveTextContent("cannot be undone");
    expect(dialog).toHaveTextContent("stays in the chain, redacted");
    expect(within(dialog).getByRole("button", { name: "erase" })).toBeDisabled();

    await userEvent.type(within(dialog).getByLabelText("subject key (required)"), "customer:9");
    await userEvent.click(within(dialog).getByRole("button", { name: "erase" }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0].url).toContain("/entries/policies/P-42/erase");
    expect(JSON.parse(String(posted[0].body))).toEqual({ subjectKey: "customer:9", detail: null });
  });

  it("an erased entry explains WHY its hashes diverge — redaction, not tamper", async () => {
    const { client: api } = client({
      entry: { status: 200, body: entry({ erasedAt: "2026-07-20T00:00:00Z", preErasureContentHash: "cccc0000" }) },
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    const panel = await screen.findByTestId("archive-entry");
    expect(panel).toHaveTextContent("differs from the sealed one BY DESIGN");
    expect(panel).toHaveTextContent("cccc0000");
  });

  it("a refusal from the engine surfaces verbatim", async () => {
    const { client: api } = client({
      verb: { status: 400, body: { ok: false, message: "the entry is under legal hold — lift it before erasing" } },
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await userEvent.click(await screen.findByRole("button", { name: "lift-hold" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "lift-hold" }));

    expect(await screen.findByText(/lift it before erasing/)).toBeInTheDocument();
  });

  it("the hold list ages each hold and can include the lifted ones", async () => {
    const { client: api, fetched } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    const row = await screen.findByRole("row", { name: /CASE-2026-7/ });
    expect(row).toHaveTextContent("2h ago");

    await userEvent.click(screen.getByLabelText("include lifted"));
    await waitFor(() => expect(fetched.some((url) => url.includes("includeLifted=true"))).toBe(true));
  });

  it("a definition list that will not load says so instead of showing an empty panel", async () => {
    const fetcher = (async () => new Response("", { status: 503 })) as typeof fetch;
    render(<ArchivalPanel client={new AdminClient({ fetcher })} now={NOW} />);

    expect((await screen.findAllByRole("alert"))[0]).toHaveTextContent(/archive definitions could not be loaded/i);
  });

  it("a verification that cannot RUN says so — it never leaves the old verdict standing", async () => {
    let verifies = 0;
    const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (init?.method === "POST" && url.includes("/verify")) {
        verifies += 1;
        // The first verification succeeds; the second cannot reach the server.
        return verifies === 1 ? json([]) : new Response("", { status: 503 });
      }

      if (url.includes("/holds") || url.includes("/erasures")) return json([]);
      return json([{ name: "policies", entries: 10, dueBacklog: 0, activeHolds: 0, chainHead: 10, purgedThrough: 0 }]);
    }) as typeof fetch;

    render(<ArchivalPanel client={new AdminClient({ fetcher })} now={NOW} />);

    const runVerify = async () => {
      await userEvent.click(await screen.findByRole("button", { name: "verify policies" }));
      await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "verify policies" }));
    };

    await runVerify();
    expect(await screen.findByTestId("chain-findings")).toHaveTextContent("the chain verifies");

    await runVerify();
    expect(await screen.findByText(/the verification could not be run/)).toBeInTheDocument();
    // The previous "verifies" verdict is GONE — an unknown chain must not read as a proven one.
    expect(screen.queryByTestId("chain-findings")).toBeNull();
  });

  it("a lifecycle verb that never reached the server is NOT reported as applied", async () => {
    const { client: api } = client({ verb: { status: 503, body: {} } });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await userEvent.click(await screen.findByRole("button", { name: "lift-hold" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "lift-hold" }));

    expect(await screen.findByText(/did not reach the server — it may not have been applied/)).toBeInTheDocument();
  });

  it("switching archive clears the open entry — a key means nothing outside its archive", async () => {
    const { client: api } = client({
      definitions: [
        { name: "policies", entries: 10, dueBacklog: 0, activeHolds: 0, chainHead: 10, purgedThrough: 0 },
        { name: "claims", entries: 4, dueBacklog: 0, activeHolds: 0, chainHead: 4, purgedThrough: 0 },
      ],
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve();
    await screen.findByTestId("archive-entry");

    await userEvent.click(screen.getByRole("combobox", { name: "archive" }));
    await userEvent.click(await screen.findByRole("option", { name: "claims" }));

    // The policies entry must not linger under the claims archive's name.
    expect(screen.queryByTestId("archive-entry")).toBeNull();
    expect(screen.queryByText(/No entry for/)).toBeNull();
  });

  it("a lifted hold shows who lifted it and when — the row stays as evidence", async () => {
    const { client: api } = client({
      holds: [hold({ liftedBy: "legal@example.com", liftedAt: "2026-07-27T05:00:00Z" })],
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    const row = await screen.findByRole("row", { name: /CASE-2026-7/ });
    expect(row).toHaveTextContent("lifted by legal@example.com at 2026-07-27T05:00:00Z");
    expect(within(row).queryByText("Held")).toBeNull();
  });
});

describe("the archival panel — the remaining doors", () => {
  it("each erasure row says who asked, when, how much it touched — and a dash for no detail", async () => {
    const { client: api } = client({
      erasures: [
        { id: 1, subjectKey: "CUST-9", requestedBy: "dpo@example.com", requestedAt: "2026-07-20T00:00:00Z", entriesAffected: 4, detail: "subject request 2026/17" },
        { id: 2, subjectKey: "CUST-10", requestedBy: "dpo@example.com", requestedAt: "2026-07-21T00:00:00Z", entriesAffected: 1, detail: null },
      ],
    });
    render(<ArchivalPanel client={api} now={NOW} />);

    const first = await screen.findByRole("row", { name: /CUST-9/ });
    expect(first).toHaveTextContent("dpo@example.com");
    expect(first).toHaveTextContent("2026-07-20T00:00:00Z");
    expect(first).toHaveTextContent("4");
    expect(first).toHaveTextContent("subject request 2026/17");
    expect(screen.getByRole("row", { name: /CUST-10/ })).toHaveTextContent("—");
  });

  it("a blank key retrieves nothing — no request for an empty id", async () => {
    const { client: api, fetched } = client();
    render(<ArchivalPanel client={api} now={NOW} />);

    await userEvent.type(await screen.findByLabelText("aggregate key"), "   ");
    await userEvent.click(screen.getByRole("button", { name: "retrieve" }));
    expect(fetched.some((url) => url.includes("/entries/"))).toBe(false);
    expect(screen.queryByTestId("archive-entry")).toBeNull();
  });

  it("an entry that cannot be read says which key, instead of an empty answer", async () => {
    const { client: api } = client({ entry: { status: 500, body: {} } });
    render(<ArchivalPanel client={api} now={NOW} />);

    await retrieve("P-42");
    expect(await screen.findByText("entry P-42 could not be read")).toBeInTheDocument();
    expect(screen.queryByTestId("archive-entry")).toBeNull();
  });
});
