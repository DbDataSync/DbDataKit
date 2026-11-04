using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>
/// Plan 20 O10: the server publishes a model to a serving group it is paired with as a publishing client, as a Build server publishes a semantic package: the write login of the model's connection as the group's secret,
/// a data source that refers to it, then the model naming that data source, each on the version the group has. The group is a stand-in that pairs as a Proxy does and records what it is sent.
/// </summary>
public sealed class StorePublishingTests : IAsyncLifetime
{
    private const string Model = """
        schemaVersion: 1
        name: territories
        storage: {connection: mdm}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dds-publish-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _variable = "DDS_PUBLISH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<(string Method, string Path, JsonNode? Body, ValidatedToken? Token)> _seen = [];
    private int? _modelVersion;
    private WebApplication _group = null!;

    private string StoreDir => Path.Combine(_root, "store");
    private string GroupDir => Path.Combine(_root, "group");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(StoreDir, StoreModels.Folder));
        Directory.CreateDirectory(Path.Combine(StoreDir, StoreConnections.Folder));
        File.WriteAllText(StoreModels.FileOf(StoreDir, "territories"), Model);
        File.WriteAllText(StoreConnections.FileOf(StoreDir, "mdm"), $"schema: connection/1\nname: mdm\nengine: sqlserver\nconnection_string_secret: 'env:{_variable}'\n");
        Environment.SetEnvironmentVariable(_variable, "Server=db.example.invalid;Database=mdm;User ID=writer;Password=w-SENTINEL;TrustServerCertificate=True");

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
            _seen.Add((context.Request.Method, path, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text), context.ManagementToken()));
            if (path.StartsWith("/api/v1/secrets/", StringComparison.Ordinal)) return Results.Json(new { reference = "secret:dbdata:proxy:managed-store:" + path[(path.LastIndexOf('/') + 1)..] });
            if (path.StartsWith("/api/v1/data-sources/", StringComparison.Ordinal))
                return context.Request.Method == "GET" ? Results.Json(new { error = "none" }, statusCode: 404) : Results.Json(new { version = "v1", created = true, group = true });
            if (context.Request.Method == "GET")
                return _modelVersion is { } held ? Results.Json(new { current_version = held }) : Results.Json(new { error = "none" }, statusCode: 404);
            _modelVersion = (_modelVersion ?? 0) + 1;
            return Results.Json(new { version = _modelVersion, created = _modelVersion == 1, unchanged = false });
        });
        await _group.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        await _group.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Error)> Cli(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await StoreCli.Router().RunAsync([.. args, "--repo", StoreDir], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_model_is_published_with_its_login_and_data_source_to_a_group_the_server_paired_with_as_a_publishing_client()
    {
        var code = new PairingFiles(Path.Combine(GroupDir, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        var added = await Cli("proxy", "add", "edge", "--url", _group.Urls.First(), "--code", code);
        Assert.True(added.Exit == 0, added.Error);
        Assert.Contains("edge", (await Cli("proxy", "list")).Out);
        // it paired as a server client: what it sends carries the Publisher role only
        Assert.Equal(ClientKind.Server, new PairingFiles(Path.Combine(GroupDir, "server.yaml")).Clients().All().Single().Kind);

        var published = await Cli("model", "publish", "territories", "--proxy", "edge");
        Assert.True(published.Exit == 0, published.Error);
        Assert.Contains("version 1", published.Out);

        // the login, then the data source that refers to it, then the model on that data source
        var writes = _seen.Where(s => s.Method == "PUT").ToList();
        Assert.Equal(["/api/v1/secrets/mdm-write", "/api/v1/data-sources/mdm", "/api/v1/entity-models/territories"], writes.Select(w => w.Path));
        Assert.Contains("Password=w-SENTINEL", (string)writes[0].Body!["value"]!);
        var source = (string)writes[1].Body!["text"]!;
        Assert.Contains("engine: sqlserver", source);
        Assert.Contains("connection_string_secret: 'secret:dbdata:proxy:managed-store:mdm-write'", source);
        Assert.DoesNotContain("SENTINEL", source);
        Assert.Equal(Model.ReplaceLineEndings("\n"), (string)writes[2].Body!["model"]!);
        Assert.Equal("mdm", (string)writes[2].Body!["data_source"]!);
        Assert.Null(writes[2].Body!["expected_version"]);
        Assert.All(_seen, s => Assert.Equal(ServerRole.Publisher, s.Token!.Roles));

        // published again, on the version the group answered
        _seen.Clear();
        Assert.Equal(0, (await Cli("model", "publish", "territories", "--proxy", "edge")).Exit);
        Assert.Equal(1, (int)_seen.Single(s => s.Method == "PUT" && s.Path.EndsWith("/territories", StringComparison.Ordinal)).Body!["expected_version"]!);
    }

    [Fact]
    public async Task Publishing_to_a_group_it_is_not_paired_with_or_a_model_it_does_not_have_sends_nothing()
    {
        var unpaired = await Cli("model", "publish", "territories", "--proxy", "elsewhere");
        Assert.NotEqual(0, unpaired.Exit);
        Assert.Contains("not paired", unpaired.Error);
        var code = new PairingFiles(Path.Combine(GroupDir, "server.yaml")).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        Assert.Equal(0, (await Cli("proxy", "add", "edge", "--url", _group.Urls.First(), "--code", code)).Exit);
        var missing = await Cli("model", "publish", "nothing", "--proxy", "edge");
        Assert.NotEqual(0, missing.Exit);
        Assert.Empty(_seen);
    }
}
