using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;

namespace DbDataWorkspace.Tests;

/// <summary>The catalog cache, format <c>catalog-cache/1</c> (plan 33 D20, SC20i): the strict reader over the fixtures the TypeScript twin also reads, and the draft's check reporting a damaged cache.</summary>
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
        Assert.True(expected.Count >= 7);
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
        Assert.Equal(("prod-gateway", "gateway"), (cache.Server, cache.Product));
        Assert.Equal(["source-db", "target-db"], cache.Connections.Keys.Order(StringComparer.Ordinal));
        var orders = cache.Connections["source-db"].Tables[0];
        Assert.Equal(("public", "orders", "table"), (orders.Schema, orders.Name, orders.Kind));
        Assert.True(orders.Columns[0].Key);
        Assert.False(orders.Columns[0].Nullable);
        Assert.True(orders.Columns[2].Nullable);
        Assert.Contains(orders.Columns, c => c.Name == "password");
        Assert.Equal("view", cache.Connections["source-db"].Tables[1].Kind);
    }

    [Fact]
    public void A_key_that_would_carry_a_row_or_a_login_is_refused_with_the_reason_and_the_line()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "row.yaml")), "catalog-cache.yaml");
        var d = Assert.Single(loaded.Diagnostics);
        Assert.Equal("catalog-cache.yaml", d.Path);
        Assert.Equal(13, d.Line);
        Assert.Contains("never a row and never a secret", d.Message);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_file()
    {
        var loaded = CatalogCache.Load(File.ReadAllText(Path.Combine(Fixtures, "newer.yaml")), "catalog-cache.yaml");
        Assert.Contains("catalog-cache.yaml", Assert.Single(loaded.Diagnostics).Message);
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
            Assert.Empty(CatalogCache.Check(folder));
            File.Copy(Path.Combine(Fixtures, "row.yaml"), Path.Combine(folder, CatalogCache.FileName));
            var found = Assert.Single(CatalogCache.Check(folder));
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
