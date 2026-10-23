using System.Text.Json.Nodes;
using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// GA gate G3: a manifest that selects a roadmap-only value is told so by <c>goldpath check</c>, from the
/// schema's own <c>$comment</c> annotation. The corpus is the proof: every valid entry's
/// <c># expect-warning:</c> lines are exactly the findings, and an entry without one gets none.
/// </summary>
public class RoadmapValuesTests
{
    private static JsonNode ShippedSchema() => JsonNode.Parse(SpecdriftGate.ManifestSchemaText())!;

    public static TheoryData<string> ValidCorpus()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "schemas", "manifest", "v1", "corpus", "valid")).Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ValidCorpus))]
    public void Every_valid_corpus_entry_warns_exactly_about_the_roadmap_values_it_names(string entry)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "schemas", "manifest", "v1", "corpus", "valid", entry));
        var expected = text.Split('\n')
            .Where(l => l.StartsWith("# expect-warning: ", StringComparison.Ordinal))
            .Select(l => l["# expect-warning: ".Length..].Trim())
            .Order(StringComparer.Ordinal);

        var found = RoadmapValues.Find(ShippedSchema(), ArtefactYaml.Parse(text))
            .Select(f => $"{f.Path}: {f.Value}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, found);
    }

    /// <summary>The four values G3 names, and `oracle` until #287 retires its annotation, each have a corpus row.</summary>
    [Theory]
    [InlineData("providers.broker: kafka")]
    [InlineData("providers.broker: inmemory")]
    [InlineData("observability.exporters[1]: prometheus")]
    [InlineData("features.auditTrail.store: eventstream")]
    [InlineData("providers.db: oracle")]
    public void Each_roadmap_only_value_has_a_corpus_row(string expected)
    {
        var rows = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "schemas", "manifest", "v1", "corpus", "valid"))
            .Select(File.ReadAllText);

        Assert.Contains(rows, r => r.Contains($"# expect-warning: {expected}\n", StringComparison.Ordinal));
    }

    [Fact]
    public void The_finding_names_the_annotation_that_owns_the_value()
    {
        var finding = Assert.Single(RoadmapValues.Find(ShippedSchema(), ArtefactYaml.Parse("providers:\n  broker: kafka\n")));

        Assert.Equal("providers.broker", finding.Path);
        Assert.Equal("kafka", finding.Value);
        Assert.Contains("SCHEMA-ONLY", finding.Annotation, StringComparison.Ordinal);
        Assert.Contains("#11", finding.Annotation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rabbitmq and none are WIRED. `kafka` and `inmemory` are SCHEMA-ONLY — no transport.", "inmemory,kafka")]
    [InlineData("otlp and console are WIRED; `prometheus` is SCHEMA-ONLY (no exporter).", "prometheus")]
    [InlineData("`database` is WIRED. `eventstream` is RESERVED — a written deferral.", "eventstream")]
    [InlineData("`a`, `b` or `c` are RESERVED", "a,b,c")]
    [InlineData("`postgresql` is the default; nothing here is roadmap-only.", "")]
    [InlineData("`x` is schema-only in lower case, which is not the annotation", "")]
    [InlineData("`a`, `b`, and `c` are SCHEMA-ONLY", "a,b,c")]                // the Oxford comma
    [InlineData("`a`, `b`, or `c` are RESERVED", "a,b,c")]
    [InlineData("`kafka` is schema-only for now", "")]                          // schema-honesty.sh refuses it too
    [InlineData("`kafka` (SCHEMA-ONLY) has no wiring", "")]                     // likewise: not the clause
    public void The_annotation_declares_the_backticked_values_before_is_or_are_SCHEMA_ONLY_or_RESERVED(string comment, string expected)
    {
        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal),
            RoadmapValues.Declared(comment).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The gate that holds the annotation and the CLI that reads it use ONE pattern, so a value
    /// <c>schema-honesty.sh</c> accepts as annotated is always a value <c>goldpath check</c> warns about.
    /// </summary>
    [Fact]
    public void Schema_honesty_reads_the_annotation_with_the_same_pattern()
    {
        var script = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "schema-honesty.sh"));

        var line = Assert.Single(script, l => l.StartsWith("CLAUSE = r\"", StringComparison.Ordinal));
        Assert.Equal($"CLAUSE = r\"{RoadmapValues.RoadmapClausePattern}\"", line);
    }

    /// <summary>A new roadmap value needs an annotation and no code; a value annotated but no longer accepted warns nothing.</summary>
    [Fact]
    public void A_new_annotation_is_acted_on_without_code_and_an_annotation_outliving_its_value_is_not()
    {
        var schema = JsonNode.Parse("""
            {
              "$defs": { "queue": { "enum": ["sqs", "pubsub"], "$comment": "`pubsub` is SCHEMA-ONLY (thread T99). `gone` is RESERVED." } },
              "properties": {
                "transport": { "properties": { "queue": { "$ref": "#/$defs/queue" } } },
                "lanes": { "type": "array", "items": { "properties": { "queue": { "$ref": "#/$defs/queue" } } } }
              }
            }
            """)!;

        var findings = RoadmapValues.Find(schema, ArtefactYaml.Parse("""
            transport: { queue: pubsub }
            lanes: [ { queue: sqs }, { queue: pubsub } ]
            other: gone
            """));

        Assert.Equal(["transport.queue: pubsub", "lanes[1].queue: pubsub"], findings.Select(f => $"{f.Path}: {f.Value}"));
    }

    /// <summary>
    /// An annotation under a <c>then</c> applies only when its <c>if</c> holds for this manifest: the schema gives a
    /// solution's and a service's <c>providers</c> different enums, and only the matching one may speak. A keyword the
    /// condition evaluator does not know counts as satisfied, so it can add a warning but never hide one.
    /// </summary>
    [Theory]
    [InlineData("kind: solution\nq: beta\n", true)]
    [InlineData("kind: service\nq: beta\n", false)]
    [InlineData("q: beta\n", true)]                                  // no kind: `properties` does not constrain an absent key
    [InlineData("kind: solution\nflag: false\nq: beta\n", false)] // `not const false` fails
    [InlineData("kind: solution\nflag: true\nq: beta\n", true)]
    public void A_then_branch_speaks_only_when_its_if_holds(string manifest, bool warns)
    {
        var schema = JsonNode.Parse("""
            {
              "allOf": [ {
                "if": { "properties": { "kind": { "enum": ["solution"] }, "flag": { "not": { "const": false } } }, "unknownKeyword": 1 },
                "then": { "properties": { "q": { "enum": ["alpha", "beta"], "$comment": "`beta` is SCHEMA-ONLY." } } }
              } ]
            }
            """)!;

        Assert.Equal(warns, RoadmapValues.Find(schema, ArtefactYaml.Parse(manifest)).Count == 1);
    }

    [Fact]
    public void A_required_key_the_manifest_lacks_fails_the_if()
    {
        var schema = JsonNode.Parse("""
            { "if": { "required": ["features"] }, "then": { "properties": { "q": { "enum": ["beta"], "$comment": "`beta` is RESERVED" } } } }
            """)!;

        Assert.Empty(RoadmapValues.Find(schema, ArtefactYaml.Parse("q: beta\n")));
        Assert.Single(RoadmapValues.Find(schema, ArtefactYaml.Parse("features: {}\nq: beta\n")));
    }

    /// <summary>The schema keywords the walk follows, each proven to carry an annotation to the manifest.</summary>
    [Theory]
    [InlineData("""{ "if": { "properties": { "kind": { "const": "a" } } }, "then": {}, "else": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: b\nq: beta\n", 1)]
    [InlineData("""{ "anyOf": [ { "properties": { "q": { "$ref": "#/$defs/q" } } } ] }""", "q: beta\n", 1)]
    [InlineData("""{ "oneOf": [ { "type": "boolean" }, { "properties": { "q": { "$ref": "#/$defs/q" } } } ] }""", "q: beta\n", 1)]
    [InlineData("""{ "allOf": [ { "properties": { "q": { "$ref": "#/$defs/q" } } }, { "properties": { "q": { "$ref": "#/$defs/q" } } } ] }""", "q: beta\n", 1)]   // two paths, one finding
    [InlineData("""{ "properties": { "q": { "$ref": "#/$defs/a~1b" } } }""", "q: beta\n", 1)]                                                      // JSON-pointer escape
    [InlineData("""{ "properties": { "q": { "$ref": "other.json#/$defs/q" } } }""", "q: beta\n", 0)]                                              // not this document
    [InlineData("""{ "properties": { "q": { "$ref": "#/$defs/loop" } } }""", "q: beta\n", 0)]                                                     // a $ref cycle ends
    [InlineData("""{ "properties": { "q": { "$ref": "#/$defs/spin" } } }""", "q: beta\n", 0)]                                                     // an allOf cycle ends
    [InlineData("""{ "properties": { "q": { "$ref": "#/$defs/turn" } } }""", "q: beta\n", 0)]                                                     // an if/then cycle ends
    [InlineData("""{ "properties": { "q": { "$ref": "#/$defs/t~0ilde" } } }""", "q: beta\n", 1)]                                                  // ~0 is a tilde
    [InlineData("""{ "if": true, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "q: beta\n", 1)]
    [InlineData("""{ "if": { "$ref": "#/$defs/isA" }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: b\nq: beta\n", 0)]
    [InlineData("""{ "if": { "$ref": "#/$defs/isA" }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: a\nq: beta\n", 1)]
    [InlineData("""{ "if": { "properties": { "kind": { "enum": ["a", "c"] } } }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: a\nq: beta\n", 1)]
    [InlineData("""{ "if": { "required": ["kind", "flag"] }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: a\nq: beta\n", 0)]
    [InlineData("""{ "if": { "allOf": [ { "required": ["kind"] }, { "properties": { "kind": { "const": "a" } } } ] }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: b\nq: beta\n", 0)]
    [InlineData("""{ "if": { "allOf": [ { "required": ["kind"] }, { "properties": { "kind": { "const": "a" } } } ] }, "then": { "properties": { "q": { "$ref": "#/$defs/q" } } } }""", "kind: a\nq: beta\n", 1)]
    [InlineData("""{ "properties": { "q": { "if": { "required": ["x"] }, "then": { "$ref": "#/$defs/q" } } } }""", "q: beta\n", 1)]                // `required` holds for a non-object
    public void The_walk_follows_the_schema_keywords_that_place_a_value(string body, string manifest, int warnings)
    {
        var schema = JsonNode.Parse(body)!.AsObject();
        schema["$defs"] = JsonNode.Parse("""
            {
              "q": { "enum": ["alpha", "beta"], "$comment": "`beta` is SCHEMA-ONLY." },
              "a/b": { "enum": ["beta"], "$comment": "`beta` is RESERVED." },
              "loop": { "$ref": "#/$defs/loop" },
              "spin": { "allOf": [ { "$ref": "#/$defs/spin" } ] },
              "turn": { "if": true, "then": { "$ref": "#/$defs/turn" } },
              "t~ilde": { "enum": ["beta"], "$comment": "`beta` is SCHEMA-ONLY." },
              "isA": { "properties": { "kind": { "const": "a" } } }
            }
            """);

        Assert.Equal(warnings, RoadmapValues.Find(schema, ArtefactYaml.Parse(manifest)).Count);
    }

    [Fact]
    public void Check_without_a_manifest_leaves_the_refusal_to_validate()
    {
        using var app = new FakeApp();
        File.Delete(app.Manifest);
        var output = new StringWriter();

        CliRunner.Run(["check", "--path", app.Root], new FakeProcessRunner(), output, TextWriter.Null);

        Assert.DoesNotContain("WARNING", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("── goldpath check: specdrift drift", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("- just\n- a list\n")]
    [InlineData("providers: kafka\n")]
    [InlineData("providers:\n  broker: [kafka]\n")]
    public void A_manifest_that_is_not_shaped_like_the_schema_selects_nothing(string manifest)
    {
        Assert.Empty(RoadmapValues.Find(ShippedSchema(), ArtefactYaml.Parse(manifest)));
    }

    [Fact]
    public void Check_warns_after_validate_and_still_passes()
    {
        using var app = new FakeApp();
        File.AppendAllText(app.Manifest, "\nobservability:\n  exporters: [prometheus]\n");
        var output = new StringWriter();

        var exit = CliRunner.Run(["check", "--path", app.Root], new FakeProcessRunner(), output, TextWriter.Null);

        var text = output.ToString();
        Assert.Equal(0, exit);
        var warning = text.IndexOf("── goldpath check: WARNING observability.exporters[0]: 'prometheus' is accepted by the schema and no code runs it.", StringComparison.Ordinal);
        Assert.True(warning > text.IndexOf("── goldpath check: specdrift validate", StringComparison.Ordinal), text);
        Assert.True(warning < text.IndexOf("── goldpath check: specdrift drift", StringComparison.Ordinal), text);
        Assert.Contains("The schema says: otlp and console are WIRED", text, StringComparison.Ordinal);
        Assert.Contains("── goldpath check GREEN", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_says_nothing_about_a_manifest_that_selects_only_wired_values()
    {
        using var app = new FakeApp();
        var output = new StringWriter();

        CliRunner.Run(["check", "--path", app.Root], new FakeProcessRunner(), output, TextWriter.Null);

        Assert.DoesNotContain("WARNING", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_does_not_warn_when_validate_is_red()
    {
        using var app = new FakeApp();
        File.WriteAllText(app.Manifest, File.ReadAllText(app.Manifest).Replace("broker: none", "broker: kafka", StringComparison.Ordinal));
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["--schema"] = 1;
        var output = new StringWriter();

        Assert.Equal(1, CliRunner.Run(["check", "--path", app.Root], runner, output, TextWriter.Null));
        Assert.DoesNotContain("WARNING", output.ToString(), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Goldpath.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
