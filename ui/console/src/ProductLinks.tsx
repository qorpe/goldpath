import { ExternalLink } from "lucide-react";
import type { LinkEntry } from "./registry";

export interface ProductLinksProps {
  links: LinkEntry[];
  /** The origin a same-origin link is shown relative to; the page's own by default. */
  origin?: string;
}

/**
 * Where a tile goes, said before the operator goes there: the path when the product lives
 * on this console's own origin (the Coexist shape), the host when it lives elsewhere (the
 * Mockifyr shape).
 */
export function destinationOf(url: string, origin: string): string {
  const target = new URL(url);
  return target.origin === origin ? `${target.pathname}${target.search}` : target.host;
}

/**
 * The product console tiles (console RFC §3 "configured LINK tiles", D1's "link/iframe-free
 * tile row"; decision record `docs/rfc/goldpath-console-link-tiles.md`). The console LINKS,
 * never embeds: each tile is a native link to a console whose UI is its own.
 *
 * A tile asserts an ADDRESS, never health — there is no dot, no count and no probe here,
 * because a status the console never measured is the false claim triage exists to prevent.
 * Same tab, like the kit's own `href` rail items: a ⌘-click still opens a second one, and
 * there is no new-window announcement to get wrong.
 *
 * No links, no section — never an empty one.
 */
export function ProductLinks({ links, origin = globalThis.location?.origin ?? "" }: ProductLinksProps) {
  if (links.length === 0) return null;

  return (
    <nav aria-label="Product consoles" data-testid="product-links">
      <h2 className="section-title">Product consoles</h2>
      <ul className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {links.map((link) => (
          <li key={link.name}>
            <a
              href={link.url}
              referrerPolicy="strict-origin-when-cross-origin"
              className="flex items-center gap-3 rounded-2xl border border-border bg-background p-4 transition-colors hover:border-border-strong"
              style={{ boxShadow: "var(--shadow-surface)" }}
            >
              <span className="min-w-0 flex-1">
                <span className="block text-sm font-semibold">{link.name}</span>
                <span className="block truncate text-xs text-muted-foreground">{destinationOf(link.url, origin)}</span>
              </span>
              <ExternalLink aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
