# Goldpath.Bulk

File/set intake — **L3 of the execution ladder**. A file becomes a validated, approved,
resumable run:

```
upload → parse (CSV v1) → per-row validation → report → APPROVE/REJECT gate → execute
                                                              │
                                                              └── as a Goldpath.Jobs run:
                                                                  chunked, checkpointed,
                                                                  repair queue + replay
```

## Wire it

```csharp
builder.AddGoldpathBulk<WebApplicationBuilder, OrdersDbContext>(bulk =>
{
    bulk.AddBatch<PaymentRow>("payments", b => b
        .Csv(c => c.Delimiter = ';')
        .MaxRows(50_000)                       // the ceiling is a decision, not a default
        .RowKey(r => r.EndToEndId)             // in-file duplicate = validation error
        .Validate((row, ctx) =>
        {
            if (row.Amount <= 0) ctx.Fail(nameof(row.Amount), "amount must be positive");
        }));
});
builder.Services.AddScoped<IGoldpathBulkRowHandler<PaymentRow>, PaymentRowHandler>();

builder.AddGoldpathJobs<WebApplicationBuilder, OrdersDbContext>(jobs =>
{
    jobs.ConnectionName = "ordersdb";
    jobs.AddGoldpathBulkJobs<OrdersDbContext>();    // validate + execute runs (cron safety nets)
});

// DbContext: modelBuilder.AddGoldpathBulk();  modelBuilder.AddGoldpathJobs();
```

## The guarantees

- **Content-hash dedup:** identical bytes return the SAME batch — a client retry storm
  cannot create a double-payment risk. A REJECTED file may be resubmitted deliberately.
- **The gate:** approval refuses invalid rows by default (`TolerateInvalidRows()` opts
  into executing the valid subset — the report is the evidence of what was skipped).
  Rejection requires a reason. Every decision carries the actor.
- **Value-free reports:** row errors carry row + field + message, never the offending
  value — classified data stays out of reports by construction; the raw file is the
  evidence, with `DeleteFileAfter` retention.
- **At-most-once-or-repair:** the executing chunk CLAIMS rows (persisted) before any
  side effect. A row claimed but never stamped was interrupted mid-flight — it goes to
  the repair queue for a human-confirmed replay instead of being silently re-sent.
- **Row failures don't stop the batch:** they land in the jobs repair queue; the jobs
  `replay-items` verb retries them through your handler; the last cleared failure flips
  the batch to Completed.
- **A row runs as its batch's tenant:** on a multi-tenant app (one that composes
  `AddGoldpathMultiTenancy`) the engine pins the batch's tenant (the uploader's) as the
  ambient tenant around your row handler and around the chunk's save, and around a replay.
  Your handler does not wrap itself in `GoldpathTenant.Use(...)`: an entity it adds is
  stamped and guarded as that tenant. A single-tenant app's batch label is free-form and
  pins nothing. On a multi-tenant app a stored tenant that is not a tenant id fails each
  row, and its replay, with a message naming it.
- **What a failed row newly added or loaded is not saved:** when your handler throws, the
  entities it started tracking on the engine's `DbContext` are untracked, so the chunk's
  save does not write them; the row goes to the repair queue with your exception's message.
  Its changes to an entity tracked BEFORE it (one an earlier row added, or a shared one
  loaded earlier) are NOT undone and are saved with the chunk (open thread T71). What the
  handler already SAVED itself, or sent to another system, is beyond the engine's reach,
  which is why a handler should leave the save to the engine. A save that fails on one
  row's data (a constraint) still fails the whole chunk today (open thread T70).

Ops surface (admin API, metrics panel, runbooks) ships in S2; run views live in the JOBS
console today — a bulk execution IS a jobs run.
