using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The sync Designer's questions of a draft (<see cref="SyncDesigner"/>), answered in the Workspace's process from Sync's libraries and the draft's
/// catalog cache, with no database and no child process. The draft is the sync template (connections <c>source-db</c>, SQL Server, and
/// <c>target-db</c>, PostgreSQL; the replication <c>orders</c> with its mapping <c>orders</c>) with the replication's endpoints set and a cache of both
/// connections.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSyncDesignerTests
{
    private const string Task = """
        schemaVersion: 2
        name: orders
        scheduling:
          mode: Periodic
          cronExpression: '*/15 * * * *'
        changeProcessing:
          reader:
            kind: MsSqlChangeTracking
            options: {}
          cache:
            kind: MsSqlStagingTable
            options: {}
          writer:
            kind: ChangeApply
            options: {}
        endpoints:
          source:
            connectionName: source-db
            database: App
          target:
            connectionName: target-db
            database: dw
        """;

    private const string Cache = """
        schema: catalog-cache/2
        generated_at: 2026-11-20T10:00:00Z
        connections:
          source-db:
            engine: sqlserver
            read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-20T10:00:00Z}
            databases:
              - {name: App, schemas: [dbo]}
            tables:
              - database: App
                schema: dbo
                name: Orders
                kind: table
                columns:
                  - {name: id, type: bigint, nullable: false, key: true}
                  - {name: name, type: "nvarchar(50)", nullable: true}
                  - {name: amount, type: "decimal(18,2)", nullable: true}
                  - {name: shape, type: geography, nullable: true}
              - database: App
                schema: dbo
                name: Customers
                kind: table
                columns:
                  - {name: customer_id, type: int, nullable: false, key: true}
                  - {name: name, type: "nvarchar(100)", nullable: true}
              - database: App
                schema: dbo
                name: Lines
                kind: table
                columns:
                  - {name: order_id, type: bigint, nullable: false}
                  - {name: qty, type: int, nullable: true}
          target-db:
            engine: postgres
            read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-20T10:00:00Z}
            databases:
              - {name: dw, schemas: [public, dbo]}
            tables:
              - database: dw
                schema: dbo
                name: Customers
                kind: table
                columns:
                  - {name: customer_id, type: integer, nullable: false, key: true}
                  - {name: name, type: "character varying(100)", nullable: true}
        """;

    private static async Task<string> DraftAsync(WorkspaceHarness server, bool cache = true)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders-set","kind":"sync"}""")).Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders-set/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        Assert.True((await Put(server, id, "config/replications/orders/task.yaml", Task)).Response.IsSuccessStatusCode);
        if (cache) Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);
        return id;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Put(WorkspaceHarness server, string id, string path, string text) =>
        server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/{path}", new JsonObject { ["content"] = text.ReplaceLineEndings("\n") }.ToJsonString());

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Ask(WorkspaceHarness server, string id, string route, JsonObject body, ServerRole role = ServerRole.Author) =>
        server.Send(role == ServerRole.Author ? "ada" : "vic", role, HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/sync/{route}", body.ToJsonString());

    private static JsonObject Kind(JsonNode? list, string kind) => Assert.Single(list!.AsArray(), k => (string)k!["kind"]! == kind)!.AsObject();

    [Fact]
    public async Task The_slots_a_binding_can_name_come_with_the_levels_each_binds_at()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);

        var answer = await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/sync/script-slots");
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        var slots = answer.Body!["slots"]!.AsArray();
        var column = Assert.Single(slots, s => (string)s!["slot"]! == "sqlColumnExpression")!;
        Assert.Equal(["connection", "replication", "mapping"], column["levels"]!.AsArray().Select(l => (string)l!));
        Assert.False(string.IsNullOrEmpty((string?)column["label"]));
        var metadata = Assert.Single(slots, s => (string)s!["slot"]! == "metadataProvider")!;
        Assert.Equal(["connection"], metadata["levels"]!.AsArray().Select(l => (string)l!));
        Assert.DoesNotContain(slots, s => (string)s!["slot"]! == "hook");
    }

    [Fact]
    public async Task A_SQL_Server_connection_offers_its_readers_and_writers_with_the_key_each_needs()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);

        var answer = await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "source-db" }, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        var body = answer.Body!;
        Assert.Equal("MsSql", (string)body["driverType"]!);
        var tracking = Kind(body["readers"], "MsSqlChangeTracking");
        Assert.Equal("SourceKey", (string)tracking["needsKey"]!);
        Assert.True((bool)tracking["detectsDeletes"]!);
        Assert.Equal(["Changes", "ChangesFromEarliest", "ChangesFromLatest"], tracking["supportedIntents"]!.AsArray().Select(i => (string)i!));
        Assert.Contains(tracking["parameters"]!.AsArray(), p => (string)p!["name"]! == "snapshotIsolation");
        Assert.Equal("None", (string)Kind(body["readers"], "Watermark")["needsKey"]!);
        Assert.Equal("TargetKey", (string)Kind(body["writers"], "MsSqlMerge")["needsKey"]!);
        Assert.True((bool)Kind(body["writers"], "MsSqlBulkInsert")["supportsReconciliation"]!);
        Assert.Contains(body["stagingProviders"]!.AsArray(), s => (string)s!["kind"]! == "MsSqlStagingTable");

        var postgres = (await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "target-db" })).Body!;
        Assert.Equal("SourceKey", (string)Kind(postgres["readers"], "PgLogicalSlot")["needsKey"]!);
        Assert.DoesNotContain(postgres["writers"]!.AsArray(), w => ((string)w!["kind"]!).StartsWith("MsSql", StringComparison.Ordinal));
        Assert.True((bool)body["keepsStateTable"]!);
        Assert.True((bool)postgres["keepsStateTable"]!);
    }

    [Fact]
    public async Task A_MySQL_connection_says_its_target_keeps_no_state_table()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "config/connections/mart.yaml", "schema: connection/1\nname: mart\nengine: mysql\naddress:\n  host: mart.example.com\n  database: mart\nauth:\n  mode: none\n")).Response.IsSuccessStatusCode);

        var answer = await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "mart" });
        Assert.True(answer.Response.StatusCode == HttpStatusCode.OK, answer.Body?.ToJsonString());
        Assert.Equal("MySql", (string)answer.Body!["driverType"]!);
        Assert.False((bool)answer.Body["keepsStateTable"]!);
        Assert.NotEmpty(answer.Body["writers"]!.AsArray());
    }

    [Fact]
    public async Task A_driver_the_draft_carries_is_described_from_its_driver_yaml_and_a_missing_one_is_refused_in_words()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "drivers/legacy/driver.yaml", """
            id: legacy
            displayName: Legacy
            library: system-data-odbc
            dialect:
              quoteIdentifier: bracket
              parameterPrefix: "@"
              rowLimit: topN
            capabilities:
              readers: [Watermark, BatchReload]
              staging: []
              writers: []
            """)).Response.IsSuccessStatusCode);
        Assert.True((await Put(server, id, "config/connections/old.yaml", "schema: connection/1\nname: old\nengine: legacy\naddress:\n  host: old.example.com\n  database: app\nauth:\n  mode: none\n")).Response.IsSuccessStatusCode);
        Assert.True((await Put(server, id, "config/connections/gone.yaml", "schema: connection/1\nname: gone\nengine: compiled-one\naddress:\n  host: gone.example.com\n  database: app\nauth:\n  mode: none\n")).Response.IsSuccessStatusCode);

        var legacy = await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "old" });
        Assert.True(legacy.Response.StatusCode == HttpStatusCode.OK, legacy.Body?.ToJsonString());
        Assert.Equal(["Watermark", "BatchReload"], legacy.Body!["readers"]!.AsArray().Select(r => (string)r!["kind"]!));
        Assert.Empty(legacy.Body["writers"]!.AsArray());
        Assert.False((bool)legacy.Body["keepsStateTable"]!);

        var gone = await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "gone" });
        Assert.Equal(HttpStatusCode.NotFound, gone.Response.StatusCode);
        Assert.Contains("not known offline", (string)gone.Body!["error"]!);

        var missing = await Ask(server, id, "capabilities", new JsonObject { ["connection"] = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, missing.Response.StatusCode);
        Assert.Contains("config/connections/nope.yaml", (string)missing.Body!["error"]!);
    }

    [Fact]
    public async Task A_mappings_target_types_and_natural_key_are_inferred_from_the_cached_source()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "config/replications/orders/table-mappings/orders.yaml", """
            schemaVersion: 1
            name: orders
            sources:
            - schema: dbo
              table: Orders
            targets:
            - schema: public
              table: orders
            columnMappings:
            - sourceColumn: id
              targetColumn: order_id
            - sourceColumn: name
              targetColumn: name
            """)).Response.IsSuccessStatusCode);

        var answer = await Ask(server, id, "inferred-columns", new JsonObject { ["replication"] = "orders", ["mapping"] = "orders" }, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        Assert.True(answer.Body!["problem"] is null, answer.Body.ToJsonString());
        var columns = answer.Body!["columns"]!.AsArray();
        Assert.Equal(["id", "name", "amount", "shape"], columns.Select(c => (string)c!["sourceColumn"]!));
        Assert.Equal("bigint", (string)columns[0]!["targetType"]!);
        Assert.Equal("varchar(50)", (string)columns[1]!["targetType"]!);
        Assert.Equal("numeric(18,2)", (string)columns[2]!["targetType"]!);
        Assert.Null(columns[3]!["targetType"]);
        Assert.False(string.IsNullOrEmpty((string?)columns[3]!["problem"]));
        // the source's key in the target's names
        Assert.Equal(["order_id"], answer.Body["naturalKey"]!["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Null(answer.Body["problem"]);
    }

    [Fact]
    public async Task Create_mappings_answers_a_file_for_each_ticked_table_and_writes_nothing()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);

        var answer = await Ask(server, id, "bulk-create", new JsonObject
        {
            ["replication"] = "orders",
            ["tables"] = new JsonArray(new JsonObject { ["schema"] = "dbo", ["table"] = "Customers" }, new JsonObject { ["schema"] = "dbo", ["table"] = "Lines" }, new JsonObject { ["schema"] = "dbo", ["table"] = "Customers" }),
        });
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        var body = answer.Body!;
        Assert.Equal(["dbo.Customers", "dbo.Lines"], body["created"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["dbo.Customers"], body["skipped"]!.AsArray().Select(c => (string)c!));
        var files = body["files"]!.AsArray();
        Assert.Equal(["config/replications/orders/table-mappings/dbo.Customers.yaml", "config/replications/orders/table-mappings/dbo.Lines.yaml"], files.Select(f => (string)f!["path"]!));
        var customers = (string)files[0]!["text"]!;
        Assert.Contains("sourceColumn: customer_id", customers);
        Assert.Contains("targetColumns:", customers);   // the target is in the cache, so its shape is captured
        Assert.DoesNotContain("targetType", customers);
        // the target of Lines is not in the cache: every source column is mapped, and the note says provisioning creates it
        Assert.Contains("sourceColumn: qty", (string)files[1]!["text"]!);
        Assert.Contains(body["notes"]!.AsArray(), n => (string)n!["mapping"]! == "dbo.Lines" && (string)n["side"]! == "target");
        // what would be inferred: Customers has a key, Lines has none
        Assert.Equal(["customer_id"], body["inferred"]!["dbo.Customers"]!["naturalKey"]!["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Contains("no primary key", (string)body["inferred"]!["dbo.Lines"]!["naturalKey"]!["problem"]!);
        Assert.Equal("integer", (string)body["inferred"]!["dbo.Lines"]!["columns"]![1]!["targetType"]!);

        // nothing was written into the draft
        var listed = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files")).Body!["files"]!.AsArray();
        Assert.DoesNotContain(listed, f => ((string)f!["path"]!).Contains("dbo.Customers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_viewer_is_refused_Create_mappings()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);

        var answer = await Ask(server, id, "bulk-create", new JsonObject { ["replication"] = "orders", ["tables"] = new JsonArray(new JsonObject { ["schema"] = "dbo", ["table"] = "Customers" }) }, ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, answer.Response.StatusCode);
    }

    [Fact]
    public async Task The_unsaved_edits_sent_with_a_question_are_part_of_the_answer()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server, cache: false);
        var unsaved = new JsonObject { ["catalog-cache.yaml"] = Cache.ReplaceLineEndings("\n") };

        // the saved draft has no cache: nothing is captured
        var saved = await Ask(server, id, "bulk-create", new JsonObject { ["replication"] = "orders", ["tables"] = new JsonArray(new JsonObject { ["schema"] = "dbo", ["table"] = "Customers" }) });
        Assert.Contains(saved.Body!["notes"]!.AsArray(), n => ((string)n!["reason"]!).Contains("catalog-cache.yaml", StringComparison.Ordinal));

        // with the person's unsaved refresh, the columns are there
        var withEdits = await Ask(server, id, "bulk-create", new JsonObject { ["replication"] = "orders", ["tables"] = new JsonArray(new JsonObject { ["schema"] = "dbo", ["table"] = "Customers" }), ["unsaved"] = unsaved });
        Assert.Equal(HttpStatusCode.OK, withEdits.Response.StatusCode);
        Assert.Contains("sourceColumn: customer_id", (string)withEdits.Body!["files"]![0]!["text"]!);

        // only a file an answer reads may be sent
        var outside = await Ask(server, id, "bulk-create", new JsonObject { ["replication"] = "orders", ["tables"] = new JsonArray(new JsonObject { ["table"] = "x" }), ["unsaved"] = new JsonObject { ["../secrets/x.yaml"] = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.Response.StatusCode);
    }

    [Theory]
    [InlineData("capabilities", """{"connection":"../x"}""")]
    [InlineData("inferred-columns", """{"replication":"orders"}""")]
    [InlineData("bulk-create", """{"replication":"orders","tables":[]}""")]
    [InlineData("bulk-create", """{"replication":"orders","tables":[{"schema":"dbo"}]}""")]
    public async Task A_question_that_does_not_name_what_it_asks_about_is_refused(string route, string body)
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);

        var answer = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/sync/{route}", body);
        Assert.Equal(HttpStatusCode.BadRequest, answer.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty((string?)answer.Body!["error"]));
    }
}
