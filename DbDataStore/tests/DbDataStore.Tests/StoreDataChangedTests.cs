using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>
/// Plan 24a UC13, DbDataStore's producer without a database: an applied write names the entities it changed as lineage names the model's tables (the names the Gateway's graph holds, which is what the Store's own lineage route sends), to the groups the
/// server is paired with, signed as the server. No pairing, a connection the server cannot read and a gateway that is down change nothing for the write.
/// </summary>
public sealed class StoreDataChangedTests : IDisposable
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
          region:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
        """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-changed-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> bodies = [];
    private readonly List<string> log = [];
    private readonly ConsoleIdentity self = new("store-1", ConsoleSigningKey.Generate());

    public StoreDataChangedTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, StoreConnections.Folder));
        File.WriteAllText(StoreConnections.FileOf(directory, "mdm"), "schema: connection/1\nname: mdm\nengine: postgres\naddress: { host: mdm.example, port: 5432, database: masters }\n");
        Directory.CreateDirectory(Path.GetDirectoryName(StoreModels.FileOf(directory, "territories"))!);
        File.WriteAllText(StoreModels.FileOf(directory, "territories"), Model);
    }

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    private sealed class Gateway(List<string> bodies, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent("""{"served":true}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Pairings(params GatewayPairing[] all) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => all;
    }

    private static readonly GatewayPairing Edge = new("edge", "https://127.0.0.1:1", "gw", "ab", DateTimeOffset.UnixEpoch);

    private StoreDataChanges Changes(IGatewayPairings pairings, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new DataChangeNotifier("store", pairings, _ => Task.FromResult(self), null, new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(20) }, handler: _ => new Gateway(bodies, status)), new StoreConnections(directory), log.Add);

    [Fact]
    public async Task The_entities_a_write_changed_are_named_as_the_models_lineage_names_its_tables_once_each()
    {
        var changes = Changes(new Pairings(Edge));

        await changes.AppliedAsync("mdm", "territories", ["territory", "territory", "region"], "req-1");
        await ((DataChangeNotifier)changes.Notifier).FlushAsync();

        var sent = bodies.Select(b => (JsonObject)JsonNode.Parse(b)!).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, s => Assert.Equal(("data.changed/1", "store", "req-1"), ((string)s["message"]!, (string)s["product"]!, (string)s["run"]!)));
        // these are the ids of the table nodes the Store's own lineage graph holds for the model: what a Gateway that serves the model has in its graph
        var (graph, problems) = await StoreLineage.BuildAsync(directory, "store-1", new StoreConnections(directory), CancellationToken.None);
        Assert.Empty(problems);
        var tables = graph.Nodes.Where(n => n.Kind == DbDataKit.Lineage.NodeKind.Dataset).Select(n => n.Id).ToHashSet();
        Assert.Equal(2, sent.Select(s => (string)s["dataset"]!).Distinct().Count());
        Assert.All(sent, s => Assert.Contains((string)s["dataset"]!, tables));
        Assert.All(sent, s => Assert.StartsWith("postgres://mdm.example", (string)s["dataset"]!));
    }

    [Fact]
    public async Task With_no_pairing_nothing_is_sent_and_a_connection_the_server_cannot_read_is_a_line_in_the_log_not_an_error()
    {
        var none = Changes(new Pairings());
        await none.AppliedAsync("mdm", "territories", ["territory"]);
        await ((DataChangeNotifier)none.Notifier).FlushAsync();
        Assert.Empty(bodies);

        var changes = Changes(new Pairings(Edge));
        await changes.AppliedAsync("missing", "territories", ["territory"]);
        await ((DataChangeNotifier)changes.Notifier).FlushAsync();
        Assert.Empty(bodies);
        Assert.Contains(log, l => l.Contains("territory") && l.Contains("missing"));
    }

    [Fact]
    public async Task A_gateway_that_is_down_is_counted_after_one_retry_and_the_call_returns()
    {
        var changes = Changes(new Pairings(Edge), HttpStatusCode.ServiceUnavailable);

        await changes.AppliedAsync("mdm", "territories", ["territory"]);
        await ((DataChangeNotifier)changes.Notifier).FlushAsync();

        Assert.Equal(2, bodies.Count);
        Assert.Equal((0L, 1L, 1L), (changes.Notifier.Stats.Sent, changes.Notifier.Stats.Failed, changes.Notifier.Stats.Retried));
    }
}
