using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Oracle.EntityFrameworkCore.Infrastructure;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The composed model the Oracle Data proofs run against (G7, slice O-2, #405): Data's own conventions on an
/// app entity, then the four module contributions an adopter's context composes the way the template does —
/// Jobs (the run model and the Quartz store), Campaign, Bulk and Archival.
/// </summary>
public sealed class OracleComposedDb(DbContextOptions<OracleComposedDb> options) : DbContext(options)
{
    /// <summary>Data's own test entity: one column of every CLR shape the conventions decide.</summary>
    public DbSet<OracleDataRow> Rows => Set<OracleDataRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyGoldpathConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => Compose(modelBuilder);

    /// <summary>The composition, shared with the migration context: the app entity first, then the modules, as the template orders them.</summary>
    public static void Compose(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OracleDataRow>(row =>
        {
            row.ToTable("OracleDataRows");
            row.HasKey(r => r.Id);
            row.Property(r => r.Id).ValueGeneratedNever();
            row.Property(r => r.Document).HasMaxLength(-1);
            row.HasIndex(r => new { r.StampedAt, r.Id });
        });
        modelBuilder.ApplyGoldpathModelDefaults();
        modelBuilder.AddGoldpathArchiveModel();
        modelBuilder.AddGoldpathBulk();
        modelBuilder.AddGoldpathCampaign();
        modelBuilder.AddGoldpathJobs();
    }

    /// <summary>
    /// A context over Oracle that builds its model fresh (no cached provider), in the SQL form of the supported
    /// floor: RFC goldpath-oracle says "the 19c form is what ships", and the provider's own default is 23ai's
    /// (a <c>BOOLEAN</c> column 19c does not have).
    /// </summary>
    public static OracleComposedDb Create(string connectionString)
        => new(new DbContextOptionsBuilder<OracleComposedDb>()
            .UseOracle(connectionString, oracle => oracle.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19))
            .EnableServiceProviderCaching(false)
            .Options);

    /// <summary>
    /// The model as Oracle will build it, one line per column, ordered: <c>table.column: store type [null|not null]</c>.
    /// The Quartz <c>QRTZ_*</c> tables are left out (upper-case on Oracle, so matched without regard to case): their names
    /// and types are pinned against Quartz's own Oracle DDL by slice O-3 (#406, <c>JobsOracleTests</c>).
    /// </summary>
    public static string Describe(IModel model)
    {
        var lines = new List<string>();
        foreach (var table in model.GetRelationalModel().Tables.Where(t => !t.Name.StartsWith("qrtz_", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var column in table.Columns)
            {
                lines.Add($"{table.Name}.{column.Name}: {column.StoreType} {(column.IsNullable ? "null" : "not null")}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines) + "\n";
    }
}

/// <summary>Data's own test entity for the Oracle proofs: one property of each shape the conventions decide.</summary>
public sealed record OracleDataRow
{
    public long Id { get; set; }

    public DateTimeOffset StampedAt { get; set; }

    public Guid Correlation { get; set; }

    public bool Flag { get; set; }

    public string Label { get; set; } = "";

    public string? Note { get; set; }

    public string? Document { get; set; }

    public decimal Amount { get; set; }

    public OracleDataKind Kind { get; set; }

    public TenantId? Tenant { get; set; }

    public DateOnly Day { get; set; }

    public DateOnly? EndDay { get; set; }

    public TimeOnly At { get; set; }

    public TimeOnly? Until { get; set; }
}

/// <summary>An enum the conventions store as a string.</summary>
public enum OracleDataKind
{
    Plain = 0,
    Fancy = 1,
}
