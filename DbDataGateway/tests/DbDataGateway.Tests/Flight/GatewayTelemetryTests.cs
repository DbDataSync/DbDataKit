using DbDataKit.State;
using DbDataGateway.Client;
using DbDataGateway.Data;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using DbDataKit.Telemetry;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataGateway.Tests.Flight;

/// <summary>
/// Plan 22a GP28: the Gateway with <c>telemetry: enabled</c> (<c>Gateway:Telemetry:Enabled</c>) makes a span for each request of a Flight query and counts it; the query text, the row value and the bearer token are in no attribute or label.
/// </summary>
public sealed class GatewayTelemetryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbgateway-telemetry-" + Guid.NewGuid().ToString("N"));
    private TelemetryCapture _capture = null!;
    private WebApplication? _app;
    private string _address = "";
    private StateDatabase _state = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _capture = TelemetryCapture.Start();
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Listen:Address"] = "127.0.0.1",
            ["Gateway:Listen:Port"] = "0",
            ["Gateway:Tls:Mode"] = "None",
            ["Gateway:Management:Enabled"] = "false",
            ["Gateway:DataDirectory"] = _dir,
            ["Gateway:Compression:Mode"] = "None",
            ["Gateway:Telemetry:Enabled"] = "true",
            ["Gateway:Telemetry:Endpoint"] = "http://127.0.0.1:1",
            ["Gateway:DataSources:erp:Provider"] = "duckdb",
            ["Gateway:DataSources:erp:ConnectionString"] = "Data Source=:memory:",
            ["Gateway:DataSources:erp:Classifier"] = "off",
            ["Gateway:DataSources:erp:Groups:0"] = "finance",
        };
        _app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
        _state = _app.Services.GetRequiredService<StateDatabase>();
    }

    public async ValueTask DisposeAsync()
    {
        _capture.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_flight_query_is_a_span_and_a_counter_and_neither_the_text_nor_the_row_nor_the_token_leaves()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");
        using var client = new GatewayClient(_address);
        client.UseHeader("Bearer " + token);
        var result = await client.Raw.QueryAsync("SELECT 'ROW-SENTINEL-93' AS secret_col", new QueryOptions { DataSource = "erp" });
        await foreach (var batch in result) Assert.True(batch.Length > 0);
        _capture.Flush(_app!.Services);

        Assert.Contains(_capture.Spans, s => (string?)s.GetTagItem(HostTelemetry.ProductTag) == "dbdatagateway" && s.Kind == System.Diagnostics.ActivityKind.Server);
        Assert.True(_capture.Sum("dbdatakit.http.server.requests", (HostTelemetry.ProductTag, "dbdatagateway")) >= 1);
        var everything = string.Join('\n', _capture.AllAttributes());
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain("ROW-SENTINEL", everything);
        Assert.DoesNotContain("secret_col", everything);
        Assert.DoesNotContain("SELECT", everything);
    }
}
