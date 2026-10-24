using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// #300: duplicate detection keeps hashes, not keys, and must still answer exactly what the
/// string table answered (bulk RFC D2) — the same report text, the same first row, the same
/// counts — including when the first row is already written, when two different keys share a
/// hash, and when a key that collided repeats.
/// </summary>
public class DuplicateKeyTests
{
    private static ValueTask<string?> Keys(IReadOnlyDictionary<int, string> byRow, int row) => ValueTask.FromResult<string?>(byRow[row]);

    [Fact]
    public async Task A_repeated_key_answers_the_row_that_carried_it_first()
    {
        var duplicates = new GoldpathBulkDuplicateKeys("test");
        var byRow = new Dictionary<int, string> { [1] = "E1", [2] = "E2" };

        Assert.Null(await duplicates.FirstRowOfAsync("E1", 1, row => Keys(byRow, row)));
        Assert.Null(await duplicates.FirstRowOfAsync("E2", 2, row => Keys(byRow, row)));
        Assert.Equal(1, await duplicates.FirstRowOfAsync("E1", 3, row => Keys(byRow, row)));
        Assert.Equal(1, await duplicates.FirstRowOfAsync("E1", 4, row => Keys(byRow, row)));   // still the FIRST, never the latest
        Assert.Equal(2, await duplicates.FirstRowOfAsync("E2", 5, row => Keys(byRow, row)));
    }

    [Fact]
    public async Task Keys_that_differ_only_in_case_or_culture_are_different_keys()
    {
        // Ordinal, as the string table was: no culture folds İ/i/ı, and "e1" is not "E1". Every
        // key shares one hash, so each of them reaches the exact comparison.
        var duplicates = new GoldpathBulkDuplicateKeys("test", _ => 1);
        var byRow = new Dictionary<int, string> { [1] = "E1", [2] = "e1", [3] = "İ", [4] = "i", [5] = "ı" };
        foreach (var (row, key) in byRow)
        {
            Assert.Null(await duplicates.FirstRowOfAsync(key, row, r => Keys(byRow, r)));
        }
    }

    [Fact]
    public async Task Two_different_keys_that_share_a_hash_are_not_duplicates_and_each_one_repeats_on_its_own_first_row()
    {
        var asked = new List<int>();
        var duplicates = new GoldpathBulkDuplicateKeys("test", _ => 42);
        var byRow = new Dictionary<int, string> { [1] = "A", [2] = "B", [3] = "C" };
        ValueTask<string?> KeyOf(int row)
        {
            asked.Add(row);
            return Keys(byRow, row);
        }

        Assert.Null(await duplicates.FirstRowOfAsync("A", 1, KeyOf));
        Assert.Empty(asked);   // a free hash never asks for an earlier row
        Assert.Null(await duplicates.FirstRowOfAsync("B", 2, KeyOf));   // collides with A: confirmed different
        Assert.Null(await duplicates.FirstRowOfAsync("C", 3, KeyOf));
        Assert.Equal(2, await duplicates.FirstRowOfAsync("B", 4, KeyOf));
        Assert.Equal(3, await duplicates.FirstRowOfAsync("C", 5, KeyOf));
        Assert.Equal(1, await duplicates.FirstRowOfAsync("A", 6, KeyOf));
        Assert.Equal([1, 1, 1, 1, 1], asked);   // the confirmation always reads the hash's FIRST row
    }

    [Theory]
    [InlineData("", -3750763034362895579L)]
    [InlineData("a", 620337896427418084L)]
    [InlineData("device-0000000001", -7304910308761849455L)]
    [InlineData("İı", 4944459784769174304L)]
    public void The_hash_is_FNV_1a_over_both_bytes_of_every_UTF16_unit(string key, long expected)
        => Assert.Equal(expected, GoldpathBulkDuplicateKeys.Fnv1a64(key));

    [Fact]
    public void The_hash_tells_apart_keys_that_differ_only_in_order_or_in_a_high_byte()
    {
        Assert.NotEqual(GoldpathBulkDuplicateKeys.Fnv1a64("ab"), GoldpathBulkDuplicateKeys.Fnv1a64("ba"));
        Assert.NotEqual(GoldpathBulkDuplicateKeys.Fnv1a64("İ"), GoldpathBulkDuplicateKeys.Fnv1a64("0"));   // İ vs 0: same low byte
    }

    /// <summary>
    /// Through the engine, with every key forced onto one hash: each confirmation must find the
    /// first row whether it is still in the flush buffer or already written (InsertBatchSize 2),
    /// and the report must read exactly as the string table's did.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_report_is_unchanged_across_flushes_and_with_every_key_colliding(bool collideEverything)
    {
        using var fixture = new BulkFixture();
        fixture.Options.InsertBatchSize = 2;
        if (collideEverything)
        {
            fixture.Options.DuplicateKeyHash = _ => 7;
        }

        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(
            ("E1", "TR1", "10", null),    // 1
            ("E2", "TR2", "20", null),    // 2  (rows 1-2 flush here)
            ("E3", "TR3", "-1", null),    // 3  invalid: records no key
            ("E1", "TR4", "40", null),    // 4  duplicate of 1, already written
            ("E3", "TR5", "50", null),    // 5  valid: the invalid row 3 never claimed E3
            ("E5", "TR6", "60", null),    // 6
            ("E5", "TR7", "70", null),    // 7  duplicate of 6, still in the buffer
            ("E3", "TR8", "80", null),    // 8  duplicate of 5
            ("E2", "TR9", "90", null)));  // 9  duplicate of 2

        Assert.Equal(GoldpathBulkBatchState.Validated, batch.State);
        Assert.Equal(9, batch.TotalRows);
        Assert.Equal(4, batch.ValidRows);
        Assert.Equal(5, batch.InvalidRows);
        Assert.Equal([1, 2, 5, 6], fixture.Query(db => db.Set<GoldpathBulkRow>().OrderBy(r => r.RowNumber).Select(r => r.RowNumber).ToList()));
        var report = fixture.Query(db => db.Set<GoldpathBulkRowError>().AsNoTracking().OrderBy(e => e.RowNumber)
            .Select(e => new { e.RowNumber, e.Field, e.Message }).ToList());
        Assert.Equal(
            [
                (3, "Amount", "amount must be positive"),
                (4, "(row key)", "duplicate of row 1 within the file"),
                (7, "(row key)", "duplicate of row 6 within the file"),
                (8, "(row key)", "duplicate of row 5 within the file"),
                (9, "(row key)", "duplicate of row 2 within the file"),
            ],
            report.Select(e => (e.RowNumber, e.Field, e.Message)).ToList());
    }

    [Fact]
    public void A_definition_without_a_row_key_derives_no_key_from_a_payload()
    {
        var options = new GoldpathBulkOptions();
        options.AddBatch<PaymentRow>("keyless", b => b.MaxRows(10));
        options.AddBatch<PaymentRow>("keyed", b => b.MaxRows(10).RowKey(r => r.EndToEndId));
        const string payload = """{"EndToEndId":"E9","Iban":"TR9","Amount":9,"Note":null}""";

        Assert.Null(options.Definition("keyless").KeyOfPayload(payload));
        Assert.Equal("E9", options.Definition("keyed").KeyOfPayload(payload));
        Assert.Throws<InvalidOperationException>(() => options.Definition("keyed").KeyOfPayload("null"));
    }
}
