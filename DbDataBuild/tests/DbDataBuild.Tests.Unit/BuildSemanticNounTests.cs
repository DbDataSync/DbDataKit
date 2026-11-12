using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;
using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;
using Json.Schema;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The <c>semantic</c> noun in Build (plan 14a SL22): the shared verbs and documents over a Gateway the server is paired with. The connection is the only part that is Build's, so a stand-in session
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

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync([.. args, "--repo", root], o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Describe_query_explain_and_members_answer_the_shared_documents_and_send_the_shared_statements()
    {
        var session = new FakeSession();
        BuildSemantic.Connect = (_, _) => Task.FromResult<ISemanticSession>(session);

        var query = await Run("semantic", "query", "shop", "--sql", "SELECT region, net FROM shop", "--gateway", "any", "--format", "json");
        Assert.Equal(0, query.Exit);
        var document = JsonNode.Parse(query.Out)!;
        Assert.True(JsonSchema.FromText(ServerCli.OutputSchema(["semantic"])).Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, query.Out);
        Assert.Equal(2, (int)document["data"]!["row_count"]!);
        Assert.Equal("complete", (string)document["data"]!["notes"]!["qis.status"]!);

        Assert.Equal(0, (await Run("semantic", "describe", "shop", "--gateway", "any")).Exit);
        Assert.Equal(0, (await Run("semantic", "explain", "shop", "--sql", "SELECT 1", "--gateway", "any")).Exit);
        Assert.Equal(0, (await Run("semantic", "members", "shop", "customer.region", "--limit", "5", "--gateway", "any")).Exit);
        Assert.Equal([("shop", "SELECT region, net FROM shop"), ("shop", """{"kind": "describe"}"""), ("shop", "EXPLAIN SELECT 1"), ("shop", """{"kind":"members","attribute":"customer.region","limit":5}""")], session.Asked);
    }

    [Fact]
    public async Task A_connection_without_a_token_is_refused_naming_the_variable_and_an_unpaired_gateway_is_refused_naming_the_pairing_verb()
    {
        Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, null);
        var noToken = await Run("semantic", "describe", "shop", "--gateway", "any");
        Assert.NotEqual(0, noToken.Exit);
        Assert.Contains(BuildSemantic.TokenVariable, noToken.Err);

        Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, "t0ken");
        try
        {
            var unpaired = await Run("semantic", "describe", "shop", "--gateway", "nobody");
            Assert.NotEqual(0, unpaired.Exit);
            Assert.Contains("server proxy list", unpaired.Err);
            Assert.DoesNotContain("t0ken", unpaired.Err + unpaired.Out);
        }
        finally { Environment.SetEnvironmentVariable(BuildSemantic.TokenVariable, null); }
    }

    [Fact]
    public void The_flight_address_is_the_paired_gateways_host_on_the_flight_port_or_the_one_given()
    {
        new ProxiesFile(root).Add(new PairedProxy("gw", "https://gateway.example.com:8443/", "inst", "ff", DateTimeOffset.UnixEpoch));
        CliArgs Parse(params string[] args) => CliArgs.Parse(args, new HashSet<string> { "insecure" }, new HashSet<string>(BuildSemantic.Options));
        Assert.Equal("https://gateway.example.com:8815/", BuildSemantic.AddressOf(Parse("--gateway", "gw"), root).ToString());
        Assert.Equal("https://gateway.example.com:9000/", BuildSemantic.AddressOf(Parse("--gateway", "gw", "--flight-port", "9000"), root).ToString());
        Assert.Equal("https://other:1/", BuildSemantic.AddressOf(Parse("--address", "https://other:1"), root).ToString());
        Assert.Throws<InvalidOperationException>(() => BuildSemantic.AddressOf(Parse("--flight-port", "9000"), root));
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
            var exit = DbDataBuild.Cli.CliApp.Run([.. args, "--gateway", "any", "--format", "json"], o, e, environment: _ => null);
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
    public void An_agent_is_offered_the_verbs_without_a_way_to_send_the_token_anywhere_but_to_a_paired_gateway_and_the_management_api_does_not_offer_them_at_all()
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

        Assert.DoesNotContain(DbDataBuild.Cli.Server.BuildManagementApi.FullSurface(root, catalog).Tools.Keys, k => k.StartsWith("semantic_"));
        Assert.DoesNotContain(DbDataBuild.Cli.Server.BuildManagementApi.ReadOnlySurface(root, catalog).Tools.Keys, k => k.StartsWith("semantic_"));
    }
}
