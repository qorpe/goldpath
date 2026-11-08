using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// <see cref="GoldpathQueryableExtensions.ChunkedContains{T, TValue}"/> (RFC goldpath-oracle §2.5, ORA-5 #408): on
/// Oracle a list above 1,000 becomes OR-joined chunks of at most 1,000; elsewhere it is the plain <c>Contains</c>.
/// The real Oracle proof is the integration suite's 5,000-item release; these facts pin the shape and the meaning.
/// </summary>
public sealed partial class ChunkedContainsTests : IDisposable
{
    private const string Oracle = "Oracle.EntityFrameworkCore";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    private sealed class Item
    {
        public long Seq { get; set; }
        public string? Code { get; set; }
    }

    private sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Item>().HasKey(i => i.Seq);
    }

    private readonly SqliteConnection _connection;
    private readonly TestDb _db;

    public ChunkedContainsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TestDb(new DbContextOptionsBuilder<TestDb>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Items.AddRange(Enumerable.Range(1, 3_000).Select(i => new Item { Seq = i, Code = i % 7 == 0 ? null : $"c{i}" }));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static long[] Seqs(int count) => [.. Enumerable.Range(1, count).Select(i => (long)(i * 2))];

    /// <summary>The lists the predicate reads, in order: one per <c>Contains</c>.</summary>
    private static List<long[]> ChunksOf(IQueryable<Item> query)
    {
        var where = Assert.IsAssignableFrom<MethodCallExpression>(query.Expression);
        Assert.Equal(nameof(Queryable.Where), where.Method.Name);
        var lambda = (LambdaExpression)((UnaryExpression)where.Arguments[1]).Operand;
        var chunks = new List<long[]>();
        Collect(lambda.Body, chunks);
        return chunks;
    }

    private static void Collect(Expression expression, List<long[]> chunks)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                Collect(or.Left, chunks);
                Collect(or.Right, chunks);
                break;
            case MethodCallExpression { Method.Name: nameof(Enumerable.Contains) } call:
                chunks.Add((long[])Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke()!);
                break;
            default:
                throw new InvalidOperationException($"unexpected node {expression.NodeType}: {expression}");
        }
    }

    [Theory]
    [InlineData(0, new int[] { 0 })]
    [InlineData(1, new[] { 1 })]
    [InlineData(1_000, new[] { 1_000 })]
    [InlineData(1_001, new[] { 1_000, 1 })]
    [InlineData(2_500, new[] { 1_000, 1_000, 500 })]
    public void On_Oracle_a_list_is_split_into_chunks_of_at_most_1000(int count, int[] sizes)
    {
        var values = Seqs(count);
        var chunks = ChunksOf(_db.Items.ChunkedContains(i => i.Seq, values, Oracle));

        Assert.Equal(sizes, chunks.Select(c => c.Length));
        Assert.Equal(values, chunks.SelectMany(c => c));   // every value, once, in order
    }

    [Theory]
    [InlineData(Sqlite)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData(null)]
    public void Elsewhere_a_list_of_any_length_is_one_contains(string? provider)
    {
        var chunks = ChunksOf(_db.Items.ChunkedContains(i => i.Seq, Seqs(2_500), provider));

        Assert.Equal([2_500], chunks.Select(c => c.Length));
    }

    [Fact]
    public void Off_Oracle_the_sql_is_the_plain_contains_filter()
    {
        var values = Seqs(2_500);
        var plain = _db.Items.Where(i => values.Contains(i.Seq)).ToQueryString();
        var chunked = _db.Items.ChunkedContains(i => i.Seq, values, Sqlite).ToQueryString();

        Assert.Equal(Normalise(plain), Normalise(chunked));
        Assert.DoesNotContain(" OR ", chunked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(1_999)]
    [InlineData(2_500)]
    [InlineData(5_000)]
    public void On_Oracle_no_in_list_the_sql_carries_passes_1000_even_after_parameter_padding(int count)
    {
        // EF pads a parameterised list up to a bucket size; the padding must never carry a chunk past the limit.
        var sql = _db.Items.ChunkedContains(i => i.Seq, Seqs(count), Oracle).ToQueryString();
        var statement = string.Join('\n', sql.Split('\n').Where(line => !line.StartsWith(".param", StringComparison.Ordinal)));
        var where = statement[(statement.IndexOf("WHERE", StringComparison.Ordinal) + 5)..];
        var lists = where.Split(" OR ").Select(arm => ParameterName().Matches(arm).Count).ToList();   // one arm per chunk: "= @p" or "IN (@p, …)"

        Assert.Equal((count + 999) / 1_000, lists.Count);
        Assert.All(lists, size => Assert.InRange(size, 1, GoldpathQueryableExtensions.OracleInListLimit));
        Assert.DoesNotContain((count * 2).ToString(System.Globalization.CultureInfo.InvariantCulture), statement, StringComparison.Ordinal);   // values are parameters, never SQL text
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(2_500)]
    public void Chunked_or_not_the_rows_are_those_contains_selects(int count)
    {
        var values = Seqs(count);
        var expected = _db.Items.Where(i => values.Contains(i.Seq)).Select(i => i.Seq).OrderBy(s => s).ToList();

        Assert.Equal(expected, _db.Items.ChunkedContains(i => i.Seq, values, Oracle).Select(i => i.Seq).OrderBy(s => s).ToList());
        Assert.Equal(expected, _db.Items.ChunkedContains(i => i.Seq, values, Sqlite).Select(i => i.Seq).OrderBy(s => s).ToList());
        Assert.Equal(expected, values.Where(v => v <= 3_000).ToList());
    }

    [Fact]
    public void It_composes_with_the_rest_of_the_filter()
    {
        var values = Seqs(1_500);   // 2..3000, even
        var rows = _db.Items.Where(i => i.Seq > 2_990).ChunkedContains(i => i.Seq, values, Oracle).Select(i => i.Seq).OrderBy(s => s).ToList();

        Assert.Equal([2_992L, 2_994, 2_996, 2_998, 3_000], rows);
    }

    [Fact]
    public void A_null_element_means_what_it_means_to_contains()
    {
        var codes = new List<string?> { "c1", null, "c14" };   // c14 does not exist: 14 % 7 == 0 stores NULL
        var plain = _db.Items.Where(i => codes.Contains(i.Code)).Select(i => i.Seq).OrderBy(s => s).ToList();

        Assert.Equal(plain, _db.Items.ChunkedContains(i => i.Code, codes, Oracle).Select(i => i.Seq).OrderBy(s => s).ToList());
        Assert.Equal(plain, _db.Items.ChunkedContains(i => i.Code, codes, Sqlite).Select(i => i.Seq).OrderBy(s => s).ToList());
    }

    [Fact]
    public void The_values_are_copied_when_the_filter_is_built()
    {
        var values = new List<long> { 1, 2 };
        var query = _db.Items.ChunkedContains(i => i.Seq, values, Oracle);
        values.Add(3);

        Assert.Equal([1L, 2], query.Select(i => i.Seq).OrderBy(s => s).ToList());
    }

    [Fact]
    public void Null_arguments_are_refused_by_name()
    {
        IQueryable<Item> none = null!;
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() => none.ChunkedContains(i => i.Seq, [1L], Oracle)).ParamName);
        Assert.Equal("member", Assert.Throws<ArgumentNullException>(() => _db.Items.ChunkedContains<Item, long>(null!, [1L], Oracle)).ParamName);
        Assert.Equal("values", Assert.Throws<ArgumentNullException>(() => _db.Items.ChunkedContains(i => i.Seq, null!, Oracle)).ParamName);
    }

    [Fact]
    public void Only_a_provider_named_Oracle_is_chunked()
    {
        // The match is ordinal on the provider's assembly name, Oracle.EntityFrameworkCore.
        Assert.Equal(2, ChunksOf(_db.Items.ChunkedContains(i => i.Seq, Seqs(1_001), "Oracle")).Count);
        Assert.Single(ChunksOf(_db.Items.ChunkedContains(i => i.Seq, Seqs(1_001), "oracle")));
        Assert.Single(ChunksOf(_db.Items.ChunkedContains(i => i.Seq, Seqs(1_001), "")));
    }

    /// <summary>The two queries differ only in what EF names their parameter.</summary>
    private static string Normalise(string sql) => ParameterName().Replace(sql, "@p");

    [GeneratedRegex(@"@\w+")]
    private static partial Regex ParameterName();

}
