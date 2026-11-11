using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Medallion.Threading;

namespace Goldpath;

/// <summary>
/// Keeps every lock name inside what <c>DBMS_LOCK</c> stores. <c>SYS.DBMS_LOCK_ALLOCATED.NAME</c> holds
/// 128 BYTES, and the library decides whether to hash a name by its CHARACTER count, so a name of at
/// most 128 characters whose UTF-8 is longer (Turkish, or a long tenant prefix on a non-ASCII job name)
/// reached Oracle unchanged and threw ORA-12899 from <c>TryAcquireLockAsync</c> instead of returning a
/// handle — the contract the Redis and Postgres providers hold for any name.
/// </summary>
/// <remarks>
/// A name that is pure ASCII and fits in 128 bytes passes unchanged, so held locks stay readable in
/// <c>DBMS_LOCK_ALLOCATED</c>. Any other name becomes its leading printable ASCII (at most 40 characters, for a
/// reader), <c>#</c>, and the lower-case hex SHA-256 of its UTF-8 — at most 105 ASCII bytes, deterministic
/// on every host and in every culture. A name that already has that shape is hashed too, so an ASCII name
/// can never be the stand-in for a different one.
/// </remarks>
internal sealed class OracleLockNames(IDistributedLockProvider inner) : IDistributedLockProvider
{
    /// <summary>What <c>DBMS_LOCK_ALLOCATED.NAME</c> holds, in bytes.</summary>
    internal const int MaxNameBytes = 128;

    private const int ReadablePrefixLength = 40;

    private const int DigestLength = 64;

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <inheritdoc />
    public IDistributedLock CreateLock(string name) => inner.CreateLock(Safe(name));

    /// <summary>The name Oracle is given for <paramref name="name"/>.</summary>
    internal static string Safe(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length <= MaxNameBytes && Ascii.IsValid(name) && !LooksHashed(name))
        {
            return name;
        }

        // The readable part: the name's leading printable ASCII, at most ReadablePrefixLength characters.
        var head = name.AsSpan(0, Math.Min(name.Length, ReadablePrefixLength));
        var stop = head.IndexOfAnyExceptInRange(' ', '~');
        if (stop >= 0)
        {
            head = head[..stop];
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return string.Concat(head, "#", digest);
    }

    // "<anything>#<64 lower-case hex>" is the shape Safe produces; such a name is never passed through.
    private static bool LooksHashed(string name)
        => name.Length > DigestLength
           && name[^(DigestLength + 1)] == '#'
           && name.AsSpan(name.Length - DigestLength).IndexOfAnyExcept(LowerHex) < 0;
}
