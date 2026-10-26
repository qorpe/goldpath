import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type NotificationInfo, type NotificationTemplateStatus } from "./adminClient";
import { NotificationPanel, retentionWords } from "./NotificationPanel";

const notification = (over: Partial<NotificationInfo> = {}): NotificationInfo => ({
  id: "n1a2b3c4-0000-4000-8000-000000000001",
  dedupKey: "renewal:P-42:2026-08",
  template: "policy-renewal",
  templateHash: "9f21c0b5e4d3a2f1",
  channel: "email",
  // The server masks BOTH halves (`a***@e***`) — the fixture mirrors the real
  // output, not a friendlier invention.
  maskedRecipient: "a***@e***",
  culture: "tr",
  state: "Sent",
  attempts: 1,
  requestedAt: "2026-07-27T06:00:00Z",
  sentAt: "2026-07-27T06:00:30Z",
  ...over,
});

const template = (over: Partial<NotificationTemplateStatus> = {}): NotificationTemplateStatus => ({
  key: "policy-renewal",
  hash: "9f21c0b5e4d3a2f1c0de",
  deleteBodyAfter: "90.00:00:00",
  byState: { Sent: 12, Failed: 1 },
  oldestRequestedSeconds: 300,
  ...over,
});

interface Routes {
  templates?: unknown;
  list?: unknown;
  failures?: unknown;
  suppressions?: unknown;
}

function client(routes: Routes = {}) {
  const fetched: string[] = [];
  const fetcher = (async (input: RequestInfo | URL) => {
    const url = String(input);
    fetched.push(url);
    const json = (body: unknown) =>
      new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });

    // The by-id route must be matched BEFORE the list route it is nested under.
    if (/\/notifications\/[^/?]+$/.test(url)) {
      const id = url.split("/").pop();
      const all = [
        notification(),
        notification({ state: "Failed", id: "failed-1", attempts: 3, detail: "the relay refused: mailbox full", failedAt: "2026-07-27T06:02:00Z" }),
        notification({ state: "Suppressed", id: "suppressed-1", detail: "suppressed by the MaySend hook — suppression is evidence too" }),
        ...(Array.isArray(routes.list) ? (routes.list as NotificationInfo[]) : []),
      ];
      return json(all.find((row) => row.id === id) ?? notification());
    }

    if (url.includes("/failures")) return json(routes.failures ?? [notification({ state: "Failed", id: "failed-1", attempts: 3, detail: "the relay refused: mailbox full", failedAt: "2026-07-27T06:02:00Z" })]);
    if (url.includes("/suppressions")) return json(routes.suppressions ?? [notification({ state: "Suppressed", id: "suppressed-1", detail: "suppressed by the MaySend hook — suppression is evidence too" })]);
    if (url.includes("/notifications")) return json(routes.list ?? [notification()]);
    if (url.includes("/templates")) return json(routes.templates ?? [template()]);
    return new Response("not found", { status: 404 });
  }) as typeof fetch;

  return { client: new AdminClient({ fetcher }), fetched };
}

describe("the retention promise, in words an operator reads", () => {
  it("spells a .NET TimeSpan out in days", () => {
    expect(retentionWords("90.00:00:00")).toBe("body deleted after 90d");
  });

  it("falls back to hours and minutes", () => {
    expect(retentionWords("06:00:00")).toBe("body deleted after 6h");
    expect(retentionWords("00:30:00")).toBe("body deleted after 30m");
  });

  it("says plainly when the body is kept forever", () => {
    expect(retentionWords(null)).toBe("kept");
  });
});

describe("the notification evidence panel (read-only by contract)", () => {
  it("shows each template with the hash that proves WHICH text was sent", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    // The template name also appears in the filter's <option> list and in every row, so
    // the assertion anchors on what only the template line carries.
    expect(await screen.findByText("9f21c0b5e4d3")).toHaveAttribute("title", "9f21c0b5e4d3a2f1c0de");
    expect(screen.getByText("body deleted after 90d")).toBeInTheDocument();
    expect(screen.getByText("Sent: 12")).toBeInTheDocument();
  });

  it("leads with the oldest request still waiting", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    expect(await screen.findByRole("status")).toHaveTextContent("policy-renewal: oldest request waiting 5m");
  });

  it("offers NO verbs — requesting is the app's, re-sending is the run console's", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    await screen.findByText("9f21c0b5e4d3");
    expect(screen.queryByRole("button", { name: /resend|retry|send/i })).toBeNull();
    expect(screen.getByText(/Requesting belongs to the app/)).toBeInTheDocument();
  });

  it("filters the broad list through the contract's own parameters", async () => {
    const { client: api, fetched } = client();
    render(<NotificationPanel client={api} />);

    // R3: TWO states in one visit travel as REPEATED params — OR on the server.
    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Suppressed/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(fetched.some((url) => url.includes("state=Failed") && url.includes("state=Suppressed"))).toBe(true),
    );

    // The template filter ANDs on top of the state OR.
    await userEvent.click(screen.getByRole("button", { name: /Template/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /policy-renewal/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(fetched.some((url) => url.includes("state=Failed") && url.includes("template=policy-renewal"))).toBe(true),
    );

    // Un-toggling BOTH states empties the filter: the follow-up fetch drops state=
    // entirely while the untouched template filter stays.
    const before = fetched.length;
    await userEvent.click(screen.getByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Suppressed/ }));
    await userEvent.keyboard("{Escape}");
    await waitFor(() => {
      const since = fetched.slice(before).filter((url) => url.includes("/notifications?"));
      expect(since.length).toBeGreaterThan(0);
      expect(since.at(-1)).not.toContain("state=");
      expect(since.at(-1)).toContain("template=policy-renewal");
    });
  });

  it("each focused lens reads the contract's OWN route, not a re-filtered list", async () => {
    const { client: api, fetched } = client();
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "Failures" }));
    await waitFor(() => expect(fetched.some((url) => url.includes("/notification/failures"))).toBe(true));

    await userEvent.click(screen.getByRole("button", { name: "Suppressions" }));
    await waitFor(() => expect(fetched.some((url) => url.includes("/notification/suppressions"))).toBe(true));
  });

  it("a suppression carries its reason — suppression is evidence too", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "Suppressions" }));
    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));

    const detail = await screen.findByTestId("notification-detail");
    expect(detail).toHaveTextContent("suppressed by the MaySend hook");
  });

  it("a failure shows the transport's own words and the attempts it took", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "Failures" }));
    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));

    const detail = await screen.findByTestId("notification-detail");
    expect(detail).toHaveTextContent("the relay refused: mailbox full");
    expect(detail).toHaveTextContent("3");
  });

  it("the row shows the MASKED recipient and nothing more — the console never has the address", async () => {
    const { client: api } = client();
    const { container } = render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    await screen.findByTestId("notification-detail");

    // The full local part never appears anywhere in the DOM: the API masked it, and the
    // console has nothing else to show.
    // Neither the local part nor the domain is recoverable from the DOM.
    expect(container.textContent).not.toMatch(/alice@|a[a-z]+@|@example\.com/);
    expect(within(screen.getByTestId("notification-detail")).getByText("renewal:P-42:2026-08")).toBeInTheDocument();
  });

  it("says when the body was deleted — the retention promise, kept and recorded", async () => {
    const { client: api } = client({ list: [notification({ id: "deleted-1", bodyDeletedAt: "2026-10-25T00:00:00Z" })] });
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    expect(await screen.findByTestId("notification-detail")).toHaveTextContent("deleted 2026-10-25T00:00:00Z");
  });

  it("a template list that will not load says so instead of showing an empty panel", async () => {
    const fetcher = (async () => new Response("", { status: 503 })) as typeof fetch;
    render(<NotificationPanel client={new AdminClient({ fetcher })} />);

    // The table reports its own failure too — the panel-level banner is the first one.
    expect((await screen.findAllByRole("alert"))[0]).toHaveTextContent(/notification templates could not be loaded/i);
  });

  it("tenant and correlation id appear when the row carries them — and never as empty labels", async () => {
    const { client: api } = client({
      list: [notification({ id: "traced-1", tenant: "acme", correlationId: "0af7651916cd43dd8448eb211c80319c" })],
    });
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    const detail = await screen.findByTestId("notification-detail");
    expect(detail).toHaveTextContent("acme");
    expect(detail).toHaveTextContent("0af7651916cd43dd8448eb211c80319c");
  });

  it("a row WITHOUT them shows no tenant or correlation block at all", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    const detail = await screen.findByTestId("notification-detail");
    expect(within(detail).queryByText("Tenant")).toBeNull();
    expect(within(detail).queryByText("Correlation")).toBeNull();
  });
});

describe("the notification evidence panel — the remaining doors", () => {
  it("a retention it cannot parse is shown as the server wrote it, never guessed at", () => {
    expect(retentionWords("P90D")).toBe("P90D");
  });

  it("refresh re-reads the list — evidence moves while the operator watches", async () => {
    const { client: api, fetched } = client();
    render(<NotificationPanel client={api} />);
    await screen.findByRole("button", { name: "a***@e***" });
    const lists = () => fetched.filter((url) => url.includes("/notifications?")).length;
    const before = lists();

    await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(lists()).toBeGreaterThan(before));
  });

  it("clearing the state facet drops state= from the query", async () => {
    const { client: api, fetched } = client();
    render(<NotificationPanel client={api} />);
    const lists = () => fetched.filter((url) => url.includes("/notifications?"));

    await userEvent.click(await screen.findByRole("button", { name: /State/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /Failed/ }));
    await waitFor(() => expect(lists().at(-1)).toContain("state=Failed"));
    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("state="));
  });

  it("unticking a template and clearing the facet each drop it from the query", async () => {
    const { client: api, fetched } = client({ templates: [template(), template({ key: "welcome" })] });
    render(<NotificationPanel client={api} />);
    const lists = () => fetched.filter((url) => url.includes("/notifications?"));

    await userEvent.click(await screen.findByRole("button", { name: /Template/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /policy-renewal/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /welcome/ }));
    await waitFor(() => expect(lists().at(-1)).toContain("template=welcome"));

    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: /policy-renewal/ }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("template=policy-renewal"));
    expect(lists().at(-1)).toContain("template=welcome");

    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("template="));
  });

  it("closing the sheet lets go of the row", async () => {
    const { client: api } = client();
    render(<NotificationPanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    await screen.findByTestId("notification-detail");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("notification-detail")).toBeNull());
  });

  it("a row that cannot be read says which one", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (/\/notifications\/[^/?]+$/.test(url)) return new Response("", { status: 500 });
      if (url.includes("/notifications")) return json([notification()]);
      return json([template()]);
    }) as typeof fetch;
    render(<NotificationPanel client={new AdminClient({ fetcher })} />);

    await userEvent.click(await screen.findByRole("button", { name: "a***@e***" }));
    expect(await screen.findByText(`notification ${notification().id} could not be read`)).toBeInTheDocument();
  });
});
