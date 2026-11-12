using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using Json.Schema;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>Plan 27a KN21: <c>query run-verified</c> and <c>query list-verified</c> over a real index on SQLite, every document validated against its schema; the same query run as two people gives two result sets.</summary>
public sealed class VerifiedQueryNounTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private const string Request = "request:\n  kind: query\n  query:\n    measures: [revenue]\n    attributes: [region.name]\n    filters:\n      - { attribute: date.quarter, operator: equals, values: [{ param: quarter }] }\n    limit: 10";

    private static KnowledgeItem Query(string id, string extra = "", string body = Request, string model = "Sales")
    {
        var text = $"---\nknowledge: 1\nkind: verified-query\ntitle: {id}\nowner: finance-data\nmodel: {model}\nquestions:\n  - What was revenue by region in a quarter?\n{body}\nverified: {{ by: alice, at: 2026-11-01, model-version: \"4\" }}\n{extra}---\nNotes.";
        var result = KnowledgeItemLoader.Load(text, id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private StateKnowledgeIndex Index(params KnowledgeItem[] items)
    {
        var index = new StateKnowledgeIndex(new KnowledgeStore(_engines.Provision("Sqlite").Open([KnowledgeModule.Module], time: new StateTestClock(DateTimeOffset.Parse("2026-11-12T09:00:00Z")))), "prod");
        foreach (var item in items) index.Put(item, Chunker.Split(item), "project", item.Hash[..12]);
        return index;
    }

    /// <summary>A fake model that answers what the person running it may see: a person with the role <c>all</c> sees two regions, anyone else one.</summary>
    private sealed class Model(string person) : IVerifiedQueryReader
    {
        public List<string> Statements { get; } = [];

        public VerifiedQueryView? Find(string id, string channel) => throw new NotSupportedException();

        public IReadOnlyList<VerifiedQueryView> List(string channel, string? model) => throw new NotSupportedException();

        public Task<VerifiedAnswer> RunAsync(string model, string statement, int maxRows, CancellationToken cancellation)
        {
            Statements.Add(statement);
            IReadOnlyList<IReadOnlyList<object?>> rows = person == "boss" ? [["EMEA", 120.5], ["APAC", 80.0]] : [["EMEA", 120.5]];
            return Task.FromResult(new VerifiedAnswer([("region", "string"), ("revenue", "double")], rows, false, new Dictionary<string, string>()));
        }

        public string? CurrentModelVersion(string model) => "5";
    }

    private (NounRouter Router, Model Model) Build(string person, StateKnowledgeIndex index, params string[] readable)
    {
        var model = new Model(person);
        var reader = new StateVerifiedQueryReader(_ => index, new Caller(readable), Catalog(), (m, s, n, c) => model.RunAsync(m, s, n, c), _ => "5");
        return (new NounRouter("demo", [VerifiedQueryNoun.Create(reader)]), model);
    }

    private static async Task<(int Exit, JsonElement Root, string Out, string Err)> Run(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        var text = output.ToString();
        var root = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement : default;
        if (root.ValueKind == JsonValueKind.Object && exit == 0)
            Assert.True(JsonSchema.FromText(router.OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, text);
        var errors = error + (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) ? string.Join("\n", e.EnumerateArray().Select(x => x.ToString())) : "");
        return (exit, root, text, errors);
    }

    [Fact]
    public async Task A_verified_query_runs_with_its_parameters_and_the_answer_says_which_query_ran()
    {
        var (router, model) = Build("boss", Index(Query("revenue.md")), "semantic-model:Sales");

        var (exit, root, _, _) = await Run(router, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1");

        Assert.Equal(0, exit);
        var data = root.GetProperty("data");
        Assert.Equal(2, data.GetProperty("row_count").GetInt32());
        var used = data.GetProperty("verified_query");
        Assert.Equal("revenue.md", used.GetProperty("id").GetString());
        Assert.Equal("alice", used.GetProperty("verified_by").GetString());
        Assert.Equal("4", used.GetProperty("model_version").GetString());
        Assert.Contains("version 4 of Sales, which is now at version 5", data.GetProperty("note").GetString(), StringComparison.Ordinal);
        var statement = JsonNode.Parse(Assert.Single(model.Statements))!;
        Assert.Equal("FY27-Q1", statement["parameters"]!["quarter"]!.GetValue<string>());
        Assert.Equal("query", statement["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_same_query_as_two_people_gives_two_result_sets()
    {
        var index = Index(Query("revenue.md"));
        var (boss, _) = Build("boss", index, "semantic-model:Sales");
        var (clerk, _) = Build("clerk", index, "semantic-model:Sales");

        var first = await Run(boss, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1");
        var second = await Run(clerk, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1");

        Assert.Equal(2, first.Root.GetProperty("data").GetProperty("row_count").GetInt32());
        Assert.Equal(1, second.Root.GetProperty("data").GetProperty("row_count").GetInt32());
    }

    [Fact]
    public async Task A_person_who_may_not_see_the_model_does_not_find_the_query()
    {
        var (router, model) = Build("clerk", Index(Query("revenue.md")));

        var (exit, _, _, error) = await Run(router, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1");

        Assert.NotEqual(0, exit);
        Assert.Contains("no verified query 'revenue.md'", error, StringComparison.Ordinal);
        Assert.Empty(model.Statements);
    }

    [Fact]
    public async Task A_missing_or_unknown_parameter_is_refused_naming_what_the_query_takes()
    {
        var (router, model) = Build("boss", Index(Query("revenue.md")), "semantic-model:Sales");

        var missing = await Run(router, "query", "run-verified", "revenue.md");
        var unknown = await Run(router, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1", "--param", "year=2027");

        Assert.NotEqual(0, missing.Exit);
        Assert.Contains("needs a value for: quarter", missing.Err, StringComparison.Ordinal);
        Assert.NotEqual(0, unknown.Exit);
        Assert.Contains("has no parameter 'year'. It takes: quarter", unknown.Err, StringComparison.Ordinal);
        Assert.Empty(model.Statements);
    }

    [Fact]
    public async Task A_saved_query_of_the_model_runs_as_a_call_with_named_arguments()
    {
        var index = Index(Query("monthly.md", body: "saved-query: monthly_revenue@4"));
        var (router, model) = Build("boss", index, "semantic-model:Sales");

        var (exit, _, _, error) = await Run(router, "query", "run-verified", "monthly.md", "--param", "quarter=O'Neil");

        Assert.Equal(0, exit);
        Assert.Equal("", error.Trim());
        Assert.Equal("SELECT * FROM monthly_revenue(quarter := 'O''Neil')", Assert.Single(model.Statements));
        Assert.Equal("4", index.Store.Verified("prod", "monthly.md")!.SavedVersion);
    }

    [Fact]
    public async Task A_query_that_needs_re_verification_is_listed_and_found_but_not_run_as_verified()
    {
        var index = Index(Query("revenue.md"));
        index.Store.SetVerifiedStatus("prod", "revenue.md", VerifiedStatus.NeedsReverification, "the measure revenue is gone from Sales");
        var (router, model) = Build("boss", index, "semantic-model:Sales");

        var list = await Run(router, "query", "list-verified", "--model", "Sales");
        var run = await Run(router, "query", "run-verified", "revenue.md", "--param", "quarter=FY27-Q1");

        Assert.Equal(0, list.Exit);
        var entry = Assert.Single(list.Root.GetProperty("data").GetProperty("queries").EnumerateArray());
        Assert.Equal("needs-reverification", entry.GetProperty("status").GetString());
        Assert.Contains("the measure revenue is gone", entry.GetProperty("status_note").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("needs re-verification", run.Err, StringComparison.Ordinal);
        Assert.Empty(model.Statements);
    }

    [Fact]
    public async Task The_list_gives_the_questions_and_only_what_the_person_may_see()
    {
        var index = Index(Query("revenue.md"), Query("budget.md", model: "Finance"));
        var (router, _) = Build("boss", index, "semantic-model:Sales");

        var (exit, root, _, _) = await Run(router, "query", "list-verified");

        Assert.Equal(0, exit);
        var entry = Assert.Single(root.GetProperty("data").GetProperty("queries").EnumerateArray());
        Assert.Equal("revenue.md", entry.GetProperty("id").GetString());
        Assert.Equal("What was revenue by region in a quarter?", entry.GetProperty("questions")[0].GetString());
    }

    [Fact]
    public void The_semantic_sql_of_a_check_that_disagrees_with_its_request_is_an_error_of_knowledge_check()
    {
        using var folder = new Folder();
        folder.With("queries/revenue.md", $"---\nknowledge: 1\nkind: verified-query\ntitle: Revenue\nowner: finance-data\nmodel: Sales\nquestions:\n  - revenue?\nsemantic-sql: SELECT revenue FROM Sales\n{Request}\nverified: {{ by: alice, at: 2026-11-01, model-version: \"4\" }}\n---\nn");
        static (JsonObject?, string?) Other(string sql, string model) => (JsonNode.Parse("""{"kind":"query","query":{"measures":["margin"],"limit":10}}""")!.AsObject(), null);

        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, [], translate: Other);

        Assert.False(report.Ok);
        var error = Assert.Single(report.Diagnostics, d => d.Code == VerifiedQueries.SqlDisagrees);
        Assert.Contains("The request is what runs", error.Message, StringComparison.Ordinal);
    }
}
