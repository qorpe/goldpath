using System.Text.RegularExpressions;
using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// Oracle RFC §4 (#412): a container image a template names is pinned to an exact version —
/// never <c>latest</c>, <c>23</c> or <c>slim</c>. Fail-closed: a tag is accepted only when it
/// starts with a full <c>major.minor.patch</c>; a call with no tag argument, a floating word, a
/// partial version, or a tag built from anything but a literal is refused. Images Aspire chooses
/// itself (<c>AddPostgres</c>, <c>AddSqlServer</c>, …) are pinned by the pinned Aspire package and
/// are not this test's.
/// </summary>
public class TemplateImagePinTests
{
    private static readonly Regex ImageCall = new(
        @"\.WithImage(?<tag>Tag)?\s*\((?<args>[^;]*?)\)\s*(?=[.;\r\n])",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Pinned = new(
        @"^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant);

    /// <summary>The image calls in an AppHost's source, each with the verdict on its tag (null = pinned).</summary>
    public static IReadOnlyList<(string Call, string? Refusal)> Judge(string source)
    {
        var verdicts = new List<(string, string?)>();
        foreach (Match call in ImageCall.Matches(source))
        {
            var args = Regex.Matches(call.Groups["args"].Value, @"""(?<s>[^""]*)""|(?<other>[^,\s][^,]*)")
                .Select(m => m.Groups["s"].Success ? (Literal: true, Text: m.Groups["s"].Value) : (Literal: false, Text: m.Groups["other"].Value.Trim()))
                .ToList();
            var tag = call.Groups["tag"].Success ? args.ElementAtOrDefault(0) : args.ElementAtOrDefault(1);
            string? refusal = tag.Text is null ? "no tag: the image floats to whatever its registry calls latest"
                : !tag.Literal ? $"the tag is not a literal ({tag.Text}), so it cannot be read as a pin"
                : !Pinned.IsMatch(tag.Text) ? $"'{tag.Text}' is not an exact major.minor.patch version"
                : null;
            verdicts.Add((call.Value, refusal));
        }

        return verdicts;
    }

    [Fact]
    public void Every_image_a_template_AppHost_names_is_pinned_to_an_exact_version()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var calls = Directory.EnumerateFiles(Path.Combine(root, "templates"), "AppHost.cs", SearchOption.AllDirectories)
            .SelectMany(file => Judge(File.ReadAllText(file)).Select(v => (File: Path.GetRelativePath(root, file), v.Call, v.Refusal)))
            .ToList();

        Assert.Contains(calls, c => c.Call.Contains("gvenzl/oracle-free", StringComparison.Ordinal));   // the scan sees the one image named today
        Assert.All(calls, c => Assert.True(c.Refusal is null, $"{c.File}: {c.Call} — {c.Refusal}"));
    }

    [Theory]
    [InlineData(""".WithImage("gvenzl/oracle-free", "23.26.3-slim-faststart");""")]
    [InlineData(""".WithImage("redis", "8.2.1-alpine");""")]
    [InlineData(""".WithImageTag("23.26.3");""")]
    [InlineData(""".WithImage("x/y", "1.2.3")\n    .WithImageRegistry("docker.io");""")]
    public void An_exact_version_passes(string source)
        => Assert.All(Judge(source), v => Assert.Null(v.Refusal));

    [Theory]
    [InlineData(""".WithImage("gvenzl/oracle-free");""")]                       // no tag
    [InlineData(""".WithImage("gvenzl/oracle-free", "latest");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", "23");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", "slim");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", "23-slim");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", "23.26");""")]
    [InlineData(""".WithImage("postgres", "17.6");""")]                         // two parts: fail-closed, even where the vendor stops there
    [InlineData(""".WithImage("gvenzl/oracle-free", "23.26-slim-faststart");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", " 23.26.3");""")]
    [InlineData(""".WithImage("gvenzl/oracle-free", OracleTag);""")]            // not a literal
    [InlineData(""".WithImageTag("latest");""")]
    [InlineData(""".WithImageTag(tag);""")]
    public void A_floating_or_unreadable_tag_is_refused(string source)
    {
        var verdict = Assert.Single(Judge(source));

        Assert.NotNull(verdict.Refusal);
    }
}
