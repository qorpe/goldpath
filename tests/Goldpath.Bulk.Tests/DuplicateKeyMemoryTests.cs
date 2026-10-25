using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// #300: in-file duplicate detection must not keep every key. A 10M-row device list is a legal
/// file (MaxRows is the adopter's ceiling, GP1501), and keeping each distinct key as a string
/// costs its characters on top of the table entry, so the bill grows with the KEYS' LENGTH as
/// well as their number. The bar: retained memory per extra distinct key stays at the cost of
/// a fixed-size table entry, whatever the key's length.
/// </summary>
[Collection(DuplicateKeyMemoryCollection.Name)]
public class DuplicateKeyMemoryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Bytes retained per extra distinct key. A table entry of a 64-bit hash and a row number
    /// costs about 28 bytes at the table's own load (an entry plus its bucket), and a growth step
    /// can leave up to twice that allocated; a 17-character key held as a string costs about
    /// 56 bytes more on top of its entry.
    /// </summary>
    private const double BytesPerKeyBudget = 64;

    [Fact]
    public async Task Retained_memory_per_distinct_key_does_not_include_the_key_itself()
    {
        var oneMillion = await RetainedAsync(1_000_000);
        var twoMillion = await RetainedAsync(2_000_000);
        var perKey = (twoMillion - oneMillion) / 1_000_000.0;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"retained: 1M keys {oneMillion / 1_048_576.0:F1} MiB, 2M keys {twoMillion / 1_048_576.0:F1} MiB, {perKey:F1} bytes per extra key"));
        Assert.True(perKey <= BytesPerKeyBudget, string.Create(CultureInfo.InvariantCulture,
            $"each extra distinct key retained {perKey:F1} bytes (budget {BytesPerKeyBudget}) — the keys themselves are being kept"));
    }

    /// <summary>Retained bytes after recording <paramref name="count"/> distinct 17-character keys.</summary>
    private static async Task<long> RetainedAsync(int count)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var duplicates = new GoldpathBulkDuplicateKeys("test");
        for (var row = 1; row <= count; row++)
        {
            Assert.Null(await duplicates.FirstRowOfAsync(Key(row), row, NeverAsked));
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(duplicates);
        return after - before;
    }

    private static string Key(int row) => string.Create(CultureInfo.InvariantCulture, $"device-{row:D10}");

    private static ValueTask<string?> NeverAsked(int row)
        => throw new Xunit.Sdk.XunitException($"distinct keys never need an earlier row's key (asked for row {row})");
}

/// <summary>Runs the memory measurement alone: a parallel test's allocations would land in its numbers.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DuplicateKeyMemoryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "bulk-duplicate-key-memory";
}
