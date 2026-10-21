import { render, screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConsoleApp } from "./ConsoleApp";

/**
 * A fake estate: two services with DIFFERENT compositions, so switching has to re-discover
 * rather than reuse what it already drew.
 */
function estate(options: { registry?: unknown; registryStatus?: number } = {}) {
  const asked: string[] = [];
  const fetcher = (async (input: RequestInfo | URL) => {
    const url = String(input);
    const json = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

    if (url.includes("console.config.json")) {
      if (options.registryStatus && options.registryStatus !== 200) return new Response("", { status: options.registryStatus });
      return json(
        options.registry ?? {
          services: [
            { name: "payments", adminBaseUrl: "https://payments.internal" },
            { name: "claims", adminBaseUrl: "https://claims.internal" },
          ],
        },
      );
    }

    asked.push(url);
    // payments composes jobs + bulk; claims composes archival only.
    if (url.startsWith("https://payments.internal")) {
      if (url.includes("/jobs/fleets")) return json([]);
      if (url.includes("/bulk/definitions")) return json([]);
      return new Response("", { status: 404 });
    }

    if (url.startsWith("https://claims.internal")) {
      if (url.includes("/archival/definitions")) return json([]);
      return new Response("", { status: 404 });
    }

    return new Response("", { status: 404 });
  }) as typeof fetch;

  return { fetcher, asked };
}

describe("the console across services", () => {
  it("lands on TODAY — the operator's question is 'is anything wrong', not 'what exists'", async () => {
    const { fetcher } = estate();
    render(<ConsoleApp fetcher={fetcher} search="" />);

    expect(await screen.findByTestId("triage-home")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Today" })).toHaveAttribute("aria-current", "page");
  });

  it("lists the registry's services and navigates the first one's modules", async () => {
    const { fetcher } = estate();
    render(<ConsoleApp fetcher={fetcher} search="" />);

    const picker = await screen.findByRole("combobox", { name: "service" });
    expect(picker).toHaveTextContent("payments");
    await userEvent.click(await screen.findByRole("button", { name: "Bulk intake" }));
    expect(await screen.findByTestId("bulk-panel")).toBeInTheDocument();
  });

  it("switching service RE-DISCOVERS — one service's panels never appear under another's name", async () => {
    const { fetcher, asked } = estate();
    render(<ConsoleApp fetcher={fetcher} search="" />);

    await screen.findByRole("button", { name: "Bulk intake" });

    await userEvent.click(await screen.findByRole("combobox", { name: "service" }));
    await userEvent.click(await screen.findByRole("option", { name: "claims" }));

    // Claims composes archival only: bulk must be GONE, not carried over.
    expect(await screen.findByRole("button", { name: "Archival" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Bulk intake" })).toBeNull());

    // And the probes went to the claims service, not to payments again.
    expect(asked.some((url) => url.startsWith("https://claims.internal"))).toBe(true);
  });

  it("a forbidden capability is NAMED with the server's words", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("console.config.json")) return new Response("", { status: 404 });
      return new Response(JSON.stringify({ message: "the 'goldpath-ops' role is required" }), {
        status: 403,
        headers: { "content-type": "application/json" },
      });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    await userEvent.click(await screen.findByRole("button", { name: "Runs" }));
    expect(await screen.findByText(/lacks the ops role/)).toHaveTextContent("the 'goldpath-ops' role is required");
  });

  it("a REFUSED capability is named too — composed, reachable, and saying no", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("console.config.json")) return new Response("", { status: 404 });
      return new Response(JSON.stringify({ title: "Tenant could not be resolved." }), {
        status: 400,
        headers: { "content-type": "application/json" },
      });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    await userEvent.click(await screen.findByRole("button", { name: "Runs" }));
    expect(await screen.findByText(/composed here but refused this request/)).toHaveTextContent("Tenant could not be resolved.");
  });

  it("a service with NO Goldpath surface says so instead of an empty frame", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("console.config.json")) return new Response("", { status: 404 });
      return new Response("", { status: 404 });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    // Today still answers — it just has nothing to report from a service that composes none.
    expect(await screen.findByTestId("triage-home")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Runs" })).toBeNull();
  });

  it("a single-service console shows no picker at all — nothing to choose between", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("console.config.json")) return new Response("", { status: 404 });
      return new Response(JSON.stringify([]), { status: 200, headers: { "content-type": "application/json" } });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    await screen.findByRole("button", { name: "Runs" });
    expect(screen.queryByLabelText(/service/i)).toBeNull();
  });

  it("a service that never answers is reported as unreachable, on Today and in its own screen", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("console.config.json")) return new Response("", { status: 404 });
      throw new TypeError("network down");   // what a blocked or dead service looks like
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    // Today says it first — a blind spot outranks everything, including nothing.
    expect(await screen.findByText(/did not answer at all/)).toBeInTheDocument();
    // And there is no module nav to click into: every link would say the same thing.
    expect(screen.queryByRole("button", { name: "Runs" })).toBeNull();
  });

  it("a triage row opens the panel it belongs to, on the SERVICE it belongs to", async () => {
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (url.includes("console.config.json")) {
        return json({
          services: [
            { name: "payments", adminBaseUrl: "https://payments.internal" },
            { name: "claims", adminBaseUrl: "https://claims.internal" },
          ],
        });
      }

      // Only CLAIMS has something wrong: a batch waiting at its four-eyes gate.
      if (url.startsWith("https://claims.internal")) {
        if (url.includes("/bulk/definitions")) {
          return json([{ name: "payouts", batchesByState: { Validated: 1 }, awaitingApproval: 1, oldestAwaitingApprovalSeconds: 7200 }]);
        }

        if (url.includes("/bulk/batches")) return json([]);
        return new Response("", { status: 404 });
      }

      if (url.includes("/jobs/fleets")) return json([]);
      return new Response("", { status: 404 });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);

    const row = await screen.findByRole("button", { name: /awaiting approval in payouts/ });
    await userEvent.click(row);

    // The console switched service AND section — the row is a deep link, not a label.
    expect(await screen.findByTestId("bulk-panel")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "service" })).toHaveTextContent("claims");
  });

  it("a registry that failed to load is ANNOUNCED — an operator who configured four services must not silently see one", async () => {
    const { fetcher } = estate({ registryStatus: 500 });
    render(<ConsoleApp fetcher={fetcher} search="" />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/service registry answered 500 — showing this service only/);
  });

  it("a partially broken registry names the loss — the console did NOT fall back, it is incomplete", async () => {
    const { fetcher } = estate({
      registry: { services: [{ name: "payments", adminBaseUrl: "https://payments.internal" }, { adminBaseUrl: "https://nameless.internal" }] },
    });
    render(<ConsoleApp fetcher={fetcher} search="" />);

    const banner = await screen.findByRole("alert");
    expect(banner).toHaveTextContent("1 registry entry has no name and was skipped");
    expect(banner).toHaveTextContent("missing a service you configured");
    expect(banner).not.toHaveTextContent("showing this service only");
  });

  it("?base= drives one named service, whatever the registry says", async () => {
    const { fetcher } = estate();
    render(<ConsoleApp fetcher={fetcher} search="?base=https://payments.internal" />);

    await screen.findByRole("button", { name: "Bulk intake" });
    expect(screen.queryByLabelText(/service/i)).toBeNull();   // one service: no picker
  });

  it("the palette opens from the rail's Search trigger and GOES where the rail goes", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    await screen.findByRole("button", { name: "Runs" });

    // The trigger is the reference's field-with-⌘K-hint; clicking it opens the dialog.
    await userEvent.click(screen.getByRole("button", { name: /Search/ }));
    const dialog = await screen.findByRole("dialog");

    // Every rail destination is a command; picking one closes and navigates.
    await userEvent.click(within(dialog).getByText("Bulk intake"));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Bulk intake" })).toBeInTheDocument();
  });

  it("with several services, the palette can switch the estate too", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    await screen.findByRole("button", { name: "Runs" });

    await userEvent.keyboard("{Meta>}k{/Meta}");
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByText("Open claims"));

    // claims composes ONLY archival — the switch re-discovered, the rail followed.
    expect(await screen.findByRole("button", { name: "Archival" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Runs" })).not.toBeInTheDocument());
  });

  it("Today opens with the estate's stat cards — only the modules someone composed, zeros neutral", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    const cards = await screen.findByTestId("triage-cards");

    // payments composes jobs + bulk, claims composes archival: three countable modules.
    expect(within(cards).getByText("Failed runs")).toBeInTheDocument();
    expect(within(cards).getByText("Awaiting approval")).toBeInTheDocument();
    expect(within(cards).getByText("Due to archive")).toBeInTheDocument();
    // Nobody composed campaign or notification — no card, not a false zero.
    expect(within(cards).queryByText("Failed campaign items")).not.toBeInTheDocument();
    expect(within(cards).queryByText("Failed notifications")).not.toBeInTheDocument();
  });

  it("a stat card deep-links like a triage row — to the SERVICE that owns the number", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    const cards = await screen.findByTestId("triage-cards");

    // "Due to archive" belongs to claims: the click switches the estate AND the section.
    await userEvent.click(within(cards).getByRole("button", { name: /Due to archive/ }));
    expect(await screen.findByRole("heading", { name: "Archival" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Runs" })).not.toBeInTheDocument());
  });

  it("a stat owned by TWO services is a plain card — a combined total deep-links nowhere", async () => {
    // Both services compose jobs: the "Failed runs" number belongs to neither alone.
    const fetcher = (async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
      if (url.includes("console.config.json")) {
        return json({ services: [
          { name: "payments", adminBaseUrl: "https://payments.internal" },
          { name: "claims", adminBaseUrl: "https://claims.internal" },
        ] });
      }
      if (url.includes("/jobs/fleets")) return json([]);
      return new Response("", { status: 404 });
    }) as typeof fetch;

    render(<ConsoleApp fetcher={fetcher} search="" />);
    const cards = await screen.findByTestId("triage-cards");

    expect(within(cards).getByText("Failed runs")).toBeInTheDocument();
    expect(within(cards).queryByRole("button", { name: /Failed runs/ })).not.toBeInTheDocument();
  });

  it("§8.1: Today opens with the standard page header — one header pattern everywhere", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    const heading = await screen.findByRole("heading", { level: 1, name: "Today" });
    expect(heading).toBeInTheDocument();
    // The scope line rides the header's purpose slot, not a stray strip.
    expect(screen.getByText(/most recent 50 rows/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh" })).toBeInTheDocument();
  });

  it("the brand goes HOME: from any section, clicking the title lands on Today", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    await userEvent.click(await screen.findByRole("button", { name: "Runs" }));
    await screen.findByRole("heading", { name: "Runs" });

    await userEvent.click(screen.getByRole("button", { name: /Goldpath Console/ }));
    expect(await screen.findByRole("heading", { level: 1, name: "Today" })).toBeInTheDocument();
  });

  it("the rail renders ONE Modules group — the owner's amendment, pinned", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    const rail = await screen.findByTestId("shell-rail");
    await screen.findByRole("button", { name: "Runs" });

    // One family, one heading — and none of the four concern-groups B1 shipped with.
    expect(within(rail).getAllByText(/^Modules$/)).toHaveLength(1);
    for (const retired of ["Execution", "Intake", "Outbound", "Compliance"]) {
      expect(within(rail).queryByText(new RegExp(`^${retired}$`))).toBeNull();
    }
  });
});

// Product console link tiles (#257) — decision record docs/rfc/goldpath-console-link-tiles.md.
describe("product console link tiles on Today", () => {
  const withLinks = (registry: unknown) => estate({ registry });
  const LINKS = [
    { name: "Coexist", url: "/coexist/console/" },
    { name: "Mockifyr", url: "https://mockifyr.internal/" },
  ];

  it("§4: Today draws one native link per product, named by the product AND where it goes", async () => {
    render(<ConsoleApp fetcher={withLinks({ links: LINKS }).fetcher} search="" />);

    const tiles = await screen.findByRole("navigation", { name: "Product consoles" });
    const links = within(tiles).getAllByRole("link");
    expect(links).toHaveLength(2);
    // Same-origin shows its PATH, another origin its HOST — the operator sees the
    // destination before going there.
    expect(links[0]).toHaveAccessibleName("Coexist /coexist/console/");
    expect(links[0]).toHaveAttribute("href", `${window.location.origin}/coexist/console/`);
    expect(links[1]).toHaveAccessibleName("Mockifyr mockifyr.internal");
    expect(links[1]).toHaveAttribute("href", "https://mockifyr.internal/");
    // Same tab, like the kit's href rail items; the referrer is never loosened.
    expect(links[1]).not.toHaveAttribute("target");
    expect(links[1]).toHaveAttribute("referrerpolicy", "strict-origin-when-cross-origin");
  });

  it("§4: a links-only registry shows the tiles, ONE service, and no registry warning", async () => {
    render(<ConsoleApp fetcher={withLinks({ links: LINKS }).fetcher} search="" />);

    await screen.findByRole("navigation", { name: "Product consoles" });
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByRole("combobox", { name: "service" })).toBeNull();
  });

  it("§1: a tile is NEVER probed — the console sends nothing to a product's address", async () => {
    const { fetcher, asked } = withLinks({ services: [{ name: "payments", adminBaseUrl: "https://payments.internal" }], links: LINKS });
    render(<ConsoleApp fetcher={fetcher} search="" />);

    await screen.findByRole("navigation", { name: "Product consoles" });
    await screen.findByTestId("triage-cards");
    expect(asked.some((url) => url.includes("mockifyr") || url.includes("coexist"))).toBe(false);
  });

  it("§4: no links, no section — never an empty one", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);

    await screen.findByTestId("triage-cards");
    expect(screen.queryByRole("navigation", { name: "Product consoles" })).toBeNull();
    expect(screen.queryByText("Product consoles")).toBeNull();
  });

  it("§4: a dropped link is ANNOUNCED as a missing product console, not a missing service", async () => {
    render(
      <ConsoleApp
        fetcher={withLinks({ links: [...LINKS, { name: "Evil", url: "javascript:alert(1)" }] }).fetcher}
        search=""
      />,
    );

    const banner = await screen.findByRole("alert");
    expect(banner).toHaveTextContent("1 product console link was skipped");
    expect(banner).toHaveTextContent("the console is missing a product console you configured");
    // The two good tiles are still drawn; the refused one is nowhere on the page.
    const tiles = await screen.findByRole("navigation", { name: "Product consoles" });
    expect(within(tiles).getAllByRole("link")).toHaveLength(2);
    expect(screen.queryByText("Evil")).toBeNull();
  });

  it("§4: the palette offers each product console as a place to GO, to the approved address", async () => {
    const navigate = vi.fn();
    render(<ConsoleApp fetcher={withLinks({ links: LINKS }).fetcher} search="" navigate={navigate} />);
    await screen.findByRole("navigation", { name: "Product consoles" });

    await userEvent.keyboard("{Meta>}k{/Meta}");
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Consoles")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByText("Open Mockifyr"));

    expect(navigate).toHaveBeenCalledWith("https://mockifyr.internal/");
  });

  it("without links the palette has no Consoles group", async () => {
    render(<ConsoleApp fetcher={estate().fetcher} search="" />);
    await screen.findByRole("button", { name: "Runs" });

    await userEvent.keyboard("{Meta>}k{/Meta}");
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByText("Consoles")).toBeNull();
  });

  it("the rail does NOT grow a Products group — tiles live on Today only (owner, 2026-10-19)", async () => {
    render(<ConsoleApp fetcher={withLinks({ links: LINKS }).fetcher} search="" />);
    const rail = await screen.findByTestId("shell-rail");
    await screen.findByRole("navigation", { name: "Product consoles" });

    expect(within(rail).queryByText("Coexist")).toBeNull();
    expect(within(rail).queryByText(/^Products$/)).toBeNull();
  });
});
