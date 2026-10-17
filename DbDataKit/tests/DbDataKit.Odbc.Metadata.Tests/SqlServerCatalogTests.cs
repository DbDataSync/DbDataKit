using System.Data.Odbc;
using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>
/// The native layer against a real driver. Variables (DBDATA_ prefix, or the DBPROXY_ prefix the proxy's tests use): TEST_SQLSERVER_SERVER
/// (default .\SQLEXPRESS with Windows authentication), TEST_SQLSERVER_USER and _PASSWORD for SQL authentication, and REQUIRE_SQLSERVER=true
/// to fail instead of skip when the server or driver is missing. TEST_SQLSERVER alone is still read as the server name, as before, unless
/// it holds a connection string: the SqlServer and Transfer suites use that same variable for a full connection string, and one value
/// cannot be both, so a string is ignored here. testenv/env/dbdatakit.map sets all of them.
/// </summary>
public sealed class SqlServerCatalogTests : IAsyncLifetime
{
    // DBDATA_* first, then the DBPROXY_* names the Proxy tests use.
    private static string? Env(string name) => Environment.GetEnvironmentVariable("DBDATA_" + name) ?? Environment.GetEnvironmentVariable("DBPROXY_" + name);

    private static readonly string Server = Env("TEST_SQLSERVER_SERVER") ?? ServerNameOrNull(Env("TEST_SQLSERVER")) ?? @".\SQLEXPRESS";

    /// <summary>A server name, or null for a connection string (which has '=' and ';' in it, and no server name does).</summary>
    internal static string? ServerNameOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains('=') || value.Contains(';') ? null : value;
    private static readonly string? User = Env("TEST_SQLSERVER_USER");
    private static readonly string? Password = Env("TEST_SQLSERVER_PASSWORD");
    private static readonly bool Required = string.Equals(Env("REQUIRE_SQLSERVER"), "true", StringComparison.OrdinalIgnoreCase);

    private const string Schema = "dbdata_meta";
    private string? connectionString;

    private static string Connection(string driver) =>
        $"Driver={{{driver}}};Server={Server};Database=tempdb;{(string.IsNullOrEmpty(User) ? "Trusted_Connection=Yes" : $"UID={User};PWD={Password}")};TrustServerCertificate=Yes;Connect Timeout=3";

    public async ValueTask InitializeAsync()
    {
        string[] drivers = ["ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server"];
        string? failure = null;
        foreach (string driver in drivers)
        {
            var connection = new OdbcConnection(Connection(driver));
            try { await connection.OpenAsync(); }
            catch (OdbcException ex)
            {
                failure = ex.Message;
                await connection.DisposeAsync();
                continue;
            }

            // Only failing to connect skips the tests; a failure creating the objects is a real failure.
            await using (connection)
            {
                connectionString = Connection(driver);
                await CreateObjectsAsync(connection);
            }
            return;
        }
        if (Required) throw new InvalidOperationException($"SQL Server '{Server}' is required but not usable through ODBC: {failure}");
        Assert.Skip($"SQL Server '{Server}' is not reachable through an ODBC driver: {failure}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task CreateObjectsAsync(OdbcConnection connection)
    {
        string[] statements =
        [
            $"IF OBJECT_ID('{Schema}.usp_add') IS NOT NULL DROP PROCEDURE {Schema}.usp_add",
            $"IF OBJECT_ID('{Schema}.child') IS NOT NULL DROP TABLE {Schema}.child",
            $"IF OBJECT_ID('{Schema}.v_parent') IS NOT NULL DROP VIEW {Schema}.v_parent",
            $"IF OBJECT_ID('{Schema}.parent') IS NOT NULL DROP TABLE {Schema}.parent",
            $"IF OBJECT_ID('{Schema}.nokey') IS NOT NULL DROP TABLE {Schema}.nokey",
            $"IF OBJECT_ID('{Schema}.my_table') IS NOT NULL DROP TABLE {Schema}.my_table",
            $"IF OBJECT_ID('{Schema}.myXtable') IS NOT NULL DROP TABLE {Schema}.myXtable",
            $"IF SCHEMA_ID('{Schema}') IS NULL EXEC('CREATE SCHEMA {Schema}')",
            $"CREATE TABLE {Schema}.parent (a int NOT NULL, b int NOT NULL, label varchar(20) NULL, amount decimal(38,0) NOT NULL, CONSTRAINT pk_parent PRIMARY KEY (b, a))",
            $"CREATE TABLE {Schema}.child (id int NOT NULL PRIMARY KEY, pa int NOT NULL, pb int NOT NULL, note nvarchar(50) NULL, CONSTRAINT fk_child_parent FOREIGN KEY (pb, pa) REFERENCES {Schema}.parent (b, a))",
            $"CREATE UNIQUE INDEX ux_parent_label ON {Schema}.parent (label DESC)",
            $"CREATE INDEX ix_child_note ON {Schema}.child (note)",
            $"CREATE TABLE {Schema}.nokey (x int NULL)",
            $"CREATE TABLE {Schema}.my_table (x int NOT NULL PRIMARY KEY)",
            $"CREATE TABLE {Schema}.myXtable (x int NOT NULL PRIMARY KEY)",
        ];
        foreach (string sql in statements)
        {
            await using var command = new OdbcCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var procedure = new OdbcCommand($"CREATE PROCEDURE {Schema}.usp_add @a int, @b int OUTPUT AS SET @b = @a + 1", connection);
        await procedure.ExecuteNonQueryAsync();
        await using var view = new OdbcCommand($"CREATE VIEW {Schema}.v_parent AS SELECT a, b FROM {Schema}.parent", connection);
        await view.ExecuteNonQueryAsync();
    }

    private OdbcCatalog Open() => OdbcCatalog.Connect(connectionString!);

    [Fact]
    public async Task Lists_tables_and_views_of_a_schema()
    {
        await using var catalog = Open();
        var tables = await catalog.TablesAsync(schema: Schema);

        Assert.Equal(["child", "myXtable", "my_table", "nokey", "parent", "v_parent"], tables.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.True(tables.Single(t => t.Name == "v_parent").IsView);
        Assert.False(tables.Single(t => t.Name == "parent").IsView);
    }

    [Fact]
    public async Task A_name_with_an_underscore_matches_only_itself()
    {
        await using var catalog = Open();
        var tables = await catalog.TablesAsync(schema: Schema, table: "my_table");
        Assert.Equal("my_table", Assert.Single(tables).Name);

        var columns = await catalog.ColumnsAsync(schema: Schema, table: "my_table");
        Assert.Equal("x", Assert.Single(columns).Name);
    }

    [Fact]
    public async Task Reports_composite_primary_keys_in_key_order()
    {
        await using var catalog = Open();
        var key = await catalog.PrimaryKeyAsync(null, Schema, "parent");

        Assert.NotNull(key);
        Assert.Equal("pk_parent", key.Name);
        Assert.Equal(["b", "a"], key.Columns);   // the key is (b, a), not table order
        Assert.Null(await catalog.PrimaryKeyAsync(null, Schema, "nokey"));
    }

    [Fact]
    public async Task Collects_primary_keys_across_a_table_list_and_skips_views()
    {
        await using var catalog = Open();
        var (keys, supported) = await catalog.PrimaryKeysAsync(await catalog.TablesAsync(schema: Schema));

        Assert.True(supported);
        Assert.Equal(["child", "myXtable", "my_table", "parent"], keys.Select(k => k.Table).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reports_declared_column_details()
    {
        await using var catalog = Open();
        var columns = await catalog.ColumnsAsync(schema: Schema, table: "parent");

        Assert.Equal(["a", "b", "label", "amount"], columns.Select(c => c.Name));
        var amount = columns.Single(c => c.Name == "amount");
        Assert.Equal((38, 0, false), (amount.Size, amount.Scale, amount.Nullable));
        Assert.True(columns.Single(c => c.Name == "label").Nullable);
        Assert.Equal([1, 2, 3, 4], columns.Select(c => c.Ordinal));
    }

    [Fact]
    public async Task Reports_foreign_keys_with_their_column_pairs()
    {
        await using var catalog = Open();
        var key = Assert.Single(await catalog.ForeignKeysAsync(null, Schema, "child"));

        Assert.Equal("fk_child_parent", key.Name);
        Assert.Equal(["pb", "pa"], key.Columns);
        Assert.Equal("parent", key.ReferencedTable);
        Assert.Equal(["b", "a"], key.ReferencedColumns);
    }

    [Fact]
    public async Task Reports_indexes_with_direction_and_uniqueness()
    {
        await using var catalog = Open();
        var indexes = await catalog.IndexesAsync(null, Schema, "parent");

        var unique = indexes.Single(i => i.Name == "ux_parent_label");
        Assert.True(unique.Unique);
        Assert.Equal([("label", true)], unique.Columns.Select(c => (c.Name, c.Descending)));
        Assert.Contains(indexes, i => i.Name == "pk_parent" && i.Unique);
    }

    [Fact]
    public async Task Reports_driver_facts()
    {
        await using var catalog = Open();
        var info = await catalog.GetInfoAsync();

        Assert.Contains("SQL Server", info.DbmsName);
        Assert.NotNull(info.DbmsVersion);
        Assert.Equal("\"x\"", info.IdentifierQuote!.Apply("x"));
        Assert.NotNull(info.SchemaTerm);   // "owner" from driver 17, "schema" from 18
        Assert.Contains("tempdb", await catalog.CatalogsAsync());
        Assert.Contains(Schema, await catalog.SchemasAsync());
    }

    [Fact]
    public async Task A_failed_connection_reports_the_sqlstate_only()
    {
        await using var catalog = OdbcCatalog.Connect("Driver={No Such Driver};Server=x");
        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.TablesAsync());
        Assert.Equal("SQLDriverConnect", ex.Function);
        Assert.DoesNotContain("No Such Driver", ex.Message);
    }

    [Fact]
    public async Task Lists_the_data_types_the_server_supports()
    {
        await using var catalog = Open();
        var types = await catalog.TypeInfoAsync();

        Assert.Contains(types, t => t.TypeName == "int" && t.SqlType == OdbcSqlType.Integer);
        Assert.Contains(types, t => t.TypeName == "varchar" && t.SqlType == OdbcSqlType.Varchar && t.ColumnSize == 8000);
        Assert.Single(await catalog.TypeInfoAsync(OdbcSqlType.Integer), t => t.TypeName == "int");
    }

    [Fact]
    public async Task Lists_procedures_and_their_parameters()
    {
        await using var catalog = Open();
        var procedure = Assert.Single(await catalog.ProceduresAsync(schema: Schema, procedure: "usp_add"));
        Assert.Equal("usp_add", procedure.Name);          // the ;1 version suffix is removed

        var columns = await catalog.ProcedureColumnsAsync(schema: Schema, procedure: "usp_add");
        Assert.Contains(columns, c => c.Name == "@a" && c.Kind == OdbcParameterKind.Input);
        Assert.Contains(columns, c => c.Name == "@b" && c.Kind == OdbcParameterKind.InputOutput);
    }

    [Fact]
    public async Task A_session_crawls_the_schema_on_one_connection_and_streams_match_the_buffered_calls()
    {
        await using var session = await OdbcCatalogSession.OpenAsync(connectionString!, new OdbcCatalogOptions { StatementTimeout = TimeSpan.FromSeconds(30) });
        var crawl = await session.CrawlAsync(schema: Schema);

        Assert.Contains(crawl.Tables, t => t.Name == "parent");
        Assert.Contains(crawl.ForeignKeys, k => k.Name == "fk_child_parent");
        Assert.Contains(crawl.PrimaryKeys, k => k.Name == "pk_parent");

        var streamed = new List<string>();
        await foreach (var c in session.Catalog.StreamColumnsAsync(schema: Schema, table: "parent")) streamed.Add(c.Name);
        Assert.Equal(4, streamed.Count);
        Assert.Contains("label", streamed);
    }

    [Fact]
    public async Task The_row_cap_applies_to_a_real_driver()
    {
        var options = new OdbcCatalogOptions { MaxRows = 2, RowLimitBehavior = OdbcRowLimitBehavior.Truncate };
        await using var catalog = OdbcCatalog.Connect(connectionString!, options);

        var tables = await catalog.TablesAsync(schema: Schema);
        Assert.Equal(2, tables.Count);
        Assert.Equal(2, tables.GetTruncation()!.MaxRows);
    }

    [Fact]
    public async Task The_driver_manager_is_not_32_bit_wide_in_SQLWCHAR()
    {
        // Connecting runs the SQLWCHAR width probe; a UTF-32 driver manager would throw OdbcConfigurationException here.
        await using var catalog = Open();
        Assert.NotNull((await catalog.GetInfoAsync()).DbmsName);
    }
}
