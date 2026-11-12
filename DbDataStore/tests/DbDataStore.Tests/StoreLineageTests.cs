using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataStore.Tests;

/// <summary>The Store's lineage (plan 18, G12): its models as tables in the suite's graph, served to a console in the wire form, with what could not be drawn named and left out.</summary>
public sealed class StoreLineageTests : IAsyncLifetime
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

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-lineage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private Uri url = null!;
    private StoreSettings settings = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("DDS_LINEAGE_TEST_MDM", "Server=SQL01;Database=Mdm;User Id=sa;Password=hunter2");
        Directory.CreateDirectory(Path.Combine(directory, "models"));
        Directory.CreateDirectory(Path.Combine(directory, "connections"));
        File.WriteAllText(Path.Combine(directory, "models", "territories.yaml"), Territories);
        File.WriteAllText(Path.Combine(directory, "models", "orphan.yaml"), Territories.Replace("name: territories", "name: orphan").Replace("mdm_sql", "nowhere"));
        File.WriteAllText(Path.Combine(directory, "connections", "mdm_sql.yaml"), "schema: connection/1\nname: mdm_sql\nengine: sqlserver\nconnection_string_secret: 'env:DDS_LINEAGE_TEST_MDM'\n");
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

    private async Task<HttpResponseMessage> Get(ServerRole role)
    {
        var code = StoreState.Pairing(settings.Directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/lineage");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_viewer_gets_the_models_as_tables_of_the_graph_and_a_model_with_no_connection_is_named_and_left_out()
    {
        var response = await Get(ServerRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var graph = LineageJson.Read(body);
        var table = Assert.Single(graph.Nodes, n => n.Kind == NodeKind.Dataset);
        Assert.Equal(("sqlserver://sql01/Mdm.territories.territory", true), (table.Id, table.UnknownUpstream));
        Assert.Equal(["code", "name"], graph.Nodes.Where(n => n.Kind == NodeKind.Column).Select(n => n.Name).Order());
        Assert.Equal(NodeKind.Serving, graph.Node(Assert.Single(graph.Downstream(table.Id)).To)!.Kind);
        var problem = Assert.Single(body["problems"]!.AsArray());
        Assert.Contains("Model 'orphan' is left out", (string)problem!);
        Assert.Contains("nowhere", (string)problem!);
        Assert.DoesNotContain("hunter2", body.ToJsonString());      // only the address is read from the secret
    }

    [Fact]
    public async Task A_call_without_a_role_the_server_knows_is_refused()
    {
        // the role check is the shared one: a token for no role at all holds none of them
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(ServerRole.None)).StatusCode);
    }
}
