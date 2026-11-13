using Apache.Arrow;
using DbDataGateway.Client;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;
using DbDataKit.Serving.Semantic;
using DuckDB.NET.Data;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataGateway.Tests.Flight;

/// <summary>
/// Plan 22a GP12 (QG3) through the real server and a Flight SQL client, on a DuckDB data source: a model that puts access rules on a physical table closes that table to pass-through SQL (a statement, a prepared statement, an update) with the rule and the model named,
/// the catalog listing of the raw data source leaves the table out while the model lists its own datasets, the same data through the semantic path answers as the rules say, a table nobody put rules on and a data source with no rules at all are untouched, and a model taken out of
/// service frees the table (put back, it closes it again).
/// </summary>
public sealed class PassThroughRulesFlightTests : IAsyncLifetime
{
    private const string Catalog = """
        version: "0.3"
        name: orders_model
        timezone: UTC
        sources:
          warehouse: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
        tables:
          - name: fact_sales
            x_source: warehouse
            source: fact_sales
            primary_key: [order_id]
            columns:
              - {name: order_id, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: order_id}]}}
              - {name: customer_key, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: customer_key}]}}
              - {name: net_amount, datatype: Decimal, expression: {dialects: [{dialect: ANSI_SQL, expression: net_amount}]}}
          - name: dim_customer
            x_source: warehouse
            source: Dim_Customer
            primary_key: [customer_key]
            columns:
              - {name: customer_key, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: customer_key}]}}
              - {name: customer, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: customer}]}}
              - {name: region, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region}]}}
          - name: region_access
            x_source: warehouse
            source: region_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}
        relationships:
          - {name: sales_to_customer, from: fact_sales, to: dim_customer, from_columns: [customer_key], to_columns: [customer_key]}
        dimensions:
          customers:
            attributes:
              region:
                type: string
                bindings:
                  warehouse: {table: dim_customer, column: region}
        measures:
          orders.net_revenue:
            type: number
            source: warehouse
            agg: sum
            of: {table: fact_sales, column: net_amount}
            semantics: {grain: [customers.region], rollup: {default: sum}, spread: blank, status: governed}
        access:
          max_age: 24h
          row:
            - dataset: dim_customer
              groups:
                - id: by_region
                  rules:
                    - { id: region_of_group, effect: include, table: region_access, subject: who, columns: { region: region_code } }
          columns:
            - { subject: interns, dataset: dim_customer, column: customer, treatment: mask }
        """;

    private const string Seed = """
        CREATE TABLE Dim_Customer (customer_key INT, customer VARCHAR, region VARCHAR);
        INSERT INTO Dim_Customer VALUES (1, 'C1', 'West'), (2, 'C2', 'West'), (3, 'C3', 'East');
        CREATE TABLE fact_sales (order_id INT, customer_key INT, net_amount DECIMAL(19,4));
        INSERT INTO fact_sales VALUES (1, 1, 100), (2, 2, 120), (3, 3, 70);
        CREATE TABLE region_access (who VARCHAR, region_code VARCHAR);
        INSERT INTO region_access VALUES ('analysts', 'West');
        CREATE TABLE prices (id INT, note VARCHAR);
        INSERT INTO prices VALUES (1, 'a'), (2, 'b');
        """;

    private WebApplication? _app;
    private string _dir = "";
    private string _token = "";
    private string _address = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbgateway-gp12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var database = Path.Combine(_dir, "warehouse.duckdb");
        using (var c = new DuckDBConnection($"Data Source={database}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = Seed;
            cmd.ExecuteNonQuery();
        }

        var plain = Path.Combine(_dir, "plain.duckdb");
        using (var c = new DuckDBConnection($"Data Source={plain}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE dim_customer (id INT, note VARCHAR); INSERT INTO dim_customer VALUES (9, 'other')";
            cmd.ExecuteNonQuery();
        }

        var catalog = Path.Combine(_dir, "orders.catalog.yaml");
        File.WriteAllText(catalog, Catalog);
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Listen:Address"] = "127.0.0.1",
            ["Gateway:Listen:Port"] = "0",
            ["Gateway:Tls:Mode"] = "None",
            ["Gateway:Management:Enabled"] = "false",
            ["Gateway:DataDirectory"] = _dir,
            ["Gateway:Compression:Mode"] = "None",
            ["Gateway:DataSources:warehouse:Provider"] = "duckdb",
            ["Gateway:DataSources:warehouse:ConnectionString"] = $"Data Source={database}",
            ["Gateway:DataSources:warehouse:AllowWrites"] = "true",
            ["Gateway:DataSources:plain:Provider"] = "duckdb",
            ["Gateway:DataSources:plain:ConnectionString"] = $"Data Source={plain}",
            ["Gateway:Semantic:Models:0:Catalog"] = catalog,
            ["Gateway:Semantic:Models:0:Sources:warehouse"] = "warehouse",
        };
        HomeSeed.Open(settings, ["warehouse", "plain"], group: "analysts");
        _app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _token = HomeSeed.ServiceAccount(_app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), "alice", "analysts");
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<GatewayFlightClient> LoginAsync()
    {
        var client = new GatewayFlightClient(_address);
        await client.AuthenticateAsync("alice", _token);
        return client;
    }

    private static async Task<List<string>> RowsOf(IAsyncEnumerable<RecordBatch> result)
    {
        var rows = new List<string>();
        await foreach (var batch in result)
            for (var r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => batch.Column(c) switch
                {
                    StringArray a => a.GetString(r),
                    Int32Array a => a.GetValue(r)!.Value.ToString(),
                    Int64Array a => a.GetValue(r)!.Value.ToString(),
                    DoubleArray a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    Decimal128Array a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    var other => other.GetType().Name,
                })));
        return rows;
    }

    private async Task<List<string>> Sql(string text, string source = "warehouse")
    {
        using var client = await LoginAsync();
        return await RowsOf(await client.QueryAsync(text, new QueryOptions { DataSource = source }));
    }

    private async Task<RpcException> Refused(string text, string source = "warehouse") => await Rpc.ExpectAsync(() => Sql(text, source));

    private async Task<List<string>> Tables(string source)
    {
        using var client = await LoginAsync();
        var names = new List<string>();
        await foreach (var batch in await client.GetTablesAsync(options: new QueryOptions { DataSource = source }))
            for (var i = 0; i < batch.Length; i++) names.Add(((StringArray)batch.Column("table_name")).GetString(i));
        return names;
    }

    [Theory]
    [InlineData("SELECT * FROM Dim_Customer")]
    [InlineData("select * from dim_customer")]
    [InlineData("SELECT * FROM \"DIM_CUSTOMER\"")]
    [InlineData("SELECT * FROM main.dim_customer")]
    [InlineData("SELECT * FROM \"main\".\"Dim_Customer\" c")]
    [InlineData("SELECT * FROM [dim_customer]")]
    [InlineData("SELECT * FROM `dim_customer`")]
    [InlineData("SELECT * FROM warehouse.main . /* c */ dim_customer")]
    [InlineData("SELECT * FROM main -- c\n . dim_customer")]
    [InlineData("SELECT f.order_id FROM fact_sales f JOIN dim_customer c ON c.customer_key = f.customer_key")]
    [InlineData("SELECT order_id FROM fact_sales WHERE customer_key IN (SELECT customer_key FROM dim_customer)")]
    [InlineData("WITH x AS (SELECT * FROM dim_customer) SELECT * FROM x")]
    public async Task A_statement_that_names_a_table_with_rules_is_refused_in_every_spelling_and_names_the_rule_and_the_model(string text)
    {
        var error = await Refused(text);

        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.Contains("dim_customer", error.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row rules 'region_of_group'", error.Status.Detail);
        Assert.Contains("column rules on 'customer' (mask)", error.Status.Detail);
        Assert.Contains("semantic:orders_model", error.Status.Detail);
        Assert.Contains("Ask the model", error.Status.Detail);
    }

    [Fact]
    public async Task A_prepared_statement_and_an_update_are_refused_too()
    {
        using var client = await LoginAsync();
        var prepared = await Rpc.ExpectAsync(async () => await RowsOf(await client.QueryAsync("SELECT * FROM dim_customer WHERE customer_key = ?", [1], new QueryOptions { DataSource = "warehouse" })));
        Assert.Equal(StatusCode.PermissionDenied, prepared.StatusCode);
        Assert.Contains("semantic:orders_model", prepared.Status.Detail);

        var update = await Rpc.ExpectAsync(() => client.ExecuteUpdateAsync("UPDATE dim_customer SET region = 'North'", new QueryOptions { DataSource = "warehouse" }));
        Assert.Equal(StatusCode.PermissionDenied, update.StatusCode);
        // the update never ran: the model still reads the three customers' West rows for the group
        Assert.Equal(["West|220"], await Sql("SELECT customers.region, MEASURE(orders.net_revenue) FROM orders_model GROUP BY ALL LIMIT 100", "semantic:orders_model"));
    }

    [Fact]
    public async Task Dynamic_SQL_cannot_be_checked_so_it_is_refused_on_a_source_with_rules_and_a_name_in_a_literal_is_caught()
    {
        var dynamic = await Refused("SELECT * FROM query('select * from dim_customer')");   // the text of a literal may be a statement
        Assert.Equal(StatusCode.PermissionDenied, dynamic.StatusCode);
        Assert.Contains("semantic:orders_model", dynamic.Status.Detail);

        var exec = await Refused("EXEC('select 1')");   // text the scan cannot see
        Assert.Equal(StatusCode.PermissionDenied, exec.StatusCode);
        Assert.Contains("dynamic execution", exec.Status.Detail);

        // a source with no rules runs such a statement as it always did (it fails here only because DuckDB has no EXEC)
        Assert.NotEqual(StatusCode.PermissionDenied, (await Refused("EXEC('select 1')", "plain")).StatusCode);
    }

    [Fact]
    public async Task A_table_with_no_rule_is_untouched_and_so_is_the_rules_table_itself()
    {
        Assert.Equal(["1|a", "2|b"], await Sql("SELECT id, note FROM prices ORDER BY id"));
        Assert.Equal(["analysts|West"], await Sql("SELECT who, region_code FROM region_access"));
        Assert.Equal(["1", "2", "3"], await Sql("SELECT order_id FROM fact_sales ORDER BY order_id"));
    }

    [Fact]
    public async Task A_data_source_no_model_put_rules_on_is_untouched_even_for_a_table_of_the_same_name()
    {
        Assert.Equal(["9|other"], await Sql("SELECT * FROM dim_customer", "plain"));
        Assert.Contains(await Tables("plain"), n => n == "dim_customer");
    }

    [Fact]
    public async Task The_same_data_through_the_semantic_path_works_and_is_filtered_by_the_rules()
    {
        var rows = await Sql("SELECT customers.region, MEASURE(orders.net_revenue) FROM orders_model GROUP BY ALL LIMIT 100", "semantic:orders_model");

        Assert.Equal(["West|220"], rows);   // the group analysts has the rule row West: East is not shown
    }

    [Fact]
    public async Task The_raw_listing_leaves_a_ruled_table_out_and_lists_the_others()
    {
        var raw = await Tables("warehouse");
        Assert.DoesNotContain(raw, n => string.Equals(n, "dim_customer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("fact_sales", raw);
        Assert.Contains("prices", raw);
        Assert.Contains("region_access", raw);


        // the model's own datasets are listed through the model (AccessRowRulesTests reads them with the catalog service)
    }

    [Fact]
    public async Task A_model_taken_out_of_service_frees_the_table_and_put_back_it_closes_it_again()
    {
        var models = _app!.Services.GetRequiredService<SemanticModels>();
        var model = models.Find("orders_model")!;
        Assert.Equal(StatusCode.PermissionDenied, (await Refused("SELECT * FROM dim_customer")).StatusCode);

        Assert.True(models.Remove("orders_model"));
        Assert.Equal(["1|C1|West", "2|C2|West", "3|C3|East"], await Sql("SELECT * FROM dim_customer ORDER BY 1"));
        Assert.Contains(await Tables("warehouse"), n => string.Equals(n, "dim_customer", StringComparison.OrdinalIgnoreCase));

        models.Set(model);   // a rollback to the version that had the rules
        Assert.Equal(StatusCode.PermissionDenied, (await Refused("SELECT * FROM dim_customer")).StatusCode);
        Assert.DoesNotContain(await Tables("warehouse"), n => string.Equals(n, "dim_customer", StringComparison.OrdinalIgnoreCase));
    }
}
