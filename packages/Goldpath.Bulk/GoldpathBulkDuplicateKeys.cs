namespace Goldpath;

/// <summary>
/// In-file duplicate detection for one validate pass (bulk RFC D2): the FIRST row that carried
/// a key wins, and every later row with the same key is reported as its duplicate.
/// </summary>
internal sealed class GoldpathBulkDuplicateKeys
{
    private readonly Dictionary<string, int> _firstRowByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Records <paramref name="key"/> for <paramref name="rowNumber"/>, or answers the row that
    /// carried it first. <paramref name="keyOfRow"/> re-derives the key of an earlier row.
    /// </summary>
    public ValueTask<int?> FirstRowOfAsync(string key, int rowNumber, Func<int, ValueTask<string?>> keyOfRow)
    {
        if (_firstRowByKey.TryGetValue(key, out var first))
        {
            return ValueTask.FromResult<int?>(first);
        }

        _firstRowByKey[key] = rowNumber;
        return ValueTask.FromResult<int?>(null);
    }
}
