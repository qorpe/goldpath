using Xunit;

namespace Goldpath.Cli.Tests;

/// <summary>
/// <c>goldpath spec ready</c> (goldpath-authoring RFC §5, Slice 0). The engine's half (V1 shape,
/// V2 proposed manifest) is a process call the fake runner records; the half no schema can
/// see (V3, and gate #1) is pinned here against the three planted defects the RFC names —
/// a duplicate id, a dangling source line, a self-approval — and the rest of its checks.
/// </summary>
public class SpecReadyTests
{
    /// <summary>A repository holding one clean proposal: a request, a rule, a term, a spec and its decisions.</summary>
    private sealed class AuthoringRepo : IDisposable
    {
        public AuthoringRepo(bool clean = true)
        {
            Root = Path.Combine(Path.GetTempPath(), $"goldpath-spec-ready-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            if (!clean)
            {
                return;
            }

            Write("docs/requests/2026-10-20-beneficiary-removal.md", """
                # Request
                A treasurer can remove a saved beneficiary.
                Payments that already reference it stay intact.
                Removed beneficiaries no longer appear in the list.
                Nobody said whether a removal can be undone.
                """);
            Write("docs/domain/payments/rules/RULE-0001.yaml", Rule("RULE-0001", "docs/requests/2026-10-20-beneficiary-removal.md", 2, 4));
            Write("docs/domain/payments/terms/beneficiary.yaml", """
                term: Beneficiary
                context: Payments.Beneficiaries
                definition: A party a corporate customer has saved to pay again.
                aliases_in_code: [Beneficiary]
                source_ref: ["docs/requests/2026-10-20-beneficiary-removal.md:2"]
                status: proposed
                schemaVersion: 1
                """);
            Write("specs/proposals/SPEC-0001/spec.yaml", Spec("SPEC-0001", approvedBy: null, openBrackets: 1, body: "Then a restore is [OPEN — pending expert decision: who may restore]"));
            Write("specs/proposals/SPEC-0001/decisions.yaml", """
                schemaVersion: 1
                spec_id: SPEC-0001
                decisions:
                  - path: features.softDelete
                    change: rely
                    source:
                      file: docs/requests/2026-10-20-beneficiary-removal.md
                      line_start: 3
                      line_end: 4
                    opt_ins:
                      - opt_in: "mark soft-deletable entities: partial class X : ISoftDeletable (deletes become stamped updates)"
                        question: Q-001
                questions:
                  - id: Q-001
                    text: Can a removed beneficiary be restored, and by whom?
                """);
            Write("specs/proposals/SPEC-0001/manifest.proposed.yaml", "schemaVersion: 1\nkind: solution\nname: Shop\n");
        }

        public string Root { get; }

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content + "\n");
        }

        public void Delete(string relative) => File.Delete(Path.Combine(Root, relative));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string Rule(string id, string file, int start, int end) => $"""
        rule_id: {id}
        version: 1
        schemaVersion: 1
        context: Payments.Beneficiaries
        statement: Removing a beneficiary hides it from the list and keeps every payment that references it intact.
        source_ref:
          - file: {file}
            line_start: {start}
            line_end: {end}
        confidence: inferred
        """;

    private static string Spec(string id, string? approvedBy, int openBrackets, string body, string rules = "[RULE-0001]") => $"""
        spec_id: {id}
        rule_ids: {rules}
        notation: gherkin
        body: |
          Scenario: a removed beneficiary is hidden but not forgotten
            {body}
        open_brackets: {openBrackets}
        {(approvedBy is null ? "" : $"approved_by: {approvedBy}")}
        schemaVersion: 1
        """;

    private static (int ExitCode, string Output) Ready(AuthoringRepo repo, FakeProcessRunner? runner = null, params string[] extra)
    {
        var output = new StringWriter();
        var exitCode = CliRunner.Run(["spec", "ready", "--path", repo.Root, .. extra], runner ?? new FakeProcessRunner(), output, TextWriter.Null);
        return (exitCode, output.ToString());
    }

    [Fact]
    public void A_clean_proposal_is_ready_and_every_artefact_went_through_the_engine()
    {
        using var repo = new AuthoringRepo();
        var runner = new FakeProcessRunner();

        var (exitCode, output) = Ready(repo, runner);

        Assert.Equal(0, exitCode);
        Assert.Contains("── goldpath spec ready GREEN: 1 rule(s), 1 term(s), 1 spec(s), 1 decision record(s), 1 proposed manifest(s)", output, StringComparison.Ordinal);
        var validated = runner.Calls.Select(c => c.Arguments[1]).ToList();
        Assert.Equal(
            ["docs/domain/payments/rules/RULE-0001.yaml", "docs/domain/payments/terms/beneficiary.yaml", "specs/proposals/SPEC-0001/spec.yaml", "specs/proposals/SPEC-0001/decisions.yaml", "specs/proposals/SPEC-0001/manifest.proposed.yaml"],
            validated);
        Assert.All(runner.Calls, c => Assert.Equal("validate", c.Arguments[0]));
    }

    [Fact]
    public void Each_artefact_is_judged_by_its_own_pinned_schema()
    {
        using var repo = new AuthoringRepo();
        var runner = new FakeProcessRunner();

        Ready(repo, runner);

        string SchemaFor(string artefact) => Path.GetFileName(runner.Calls.Single(c => c.Arguments[1] == artefact).Arguments[3]);
        Assert.Equal("goldpath-rule.schema.v1.json", SchemaFor("docs/domain/payments/rules/RULE-0001.yaml"));
        Assert.Equal("goldpath-ledger-term.schema.v1.json", SchemaFor("docs/domain/payments/terms/beneficiary.yaml"));
        Assert.Equal("goldpath-spec.schema.v1.json", SchemaFor("specs/proposals/SPEC-0001/spec.yaml"));
        Assert.Equal("goldpath-decisions.schema.json", SchemaFor("specs/proposals/SPEC-0001/decisions.yaml"));
        Assert.Equal("goldpath-manifest.schema.v1.json", SchemaFor("specs/proposals/SPEC-0001/manifest.proposed.yaml"));
    }

    [Fact]
    public void The_extracted_schemas_are_the_pinned_bytes()
    {
        using var repo = new AuthoringRepo();
        var runner = new FakeProcessRunner();
        Ready(repo, runner);

        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        foreach (var (artefact, pinned) in new[]
        {
            ("docs/domain/payments/rules/RULE-0001.yaml", "schemas/specanchor/v1/rule.schema.v1.json"),
            ("specs/proposals/SPEC-0001/decisions.yaml", "schemas/authoring/v1/decisions.schema.json"),
        })
        {
            var extracted = runner.Calls.Single(c => c.Arguments[1] == artefact).Arguments[3];
            Assert.Equal(File.ReadAllText(Path.Combine(repositoryRoot, pinned)), File.ReadAllText(extracted));
        }
    }

    [Fact]
    public void A_proposed_manifest_is_judged_with_the_apps_own_rules()
    {
        using var repo = new AuthoringRepo();
        repo.Write(".specdrift/rules.yaml", "version: 1\nrules: []");
        var runner = new FakeProcessRunner();

        Ready(repo, runner);

        var manifestCall = runner.Calls.Single(c => c.Arguments[1] == "specs/proposals/SPEC-0001/manifest.proposed.yaml");
        Assert.Contains("--rules", manifestCall.Arguments);
        Assert.DoesNotContain(runner.Calls.Where(c => c != manifestCall), c => c.Arguments.Contains("--rules"));
    }

    [Fact]
    public void A_shape_the_engine_rejects_is_a_V1_finding_and_is_not_cross_checked()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-0001.yaml", Rule("RULE-0001", "docs/requests/missing.md", 1, 1));
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["RULE-0001.yaml"] = 1;

        var (exitCode, output) = Ready(repo, runner);

        Assert.Equal(1, exitCode);
        Assert.Contains("V1 docs/domain/payments/rules/RULE-0001.yaml: the engine rejected its shape", output, StringComparison.Ordinal);
        Assert.DoesNotContain("missing.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposed_manifest_the_engine_rejects_is_a_V2_finding()
    {
        using var repo = new AuthoringRepo();
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["manifest.proposed.yaml"] = 1;

        var (exitCode, output) = Ready(repo, runner);

        Assert.Equal(1, exitCode);
        Assert.Contains("V2 specs/proposals/SPEC-0001/manifest.proposed.yaml: the engine rejected the proposed manifest", output, StringComparison.Ordinal);
    }

    // ── the RFC's three planted defects ──────────────────────────────────────────────────

    [Fact]
    public void Planted_duplicate_id_two_contexts_allocating_the_same_rule_is_red()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/approvals/rules/RULE-0001.yaml", Rule("RULE-0001", "docs/requests/2026-10-20-beneficiary-removal.md", 2, 2));

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 RULE-0001 is declared by 2 files (docs/domain/approvals/rules/RULE-0001.yaml, docs/domain/payments/rules/RULE-0001.yaml)", output, StringComparison.Ordinal);
        Assert.Contains("── goldpath spec ready RED: 1 finding(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Planted_dangling_source_line_is_red()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-0001.yaml", Rule("RULE-0001", "docs/requests/2026-10-20-beneficiary-removal.md", 4, 40));

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 RULE-0001 source_ref[0]: points at lines 4–40 of docs/requests/2026-10-20-beneficiary-removal.md, which has 5 line(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Planted_self_approval_fails_gate_one()
    {
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0001/spec.yaml", Spec("SPEC-0001", approvedBy: "Alice", openBrackets: 0, body: "Then the beneficiary is hidden"));

        var (exitCode, output) = Ready(repo, null, "--gate", "SPEC-0001", "--author", "alice");

        Assert.Equal(1, exitCode);
        Assert.Contains("gate #1 SPEC-0001: approved by its own author (Alice) — the approval must come from someone else (ADR-0010)", output, StringComparison.Ordinal);
    }

    // ── gate #1, the rest of it ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate_one_passes_a_spec_someone_else_approved_with_no_open_bracket()
    {
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0001/spec.yaml", Spec("SPEC-0001", approvedBy: "treasury-analyst", openBrackets: 0, body: "Then the beneficiary is hidden"));

        var (exitCode, output) = Ready(repo, null, "--gate", "SPEC-0001", "--author", "alice");

        Assert.Equal(0, exitCode);
        Assert.Contains("+ gate #1 for SPEC-0001", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_one_refuses_an_unapproved_spec_and_one_with_open_brackets()
    {
        using var repo = new AuthoringRepo();

        var (exitCode, output) = Ready(repo, null, "--gate", "SPEC-0001", "--author", "alice");

        Assert.Equal(1, exitCode);
        Assert.Contains("gate #1 SPEC-0001: no approved_by", output, StringComparison.Ordinal);
        Assert.Contains("gate #1 SPEC-0001: 1 open bracket(s) remain", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_gate_an_open_spec_is_still_ready_for_review()
    {
        // Open brackets are a review-stage FACT, not a defect: only gate #1 requires zero.
        using var repo = new AuthoringRepo();

        Assert.Equal(0, Ready(repo).ExitCode);
    }

    [Fact]
    public void Gate_one_names_a_spec_that_does_not_exist()
    {
        using var repo = new AuthoringRepo();

        var (exitCode, output) = Ready(repo, null, "--gate", "SPEC-0042", "--author", "alice");

        Assert.Equal(1, exitCode);
        Assert.Contains("gate #1 SPEC-0042: no spec declares it under specs/proposals/", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_one_cannot_approve_a_spec_that_failed_its_shape()
    {
        using var repo = new AuthoringRepo();
        var runner = new FakeProcessRunner();
        runner.ExitCodeWhenArgumentsContain["spec.yaml"] = 1;

        var (_, output) = Ready(repo, runner, "--gate", "SPEC-0001", "--author", "alice");

        Assert.Contains("gate #1 SPEC-0001: its spec did not pass V1, so it cannot be approved", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_without_an_author_is_a_usage_error()
    {
        using var repo = new AuthoringRepo();

        Assert.Equal(2, Ready(repo, null, "--gate", "SPEC-0001").ExitCode);
    }

    // ── V3, the rest of it ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_rule_card_is_named_after_its_id()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-0001.yaml", Rule("RULE-0007", "docs/requests/2026-10-20-beneficiary-removal.md", 2, 2));

        var (_, output) = Ready(repo);

        Assert.Contains("V3 docs/domain/payments/rules/RULE-0001.yaml: declares RULE-0007, but a rule card is named after its id (RULE-0007.yaml)", output, StringComparison.Ordinal);
        Assert.Contains("V3 SPEC-0001 cites RULE-0001, and no rule card declares it", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_must_exist_and_live_inside_the_repository()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-0001.yaml", Rule("RULE-0001", "docs/requests/never-written.md", 1, 1));
        repo.Write("docs/domain/payments/rules/RULE-0002.yaml", Rule("RULE-0002", "../outside.md", 1, 1));
        repo.Write("docs/domain/payments/rules/RULE-0003.yaml", Rule("RULE-0003", "docs/requests/2026-10-20-beneficiary-removal.md", 3, 2));

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 RULE-0001 source_ref[0]: points at docs/requests/never-written.md, which does not exist", output, StringComparison.Ordinal);
        Assert.Contains("V3 RULE-0002 source_ref[0]: ../outside.md is outside the repository", output, StringComparison.Ordinal);
        Assert.Contains("V3 RULE-0003 source_ref[0]: line_start 3 is after line_end 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_object_source_is_refused_in_forward_authoring()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-0001.yaml", """
            rule_id: RULE-0001
            version: 1
            schemaVersion: 1
            context: Payments.Beneficiaries
            statement: Removing a beneficiary hides it.
            source_ref:
              - object: dbo.usp_RemoveBeneficiary
                kind: procedure
            confidence: inferred
            """);

        var (_, output) = Ready(repo);

        Assert.Contains("V3 RULE-0001 source_ref[0]: a database-object source cannot be resolved in forward authoring", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_term_source_names_a_file_and_lines_that_exist()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/terms/beneficiary.yaml", """
            term: Beneficiary
            context: Payments.Beneficiaries
            definition: A saved party.
            aliases_in_code: [Beneficiary]
            source_ref: ["docs/requests/2026-10-20-beneficiary-removal.md:5-9", "docs/requests/gone.md"]
            status: proposed
            schemaVersion: 1
            """);
        repo.Write("docs/domain/payments/terms/beneficiary-again.yaml", """
            term: Beneficiary
            context: Payments.Beneficiaries
            definition: The same party, defined twice.
            aliases_in_code: [Beneficiary]
            source_ref: ["docs/requests/2026-10-20-beneficiary-removal.md"]
            status: proposed
            schemaVersion: 1
            """);

        var (_, output) = Ready(repo);

        Assert.Contains("V3 term 'Beneficiary' source_ref[0]: points at lines 5–9 of docs/requests/2026-10-20-beneficiary-removal.md, which has 5 line(s)", output, StringComparison.Ordinal);
        Assert.Contains("V3 term 'Beneficiary' source_ref[1]: points at docs/requests/gone.md, which does not exist", output, StringComparison.Ordinal);
        Assert.Contains("V3 the term 'Payments.Beneficiaries / Beneficiary' is defined by 2 files", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_bracket_count_must_tell_the_truth()
    {
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0001/spec.yaml", Spec("SPEC-0001", approvedBy: null, openBrackets: 0, body: "Then a restore is [OPEN — pending expert decision]"));

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 SPEC-0001: open_brackets says 0, the body carries 1 [OPEN …] placeholder(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spec_sits_in_the_folder_named_after_it_and_its_id_is_unique()
    {
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0002/spec.yaml", Spec("SPEC-0001", approvedBy: null, openBrackets: 0, body: "Then it is hidden"));

        var (_, output) = Ready(repo);

        Assert.Contains("V3 specs/proposals/SPEC-0002/spec.yaml: declares SPEC-0001, but its proposal folder is specs/proposals/SPEC-0002", output, StringComparison.Ordinal);
        Assert.Contains("V3 SPEC-0001 is declared by 2 specs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposal_folder_without_a_spec_is_red()
    {
        using var repo = new AuthoringRepo();
        repo.Delete("specs/proposals/SPEC-0001/spec.yaml");

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 specs/proposals/SPEC-0001: a proposal without spec.yaml", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_question_a_decision_names_is_declared_and_every_source_resolves()
    {
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0001/decisions.yaml", """
            schemaVersion: 1
            spec_id: SPEC-0002
            decisions:
              - path: features.approvals
                change: ask
                question: Q-007
                source:
                  file: docs/requests/2026-10-20-beneficiary-removal.md
                  line_start: 1
                  line_end: 99
                opt_ins:
                  - opt_in: "declare ladders in AddGoldpathApprovals"
                    answer: One ladder.
                    source:
                      file: docs/requests/nowhere.md
                      line_start: 1
                      line_end: 1
            questions:
              - id: Q-001
                text: First.
              - id: Q-001
                text: Again.
            """);

        var (exitCode, output) = Ready(repo);

        Assert.Equal(1, exitCode);
        Assert.Contains("V3 specs/proposals/SPEC-0001/decisions.yaml: records decisions for SPEC-0002, but sits in specs/proposals/SPEC-0001", output, StringComparison.Ordinal);
        Assert.Contains("V3 specs/proposals/SPEC-0001/decisions.yaml: question Q-001 is declared 2 times", output, StringComparison.Ordinal);
        Assert.Contains("V3 specs/proposals/SPEC-0001/decisions.yaml decisions[0] (features.approvals): names Q-007, which the record's questions do not declare", output, StringComparison.Ordinal);
        Assert.Contains("V3 specs/proposals/SPEC-0001/decisions.yaml decisions[0] (features.approvals): points at lines 1–99", output, StringComparison.Ordinal);
        Assert.Contains("decisions[0] (features.approvals) opt-in answer: points at docs/requests/nowhere.md, which does not exist", output, StringComparison.Ordinal);
    }

    // ── ids, empty repositories ──────────────────────────────────────────────────────────

    [Fact]
    public void Next_id_is_one_above_every_id_written_anywhere()
    {
        // RULE-0002 has no card yet — a spec on this branch cites it — so it is already taken.
        using var repo = new AuthoringRepo();
        repo.Write("specs/proposals/SPEC-0004/spec.yaml", Spec("SPEC-0004", approvedBy: null, openBrackets: 0, body: "Then x", rules: "[RULE-0001, RULE-0002]"));

        Assert.Equal("RULE-0003", Ready(repo, null, "--next-id", "rule").Output.Trim());
        Assert.Equal("SPEC-0005", Ready(repo, null, "--next-id", "spec").Output.Trim());
    }

    [Fact]
    public void Next_id_in_an_empty_repository_starts_at_one_and_runs_no_engine()
    {
        using var repo = new AuthoringRepo(clean: false);
        var runner = new FakeProcessRunner();

        Assert.Equal("RULE-0001", Ready(repo, runner, "--next-id", "rule").Output.Trim());
        Assert.Empty(runner.Calls);
        Assert.Equal(2, Ready(repo, null, "--next-id", "term").ExitCode);
    }

    [Fact]
    public void Next_id_refuses_when_the_id_space_is_exhausted()
    {
        using var repo = new AuthoringRepo();
        repo.Write("docs/domain/payments/rules/RULE-9999.yaml", Rule("RULE-9999", "docs/requests/2026-10-20-beneficiary-removal.md", 2, 2));

        Assert.Equal(1, Ready(repo, null, "--next-id", "rule").ExitCode);
    }

    [Fact]
    public void A_repository_without_artefacts_has_nothing_to_check()
    {
        using var repo = new AuthoringRepo(clean: false);
        var runner = new FakeProcessRunner();

        var (exitCode, output) = Ready(repo, runner);

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to check", output, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Unknown_arguments_are_a_usage_error()
    {
        using var repo = new AuthoringRepo();

        Assert.Equal(2, Ready(repo, null, "--fix").ExitCode);
        Assert.Equal(2, Ready(repo, null, "--author").ExitCode);
    }
}
