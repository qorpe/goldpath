using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Goldpath;

/// <summary>The outbox and inbox tables on an application's own context (RFC goldpath-messaging-engines §4.2, §4.4).</summary>
public static class GoldpathOutboxModelExtensions
{
    /// <summary>
    /// Maps the Goldpath outbox schema — <c>InboxState</c>, <c>OutboxMessage</c> and <c>OutboxState</c> —
    /// which is the schema every adopter's migrations already hold: the same tables and columns the
    /// three engine-named calls map, so swapping them for this one generates no migration.
    /// </summary>
    public static ModelBuilder AddGoldpathOutboxEntities(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        return modelBuilder;
    }
}
