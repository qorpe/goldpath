using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Goldpath.Cli;

/// <summary>
/// Reads an authoring artefact (a specanchor rule/term/spec card, a decisions record) into a
/// JSON tree for <c>goldpath spec ready</c>'s cross-file checks. The conversion mirrors
/// specdrift's own YAML reader rule for rule — same YamlDotNet version, YAML 1.2 core-schema
/// scalars (bool/int/float/null, quoted scalars always strings) — because the engine judges the
/// artefact's SHAPE first, and the CLI must never read a file differently from the engine that
/// accepted it. The manifest is a different case: the CLI never parses it (see
/// <see cref="ManifestEditor"/>).
/// </summary>
public static class ArtefactYaml
{
    /// <summary>Parses YAML (or JSON) text; null for an empty document.</summary>
    /// <exception cref="YamlException">The text is not YAML.</exception>
    public static JsonNode? Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping),
        YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => throw new YamlException($"Unsupported YAML node kind '{node.GetType().Name}'."),
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            result[((YamlScalarNode)key).Value ?? ""] = Convert(value);
        }

        return result;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
        {
            return null;
        }

        // Quoted scalars are ALWAYS strings — "true" and true differ on purpose.
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(value);
        }

        return value switch
        {
            "null" or "~" or "" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            _ => JsonValue.Create(value),
        };
    }
}
