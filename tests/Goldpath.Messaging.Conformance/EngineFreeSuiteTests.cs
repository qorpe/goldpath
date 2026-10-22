using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The suite speaks Goldpath's vocabulary only (RFC goldpath-messaging-engines §4.3, G1 Part 2/4): a
/// second engine runs these facts unchanged only if no scenario or harness file names the first one.
/// Read from the source, because a using directive the code never exercises still binds the suite to an
/// engine and compiles cleanly.
/// </summary>
public sealed class EngineFreeSuiteTests
{
    private static readonly Regex EngineReference = new(@"^\s*(global\s+)?using\s+(static\s+)?(MassTransit|Wolverine)\b|\b(MassTransit|Wolverine)\.[A-Z]", RegexOptions.Multiline);

    [Fact]
    public void No_scenario_or_harness_file_names_an_engine()
    {
        var offenders = Directory.EnumerateFiles(SuiteDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => EngineReference.Matches(StripComments(File.ReadAllText(path)))
                .Select(match => $"{Path.GetFileName(path)}: {match.Value.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "the suite names an engine: " + string.Join("; ", offenders));
    }

    private static string SuiteDirectory([CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;

    // Comments may name the oracle ("the oracle is MassTransit 8.5.11"); code may not.
    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
}
