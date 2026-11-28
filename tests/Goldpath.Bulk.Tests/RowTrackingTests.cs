using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// #605: the execute run leaves a failed row's work out of the chunk's save (#539) without paying, per row, for what
/// the context already tracks. Since #587 every row snapshotted the change tracker, which ran <c>DetectChanges</c> over
/// every tracked entity: the 100k bench went from 35 s to not finishing in 21 minutes. The guard counts full detections
/// per chunk, so it is deterministic where a timing would be flaky. The attribution facts hold what the snapshot gave
/// for free: an entity a detection finds is the failing row's only when it is linked to what that row owns.
/// </summary>
public sealed class RowTrackingTests : IDisposable
{
    private readonly BulkFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<GoldpathBulkBatch> ApprovedAsync(params (string Id, string Iban, string Amount, string? Note)[] rows)
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(rows));
        using var scope = _fixture.Scope();
        Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", null, CancellationToken.None)).Ok);
        return batch;
    }

    /// <summary>
    /// Runs the execute flow in ONE scope, as the bench does, after <paramref name="before"/> has loaded what the context
    /// tracks before the first row. Returns how many full detections the run's context made.
    /// </summary>
    private async Task<int> ExecuteInOneScopeAsync(Action<BulkTestContext>? before = null)
    {
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        before?.Invoke(db);
        var detections = 0;
        db.ChangeTracker.DetectingAllChanges += (_, _) => detections++;

        var adopted = await _fixture.Engine.AdoptForExecutionAsync(scope.ServiceProvider, Guid.NewGuid(), CancellationToken.None);
        var index = 0;
        foreach (var batch in adopted)
        {
            for (long start = 1; start <= batch.TotalRows; start += _fixture.Options.ChunkSize)
            {
                var end = Math.Min(start + _fixture.Options.ChunkSize, batch.TotalRows + 1);
                var chunk = BulkFixture.MakeChunk(index++, $"{batch.Id:N}|{start}:{end}");
                await _fixture.Engine.ExecuteRangeAsync(scope.ServiceProvider, chunk, batch.Id, start, end, CancellationToken.None);
            }
        }

        return detections;
    }

    private static (string, string, string, string?)[] Rows(int count, string prefix, string? note)
        => [.. Enumerable.Range(1, count).Select(i => ($"{prefix}{i}", $"TR{i}", "10", note))];

    [Theory]
    [InlineData(null)]     // every row succeeds
    [InlineData("FAIL")]   // every row fails: the failure path pays for what the row tracked, not for the tracker
    public async Task A_chunk_detects_changes_as_often_for_200_rows_as_for_2(string? note)
    {
        // The handler tracks one new entity per row, so the tracker grows with the rows, as an adopter's does.
        _fixture.Handler.Probe = (row, context) => context.Services.GetRequiredService<BulkTestContext>()
            .Add(new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId });

        await ApprovedAsync(Rows(2, "S", note));
        var small = await ExecuteInOneScopeAsync();
        await ApprovedAsync(Rows(200, "L", note));
        var large = await ExecuteInOneScopeAsync();

        Assert.Equal(small, large);
        Assert.Equal(note is null ? 202 : 0, _fixture.Query(db => db.Set<TrackedInstruction>().Count()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_the_children_it_hung_on_its_own_entity_even_after_a_detection_found_them()
    {
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"), ("E3", "TR3", "30", null));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            instruction.Lines.Add(new TrackedLine());   // hung AFTER the add: only a detection finds it
            db.ChangeTracker.DetectChanges();                                 // the handler's own detection does, mid-row
        };

        await ExecuteInOneScopeAsync();

        Assert.Equal(["E1", "E3"], _fixture.Query(db => db.Set<TrackedInstruction>().Select(i => i.Row).OrderBy(r => r).ToList()));
        Assert.Equal(2, _fixture.Query(db => db.Set<TrackedLine>().Count()));
    }

    [Fact]
    public async Task An_earlier_rows_entry_that_a_failing_rows_detection_finds_first_is_still_saved()
    {
        var ledgerId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLedger { Id = ledgerId }));
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            if (row.Note is null)
            {
                // E1 succeeds: it hangs its entry on a ledger tracked BEFORE the run, and nothing detects it yet.
                db.Set<TrackedLedger>().Local.Single().Entries.Add(new TrackedEntry { Row = row.EndToEndId });
            }
            else
            {
                db.ChangeTracker.DetectChanges();   // E2's detection is the first to find E1's entry; then E2 fails
            }
        };

        await ExecuteInOneScopeAsync(db => db.Set<TrackedLedger>().Single(l => l.Id == ledgerId));

        Assert.Equal(["E1"], _fixture.Query(db => db.Set<TrackedEntry>().Select(e => e.Row).ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_a_new_principal_it_set_on_its_own_entitys_reference()
    {
        var ledgerId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLedger { Id = ledgerId }));
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var entry = new TrackedEntry { Row = row.EndToEndId, TrackedLedgerId = ledgerId };
            db.Add(entry);
            entry.Ledger = new TrackedLedger();   // a principal reached only through the row's own entity's reference
            db.ChangeTracker.DetectChanges();
        };

        await ExecuteInOneScopeAsync();

        Assert.Equal([ledgerId], _fixture.Query(db => db.Set<TrackedLedger>().Select(l => l.Id).ToList()));
        Assert.Empty(_fixture.Query(db => db.Set<TrackedEntry>().ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_an_existing_child_it_moved_under_its_own_entity()
    {
        var lineId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLine { Id = lineId }));
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            instruction.Lines.Add(new TrackedLine { Id = lineId });   // a row that exists: a detection finds it MODIFIED
            db.ChangeTracker.DetectChanges();
        };

        await ExecuteInOneScopeAsync();

        Assert.Null(_fixture.Query(db => db.Set<TrackedLine>().Single(l => l.Id == lineId).TrackedInstructionId));
        Assert.Empty(_fixture.Query(db => db.Set<TrackedInstruction>().ToList()));
    }

    [Fact]
    public async Task A_failing_rows_new_principal_is_told_apart_from_an_earlier_rows_by_its_key()
    {
        var ledgerId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLedger { Id = ledgerId, Entries = { new TrackedEntry { Id = entryId, Row = "seed" } } }));
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        TrackedEntry? seeded = null;
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            if (row.Note is null)
            {
                // E1 succeeds: it moves an entry tracked BEFORE the run under a new ledger, undetected so far (no
                // DbSet.Local here: reading it runs a detection).
                seeded!.Ledger = new TrackedLedger();
            }
            else
            {
                var own = new TrackedEntry { Row = row.EndToEndId };
                db.Add(own);
                own.Ledger = new TrackedLedger();   // E2's own new ledger; its detection finds E1's ledger too
                db.ChangeTracker.DetectChanges();
            }
        };

        await ExecuteInOneScopeAsync(db => seeded = db.Set<TrackedEntry>().Single(e => e.Id == entryId));

        var moved = _fixture.Query(db => db.Set<TrackedEntry>().Single(e => e.Id == entryId).TrackedLedgerId);
        Assert.NotEqual(ledgerId, moved);
        Assert.Equal(2, _fixture.Query(db => db.Set<TrackedLedger>().Count()));   // the seed's and E1's, not E2's
        Assert.Equal(["seed"], _fixture.Query(db => db.Set<TrackedEntry>().Select(e => e.Row).ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_a_grandchild_reached_only_through_a_child_a_detection_found()
    {
        var lineId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLine { Id = lineId, Notes = { new TrackedNote { Id = noteId } } }));
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            var line = new TrackedLine { Id = lineId };
            line.Notes.Add(new TrackedNote { Id = noteId, TrackedLineId = lineId });
            instruction.Lines.Add(line);        // an existing line and its existing note, found by the detection below
            db.ChangeTracker.DetectChanges();
            line.Notes.Single().TrackedLineId = null;   // the note's change: saved only if the note stayed tracked
        };

        await ExecuteInOneScopeAsync();

        Assert.Equal(lineId, _fixture.Query(db => db.Set<TrackedNote>().Single(n => n.Id == noteId).TrackedLineId));
        Assert.Null(_fixture.Query(db => db.Set<TrackedLine>().Single(l => l.Id == lineId).TrackedInstructionId));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_what_it_tracked_directly_after_its_own_detection()
    {
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            db.ChangeTracker.DetectChanges();   // a detection that finds nothing, then closes
            db.Add(new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId });
        };

        await ExecuteInOneScopeAsync();

        Assert.Empty(_fixture.Query(db => db.Set<TrackedInstruction>().ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_an_entity_it_tracked_detached_and_tracked_again()
    {
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            db.Entry(instruction).State = EntityState.Detached;
            db.Add(instruction);
        };

        await ExecuteInOneScopeAsync();

        Assert.Empty(_fixture.Query(db => db.Set<TrackedInstruction>().ToList()));
    }

    [Fact]
    public void A_disposed_recorder_records_nothing_more()
    {
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        var tracking = new BulkRowTracking(db.ChangeTracker);
        tracking.BeginRow();
        tracking.Dispose();
        var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = "after" };
        db.Add(instruction);

        tracking.UntrackRow(db);

        Assert.Equal(EntityState.Added, db.Entry(instruction).State);
    }

    [Fact]
    public async Task An_earlier_rows_discovered_entity_that_a_failing_row_links_to_is_still_saved()
    {
        var ledgerId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLedger { Id = ledgerId, Entries = { new TrackedEntry { Id = entryId, Row = "seed" } } }));
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        TrackedEntry? seeded = null;
        TrackedLedger? earlier = null;
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            if (row.Note is null)
            {
                earlier = new TrackedLedger();
                seeded!.Ledger = earlier;               // E1 succeeds; its own detection finds the new ledger
                db.ChangeTracker.DetectChanges();
            }
            else
            {
                db.Add(new TrackedEntry { Row = row.EndToEndId, Ledger = earlier });   // E2 links to E1's ledger, then fails
            }
        };

        await ExecuteInOneScopeAsync(db => seeded = db.Set<TrackedEntry>().Single(e => e.Id == entryId));

        Assert.Equal(2, _fixture.Query(db => db.Set<TrackedLedger>().Count()));
        Assert.Equal(["seed"], _fixture.Query(db => db.Set<TrackedEntry>().Select(e => e.Row).ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_what_names_its_entity_only_by_foreign_key()
    {
        var ledgerId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedLedger { Id = ledgerId }));
        await ApprovedAsync(("E1", "TR1", "10", "FAIL"));
        TrackedLedger? ledger = null;
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            ledger!.Receipts.Add(new TrackedReceipt { InstructionId = instruction.Id });   // no navigation to it
            db.ChangeTracker.DetectChanges();
        };

        await ExecuteInOneScopeAsync(db => ledger = db.Set<TrackedLedger>().Single(l => l.Id == ledgerId));

        Assert.Empty(_fixture.Query(db => db.Set<TrackedReceipt>().ToList()));
        Assert.Empty(_fixture.Query(db => db.Set<TrackedInstruction>().ToList()));
    }

    [Fact]
    public async Task A_failing_row_leaves_out_the_join_row_that_links_its_entity_to_one_tracked_before_it()
    {
        var tagId = Guid.NewGuid();
        _fixture.Mutate(db => db.Add(new TrackedTag { Id = tagId }));
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            var instruction = new TrackedInstruction { Id = Guid.NewGuid(), Row = row.EndToEndId };
            db.Add(instruction);
            instruction.Tags.Add(db.Set<TrackedTag>().Local.Single());   // a join entity: no navigation, two foreign keys
            db.ChangeTracker.DetectChanges();
        };

        await ExecuteInOneScopeAsync(db => db.Set<TrackedTag>().Single(t => t.Id == tagId));

        var tagged = _fixture.Query(db => db.Set<TrackedInstruction>().Include(i => i.Tags)
            .Select(i => new { i.Row, Tags = i.Tags.Count }).ToList());
        Assert.Equal("E1", Assert.Single(tagged).Row);
        Assert.Equal(1, tagged[0].Tags);
    }
    /// <summary>
    /// #609: an entity the failing row did not CREATE is never the anchor that makes another entity the failing row's.
    /// E1 succeeds by hanging a receipt that names an existing instruction by foreign key on a ledger tracked before the
    /// run, and nothing detects it. E2 brings that instruction into the tracker (by a query, by <c>Attach</c>, or by
    /// <c>Update</c>), detects, and fails: E1's receipt must still be saved, since E1 is executed.
    /// </summary>
    [Theory]
    [InlineData("load")]
    [InlineData("attach")]
    [InlineData("update")]
    public async Task An_earlier_rows_entity_naming_one_the_failing_row_only_brought_into_the_tracker_is_still_saved(string how)
    {
        var ledgerId = Guid.NewGuid();
        var instructionId = Guid.NewGuid();
        _fixture.Mutate(db =>
        {
            db.Add(new TrackedLedger { Id = ledgerId });
            db.Add(new TrackedInstruction { Id = instructionId, Row = "seed" });
        });
        await ApprovedAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"));
        TrackedLedger? ledger = null;
        _fixture.Handler.Probe = (row, context) =>
        {
            var db = context.Services.GetRequiredService<BulkTestContext>();
            if (row.Note is null)
            {
                ledger!.Receipts.Add(new TrackedReceipt { InstructionId = instructionId });   // E1: undetected so far
                return;
            }

            _ = how switch
            {
                "load" => db.Set<TrackedInstruction>().Single(i => i.Id == instructionId),
                "attach" => db.Attach(new TrackedInstruction { Id = instructionId, Row = "seed" }).Entity,
                _ => db.Update(new TrackedInstruction { Id = instructionId, Row = "seed" }).Entity,
            };
            db.ChangeTracker.DetectChanges();   // E2's detection is the first to find E1's receipt; then E2 fails
        };

        await ExecuteInOneScopeAsync(db => ledger = db.Set<TrackedLedger>().Single(l => l.Id == ledgerId));

        var receipt = Assert.Single(_fixture.Query(db => db.Set<TrackedReceipt>().ToList()));
        Assert.Equal(instructionId, receipt.InstructionId);
        Assert.Equal(ledgerId, receipt.TrackedLedgerId);
    }
}
