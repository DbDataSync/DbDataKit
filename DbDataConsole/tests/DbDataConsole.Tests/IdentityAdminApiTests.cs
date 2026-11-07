using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataConsole.Tests;

/// <summary>
/// Plan 13a U21: the identity home's admin API on the management API of <c>dbdatakit identity run</c>. A console pairs with the home like any server; each kind (people, groups, role assignments, app clients, external group
/// mappings, personal access tokens, service accounts) is listed, created and removed by an administrator, and refused to anyone else. The home serves no web app of its own any more.
/// </summary>
public sealed class IdentityAdminApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-idadmin-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string instanceId = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var port = TestPorts.Next();
        app = IdentityHost.Build(new IdentityHostSettings(directory, new Uri($"https://localhost:{port}")), ConsoleHost.Certificate(directory), _ => { });
        var url = new Uri($"https://127.0.0.1:{port}");   // the home listens on the IPv4 loopback for localhost
        await app.StartAsync();

        // the operator pairs a console with the home, as with any server
        var code = new PairingFiles(Path.Combine(directory, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        instanceId = paired.InstanceId;
        Assert.StartsWith("identity-", instanceId);
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    /// <summary>A token works once (the server keeps the ids it has seen), as a console makes one per call.</summary>
    private string Token(ServerRole roles) => console.Issuer().Issue(new ConsoleTokenRequest(instanceId, "grace", "Grace Hopper", ServerRoles.Close(roles)));

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { /* released later */ }
    }

    /// <param name="roles">What the caller holds; null sends no token at all.</param>
    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, object? body = null, ServerRole? roles = ServerRole.Admin)
    {
        using var request = new HttpRequestMessage(method, IdentityAdminApi.Prefix + "/" + path) { Content = body is null ? null : JsonContent.Create(body) };
        if (roles is { } held) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, Token(held));
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private async Task<JsonArray> List(string path, string key)
    {
        var (status, body) = await Call(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, status);
        return body![key]!.AsArray();
    }

    [Fact]
    public async Task Each_kind_is_listed_created_and_removed_by_an_administrator()
    {
        // people: invited (the link answered once), listed, disabled (kept, so the audit still names them), enabled again
        var invite = await Call(HttpMethod.Post, "people", new { email = "ada@example.invalid", role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, invite.Status);
        Assert.Contains("/invite#", (string)invite.Body!["url"]!);
        var state = IdentityHostState.Open(directory);
        var ada = new IdentityUserStore(state).CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer, emailVerified: true);
        Assert.Contains(await List("people", "people"), p => (string)p!["id"]! == ada.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"people/{ada.Id}")).Status);
        Assert.False(new IdentityUserStore(state).Get(ada.Id)!.Enabled);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"people/{ada.Id}/enable")).Status);

        // groups, with a member
        var group = (string)(await Call(HttpMethod.Post, "groups", new { name = "Operators" })).Body!["id"]!;
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"groups/{group}/members", new { user = ada.Id })).Status);
        Assert.Contains(ada.Id, (await List("groups", "groups")).Single(g => (string)g!["id"]! == group)!["members"]!.AsArray().Select(m => (string)m!));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"groups/{group}/members/{ada.Id}")).Status);

        // role assignments, to a person and to a group
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "assignments", new { user = ada.Id, server = "build-1", roles = new[] { "operator" } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "assignments", new { group, app = "console", roles = "admin" })).Status);
        var assignments = await List("assignments", "assignments");
        Assert.Equal(2, assignments.Count);
        Assert.True(new HomeDirectory(state).RolesOn(ada.Id, "build-1").Roles.HasFlag(ServerRole.Operator));
        var adas = assignments.Single(a => (string)a!["subject"]! == ada.Id)!;
        Assert.Equal("Ada Lovelace", (string)adas["subject_name"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"assignments/{(string)adas["id"]!}")).Status);
        Assert.False(new HomeDirectory(state).RolesOn(ada.Id, "build-1").Roles.HasFlag(ServerRole.Operator));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"groups/{group}")).Status);
        Assert.Empty(await List("assignments", "assignments"));   // the group's assignment went with it

        // app clients: a confidential client's secret is answered once
        var client = await Call(HttpMethod.Post, "clients", new { name = "Reports app", redirect_uris = new[] { "https://reports.example.invalid/callback" } });
        Assert.NotNull((string?)client.Body!["secret"]);
        var clientId = (string)client.Body["id"]!;
        Assert.DoesNotContain("secret", (await Call(HttpMethod.Get, "clients")).Body!.ToJsonString());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"clients/{clientId}")).Status);
        Assert.Null(new OidcClients(state).Find(clientId));

        // external group mappings
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "group-mappings", new { source = "entra", group = "7f3c1b0e-0001", server = "build-1", project = "retail", roles = new[] { "author" } })).Status);
        var mapping = (await List("group-mappings", "group_mappings")).Single()!;
        Assert.Equal("retail", (string)mapping["project"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"group-mappings/{(string)mapping["id"]!}")).Status);
        Assert.Empty(await List("group-mappings", "group_mappings"));

        // service accounts and their tokens; a person's token is listed and revoked too
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "service-accounts", new { name = "nightly", description = "the 2am refresh" })).Status);
        var made = await Call(HttpMethod.Post, "service-accounts/nightly/tokens", new { name = "report server", no_expiry = true });
        Assert.Equal(HttpStatusCode.OK, made.Status);
        var secret = (string)made.Body!["token"]!;
        var tokens = new AccessTokens(state, new IdentityUserStore(state));
        Assert.NotNull(tokens.Verify("nightly", secret));
        var adasToken = tokens.Create(ada.Id, "Excel", ada.Id);
        var listed = await List("tokens", "tokens");
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(secret.Split('_', 3)[2], listed.ToJsonString());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"tokens/{adasToken.Record.Id}")).Status);
        Assert.Null(tokens.Verify(ada.Id, adasToken.Secret));
        Assert.Single(await List("service-accounts", "service_accounts"));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, "service-accounts/nightly")).Status);
        Assert.Null(tokens.Verify("nightly", secret));
        Assert.Empty(await List("service-accounts", "service_accounts"));
    }

    [Fact]
    public async Task Every_call_is_refused_to_anyone_who_is_not_an_administrator_and_to_a_caller_with_no_token()
    {
        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "people", null), (HttpMethod.Post, "people", new { email = "x@example.invalid" }), (HttpMethod.Delete, "people/x", null),
            (HttpMethod.Get, "groups", null), (HttpMethod.Post, "groups", new { name = "g" }), (HttpMethod.Delete, "groups/x", null),
            (HttpMethod.Get, "assignments", null), (HttpMethod.Post, "assignments", new { user = "x", server = "s", roles = "viewer" }), (HttpMethod.Delete, "assignments/x", null),
            (HttpMethod.Get, "clients", null), (HttpMethod.Post, "clients", new { name = "c", redirect_uris = new[] { "https://c.invalid/cb" } }), (HttpMethod.Delete, "clients/x", null),
            (HttpMethod.Get, "group-mappings", null), (HttpMethod.Post, "group-mappings", new { source = "entra", group = "g", server = "s", roles = "viewer" }), (HttpMethod.Delete, "group-mappings/x", null),
            (HttpMethod.Get, "tokens", null), (HttpMethod.Delete, "tokens/x", null),
            (HttpMethod.Get, "service-accounts", null), (HttpMethod.Post, "service-accounts", new { name = "s" }), (HttpMethod.Delete, "service-accounts/s", null),
            (HttpMethod.Post, "service-accounts/s/tokens", new { name = "t" }),
        };
        foreach (var (method, path, body) in calls)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(method, path, body, ServerRole.Viewer | ServerRole.Operator)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(method, path, body, roles: null)).Status);
        }
        // nothing was made by any of them
        var state = IdentityHostState.Open(directory);
        Assert.Empty(new HomeDirectory(state).Groups());
        Assert.Empty(new OidcClients(state).List());
        Assert.Empty(new AccessTokens(state, new IdentityUserStore(state)).ServiceAccounts());
    }

    [Fact]
    public async Task The_home_serves_no_web_app_of_its_own_and_the_old_web_root_flag_is_refused_with_a_message()
    {
        // what a browser gets for a console screen's address: a line saying what this is, not the console's Users or invitation screen
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        using var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = http.BaseAddress };
        var users = await anonymous.GetAsync("/users");
        Assert.Equal("text/plain", users.Content.Headers.ContentType?.MediaType);
        Assert.Contains("pair a console", await users.Content.ReadAsStringAsync());
        // its own invitation page is the one a person redeems
        Assert.Contains("identity-invite", await anonymous.GetStringAsync(IdentityHome.InvitePath));

        var e = new StringWriter();
        var exit = await ConsoleCli.RunAsync(["identity", "run", "--repo", directory, "--web-root", webRoot], new DbDataKit.Cli.CliContext(new StringWriter(), e, TextReader.Null));
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, exit);
        Assert.Contains("--web-root is gone", e.ToString());
    }
}
