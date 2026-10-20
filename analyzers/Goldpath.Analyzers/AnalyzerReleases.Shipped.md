; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 0.1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
GP0102 | Goldpath | Warning | Use IHttpClientFactory instead of new HttpClient()
GP0202 | Goldpath | Warning | Use keyset pagination instead of Skip/Take
GP0301 | Goldpath | Warning | Use DateTimeOffset on entities
GP0302 | Goldpath | Warning | Runtime Migrate/EnsureCreated must be Development-guarded
GP0303 | Goldpath | Error | Do not compose raw SQL from interpolated or concatenated strings
GP0401 | Goldpath | Error | Only IIntegrationEvent types may cross the service boundary
GP0402 | Goldpath | Error | A Mediant notification must not be an integration event
GP0501 | Goldpath | Error | IAuditLogged entities require AddGoldpathAuditLog() in the model
GP0502 | Goldpath | Warning | Audit stamp fields are filled by the AuditTrail contributor
GP0601 | Goldpath | Error | ISoftDeletable entities require ApplyGoldpathSoftDelete() in the model
GP1003 | Goldpath | Info | [Idempotent] on a query has no effect
GP0701 | Goldpath | Warning | Classified property on an integration event
GP0702 | Goldpath | Info | Classified property without the DataProtection module
GP0801 | Goldpath | Error | [Cacheable] on a command
GP0802 | Goldpath | Info | [InvalidatesCache] on a query has no effect
GP0803 | Goldpath | Warning | Raw string cache key bypasses tenant scoping
GP0901 | Goldpath | Error | IMultiTenant entities require ApplyGoldpathMultiTenancy in the model
GP0902 | Goldpath | Warning | TenantId is stamped by the MultiTenancy contributor
GP0903 | Goldpath | Warning | IgnoreQueryFilters on a tenant-filtered entity without Bypass
GP1101 | Goldpath | Warning | Raw string lock name bypasses tenant scoping
GP1102 | Goldpath | Warning | Lock handle is not disposed
GP1201 | Goldpath | Info | Anonymous endpoint inventory
GP1202 | Goldpath | Error | Auth secret as a string literal
GP1301 | Goldpath | Error | ExecuteChunkAsync opens its own transaction
GP1302 | Goldpath | Warning | Job registered without a Deadline
GP1303 | Goldpath | Info | PlanAsync materializes the item list
GP1401 | Goldpath | Error | Archived graph carries classified data without the DataProtection module
GP1402 | Goldpath | Warning | Row retention without a Where guard
GP1403 | Goldpath | Info | Archive definition without a DueWhen lifecycle event
GP1501 | Goldpath | Error | Bulk batch definition without a MaxRows ceiling
GP1502 | Goldpath | Warning | Bulk row handler calls SaveChanges directly
GP1503 | Goldpath | Info | Bulk batch definition skips the approval gate
GP1601 | Goldpath | Warning | Direct SMTP client bypasses the notifier
GP1602 | Goldpath | Info | Notification template without a DeleteBodyAfter window
GP1701 | Goldpath | Error | Campaign type without a MaxTargets ceiling
GP1702 | Goldpath | Warning | Campaign item handler calls SaveChanges directly
GP1703 | Goldpath | Info | Campaign item handler messages humans without the notification seam
GP1801 | Goldpath | Warning | Shared Goldpath tables mapped by a second context without excludeFromMigrations
GP0904 | Goldpath | Warning | Admin endpoint takes a tenant parameter but never consults AdminTenantScope
GP1001 | Goldpath | Warning | Mediant command without [Idempotent] while idempotency is composed
GP1002 | Goldpath | Error | Broker consumer composed without the EF inbox (AddGoldpathOutbox)
GP1004 | Goldpath | Warning | [Idempotent] with no key expression and no detectable natural key
GP0404 | Goldpath | Warning | Publish through IIntegrationEventPublisher, not MassTransit's IPublishEndpoint
GP0405 | Goldpath | Warning | Consume through IIntegrationEventHandler + AddGoldpathHandler, not MassTransit's IConsumer
GP2001 | Goldpath | Warning | ProductDeclaresGoldpathNamespaceAnalyzer, [Documentation](https://github.com/qorpe/goldpath/blob/main/docs/rfc/goldpath-platform-sdk.md)
GP1901 | Goldpath | Error | Approval ladder without an unbounded top rung
GP1902 | Goldpath | Warning | Approvals composed without the escalation sweep
GP2101 | Goldpath | Warning | File rail without a row contract (ValidateRow)
GP2102 | Goldpath | Warning | File exchange composed without a rail
