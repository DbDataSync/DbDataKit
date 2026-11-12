using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.Catalog;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The producer of <c>catalog-cache.yaml</c> (plan 33 SC20j, D20): the Workspace asks a paired Gateway, which holds the data source's login, for the tables and columns of a data source, and writes the answer into the draft in the format
/// <c>catalog-cache/1</c> after the strict reader accepted it. The Workspace holds no login and reads no row; a data source made by hand is asked for only with the person's confirmation (<c>adopt</c>). The Gateway here is a fake with real pairing.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceCatalogRefreshTests
{
    private const string Project = "ledger";

    private static JsonObject Warehouse(string columnType = "int64") => new()
    {
        ["data_source"] = "warehouse", ["engine"] = "postgres", ["truncated"] = false,
        ["tables"] = new JsonArray
        {
            new JsonObject { ["schema"] = "public", ["name"] = "orders", ["kind"] = "table", ["columns"] = new JsonArray
            {
                new JsonObject { ["name"] = "id", ["type"] = columnType, ["nullable"] = false, ["key"] = true },
                new JsonObject { ["name"] = "placed_at", ["type"] = "timestamp", ["nullable"] = true, ["key"] = false },
            } },
            new JsonObject { ["schema"] = "public", ["name"] = "order_totals", ["kind"] = "view", ["columns"] = new JsonArray { new JsonObject { ["name"] = "total", ["type"] = "decimal(18,2)", ["nullable"] = true, ["key"] = false } } },
        },
    };

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "store" }.ToJsonString())).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task Pair(WorkspaceHarness server, FakeGateway gateway, string name = "edge") =>
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, $"/api/v1/publisher-keys/{name}/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString())).Response.StatusCode);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Refresh(WorkspaceHarness server, string workspace, JsonObject body) =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/catalog/refresh", body.ToJsonString());

    private static async Task<string> CacheOf(WorkspaceHarness server, string workspace) =>
        (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/catalog-cache.yaml")).Body!["content"]!;

    [Fact]
    public async Task The_catalog_a_paired_gateway_answers_is_written_into_the_draft_as_a_cache_the_strict_reader_accepts_and_another_connection_keeps_what_it_had()
    {
        await using var gateway = await FakeGateway.StartAsync();
        gateway.Schemas["warehouse"] = Warehouse();
        gateway.Schemas["crm"] = new JsonObject { ["engine"] = "sqlserver", ["tables"] = new JsonArray { new JsonObject { ["schema"] = "dbo", ["name"] = "customers", ["kind"] = "table", ["columns"] = new JsonArray { new JsonObject { ["name"] = "id", ["type"] = "int32", ["nullable"] = false, ["key"] = true } } } } };
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);

        var first = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "warehouse" });
        Assert.Equal(HttpStatusCode.OK, first.Response.StatusCode);
        Assert.Equal((2, 3, "catalog-cache.yaml"), ((int)first.Body!["tables"]!, (int)first.Body["columns"]!, (string)first.Body["file"]!));
        var load = CatalogCache.Load(await CacheOf(server, workspace));
        Assert.True(load.Ok, string.Join("; ", load.Diagnostics));
        var warehouse = load.Cache!.Connections["warehouse"];
        Assert.Equal("postgres", warehouse.Engine);
        Assert.Equal("edge", load.Cache.Server);
        Assert.Equal("gateway", load.Cache.Product);
        var orders = warehouse.Tables.Single(t => t.Name == "orders");
        Assert.Equal(("public", "table"), (orders.Schema, orders.Kind));
        Assert.Equal([("id", "int64", false, true), ("placed_at", "timestamp", true, false)], orders.Columns.Select(c => (c.Name, c.Type, c.Nullable, c.Key)));
        Assert.Equal("view", warehouse.Tables.Single(t => t.Name == "order_totals").Kind);
        Assert.Contains("POST /api/v1/data-sources/warehouse/schema as client:worksp", Assert.Single(gateway.Calls));   // the paired client asked; nothing else was called

        // a second connection is added, the first stays; the draft's check is sound with the cache in it
        Assert.Equal(HttpStatusCode.OK, (await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "customers-db", ["data_source"] = "crm" })).Response.StatusCode);
        var both = CatalogCache.Load(await CacheOf(server, workspace)).Cache!;
        Assert.Equal(["customers-db", "warehouse"], both.Connections.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("sqlserver", both.Connections["customers-db"].Engine);
        var checkedDraft = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/check");
        Assert.True((bool)checkedDraft.Body!["ok"]!, checkedDraft.Body.ToJsonString());
    }

    [Fact]
    public async Task A_data_source_made_by_hand_is_asked_for_only_with_the_confirmation_and_the_gateway_records_it()
    {
        await using var gateway = await FakeGateway.StartAsync();
        gateway.Schemas["legacy"] = Warehouse();
        gateway.HandMade.Add("legacy");
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        var workspace = await DraftAsync(server);

        var asked = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "legacy" });
        Assert.Equal(HttpStatusCode.Conflict, asked.Response.StatusCode);
        Assert.Equal(("legacy", "hand"), ((string)asked.Body!["adopt"]!["data_source"]!, (string)asked.Body["adopt"]!["made_by"]!));
        Assert.Empty(gateway.Adoptions);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/files/catalog-cache.yaml")).Response.StatusCode);

        var confirmed = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "legacy", ["adopt"] = true });
        Assert.Equal(HttpStatusCode.OK, confirmed.Response.StatusCode);
        Assert.Contains("legacy schema-read by ", Assert.Single(gateway.Adoptions));
        Assert.True(CatalogCache.Load(await CacheOf(server, workspace)).Ok);
    }

    [Fact]
    public async Task What_cannot_be_cached_is_refused_in_words_and_nothing_is_written_a_runner_is_not_a_source_and_a_damaged_cache_is_not_overwritten()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var runner = await FakeRunner.StartAsync();
        gateway.Schemas["warehouse"] = Warehouse(columnType: "password=hunter2");     // a type that looks like a login: the strict reader refuses the file
        await using var server = await WorkspaceHarness.StartAsync();
        await Pair(server, gateway);
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode() }.ToJsonString())).Response.StatusCode);
        var workspace = await DraftAsync(server);
        var file = $"/api/v1/projects/{Project}/workspaces/{workspace}/files/catalog-cache.yaml";

        var looksLikeSecret = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "warehouse" });
        Assert.Equal(HttpStatusCode.Conflict, looksLikeSecret.Response.StatusCode);
        Assert.Contains("cannot be cached", (string)looksLikeSecret.Body!["error"]!);
        Assert.DoesNotContain("hunter2", looksLikeSecret.Body.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, file)).Response.StatusCode);

        var notGateway = await Refresh(server, workspace, new JsonObject { ["target"] = "jobs", ["connection"] = "warehouse" });
        Assert.Equal(HttpStatusCode.Conflict, notGateway.Response.StatusCode);
        Assert.Contains("read from a Gateway", (string)notGateway.Body!["error"]!);

        var unknown = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "nowhere" });
        Assert.Contains("There is no data source 'nowhere'", (string)unknown.Body!["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(server, workspace, new JsonObject { ["target"] = "edge" })).Response.StatusCode);

        gateway.Schemas["warehouse"] = Warehouse();
        Assert.True((await server.AsAda(HttpMethod.Put, file, """{"content":"schema: catalog-cache/1\nrows: [1]\n"}""")).Response.IsSuccessStatusCode);
        var damaged = await Refresh(server, workspace, new JsonObject { ["target"] = "edge", ["connection"] = "warehouse" });
        Assert.Equal(HttpStatusCode.Conflict, damaged.Response.StatusCode);
        Assert.Contains("is damaged", (string)damaged.Body!["error"]!);
        Assert.Contains("rows: [1]", (string)(await server.AsAda(HttpMethod.Get, file)).Body!["content"]!);       // not overwritten
    }
}
