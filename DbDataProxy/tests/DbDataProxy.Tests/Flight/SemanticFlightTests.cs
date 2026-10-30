using Apache.Arrow;
using DbDataProxy.Client;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using DuckDB.NET.Data;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Flight;

/// <summary>Semantic queries through the real server (plan 19, J10): a model named in <c>Semantic</c> answers a request or semantic SQL sent as a Flight SQL statement against <c>semantic:&lt;model&gt;</c>, reading its data through the data source as the signed-in client.</summary>
public sealed class SemanticFlightTests : IAsyncLifetime
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                   "order": [["orders.net_revenue", "desc"]]}}
        """;

    private WebApplication? _app;
    private string _dir = "";
    private string _address = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-semantic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var database = Path.Combine(_dir, "warehouse.duckdb");
        using (var c = new DuckDBConnection($"Data Source={database}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            cmd.ExecuteNonQuery();
        }

        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:DataSources:warehouse:Provider"] = "duckdb",
            ["Proxy:DataSources:warehouse:ConnectionString"] = $"Data Source={database}",
            ["Proxy:DataSources:other:Provider"] = "duckdb",
            ["Proxy:DataSources:other:ConnectionString"] = "Data Source=:memory:",
            ["Proxy:Semantic:Models:0:Catalog"] = Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"),
            ["Proxy:Semantic:Models:0:Sources:sqlserver_sales"] = "warehouse",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "warehouse",
            ["Proxy:Users:1:Name"] = "bob",
            ["Proxy:Users:1:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:1:DataSources:0"] = "other",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(Schema Schema, List<string> Rows)> QueryAsync(string user, string text)
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync(user, "pw");
        var result = await client.QueryAsync(text, new QueryOptions { DataSource = "semantic:sales_catalog" });
        var rows = new List<string>();
        await foreach (var batch in result)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => batch.Column(c) switch
                {
                    StringArray a => a.GetString(r),
                    Int64Array a => a.GetValue(r)!.Value.ToString(),
                    DoubleArray a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    var other => other.GetType().Name,
                })));
        return (result.Schema, rows);
    }

    [Fact]
    public async Task AJsonRequestAnswersWithTheFixturesRows()
    {
        var (schema, rows) = await QueryAsync("alice", Request);

        Assert.Equal(["West|500|6", "East|400|4"], rows);
        Assert.Equal("complete", schema.Metadata["qis.status"]);
    }

    private const string Marked = "SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM sales_catalog WHERE date.fiscal_quarter = {0} GROUP BY ALL ORDER BY MEASURE(orders.net_revenue) DESC LIMIT 100";

    private async Task<List<string>> BoundAsync(string sql, object values)
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        var options = new QueryOptions { DataSource = "semantic:sales_catalog" };
        var result = values switch
        {
            IReadOnlyDictionary<string, object?> named => await client.QueryAsync(sql, named, options),
            IReadOnlyList<object?> positional => await client.QueryAsync(sql, positional, options),
            _ => throw new ArgumentException("named or positional"),
        };
        var rows = new List<string>();
        await foreach (var batch in result)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => batch.Column(c) switch
                {
                    StringArray a => a.GetString(r),
                    Int64Array a => a.GetValue(r)!.Value.ToString(),
                    DoubleArray a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    var other => other.GetType().Name,
                })));
        return rows;
    }

    [Fact]
    public async Task APreparedStatementOnAModelBindsNamedAndPositionalValuesAndAnswersTheLiteralRows()
    {
        var (_, expected) = await QueryAsync("alice", string.Format(Marked, "'FY27-Q1'"));

        Assert.Equal(expected, await BoundAsync(string.Format(Marked, ":quarter"), new Dictionary<string, object?> { ["quarter"] = "FY27-Q1" }));
        Assert.Equal(expected, await BoundAsync(string.Format(Marked, "?"), new List<object?> { "FY27-Q1" }));
    }

    [Fact]
    public async Task APreparedStatementOnAModelReportsItsParametersWithTheAttributeEachIsFor()
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        var schema = await client.DescribeParametersAsync(
            "SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog WHERE date.fiscal_quarter = :quarter AND customers.region IN :regions GROUP BY ALL LIMIT $top",
            new QueryOptions { DataSource = "semantic:sales_catalog" });

        Assert.Equal(["quarter", "regions", "top"], schema.FieldsList.Select(f => f.Name));
        Assert.Equal("date.fiscal_quarter", schema.GetFieldByName("quarter").Metadata["qis.for"]);
        Assert.IsType<Apache.Arrow.Types.StringType>(schema.GetFieldByName("quarter").DataType);
        Assert.IsType<Apache.Arrow.Types.ListType>(schema.GetFieldByName("regions").DataType);
        Assert.IsType<Apache.Arrow.Types.Int64Type>(schema.GetFieldByName("top").DataType);
    }

    [Fact]
    public async Task AListValueBindsToAListParameter()
    {
        var literal = "SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog WHERE customers.region IN ('East', 'West') GROUP BY ALL LIMIT 100";
        var (_, expected) = await QueryAsync("alice", literal);

        var bound = await BoundAsync(literal.Replace("('East', 'West')", ":regions"), new Dictionary<string, object?> { ["regions"] = new[] { "East", "West" } });

        Assert.NotEmpty(expected);
        Assert.Equal(expected, bound);
    }

    [Fact]
    public async Task SemanticSqlAnswersWithTheSameRows()
    {
        var (_, rows) = await QueryAsync("alice", """
            SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count)
            FROM sales_catalog WHERE date.fiscal_quarter = 'FY27-Q1' GROUP BY ALL ORDER BY MEASURE(orders.net_revenue) DESC LIMIT 100
            """);

        Assert.Equal(["West|500|6", "East|400|4"], rows);
    }

    [Fact]
    public async Task AClientWithoutTheDataSourceIsRefusedAsForPlainSql()
    {
        var error = await Rpc.ExpectAsync(() => QueryAsync("bob", Request));

        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }

    [Fact]
    public async Task ARequestTheEngineRefusesIsInvalidArgument()
    {
        var error = await Rpc.ExpectAsync(() => QueryAsync("alice", Request.Replace("orders.net_revenue", "orders.no_such_measure")));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("no_such_measure", error.Status.Detail);
    }

    [Fact]
    public async Task APlainSqlStatementOnTheDataSourceIsUnchanged()
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        var result = await client.QueryAsync("SELECT count(*) AS n FROM fact_sales", new QueryOptions { DataSource = "warehouse" });
        long n = 0;
        await foreach (var batch in result) n += ((Int64Array)batch.Column(0)).GetValue(0)!.Value;

        Assert.True(n > 0);
    }
}
