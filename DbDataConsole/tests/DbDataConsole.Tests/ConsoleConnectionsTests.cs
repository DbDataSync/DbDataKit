using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Connections;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>
/// Copying a connection to another server (plan 13a, E05), against two servers shaped as a Sync and a Proxy are for connections: Sync keeps `connection/1` files under <c>/api/connections/{name}/file</c> (camelCase, a password beside the file) and a Proxy
/// under <c>/api/v1/data-sources/{name}</c> (snake_case). Each fake reads what it is sent with the shared strict loader and refuses what the real one would, so what is tested is what the console sends and what it makes of the answers.
/// </summary>
public sealed class ConsoleConnectionsTests : IAsyncLifetime
{
    private const string SyncFile = """
        schema: connection/1
        name: erp
        engine: sqlserver
        managed_by: build-9
        address: { host: sql01, port: 1433, database: Erp }
        auth: { mode: sql, user: svc_erp, password_secret: 'secret:dbdata:sync:connection:erp' }
        extensions:
          sync:
            testQuery: SELECT 1
            scripts:
              before: { name: warm-up }
        """;

    private const string ProxyFile = """
        # the ERP reader
        schema: connection/1
        name: erp
        engine: sqlserver
        address: { host: sql01, database: Erp }
        auth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }
        extensions:
          proxy:
            allow_writes: false
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentDictionary<string, string> syncFiles = new();
    private readonly ConcurrentDictionary<string, string> proxyFiles = new();
    private readonly ConcurrentBag<JsonNode> syncWrites = [];
    private readonly ConcurrentBag<string> callers = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer sync = null!;
    private PairableServer proxy = null!;
    private PairableServer build = null!;

    public async ValueTask InitializeAsync()
    {
        syncFiles["erp"] = SyncFile;
        proxyFiles["erp"] = ProxyFile;
        sync = await PairableServer.StartAsync(product: "dbdatasync", instanceId: "sync-1", map: (server, _, _) =>
        {
            server.MapGet("/api/connections/{name}/file", (string name) =>
                syncFiles.TryGetValue(name, out var text) ? Results.Json(new { name, text, version = Version(text) }) : Results.Json(new { error = $"There is no connection '{name}'." }, statusCode: 404));
            server.MapPut("/api/connections/{name}/file", async (HttpContext context, string name) =>
            {
                var token = context.ManagementToken();
                callers.Add(token.SubjectName ?? token.SubjectId);
                if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) return Results.Json(new { error = "needs admin" }, statusCode: 403);
                var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
                syncWrites.Add(body);
                var text = (string)body["text"]!;
                var loaded = ConnectionDefinitionLoader.Load(text, $"{name}.yaml");
                // what Sync adds to the shared loader: it does not use these keys
                var refusals = loaded.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => (code: d.Code, line: d.Line, message: d.Message)).ToList();
                if (loaded.Definition is { } d0)
                {
                    if (d0.Access == AccessMode.Readonly) refusals.Add(("DBD-SYNC", 1, "`access` is not a setting Sync uses."));
                    if (d0.ManagedBy is not null) refusals.Add(("DBD-SYNC", 1, "`managed_by` is not a setting Sync uses."));
                    if (d0.Auth is { Mode: AuthMode.Sql } && d0.Auth.PasswordSecret != $"secret:dbdata:sync:connection:{name}") refusals.Add(("DBD-SYNC", 1, "the password reference must be `secret:dbdata:sync:connection:" + name + "`."));
                    if (d0.Auth is { Mode: AuthMode.Sql } && body["password"] is null) refusals.Add(("DBD-SYNC", 1, "No password was provided and no existing credential is stored."));
                }
                if (refusals.Count > 0)
                    return Results.Json(new { error = "The connection is not valid, so nothing was written.", valid = false, diagnostics = refusals.Select(r => new { r.code, severity = "error", key = (string?)null, file = $"{name}.yaml", r.line, column = 1, r.message, fix = (string?)null }) }, statusCode: 400);
                if (syncFiles.ContainsKey(name)) return Results.Json(new { error = "The connection changed since it was read.", currentVersion = Version(syncFiles[name]) }, statusCode: 409);
                syncFiles[name] = text;
                return Results.Json(new { name, version = Version(text), created = true });
            });
        });
        proxy = await PairableServer.StartAsync(product: "dbdataproxy", instanceId: "proxy-1", map: (server, _, _) =>
        {
            server.MapGet("/api/v1/data-sources/{name}", (string name) =>
                proxyFiles.TryGetValue(name, out var text) ? Results.Json(new { name, text, version = Version(text) }) : Results.Json(new { error = $"There is no data source '{name}'." }, statusCode: 404));
            server.MapPut("/api/v1/data-sources/{name}", async (HttpContext context, string name) =>
            {
                callers.Add(context.ManagementToken().SubjectName ?? "?");
                if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "needs admin" }, statusCode: 403);
                var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
                var text = (string)body["text"]!;
                var loaded = ConnectionDefinitionLoader.Load(text, $"{name}.yaml");
                if (!loaded.Ok)
                    return Results.Json(new { error = "The data source is not valid, so nothing was written.", valid = false, diagnostics = loaded.Diagnostics.Select(d => new { code = d.Code, severity = d.Severity.ToString().ToLowerInvariant(), key = d.KeyPath, file = d.Path, line = d.Line, column = d.Column, message = d.Message, fix = d.Fix }) }, statusCode: 400);
                if (proxyFiles.ContainsKey(name)) return Results.Json(new { error = "The data source changed since it was read.", current_version = Version(proxyFiles[name]) }, statusCode: 409);
                proxyFiles[name] = text;
                return Results.Json(new { name, version = Version(text), created = true, diagnostics = Array.Empty<object>() });
            });
        });
        build = await PairableServer.StartAsync(product: "dbdatabuild", instanceId: "build-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        var every = ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver;
        await instances.PairAsync(sync.Url, sync.NewCodeWith(every), "Sync one", default);
        await instances.PairAsync(proxy.Url, proxy.NewCodeWith(every), "Proxy one", default);
        await instances.PairAsync(build.Url, build.NewCodeWith(every), "Build one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { sync, proxy, build }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static string Version(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task<(HttpStatusCode Status, JsonNode Body)> Copy(object body, UserRole role = UserRole.Admin, string person = "Ana")
    {
        var session = app.Services.GetRequiredService<SessionStore>().Create(app.Services.GetRequiredService<UserStore>().CreateUser(person, null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/connections/copy") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_Sync_connection_is_copied_to_a_Proxy_as_the_file_it_is_with_what_is_the_Syncs_own_left_out()
    {
        var (status, body) = await Copy(new { from = "sync-1", name = "erp", to = "proxy-1", newName = "erp-copy" });

        Assert.Equal(HttpStatusCode.OK, status);
        var written = proxyFiles["erp-copy"];
        var copy = ConnectionDefinitionLoader.Load(written, "erp-copy.yaml").Require();
        Assert.Equal("erp-copy", copy.Name);
        Assert.Equal("sql01", copy.Address!.Host);
        Assert.Equal("svc_erp", copy.Auth!.User);
        Assert.Null(copy.ManagedBy);                                         // build-9 is a client of the source's server
        Assert.Empty(copy.Extensions.Items);                                 // extensions.sync is not read by a Proxy
        Assert.Equal("secret:dbdata:proxy:connection:erp-copy", copy.Auth.PasswordSecret);   // never Sync's store
        Assert.DoesNotContain("sync", written.Replace("sqlserver", ""));
        Assert.Equal("Sync one", (string)body["from"]!);
        Assert.Equal("Proxy one", (string)body["to"]!);
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("managed_by (build-9)"));
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("extensions.sync"));
        Assert.Equal(["secret:dbdata:proxy:connection:erp-copy"], body["secrets"]!.AsArray().Select(s => (string)s!));
        Assert.Equal(SyncFile, syncFiles["erp"]);                            // the source is untouched
        Assert.Equal(["Ana"], callers.ToArray());                            // written as the person who copied
    }

    [Fact]
    public async Task A_Proxy_data_source_is_copied_to_Sync_with_its_own_secret_reference_and_the_typed_password_sent_only_to_Sync()
    {
        var (status, body) = await Copy(new { from = "Proxy one", name = "erp", to = "Sync one", newName = "erp-pg", password = "typed-once" });

        Assert.Equal(HttpStatusCode.OK, status);
        var written = syncFiles["erp-pg"];
        var copy = ConnectionDefinitionLoader.Load(written, "erp-pg.yaml").Require();
        Assert.Equal("secret:dbdata:sync:connection:erp-pg", copy.Auth!.PasswordSecret);
        Assert.Empty(copy.Extensions.Items);                                 // the Proxy's serving policy stays on the Proxy
        Assert.StartsWith("# the ERP reader", written);                      // the file is edited, not rewritten
        var sent = Assert.Single(syncWrites);
        Assert.Equal("typed-once", (string)sent["password"]!);
        Assert.DoesNotContain("typed-once", written);                        // never in the file
        Assert.DoesNotContain("typed-once", body.ToJsonString());            // nor in what the console answers
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("stored on the target"));
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("extensions.proxy"));
    }

    [Fact]
    public async Task A_copy_between_two_servers_of_one_kind_keeps_that_kinds_own_settings_and_an_environment_reference()
    {
        var (status, _) = await Copy(new { from = "proxy-1", name = "erp", to = "proxy-1", newName = "erp-2" });

        Assert.Equal(HttpStatusCode.OK, status);
        var copy = ConnectionDefinitionLoader.Load(proxyFiles["erp-2"], "erp-2.yaml").Require();
        Assert.Equal("env:ERP_DB_PASSWORD", copy.Auth!.PasswordSecret);      // a reference the target holds the same way
        Assert.True(copy.Extensions.Items.ContainsKey("proxy"));
    }

    [Fact]
    public async Task What_the_target_refuses_comes_back_with_its_diagnostics_and_nothing_is_written()
    {
        // a Sync copy without a password: Sync says what it lacks, in its words, and the console passes it on with the notes of what it changed
        var (status, body) = await Copy(new { from = "proxy-1", name = "erp", to = "sync-1", newName = "erp-pg" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("did not accept the copy", (string)body["error"]!);
        Assert.Contains(body["diagnostics"]!.AsArray(), d => ((string)d!["message"]!).Contains("No password was provided"));
        Assert.Contains(body["notes"]!.AsArray(), n => ((string)n!).Contains("extensions.proxy"));
        Assert.False(syncFiles.ContainsKey("erp-pg"));
    }

    [Fact]
    public async Task A_name_the_target_already_has_is_a_conflict_and_the_file_there_is_not_overwritten()
    {
        proxyFiles["taken"] = ProxyFile.Replace("name: erp", "name: taken");
        var before = proxyFiles["taken"];

        var (status, body) = await Copy(new { from = "sync-1", name = "erp", to = "proxy-1", newName = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(before, proxyFiles["taken"]);
        Assert.NotNull(body["currentVersion"]);
    }

    [Fact]
    public async Task A_person_the_target_does_not_let_write_is_refused_by_the_target_not_by_the_console()
    {
        var (status, body) = await Copy(new { from = "sync-1", name = "erp", to = "proxy-1", newName = "by-viewer" }, UserRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("needs admin", (string)body["error"]!);
        Assert.False(proxyFiles.ContainsKey("by-viewer"));
    }

    [Fact]
    public async Task A_connection_the_source_does_not_have_is_the_sources_404()
    {
        var (status, body) = await Copy(new { from = "sync-1", name = "nope", to = "proxy-1" });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("There is no connection 'nope'", (string)body["error"]!);
    }

    [Fact]
    public async Task The_servers_must_be_registered_keep_connections_and_a_copy_on_one_server_needs_another_name()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(new { from = "nobody", name = "erp", to = "proxy-1" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Copy(new { from = "sync-1", name = "erp", to = "nobody" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "sync-1", name = "erp", to = "build-1" })).Status);   // a Build server keeps no connections the console copies
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "build-1", name = "erp", to = "proxy-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "sync-1", name = "erp", to = "sync-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "sync-1", name = "erp", to = "proxy-1", newName = "1 bad name" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Copy(new { from = "sync-1", name = "erp", to = "proxy-1", password = "x" })).Status);   // a Proxy takes no password from here
        Assert.Equal(SyncFile, syncFiles["erp"]);
        Assert.Single(proxyFiles);
        Assert.Empty(callers);
    }

    [Fact]
    public async Task Nobody_signed_out_copies_anything()
    {
        var response = await http.PostAsJsonAsync("/api/connections/copy", new { from = "sync-1", name = "erp", to = "proxy-1" });
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, response.StatusCode.ToString());
        Assert.Single(proxyFiles);
    }
}
