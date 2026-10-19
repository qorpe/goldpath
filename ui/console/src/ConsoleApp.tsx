import { useEffect, useMemo, useState } from "react";
import { AppShell, Banner, CommandPalette, DensityProvider, PageHeader, initialCollapsed, openCommand } from "@qorpe/ui";
import type { CommandGroup, ShellNavItem } from "@qorpe/ui";
import { AdminClient, type ModuleName } from "./adminClient";
import { composedSections, isUnreachable, SECTION_GROUP, SECTION_ICON, SECTION_LABEL, SECTION_PURPOSE, ServicePanels, type Capabilities } from "./sections";
import { TriageHome } from "./TriageHome";
import { loadRegistry, SAME_ORIGIN, type LinkEntry, type ServiceEntry } from "./registry";

export interface ConsoleAppProps {
  title?: string;
  fetcher?: typeof fetch;
  /** Injected in tests; defaults to the browser's own query string. */
  search?: string;
  now?: Date;
  /** How a palette command leaves for a product console; the browser's own by default. */
  navigate?: (url: string) => void;
}

/** The landing section is the estate, not a module — hence its own id. */
const TODAY = "today";

const GROUP_ORDER = ["Modules"];
type Section = typeof TODAY | ModuleName;

/**
 * The console (console RFC §3 + D2). It owns what a single service cannot: WHICH services
 * exist (config, not discovery), which one the operator is looking at, and the triage home
 * that answers "is anything wrong" across all of them before any of them is opened.
 */
export function ConsoleApp({
  title = "Goldpath Console",
  fetcher,
  search,
  now,
  navigate = (url) => globalThis.location.assign(url),
}: ConsoleAppProps) {
  const [services, setServices] = useState<ServiceEntry[] | null>(null);
  const [links, setLinks] = useState<LinkEntry[]>([]);
  const [problem, setProblem] = useState<{ text: string; fellBack: boolean; missing: string } | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [section, setSection] = useState<Section>(TODAY);
  const [collapsed, setCollapsed] = useState(initialCollapsed);
  // A batch handed its run id across sections — carried as a fresh object per ask.
  const [runRequest, setRunRequest] = useState<{ id: string } | null>(null);

  useEffect(() => {
    let live = true;
    void loadRegistry(fetcher, search).then((result) => {
      if (!live) return;
      setServices(result.services);
      setLinks(result.links);
      setProblem(
        result.problem
          ? { text: result.problem, fellBack: result.fellBack === true, missing: result.missing ?? "a service" }
          : null,
      );
      setActive((current) => current ?? result.services[0]?.name ?? null);
    });
    return () => {
      live = false;
    };
  }, [fetcher, search]);

  // One client per service, kept STABLE across renders: the capability hook keys off it,
  // and a fresh client every render would re-probe every service forever.
  const clients = useMemo(() => {
    const map = new Map<string, AdminClient>();
    for (const service of services ?? []) {
      map.set(service.name, new AdminClient({ baseUrl: service.adminBaseUrl, fetcher }));
    }

    return map;
  }, [services, fetcher]);

  // EVERY service is probed, not just the open one: the triage home speaks for the whole
  // estate, and a service whose capabilities are unknown is a service it cannot speak for.
  const [discovered, setDiscovered] = useState<Map<string, Capabilities>>(new Map());

  useEffect(() => {
    let live = true;
    setDiscovered(new Map());
    void Promise.all(
      [...clients].map(async ([name, entry]) => [name, await entry.discoverCapabilities()] as const),
    ).then((entries) => live && setDiscovered(new Map(entries)));
    return () => {
      live = false;
    };
  }, [clients]);

  const service = (services ?? []).find((entry) => entry.name === active) ?? services?.[0] ?? SAME_ORIGIN;
  const client = clients.get(service.name) ?? new AdminClient({ baseUrl: service.adminBaseUrl, fetcher });
  const capabilities = discovered.get(service.name) ?? null;

  if (services === null) {
    return <p className="p-6 text-sm text-muted-foreground">Reading the service registry…</p>;
  }

  const nav: ShellNavItem[] = [
    { id: TODAY, label: "Today", icon: SECTION_ICON.today, group: "Overview", onSelect: () => setSection(TODAY) },
    // A service that never answered has no sections to offer: the nav would be a list of
    // links that each say the same thing.
    // One family, one group (v1.1 §7.2 as amended); the sort stays for the day a second
    // group exists.
    ...(isUnreachable(capabilities) ? [] : composedSections(capabilities))
      .slice()
      .sort((a, b) => GROUP_ORDER.indexOf(SECTION_GROUP[a]) - GROUP_ORDER.indexOf(SECTION_GROUP[b]))
      .map((module) => ({
      id: module,
      label: SECTION_LABEL[module],
      icon: SECTION_ICON[module],
      group: SECTION_GROUP[module],
      onSelect: () => setSection(module),
    })),
  ];

  const open = (target: string, targetSection: ModuleName) => {
    setActive(target);
    setSection(targetSection);
  };

  // The palette offers exactly what the rail offers — same sections, same icons — plus
  // the estate's service switch. Nothing here is a verb: commands GO places, and the
  // places themselves own their actions (v1.1 §7.14).
  const commands: CommandGroup[] = [
    { heading: "Go to", items: nav.map((item) => ({ id: item.id, label: item.label, icon: item.icon, run: item.onSelect })) },
    ...(services.length > 1
      ? [{
          heading: "Services",
          items: services.map((entry) => ({ id: `service-${entry.name}`, label: `Open ${entry.name}`, run: () => setActive(entry.name) })),
        }]
      : []),
    // A product console is a place too, so it is a command — and the only address it can
    // go to is one the registry reader already resolved and approved (resolveLink).
    ...(links.length > 0
      ? [{
          heading: "Consoles",
          items: links.map((link) => ({ id: `link-${link.name}`, label: `Open ${link.name}`, run: () => navigate(link.url) })),
        }]
      : []),
  ];

  return (
    <DensityProvider>
    <AppShell
      title={title}
      subtitle="Operations"
      nav={nav}
      activeId={section}
      services={services.length > 1 ? services.map((entry) => ({ name: entry.name, onSelect: () => setActive(entry.name) })) : undefined}
      activeService={service.name}
      collapsed={collapsed}
      onToggleCollapsed={() => setCollapsed(!collapsed)}
      onSearch={openCommand}
      onHome={() => setSection(TODAY)}
    >
      <CommandPalette label="Search sections…" groups={commands} />

      {problem && (
        // Config that failed to load is NOT a quiet fallback: an operator who configured
        // four services and sees one is looking at the wrong console.
        <Banner tone="warning" live="alert">
          {problem.text}
          {problem.fellBack ? " — showing this service only." : ` — the console is missing ${problem.missing} you configured.`}
        </Banner>
      )}

      {section === TODAY ? (
        <TriageHome
          services={services.map((entry) => ({
            name: entry.name,
            client: clients.get(entry.name)!,
            capabilities: discovered.get(entry.name) ?? null,
          }))}
          onOpen={open}
          now={now}
          links={links}
        />
      ) : (
        <>
          <PageHeader title={SECTION_LABEL[section]} purpose={SECTION_PURPOSE[section]} />
          <ServicePanels
            client={client}
            capabilities={capabilities}
            section={section}
            now={now}
            openRunRequest={runRequest}
            onRunRequestConsumed={() => setRunRequest(null)}
            onOpenRun={(runId) => {
              setRunRequest({ id: runId });
              setSection("jobs");
            }}
          />
        </>
      )}
    </AppShell>
    </DensityProvider>
  );
}
