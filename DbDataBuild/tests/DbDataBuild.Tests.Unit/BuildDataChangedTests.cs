using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Secrets;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 24a UC13, Build's producer: when a refresh the server ran ends (from a screen through the commands route, or on its own schedule) the models that loaded are told to the serving groups it is paired with, as <c>data.changed/1</c>, signed as the server itself
/// (its client id, the Publisher role), naming each table as lineage does and nothing else. A dry run, a load that did not finish, a server with no pairing tell no one, and a group that is down never changes the answer or the run.
/// </summary>
public sealed class BuildDataChangedTests : IAsyncLifetime
{
    private const string Refreshed = """
        {"command":"connection refresh","ok":true,"data":{"connection":"sqlserver","dry_run":false,"success":true,"results":[
          {"model":"marts.orders","member":"marts.orders","load":"full","state":"done","status":"ok","rows":10},
          {"model":"marts.lines","member":"marts.lines","load":"full","state":"failed","status":"failed"},
          {"model":"marts.people","member":"marts.people","load":"full","state":"skipped","status":"skipped: busy"}]}}
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-changed-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<(string Method, string Path, string Body, ValidatedToken Token)> seen = [];
    private readonly Fake runner = new();
    private int gatewayStatus = 200;
    private WebApplication gateway = null!;
    private WebApplication build = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private ProxyPublisher publisher = null!;
    private BuildDataChanges changes = null!;
    private ConsoleKeyStore identity = null!;
    private string project = null!;
    private string id = null!;

    private string BuildDir => Path.Combine(root, "build-server");

    private sealed class Fake : IProjectCommandRunner
    {
        public string Output { get; set; } = Refreshed;
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, Output, ""));
    }

    public async Task InitializeAsync()
    {
        project = Path.Combine(root, "sales");
        Directory.CreateDirectory(BuildDir);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        id = "sales";
        var projects = new ProjectsFile(BuildDir);
        projects.Add(new HostedProject(id, project, null, null));
        var secrets = ServerSecrets.Open(BuildDir);
        using (var read = SecretValue.From("Server=db.example;Database=Sales;User Id=reader;Password=READ-pw-1c9")) await secrets.SetAsync(ServerSecrets.NameOf("sqlserver", DbDataKit.Connections.LoginRole.Read), read);
        identity = new ConsoleKeyStore(secrets, secrets);

        gateway = StartFakeGateway();
        await gateway.StartAsync();
        var settings = new BuildServerSettings(BuildDir, new Uri("https://127.0.0.1:0"));
        publisher = new ProxyPublisher(new ProxiesFile(BuildDir), identity, secrets);
        changes = BuildDataChanges.Create(BuildDir, identity, options: new DataChangeNotifierOptions { RetryAfter = TimeSpan.FromMilliseconds(50) });
        build = BuildServer.Build(settings, BuildServer.Certificate(BuildDir), runner, projects, new TuiCommand.CliHost(_ => null).Commands, _ => { }, proxyPublisher: publisher, dataChanges: changes);
        await build.StartAsync();
        var url = new Uri(build.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator | ServerRole.Author));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await build.StopAsync(); await build.DisposeAsync();
        await gateway.StopAsync(); await gateway.DisposeAsync();
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private WebApplication StartFakeGateway()
    {
        var dir = Path.Combine(root, "gateway");
        Directory.CreateDirectory(dir);
        var settings = new BuildServerSettings(dir, new Uri("https://127.0.0.1:0"));
        var files = new PairingFiles(settings.ServerYaml);
        var instanceId = ServerIdentity.InstanceId(settings.ServerYaml, "gateway", "fake gateway");
        var certificate = DirectoryCertificate.Load(dir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.MapPairing(new PairingService(instanceId, PairingProof.CertificateFingerprint(certificate), files.Clients(), files.Codes()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, files.Clients(), new MemoryReplayCache()), _ => { });
        app.MapPost("/api/v1/cache/changed", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            seen.Add((context.Request.Method, context.Request.Path.Value!, await reader.ReadToEndAsync(), context.ManagementToken()));
            return Results.Json(new { served = true }, statusCode: gatewayStatus);
        });
        return app;
    }

    private async Task PairWithGateway()
    {
        var gatewayDir = Path.Combine(root, "gateway");
        var code = new PairingFiles(new BuildServerSettings(gatewayDir, new Uri("https://127.0.0.1:0")).ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Publisher));
        await publisher.PairAsync("edge", new Uri(gateway.Urls.First()), code.Code, "Build server test", CancellationToken.None);
    }

    private async Task<HttpResponseMessage> Refresh(string body = """{"connection":"sqlserver","plan":"nightly"}""")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_refresh") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Operator | ServerRole.Author)));
        return await http.SendAsync(request);
    }

    private Task Flush() => ((DataChangeNotifier)changes.Notifier).FlushAsync();

    [Fact]
    public async Task A_refresh_tells_the_paired_gateway_about_the_models_that_loaded_signed_as_the_server_itself_and_only_those()
    {
        await PairWithGateway();

        var response = await Refresh();
        await Flush();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(seen);      // marts.orders only: one failed and one was skipped
        Assert.Equal(("POST", "/api/v1/cache/changed"), (call.Method, call.Path));
        var sent = (JsonObject)JsonNode.Parse(call.Body)!;
        Assert.Equal(["message", "dataset", "product", "at", "only_if_served"], sent.Select(p => p.Key).ToArray());
        Assert.Equal(("data.changed/1", "build"), ((string)sent["message"]!, (string)sent["product"]!));
        var dataset = (string)sent["dataset"]!;
        Assert.StartsWith("sqlserver://db.example/", dataset);
        Assert.EndsWith("marts.orders", dataset);
        // signed as the Build server itself: its own client id, its name, and the Publisher role; not the console's person
        var self = await identity.GetOrCreateAsync();
        Assert.Equal((self.Id, "build server", ServerRole.Publisher), (call.Token.SubjectId, call.Token.SubjectName, call.Token.Roles));
        Assert.Equal(1, changes.Notifier.Stats.Sent);
    }

    [Fact]
    public async Task A_dry_run_and_a_server_with_no_pairing_tell_no_one_and_the_answer_is_the_same()
    {
        var plain = await Refresh();                 // not paired yet
        await Flush();
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(seen);

        await PairWithGateway();
        runner.Output = Refreshed.Replace("\"dry_run\":false", "\"dry_run\":true");
        Assert.Equal(HttpStatusCode.OK, (await Refresh("""{"connection":"sqlserver","plan":"nightly","dry_run":true}""")).StatusCode);
        runner.Output = """{"command":"connection refresh","ok":false,"data":{"connection":"sqlserver","dry_run":false,"results":[{"model":"marts.lines","member":"marts.lines","load":"full","state":"failed","status":"failed"}]}}""";
        Assert.Equal(HttpStatusCode.OK, (await Refresh()).StatusCode);
        runner.Output = "not json at all";
        Assert.Equal(HttpStatusCode.OK, (await Refresh()).StatusCode);
        await Flush();

        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_gateway_that_is_down_is_a_counter_and_one_retry_and_the_refresh_still_answers_as_it_did()
    {
        await PairWithGateway();
        gatewayStatus = 503;

        var response = await Refresh();
        await Flush();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seen.Count);                 // the first call and the retry
        var stats = changes.Notifier.Stats;
        Assert.Equal((0L, 1L, 1L), (stats.Sent, stats.Failed, stats.Retried));
        Assert.Contains("503", stats.LastFailure);
    }

    [Fact]
    public void Only_the_models_that_finished_are_read_from_the_document_and_nothing_else_is()
    {
        Assert.Equal([("sqlserver", "marts.orders")], BuildDataChanges.LoadedModels(Refreshed));
        Assert.Empty(BuildDataChanges.LoadedModels(null));
        Assert.Empty(BuildDataChanges.LoadedModels("""{"data":{"connection":"x","results":"no"}}"""));
        Assert.Empty(BuildDataChanges.LoadedModels("""{"data":{"results":[{"model":"a","state":"done"}]}}"""));
    }
}

/// <summary>The same producer on the server's own schedule: a plan the scheduler runs, whose models loaded, tells the groups; one that failed to start or loaded nothing does not.</summary>
public sealed class BuildScheduledRefreshDataChangedTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-schedchanged-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly List<string> bodies = [];

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void To(DateTimeOffset when) => _now = when;
    }

    private sealed class Runner(string output) : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, output, ""));
    }

    private sealed class Pairings : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => [new("edge", "https://127.0.0.1:1", "gw", "ab", DateTimeOffset.UnixEpoch)];
    }

    private sealed class Gateway(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"served":true}""", Encoding.UTF8, "application/json") };
        }
    }

    public BuildScheduledRefreshDataChangedTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_scheduled_run_tells_the_groups_about_the_models_it_loaded()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var projects = new ProjectsFile(root);
        projects.Add(new HostedProject("sales", project, null, null));
        await using var notifier = new DataChangeNotifier("build", new Pairings(), _ => Task.FromResult(new ConsoleIdentity("build-1", ConsoleSigningKey.Generate())), handler: _ => new Gateway(bodies));
        var output = """{"command":"connection refresh","ok":true,"data":{"connection":"sqlserver","dry_run":false,"results":[{"model":"marts.orders","member":"marts.orders","load":"incremental","state":"done","status":"ok"}]}}""";
        var scheduler = new BuildScheduler(projects, new Runner(output), BuildSchedulerSchema.Open(root, clock), clock, _ => { }, dataChanges: new BuildDataChanges(notifier, root));
        scheduler.Tick();
        clock.To(new DateTimeOffset(2026, 10, 25, 12, 16, 0, TimeSpan.Zero));
        scheduler.Tick();

        var (item, hosted) = scheduler.Claim("test")!.Value;
        await scheduler.RunAsync(item, hosted, "test", CancellationToken.None);
        await notifier.FlushAsync();

        var sent = (JsonObject)JsonNode.Parse(Assert.Single(bodies))!;
        Assert.EndsWith("marts.orders", (string)sent["dataset"]!);
        Assert.Equal(item.RunId.ToString(), (string)sent["run"]!);
        Assert.Equal("build", (string)sent["product"]!);
    }
}
