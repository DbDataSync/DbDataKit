using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using LibGit2Sharp;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The Build editor for a model's saved queries (plan 14a SL20, Build's side): the translation check of one entry of the draft's model, and a try-run on the Gateway the model is published to. The Gateway is the stand-in of
/// <see cref="BuildServerProxyTests"/>, which records the call and answers as a test says.
/// </summary>
public sealed partial class BuildServerProxyTests
{
    private (int Status, JsonObject Body) runAnswerIs = (200, new JsonObject { ["status"] = "ok", ["columns"] = new JsonArray(), ["rows"] = new JsonArray(), ["model_version"] = 4 });

    private async Task<string> MakeRetailWorkspace()
    {
        using (var repo = new Repository(Repository.Init(retail)))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("first", who, who);
        }
        using var made = await Send("ana", ServerRole.Author, HttpMethod.Post, "/api/v1/projects/retail/workspaces", """{"name":"edit"}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (string)JsonNode.Parse(await made.Content.ReadAsStringAsync())!["id"]!;
    }

    private async Task<JsonNode> CheckQuery(string id, string entry, string who = "ana", HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var reply = await Send(who, ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/retail/workspaces/{id}/models/retail_sales/queries/check", entry);
        Assert.Equal(expect, reply.StatusCode);
        return JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task The_translation_check_says_what_the_compiler_will_say_and_lists_the_parameters_a_query_asks_for()
    {
        var id = await MakeRetailWorkspace();

        var good = await CheckQuery(id, """{"sql":"SELECT sales.region, MEASURE(sales.net_amount) FROM retail_sales WHERE sales.region IN (:regions) GROUP BY ALL","parameters":{"regions":{"for":"sales.region","many":true}}}""");
        Assert.True((bool)good["ok"]!, good.ToJsonString());
        Assert.Equal("query", (string?)good["request"]!["kind"]);
        var parameter = Assert.Single(good["parameters"]!.AsArray());
        Assert.Equal(("regions", true), ((string?)parameter!["name"], (bool)parameter["many"]!));
        Assert.Empty(good["warnings"]!.AsArray());

        var wrongModel = await CheckQuery(id, """{"sql":"SELECT sales.net_amount FROM somewhere_else"}""");
        Assert.False((bool)wrongModel["ok"]!);
        Assert.Equal("sql_wrong_model", (string?)wrongModel["errors"]![0]!["code"]);

        var syntax = await CheckQuery(id, """{"sql":"SELEKT 1"}""");
        Assert.Equal("sql_syntax", (string?)syntax["errors"]![0]!["code"]);

        var both = await CheckQuery(id, """{"sql":"SELECT sales.net_amount FROM retail_sales","request":{"kind":"query"}}""");
        Assert.Equal("query_invalid", (string?)both["errors"]![0]!["code"]);

        var notQuery = await CheckQuery(id, """{"request":{"kind":"describe"}}""");
        Assert.Equal("query_invalid", (string?)notQuery["errors"]![0]!["code"]);

        var undefined = await CheckQuery(id, """{"sql":"SELECT sales.region, MEASURE(sales.net_amount) FROM retail_sales WHERE sales.region = :region GROUP BY ALL","parameters":{"stale":{"type":"string"}}}""");
        Assert.True((bool)undefined["ok"]!);
        Assert.Equal(["parameter_undefined", "parameter_unused"], undefined["warnings"]!.AsArray().Select(w => (string)w!["code"]!).ToArray());
    }

    [Fact]
    public async Task The_translation_check_is_the_owners_of_the_workspace_and_needs_a_model_that_compiles()
    {
        var id = await MakeRetailWorkspace();
        await CheckQuery(id, """{"sql":"SELECT 1"}""", who: "bob", expect: HttpStatusCode.Forbidden);
        using (var none = await Send("ana", ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/retail/workspaces/{id}/models/nothing/queries/check", """{"sql":"SELECT 1"}""")) Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        using (var bad = await Send("ana", ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/retail/workspaces/{id}/models/retail_sales/queries/check", "[1]")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_try_run_goes_to_the_paired_gateway_as_the_person_with_the_publisher_role_and_the_gateways_answer_or_refusal_comes_back()
    {
        await Pair();
        var body = """{"model":"retail_sales","sql":"SELECT sales.net_amount FROM retail_sales","parameters":{},"limit":20}""";

        using var ran = await Send("ana", ServerRole.Viewer, HttpMethod.Post, "/api/v1/proxies/edge/try-query", body);
        Assert.Equal(HttpStatusCode.OK, ran.StatusCode);
        Assert.Equal(4, (int)JsonNode.Parse(await ran.Content.ReadAsStringAsync())!["model_version"]!);
        var call = seen.Single(r => r.Path == "/api/v1/my/runs");
        Assert.Equal("POST", call.Method);
        Assert.Equal(("ana", true), (call.Token.SubjectId, call.Token.Roles.HasFlag(ServerRole.Publisher)));
        Assert.Equal(20, (int)JsonNode.Parse(call.Body)!["limit"]!);

        runAnswerIs = (400, new JsonObject { ["error"] = "unknown member sales.nope", ["code"] = "unknown_member" });
        using var refused = await Send("ana", ServerRole.Viewer, HttpMethod.Post, "/api/v1/proxies/edge/try-query", body);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("unknown_member", (string?)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["code"]);

        using var nobody = await Send("ana", ServerRole.Viewer, HttpMethod.Post, "/api/v1/proxies/nowhere/try-query", body);
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        using var malformed = await Send("ana", ServerRole.Viewer, HttpMethod.Post, "/api/v1/proxies/edge/try-query", """{"model":"retail_sales"}""");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }
}
