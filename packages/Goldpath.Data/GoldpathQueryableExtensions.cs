using System.Linq.Expressions;
using System.Reflection;

namespace Goldpath;

/// <summary>
/// Query helpers that keep one LINQ shape correct on every supported provider (RFC goldpath-oracle §2.5).
/// </summary>
public static class GoldpathQueryableExtensions
{
    /// <summary>
    /// The most elements one <c>IN (…)</c> list may carry on Oracle; a longer list is refused with ORA-01795.
    /// </summary>
    public const int OracleInListLimit = 1_000;

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    /// <summary>
    /// Filters <paramref name="source"/> to the rows whose <paramref name="member"/> is one of <paramref name="values"/> —
    /// the query <c>source.Where(x =&gt; values.Contains(x.Member))</c> — in a form every provider accepts.
    /// <para>
    /// On Oracle (a <paramref name="providerName"/> containing <c>Oracle</c>) a list longer than
    /// <see cref="OracleInListLimit"/> is split into chunks of at most that many, joined with <c>OR</c>, because Oracle
    /// refuses a longer <c>IN</c> list (ORA-01795). On every other provider, and for a list within the limit, it is
    /// exactly the plain <c>Contains</c> filter. Each chunk is a query parameter, never SQL text.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The queried entity.</typeparam>
    /// <typeparam name="TValue">The type of the member and of the values.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="member">The member compared, for example <c>i =&gt; i.Seq</c>.</param>
    /// <param name="values">The local values. They are copied when the filter is built, so a later change to the list is not seen.</param>
    /// <param name="providerName">The context's <c>Database.ProviderName</c>; <see langword="null"/> is treated as a provider without the limit.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<T> ChunkedContains<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> member,
        IReadOnlyCollection<TValue> values,
        string? providerName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(values);

        var chunkSize = IsOracle(providerName) ? OracleInListLimit : int.MaxValue;
        var contains = EnumerableContains.MakeGenericMethod(typeof(TValue));
        Expression? predicate = null;
        foreach (var chunk in Chunks(values, chunkSize))
        {
            // A member read off a closure object, as the compiler writes a captured local: EF turns it into a
            // parameter. A constant array would be inlined into the SQL text and the query cache, one entry per list.
            var list = Expression.Property(Expression.Constant(new Captured<TValue>(chunk)), nameof(Captured<TValue>.Values));
            var test = Expression.Call(contains, list, member.Body);
            predicate = predicate is null ? test : Expression.OrElse(predicate, test);
        }

        return source.Where(Expression.Lambda<Func<T, bool>>(predicate!, member.Parameters));
    }

    private static bool IsOracle(string? providerName)
        => providerName is not null && providerName.Contains("Oracle", StringComparison.Ordinal);

    private static IEnumerable<TValue[]> Chunks<TValue>(IReadOnlyCollection<TValue> values, int size)
    {
        if (values.Count <= size)
        {
            // Within the limit (an empty list included): one list, the plain Contains.
            yield return [.. values];
            yield break;
        }

        foreach (var chunk in values.Chunk(size))
        {
            yield return chunk;
        }
    }

    /// <summary>The closure a chunk is read from.</summary>
    private sealed class Captured<TValue>(TValue[] values)
    {
        public TValue[] Values { get; } = values;
    }
}
