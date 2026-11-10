using Apache.Arrow;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataGateway.Tests.Flight;

/// <summary>
/// Uses the Microsoft Text ODBC driver, which exists only as a 32-bit driver, to prove queries really run in the
/// 32-bit worker: the same data source fails in the 64-bit gateway process.
/// </summary>
public sealed class WorkerTests : IAsyncLifetime
{
    private const string Driver = "{Microsoft Text Driver (*.txt; *.csv)}";

    private string _dir = "";
    private WebApplication? _app;
    private string _address = "";
    private string _aliceToken = "";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Requires 64-bit Windows with the 32-bit text ODBC driver.");

        _dir = Path.Combine(Path.GetTempPath(), "dbgateway-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "name,age\r\nAda,36\r\nGrace,45\r\nLinus,28\r\n");

        string connection = $"Driver={Driver};Dbq={_dir};Extensions=asc,csv,tab,txt;";
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Listen:Address"] = "127.0.0.1",
            ["Gateway:Listen:Port"] = "0",
            ["Gateway:Tls:Mode"] = "None",
            ["Gateway:Management:Enabled"] = "false",   // the management API is https only and on by default (plan 33 D15); not the subject here
            ["Gateway:DataDirectory"] = _dir,
            ["Gateway:Compression:Mode"] = "None",
            ["Gateway:Worker:Path"] = WorkerBuild.EnsurePublished(),
            ["Gateway:DataSources:text32:Provider"] = "odbc",
            ["Gateway:DataSources:text32:Bitness"] = "x86",
            ["Gateway:DataSources:text32:ConnectionString"] = connection,
            ["Gateway:DataSources:text64:Provider"] = "odbc",
            ["Gateway:DataSources:text64:Bitness"] = "x64",
            ["Gateway:DataSources:text64:ConnectionString"] = connection,
        };
        HomeSeed.Open(settings, ["text32", "text64"]);
        _app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _aliceToken = HomeSeed.ServiceAccount(_app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), "alice");
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { if (_dir.Length > 0) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<GatewayClient> LoginAsync()
    {
        var client = new GatewayClient(_address);
        await client.AuthenticateAsync("alice", _aliceToken);
        return client;
    }

    [Fact]
    public async Task Queries_a_32_bit_only_driver_through_the_worker()
    {
        using var client = await LoginAsync();
        var (schema, batches) = await client.QueryAsync("SELECT name, age FROM [people.csv] ORDER BY age", dataSource: "text32");

        Assert.Equal(["name", "age"], schema.FieldsList.Select(f => f.Name));
        var batch = Assert.Single(batches);
        Assert.Equal(3, batch.Length);
        Assert.Equal("Linus", ((StringArray)batch.Column("name")).GetString(0));
        Assert.Equal("Grace", ((StringArray)batch.Column("name")).GetString(2));
    }

    [Fact]
    public async Task The_same_driver_is_unavailable_to_the_64_bit_gateway_process()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT name FROM [people.csv]", dataSource: "text64"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Lists_tables_through_the_odbc_catalog_api_without_information_schema()
    {
        using var client = await LoginAsync();

        // The text driver has no INFORMATION_SCHEMA; table listing goes through SQLTables inside the worker.
        var rows = new List<string?[]>();
        await foreach (var batch in await client.Raw.GetTablesAsync(options: new DbDataGateway.Client.QueryOptions { DataSource = "text32" }))
            for (int r = 0; r < batch.Length; r++)
                rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => DbDataGateway.Data.ArrowText.Get(batch.Column(c), r)).ToArray());

        var people = Assert.Single(rows, r => r[2]!.StartsWith("people", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("TABLE", people[3]);

        Assert.Empty(await Flatten(await client.Raw.GetPrimaryKeysAsync("people#csv", options: new DbDataGateway.Client.QueryOptions { DataSource = "text32" })));
    }


    [Fact]
    public async Task Table_schemas_use_the_drivers_identifier_quoting()
    {
        using var client = await LoginAsync();
        var batches = new List<RecordBatch>();
        await foreach (var batch in await client.Raw.GetTablesAsync(includeSchema: true, options: new DbDataGateway.Client.QueryOptions { DataSource = "text32" }))
            batches.Add(batch);

        var batch0 = batches.First(b => b.Length > 0);
        var names = (StringArray)batch0.Column("table_name");
        int row = Enumerable.Range(0, batch0.Length).First(i => names.GetString(i).StartsWith("people", StringComparison.OrdinalIgnoreCase));
        var schemaBytes = ((BinaryArray)batch0.Column("table_schema")).GetBytes(row).ToArray();

        // An unreadable table is reported with an empty schema, so a real one must serialize larger than that.
        int emptyLength = Apache.Arrow.Flight.Sql.SchemaExtensions.SerializeSchema(new Schema([], null)).Length;
        Assert.True(schemaBytes.Length > emptyLength, "the table's columns were not read");
    }

    private static async Task<List<RecordBatch>> Flatten(DbDataGateway.Client.QueryResultStream stream)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in stream) if (batch.Length > 0) batches.Add(batch);
        return batches;
    }

    [Fact]
    public async Task Parameters_reach_the_32_bit_worker()
    {
        using var client = await LoginAsync();
        var result = await client.Raw.QueryAsync("SELECT name FROM [people.csv] WHERE age > ? ORDER BY age",
            [30], new DbDataGateway.Client.QueryOptions { DataSource = "text32" });
        var names = new List<string?>();
        await foreach (var batch in result)
            for (int r = 0; r < batch.Length; r++) names.Add(((StringArray)batch.Column("name")).GetString(r));
        Assert.Equal(["Ada", "Grace"], names);
    }

    [Fact]
    public async Task Declared_columns_are_listed_inside_the_worker()
    {
        var catalog = _app!.Services.GetRequiredService<DbDataGateway.Server.Data.DataSourceCatalog>();
        var source = catalog.Resolve(new GatewayPrincipal("alice", "test", ["*"]), "text32");
        var provider = Assert.IsAssignableFrom<DbDataGateway.Data.IMetadataProvider>(source.Executor);

        var columns = await provider.ListColumnsAsync(_dir, null, "people.csv", default);
        Assert.Equal(["name", "age"], columns.Select(c => c.Name.ToLowerInvariant()));
    }

    [Fact]
    public async Task Sql_info_describes_the_odbc_driver_through_the_worker()
    {
        using var client = await LoginAsync();
        var info = await SqlInfoReader.ReadAsync(await client.Raw.GetSqlInfoAsync(new DbDataGateway.Client.QueryOptions { DataSource = "text32" }));
        Assert.Equal("TEXT", info[0]);                    // the 32-bit driver names itself
        Assert.Equal("`", info[504]);                     // and says how it quotes identifiers
        Assert.Equal(true, info[3]);                      // AllowWrites is off for this source
    }

    [Fact]
    public async Task Worker_reports_sql_errors_to_the_client()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT nothing FROM [missing.csv]", dataSource: "text32"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Worker_serves_many_sequential_and_concurrent_queries()
    {
        using var client = await LoginAsync();
        var tasks = Enumerable.Range(0, 8).Select(_ => client.QueryAsync("SELECT name FROM [people.csv]", dataSource: "text32"));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(3, r.Batches.Sum(b => b.Length)));
    }
}
