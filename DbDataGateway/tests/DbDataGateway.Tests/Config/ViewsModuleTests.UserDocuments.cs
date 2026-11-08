using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 32, DBR12 on the Gateway host: a person's own dashboards on the management API a console relays (<c>/api/v1/my/...</c>), through the real host (state database with the <c>user_views</c> module, the serving core over the data source
/// that only the home group <c>analysts</c> may read, identity home people as the console vouches for them). The store's and the module's own rules are <c>DbDataKit.Views.Hosting.Tests</c>; this shows what only the Gateway decides:
/// who a person is (the home user, owner by id, groups of the home), and that **the rows follow the viewer's access**, not the owner's.
/// </summary>
public sealed partial class ViewsModuleTests
{
    [Fact]
    public async Task A_person_keeps_their_own_dashboards_shares_them_to_a_home_group_or_a_person_and_everyone_sees_only_what_is_shared_and_the_rows_their_own_access_gives()
    {
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada Lovelace", "ada@example.invalid", Analysts);
        var (cy, _) = HomeSeed.Person(state, "Cy", "cy@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        const string mine = "/api/v1/my/dashboards/board";
        var save = new JsonObject { ["text"] = Dashboard("Ada's board") };

        // Ada saves (a viewer role is all a person's own space needs); a second create and a stale save are conflicts; a bad document is refused in the publish's words and nothing is stored
        var created = await ManagementAsync(HttpMethod.Put, mine, ada.Id, ServerRole.Viewer, save);
        Assert.Equal((HttpStatusCode.OK, "1"), (created.Status, (string)created.Body!["version"]!));
        Assert.Equal(ada.Id.ToLowerInvariant(), (string)created.Body["owner"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await ManagementAsync(HttpMethod.Put, mine, ada.Id, ServerRole.Viewer, save)).Status);
        var wrong = await ManagementAsync(HttpMethod.Put, mine, ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = Dashboard().Replace("orders.net_revenue}", "orders.no_such_measure}"), ["expected_version"] = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("no_such_measure", (string)wrong.Body!["message"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManagementAsync(HttpMethod.Put, mine, ada.Id, ServerRole.None, save)).Status);
        Assert.Empty(Get<GatewayViewsPublishing>().List());   // nothing was published to the group

        // alone: nobody else sees it, and the name is the owner's own
        var adaList = await ManagementAsync(HttpMethod.Get, "/api/v1/my/views", ada.Id, ServerRole.Viewer);
        Assert.Equal("Ada Lovelace", (string)adaList.Body!["items"]![0]!["owner_name"]!);
        Assert.True((bool)adaList.Body["items"]![0]!["own"]!);
        foreach (var other in new[] { cy.Id, bob.Id })
        {
            Assert.Empty((await ManagementAsync(HttpMethod.Get, "/api/v1/my/views", other, ServerRole.Viewer)).Body!["items"]!.AsArray());
            Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Get, $"{mine}?owner={ada.Id}", other, ServerRole.Viewer)).Status);
        }

        // shared to the home group analysts: Cy (in it) opens and renders with rows; Bob (visitors) does not see it at all
        var shares = await ManagementAsync(HttpMethod.Put, mine + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("group:analysts") });
        Assert.Equal("group:analysts", (string)shares.Body!["grantees"]![0]!);
        var render = new JsonObject { ["parameters"] = new JsonObject(), ["layout"] = "desk" };
        var asCy = await ManagementAsync(HttpMethod.Post, $"{mine}/render?owner={ada.Id}", cy.Id, ServerRole.Viewer, render);
        Assert.Equal(HttpStatusCode.OK, asCy.Status);
        Assert.Equal("table", (string)asCy.Body!["widgets"]!["by_region"]!["type"]!);
        Assert.NotEmpty(asCy.Body["widgets"]!["by_region"]!["rows"]!.AsArray());
        Assert.Equal("Ada Lovelace", (string)(await ManagementAsync(HttpMethod.Get, "/api/v1/my/views?scope=shared", cy.Id, ServerRole.Viewer)).Body!["items"]![0]!["owner_name"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(HttpMethod.Post, $"{mine}/render?owner={ada.Id}", bob.Id, ServerRole.Viewer, render)).Status);

        // shared to Bob by his name: he opens it and the rows follow HIS access, which the data source refuses: an error entry per widget, in the data source's words, not Ada's rows
        await ManagementAsync(HttpMethod.Put, mine + "/shares", ada.Id, ServerRole.Viewer, new JsonObject { ["grantees"] = new JsonArray("group:analysts", "user:bob") });
        var asBob = await ManagementAsync(HttpMethod.Post, $"{mine}/render?owner={ada.Id}", bob.Id, ServerRole.Viewer, render);
        Assert.Equal(HttpStatusCode.OK, asBob.Status);
        Assert.Equal("error", (string)asBob.Body!["widgets"]!["by_region"]!["type"]!);
        Assert.Contains("not", (string)asBob.Body["widgets"]!["by_region"]!["message"]!, StringComparison.OrdinalIgnoreCase);
        // ... and he cannot change, share, restore, promote or delete it: those calls are on his own documents, of which he has none
        foreach (var (method, path) in new[] { (HttpMethod.Delete, mine), (HttpMethod.Get, mine + "/history"), (HttpMethod.Get, mine + "/promote"), (HttpMethod.Get, mine + "/shares") })
            Assert.Equal(HttpStatusCode.NotFound, (await ManagementAsync(method, path, bob.Id, ServerRole.Viewer)).Status);

        // history and promote are Ada's: an edit is a new version, the file is the document as the loader reads it
        var edited = await ManagementAsync(HttpMethod.Put, mine, ada.Id, ServerRole.Viewer, new JsonObject { ["text"] = Dashboard("Ada's board, edited"), ["expected_version"] = 1 });
        Assert.Equal("2", (string)edited.Body!["version"]!);
        Assert.Equal(2, (await ManagementAsync(HttpMethod.Get, mine + "/history", ada.Id, ServerRole.Viewer)).Body!["items"]!.AsArray().Count);
        var promoted = await ManagementAsync(HttpMethod.Get, mine + "/promote", ada.Id, ServerRole.Viewer);
        Assert.Equal("dashboards/board.yaml", (string)promoted.Body!["path"]!);
        Assert.Contains("Ada's board, edited", (string)promoted.Body["text"]!);

        // audited as the person, never the text
        var events = new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId).Read(new AuditQuery(VerbPrefix: "view.my.", Limit: 200));
        Assert.Contains(events, e => e.Entry.Verb == "view.my.save" && e.Entry.ActorId == ada.Id && e.Entry.Outcome == "ok");
        Assert.Contains(events, e => e.Entry.Verb == "view.my.render" && e.Entry.ActorId == bob.Id && e.Entry.Outcome == "ok");
        Assert.Contains(events, e => e.Entry.Verb == "view.my.render" && e.Entry.ActorId == bob.Id && e.Entry.Outcome == "refused");
        Assert.DoesNotContain(events, e => (e.Entry.Detail ?? "").Contains("Ada's board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gateway_without_the_views_module_answers_a_persons_own_dashboards_with_404_in_words()
    {
        await StartAsync(views: false);
        var (ada, _) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        var reply = await ManagementAsync(HttpMethod.Get, "/api/v1/my/views", ada.Id, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        Assert.Contains("modules", (string)reply.Body!["message"]!);
    }
}
