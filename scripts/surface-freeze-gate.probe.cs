#:project ../tools/Goldpath.Cli/Goldpath.Cli.csproj
#:property IsPackable=false

// The CLI half of the surface freeze gate (GA G4 API-10, #399). It is run by
// scripts/surface-freeze-gate.sh, and never shipped.
//
// The CLI has no parse table and no per-verb --help: each verb's flags live in hand-written
// switches, and the one --help text omits --path on four verb forms. So the probe asks the PARSER.
// Every verb form the gate read from the dispatcher is driven through CliRunner.Run with every
// flag literal in the CLI's source, against a process runner that runs nothing, in an empty
// working directory, with an empty stdin. A flag is the verb's when the parser does not reject it
// the way it rejects a flag nobody declared: the control is the same call with a sentinel flag of
// the same shape ("--x" or "-x"), and "rejected" means the same exit code and the same message
// with the sentinel's name in the flag's place. A literal the CLI only passes to dotnet or specdrift (--nologo, --startup-project)
// is then no verb's, which a grep could not tell. A verb that accepts the sentinel too hands its
// arguments on unread (dotnet new) and is recorded as a pass-through, its flags being the
// template's parameters.
//
// Usage: dotnet run scripts/surface-freeze-gate.probe.cs -- <verb form>... -- <flag>...
// Prints the cli-* lines of the snapshot, unsorted.
using Goldpath.Cli;

const string LongSentinel = "--surface-freeze-probe-undeclared";
const string ShortSentinel = "-surface-freeze-probe-undeclared";
const string Value = "probe";

var separator = Array.IndexOf(args, "--");
if (separator < 0)
{
    Console.Error.WriteLine("usage: surface-freeze-gate.probe.cs <verb form>... -- <flag>...");
    return 2;
}

var verbs = args[..separator];
var flags = args[(separator + 1)..];

// A positional the parser needs before it reads any flag. Without it the verb stops at its
// own "needs a name" refusal and every flag would read as rejected.
var positionals = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["add feature"] = "caching",
    ["add worker"] = "Probe",
    ["db add"] = "Probe",
    ["new service"] = "Probe",
};

var work = Directory.CreateTempSubdirectory("goldpath-surface-probe-");
var originalDirectory = Directory.GetCurrentDirectory();
var originalOut = Console.Out;
var originalIn = Console.In;
var lines = new List<string>();
try
{
    foreach (var verb in verbs)
    {
        var prefix = new List<string>(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (positionals.TryGetValue(verb, out var positional))
        {
            prefix.Add(positional);
        }

        lines.Add($"cli-verb {verb}");

        // The bare wizard (`goldpath new`) and `help` take nothing after them: any further token
        // is another verb form, so there are no flags to ask about.
        if (verb is "new" or "help")
        {
            continue;
        }

        // One control per flag SHAPE: `db bundle [dir]` takes a token without a leading "--" as
        // its directory, so a single-dash token there is a positional, not a flag, and is not asked about.
        var controls = new Dictionary<string, (Outcome Bare, Outcome Valued)>(StringComparer.Ordinal);
        foreach (var sentinel in new[] { LongSentinel, ShortSentinel })
        {
            controls[sentinel] = (Invoke([.. prefix, sentinel]), Invoke([.. prefix, sentinel, Value]));
        }

        if (controls[LongSentinel].Bare.ExitCode != 2 && controls[LongSentinel].Valued.ExitCode != 2)
        {
            lines.Add($"cli-passthrough {verb}");
            continue;
        }

        foreach (var flag in flags)
        {
            var sentinel = flag.StartsWith("--", StringComparison.Ordinal) ? LongSentinel : ShortSentinel;
            var (bare, valued) = controls[sentinel];
            if (bare.ExitCode != 2 && valued.ExitCode != 2)
            {
                continue;
            }

            if (Accepts(Invoke([.. prefix, flag]), bare, sentinel, flag)
                || Accepts(Invoke([.. prefix, flag, Value]), valued, sentinel, flag))
            {
                lines.Add($"cli-flag {verb} {flag}");
            }
        }
    }

    // The top level: a flag that is a verb form of its own (--help, --version and their short
    // spellings). Anything else there is the dispatcher's usage error.
    foreach (var flag in flags)
    {
        if (Invoke([flag]).ExitCode != 2)
        {
            lines.Add($"cli-global {flag}");
        }
    }
}
finally
{
    Directory.SetCurrentDirectory(originalDirectory);
    Console.SetOut(originalOut);
    Console.SetIn(originalIn);
    work.Delete(recursive: true);
}

foreach (var line in lines)
{
    Console.WriteLine(line);
}

return 0;

// Rejected means: the same exit code as the sentinel and the same message once the sentinel's
// name is put in the flag's place.
static bool Accepts(Outcome outcome, Outcome control, string sentinel, string flag)
    => outcome.ExitCode != control.ExitCode
       || !string.Equals(outcome.Error, control.Error.Replace(sentinel, flag, StringComparison.Ordinal), StringComparison.Ordinal);

Outcome Invoke(string[] arguments)
{
    // Every call starts in an empty directory of its own, so one call's files cannot change
    // what the next one is told.
    var directory = work.CreateSubdirectory(Guid.NewGuid().ToString("N"));
    Directory.SetCurrentDirectory(directory.FullName);
    using var output = new StringWriter();
    using var error = new StringWriter();
    Console.SetOut(output);
    Console.SetIn(new StringReader(string.Empty));
    try
    {
        var exitCode = CliRunner.Run(arguments, new InertRunner(), output, error);
        return new Outcome(exitCode, error.ToString());
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        // Parsing comes before any command touches the file system, so an exception from the
        // command itself (a missing directory, a missing manifest) means the parser let it through.
        return new Outcome(-1, exception.GetType().FullName ?? exception.GetType().Name);
    }
    finally
    {
        Console.SetOut(originalOut);
        Directory.SetCurrentDirectory(originalDirectory);
    }
}

internal sealed record Outcome(int ExitCode, string Error);

/// <summary>Runs nothing and reports success: the probe asks the parser, not dotnet or specdrift.</summary>
internal sealed class InertRunner : IProcessRunner
{
    public int Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory) => 0;
}
