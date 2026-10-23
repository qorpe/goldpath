namespace Goldpath;

/// <summary>
/// In-file duplicate detection for one validate pass (bulk RFC D2): the FIRST row that carried
/// a key wins, and every later row with the same key is reported as its duplicate.
/// <para>
/// It keeps a 64-bit hash of each key and the row that carried it first, never the key itself
/// (#300): the cost per distinct key is one fixed-size table entry, whatever the key's length.
/// A hash hit is confirmed EXACTLY by re-deriving the first row's key, so two different keys
/// that share a hash are never reported as duplicates; a key that collides is then kept in
/// full, which happens about once in tens of millions of keys.
/// </para>
/// </summary>
internal sealed class GoldpathBulkDuplicateKeys
{
    private readonly Dictionary<long, int> _firstRowByHash = [];
    private readonly Dictionary<string, int> _collided = new(StringComparer.Ordinal);
    private readonly Func<string, long> _hash;

    /// <summary>The production hash; <paramref name="hash"/> lets a test force a collision.</summary>
    public GoldpathBulkDuplicateKeys(Func<string, long>? hash = null) => _hash = hash ?? Fnv1a64;

    /// <summary>
    /// Records <paramref name="key"/> for <paramref name="rowNumber"/>, or answers the row that
    /// carried it first. <paramref name="keyOfRow"/> re-derives the key of an earlier row; it is
    /// asked only when the hash of <paramref name="key"/> is already taken.
    /// </summary>
    public async ValueTask<int?> FirstRowOfAsync(string key, int rowNumber, Func<int, ValueTask<string?>> keyOfRow)
    {
        var hash = _hash(key);
        if (!_firstRowByHash.TryGetValue(hash, out var first))
        {
            _firstRowByHash.Add(hash, rowNumber);
            return null;
        }

        if (string.Equals(await keyOfRow(first), key, StringComparison.Ordinal))
        {
            return first;
        }

        // A different key with a taken hash: remembered exactly, so its own repeats are found.
        if (_collided.TryGetValue(key, out var firstOfCollided))
        {
            return firstOfCollided;
        }

        _collided.Add(key, rowNumber);
        return null;
    }

    /// <summary>FNV-1a over the key's UTF-16 code units, both bytes of each: deterministic and allocation-free.</summary>
    internal static long Fnv1a64(string key)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var unit in key)
        {
            hash = (hash ^ (byte)unit) * prime;
            hash = (hash ^ (byte)(unit >> 8)) * prime;
        }

        return unchecked((long)hash);
    }
}
