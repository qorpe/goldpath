using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace Goldpath.Cli;

/// <summary>What <c>goldpath spec ready</c> was asked to do.</summary>
/// <param name="AppRoot">The app (repository) root.</param>
/// <param name="GateSpecs">The specs to hold to gate #1 (approved, no open brackets, not self-approved).</param>
/// <param name="Author">The author of the change under review — required with <paramref name="GateSpecs"/>.</param>
/// <param name="NextId">"rule" or "spec": print the next free id instead of checking.</param>
public sealed record SpecReadyOptions(string AppRoot, IReadOnlyList<string> GateSpecs, string? Author, string? NextId);

/// <summary>
/// <c>goldpath spec ready</c> — the deterministic floor under authoring (goldpath-authoring RFC
/// §5). V1: every artefact's shape, judged by the engine against the pinned schemas. V2: every
/// proposed manifest, judged by the engine exactly as the real one is. V3: what no single-file
/// schema can see — ids unique across the repository, every source pointing at lines that
/// exist, every spec's rules present, every question a decision names declared, the open-bracket
/// count telling the truth, and, for the specs named with <c>--gate</c>, ADR-0010's gate #1.
/// It reads and reports; it never writes, and it calls no LLM (ADR-0004).
/// </summary>
public static class SpecReadyCommand
{
    private const string DomainRoot = "docs/domain";
    private const string ProposalsRoot = "specs/proposals";

    private static readonly Regex s_openBracket = new(@"\[OPEN\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex s_termSource = new(@"^(?<file>.+?)(?::(?<start>\d+)(?:-(?<end>\d+))?)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Runs the checks (or prints the next id); 0 ready, 1 findings, 2 usage.</summary>
    public static int Run(SpecReadyOptions options, IProcessRunner runner, TextWriter output)
    {
        if (!Directory.Exists(options.AppRoot))
        {
            throw new CliFailureException($"no directory at {options.AppRoot} — run goldpath spec ready inside the app (or pass --path).");
        }

        var found = Found.Scan(options.AppRoot);
        if (options.NextId is { } kind)
        {
            output.WriteLine(NextId(kind, found));
            return 0;
        }

        if (found.Total == 0)
        {
            output.WriteLine($"── goldpath spec ready: no authoring artefacts under {DomainRoot}/<context>/rules|terms or {ProposalsRoot}/SPEC-nnnn — nothing to check");
            return 0;
        }

        var findings = new List<string>();

        output.WriteLine("── goldpath spec ready: V1 artefact shape (specdrift validate, the pinned schemas)");
        var shaped = new List<Artefact>();
        foreach (var artefact in found.Artefacts)
        {
            if (SpecdriftGate.ValidateArtefact(options.AppRoot, artefact.Path, artefact.Schema, runner) == 0)
            {
                shaped.Add(artefact);
            }
            else
            {
                findings.Add($"V1 {artefact.Path}: the engine rejected its shape (its findings are above)");
            }
        }

        output.WriteLine("── goldpath spec ready: V2 proposed manifests (specdrift validate, schema + the app's rules)");
        foreach (var manifest in found.ProposedManifests)
        {
            if (SpecdriftGate.ValidateArtefact(options.AppRoot, manifest, SpecdriftGate.ManifestSchema, runner) != 0)
            {
                findings.Add($"V2 {manifest}: the engine rejected the proposed manifest (its findings are above)");
            }
        }

        output.WriteLine(options.GateSpecs.Count == 0
            ? "── goldpath spec ready: V3 ids, sources, rule links, questions, open brackets"
            : $"── goldpath spec ready: V3 ids, sources, rule links, questions, open brackets + gate #1 for {string.Join(", ", options.GateSpecs)}");
        findings.AddRange(CrossChecks(options, found, shaped));

        foreach (var finding in findings)
        {
            output.WriteLine($"  {finding}");
        }

        if (findings.Count > 0)
        {
            output.WriteLine($"── goldpath spec ready RED: {findings.Count} finding(s)");
            return 1;
        }

        output.WriteLine($"── goldpath spec ready GREEN: {found.Count(Kind.Rule)} rule(s), {found.Count(Kind.Term)} term(s), {found.Count(Kind.Spec)} spec(s), {found.Count(Kind.Decisions)} decision record(s), {found.ProposedManifests.Count} proposed manifest(s)");
        return 0;
    }

    /// <summary>
    /// The next free id of a kind: one above every id of that kind written ANYWHERE in the
    /// artefacts — a spec citing a rule that another branch has not merged yet still reserves
    /// it. Uniqueness across branches is then V3's job on the merge.
    /// </summary>
    private static string NextId(string kind, Found found)
    {
        var prefix = kind switch
        {
            "rule" => "RULE",
            "spec" => "SPEC",
            _ => throw new CliUsageException($"--next-id takes rule or spec, not '{kind}'"),
        };
        var pattern = new Regex($@"{prefix}-(\d{{4}})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var texts = found.Artefacts.Select(a => a.Path)
            .Concat(found.ProposedManifests)
            .Concat(found.ProposalFolders)
            .ToList();
        texts.AddRange(found.Artefacts.Select(a => File.ReadAllText(Path.Combine(found.Root, a.Path))));
        var highest = texts
            .SelectMany(text => pattern.Matches(text))
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
        if (highest >= 9999)
        {
            throw new CliFailureException($"{prefix}-9999 is taken — the four-digit id space (specanchor's pattern) is exhausted.");
        }

        return $"{prefix}-{highest + 1:D4}";
    }

    private static List<string> CrossChecks(SpecReadyOptions options, Found found, List<Artefact> shaped)
    {
        var findings = new List<string>();
        var parsed = new List<(Artefact Artefact, JsonObject Node)>();
        foreach (var artefact in shaped)
        {
            try
            {
                if (ArtefactYaml.Parse(File.ReadAllText(Path.Combine(found.Root, artefact.Path))) is JsonObject node)
                {
                    parsed.Add((artefact, node));
                }
            }
            catch (YamlException exception)
            {
                findings.Add($"V3 {artefact.Path}: not readable as YAML ({exception.Message})");
            }
        }

        foreach (var folder in found.ProposalFolders.Where(f => !found.Artefacts.Any(a => a.Kind == Kind.Spec && a.Folder == f)))
        {
            findings.Add($"V3 {folder}: a proposal without spec.yaml — the folder holds one change, and the spec is what gate #1 approves");
        }

        var rules = parsed.Where(p => p.Artefact.Kind == Kind.Rule).ToList();
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rules.GroupBy(r => Text(r.Node, "rule_id"), StringComparer.Ordinal))
        {
            ruleIds.Add(group.Key);
            if (group.Count() > 1)
            {
                findings.Add($"V3 {group.Key} is declared by {group.Count()} files ({string.Join(", ", group.Select(g => g.Artefact.Path))}) — ids are unique across the repository; take a new one with --next-id rule");
            }
        }

        foreach (var (artefact, node) in rules)
        {
            var id = Text(node, "rule_id");
            if (!string.Equals(Path.GetFileNameWithoutExtension(artefact.Path), id, StringComparison.Ordinal))
            {
                findings.Add($"V3 {artefact.Path}: declares {id}, but a rule card is named after its id ({id}.yaml)");
            }

            var index = 0;
            foreach (var source in Items(node, "source_ref"))
            {
                var where = $"{id} source_ref[{index++}]";
                if (source?["file"] is null)
                {
                    findings.Add($"V3 {where}: a database-object source cannot be resolved in forward authoring — cite the request or the scenario card by file and lines");
                    continue;
                }

                findings.AddRange(Span(found.Root, where, Text(source, "file"), Number(source, "line_start"), Number(source, "line_end")));
            }
        }

        var terms = parsed.Where(p => p.Artefact.Kind == Kind.Term).ToList();
        foreach (var group in terms.GroupBy(t => $"{Text(t.Node, "context")} / {Text(t.Node, "term")}", StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            findings.Add($"V3 the term '{group.Key}' is defined by {group.Count()} files ({string.Join(", ", group.Select(g => g.Artefact.Path))}) — one entry per term per context");
        }

        foreach (var (artefact, node) in terms)
        {
            var index = 0;
            foreach (var source in Items(node, "source_ref"))
            {
                var where = $"term '{Text(node, "term")}' source_ref[{index++}]";
                var match = s_termSource.Match(source?.GetValue<string>() ?? "");
                long? start = match.Groups["start"].Success ? long.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture) : null;
                long? end = match.Groups["end"].Success ? long.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) : start;
                findings.AddRange(Span(found.Root, where, match.Groups["file"].Value, start, end));
            }
        }

        var specs = parsed.Where(p => p.Artefact.Kind == Kind.Spec).ToList();
        foreach (var group in specs.GroupBy(s => Text(s.Node, "spec_id"), StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            findings.Add($"V3 {group.Key} is declared by {group.Count()} specs ({string.Join(", ", group.Select(g => g.Artefact.Path))}) — ids are unique across the repository; take a new one with --next-id spec");
        }

        foreach (var (artefact, node) in specs)
        {
            var id = Text(node, "spec_id");
            if (!string.Equals(Path.GetFileName(artefact.Folder), id, StringComparison.Ordinal))
            {
                findings.Add($"V3 {artefact.Path}: declares {id}, but its proposal folder is {artefact.Folder} — the folder is named after the spec");
            }

            foreach (var rule in Items(node, "rule_ids").Select(r => r?.GetValue<string>() ?? ""))
            {
                if (!ruleIds.Contains(rule))
                {
                    findings.Add($"V3 {id} cites {rule}, and no rule card declares it (expected under {DomainRoot}/<context>/rules/{rule}.yaml)");
                }
            }

            var declared = Number(node, "open_brackets") ?? 0;
            var counted = s_openBracket.Matches(Text(node, "body")).Count;
            if (declared != counted)
            {
                findings.Add($"V3 {id}: open_brackets says {declared}, the body carries {counted} [OPEN …] placeholder(s) — the count is never resolved silently");
            }
        }

        foreach (var (artefact, node) in parsed.Where(p => p.Artefact.Kind == Kind.Decisions))
        {
            var id = Text(node, "spec_id");
            if (!string.Equals(Path.GetFileName(artefact.Folder), id, StringComparison.Ordinal))
            {
                findings.Add($"V3 {artefact.Path}: records decisions for {id}, but sits in {artefact.Folder}");
            }

            var questions = Items(node, "questions").Select(q => Text(q, "id")).ToList();
            foreach (var duplicate in questions.GroupBy(q => q, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                findings.Add($"V3 {artefact.Path}: question {duplicate.Key} is declared {duplicate.Count()} times");
            }

            var index = 0;
            foreach (var decision in Items(node, "decisions"))
            {
                var where = $"{artefact.Path} decisions[{index++}] ({Text(decision, "path")})";
                var cited = new List<string>();
                if (decision?["question"] is { } question)
                {
                    cited.Add(question.GetValue<string>());
                }

                foreach (var optIn in Items(decision, "opt_ins"))
                {
                    if (optIn?["question"] is { } optInQuestion)
                    {
                        cited.Add(optInQuestion.GetValue<string>());
                    }

                    if (optIn?["source"] is JsonObject answerSource && answerSource["file"] is not null)
                    {
                        findings.AddRange(Span(found.Root, $"{where} opt-in answer", Text(answerSource, "file"), Number(answerSource, "line_start"), Number(answerSource, "line_end")));
                    }
                }

                foreach (var missing in cited.Where(q => !questions.Contains(q, StringComparer.Ordinal)))
                {
                    findings.Add($"V3 {where}: names {missing}, which the record's questions do not declare");
                }

                if (decision?["source"] is JsonObject source && source["file"] is not null)
                {
                    findings.AddRange(Span(found.Root, where, Text(source, "file"), Number(source, "line_start"), Number(source, "line_end")));
                }
            }
        }

        findings.AddRange(GateOne(options, specs, found));
        return findings;
    }

    /// <summary>
    /// ADR-0010's gate #1 for the named specs: approved by someone, no open bracket left, and the
    /// approver is not the change's own author — the one check that keeps the gate from being a
    /// rubber stamp the author hands themselves (RFC §10).
    /// </summary>
    private static IEnumerable<string> GateOne(SpecReadyOptions options, List<(Artefact Artefact, JsonObject Node)> specs, Found found)
    {
        foreach (var gated in options.GateSpecs)
        {
            var spec = specs.FirstOrDefault(s => string.Equals(Text(s.Node, "spec_id"), gated, StringComparison.Ordinal));
            if (spec.Node is null)
            {
                yield return found.Artefacts.Any(a => a.Kind == Kind.Spec && Path.GetFileName(a.Folder) == gated)
                    ? $"gate #1 {gated}: its spec did not pass V1, so it cannot be approved"
                    : $"gate #1 {gated}: no spec declares it under {ProposalsRoot}/";
                continue;
            }

            var approver = Text(spec.Node, "approved_by").Trim();
            if (approver.Length == 0)
            {
                yield return $"gate #1 {gated}: no approved_by — the spec is a proposal until a business approver signs it";
            }
            else if (string.Equals(approver, options.Author?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                yield return $"gate #1 {gated}: approved by its own author ({approver}) — the approval must come from someone else (ADR-0010)";
            }

            var open = Number(spec.Node, "open_brackets") ?? 0;
            if (open > 0)
            {
                yield return $"gate #1 {gated}: {open} open bracket(s) remain — an expert decision is still pending";
            }
        }
    }

    /// <summary>A source span must exist: a file inside the repository, and lines that file has.</summary>
    private static IEnumerable<string> Span(string root, string where, string file, long? start, long? end)
    {
        if (file.Length == 0)
        {
            yield return $"V3 {where}: names no file";
            yield break;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, file));
        if (Path.IsPathRooted(file) || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            yield return $"V3 {where}: {file} is outside the repository — a source is something a reviewer can open in the same change";
            yield break;
        }

        if (!File.Exists(full))
        {
            yield return $"V3 {where}: points at {file}, which does not exist";
            yield break;
        }

        if (start is null)
        {
            yield break;
        }

        var lines = File.ReadAllLines(full).Length;
        if (start > end)
        {
            yield return $"V3 {where}: line_start {start} is after line_end {end}";
        }
        else if (end > lines)
        {
            yield return $"V3 {where}: points at lines {start}–{end} of {file}, which has {lines} line(s)";
        }
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? node, string key)
        => node?[key] is JsonArray array ? array : [];

    private static string Text(JsonNode? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static long? Number(JsonNode? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private enum Kind
    {
        Rule,
        Term,
        Spec,
        Decisions,
    }

    /// <summary>One artefact: its repository-relative path, its kind, and the schema that judges it.</summary>
    private sealed record Artefact(string Path, Kind Kind, string Schema, string Folder);

    /// <summary>Every artefact under the two roots, found by place — the layout IS the contract (RFC §4).</summary>
    private sealed class Found
    {
        public required string Root { get; init; }

        public List<Artefact> Artefacts { get; } = [];

        public List<string> ProposedManifests { get; } = [];

        public List<string> ProposalFolders { get; } = [];

        public int Total => Artefacts.Count + ProposedManifests.Count + ProposalFolders.Count;

        public int Count(Kind kind) => Artefacts.Count(a => a.Kind == kind);

        public static Found Scan(string root)
        {
            var found = new Found { Root = root };
            var domain = System.IO.Path.Combine(root, DomainRoot);
            if (Directory.Exists(domain))
            {
                foreach (var file in Directory.EnumerateFiles(domain, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    if (!IsYaml(file))
                    {
                        continue;
                    }

                    var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file));
                    var relative = Relative(root, file);
                    if (folder == "rules")
                    {
                        found.Artefacts.Add(new Artefact(relative, Kind.Rule, "rule.schema.v1.json", Relative(root, System.IO.Path.GetDirectoryName(file)!)));
                    }
                    else if (folder == "terms")
                    {
                        found.Artefacts.Add(new Artefact(relative, Kind.Term, "ledger-term.schema.v1.json", Relative(root, System.IO.Path.GetDirectoryName(file)!)));
                    }
                }
            }

            var proposals = System.IO.Path.Combine(root, ProposalsRoot);
            if (Directory.Exists(proposals))
            {
                foreach (var folder in Directory.EnumerateDirectories(proposals).Order(StringComparer.Ordinal))
                {
                    var relativeFolder = Relative(root, folder);
                    found.ProposalFolders.Add(relativeFolder);
                    AddIfPresent(found, root, folder, "spec.yaml", Kind.Spec, "spec.schema.v1.json", relativeFolder);
                    AddIfPresent(found, root, folder, "decisions.yaml", Kind.Decisions, "decisions.schema.json", relativeFolder);
                    var manifest = System.IO.Path.Combine(folder, "manifest.proposed.yaml");
                    if (File.Exists(manifest))
                    {
                        found.ProposedManifests.Add(Relative(root, manifest));
                    }
                }
            }

            return found;
        }

        private static void AddIfPresent(Found found, string root, string folder, string name, Kind kind, string schema, string relativeFolder)
        {
            var path = System.IO.Path.Combine(folder, name);
            if (File.Exists(path))
            {
                found.Artefacts.Add(new Artefact(Relative(root, path), kind, schema, relativeFolder));
            }
        }

        private static bool IsYaml(string file)
            => file.EndsWith(".yaml", StringComparison.Ordinal) || file.EndsWith(".yml", StringComparison.Ordinal);

        // Forward slashes on every platform: these paths are printed and cited, never re-joined.
        private static string Relative(string root, string path)
            => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
