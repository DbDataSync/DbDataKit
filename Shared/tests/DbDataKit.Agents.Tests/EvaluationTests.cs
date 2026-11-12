using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents.Evaluation;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>
/// Plan 27a AG23: evaluation sets. A case is read strictly (<c>evaluation/1</c>); the suite decides the tools called, the answer's text, a check of the host's and the rows of the agent's query against a verified query; a judge model reads only a
/// rubric, and what it says is a judgement apart from the measurements; a run records the agent version, the knowledge versions given, the model, the score per case and the cost; a promotion below the minimum is refused naming the score.
/// </summary>
public sealed class EvaluationTests
{
    private const string Case = """
        evaluation: 1
        name: revenue-by-region
        task: What was revenue by region last quarter?
        fixtures:
          note: Use the Sales model.
        pass-at: 0.8
        expect:
          - tools-called: [semantic_query]
          - tools-not-called: connection_deploy
          - answer-contains: EMEA
          - answer-matches: '\d+(\.\d+)?'
          - rows-match-verified-query: queries/revenue-by-region.md
          - check: workspace-compiles
          - rubric: The answer says which period the figures are for.
        """;

    private static EvaluationCase Read(string text = Case)
    {
        var result = EvaluationCaseLoader.Load(text, "agents/sales-helper/tests/evaluation/revenue-by-region.yml");
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Case!;
    }

    // ---- the case file ----

    [Fact]
    public void A_case_is_read_with_its_task_fixtures_and_every_kind_of_check()
    {
        var c = Read();

        Assert.Equal(("revenue-by-region", 0.8), (c.Name, c.PassAt));
        Assert.Equal("What was revenue by region last quarter?", c.Task);
        Assert.Equal([new KeyValuePair<string, string>("note", "Use the Sales model.")], c.Fixtures);
        Assert.Equal(EvaluationCaseLoader.CheckKinds, c.Expect.Select(e => e.Kind));
        Assert.Equal(["semantic_query"], c.Expect[0].Values);
        Assert.True(c.Expect[6].IsJudged);
        Assert.All(c.Expect.Take(6), e => Assert.False(e.IsJudged));
    }

    [Fact]
    public void The_name_is_the_files_when_left_out()
    {
        var c = Read("evaluation: 1\ntask: Ask\nexpect:\n  - answer-contains: x");

        Assert.Equal("revenue-by-region", c.Name);
        Assert.Equal(1.0, c.PassAt);
    }

    [Theory]
    [InlineData("evaluation: 1\nexpect:\n  - answer-contains: x", 1, "Required key `task`")]
    [InlineData("evaluation: 1\ntask: t", 1, "Required key `expect`")]
    [InlineData("evaluation: 1\ntask: t\nexpect:\n  - answer-contain: x", 4, "Did you mean `answer-contains`")]
    [InlineData("evaluation: 1\ntask: t\nexpect:\n  - answer-matches: '('", 4, "regular expression")]
    [InlineData("evaluation: 1\ntask: t\nexpect:\n  - answer-contains: x\n    rubric: y", 4, "one key")]
    [InlineData("evaluation: 1\ntask: t\npass-at: 2\nexpect:\n  - answer-contains: x", 3, "above 0 and at most 1")]
    [InlineData("evaluation: 1\ntask: t\nextra: 1\nexpect:\n  - answer-contains: x", 3, "Unknown key")]
    [InlineData("evaluation: 1\ntask: t\nexpect: []", 3, "one to 50 checks")]
    public void A_bad_case_is_refused_with_file_line_and_a_suggestion(string text, int line, string words)
    {
        var result = EvaluationCaseLoader.Load(text, "case.yml");

        Assert.False(result.Ok);
        Assert.Null(result.Case);
        var error = result.Diagnostics.First(d => d.Severity == ConfigSeverity.Error);
        Assert.Equal("case.yml", error.Path);
        Assert.Equal(line, error.Line);
        Assert.Contains(words, error.Message + " " + error.Fix);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_file_and_both_numbers()
    {
        var result = EvaluationCaseLoader.Load("evaluation: 2\ntask: t\nexpect:\n  - answer-contains: x", "tests/evaluation/a.yml");

        var error = Assert.Single(result.Diagnostics);
        Assert.Contains("tests/evaluation/a.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("format 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("format 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_names_the_keys_and_check_kinds_the_reader_reads()
    {
        using var schema = JsonDocument.Parse(EvaluationCaseLoader.Schema());
        var properties = schema.RootElement.GetProperty("properties");

        Assert.Equal(EvaluationCaseLoader.CaseKeys.Order(), properties.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(EvaluationCaseLoader.CheckKinds.Order(), properties.GetProperty("expect").GetProperty("items").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
    }

    // ---- running a set ----

    private sealed class Host(Func<EvaluationCase, EvaluationAgentRun> run) : IEvaluationHost
    {
        public JudgeVerdict? Verdict { get; set; }

        public bool? WorkspaceCompiles { get; set; } = true;

        public RowSet? Verified { get; set; }

        public List<string> Judged { get; } = [];

        public Task<EvaluationAgentRun> RunAsync(EvaluationCase evaluation, CancellationToken cancellation) => Task.FromResult(run(evaluation));

        public Task<JudgeVerdict?> JudgeAsync(string rubric, EvaluationCase evaluation, string answer, CancellationToken cancellation)
        {
            Judged.Add(rubric);
            return Task.FromResult(Verdict);
        }

        public Task<bool?> CheckAsync(string name, EvaluationCase evaluation, EvaluationAgentRun run1, CancellationToken cancellation) => Task.FromResult(name == "workspace-compiles" ? WorkspaceCompiles : null);

        public Task<RowSet?> VerifiedRowsAsync(string id, CancellationToken cancellation) => Task.FromResult(id == "queries/revenue-by-region.md" ? Verified : null);
    }

    private static string Rows(params object[][] rows) =>
        new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["columns"] = new JsonArray(new JsonObject { ["name"] = "region", ["type"] = "string" }, new JsonObject { ["name"] = "revenue", ["type"] = "double" }),
                ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonArray([.. r.Select(c => JsonValue.Create(c))]))]),
            },
        }.ToJsonString();

    private static readonly RowSet VerifiedRows = new(["region", "revenue"], [["EMEA", 120.5], ["APAC", 80.0]]);

    private static EvaluationAgentRun Good(string answer = "For Q3 2026: EMEA 120.5, APAC 80.", params RecordedToolCall[] calls) =>
        new(answer, calls.Length > 0 ? calls : [new RecordedToolCall("semantic_query", "{}", Rows(["EMEA", 120.5], ["APAC", 80.0]), false)], 1200, 300, 0.0123m, "anthropic/claude", ["policy/refunds.md@v3", "queries/revenue-by-region.md@v1"]);

    [Fact]
    public async Task A_case_scores_by_what_the_suite_decides_and_what_the_judge_says_and_the_run_records_the_five_facts()
    {
        var host = new Host(_ => Good()) { Verified = VerifiedRows, Verdict = new JudgeVerdict(0.5, "It gives the figures but not the period.") };

        var run = await EvaluationRunner.RunAsync([Read()], host, "sales-helper", "3", "test", "schedule", new FixedClock());

        var result = Assert.Single(run.Cases);
        Assert.Equal(7, result.Checks.Count);
        // six checks of the suite hold (1 each) and the judge gave 0.5: the mean of seven
        Assert.Equal(Math.Round((6 + 0.5) / 7, 4), result.Score);
        Assert.True(result.Passed);                                   // 0.93 against pass-at 0.8
        var judged = result.Checks.Single(c => c.Method == "judge");
        Assert.Equal(("rubric", 0.5, "It gives the figures but not the period."), (judged.Kind, judged.Score, judged.Judgement));
        Assert.All(result.Checks.Where(c => c.Method == "suite"), c => Assert.Null(c.Judgement));
        Assert.Equal(["The answer says which period the figures are for."], host.Judged);

        // the five facts: the agent version, the knowledge versions given, the model, the score per case, the cost
        Assert.Equal(("sales-helper", "3", "test", "anthropic/claude", "schedule"), (run.Agent, run.AgentVersion, run.Channel, run.Model, run.Trigger));
        Assert.Equal(["policy/refunds.md@v3", "queries/revenue-by-region.md@v1"], run.KnowledgeVersions);
        Assert.Equal(("revenue-by-region", result.Score), (run.Cases[0].Case, run.Cases[0].Score));
        Assert.Equal(0.0123m, run.Cost);
        Assert.Equal((1, 1, 0), (run.Passed, run.Total, run.NotJudged));
        Assert.Equal(result.Score, run.Score);
    }

    [Fact]
    public async Task A_verified_query_case_passes_and_fails_by_comparing_rows_whatever_their_order_and_the_way_a_number_is_written()
    {
        var onlyRows = Read("evaluation: 1\ntask: t\nexpect:\n  - rows-match-verified-query: queries/revenue-by-region.md");
        var host = new Host(_ => Good(calls: new RecordedToolCall("semantic_query", "{}", Rows(["APAC", "80"], ["EMEA", 120.5000001]), false))) { Verified = VerifiedRows };

        var same = (await EvaluationRunner.RunAsync([onlyRows], host, "a", "1", "test")).Cases.Single();

        Assert.True(same.Passed, same.Checks[0].Message);
        Assert.Equal(1.0, same.Score);

        var different = new Host(_ => Good(calls: new RecordedToolCall("semantic_query", "{}", Rows(["EMEA", 120.5], ["APAC", 79.0]), false))) { Verified = VerifiedRows };
        var wrong = (await EvaluationRunner.RunAsync([onlyRows], different, "a", "1", "test")).Cases.Single();
        Assert.False(wrong.Passed);
        Assert.Equal(0.0, wrong.Score);
        Assert.Contains("rows differ", wrong.Checks[0].Message, StringComparison.Ordinal);

        var fewer = new Host(_ => Good(calls: new RecordedToolCall("semantic_query", "{}", Rows(["EMEA", 120.5]), false))) { Verified = VerifiedRows };
        Assert.Contains("1 rows and the verified one 2", (await EvaluationRunner.RunAsync([onlyRows], fewer, "a", "1", "test")).Cases.Single().Checks[0].Message, StringComparison.Ordinal);

        var noQuery = new Host(_ => Good(calls: new RecordedToolCall("semantic_describe", "{}", "no rows here", false))) { Verified = VerifiedRows };
        Assert.Contains("ran no query", (await EvaluationRunner.RunAsync([onlyRows], noQuery, "a", "1", "test")).Cases.Single().Checks[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_are_compared_by_column_name_in_any_order_and_a_missing_or_extra_column_is_said()
    {
        var expected = new RowSet(["region", "revenue"], [["EMEA", 1.0]]);

        Assert.Null(RowSets.Difference(expected, new RowSet(["Revenue", "REGION"], [[1.0, "EMEA"]])));
        Assert.Contains("missing: revenue", RowSets.Difference(expected, new RowSet(["region"], [["EMEA"]]))!, StringComparison.Ordinal);
        Assert.Contains("extra: margin", RowSets.Difference(expected, new RowSet(["region", "revenue", "margin"], [["EMEA", 1.0, 0.1]]))!, StringComparison.Ordinal);
        Assert.Null(RowSets.Difference(new RowSet(["a"], [[null]]), new RowSet(["a"], [[null]])));
        Assert.NotNull(RowSets.Difference(new RowSet(["a"], [[null]]), new RowSet(["a"], [["null"]])));
    }

    [Fact]
    public async Task A_tool_not_called_or_called_in_error_and_a_text_missing_from_the_answer_fail_their_checks_and_say_why()
    {
        var host = new Host(_ => Good("Revenue is high.", new RecordedToolCall("semantic_query", "{}", "error", true), new RecordedToolCall("connection_deploy", "{}", "ok", false))) { Verified = VerifiedRows, Verdict = new JudgeVerdict(1, "fine") };

        var result = (await EvaluationRunner.RunAsync([Read()], host, "a", "1", "test")).Cases.Single();

        string Message(string kind) => result.Checks.First(c => c.Kind == kind).Message;
        Assert.Contains("Not called: semantic_query", Message("tools-called"), StringComparison.Ordinal);   // a call that failed does not count as called
        Assert.Contains("Called, and should not have been: connection_deploy", Message("tools-not-called"), StringComparison.Ordinal);
        Assert.Equal("The answer does not say it.", Message("answer-contains"));
        Assert.Equal("The answer does not match.", Message("answer-matches"));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task With_no_judge_a_rubric_is_not_judged_left_out_of_the_score_and_counted()
    {
        var host = new Host(_ => Good()) { Verified = VerifiedRows, Verdict = null };

        var run = await EvaluationRunner.RunAsync([Read()], host, "a", "1", "test");

        var result = run.Cases.Single();
        var rubric = result.Checks.Single(c => c.Kind == "rubric");
        Assert.Null(rubric.Score);
        Assert.Null(rubric.Passed);
        Assert.Contains("No judge model is bound", rubric.Message, StringComparison.Ordinal);
        Assert.Equal(1.0, result.Score);                     // the six checks of the suite hold; the rubric is not part of the score
        Assert.Equal(1, run.NotJudged);
    }

    [Fact]
    public async Task A_check_the_host_does_not_know_is_not_decided_and_a_case_that_cannot_be_run_scores_nothing_and_the_set_goes_on()
    {
        var unknown = Read("evaluation: 1\nname: a\ntask: t\nexpect:\n  - check: coverage-above-80");
        var broken = Read("evaluation: 1\nname: b\ntask: t\nexpect:\n  - answer-contains: x");
        var fine = Read("evaluation: 1\nname: c\ntask: t\nexpect:\n  - answer-contains: EMEA");
        var host = new Host(c => c.Name == "b" ? throw new InvalidOperationException("the model is down") : Good());

        var run = await EvaluationRunner.RunAsync([unknown, broken, fine], host, "a", "1", "test");

        Assert.Equal([0.0, 0.0, 1.0], run.Cases.Select(c => c.Score));
        Assert.Contains("every check needed something this host does not have", run.Cases[0].Problem, StringComparison.Ordinal);
        Assert.Contains("the model is down", run.Cases[1].Problem, StringComparison.Ordinal);
        Assert.Equal((1, 3), (run.Passed, run.Total));
        Assert.Equal(Math.Round(1.0 / 3, 4), run.Score);
    }

    // ---- the gate ----

    [Theory]
    [InlineData(null, 0.5, null)]                                                                            // no minimum: nothing is refused
    [InlineData(0.8, 0.85, null)]
    [InlineData(0.8, 0.8, null)]
    [InlineData(0.8, 0.62, "its last evaluation scored 0.62 (5 of 8 cases passed), below the minimum of 0.80 set for prod")]
    [InlineData(0.8, null, "has not been evaluated there")]
    public void A_promotion_below_the_minimum_is_refused_naming_the_score(double? minimum, double? score, string? words)
    {
        var refusal = EvaluationGate.Refusal("sales-helper", "3", "prod", (decimal?)minimum, (decimal?)score, 5, 8);

        if (words is null) Assert.Null(refusal);
        else
        {
            Assert.StartsWith("sales-helper 3 cannot be promoted to prod", refusal, StringComparison.Ordinal);
            Assert.Contains(words, refusal, StringComparison.Ordinal);
        }
    }

    // ---- the host over the runner ----

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-12T09:00:00Z");
    }

    [Fact]
    public async Task The_runner_host_puts_the_case_to_the_agent_records_its_tool_calls_with_their_results_and_the_usage()
    {
        var query = new AgentTool("semantic_query", "Run a query", Traits.ReadsData, new JsonObject { ["type"] = "object" }, "command semantic query", (_, _) => Task.FromResult(new ToolOutcome(Rows(["EMEA", 120.5], ["APAC", 80.0]), false)));
        var client = new ScriptedChatClient(ScriptStep.Call("semantic_query", new { }).WithUsage(500, 40), ScriptStep.Say("For Q3: EMEA 120.5 and APAC 80.").WithUsage(600, 60));
        var host = new AgentEvaluationHost(c => new EvaluationAgent(client, [query], new AgentRunOptions(), new AgentRunContext("evaluation", "sales-helper", "3"), "fake/model", ["policy/refunds.md@v3"], u => u.Tokens * 0.00001m),
            verifiedRows: (_, _) => Task.FromResult<RowSet?>(VerifiedRows));

        var run = await EvaluationRunner.RunAsync([Read("evaluation: 1\ntask: Revenue by region?\nfixtures:\n  note: Sales.\nexpect:\n  - tools-called: semantic_query\n  - answer-contains: EMEA\n  - rows-match-verified-query: queries/revenue-by-region.md")], host, "sales-helper", "3", "test");

        var result = run.Cases.Single();
        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => c.Message)));
        Assert.Equal((1100L, 100L), (result.InputTokens, result.OutputTokens));
        Assert.Equal(0.012m, result.Cost);
        Assert.Equal("fake/model", run.Model);
        Assert.Equal(["policy/refunds.md@v3"], run.KnowledgeVersions);
        // the agent was put the task and the fixture the way a person would
        var asked = client.Requests[0].Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Revenue by region?", asked, StringComparison.Ordinal);
        Assert.Contains("[note]\nSales.", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_stops_before_an_answer_is_a_problem_of_the_case()
    {
        var client = new ScriptedChatClient(ScriptStep.Refuse());
        var host = new AgentEvaluationHost(_ => new EvaluationAgent(client, [], new AgentRunOptions(), new AgentRunContext("evaluation", "a", "1"), "fake/model", []));

        var result = (await EvaluationRunner.RunAsync([Read("evaluation: 1\ntask: t\nexpect:\n  - answer-contains: x")], host, "a", "1", "test")).Cases.Single();

        Assert.False(result.Passed);
        Assert.Contains("stopped before an answer", result.Problem, StringComparison.Ordinal);
    }
}
