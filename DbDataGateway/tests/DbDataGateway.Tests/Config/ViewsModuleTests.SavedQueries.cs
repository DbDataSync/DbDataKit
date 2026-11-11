using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 14a SL17 (D20) on the Gateway host: a person's own saved queries on the management API a console relays (<c>/api/v1/my/queries</c>), through the real host. The store's own rules are <c>SavedQueryStoreTests</c>; this shows what only
/// the Gateway decides: who a person is, that a query is checked against its model when it is saved (a missing member is named), the stale save that says where the query is, history and restore, and that sharing shows the text to
/// the grantee and to no one else.
/// </summary>
public sealed partial class ViewsModuleTests
{
    private static string Request(string measure = "orders.net_revenue", string attribute = "customers.region") =>
        "{\"spec_version\": \"0.3\", \"kind\": \"query\", \"query\": {\"attributes\": [\"" + attribute + "\"], \"measures\": [\"" + measure + "\"]}}";

    [Fact]
    public async Task A_person_saves_a_query_on_a_model_checked_against_it_keeps_its_history_and_shares_the_text_with_a_person_a_group_or_everyone()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (cy, _) = HomeSeed.Person(state, "Cy", "cy@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        string mine(string name = "regions") => $"/api/v1/my/queries/{Model}/{name}";
        JsonObject body(string request, int? expected = null, string? description = null) =>
            new() { ["request"] = JsonNode.Parse(request), ["description"] = description, ["expected_version"] = expected };

        // saved: version 1; the same name again without the version read is a conflict that says where the query is
        var created = await ManagementAsync(HttpMethod.Put, mine(), ada.Id, ServerRole.Viewer, body(Request(), description: "Revenue by region"));
        Assert.Equal((HttpStatusCode.OK, 1), (created.Status, (int)created.Body!["version"]!));
        var stale = await ManagementAsync(HttpMethod.Put, mine(), ada.Id, ServerRole.Viewer, body(Request()));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal(1, (int)stale.Body!["current_version"]!);
        Assert.Contains("version 1", (string)stale.Body["error"]!);

        // checked against the model: a member it does not have is named, in a request and in semantic SQL, and nothing is stored
        var missing = await ManagementAsync(HttpMethod.Put, mine("broken"), ada.Id, ServerRole.Viewer, body(Request(measure: "orders.no_such_measure")));
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("orders.no_such_measure", (string)missing.Body!["error"]!);
        var sqlMissing = await ManagementAsync(HttpMethod.Put, mine("broken"), ada.Id, ServerRole.Viewer,
            new JsonObject { ["sql"] = $"SELECT customers.nowhere, MEASURE(orders.net_revenue) FROM {Model} GROUP BY ALL" });
        Assert.Equal(HttpStatusCode.BadRequest, sqlMissing.Status);
        Assert.Contains("customers.nowhere", (string)sqlMissing.Body!["error"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, mine("broken"), ada.Id, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Put, $"/api/v1/my/queries/no_model/regions", ada.Id, ServerRole.Viewer, body(Request()))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Put, mine("both"), ada.Id, ServerRole.Viewer, new JsonObject { ["sql"] = "SELECT 1", ["request"] = JsonNode.Parse(Request()) })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Put, mine(), ada.Id, ServerRole.None, body(Request(), 1))).Status);

        // an edit is a new version; history lists both; restoring the first saves it again as version 3
        var edited = await ManagementAsync(HttpMethod.Put, mine(), ada.Id, ServerRole.Viewer, body(Request(attribute: "customers.region").Replace("customers.region", "customers.region"), 1, "Revenue by region, edited"));
        Assert.Equal(2, (int)edited.Body!["version"]!);
        Assert.Equal(2, (await ManagementAsync(HttpMethod.Get, mine() + "/history", ada.Id, ServerRole.Viewer)).Body!["versions"]!.AsArray().Count);
        var restored = await ManagementAsync(HttpMethod.Post, mine() + "/restore", ada.Id, ServerRole.Viewer, new JsonObject { ["version"] = 1 });
        Assert.Equal((3, "Revenue by region"), ((int)restored.Body!["version"]!, (string)restored.Body["description"]!));

        // alone: nobody else lists or opens it
        Assert.Single((await ManagementAsync(HttpMethod.Get, "/api/v1/my/queries", ada.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());
        foreach (var other in new[] { cy.Id, bob.Id })
        {
            Assert.Empty((await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", other, ServerRole.Viewer)).Body!["queries"]!.AsArray());
            Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, mine() + "?owner=Ada", other, ServerRole.Viewer)).Status);
        }

        // shared to Cy by name: Cy lists and opens the text; Bob still does not. Sharing again with another set replaces it (the whole set is sent)
        var shared = await ManagementAsync(HttpMethod.Put, mine() + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("user:cy") });
        Assert.Equal("user:cy", (string)shared.Body!["grantees"]![0]!);
        var cyList = (await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", cy.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray();
        Assert.Equal(("regions", "Ada", false), ((string)cyList[0]!["name"]!, (string)cyList[0]!["owner"]!, (bool)cyList[0]!["own"]!));
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Get, mine() + "?owner=Ada", cy.Id, ServerRole.Viewer)).Status);
        Assert.Empty((await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", bob.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());

        await ManagementAsync(HttpMethod.Put, mine() + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("group:analysts") });
        Assert.Single((await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", cy.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());
        await ManagementAsync(HttpMethod.Put, mine() + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("*") });
        Assert.Single((await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", bob.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await ManagementAsync(HttpMethod.Put, mine() + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("somebody") })).Status);

        // a grantee cannot change, share, restore or delete it: those calls are on their own queries, of which they have none
        foreach (var (method, path) in new[] { (HttpMethod.Delete, mine()), (HttpMethod.Get, mine() + "/history"), (HttpMethod.Get, mine() + "/shares") })
            Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(method, path, bob.Id, ServerRole.Viewer)).Status);

        // removed by the owner; audited as saved-query changes, never the text
        Assert.Equal(HttpStatusCode.OK, (await ManagementAsync(HttpMethod.Delete, mine(), ada.Id, ServerRole.Viewer)).Status);
        Assert.Empty((await ManagementAsync(HttpMethod.Get, "/api/v1/my/queries", ada.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());
        Assert.Empty((await ManagementAsync(HttpMethod.Get, $"/api/v1/my/queries?model={Model}", bob.Id, ServerRole.Viewer)).Body!["queries"]!.AsArray());
    }

    [Fact]
    public async Task A_gateway_without_the_views_module_answers_saved_queries_with_404_in_words()
    {
        await StartAsync(views: false);
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        var answer = await ManagementAsync(HttpMethod.Get, "/api/v1/my/queries", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.Contains("views", (string)answer.Body!["error"]!);
    }
}
