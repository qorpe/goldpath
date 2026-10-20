/**
 * The cross-service registry (console RFC §3). Within ONE app there is nothing to
 * configure — the fleets are store-discovered and the app's own admin surface speaks for
 * all of them. ACROSS services there is nothing to discover: a payments service cannot
 * know that a claims service exists, so the registry is CONFIG, and the console reads it
 * from where it is served rather than being rebuilt per adopter.
 */
export interface ServiceEntry {
  /** The operator's word for the service — what the rail shows. */
  name: string;
  /** Its management head; "" means the app serving this console (same-origin). */
  adminBaseUrl: string;
}

/**
 * A product console the core console LINKS to and never probes (console RFC §3 link tiles;
 * `docs/rfc/goldpath-console-link-tiles.md`). A tile asserts an address, never health.
 */
export interface LinkEntry {
  /** The operator's word for the product — what the tile shows. */
  name: string;
  /** The address, already resolved and checked by {@link resolveLink}: absolute http(s). */
  url: string;
}

/** The shape of `console.config.json`, as an adopter writes it. */
interface RegistryFile {
  services?: { name?: unknown; adminBaseUrl?: unknown }[];
  links?: { name?: unknown; url?: unknown }[];
}

/** The single-service console: the app that serves the console is the only service. */
export const SAME_ORIGIN: ServiceEntry = { name: "this service", adminBaseUrl: "" };

/**
 * Reads the registry.
 *
 * Precedence, and why:
 * 1. `?base=` — the DEV override, and the escape hatch for pointing the console at a
 *    service it was not served by. One service, named by its own URL.
 * 2. `console.config.json` next to the console — the adopter's registry. Served by the
 *    app (or by whatever hosts the dist), so adding a service is a config change, never
 *    a rebuild.
 * 3. Nothing — same-origin, single service.
 *
 * A config that cannot be read or makes no sense yields the same-origin default AND says
 * so: a console that silently shows one service when the operator configured four would
 * hide exactly the outage they came to find.
 */
export interface Registry {
  services: ServiceEntry[];
  /** Product consoles to offer as link tiles — never probed, never a triage row. */
  links: LinkEntry[];
  /** What went wrong, in words the console shows verbatim. */
  problem?: string;
  /** True when the problem cost us the registry entirely and we fell back to same-origin. */
  fellBack?: boolean;
  /** What a PARTIAL drop cost: "a service", "a product console", or both — said in the banner. */
  missing?: string;
}

/**
 * The client half of the link rule (the server's `ConsoleLinkRules` is the first half; this
 * re-check is defence in depth for a registry file an adopter wrote by hand). The same two
 * forms are accepted — a root-relative path, or an absolute http(s) address without
 * credentials — and the result is the RESOLVED address, so nothing downstream ever hands a
 * browser a string this function did not approve. React's own handling of `javascript:`
 * hrefs is not relied on.
 */
export function resolveLink(raw: string, base: string): string | null {
  // Whitespace, control characters and backslashes: browsers strip or rewrite them ("java<TAB>script:",
  // "/\\host" read as "//host"), so an address carrying one is refused rather than interpreted.
  if (raw.length === 0 || /[\s\u0000-\u001f\u007f-\u009f\\]/.test(raw)) return null;
  const rootRelative = raw.startsWith("/") && !raw.startsWith("//");
  if (!rootRelative && !/^https?:\/\//i.test(raw)) return null;

  let resolved: URL;
  try {
    resolved = new URL(raw, base);
  } catch {
    return null;
  }

  if (resolved.protocol !== "http:" && resolved.protocol !== "https:") return null;
  if (resolved.username !== "" || resolved.password !== "") return null;   // a credential never rides a link
  return resolved.href;
}

export async function loadRegistry(
  fetcher: typeof fetch = (input, init) => globalThis.fetch(input, init),
  search: string = globalThis.location?.search ?? "",
  base: string = globalThis.document?.baseURI ?? "http://localhost/",
): Promise<Registry> {
  // `?base=` is a one-service DEV override and carries no links: links come only from the
  // server's configuration, never from the query string (link-tiles RFC §5, open redirect).
  const override = new URLSearchParams(search).get("base");
  if (override !== null) {
    return { services: [{ name: override || "same-origin", adminBaseUrl: override }], links: [] };
  }

  try {
    const response = await fetcher("console.config.json", { headers: { accept: "application/json" } });
    if (response.status === 404) return { services: [SAME_ORIGIN], links: [] };   // no registry: one service
    if (!response.ok) {
      return { services: [SAME_ORIGIN], links: [], problem: `the service registry answered ${response.status}`, fellBack: true };
    }

    const file = (await response.json()) as RegistryFile;
    const listed = file.links ?? [];
    const names = new Set<string>();
    const links: LinkEntry[] = [];
    for (const entry of listed) {
      const name = typeof entry.name === "string" ? entry.name.trim() : "";
      const url = typeof entry.url === "string" ? resolveLink(entry.url, base) : null;
      // A repeated name is two tiles nobody can tell apart; the server refuses it, a
      // hand-written file may not have met the server.
      if (name.length === 0 || url === null || names.has(name.toLowerCase())) continue;
      names.add(name.toLowerCase());
      links.push({ name, url });
    }

    const droppedLinks = listed.length - links.length;
    const linkProblem = droppedLinks > 0
      ? `${droppedLinks} product console link${droppedLinks === 1 ? " was" : "s were"} skipped (no name, a repeated name, or an address the console will not open)`
      : undefined;

    // ABSENT services with links present is the links-only registry: this service, plus
    // tiles. PRESENT-but-empty stays what it always was — a broken registry — because the
    // server omits the key rather than emptying it, so only a mangled file says `[]`.
    if (file.services === undefined && listed.length > 0) {
      return linkProblem
        ? { services: [SAME_ORIGIN], links, problem: linkProblem, missing: "a product console" }
        : { services: [SAME_ORIGIN], links };
    }

    const services = (file.services ?? [])
      .map((entry) => ({
        name: typeof entry.name === "string" ? entry.name.trim() : "",
        adminBaseUrl: typeof entry.adminBaseUrl === "string" ? entry.adminBaseUrl.trim() : "",
      }))
      .filter((entry) => entry.name.length > 0);

    if (services.length === 0) {
      return { services: [SAME_ORIGIN], links, problem: "the service registry lists no service with a name", fellBack: true };
    }

    // A PARTIAL drop is the dangerous one: the console still works, still looks right, and
    // is quietly missing a service the operator configured (review R1 on this PR). A quietly
    // missing TILE is the same lie, so it is counted the same way.
    const dropped = (file.services ?? []).length - services.length;
    const serviceProblem = dropped > 0
      ? `${dropped} registry entr${dropped === 1 ? "y has" : "ies have"} no name and ${dropped === 1 ? "was" : "were"} skipped`
      : undefined;
    const problems = [serviceProblem, linkProblem].filter((text): text is string => text !== undefined);
    if (problems.length === 0) return { services, links };

    const missing = [serviceProblem && "a service", linkProblem && "a product console"].filter(Boolean).join(" and ");
    return { services, links, problem: problems.join("; "), missing };
  } catch {
    return { services: [SAME_ORIGIN], links: [], problem: "the service registry could not be read", fellBack: true };
  }
}
