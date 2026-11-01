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
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
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
}
