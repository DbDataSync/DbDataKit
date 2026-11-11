using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The move of a replication's owner (<c>PUT /api/replications/{name}/owner</c>) waits at Sync for work in flight, up to 30 minutes: the relay waits longer for exactly that route (it would otherwise answer a 504 or 502
/// after two minutes while the move goes on), and when even that wait ends it says the move is in progress, not that the server failed.
/// </summary>
public sealed class ConsoleRelayOwnerMoveTests : IAsyncLifetime
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1500);
    // an ordinary call waits 400 ms here, an owner move 6 s: a stand-in for 2 minutes and 35 minutes
    private static readonly RelayTimeouts Short = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(6));

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;
    private string admin = null!;
    private RelayTimeouts timeouts = Short;
    private TimeSpan ownerDelay = Delay;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync(map: (app, _, _) =>
        {
            app.MapPut("/api/replications/{name}/owner", async (string name, HttpContext context) =>
            {
                await Task.Delay(ownerDelay, context.RequestAborted);
                return Results.Json(new { replication = name, from = "old", to = "runner" });
            });
            app.MapPut("/api/replications/{name}/pause", async (string name, HttpContext context) =>
            {
                await Task.Delay(Delay, context.RequestAborted);
                return Results.Json(new { replication = name });
            });
        });
        await StartConsoleAsync();
    }

    private async Task StartConsoleAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services => services.AddSingleton(timeouts));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var user = app.Services.GetRequiredService<IUserAccounts>().CreateUser("Admin person", null, UserRole.Admin);
        admin = app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id;
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(DbDataKit.Web.Trust.ServerRole.Admin | DbDataKit.Web.Trust.ServerRole.Operator | DbDataKit.Web.Trust.ServerRole.Author | DbDataKit.Web.Trust.ServerRole.Approver), "Fake", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Put(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/relay/{PairableServer.InstanceId}/{path}") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}");
        return http.SendAsync(request);
    }

    [Theory]
    [InlineData("PUT", "api/replications/sales/owner", true)]
    [InlineData("PUT", "api/replications/sales/owner/", true)]
    [InlineData("put", "api/replications/sales/owner", true)]
    [InlineData("GET", "api/replications/sales/owner", false)]
    [InlineData("PUT", "api/replications/sales/pause", false)]
    [InlineData("PUT", "api/replications/owner", false)]
    [InlineData("PUT", "api/replications/a/b/owner", false)]
    [InlineData("PUT", "api/v1/tasks/p/prod/t/owner", false)]
    public void Only_the_owner_move_of_a_replication_is_a_long_call(string method, string path, bool expected) =>
        Assert.Equal(expected, ConsoleRelay.IsOwnerMove(method, path));

    [Fact]
    public void The_default_relay_waits_longer_for_an_owner_move_than_Sync_does()
    {
        Assert.True(RelayTimeouts.Default.OwnerMove > TimeSpan.FromMinutes(30));   // ReplicationOwnership.DefaultWait
        Assert.True(RelayTimeouts.Default.OwnerMove > RelayTimeouts.Default.Call);
    }

    [Fact]
    public async Task An_owner_move_that_takes_longer_than_an_ordinary_call_may_still_gets_its_answer_and_another_slow_call_does_not()
    {
        var move = await Put("api/replications/sales/owner");
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.Equal("runner", (string)JsonNode.Parse(await move.Content.ReadAsStringAsync())!["to"]!);

        var other = await Put("api/replications/sales/pause");   // the same delay on a route that is not the owner's
        Assert.Equal(HttpStatusCode.BadGateway, other.StatusCode);
        Assert.Contains("did not answer", await other.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_owner_move_that_outlasts_even_the_long_wait_is_in_progress_and_not_an_error_of_the_server()
    {
        ownerDelay = TimeSpan.FromSeconds(9);
        var response = await Put("api/replications/sales/owner");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True((bool)body["inProgress"]!);
        Assert.Contains("still moving", (string)body["error"]!);
    }
}
