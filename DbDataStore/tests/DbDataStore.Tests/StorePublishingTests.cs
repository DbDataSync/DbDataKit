using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbDataStore.Tests;

/// <summary>
/// Plan 20 O10b (owner decision 2026-11-04): publishing is the one act that makes the model being worked on available. Its tables are brought to it first, this server's copy is pinned to it, and that version, never one
/// still being worked on, goes to the serving groups; a published model is read and written there only. The group is a stand-in that pairs as a Proxy does and records what it is sent; the tables are on a real SQL Server
/// (<c>DBDATAKIT_TEST_SQLSERVER</c>; skipped without it).
/// </summary>
public sealed class StorePublishingTests : IAsyncLifetime
{
    private static string Model(string? extra = null) => $$"""
        schemaVersion: 1
        name: territories
        storage: {connection: mdm}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true}{{extra}}
        """;

    private readonly string? _server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds-publish-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _database = "dds_publish_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _variable = "DDS_PUBLISH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<(string Method, string Path, JsonNode? Body)> _seen = [];
    private readonly ConsoleIdentity _console = new("console-1", ConsoleSigningKey.Generate());
    private int? _modelVersion;
    private bool _groupRefuses;
    private WebApplication _group = null!;
    private WebApplication? _store;

    private string StoreDir => Path.Combine(_root, "store");
    private string GroupDir => Path.Combine(_root, "group");
    private string ConnectionTo(string database) => new SqlConnectionStringBuilder(_server) { InitialCatalog = database }.ConnectionString;

    private async Task RunAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(ConnectionTo(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> HasColumnAsync(string column)
    {
        await using var connection = new SqlConnection(ConnectionTo(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT count(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'territories' AND COLUMN_NAME = '{column}'", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(StoreDir, StoreModels.Folder));
        Directory.CreateDirectory(Path.Combine(StoreDir, StoreConnections.Folder));
        File.WriteAllText(StoreModels.FileOf(StoreDir, "territories"), Model());
        File.WriteAllText(StoreConnections.FileOf(StoreDir, "mdm"), $"schema: connection/1\nname: mdm\nengine: sqlserver\nconnection_string_secret: 'env:{_variable}'\ntls:\n  encrypt: false\n");
        if (_server is not null)
        {
            await RunAsync("master", $"CREATE DATABASE {_database}");
            Environment.SetEnvironmentVariable(_variable, ConnectionTo(_database));
        }

        Directory.CreateDirectory(GroupDir);
        var serverYaml = Path.Combine(GroupDir, "server.yaml");
        var files = new PairingFiles(serverYaml);
        var instanceId = ServerIdentity.InstanceId(serverYaml, "proxy", "stand-in group");
        var certificate = DirectoryCertificate.Load(GroupDir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        _group = builder.Build();
        _group.UseManagementApiVersion();
        _group.MapPairing(new PairingService(instanceId, PairingProof.CertificateFingerprint(certificate), files.Clients(), files.Codes()));
        _group.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, files.Clients(), new MemoryReplayCache()), _ => { });
        _group.MapMethods("/api/v1/{**rest}", ["GET", "PUT", "DELETE"], async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            var path = context.Request.Path.Value!;
            _seen.Add((context.Request.Method, path, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            if (path.StartsWith("/api/v1/secrets/", StringComparison.Ordinal)) return Results.Json(new { reference = "secret:dbdata:gateway:managed-store:" + path[(path.LastIndexOf('/') + 1)..] });
            if (path.StartsWith("/api/v1/data-sources/", StringComparison.Ordinal))
                return context.Request.Method == "GET" ? Results.Json(new { error = "none" }, statusCode: 404) : Results.Json(new { version = "v1", created = true, group = true });
            if (context.Request.Method == "DELETE") { _modelVersion = null; return Results.Json(new { removed = true }); }
            if (context.Request.Method == "GET")
                return _modelVersion is { } held ? Results.Json(new { current_version = held }) : Results.Json(new { error = "none" }, statusCode: 404);
            if (_groupRefuses) return Results.Json(new { error = "The group is down for maintenance." }, statusCode: 503);
            _modelVersion = (_modelVersion ?? 0) + 1;
            return Results.Json(new { version = _modelVersion, created = _modelVersion == 1, unchanged = false });
        });
        await _group.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null) { await _store.StopAsync(); await _store.DisposeAsync(); }
        await _group.DisposeAsync();
        Environment.SetEnvironmentVariable(_variable, null);
        if (_server is not null)
        {
            SqlConnection.ClearAllPools();
            await RunAsync("master", $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE {_database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {_database}; END");
        }
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Error)> Cli(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await StoreCli.Router().RunAsync([.. args, "--repo", StoreDir], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private async Task Pair()
    {
        var code = new PairingFiles(Path.Combine(GroupDir, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        var added = await Cli("proxy", "add", "edge", "--url", _group.Urls.First(), "--code", code);
        Assert.True(added.Exit == 0, added.Error);
    }

    /// <summary>This server's own data API, as a console calls it.</summary>
    private async Task<Func<HttpMethod, string, Task<HttpStatusCode>>> StoreApi()
    {
        var settings = new StoreSettings(StoreDir, new Uri("https://127.0.0.1:0"));
        _store = StoreServer.Build(settings, StoreServer.Certificate(StoreDir), _ => { });
        await _store.StartAsync();
        var url = new Uri(_store.Urls.First());
        var paired = await PairingClient.PairAsync(url, StoreState.Pairing(settings.Directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author)).Code, _console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return async (method, path) =>
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, _console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Admin | ServerRole.Author)));
            if (method == HttpMethod.Post) request.Content = new StringContent("""{"changes": [{"action": "insert", "entity": "territory", "values": {"code": "NO", "name": "North"}}]}""", Encoding.UTF8, "application/json");
            return (await http.SendAsync(request)).StatusCode;
        };
    }

    private IEnumerable<(string Method, string Path, JsonNode? Body)> ModelPuts => _seen.Where(s => s.Method == "PUT" && s.Path == "/api/v1/entity-models/territories");

    [Fact]
    public async Task Publishing_brings_the_tables_to_the_model_first_and_sends_only_that_version_and_the_model_is_then_read_and_written_in_the_group_only()
    {
        Assert.SkipWhen(_server is null, "set DBDATAKIT_TEST_SQLSERVER (testenv/testenv.sh up mssql; eval \"$(testenv/testenv.sh env dbdatakit)\") to publish against a real database");
        await Pair();

        // the first publish names the group, makes the tables (there were none) and sends the model with its login and data source
        var first = await Cli("model", "publish", "territories", "--proxy", "edge");
        Assert.True(first.Exit == 0, first.Error);
        Assert.Equal(["/api/v1/secrets/mdm-write", "/api/v1/data-sources/mdm", "/api/v1/entity-models/territories"], _seen.Where(s => s.Method == "PUT").Select(s => s.Path));
        Assert.DoesNotContain("SENTINEL", (string)_seen.Single(s => s.Path == "/api/v1/data-sources/mdm" && s.Method == "PUT").Body!["text"]!);
        // the file says schemaVersion: 1; what is published is the current format of it (plan 33 6d, rule 5), and the data source is the current connection format
        Assert.Equal(DbDataKit.DataModel.DataModelLoader.WithCurrentVersion(Model()), (string)ModelPuts.Single().Body!["model"]!);
        Assert.StartsWith("schema: connection/2\n", (string)_seen.Single(s => s.Path == "/api/v1/data-sources/mdm" && s.Method == "PUT").Body!["text"]!);
        Assert.True(await HasColumnAsync("name"));
        Assert.Equal(["edge"], new StorePublications(StoreDir).Of("territories"));

        // being worked on: a new attribute saved, not published. The group is sent nothing, and this server does not serve it either: the published model is read and written in the group only
        var edited = Model("\n      region: {type: VARCHAR(20)}");
        File.WriteAllText(StoreModels.FileOf(StoreDir, "territories"), edited);
        var api = await StoreApi();
        Assert.Equal(HttpStatusCode.Conflict, await api(HttpMethod.Get, "/api/v1/models/territories/entities/territory/rows"));
        Assert.Equal(HttpStatusCode.Conflict, await api(HttpMethod.Post, "/api/v1/models/territories/changes"));
        Assert.Equal(HttpStatusCode.Conflict, await api(HttpMethod.Post, "/api/v1/models/territories/migrate?apply=true"));   // its tables move only by publishing
        Assert.Equal(HttpStatusCode.OK, await api(HttpMethod.Post, "/api/v1/models/territories/migrate"));                    // a dry run changes nothing
        Assert.NotEqual(0, (await Cli("storage", "create", "--model", "territories")).Exit);
        Assert.False(await HasColumnAsync("region"));

        // a dry run says what publishing would do, and does none of it
        _seen.Clear();
        var dry = await Cli("model", "publish", "territories", "--dry-run");
        Assert.True(dry.Exit == 0, dry.Error);
        Assert.Contains("region", dry.Out, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_seen, s => s.Method == "PUT");
        Assert.False(await HasColumnAsync("region"));

        // publishing it: the column first, then the group (the one it is published to, unnamed now) gets the model as the tables now have it
        var second = await Cli("model", "publish", "territories");
        Assert.True(second.Exit == 0, second.Error);
        Assert.True(await HasColumnAsync("region"));
        Assert.Equal(DbDataKit.DataModel.DataModelLoader.WithCurrentVersion(edited), (string)ModelPuts.Single().Body!["model"]!);
        Assert.Equal(1, (int)ModelPuts.Single().Body!["expected_version"]!);

        // taken off the group, it is read and written here again
        Assert.Equal(0, (await Cli("model", "unpublish", "territories", "--proxy", "edge")).Exit);
        Assert.Contains(_seen, s => s.Method == "DELETE" && s.Path == "/api/v1/entity-models/territories");
        Assert.Empty(new StorePublications(StoreDir).Of("territories"));
        Assert.Equal(HttpStatusCode.OK, await api(HttpMethod.Post, "/api/v1/models/territories/changes"));
    }

    [Fact]
    public async Task A_group_that_does_not_take_the_version_keeps_the_one_it_had_and_publishing_again_sends_it()
    {
        Assert.SkipWhen(_server is null, "set DBDATAKIT_TEST_SQLSERVER to publish against a real database");
        await Pair();
        Assert.Equal(0, (await Cli("model", "publish", "territories", "--proxy", "edge")).Exit);
        File.WriteAllText(StoreModels.FileOf(StoreDir, "territories"), Model("\n      region: {type: VARCHAR(20)}"));

        _groupRefuses = true;
        var refused = await Cli("model", "publish", "territories");
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("released here", refused.Error);
        Assert.Contains("keeps the version it had", refused.Error);
        Assert.True(await HasColumnAsync("region"));     // the tables are at the new version, and this server's copy with them
        Assert.Equal(1, _modelVersion);

        _groupRefuses = false;
        Assert.Equal(0, (await Cli("model", "publish", "territories")).Exit);
        Assert.Equal(2, _modelVersion);
    }

    [Fact]
    public async Task Publishing_to_a_group_it_is_not_paired_with_a_model_it_does_not_have_or_one_never_published_without_naming_a_group_sends_nothing()
    {
        var unpaired = await Cli("model", "publish", "territories", "--proxy", "elsewhere");
        Assert.NotEqual(0, unpaired.Exit);
        Assert.Contains("not paired", unpaired.Error);
        Assert.NotEqual(0, (await Cli("model", "publish", "nothing", "--proxy", "edge")).Exit);
        var unnamed = await Cli("model", "publish", "territories");
        Assert.NotEqual(0, unnamed.Exit);
        Assert.Contains("--proxy", unnamed.Error);
        Assert.Empty(_seen);
    }
}
