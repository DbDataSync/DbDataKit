using DbDataProxy.Client;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Tests.Flight;

/// <summary>The driver's own error text never reaches a client; it reaches the local log (scrubbed) unless <c>Proxy:LogDriverErrors</c> is off.</summary>
public class DriverTextLoggingTests
{
    private static async Task<(string ClientDetail, IReadOnlyList<CapturedLine> Log)> RunAsync(bool logDriverErrors)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbproxy-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logs = new CaptureLoggerProvider();
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:LogDriverErrors"] = logDriverErrors ? "true" : "false",
            ["Proxy:DataSources:duck:Provider"] = "duckdb",
            ["Proxy:DataSources:duck:ConnectionString"] = "Data Source=:memory:",
            ["Proxy:DataSources:duck:Classifier"] = "off",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "duck",
        };
        await using var app = ProxyHost.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.Logging.SetMinimumLevel(LogLevel.Trace);
            b.Logging.AddProvider(logs);
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");

        using var client = new ProxyFlightClient(address);
        await client.AuthenticateAsync("alice", "pw");
        var ex = await Rpc.ExpectAsync(async () => await client.QueryAsync("SELECT * FROM salaries_of_the_board", new QueryOptions { DataSource = "duck" }));
        try { Directory.Delete(dir, true); } catch (IOException) { }
        return (ex.Status.Detail, logs.Lines);
    }

    [Fact]
    public async Task Driver_text_goes_to_the_log_not_to_the_client()
    {
        var (detail, log) = await RunAsync(logDriverErrors: true);

        Assert.DoesNotContain("salaries_of_the_board", detail);
        var line = Assert.Single(log, l => l.Text.Contains("Data source duck failed"));
        Assert.Contains("Syntax Catalog-Error", line.Text);
        Assert.Contains("salaries_of_the_board", line.Text);
    }

    [Fact]
    public async Task With_log_driver_errors_off_only_kind_and_code_are_logged()
    {
        var (detail, log) = await RunAsync(logDriverErrors: false);

        Assert.DoesNotContain("salaries_of_the_board", detail);
        var line = Assert.Single(log, l => l.Text.Contains("Data source duck failed"));
        Assert.Contains("Syntax Catalog-Error", line.Text);
        Assert.DoesNotContain("salaries_of_the_board", line.Text);
    }
}
