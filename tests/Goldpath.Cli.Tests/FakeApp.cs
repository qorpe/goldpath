
namespace Goldpath.Cli.Tests;

/// <summary>A recorded process invocation.</summary>
public sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>Records invocations and returns scripted exit codes (default 0).</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    /// <summary>Everything that was run, in order.</summary>
    public List<ProcessCall> Calls { get; } = [];

    /// <summary>Scripts an exit code for calls whose arguments contain the marker.</summary>
    public Dictionary<string, int> ExitCodeWhenArgumentsContain { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public int Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Calls.Add(new ProcessCall(fileName, arguments, workingDirectory));
        foreach (var (marker, exitCode) in ExitCodeWhenArgumentsContain)
        {
            if (arguments.Any(a => a.Contains(marker, StringComparison.Ordinal)))
            {
                return exitCode;
            }
        }

        return 0;
    }
}

/// <summary>
/// A minimal on-disk replica of a generated app: exactly the anchored files goldpath add edits,
/// with the anchor lines the template ships. Disposable temp directory.
/// </summary>
public sealed class FakeApp : IDisposable
{
    /// <summary>The app root (pass as --path).</summary>
    public string Root { get; }

    /// <summary>Creates the fixture; flags mirror common generation shapes.</summary>
    /// <param name="sampleCommand">
    /// Where the template's sample <c>CreateOrderCommand</c> lives, generated WITHOUT idempotency:
    /// null (none — the team deleted it), <c>"api"</c> (vertical-slice) or <c>"application"</c>
    /// (clean-architecture, its own project with no packages anchor).
    /// </param>
    public FakeApp(bool sqlServer = false, bool cachingWired = false, string kind = "solution", bool jobsWired = false, bool messagingWired = false, bool authWired = false, bool apiKey = false, bool auditTrailWired = false, bool softDeleteWired = false, bool multiTenancyWired = false, bool dataProtectionWired = false, bool lockingWired = false, string? sampleCommand = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"goldpath-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(Root, ".goldpath"));
        Directory.CreateDirectory(Path.Combine(Root, ".specdrift"));
        File.WriteAllText(Path.Combine(Root, ".specdrift/rules.yaml"), "version: 1\nrules: []\n");
        Directory.CreateDirectory(Path.Combine(Root, "src/Shop.Api"));
        Directory.CreateDirectory(Path.Combine(Root, "src/Shop.AppHost"));

        File.WriteAllText(Manifest, $"""
            schemaVersion: 1
            kind: {kind}
            name: Shop
            description: test fixture
            owner: team-shop
            providers:
              db: {(sqlServer ? "sqlserver" : "postgresql")}
              broker: none
              auth: none
            """);

        var provider = sqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Npgsql.EntityFrameworkCore.PostgreSQL";
        File.WriteAllText(ApiProject, $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Goldpath.Abstractions" />
                <!-- goldpath:features packages — the drift profile is the source of these rows -->
                <PackageReference Include="{provider}" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(AppHostProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.AppHost" />
                <!-- goldpath:features packages — the drift profile is the source of these rows -->
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Shop.Api/Shop.Api.csproj" />
                <!-- goldpath:workers references — worker projects chain here (goldpath add worker) -->
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(Root, "Shop.sln"), "");
        File.WriteAllText(Path.Combine(Root, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Aspire.Hosting.AppHost" Version="13.4.6" />
                <PackageVersion Include="Goldpath.Abstractions" Version="0.1.0-preview.7" />
                <PackageVersion Include="Mediant.AspNetCore" Version="1.4.1" />
              </ItemGroup>
            </Project>
            """);

        var auth = authWired ? (apiKey ? "\nbuilder.AddGoldpathAuth(o => o.Strategy = GoldpathAuthStrategy.ApiKey);\n" : "\nbuilder.AddGoldpathAuth();\n") : string.Empty;
        var features = string.Concat(
            multiTenancyWired ? "builder.AddGoldpathMultiTenancy();\n" : "",
            auditTrailWired ? "builder.AddGoldpathAuditTrail<WebApplicationBuilder, ShopDbContext>();\n" : "",
            softDeleteWired ? "builder.AddGoldpathSoftDelete();\n" : "",
            dataProtectionWired ? "builder.AddGoldpathDataProtection();\n" : "",
            lockingWired ? (sqlServer ? "builder.AddGoldpathSqlServerLocking(o => o.ConnectionName = \"shopdb\");\n" : "builder.AddGoldpathLocking(o => { o.Provider = GoldpathLockProvider.Postgres; o.ConnectionName = \"shopdb\"; });\n") : "");
        var caching = cachingWired ? "\nbuilder.AddGoldpathCaching();\n" : string.Empty;
        var messaging = messagingWired
            ? """

              builder.AddGoldpathMessaging(bus =>
              {
                  // goldpath:features consumers — bus-riding features register here
                  bus.AddConsumer<OrderPlacedConsumer>();
              });

              """
            : string.Empty;
        var jobs = jobsWired
            ? """

              builder.AddGoldpathJobs<WebApplicationBuilder, ShopDbContext>(jobs =>
              {
                  jobs.ConnectionName = "shopdb";              // runs + schedules live in the app database
                  jobs.AddGoldpathArchivalJobs<ShopDbContext>();    // archive nightly, purge chained after it, verify weekly
              });

              """
            : string.Empty;
        File.WriteAllText(Program, $"""
            var builder = WebApplication.CreateBuilder(args);
            builder.AddGoldpathServiceDefaults();
            // goldpath:features registrations — the drift profile is the source of these rows
            {auth}{features}{caching}{jobs}{messaging}
            var shopDbConnection = builder.Configuration.GetConnectionString("shopdb");

            var app = builder.Build();

            // goldpath:features middleware — the drift profile is the source of these rows
            app.MapGoldpathDefaultEndpoints();
            // goldpath:features endpoints — admin surfaces map here (put them behind the auth floor)
            app.Run();
            """);

        File.WriteAllText(Model, """
            public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyGoldpathModelDefaults();
                    // goldpath:features model — the drift profile is the source of these rows
                }
            }
            """);

        File.WriteAllText(AppHost, """
            var builder = DistributedApplication.CreateBuilder(args);

            var database = builder.AddPostgres("dbserver").AddDatabase("shopdb");
            // goldpath:features resources — the drift profile is the source of these rows

            builder.AddProject<Projects.Shop_Api>("api")
                .WithReference(database).WaitFor(database)
                // goldpath:features references — the drift profile is the source of these rows
                .WithHttpHealthCheck("/health/ready");

            // goldpath:workers — additional worker projects wire here (goldpath add worker)

            builder.Build().Run();
            """);

        if (sampleCommand is not null)
        {
            var project = sampleCommand == "application" ? "Shop.Application" : "Shop.Api";
            if (sampleCommand == "application")
            {
                Directory.CreateDirectory(Path.Combine(Root, "src/Shop.Application"));
                File.WriteAllText(ApplicationProject, """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <ItemGroup>
                        <PackageReference Include="Mediant.AspNetCore" />
                        <PackageReference Include="Goldpath.Data" />
                      </ItemGroup>
                    </Project>
                    """);
            }

            SampleCommand = Path.Combine(Root, $"src/{project}/Orders/Features/CreateOrder.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(SampleCommand)!);
            File.WriteAllText(SampleCommand, $$"""
                namespace {{project}}.Orders.Features;

                [HttpEndpoint("POST", "/api/v1/orders")]
                public record CreateOrderCommand(string Reference, decimal Amount) : ICommand<Result<long>>;

                public class CreateOrderHandler(ShopDbContext db)
                    : ICommandHandler<CreateOrderCommand, Result<long>>
                {
                }
                """);
        }
    }

    /// <summary>Path of the template's sample command file, when the fixture has one.</summary>
    public string? SampleCommand { get; }

    /// <summary>Path of the clean-architecture Application csproj (sampleCommand: "application").</summary>
    public string ApplicationProject => Path.Combine(Root, "src/Shop.Application/Shop.Application.csproj");

    /// <summary>Path of the manifest.</summary>
    public string Manifest => Path.Combine(Root, ".goldpath/manifest.yaml");

    /// <summary>Path of the Api csproj.</summary>
    public string ApiProject => Path.Combine(Root, "src/Shop.Api/Shop.Api.csproj");

    /// <summary>Path of the AppHost csproj.</summary>
    public string AppHostProject => Path.Combine(Root, "src/Shop.AppHost/Shop.AppHost.csproj");

    /// <summary>Path of the composition root.</summary>
    public string Program => Path.Combine(Root, "src/Shop.Api/Program.cs");

    /// <summary>Path of the DbContext file.</summary>
    public string Model => Path.Combine(Root, "src/Shop.Api/ShopDbContext.cs");

    /// <summary>Path of the AppHost composition file.</summary>
    public string AppHost => Path.Combine(Root, "src/Shop.AppHost/AppHost.cs");

    /// <summary>Reads a fixture file.</summary>
    public string Read(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
