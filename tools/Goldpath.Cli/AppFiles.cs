namespace Goldpath.Cli;

/// <summary>
/// Locates the anchored files of a generated app by CONTENT, not by path convention: the
/// file carrying the anchor is the right file whatever the team renamed. Missing anchors
/// fail loudly with the anchor name.
/// </summary>
public sealed class AppFiles
{
    /// <summary>The Api project file (the app's single web-SDK csproj — naming/identity).</summary>
    public required string ApiProject { get; init; }

    /// <summary>
    /// The csproj carrying the packages anchor — where feature package references land.
    /// The Api project itself in the vertical-slice layout; the Infrastructure project in
    /// clean-architecture (model-touching packages must compile where the DbContext lives).
    /// </summary>
    public required string PackagesProject { get; init; }

    /// <summary>The AppHost project file (packages anchor + IsAspireHost).</summary>
    public required string AppHostProject { get; init; }

    /// <summary>The composition root carrying the registrations/middleware anchors.</summary>
    public required string ProgramFile { get; init; }

    /// <summary>The DbContext file carrying the model anchor.</summary>
    public required string ModelFile { get; init; }

    /// <summary>The AppHost composition file carrying the resources/references anchors.</summary>
    public required string AppHostFile { get; init; }

    /// <summary>The manifest (single source of truth).</summary>
    public required string ManifestFile { get; init; }

    /// <summary>
    /// The central package-version file (<c>Directory.Packages.props</c>) at the app root, or
    /// null when the app does not use central package management (see <see cref="PackagePins"/>).
    /// </summary>
    public string? PackagesProps { get; init; }

    /// <summary>What declares the template's sample command — found by content, like the anchors.</summary>
    public const string SampleCommandDeclaration = "public record CreateOrderCommand(";

    /// <summary>
    /// The file declaring the template's sample command, or null when the team deleted it (or
    /// two files declare one — goldpath does not choose). Not an anchor: an app without the
    /// sample is a normal app, so nothing fails for its absence.
    /// </summary>
    public string? SampleCommandFile { get; init; }

    /// <summary>The csproj that compiles <see cref="SampleCommandFile"/> — the nearest one above it.</summary>
    public string? SampleCommandProject { get; init; }

    /// <summary>Scans the app root and resolves every anchored file.</summary>
    public static AppFiles Locate(string appRoot)
    {
        var manifest = Path.Combine(appRoot, ".goldpath", "manifest.yaml");
        if (!File.Exists(manifest))
        {
            throw new CliFailureException($"no manifest at {manifest} — goldpath add runs inside a Goldpath-generated app (or pass --path).");
        }

        var projects = FindByContent(appRoot, "*.csproj", Anchors.Packages);
        var sources = FindByContent(appRoot, "*.cs", Anchors.Registrations);
        var models = FindByContent(appRoot, "*.cs", Anchors.Model);
        var hosts = FindByContent(appRoot, "*.cs", Anchors.Resources);

        // The Api project is the app's WEB project, found by SDK rather than by anchor:
        // in clean-architecture the packages anchor lives with the Infrastructure csproj
        // (the DbContext's model calls must compile there), so anchor-bearing and web are
        // different files — in vertical-slice they are the same one.
        var webProjects = FindByContent(appRoot, "*.csproj", "Microsoft.NET.Sdk.Web");
        var samples = FindByContent(appRoot, "*.cs", SampleCommandDeclaration);
        var sample = samples.Count == 1 ? samples[0] : null;

        return new AppFiles
        {
            ApiProject = Single(webProjects.Where(p =>
            {
                var text = File.ReadAllText(p);
                // Additional heads (goldpath new service|gateway) and workers (goldpath add
                // worker — a web host for probes) are web projects too — the markers keep
                // the PRIMARY head unambiguous for naming and features. Found by the
                // GmWorkerInSolution shape (2026-09-05): the SECOND add verb after a worker
                // saw two web projects and refused.
                return !text.Contains("IsAspireHost", StringComparison.Ordinal)
                    && !text.Contains("goldpath:service-head", StringComparison.Ordinal)
                    && !text.Contains("goldpath:worker-head", StringComparison.Ordinal);
            }), "Api csproj", "Microsoft.NET.Sdk.Web"),
            PackagesProject = Single(projects.Where(p => !File.ReadAllText(p).Contains("IsAspireHost", StringComparison.Ordinal)), "packages csproj", Anchors.Packages),
            AppHostProject = Single(projects.Where(p => File.ReadAllText(p).Contains("IsAspireHost", StringComparison.Ordinal)), "AppHost csproj", Anchors.Packages),
            ProgramFile = Single(sources, "Program.cs", Anchors.Registrations),
            ModelFile = Single(models, "DbContext", Anchors.Model),
            AppHostFile = Single(hosts, "AppHost.cs", Anchors.Resources),
            ManifestFile = manifest,
            PackagesProps = File.Exists(Path.Combine(appRoot, "Directory.Packages.props")) ? Path.Combine(appRoot, "Directory.Packages.props") : null,
            SampleCommandFile = sample,
            SampleCommandProject = sample is null ? null : OwningProject(sample, appRoot),
        };
    }

    private static string? OwningProject(string file, string appRoot)
    {
        var rootLength = appRoot.TrimEnd(Path.DirectorySeparatorChar).Length;
        for (var directory = Path.GetDirectoryName(file); directory is not null && directory.Length >= rootLength; directory = Path.GetDirectoryName(directory))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (project is not null)
            {
                return project;
            }
        }

        return null;
    }

    private static List<string> FindByContent(string appRoot, string pattern, string anchor)
        => Directory.EnumerateFiles(appRoot, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(anchor, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static string Single(IEnumerable<string> candidates, string what, string anchor)
    {
        var list = candidates.ToList();
        return list.Count switch
        {
            1 => list[0],
            0 => throw new CliFailureException($"no {what} carries the '{anchor}' anchor — was this app generated from a Goldpath template (or the anchor removed)?"),
            _ => throw new CliFailureException($"{list.Count} files carry the '{anchor}' anchor for {what} ({string.Join(", ", list)}) — goldpath cannot choose; keep exactly one."),
        };
    }
}

/// <summary>The anchor vocabulary the templates ship (RFC D4). Content match, not full line.</summary>
public static class Anchors
{
    /// <summary>csproj package-reference insertion point.</summary>
    public const string Packages = "goldpath:features packages";

    /// <summary>Program.cs builder-registration insertion point.</summary>
    public const string Registrations = "// goldpath:features registrations";

    /// <summary>Program.cs middleware insertion point (order-sensitive: before auth).</summary>
    public const string Middleware = "// goldpath:features middleware";

    /// <summary>Program.cs endpoint-mapping insertion point (admin surfaces, after auth).</summary>
    public const string Endpoints = "// goldpath:features endpoints";

    /// <summary>
    /// Insertion point INSIDE an existing AddGoldpathJobs configuration: the ConnectionName
    /// line every jobs composition carries (content match — jobs-riding features add
    /// their AddGoldpath*Jobs call after it instead of opening a second scheduler).
    /// </summary>
    public const string JobsOptions = "jobs.ConnectionName";

    /// <summary>
    /// Insertion point INSIDE an existing AddGoldpathMessaging configuration — bus-riding
    /// features register their consumers after it instead of opening a second bus.
    /// </summary>
    public const string BusConsumers = "// goldpath:features consumers";

    /// <summary>OnModelCreating insertion point.</summary>
    public const string Model = "// goldpath:features model";

    /// <summary>AppHost resource insertion point.</summary>
    public const string Resources = "// goldpath:features resources";

    /// <summary>AppHost insertion point for ADDITIONAL worker projects (after the Api's chain).</summary>
    public const string Workers = "// goldpath:workers";

    /// <summary>AppHost csproj insertion point for worker project references.</summary>
    public const string WorkerReferences = "goldpath:workers references";

    /// <summary>AppHost project-reference chain insertion point.</summary>
    public const string References = "// goldpath:features references";
}
