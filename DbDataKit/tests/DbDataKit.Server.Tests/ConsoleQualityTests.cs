using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The suite's data quality in the console (plan 23a FQ17). The servers that evaluated checks (Build, Sync, Store) were deleted (plan 33 SC23) and no server of the suite keeps check results today, so no server is asked and the overview is
/// empty, whatever a server would answer; the join of answers by dataset identity stays for the server that will.
/// </summary>
public sealed class ConsoleQualityTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-quality-" + Guid.NewGuid().ToString("N")[..8]);
    private int asked;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer runner = null!;
    private PairableServer gateway = null!;

    public async ValueTask InitializeAsync()
    {
        runner = await PairableServer.StartAsync(product: "dbdatarunner", instanceId: "runner-1", map: (server, _, _) =>
            server.MapGet("/api/v1/quality", () => { Interlocked.Increment(ref asked); return Results.Json(new { datasets = Array.Empty<object>() }); }));
        gateway = await PairableServer.StartAsync(product: "dbdatagateway", instanceId: "gateway-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(runner.Url, runner.NewCodeWith(ServerRole.Viewer), "Runner one", default);
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Viewer), "Gateway one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { runner, gateway }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path, bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signedIn)
        {
            var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, UserRole.Viewer).Id).Id;
            request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        }
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.StartsWith('{') ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task No_server_of_the_suite_keeps_check_results_so_none_is_asked_and_the_overview_is_empty()
    {
        var (status, body) = await Get("/api/quality");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!["datasets"]!.AsArray());
        Assert.Empty(body["servers"]!.AsArray());
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Nobody_signed_in_reads_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/quality", signedIn: false)).Status);
    }

    [Fact]
    public void No_product_has_a_path_for_its_checks()
    {
        foreach (var product in new[] { ServerProducts.Gateway, ServerProducts.Runner, ServerProducts.Workspace, RemovedProducts.Build, RemovedProducts.Sync, RemovedProducts.Store, null })
            Assert.Null(ConsoleQuality.PathOf(product));
    }
}
