namespace Goldpath.Cli;

/// <summary>
/// <c>goldpath add feature X</c> — the drift profile's row applied to an EXISTING app: manifest
/// line + package reference + registration + model call (+ feature-specific extras exactly
/// as the template would generate them). Ends with a specdrift round-trip; any finding
/// restores every touched file and fails loudly (RFC Slice C).
/// </summary>
public static class AddFeatureCommand
{
    /// <summary>Applies the feature, verifies with the engine, rolls back on findings.</summary>
    public static int Run(string feature, string appRoot, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        var manifestPath = Path.Combine(appRoot, ".goldpath", "manifest.yaml");
        if (!File.Exists(manifestPath))
        {
            throw new CliFailureException($"no manifest at {manifestPath} — goldpath add runs inside a Goldpath-generated app (or pass --path).");
        }

        var manifest = File.ReadAllText(manifestPath);
        if (ManifestEditor.ReadKind(manifest) is var kind && kind != "solution")
        {
            throw new CliFailureException(
                $"this manifest is kind '{kind ?? "<none>"}' — Ring B features live in the owning SOLUTION's manifest; run goldpath add there.");
        }

        var files = AppFiles.Locate(appRoot);

        var facts = AppFacts.Read(files);
        var plan = FeatureRecipes.Build(feature, facts);

        if (ManifestEditor.IsEnabled(manifest, plan.ManifestKey))
        {
            output.WriteLine($"goldpath: '{feature}' is already enabled ({plan.ManifestKey}) — nothing to do.");
            return 0;
        }

        // Snapshot BEFORE touching anything: the engine is the acceptance test, and a red
        // engine means the app must come back byte-identical.
        // Deduped once here: ApiProject and PackagesProject are the SAME file in the
        // vertical-slice layout, two files in clean-architecture.
        var touched = new[] { files.ManifestFile, files.ApiProject, files.PackagesProject, files.AppHostProject, files.ProgramFile, files.ModelFile, files.AppHostFile, files.PackagesProps, files.SampleCommandFile, files.SampleCommandProject, files.SmokeTestFile, files.SmokeTestProject }
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var snapshot = touched.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        try
        {
            Apply(plan, files, manifest);

            output.WriteLine($"goldpath: '{feature}' wired — running the engine (specdrift validate + drift)");
            var exitCode = SpecdriftGate.Validate(appRoot, runner);
            if (exitCode == 0)
            {
                exitCode = SpecdriftGate.Drift(appRoot, runner);
            }

            if (exitCode != 0)
            {
                Restore(snapshot);
                error.WriteLine($"goldpath: the engine rejected the result — ALL files restored; fix the findings above and retry ('{feature}' was NOT added).");
                return 1;
            }
        }
        catch
        {
            Restore(snapshot);
            throw;
        }

        if (plan.ModelCalls.Count > 0)
        {
            plan.NextSteps.Add($"the model grew: run `goldpath db add Add{AddWorkerCommand.Pascal(feature)}` and commit the migration (production applies the bundle — migrations RFC D5)");
        }

        output.WriteLine($"goldpath: '{feature}' added — engine clean. Your decisions (goldpath never guesses domain opt-ins):");
        foreach (var step in plan.NextSteps)
        {
            output.WriteLine($"  → {step}");
        }

        return 0;
    }

    private static void Apply(RecipePlan plan, AppFiles files, string manifest)
    {
        var manifestText = ManifestEditor.AddFeatureLines(manifest, plan.ManifestLines);
        foreach (var (key, value) in plan.ProviderEdits)
        {
            manifestText = ManifestEditor.SetProviderScalar(manifestText, key, value);
        }

        File.WriteAllText(files.ManifestFile, manifestText);

        if (plan.PackageVersions.Count > 0 && files.PackagesProps is { } propsPath)
        {
            File.WriteAllText(propsPath, PackagePins.AddMissing(File.ReadAllText(propsPath), plan.PackageVersions));
        }

        AddMissingReferences(files.PackagesProject, plan.ApiPackages);
        AddMissingReferences(files.AppHostProject, plan.AppHostPackages);

        var program = File.ReadAllText(files.ProgramFile);
        foreach (var marker in plan.RemoveFromProgram)
        {
            program = TextEdits.RemoveLinesContaining(program, marker);
        }

        foreach (var ns in plan.Usings)
        {
            program = TextEdits.EnsureUsing(program, ns);
        }

        if (plan.Registrations.Count > 0)
        {
            program = TextEdits.InsertAfterAnchor(program, Anchors.Registrations, plan.Registrations);
        }

        if (plan.Middleware.Count > 0)
        {
            program = TextEdits.InsertAfterAnchor(program, Anchors.Middleware, plan.Middleware);
        }

        // One line per insertion: endpoint lines are self-contained statements, and two
        // jobs-riding features SHARE MapGoldpathJobsAdmin — batch insertion would either
        // duplicate it or (because the duplicate probe checks only the first line) drop the
        // second feature's own admin endpoint with it.
        foreach (var endpoint in plan.Endpoints.AsEnumerable().Reverse())   // each lands at anchor+1 — reverse keeps the declared order
        {
            program = TextEdits.InsertAfterAnchor(program, Anchors.Endpoints, [endpoint]);
        }

        if (plan.JobsOptionsLines.Count > 0)
        {
            program = TextEdits.InsertAfterAnchor(program, Anchors.JobsOptions, plan.JobsOptionsLines);
        }

        if (plan.BusLines.Count > 0)
        {
            program = TextEdits.InsertAfterAnchor(program, Anchors.BusConsumers, plan.BusLines);
        }

        File.WriteAllText(files.ProgramFile, program);

        if (plan.ModelCalls.Count > 0)
        {
            // Same per-line rule as the endpoints: AddGoldpathJobs() is shared by every
            // jobs-riding feature and must land exactly once.
            var model = File.ReadAllText(files.ModelFile);
            foreach (var ns in plan.ModelUsings)
            {
                model = TextEdits.EnsureUsing(model, ns);
            }

            foreach (var call in plan.ModelCalls.AsEnumerable().Reverse())   // each lands at anchor+1 — reverse keeps the declared order
            {
                model = TextEdits.InsertAfterAnchor(model, Anchors.Model, [call]);
            }

            File.WriteAllText(files.ModelFile, model);
        }

        if (plan.Resources.Count > 0 || plan.References.Count > 0)
        {
            var appHost = File.ReadAllText(files.AppHostFile);
            if (plan.Resources.Count > 0)
            {
                appHost = TextEdits.InsertAfterAnchor(appHost, Anchors.Resources, plan.Resources);
            }

            if (plan.References.Count > 0)
            {
                appHost = TextEdits.InsertAfterAnchor(appHost, Anchors.References, plan.References);
            }

            File.WriteAllText(files.AppHostFile, appHost);
        }

        if (plan.SampleCommandLines.Count > 0 && files.SampleCommandFile is { } sample)
        {
            MarkSampleCommand(sample, plan.SampleCommandLines);
            if (files.SampleCommandProject is { } owner)
            {
                AddMissingReferences(owner, plan.SampleCommandPackages);
            }
        }

        if (plan.SmokeClientLines.Count > 0 && files.SmokeTestFile is { } smokePath)
        {
            var smoke = File.ReadAllText(smokePath);
            if (plan.SmokeDoneMarker is null || !smoke.Contains(plan.SmokeDoneMarker, StringComparison.Ordinal))
            {
                foreach (var ns in plan.SmokeUsings)
                {
                    smoke = TextEdits.EnsureUsing(smoke, ns);
                }

                File.WriteAllText(smokePath, TextEdits.InsertAfterAnchor(smoke, AppFiles.SmokeClientLine, plan.SmokeClientLines));
                if (files.SmokeTestProject is { } smokeProject)
                {
                    AddMissingReferences(smokeProject, plan.SmokePackages);
                }
            }
        }
    }

    /// <summary>
    /// Places the recipe's lines directly above the sample command's declaration, where the
    /// template puts them. A sample that already carries <c>[Idempotent]</c> — the team got
    /// there first — is left exactly as it is.
    /// </summary>
    private static void MarkSampleCommand(string path, IReadOnlyList<string> lines)
    {
        var text = File.ReadAllText(path);
        if (text.Contains("Idempotent]", StringComparison.Ordinal) || text.Contains("Idempotent(", StringComparison.Ordinal))
        {
            return;
        }

        var at = text.IndexOf(AppFiles.SampleCommandDeclaration, StringComparison.Ordinal);
        var lineStart = at <= 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, text.Insert(lineStart, string.Join(newline, lines) + newline));
    }

    private static void Restore(Dictionary<string, string> snapshot)
    {
        foreach (var (path, content) in snapshot)
        {
            File.WriteAllText(path, content);
        }
    }

    /// <summary>
    /// Adds a PackageReference for each package the project does not already reference, and
    /// only those. Checked PER PACKAGE, against the whole project file: the anchor insertion's
    /// own guard looks only at the first line of the block it is given, and six recipes ride the
    /// jobs runtime — each leads with its own package and then asks for Goldpath.Jobs, so the
    /// guard never saw Goldpath.Jobs was already there and every second jobs feature referenced
    /// it again (NU1504, which the generated apps treat as an error). The anchor guard is left
    /// as it is: it also inserts Program.cs blocks, where lines like "{" repeat legitimately.
    ///
    /// A project without the packages anchor — clean-architecture's Application project, which
    /// the template never meant goldpath add to grow — takes the reference after its last one.
    /// </summary>
    private static void AddMissingReferences(string projectPath, IReadOnlyCollection<string> packages)
    {
        if (packages.Count == 0)
        {
            return;
        }

        var project = File.ReadAllText(projectPath);
        var missing = packages
            .Distinct(StringComparer.Ordinal)
            .Where(p => !project.Contains($"<PackageReference Include=\"{p}\"", StringComparison.Ordinal))
            .Select(p => $"    <PackageReference Include=\"{p}\" />")
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        if (project.Contains(Anchors.Packages, StringComparison.Ordinal))
        {
            File.WriteAllText(projectPath, TextEdits.InsertAfterAnchor(project, Anchors.Packages, missing));
            return;
        }

        var last = project.LastIndexOf("<PackageReference ", StringComparison.Ordinal);
        if (last < 0)
        {
            throw new CliFailureException($"{projectPath} carries neither the '{Anchors.Packages}' anchor nor a PackageReference to add {string.Join(", ", packages)} beside — add it by hand.");
        }

        var newline = project.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lineEnd = project.IndexOf('\n', last);
        var block = string.Join(newline, missing) + newline;
        File.WriteAllText(projectPath, lineEnd < 0 ? project + newline + block : project.Insert(lineEnd + 1, block));
    }
}
