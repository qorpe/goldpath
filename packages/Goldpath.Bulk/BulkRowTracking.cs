using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Goldpath;

/// <summary>
/// What one row's handler started tracking on the execute run's context, so a failed row's work can be left out of
/// the chunk's save (#539) without a snapshot of the whole tracker per row (#605). Recording costs O(1) per entity the
/// row tracks; nothing here enumerates what the context already tracked.
/// </summary>
/// <remarks>
/// <para>
/// EF raises <see cref="ChangeTracker.Tracked"/> for two different reasons, and the difference matters. An entity the
/// handler adds, attaches or loads is the row's own (<c>direct</c>). An entity found by <c>DetectChanges</c> hanging
/// on a navigation (<c>discovered</c>) may be the row's — a child it hung on its own new entity — or an EARLIER,
/// successful row's child hung on an entry tracked before this row, which nothing discovered until this row's handler
/// happened to trigger a detection. The tracker snapshot this replaces ran a detection at every row start, so the
/// earlier row's child was never attributed to this one; this keeps that: a discovered entity is the row's only when
/// a navigation or a foreign key joins it to an entity the row owns, in either direction (a child, a grandchild, a
/// principal, a many-to-many join entity). Anything else stays tracked and is saved.
/// </para>
/// <para>
/// Only an entity the row CREATED may claim another (<c>anchors</c>): one EF began tracking as <see cref="EntityState.Added"/>
/// and not from a query. An entity the row loaded, attached or marked for update is not the row's work, so it is still
/// left out itself, and an earlier, successful row's entity may name it by a foreign key or hang on it, so it claims a
/// discovered entity only when nothing outside the row links to that entity: then the entity was not reachable before
/// the row, it is the row's (a child the row hung on what it loaded), and it is left out, as the per-row snapshot did
/// (#609). When something outside the row also links to it, the attribution is AMBIGUOUS — an earlier row's write and
/// this row's insert look the same — and the engine does not guess: <see cref="UntrackRow"/> answers
/// <see langword="false"/> and the chunk's save is refused (<see cref="RevertChunk"/>), so a failed row never has an
/// insert committed and an executed row's write is never silently dropped. Finding what is outside enumerates the
/// tracker, and only a failing row that brought an entity in and has a discovered entity linked to it pays for it.
/// </para>
/// <para>
/// What the row did through entries tracked BEFORE it, including a new entity hung on such an entry's navigation that
/// is linked to nothing the row tracked, is not undone (open thread T71).
/// </para>
/// </remarks>
internal sealed class BulkRowTracking : IDisposable
{
    private readonly ChangeTracker _tracker;
    private readonly List<EntityEntry> _direct = [];
    private readonly HashSet<object> _created = new(ReferenceEqualityComparer.Instance);
    private readonly List<EntityEntry> _discovered = [];
    private int _detecting;

    /// <summary>Starts recording on <paramref name="tracker"/> until disposed.</summary>
    public BulkRowTracking(ChangeTracker tracker)
    {
        _tracker = tracker;
        _tracker.Tracked += OnTracked;
        _tracker.DetectingAllChanges += OnDetecting;
        _tracker.DetectedAllChanges += OnDetected;
        _tracker.DetectingEntityChanges += OnDetecting;
        _tracker.DetectedEntityChanges += OnDetected;
    }

    /// <summary>Forgets the previous row: what is recorded from here on is the next row's.</summary>
    public void BeginRow()
    {
        _direct.Clear();
        _created.Clear();
        _discovered.Clear();
        _detecting = 0;
    }

    /// <summary>
    /// Stops tracking what the current row owns, so the chunk's save skips it. Answers <see langword="false"/>, and
    /// untracks nothing, when what the row owns cannot be told apart from work tracked outside it (#609): the caller
    /// must then refuse the chunk's save.
    /// </summary>
    public bool UntrackRow(DbContext db)
    {
        // No detection while the row's entities are read: a detection here would track more, mid-walk.
        var autoDetect = _tracker.AutoDetectChangesEnabled;
        _tracker.AutoDetectChangesEnabled = false;
        try
        {
            if (Owned() is not { } owned)
            {
                return false;
            }

            foreach (var recorded in owned)
            {
                var entry = Current(db, recorded);
                if (entry.State != EntityState.Detached)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return true;
        }
        finally
        {
            _tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    /// <summary>
    /// Refuses the chunk's save (#609): every change pending on the context except the engine's own bookkeeping is
    /// undone: an added entity is untracked, and a modified or deleted one is made unchanged, which EF does by restoring
    /// its original values. Every change pending here is the chunk's handlers' work, because the claim's save committed
    /// everything before the first handler ran. The caller then stamps the chunk's executed rows failed, so they reach
    /// the repair queue. <see cref="ChangeTracker.Entries()"/> detects first, so a new entity hung on a navigation is
    /// found and untracked too.
    /// </summary>
    public static void RevertChunk(DbContext db, Func<EntityEntry, bool> bookkeeping)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => !bookkeeping(e)).ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tracker.Tracked -= OnTracked;
        _tracker.DetectingAllChanges -= OnDetecting;
        _tracker.DetectedAllChanges -= OnDetected;
        _tracker.DetectingEntityChanges -= OnDetecting;
        _tracker.DetectedEntityChanges -= OnDetected;
    }

    /// <summary>
    /// The row's direct entities, then every discovered one linked to an anchor, to a fixpoint. The anchors are what
    /// the row created, then every discovered entity they claim. Then a discovered entity linked to any owned entity —
    /// one the row only loaded or attached included — is the row's when nothing outside the row links to it, again to a
    /// fixpoint. <see langword="null"/> when something outside does: the attribution is ambiguous (#609). The discovered
    /// list is what this row's own detections found, so the walk is bounded by the row's work.
    /// </summary>
    private List<EntityEntry>? Owned()
    {
        var owned = new Dictionary<object, EntityEntry>(ReferenceEqualityComparer.Instance);
        var anchors = new List<EntityEntry>();
        foreach (var entry in _direct)
        {
            if (owned.TryAdd(entry.Entity, entry) && _created.Contains(entry.Entity))
            {
                anchors.Add(entry);
            }
        }

        var pending = _discovered.Where(e => !owned.ContainsKey(e.Entity)).DistinctBy(e => e.Entity, ReferenceEqualityComparer.Instance).ToList();
        Claim(pending, anchors, owned);

        // What is left and linked only through an entity the row did not create: the row's, unless it is also reachable
        // from outside the row, where an earlier row may have put it.
        var suspects = Claim(pending, [.. owned.Values], owned);
        if (suspects.Count == 0)
        {
            return [.. owned.Values];
        }

        var outside = _tracker.Entries().Where(e => !owned.ContainsKey(e.Entity)).ToList();
        return suspects.Any(s => Linked(s, outside)) ? null : [.. owned.Values];
    }

    /// <summary>
    /// Moves every entry of <paramref name="pending"/> linked to one of <paramref name="sources"/> into
    /// <paramref name="owned"/>, to a fixpoint: each one claimed becomes a source too. Returns what it claimed.
    /// </summary>
    private static List<EntityEntry> Claim(List<EntityEntry> pending, List<EntityEntry> sources, Dictionary<object, EntityEntry> owned)
    {
        var claimed = new List<EntityEntry>();
        var grew = true;
        while (grew && pending.Count > 0)
        {
            grew = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (Linked(pending[i], sources))
                {
                    owned.Add(pending[i].Entity, pending[i]);
                    sources.Add(pending[i]);
                    claimed.Add(pending[i]);
                    pending.RemoveAt(i);
                    grew = true;
                }
            }
        }

        return claimed;
    }

    /// <summary>
    /// The entity's entry as the tracker holds it NOW, so one the handler detached and tracked again is not missed. A
    /// shared-type entity (a many-to-many join) cannot be looked up by its object, so its recorded entry is used.
    /// </summary>
    private static EntityEntry Current(DbContext db, EntityEntry recorded)
        => recorded.Metadata.HasSharedClrType ? recorded : db.Entry(recorded.Entity);

    /// <summary>
    /// Whether <paramref name="candidate"/> is joined to an owned entity, in either direction: a dependent the row hung
    /// on its own entity (a child, an existing row moved under it, a many-to-many join entity) or a new principal the
    /// row set on its own entity. A navigation joins them even when the handler has since changed the foreign key, and a
    /// foreign key joins them where no navigation exists (a join entity has none), so both are read.
    /// </summary>
    private static bool Linked(EntityEntry candidate, IEnumerable<EntityEntry> owned)
        => owned.Any(o => Points(candidate, o) || Points(o, candidate)
            || candidate.Metadata.GetForeignKeys().Any(fk => References(candidate, fk, o))
            || o.Metadata.GetForeignKeys().Any(fk => References(o, fk, candidate)));

    /// <summary>Whether a navigation of <paramref name="from"/> holds <paramref name="to"/>'s entity now.</summary>
    private static bool Points(EntityEntry from, EntityEntry to)
        => from.Navigations.Any(n => n.CurrentValue switch
        {
            null => false,
            System.Collections.IEnumerable many when n.Metadata.IsCollection
                => many.Cast<object?>().Any(t => ReferenceEquals(t, to.Entity)),
            var one => ReferenceEquals(one, to.Entity),
        });

    private static bool References(EntityEntry dependent, IForeignKey foreignKey, EntityEntry principal)
    {
        if (!foreignKey.PrincipalEntityType.IsAssignableFrom(principal.Metadata))
        {
            return false;
        }

        for (var i = 0; i < foreignKey.Properties.Count; i++)
        {
            var value = dependent.Property(foreignKey.Properties[i].Name).CurrentValue;
            var key = principal.Property(foreignKey.PrincipalKey.Properties[i].Name).CurrentValue;
            if (value is null || !Equals(value, key))
            {
                return false;
            }
        }

        return true;
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs e)
    {
        if (_detecting > 0)
        {
            _discovered.Add(e.Entry);
            return;
        }

        _direct.Add(e.Entry);
        if (!e.FromQuery && e.Entry.State == EntityState.Added)
        {
            _created.Add(e.Entry.Entity);   // the row's own creation: only this may claim a discovered entity
        }
    }

    private void OnDetecting(object? sender, EventArgs e) => _detecting++;

    private void OnDetected(object? sender, EventArgs e) => _detecting = Math.Max(0, _detecting - 1);
}
