using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// <c>semantic publish</c> and <c>semantic rollback</c> on the server's command line (plan 14a SL30): the two calls of the Publish tab from a shell, over the pairing the server already has (no token of Build's own is read, from the
/// environment or a flag: the server's directory is the credential). The Gateway is the stand-in of <see cref="BuildServerProxyTests"/>.
/// </summary>
public sealed partial class BuildServerProxyTests
{
    private (int Status, JsonObject Body) rollbackAnswerIs = (200, new JsonObject { ["name"] = "retail_sales", ["version"] = 2, ["from_version"] = 3 });

    private async Task<(int Exit, string Out, string Err)> Semantic(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync(["semantic", .. args, "--repo", BuildDir], o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Publish_by_command_sends_the_connections_then_the_catalog_as_the_operating_system_user_with_the_publisher_role()
    {
        await Pair();

        var result = await Semantic("publish", "retail", "retail_sales", "--gateway", "edge", "--format", "json");

        Assert.True(result.Exit == 0, result.Err + result.Out);
        var data = JsonNode.Parse(result.Out)!["data"]!;
        Assert.Equal(("edge", "retail_sales", 1), ((string)data["gateway"]!, (string)data["model"]!, (int)data["version"]!));
        Assert.Equal(["sqlserver"], data["data_sources"]!.AsArray().Select(d => (string)d!));
        var put = seen.Single(r => r.Method == "PUT" && r.Path == "/api/v1/definitions/semantic-model/retail_sales");
        Assert.Equal("cli:" + Environment.UserName, put.Token.SubjectId);
        Assert.True(put.Token.Roles.HasFlag(DbDataKit.Web.Trust.ServerRole.Publisher));
        Assert.DoesNotContain(seen, r => r.Body.Contains("WRITE-pw-77f"));         // the write login never leaves, as in the Publish tab
    }

    [Fact]
    public async Task Publish_names_what_is_wrong_before_or_in_the_gateways_words_and_takes_no_token_flag()
    {
        await Pair();
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Semantic("publish", "retail", "--gateway", "edge")).Exit);
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Semantic("publish", "retail", "retail_sales")).Exit);
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Semantic("publish", "retail", "retail_sales", "--gateway", "edge", "--token", "x")).Exit);
        Assert.Contains("not a Gateway this server is paired with", (await Semantic("publish", "retail", "retail_sales", "--gateway", "nowhere")).Err);
        Assert.Contains("hosts no project", (await Semantic("publish", "nothing", "retail_sales", "--gateway", "edge")).Err);
        Assert.Contains("no semantic model", (await Semantic("publish", "retail", "nothing", "--gateway", "edge")).Err);
        Assert.Empty(seen);

        modelAnswer = System.Net.HttpStatusCode.UnprocessableEntity;
        var refused = await Semantic("publish", "retail", "retail_sales", "--gateway", "edge");
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("The proxy refused publishing the model", refused.Err);
    }

    [Fact]
    public async Task Rollback_by_command_asks_for_the_version_before_or_the_one_named_and_says_what_the_gateway_refused()
    {
        await Pair();

        var back = await Semantic("rollback", "retail_sales", "--gateway", "edge", "--format", "json");
        Assert.True(back.Exit == 0, back.Err + back.Out);
        var data = JsonNode.Parse(back.Out)!["data"]!;
        Assert.Equal((2, 3), ((int)data["version"]!, (int)data["from_version"]!));
        var call = seen.Single(r => r.Method == "POST" && r.Path == "/api/v1/semantic-models/retail_sales/rollback");
        Assert.Equal("", call.Body);
        Assert.Equal("cli:" + Environment.UserName, call.Token.SubjectId);

        seen.Clear();
        Assert.Equal(0, (await Semantic("rollback", "retail_sales", "--gateway", "edge", "--to-version", "1")).Exit);
        Assert.Equal(1, (int)JsonNode.Parse(seen.Single().Body)!["to_version"]!);

        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Semantic("rollback", "retail_sales", "--gateway", "edge", "--to-version", "zero")).Exit);

        rollbackAnswerIs = (404, new JsonObject { ["error"] = "There is no earlier version to go back to." });
        var none = await Semantic("rollback", "retail_sales", "--gateway", "edge");
        Assert.NotEqual(0, none.Exit);
        Assert.Contains("There is no earlier version to go back to.", none.Err);
    }
}
