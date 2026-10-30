using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Oracle.EntityFrameworkCore.Infrastructure;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The Data module on Oracle (G7, slice O-2, #405; RFC goldpath-oracle §2.2, §2.4, §7). Every fact runs against
/// the composed model of <see cref="OracleComposedDb"/> in the 19c SQL form the RFC ships. The facts that need
/// a database each get a schema of their own on the lane's one container, so none depends on another's order.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class OracleDataTests(OracleFixture oracle, ITestOutputHelper output)
{
    /// <summary>Oracle's identifier limit from 12.2 on, and so at the 19c floor (RFC §2.2).</summary>
    private const int MaxIdentifierBytes = 128;

    /// <summary>
    /// The non-nullable string columns where Goldpath may legitimately write <c>""</c>, each naming its open issue.
    /// Oracle stores <c>''</c> as NULL, so every one of them is a defect on Oracle until the issue lands.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MayWriteEmpty = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GoldpathCampaignCeilings.Name"] = "#384 — the global ceiling row's name is empty",
        ["GoldpathCampaigns.Name"] = "#476 — campaign create takes a blank name",
        ["GoldpathBulkFiles.FileName"] = "#476 — an upload's empty ?fileName= is stored as is",
        ["GoldpathErasureRecords.SubjectKey"] = "#476 — erase takes a blank subject key",
    };

    private static string ModelGolden([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Oracle", "composed-model.golden.txt");

    // ---- model goldens (no database needed: the provider builds the model) ----

    [Fact]
    public void Every_column_keeps_its_oracle_store_type()
    {
        using var db = OracleComposedDb.Create(oracle.ConnectionString);
        var actual = OracleComposedDb.Describe(db.GetService<IDesignTimeModel>().Model);
        var golden = File.ReadAllText(ModelGolden()).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (actual != golden)
        {
            var path = Path.Combine(Path.GetTempPath(), "composed-model.actual.txt");
            File.WriteAllText(path, actual);
            var expectedLines = golden.Split('\n');
            var actualLines = actual.Split('\n');
            var changed = actualLines.Except(expectedLines, StringComparer.Ordinal).Take(10)
                .Concat(expectedLines.Except(actualLines, StringComparer.Ordinal).Take(10).Select(line => "missing: " + line));
            Assert.Fail($"The Oracle store types moved (actual written to {path}; review it, then copy it over the golden):\n"
                + string.Join('\n', changed));
        }
    }

    [Fact]
    public void Every_identifier_fits_in_128_bytes()
    {
        using var db = OracleComposedDb.Create(oracle.ConnectionString);
        var identifiers = IdentifiersOf(db.GetService<IDesignTimeModel>().Model).ToList();
        output.WriteLine($"{identifiers.Count} identifiers; the longest is {identifiers.Max(i => Encoding.UTF8.GetByteCount(i.Name))} bytes");
        Assert.True(identifiers.Count > 300, $"only {identifiers.Count} identifiers — the walk lost a kind of name");
        Assert.Empty(Oversized(identifiers));
    }

    [Fact]
    public void The_identifier_check_counts_bytes_not_characters()
    {
        // 64 dotted capital İ are 64 characters and 128 bytes: they fit. One more character does not.
        var fits = new string('İ', 64);
        var over = new string('İ', 65);
        Assert.Empty(Oversized([("table", fits)]));
        Assert.Equal([$"table {over} is 130 bytes"], Oversized([("table", over)]));
    }

    // ---- '' is NULL on Oracle ----

    [Fact]
    public async Task An_empty_string_is_refused_by_every_non_nullable_string_column()
    {
        var connection = await oracle.CreateSchemaAsync("GP_EMPTY");
        await using var db = OracleComposedDb.Create(connection);
        await db.Database.EnsureCreatedAsync();

        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var probed = 0;
        var outcomes = new List<string>();
        await using var oracleConnection = new OracleConnection(connection);
        await oracleConnection.OpenAsync();
        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns.Where(c => !c.IsNullable && c.ProviderClrType == typeof(string)))
            {
                probed++;
                var outcome = await InsertEmptyAsync(oracleConnection, table, column);
                outcomes.Add($"{table.Name}.{column.Name}: {outcome}");
                if (MayWriteEmpty.ContainsKey($"{table.Name}.{column.Name}"))
                {
                    continue;
                }

                Assert.True(outcome == "refused (ORA-01400)", $"{table.Name}.{column.Name}: {outcome}");
            }
        }

        foreach (var line in outcomes)
        {
            output.WriteLine(line);
        }

        Assert.True(probed > 80, $"only {probed} columns probed — the walk lost a table");
        Assert.All(MayWriteEmpty, entry => Assert.Matches("^#[0-9]+ ", entry.Value));
        Assert.All(MayWriteEmpty.Keys, key => Assert.Contains(outcomes, line => line.StartsWith(key + ": refused (ORA-01400)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_global_ceiling_row_as_designed_cannot_be_written()
    {
        // #384: R3.2 keys the global ceiling on (Global, ""), and '' is NULL here. This fact flips when #384 lands.
        var connection = await oracle.CreateSchemaAsync("GP_CEILING");
        await using var db = OracleComposedDb.Create(connection);
        await db.Database.EnsureCreatedAsync();
        db.Set<GoldpathCampaignCeiling>().Add(new GoldpathCampaignCeiling
        {
            Kind = GoldpathCampaignCeilingKind.Global,
            Name = "",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "seed",
        });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ORA-01400", refused.InnerException?.Message, StringComparison.Ordinal);
        Assert.Contains("\"GoldpathCampaignCeilings\".\"Name\"", refused.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_column_holds_2000_characters_and_refuses_the_2001st()
    {
        // #475: HasMaxLength(-1) is NVARCHAR2(2000) on Oracle, not an unbounded type. This fact flips when #475 lands.
        var connection = await oracle.CreateSchemaAsync("GP_DOCUMENT");
        await using (var db = OracleComposedDb.Create(connection))
        {
            await db.Database.EnsureCreatedAsync();
            db.Rows.Add(NewRow(1) with { Document = new string('x', 2_000) });
            await db.SaveChangesAsync();
        }

        await using var again = OracleComposedDb.Create(connection);
        again.Rows.Add(NewRow(2) with { Document = new string('x', 2_001) });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        output.WriteLine(refused.InnerException?.Message);
        Assert.Contains("ORA-12899", refused.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_document_fails_in_the_client_before_it_reaches_oracle()
    {
        // #475: the provider sizes a HasMaxLength(-1) parameter at -1 and ODP.NET refuses a negative size for a NULL.
        // GoldpathJobExecutions.Error is null on every successful run. This fact flips when #475 lands.
        // Asserted by behaviour, not by a stack frame: the JIT inlines the parameter setters once the path is hot.
        var connection = await oracle.CreateSchemaAsync("GP_NULL_DOCUMENT");
        var commands = new List<string>();
        await using var db = new OracleComposedDb(new DbContextOptionsBuilder<OracleComposedDb>()
            .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))
            .EnableServiceProviderCaching(false)
            .LogTo(commands.Add, [RelationalEventId.CommandExecuting, RelationalEventId.CommandExecuted, RelationalEventId.CommandError])
            .Options);
        await db.Database.EnsureCreatedAsync();
        commands.Clear();

        db.Rows.Add(NewRow(1) with { Document = null });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<ArgumentOutOfRangeException>(refused.InnerException);
        Assert.DoesNotContain(commands, line => line.Contains("INSERT", StringComparison.Ordinal));
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Rows.CountAsync());
    }

    // ---- DateOnly / TimeOnly ----

    [Fact]
    public async Task DateOnly_and_TimeOnly_round_trip_on_the_campaign_row_and_an_app_row()
    {
        var connection = await oracle.CreateSchemaAsync("GP_TEMPORAL");
        var spring = new DateOnly(2026, 3, 29);   // DST changes in Europe that day
        var lastTick = new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9_999_999));
        var id = Guid.NewGuid();
        await using (var db = OracleComposedDb.Create(connection))
        {
            await db.Database.EnsureCreatedAsync();
            db.Set<GoldpathCampaign>().Add(new GoldpathCampaign
            {
                Id = id,
                Type = "fleet",
                Name = "temporal",
                ParametersJson = "{}",
                Tps = 1,
                MaxInFlight = 1,
                TimeZoneId = "Europe/Berlin",
                CreatedBy = "o2",
                CreatedAt = DateTimeOffset.UtcNow,
                EndDate = spring,
                QuotaDay = new DateOnly(2026, 10, 25),
                WindowStart = new TimeOnly(22, 0),
                WindowEnd = lastTick,
            });
            db.Rows.Add(NewRow(1) with { Day = spring, EndDay = DateOnly.MaxValue, At = lastTick, Until = TimeOnly.MinValue });
            await db.SaveChangesAsync();
        }

        await using var read = OracleComposedDb.Create(connection);
        var campaign = await read.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(spring, campaign.EndDate);
        Assert.Equal(new DateOnly(2026, 10, 25), campaign.QuotaDay);
        Assert.Equal(new TimeOnly(22, 0), campaign.WindowStart);
        Assert.Equal(lastTick, campaign.WindowEnd);
        var row = await read.Rows.AsNoTracking().SingleAsync();
        Assert.Equal((spring, DateOnly.MaxValue, lastTick, TimeOnly.MinValue), (row.Day, row.EndDay, row.At, row.Until));

        // A filter on the string-stored column still orders as dates and times do.
        Assert.Equal(1, await read.Set<GoldpathCampaign>().CountAsync(c => c.EndDate < new DateOnly(2026, 3, 30) && c.EndDate > new DateOnly(2026, 3, 28)));
        Assert.Equal(1, await read.Set<GoldpathCampaign>().CountAsync(c => c.WindowStart < new TimeOnly(22, 0, 1) && c.WindowEnd > new TimeOnly(23, 59, 59)));
    }

    // ---- keyset (RFC §2.4: no Oracle branch in KeysetPagination) ----

    [Fact]
    public async Task Composite_datetimeoffset_walk_translates_and_is_exact_on_oracle()
    {
        var (db, sql) = await KeysetDbAsync("GP_KEYSET_A");
        await using (db)
        {
            var baseTime = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 1; i <= 30; i++)
            {
                db.Rows.Add(NewRow(i) with { StampedAt = baseTime.AddMinutes(i / 4) });   // heavy duplicates → tiebreaker pressure
            }

            await db.SaveChangesAsync();
            var seen = await WalkAsync(db.Rows, 7, async (rows, request) => await rows.ToPageAsync(request, r => r.StampedAt, r => r.Id), r => r.Id);
            Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), seen);
            Assert.Contains(sql, statement => statement.Contains("FETCH FIRST", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Descending_guid_single_key_translates_on_oracle_stable_but_not_chronological()
    {
        var (db, _) = await KeysetDbAsync("GP_KEYSET_B");
        await using (db)
        {
            var inserted = Enumerable.Range(1, 9).Select(i => NewRow(i) with { Correlation = Guid.NewGuid() }).ToList();
            db.Rows.AddRange(inserted);
            await db.SaveChangesAsync();

            var seen = await WalkAsync(db.Rows, 4, async (rows, request) => await rows.ToPageAsync(request, r => r.Correlation, SortDirection.Descending, CancellationToken.None), r => r.Correlation);
            Assert.Equal(9, seen.Count);
            Assert.Equal(seen.Distinct().Count(), seen.Count);
            // RAW(16) orders by its bytes; a second walk returns the same order (stable), and nothing promises insertion order.
            var again = await WalkAsync(db.Rows, 4, async (rows, request) => await rows.ToPageAsync(request, r => r.Correlation, SortDirection.Descending, CancellationToken.None), r => r.Correlation);
            Assert.Equal(seen, again);
            Assert.Equal(seen.OrderByDescending(g => g.ToByteArray(), ByteOrder.Instance), seen);
        }
    }

    [Fact]
    public async Task Member_init_projection_walk_translates_on_oracle()
    {
        var (db, _) = await KeysetDbAsync("GP_KEYSET_C");
        await using (db)
        {
            var t0 = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
            for (var i = 1; i <= 12; i++)
            {
                db.Rows.Add(NewRow(i) with { StampedAt = t0.AddSeconds(i) });
            }

            await db.SaveChangesAsync();
            var page = await db.Rows
                .Select(r => new RowDto { Id = r.Id, StampedAt = r.StampedAt })
                .ToPageAsync(new PageRequest(Size: 5), d => d.StampedAt, d => d.Id);
            Assert.Equal([1L, 2L, 3L, 4L, 5L], page.Items.Select(d => d.Id));
            Assert.NotNull(page.NextCursor);
        }
    }

    [Fact]
    public async Task A_string_key_walks_in_binary_order_under_the_default_nls_sort()
    {
        // A pooled ODP.NET session takes its NLS_SORT from the culture of the thread that creates the pool, and this
        // fact's schema is its own pool, so it pins the culture whose session sorts BINARY rather than inheriting the
        // runner's locale (a tr-TR runner gets TURKISH: the next fact).
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var (db, _) = await KeysetDbAsync("GP_KEYSET_D");
        await using (db)
        {
            Assert.Equal("BINARY", await SessionNlsSortAsync(db));

            // Upper before lower, and the dotted/dotless Turkish i by code point: BINARY is not a linguistic sort.
            string[] labels = ["b", "B", "a", "A", "ı", "İ", "i", "I", "z", "Z", "é", "e"];
            db.Rows.AddRange(labels.Select((label, index) => NewRow(index + 1) with { Label = label }));
            await db.SaveChangesAsync();

            var seen = await WalkAsync(db.Rows, 5, async (rows, request) => await rows.ToPageAsync(request, r => r.Label, r => r.Id), r => r.Label);
            Assert.Equal(labels.OrderBy(label => label, StringComparer.Ordinal), seen);
        }
    }

    [Fact]
    public async Task A_string_key_walk_in_a_turkish_process_sorts_linguistically_and_skips_rows()
    {
        // #496: a pool created on a tr-TR thread opens its sessions with NLS_SORT=TURKISH while NLS_COMP stays BINARY,
        // so ToPageAsync's ORDER BY sorts linguistically and its "key > cursor" compares by code point: the cursor
        // jumps past rows, silently. This fact pins that defect; it flips when the Oracle composition pins every
        // session to a sort its comparisons agree with (#496, open-threads T54 (4)).
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("tr-TR"));
        var (db, _) = await KeysetDbAsync("GP_KEYSET_E");
        await using (db)
        {
            Assert.Equal("TURKISH", await SessionNlsSortAsync(db));

            string[] labels = ["ı", "I", "i", "İ", "X", "XA", "x", "ğ", "G", "ı", "I", "X"];
            db.Rows.AddRange(labels.Select((label, index) => NewRow(index + 1) with { Label = label }));
            await db.SaveChangesAsync();

            var seen = await WalkAsync(db.Rows, 5, async (rows, request) => await rows.ToPageAsync(request, r => r.Label, r => r.Id), r => r.Id);
            Assert.Equal(labels.Length, await db.Rows.CountAsync());
            Assert.Equal(seen.Distinct().Count(), seen.Count);
            Assert.True(seen.Count < labels.Length, $"the walk returned all {labels.Length} rows: #496 has landed, flip this fact");
        }
    }

    [Fact]
    public async Task A_string_key_walk_in_a_turkish_process_walks_every_row_once_the_session_sort_is_pinned()
    {
        // The workaround ops/data.md gives until #496: pin every session to BINARY when the connection opens. Same
        // tr-TR pool and labels as the previous fact; the pin alone makes the walk total.
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("tr-TR"));
        var (db, _) = await KeysetDbAsync("GP_KEYSET_F", new SessionSortPin());
        await using (db)
        {
            // Each open path on a fresh tr-TR pool: a session the other path already pinned would hide a missing override.
            OracleConnection.ClearPool((OracleConnection)db.Database.GetDbConnection());
            Assert.Equal("BINARY", SessionNlsSort(db));
            OracleConnection.ClearPool((OracleConnection)db.Database.GetDbConnection());
            Assert.Equal("BINARY", await SessionNlsSortAsync(db));

            string[] labels = ["ı", "I", "i", "İ", "X", "XA", "x", "ğ", "G", "ı", "I", "X"];
            db.Rows.AddRange(labels.Select((label, index) => NewRow(index + 1) with { Label = label }));
            await db.SaveChangesAsync();

            foreach (var size in new[] { 1, 5 })
            {
                var seen = await WalkAsync(db.Rows, size, async (rows, request) => await rows.ToPageAsync(request, r => r.Label, r => r.Id), r => r.Id);
                Assert.Equal(Enumerable.Range(1, labels.Length).Select(id => (long)id).Order(), seen.Order());
            }
        }
    }

    // ---- D7: migrate up, down and idempotent ----

    [Fact]
    public async Task The_composed_model_migrates_up_down_and_up_again_and_a_second_migrate_is_a_no_op()
    {
        var connection = await oracle.CreateSchemaAsync("GP_MIGRATE");
        await using var db = OracleMigratedDb.Create(connection);
        var differ = db.GetService<IMigrationsModelDiffer>();
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        ComposedOnOracle.Generated = differ.GetDifferences(null, model);
        ComposedOnOracle.GeneratedDown = differ.GetDifferences(model, null);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync();
        var tables = await UserTablesAsync(connection);
        Assert.Contains("GoldpathCampaignItems", tables);
        Assert.Contains("QRTZ_JOB_DETAILS", tables);   // upper-case on Oracle since O-3 (#406)
        Assert.Equal(model.Tables.Count() + 1, tables.Count);   // + __EFMigrationsHistory

        await migrator.MigrateAsync("0");
        Assert.Equal(["__EFMigrationsHistory"], await UserTablesAsync(connection));

        await migrator.MigrateAsync();
        Assert.Equal(model.Tables.Count() + 1, (await UserTablesAsync(connection)).Count);

        // A second migrate changes nothing: every object's DDL time is what it was. (The provider still runs its
        // guarded "create the history table if it is missing" block, which is a no-op when the table exists.)
        var before = await ObjectsAsync(connection);
        await Task.Delay(TimeSpan.FromSeconds(1.1));   // LAST_DDL_TIME has one-second resolution
        await migrator.MigrateAsync();
        Assert.Equal(before, await ObjectsAsync(connection));
        Assert.Equal(["20261028000000_ComposedOnOracle"], await db.Database.GetAppliedMigrationsAsync());
    }

    // ---- helpers ----

    private static OracleDataRow NewRow(long id) => new()
    {
        Id = id,
        StampedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        Correlation = Guid.NewGuid(),
        Label = "row",
        Document = "{}",   // never null here: a null document is its own fact (#475)
        Day = new DateOnly(2026, 7, 1),
    };

    private async Task<(OracleComposedDb Db, List<string> Sql)> KeysetDbAsync(string schema, params IInterceptor[] interceptors)
    {
        var connection = await oracle.CreateSchemaAsync(schema);
        var sql = new List<string>();
        var db = new OracleComposedDb(new DbContextOptionsBuilder<OracleComposedDb>()
            .UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))
            .AddInterceptors(interceptors)
            .EnableServiceProviderCaching(false)
            .LogTo(sql.Add, [RelationalEventId.CommandExecuted])
            .Options);
        await db.Database.EnsureCreatedAsync();
        return (db, sql);
    }

    private static async Task<string> SessionNlsSortAsync(DbContext db)
    {
        // Opened through EF, so a connection interceptor runs exactly as it does for a query.
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_SORT'";
            return (string)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static string SessionNlsSort(DbContext db)
    {
        // The synchronous open: EF calls the interceptor's ConnectionOpened here, not ConnectionOpenedAsync.
        db.Database.OpenConnection();
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_SORT'";
            return (string)command.ExecuteScalar()!;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static async Task<List<TKey>> WalkAsync<TKey>(
        IQueryable<OracleDataRow> rows, int size, Func<IQueryable<OracleDataRow>, PageRequest, Task<Page<OracleDataRow>>> page, Func<OracleDataRow, TKey> key)
    {
        var seen = new List<TKey>();
        string? cursor = null;
        do
        {
            var next = await page(rows, new PageRequest(cursor, size));
            seen.AddRange(next.Items.Select(key));
            cursor = next.NextCursor;
        }
        while (cursor is not null);

        return seen;
    }

    private static async Task<List<string>> ObjectsAsync(string connectionString)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_TYPE || ' ' || OBJECT_NAME || ' ' || TO_CHAR(LAST_DDL_TIME, 'YYYY-MM-DD HH24:MI:SS') FROM USER_OBJECTS ORDER BY 1";
        var objects = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    private static async Task<List<string>> UserTablesAsync(string connectionString)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TABLE_NAME FROM USER_TABLES ORDER BY TABLE_NAME";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Inserts one row whose every other NOT NULL column holds a valid value and whose probed column holds <c>''</c>,
    /// inside a transaction that is always rolled back.
    /// </summary>
    private static async Task<string> InsertEmptyAsync(OracleConnection connection, ITable table, IColumn probed)
    {
        var required = table.Columns.Where(c => !c.IsNullable).ToList();
        var names = string.Join(", ", required.Select(c => $"\"{c.Name}\""));
        var values = string.Join(", ", required.Select(c => c == probed ? "''" : ValidLiteral(c.StoreType)));
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (OracleTransaction)transaction;
        command.CommandText = $"INSERT INTO \"{table.Name}\" ({names}) VALUES ({values})";
        try
        {
            await command.ExecuteNonQueryAsync();
            return "accepted (stored as NULL? no: the column is NOT NULL — this would be a provider surprise)";
        }
        catch (OracleException e) when (e.Number == 1400)
        {
            return "refused (ORA-01400)";
        }
        catch (OracleException e)
        {
            return $"other: ORA-{e.Number:D5} {e.Message}";
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>A literal every Goldpath store type accepts, never NULL and never <c>''</c>.</summary>
    private static string ValidLiteral(string storeType)
        => storeType switch
        {
            _ when storeType.StartsWith("NVARCHAR2", StringComparison.Ordinal) => "N'x'",
            _ when storeType.StartsWith("VARCHAR2", StringComparison.Ordinal) => "'1'",   // Quartz's VARCHAR2(1) flags (O-3)
            _ when storeType.StartsWith("NUMBER", StringComparison.Ordinal) || storeType.StartsWith("DECIMAL", StringComparison.Ordinal) => "1",
            _ when storeType.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "SYSTIMESTAMP",
            _ when storeType.StartsWith("RAW", StringComparison.Ordinal) => "SYS_GUID()",
            "BINARY_DOUBLE" => "1",
            "BLOB" => "EMPTY_BLOB()",
            "NCLOB" => "N'x'",
            _ => throw new InvalidOperationException($"no literal for {storeType}"),
        };

    /// <summary>Every name Oracle stores for the model: tables, columns, keys, foreign keys, indexes, sequences.</summary>
    private static IEnumerable<(string Kind, string Name)> IdentifiersOf(IModel model)
    {
        var relational = model.GetRelationalModel();
        foreach (var table in relational.Tables)
        {
            yield return ("table", table.Name);
            foreach (var column in table.Columns)
            {
                yield return ("column", column.Name);
            }

            foreach (var key in table.UniqueConstraints)
            {
                yield return (key.GetIsPrimaryKey() ? "primary key" : "unique constraint", key.Name);
            }

            foreach (var foreignKey in table.ForeignKeyConstraints)
            {
                yield return ("foreign key", foreignKey.Name);
            }

            foreach (var index in table.Indexes)
            {
                yield return ("index", index.Name);
            }
        }

        foreach (var sequence in relational.Sequences)
        {
            yield return ("sequence", sequence.Name);
        }
    }

    private static List<string> Oversized(IEnumerable<(string Kind, string Name)> identifiers)
        => [.. identifiers
            .Where(i => Encoding.UTF8.GetByteCount(i.Name) > MaxIdentifierBytes)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Kind} {i.Name} is {Encoding.UTF8.GetByteCount(i.Name)} bytes"))];

    private sealed record RowDto
    {
        public long Id { get; init; }

        public DateTimeOffset StampedAt { get; init; }
    }

    /// <summary>Sets the thread's culture for one fact, so a connection opened inside it takes that culture's NLS settings.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }

    /// <summary>
    /// Pins every session to BINARY sort and comparison as its connection opens: the #496 workaround. EF calls
    /// <c>ConnectionOpened</c> for a synchronous open and <c>ConnectionOpenedAsync</c> for an asynchronous one, so both are pinned.
    /// </summary>
    private sealed class SessionSortPin : DbConnectionInterceptor
    {
        private const string Pin = "ALTER SESSION SET NLS_SORT=BINARY NLS_COMP=BINARY";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = Pin;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Pin;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Oracle's RAW comparison: byte by byte, unsigned.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>The same composed model, as a context the runtime migration below belongs to (D7 on Oracle).</summary>
public sealed class OracleMigratedDb(DbContextOptions<OracleMigratedDb> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyGoldpathConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => OracleComposedDb.Compose(modelBuilder);

    /// <summary>
    /// The context in the 19c form. The migration's operations are computed at test time, so there is no model
    /// snapshot to compare with: the pending-changes warning is moot.
    /// </summary>
    public static OracleMigratedDb Create(string connectionString)
        => new(new DbContextOptionsBuilder<OracleMigratedDb>()
            .UseOracle(connectionString, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
}

/// <summary>
/// The composed model's one migration, GENERATED against the Oracle provider at test time: its operations are the
/// provider's own model differ from nothing to the model (up) and back (down).
/// </summary>
[DbContext(typeof(OracleMigratedDb))]
[Migration("20261028000000_ComposedOnOracle")]
public sealed class ComposedOnOracle : Migration
{
    internal static IReadOnlyList<MigrationOperation> Generated { get; set; } = [];

    internal static IReadOnlyList<MigrationOperation> GeneratedDown { get; set; } = [];

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Operations.AddRange(Generated);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Operations.AddRange(GeneratedDown);
}
