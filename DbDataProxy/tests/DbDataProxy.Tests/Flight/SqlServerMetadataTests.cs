using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// SQL Server metadata through the shared SqlServerCatalogReader (sys views) must give the answers the INFORMATION_SCHEMA queries it replaced gave: the same
/// tables and types, schemas, primary keys and declared columns. Runs against the test SQL Server (skips when it is unreachable); the objects live in tempdb.
/// </summary>
public sealed class SqlServerMetadataTests : IAsyncLifetime
{
    private readonly string _prefix = "pm_" + Guid.NewGuid().ToString("N")[..8];
    private WebApplication? _app;
    private string _address = "";
    private string _dir = "";

    private string Connection => SqlServerEnv.SqlClient() + ";Initial Catalog=tempdb";

    public async ValueTask InitializeAsync()
    {
        await SqlServerEnv.EnsureReachableAsync();
        await using (var c = new SqlConnection(Connection))
        {
            await c.OpenAsync();
            string sql = $"""
                CREATE SCHEMA {_prefix}_s;
                """;
            // The schema must be created in its own batch.
            await using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
            foreach (var statement in new[]
            {
                $"CREATE TABLE dbo.{_prefix}_cust(id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL, balance DECIMAL(12,2) NULL, created DATETIME2(3) NULL, note VARCHAR(max) NULL)",
                $"CREATE TABLE {_prefix}_s.{_prefix}_ord(order_id BIGINT NOT NULL, line INT NOT NULL, qty INT NOT NULL, price DECIMAL(10,4) NULL, CONSTRAINT pk_{_prefix} PRIMARY KEY (order_id, line))",
                $"CREATE TABLE dbo.{_prefix}_nokey(a INT NULL, b FLOAT NULL)",
                $"CREATE VIEW dbo.{_prefix}_v AS SELECT id, name FROM dbo.{_prefix}_cust",
            })
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-sqlmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:DataSources:sql:Provider"] = "sqlserver",
            ["Proxy:DataSources:sql:ConnectionString"] = Connection,
            ["Proxy:DataSources:sql:Classifier"] = "off",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "sql",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            await using var c = new SqlConnection(Connection);
            await c.OpenAsync();
            foreach (var statement in new[]
            {
                $"DROP VIEW IF EXISTS dbo.{_prefix}_v", $"DROP TABLE IF EXISTS dbo.{_prefix}_cust", $"DROP TABLE IF EXISTS {_prefix}_s.{_prefix}_ord",
                $"DROP TABLE IF EXISTS dbo.{_prefix}_nokey", $"DROP SCHEMA IF EXISTS {_prefix}_s",
            })
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (SqlException) { /* cleanup is best effort in tempdb */ }
    }

    private async Task<List<string?[]>> InformationSchemaAsync(string sql)
    {
        await using var c = new SqlConnection(Connection);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await r.ReadAsync())
            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        return rows;
    }

    private static async Task<List<string?[]>> RowsAsync(QueryResultStream stream)
    {
        var rows = new List<string?[]>();
        await foreach (var batch in stream)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowText.Get(batch.Column(c), r)).ToArray());
        return rows;
    }

    private async Task<ProxyFlightClient> LoginAsync()
    {
        var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        return client;
    }

    [Fact]
    public async Task Tables_and_types_match_information_schema()
    {
        using var client = await LoginAsync();
        var options = new QueryOptions { DataSource = "sql" };
        var actual = (await RowsAsync(await client.GetTablesAsync(tablePattern: _prefix + "\\_%", options: options)))
            .Select(r => string.Join('|', r)).Order(StringComparer.Ordinal).ToList();

        var expected = (await InformationSchemaAsync(
                $"SELECT table_catalog, table_schema, table_name, table_type FROM information_schema.tables WHERE table_name LIKE '{_prefix}[_]%'"))
            .Select(r => string.Join('|', r[0], r[1], r[2], r[3] == "BASE TABLE" ? "TABLE" : r[3])).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(4, expected.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Schemas_match_information_schema_including_empty_ones()
    {
        using var client = await LoginAsync();
        var actual = (await RowsAsync(await client.GetSchemasAsync(options: new QueryOptions { DataSource = "sql" }))).Select(r => string.Join('|', r)).Order(StringComparer.Ordinal).ToList();
        var expected = (await InformationSchemaAsync("SELECT catalog_name, schema_name FROM information_schema.schemata")).Select(r => string.Join('|', r)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
        Assert.Contains($"tempdb|{_prefix}_s", actual);
    }

    [Fact]
    public async Task Primary_keys_match_information_schema_in_column_order()
    {
        using var client = await LoginAsync();
        var options = new QueryOptions { DataSource = "sql" };

        var composite = await RowsAsync(await client.GetPrimaryKeysAsync($"{_prefix}_ord", options: options));
        Assert.Equal(["order_id", "line"], composite.Select(r => r[3]));
        Assert.Equal(["1", "2"], composite.Select(r => r[5]));
        Assert.All(composite, r => Assert.Equal($"pk_{_prefix}", r[4]));
        Assert.All(composite, r => Assert.Equal($"{_prefix}_s", r[1]));

        var single = await RowsAsync(await client.GetPrimaryKeysAsync($"{_prefix}_cust", schema: "dbo", options: options));
        Assert.Equal(["id"], single.Select(r => r[3]));
        Assert.Empty(await RowsAsync(await client.GetPrimaryKeysAsync($"{_prefix}_nokey", options: options)));
        Assert.Empty(await RowsAsync(await client.GetPrimaryKeysAsync($"{_prefix}_v", options: options)));
    }

    [Fact]
    public async Task Table_schemas_carry_declared_nullability_and_decimal_precision()
    {
        using var client = await LoginAsync();
        Schema schema = null!;
        await foreach (var batch in await client.GetTablesAsync(tablePattern: $"{_prefix}\\_cust", includeSchema: true, options: new QueryOptions { DataSource = "sql" }))
            if (batch.Length > 0) schema = SchemaExtensions.DeserializeSchema(((BinaryArray)batch.Column("table_schema")).GetBytes(0).ToArray());

        Assert.False(schema.GetFieldByName("id").IsNullable);
        Assert.False(schema.GetFieldByName("name").IsNullable);
        Assert.True(schema.GetFieldByName("balance").IsNullable);
        var balance = Assert.IsType<Decimal128Type>(schema.GetFieldByName("balance").DataType);
        Assert.Equal((12, 2), (balance.Precision, balance.Scale));
    }

    [Fact]
    public async Task Server_info_names_the_product_and_keeps_the_static_dialect_facts()
    {
        using var client = await LoginAsync();
        var info = await SqlInfoReader.ReadAsync(await client.GetSqlInfoAsync(new QueryOptions { DataSource = "sql" }));
        Assert.Equal("Microsoft SQL Server", info[0]);
        Assert.False(string.IsNullOrEmpty((string)info[1]));
        Assert.Equal("\"", info[504]);
        Assert.Equal(1L, info[503]);
        Assert.Equal(1L, info[507]);
    }
}
