using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;

namespace DbDataKit.Semantic.Cli.Tests;

/// <summary>The semantic noun (plan 19, J15): each verb sends the statement the server's model source takes, and answers one document.</summary>
public sealed class SemanticNounTests
{
    private sealed class Fake : ISemanticSession
    {
        public List<(string Model, string Statement, int MaxRows)> Sent { get; } = [];

        public Task<SemanticTable> RunAsync(string model, string statement, int maxRows, CancellationToken cancellationToken)
        {
            Sent.Add((model, statement, maxRows));
            return Task.FromResult(new SemanticTable([new("region", "string"), new("net", "double")], [["East", 400.5], ["West", null]], Truncated: true,
                new Dictionary<string, string> { ["qis.status"] = "complete" }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (Noun Noun, Fake Session) Make()
    {
        var session = new Fake();
        return (SemanticNoun.Create((_, _) => Task.FromResult<ISemanticSession>(session), ["address", "user"], ["insecure"], "[--address URL] [--user U] [--insecure]"), session);
    }

    private static async Task<(int Code, CliContext Context, string Out, string Error)> RunAsync(Noun noun, string verb, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var context = new CliContext(output, error, TextReader.Null);
        var code = await noun.Verbs.Single(v => v.Name == verb).Run(args, context);
        return (code, context, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Query_sends_the_sql_as_written_and_answers_the_table_as_data()
    {
        var (noun, session) = Make();
        var (code, context, output, _) = await RunAsync(noun, "query", "sales", "--sql", "SELECT customers.region FROM sales GROUP BY ALL LIMIT 10", "--max-rows", "5", "--user", "alice", "--insecure");

        Assert.Equal(0, code);
        Assert.Equal(("sales", "SELECT customers.region FROM sales GROUP BY ALL LIMIT 10", 5), session.Sent.Single());
        Assert.Contains("East | 400.5", output);
        Assert.Contains("West | NULL", output);
        Assert.Equal(2, context.Data["row_count"]);
        Assert.Equal(true, context.Data["truncated"]);
        Assert.Equal("complete", ((Dictionary<string, object?>)context.Data["notes"]!)["qis.status"]);
    }

    [Fact]
    public async Task Explain_marks_the_sql_and_turns_a_request_into_an_explain_request()
    {
        var (noun, session) = Make();
        await RunAsync(noun, "explain", "sales", "--sql", "SELECT 1");
        await RunAsync(noun, "explain", "sales", "--request", """{"kind": "query", "query": {"limit": 1}}""");

        Assert.Equal("EXPLAIN SELECT 1", session.Sent[0].Statement);
        Assert.Equal("""{"kind":"explain","query":{"limit":1}}""", session.Sent[1].Statement);
    }

    [Fact]
    public async Task Members_and_describe_send_their_request_kinds()
    {
        var (noun, session) = Make();
        await RunAsync(noun, "members", "sales", "customers.region", "--search", "ea", "--limit", "7");
        await RunAsync(noun, "describe", "sales");

        Assert.Equal("""{"kind":"members","attribute":"customers.region","search":"ea","limit":7}""", session.Sent[0].Statement);
        Assert.Equal("""{"kind": "describe"}""", session.Sent[1].Statement);
    }

    [Theory]
    [InlineData("query", "sales")]                                                                // neither --sql nor --request
    [InlineData("query", "sales", "--sql", "x", "--request", "{}")]                                // both
    [InlineData("query", "sales", "--sql", "x", "--max-rows", "0")]
    [InlineData("query", "sales", "--sql", "x", "--no-such-option", "1")]
    [InlineData("members", "sales")]                                                              // no attribute
    [InlineData("members", "sales", "customers.region", "--limit", "many")]
    [InlineData("explain", "sales", "--request", "not json")]
    public async Task What_does_not_fit_is_a_usage_error_and_nothing_is_sent(string verb, params string[] args)
    {
        var (noun, session) = Make();
        var (code, _, _, error) = await RunAsync(noun, verb, args);

        Assert.Equal(CliExit.Usage, code);
        Assert.NotEmpty(error);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void Every_verb_has_a_closed_data_schema()
    {
        var (noun, _) = Make();

        Assert.All(noun.Verbs, v => Assert.Contains("\"additionalProperties\":false", v.DataSchema!.Replace(" ", "")));
        Assert.Equal(["describe", "explain", "members", "query"], noun.Verbs.Select(v => v.Name).Order());
    }
}
