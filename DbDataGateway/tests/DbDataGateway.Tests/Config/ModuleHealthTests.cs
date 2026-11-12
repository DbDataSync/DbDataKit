using System.Net;
using System.Net.Sockets;
using DbDataKit.Hosting;
using DbDataKit.Serving.FlightSql;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 20 O18: every module of the serving layer is one line of the host's readiness report, and one module's trouble turns its line and no other.</summary>
public sealed class ModuleHealthTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-health-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task StartAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var ports = new HashSet<int>();
            while (ports.Count < 4) ports.Add(FreePort());
            var p = ports.ToArray();
            app = GatewayHost.Build([
                $"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Management:Enabled=false", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0",
                "--Gateway:Modules:0=flightsql", "--Gateway:Modules:1=identity", "--Gateway:Modules:2=apps", "--Gateway:Modules:3=store", "--Gateway:Modules:4=views",
                $"--Gateway:Identity:Address=127.0.0.1", $"--Gateway:Identity:Port={p[0]}", $"--Gateway:Identity:PublicUrl=https://localhost:{p[0]}",
                "--Gateway:Apps:Address=127.0.0.1", $"--Gateway:Apps:Port={p[1]}", "--Gateway:Apps:Home:ClientId=health-test",
                "--Gateway:Store:Address=127.0.0.1", $"--Gateway:Store:Port={p[2]}",
                "--Gateway:Views:Address=127.0.0.1", $"--Gateway:Views:Port={p[3]}",
            ]);
            try { await app.StartAsync(); return; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }
    }

    private async Task<Dictionary<string, ComponentHealth>> ReportAsync()
    {
        var report = await HealthEvaluator.EvaluateAsync(app!.Services.GetServices<IReadinessProbe>(), app.Services.GetRequiredService<IDrainCoordinator>(), default);
        return report.Components.ToDictionary(c => c.Name);
    }

    private static readonly string[] Modules = ["flightsql", "identity", "apps", "store", "views"];

    [Fact]
    public async Task The_five_modules_are_lines_of_the_report_and_stopping_or_draining_one_turns_only_its_line()
    {
        await StartAsync();
        var all = await ReportAsync();
        foreach (var module in Modules) Assert.Equal(HealthStatus.Healthy, all[module].Status);

        // a module that stops is down; a module that drains is degraded; the others stay as they were
        await app!.Services.GetRequiredService<GatewayStoreModule>().StopAsync(default);
        await app.Services.GetRequiredService<FlightSqlModule>().DrainAsync();
        var after = await ReportAsync();
        Assert.Equal(HealthStatus.Unhealthy, after["store"].Status);
        Assert.Contains("not started", after["store"].Detail);
        Assert.Equal(HealthStatus.Degraded, after["flightsql"].Status);
        foreach (var module in new[] { "identity", "apps", "views" }) Assert.Equal(HealthStatus.Healthy, after[module].Status);
    }

    [Fact]
    public async Task A_state_database_that_does_not_answer_turns_the_modules_that_read_it()
    {
        await StartAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dataDirectory, recursive: true);   // the state file is gone with its folder: the back-end every module reads does not answer
        var after = await ReportAsync();
        foreach (var module in new[] { "identity", "apps", "store" })
        {
            Assert.Equal(HealthStatus.Unhealthy, after[module].Status);
            Assert.Contains("state database", after[module].Detail);
        }
        Assert.Equal(HealthStatus.Degraded, after["flightsql"].Status);
    }
}
