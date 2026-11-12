using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Management;
using DbDataSync.Core.Config;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>The API with the notifier's network swapped for a stand-in Gateway that records the messages it is sent.</summary>
public sealed class SyncDataChangedFactory : TestApiFactory
{
    public List<string> Bodies { get; } = [];

    private sealed class Gateway(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (bodies) bodies.Add(text);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"served":true}""", Encoding.UTF8, "application/json") };
        }
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<DataChangeNotifier>();
        services.AddSingleton(sp => new DataChangeNotifier(SyncDataChanges.Product, SyncDataChanges.PairingsIn(RepoRoot), _ => Task.FromResult(new ConsoleIdentity("sync-1", ConsoleSigningKey.Generate())),
            options: new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(50) }, handler: _ => new Gateway(Bodies)));
    }
}

/// <summary>
/// Plan 24a UC13, Sync's producer: a run that wrote rows to its target tells the serving groups this server is paired with, naming the mapping's target tables as the graph Sync sends does, signed as the server; a run that wrote nothing, a verification and a server with no
/// pairing tell no one. The pairing calls of the console's Link (<c>/api/gateways</c>) need an administrator and answer in words.
/// </summary>
public sealed class SyncDataChangedTests(SyncDataChangedFactory factory) : IClassFixture<SyncDataChangedFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private ConfigRepository Config => factory.Services.GetRequiredService<ConfigRepository>();

    private void Pair() => File.WriteAllText(Path.Combine(factory.RepoRoot, SyncDataChanges.FileName),
        """[{"name":"edge","url":"https://127.0.0.1:1","instanceId":"gw","fingerprint":"ab","pairedAt":"2026-11-12T00:00:00Z"}]""");

    private void Unpair() => File.Delete(Path.Combine(factory.RepoRoot, SyncDataChanges.FileName));

    private string Replication()
    {
        var name = $"changed-{Guid.NewGuid():N}"[..20];
        Config.SaveConnection(new ConnectionInput { Name = $"src-{name}", DriverType = DriverIds.MsSql, Host = "sql01", Database = "Crm", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveConnection(new ConnectionInput { Name = $"tgt-{name}", DriverType = DriverIds.Postgres, Host = "pg01", Database = "dwh", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "BatchReload" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = $"src-{name}", Database = "Crm" }, Target = new EndpointRef { ConnectionName = $"tgt-{name}", Database = "dwh" } },
        }, Author);
        Config.SaveTableMapping(name, new TableMappingConfig
        {
            Name = "customers",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Customers" }],
            Targets = [new TableSpec { Schema = "public", Table = "customers" }],
        }, Author);
        return name;
    }

    private Guid Run(string replication, RunKind kind, RunStatus status, long rows)
    {
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();
        var id = queue.Enqueue(replication, kind, "customers");
        runs.BeginRun(id, pid: 1);
        runs.CompleteRun(id, status, rows, rows, errorSummary: status == RunStatus.Failed ? "boom" : null);
        return id;
    }

    private async Task<List<JsonObject>> Heard(int expected)
    {
        var notifier = factory.Services.GetRequiredService<DataChangeNotifier>();
        for (var i = 0; i < 100; i++)
        {
            await notifier.FlushAsync(TimeSpan.FromSeconds(1));
            lock (factory.Bodies) if (factory.Bodies.Count >= expected) break;
            await Task.Delay(100);
        }

        await Task.Delay(1500);        // long enough for a second message that should not come
        await notifier.FlushAsync(TimeSpan.FromSeconds(2));
        lock (factory.Bodies) return [.. factory.Bodies.Select(b => (JsonObject)JsonNode.Parse(b)!)];
    }

    [Fact]
    public async Task A_run_that_wrote_rows_tells_the_paired_group_the_target_table_and_one_that_wrote_none_a_verification_and_no_pairing_tell_no_one()
    {
        factory.Bodies.Clear();

        // a queue entry is one per replication, kind and mapping, so each scenario is a replication of its own
        Unpair();
        Run(Replication(), RunKind.Primary, RunStatus.Succeeded, 40);                      // no pairing: nothing
        Assert.Empty(await Heard(0));

        Pair();
        Run(Replication(), RunKind.Primary, RunStatus.Succeeded, 0);                       // rows 0: nothing changed
        Run(Replication(), RunKind.Verification, RunStatus.Succeeded, 40);                 // a comparison writes nothing
        Run(Replication(), RunKind.BulkLoad, RunStatus.Cancelled, 40);                     // cancelled: not a finished load
        Assert.Empty(await Heard(0));

        var loadedReplication = Replication();
        var loaded = Run(loadedReplication, RunKind.BulkLoad, RunStatus.Succeeded, 40);
        var failedReplication = Replication();
        var failedAfterRows = Run(failedReplication, RunKind.Primary, RunStatus.Failed, 7);   // rows went in before it failed: the cache must not keep the old ones
        var sent = await Heard(2);

        Assert.Equal(2, sent.Count);
        Assert.All(sent, s => Assert.Equal(("data.changed/1", "sync"), ((string)s["message"]!, (string)s["product"]!)));
        Assert.Equal(new[] { failedAfterRows.ToString(), loaded.ToString() }.Order(), sent.Select(s => (string)s["run"]!).Order());
        Assert.All(sent, s => Assert.StartsWith("postgres://pg01", (string)s["dataset"]!));
        Assert.All(sent, s => Assert.EndsWith("public.customers", (string)s["dataset"]!));
        // the name is the one the graph Sync sends holds for the target table
        var graph = LineageJson.Read(JsonNode.Parse(await (await factory.CreateClient().GetAsync("/api/lineage")).Content.ReadAsStringAsync()));
        Assert.All(sent, s => Assert.Contains(graph.Nodes, n => n.Id == (string)s["dataset"]!));
        Unpair();
    }

    [Fact]
    public async Task The_pairing_calls_need_an_administrator_name_a_gateway_and_forget_it_again()
    {
        Unpair();
        var client = factory.CreateClient();

        Assert.Empty((await Json(await client.GetAsync("/api/gateways")))["gateways"]!.AsArray());
        var bad = await client.PostAsync("/api/gateways", new StringContent("""{"name":"Edge Gateway","url":"http://x","code":"c"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("https", (string)(await Json(bad))["error"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/gateways/edge")).StatusCode);

        Pair();
        var listed = await Json(await client.GetAsync("/api/gateways"));
        Assert.Equal("edge", (string)listed["gateways"]![0]!["name"]!);
        Assert.NotNull(listed["notifications"]);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/gateways/edge")).StatusCode);
        Assert.Empty((await Json(await client.GetAsync("/api/gateways")))["gateways"]!.AsArray());
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
