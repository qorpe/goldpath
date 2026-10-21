using System.Reflection;

namespace Goldpath.Cli;

/// <summary>
/// Runs the deterministic engine against an app: validate (schema embedded in this CLI, so
/// tool and manifest contract cannot drift apart) and drift. The CLI never re-implements a
/// check the engine owns (ADR-0004 discipline).
/// </summary>
public static class SpecdriftGate
{
    /// <summary>Runs <c>specdrift validate</c> with the embedded schema (+ app rules when present).</summary>
    public static int Validate(string appRoot, IProcessRunner runner)
    {
        var (fileName, prefix) = ResolveEngine();
        List<string> arguments =
        [
            .. prefix,
            "validate", Path.Combine(".goldpath", "manifest.yaml"),
            "--schema", ExtractEmbeddedSchema(),
        ];
        var rules = Path.Combine(appRoot, ".specdrift", "rules.yaml");
        if (File.Exists(rules))
        {
            arguments.AddRange(["--rules", Path.Combine(".specdrift", "rules.yaml")]);
        }

        return runner.Run(fileName, arguments, appRoot);
    }

    /// <summary>Validates an ADDITIONAL manifest (service/gateway heads) — schema only.</summary>
    public static int ValidateManifest(string appRoot, string manifestRelative, IProcessRunner runner)
    {
        var (fileName, prefix) = ResolveEngine();
        return runner.Run(fileName, [.. prefix, "validate", manifestRelative, "--schema", ExtractEmbeddedSchema()], appRoot);
    }

    /// <summary>
    /// Validates one authoring artefact (goldpath-authoring RFC V1/V2) against a schema this CLI
    /// carries: a specanchor rule/ledger-term/spec, a decisions record, or a proposed manifest.
    /// A proposed manifest is judged exactly like the real one — embedded schema plus the app's
    /// own rules — so a proposal cannot pass here and fail the day it is applied.
    /// </summary>
    public static int ValidateArtefact(string appRoot, string relativePath, string schemaResource, IProcessRunner runner)
    {
        var (fileName, prefix) = ResolveEngine();
        List<string> arguments = [.. prefix, "validate", relativePath, "--schema", ExtractEmbedded(schemaResource)];
        var rules = Path.Combine(appRoot, ".specdrift", "rules.yaml");
        if (schemaResource == ManifestSchema && File.Exists(rules))
        {
            arguments.AddRange(["--rules", Path.Combine(".specdrift", "rules.yaml")]);
        }

        return runner.Run(fileName, arguments, appRoot);
    }

    /// <summary>The embedded manifest schema's resource name.</summary>
    public const string ManifestSchema = "goldpath-manifest.schema.json";

    /// <summary>Runs <c>specdrift drift</c> against the app root.</summary>
    public static int Drift(string appRoot, IProcessRunner runner)
    {
        var (fileName, prefix) = ResolveEngine();
        return runner.Run(fileName, [.. prefix, "drift", "--repo", "."], appRoot);
    }

    // GOLDPATH_SPECDRIFT lets tests and dev loops point at a checkout ("dotnet run --project ... --").
    private static (string FileName, string[] Prefix) ResolveEngine()
    {
        var overrideCommand = Environment.GetEnvironmentVariable("GOLDPATH_SPECDRIFT");
        if (string.IsNullOrWhiteSpace(overrideCommand))
        {
            return ("specdrift", []);
        }

        var parts = overrideCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts[1..]);
    }

    private static string ExtractEmbeddedSchema() => ExtractEmbedded(ManifestSchema);

    // Each schema lands under its own name: the manifest keeps the file name it always had,
    // the authoring schemas sit beside it with theirs.
    private static string ExtractEmbedded(string resource)
    {
        var target = Path.Combine(Path.GetTempPath(), resource == ManifestSchema ? "goldpath-manifest.schema.v1.json" : $"goldpath-{resource}");
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Goldpath.Cli.{resource}")
            ?? throw new InvalidOperationException($"The embedded schema '{resource}' is missing from this build.");
        using var file = File.Create(target);
        stream.CopyTo(file);
        return target;
    }
}
