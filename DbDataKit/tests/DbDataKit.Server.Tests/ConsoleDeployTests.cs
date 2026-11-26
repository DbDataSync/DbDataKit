using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The control service deploys a build (Design: docs/publishing.md#deploying-a-build-from-the-control-service): a real Workspace server and a runner, each in this process and each paired with the console only, never with each other. The
/// Workspace builds a Sync, a Build and a Store project (the Sync and Build programs are stand-ins, a shell script that writes the package the way the real ones do; the Store kind builds in process); the console reads the build from the
/// Workspace as the signed-in person, deploys it to the runner as that person, signed with the console's publisher key over the build record, and keeps the deploy record. The runner records the person, the signature and the build record.
/// </summary>
public sealed class ConsoleDeployTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-deploy-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication console = null!;
    private WebApplication workspace = null!;
    private PairedRunner runner = null!;
    private InProcessTasks tasks = null!;
    private HttpClient http = null!;
    private string session = null!;
    private string personId = null!;
    private string workspaceId = null!;
    private string runnerId = null!;
    private string? tools;

    /// <summary>
    /// The stand-in of <c>dbdatasync</c> and <c>dbdatabuild</c> for the verbs the Workspace runs to check and package a project: a draft with a file named <c>broken</c> is refused in the program's way (the file, on the error output, exit
    /// 1); otherwise the package is a <c>package/1</c> manifest whose manual tasks <c>load</c>, <c>provision</c> and <c>preview</c> read the connection slot <c>source</c>, with the project's files under <c>files/</c>. A draft with a file named
/// <c>preflight</c> also has the manual task <c>preflight</c>.
    /// </summary>
    private const string Tool = """
        #!/bin/bash
        out=""; root="."; pkg="orders"; to="runner"; verb="$1 $2"; third="$3"
        if [ "$1 $2 $3" = "project create starter" ]; then mkdir -p "$4/rendered"; printf 'schema: dbdatabuild.project/2\ntitle: starter\n' > "$4/dbdatabuild.yml"; printf 'cron: "*/15 * * * *"\n' > "$4/rendered/refresh.plan.yaml"; echo "{}"; exit 0; fi
        while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; --repo|--project) root="$2"; shift;; --package) pkg="$2"; shift;; --to) to="$2"; shift;; esac; shift; done
        if [ -e "$root/broken" ]; then echo "$root/broken: this draft is broken on purpose" >&2; exit 1; fi
        if [ "$verb" = "project show" ]; then echo '{"diagnostics":[]}'; exit 0; fi
        if [ -n "$out" ]; then
          mkdir -p "$out/files/repo" "$out/files/bin"; printf 'a stand-in assembly' > "$out/files/bin/Orders.dll"; for f in "$root"/*; do case "$(basename "$f")" in .git) ;; *) cp -r "$f" "$out/files/repo/";; esac; done
          printf 'schema: package/1\nname: %s\nlaunch: { kind: assembly, assembly: bin/Orders.dll }\nbuilt_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }\ntasks:\n  - name: load\n    entry: { type: Orders.Load }\n    triggers:\n      - manual: true\n    connections:\n      - { name: source, kind: sqlserver }\n  - name: provision\n    entry: { type: Orders.Provision }\n    triggers:\n      - manual: true\n    connections:\n      - { name: source, kind: sqlserver }\n  - name: preview\n    entry: { type: Orders.Preview }\n    triggers:\n      - manual: true\n    connections:\n      - { name: source, kind: sqlserver }\n' "$pkg" > "$out/package.yaml"
          if [ -e "$root/preflight" ]; then printf '  - name: preflight\n    entry: { type: Orders.Preflight }\n    triggers:\n      - manual: true\n    connections:\n      - { name: source, kind: sqlserver }\n' >> "$out/package.yaml"; fi
        fi
        if [ "$verb" = "project publish" ]; then echo "{\"data\":{\"package\":\"$pkg\",\"files\":1,\"tasks\":[{\"name\":\"load\",\"plan\":\"refresh\"}]}}"; exit 0; fi
        echo "{\"package\":\"$pkg\",\"mode\":\"check\",\"files\":1,\"replications\":[{\"replication\":\"orders\",\"task\":\"load\"}]}"
        """;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            tools = Path.Combine(directory, "tools");
            Directory.CreateDirectory(tools);
            foreach (var name in new[] { "dbdatasync", "dbdatabuild" })
            {
                File.WriteAllText(Path.Combine(tools, name), Tool.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                File.SetUnixFileMode(Path.Combine(tools, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        runner = await PairedRunner.StartAsync(tasks: state => tasks = new InProcessTasks(state, StepsOf, Answer));
        var workspaceDirectory = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspaceDirectory);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0"))
        {
            SyncExecutable = tools is null ? null : Path.Combine(tools, "dbdatasync"),
            BuildExecutable = tools is null ? null : Path.Combine(tools, "dbdatabuild"),
        }, WorkspaceServer.Certificate(workspaceDirectory), _ => { });
        await workspace.StartAsync();

        var consoleDirectory = Path.Combine(directory, "console");
        // people who sign in through an identity home hold the roles it gives them on each server and project, which is how a person reads one project of a Workspace server and publishes on a Runner
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"),
            services => services.AddSingleton(sp => new FederatedUsers(sp.GetRequiredService<StateDatabase>(), sp.GetRequiredService<IUserAccounts>(), sp.GetRequiredService<ISessionStore>(), HomeApp, FederationPolicy.With(t => t, t => t))));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()), Timeout = TimeSpan.FromMinutes(3) };
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;
        session = console.Services.GetRequiredService<ISessionStore>().Create(personId).Id;

        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        var code = WorkspaceState.Pairing(workspaceDirectory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), code.Code, "Workspace", default)).InstanceId;
        await runner.PairAsync(console, "Runner one");
        runnerId = "runner-1";
        // the console's publisher key in the runner's trust store, as the console's own pass puts it there once the runner is paired
        var trusted = await console.Services.GetRequiredService<ConsoleTrust>().EnsureAsync(instances.Registry.Find(runnerId)!);
        Assert.True(trusted.Done, trusted.Problem);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await console.StopAsync();
        await console.DisposeAsync();
        await workspace.StopAsync();
        await workspace.DisposeAsync();
        await runner.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const string HomeApp = "console";

    /// <summary>A session of a person who signed in through the identity home with <paramref name="roles"/> (keys <c>server:&lt;id&gt;</c> and <c>server:&lt;id&gt;/&lt;project&gt;</c>): a viewer of this console, and what the home says elsewhere.</summary>
    private string SignedInThroughHome(string id, string name, Dictionary<string, IReadOnlyList<string>> roles)
    {
        roles[$"app:{HomeApp}"] = ["viewer"];
        var user = console.Services.GetRequiredService<FederatedUsers>().SignIn(new HomeIdentity(id, name, null, [], roles))!;
        return console.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id;
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> As(HttpMethod method, string path, JsonNode? body = null) => As(session, method, path, body);

    private async Task<(HttpStatusCode Status, JsonNode? Body)> As(string signedIn, HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={signedIn}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? parsed = null;
        try { parsed = text.Length > 0 ? JsonNode.Parse(text) : null; }
        catch (System.Text.Json.JsonException) { /* not JSON */ }
        return (response.StatusCode, parsed);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Workspace(HttpMethod method, string path, JsonNode? body = null) => As(method, $"/relay/{workspaceId}/api/v1{path}", body);

    /// <summary>A project of <paramref name="kind"/> with a draft, built through the console's relay as the person; the draft gets the files of <paramref name="files"/> first. Returns the draft and the build.</summary>
    private async Task<(string Draft, string Build)> BuildAsync(string project, string kind, IReadOnlyDictionary<string, string>? files = null, bool succeeds = true)
    {
        var made = await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = project, ["kind"] = kind });
        Assert.True(made.Status == HttpStatusCode.Created, made.Body?.ToJsonString());
        var draft = (string)(await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces", new JsonObject { ["name"] = "draft" })).Body!["id"]!;
        foreach (var (path, text) in files ?? new Dictionary<string, string>())
            Assert.True((await Workspace(HttpMethod.Put, $"/projects/{project}/workspaces/{draft}/files/{path}", new JsonObject { ["content"] = text })).Status is HttpStatusCode.OK or HttpStatusCode.Created);
        var requested = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/builds");
        Assert.True(requested.Status == HttpStatusCode.Accepted, requested.Body?.ToJsonString());
        var build = (string)requested.Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var read = (await Workspace(HttpMethod.Get, $"/projects/{project}/workspaces/{draft}/builds/{build}")).Body!;
            var status = (string)read["build"]!["status"]!;
            if (status is "succeeded" or "failed")
            {
                Assert.True(status == (succeeds ? "succeeded" : "failed"), read.ToJsonString());
                return (draft, build);
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> DeployAsync(string project, string draft, string build, JsonObject? extra = null, string? signedIn = null)
    {
        var body = new JsonObject { ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = build, ["target"] = runnerId };
        foreach (var (key, value) in extra ?? []) body[key] = value?.DeepClone();
        return As(signedIn ?? session, HttpMethod.Post, "/api/deploys", body);
    }

    private DefinitionVersion Stored(string package, int version) => new DefinitionStore(runner.State).Versions("integration", package).Single(v => v.Version == version);

    [Fact]
    public async Task A_sync_projects_build_is_staged_on_the_runner_by_the_console_as_the_person_signed_over_its_build_record_and_the_deploy_is_recorded()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");

        var deployed = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["bind"] = new JsonObject { ["source"] = "erp" } });

        Assert.True(deployed.Status == HttpStatusCode.OK, deployed.Body?.ToJsonString());
        var answer = deployed.Body!["deployed"]!;
        Assert.Equal(("orders", "dev", "stage", 1, "verified"), ((string?)answer["package"], (string?)answer["channel"], (string?)answer["mode"], (int)answer["version"]!, (string?)answer["signature"]));

        // the runner holds version 1 staged in dev with the slot bound, published by the person, signed with the console's publisher key over the build record it keeps
        var key = await console.Services.GetRequiredService<ConsolePublisherKeyStore>().GetOrCreateAsync();
        var version = Stored("orders", 1);
        Assert.Equal(personId, version.PublishedBy);
        Assert.Equal(key.Fingerprint, version.Signer);
        var record = BuildRecord.Read(version.Build)!;
        Assert.Equal((workspaceId, "orders", "sync", draft, build, "package", true), ((string?)record["server"], (string?)record["project"], (string?)record["kind"], (string?)record["workspace"], (string?)record["build"], (string?)record["item"], (bool)record["checks"]!["passed"]!));
        Assert.Contains($"[workspace:{workspaceId}/orders/{draft}]", version.Note);
        Assert.Contains(runner.Publishing.Publisher.Bindings("dev", "integration", "orders"), b => b.Slot == "source" && b.BoundTo == "erp");
        // the record names the same content the runner holds: the hash of the item's list of files
        var contents = (await Workspace(HttpMethod.Get, $"/projects/orders/workspaces/{draft}/builds/{build}/contents")).Body!;
        Assert.Equal((string?)contents["items"]![0]!["manifest_sha256"], (string?)record["manifest_sha256"]);

        // the console keeps the deploy, found by the Workspace server, project, workspace and build
        var listed = await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=orders&workspace={draft}&build={build}");
        var kept = Assert.Single(listed.Body!["deploys"]!.AsArray())!;
        Assert.Equal(("ok", runnerId, "dev", 1, "verified", "package", personId), ((string?)kept["outcome"], (string?)kept["target"], (string?)kept["channel"], (int)kept["version"]!, (string?)kept["signature"], (string?)kept["item"], (string?)kept["deployed_by"]));
        Assert.Equal(build, (string?)kept["build_record"]!["build"]);
        var audited = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeploys.AuditVerb));
        Assert.Contains(audited, a => a.Entry.ActorId == personId && a.Entry.Outcome == AuditEvent.Ok && a.Entry.TargetId == runnerId);

        // the Workspace server sent nothing: it has no targets to list (the relay passes no such call) and knows nothing of the deploy
        Assert.Equal(HttpStatusCode.Forbidden, (await Workspace(HttpMethod.Get, "/publish-targets")).Status);
        Assert.Null((await Workspace(HttpMethod.Get, $"/projects/orders/workspaces/{draft}/builds/{build}")).Body!["publishes"]);
    }

    [Fact]
    public async Task A_build_projects_package_and_a_store_models_package_are_deployed_the_same_way()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatabuild is a shell script");
        var (buildDraft, buildBuild) = await BuildAsync("refresh", "build");
        var built = await DeployAsync("refresh", buildDraft, buildBuild, new JsonObject { ["channel"] = "dev", ["bind"] = new JsonObject { ["source"] = "erp" } });
        Assert.True(built.Status == HttpStatusCode.OK, built.Body?.ToJsonString());
        Assert.Equal(("build", "package"), ((string?)BuildRecord.Read(Stored("refresh", 1).Build)!["kind"], (string?)BuildRecord.Read(Stored("refresh", 1).Build)!["item"]));

        // a Store project's build holds one deploy package per model; the person names the model, and its storage is bound in the channel
        var (storeDraft, storeBuild) = await BuildAsync("territories", "store");
        var staged = await DeployAsync("territories", storeDraft, storeBuild, new JsonObject { ["channel"] = "dev", ["model"] = "territories", ["bind"] = new JsonObject { ["storage"] = "mdm" } });
        Assert.True(staged.Status == HttpStatusCode.OK, staged.Body?.ToJsonString());
        Assert.Equal(("model-territories", "stage", "verified"), ((string?)staged.Body!["deployed"]!["package"], (string?)staged.Body["deployed"]!["mode"], (string?)staged.Body["deployed"]!["signature"]));
        Assert.Equal("model-package/territories", (string?)BuildRecord.Read(Stored("model-territories", 1).Build)!["item"]);
        Assert.Contains(runner.Publishing.Publisher.Bindings("dev", "integration", "model-territories"), b => b.Slot == "storage" && b.BoundTo == "mdm");

        var listed = (await As(HttpMethod.Get, $"/api/deploys?workspace_server=Workspace")).Body!["deploys"]!.AsArray();
        Assert.Equal(["model-package/territories", "package"], listed.Select(d => (string?)d!["item"]));
    }

    [Fact]
    public async Task A_preview_of_a_build_that_is_not_an_app_is_refused_before_anything_is_sent_and_recorded()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");

        var refused = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["previewInstance"] = "orders" });

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("Only an app has a preview with data.", (string?)refused.Body!["error"]);
        Assert.Empty(new DefinitionStore(runner.State).Versions("integration", "orders"));
        Assert.Equal(("refused", "Only an app has a preview with data."), ((string?)refused.Body["deploy"]!["outcome"], (string?)refused.Body["deploy"]!["error"]));
    }

    [Fact]
    public async Task A_failed_build_and_a_package_the_runner_cannot_take_are_refused_with_the_same_words_as_before_and_recorded()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (failedDraft, failed) = await BuildAsync("broken", "sync", new Dictionary<string, string> { ["broken"] = "on purpose" }, succeeds: false);
        var refused = await DeployAsync("broken", failedDraft, failed, new JsonObject { ["channel"] = "dev" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("This build is failed, so there is nothing to publish. Only a build that succeeded is published.", (string?)refused.Body!["error"]);

        var (draft, build) = await BuildAsync("orders", "sync");
        var noChannel = await DeployAsync("orders", draft, build);
        Assert.Equal(HttpStatusCode.BadRequest, noChannel.Status);
        Assert.Equal("Name the channel of the Runner the package goes to (\"channel\").", (string?)noChannel.Body!["error"]);
        Assert.Empty(new DefinitionStore(runner.State).Versions("integration", "orders"));

        // a target that is not a Runner or a Gateway
        var toWorkspace = await As(HttpMethod.Post, "/api/deploys", new JsonObject { ["workspaceServer"] = workspaceId, ["project"] = "orders", ["workspace"] = draft, ["build"] = build, ["target"] = workspaceId, ["channel"] = "dev" });
        Assert.Equal(HttpStatusCode.BadRequest, toWorkspace.Status);
        Assert.Contains("a build is deployed to a Runner or a Gateway", (string?)toWorkspace.Body!["error"]);

        var kept = (await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}")).Body!["deploys"]!.AsArray();
        Assert.All(kept, d => Assert.Equal("refused", (string?)d!["outcome"]));
        Assert.Contains(kept, d => (string?)d!["error"] == "Name the channel of the Runner the package goes to (\"channel\").");
    }

    [Fact]
    public async Task A_viewer_of_the_project_who_publishes_and_operates_on_the_runner_deploys_a_build_and_runs_its_task_from_the_runs_tab()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        // Ben reads the project orders and nothing else of the Workspace server; on the runner he publishes and operates, and administers nothing
        var ben = SignedInThroughHome("ben", "Ben", new() { [$"server:{workspaceId}/orders"] = ["viewer"], [$"server:{runnerId}"] = ["publisher", "operator"] });

        var served = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["serve"] = true, ["bind"] = new JsonObject { ["source"] = "erp" } }, ben);
        Assert.True(served.Status == HttpStatusCode.OK, served.Body?.ToJsonString());
        Assert.Equal(("serve", 1), ((string?)served.Body!["deployed"]!["mode"], (int)served.Body["deployed"]!["version"]!));
        Assert.Equal("ben", Stored("orders", 1).PublishedBy);
        var deploy = (string)served.Body["deploy"]!["id"]!;

        // Run now on the Runs tab: the task of the version the channel serves, read back through the console as Ben
        var ran = await As(ben, HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "load", ["served"] = true });
        Assert.True(ran.Status == HttpStatusCode.Accepted, ran.Body?.ToJsonString());
        var runId = (string)ran.Body!["run"]!["run_id"]!;
        for (var i = 0; ; i++)
        {
            var read = await As(ben, HttpMethod.Get, $"/api/deploys/{deploy}/runs/{runId}?step=pass-record");
            if (read.Status == HttpStatusCode.NotFound && i < 100) { await Task.Delay(100); continue; }
            Assert.True(read.Status == HttpStatusCode.OK, read.Body?.ToJsonString());
            if (read.Body!["run"]!["finished"] is null && i < 300) { await Task.Delay(100); continue; }
            Assert.Equal("succeeded", (string?)read.Body["run"]!["outcome"]);
            break;
        }
        Assert.Contains("Ben", tasks.Requests.Single(r => r.Task.Task == "load").RequestedBy);

        // testing a login is the runner administrator's: the runner's roles decide, and Ben holds none of it there
        var tested = await As(ben, HttpMethod.Post, $"/api/deploys/{deploy}/binding-test", new JsonObject { ["slot"] = "source" });
        Assert.Equal(HttpStatusCode.Forbidden, tested.Status);
        Assert.Contains("Runner one", (string?)tested.Body!["error"]);
    }

    [Fact]
    public async Task A_person_who_may_not_read_the_project_is_refused_by_the_workspace_server_whatever_they_hold_on_the_runner()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        var deploy = (string)(await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["serve"] = true, ["bind"] = new JsonObject { ["source"] = "erp" } })).Body!["deploy"]!["id"]!;
        // Pat reads another project of the Workspace server, and administers the runner
        var pat = SignedInThroughHome("pat", "Pat", new() { [$"server:{workspaceId}/sales"] = ["viewer"], [$"server:{runnerId}"] = ["admin", "publisher"] });

        var deployed = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev" }, pat);
        Assert.Equal(HttpStatusCode.Forbidden, deployed.Status);
        Assert.Equal("Workspace does not let you read the project 'orders': This needs the viewer role.", (string?)deployed.Body!["error"]);
        Assert.Single(new DefinitionStore(runner.State).Versions("integration", "orders"));

        foreach (var (method, path, body) in new (HttpMethod, string, JsonNode?)[]
        {
            (HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "load", ["served"] = true }),
            (HttpMethod.Get, $"/api/deploys/{deploy}/runs/{Guid.NewGuid()}", null),
            (HttpMethod.Get, $"/api/deploys/{deploy}/runs?task=load", null),
            (HttpMethod.Post, $"/api/deploys/{deploy}/binding-test", new JsonObject { ["slot"] = "source" }),
            (HttpMethod.Post, $"/api/deploys/{deploy}/contract", new JsonObject { ["planRun"] = Guid.NewGuid().ToString(), ["approve"] = "c0ffee11" }),
            (HttpMethod.Post, $"/api/deploys/{deploy}/activate", new JsonObject()),
        })
        {
            var refused = await As(pat, method, path, body);
            Assert.True(refused.Status == HttpStatusCode.Forbidden, $"{method} {path}: {refused.Status} {refused.Body?.ToJsonString()}");
            Assert.Equal("Workspace does not let you read the project 'orders': This needs the viewer role.", (string?)refused.Body!["error"]);
        }
        Assert.DoesNotContain(tasks.Requests, r => r.Task.Package == "orders");
    }

    // ---- the test of a draft's connection before anything is published (Design: docs/authoring-metadata.md#testing-a-connection) ----

    [Fact]
    public async Task A_draft_with_no_build_tests_its_connection_in_a_channel_that_binds_the_slot_and_a_channel_that_does_not_says_so()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        Assert.Equal(HttpStatusCode.Created, (await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = "orders", ["kind"] = "sync" })).Status);
        Assert.Equal(HttpStatusCode.Created, (await Workspace(HttpMethod.Post, "/projects/orders/workspaces", new JsonObject { ["name"] = "draft" })).Status);
        // an administrator bound the slot in dev to a connection the runner has; the runner holds no version of the package anywhere
        Directory.CreateDirectory(runner.ConnectionsDirectory);
        File.WriteAllText(Path.Combine(runner.ConnectionsDirectory, "erp.yaml"), "schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string: 'Server=erp-dev;Database=erp'\nauth: { mode: integrated }\n");
        runner.Publishing.Bind("orders", "dev", new Dictionary<string, string> { ["source"] = "erp" }, null, DbDataKit.Runner.RunnerActor.Local());
        Assert.Empty(new DefinitionStore(runner.State).Versions("integration", "orders"));
        var path = $"/api/workspace-catalog/{workspaceId}/orders/binding-test";

        var dev = await As(HttpMethod.Post, path, new JsonObject { ["runner"] = runnerId, ["channel"] = "dev", ["connection"] = "source" });
        Assert.True(dev.Status == HttpStatusCode.OK, dev.Body?.ToJsonString());
        var tested = dev.Body!.AsObject();
        Assert.Equal(("orders", "source", "dev", "erp", true), ((string?)tested["package"], (string?)tested["slot"], (string?)tested["channel"], (string?)tested["bound_to"], (bool)tested["ok"]!));
        // the binding, the connection and the login passed; this runner opens no connection, so connect was not tried
        Assert.Equal(["binding:passed", "connection:passed", "login:passed", "connect:skipped"], tested["steps"]!.AsArray().Select(s => $"{s!["step"]}:{s["state"]}"));
        Assert.False((bool)tested["connected"]!);
        // the steps come back and nothing else: no row, no address, no login
        Assert.Equal(["bound_to", "channel", "connected", "connection", "message", "ok", "package", "production", "role", "runner", "runner_name", "slot", "step", "steps"], tested.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("erp-dev", dev.Body.ToJsonString());

        var prod = await As(HttpMethod.Post, path, new JsonObject { ["runner"] = runnerId, ["channel"] = "prod", ["connection"] = "source" });
        Assert.True(prod.Status == HttpStatusCode.OK, prod.Body?.ToJsonString());
        Assert.Equal(("binding", false, "The slot 'source' is not bound in channel 'prod'."), ((string?)prod.Body!["step"], (bool)prod.Body["ok"]!, (string?)prod.Body["message"]));
        Assert.Equal(["binding:failed", "connection:skipped", "login:skipped", "connect:skipped"], prod.Body["steps"]!.AsArray().Select(s => $"{s!["step"]}:{s["state"]}"));

        // the runner's roles decide: Ben reads the project and operates the runner, and administers nothing there
        var ben = SignedInThroughHome("ben", "Ben", new() { [$"server:{workspaceId}/orders"] = ["viewer"], [$"server:{runnerId}"] = ["publisher", "operator"] });
        var notAdmin = await As(ben, HttpMethod.Post, path, new JsonObject { ["runner"] = runnerId, ["channel"] = "dev", ["connection"] = "source" });
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.Status);
        Assert.Equal("This needs the admin role.", (string?)notAdmin.Body!["error"]);
        // the Workspace server decides who reads the project: Pat administers the runner and reads another project only
        var pat = SignedInThroughHome("pat", "Pat", new() { [$"server:{workspaceId}/sales"] = ["viewer"], [$"server:{runnerId}"] = ["admin"] });
        var unreadable = await As(pat, HttpMethod.Post, path, new JsonObject { ["runner"] = runnerId, ["channel"] = "dev", ["connection"] = "source" });
        Assert.Equal(HttpStatusCode.Forbidden, unreadable.Status);
        Assert.Contains("This needs the viewer role", (string?)unreadable.Body!["error"]);

        // each test the runner answered is audited under the person, on the runner
        var audited = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeployRuns.BindingTestVerb));
        Assert.Equal([AuditEvent.Ok, AuditEvent.Failed], audited.Where(a => a.Entry.ActorId == personId).OrderBy(a => a.Entry.Outcome == AuditEvent.Ok ? 0 : 1).Select(a => a.Entry.Outcome));
        Assert.Contains(audited, a => a.Entry.ActorId != personId && a.Entry.Outcome == AuditEvent.Refused && a.Entry.TargetId == runnerId);
    }

    // ---- the runs of what a deploy put on the runner (Design: docs/publishing.md#runs-of-a-deployed-build) ----

    /// <summary>What each task records while it runs on the runner, as the real ones do: provision its report of statements, load a pass of a mapping, contract its plan (dropping one column) or, approved, what it ran.</summary>
    private static IReadOnlyList<(string Step, string Result)> StepsOf(DbDataKit.Runner.Execution.TaskRunRequest request) => request.Task.Task switch
    {
        "provision" => [("provision-applied", new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["title"] = $"Create the table of {request.Inputs.GetValueOrDefault("mapping")}", ["succeeded"] = true }) }.ToJsonString())],
        // the package risky finds an error on its database, any other a warning
        "preflight" => [(DbDataKit.Runner.Preflight.StepName, DbDataKit.Runner.Preflight.Document("status", request.Task.Package == "risky"
            ? [new DbDataKit.Runner.PreflightFinding(DbDataKit.Runner.Preflight.Error, "The deployed structure differs from the project: the table orders has no column region.", "source")]
            : [new DbDataKit.Runner.PreflightFinding(DbDataKit.Runner.Preflight.Warning, "The refresh plan nightly has never run on this connection.", "source")]).ToJsonString())],
        "load" => [("pass-record", new JsonObject { ["mapping"] = "orders", ["kind"] = "Primary", ["status"] = "Succeeded", ["rowsRead"] = 3, ["rowsWritten"] = 3 }.ToJsonString())],
        "contract" => [("contract-report", new JsonObject
        {
            ["model"] = "territories", ["schema"] = request.Inputs.GetValueOrDefault("schema") ?? "territories", ["engine"] = "sqlserver", ["hash"] = "c0ffee11", ["dryRun"] = string.IsNullOrEmpty(request.Inputs.GetValueOrDefault("approve")),
            ["applied"] = !string.IsNullOrEmpty(request.Inputs.GetValueOrDefault("approve")), ["blocked"] = new JsonArray(), ["notes"] = new JsonArray(),
            ["steps"] = new JsonArray(new JsonObject { ["object"] = "territory.region", ["description"] = "drop the column region of territory", ["text"] = "ALTER TABLE territory DROP COLUMN region", ["requiresEveryRowToHaveAValue"] = false }),
            ["dropped"] = new JsonArray(new JsonObject { ["description"] = "drop the column region of territory", ["columnId"] = "mssql://mdm/territories.territory/region", ["datasetId"] = "mssql://mdm/territories.territory" }),
        }.ToJsonString())],
        _ => [],
    };

    /// <summary>A value of a row the task preview answers: the console passes it on and keeps it nowhere.</summary>
    private const string RowValue = "row-value-6620";

    /// <summary>What a task answers to the runner's memory, as the Sync set's preview does: its first rows.</summary>
    private void Answer(DbDataKit.Runner.Execution.TaskRunRequest request, Guid run)
    {
        if (request.Task.Task == "preview")
            runner.Host.Answers!.Put(run, new JsonObject { ["columns"] = new JsonArray("id", "note"), ["rows"] = new JsonArray(new JsonArray(1, RowValue)), ["more"] = false }.ToJsonString());
    }

    /// <summary>The run <paramref name="runId"/> read back through the console until it ended, with the steps named <paramref name="step"/>. A run still queued is not on the runner's record yet, and is read again, as the screens do.</summary>
    private async Task<JsonNode> RunThroughConsoleAsync(string deploy, string runId, string step)
    {
        for (var i = 0; i < 300; i++)
        {
            var read = await As(HttpMethod.Get, $"/api/deploys/{deploy}/runs/{runId}?step={step}");
            if (read.Status == HttpStatusCode.NotFound && i < 100) { await Task.Delay(100); continue; }
            Assert.True(read.Status == HttpStatusCode.OK, read.Body?.ToJsonString());
            if (read.Body!["run"]!["finished"] is not null) return read.Body["run"]!;
            await Task.Delay(100);
        }
        throw new TimeoutException("the run did not end");
    }

    [Fact]
    public async Task A_sync_build_the_console_deployed_runs_its_provision_and_its_replication_through_the_console_and_the_workspace_server_is_never_asked()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        var staged = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["bind"] = new JsonObject { ["source"] = "erp" } });
        Assert.True(staged.Status == HttpStatusCode.OK, staged.Body?.ToJsonString());

        // the Provisioning tab finds the version in the console's record of the draft's deploys: the package's tasks are there too
        var listed = (await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=orders&workspace={draft}")).Body!["deploys"]!.AsArray();
        var record = Assert.Single(listed)!;
        var deploy = (string)record["id"]!;
        Assert.Equal(["load", "preview", "provision"], record["tasks"]!.AsArray().Select(t => (string?)t).Order());

        // Apply on the Provisioning tab: the manual task provision of the staged version, run for the person, its report read back through the console
        var applied = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "provision", ["inputs"] = new JsonObject { ["replication"] = "orders", ["mapping"] = "orders", ["action"] = "create" } });
        Assert.True(applied.Status == HttpStatusCode.Accepted, applied.Body?.ToJsonString());
        Assert.Equal(("provision", "orders", "dev", "1", runnerId), ((string?)applied.Body!["run"]!["task"], (string?)applied.Body["run"]!["package"], (string?)applied.Body["run"]!["channel"], (string?)applied.Body["run"]!["version"], (string?)applied.Body["run"]!["instance"]));
        var provision = await RunThroughConsoleAsync(deploy, (string)applied.Body["run"]!["run_id"]!, "provision-applied");
        Assert.Equal("succeeded", (string?)provision["outcome"]);
        Assert.Equal("Create the table of orders", (string?)provision["steps"]![0]!["result"]!["steps"]![0]!["title"]);
        var asked = tasks.Requests.Single(r => r.Task.Task == "provision");
        Assert.Equal(("1", "orders", "create"), (asked.PackageVersion, asked.Inputs["mapping"], asked.Inputs["action"]));
        Assert.Contains("Ada Admin", asked.RequestedBy);

        // Run now on the Runs tab runs the version the channel serves: the build is published served, then the replication's task runs and its pass comes back
        var served = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["serve"] = true });
        Assert.True(served.Status == HttpStatusCode.OK, served.Body?.ToJsonString());
        var newest = (string)(await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=orders&workspace={draft}")).Body!["deploys"]![0]!["id"]!;
        var ran = await As(HttpMethod.Post, $"/api/deploys/{newest}/runs", new JsonObject { ["task"] = "load", ["served"] = true, ["inputs"] = new JsonObject { ["run_all"] = "true" } });
        Assert.True(ran.Status == HttpStatusCode.Accepted, ran.Body?.ToJsonString());
        Assert.Equal("served", (string?)ran.Body!["run"]!["version"]);
        var load = await RunThroughConsoleAsync(newest, (string)ran.Body["run"]!["run_id"]!, "pass-record");
        Assert.Equal("succeeded", (string?)load["outcome"]);
        Assert.Equal(("orders", "Succeeded", 3), ((string?)load["steps"]![0]!["result"]!["mapping"], (string?)load["steps"]![0]!["result"]!["status"], (int)load["steps"]![0]!["result"]!["rowsWritten"]!));

        // Test of a connection: the runner tries the login bound to the slot, and says how far it got (it holds no connection erp, so not far); no value comes back
        var tested = await As(HttpMethod.Post, $"/api/deploys/{deploy}/binding-test", new JsonObject { ["slot"] = "source" });
        Assert.True(tested.Status == HttpStatusCode.OK, $"{tested.Status} {tested.Body?.ToJsonString()}");
        Assert.Equal(("source", "orders", "dev"), ((string?)tested.Body!["test"]!["slot"], (string?)tested.Body["test"]!["package"], (string?)tested.Body["test"]!["channel"]));
        Assert.False((bool)tested.Body["test"]!["ok"]!);

        // each is audited under the person, on the runner
        var audited = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeployRuns.RunVerb));
        Assert.Equal(2, audited.Count(a => a.Entry.ActorId == personId && a.Entry.Outcome == AuditEvent.Ok && a.Entry.TargetId == runnerId));
        Assert.NotEmpty(console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeployRuns.BindingTestVerb)));

        // the Workspace server knows nothing of where the build went: it lists no target and runs no task (the relay passes neither call)
        Assert.Equal(HttpStatusCode.Forbidden, (await Workspace(HttpMethod.Get, "/publish-targets")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Workspace(HttpMethod.Post, $"/projects/orders/workspaces/{draft}/builds/{build}/run", new JsonObject { ["task"] = "provision" })).Status);
    }

    [Fact]
    public async Task A_preview_started_through_the_console_answers_its_rows_in_the_call_once_and_nothing_of_them_is_kept()
    {
        // the Designer's Preview rows (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): the console starts the task for the person, waits for its answer and passes it on
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        var staged = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["bind"] = new JsonObject { ["source"] = "erp" } });
        Assert.True(staged.Status == HttpStatusCode.OK, staged.Body?.ToJsonString());
        var deploy = (string)(await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=orders&workspace={draft}")).Body!["deploys"]![0]!["id"]!;

        var previewed = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "preview", ["answer"] = true, ["inputs"] = new JsonObject { ["replication"] = "orders", ["mapping"] = "orders" } });
        Assert.True(previewed.Status == HttpStatusCode.OK, previewed.Body?.ToJsonString());
        Assert.Equal(("done", "dev", false), ((string?)previewed.Body!["state"], (string?)previewed.Body["channel"], (bool)previewed.Body["production"]!));
        Assert.Equal(RowValue, (string?)previewed.Body["answer"]!["rows"]![0]![1]);
        var run = (string)previewed.Body["run_id"]!;
        Assert.Equal($"/servers/{runnerId}/runs?run={run}", (string?)previewed.Body["link"]);
        Assert.Contains("Ada Admin", tasks.Requests.Single(r => r.Task.Task == "preview").RequestedBy);

        // handed out once: asked again, the runner says it is gone
        var again = await As(HttpMethod.Get, $"/api/deploys/{deploy}/runs/{run}/answer");
        Assert.Equal(HttpStatusCode.Gone, again.Status);
        Assert.Equal("gone", (string?)again.Body!["state"]);

        // kept nowhere the console keeps anything: its audit names the run only, and the deploy's record is as it was
        Assert.DoesNotContain(console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Limit: 1000)), a => (a.Entry.Detail ?? "").Contains(RowValue, StringComparison.Ordinal));
        Assert.DoesNotContain(RowValue, (await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=orders&workspace={draft}")).Body!.ToJsonString(), StringComparison.Ordinal);
    }

    // ---- the preflight before a version is served in production (Design: docs/publishing.md#preflight-before-activation) ----

    [Fact]
    public async Task Deploying_a_build_to_prod_runs_its_preflight_on_the_staged_version_before_activating_it_and_an_error_stops_activation_until_the_person_confirms()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatabuild is a shell script");
        var prod = new JsonObject { ["channel"] = "prod", ["serve"] = true, ["bind"] = new JsonObject { ["source"] = "erp" } };

        // a preflight with a warning only: the staged version is checked, then activated, and the record keeps the run and its link
        var (cleanDraft, cleanBuild) = await BuildAsync("clean", "build", new Dictionary<string, string> { ["preflight"] = "yes" });
        var clean = await DeployAsync("clean", cleanDraft, cleanBuild, prod);
        Assert.True(clean.Status == HttpStatusCode.OK, clean.Body?.ToJsonString());
        var cleanPreflight = clean.Body!["deployed"]!["preflight"]!;
        Assert.Equal(("serve", "activated", 0, 1), ((string?)clean.Body["deployed"]!["mode"], (string?)cleanPreflight["activation"], (int)cleanPreflight["errors"]!, (int)cleanPreflight["warnings"]!));
        Assert.StartsWith($"/servers/{runnerId}/runs?run=", (string?)cleanPreflight["link"]);
        var checkedFirst = tasks.Requests.Single(r => r.Task.Package == "clean" && r.Task.Task == DbDataKit.Runner.Preflight.TaskName);
        Assert.Equal(("1", "prod"), (checkedFirst.PackageVersion, checkedFirst.Task.Channel));
        Assert.Equal(1, runner.Publishing.Publisher.Head("integration", "clean", "prod")!.Active);
        Assert.Contains(runner.Publishing.Publisher.EventsOf("integration", "clean"), e => e.Action == "stage" && e.Version == 1);
        Assert.Contains(runner.Publishing.Publisher.EventsOf("integration", "clean"), e => e.Action == "activate" && e.ActorId == personId && e.Note!.Contains("found no error", StringComparison.Ordinal));
        Assert.Equal("serve", (string?)clean.Body["deploy"]!["mode"]);

        // an error: the version stays staged and held, the runner does not activate it by itself, and the answer says what was found, with the run's link
        var (riskyDraft, riskyBuild) = await BuildAsync("risky", "build", new Dictionary<string, string> { ["preflight"] = "yes" });
        var risky = await DeployAsync("risky", riskyDraft, riskyBuild, prod);
        Assert.True(risky.Status == HttpStatusCode.Conflict, risky.Body?.ToJsonString());
        Assert.Contains("found 1 error, so it was not activated in prod", (string?)risky.Body!["error"]);
        Assert.Contains("the table orders has no column region", (string?)risky.Body["error"]);
        var riskyPreflight = risky.Body["deploy"]!["preflight"]!;
        Assert.Equal(("refused", 1), ((string?)riskyPreflight["activation"], (int)riskyPreflight["errors"]!));
        Assert.Equal(("error", "source"), ((string?)riskyPreflight["findings"]![0]!["severity"], (string?)riskyPreflight["findings"]![0]!["about"]));
        await Task.Delay(TimeSpan.FromSeconds(1));   // several passes of the runner's staging follower
        var held = runner.Publishing.Publisher.Head("integration", "risky", "prod")!;
        Assert.Equal((null, 1), (held.Active, held.Staged));
        Assert.NotNull(runner.Publishing.Publisher.HoldOf("integration", "risky", "prod"));
        var deploy = (string)risky.Body["deploy"]!["id"]!;

        // activation without the confirmation is refused again; with it, the runner activates the version and keeps the confirmation in its record
        var unconfirmed = await As(HttpMethod.Post, $"/api/deploys/{deploy}/activate", new JsonObject());
        Assert.True(unconfirmed.Status == HttpStatusCode.Conflict, unconfirmed.Body?.ToJsonString());
        Assert.Null(runner.Publishing.Publisher.Head("integration", "risky", "prod")!.Active);
        var confirmed = await As(HttpMethod.Post, $"/api/deploys/{deploy}/activate", new JsonObject { ["confirm"] = true });
        Assert.True(confirmed.Status == HttpStatusCode.OK, confirmed.Body?.ToJsonString());
        Assert.Equal(("activated", 1), ((string?)confirmed.Body!["activation"], (int)confirmed.Body["active"]!));
        Assert.Equal(1, runner.Publishing.Publisher.Head("integration", "risky", "prod")!.Active);
        Assert.Null(runner.Publishing.Publisher.HoldOf("integration", "risky", "prod"));
        var activated = runner.Publishing.Publisher.EventsOf("integration", "risky").Single(e => e.Action == "activate");
        Assert.Equal(personId, activated.ActorId);
        Assert.Contains("Ada Admin confirmed the activation through the console although preflight run", activated.Note);
        Assert.Contains("the table orders has no column region", activated.Note);
        var kept = (await As(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}&project=risky")).Body!["deploys"]![0]!;
        Assert.Equal(("serve", "activated", personId), ((string?)kept["mode"], (string?)kept["preflight"]!["activation"], (string?)kept["preflight"]!["confirmed_by"]));
        var audit = console.Services.GetRequiredService<AuditLog>();
        Assert.Single(audit.Read(new AuditQuery(Verb: ConsoleDeployPreflight.ConfirmVerb)));
        Assert.Contains(audit.Read(new AuditQuery(Verb: ConsoleDeployPreflight.ActivateVerb)), a => a.Entry.Outcome == AuditEvent.Refused);
        Assert.Equal(HttpStatusCode.Conflict, (await As(HttpMethod.Post, $"/api/deploys/{deploy}/activate", new JsonObject { ["confirm"] = true })).Status);

        // Re-check on the Runs tab: the preflight of the version the channel serves, started through the console, its findings read back the same way
        var recheck = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = DbDataKit.Runner.Preflight.TaskName, ["served"] = true });
        Assert.True(recheck.Status == HttpStatusCode.Accepted, recheck.Body?.ToJsonString());
        var read = await RunThroughConsoleAsync(deploy, (string)recheck.Body!["run"]!["run_id"]!, DbDataKit.Runner.Preflight.StepName);
        Assert.Equal("error", (string?)DbDataKit.Runner.Preflight.Findings(read["steps"]!.AsArray().Select(step => step!["result"])).Single().Severity);

        // the newest check of the channel, as a Designer's card reads it: the Re-check, newer than the preflight before activation, with its findings and link
        var newest = await As(HttpMethod.Get, $"/api/deploys/{deploy}/runs?task={DbDataKit.Runner.Preflight.TaskName}&step={DbDataKit.Runner.Preflight.StepName}");
        Assert.True(newest.Status == HttpStatusCode.OK, newest.Body?.ToJsonString());
        Assert.Equal(((string?)recheck.Body["run"]!["run_id"], "prod", "risky"), ((string?)newest.Body!["run"]!["id"], (string?)newest.Body["channel"], (string?)newest.Body["package"]));
        Assert.Equal($"/servers/{runnerId}/runs?run={(string?)recheck.Body["run"]!["run_id"]}", (string?)newest.Body["link"]);
        Assert.Equal("error", (string?)DbDataKit.Runner.Preflight.Findings(newest.Body["run"]!["steps"]!.AsArray().Select(step => step!["result"])).Single().Severity);
        var never = await As(HttpMethod.Get, $"/api/deploys/{deploy}/runs?task=status");
        Assert.True(never.Status == HttpStatusCode.OK, never.Body?.ToJsonString());
        Assert.Null(never.Body!["run"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await As(HttpMethod.Get, $"/api/deploys/{deploy}/runs")).Status);
    }

    [Fact]
    public void A_deploy_and_an_activation_are_not_cut_off_by_the_hosts_request_timeout_since_a_deploy_to_prod_waits_for_its_preflight()
    {
        // the host's default request timeout (100 s) is shorter than the preflight wait (two minutes): a deploy cut off by it is cancelled midway and answers 504 with no body
        Assert.True(console.Services.GetRequiredService<ConsoleDeploys>().PreflightWait > TimeSpan.FromSeconds(100));
        var endpoints = console.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();
        foreach (var route in new[] { "api/deploys", "api/deploys/{id}/activate" })
        {
            var endpoint = Assert.Single(endpoints, e => e.RoutePattern.RawText == route && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
            Assert.NotNull(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Timeouts.DisableRequestTimeoutAttribute>());
        }
    }

    [Fact]
    public async Task A_package_with_no_preflight_is_served_in_prod_as_before_and_the_deploy_says_nothing_was_checked()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        var served = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "prod", ["serve"] = true, ["bind"] = new JsonObject { ["source"] = "erp" } });
        Assert.True(served.Status == HttpStatusCode.OK, served.Body?.ToJsonString());
        Assert.Equal("serve", (string?)served.Body!["deployed"]!["mode"]);
        Assert.False((bool)served.Body["deployed"]!["preflight"]!["declared"]!);
        Assert.Contains(served.Body["steps"]!.AsArray(), s => (string?)s == "The package declares no preflight task, so nothing is checked before it is served in prod.");
        Assert.Equal(1, runner.Publishing.Publisher.Head("integration", "orders", "prod")!.Active);
        Assert.DoesNotContain(tasks.Requests, r => r.Task.Task == DbDataKit.Runner.Preflight.TaskName);
    }

    [Fact]
    public async Task A_run_is_refused_in_words_for_a_deploy_that_put_nothing_on_a_runner_and_by_the_runner_for_a_person_who_may_not_operate_there()
    {
        if (tools is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        var (draft, build) = await BuildAsync("orders", "sync");
        var refused = await DeployAsync("orders", draft, build);   // no channel: refused, and kept
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        var deploy = (string)refused.Body!["deploy"]!["id"]!;

        var run = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "provision" });
        Assert.Equal(HttpStatusCode.Conflict, run.Status);
        Assert.StartsWith("This deploy was refused", (string?)run.Body!["error"]);
        Assert.Equal(HttpStatusCode.NotFound, (await As(HttpMethod.Post, "/api/deploys/no-such-deploy/runs", new JsonObject { ["task"] = "provision" })).Status);

        var staged = await DeployAsync("orders", draft, build, new JsonObject { ["channel"] = "dev", ["bind"] = new JsonObject { ["source"] = "erp" } });
        Assert.True(staged.Status == HttpStatusCode.OK, staged.Body?.ToJsonString());
        var id = (string)staged.Body!["deploy"]!["id"]!;
        var viewer = console.Services.GetRequiredService<ISessionStore>().Create(console.Services.GetRequiredService<IUserAccounts>().CreateUser("Vic Viewer", null, UserRole.Viewer).Id).Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/deploys/{id}/runs") { Content = new StringContent(new JsonObject { ["task"] = "provision" }.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={viewer}");
        using var response = await http.SendAsync(request);
        // Vic may read the project (a viewer of this console reads every server), so the Workspace server lets the call through; the runner asks for its operator role, which Vic does not hold
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("needs the operator role on Runner one", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(tasks.Requests, r => r.Task.Package == "orders");
    }

    [Fact]
    public async Task A_store_models_contract_is_read_then_run_as_approved_through_the_console_after_the_gateways_it_manages_were_asked()
    {
        // a model's package runs on the runtime dbdatastore-runtime: a stand-in of it is served in dev (its tasks run in this process, so its files are never started)
        var runtime = Directory.CreateDirectory(Path.Combine(directory, "store-runtime")).FullName;
        Directory.CreateDirectory(Path.Combine(runtime, "files", "bin"));
        File.WriteAllText(Path.Combine(runtime, "files", "bin", "dbdatastore.dll"), "a stand-in");
        File.WriteAllText(Path.Combine(runtime, "package.yaml"), $"schema: package/1\nname: dbdatastore-runtime\nruntime: true\nversion: \"1.0.0\"\nplatform: {DbDataKit.Runner.RuntimePlatforms.Current}\nlaunch: {{ kind: executable, command: dotnet, args: [\"{{runtime}}/bin/dbdatastore.dll\", \"--runner\"] }}\nbuilt_against: {{ dotnet: \"10.0\", kit: \"1\", protocol: dbdatakit.runner.v1 }}\n");
        DbDataKit.Runner.Api.RunnerPackageInstaller.Install(runner.State, Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), "dev", "test");

        var (draft, build) = await BuildAsync("territories", "store");
        var staged = await DeployAsync("territories", draft, build, new JsonObject { ["channel"] = "dev", ["model"] = "territories", ["bind"] = new JsonObject { ["storage"] = "mdm" } });
        Assert.True(staged.Status == HttpStatusCode.OK, staged.Body?.ToJsonString());
        var deploy = (string)staged.Body!["deploy"]!["id"]!;

        // the plan: contract without an approval, its report read back with its hash
        var planned = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "contract", ["inputs"] = new JsonObject { ["schema"] = "mdm_territories" } });
        Assert.True(planned.Status == HttpStatusCode.Accepted, planned.Body?.ToJsonString());
        var planRun = (string)planned.Body!["run"]!["run_id"]!;
        var plan = await RunThroughConsoleAsync(deploy, planRun, "contract-report");
        Assert.True(plan["steps"]!.AsArray().Count == 1, plan.ToJsonString());
        Assert.Equal("c0ffee11", (string?)plan["steps"]![0]!["result"]!["hash"]);

        // an approval is never a plain run: it goes through .../contract
        var plain = await As(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject { ["task"] = "contract", ["inputs"] = new JsonObject { ["approve"] = "c0ffee11" } });
        Assert.Equal(HttpStatusCode.Conflict, plain.Status);
        Assert.Contains("through .../contract", (string?)plain.Body!["error"]);
        // a hash the plan did not have
        var wrong = await As(HttpMethod.Post, $"/api/deploys/{deploy}/contract", new JsonObject { ["planRun"] = planRun, ["approve"] = "deadbeef" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.Status);
        Assert.Contains("did not read a plan with the hash deadbeef", (string?)wrong.Body!["error"]);

        // a server the console manages that does not answer may be a Gateway whose consumers read the column: the step is refused naming it, until the person allows it
        console.Services.GetRequiredService<ConsoleInstances>().Registry.Add(new PairedServer(new Uri("https://127.0.0.1:1"), "gateway-gone", new string('0', 64), 1, "Gateway gone", ServerRole.Admin));
        var blocked = await As(HttpMethod.Post, $"/api/deploys/{deploy}/contract", new JsonObject { ["planRun"] = planRun, ["approve"] = "c0ffee11" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("'Gateway gone'", (string?)blocked.Body!["error"]);
        Assert.Contains("gateway:Gateway gone", (string?)blocked.Body["error"]);
        Assert.DoesNotContain(tasks.Requests, r => r.Task.Task == "contract" && !string.IsNullOrEmpty(r.Inputs.GetValueOrDefault("approve")));

        var contracted = await As(HttpMethod.Post, $"/api/deploys/{deploy}/contract", new JsonObject { ["planRun"] = planRun, ["approve"] = "c0ffee11", ["allowConsumer"] = new JsonArray("gateway:Gateway gone") });
        Assert.True(contracted.Status == HttpStatusCode.Accepted, contracted.Body?.ToJsonString());
        Assert.Equal(1, (int)contracted.Body!["checked"]!);
        var ran = await RunThroughConsoleAsync(deploy, (string)contracted.Body["run"]!["run_id"]!, "contract-report");
        Assert.True((bool)ran["steps"]![0]!["result"]!["applied"]!);
        var asked = tasks.Requests.Single(r => r.Task.Task == "contract" && !string.IsNullOrEmpty(r.Inputs.GetValueOrDefault("approve")));
        Assert.Equal(("c0ffee11", "mdm_territories"), (asked.Inputs["approve"], asked.Inputs["schema"]));
        var allowances = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeployRuns.AllowConsumerVerb));
        Assert.Contains(allowances, a => a.Entry.ActorId == personId && a.Entry.Detail!.Contains("gateway:Gateway gone"));
    }

    [Fact]
    public async Task What_a_workspace_key_published_is_handed_over_on_a_gateway_only_and_a_refused_step_is_audited()
    {
        var unnamed = await As(HttpMethod.Post, "/api/deploys/handover", new JsonObject());
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.Status);
        Assert.Contains("Name the Gateway (\"gateway\")", (string?)unnamed.Body!["error"]);

        var unknown = await As(HttpMethod.Post, "/api/deploys/handover", new JsonObject { ["gateway"] = "nowhere" });
        Assert.Equal((HttpStatusCode.NotFound, "No server 'nowhere' is registered."), (unknown.Status, (string?)unknown.Body!["error"]));

        var onRunner = await As(HttpMethod.Post, "/api/deploys/handover", new JsonObject { ["gateway"] = runnerId });
        Assert.Equal(HttpStatusCode.BadRequest, onRunner.Status);
        Assert.Contains("what a publisher key published is handed over on a Gateway", (string?)onRunner.Body!["error"]);
        var audited = console.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: ConsoleDeploys.HandoverVerb));
        Assert.Contains(audited, a => a.Entry.ActorId == personId && a.Entry.Outcome == "refused" && a.Entry.TargetId == runnerId);
    }
}
