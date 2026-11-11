using System.Globalization;
using System.Text;
using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The names <c>AddGoldpathOracleLocking</c> hands to <c>DBMS_LOCK</c>, read back through the lock's own
/// <see cref="IDistributedLock.Name"/> on the composed provider — no database is opened to create a lock, so
/// these run without the Oracle container. <c>DBMS_LOCK_ALLOCATED.NAME</c> holds 128 BYTES (#407 verifier
/// finding); <see cref="LockContractOracleTests"/> proves the same names on a real server.
/// </summary>
public sealed class OracleLockNameTests
{
    private static IDistributedLockProvider Provider()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:database"] = "Data Source=localhost:1521/FREEPDB1;User Id=unused;Password=unused";
        builder.AddGoldpathOracleLocking();
        return builder.Build().Services.GetRequiredService<IDistributedLockProvider>();
    }

    private static string OracleName(string name) => Provider().CreateLock(name).Name;

    private static void AssertStorable(string stored)
    {
        Assert.True(Encoding.UTF8.GetByteCount(stored) <= 128, $"{stored} is {Encoding.UTF8.GetByteCount(stored)} bytes");
        Assert.All(stored, c => Assert.True(c <= 0x7F, $"non-ASCII '{c}' in {stored}"));
    }

    [Theory]
    [InlineData("goldpath:global:lock:nightly-report")]
    [InlineData("goldpath:tenant:acme:lock:nightly-report")]
    public void An_ascii_name_that_fits_is_passed_unchanged(string name)
        => Assert.Equal(name, OracleName(name));

    [Fact]
    public void An_ascii_name_of_exactly_128_bytes_is_passed_unchanged_and_129_is_hashed()
    {
        var exact = new string('a', 128);
        Assert.Equal(exact, OracleName(exact));

        var over = OracleName(new string('a', 129));
        Assert.NotEqual(new string('a', 129), over);
        AssertStorable(over);
    }

    [Theory]
    [InlineData(60)]    // 87 characters, 148 bytes: the verifier's P3
    [InlineData(61)]
    [InlineData(100)]   // 127 characters, 228 bytes
    [InlineData(400)]   // over 128 characters as well
    public void A_multi_byte_name_over_128_bytes_becomes_a_storable_ascii_name(int dottedCapitals)
    {
        var name = GoldpathLockNames.Global("işlem-" + new string('İ', dottedCapitals));
        var stored = OracleName(name);

        AssertStorable(stored);
        Assert.StartsWith("goldpath:global:lock:i", stored, StringComparison.Ordinal);   // a reader sees whose lock it is
        Assert.Equal(stored, OracleName(name));                                          // deterministic
        Assert.NotEqual(stored, OracleName(name[..^1] + "I"));                           // the last character still counts
    }

    [Fact]
    public void A_short_non_ascii_name_is_hashed_too_because_it_is_not_ascii()
    {
        var stored = OracleName("ödeme");
        AssertStorable(stored);
        Assert.Matches("^#[0-9a-f]{64}$", stored);
    }

    [Fact]
    public void An_ascii_name_shaped_like_a_stand_in_is_hashed_so_it_cannot_alias_another_name()
    {
        var standIn = OracleName("ödeme");
        Assert.NotEqual(standIn, OracleName(standIn));
        AssertStorable(OracleName(standIn));
    }

    private static string Digest(string name)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    [Fact]
    public void A_hashed_name_is_the_first_forty_printable_characters_a_hash_sign_and_the_digest()
    {
        var name = new string('a', 129);
        Assert.Equal(new string('a', 40) + "#" + Digest(name), OracleName(name));
    }

    [Theory]
    [InlineData(" ~x", " ~x")]            // space and tilde are the ends of the printable range: kept
    [InlineData("ab\u007Fcd", "ab")]      // DEL is ASCII but not printable: the readable part stops there
    [InlineData("ab\u001Fcd", "ab")]      // so is a control character
    [InlineData("abİcd", "ab")]            // and the first non-ASCII character
    public void The_readable_part_stops_at_the_first_unprintable_character(string start, string readable)
    {
        start = System.Text.RegularExpressions.Regex.Unescape(start);
        var name = start + new string('z', 200);
        var expectedHead = readable == start ? start + new string('z', 40 - start.Length) : readable;
        Assert.Equal(expectedHead + "#" + Digest(name), OracleName(name));
    }

    [Theory]
    [InlineData("ab\u007Fcd")]       // DEL and a control character are ASCII: a short name with them fits as it is
    [InlineData("ab\u0001cd")]
    public void A_short_ascii_name_with_unprintable_characters_is_passed_unchanged(string name)
    {
        name = System.Text.RegularExpressions.Regex.Unescape(name);
        Assert.Equal(name, OracleName(name));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]    // 64 hex, no '#'
    [InlineData("#123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]    // '#' + 63 hex: 64 characters
    [InlineData("#0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]   // upper-case hex
    [InlineData("#g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]   // a non-hex first digit
    [InlineData("x#0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde#f")]  // '#' not 65th from the end
    public void An_ascii_name_not_shaped_like_a_stand_in_is_passed_unchanged(string name)
        => Assert.Equal(name, OracleName(name));

    [Theory]
    [InlineData("#0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]   // exactly the shape, no prefix
    [InlineData("job#0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void An_ascii_name_exactly_shaped_like_a_stand_in_is_hashed(string name)
        => Assert.Equal(name[..40] + "#" + Digest(name), OracleName(name));   // its readable part may hold a '#': only the 65th-from-last counts

    [Fact]
    public void A_null_name_is_refused()
        => Assert.Throws<ArgumentNullException>(() => Provider().CreateLock(null!));

    [Fact]
    public void The_stored_name_does_not_depend_on_the_culture()
    {
        var name = GoldpathLockNames.Global("İşlem-ı-" + new string('ğ', 80));
        var invariant = OracleName(name);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(invariant, OracleName(name));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
