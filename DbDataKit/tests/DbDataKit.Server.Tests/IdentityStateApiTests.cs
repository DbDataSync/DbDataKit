using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 29b C05 and C07 for the identity home that runs on its own: a paired console reads its state (<c>GET /api/v1/state</c>) and puts it into maintenance, which answers the home's own endpoints with 503 and leaves the management calls
/// working; the state settings come from the <c>state:</c> section of <c>server.yaml</c> and the state says where each came from.
/// </summary>
public sealed class IdentityStateApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-idstate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string instanceId = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var port = TestPorts.Next();
        app = IdentityHost.Build(new IdentityHostSettings(directory, new Uri($"https://localhost:{port}")), ConsoleHost.Certificate(directory), _ => { });
        var url = new Uri($"https://127.0.0.1:{port}");
        await app.StartAsync();
        var code = new PairingFiles(Path.Combine(directory, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        instanceId = paired.InstanceId;
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { /* released later */ }
    }

    private string Token(ServerRole roles) => console.Issuer().Issue(new ConsoleTokenRequest(instanceId, "grace", "Grace Hopper", ServerRoles.Close(roles)));

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, object? body = null, ServerRole? roles = ServerRole.Admin)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (roles is { } held) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, Token(held));
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task A_paired_console_reads_the_homes_state_and_the_modules_it_is_built_from()
    {
        var (status, state) = await Call(HttpMethod.Get, "/api/v1/state", roles: ServerRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("dbdatakit-identity", (string)state!["product"]!);
        Assert.Equal("sqlite", ((string)state["engine"]!).ToLowerInvariant());
        Assert.Contains("identity", state["modules"]!.AsArray().Select(m => (string)m!["module"]!));
        Assert.Equal("default", (string)state["sources"]!["engine"]!["kind"]!);
        Assert.False((bool)state["maintenance"]!["enabled"]!);

        // no token, no answer
        Assert.NotEqual(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/state", roles: null)).Status);
    }

    [Fact]
    public async Task Maintenance_closes_the_homes_own_endpoints_and_not_the_management_calls_and_only_an_administrator_changes_it()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/state/maintenance", new { enabled = true, reason = "moving" }, ServerRole.Operator)).Status);

        var on = await Call(HttpMethod.Post, "/api/v1/state/maintenance", new { enabled = true, reason = "moving the state" });
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.True((bool)on.Body!["enabled"]!);

        using (var open = await http.GetAsync("/.well-known/openid-configuration"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, open.StatusCode);
            Assert.True(open.Headers.RetryAfter is not null);
        }

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/state/maintenance", roles: ServerRole.Viewer)).Status);   // the management calls still answer
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/state/maintenance", new { enabled = false })).Status);
        using var again = await http.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public void The_state_settings_come_from_the_state_section_of_server_yaml_and_say_so_and_a_variable_beats_it()
    {
        File.AppendAllText(Path.Combine(directory, "server.yaml"), "\nstate:\n  engine: postgres\n  connection: Host=db;Database=identity;Username=id;Password=secret:state-db\n");

        var fromFile = IdentityHostSettings.ResolveState(directory, _ => null);
        Assert.Equal("postgres", fromFile.Settings.Engine);
        Assert.Equal(StateSourceKind.File, fromFile.Sources.Engine.Kind);
        Assert.Equal(StateSourceKind.File, fromFile.Sources.Connection.Kind);
        Assert.NotNull(IdentityHostSettings.StateFrom(_ => null, directory: directory));

        var overridden = IdentityHostSettings.ResolveState(directory, name => name == IdentityHostSettings.EngineVariable ? "mssql" : null);
        Assert.Equal("mssql", overridden.Settings.Engine);
        Assert.Equal(StateSourceKind.Environment, overridden.Sources.Engine.Kind);
        Assert.Equal(IdentityHostSettings.EngineVariable, overridden.Sources.Engine.Name);
    }
}
