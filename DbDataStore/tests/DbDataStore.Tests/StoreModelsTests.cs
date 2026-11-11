using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataStore.Tests;

/// <summary>The models a DbDataStore server holds (plan 19, Z08): saved only if valid, named for their file, read by a viewer, changed by an author, removed by an administrator.</summary>
public sealed class StoreModelsTests : IAsyncLifetime
{
    private const string Territories = """
        schemaVersion: 1
        name: territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100)}
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-models-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private Uri url = null!;
    private StoreSettings settings = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0"));
        app = StoreServer.Build(settings, StoreServer.Certificate(directory), _ => { });
        await app.StartAsync();
        url = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<Func<HttpMethod, string, string?, ServerRole, Task<HttpResponseMessage>>> Client()
    {
        var code = StoreState.Pairing(settings.Directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (method, path, body, role) =>
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
            return http.SendAsync(request);
        };
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task An_author_saves_a_valid_model_and_a_viewer_lists_and_reads_it()
    {
        var send = await Client();

        var saved = await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author)).StatusCode);
        Assert.True(File.Exists(StoreModels.FileOf(directory, "territories")));

        var list = await Json(await send(HttpMethod.Get, "/api/v1/models", null, ServerRole.Viewer));
        var only = Assert.Single(list["models"]!.AsArray());
        Assert.Equal("territories", (string)only!["name"]!);
        Assert.True((bool)only["valid"]!);
        Assert.Equal("territory", (string)only["entities"]![0]!);

        var read = await Json(await send(HttpMethod.Get, "/api/v1/models/territories", null, ServerRole.Viewer));
        Assert.Contains("name: territories", (string)read["text"]!);
    }

    [Fact]
    public async Task A_save_writes_the_current_format_of_a_model_read_in_the_previous_one_and_a_format_out_of_range_is_refused_naming_the_file()
    {
        // plan 33 6d: model/1 loads, and what a save writes of it is model/2
        var send = await Client();
        Assert.Equal(HttpStatusCode.Created, (await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author)).StatusCode);
        var text = File.ReadAllText(StoreModels.FileOf(directory, "territories"));
        Assert.StartsWith("schemaVersion: 2\n", text);
        Assert.Equal(Territories.Replace("schemaVersion: 1", "schemaVersion: 2"), text);

        var tooNew = await send(HttpMethod.Put, "/api/v1/models/robots", Territories.Replace("schemaVersion: 1", "schemaVersion: 3").Replace("name: territories", "name: robots"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooNew.StatusCode);
        Assert.Equal("robots.yaml", (string)(await Json(tooNew))["diagnostics"]![0]!["file"]!);
        Assert.False(File.Exists(StoreModels.FileOf(directory, "robots")));
    }

    [Fact]
    public async Task A_model_that_does_not_load_is_refused_with_its_diagnostics_and_nothing_is_saved()
    {
        var send = await Client();

        var refused = await send(HttpMethod.Put, "/api/v1/models/territories", Territories.Replace("VARCHAR(100)", "NOT_A_TYPE"), ServerRole.Author);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await Json(refused);
        Assert.Equal("territories.yaml", (string)body["diagnostics"]![0]!["file"]!);
        Assert.False(File.Exists(StoreModels.FileOf(directory, "territories")));
    }

    [Fact]
    public async Task A_model_must_be_called_what_its_file_is_called()
    {
        var send = await Client();

        var refused = await send(HttpMethod.Put, "/api/v1/models/other", Territories, ServerRole.Author);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("territories", (string)(await Json(refused))["diagnostics"]![0]!["message"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await send(HttpMethod.Put, "/api/v1/models/Bad..Name", Territories, ServerRole.Author)).StatusCode);
    }

    [Fact]
    public async Task Validate_is_a_dry_run_and_each_role_may_do_only_its_part()
    {
        var send = await Client();

        var dry = await Json(await send(HttpMethod.Post, "/api/v1/models/territories/validate", Territories, ServerRole.Viewer));
        Assert.True((bool)dry["valid"]!);
        Assert.False(File.Exists(StoreModels.FileOf(directory, "territories")));

        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Viewer)).StatusCode);
        await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author);
        Assert.Equal(HttpStatusCode.Forbidden, (await send(HttpMethod.Delete, "/api/v1/models/territories", null, ServerRole.Author)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await send(HttpMethod.Delete, "/api/v1/models/territories", null, ServerRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories", null, ServerRole.Viewer)).StatusCode);
    }

    [Fact]
    public async Task A_read_says_the_version_and_what_this_caller_may_do_and_a_save_may_quote_the_version_it_read()
    {
        var send = await Client();
        var created = await Json(await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author));
        var version = (string)created["version"]!;

        var read = await Json(await send(HttpMethod.Get, "/api/v1/models/territories", null, ServerRole.Viewer));
        Assert.Equal(version, (string)read["version"]!);
        Assert.Equal(StoreModels.VersionOf((string)read["text"]!), version);
        Assert.False((bool)read["can"]!["save"]!);
        Assert.False((bool)read["can"]!["remove"]!);
        var asAuthor = await Json(await send(HttpMethod.Get, "/api/v1/models/territories", null, ServerRole.Author));
        Assert.True((bool)asAuthor["can"]!["save"]!);
        Assert.False((bool)asAuthor["can"]!["remove"]!);
        Assert.False((bool)asAuthor["can"]!["admin"]!);
        var asAdmin = await Json(await send(HttpMethod.Get, "/api/v1/models/territories", null, ServerRole.Admin));
        Assert.True((bool)asAdmin["can"]!["save"]!);
        Assert.True((bool)asAdmin["can"]!["remove"]!);
        Assert.True((bool)asAdmin["can"]!["admin"]!);

        // quoting the version that is held saves; quoting an old one is a conflict that names the current version and changes nothing
        var changed = Territories.Replace("VARCHAR(100)", "VARCHAR(120)");
        var saved = await send(HttpMethod.Put, $"/api/v1/models/territories?expected_version={version}", changed, ServerRole.Author);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var newVersion = (string)(await Json(saved))["version"]!;
        Assert.NotEqual(version, newVersion);
        var stale = await send(HttpMethod.Put, $"/api/v1/models/territories?expected_version={version}", Territories, ServerRole.Author);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(newVersion, (string)(await Json(stale))["current_version"]!);
        Assert.Contains("VARCHAR(120)", File.ReadAllText(StoreModels.FileOf(directory, "territories")));

        // a model that is not there cannot be saved against a version
        var gone = await send(HttpMethod.Put, $"/api/v1/models/other?expected_version={version}", Territories.Replace("name: territories", "name: other"), ServerRole.Author);
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Null((await Json(gone))["current_version"]);
    }

    [Fact]
    public async Task The_requests_list_says_whether_this_caller_may_decide_and_which_role_would()
    {
        var send = await Client();
        await send(HttpMethod.Put, "/api/v1/models/territories", Territories.Replace("storage: {connection: mdm_sql}", "storage: {connection: mdm_sql}\napproval: {changes: owner}"), ServerRole.Author);
        await send(HttpMethod.Put, "/api/v1/models/stewarded", Territories.Replace("name: territories", "name: stewarded").Replace("storage: {connection: mdm_sql}", "storage: {connection: mdm_sql}\napproval: {changes: steward}"), ServerRole.Author);

        var owner = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/requests", null, ServerRole.Author));
        Assert.False((bool)owner["can_decide"]!);
        Assert.Equal("admin", (string)owner["decision_needs"]!);
        Assert.Equal("owner", (string)owner["approval"]!);
        Assert.Empty(owner["requests"]!.AsArray());
        Assert.True((bool)(await Json(await send(HttpMethod.Get, "/api/v1/models/territories/requests", null, ServerRole.Admin)))["can_decide"]!);

        var steward = await Json(await send(HttpMethod.Get, "/api/v1/models/stewarded/requests", null, ServerRole.Author));
        Assert.True((bool)steward["can_decide"]!);
        Assert.Equal("author", (string)steward["decision_needs"]!);
        Assert.False((bool)(await Json(await send(HttpMethod.Get, "/api/v1/models/stewarded/requests", null, ServerRole.Viewer)))["can_decide"]!);
    }

    [Fact]
    public async Task The_schema_is_the_model_as_data_for_a_form_a_grid_or_a_typed_client()
    {
        var send = await Client();
        Assert.Equal(HttpStatusCode.NotFound, (await send(HttpMethod.Get, "/api/v1/models/territories/schema", null, ServerRole.Viewer)).StatusCode);
        await send(HttpMethod.Put, "/api/v1/models/territories", Territories, ServerRole.Author);

        var schema = await Json(await send(HttpMethod.Get, "/api/v1/models/territories/schema", null, ServerRole.Viewer));

        var entity = schema["entities"]![0]!;
        Assert.Equal("territory", (string)entity["name"]!);
        Assert.Equal(["code"], entity["key"]!.AsArray().Select(k => (string)k!).ToArray());
        var code = entity["attributes"]![0]!;
        Assert.Equal("string", (string)code["kind"]!);
        Assert.Equal(10, (int)code["length"]!);
        Assert.True((bool)code["required"]!);
        Assert.Equal("none", (string)entity["history"]!);
        Assert.Equal("none", (string)schema["instances"]!);
    }
}
