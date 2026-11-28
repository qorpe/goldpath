using System.Text.Json;
using System.Text.RegularExpressions;

namespace Goldpath.Cli;

/// <summary>
/// <c>goldpath new service &lt;Name&gt;</c> and <c>goldpath new gateway</c> — the
/// microservice layout's verbs (template RFC 7d; platform RFC step 5). A service is an
/// ADDITIONAL web head with its OWN database and its OWN manifest (<c>kind: service</c> —
/// the manifest is the unit, foundation §10); the gateway is a YARP head routing
/// <c>/{head}/…</c> to the api and every service over Aspire service discovery. Both wear
/// the <c>goldpath:service-head</c> marker so the PRIMARY head stays unambiguous, both
/// wire into the AppHost through the same anchors <c>add worker</c> uses, and both are
/// snapshot-guarded: a red engine restores every touched file byte-identical.
/// </summary>
public static class NewServiceCommand
{
    /// <summary>Adds a service head (own db, own manifest) to an existing solution.</summary>
    public static int RunService(string name, string appRoot, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        var files = AppFiles.Locate(appRoot);
        RequireSolution(files);
        var facts = AppFacts.Read(files);
        facts.RequireWritableProvider("new service");
        if (facts.DatabaseProvider is "none")
        {
            throw new CliFailureException("a service head owns a database, and this app's provider could not be inferred — the api csproj references neither Npgsql.EntityFrameworkCore.PostgreSQL nor Microsoft.EntityFrameworkCore.SqlServer.");
        }

        var prefix = ApiPrefix(files.ApiProject);
        var pascal = AddWorkerCommand.Pascal(name);
        var projectName = $"{prefix}.{pascal}Service";
        var kebab = $"{AddWorkerCommand.Kebab(pascal)}-service";
        var safe = projectName.Replace('.', '_').Replace('-', '_');
        var projectDir = Path.Combine(appRoot, "src", projectName);
        if (Directory.Exists(projectDir))
        {
            throw new CliFailureException($"{projectDir} already exists — pick another name.");
        }

        var solutionFile = SingleSolution(appRoot);
        var smokeFile = FindSmokeFile(appRoot);
        var gatewayAppSettings = FindGatewayAppSettings(appRoot);
        var touched = new List<string> { files.AppHostFile, files.AppHostProject, solutionFile, files.ManifestFile };
        if (smokeFile is not null)
        {
            touched.Add(smokeFile);
        }

        if (gatewayAppSettings is not null)
        {
            touched.Add(gatewayAppSettings);
        }

        var snapshot = touched.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
        try
        {
            WriteServiceProject(projectDir, projectName, kebab, facts);
            WireAppHostService(files, projectName, kebab, safe, facts);
            if (runner.Run("dotnet", ["sln", solutionFile, "add", Path.Combine(projectDir, $"{projectName}.csproj")], appRoot) != 0)
            {
                throw new CliFailureException("dotnet sln add failed — see the output above.");
            }

            FlipDeploymentModel(files.ManifestFile);
            if (smokeFile is not null)
            {
                AppendSmokeHead(smokeFile, safe, kebab, routedProbe: null);
            }

            if (gatewayAppSettings is not null)
            {
                AppendGatewayRoute(gatewayAppSettings, kebab);
                WireGatewayReference(files.AppHostFile, $"{safe}Resource");
            }

            Gate(appRoot, runner, Path.Combine("src", projectName, ".goldpath", "manifest.yaml"));
        }
        catch
        {
            Restore(snapshot, projectDir);
            throw;
        }

        output.WriteLine($"── goldpath new service: {projectName} ({kebab}) — its OWN database, its OWN manifest (kind: service)");
        output.WriteLine($"   next: build once, then `goldpath db init` commits its first contract to specs/{projectName}.json and generates its Initial migration once it has entities;");
        output.WriteLine("   features still compose on the PRIMARY head — per-service features arrive with the products pilot (platform RFC).");
        return 0;
    }

    /// <summary>Adds the YARP gateway head routing the api and every service.</summary>
    public static int RunGateway(string appRoot, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        var files = AppFiles.Locate(appRoot);
        RequireSolution(files);
        var prefix = ApiPrefix(files.ApiProject);
        var projectName = $"{prefix}.Gateway";
        var projectDir = Path.Combine(appRoot, "src", projectName);
        if (Directory.Exists(projectDir))
        {
            throw new CliFailureException($"{projectDir} already exists — one gateway per solution.");
        }

        var solutionFile = SingleSolution(appRoot);
        var smokeFile = FindSmokeFile(appRoot);
        var heads = RoutedHeads(File.ReadAllText(files.AppHostFile));
        var touched = new List<string> { files.AppHostFile, files.AppHostProject, solutionFile, files.ManifestFile };
        if (smokeFile is not null)
        {
            touched.Add(smokeFile);
        }

        var snapshot = touched.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
        try
        {
            WriteGatewayProject(projectDir, projectName, heads);
            WireAppHostGateway(files, projectName, heads);
            if (runner.Run("dotnet", ["sln", solutionFile, "add", Path.Combine(projectDir, $"{projectName}.csproj")], appRoot) != 0)
            {
                throw new CliFailureException("dotnet sln add failed — see the output above.");
            }

            DeclareGatewayModule(files.ManifestFile);
            if (smokeFile is not null)
            {
                // The routing proof rides health: /api/… strips to the api's own probe —
                // green through the gateway is green END TO END, for any auth shape.
                AppendSmokeHead(smokeFile, "gateway", "gateway", routedProbe: "/api/health/ready");
            }

            Gate(appRoot, runner, Path.Combine("src", projectName, ".goldpath", "manifest.yaml"));
        }
        catch
        {
            Restore(snapshot, projectDir);
            throw;
        }

        output.WriteLine($"── goldpath new gateway: {projectName} — YARP over Aspire service discovery; routes /{{head}}/… for: {string.Join(", ", heads)}");
        output.WriteLine("   new services register their route automatically (goldpath new service edits the gateway's appsettings).");
        return 0;
    }

    private static void RequireSolution(AppFiles files)
    {
        var kind = ManifestEditor.ReadKind(File.ReadAllText(files.ManifestFile));
        if (kind != "solution")
        {
            throw new CliFailureException($"this manifest is kind '{kind}' — service and gateway heads join a SOLUTION's AppHost.");
        }
    }

    /// <summary>The api plus every service head — the set the gateway routes.</summary>
    private static List<string> RoutedHeads(string appHost)
        => Regex.Matches(appHost, "AddProject<[^>]+>\\(\"([a-z0-9-]+)\"\\)")
            .Select(m => m.Groups[1].Value)
            .Where(kebab => kebab == "api" || kebab.EndsWith("-service", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string ApiPrefix(string apiProject)
    {
        var name = Path.GetFileNameWithoutExtension(apiProject);
        return name.EndsWith(".Api", StringComparison.Ordinal) ? name[..^".Api".Length] : name;
    }

    private static string SingleSolution(string appRoot)
    {
        var solutions = Directory.GetFiles(appRoot, "*.sln");
        return solutions.Length == 1
            ? solutions[0]
            : throw new CliFailureException($"{solutions.Length} .sln files at {appRoot} — exactly one expected.");
    }

    private static string? FindSmokeFile(string appRoot)
        => Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .FirstOrDefault(p => File.ReadAllText(p).Contains(SmokeHeadsAnchor, StringComparison.Ordinal));

    private static string? FindGatewayAppSettings(string appRoot)
    {
        var candidate = Directory.GetDirectories(Path.Combine(appRoot, "src"), "*.Gateway").FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var settings = Path.Combine(candidate, "appsettings.json");
        return File.Exists(settings) ? settings : null;
    }

    private const string SmokeHeadsAnchor = "// goldpath:smoke heads";
    private const string GatewayReferencesAnchor = "// goldpath:gateway references";

    private static void Gate(string appRoot, IProcessRunner runner, string newManifestRelative)
    {
        if (SpecdriftGate.Validate(appRoot, runner) != 0
            || SpecdriftGate.ValidateManifest(appRoot, newManifestRelative, runner) != 0
            || SpecdriftGate.Drift(appRoot, runner) != 0)
        {
            throw new CliFailureException("the engine rejected the result — ALL files restored; nothing half-applied.");
        }
    }

    private static void Restore(Dictionary<string, string> snapshot, string projectDir)
    {
        foreach (var (path, text) in snapshot)
        {
            File.WriteAllText(path, text);
        }

        if (Directory.Exists(projectDir))
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static void FlipDeploymentModel(string manifestFile)
    {
        var text = File.ReadAllText(manifestFile);
        if (text.Contains("deploymentModel: microservice", StringComparison.Ordinal))
        {
            return;
        }

        // The manifest tells the truth the composition just became: more than one service
        // head IS the microservice deployment model.
        if (Regex.IsMatch(text, "deploymentModel: [a-z-]+"))
        {
            text = Regex.Replace(text, "deploymentModel: [a-z-]+", "deploymentModel: microservice");
        }
        else
        {
            // An app without the architecture block (attached, not generated) gains one.
            text = text.TrimEnd('\n') + "\narchitecture:\n  deploymentModel: microservice\n";
        }

        File.WriteAllText(manifestFile, text);
    }

    private static void DeclareGatewayModule(string manifestFile)
    {
        var text = File.ReadAllText(manifestFile);
        if (text.Contains("yarpGateway", StringComparison.Ordinal))
        {
            return;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        File.WriteAllText(manifestFile, lines + "\nmodules: [yarpGateway]\n");
    }

    private static void AppendSmokeHead(string smokeFile, string safe, string kebab, string? routedProbe)
    {
        var text = File.ReadAllText(smokeFile);
        var block = $"""
        var {safe}Client = app.CreateHttpClient("{kebab}");
        await WaitUntilAsync(async () =>
            (await {safe}Client.GetAsync("/health/ready", timeout.Token)).IsSuccessStatusCode, timeout.Token);
""";
        if (routedProbe is not null)
        {
            block += $"""

        // Routed THROUGH the head: a 2xx here is the whole chain answering.
        Assert.True((await {safe}Client.GetAsync("{routedProbe}", timeout.Token)).IsSuccessStatusCode);
""";
        }

        File.WriteAllText(smokeFile, TextEdits.InsertAfterAnchor(text, SmokeHeadsAnchor, block.Split('\n')));
    }

    private static void AppendGatewayRoute(string appSettingsFile, string kebab)
    {
        var text = File.ReadAllText(appSettingsFile);
        var routes = RouteJson(kebab, indent: "      ");
        var clusters = ClusterJson(kebab, indent: "      ");
        text = text.Replace("\"Routes\": {", "\"Routes\": {\n" + routes + ",", StringComparison.Ordinal);
        text = text.Replace("\"Clusters\": {", "\"Clusters\": {\n" + clusters + ",", StringComparison.Ordinal);
        JsonDocument.Parse(text);   // a malformed edit must fail HERE, inside the snapshot guard
        File.WriteAllText(appSettingsFile, text);
    }

    private static void WireGatewayReference(string appHostFile, string resourceVar)
    {
        var text = File.ReadAllText(appHostFile);
        if (!text.Contains(GatewayReferencesAnchor, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(appHostFile, TextEdits.InsertAfterAnchor(
            text, GatewayReferencesAnchor, [$"    .WithReference({resourceVar})"]));
    }

    private static string RouteJson(string kebab, string indent)
        => $$"""
{{indent}}"{{kebab}}": {
{{indent}}  "ClusterId": "{{kebab}}",
{{indent}}  "Match": { "Path": "/{{kebab}}/{**rest}" },
{{indent}}  "Transforms": [ { "PathRemovePrefix": "/{{kebab}}" } ]
{{indent}}}
""";

    private static string ClusterJson(string kebab, string indent)
        => $$"""
{{indent}}"{{kebab}}": {
{{indent}}  "Destinations": { "head": { "Address": "https+http://{{kebab}}" } }
{{indent}}}
""";

    private static void WriteServiceProject(string projectDir, string projectName, string kebab, AppFacts facts)
    {
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "Properties"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".goldpath"));

        var providerPackage = facts.DatabaseProvider == "postgres"
            ? "Npgsql.EntityFrameworkCore.PostgreSQL"
            : "Microsoft.EntityFrameworkCore.SqlServer";
        File.WriteAllText(Path.Combine(projectDir, $"{projectName}.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- goldpath:service-head — an additional head; the PRIMARY head keeps the anchors. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocuments>true</OpenApiGenerateDocuments>
    <OpenApiGenerateDocumentsOnBuild>true</OpenApiGenerateDocumentsOnBuild>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Goldpath.ApiDefaults" />
    <PackageReference Include="Goldpath.ServiceDefaults" />
    <PackageReference Include="Goldpath.Data" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server" PrivateAssets="all" />
    <PackageReference Include="{providerPackage}" />
  </ItemGroup>

</Project>
""");

        var provider = facts.DatabaseProvider == "postgres" ? "UseNpgsql" : "UseSqlServer";
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), $$"""
using Goldpath;
using Microsoft.EntityFrameworkCore;
using {{projectName}};

// A service head (microservice layout): its OWN database, its OWN manifest — the unit
// Goldpath binds to is the manifest, not the repo (foundation §10). Features compose on
// the PRIMARY head today; per-service features arrive with the products pilot.
var builder = WebApplication.CreateBuilder(args);

builder.AddGoldpathServiceDefaults();
builder.AddGoldpathApiDefaults();

// Design time and docgen tolerate a missing connection; nothing connects until used.
var connection = builder.Configuration.GetConnectionString("{{kebab.Replace("-service", "servicedb")}}");
builder.AddGoldpathData<WebApplicationBuilder, {{ServiceDbClass(projectName)}}>(options =>
{
    if (connection is not null)
    {
        options.{{provider}}(connection);
    }
    else
    {
        options.{{provider}}();
    }
});

var app = builder.Build();

app.MapGoldpathDefaultEndpoints();
app.MapGoldpathApi();
app.MapGet("/api/v1/ping", () => new { service = "{{kebab}}", status = "alive" });

app.Run();
""");

        File.WriteAllText(Path.Combine(projectDir, $"{ServiceDbClass(projectName)}.cs"), $$"""
using Goldpath;
using Microsoft.EntityFrameworkCore;

namespace {{projectName}};

/// <summary>
/// This service's OWN schema (db-per-service): starts empty on purpose — entities arrive
/// with the service's features, migrations with `goldpath db add`.
/// </summary>
public class {{ServiceDbClass(projectName)}}(DbContextOptions<{{ServiceDbClass(projectName)}}> options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyGoldpathConventions();
}
""");

        var port = 5300 + Math.Abs(projectName.Sum(c => c * 31)) % 200;
        File.WriteAllText(Path.Combine(projectDir, "Properties", "launchSettings.json"), $$"""
{
  "profiles": {
    "{{projectName}}": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:{{port}}"
    }
  }
}
""");

        File.WriteAllText(Path.Combine(projectDir, ".goldpath", "manifest.yaml"), $"""
schemaVersion: 1
kind: service
name: {projectName}
description: {kebab} — a service head with its own database (db-per-service)
owner: platform-team
boundedContext: {kebab.Replace("-service", "")}
specs:
  openapi:
    - specs/{projectName}.json
""");
    }

    private static string ServiceDbClass(string projectName)
        => projectName.Split('.')[^1].Replace("Service", "Db");

    private static void WireAppHostService(AppFiles files, string projectName, string kebab, string safe, AppFacts facts)
    {
        var appHost = File.ReadAllText(files.AppHostFile);
        var addServer = facts.DatabaseProvider == "postgres" ? "AddPostgres" : "AddSqlServer";
        var dbName = kebab.Replace("-service", "servicedb");
        var wiring = $"""
var {safe}Db = builder.{addServer}("{kebab}-db").AddDatabase("{dbName}");
var {safe}Resource = builder.AddProject<Projects.{safe}>("{kebab}")
    .WithReference({safe}Db).WaitFor({safe}Db)
    .WithHttpHealthCheck("/health/ready");

""";
        File.WriteAllText(files.AppHostFile, TextEdits.InsertAfterAnchor(appHost, Anchors.Workers, wiring.Split('\n')));

        var project = File.ReadAllText(files.AppHostProject);
        File.WriteAllText(files.AppHostProject, TextEdits.InsertAfterAnchor(
            project, Anchors.WorkerReferences,
            [$"    <ProjectReference Include=\"../{projectName}/{projectName}.csproj\" />"]));
    }

    private static void WriteGatewayProject(string projectDir, string projectName, List<string> heads)
    {
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "Properties"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".goldpath"));

        File.WriteAllText(Path.Combine(projectDir, $"{projectName}.csproj"), """
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- goldpath:service-head — an additional head; the PRIMARY head keeps the anchors. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Goldpath.ServiceDefaults" />
    <PackageReference Include="Yarp.ReverseProxy" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery.Yarp" />
  </ItemGroup>

</Project>
""");

        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), """
using Goldpath;

// The YARP gateway head (modules: [yarpGateway]): routes /{head}/… to the api and every
// service over Aspire service discovery — configuration, not code (ADR-0003: YARP is
// configured, never wrapped). Routes live in appsettings; goldpath new service appends.
var builder = WebApplication.CreateBuilder(args);

builder.AddGoldpathServiceDefaults();
builder.Services.AddServiceDiscovery();   // the resolver below needs the discovery CORE (config provider)
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.MapGoldpathDefaultEndpoints();
app.MapReverseProxy();

app.Run();
""");

        var routes = string.Join(",\n", heads.Select(h => RouteJson(h, "      ")));
        var clusters = string.Join(",\n", heads.Select(h => ClusterJson(h, "      ")));
        var settings = $$"""
{
  "ReverseProxy": {
    "Routes": {
{{routes}}
    },
    "Clusters": {
{{clusters}}
    }
  }
}
""";
        JsonDocument.Parse(settings);
        File.WriteAllText(Path.Combine(projectDir, "appsettings.json"), settings);

        var port = 5300 + Math.Abs(projectName.Sum(c => c * 31)) % 200;
        File.WriteAllText(Path.Combine(projectDir, "Properties", "launchSettings.json"), $$"""
{
  "profiles": {
    "{{projectName}}": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:{{port}}"
    }
  }
}
""");

        File.WriteAllText(Path.Combine(projectDir, ".goldpath", "manifest.yaml"), $$"""
schemaVersion: 1
kind: gateway
name: {{projectName}}
description: YARP gateway — routes /{head}/… to the api and every service
owner: platform-team
autoRegisterServices: true
""");
    }

    private static void WireAppHostGateway(AppFiles files, string projectName, List<string> heads)
    {
        var appHost = File.ReadAllText(files.AppHostFile);
        var safe = projectName.Replace('.', '_').Replace('-', '_');

        // The api chain is unassigned in the template — the gateway needs its handle for
        // service-discovery config injection (the corpay T12 precedent: name the var).
        appHost = Regex.Replace(appHost,
            @"^builder\.AddProject<Projects\.(\w+_Api)>\(""api""\)",
            "var api = builder.AddProject<Projects.$1>(\"api\")",
            RegexOptions.Multiline);

        var references = string.Join("\n", heads.Select(h =>
            h == "api" ? "    .WithReference(api)" : $"    .WithReference({HeadVar(appHost, h)})"));
        var wiring = $"""
builder.AddProject<Projects.{safe}>("gateway")
{references}
    {GatewayReferencesAnchor} — services join here (goldpath new service)
    .WithHttpHealthCheck("/health/ready");

""";
        // The gateway REFERENCES every head, so it must be composed AFTER all of them —
        // it lands just above Build().Run(), not at the workers anchor.
        const string runLine = "builder.Build().Run();";
        if (!appHost.Contains(runLine, StringComparison.Ordinal))
        {
            throw new CliFailureException("the AppHost has no 'builder.Build().Run();' line — cannot place the gateway last.");
        }

        File.WriteAllText(files.AppHostFile, appHost.Replace(runLine, wiring + runLine, StringComparison.Ordinal));

        var project = File.ReadAllText(files.AppHostProject);
        File.WriteAllText(files.AppHostProject, TextEdits.InsertAfterAnchor(
            project, Anchors.WorkerReferences,
            [$"    <ProjectReference Include=\"../{projectName}/{projectName}.csproj\" />"]));
    }

    private static string HeadVar(string appHost, string kebab)
    {
        var match = Regex.Match(appHost, $"var (\\w+) = builder\\.AddProject<[^>]+>\\(\"{Regex.Escape(kebab)}\"\\)");
        return match.Success
            ? match.Groups[1].Value
            : throw new CliFailureException($"the '{kebab}' head has no named resource variable in the AppHost — regenerate the service with a current goldpath, or name the chain's variable.");
    }
}
