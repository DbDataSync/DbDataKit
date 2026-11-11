using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>The suite's data quality in the console (plan 23a FQ17): a Sync server and a Build server each send the results of their checks, and a table Sync writes and a Build model reads is one dataset with the checks of both.</summary>
public sealed class ConsoleQualityTests : IAsyncLifetime
{
    private const string Warehouse = "postgres://pg01/dwh.public.customers";
    private const string Model = "postgres://pg01/dwh.marts.dim_customer";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-lineage-" + Guid.NewGuid().ToString("N")[..8]);
    private bool syncFails;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer sync = null!;
    private PairableServer build = null!;
    private PairableServer gateway = null!;

    private static JsonObject Check(string id, string status, bool blocking, string severity = "block") => new()
    {
        ["check_id"] = id, ["severity"] = severity, ["on_error"] = "alert", ["status"] = status, ["status_since"] = "2026-11-08T12:00:00+00:00", ["measured"] = status == "fail" ? 2 : 0, ["threshold"] = 0,
        ["evaluated_at"] = "2026-11-08T12:00:00+00:00", ["blocking"] = blocking, ["blocked_since"] = blocking ? "2026-11-08T12:00:00+00:00" : null,
    };

    private static JsonObject Overview(bool canRun, string? note, params (string Dataset, JsonObject[] Checks)[] datasets) => new()
    {
        ["datasets"] = new JsonArray(datasets.Select(d => (JsonNode)new JsonObject { ["dataset"] = d.Dataset, ["blocking"] = d.Checks.Count(c => (bool)c["blocking"]!), ["checks"] = new JsonArray(d.Checks.Select(c => (JsonNode)c).ToArray()) }).ToArray()),
        ["can_run"] = canRun, ["run_note"] = note,
    };

    public async ValueTask InitializeAsync()
    {
        sync = await PairableServer.StartAsync(product: "dbdatasync", instanceId: "sync-1", map: (server, _, _) =>
            server.MapGet("/api/quality", () => syncFails
                ? Results.Json(new { error = "the state database cannot be read" }, statusCode: 500)
                : Results.Json(Overview(true, null, (Warehouse, [Check("customers_fresh", "fail", true)])))));
        build = await PairableServer.StartAsync(product: "dbdatabuild", instanceId: "build-1", map: (server, _, _) =>
            server.MapGet("/api/v1/quality", () => Results.Json(Overview(false, "Build evaluates after a refresh.", (Warehouse, [Check("customers_id_unique", "pass", false)]), (Model, [Check("dim_customer_key_not_null", "pass", false, "warn")])))));
        gateway = await PairableServer.StartAsync(product: "dbdatagateway", instanceId: "gateway-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(sync.Url, sync.NewCodeWith(ServerRole.Viewer), "Sync one", default);
        await instances.PairAsync(build.Url, build.NewCodeWith(ServerRole.Viewer), "Build one", default);
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(ServerRole.Viewer), "Gateway one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { sync, build, gateway }) await s.DisposeAsync();
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
    public async Task A_table_two_servers_check_is_one_dataset_with_the_checks_of_both_and_each_says_which_server_it_is_from()
    {
        var (status, body) = await Get("/api/quality");

        Assert.Equal(HttpStatusCode.OK, status);
        var datasets = body!["datasets"]!.AsArray();
        Assert.Equal([Model, Warehouse], datasets.Select(d => (string)d!["dataset"]!));                // by identity
        var warehouse = datasets[1]!;
        Assert.Equal(1, (int)warehouse["blocking"]!);                                                  // Sync's freshness check blocks; Build's unique check passes
        Assert.Equal([("customers_fresh", "Sync one"), ("customers_id_unique", "Build one")], warehouse["checks"]!.AsArray().Select(c => ((string)c!["check_id"]!, (string)c["server"]!)));
        var servers = body["servers"]!.AsArray();
        Assert.Equal(["Build one", "Sync one"], servers.Select(s => (string)s!["server"]!));            // the Gateway evaluates no checks and is not part of this
        Assert.Equal((false, true), ((bool)servers[0]!["can_run"]!, (bool)servers[1]!["can_run"]!));
        Assert.Equal("Build evaluates after a refresh.", (string)servers[0]!["run_note"]!);
        Assert.Equal(["build-1", "sync-1"], servers.Select(s => (string)s!["server_id"]!));              // what the relay is addressed by (history and run go to the server itself)
        Assert.Equal("sync-1", (string)warehouse["checks"]![0]!["server_id"]!);
    }

    [Fact]
    public async Task A_server_that_fails_is_named_with_its_reason_and_left_out_and_the_others_still_show()
    {
        syncFails = true;

        var (_, body) = await Get("/api/quality");

        var failed = Assert.Single(body!["servers"]!.AsArray(), s => (string)s!["server"]! == "Sync one")!;
        Assert.Equal("the state database cannot be read", (string)failed["error"]!);
        Assert.Equal(2, body["datasets"]!.AsArray().Count);
        Assert.DoesNotContain(body["datasets"]!.AsArray().SelectMany(d => d!["checks"]!.AsArray()), c => (string)c!["server"]! == "Sync one");
    }

    [Fact]
    public async Task Nobody_signed_in_reads_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/quality", signedIn: false)).Status);
    }
}
