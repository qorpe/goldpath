namespace Goldpath;

/// <summary>
/// In-file duplicate detection for one validate pass (bulk RFC D2): the FIRST row that carried
/// a key wins, and every later row with the same key is reported as its duplicate.
/// <para>
/// It keeps a 64-bit hash of each key and the row that carried it first, never the key itself
/// (#300): the cost per distinct key is one fixed-size table entry, whatever the key's length.
/// A hash hit is confirmed EXACTLY by re-deriving the first row's key, so two different keys
/// that share a hash are never reported as duplicates; a key that collides is then kept in
/// full. That practically never happens at the file sizes MaxRows allows: the exact path
/// exists for correctness, not frequency.
/// </para>
/// <para>
/// The confirmation relies on the key surviving its row's JSON round trip. A definition whose
/// key does not (a private setter, a <c>[JsonIgnore]</c> property) is switched to
/// <see cref="KeepKeysInFull"/> by the engine before its first key is recorded. A hit whose
/// re-derived key no longer hashes to the hit's own hash — a key lost by SOME rows only, which
/// the first-row check cannot see — throws <see cref="GoldpathBulkKeyNotReproducibleException"/>
/// rather than being read as a collision, and the engine takes the pass again keeping keys in
/// full, so a genuine duplicate is never staged as valid.
/// </para>
/// </summary>
internal sealed class GoldpathBulkDuplicateKeys
{
    private readonly Dictionary<long, int> _firstRowByHash = [];
    private readonly Dictionary<string, int> _collided = new(StringComparer.Ordinal);
    private readonly string _definition;
    private readonly Func<string, long> _hash;
    private bool _keysInFull;

    /// <summary>
    /// The production hash for <paramref name="definition"/> (named in a refusal);
    /// <paramref name="hash"/> lets a test force a collision.
    /// </summary>
    public GoldpathBulkDuplicateKeys(string definition, Func<string, long>? hash = null)
    {
        _definition = definition;
        _hash = hash ?? Fnv1a64;
    }

    /// <summary>
    /// Keeps every key in full for this pass, as the string table did: the definition's key
    /// cannot be re-derived from its staged payload, so a hash hit could not be confirmed.
    /// Called before any key is recorded.
    /// </summary>
    public void KeepKeysInFull() => _keysInFull = true;

    /// <summary>
    /// Records <paramref name="key"/> for <paramref name="rowNumber"/>, or answers the row that
    /// carried it first. <paramref name="keyOfRow"/> re-derives the key of an earlier row; it is
    /// asked only when the hash of <paramref name="key"/> is already taken.
    /// </summary>
    public async ValueTask<int?> FirstRowOfAsync(string key, int rowNumber, Func<int, ValueTask<string?>> keyOfRow)
    {
        if (_keysInFull)
        {
            return RememberInFull(key, rowNumber);
        }

        var hash = _hash(key);
        if (!_firstRowByHash.TryGetValue(hash, out var first))
        {
            _firstRowByHash.Add(hash, rowNumber);
            return null;
        }

        var keyOfFirst = await keyOfRow(first);
        if (string.Equals(keyOfFirst, key, StringComparison.Ordinal))
        {
            return first;
        }

        // The first row's key hashed to this hash when it was recorded. If what its payload gives
        // back does not, the payload lost the key, and reading this as a collision would stage a
        // genuine duplicate as valid.
        if (keyOfFirst is null || _hash(keyOfFirst) != hash)
        {
            throw new GoldpathBulkKeyNotReproducibleException(
                $"The row key of bulk definition '{_definition}' is not reproducible from its staged payload (row {first}); "
                + "the key must come from public, serialized properties of the row.");
        }

        // A different key with a taken hash: remembered exactly, so its own repeats are found.
        return RememberInFull(key, rowNumber);
    }

    private int? RememberInFull(string key, int rowNumber)
    {
        if (_collided.TryGetValue(key, out var first))
        {
            return first;
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

/// <summary>
/// A hash hit whose first row's payload no longer gives its key back: the pass cannot confirm hits
/// on hashes and is taken again with <see cref="GoldpathBulkDuplicateKeys.KeepKeysInFull"/>.
/// </summary>
internal sealed class GoldpathBulkKeyNotReproducibleException(string message) : InvalidOperationException(message);
