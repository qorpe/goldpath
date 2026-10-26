import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminClient, type FileInfo, type FileQuarantineInfo, type FileRailInfo } from "./adminClient";
import { FileExchangePanel } from "./FileExchangePanel";

const rail = (over: Partial<FileRailInfo> = {}): FileRailInfo => ({
  name: "registry-daily",
  headerLines: 1,
  filesArchived: 2,
  quarantineDepth: 1,
  lastArchivedAt: "2026-09-03T06:10:00Z",
  ...over,
});

const file = (over: Partial<FileInfo> = {}): FileInfo => ({
  rail: "registry-daily",
  file: "reg-0901.csv",
  processedRows: 2,
  quarantinedRows: 1,
  archived: true,
  archivedAt: "2026-09-03T06:00:00Z",
  ...over,
});

const quarantined = (over: Partial<FileQuarantineInfo> = {}): FileQuarantineInfo => ({
  rail: "registry-daily",
  file: "reg-0901.csv",
  line: 3,
  reason: "non-positive amount",
  quarantinedAt: "2026-09-03T06:00:00Z",
  ...over,
});

interface Routes {
  rails?: unknown;
  files?: unknown;
  quarantine?: unknown;
}

function client(routes: Routes = {}) {
  const fetched: string[] = [];
  const fetcher = (async (input: RequestInfo | URL) => {
    const url = String(input);
    fetched.push(url);
    const json = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

    if (url.includes("/fileexchange/rails")) return json(routes.rails ?? [rail(), rail({ name: "bank-status", headerLines: 0, filesArchived: 0, quarantineDepth: 0, lastArchivedAt: null })]);
    if (url.includes("/fileexchange/files")) return json(routes.files ?? [file()]);
    if (url.includes("/fileexchange/quarantine")) return json(routes.quarantine ?? [quarantined()]);
    return json([], 404);
  }) as typeof fetch;

  return { api: new AdminClient({ fetcher }), fetched };
}

describe("FileExchangePanel — the rails and their quarantine", () => {
  it("lists the rails with their counts and says the quarantine out loud", async () => {
    const { api } = client();
    render(<FileExchangePanel client={api} />);

    const rails = within(await screen.findByTestId("rails"));
    expect(rails.getByText("registry-daily")).toBeInTheDocument();
    expect(rails.getByText("bank-status")).toBeInTheDocument();
    // The banner names the rail with rows waiting, and only that rail.
    expect(screen.getByRole("status")).toHaveTextContent("registry-daily: 1 row in quarantine");
    expect(screen.getByRole("status")).not.toHaveTextContent("bank-status");
  });

  it("lists the files newest archive first and opens one with its quarantine reasons", async () => {
    const { api, fetched } = client();
    render(<FileExchangePanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "reg-0901.csv" }));

    const detail = within(await screen.findByTestId("file-detail"));
    expect(await detail.findByText("non-positive amount")).toBeInTheDocument();
    expect(detail.getByText("3")).toBeInTheDocument();
    // File names ride the QUERY (dots and slashes), never the path — and the read is scoped to this file.
    expect(fetched.some((url) => url.includes("/fileexchange/quarantine?rail=registry-daily&file=reg-0901.csv"))).toBe(true);
  });

  it("a rail facet re-reads the files with R3 repeats", async () => {
    const { api, fetched } = client();
    render(<FileExchangePanel client={api} />);
    await screen.findByRole("button", { name: "reg-0901.csv" });

    await userEvent.click(screen.getByRole("button", { name: /rail/i }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /bank-status/ }));
    await userEvent.keyboard("{Escape}");

    await waitFor(() => expect(fetched.some((url) => url.includes("/fileexchange/files?rail=bank-status"))).toBe(true));
  });

  it("an empty estate says so instead of pretending", async () => {
    const { api } = client({ rails: [], files: [] });
    render(<FileExchangePanel client={api} />);

    expect(await screen.findByText("No rails are declared in this app.")).toBeInTheDocument();
    expect(await screen.findByText("No files have arrived on these rails yet.")).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("a rail surface that fails to load says so in a banner", async () => {
    const fetcher = (async () => {
      throw new Error("down");
    }) as unknown as typeof fetch;
    render(<FileExchangePanel client={new AdminClient({ fetcher })} />);

    expect(await screen.findByText("the file rails could not be loaded")).toBeInTheDocument();
  });
});

describe("FileExchangePanel — the remaining doors", () => {
  it("refresh re-reads the rails and the files", async () => {
    const { api, fetched } = client();
    render(<FileExchangePanel client={api} />);
    await screen.findByRole("button", { name: "reg-0901.csv" });
    const count = (part: string) => fetched.filter((url) => url.includes(part)).length;
    const rails = count("/fileexchange/rails");
    const files = count("/fileexchange/files");

    await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => {
      expect(count("/fileexchange/rails")).toBeGreaterThan(rails);
      expect(count("/fileexchange/files")).toBeGreaterThan(files);
    });
  });

  it("unticking a rail and clearing the facet each drop it from the query", async () => {
    const { api, fetched } = client();
    render(<FileExchangePanel client={api} />);
    const lists = () => fetched.filter((url) => url.includes("/fileexchange/files"));

    await userEvent.click(await screen.findByRole("button", { name: /Rail/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /registry-daily/ }));
    await userEvent.click(await screen.findByRole("menuitemcheckbox", { name: /bank-status/ }));
    await waitFor(() => expect(lists().at(-1)).toContain("rail=bank-status"));

    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: /registry-daily/ }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("rail=registry-daily"));
    expect(lists().at(-1)).toContain("rail=bank-status");

    await userEvent.click(screen.getByRole("menuitem", { name: "clear" }));
    await waitFor(() => expect(lists().at(-1)).not.toContain("rail="));
  });

  it("closing the sheet lets go of the file", async () => {
    const { api } = client();
    render(<FileExchangePanel client={api} />);

    await userEvent.click(await screen.findByRole("button", { name: "reg-0901.csv" }));
    await screen.findByTestId("file-detail");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("file-detail")).toBeNull());
  });

  it("a quarantine that cannot be read names the file", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (url.includes("/fileexchange/quarantine")) return new Response("", { status: 500 });
      if (url.includes("/fileexchange/files")) return json([file()]);
      return json([rail()]);
    }) as typeof fetch;
    render(<FileExchangePanel client={new AdminClient({ fetcher })} />);

    await userEvent.click(await screen.findByRole("button", { name: "reg-0901.csv" }));
    expect(await screen.findByText("the quarantine of reg-0901.csv could not be read")).toBeInTheDocument();
  });
});
