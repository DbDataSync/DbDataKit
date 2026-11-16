using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.Connections;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Copying a connection to another server (plan 13a, E05), against two servers shaped as a Gateway is for data sources (<c>/api/v1/data-sources/{name}</c>, snake_case). Each fake reads what it is sent with the shared strict loader and refuses
/// what the real one would, so what is tested is what the console sends and what it makes of the answers. (The copy between two real Gateways is <c>ConnectionsAcrossServersTests</c>.)
/// </summary>
public sealed class ConsoleConnectionsTests : IAsyncLifetime
{
    private const string SourceFile = """
        schema: connection/1
        name: erp
        engine: sqlserver
        managed_by: build-9
        address: { host: sql01, port: 1433, database: Erp }
        auth: { mode: sql, user: svc_erp, password_secret: 'secret:dbdatakit:gateway:connection:erp' }
        extensions:
          sync:
            test_query: SELECT 1
          gateway:
            allow_writes: false
        """;

    private const string GatewayFile = """
        # the ERP reader
        schema: connection/1
        name: erp
        engine: sqlserver
        address: { host: sql01, database: Erp }
        auth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }
        extensions:
          gateway:
            allow_writes: false
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentDictionary<string, string> sourceFiles = new();
    private readonly ConcurrentDictionary<string, string> gatewayFiles = new();
    private readonly ConcurrentBag<string> callers = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer source = null!;
    private PairableServer gateway = null!;
    private PairableServer runner = null!;

    /// <summary>A Gateway as it is for data sources: <c>/api/v1/data-sources/{name}</c>, the file read back with the shared strict loader, a name that is taken a conflict, a person without the admin role refused.</summary>
    private Task<PairableServer> GatewayAsync(string id, ConcurrentDictionary<string, string> files) => PairableServer.StartAsync(product: "dbdatagateway", instanceId: id, map: (server, _, _) =>
    {
        server.MapGet("/api/v1/data-sources/{name}", (string name) =>
            files.TryGetValue(name, out var text) ? Results.Json(new { name, text, version = Version(text) }) : Results.Json(new { error = $"There is no data source '{name}'." }, statusCode: 404));
        server.MapPut("/api/v1/data-sources/{name}", async (HttpContext context, string name) =>
        {
            callers.Add(context.ManagementToken().SubjectName ?? "?");
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "needs admin" }, statusCode: 403);
            var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
            var text = (string)body["text"]!;
            var loaded = ConnectionDefinitionLoader.LoadDatabaseConnection(text, $"{name}.yaml");
            var refusals = loaded.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => (code: d.Code, line: d.Line, message: d.Message)).ToList();
            // what a Gateway adds to the shared loader in this test: a data source with no address is not one it serves
            if (loaded.Definition is { Address: null, ConnectionString: null or "" }) refusals.Add(("DBD-GW", 1, "A data source needs an address."));
            if (refusals.Count > 0)
                return Results.Json(new { error = "The data source is not valid, so nothing was written.", valid = false, diagnostics = refusals.Select(r => new { r.code, severity = "error", key = (string?)null, file = $"{name}.yaml", r.line, column = 1, r.message, fix = (string?)null }) }, statusCode: 400);
            if (files.ContainsKey(name)) return Results.Json(new { error = "The data source changed since it was read.", current_version = Version(files[name]) }, statusCode: 409);
            files[name] = text;
            return Results.Json(new { name, version = Version(text), created = true, diagnostics = Array.Empty<object>() });
        });
    });

    public async ValueTask InitializeAsync()
    {
        sourceFiles["erp"] = SourceFile;
        sourceFiles["no-address"] = "schema: connection/1\nname: no-address\nengine: sqlserver\nconnection_string_secret: 'env:WHOLE'\n";
        gatewayFiles["erp"] = GatewayFile;
        source = await GatewayAsync("gateway-src", sourceFiles);
        gateway = await GatewayAsync("gateway-1", gatewayFiles);
        runner = await PairableServer.StartAsync(product: "dbdatarunner", instanceId: "runner-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        var every = ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver;
        await instances.PairAsync(source.Url, source.NewCodeWith(every), "Gateway source", default);
        await instances.PairAsync(gateway.Url, gateway.NewCodeWith(every), "Gateway one", default);
        await instances.PairAsync(runner.Url, runner.NewCodeWith(every), "Runner one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { source, gateway, runner }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static string Version(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task<(HttpStatusCode Status, JsonNode Body)> Copy(object body, UserRole role = UserRole.Admin, string person = "Ana")
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(person, null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/connections/copy") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_data_source_is_copied_to_another_Gateway_as_the_file_it_is_with_what_is_not_the_targets_left_out()
    {
        var (status, body) = await Copy(new { from = "gateway-src", name = "erp", to = "gateway-1", newName = "erp-copy" });

        Assert.Equal(HttpStatusCode.OK, status);
        var written = gatewayFiles["erp-copy"];
        var copy = ConnectionDefinitionLoader.LoadDatabaseConnection(written, "erp-copy.yaml").Require();
        Assert.Equal("erp-copy", copy.Name);
        Assert.Equal("sql01", copy.Address!.Host);
        Assert.Equal("svc_erp", copy.Auth!.User);
        Assert.Null(copy.ManagedBy);                                         // build-9 is a client of the source's server
        Assert.Equal(["gateway"], copy.Extensions.Items.Keys);               // extensions.sync is not read by a Gateway
        Assert.Equal("secret:dbdatakit:gateway:connection:erp", copy.Auth.PasswordSecret);   // a reference, kept as written
        Assert.Equal("Gateway source", (string)body["from"]!);
        Assert.Equal("Gateway one", (string)body["to"]!);
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("managed_by (build-9)"));
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("extensions.sync"));
        Assert.Equal(["secret:dbdatakit:gateway:connection:erp"], body["secrets"]!.AsArray().Select(s => (string)s!));
        Assert.Equal(SourceFile, sourceFiles["erp"]);                        // the source is untouched
        Assert.Equal(["Ana"], callers.ToArray());                            // written as the person who copied
    }

    [Fact]
    public async Task A_copy_on_the_same_Gateway_keeps_its_own_settings_comments_and_an_environment_reference()
    {
        var (status, _) = await Copy(new { from = "gateway-1", name = "erp", to = "gateway-1", newName = "erp-2" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("# the ERP reader", gatewayFiles["erp-2"]);       // the file is edited, not rewritten
        var copy = ConnectionDefinitionLoader.LoadDatabaseConnection(gatewayFiles["erp-2"], "erp-2.yaml").Require();
        Assert.Equal("env:ERP_DB_PASSWORD", copy.Auth!.PasswordSecret);
        Assert.True(copy.Extensions.Items.ContainsKey("gateway"));
    }

    [Fact]
    public async Task What_the_target_refuses_comes_back_with_its_diagnostics_and_nothing_is_written()
    {
        var (status, body) = await Copy(new { from = "gateway-src", name = "no-address", to = "gateway-1" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("did not accept the copy", (string)body["error"]!);
        Assert.Contains(body["diagnostics"]!.AsArray(), d => ((string)d!["message"]!).Contains("needs an address"));
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("env:WHOLE"));
        Assert.False(gatewayFiles.ContainsKey("no-address"));
    }

    [Fact]
    public async Task A_name_the_target_already_has_is_a_conflict_and_the_file_there_is_not_overwritten()
    {
        gatewayFiles["taken"] = GatewayFile.Replace("name: erp", "name: taken");
        var before = gatewayFiles["taken"];

        var (status, body) = await Copy(new { from = "gateway-src", name = "erp", to = "gateway-1", newName = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(before, gatewayFiles["taken"]);
        Assert.NotNull(body["current_version"]);
    }

    [Fact]
    public async Task A_person_the_target_does_not_let_write_is_refused_by_the_target_not_by_the_console()
    {
        var (status, body) = await Copy(new { from = "gateway-src", name = "erp", to = "gateway-1", newName = "by-viewer" }, UserRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("needs admin", (string)body["error"]!);
        Assert.False(gatewayFiles.ContainsKey("by-viewer"));
    }

    [Fact]
    public async Task A_connection_the_source_does_not_have_is_the_sources_404()
    {
        var (status, body) = await Copy(new { from = "gateway-src", name = "nope", to = "gateway-1" });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("There is no data source 'nope'", (string)body["error"]!);
    }

    [Fact]
    public async Task The_servers_must_be_registered_keep_connections_and_a_copy_on_one_server_needs_another_name()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(new { from = "nobody", name = "erp", to = "gateway-1" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(new { from = "gateway-src", name = "erp", to = "nobody" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "gateway-src", name = "erp", to = "runner-1" })).Status);   // a runner keeps no connections the console copies
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "runner-1", name = "erp", to = "gateway-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "gateway-1", name = "erp", to = "gateway-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "gateway-src", name = "erp", to = "gateway-1", newName = "1 bad name" })).Status);
        Assert.Equal(SourceFile, sourceFiles["erp"]);
        Assert.Single(gatewayFiles);
        Assert.Empty(callers);
    }

    [Fact]
    public async Task Nobody_signed_out_copies_anything()
    {
        var response = await http.PostAsJsonAsync("/api/connections/copy", new { from = "gateway-src", name = "erp", to = "gateway-1" });
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, response.StatusCode.ToString());
        Assert.Single(gatewayFiles);
    }
}
