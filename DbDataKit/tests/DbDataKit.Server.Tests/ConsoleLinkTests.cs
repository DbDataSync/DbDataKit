using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>The console brokers a pairing between two servers it manages (plan 13, T11; plan 24a UC13): the gateway makes a code, the runner pairs with it to tell the gateway's cache when it wrote data, as the signed-in person, and nobody types a code.</summary>
public sealed class ConsoleLinkTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> codeAskers = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer gateway = null!;
    private PairableServer workspace = null!;
    private PairableServer runner = null!;
    private readonly List<(string Route, JsonNode Body)> tellers = [];

    public async ValueTask InitializeAsync()
    {
        gateway = await PairableServer.StartAsync(product: "dbdatagateway", instanceId: "gateway-1", map: (server, codes, _) =>
            server.MapPost(PairingCodeIssuer.Path, (HttpContext context) =>
            {
                var token = context.ManagementToken();
                codeAskers.Add(token.SubjectName ?? token.SubjectId);
                var (status, body) = PairingCodeIssuer.Issue(codes, ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed, null);
                return Results.Json(body, statusCode: status);
            }));
        runner = await PairableServer.StartAsync(product: "dbdatarunner", instanceId: "runner-1", map: (server, _, _) =>
            server.MapPost("/api/v1/gateways", async (HttpContext context) =>
            {
                if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "needs admin" }, statusCode: 403);
                var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
                tellers.Add(("/api/v1/gateways", body));
                return Results.Json(new { name = (string?)body["name"] });
            }));
        workspace = await PairableServer.StartAsync(product: "dbdataworkspace", instanceId: "workspace-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        var every = ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver;
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(every), "Edge gateway", default);
        await instances.PairAsync(runner.Url, runner.NewCodeWith(every), "Runner", default);
        await instances.PairAsync(workspace.Url, workspace.NewCodeWith(every), "Authoring", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { gateway, runner, workspace }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Link(UserRole role, object body)
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana", null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances/link") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return http.SendAsync(request);
    }

    [Fact]
    public async Task A_runner_is_linked_to_a_gateway_through_its_own_route_with_a_code_the_gateway_made_for_it_and_no_code_is_typed()
    {
        Assert.Equal(HttpStatusCode.OK, (await Link(UserRole.Admin, new { from = "runner-1", to = "gateway-1" })).StatusCode);

        var (route, paired) = Assert.Single(tellers);
        Assert.Equal("/api/v1/gateways", route);
        Assert.Equal("edge-gateway", (string)paired["name"]!);                                   // the gateway's name, made into an id
        Assert.Equal(gateway.Url.AbsoluteUri.TrimEnd('/'), (string)paired["url"]!);
        Assert.Equal(["Ana"], codeAskers);                                                     // the gateway made the code for the signed-in person
        // the code the runner received is the one the gateway made: it works once, and carries the publisher ceiling and nothing else
        Assert.Equal(ServerRole.Publisher, gateway.Codes.Redeem((string)paired["code"]!)!.Ceiling);
    }

    [Fact]
    public async Task A_name_can_be_given_and_only_a_runner_and_a_gateway_can_be_linked()
    {
        Assert.Equal(HttpStatusCode.OK, (await Link(UserRole.Admin, new { from = "runner-1", to = "gateway-1", name = "edge" })).StatusCode);
        Assert.Equal("edge", (string)tellers.Single().Body["name"]!);

        foreach (var wrong in new[] { new { from = "workspace-1", to = "gateway-1", name = (string?)null }, new { from = "runner-1", to = "workspace-1", name = (string?)null }, new { from = "gateway-1", to = "runner-1", name = (string?)null }, new { from = "runner-1", to = "runner-1", name = (string?)null } })
            Assert.Equal(HttpStatusCode.BadRequest, (await Link(UserRole.Admin, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Link(UserRole.Admin, new { from = "runner-1", to = "nobody" })).StatusCode);
        Assert.Single(tellers);
    }

    [Fact]
    public async Task A_viewer_cannot_link_servers()
    {
        var response = await Link(UserRole.Viewer, new { from = "runner-1", to = "gateway-1" });
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
        Assert.Empty(tellers);
        Assert.Empty(codeAskers);
    }
}
