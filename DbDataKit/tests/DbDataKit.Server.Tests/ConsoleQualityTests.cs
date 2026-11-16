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
/// The suite's data quality in the console (plan 23a FQ17, plan 35 I3). A Runner keeps the results of the checks its tasks evaluated (its group's <c>quality</c> module, <c>/api/v1/quality</c>), so the console asks each paired Runner, as the
/// signed-in person, and joins the answers by dataset identity; a Gateway keeps none and is not asked. The history of a check is the Runner's own route through the relay. A real console paired with a runner in this process
/// (<see cref="PairedRunner"/>: the real routes behind the token check).
/// </summary>
public sealed class ConsoleQualityTests : IAsyncLifetime
{
    private const string Dataset = "postgres://pg1/warehouse.sales.orders";
    private const string RunId = "0197f0e0-0000-7000-8000-0000000000b3";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-quality-" + Guid.NewGuid().ToString("N")[..8]);
    private int gatewayAsked;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedRunner runner = null!;
    private PairableServer gateway = null!;

    public async ValueTask InitializeAsync()
    {
        runner = await PairedRunner.StartAsync("runner-1");
        gateway = await PairableServer.StartAsync(product: "dbdatagateway", instanceId: "gateway-1", map: (server, _, _) =>
            server.MapGet("/api/v1/quality", () => { Interlocked.Increment(ref gatewayAsked); return Results.Json(new { datasets = Array.Empty<object>() }); }));
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        await runner.PairAsync(app, "Runner one");
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Viewer), "Gateway one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await gateway.DisposeAsync();
        await runner.DisposeAsync();
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
    public async Task The_results_a_runner_keeps_are_on_the_quality_page_with_the_runner_named_and_its_history_through_the_relay()
    {
        var store = new QualityStore(runner.State);
        var at = DateTimeOffset.UtcNow;
        store.Record(new CheckResultRecord("orders_customer_present", Dataset, CheckOutcome.Fail, at, 2, 0, ["order_id=7"], RunId, "block"));
        store.Record(new CheckResultRecord("orders_rows", Dataset, CheckOutcome.Pass, at, 0, 50, RunId: RunId, Observed: 1234));

        var (status, body) = await Get("/api/quality");

        Assert.Equal(HttpStatusCode.OK, status);
        var dataset = Assert.Single(body!["datasets"]!.AsArray());
        Assert.Equal(Dataset, (string?)dataset!["dataset"]);
        Assert.Equal(1, (int)dataset["blocking"]!);
        var failing = dataset["checks"]!.AsArray().Single(c => (string?)c!["check_id"] == "orders_customer_present")!;
        Assert.Equal(("fail", true, "Runner one", "runner-1", "dbdatarunner"), ((string?)failing["status"], (bool)failing["blocking"]!, (string?)failing["server"], (string?)failing["server_id"], (string?)failing["product"]));
        var server = Assert.Single(body["servers"]!.AsArray());
        Assert.Equal(("Runner one", 2, false, true), ((string?)server!["server"], (int)server["checks"]!, (bool)server["can_run"]!, (bool)server["can_release"]!));
        Assert.Null((string?)server["error"]);
        Assert.Equal(0, gatewayAsked);      // a Gateway keeps no results

        var (read, history) = await Get($"/relay/runner-1/api/v1/quality/history?dataset={Uri.EscapeDataString(Dataset)}&check=orders_customer_present");
        Assert.Equal(HttpStatusCode.OK, read);
        var result = Assert.Single(history!["results"]!.AsArray());
        Assert.Equal((RunId, "order_id=7"), ((string?)result!["run_id"], (string?)result["sample_keys"]![0]));
    }

    [Fact]
    public async Task A_runner_with_no_results_answers_an_empty_overview()
    {
        var (status, body) = await Get("/api/quality");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!["datasets"]!.AsArray());
        Assert.Equal(0, (int)Assert.Single(body["servers"]!.AsArray())!["checks"]!);
    }

    [Fact]
    public async Task Nobody_signed_in_reads_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/quality", signedIn: false)).Status);
    }

    [Fact]
    public void Only_the_runner_has_a_path_for_its_checks()
    {
        Assert.Equal("/api/v1/quality", ConsoleQuality.PathOf(ServerProducts.Runner));
        foreach (var product in new[] { ServerProducts.Gateway, ServerProducts.Workspace, RemovedProducts.Build, RemovedProducts.Sync, RemovedProducts.Store, null })
            Assert.Null(ConsoleQuality.PathOf(product));
    }
}
