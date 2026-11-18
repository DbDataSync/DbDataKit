using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.Catalog;

namespace DbDataWorkspace.Tests;

/// <summary>The catalog cache, format <c>catalog-cache/2</c>: the strict reader over the fixtures the TypeScript twin also reads, the writer read back by it, and the draft's check reporting a damaged cache.</summary>
[Collection(ServerCollection.Name)]
public sealed class CatalogCacheTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-cache");

    private static Dictionary<string, string[]> Expected() =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(Fixtures, "expected.json")))!;

    [Fact]
    public void Every_fixture_gives_exactly_the_findings_the_file_lists()
    {
        var expected = Expected();
        Assert.True(expected.Count >= 9);
        foreach (var (file, findings) in expected)
        {
            var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, file)), file);
            var got = loaded.Diagnostics.Select(d => $"{d.Code} {d.KeyPath}").Order(StringComparer.Ordinal).ToArray();
            Assert.True(findings.Order(StringComparer.Ordinal).SequenceEqual(got), $"{file}: expected [{string.Join("; ", findings)}] but got [{string.Join("; ", got)}]");
            Assert.Equal(findings.Length == 0, loaded.Ok);
        }
    }

    [Fact]
    public void A_sound_cache_is_read_whole_and_a_column_called_password_is_only_a_name()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "sound.yaml")), "sound.yaml");
        var cache = loaded.Cache!;
        Assert.Equal(["source-db", "target-db"], cache.Connections.Keys.Order(StringComparer.Ordinal));
        var source = cache.Connections["source-db"];
        Assert.Equal(new CacheRead("runners-eu", "runner", "dev", "2026-11-18T10:00:00Z"), source.Read);
        Assert.Equal(new CacheRead("prod-gateway", "gateway", null, "2026-11-11T10:00:00Z"), cache.Connections["target-db"].Read);
        var shop = Assert.Single(source.Databases);
        Assert.Equal("shop", shop.Name);
        Assert.Equal(["public", "archive"], shop.Schemas);
        var orders = source.Tables[0];
        Assert.Equal(("shop", "public", "orders", "table"), (orders.Database, orders.Schema, orders.Name, orders.Kind));
        Assert.True(orders.Columns[0].Key);
        Assert.False(orders.Columns[0].Nullable);
        Assert.True(orders.Columns[2].Nullable);
        Assert.Contains(orders.Columns, c => c.Name == "password");
        Assert.Equal(["customer_id", "placed_at"], Assert.Single(orders.UniqueKeys).Columns);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal(("fk_orders_customers", "public", "customers"), (fk.Name, fk.Schema, fk.Table));
        Assert.Equal(["id"], fk.References);
        Assert.Equal("view", source.Tables[1].Kind);
    }

    [Fact]
    public void What_the_writer_writes_the_reader_reads_back_the_same()
    {
        var cache = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "sound.yaml")), "sound.yaml").Cache!;
        var text = CatalogCache.Write(cache);
        Assert.StartsWith("schema: catalog-cache/2\n", text, StringComparison.Ordinal);
        // the TypeScript writer is held to the same text (catalogCache.test.ts)
        Assert.Equal(File.ReadAllText(Path.Combine(Fixtures, "sound.written.txt")).ReplaceLineEndings("\n"), text);
        var again = CatalogCache.Load(text, "catalog-cache.yaml");
        Assert.True(again.Ok, string.Join("; ", again.Diagnostics));
        Assert.Equal(text, CatalogCache.Write(again.Cache!));
        var orders = again.Cache!.Connections["source-db"].Tables.Single(t => t.Name == "orders");
        Assert.Equal(("shop", 1, 1), (orders.Database, orders.UniqueKeys.Count, orders.ForeignKeys.Count));
        Assert.Equal(["archive", "public"], again.Cache.Connections["source-db"].Databases.Single().Schemas);
        Assert.Null(again.Cache.Connections["target-db"].Read!.Channel);
    }

    [Fact]
    public void An_answer_becomes_a_connection_with_where_it_was_read_and_its_empty_schemas()
    {
        var answer = (JsonObject)JsonNode.Parse("""
            {"engine": "postgres", "databases": [{"name": "shop", "schemas": ["public", "empty"]}],
             "tables": [{"database": "shop", "schema": "public", "name": "orders", "kind": "table", "rows": [[1]],
                         "columns": [{"name": "id", "type": "integer", "nullable": false, "key": true}, {"name": "customer_id", "type": "integer", "nullable": false}],
                         "foreign_keys": [{"name": "fk", "columns": ["customer_id"], "references": {"schema": "public", "table": "customers", "columns": ["id"]}}]},
                        {"database": "shop", "schema": "public", "name": "nothing", "kind": "table", "columns": []}]}
            """)!;
        var connection = CatalogCache.FromAnswer(answer, new CacheRead("runners-eu", "runner", "dev", "2026-11-18T10:00:00Z"));
        var file = CatalogCache.WithConnection(null, "shop-db", connection, DateTimeOffset.Parse("2026-11-18T10:00:05Z", System.Globalization.CultureInfo.InvariantCulture));
        var text = CatalogCache.Write(file);
        Assert.DoesNotContain("rows", text, StringComparison.Ordinal);
        var read = CatalogCache.Load(text);
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics));
        var c = read.Cache!.Connections["shop-db"];
        Assert.Equal("dev", c.Read!.Channel);
        Assert.Contains("empty", c.Databases.Single().Schemas);
        Assert.Equal("orders", Assert.Single(c.Tables).Name);
        Assert.Equal("customers", Assert.Single(c.Tables[0].ForeignKeys).Table);
    }

    [Fact]
    public void A_key_that_would_carry_a_row_or_a_login_is_refused_with_the_reason_and_the_line()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "row.yaml")), "catalog-cache.yaml");
        var d = Assert.Single(loaded.Diagnostics);
        Assert.Equal("catalog-cache.yaml", d.Path);
        Assert.Equal(14, d.Line);
        Assert.Contains("never a row, a value or a statistic of values, and never a secret", d.Message);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_file()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "newer.yaml")), "catalog-cache.yaml");
        Assert.Contains("catalog-cache.yaml", Assert.Single(loaded.Diagnostics).Message);
    }

    [Fact]
    public void A_file_of_format_1_is_refused_naming_the_file_and_saying_how_to_make_it_again()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "format1.yaml")), "catalog-cache.yaml");
        Assert.False(loaded.Ok);
        var d = Assert.Single(loaded.Diagnostics);
        Assert.Equal(CatalogCache.BadSchema, d.Code);
        Assert.Contains("catalog-cache.yaml is in format catalog-cache/1", d.Message);
        Assert.Contains("refresh the catalog", d.Message);
    }

    [Fact]
    public void Text_that_is_not_yaml_is_a_finding_and_not_an_exception()
    {
        Assert.False(CatalogCache.Load("a: [1", "catalog-cache.yaml").Ok);
        Assert.False(CatalogCache.Load("- just\n- a list\n", "catalog-cache.yaml").Ok);
    }

    [Fact]
    public void A_draft_with_no_cache_has_nothing_to_report_and_one_with_a_damaged_cache_reports_it_with_its_file()
    {
        var folder = Directory.CreateTempSubdirectory("cc-").FullName;
        try
        {
            Assert.Empty(CatalogCacheCheck.Check(folder));
            File.Copy(Path.Combine(Fixtures, "row.yaml"), Path.Combine(folder, CatalogCache.FileName));
            var found = Assert.Single(CatalogCacheCheck.Check(folder));
            Assert.Equal(("error", CatalogCache.FileName, CatalogCache.UnknownKey), (found.Severity, found.File, found.Code));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task The_check_of_any_kind_reports_a_damaged_cache_in_the_draft()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var url = $"/api/v1/projects/orders/workspaces/{id}";
        Assert.True((bool)(await server.AsAda(HttpMethod.Get, url + "/check")).Body!["ok"]!);

        var write = await server.AsAda(HttpMethod.Put, url + "/files/catalog-cache.yaml", new JsonObject { ["content"] = File.ReadAllText(Path.Combine(Fixtures, "secrets.yaml")) }.ToJsonString());
        Assert.True(write.Response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
        var checkedDraft = await server.AsAda(HttpMethod.Get, url + "/check");
        Assert.False((bool)checkedDraft.Body!["ok"]!);
        var findings = checkedDraft.Body!["diagnostics"]!.AsArray().Where(d => (string?)d!["file"] == "catalog-cache.yaml").ToList();
        Assert.Equal(4, findings.Count);
        Assert.All(findings, f => Assert.Equal("DBD-CAT-004", (string)f!["code"]!));

        write = await server.AsAda(HttpMethod.Put, url + "/files/catalog-cache.yaml", new JsonObject { ["content"] = File.ReadAllText(Path.Combine(Fixtures, "sound.yaml")) }.ToJsonString());
        Assert.True(write.Response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
        Assert.True((bool)(await server.AsAda(HttpMethod.Get, url + "/check")).Body!["ok"]!);
    }
}
