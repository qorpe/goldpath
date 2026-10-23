using System.Text.Json.Nodes;

namespace Goldpath.Cli;

/// <summary>
/// <c>goldpath check</c> — the one verb a team runs before pushing: specdrift validate, specdrift
/// drift, db status (migrations RFC D5), then <c>dotnet build</c>. Stops at the first red step (each later step assumes the
/// earlier one). A valid manifest that selects a roadmap-only value is warned about after validate,
/// without failing (GA gate G3).
/// </summary>
public static class CheckCommand
{
    /// <summary>Runs validate → drift → build, returning the first nonzero exit code.</summary>
    public static int Run(string appRoot, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        output.WriteLine("── goldpath check: specdrift validate");
        var exitCode = SpecdriftGate.Validate(appRoot, runner);
        if (exitCode != 0)
        {
            return exitCode;
        }

        WarnRoadmapOnly(appRoot, output);

        output.WriteLine("── goldpath check: specdrift drift");
        exitCode = SpecdriftGate.Drift(appRoot, runner);
        if (exitCode != 0)
        {
            return exitCode;
        }

        output.WriteLine("── goldpath check: db status (a model change without a migration is drift too)");
        exitCode = DbCommand.StatusForCheck(appRoot, runner, output, error);
        if (exitCode != 0)
        {
            return exitCode;
        }

        output.WriteLine("── goldpath check: dotnet build");
        exitCode = runner.Run("dotnet", ["build", "--nologo"], appRoot);
        if (exitCode == 0)
        {
            output.WriteLine("── goldpath check GREEN");
        }

        return exitCode;
    }

    /// <summary>
    /// GA gate G3: a valid manifest that selects a value no code runs is told so, as a warning that does not
    /// fail the check — the schema accepts the value on purpose, so a manifest can declare intent
    /// (<see cref="RoadmapValues"/>).
    /// </summary>
    private static void WarnRoadmapOnly(string appRoot, TextWriter output)
    {
        var manifest = Path.Combine(appRoot, ".goldpath", "manifest.yaml");
        if (!File.Exists(manifest))
        {
            return;   // validate has already said so, in the engine's words
        }

        var schema = JsonNode.Parse(SpecdriftGate.ManifestSchemaText())!;
        foreach (var finding in RoadmapValues.Find(schema, ArtefactYaml.Parse(File.ReadAllText(manifest))))
        {
            output.WriteLine(
                $"── goldpath check: WARNING {finding.Path}: '{finding.Value}' is accepted by the schema and no code runs it. " +
                $"The schema says: {finding.Annotation}");
        }
    }
}
