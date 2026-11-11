using System.Net;
using Xunit;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Web.Tests;

/// <summary>The token check in front of a server's management API (plan 13, W12): who may reach what is decided by the endpoint, but nobody without a good token reaches it.</summary>
public sealed class ManagementAuthenticationTests : IAsyncLifetime
{
    private readonly ConsoleSigningKey _key = ConsoleSigningKey.Generate();
    private readonly List<TokenResult> _refusals = [];
    private TestServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestServer.StartAsync((app, holder) =>
        {
            app.UseManagementAuthentication(new ConsoleTokenValidator(TestServer.InstanceId, holder.Clients, new MemoryReplayCache()), _refusals.Add);
            app.MapGet("/api/v1/whoami", (HttpContext context) => Results.Ok(new { who = context.ManagementToken().SubjectId, roles = context.ManagementToken().Roles.ToString() }));
            app.MapGet("/open", () => Results.Ok("open"));
            holder.Clients.Upsert(new PairedClient("console-1", "Console", _key.PublicKey, ServerRole.Operator));
        });
        _http = new HttpClient(PairingClient.Pinned(_server.Fingerprint)) { BaseAddress = _server.Url };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private string Token(ServerRole roles = ServerRole.Viewer, string audience = TestServer.InstanceId) =>
        new ConsoleTokenIssuer("console-1", _key).Issue(new ConsoleTokenRequest(audience, "alice", "Alice", roles));

    private async Task<HttpResponseMessage> Get(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_good_token_reaches_the_endpoint_which_sees_who_called()
    {
        var response = await Get("/api/v1/whoami", Token(ServerRole.Viewer));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alice", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_token_a_bad_token_and_a_token_for_another_server_get_401_with_no_body()
    {
        foreach (var token in new[] { null, "garbage", Token(audience: "someone-else") })
        {
            var response = await Get("/api/v1/whoami", token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, _refusals.Count);
        Assert.Contains(_refusals, r => r.Failure == TokenFailure.WrongAudience);
    }

    [Fact]
    public async Task A_token_claiming_more_than_the_console_may_have_is_refused()
    {
        var response = await Get("/api/v1/whoami", Token(ServerRole.Admin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(TokenFailure.AboveCeiling, Assert.Single(_refusals).Failure);
    }

    [Fact]
    public async Task A_token_works_once()
    {
        var token = Token();

        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/whoami", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/whoami", token)).StatusCode);
    }

    [Fact]
    public async Task Paths_outside_the_prefix_are_not_checked()
    {
        var response = await Get("/open", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_refusals);
    }
}
