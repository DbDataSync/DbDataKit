using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;
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
    private HttpClient http = null!;
    private string session = null!;
    private string personId = null!;
    private string workspaceId = null!;
    private string runnerId = null!;
    private string? tools;

    /// <summary>
    /// The stand-in of <c>dbdatasync</c> and <c>dbdatabuild</c> for the verbs the Workspace runs to check and package a project: a draft with a file named <c>broken</c> is refused in the program's way (the file, on the error output, exit
    /// 1); otherwise the package is a <c>package/1</c> manifest whose task reads the connection slot <c>source</c>, with the project's files under <c>files/</c>.
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
          printf 'schema: package/1\nname: %s\nlaunch: { kind: assembly, assembly: bin/Orders.dll }\nbuilt_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }\ntasks:\n  - name: load\n    entry: { type: Orders.Load }\n    triggers:\n      - manual: true\n    connections:\n      - { name: source, kind: sqlserver }\n' "$pkg" > "$out/package.yaml"
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

        runner = await PairedRunner.StartAsync();
        var workspaceDirectory = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspaceDirectory);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0"))
        {
            SyncExecutable = tools is null ? null : Path.Combine(tools, "dbdatasync"),
            BuildExecutable = tools is null ? null : Path.Combine(tools, "dbdatabuild"),
        }, WorkspaceServer.Certificate(workspaceDirectory), _ => { });
        await workspace.StartAsync();

        var consoleDirectory = Path.Combine(directory, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
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

    private async Task<(HttpStatusCode Status, JsonNode? Body)> As(HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
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

    private Task<(HttpStatusCode Status, JsonNode? Body)> DeployAsync(string project, string draft, string build, JsonObject? extra = null)
    {
        var body = new JsonObject { ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = build, ["target"] = runnerId };
        foreach (var (key, value) in extra ?? []) body[key] = value?.DeepClone();
        return As(HttpMethod.Post, "/api/deploys", body);
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

        // the Workspace server sent nothing: it holds no publisher key and no target, and knows nothing of the deploy
        Assert.Empty((await Workspace(HttpMethod.Get, "/publish-targets")).Body!["targets"]!.AsArray());
        Assert.Empty((await Workspace(HttpMethod.Get, $"/projects/orders/workspaces/{draft}/builds/{build}")).Body!["publishes"]!.AsArray());
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
    public async Task Only_a_person_who_may_approve_on_the_workspace_server_deploys_from_it()
    {
        var viewer = console.Services.GetRequiredService<ISessionStore>().Create(console.Services.GetRequiredService<IUserAccounts>().CreateUser("Vic Viewer", null, UserRole.Viewer).Id).Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/deploys")
        {
            Content = new StringContent(new JsonObject { ["workspaceServer"] = workspaceId, ["project"] = "orders", ["workspace"] = "w", ["build"] = Guid.NewGuid().ToString(), ["target"] = runnerId }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={viewer}");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("needs the approver role on Workspace", await response.Content.ReadAsStringAsync());
    }
}
