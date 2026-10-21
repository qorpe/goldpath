import { useEffect, useState } from "react";
import { Banner, DensityToggle, IconAction, PageHeader, StatCard, StateBadge } from "@qorpe/ui";
import { RefreshCw } from "lucide-react";
import { MODULES, type AdminClient, type ModuleName } from "./adminClient";
import { SECTION_ICON, type Capabilities } from "./sections";
import { ProductLinks } from "./ProductLinks";
import type { LinkEntry } from "./registry";
import { collectServiceTriage, orderTriage, TRIAGE_SCOPE, type ServiceTriage, type TriageRow } from "./triage";

export interface TriageService {
  name: string;
  client: AdminClient;
  capabilities: Capabilities | null;
}

export interface TriageHomeProps {
  services: TriageService[];
  /** Opens the panel a row belongs to, on the service it belongs to. */
  onOpen: (service: string, section: ModuleName) => void;
  now?: Date;
  /** Product consoles, drawn as link tiles BELOW the triage — "is anything wrong" stays first. */
  links?: LinkEntry[];
}

/**
 * What each module's card COUNTS — the one number that module is asked about the morning
 * after. Every count is read from the contract's own take-bounded lists (the scope line
 * above the cards says so); tone applies only when the number is not zero.
 */
const CARD: Record<ModuleName, { label: string; tone: "danger" | "warning" }> = {
  jobs: { label: "Failed runs", tone: "danger" },
  bulk: { label: "Awaiting approval", tone: "warning" },
  campaign: { label: "Failed campaign items", tone: "warning" },
  notification: { label: "Failed notifications", tone: "danger" },
  archival: { label: "Due to archive", tone: "warning" },
  approvals: { label: "Awaiting decision", tone: "warning" },
  fileexchange: { label: "Rows in quarantine", tone: "warning" },
};

/**
 * The operator's first screen (console RFC D2): "is anything wrong", across every service
 * in the registry. Fleet browsing is one click away, never the landing page — an operator
 * opens a console to answer that question, not to browse.
 *
 * Every row is a deep link into the panel that owns it. Nothing here is a metric the API
 * does not already publish: the triage reads the same take-bounded lists an operator would
 * and counts what it was given, which is why the scope is printed rather than implied.
 */
export function TriageHome({ services, onOpen, now, links = [] }: TriageHomeProps) {
  const [collected, setCollected] = useState<{ rows: TriageRow[]; services: (ServiceTriage & { name: string })[] } | null>(null);
  const [refreshToken, setRefreshToken] = useState(0);
  const rows = collected?.rows ?? null;

  const ready = services.filter((service) => service.capabilities !== null);
  const readyKey = ready.map((service) => service.name).join("|");

  useEffect(() => {
    if (ready.length === 0) return;
    let live = true;
    setCollected(null);
    void Promise.all(
      ready.map((service) =>
        collectServiceTriage(service.name, service.client, service.capabilities!, now).then((result) => ({ ...result, name: service.name })),
      ),
    ).then((services) => live && setCollected({ rows: orderTriage(services.flatMap((service) => service.rows)), services }));
    return () => {
      live = false;
    };
    // `readyKey` is the honest dependency: the set of services whose capabilities are known.
  }, [readyKey, refreshToken]);

  const waiting = services.length - ready.length;

  return (
    <div data-testid="triage-home" className="space-y-4">
      {/* The estate screen opens like every other screen (§8.1) — one header pattern. */}
      <PageHeader
        title="Today"
        purpose={`Across ${services.length} service${services.length === 1 ? "" : "s"} · ${TRIAGE_SCOPE}.`}
        actions={
          <span className="flex items-center gap-2">
            <IconAction icon={<RefreshCw />} label="Refresh" onClick={() => setRefreshToken((token) => token + 1)} />
            <DensityToggle />
          </span>
        }
      />

      {waiting > 0 && (
        <p className="text-xs text-muted-foreground">
          {waiting} service{waiting === 1 ? " is" : "s are"} still answering — this list will grow.
        </p>
      )}

      {rows === null && ready.length > 0 && <p className="text-sm text-muted-foreground">Reading every surface…</p>}

      {collected !== null && (() => {
        // One card per module the estate can actually COUNT: a module nobody composed —
        // or whose surface could not be read — shows no card rather than a false zero.
        const cards = MODULES.filter((module) => collected.services.some((service) => service.stats[module] !== undefined));
        if (cards.length === 0) return null;
        return (
          <div data-testid="triage-cards" className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5">
            {cards.map((module) => {
              const owners = collected.services.filter((service) => service.stats[module] !== undefined);
              const value = owners.reduce((sum, service) => sum + (service.stats[module] ?? 0), 0);
              // The card deep-links ONLY when one service owns the whole number: a
              // combined total belongs to no single service, and landing an operator on
              // a screen that accounts for part of it would contradict the count they
              // clicked (review R1). The rows below carry the per-service links.
              const owner = owners.length === 1 ? owners[0] : undefined;
              return (
                <StatCard
                  key={module}
                  icon={SECTION_ICON[module]}
                  label={CARD[module].label}
                  value={value.toLocaleString("en-US")}
                  tone={value > 0 ? CARD[module].tone : undefined}
                  onClick={owner ? () => onOpen(owner.name, module) : undefined}
                />
              );
            })}
          </div>
        );
      })()}

      {rows !== null && rows.length === 0 && (
        // The quiet morning. Said plainly, and scoped — "nothing found HERE" is not the
        // same claim as "nothing is wrong", and the console must not make the second one.
        <Banner tone="success" live="status">
          Nothing is waiting for you in {TRIAGE_SCOPE}.
        </Banner>
      )}

      {rows !== null && rows.length > 0 && (
        // The family card-list (§8.2): ONE lifted card, rows divided inside — the
        // reference dashboard's activity-list pattern, not floating row chips.
        <ul className="divide-y divide-border rounded-2xl border border-border bg-background" style={{ boxShadow: "var(--shadow-surface)" }}>
          {rows.map((row, index) => (
            <li
              key={`${row.service}-${row.section}-${row.headline}-${index}`}
              className="flex flex-wrap items-center gap-3 px-4 py-3"
            >
              {/* The word carries the meaning; the tone only reinforces it. Colour alone
                  would leave the row unreadable to whoever cannot see the difference. */}
              <StateBadge
                state={row.blind ? "cannot see" : row.tone === "danger" ? "went wrong" : "will, unless"}
                tone={row.tone}
              />
              <button
                className="text-sm font-medium underline-offset-2 hover:underline"
                onClick={() => onOpen(row.service, row.section)}
              >
                {row.headline}
              </button>
              <span className="text-xs text-muted-foreground">{row.detail}</span>
              <span className="ml-auto text-xs text-faint">{row.service}</span>
            </li>
          ))}
        </ul>
      )}

      <ProductLinks links={links} />
    </div>
  );
}
