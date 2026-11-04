using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>An active delegation row (the record itself has no key; the row carries one).</summary>
public sealed class GoldpathApprovalDelegationRow
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>Who delegated.</summary>
    public string From { get; set; } = "";

    /// <summary>Who received the delegation.</summary>
    public string To { get; set; } = "";

    /// <summary>Absolute UTC expiry.</summary>
    public DateTimeOffset Until { get; set; }
}

/// <summary>One collected quorum signature as a row (the record itself has no key; the row carries one).</summary>
public sealed class GoldpathApprovalSignatureRow
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The request the signature belongs to.</summary>
    public Guid RequestId { get; set; }

    /// <summary>Who signed.</summary>
    public string SignedBy { get; set; } = "";

    /// <summary>The rung the signature counts for.</summary>
    public string Role { get; set; } = "";

    /// <summary>When it was signed (UTC).</summary>
    public DateTimeOffset At { get; set; }
}

/// <summary>Model mapping for the database-backed approval store.</summary>
public static class GoldpathApprovalModelExtensions
{
    private static readonly JsonSerializerOptions TrailJson = JsonSerializerOptions.Default;

    /// <summary>Maps the approval tables. Call from the app context's <c>OnModelCreating</c>.</summary>
    public static ModelBuilder AddGoldpathApprovalModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldpathApprovalRequest>(e =>
        {
            e.ToTable("GoldpathApprovals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Ladder).HasMaxLength(128);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.RequestedBy).HasMaxLength(128);
            e.Property(x => x.PendingRole).HasMaxLength(128);
            e.Property(x => x.DecidedBy).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Status);
            // The trail is one audit document, read and written with its request — a JSON
            // column, not a join, so the store stays two tables on every provider.
            e.Property(x => x.Trail)
                .HasConversion(
                    trail => JsonSerializer.Serialize(trail, TrailJson),
                    json => JsonSerializer.Deserialize<List<GoldpathApprovalTrailEntry>>(json, TrailJson) ?? new List<GoldpathApprovalTrailEntry>(),
                    new ValueComparer<List<GoldpathApprovalTrailEntry>>(
                        (a, b) => JsonSerializer.Serialize(a, TrailJson) == JsonSerializer.Serialize(b, TrailJson),
                        v => JsonSerializer.Serialize(v, TrailJson).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<GoldpathApprovalTrailEntry>>(JsonSerializer.Serialize(v, TrailJson), TrailJson)!))
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                // A DOCUMENT, not a scalar (#198): a conventions host defaults strings to
                // 256, and three trail entries already exceed that — the audit column must
                // say its own shape.
                .HasMaxLength(-1);
            e.Property(x => x.Reason).HasMaxLength(1024);   // human decision text — bounded, but not by accident
            // Optimistic concurrency (#480): every write checks the version the request was read at, so two decisions
            // read from the same state cannot both apply. Internal on the entity, so the public shape is unchanged.
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<GoldpathApprovalDelegationRow>(e =>
        {
            e.ToTable("GoldpathApprovalDelegations");
            e.HasKey(x => x.Id);
            e.Property(x => x.From).HasMaxLength(128);
            e.Property(x => x.To).HasMaxLength(128);
            e.HasIndex(x => x.Until);
        });

        modelBuilder.Entity<GoldpathApprovalSignatureRow>(e =>
        {
            e.ToTable("GoldpathApprovalSignatures");
            e.HasKey(x => x.Id);
            e.Property(x => x.SignedBy).HasMaxLength(128);
            e.Property(x => x.Role).HasMaxLength(128);
            // One identity signs a request once (#480): the distinct-eyes rule, held by the database as well as the
            // engine, so a double click cannot slip two signatures between the check and the write. It also serves
            // the per-request lookup the plain RequestId index used to.
            e.HasIndex(x => new { x.RequestId, x.SignedBy }).IsUnique();
        });

        return modelBuilder;
    }
}

/// <summary>
/// The database-backed store: approval state lives in the app's own DbContext (mapped by
/// <see cref="GoldpathApprovalModelExtensions.AddGoldpathApprovalModel"/>), so requests
/// survive restarts and every node sees the same worklist. Each call runs in its own scope,
/// so the store itself carries no scoped state.
/// </summary>
public sealed class GoldpathEfApprovalStore<TContext> : IGoldpathApprovalStore, IGoldpathApprovalAtomicStore
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;

    /// <summary>Registered by <c>AddGoldpathApprovals&lt;TBuilder, TContext&gt;</c>.</summary>
    public GoldpathEfApprovalStore(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <inheritdoc />
    public async Task AddAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GoldpathApprovalRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Refused with an <see cref="InvalidOperationException"/> when the row changed since
    /// <paramref name="request"/> was read (#480).</remarks>
    public async Task UpdateAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var readAt = Track(db, request);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            request.Version = readAt;
            throw new GoldpathApprovalConflictException(request.Id, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathApprovalRequest>().AsNoTracking()
            .Where(x => x.Status == GoldpathApprovalStatus.Pending)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathApprovalRequest>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // Server-side ORDER BY + Take is the contract: the table grows unboundedly and
        // the admin window must not pull it whole. SQLite refuses ORDER BY on a raw
        // DateTimeOffset column — SQLite consumers apply the family convention
        // (DateTimeOffsetToBinaryConverter in ConfigureConventions), same as Bulk/Archival.
        return await db.Set<GoldpathApprovalRequest>().AsNoTracking()
            .OrderByDescending(r => r.RequestedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddDelegationAsync(GoldpathApprovalDelegation delegation, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(new GoldpathApprovalDelegationRow { From = delegation.From, To = delegation.To, Until = delegation.Until });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathApprovalDelegation>> GetDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // DateTimeOffset comparison does not translate on every provider (SQLite);
        // delegations are few by nature, so the expiry filter runs client-side.
        var rows = await db.Set<GoldpathApprovalDelegationRow>().AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.Where(r => r.Until > now)
            .Select(r => new GoldpathApprovalDelegation(r.From, r.To, r.Until)).ToList();
    }

    /// <inheritdoc />
    /// <remarks>A second signature by the same <c>SignedBy</c> on the same request is refused with an
    /// <see cref="InvalidOperationException"/> (#480).</remarks>
    public async Task AddSignatureAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(Row(signature));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            if (await SignedAsync(signature, cancellationToken))
            {
                throw new GoldpathApprovalConflictException(signature.RequestId, e);
            }

            throw;
        }
    }

    async Task IGoldpathApprovalAtomicStore.SignAsync(GoldpathApprovalSignature signature, GoldpathApprovalRequest request, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(Row(signature));
        var readAt = Track(db, request);
        try
        {
            // One SaveChanges is one transaction: the signature and the request's new state land together or not at all.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            request.Version = readAt;
            throw new GoldpathApprovalConflictException(request.Id, e);
        }
        catch (DbUpdateException e)
        {
            request.Version = readAt;
            if (await SignedAsync(signature, cancellationToken))
            {
                // The unique key refused it: the same identity's signature landed first.
                throw new GoldpathApprovalConflictException(request.Id, e);
            }

            throw;
        }
        catch
        {
            request.Version = readAt;
            throw;
        }
    }

    /// <summary>Attaches a request read elsewhere as modified, checked against the version it was read at and moved on
    /// by one. Returns the version it was read at.</summary>
    private static long Track(TContext db, GoldpathApprovalRequest request)
    {
        var readAt = request.Version;
        db.Update(request);
        var version = db.Entry(request).Property(x => x.Version);
        version.OriginalValue = readAt;
        version.CurrentValue = readAt + 1;
        return readAt;
    }

    private static GoldpathApprovalSignatureRow Row(GoldpathApprovalSignature signature)
        => new() { RequestId = signature.RequestId, SignedBy = signature.SignedBy, Role = signature.Role, At = signature.At };

    /// <summary>Whether a failed write failed because this identity's signature is already there — read on a fresh
    /// context, so a provider's error text never decides it.</summary>
    private async Task<bool> SignedAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathApprovalSignatureRow>().AsNoTracking()
            .AnyAsync(x => x.RequestId == signature.RequestId && x.SignedBy == signature.SignedBy, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathApprovalSignature>> GetSignaturesAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // Ordered client-side for the same provider reason as the delegations above —
        // DateTimeOffset ordering does not translate on SQLite, and one request's
        // signatures are few by nature.
        var rows = await db.Set<GoldpathApprovalSignatureRow>().AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.At)
            .Select(r => new GoldpathApprovalSignature(r.RequestId, r.SignedBy, r.Role, r.At)).ToList();
    }
}
