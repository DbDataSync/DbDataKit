using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner.Client;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E08: the API action that publishes the replication set to the paired runner (<c>POST /api/runner/package/publish</c>) and the calls around it. The verb and the runner are fakes here (the verb's own words are proved in
/// <c>PackageCommandTests</c>, the runner's in <c>PackagePublishOnRealRunnerTests</c>); what is real is the host, the roles of a console's token, the link file, the audit log beside the state and what is relayed unchanged.
/// </summary>
public sealed class RunnerPublishApiTests : IDisposable
{
    private const string Refusal = "/data/config/replications/returns/table-mappings/t1.yaml: Table mapping 't1' has no cached source column metadata. Refresh it first.";
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed class FakeLauncher : IPackageLauncher
    {
        public List<string[]> Runs { get; } = [];

        public Func<string[], PackageLaunch> Answer { get; set; } = _ => new PackageLaunch(0, "{}", "");

        public Task<PackageLaunch> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Runs.Add([.. arguments]);
            return Task.FromResult(Answer([.. arguments]));
        }
    }

    private sealed class FakePackages : IRunnerPackages
    {
        public JsonNode? Described { get; set; }

        public Dictionary<string, string>? Bound { get; private set; }

        public Exception? Refuse { get; set; }

        public Task<JsonNode?> DescribeAsync(CancellationToken ct) => Refuse is null ? Task.FromResult(Described) : Task.FromException<JsonNode?>(Refuse);

        public Task BindAsync(IReadOnlyDictionary<string, string> slots, CancellationToken ct)
        {
            if (Refuse is not null) return Task.FromException(Refuse);
            Bound = new Dictionary<string, string>(slots);
            return Task.CompletedTask;
        }
    }

    private sealed class Factory : AuthenticatedApiFactory
    {
        public FakeLauncher Launcher { get; } = new();

        public FakePackages Packages { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPackageLauncher>();
                services.AddSingleton<IPackageLauncher>(Launcher);
                services.RemoveAll<ReplicationSetPublishing>();
                services.AddSingleton(sp => new ReplicationSetPublishing(sp.GetRequiredService<RunnerAccess>(), Launcher, _ => Packages,
                    new FileAuditLog(Path.Combine(RepoRoot, "management-audit.log")), TimeProvider.System));
            });
        }
    }

    private const string Result = """{"package":"sales-set","channel":"prod","mode":"stage","version":3,"changed":true,"uploaded":12,"skipped":4,"files":16,"bound":["crm_src"],"replications":[{"replication":"orders","task":"orders","mode":"Periodic","cron":"*/15 * * * *","slots":["crm_src","dwh"],"mappings":2}]}""";

    private async Task<HttpClient> LinkedAsync(UserRole role)
    {
        var admin = await _factory.SignedInAsAsync(UserRole.Admin);
        var put = await admin.PutAsJsonAsync("/api/runner", new { server = "runner-1", channel = "prod", package = "sales-set", clientDirectory = "/clients/ops" });
        put.EnsureSuccessStatusCode();
        return role == UserRole.Admin ? admin : await _factory.SignedInAsAsync(role);
    }

    private IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Path.Combine(_factory.RepoRoot, "management-audit.log")).Read();

    [Fact]
    public async Task APublishRunsTheVerbWithTheStoredLinkAndTheBindings_AndAnswersWhatTheRunnerGave()
    {
        var admin = await LinkedAsync(UserRole.Admin);
        _factory.Launcher.Answer = _ => new PackageLaunch(0, Result, "");

        var response = await admin.PostAsJsonAsync("/api/runner/package/publish", new { mode = "stage", bind = new Dictionary<string, string> { ["dwh"] = "dwh-prod", ["crm_src"] = "crm-prod" }, expectedVersion = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = Assert.Single(_factory.Launcher.Runs);
        Assert.Equal(["package", "publish", "--to", "runner", "--json", "--server", "runner-1", "--channel", "prod", "--package", "sales-set", "--client-dir", "/clients/ops",
            "--bind", "crm_src=crm-prod", "--bind", "dwh=dwh-prod", "--expected-version", "2"], run);
        var body = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal(3, body["version"]!.GetValue<int>());
        Assert.Equal("stage", body["mode"]!.GetValue<string>());
        Assert.Equal(12, body["uploaded"]!.GetValue<int>());
        Assert.Equal(["crm_src", "dwh"], body["replications"]![0]!["slots"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task ServeAsksForTheVersionToBeServedAtOnce_AndAnythingElseIsRefusedBeforeTheVerbRuns()
    {
        var admin = await LinkedAsync(UserRole.Admin);
        _factory.Launcher.Answer = _ => new PackageLaunch(0, Result.Replace("\"stage\"", "\"serve\""), "");

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/runner/package/publish", new { mode = "serve" })).StatusCode);
        Assert.Contains("--serve", _factory.Launcher.Runs.Single());

        var other = await admin.PostAsJsonAsync("/api/runner/package/publish", new { mode = "later" });
        var badBind = await admin.PostAsJsonAsync("/api/runner/package/publish", new { bind = new Dictionary<string, string> { ["a=b"] = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBind.StatusCode);
        Assert.Single(_factory.Launcher.Runs);
    }

    [Fact]
    public async Task ARefusalOfTheVerbIsRelayedInItsOwnWords_AndAuditedAsRefused()
    {
        var admin = await LinkedAsync(UserRole.Admin);
        _factory.Launcher.Answer = _ => new PackageLaunch(1, "Nothing was sent.", Refusal);

        var response = await admin.PostAsJsonAsync("/api/runner/package/publish", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>();
        Assert.Equal(Refusal, error);
        var entry = Assert.Single(Audit().Where(a => a.Command == "runner.publish"));
        Assert.StartsWith("refused: ", entry.Outcome);
        Assert.Contains("has no cached source column metadata", entry.Outcome);
        Assert.Equal("sales-set/prod@runner-1", entry.Target);
    }

    [Fact]
    public async Task APublishIsAuditedWithWhoMadeIt_AndNoPublishWithoutALink()
    {
        var unlinked = await _factory.SignedInAsAsync(UserRole.Admin);
        var none = await unlinked.PostAsJsonAsync("/api/runner/package/publish", new { });
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Contains("No runner is linked", (await none.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
        Assert.Empty(_factory.Launcher.Runs);

        var (admin, id, _) = await _factory.SignedInAsPersonAsync(UserRole.Admin);
        (await admin.PutAsJsonAsync("/api/runner", new { server = "runner-1", channel = "prod", package = "sales-set" })).EnsureSuccessStatusCode();
        _factory.Launcher.Answer = _ => new PackageLaunch(0, Result, "");
        (await admin.PostAsJsonAsync("/api/runner/package/publish", new { })).EnsureSuccessStatusCode();

        var entry = Assert.Single(Audit());
        Assert.Equal(id, entry.UserId);
        Assert.Equal("runner.publish", entry.Command);
        Assert.Equal("ok: staged version 3, 12 uploaded, 4 already there", entry.Outcome);
    }

    [Fact]
    public async Task AViewerReadsWhatTheRunnerHoldsAndWhatAPublishWouldHold_ButPublishesAndBindsNothing()
    {
        var viewer = await LinkedAsync(UserRole.Viewer);
        _factory.Launcher.Answer = _ => new PackageLaunch(0, Result.Replace("\"stage\"", "\"check\""), "");
        _factory.Packages.Described = null;

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/runner/package")).StatusCode);
        var check = await viewer.PostAsync("/api/runner/package/check", null);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(["package", "publish", "--to", "runner", "--check", "--json", "--package", "sales-set"], _factory.Launcher.Runs.Single());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/runner/package/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/runner/package/bindings", new { slots = new Dictionary<string, string> { ["dwh"] = "x" } })).StatusCode);
        Assert.Single(_factory.Launcher.Runs);
        Assert.Null(_factory.Packages.Bound);
        Assert.Empty(Audit());
    }

    [Fact]
    public async Task TheCheckRelaysTheRefusalOfAMappingWithNoColumnCache()
    {
        var viewer = await LinkedAsync(UserRole.Viewer);
        _factory.Launcher.Answer = _ => new PackageLaunch(1, "Nothing was sent.", Refusal);

        var response = await viewer.PostAsync("/api/runner/package/check", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Refusal, (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
    }

    private static JsonNode Described() => JsonNode.Parse("""
        {"name":"sales-set","latest_version":4,
         "channels":[{"channel":"dev","active":1,"previous":null,"staged":null},{"channel":"prod","active":3,"previous":2,"staged":4}],
         "staging":[{"channel":"prod","version":4,"summary":"1 of 2 instances ready","all_ready":false,"failed":[{"instance":"b","error":"slot dwh is not bound in prod"}],"losses":["orders: watermark"],"needs_confirmation":true}],
         "bindings":[{"channel":"prod","slot":"crm_src","bound_to":"crm-prod","revision":5},{"channel":"dev","slot":"dwh","bound_to":"dwh-dev","revision":1}],
         "slots":[{"channel":"prod","slot":"crm_src","kind":"sqlserver","tasks":["orders"],"versions":[3,4]},{"channel":"prod","slot":"dwh","kind":"postgres","tasks":["orders","events"],"versions":[4]},{"channel":"dev","slot":"dwh","kind":"postgres","tasks":["orders"],"versions":[1]}],
         "instances":[{"instance":"a","channel":"prod","version":4,"state":"ready","error":null,"note":null,"has_losses":true,"at":"2026-11-09T10:00:00Z","gone":false},{"instance":"b","channel":"prod","version":4,"state":"failed","error":"slot dwh is not bound in prod","note":null,"has_losses":false,"at":"2026-11-09T10:00:01Z","gone":false}],
         "events":[{"id":1,"action":"publish","channel":"dev","version":1,"actor":"x","at":"2026-11-08T10:00:00Z","note":null},{"id":2,"action":"stage","channel":"prod","version":4,"actor":"ops","at":"2026-11-09T09:59:00Z","note":null}]}
        """)!;

    [Fact]
    public async Task TheStatusIsTheLinkedChannelsVersionStagedVersionSlotsBindingsAndRunners()
    {
        var viewer = await LinkedAsync(UserRole.Viewer);
        _factory.Packages.Described = Described();

        var view = (await (await viewer.GetAsync("/api/runner/package")).Content.ReadFromJsonAsync<JsonNode>())!;

        Assert.True(view["published"]!.GetValue<bool>());
        Assert.Equal((3, 2, 4, 4), (view["active"]!.GetValue<int>(), view["previous"]!.GetValue<int>(), view["staged"]!.GetValue<int>(), view["latest"]!.GetValue<int>()));
        Assert.False(view["staging"]!["allReady"]!.GetValue<bool>());
        Assert.Equal("1 of 2 instances ready", view["staging"]!["summary"]!.GetValue<string>());
        Assert.Equal(["b: slot dwh is not bound in prod"], view["staging"]!["failed"]!.AsArray().Select(f => f!.GetValue<string>()));
        var slots = view["slots"]!.AsArray();
        Assert.Equal(["crm_src", "dwh"], slots.Select(s => s!["slot"]!.GetValue<string>()));       // the dev slot is another channel's
        Assert.Equal("crm-prod", slots[0]!["boundTo"]!.GetValue<string>());
        Assert.Null(slots[1]!["boundTo"]);                                                           // dwh is not bound in prod: the runner will not activate version 4
        Assert.Equal(["a", "b"], view["instances"]!.AsArray().Select(i => i!["instance"]!.GetValue<string>()));
        Assert.Equal(["stage"], view["events"]!.AsArray().Select(e => e!["action"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ThePackageNotPublishedYetAndNoLinkAreStatesAndNotErrors_AnUnreachableRunnerIsA502InItsWords()
    {
        var unlinked = await _factory.SignedInAsAsync(UserRole.Viewer);
        var none = (await (await unlinked.GetAsync("/api/runner/package")).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.False(none["linked"]!.GetValue<bool>());

        var viewer = await LinkedAsync(UserRole.Viewer);
        _factory.Packages.Described = null;
        var fresh = (await (await viewer.GetAsync("/api/runner/package")).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.True(fresh["linked"]!.GetValue<bool>());
        Assert.False(fresh["published"]!.GetValue<bool>());

        _factory.Packages.Refuse = new RunnerClientException("https://runner-1:8495 could not be reached (refused).");
        var down = await viewer.GetAsync("/api/runner/package");
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Contains("could not be reached", (await down.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task BindingSlotsGoesToTheLinkedChannel_TheRunnersRefusalIsRelayed_AndBothAreAudited()
    {
        var admin = await LinkedAsync(UserRole.Admin);
        _factory.Packages.Described = Described();

        var ok = await admin.PutAsJsonAsync("/api/runner/package/bindings", new { slots = new Dictionary<string, string> { ["dwh"] = "dwh-prod" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("dwh-prod", _factory.Packages.Bound!["dwh"]);

        _factory.Packages.Refuse = new RunnerClientException("The runner refused the bindings of sales-set on prod (409): the connection 'nope' does not exist on this runner.");
        var refused = await admin.PutAsJsonAsync("/api/runner/package/bindings", new { slots = new Dictionary<string, string> { ["dwh"] = "nope" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("the connection 'nope' does not exist", (await refused.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());

        var audit = Audit().Where(a => a.Command == "runner.bind").ToList();
        Assert.Equal(["ok: dwh", "refused: The runner refused the bindings of sales-set on prod (409): the connection 'nope' does not exist on this runner."], audit.Select(a => a.Outcome));
        Assert.DoesNotContain(audit, a => a.Outcome.Contains("dwh-prod"));       // never a value, only the slot
    }

    [Fact]
    public async Task OnlyOnePublishRunsAtATime()
    {
        var admin = await LinkedAsync(UserRole.Admin);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var slow = new SlowLauncher(started, release.Task);
        var publishing = new ReplicationSetPublishing(_factory.Services.GetRequiredService<RunnerAccess>(), slow, _ => _factory.Packages, new FileAuditLog(Path.Combine(_factory.RepoRoot, "slow-audit.log")), TimeProvider.System);

        var first = publishing.PublishAsync(new PublishRunnerRequest(), "ops-id", "ops", CancellationToken.None);
        await started.Task;
        var second = await Assert.ThrowsAsync<OwnershipException>(() => publishing.PublishAsync(new PublishRunnerRequest(), "ops-id", "ops", CancellationToken.None));
        release.SetResult();
        await first;

        Assert.Contains("already running", second.Message);
        _ = admin;
    }

    private sealed class SlowLauncher(TaskCompletionSource started, Task release) : IPackageLauncher
    {
        public async Task<PackageLaunch> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            started.SetResult();
            await release;
            return new PackageLaunch(0, Result, "");
        }
    }

    private static string? CliDll()
    {
        // the CLI is built beside the tests (the project reference of this project) in its own folder, `bin/<project>/<configuration>` of an artifacts layout or `<project>/bin/<configuration>/<framework>` of the default one
        foreach (var from in new[] { "DbDataSync.Api.Tests", "DbDataSync.Api" })
        {
            var candidate = Path.Combine(AppContext.BaseDirectory.Replace(from, "DbDataSync.Cli"), "DbDataSync.Cli.dll");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Fact]
    public async Task TheRealChildIsTheCliOfThisApplication_ItRefusesInItsOwnWordsWhenThereIsNoDefinition()
    {
        var dll = CliDll();
        Assert.True(dll is not null, "DbDataSync.Cli.dll was not found beside the test build; the project reference should have built it.");
        var empty = Directory.CreateTempSubdirectory("dbdatasync-publish-empty-").FullName;
        try
        {
            var launcher = new CliPackageLauncher(new DbDataSync.Api.Configuration.ApiOptions { RepoRoot = empty, StateDbPath = Path.Combine(empty, "state.db"), TaskRunnerDllPath = "unused", CliDllPath = dll });
            var publishing = new ReplicationSetPublishing(_factory.Services.GetRequiredService<RunnerAccess>(), launcher, _ => _factory.Packages, new FileAuditLog(Path.Combine(empty, "audit.log")), TimeProvider.System);

            var refused = await Assert.ThrowsAsync<ReplicationSetRefusedException>(() => publishing.CheckAsync(CancellationToken.None));

            Assert.Contains("there is no `config/` folder", refused.Message);
            Assert.Contains(empty, refused.Message);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }
}
