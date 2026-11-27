namespace Goldpath.Cli;

/// <summary>
/// <c>goldpath new solution|worker ...</c> — a strict passthrough to <c>dotnet new</c> with the
/// golden template names, so teams learn ONE entry point. All template arguments
/// (<c>--features</c>, <c>--trigger</c>, <c>--db</c>...) flow through untouched.
/// </summary>
public static class NewCommand
{
    /// <summary>Maps the kind to its template and delegates to dotnet new.</summary>
    public static int Run(string kind, IReadOnlyList<string> rest, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        if (kind is "service" or "gateway")
        {
            // Every other verb errors on a flag it does not know; these two took --path and
            // dropped the rest silently, so `goldpath new service Billing --db sqlserver`
            // generated a head on the WRONG database without a word (preview.8 audit).
            RejectUnknownFlags(kind, rest);
        }

        if (kind is "service")
        {
            var serviceName = FirstBareToken(rest)
                ?? throw new CliUsageException("goldpath new service needs a name: goldpath new service <Name>");
            return NewServiceCommand.RunService(serviceName, FindFlagValue(rest, "--path") ?? Directory.GetCurrentDirectory(), runner, output, error);
        }

        if (kind is "gateway")
        {
            return NewServiceCommand.RunGateway(FindFlagValue(rest, "--path") ?? Directory.GetCurrentDirectory(), runner, output, error);
        }

        var template = kind switch
        {
            "solution" => "goldpath-solution",
            "worker" => "goldpath-worker",
            _ => throw new CliUsageException($"unknown kind '{kind}' — goldpath new solution|worker|service|gateway"),
        };

        if (kind is "solution")
        {
            RefuseOracleWithBroker(rest);
        }

        var exitCode = runner.Run("dotnet", ["new", template, .. rest], Directory.GetCurrentDirectory());
        if (exitCode != 0)
        {
            error.WriteLine("goldpath: generation failed — if the template is missing: dotnet new install Goldpath.Templates");
            return exitCode;
        }

        // Migrations RFC D2: the template cannot carry one Initial migration per feature
        // combination — the CLI generates it against the just-composed model. Best effort:
        // a feed-less restore may legitimately fail here; generation still succeeded.
        var appRoot = OutputDirectory(rest);
        try
        {
            // db init owns the first-contract commit too (#32) — it owns the build moment.
            DbCommand.Run("init", null, appRoot, runner, output, error);
        }
        catch (CliFailureException exception)
        {
            output.WriteLine($"goldpath: generated, but the Initial migration is pending ({exception.Message})");
            output.WriteLine("goldpath: wire your package feed (nuget.config), then run: goldpath db init");
        }

        return 0;
    }

    private static string OutputDirectory(IReadOnlyList<string> rest)
        => ResolveAppRoot(rest, Directory.GetCurrentDirectory());

    /// <summary>
    /// Where the generated app lives: <c>-o</c> when given; otherwise the template's
    /// <c>preferNameDirectory</c> put it under <c>&lt;cwd&gt;/&lt;name&gt;</c> — resolve THAT when
    /// it exists, so the post-generation db init and the first-contract commit find the
    /// app instead of silently skipping it (they searched <c>&lt;cwd&gt;/src</c> before).
    /// </summary>
    public static string ResolveAppRoot(IReadOnlyList<string> rest, string currentDirectory)
    {
        if (FindFlagValue(rest, "-o", "--output") is { } explicitOutput)
        {
            return explicitOutput;
        }

        if (FindFlagValue(rest, "-n", "--name") is { } name && Directory.Exists(Path.Combine(currentDirectory, name)))
        {
            return Path.Combine(currentDirectory, name);
        }

        return currentDirectory;
    }

    /// <summary>
    /// A service/gateway head takes ONE flag. Anything else is a typo or a template argument
    /// that does not apply to a head, and silence about it is a lie about what was generated.
    /// </summary>
    private static void RejectUnknownFlags(string kind, IReadOnlyList<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            var token = rest[i];
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (token is "--path")
            {
                i++;   // its value rides with it
                continue;
            }

            throw new CliUsageException(
                $"goldpath new {kind} does not take '{token}' — a head follows the SOLUTION's shape (database, broker, auth); the only flag is --path <dir>.");
        }
    }

    /// <summary>
    /// <c>--db oracle</c> ships broker-less first (Oracle RFC §2.9, §3): the messaging engine's
    /// outbox has no proven Oracle store, which open-threads T16 tracks. The template's DEFAULT
    /// broker is <c>rabbitmq</c>, so a bare <c>--db oracle</c> would land on exactly that
    /// unproven combination. Owner decision 2026-11-25 (#412): refuse and generate nothing,
    /// rather than switch the broker default by database. Fail-closed: the broker must READ as
    /// <c>none</c> in a spelling this method recognises; any other spelling is refused too.
    /// </summary>
    private static void RefuseOracleWithBroker(IReadOnlyList<string> rest)
    {
        var db = TemplateOptionValues(rest, "--db", "-db");
        if (!db.Any(value => string.Equals(value, "oracle", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var broker = TemplateOptionValues(rest, "--broker", "-b");
        if (broker.Count > 0 && broker.All(value => string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new CliUsageException(
            "goldpath new solution --db oracle needs --broker none: an Oracle app ships without a broker until the messaging engine has a proven Oracle outbox store (open-threads T16). Nothing was generated. Re-run with --broker none.");
    }

    /// <summary>
    /// Every value a template option was given, in the spellings <c>dotnet new</c> accepts:
    /// <c>--db oracle</c>, <c>--db=oracle</c> and <c>--db:oracle</c>, and the same for the alias
    /// the template engine prints in <c>dotnet new goldpath-solution -h</c> (<c>-db</c>, <c>-b</c>).
    /// </summary>
    private static List<string> TemplateOptionValues(IReadOnlyList<string> rest, params string[] names)
    {
        var values = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            foreach (var name in names)
            {
                var token = rest[i];
                if (string.Equals(token, name, StringComparison.Ordinal))
                {
                    values.Add(i + 1 < rest.Count ? rest[i + 1].Trim() : "");
                }
                else if (token.Length > name.Length + 1
                    && token.StartsWith(name, StringComparison.Ordinal)
                    && token[name.Length] is '=' or ':')
                {
                    values.Add(token[(name.Length + 1)..].Trim().Trim('"', '\''));
                }
            }
        }

        return values;
    }

    /// <summary>The value following the first matching flag, or null.</summary>
    private static string? FindFlagValue(IReadOnlyList<string> rest, params string[] flags)
    {
        for (var i = 0; i < rest.Count - 1; i++)
        {
            if (flags.Contains(rest[i], StringComparer.Ordinal))
            {
                return rest[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// The first token that is neither a flag nor a flag's VALUE — so
    /// `new service --path /app Billing` names Billing, whatever the order (review R3).
    /// </summary>
    private static string? FirstBareToken(IReadOnlyList<string> rest)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].StartsWith("-", StringComparison.Ordinal))
            {
                i++;   // its value rides with it
                continue;
            }

            return rest[i];
        }

        return null;
    }
}
