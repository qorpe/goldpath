using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// The source scan RFC goldpath-oracle §2.7 asks for (G7 ORA-6, #409): hand-written SQL in <c>packages/</c> must carry
/// an Oracle branch in the method that sends it, so the next piece of raw SQL cannot forget the third provider. A plain
/// file scan, no container: it runs in the gates job's unit tests.
/// </summary>
public sealed partial class RawSqlOracleBranchTests
{
    /// <summary>
    /// Methods that send raw SQL without an Oracle branch on purpose, each with the reason. An entry that matches no
    /// call any more fails the scan, so the list cannot outlive the code it excuses.
    /// </summary>
    private static readonly Dictionary<(string File, string Method), string> Exempt = new()
    {
        [("packages/Goldpath.Messaging.Wolverine/WolverineOutbox.cs", "DeliverOneAsync")] =
            "the Wolverine adapter's outbox runs on PostgreSQL only: WolverineOutboxProviderCheck refuses any other EF provider at start",
        [("packages/Goldpath.Messaging.Wolverine/WolverineMessagingEngine.cs", "HandleOnceAsync")] =
            "the inbox lives on the same outbox context, which WolverineOutboxProviderCheck refuses at start unless it is PostgreSQL",
    };

    [Fact]
    public void Every_raw_sql_call_in_the_packages_has_an_Oracle_branch_in_its_method()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "packages"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(path => (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path)));

        var (problems, used) = Scan(files);
        problems.AddRange(Exempt.Keys.Where(key => !used.Contains(key))
            .Select(key => $"{key.File}: exemption for {key.Method} matches no raw-SQL call any more; remove it"));

        Assert.True(problems.Count == 0, "hand-written SQL without an Oracle branch (RFC goldpath-oracle §2.7):\n  " + string.Join("\n  ", problems));
    }

    [Theory]
    [InlineData("db.Database.ExecuteSqlRaw(\"DELETE FROM t\");")]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"DELETE FROM t\", ct);")]
    [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"DELETE FROM t WHERE id = {id}\", ct);")]
    [InlineData("await db.Database.ExecuteSqlAsync($\"DELETE FROM t WHERE id = {id}\", ct);")]
    [InlineData("db.Set<T>().FromSqlRaw(\"SELECT * FROM t\").ToList();")]
    [InlineData("db.Set<T>().FromSqlInterpolated($\"SELECT * FROM t\").ToList();")]
    [InlineData("db.Set<T>().FromSql($\"SELECT * FROM t\").ToList();")]
    [InlineData("db.Database.SqlQueryRaw<int>(\"SELECT 1\").ToList();")]
    [InlineData("db.Database.SqlQuery<int>($\"SELECT 1\").ToList();")]
    [InlineData("command.CommandText = \"SELECT 1\";")]
    [InlineData("db.Database . ExecuteSqlRaw (\"DELETE FROM t\");")]
    public void An_unbranched_raw_sql_call_is_red(string call)
    {
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method(call))]);
        Assert.Contains(problems, p => p.Contains("packages/Planted/Planted.cs", StringComparison.Ordinal) && p.Contains("Run", StringComparison.Ordinal));
    }

    [Fact]
    public void A_migration_builder_Sql_call_without_an_Oracle_branch_is_red()
    {
        var source = "using Microsoft.EntityFrameworkCore.Migrations;\n" + Method("migrationBuilder.Sql(\"UPDATE t SET a = 1\");", "MigrationBuilder migrationBuilder");
        var (problems, _) = Scan([("packages/Planted/Planted.cs", source)]);
        Assert.Single(problems);
    }

    [Fact]
    public void A_Sql_call_in_a_file_that_never_names_a_MigrationBuilder_is_not_raw_sql()
    {
        // Something else's Sql(...), a fluent builder say; the scan names MigrationBuilder's, as the RFC does.
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method("builder.Sql(\"x\");"))]);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("if (provider.Contains(\"Oracle\", StringComparison.Ordinal)) { db.Database.ExecuteSqlRaw(\"DELETE FROM t\"); }")]
    [InlineData("var sql = provider.StartsWith(\"Oracle.EntityFrameworkCore\") ? \"a\" : \"b\"; db.Database.ExecuteSqlRaw(sql);")]
    [InlineData("var sql = provider == \"Oracle.EntityFrameworkCore\" ? \"a\" : \"b\"; db.Database.ExecuteSqlRaw(sql);")]
    public void A_raw_sql_call_whose_method_checks_for_Oracle_is_green(string body)
    {
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method(body))]);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("// provider.Contains(\"Oracle\")\n db.Database.ExecuteSqlRaw(\"DELETE FROM t\");")]
    [InlineData("/* if (provider.Contains(\"Oracle\")) */ db.Database.ExecuteSqlRaw(\"DELETE FROM t\");")]
    [InlineData("throw new NotSupportedException(\"no Oracle form\"); db.Database.ExecuteSqlRaw(\"DELETE FROM t\");")]
    public void A_comment_or_a_message_naming_Oracle_is_not_a_branch(string body)
    {
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method(body))]);
        Assert.Single(problems);
    }

    [Fact]
    public void An_Oracle_branch_in_another_method_does_not_cover_the_call()
    {
        var source = """
            namespace P;
            public static class C
            {
                public static bool IsOracle(string provider) => provider.Contains("Oracle", StringComparison.Ordinal);

                public static void Run(Db db)
                {
                    db.Database.ExecuteSqlRaw("DELETE FROM t");
                }
            }
            """;
        var (problems, _) = Scan([("packages/Planted/Planted.cs", source)]);
        Assert.Contains(problems, p => p.Contains("Run", StringComparison.Ordinal));
    }

    [Fact]
    public void An_expression_bodied_member_is_judged_by_its_own_statement_not_by_the_file()
    {
        var source = """
            namespace P;
            public static class C
            {
                public static bool IsOracle(string provider) => provider.Contains("Oracle", StringComparison.Ordinal);

                public static int Purge(Db db) => db.Database.ExecuteSqlRaw("DELETE FROM t");
            }
            """;
        var (problems, _) = Scan([("packages/Planted/Planted.cs", source)]);
        Assert.Contains(problems, p => p.Contains("in Purge", StringComparison.Ordinal));
    }

    [Fact]
    public void A_primary_constructor_type_is_not_mistaken_for_the_member_that_holds_the_call()
    {
        var source = """
            namespace P;
            public sealed class C(Db db)
            {
                public bool IsOracle(string provider) => provider.Contains("Oracle", StringComparison.Ordinal);

                public int Purge() => db.Database.ExecuteSqlRaw("DELETE FROM t");
            }
            """;
        var (problems, _) = Scan([("packages/Planted/Planted.cs", source)]);
        Assert.Contains(problems, p => p.Contains("in Purge", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_initializer_is_not_mistaken_for_the_member_that_holds_the_call()
    {
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method("var r = new Result(1) { Rows = db.Database.ExecuteSqlRaw(\"x\") };"))]);
        Assert.Contains(problems, p => p.Contains("in Run", StringComparison.Ordinal));
    }

    [Fact]
    public void A_raw_string_holding_braces_does_not_move_the_method_boundary()
    {
        var source = Method("var json = \"\"\"{ \"a\": 1\"\"\"; var v = @\"}\"; var c = '{'; db.Database.ExecuteSqlRaw(\"x\");")
            + "\npublic static class D { public static bool O(string p) => p.Contains(\"Oracle\"); }";
        var (problems, _) = Scan([("packages/Planted/Planted.cs", source)]);
        Assert.Contains(problems, p => p.Contains("in Run", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_inside_a_lambda_is_judged_by_the_method_that_holds_the_lambda()
    {
        var unbranched = Method("Func<Task> f = async () => { await db.Database.ExecuteSqlRawAsync(\"DELETE FROM t\"); }; await f();");
        Assert.Single(Scan([("packages/Planted/Planted.cs", unbranched)]).Problems);

        var branched = Method("if (!provider.Contains(\"Oracle\")) { return; } Func<Task> f = async () => { await db.Database.ExecuteSqlRawAsync(\"x\"); }; await f();");
        Assert.Empty(Scan([("packages/Planted/Planted.cs", branched)]).Problems);
    }

    [Fact]
    public void A_raw_sql_name_inside_a_string_or_a_comment_is_not_a_call()
    {
        var (problems, _) = Scan([("packages/Planted/Planted.cs", Method("var help = \"call ExecuteSqlRaw( yourself\"; // FromSqlRaw(\n"))]);
        Assert.Empty(problems);
    }

    [Fact]
    public void An_exempted_method_is_green_and_recorded_as_used()
    {
        var (problems, used) = Scan([("packages/Goldpath.Messaging.Wolverine/WolverineOutbox.cs", Method("db.Set<T>().FromSqlRaw(\"x\");", name: "DeliverOneAsync"))]);
        Assert.Empty(problems);
        Assert.Contains(("packages/Goldpath.Messaging.Wolverine/WolverineOutbox.cs", "DeliverOneAsync"), used);
    }

    private static string Method(string body, string parameters = "Db db, string provider", string name = "Run") => $$"""
        namespace P;
        public static class C
        {
            public static async Task {{name}}({{parameters}})
            {
                {{body}}
            }
        }
        """;

    /// <summary>Scans the files; returns each unbranched call, and the exemptions that matched one.</summary>
    internal static (List<string> Problems, HashSet<(string File, string Method)> Used) Scan(IEnumerable<(string Path, string Text)> files)
    {
        var problems = new List<string>();
        var used = new HashSet<(string, string)>();
        foreach (var (path, text) in files)
        {
            var code = BlankCommentsAndStrings(text, out var withStrings);
            var migrations = code.Contains("MigrationBuilder", StringComparison.Ordinal);
            foreach (Match call in RawSqlCall().Matches(code))
            {
                if (call.Groups["sql"].Success && !migrations)
                {
                    continue;
                }

                var (method, start, end) = EnclosingMethod(code, call.Index);
                if (OracleBranch().IsMatch(withStrings.AsSpan(start, end - start)))
                {
                    continue;
                }

                if (Exempt.ContainsKey((path, method)))
                {
                    used.Add((path, method));
                    continue;
                }

                var line = code.AsSpan(0, call.Index).Count('\n') + 1;
                problems.Add($"{path}:{line}: {call.Value.Trim()} in {method} — give the method an Oracle branch (a provider check containing \"Oracle\"), or exempt it here with its reason");
            }
        }

        return (problems, used);
    }

    /// <summary>
    /// The innermost enclosing block whose header reads as a method, constructor, accessor or local function: blocks of
    /// statements and lambdas are walked out of, so a call is judged by the member that holds it.
    /// </summary>
    private static (string Name, int Start, int End) EnclosingMethod(string code, int index)
    {
        var opens = new Stack<int>();
        var blocks = new List<(int Open, int Close)>();
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                opens.Push(i);
            }
            else if (code[i] == '}' && opens.Count > 0)
            {
                var open = opens.Pop();
                if (open < index && i > index)
                {
                    blocks.Add((open, i));
                }
            }
        }

        // Innermost first: the blocks containing the call closed in that order.
        foreach (var (open, close) in blocks)
        {
            var header = Header(code, open);
            var member = MemberHeader().Match(header);
            if (member.Success && !StatementKeyword().IsMatch(member.Groups["name"].Value) && !TypeDeclaration().IsMatch(header))
            {
                return (member.Groups["name"].Value, open, close);
            }
        }

        // No block: an expression-bodied member (`Run(...) => ...;`). Its range is its own statement, never the file.
        var start = code.LastIndexOfAny([';', '{', '}'], index) + 1;
        var stop = code.IndexOf(';', index);
        stop = stop < 0 ? code.Length : stop;
        var bodied = ExpressionBodiedHeader().Match(code[start..index]);
        return (bodied.Success ? bodied.Groups["name"].Value : "(field initializer)", start, stop);
    }

    /// <summary>The text between the previous statement or block boundary and a <c>{</c>.</summary>
    private static string Header(string code, int open)
    {
        var start = code.LastIndexOfAny([';', '{', '}'], Math.Max(0, open - 1));
        return code[(start + 1)..open].Trim();
    }

    /// <summary>
    /// The source with comments blanked, and with string and character contents blanked too in the first copy (the one
    /// calls and blocks are found in); offsets are preserved. <paramref name="withStrings"/> keeps the strings, which is where a provider check's
    /// "Oracle" lives.
    /// </summary>
    private static string BlankCommentsAndStrings(string text, out string withStrings)
    {
        var code = new StringBuilder(text);
        var strings = new StringBuilder(text);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                Blank(code, i, end);
                Blank(strings, i, end);
                i = end;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(code, i, end);
                Blank(strings, i, end);
                i = end;
            }
            else if (text[i] == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var quotes = 3;
                while (i + quotes < text.Length && text[i + quotes] == '"')
                {
                    quotes++;
                }

                var fence = new string('"', quotes);
                var end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + quotes;
                Blank(code, i + quotes, end - quotes);
                i = end;
            }
            else if (text[i] == '"')
            {
                var verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
                var end = i + 1;
                while (end < text.Length && (verbatim || text[end] != '\n'))
                {
                    if (text[end] == '"' && !(verbatim && end + 1 < text.Length && text[end + 1] == '"'))
                    {
                        break;
                    }

                    end += text[end] == '\\' && !verbatim ? 2 : text[end] == '"' ? 2 : 1;
                }

                Blank(code, i + 1, Math.Min(end, text.Length));
                i = Math.Min(end + 1, text.Length);
            }
            else if (text[i] == '\'' && i + 2 < text.Length)
            {
                var end = text[i + 1] == '\\' ? text.IndexOf('\'', i + 3) : i + 2;
                end = end < 0 ? text.Length - 1 : end;
                Blank(code, i + 1, end);
                i = end + 1;
            }
            else
            {
                i++;
            }
        }

        withStrings = strings.ToString();
        return code.ToString();
    }

    /// <summary>Blanks a span, newlines kept, so offsets and line numbers do not move.</summary>
    private static void Blank(StringBuilder text, int start, int end)
    {
        for (var i = start; i < end && i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                text[i] = ' ';
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Goldpath.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // EF's raw-SQL entry points (each sync and async form), ADO.NET's CommandText, and MigrationBuilder.Sql — the last
    // only in a file that names MigrationBuilder (a source scan cannot read the receiver's type).
    [GeneratedRegex(@"\b(?:ExecuteSql(?:Raw|Interpolated)?(?:Async)?|FromSql(?:Raw|Interpolated)?|SqlQuery(?:Raw)?)\s*(?:<[^>()]*>)?\s*\(|\.\s*CommandText\s*=(?!=)|\.\s*(?<sql>Sql)\s*\(")]
    private static partial Regex RawSqlCall();

    // "Oracle branch", as the R2 backfill spells it: a provider check against a literal containing Oracle.
    [GeneratedRegex(@"(?:\.(?:Contains|StartsWith|EndsWith|Equals)\s*\(\s*|==\s*)""[^""]*Oracle[^""]*""")]
    private static partial Regex OracleBranch();

    [GeneratedRegex(@"(?<!\bnew\s+)(?<!\.)\b(?<name>[A-Za-z_]\w*)\s*(?:<[^<>(){};]*>)?\s*\([^;{}]*\)\s*(?:where\b[^{;]*)?(?::\s*(?:base|this)\s*\([^;{}]*\)\s*)?$|\b(?<name>get|set|init)$")]
    private static partial Regex MemberHeader();

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*(?:<[^<>(){};]*>)?\s*\([^;{}]*\)\s*=>")]
    private static partial Regex ExpressionBodiedHeader();

    [GeneratedRegex(@"^(?:if|for|foreach|while|using|switch|catch|lock|fixed|return|await|nameof|typeof|when|else|delegate)$")]
    private static partial Regex StatementKeyword();

    // A type with a primary constructor (`class C(DbContext db)`) has a method-shaped header; it is never the member.
    [GeneratedRegex(@"\b(?:class|record|struct|interface)\b")]
    private static partial Regex TypeDeclaration();
}
