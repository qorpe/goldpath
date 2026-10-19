import { loadRegistry, resolveLink, SAME_ORIGIN } from "./registry";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const fetcherFor = (response: Response | (() => never)) =>
  (async () => (typeof response === "function" ? response() : response)) as typeof fetch;

describe("the service registry (config across services, discovery within one)", () => {
  it("reads the adopter's services, in the order they wrote them", async () => {
    const fetcher = fetcherFor(
      json({ services: [{ name: "payments", adminBaseUrl: "https://payments.internal" }, { name: "claims", adminBaseUrl: "https://claims.internal" }] }),
    );

    const { services, problem } = await loadRegistry(fetcher, "");

    expect(services).toEqual([
      { name: "payments", adminBaseUrl: "https://payments.internal" },
      { name: "claims", adminBaseUrl: "https://claims.internal" },
    ]);
    expect(problem).toBeUndefined();
  });

  it("no registry means ONE service — the app that served the console", async () => {
    const { services, problem } = await loadRegistry(fetcherFor(new Response("", { status: 404 })), "");

    expect(services).toEqual([SAME_ORIGIN]);
    expect(problem).toBeUndefined();   // a single-service console is normal, not a fault
  });

  it("?base= wins — the dev override and the way to point at a service that did not serve this console", async () => {
    const fetcher = fetcherFor(json({ services: [{ name: "payments", adminBaseUrl: "https://payments.internal" }] }));

    const { services } = await loadRegistry(fetcher, "?base=http://localhost:5310");

    expect(services).toEqual([{ name: "http://localhost:5310", adminBaseUrl: "http://localhost:5310" }]);
  });

  it("an EMPTY ?base= means same-origin, named as such", async () => {
    const { services } = await loadRegistry(fetcherFor(new Response("", { status: 404 })), "?base=");

    expect(services).toEqual([{ name: "same-origin", adminBaseUrl: "" }]);
  });

  it("a registry that cannot be READ says so — a quiet fallback would hide the other services", async () => {
    const { services, problem } = await loadRegistry(fetcherFor(new Response("", { status: 500 })), "");

    expect(services).toEqual([SAME_ORIGIN]);
    expect(problem).toMatch(/answered 500/);
  });

  it("a registry that is not JSON at all is a problem, not a crash", async () => {
    const { services, problem } = await loadRegistry(fetcherFor(new Response("<html>index</html>", { status: 200 })), "");

    expect(services).toEqual([SAME_ORIGIN]);
    expect(problem).toMatch(/could not be read/);
  });

  it("an unreachable registry is a problem, not a crash", async () => {
    const { problem } = await loadRegistry(
      fetcherFor(() => {
        throw new TypeError("network down");
      }),
      "",
    );

    expect(problem).toMatch(/could not be read/);
  });

  it("entries without a name are dropped, and a file with nothing usable says so", async () => {
    const { services, problem } = await loadRegistry(
      fetcherFor(json({ services: [{ adminBaseUrl: "https://nameless.internal" }, { name: "   " }] })),
      "",
    );

    expect(services).toEqual([SAME_ORIGIN]);
    expect(problem).toMatch(/no service with a name/);
  });

  it("a PARTIAL drop is reported — the console still works and is quietly missing a service", async () => {
    const { services, problem } = await loadRegistry(
      fetcherFor(
        json({
          services: [
            { name: "payments", adminBaseUrl: "https://payments.internal" },
            { adminBaseUrl: "https://nameless.internal" },
            { name: "  ", adminBaseUrl: "https://blank.internal" },
          ],
        }),
      ),
      "",
    );

    expect(services).toEqual([{ name: "payments", adminBaseUrl: "https://payments.internal" }]);
    expect(problem).toBe("2 registry entries have no name and were skipped");
  });

  it("one dropped entry is reported in the singular — the count is the point", async () => {
    const { problem } = await loadRegistry(
      fetcherFor(json({ services: [{ name: "payments" }, { adminBaseUrl: "https://nameless.internal" }] })),
      "",
    );

    expect(problem).toBe("1 registry entry has no name and was skipped");
  });

  it("a same-origin entry is legitimate: the console's own app, listed beside the others", async () => {
    const { services } = await loadRegistry(
      fetcherFor(json({ services: [{ name: "this app", adminBaseUrl: "" }, { name: "claims", adminBaseUrl: "https://claims.internal" }] })),
      "",
    );

    expect(services[0]).toEqual({ name: "this app", adminBaseUrl: "" });
  });
});

// Product console link tiles (#257) — decision record docs/rfc/goldpath-console-link-tiles.md.
const BASE = "https://ops.internal/goldpath/console/";

describe("product console links (link-tiles RFC §4–§5)", () => {
  it("§4: links are read beside services, resolved against the console's own address", async () => {
    const { services, links, problem } = await loadRegistry(
      fetcherFor(
        json({
          services: [{ name: "payments", adminBaseUrl: "https://payments.internal" }],
          links: [{ name: "Coexist", url: "/coexist/console/" }, { name: "Mockifyr", url: "https://mockifyr.internal/" }],
        }),
      ),
      "",
      BASE,
    );

    expect(services).toEqual([{ name: "payments", adminBaseUrl: "https://payments.internal" }]);
    expect(links).toEqual([
      { name: "Coexist", url: "https://ops.internal/coexist/console/" },
      { name: "Mockifyr", url: "https://mockifyr.internal/" },
    ]);
    expect(problem).toBeUndefined();
  });

  it("§4: a links-only registry (no services KEY) is this service plus tiles — and NO warning", async () => {
    // The server omits `services` rather than emptying it; reading the absence as broken
    // would put the false registry warning back on a single-app operator's first screen.
    const registry = await loadRegistry(fetcherFor(json({ links: [{ name: "Coexist", url: "/coexist/console/" }] })), "", BASE);

    expect(registry).toEqual({ services: [SAME_ORIGIN], links: [{ name: "Coexist", url: "https://ops.internal/coexist/console/" }] });
  });

  it("§4: services PRESENT but empty is still a broken registry, links or not — the rule did not move", async () => {
    const { services, links, problem, fellBack } = await loadRegistry(
      fetcherFor(json({ services: [], links: [{ name: "Coexist", url: "/coexist/console/" }] })),
      "",
      BASE,
    );

    expect(services).toEqual([SAME_ORIGIN]);
    expect(links).toHaveLength(1);   // the tiles survive the fallback; they were never the problem
    expect(problem).toMatch(/no service with a name/);
    expect(fellBack).toBe(true);
  });

  it("§4: a dropped link is REPORTED, never quietly missing — a missing tile is the same lie as a missing service", async () => {
    const { links, problem, missing, fellBack } = await loadRegistry(
      fetcherFor(
        json({
          links: [
            { name: "Coexist", url: "/coexist/console/" },
            { name: "Evil", url: "javascript:alert(1)" },
            { url: "https://nameless.internal/" },
            { name: "coexist", url: "/again/" },
            { name: "Numeric", url: 42 },
          ],
        }),
      ),
      "",
      BASE,
    );

    expect(links.map((link) => link.name)).toEqual(["Coexist"]);
    expect(problem).toBe("4 product console links were skipped (no name, a repeated name, or an address the console will not open)");
    expect(missing).toBe("a product console");
    expect(fellBack).toBeUndefined();
  });

  it("§4: one dropped link is said in the singular, and beside a dropped service both are named", async () => {
    const { problem, missing } = await loadRegistry(
      fetcherFor(
        json({
          services: [{ name: "payments", adminBaseUrl: "" }, { adminBaseUrl: "https://nameless.internal" }],
          links: [{ name: "Bad", url: "data:text/html,x" }],
        }),
      ),
      "",
      BASE,
    );

    expect(problem).toBe(
      "1 registry entry has no name and was skipped; 1 product console link was skipped (no name, a repeated name, or an address the console will not open)",
    );
    expect(missing).toBe("a service and a product console");
  });

  it("§4: a dropped link beside healthy services names only what was lost", async () => {
    const { problem, missing } = await loadRegistry(
      fetcherFor(json({ services: [{ name: "payments", adminBaseUrl: "" }], links: [{ name: "Bad", url: "//evil.example/" }] })),
      "",
      BASE,
    );

    expect(problem).toMatch(/^1 product console link was skipped/);
    expect(missing).toBe("a product console");
  });

  it("§5: ?base= is a one-service override and carries NO links — links come only from server config", async () => {
    const fetcher = fetcherFor(json({ links: [{ name: "Coexist", url: "/coexist/console/" }] }));

    const { links } = await loadRegistry(fetcher, "?base=https://payments.internal&link=https://evil.example/", BASE);

    expect(links).toEqual([]);
  });

  it("no registry, a failed registry and an unreadable one all carry no links", async () => {
    expect((await loadRegistry(fetcherFor(new Response("", { status: 404 })), "", BASE)).links).toEqual([]);
    expect((await loadRegistry(fetcherFor(new Response("", { status: 500 })), "", BASE)).links).toEqual([]);
    expect((await loadRegistry(fetcherFor(new Response("<html>", { status: 200 })), "", BASE)).links).toEqual([]);
  });
});

describe("resolveLink — the client re-check of the server's URL rule (§5)", () => {
  it.each([
    ["/coexist/console/", "https://ops.internal/coexist/console/"],
    ["/", "https://ops.internal/"],
    ["https://mockifyr.internal/", "https://mockifyr.internal/"],
    ["http://10.0.0.5:8080/ui/", "http://10.0.0.5:8080/ui/"],
    ["HTTPS://Mockifyr.Internal/ui?x=1", "https://mockifyr.internal/ui?x=1"],
  ])("accepts %s as %s", (raw, resolved) => {
    expect(resolveLink(raw, BASE)).toBe(resolved);
  });

  it.each([
    "javascript:alert(1)",
    "JavaScript:alert(1)",
    "data:text/html,<script>alert(1)</script>",
    "vbscript:msgbox(1)",
    "file:///etc/passwd",
    "blob:https://mockifyr.internal/1",
    "//evil.example/",
    "/\\evil.example/",
    "https://evil.example\\@good/",
    "console/",
    "../x",
    "mockifyr.internal",
    "https://u:p@mockifyr.internal/",
    "https://u@mockifyr.internal/",
    "https://",
    "https:mockifyr.internal",
    "java\tscript:alert(1)",
    " /coexist/console/",
    "/coexist/\nconsole/",
    "/coexist/\u0085console/",
    "",
  ])("refuses %j", (raw) => {
    expect(resolveLink(raw, BASE)).toBeNull();
  });

  it("judges the RESOLVED address: a root-relative path on a console opened from file: is not http(s)", () => {
    // The form check passes ("/x" is root-relative); only the resolved scheme can refuse it.
    expect(resolveLink("/coexist/console/", "file:///opt/console/index.html")).toBeNull();
  });
});
