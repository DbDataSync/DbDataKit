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

    private static string Draft(string? mapping = null, string? cache = "", string sourceEngine = "sqlserver", string? task = null, (string Path, string Text)[]? files = null)
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
        Write("config/replications/orders/task.yaml", task ?? Task);
        Write(MappingFile, mapping ?? Mapping);
        foreach (var (path, text) in files ?? []) Write(path, text);
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
        Assert.Equal(("error", MappingFile, (int?)13), (missing.Severity, missing.File, missing.Line));
        Assert.Contains("crm.dbo.orders", missing.Message);
        Assert.Contains("channel dev", missing.Message);

        var filter = One(found, SyncOfflineCheck.Column, "'regoin_code'");
        Assert.Equal(("error", (int?)6), (filter.Severity, filter.Line));
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

        // the rest agrees: id, status and the filter's status are checked and say nothing; a missing column is an error, a type or a key a warning
        Assert.Equal(7, found.Count);
        Assert.All(found.Where(d => d.Code == SyncOfflineCheck.Column), d => Assert.Equal("error", d.Severity));
        Assert.All(found.Where(d => d.Code != SyncOfflineCheck.Column), d => Assert.Equal("warning", d.Severity));
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
        Assert.Equal(("error", (int?)5), (table.Severity, table.Line));
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
    public void A_driver_with_no_driver_yaml_in_the_draft_is_said_to_be_unchecked_and_its_types_are_not_reported()
    {
        var found = CheckOf(Draft(sourceEngine: "acme-odbc"));
        var skipped = One(found, SyncOfflineCheck.NotChecked, "has no driver.yaml under drivers/");
        Assert.Contains("'acme-odbc'", skipped.Message);
        Assert.DoesNotContain(found, d => d.Code is SyncOfflineCheck.UnknownType or SyncOfflineCheck.Types);
        Assert.Contains(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("'shipped_on'", StringComparison.Ordinal));   // the names are still checked
    }

    // a driver.yaml as `dbdatasync driver install` writes one, its type map without the spatial types
    private const string AcmeDriver = """
        id: acme-odbc
        displayName: Acme over ODBC
        library: system-data-odbc
        dialect:
          quoteIdentifier: bracket
          parameterPrefix: "@"
          rowLimit: topN
        typeMap:
          int: Int32
          "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
          "nvarchar(n)": { kind: String, length: n, unicode: true }
          datetime2: Timestamp
        capabilities:
          readers: [Watermark, BatchReload]
          writers: [DeleteInsert]
        """;

    [Fact]
    public void A_yaml_drivers_types_and_filters_are_read_through_its_driver_yaml()
    {
        var found = CheckOf(Draft(Mapping.Replace("status = 'open' AND regoin_code > 3", "'[region code] > 3 AND regoin_code > 3'"),
            sourceEngine: "acme-odbc", files: [("drivers/acme/driver.yaml", AcmeDriver)]));

        var unknown = One(found, SyncOfflineCheck.UnknownType, "'location'");
        Assert.Equal(("warning", (int?)15), (unknown.Severity, unknown.Line));
        Assert.Contains("the acme-odbc driver's type map (drivers/acme/driver.yaml) does not know", unknown.Message);
        One(found, SyncOfflineCheck.Types, "'amount'");   // its decimal(18,2) read by the type map, into the target's integer
        Assert.Contains(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("filter names 'regoin_code'", StringComparison.Ordinal));
        Assert.Contains(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("filter names 'region code'", StringComparison.Ordinal));   // a bracketed name, unwrapped
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.NotChecked);
    }

    [Fact]
    public void A_driver_yaml_that_does_not_read_is_named_and_its_types_are_not_checked()
    {
        var found = CheckOf(Draft(sourceEngine: "acme-odbc", files: [("drivers/acme-odbc/driver.yaml", AcmeDriver.Replace("quoteIdentifier: bracket", "quoteIdentifier: chevron"))]));
        var skipped = One(found, SyncOfflineCheck.NotChecked, "drivers/acme-odbc/driver.yaml does not read");
        Assert.Contains("chevron", skipped.Message);
        Assert.DoesNotContain(found, d => d.Code is SyncOfflineCheck.UnknownType or SyncOfflineCheck.Types);
    }

    [Fact]
    public void A_flight_sql_connection_with_no_dialect_reads_its_types_as_ansi_names()
    {
        var found = CheckOf(Draft(sourceEngine: "flightsql"));
        var unknown = One(found, SyncOfflineCheck.UnknownType, "'location'");
        Assert.Contains("the flightsql driver's type map", unknown.Message);
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.NotChecked);
        Assert.Contains(found, d => d.Code == SyncOfflineCheck.Column && d.Message.Contains("filter names 'regoin_code'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_flight_sql_connection_with_a_dialect_reads_a_name_ansi_does_not_have_as_that_engines()
    {
        var crm = "schema: connection/1\nname: crm\nengine: flightsql\ndialect: sqlserver\naddress:\n  host: crm.example.com\n  database: crm";
        var found = CheckOf(Draft(files: [("config/connections/crm.yaml", crm)]));
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.UnknownType && d.Message.Contains("'location'", StringComparison.Ordinal));
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.NotChecked);
    }

    // the replication reads by watermark, so its writer is the default behind an incremental reader: ChangeApply, which matches rows on the target's key
    private const string WatermarkTaskText = """
        schemaVersion: 2
        name: orders
        scheduling:
          mode: Continuous
          frequencySeconds: 30
        changeProcessing:
          reader:
            kind: Watermark
            options:
              column: created
          cache:
            kind: StagingTable
        endpoints:
          source:
            connectionName: crm
            database: crm
          target:
            connectionName: dw
            database: dw
        """;

    // the source file may be checked out with CRLF, and the tests below edit this text by lines
    private static readonly string WatermarkTask = WatermarkTaskText.ReplaceLineEndings("\n");

    /// <summary><paramref name="text"/> with <paramref name="old"/> replaced, failing when <paramref name="old"/> is not in it, so an edit that misses
    /// (a line ending, a changed fixture) fails here rather than leaving the fixture unchanged.</summary>
    private static string Edited(string text, string old, string replacement)
    {
        Assert.Contains(old, text, StringComparison.Ordinal);
        return text.Replace(old, replacement, StringComparison.Ordinal);
    }

    private static readonly string NoSourceKey = Cache.Replace("{name: id, type: int, nullable: false, key: true}", "{name: id, type: int, nullable: false}");

    private static readonly string NoStatedKey = Mapping[..Mapping.IndexOf("primaryKey:", StringComparison.Ordinal)];

    // the mapping with no stated key, into a target the cache does not hold yet (provisioning creates it)
    private static readonly string Unkeyed = NoStatedKey.Replace("  table: orders\ncolumnMappings", "  table: orders_copy\ncolumnMappings");

    [Fact]
    public void A_change_apply_writer_on_a_source_table_with_no_key_in_the_cache_is_a_finding_on_the_mapping()
    {
        var found = CheckOf(Draft(Unkeyed, cache: NoSourceKey, task: WatermarkTask));

        var key = One(found, SyncOfflineCheck.Key, "'ChangeApply' would refuse");
        Assert.Equal(("warning", MappingFile, (int?)8), (key.Severity, key.File, key.Line));   // the targets
        Assert.Contains("dw.public.orders_copy is not in catalog-cache.yaml, so it is checked as provisioning would create it", key.Message);
        Assert.Contains("has no primary key", key.Message);   // Sync's own words
    }

    [Fact]
    public void A_change_apply_writer_is_satisfied_by_the_sources_key_and_by_a_stated_one()
    {
        var keyed = CheckOf(Draft(Unkeyed, task: WatermarkTask));
        Assert.DoesNotContain(keyed, d => d.Code == SyncOfflineCheck.Key);

        var stated = CheckOf(Draft(Unkeyed + "primaryKey:\n- status\n", cache: NoSourceKey, task: WatermarkTask));
        Assert.DoesNotContain(stated, d => d.Code == SyncOfflineCheck.Key && d.Message.Contains("would refuse", StringComparison.Ordinal));
    }

    [Fact]
    public void A_change_apply_writer_on_a_cached_target_with_no_primary_key_is_a_finding()
    {
        var found = CheckOf(Draft(NoStatedKey, cache: Cache.Replace("{name: order_no, type: bigint, nullable: false, key: true}", "{name: order_no, type: bigint, nullable: false}"), task: WatermarkTask));
        var key = One(found, SyncOfflineCheck.Key, "'ChangeApply' would refuse");
        Assert.Contains("target table 'public.orders' has no primary key", key.Message);
        Assert.DoesNotContain("provisioning would create it", key.Message);
    }

    [Fact]
    public void An_scd2_writer_needs_a_natural_key_stated_or_derived_from_the_source()
    {
        var scd2 = Edited(WatermarkTask, "  cache:\n    kind: StagingTable", "  cache:\n    kind: StagingTable\n  writer:\n    kind: Scd2");
        var found = CheckOf(Draft(Unkeyed, cache: NoSourceKey, task: scd2));
        var key = One(found, SyncOfflineCheck.Key, "'Scd2' would refuse");
        Assert.Equal((int?)5, key.Line);
        Assert.Contains("naturalKey", key.Message);

        var stated = CheckOf(Draft(Unkeyed, cache: NoSourceKey, task: Edited(scd2, "    kind: Scd2", "    kind: Scd2\n    options:\n      naturalKey: id")));
        Assert.DoesNotContain(stated, d => d.Code == SyncOfflineCheck.Key && d.Message.Contains("would refuse", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delete_sweep_on_a_cached_target_with_no_primary_key_is_a_finding_in_the_writers_words()
    {
        var sweeping = WatermarkTask + "reconcile:\n  enabled: true\n";
        var unkeyedTarget = Cache.Replace("{name: order_no, type: bigint, nullable: false, key: true}", "{name: order_no, type: bigint, nullable: false}");
        var key = One(CheckOf(Draft(NoStatedKey, cache: unkeyedTarget, task: sweeping)), SyncOfflineCheck.Key, "'KeyReconcileDelete' would refuse");
        Assert.Contains("Table mapping 'orders' target has no cached primary key", key.Message);

        Assert.DoesNotContain(CheckOf(Draft(NoStatedKey, task: sweeping)), d => d.Message.Contains("'KeyReconcileDelete' would refuse", StringComparison.Ordinal));
    }

    [Fact]
    public void An_engines_own_reader_is_checked_for_the_key_the_built_in_drivers_table_says_it_needs()
    {
        var tracking = Edited(WatermarkTask, "kind: Watermark\n    options:\n      column: created", "kind: MsSqlChangeTracking");
        var found = CheckOf(Draft(Unkeyed, cache: NoSourceKey, task: tracking));
        var key = One(found, SyncOfflineCheck.Key, "'MsSqlChangeTracking' would refuse");
        Assert.Equal(("warning", MappingFile, (int?)5), (key.Severity, key.File, key.Line));   // the source's table
        Assert.Contains("crm.dbo.orders has no primary key", key.Message);
        Assert.DoesNotContain(found, d => d.Code == SyncOfflineCheck.NotChecked && d.Message.Contains("MsSqlChangeTracking", StringComparison.Ordinal));

        // the source's key in the cache satisfies it
        Assert.DoesNotContain(CheckOf(Draft(Unkeyed, task: tracking)), d => d.Message.Contains("'MsSqlChangeTracking' would refuse", StringComparison.Ordinal));
    }

    [Fact]
    public void A_logical_slot_reader_on_a_keyed_table_is_no_finding()
    {
        var slot = Edited(WatermarkTask, "kind: Watermark\n    options:\n      column: created", "kind: PgLogicalSlot");
        var found = CheckOf(Draft(sourceEngine: "postgres", task: slot));
        Assert.DoesNotContain(found, d => d.Message.Contains("'PgLogicalSlot'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_kind_the_connections_driver_does_not_have_is_said_once_to_be_unchecked_for_its_key()
    {
        var merge = Edited(WatermarkTask, "  cache:\n    kind: StagingTable", "  cache:\n    kind: StagingTable\n  writer:\n    kind: MsSqlMerge");
        var found = CheckOf(Draft(Unkeyed, task: merge));   // the target dw is PostgreSQL, whose driver has no MsSqlMerge
        var skipped = One(found, SyncOfflineCheck.NotChecked, "'MsSqlMerge' is not a Kind of the built-in driver");
        Assert.Equal("info", skipped.Severity);
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
    public async Task The_drafts_check_carries_the_offline_findings_and_a_missing_table_stops_it()
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
        Assert.False((bool)after["ok"]!, after.ToJsonString());
        var table = after["diagnostics"]!.AsArray().Single(d => (string)d!["code"]! == SyncOfflineCheck.Table)!;
        Assert.Equal(("error", MappingFile, 8), ((string)table["severity"]!, (string)table["file"]!, (int)table["line"]!));
        Assert.Contains("App.dbo.Orders", (string)table["message"]!);
    }
}
