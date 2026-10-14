using Apache.Arrow;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// The whole proxy in front of a real SQL Server: SqlClient and ODBC sources, INFORMATION_SCHEMA and ODBC-catalog
/// metadata, parameters, updates and database-credential pass-through. Skipped without a reachable server (see
/// <see cref="SqlServerEnv"/>); CI runs it against a SQL Server container and requires it.
/// </summary>
public sealed class SqlServerFlightTests : IAsyncLifetime
{
    private const string Table = "dbproxy_items";
    private const string OdbcDriver = "ODBC Driver 18 for SQL Server";

    private WebApplication? _app;
    private string _address = "";
    private string _dir = "";
    private bool _odbcAvailable;

    public async ValueTask InitializeAsync()
    {
        await SqlServerEnv.EnsureReachableAsync();
        await SeedAsync();
        _odbcAvailable = ProbeOdbc();

        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        string database = ";Initial Catalog=tempdb";
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "Arrow",
            ["Proxy:Compression:Codec"] = "zstd",
            ["Proxy:DataSources:sql:Provider"] = "sqlserver",
            ["Proxy:DataSources:sql:ConnectionString"] = SqlServerEnv.SqlClient(30) + database,
            ["Proxy:DataSources:sql:AllowWrites"] = "true",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "sql",
            ["Proxy:Users:0:DataSources:1"] = "odbc",
            ["Proxy:Users:0:DataSources:2"] = "sql_odbc_infoschema",
        };
        if (_odbcAvailable)
        {
            settings["Proxy:DataSources:odbc:Provider"] = "odbc";   // Metadata "auto": ODBC catalog API
            settings["Proxy:DataSources:odbc:ConnectionString"] = SqlServerEnv.Odbc(OdbcDriver) + ";Database=tempdb";
            settings["Proxy:DataSources:sql_odbc_infoschema:Provider"] = "odbc";
            settings["Proxy:DataSources:sql_odbc_infoschema:Metadata"] = "informationschema";
            settings["Proxy:DataSources:sql_odbc_infoschema:ConnectionString"] = SqlServerEnv.Odbc(OdbcDriver) + ";Database=tempdb";
        }
        if (SqlServerEnv.UsesSqlAuthentication)
        {
            settings["Proxy:DataSources:sql_pass:Provider"] = "sqlserver";
            settings["Proxy:DataSources:sql_pass:PassThrough"] = "true";
            settings["Proxy:DataSources:sql_pass:ConnectionString"] = SqlServerEnv.SqlClientWithoutLogin() + database;
        }

        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { if (_dir.Length > 0) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task SeedAsync()
    {
        await using var connection = new SqlConnection(SqlServerEnv.SqlClient(30) + ";Initial Catalog=tempdb");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF OBJECT_ID('dbo.{Table}_view') IS NOT NULL DROP VIEW dbo.{Table}_view;
            IF OBJECT_ID('dbo.{Table}') IS NOT NULL DROP TABLE dbo.{Table};
            CREATE TABLE dbo.{Table}(id int NOT NULL PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(10,2) NULL, added date NULL);
            INSERT INTO dbo.{Table} VALUES (1, N'widget', 9.50, '2024-01-02'), (2, N'gadget', 19.99, '2024-02-03'), (3, N'gizmo', NULL, NULL);
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"CREATE VIEW dbo.{Table}_view AS SELECT id, name FROM dbo.{Table};";
        await command.ExecuteNonQueryAsync();
    }

    private static bool ProbeOdbc()
    {
        try
        {
            using var c = new System.Data.Odbc.OdbcConnection(SqlServerEnv.Odbc(OdbcDriver) + ";Database=tempdb;Connect Timeout=10");
            c.Open();
            return true;
        }
        catch (System.Data.Odbc.OdbcException) { return false; }
    }

    private async Task<ProxyClient> LoginAsync(string user = "alice", string password = "pw")
    {
        var client = new ProxyClient(_address);
        await client.AuthenticateAsync(user, password);
        return client;
    }

    private static async Task<List<string?[]>> RowsAsync(QueryResultStream stream)
    {
        var rows = new List<string?[]>();
        await foreach (var batch in stream)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowText.Get(batch.Column(c), r)).ToArray());
        return rows;
    }

    private static QueryOptions On(string dataSource) => new() { DataSource = dataSource };

    [Fact]
    public async Task Queries_return_typed_columns_over_arrow_compression()
    {
        using var client = await LoginAsync();
        var rows = await RowsAsync(await client.Raw.QueryAsync($"SELECT id, name, price, added FROM dbo.{Table} ORDER BY id", On("sql")));

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "widget", "9.50", "2024-01-02"], rows[0]);
        Assert.Null(rows[2][2]);
        Assert.Null(rows[2][3]);
    }

    [Fact]
    public async Task Parameters_bind_for_question_marks_and_named_markers()
    {
        using var client = await LoginAsync();

        var positional = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT name FROM dbo.{Table} WHERE id >= ? AND name <> ? ORDER BY id", [2, "gizmo"], On("sql")));
        Assert.Equal(["gadget"], positional.Select(r => r[0]));

        var named = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT name FROM dbo.{Table} WHERE price > @min", new Dictionary<string, object?> { ["min"] = 5m }, On("sql")));
        Assert.Equal(2, named.Count);

        var hostile = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT count(*) FROM dbo.{Table} WHERE name = ?", ["x'; DROP TABLE dbo." + Table + "; --"], On("sql")));
        Assert.Equal("0", hostile[0][0]);
        Assert.Equal("3", (await RowsAsync(await client.Raw.QueryAsync($"SELECT count(*) FROM dbo.{Table}", On("sql"))))[0][0]);
    }

    [Fact]
    public async Task Updates_with_parameter_rows_insert_and_report_counts()
    {
        using var client = await LoginAsync();
        long inserted = await client.Raw.ExecuteUpdateAsync($"INSERT INTO dbo.{Table}(id, name, price) VALUES (?, ?, ?)",
            [[100, "bulk-a", 1.25m], [101, "bulk-b", null]], On("sql"));
        Assert.Equal(2, inserted);

        long deleted = await client.Raw.ExecuteUpdateAsync($"DELETE FROM dbo.{Table} WHERE id >= ?", [100], On("sql"));
        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task Metadata_comes_from_information_schema()
    {
        using var client = await LoginAsync();
        var options = On("sql");

        var tables = await RowsAsync(await client.Raw.GetTablesAsync(tablePattern: "dbproxy_items%", options: options));
        Assert.Contains(tables, r => r[2] == Table && r[3] == "TABLE" && r[1] == "dbo");
        Assert.Contains(tables, r => r[2] == Table + "_view" && r[3] == "VIEW");

        Assert.Contains(await RowsAsync(await client.Raw.GetCatalogsAsync(options)), r => r[0] == "tempdb");
        Assert.Contains(await RowsAsync(await client.Raw.GetSchemasAsync(options: options)), r => r[1] == "dbo");

        var keys = await RowsAsync(await client.Raw.GetPrimaryKeysAsync(Table, options: options));
        Assert.Equal("id", Assert.Single(keys)[3]);
    }

    [Fact]
    public async Task Table_schemas_are_included_on_request()
    {
        using var client = await LoginAsync();
        var batches = new List<RecordBatch>();
        await foreach (var b in await client.Raw.GetTablesAsync(tablePattern: Table, includeSchema: true, options: On("sql"))) batches.Add(b);

        var batch = batches.First(b => b.Length > 0);
        var schema = ((BinaryArray)batch.Column("table_schema")).GetBytes(0).ToArray();
        Assert.True(schema.Length > Apache.Arrow.Flight.Sql.SchemaExtensions.SerializeSchema(new Schema([], null)).Length);
    }

    [Fact]
    public async Task Odbc_sources_list_tables_through_the_catalog_api_and_information_schema()
    {
        Assert.SkipUnless(_odbcAvailable, $"'{OdbcDriver}' is not installed.");
        using var client = await LoginAsync();

        // "auto" uses SQLTables for ODBC; "informationschema" forces SQL; both must find the same table.
        foreach (string source in new[] { "odbc", "sql_odbc_infoschema" })
        {
            var tables = await RowsAsync(await client.Raw.GetTablesAsync(tablePattern: Table, options: On(source)));
            var table = Assert.Single(tables);
            Assert.Equal("TABLE", table[3]);
            Assert.Equal("dbo", table[1]);
        }

        var rows = await RowsAsync(await client.Raw.QueryAsync($"SELECT name FROM dbo.{Table} WHERE id = ?", [2], On("odbc")));
        Assert.Equal("gadget", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Database_credentials_pass_through_to_the_server()
    {
        Assert.SkipUnless(SqlServerEnv.UsesSqlAuthentication, "Needs SQL authentication credentials (DBPROXY_TEST_SQLSERVER_USER).");

        using (var good = await LoginAsync(SqlServerEnv.User!, SqlServerEnv.Password!))
        {
            var rows = await RowsAsync(await good.Raw.QueryAsync("SELECT SUSER_SNAME()", On("sql_pass")));
            Assert.Equal(SqlServerEnv.User, rows[0][0], ignoreCase: true);
        }

        using var bad = await LoginAsync(SqlServerEnv.User!, "definitely-not-the-password");
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await bad.Raw.QueryAsync("SELECT 1", On("sql_pass"))));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Sql_errors_become_invalid_argument()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELECT * FROM dbo.no_such_table", On("sql"))));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
