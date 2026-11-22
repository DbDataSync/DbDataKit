using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Authoring.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The offline checks of a sync draft (<see cref="SyncOfflineCheck"/>): each mapping read against the draft's <c>catalog-cache.yaml</c> and its connection files, in the
/// Workspace's process, every finding with its file and line. The draft below maps <c>crm.dbo.orders</c> (SQL Server) into <c>dw.public.orders</c> (PostgreSQL).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSyncOfflineCheckTests
{
    private const string MappingFile = "config/replications/orders/table-mappings/orders.yaml";

    private const string Task = """
        schemaVersion: 2
        name: orders
        endpoints:
          source:
            connectionName: crm
            database: crm
          target:
            connectionName: dw
            database: dw
        """;

    // the line numbers the tests name are those of this text
    private const string MappingText = """
        schemaVersion: 1
        name: orders
        sources:
        - schema: dbo
          table: orders
          filter: status = 'open' AND regoin_code > 3
        targets:
        - schema: public
          table: orders
        columnMappings:
        - sourceColumn: id
          targetColumn: id
        - sourceColumn: shipped_on
          targetColumn: shipped_on
        - sourceColumn: location
          targetColumn: location
        - sourceColumn: amount
          targetColumn: amount
        - sourceColumn: note
          targetColumn: note
        - sourceColumn: status
          targetColumn: status
        primaryKey:
        - id
        """;

    private const string CacheText = """
        schema: catalog-cache/2
        generated_at: 2026-11-18T10:00:00Z
        connections:
          crm:
            engine: sqlserver
            read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:00Z}
            databases:
              - {name: crm, schemas: [dbo]}
            tables:
              - database: crm
                schema: dbo
                name: orders
                kind: table
                columns:
                  - {name: id, type: int, nullable: false, key: true}
                  - {name: status, type: "nvarchar(20)", nullable: true}
                  - {name: region_code, type: int, nullable: true}
                  - {name: created, type: datetime2, nullable: true}
                  - {name: location, type: geography, nullable: true}
                  - {name: amount, type: "decimal(18,2)", nullable: true}
                  - {name: note, type: "nvarchar(max)", nullable: true}
          dw:
            engine: postgres
            read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:00Z}
            databases:
              - {name: dw, schemas: [public]}
            tables:
              - database: dw
                schema: public
                name: orders
                kind: table
                columns:
                  - {name: order_no, type: bigint, nullable: false, key: true}
                  - {name: id, type: integer, nullable: true}
                  - {name: status, type: "character varying(40)", nullable: true}
                  - {name: amount, type: integer, nullable: true}
                  - {name: note, type: bytea, nullable: true}
        """;

    // the source file may be checked out with CRLF; the tests edit the texts by lines
    private static readonly string Mapping = MappingText.ReplaceLineEndings("\n");
    private static readonly string Cache = CacheText.ReplaceLineEndings("\n");

    private static string Draft(string? mapping = null, string? cache = "", string sourceEngine = "sqlserver")
    {
        var root = Directory.CreateTempSubdirectory("ddw-sync-offline-").FullName;
        void Write(string path, string text)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.ReplaceLineEndings("\n") + "\n");
        }
        Write("config/connections/crm.yaml", $"schema: connection/1\nname: crm\nengine: {sourceEngine}\naddress:\n  host: crm.example.com\n  database: crm");
        Write("config/connections/dw.yaml", "schema: connection/1\nname: dw\nengine: postgres\naddress:\n  host: dw.example.com\n  database: dw");
        Write("config/replications/orders/task.yaml", Task);
        Write(MappingFile, mapping ?? Mapping);
        if (cache is not null) Write("catalog-cache.yaml", cache.Length == 0 ? Cache : cache);   // "" stands for the draft's usual cache
        return root;
    }

    private static IReadOnlyList<ProjectDiagnostic> CheckOf(string root)
    {
        try { return SyncOfflineCheck.Check(root); }
        finally { Directory.Delete(root, true); }
    }

    private static ProjectDiagnostic One(IReadOnlyList<ProjectDiagnostic> found, string code, string words) =>
        Assert.Single(found, d => d.Code == code && d.Message.Contains(words, StringComparison.Ordinal));

    [Fact]
    public void Each_check_reports_its_finding_with_the_mapping_file_and_line()
    {
        var found = CheckOf(Draft());

        var missing = One(found, SyncOfflineCheck.Column, "'shipped_on'");
        Assert.Equal(("warning", MappingFile, (int?)13), (missing.Severity, missing.File, missing.Line));
        Assert.Contains("crm.dbo.orders", missing.Message);
        Assert.Contains("channel dev", missing.Message);

        var filter = One(found, SyncOfflineCheck.Column, "'regoin_code'");
        Assert.Equal(6, filter.Line);
        Assert.Contains("source filter", filter.Message);

        var unknown = One(found, SyncOfflineCheck.UnknownType, "'location'");
        Assert.Equal(("warning", (int?)15), (unknown.Severity, unknown.Line));
        Assert.Contains("geography", unknown.Message);
        Assert.Contains("sqlserver driver's type map", unknown.Message);

        var incompatible = One(found, SyncOfflineCheck.Types, "'note'");
        Assert.Equal(20, incompatible.Line);
        Assert.Contains("not a type the target column can hold", incompatible.Message);
        var lossy = One(found, SyncOfflineCheck.Types, "'amount'");
        Assert.Equal(18, lossy.Line);
        Assert.Contains("may not fit", lossy.Message);

        var nullable = One(found, SyncOfflineCheck.Key, "nullable on the target");
        Assert.Equal((MappingFile, (int?)24), (nullable.File, nullable.Line));
        Assert.Contains("'id'", nullable.Message);
        var notTheKey = One(found, SyncOfflineCheck.Key, "is not the primary key");
        Assert.Contains("(order_no)", notTheKey.Message);

        // the rest agrees: id, status and the filter's status are checked and say nothing; every finding is a warning, never an error
        Assert.Equal(7, found.Count);
        Assert.All(found, d => Assert.Equal("warning", d.Severity));
    }

    [Fact]
    public void A_draft_with_no_cache_reports_none_of_the_checks_and_says_so()
    {
        var found = CheckOf(Draft(cache: null));
        var none = Assert.Single(found);
        Assert.Equal(("info", SyncOfflineCheck.NoCache, (string?)null), (none.Severity, none.Code, none.File));
        Assert.Contains("no catalog-cache.yaml", none.Message);
    }

    [Fact]
    public void A_damaged_cache_is_left_to_its_own_check_and_the_mappings_are_said_to_be_unchecked()
    {
        var found = CheckOf(Draft(cache: "schema: catalog-cache/1\nconnections: {}\n"));
        var none = Assert.Single(found);
        Assert.Equal((SyncOfflineCheck.NotChecked, "catalog-cache.yaml"), (none.Code, none.File));
    }

    [Fact]
    public void A_source_table_missing_from_the_cache_is_reported_on_its_table_line_and_its_columns_are_not_checked()
    {
        var found = CheckOf(Draft(Mapping.Replace("  table: orders\n  filter", "  table: order\n  filter")));
        var table = One(found, SyncOfflineCheck.Table, "crm.dbo.order ");
        Assert.Equal(5, table.Line);
        Assert.DoesNotContain(found, d => d.Code is SyncOfflineCheck.Column or SyncOfflineCheck.UnknownType or SyncOfflineCheck.Types);
    }

    [Theory]
    [InlineData("status = 'open", "is not closed")]
    [InlineData("status = 'open'; DROP TABLE orders", "';'")]
    [InlineData("id IN (SELECT id FROM dbo.closed)", "subquery")]
    [InlineData("(status = 'open'", "parentheses")]
    public void A_filter_the_lexer_cannot_read_is_not_checked_offline(string filter, string why)
    {
        var found = CheckOf(Draft(Mapping.Replace("status = 'open' AND regoin_code > 3", filter.Replace("'", "''")).Replace("filter: ", "filter: '").Replace("\ntargets:", "'\ntargets:")));
        var unread = One(found, SyncOfflineCheck.NotChecked, "source filter is not checked offline");
        Assert.Equal(("info", (int?)6), (unread.Severity, unread.Line));
        Assert.Contains(why, unread.Message);
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("filter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DATEADD(day, -7, GETDATE()) < [created] AND status IN (N'a', N'b')", null)]
    [InlineData("CAST(region_code AS varchar(10)) = '3' AND created > CURRENT_TIMESTAMP", null)]
    [InlineData("o.status = 'open' AND o.closed_on IS NULL", "closed_on")]
    [InlineData("[closed on] IS NULL OR region_code = 3", "closed on")]
    [InlineData("UPPER(stauts) = 'OPEN'", null)]   // a word inside a call may be an argument the function names, so it is not reported
    public void A_filter_reports_only_the_names_that_are_columns_and_are_missing(string filter, string? missing)
    {
        var found = CheckOf(Draft(Mapping.Replace("status = 'open' AND regoin_code > 3", filter.Replace("'", "''")).Replace("filter: ", "filter: '").Replace("\ntargets:", "'\ntargets:")));
        var said = found.Where(d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("filter", StringComparison.Ordinal)).ToList();
        if (missing is null) Assert.Empty(said);
        else Assert.Contains($"'{missing}'", Assert.Single(said).Message);
    }

    [Fact]
    public void A_driver_whose_type_map_is_syncs_own_is_said_to_be_unchecked_and_its_types_are_not_reported()
    {
        var found = CheckOf(Draft(sourceEngine: "acme-odbc"));
        var skipped = One(found, SyncOfflineCheck.NotChecked, "type map is Sync's own");
        Assert.Contains("'acme-odbc'", skipped.Message);
        Assert.DoesNotContain(found, d => d.Code is SyncOfflineCheck.UnknownType or SyncOfflineCheck.Types);
        Assert.Contains(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("'shipped_on'", StringComparison.Ordinal));   // the names are still checked
    }

    [Fact]
    public void A_connection_with_no_entry_in_the_cache_is_said_once_to_be_unchecked()
    {
        var found = CheckOf(Draft(cache: Cache[..Cache.IndexOf("  dw:", StringComparison.Ordinal)]));
        var skipped = One(found, SyncOfflineCheck.NotChecked, "'dw' has no entry");
        Assert.Equal(MappingFile, skipped.File);
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.Types || d.Message.Contains("on the target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_drafts_check_carries_the_offline_findings_and_stays_ok()
    {
        var program = FakeSync.Create();
        if (program is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: program);
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = "orders-set", ["kind"] = "sync", ["title"] = "Orders set" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders-set/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var url = $"/api/v1/projects/orders-set/workspaces/{id}";

        var before = (await server.AsAda(HttpMethod.Get, url + "/check")).Body!;
        Assert.True((bool)before["ok"]!, before.ToJsonString());
        Assert.Contains(before["diagnostics"]!.AsArray(), d => (string)d!["code"]! == SyncOfflineCheck.NoCache);

        // the template's mapping reads source-db's dbo.Orders (line 8 of its file); the cache knows the connection but not the table
        var cache = "schema: catalog-cache/2\ngenerated_at: 2026-11-18T10:00:00Z\nconnections:\n  source-db:\n    engine: sqlserver\n    read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:00Z}\n    databases:\n      - {name: App, schemas: [dbo]}\n    tables:\n      - database: App\n        schema: dbo\n        name: Customers\n        kind: table\n        columns:\n          - {name: id, type: int, nullable: false, key: true}\n";
        var write = await server.AsAda(HttpMethod.Put, url + "/files/catalog-cache.yaml", new JsonObject { ["content"] = cache }.ToJsonString());
        Assert.True(write.Response.IsSuccessStatusCode);
        var after = (await server.AsAda(HttpMethod.Get, url + "/check")).Body!;
        Assert.True((bool)after["ok"]!, after.ToJsonString());
        var table = after["diagnostics"]!.AsArray().Single(d => (string)d!["code"]! == SyncOfflineCheck.Table)!;
        Assert.Equal(("warning", MappingFile, 8), ((string)table["severity"]!, (string)table["file"]!, (int)table["line"]!));
        Assert.Contains("App.dbo.Orders", (string)table["message"]!);
    }
}
