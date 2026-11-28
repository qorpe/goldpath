using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GoldpathTemplate.Infrastructure.Persistence;

/// <summary>
/// Design time (`dotnet ef`, goldpath db): the provider must BIND without a connection —
/// nothing connects until a real string is used (same tolerance as the composition root).
/// </summary>
public sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    /// <inheritdoc />
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>();
#if (UsePostgres)
        options.UseNpgsql();
#endif
#if (UseSqlServer)
        options.UseSqlServer();
#endif
#if (UseOracle)
        options.UseOracle();
#endif
        return new OrdersDbContext(options.Options);
    }
}
