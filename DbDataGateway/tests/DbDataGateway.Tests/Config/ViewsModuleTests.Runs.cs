using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 14a SL23 (D21) on the Gateway host: a statement run as a person (here the views module's preview, which goes through the same core) leaves a record; the person reads their own through the management API
/// (<c>/api/v1/my/runs</c>), another person does not see it, an administrator may ask for everyone's, and a replay runs the record's request again on the model version it names: the same hash, and a version that is gone is said so.
/// </summary>
public sealed partial class ViewsModuleTests
{
    [Fact]
    public async Task A_statement_leaves_a_record_its_owner_reads_and_replays_on_the_version_it_names_and_nobody_else_reads()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "Ada@Example.invalid", Analysts);
        var (cy, _) = HomeSeed.Person(state, "Cy", "cy@example.invalid", Analysts);
        PublishModel();
        var draft = new JsonObject { ["text"] = Dashboard("Draft"), ["layout"] = "desk" };
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Post, "/api/v1/views/preview", ada.Id, ServerRole.Viewer, draft)).Status);

        // the owner's list: keyed by the email (lower case), with the model version the statement met
        var list = await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var runs = list.Body!["runs"]!.AsArray();
        Assert.NotEmpty(runs);
        var first = runs[0]!;
        Assert.Equal(("ada@example.invalid", Model, 1, "complete"), ((string)first["caller"]!, (string)first["model"]!, (int)first["model_version"]!, (string)first["status"]!));
        Assert.False(string.IsNullOrEmpty((string?)first["request_hash"]));
        var id = (long)first["id"]!;

        // the detail has the request document that ran; a second person does not find the record, an administrator reads everyone's on request only
        var detail = await ManagementAsync(HttpMethod.Get, $"/api/v1/my/runs/{id}", ada.Id, ServerRole.Viewer);
        Assert.Equal("query", (string)detail.Body!["request"]!["kind"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, $"/api/v1/my/runs/{id}", cy.Id, ServerRole.Viewer)).Status);
        Assert.Empty((await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs", cy.Id, ServerRole.Viewer)).Body!["runs"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs?all=true", cy.Id, ServerRole.Viewer)).Status);
        Assert.NotEmpty((await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs?all=true", cy.Id, ServerRole.Admin)).Body!["runs"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Get, $"/api/v1/my/runs/{id}", cy.Id, ServerRole.Admin)).Status);

        // a second version is published and served; the replay still meets version 1, and gives the same hash and rows
        var published = Get<PublishedSemanticModels>();
        published.Publish(Model, CatalogText + "\n# a second version\n", null, "test", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        published.ServeNow(Model);
        Assert.Equal(2, Get<SemanticModels>().Find(Model)!.Version);
        var replay = await ManagementAsync(HttpMethod.Post, $"/api/v1/my/runs/{id}/replay", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal((id, 1, true), ((long)replay.Body!["replay_of"]!, (int)replay.Body["model_version"]!, (bool)replay.Body["same_hash"]!));
        Assert.NotEmpty(replay.Body["rows"]!.AsArray());
        var again = await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs", ada.Id, ServerRole.Viewer);
        Assert.Equal(id, (long)again.Body!["runs"]![0]!["replay_of"]!);

        // a record that names a version the group does not have: gone, and it says which
        var store = Get<SemanticRunStore>();
        var gone = store.Add(new SemanticRun("ada@example.invalid", Model, 99, "query", "x", new JsonObject { ["kind"] = "query" }, "h", "complete", null, null, null, null, DateTimeOffset.UtcNow, 1, 0));
        var refused = await ManagementAsync(HttpMethod.Post, $"/api/v1/my/runs/{gone}/replay", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.Gone, refused.Status);
        Assert.Contains("Version 99", (string)refused.Body!["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs", ada.Id, ServerRole.None)).Status);
    }

    [Fact]
    public async Task A_refusal_is_recorded_with_its_code_and_counted_and_replaying_it_is_refused_again()
    {
        await StartAsync();
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        var context = new DbDataKit.Serving.ServingContext(new DbDataKit.Serving.ServingCaller("ada", "Ada", new HashSet<string>(), new Dictionary<string, string> { ["email"] = "ada@example.invalid" }));
        var queries = Get<DbDataKit.Serving.IServingServices>().Queries;
        await Assert.ThrowsAsync<DbDataKit.Serving.ServingInvalidException>(async () =>
            await queries.RunAsync(context, new DbDataKit.Serving.ServingRequest.SemanticSql("semantic:" + Model, $"SELECT customers.nowhere, MEASURE(orders.net_revenue) FROM {Model} GROUP BY ALL")));

        var list = await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs", ada.Id, ServerRole.Viewer);
        var run = list.Body!["runs"]![0]!;
        Assert.Equal(("refused", "query"), ((string)run["status"]!, (string)run["kind"]!));
        var replay = await ManagementAsync(HttpMethod.Post, $"/api/v1/my/runs/{(long)run["id"]!}/replay", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, replay.Status);
        var counts = await ManagementAsync(HttpMethod.Get, "/api/v1/my/runs/counts?days=7", ada.Id, ServerRole.Viewer);
        Assert.Equal(7, (int)counts.Body!["days"]!);
        Assert.Equal(1, counts.Body["by_code"]!.AsObject().Sum(c => (int)c.Value!));
    }

    [Fact]
    public async Task The_explorer_runs_semantic_sql_and_a_request_as_the_person_explains_without_reading_and_a_refusal_carries_the_engines_code()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        var sql = $"SELECT customers.region, MEASURE(orders.net_revenue) FROM {Model} GROUP BY ALL";
        const string runs = "/api/v1/my/runs";

        var ran = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["sql"] = sql, ["limit"] = 5 });
        Assert.True(ran.Status == HttpStatusCode.OK, ran.Body?.ToJsonString());
        Assert.Equal("ok", (string)ran.Body!["status"]!);
        Assert.NotEmpty(ran.Body["rows"]!.AsArray());
        Assert.True(ran.Body["rows"]!.AsArray().Count <= 5);
        Assert.False(string.IsNullOrEmpty((string?)ran.Body["request_hash"]));

        // the same as a request; explain answers the scans, as a record of kind explain
        var request = JsonNode.Parse(Request())!;
        var asRequest = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = request.DeepClone() });
        Assert.True(asRequest.Status == HttpStatusCode.OK, asRequest.Body?.ToJsonString());
        var explained = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = request.DeepClone(), ["explain"] = true });
        Assert.Equal("explained", (string)explained.Body!["status"]!);
        Assert.Equal(["source", "engine", "text"], explained.Body["columns"]!.AsArray().Select(c => (string)c!["name"]!));
        var explainedSql = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["sql"] = sql, ["explain"] = true });
        Assert.Equal("explained", (string)explainedSql.Body!["status"]!);
        var kinds = (await ManagementAsync(HttpMethod.Get, runs, ada.Id, ServerRole.Viewer)).Body!["runs"]!.AsArray().Select(r => (string)r!["kind"]!).ToList();
        Assert.Equal(2, kinds.Count(k => k == "explain"));

        // a refusal is a 400 with the code; a person without the data source is refused in the Gateway's words
        var refused = await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["request"] = JsonNode.Parse(Request(measure: "orders.no_such_measure")) });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.False(string.IsNullOrEmpty((string?)refused.Body!["code"]));
        Assert.Contains("orders.no_such_measure", (string)refused.Body["error"]!);
        var denied = await ManagementAsync(HttpMethod.Post, runs, bob.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model, ["sql"] = sql });
        Assert.NotEqual(HttpStatusCode.OK, denied.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = "nope", ["sql"] = "SELECT 1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Post, runs, ada.Id, ServerRole.Viewer, new JsonObject { ["model"] = Model })).Status);
    }
}
