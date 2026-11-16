using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;
using Json.Schema;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The <c>semantic</c> verbs in Build (plan 14a SL22): the shared verbs and documents over a Gateway named by its Flight address (<c>--gateway</c> named the deleted Build server's pairings, plan 33 SC23, and answers so). The connection is the only part that is Build's, so a stand-in session
/// answers here; the session class itself runs against a real Gateway in the Gateway's <c>SemanticFlightTests</c> (<c>TheSharedFlightSessionBuildUsesDescribesQueriesAndExplains...</c>).
/// </summary>
[Collection("BuildSemantic")]
public class BuildSemanticNounTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-semantic-" + Guid.NewGuid().ToString("N"));
    private readonly SemanticConnect original = BuildSemantic.Connect;

    public BuildSemanticNounTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        BuildSemantic.Connect = original;
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FakeSession : ISemanticSession
    {
        public List<(string Model, string Statement)> Asked { get; } = [];

        public Task<SemanticTable> RunAsync(string model, string statement, int maxRows, CancellationToken cancellationToken)
        {
            Asked.Add((model, statement));
            return Task.FromResult(new SemanticTable([new("region", "string"), new("net", "double")], [["north", 1.5], ["south", 2.0]], false, new Dictionary<string, string> { ["qis.status"] = "complete" }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = CliApp.Run(args, o, e, environment: _ => null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void A_connection_without_a_token_is_refused_naming_the_variable_and_a_gateway_by_pairing_name_answers_that_the_server_is_gone()
    {
        Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, null);
        var noToken = Run("semantic", "describe", "shop", "--address", "https://gateway.example.com:8815");
        Assert.NotEqual(0, noToken.Exit);
        Assert.Contains(BuildSemantic.TokenVariable, noToken.Err + noToken.Out);

        Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, "t0ken");
        try
        {
            var paired = Run("semantic", "describe", "shop", "--gateway", "nobody");
            Assert.NotEqual(0, paired.Exit);
            Assert.Contains("plan 33 SC23", paired.Err + paired.Out);
            Assert.Contains("--address", paired.Err + paired.Out);
            Assert.DoesNotContain("t0ken", paired.Err + paired.Out);
        }
        finally { Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, null); }
    }

    [Fact]
    public void The_flight_address_is_the_one_given_and_a_pairing_name_is_refused_with_the_sentence()
    {
        CliArgs Parse(params string[] args) => CliArgs.Parse(args, new HashSet<string> { "insecure" }, new HashSet<string>(BuildSemantic.Options));
        Assert.Equal("https://other:1/", BuildSemantic.AddressOf(Parse("--address", "https://other:1")).ToString());
        Assert.Equal(BuildSemantic.GatewayGone, Assert.Throws<InvalidOperationException>(() => BuildSemantic.AddressOf(Parse("--gateway", "gw"))).Message);
        Assert.Throws<InvalidOperationException>(() => BuildSemantic.AddressOf(Parse("--flight-port", "9000")));
    }

    [Fact]
    public void The_four_verbs_are_commands_of_the_tree_with_a_document_that_satisfies_the_output_schema()
    {
        var session = new FakeSession();
        BuildSemantic.Connect = (_, _) => Task.FromResult<ISemanticSession>(session);
        var schema = SchemaConformanceTests.LoadSchema("output");

        (int Exit, JsonNode Doc) Command(params string[] args)
        {
            var (o, e) = (new StringWriter(), new StringWriter());
            var exit = DbDataBuild.Cli.CliApp.Run([.. args, "--address", "https://gateway.example.com:8815", "--format", "json"], o, e, environment: _ => null);
            var doc = JsonNode.Parse(o.ToString())!;
            Assert.True(schema.Evaluate(System.Text.Json.JsonSerializer.SerializeToNode(doc), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List }).IsValid, o.ToString());
            return (exit, doc);
        }

        var query = Command("semantic", "query", "shop", "--sql", "SELECT region, net FROM shop", "--max-rows", "50");
        Assert.Equal(0, query.Exit);
        Assert.Equal("semantic query", (string)query.Doc["command"]!);
        Assert.Equal(2, (int)query.Doc["data"]!["row_count"]!);
        Assert.Equal("complete", (string)query.Doc["data"]!["notes"]!["qis.status"]!);
        Assert.Equal(0, Command("semantic", "describe", "shop").Exit);
        Assert.Equal(0, Command("semantic", "explain", "shop", "--sql", "SELECT 1").Exit);
        Assert.Equal(0, Command("semantic", "members", "shop", "customer.region", "--limit", "5", "--search", "no").Exit);
        Assert.Equal([("shop", "SELECT region, net FROM shop"), ("shop", """{"kind": "describe"}"""), ("shop", "EXPLAIN SELECT 1"), ("shop", """{"kind":"members","attribute":"customer.region","search":"no","limit":5}""")], session.Asked);

        // a usage error and a refusal are documents too
        Assert.Equal(DbDataBuild.Cli.CliApp.ExitUsage, Command("semantic", "query", "shop").Exit);
        BuildSemantic.Connect = (_, _) => throw new InvalidOperationException("The Gateway refused the token.");
        var refused = Command("semantic", "describe", "shop");
        Assert.Equal(DbDataBuild.Cli.CliApp.ExitFindings, refused.Exit);
        Assert.Contains("refused the token", refused.Doc.ToJsonString());
    }

    [Fact]
    public void An_agent_is_offered_the_verbs_without_a_way_to_send_the_token_to_an_address_of_its_choosing()
    {
        var catalog = DbDataBuild.Cli.TuiCommand.CliHost.Catalog();
        var agent = new DbDataBuild.Cli.Mcp.ToolSurface(root, catalog.Where(c => c.Name is not ("ui terminal" or "ui mcp")));
        Assert.All(new[] { "semantic_describe", "semantic_query", "semantic_explain", "semantic_members" }, name => Assert.Contains(name, agent.Tools.Keys));
        var properties = agent.Describe(agent.Tools["semantic_query"])["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("gateway", properties);
        Assert.Contains("sql", properties);
        Assert.DoesNotContain("address", properties);
        Assert.DoesNotContain("insecure", properties);
        Assert.DoesNotContain("ca_file", properties);
        Assert.DoesNotContain("server_dir", properties);
    }
}
