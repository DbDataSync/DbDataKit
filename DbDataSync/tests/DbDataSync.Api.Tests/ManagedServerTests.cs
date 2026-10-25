using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Management;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// DbDataSync as a server a console manages (plan 13, U08): a console's call carries a signed token that names the person, the token's roles become the product's two, and pairing is open to a client that holds the code.
/// The calls are the ones the web app makes with a session; only how the caller is known differs.
/// </summary>
public sealed class ManagedServerTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());

    private SyncManagement Management => factory.Services.GetRequiredService<SyncManagement>();

    private void Pair(ServerRole ceiling) =>
        Management.Files.Clients().Upsert(new PairedClient(console.Id, "test console", console.Key.PublicKey, ServerRoles.Close(ceiling), ClientKind.Console));

    /// <summary>One call as a person: a token is for one request (its id is remembered), so each call carries a new one, as the console's relay sends.</summary>
    private sealed class Caller(AuthenticatedApiFactory factory, Func<string> token)
    {
        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
        public Task<HttpResponseMessage> PostAsync(string path, HttpContent? content) => SendAsync(HttpMethod.Post, path, content);

        public async Task<string> GetStringAsync(string path) => await (await GetAsync(path)).Content.ReadAsStringAsync();

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add(ManagementAuthentication.TokenHeader, token());
            return await factory.CreateClient().SendAsync(request);
        }
    }

    private Caller As(ServerRole roles, string person = "ada", string? audience = null) =>
        new(factory, () => console.Issuer().Issue(new ConsoleTokenRequest(audience ?? Management.InstanceId, person, "Ada", roles)));

    [Fact]
    public async Task A_console_call_is_the_person_it_names_with_the_roles_it_holds()
    {
        Pair(ServerRole.Admin | ServerRole.Viewer);

        // an administrator reads the replications and the users, and the server says who they are
        var admin = As(ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/replications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        var status = JsonNode.Parse(await admin.GetStringAsync("/api/auth/status"))!;
        Assert.True((bool)status["authenticated"]!);
        Assert.Equal("Ada", (string)status["displayName"]!);
        Assert.Equal("Admin", (string)status["role"]!);

        // a viewer reads, and is refused what makes something happen or shows who has access
        var viewer = As(ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/replications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/connections/anything/test", null)).StatusCode);
    }

    [Fact]
    public async Task The_console_ceiling_limits_what_a_token_can_hold()
    {
        Pair(ServerRole.Viewer);   // this console may grant viewer here and no more

        // A token that claims admin beyond the ceiling is not a token at all.
        var response = await As(ServerRole.Admin).GetAsync("/api/replications");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(ServerRole.Viewer).GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_server_an_unpaired_console_and_a_forged_token_are_refused()
    {
        Pair(ServerRole.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(ServerRole.Viewer, audience: "someone-else").GetAsync("/api/replications")).StatusCode);

        var stranger = new ConsoleIdentity("unpaired", ConsoleSigningKey.Generate());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Caller(factory, () => stranger.Issuer().Issue(new ConsoleTokenRequest(Management.InstanceId, "ada", "Ada", ServerRole.Admin))).GetAsync("/api/replications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Caller(factory, () => "a.b.c").GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task Unpairing_takes_effect_on_the_next_call()
    {
        Pair(ServerRole.Admin);
        var client = As(ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);

        Management.Files.Clients().Remove(console.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task A_token_is_good_for_one_call()
    {
        Pair(ServerRole.Admin);
        var token = console.Issuer().Issue(new ConsoleTokenRequest(Management.InstanceId, "ada", "Ada", ServerRole.Viewer));
        var once = new Caller(factory, () => token);
        Assert.Equal(HttpStatusCode.OK, (await once.GetAsync("/api/replications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await once.GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task Every_answer_says_which_management_api_version_the_server_speaks()
    {
        var response = await factory.CreateClient().GetAsync("/api/auth/status");
        Assert.Equal(ManagementApi.Version.ToString(), response.Headers.GetValues(ManagementApi.VersionHeader).Single());
    }

    [Fact]
    public async Task Pairing_is_open_to_a_client_that_holds_the_code_and_the_server_names_itself()
    {
        var hello = JsonNode.Parse(await factory.CreateClient().GetStringAsync(ManagementApi.PairingPath))!;
        Assert.Equal(Management.InstanceId, (string)hello["instanceId"]!);
        Assert.StartsWith("sync-", Management.InstanceId);

        // Without a certificate the server can read, it says so instead of pairing against nothing (the test host has no TLS).
        var code = Management.Files.Codes().Create(ServerRoles.Close(ServerRole.Viewer)).Code;
        var key = PairingCodes.KeyFor(code)!;
        var proof = PairingProof.ForRequest(key, console.Id, console.Key.PublicKey, ClientKind.Console, "any");
        var refused = await factory.CreateClient().PostAsJsonAsync(ManagementApi.PairingPath, new { proof, clientId = console.Id, name = "x", kind = "console", publicKey = Convert.ToBase64String(console.Key.PublicKey).TrimEnd((char)61).Replace((char)43, (char)45).Replace((char)47, (char)95) });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("https", (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!);
    }
}
