using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

public class MetadataTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync()
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
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

    [Fact]
    public async Task Lists_catalogs_and_schemas()
    {
        using var client = await LoginAsync();

        var catalogs = await client.Raw.GetCatalogsAsync();
        Assert.Equal("catalog_name", catalogs.Schema.FieldsList.Single().Name);
        Assert.Contains(await RowsAsync(catalogs), r => r[0] == "test"); // the DuckDB file is test.duckdb; DuckDB also lists system and temp

        var schemas = await RowsAsync(await client.Raw.GetSchemasAsync());
        Assert.Contains(schemas, r => r[1] == "main");

        var none = await RowsAsync(await client.Raw.GetSchemasAsync(schemaPattern: "no_such_%"));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Lists_tables_with_standard_types_and_filters()
    {
        using var client = await LoginAsync();

        var all = await RowsAsync(await client.Raw.GetTablesAsync());
        var numbers = Assert.Single(all, r => r[2] == "numbers");
        Assert.Equal("TABLE", numbers[3]);
        Assert.Equal("main", numbers[1]);

        var filtered = await RowsAsync(await client.Raw.GetTablesAsync(tablePattern: "NUM%"));
        Assert.All(filtered, r => Assert.StartsWith("numbers", r[2]!, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(filtered);

        Assert.Empty(await RowsAsync(await client.Raw.GetTablesAsync(tableTypes: ["VIEW"], tablePattern: "numbers")));
    }

    [Fact]
    public async Task Tables_can_include_their_arrow_schema()
    {
        using var client = await LoginAsync();
        var stream = await client.Raw.GetTablesAsync(tablePattern: "numbers", includeSchema: true);
        Assert.Equal("table_schema", stream.Schema.FieldsList.Last().Name);

        await foreach (var batch in stream)
        {
            var bytes = ((BinaryArray)batch.Column("table_schema")).GetBytes(0).ToArray();
            var schema = SchemaExtensions.DeserializeSchema(bytes);
            Assert.Equal(["id", "name", "score", "day"], schema.FieldsList.Select(f => f.Name));
            Assert.IsType<Int32Type>(schema.GetFieldByName("id").DataType);
        }
    }

    [Fact]
    public async Task Lists_table_types()
    {
        using var client = await LoginAsync();
        var types = (await RowsAsync(await client.Raw.GetTableTypesAsync())).Select(r => r[0]).ToList();
        Assert.Contains("TABLE", types);
        Assert.Contains("VIEW", types);
    }

    [Fact]
    public async Task Reports_primary_keys_in_column_order()
    {
        using var client = await LoginAsync();
        await client.RawUpdateAsync("CREATE TABLE IF NOT EXISTS pk_demo(a INTEGER, b INTEGER, v VARCHAR, PRIMARY KEY (a, b))");

        var keys = await RowsAsync(await client.Raw.GetPrimaryKeysAsync("pk_demo"));
        Assert.Equal(["a", "b"], keys.Select(r => r[3]));
        Assert.Equal(["1", "2"], keys.Select(r => r[5]));
    }

    [Fact]
    public async Task Reports_server_info()
    {
        using var client = await LoginAsync();
        var stream = await client.Raw.GetSqlInfoAsync();
        int rows = 0;
        await foreach (var batch in stream)
        {
            rows += batch.Length;
            var names = (UInt32Array)batch.Column("info_name");
            var union = (DenseUnionArray)batch.Column("value");
            int serverName = Enumerable.Range(0, batch.Length).First(i => names.GetValue(i) == 0);
            var strings = (StringArray)union.Fields[0];
            Assert.Equal("DuckDB", strings.GetString(union.ValueOffsets[serverName]));   // alice's default data source
        }
        Assert.True(rows >= 6, $"expected the core server facts plus dialect details, got {rows} rows");
    }

    [Fact]
    public async Task Metadata_requires_authentication_and_respects_data_source_access()
    {
        using var anonymous = proxy.Connect();
        var ex = await Rpc.ExpectAsync(() => anonymous.Raw.GetTablesAsync().ContinueWith(t => RowsAsync(t.Result)).Unwrap());
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);

        using var client = await LoginAsync();
        var denied = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.GetTablesAsync(options: new QueryOptions { DataSource = "other" })));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }
}

public class DeclaredColumnTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<Schema> TableSchemaAsync(string table)
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        await foreach (var batch in await client.Raw.GetTablesAsync(tablePattern: table, includeSchema: true))
        {
            if (batch.Length == 0) continue;
            return Apache.Arrow.Flight.Sql.SchemaExtensions.DeserializeSchema(((BinaryArray)batch.Column("table_schema")).GetBytes(0).ToArray());
        }
        throw new InvalidOperationException($"Table {table} was not listed.");
    }

    [Fact]
    public async Task Table_schemas_report_declared_decimal_precision_and_scale()
    {
        var schema = await TableSchemaAsync("prices");
        var amount = Assert.IsType<Decimal128Type>(schema.GetFieldByName("amount").DataType);
        Assert.Equal((12, 3), (amount.Precision, amount.Scale));

        // A computed decimal (the shape from the bug report) keeps its fractional digits too.
        var computed = Assert.IsType<Decimal128Type>((await TableSchemaAsync("scaled")).GetFieldByName("amount").DataType);
        Assert.Equal(1, computed.Scale);
    }

    [Fact]
    public async Task Table_schemas_report_key_columns_as_not_null()
    {
        var schema = await TableSchemaAsync("prices");
        Assert.False(schema.GetFieldByName("id").IsNullable);
        Assert.True(schema.GetFieldByName("amount").IsNullable);
        Assert.True(schema.GetFieldByName("note").IsNullable);
    }

    [Fact]
    public async Task Zero_row_decimal_results_do_not_collapse_to_scale_zero()
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        var (schema, _) = await client.QueryAsync("SELECT amount FROM prices WHERE 1 = 0");
        Assert.True(Assert.IsType<Decimal128Type>(schema.GetFieldByName("amount").DataType).Scale > 0);
    }
}

public class SqlInfoTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    // Ids from the Flight SQL spec.
    private const uint ServerName = 0, ServerVersion = 1, ReadOnly = 3;
    private static readonly uint Cancel = (uint)Arrow.Flight.Protocol.Sql.SqlInfo.FlightSqlServerCancel;
    private static readonly uint BulkIngestion = (uint)Arrow.Flight.Protocol.Sql.SqlInfo.FlightSqlServerBulkIngestion;
    private const uint IdentifierCase = 503, QuoteChar = 504, NullOrdering = 507;

    private async Task<Dictionary<uint, object>> InfoAsync(string? dataSource)
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        return await SqlInfoReader.ReadAsync(await client.Raw.GetSqlInfoAsync(new DbDataProxy.Client.QueryOptions { DataSource = dataSource }));
    }

    [Fact]
    public async Task Reports_the_selected_data_sources_product_and_dialect()
    {
        var info = await InfoAsync("duck");

        Assert.Equal("DuckDB", info[ServerName]);
        Assert.False(string.IsNullOrEmpty((string)info[ServerVersion]));
        Assert.Equal("\"", info[QuoteChar]);
        Assert.Equal(1L, info[IdentifierCase]);   // case insensitive
        Assert.Equal(3L, info[NullOrdering]);     // nulls sort at the end
        Assert.Equal(false, info[Cancel]);
        Assert.Equal(false, info[BulkIngestion]);
    }

    [Fact]
    public async Task Read_only_follows_the_data_sources_write_setting()
    {
        Assert.Equal(false, (await InfoAsync("duck"))[ReadOnly]);
        Assert.Equal(true, (await InfoAsync("duck_ro"))[ReadOnly]);
    }

    [Fact]
    public async Task Uses_the_users_default_data_source_when_none_is_named()
    {
        Assert.Equal("DuckDB", (await InfoAsync(null))[ServerName]);
    }

    [Fact]
    public async Task Does_not_describe_data_sources_the_user_cannot_use()
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        var ex = await Rpc.ExpectAsync(async () => await SqlInfoReader.ReadAsync(
            await client.Raw.GetSqlInfoAsync(new DbDataProxy.Client.QueryOptions { DataSource = "other" })));
        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, ex.StatusCode);
    }
}
