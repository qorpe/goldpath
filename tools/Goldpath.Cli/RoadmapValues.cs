using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Goldpath.Cli;

/// <summary>
/// GA gate G3: a manifest that SELECTS a value the schema accepts but no code runs is told so.
/// The schema already says which values those are, in the <c>$comment</c> beside the enum that
/// accepts them (<c>`kafka` and `inmemory` are SCHEMA-ONLY</c>, <c>`eventstream` is RESERVED</c>),
/// and <c>scripts/schema-honesty.sh</c> holds every such value to carrying one. This reads the
/// SAME annotation, so a new roadmap-only value needs a <c>$comment</c> and no code here.
///
/// It runs after specdrift has validated the manifest, never instead of it: the engine decides
/// what is valid, and this only reports which valid choice will not run. The manifest is read
/// with <see cref="ArtefactYaml"/>, the reader that mirrors specdrift's rule for rule.
/// </summary>
public static partial class RoadmapValues
{
    /// <summary>One selected roadmap-only value: where it sits, what it is, and the annotation that owns it.</summary>
    public sealed record Finding(string Path, string Value, string Annotation);

    /// <summary>
    /// Every roadmap-only value <paramref name="manifest"/> selects. The schema is walked WITH the manifest, so an
    /// annotation applies only where the schema applies it: through <c>properties</c>, <c>items</c>, <c>$ref</c>
    /// and the combinators, and through a <c>then</c> only when its <c>if</c> holds for this manifest (a service's
    /// <c>providers</c> are not a solution's). A manifest that is not a mapping selects nothing.
    /// </summary>
    public static IReadOnlyList<Finding> Find(JsonNode schema, JsonNode? manifest)
    {
        var findings = new List<Finding>();
        if (manifest is JsonObject)
        {
            Walk(schema, schema, manifest, "", findings, depth: 0);
        }

        return findings;
    }

    /// <summary>The values a <c>$comment</c> declares roadmap-only: the backticked names before "is/are SCHEMA-ONLY|RESERVED".</summary>
    public static IReadOnlySet<string> Declared(string comment)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match clause in RoadmapClause().Matches(comment))
        {
            foreach (Match name in Backticked().Matches(clause.Groups["names"].Value))
            {
                values.Add(name.Groups[1].Value);
            }
        }

        return values;
    }

    private static void Walk(JsonNode root, JsonNode? node, JsonNode? value, string where, List<Finding> findings, int depth)
    {
        // A $ref cycle in a schema would never end; no manifest is this deep.
        if (node is not JsonObject schema || value is null || depth > 64)
        {
            return;
        }

        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            Walk(root, Resolve(root, reference), value, where, findings, depth + 1);
        }

        if (schema["$comment"]?.GetValue<string>() is { } comment && Declared(comment) is { Count: > 0 } declared)
        {
            Report(schema["enum"] as JsonArray, declared, comment, value, where, findings);
            if (value is JsonArray elements && schema["items"]?["enum"] is JsonArray itemEnum)
            {
                for (var i = 0; i < elements.Count; i++)
                {
                    Report(itemEnum, declared, comment, elements[i], $"{where}[{i}]", findings);
                }
            }
        }

        if (schema["properties"] is JsonObject properties && value is JsonObject mapping)
        {
            foreach (var (name, child) in properties)
            {
                if (mapping.TryGetPropertyValue(name, out var selected))
                {
                    Walk(root, child, selected, where.Length == 0 ? name : $"{where}.{name}", findings, depth + 1);
                }
            }
        }

        if (value is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Walk(root, schema["items"], array[i], $"{where}[{i}]", findings, depth + 1);
            }
        }

        if (schema["if"] is { } condition)
        {
            Walk(root, Holds(root, condition, value) ? schema["then"] : schema["else"], value, where, findings, depth + 1);
        }

        foreach (var combinator in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema[combinator] is JsonArray branches)
            {
                foreach (var branch in branches)
                {
                    Walk(root, branch, value, where, findings, depth + 1);
                }
            }
        }
    }

    private static void Report(JsonArray? accepted, IReadOnlySet<string> declared, string comment, JsonNode? value, string where, List<Finding> findings)
    {
        if (accepted is null || value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || !declared.Contains(text))
        {
            return;
        }

        if (accepted.Any(a => a is JsonValue j && j.TryGetValue<string>(out var s) && s == text)
            && !findings.Any(f => f.Path == where && f.Value == text))
        {
            findings.Add(new Finding(where, text, comment));
        }
    }

    /// <summary>
    /// Whether a manifest node satisfies an <c>if</c>. The manifest schema's conditions use <c>properties</c>,
    /// <c>const</c>, <c>enum</c>, <c>required</c>, <c>not</c> and <c>allOf</c>; a keyword outside that set is
    /// treated as satisfied, so an unfamiliar condition can only ever add a warning, never hide one.
    /// </summary>
    private static bool Holds(JsonNode root, JsonNode? condition, JsonNode? value)
    {
        if (condition is not JsonObject schema)
        {
            return true;
        }

        if (schema["$ref"]?.GetValue<string>() is { } reference && !Holds(root, Resolve(root, reference), value))
        {
            return false;
        }

        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, value))
        {
            return false;
        }

        if (schema["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            return false;
        }

        if (schema["required"] is JsonArray required
            && (value is not JsonObject present || required.Any(r => !present.ContainsKey(r?.GetValue<string>() ?? ""))))
        {
            return false;
        }

        if (schema["properties"] is JsonObject properties && value is JsonObject mapping
            && properties.Any(p => mapping.TryGetPropertyValue(p.Key, out var child) && !Holds(root, p.Value, child)))
        {
            return false;
        }

        if (schema["not"] is { } negated && Holds(root, negated, value))
        {
            return false;
        }

        return schema["allOf"] is not JsonArray all || all.All(branch => Holds(root, branch, value));
    }

    private static JsonNode? Resolve(JsonNode root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;   // the manifest schema is one document; an external reference is not ours to follow
        }

        JsonNode? node = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            node = node?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
        }

        return node;
    }

    // "`kafka` and `inmemory` are SCHEMA-ONLY", "`prometheus` is SCHEMA-ONLY", "`eventstream` is RESERVED".
    [GeneratedRegex(@"(?<names>(?:`[^`]+`(?:\s*,\s*|\s+and\s+|\s+or\s+)?)+)\s+(?:is|are)\s+(?:SCHEMA-ONLY|RESERVED)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RoadmapClause();

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
