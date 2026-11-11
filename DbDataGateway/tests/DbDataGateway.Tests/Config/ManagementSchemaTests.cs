using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// <c>POST /api/v1/data-sources/{name}/schema</c> (plan 33 SC20j, D20): the tables, columns and keys of a data source, read with the Gateway's own login, for a Workspace's catalog cache. No row, no login and no address is in the answer; an
/// administrator reads any data source, a publishing client its own and a hand-made one only with <c>adopt</c> naming it (recorded as an adoption), never another client's.
/// </summary>
public sealed partial class ManagementTests
{
    private const string RowValue = "needle-row-value-7f3a";

    /// <summary>A DuckDB file with one table that has a key, a NOT NULL column and one row, and the data source file that reads it.</summary>
    private string OrdersData(string name)
    {
        var file = Path.Combine(dataDirectory, name + ".duckdb");
        using (var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE IF NOT EXISTS orders (id INTEGER PRIMARY KEY, customer VARCHAR NOT NULL, note VARCHAR, placed TIMESTAMP); INSERT INTO orders VALUES (1, '{RowValue}', NULL, TIMESTAMP '2026-01-01 10:00:00')";
            command.ExecuteNonQuery();
        }
        return Conn.Yaml(name, "duckdb", "Data Source=" + file);
    }

    [Fact]
    public async Task An_administrator_reads_the_shape_of_a_data_source_and_the_answer_holds_no_row_and_no_address()
    {
        await StartAsync();
        var admin = await PairAsync();
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/shop", ServerRole.Admin, Put(OrdersData("shop"), null));

        var response = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/shop/schema", ServerRole.Admin, "{}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var answer = JsonNode.Parse(text)!;
        Assert.Equal("duckdb", (string)answer["engine"]!);
        var orders = answer["tables"]!.AsArray().Single(t => (string)t!["name"]! == "orders")!;
        Assert.Equal("table", (string)orders["kind"]!);
        var columns = orders["columns"]!.AsArray().ToDictionary(c => (string)c!["name"]!, c => c!);
        Assert.Equal(["id", "customer", "note", "placed"], columns.Keys);
        Assert.True((bool)columns["id"]["key"]!);
        Assert.False((bool)columns["customer"]["key"]!);
        Assert.False((bool)columns["customer"]["nullable"]!);
        Assert.True((bool)columns["note"]["nullable"]!);
        Assert.All(columns.Values, c => Assert.False(string.IsNullOrWhiteSpace((string)c["type"]!)));
        // the shape only
        Assert.DoesNotContain(RowValue, text);
        Assert.DoesNotContain(dataDirectory, text);
        Assert.DoesNotContain("Data Source", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source schema" && (string)a["target"]! == "shop" && (string)a["outcome"]! == "read");
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/nothing/schema", ServerRole.Admin, "{}")).StatusCode);
    }

    [Fact]
    public async Task A_publishing_client_reads_the_shape_of_its_own_data_source_and_of_a_hand_made_one_only_with_adopt_which_is_recorded_and_never_of_another_clients()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(OrdersData("by-hand"), null));
        await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/own", ServerRole.Publisher, Put(OrdersData("own"), null));
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/theirs", ServerRole.Publisher, Put(OrdersData("theirs"), null));

        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, first, HttpMethod.Post, "/api/v1/data-sources/own/schema", ServerRole.Publisher, "{}")).StatusCode);

        var asked = await CallAsAsync(build1, first, HttpMethod.Post, "/api/v1/data-sources/by-hand/schema", ServerRole.Publisher, "{}");
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var hint = await BodyAsync(asked);
        Assert.Equal(("by-hand", "hand"), ((string)hint["adopt"]!["data_source"]!, (string)hint["adopt"]!["made_by"]!));
        Assert.Empty((await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Admin)))["adoptions"]!.AsArray());

        var read = await CallAsAsync(build1, first, HttpMethod.Post, "/api/v1/data-sources/by-hand/schema", ServerRole.Publisher, "{\"adopt\":\"by-hand\"}");
        Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        var record = Assert.Single((await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Admin)))["adoptions"]!.AsArray())!;
        Assert.Equal(("build-1", "schema-read"), ((string)record["client"]!, (string)record["kind"]!));
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).Contains("to read its catalog", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Post, "/api/v1/data-sources/theirs/schema", ServerRole.Publisher, "{\"adopt\":\"theirs\"}")).StatusCode);
    }
}
