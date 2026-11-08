# Goldpath.Data

Data-path floor of the Goldpath enterprise asset: the keyset cursor-pagination executor, the
save-contributor seam Ring B modules plug into, and money/time-safe model conventions.
Provider-neutral — Npgsql/SqlServer/Oracle are wired by the template from the manifest.

## Getting started

```csharp
builder.AddGoldpathData<WebApplicationBuilder, OrdersDbContext>(o => o.UseNpgsql(connectionString));

public class OrdersDbContext : DbContext
{
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
        => b.ApplyGoldpathConventions();          // string 256, decimal 18,4, TenantId conversion

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyGoldpathModelDefaults();        // enums stored as strings
}
```

## Configuration

Deliberately minimal — conventions are the configuration. Explicit per-property settings
always win over the defaults.

## Advanced

**Keyset pagination** (the executor of the `Goldpath.ApiDefaults` wire contract — never OFFSET):

```csharp
Page<OrderDto> page = await db.Orders
    .Where(o => o.Status == OrderStatus.Confirmed)
    .Select(o => new OrderDto(o.Id, o.CreatedAt, ...))
    .ToPageAsync(request, o => o.CreatedAt, o => o.Id, cancellationToken: ct);   // (timestamp, id) canonical pair
```

- Ordering is applied BY the executor from the key selectors — a mismatched manual
  order-by (the classic skipped-rows bug) cannot happen.
- Keys must be unique (single) or unique-together (pair). 1–2 keys, per-key direction.
- **Projection rule:** project with member-init records/types (`new Dto { X = o.X }`).
  Positional-constructor projections cannot be traced back to columns by EF, so the keyset
  ORDER BY fails to translate (found by the golden-path walking skeleton).
- A tampered/invalid cursor throws `GoldpathInvalidCursorException` → map to HTTP 400
  (the golden-path template wires this mapping).

**A `Contains` over a long local list** (`ChunkedContains`, RFC goldpath-oracle §2.5): Oracle refuses an
`IN` list longer than 1,000 (ORA-01795). Where a list's length is set by configuration, filter through the helper:

```csharp
await db.Items
    .Where(i => i.CampaignId == id)
    .ChunkedContains(i => i.Seq, seqs, db.Database.ProviderName)   // OR-joined chunks of ≤ 1,000 on Oracle; plain Contains elsewhere
    .ExecuteUpdateAsync(...);
```

The values are copied when the filter is built, and each chunk is a query parameter, never SQL text.

**Save-contributor seam** (for Ring B modules, not application code): implement
`IEntitySaveContributor`, register it, and every save runs it over Added/Modified/Deleted
entries with `GoldpathSaveContext` (clock + tenant). AuditTrail/SoftDelete/MultiTenancy attach here.

**Migrations** (RFC D1): Development auto-migrates (template wiring); every other environment
applies the CI-produced `dotnet ef migrations bundle` artifact as an explicit pipeline step.
Runtime `Migrate()` outside Development is forbidden (GP0302).

## Providers

Chosen in the manifest (`providers.db`), wired by the template: `postgresql` (Npgsql),
`sqlserver`, `oracle`. This package references only `Microsoft.EntityFrameworkCore.Relational`.
