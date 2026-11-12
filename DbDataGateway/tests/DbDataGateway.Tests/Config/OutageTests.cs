using System.Net;
using System.Net.Sockets;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The outage table (docs/serving-layer.md, "What keeps working when a back-end is down"; plan 20 O13), a test per row with that back-end not running. The serving layer is started on its own state with nothing else beside it:
/// no Build, no Sync, no console and no DbDataStore server ever run in these tests, and what each of them would have left behind (a published package, replicated data, a paired console's absence) is what is read.
/// The DbDataStore row is on the engines the Store's tables need: <c>GroupStateTests.The_store_module_reads_and_writes_master_data_through_any_instance_with_no_store_server_running</c> and its neighbours.
/// </summary>
public sealed class OutageTests : IAsyncLifetime
{
    private const string Model = "sales_catalog";
    private const string Source = "sales-ro";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-outage-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int identityPort;

    private static string CatalogText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A DuckDB file with the semantic engine's conformance data (what a replication or a load would have left), and the data source that reads it.</summary>
    public ValueTask InitializeAsync()
    {
        var config = GatewayConfigFile.DirectoryIn(dataDirectory);
        Directory.CreateDirectory(Path.Combine(config, "connections"));
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        var file = Path.Combine(dataDirectory, "sales.duckdb");
        using (var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(config, "connections", Source + ".yaml"), Conn.Yaml(Source, "duckdb", "Data Source=" + file, gateway: ["groups: [analysts]"]));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    /// <summary>The serving layer alone: Flight SQL and the identity home, management off (no console is paired, none can call).</summary>
    private async Task StartAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            identityPort = FreePort();
            app = GatewayHost.Build([
                $"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0", "--Gateway:Management:Enabled=false",
                "--Gateway:Modules:0=flightsql", "--Gateway:Modules:1=identity", "--Gateway:Identity:Address=127.0.0.1", $"--Gateway:Identity:Port={identityPort}",
                $"--Gateway:Identity:PublicUrl=https://localhost:{identityPort}", "--Gateway:DefinitionPollSeconds=1",
            ]);
            try { await app.StartAsync(); return; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }
    }

    private T Get<T>() where T : notnull => app!.Services.GetRequiredService<T>();

    private static readonly ServingContext Ada = new(new GatewayCaller(new DbDataGateway.Server.Auth.GatewayPrincipal("ada", "test", [Source], homeGroups: ["analysts"])));

    private static async Task<int> RowsAsync(IServingAnswer answer)
    {
        await using var _ = answer;
        var rows = 0;
        await foreach (var batch in answer.ReadBatchesAsync()) rows += batch.Length;
        return rows;
    }

    private async Task<IServingAnswer> SalesByRegionAsync() =>
        await Get<IServingServices>().Queries.RunAsync(Ada, new ServingRequest.SemanticSql("semantic:" + Model, $"SELECT customers.region, MEASURE(orders.net_revenue) FROM {Model} GROUP BY ALL LIMIT 10"));

    private void Publish(string catalog)
    {
        var models = Get<PublishedSemanticModels>();
        models.Publish(Model, catalog, null, "build", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        models.ServeNow(Model);
    }

    [Fact]
    public async Task DbDataBuild_down_every_query_runs_on_the_last_published_package_and_a_changed_package_waits_for_the_next_publish()
    {
        await StartAsync();
        Publish(CatalogText);   // what Build left behind: published once, then Build is gone

        var first = await SalesByRegionAsync();
        Assert.Equal("1", first.Provenance.DefinitionVersion);
        Assert.True(await RowsAsync(first) > 0);
        // a query on the source itself needs no package at all
        Assert.Equal(4, await RowsAsync(await Get<IServingServices>().Queries.RunAsync(Ada, new ServingRequest.Sql(Source, "SELECT * FROM dim_customer"))));

        // nothing a person does at runtime changes the package: a second query is the same version, until Build publishes again
        Assert.Equal("1", (await SalesByRegionAsync()).Provenance.DefinitionVersion);
        Publish(CatalogText.Replace("Governed sales", "Governed sales, changed"));
        Assert.Equal("2", (await SalesByRegionAsync()).Provenance.DefinitionVersion);
    }

    [Fact]
    public async Task DbDataSync_down_every_query_runs_on_the_data_as_of_the_last_replication()
    {
        await StartAsync();
        Publish(CatalogText);

        // the file is what the last replication wrote; no replication runs, and the answer is that data, for the model and for the source
        var semantic = await RowsAsync(await SalesByRegionAsync());
        Assert.True(semantic > 0);
        var count = await Get<IServingServices>().Queries.RunAsync(Ada, new ServingRequest.Sql(Source, "SELECT COUNT(*) AS n FROM dim_customer"));
        await using (count)
            await foreach (var batch in count.ReadBatchesAsync())
                Assert.True(((Apache.Arrow.Int64Array)batch.Column(0)).GetValue(0) > 0);
        Assert.Equal(semantic, await RowsAsync(await SalesByRegionAsync()));
    }

    [Fact]
    public async Task The_console_down_everything_an_end_user_does_still_works_and_only_administration_waits()
    {
        await StartAsync();
        Publish(CatalogText);

        // no console is paired and the management listener is off: queries answer, and people reach the identity home to sign in
        Assert.True(await RowsAsync(await SalesByRegionAsync()) > 0);
        using var home = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var discovery = await home.GetAsync($"https://127.0.0.1:{identityPort}/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        // administration is what waits: nothing listens for it
        Assert.False(Get<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue.Management.Enabled);
    }
}
