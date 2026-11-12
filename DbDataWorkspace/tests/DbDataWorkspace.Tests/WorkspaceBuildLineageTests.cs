using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The column lineage of a Build draft (plan 33 SC20h): <c>dbdatabuild project show graph --columns</c> run on the draft, offline and read only, its document passed through. The stand-in <see cref="FakeBuild"/> for the mechanics (what is refused,
/// how a refusal of the tool is shown); the real program, found as <see cref="WorkspaceBuildKindTests"/> finds it, for the shape: the shape the screen reads was read from this tool's own answer.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildLineageTests
{
    private static string Fake()
    {
        var path = FakeBuild.Create();
        if (path is null) Assert.Skip("the stand-in dbdatabuild is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","kind":"build"}""")).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Lineage(WorkspaceHarness server, string id, string json = "{}") =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{id}/lineage", json);

    [Fact]
    public async Task The_edges_of_every_output_column_come_back_as_the_tool_printed_them()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        await server.AsAda(HttpMethod.Put, $"/api/v1/projects/shop/workspaces/{id}/files/models/marts/orders.sql", new JsonObject { ["content"] = "SELECT 1 AS id\n" }.ToJsonString());

        var answer = await Lineage(server, id);
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        Assert.True((bool)answer.Body!["ok"]!, answer.Body.ToJsonString());
        var edge = answer.Body["data"]!["column_edges"]![0]!;
        Assert.Equal(("staging.orders", "order_id", "marts.orders", "direct"), ((string)edge["from_table"]!, (string)edge["from_column"]!, (string)edge["to_model"]!, (string)edge["transform"]!));
    }

    [Fact]
    public async Task A_column_is_followed_up_and_down_by_the_tool_and_a_column_it_does_not_know_is_answered_with_its_words()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        var followed = await Lineage(server, id, """{"column":"marts.orders.order_id"}""");
        Assert.True((bool)followed.Body!["ok"]!);
        Assert.Equal("marts.orders.order_id", (string)followed.Body["data"]!["column_lineage"]!["column"]!);
        Assert.Equal(1, (int)followed.Body["data"]!["column_lineage"]!["upstream"]![0]!["distance"]!);

        var unknown = await Lineage(server, id, """{"column":"nosuch.x"}""");
        Assert.Equal(HttpStatusCode.OK, unknown.Response.StatusCode);
        Assert.False((bool)unknown.Body!["ok"]!);
        var finding = Assert.Single(unknown.Body["diagnostics"]!.AsArray())!;
        Assert.Equal("DBD-WS-BUILD-LINEAGE", (string)finding["code"]!);
        Assert.Contains("has no column `x`", (string)finding["message"]!);
    }

    [Fact]
    public async Task A_project_with_errors_has_no_graph_and_the_findings_name_the_file_and_line()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        await server.AsAda(HttpMethod.Put, $"/api/v1/projects/shop/workspaces/{id}/files/models/marts/orders.sql", new JsonObject { ["content"] = "SELECT * FROM broken\n" }.ToJsonString());

        var answer = await Lineage(server, id);
        Assert.False((bool)answer.Body!["ok"]!);
        var finding = Assert.Single(answer.Body["diagnostics"]!.AsArray())!;
        Assert.Equal(("DDB-301", "models/marts/orders.sql", 1), ((string)finding["code"]!, (string)finding["file"]!, (int)finding["line"]!));
    }

    [Theory]
    [InlineData("""{"column":"-x.y"}""")]
    [InlineData("""{"column":"a..b"}""")]
    [InlineData("""{"column":"nodot"}""")]
    [InlineData("""{"column":"a/b.c"}""")]
    [InlineData("""{"column":7}""")]
    public async Task The_column_is_checked_before_anything_runs(string body)
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        Assert.Equal(HttpStatusCode.BadRequest, (await Lineage(server, id, body)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_read_it_and_a_project_that_is_not_a_build_project_has_no_lineage()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake(), syncExecutable: FakeSync.Create());
        var id = await DraftAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{id}/lineage", "{}")).Response.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders-set","kind":"sync"}""")).Response.StatusCode);
        var other = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders-set/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{other}/lineage", "{}")).Response.StatusCode);
    }

    [Fact]
    public async Task The_real_program_gives_the_lineage_of_the_starter_template_whole_and_followed_to_the_raw_table()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: WorkspaceBuildKindTests.Need());
        var id = await DraftAsync(server);

        var whole = await Lineage(server, id);
        Assert.True((bool)whole.Body!["ok"]!, whole.Body.ToJsonString());
        Assert.NotEmpty(whole.Body["data"]!["column_edges"]!.AsArray());
        Assert.Contains(whole.Body["data"]!["column_edges"]!.AsArray(), e => (string)e!["to_model"]! == "marts.customers" && (string)e["to_column"]! == "number_of_orders" && (string)e["transform"]! == "aggregation");

        var followed = await Lineage(server, id, """{"column":"marts.customers.number_of_orders"}""");
        Assert.True((bool)followed.Body!["ok"]!, followed.Body.ToJsonString());
        var upstream = followed.Body["data"]!["column_lineage"]!["upstream"]!.AsArray();
        Assert.Contains(upstream, s => (string)s!["table"]! == "raw.orders" && (string)s["column"]! == "id" && (int)s["distance"]! == 3);

        var unknown = await Lineage(server, id, """{"column":"nosuch.x"}""");
        Assert.False((bool)unknown.Body!["ok"]!);
        Assert.Contains("has no column", (string)unknown.Body["diagnostics"]![0]!["message"]!);
    }
}
