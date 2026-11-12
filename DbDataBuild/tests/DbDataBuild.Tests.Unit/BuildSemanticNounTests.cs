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
}
