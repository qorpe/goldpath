# Goldpath.Console

The Goldpath operations console, served by **your** management head.

```csharp
app.MapGoldpathConsole();                                  // same-origin, this service only
app.MapGoldpathConsole(configure: console => console
    .AddService("payments", "https://payments.internal")
    .AddService("claims", "https://claims.internal"));     // one console, many services
app.MapGoldpathConsole(configure: console => console
    .AddLink("Coexist", "/coexist/console/")              // a product console on this app
    .AddLink("Mockifyr", "https://mockifyr.internal/"));  // a product console elsewhere
```

- The console is a **client of the frozen admin contract** — it adds no capability the API
  does not already expose, and it carries the operator's own credentials.
- Its assets are **embedded in this package**: adopters never run Node, and a generated app
  stays Node-free by construction.
- It sits behind the **same ops floor** as the admin surfaces (`goldpath-ops`). Pass
  `exposeUnsecured: true` only for a host with no auth at all — the guard logs that choice.
- The cross-service registry is **configuration**, not a file you drop beside the dist:
  `AddService(...)` is what the console reads at startup.
- Products own their UI — the console **links, never embeds**. `AddLink(...)` puts a tile on
  Today that leads to a product's own console. A link is never probed (a tile asserts an
  address, not health), and its URL must be a root-relative path or an absolute http(s)
  address without credentials: anything else stops the app at startup. Runbook:
  `ops/console.md`.

Full design: `docs/rfc/goldpath-console.md`; link tiles: `docs/rfc/goldpath-console-link-tiles.md`.
