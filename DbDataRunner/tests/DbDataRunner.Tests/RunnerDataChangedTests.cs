using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 24a UC13 and plan 27a IX12 on a real Runner server with real task processes: a task that reports what it read and wrote with the Kit's <c>run.Lineage</c> has its dataset writes told to the serving groups the server is paired with (the identity lines
/// only, signed as the server, never an outside system or a table a task named in its own words), what it reported is kept, and the runner's lineage route (<c>GET /api/v1/lineage</c>) shows the outside systems beside the package. The pairing calls
/// (<c>/api/v1/gateways</c>) need an administrator.
/// </summary>
public sealed class RunnerDataChangedTests : IDisposable
{
    private readonly string pairingDirectory = Directory.CreateTempSubdirectory("dbdr-gw-").FullName;
    private readonly List<string> bodies = [];

    public void Dispose() { try { Directory.Delete(pairingDirectory, true); } catch (IOException) { } }

    private sealed class Gateway(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (bodies) bodies.Add(text);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"served":true}""", Encoding.UTF8, "application/json") };
        }
    }

    private RunnerHarness Harness(out DataChangeNotifier notifier, bool paired)
    {
        var h = new RunnerHarness(StateEngineIds.Sqlite);
        var pairings = RunnerDataChanges.PairingsIn(h.Directory);
        if (paired) pairings.Add(new GatewayPairing("edge", "https://127.0.0.1:1", "gw", "ab", DateTimeOffset.UnixEpoch));
        notifier = new DataChangeNotifier(RunnerDataChanges.Product, pairings, _ => Task.FromResult(new ConsoleIdentity("runner-1", ConsoleSigningKey.Generate())), handler: _ => new Gateway(bodies));
        h.Settings = h.Settings with { DataChanges = new RunnerDataChanges(notifier, pairings) };
        return h;
    }

    private static async Task RunReports(RunnerHarness h)
    {
        var (queued, started) = await h.PostAsync("/api/v1/tasks/sample/dev/reports/run");
        Assert.Equal(HttpStatusCode.Accepted, queued);
        var runId = (string)started!["run_id"]!;
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&channel=dev&task=reports")).FirstOrDefault(r => (string)r!["id"]! == runId && r["finished"] is not null), "the run to end");
    }

    [Fact]
    public async Task A_task_that_wrote_a_dataset_tells_the_paired_group_that_identity_only_and_the_lineage_route_shows_the_outside_systems_it_touched()
    {
        await using var h = Harness(out var notifier, paired: true);
        h.Install(SamplePackage.Task("reports"));
        await h.StartAsync();

        await RunReports(h);
        // the message is sent when the executor returns, a moment after the run shows as finished
        await h.WaitAsync(async () => { await notifier.FlushAsync(); lock (bodies) return bodies.Count > 0 ? bodies : null; }, "the group to be told");

        // the writes that are dataset identities: one message; the share, the free-text table name and the reads are not told
        var sent = (JsonObject)JsonNode.Parse(Assert.Single(bodies))!;
        Assert.Equal(("data.changed/1", "runner", "sqlserver://wh/Sales.dbo.orders"), ((string)sent["message"]!, (string)sent["product"]!, (string)sent["dataset"]!));
        Assert.True(Guid.TryParse((string)sent["run"]!, out _));

        // what it reported is kept, once per name, and the runner's lineage route lays the outside systems beside the package (IX12)
        await RunReports(h);
        var rows = new TaskLineageStore(h.State).Read();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.Seen));
        var (status, document) = await h.GetAsync("/api/v1/lineage", ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        var graph = LineageJson.Read(document!);
        var api = LineageIdentity.ExternalSystem("http-api", "crm");
        var share = LineageIdentity.ExternalSystem("sftp", "exports");
        Assert.Equal([api, share], graph.Nodes.Where(n => n.Kind == NodeKind.ExternalSystem).Select(n => n.Id).Order());
        Assert.Contains(share, graph.Impact(api).Reached.Select(r => r.Node.Id));          // the API is upstream of what the task wrote to the share
    }

    [Fact]
    public async Task A_runner_with_no_pairing_tells_no_one_and_the_pairing_calls_need_an_administrator()
    {
        await using var h = Harness(out var notifier, paired: false);
        h.Install(SamplePackage.Task("reports"));
        await h.StartAsync();

        await RunReports(h);
        await Task.Delay(500);       // long enough for a message that should not come
        await notifier.FlushAsync();
        Assert.Empty(bodies);

        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Post, "/api/v1/gateways", ServerRole.Operator, new { name = "edge", url = "https://127.0.0.1:1", code = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, "/api/v1/gateways", ServerRole.Admin, new { name = "Edge", url = "http://x", code = "x" })).Status);
        var (listed, body) = await h.GetAsync("/api/v1/gateways", ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Empty(body!["gateways"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await h.CallAsync(HttpMethod.Delete, "/api/v1/gateways/edge", ServerRole.Admin)).Status);
    }

    [Fact]
    public void Only_a_dataset_identity_is_a_dataset_not_a_name_a_task_chose_or_an_outside_system()
    {
        Assert.True(RunnerDataChanges.IsDataset("sqlserver://wh/Sales.dbo.orders"));
        Assert.True(RunnerDataChanges.IsDataset("postgres://pg01:5433/dwh.public.customers"));
        Assert.False(RunnerDataChanges.IsDataset("external://sftp/exports"));
        Assert.False(RunnerDataChanges.IsDataset("orders_staging"));
        Assert.False(RunnerDataChanges.IsDataset("crm.orders"));
        Assert.False(RunnerDataChanges.IsDataset("sqlserver://wh"));
        Assert.False(RunnerDataChanges.IsDataset("sqlserver://wh/a b"));
        Assert.False(RunnerDataChanges.IsDataset(""));
    }
}
