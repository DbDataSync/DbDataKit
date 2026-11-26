using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataGateway.Tests.Support;
using DbDataKit.Apps.Bundling;
using DbDataKit.Authoring.Server;
using DbDataKit.Deploy;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// The control service deploys builds to a Gateway (Design: docs/publishing.md#deploying-a-build-from-the-control-service), with the Gateway the program it is: a Workspace server and a console in this process, each paired only with
/// the console. The Workspace builds an app with Bun from the vendored package store, a Build project's semantic models (the <c>dbdatabuild</c> it runs is a stand-in that writes the models the way the real one does) and a Store
/// project's models (built in process). The console reads each build from the Workspace as the signed-in person and deploys it to the Gateway as that person: the Gateway records the person, and for the models the console's
/// signature and the build record; the app goes over the Gateway's app calls, as a version and as an instance's preview. The console's deploy record lists each.
/// </summary>
public sealed class GatewayDeployTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-gateway-deploy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private WebApplication console = null!;
    private WebApplication workspace = null!;
    private HttpClient http = null!;
    private string personId = null!;
    private string gatewayId = null!;
    private string workspaceId = null!;
    private string? tool;

    /// <summary>
    /// The stand-in <c>dbdatabuild</c>: <c>project create starter</c> writes a project file, the check (<c>project show</c>) finds nothing, and <c>project publish --to gateway --out</c> writes what the real one writes for a Gateway:
    /// <c>gateway.json</c> (<c>gateway-publish/1</c>), each <c>semantic/*.yml</c> as <c>models/&lt;name&gt;.catalog.json</c> over the data source <c>warehouse</c>, and that data source's document, which names no login.
    /// </summary>
    private const string BuildTool = """
        #!/bin/bash
        verb="$1 $2"; out=""; root="."; to="runner"
        if [ "$1 $2 $3" = "project create starter" ]; then mkdir -p "$4"; printf 'schema: dbdatabuild.project/2\ntitle: starter\n' > "$4/dbdatabuild.yml"; echo "{}"; exit 0; fi
        while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; --project) root="$2"; shift;; --to) to="$2"; shift;; esac; shift; done
        if [ "$verb" = "project show" ]; then echo '{"diagnostics":[]}'; exit 0; fi
        if [ "$verb" = "project publish" ] && [ "$to" = "gateway" ]; then
          mkdir -p "$out/models" "$out/data-sources"; names=""; sep=""
          for f in "$root"/semantic/*.yml; do n=$(basename "$f" .yml); cp "$f" "$out/models/$n.catalog.json"; names="$names$sep{\"name\":\"$n\",\"file\":\"models/$n.catalog.json\",\"sources\":{\"sqlserver_sales\":\"warehouse\"},\"data_sources\":[\"warehouse\"]}"; sep=","; done
          printf "schema: connection/1\nname: warehouse\nengine: duckdb\nconnection_string: 'Data Source=:memory:'\n" > "$out/data-sources/warehouse.yaml"
          echo "{\"schema\":\"gateway-publish/1\",\"models\":[$names],\"data_sources\":[{\"name\":\"warehouse\",\"engine\":\"duckdb\",\"file\":\"data-sources/warehouse.yaml\"}]}" > "$out/gateway.json"
          echo '{"data":{"models":[],"data_sources":["warehouse"]}}'; exit 0
        fi
        echo "unknown verb $verb" >&2; exit 2
        """;

    /// <summary>A semantic model whose one table is DuckDB's own list of tables, so the Gateway's check of the mapping reads it on an in-memory data source with no file and no login.</summary>
    private const string Catalog = """
        version: "0.3"
        name: shop
        timezone: UTC
        sources:
          sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
        tables:
          - name: objects
            x_source: sqlserver_sales
            source: information_schema.tables
            columns:
              - {name: table_name, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: table_name}]}}
        dimensions:
          objects:
            attributes:
              name:
                type: string
                bindings:
                  sqlserver_sales: {table: objects, column: table_name}
        measures:
          objects.count:
            type: integer
            source: sqlserver_sales
            agg: count
            of: {table: objects, column: table_name}
            semantics: {grain: [objects.name], rollup: {default: none}, spread: blank, status: governed}
        """;

    /// <summary>A Store model whose tables are on the data source the Build project's deploy made.</summary>
    private const string Model = """
        schemaVersion: 2
        name: territories
        storage: {connection: warehouse}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
              name: {type: VARCHAR(100)}
        """;

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("the stand-in dbdatabuild is a shell script, and the app build's isolation is Linux's");
        var store = AppStoreSupport.Require();
        Directory.CreateDirectory(root);
        tool = Path.Combine(root, "dbdatabuild");
        File.WriteAllText(tool, BuildTool.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // the Gateway, with its management listener, paired with the console only
        var gatewayDirectory = Path.Combine(root, "gateway");
        Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "gateway"));
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\ndefinitionPollSeconds: 1\n");
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        var management = Programs.FreePort();
        var code = Programs.CodeIn(await RealProcess.RunAsync(Programs.Gateway, "server", "pair", "--repo", gatewayDirectory, "--max-role", "admin"));
        var gateway = RealProcess.Start(Programs.Gateway, ["server", "run", $"--Gateway:DataDirectory={gatewayDirectory}", $"--Gateway:Listen=https://127.0.0.1:{Programs.FreePort()}", $"--Gateway:Public:Listen=https://127.0.0.1:{Programs.FreePort()}",
            "--Gateway:Management:Enabled=true", $"--Gateway:Management:Listen=https://127.0.0.1:{management}", "--Gateway:Management:LocalChannel=false"]);
        started.Add(gateway);
        var gatewayUrl = new Uri($"https://127.0.0.1:{management}");
        await gateway.WaitUntilAsync(new Uri(gatewayUrl, "/health/live"), TimeSpan.FromSeconds(90));

        // the Workspace server: Bun from the vendored store for an app, the stand-in for a Build project
        var workspaceDirectory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspaceDirectory);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0"))
        {
            BuildExecutable = tool,
            Apps = new WorkspaceAppSettings(store, null, null, NetworkIsolator.IsAvailable ? NetworkIsolation.Required : NetworkIsolation.IfAvailable, 180),
        }, WorkspaceServer.Certificate(workspaceDirectory), _ => { });
        await workspace.StartAsync();

        var consoleDirectory = Path.Combine(root, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()), Timeout = TimeSpan.FromMinutes(4) };
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;

        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        gatewayId = (await instances.PairAsync(gatewayUrl, code, "Edge gateway")).InstanceId;
        var workspaceCode = WorkspaceState.Pairing(workspaceDirectory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), workspaceCode.Code, "Workspace", default)).InstanceId;
        // the console's publisher key in the Gateway's trust store, as the console's own pass puts it there once the Gateway is paired
        var trusted = await console.Services.GetRequiredService<ConsoleTrust>().EnsureAsync(instances.Registry.Find(gatewayId)!);
        Assert.True(trusted.Done, trusted.Problem);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
        foreach (var program in started) await program.DisposeAsync();
        try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AsPerson(HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? parsed = null;
        try { parsed = text.Length > 0 ? JsonNode.Parse(text) : null; }
        catch (System.Text.Json.JsonException) { /* not JSON */ }
        return (response.StatusCode, parsed);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Workspace(HttpMethod method, string path, JsonNode? body = null) => AsPerson(method, $"/relay/{workspaceId}/api/v1{path}", body);

    private Task<(HttpStatusCode Status, JsonNode? Body)> Gateway(HttpMethod method, string path, JsonNode? body = null) => AsPerson(method, $"/relay/{gatewayId}/api/v1{path}", body);

    /// <summary>A project of <paramref name="kind"/> with a draft, its files written, built through the console's relay as the person. Returns the draft and the build.</summary>
    private async Task<(string Draft, string Build)> BuildAsync(string project, string kind, IReadOnlyDictionary<string, string>? files = null)
    {
        var made = await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = project, ["kind"] = kind });
        Assert.True(made.Status == HttpStatusCode.Created, made.Body?.ToJsonString());
        var draft = (string)(await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces", new JsonObject { ["name"] = "draft" })).Body!["id"]!;
        foreach (var (path, text) in files ?? new Dictionary<string, string>())
        {
            var put = await Workspace(HttpMethod.Put, $"/projects/{project}/workspaces/{draft}/files/{path}", new JsonObject { ["content"] = text });
            Assert.True(put.Status is HttpStatusCode.OK or HttpStatusCode.Created, $"{path}: {put.Status} {put.Body}");
        }
        var requested = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/builds");
        Assert.True(requested.Status == HttpStatusCode.Accepted, requested.Body?.ToJsonString());
        var build = (string)requested.Body!["id"]!;
        for (var i = 0; i < 1200; i++)
        {
            var read = (await Workspace(HttpMethod.Get, $"/projects/{project}/workspaces/{draft}/builds/{build}")).Body!;
            var status = (string)read["build"]!["status"]!;
            if (status is "succeeded" or "failed")
            {
                Assert.True(status == "succeeded", read.ToJsonString());
                return (draft, build);
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("the build did not finish");
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> DeployAsync(string project, string draft, string build, JsonObject? extra = null)
    {
        var body = new JsonObject { ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = build, ["target"] = gatewayId };
        foreach (var (key, value) in extra ?? []) body[key] = value?.DeepClone();
        return AsPerson(HttpMethod.Post, "/api/deploys", body);
    }

    /// <summary>A stored version of a definition on the Gateway, read through the relay: who published it (from the list of versions), and its signature, signer and build record.</summary>
    private async Task<(string? PublishedBy, string? Signature, string? Signer, JsonObject? Build)> StoredAsync(string kind, string name, int version)
    {
        var listed = await Gateway(HttpMethod.Get, $"/definitions/{kind}/{name}");
        Assert.True(listed.Status == HttpStatusCode.OK, $"{kind}/{name}: {listed.Status} {listed.Body}");
        var row = listed.Body!["versions"]!.AsArray().Single(v => (int)v!["version"]! == version)!;
        var read = await Gateway(HttpMethod.Get, $"/definitions/{kind}/{name}/versions/{version}");
        Assert.True(read.Status == HttpStatusCode.OK, $"{kind}/{name}/{version}: {read.Status} {read.Body}");
        return ((string?)row["published_by"], (string?)read.Body!["signature"], (string?)read.Body["signer"], BuildRecord.Read((string?)read.Body["build"]));
    }

    [Fact]
    public async Task An_app_a_build_projects_semantic_models_and_a_store_projects_models_are_deployed_to_a_real_gateway_as_the_person_and_the_deploy_record_lists_each()
    {
        var key = await console.Services.GetRequiredService<ConsolePublisherKeyStore>().GetOrCreateAsync();

        // ---- an app, built with Bun from the package store: the Gateway checks it, then stores the version, published by the person
        var (appDraft, appBuild) = await BuildAsync("sales", "app");
        var app = await DeployAsync("sales", appDraft, appBuild, new JsonObject { ["note"] = "The first version" });
        Assert.True(app.Status == HttpStatusCode.OK, $"{app.Status} {app.Body}");
        Assert.Equal(("sales", 1), ((string?)app.Body!["deployed"]!["app"], (int)app.Body["deployed"]!["version"]!));
        var publishedBy = (string?)app.Body["deployed"]!["published_by"];
        Assert.False(string.IsNullOrEmpty(publishedBy));
        var apps = await Gateway(HttpMethod.Get, "/apps");
        var listedApp = apps.Body!["apps"]!.AsArray().Single(a => (string?)a!["name"] == "sales")!;
        Assert.Equal(publishedBy, (string?)listedApp["published_by"]);

        // ---- the same build as a preview of an instance: refused in the Gateway's words while production has no such instance, then sent to the preview channel
        var ghost = await DeployAsync("sales", appDraft, appBuild, new JsonObject { ["previewInstance"] = "ghost" });
        Assert.Equal(HttpStatusCode.Conflict, ghost.Status);
        Assert.Contains("no instance 'ghost' in production", (string?)ghost.Body!["error"]);
        var instance = await Gateway(HttpMethod.Put, "/app-instances/sales", new JsonObject { ["app"] = "sales" });
        Assert.True(instance.Status == HttpStatusCode.OK, $"{instance.Status} {instance.Body}");
        var preview = await DeployAsync("sales", appDraft, appBuild, new JsonObject { ["previewInstance"] = "sales" });
        Assert.True(preview.Status == HttpStatusCode.OK, $"{preview.Status} {preview.Body}");
        Assert.Equal(("preview", "preview", "sales"), ((string?)preview.Body!["deployed"]!["channel"], (string?)preview.Body["deployed"]!["mode"], (string?)preview.Body["deployed"]!["instance"]));
        Assert.Equal("/apps/sales~preview/", (string?)preview.Body["deployed"]!["preview_path"]);
        Assert.Equal(("preview", "sales", "/apps/sales~preview/"), ((string?)preview.Body["deploy"]!["mode"], (string?)preview.Body["deploy"]!["preview"]!["instance"], (string?)preview.Body["deploy"]!["preview"]!["path"]));

        // ---- a Build project's semantic model: its data source document goes first as the console's own, then the model over it; each version is signed with the console's key over the build record, published by the person
        var (shopDraft, shopBuild) = await BuildAsync("shop", "build", new Dictionary<string, string> { ["semantic/shop.yml"] = Catalog });
        var models = await DeployAsync("shop", shopDraft, shopBuild);
        Assert.True(models.Status == HttpStatusCode.OK, $"{models.Status} {models.Body}");
        Assert.Equal("shop", (string?)models.Body!["deployed"]!["models"]![0]!["model"]);
        var semantic = await StoredAsync("semantic-model", "shop", 1);
        Assert.Equal(publishedBy, semantic.PublishedBy);
        Assert.StartsWith("ed25519:", semantic.Signature);
        Assert.Equal(key.Fingerprint, semantic.Signer);
        Assert.Equal((workspaceId, "shop", "build", shopDraft, shopBuild, "semantic-model"),
            ((string?)semantic.Build!["server"], (string?)semantic.Build["project"], (string?)semantic.Build["kind"], (string?)semantic.Build["workspace"], (string?)semantic.Build["build"], (string?)semantic.Build["item"]));
        var dataSource = await StoredAsync("data-source", "warehouse", 1);
        Assert.Equal((publishedBy, key.Fingerprint, shopBuild), (dataSource.PublishedBy, dataSource.Signer, (string?)dataSource.Build!["build"]));

        // ---- a Store project's model, on the data source the console made: the same signature and build record
        var (storeDraft, storeBuild) = await BuildAsync("territories", "store", new Dictionary<string, string> { ["models/territories.yaml"] = Model });
        var entity = await DeployAsync("territories", storeDraft, storeBuild);
        Assert.True(entity.Status == HttpStatusCode.OK, $"{entity.Status} {entity.Body}");
        var stored = await StoredAsync("entity-model", "territories", 1);
        Assert.Equal((publishedBy, key.Fingerprint), (stored.PublishedBy, stored.Signer));
        Assert.StartsWith("ed25519:", stored.Signature);
        Assert.Equal(("territories", "store", storeBuild, "model"), ((string?)stored.Build!["project"], (string?)stored.Build["kind"], (string?)stored.Build["build"], (string?)stored.Build["item"]));

        // ---- the console's record: each deploy, by the person, to the Gateway, with what it put there; the refused preview too
        var listed = (await AsPerson(HttpMethod.Get, $"/api/deploys?workspace_server={workspaceId}")).Body!["deploys"]!.AsArray().Select(d => d!).ToList();
        Assert.All(listed, d => Assert.Equal((personId, gatewayId), ((string?)d["deployed_by"], (string?)d["target"])));
        Assert.Equal(
            [("territories", "model", "ok", "verified"), ("shop", "semantic-model", "ok", "verified"), ("sales", "app", "ok", null), ("sales", "app", "refused", null), ("sales", "app", "ok", null)],
            listed.Select(d => ((string?)d["project"], (string?)d["item"], (string?)d["outcome"], (string?)d["signature"])));
        Assert.Equal(["territories", "shop", "sales", null, "sales"], listed.Select(d => (string?)d["name"]));
        Assert.Equal("preview", (string?)listed[2]["mode"]);

        // ---- the Workspace server sent nothing: it holds no target and knows of no publish from these builds
        Assert.Empty((await Workspace(HttpMethod.Get, "/publish-targets")).Body!["targets"]!.AsArray());
        Assert.Empty((await Workspace(HttpMethod.Get, $"/projects/sales/workspaces/{appDraft}/builds/{appBuild}")).Body!["publishes"]!.AsArray());
    }
}
