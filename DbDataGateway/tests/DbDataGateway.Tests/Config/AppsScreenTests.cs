using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 26, AP24a: the management routes the console's Apps screen calls: reads of apps, versions, instances, navigation sets and approvals (viewer, or publisher), the removal of an app and of an instance within the store's rules,
/// and the revoking of an approval (the approver role, by a user of the identity home who holds it). A person of a console calls them with a token whose roles are theirs; the Gateway decides, never the screen. Each change is audited.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsScreenTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dbgateway-appsscreen-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-apps", ConsoleSigningKey.Generate());
    private WebApplication? app;
    private int managementPort;
    private PairedServer server = null!;

    private string DataDirectory => Path.Combine(root, "data");

    private Uri ManagementUrl => new($"https://127.0.0.1:{managementPort}");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(DataDirectory));
        File.WriteAllText(GatewayConfigFile.PathIn(DataDirectory), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        for (var attempt = 1; ; attempt++)
        {
            managementPort = FreePort();
            app = GatewayHost.Build([
                $"--Gateway:DataDirectory={DataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0",
                "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={managementPort}", "--Gateway:Management:LocalChannel=false",
            ]);
            try { await app.StartAsync(); break; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }
        var files = new PairingFiles(GatewayConfigFile.PathIn(DataDirectory), Path.Combine(DataDirectory, "pairing-codes.json"));
        server = await PairingClient.PairAsync(ManagementUrl, files.Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Approver | ServerRole.Publisher)).Code, console);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(root);
    }

    private StateDatabase State => app!.Services.GetRequiredService<StateDatabase>();

    private DefinitionStore Definitions => new(State);

    private AppInstanceStore Store => new(Definitions);

    /// <summary>A call as the signed-in person Ada, holding exactly these roles on this server.</summary>
    private async Task<(HttpStatusCode Status, JsonNode Body)> CallAsync(ServerRole roles, HttpMethod method, string path, string? body = null)
    {
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(server.InstanceId, "ada-id", "Ada", roles)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static AppPackage Package(string name, string version, string extra = "") =>
        new($"schemaVersion: 1\nname: {name}\nversion: {version}\nentry: index.html\nroles: [viewer]\n{extra}", new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes($"<html>{name} {version}</html>") });

    private const string Models = "models:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    private const string Portal = "schemaVersion: 1\nname: front\nversion: 1.0.0\nkind: portal\nportal:\n  mounts: [{app: acme, path: /sales/}]\n";

    private (string Approval, string Hash) Seed()
    {
        var users = new IdentityUserStore(State);
        var options = GatewayConfiguration.LoadOptions(DataDirectory);
        options.DataDirectory = DataDirectory;
        var serverId = GatewayStoreModule.ServerIdOf(options, State);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Viewer).Id), serverId, ServerRole.Approver);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id), serverId, ServerRole.Viewer);

        Store.PublishApp(Package("notes", "1.0.0", Models), "ada-laptop", note: "first");
        Store.PublishApp(Package("notes", "1.1.0", Models), "ada-laptop", note: "second");
        Store.Save(new AppInstance("acme", "notes", Title: "Acme notes"), "ada-laptop");
        Store.Save(new AppInstance("pinned", "notes", Pin: 1, Bindings: new Dictionary<string, string> { ["territories"] = "default" }), "ada-laptop");
        Store.PublishApp(new AppPackage(Portal, new Dictionary<string, byte[]>()), "ada-laptop");
        Store.Save(new AppInstance("front", "front"), "ada-laptop");
        Store.SaveNavigation(new NavigationSet("main", "Main menu", [new NavigationEntry("acme", "Acme"), new NavigationEntry("pinned")]), "ada-laptop");

        // a trusted app, approved by Grace for these bytes, published by the client that wrote it
        var vault = Package("vault", "1.0.0", "trust: trusted\n");
        var approvals = new AppApprovalStore(Definitions, approvers: new InMemoryApprovers().Add("u-grace", "Grace Hopper", approver: true));
        var approval = approvals.Approve(vault, "ada-laptop", "Grace Hopper", "reviewed");
        Store.PublishApp(vault, "ada-laptop", approvals: approvals);
        Store.Save(new AppInstance("safe", "vault"), "ada-laptop");
        return (approval.Id, approval.ContentHash);
    }

    [Fact]
    public async Task A_viewer_reads_apps_versions_instances_navigation_and_approvals_and_the_answer_says_what_the_caller_may_do()
    {
        var (approvalId, hash) = Seed();

        var apps = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps");
        Assert.Equal(HttpStatusCode.OK, apps.Status);
        var notes = apps.Body["apps"]!.AsArray().Single(a => (string?)a!["name"] == "notes")!;
        Assert.Equal(2, (int)notes["version"]!);
        Assert.Equal([1, 2], notes["versions"]!.AsArray().Select(v => (int)v!).Order().ToArray());
        Assert.Equal(["acme", "pinned"], notes["instances"]!.AsArray().Select(v => (string)v!).Order().ToArray());
        Assert.Equal("trusted", (string?)apps.Body["apps"]!.AsArray().Single(a => (string?)a!["name"] == "vault")!["trust"]);
        // what the viewer may do: nothing but read, and the screen says why its buttons are off
        Assert.False((bool)apps.Body["caller"]!["can_publish"]!);
        Assert.False((bool)apps.Body["caller"]!["can_approve"]!);
        Assert.Equal("Ada", (string?)apps.Body["caller"]!["you"]);

        var detail = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/notes");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal(2, (int)detail.Body["serving"]!);
        var versions = detail.Body["versions"]!.AsArray();
        Assert.Equal([2, 1], versions.Select(v => (int)v!["version"]!).ToArray());              // newest first
        Assert.True((bool)versions[0]!["serving"]!);
        Assert.False((bool)versions[1]!["serving"]!);
        Assert.Equal("ada-laptop", (string?)versions[0]!["published_by"]);
        Assert.Equal("second", (string?)versions[0]!["note"]);
        // the about data (AP18): the models and what it does with each entity, who published it
        var about = versions[0]!["about"]!;
        Assert.Equal("territories", (string?)about["models"]![0]!["model"]);
        Assert.Equal("territory", (string?)about["models"]![0]!["entity"]);
        Assert.Equal("read and write", (string?)about["models"]![0]!["operations"]);
        Assert.Equal("ada-laptop", (string?)about["published_by"]);
        Assert.Null(about["approval"]);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/nothing")).Status);

        // the trusted app's only version carries its approval
        var vault = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/vault")).Body["versions"]![0]!;
        Assert.Equal(approvalId, (string?)vault["approval_id"]);
        Assert.Equal(hash, (string?)vault["hash"]);
        Assert.Equal("Grace Hopper", (string?)vault["about"]!["approval"]!["approved_by"]);

        var instances = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances");
        Assert.Equal(HttpStatusCode.OK, instances.Status);
        var rows = instances.Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.True((bool)rows["acme"]!["follows"]!);
        Assert.Equal(2, (int)rows["acme"]!["app_version"]!);                                      // it follows what the app serves
        Assert.Equal("1.1.0", (string?)rows["acme"]!["manifest_version"]);
        Assert.Equal("/apps/acme/", (string?)rows["acme"]!["path"]);
        Assert.Null(rows["acme"]!["address"]);                                                     // no apps module on this Gateway: no address to open
        Assert.Equal(["front"], rows["acme"]!["mounted_by"]!.AsArray().Select(v => (string)v!).ToArray());
        Assert.Equal(["main"], rows["acme"]!["navigation"]!.AsArray().Select(v => (string)v!).ToArray());
        Assert.False((bool)rows["pinned"]!["follows"]!);
        Assert.Equal(1, (int)rows["pinned"]!["pin"]!);
        Assert.Equal(1, (int)rows["pinned"]!["app_version"]!);                                    // the pin keeps it on version 1 whatever the app serves
        Assert.Equal(2, (int)rows["pinned"]!["serving_version"]!);
        Assert.Equal("default", (string?)rows["pinned"]!["bindings"]!["territories"]);
        Assert.Equal("portal", (string?)rows["front"]!["kind"]);
        Assert.Equal("acme", (string?)rows["front"]!["mounts"]![0]!["instance"]);
        Assert.True((bool)rows["safe"]!["runs_as_asked"]!);
        Assert.Equal(approvalId, (string?)rows["safe"]!["approval_id"]);

        var navigation = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-navigation");
        Assert.Equal(HttpStatusCode.OK, navigation.Status);
        var set = navigation.Body["navigation"]!.AsArray().Single()!;
        Assert.Equal("main", (string?)set["name"]);
        Assert.Equal(["acme", "pinned"], set["entries"]!.AsArray().Select(e => (string)e!["instance"]!).ToArray());

        var approvals = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-approvals");
        Assert.Equal(HttpStatusCode.OK, approvals.Status);
        var approval = approvals.Body["approvals"]!.AsArray().Single()!;
        Assert.Equal(approvalId, (string?)approval["id"]);
        Assert.Equal("vault", (string?)approval["app"]);
        Assert.Equal("Grace Hopper", (string?)approval["approved_by"]);
        Assert.Equal(hash, (string?)approval["hash"]);
        Assert.Equal(1, (int?)approval["published_version"]);
        Assert.False((bool)approval["revoked"]!);

        // an administrator may publish and an approver may approve: the screen's buttons follow
        Assert.True((bool)(await CallAsync(ServerRole.Admin, HttpMethod.Get, "/api/v1/app-approvals")).Body["caller"]!["can_publish"]!);
        var approver = (await CallAsync(ServerRole.Approver, HttpMethod.Get, "/api/v1/app-approvals")).Body["caller"]!;
        Assert.True((bool)approver["can_approve"]!);
        Assert.False((bool)approver["can_publish"]!);
    }

    [Fact]
    public async Task Reads_need_a_role_and_a_change_needs_the_role_it_changes_with()
    {
        Seed();
        // a publisher reads too (a paired Build or Store server, or the command line's `apps list --to`); a pairing with no role at all does not
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Get, "/api/v1/app-instances")).Status);
        foreach (var path in new[] { "/api/v1/apps", "/api/v1/apps/notes", "/api/v1/app-instances", "/api/v1/app-navigation", "/api/v1/app-approvals" })
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.None, HttpMethod.Get, path)).Status);

        // removing is for the publisher and the administrator; a viewer and an approver (who only reads) are refused and nothing is removed
        foreach (var role in new[] { ServerRole.Viewer, ServerRole.Approver, ServerRole.Operator })
        {
            var refused = await CallAsync(role, HttpMethod.Delete, "/api/v1/app-instances/safe");
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Contains("publisher", (string)refused.Body["error"]!);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(role, HttpMethod.Delete, "/api/v1/apps/vault")).Status);
        }
        Assert.NotNull(Store.Get("safe"));

        // revoking is the approver's: an administrator without the role is refused, and says which pairing would do
        var approvalId = new AppApprovalStore(Definitions).All().Single().Id;
        var admin = await CallAsync(ServerRole.Admin, HttpMethod.Post, $"/api/v1/app-approvals/{approvalId}/revoke", "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.Forbidden, admin.Status);
        Assert.Contains("approver role", (string)admin.Body["error"]!);
        Assert.Null(new AppApprovalStore(Definitions).All().Single().RevokedAt);
    }

    [Fact]
    public async Task An_instance_and_an_app_are_removed_only_when_the_stores_rules_allow_and_each_refusal_is_a_message()
    {
        Seed();
        var audit = new StateAuditLog(State);

        // the portal mounts acme: the store refuses, naming the portal; nothing changes
        var mounted = await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme");
        Assert.Equal(HttpStatusCode.Conflict, mounted.Status);
        Assert.Contains("mounted by the portal instance(s) front", (string)mounted.Body["error"]!);
        Assert.NotNull(Store.Get("acme"));

        // the app still has instances: refused, naming them
        var used = await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/apps/front");
        Assert.Equal(HttpStatusCode.Conflict, used.Status);
        Assert.Contains("still has instances", (string)used.Body["error"]!);
        Assert.Contains("front", (string)used.Body["error"]!);

        // in the right order it works: the portal's instance, then the instance it mounted, then the apps
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/front")).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme")).Status);
        Assert.Null(Store.Get("acme"));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/apps/front")).Status);
        Assert.DoesNotContain(Store.Apps(), a => a.Name == "front");
        Assert.Contains(Store.AppVersions("front"), v => v.Version == 1);                         // the version stays on record
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme")).Status);

        // a publisher pairing may remove as well
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/app-instances/pinned")).Status);

        // the list no longer shows what was removed; the audit names the person and the outcome of each, refusals included
        var instances = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Select(i => (string)i!["name"]!).Order().ToArray();
        Assert.Equal(["safe"], instances);
        var entries = audit.Read(100);
        Assert.Contains(entries, a => a.Command == "app instance-remove" && a.Target == "acme" && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app instance-remove" && a.Target == "acme" && a.Outcome == "retired" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app remove" && a.Target == "front" && a.Outcome == "refused");
        Assert.Contains(entries, a => a.Command == "app remove" && a.Target == "front" && a.Outcome == "retired");
    }

    [Fact]
    public async Task Rolling_back_and_pinning_from_the_screen_use_the_routes_the_command_line_uses_and_the_list_follows()
    {
        Seed();
        var rolled = await CallAsync(ServerRole.Admin, HttpMethod.Post, "/api/v1/apps/notes/rollback", "{\"to\": 1}");
        Assert.Equal(HttpStatusCode.OK, rolled.Status);
        var rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.Equal(1, (int)rows["acme"]!["app_version"]!);                                      // an unpinned instance follows the rollback
        Assert.Equal(1, (int)(await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/notes")).Body["serving"]!);

        // pin acme to version 2 (the whole instance is sent, as the command does), then unpin it again
        var pin = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/acme", "{\"app\": \"notes\", \"pin\": 2, \"title\": \"Acme notes\"}");
        Assert.Equal(HttpStatusCode.OK, pin.Status);
        rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.Equal(2, (int)rows["acme"]!["app_version"]!);
        Assert.False((bool)rows["acme"]!["follows"]!);
        Assert.Equal("Acme notes", (string?)rows["acme"]!["title"]);

        var refused = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/acme", "{\"app\": \"notes\", \"pin\": 9}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("no version 9", (string)refused.Body["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Viewer, HttpMethod.Post, "/api/v1/apps/notes/rollback", "{\"to\": 2}")).Status);
    }

    [Fact]
    public async Task An_instance_is_made_from_the_screen_with_its_settings_and_bindings_and_the_list_reads_them_back()
    {
        Seed();
        var audit = new StateAuditLog(State);
        const string body = "{\"app\": \"notes\", \"pin\": 1, \"title\": \"Sales notes\", \"settings\": {\"embed.allowed_origins\": \"https://portal.example.com\", \"theme\": \"dark\"}, \"bindings\": {\"territories\": \"live\"}}";

        var made = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/sales", body);
        Assert.Equal(HttpStatusCode.OK, made.Status);
        Assert.Equal(1, (int)made.Body["version"]!);

        var row = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "sales")!;
        Assert.Equal("Sales notes", (string?)row["title"]);
        Assert.Equal(1, (int)row["pin"]!);
        Assert.Equal("dark", (string?)row["settings"]!["theme"]);
        Assert.Equal("https://portal.example.com", (string?)row["settings"]!["embed.allowed_origins"]);
        Assert.Equal("live", (string?)row["bindings"]!["territories"]);

        // the screen's pin control sends the whole instance back: the Gateway replaces it, so a call without the settings would drop them
        var pinned = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/sales", "{\"app\": \"notes\", \"pin\": 2, \"title\": \"Sales notes\", \"settings\": {\"embed.allowed_origins\": \"https://portal.example.com\", \"theme\": \"dark\"}, \"bindings\": {\"territories\": \"live\"}}");
        Assert.Equal(HttpStatusCode.OK, pinned.Status);
        Assert.Equal(2, (int)pinned.Body["version"]!);
        Assert.Equal("dark", Store.Get("sales")!.Settings["theme"]);
        await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/sales", "{\"app\": \"notes\", \"title\": \"Sales notes\"}");
        Assert.Empty(Store.Get("sales")!.Settings);
        Assert.Empty(Store.Get("sales")!.Bindings);

        // a binding for a model the app does not use, a setting the host cannot read, a name that is not a name, an app that is not published, a body without an app: each refused in the store's words, nothing saved
        var unlisted = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/other", "{\"app\": \"notes\", \"bindings\": {\"orders\": \"live\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, unlisted.Status);
        Assert.Contains("'orders' is not a model the app 'notes' uses", (string)unlisted.Body["error"]!);
        Assert.Contains("territories", (string)unlisted.Body["error"]!);
        var origin = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/other", "{\"app\": \"notes\", \"settings\": {\"embed.allowed_origins\": \"https://portal.example.com/path\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, origin.Status);
        Assert.Contains("is not an origin", (string)origin.Body["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/Not A Name", "{\"app\": \"notes\"}")).Status);
        Assert.Contains("has not been published", (string)(await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/other", "{\"app\": \"nothing\"}")).Body["error"]!);
        var noApp = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/other", "{\"title\": \"x\"}");
        Assert.Equal(HttpStatusCode.BadRequest, noApp.Status);
        Assert.Null(Store.Get("other"));

        // only a publisher or an administrator writes an instance; a viewer, an approver and an operator are refused and nothing is saved
        foreach (var role in new[] { ServerRole.Viewer, ServerRole.Approver, ServerRole.Operator, ServerRole.None })
        {
            var refused = await CallAsync(role, HttpMethod.Put, "/api/v1/app-instances/viewed", body);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        }
        Assert.Null(Store.Get("viewed"));

        // audited under the signed-in person: the saves and the refusals
        var entries = audit.Read(100);
        Assert.Contains(entries, a => a.Command == "app instance-save" && a.Target == "sales" && a.Outcome == "version 1" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app instance-save" && a.Target == "other" && a.Outcome == "refused" && a.UserName == "Ada");
    }

    /// <summary>The body a browser sends for a package: the manifest as text, every other file as base64, as `PUT /api/v1/apps/{name}` and `POST /api/v1/apps/check` take it.</summary>
    private static string Body(AppPackage package, string? note = null, string? by = null, string? egress = null)
    {
        var files = new JsonObject();
        foreach (var (path, bytes) in package.Files) files[path] = Convert.ToBase64String(bytes);
        return new JsonObject { ["manifest"] = package.ManifestYaml, ["files"] = files, ["note"] = note, ["by"] = by, ["egress"] = egress }.ToJsonString();
    }

    [Fact]
    public async Task A_version_is_checked_and_published_from_the_screen_with_the_files_of_a_folder_and_each_refusal_is_a_message()
    {
        Seed();
        var audit = new StateAuditLog(State);
        var next = Package("notes", "1.2.0", Models);

        // the check says what a publish would do and stores nothing: the version it would be, the trust, the hash the CLI computes for the same folder, no approval needed
        var check = await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(next));
        Assert.Equal(HttpStatusCode.OK, check.Status);
        Assert.True((bool)check.Body["ok"]!);
        Assert.Equal("notes", (string?)check.Body["name"]);
        Assert.Equal("1.2.0", (string?)check.Body["manifest_version"]);
        Assert.Equal(3, (int)check.Body["next_version"]!);
        Assert.Equal("sandbox", (string?)check.Body["trust"]);
        Assert.Equal(1, (int)check.Body["files"]!);
        var (bundle, manifestHash) = AppApprovalStore.Hashes(next);
        Assert.Equal(AppApprovals.ContentHash(bundle, manifestHash), (string?)check.Body["hash"]);
        Assert.Equal("territories", (string?)check.Body["about"]!["models"]![0]!["model"]);
        Assert.Empty(check.Body["diagnostics"]!.AsArray());
        Assert.Equal(2, Store.AppVersions("notes").Count);                                          // nothing was published
        Assert.DoesNotContain(audit.Read(100), a => a.Command == "app publish");

        // a manifest that names a file the folder does not have, one that does not read, a file that is not base64 and a body without a manifest: said in words, nothing stored
        var missing = await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(new AppPackage("schemaVersion: 1\nname: notes\nversion: 1.3.0\nentry: app.html\nroles: [viewer]\n")));
        Assert.False((bool)missing.Body["ok"]!);
        Assert.Contains(missing.Body["diagnostics"]!.AsArray(), d => ((string)d!["message"]!).Contains("app.html") && (string?)d["severity"] == "error");
        var unreadable = await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", "{\"manifest\": \"name: [oops\"}");
        Assert.False((bool)unreadable.Body["ok"]!);
        Assert.NotEmpty(unreadable.Body["diagnostics"]!.AsArray());
        Assert.Null(unreadable.Body["name"]);
        var notBase64 = await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", "{\"manifest\": \"name: notes\", \"files\": {\"index.html\": \"***\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, notBase64.Status);
        Assert.Contains("'index.html' is not base64", (string)notBase64.Body["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", "{}")).Status);

        // publishing: the new version serves at once, its note and its author are kept, the instance that follows moves, and the audit names the person
        var published = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes", Body(next, note: "from the screen"));
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal(3, (int)published.Body["version"]!);
        Assert.Equal(1, (int)published.Body["files"]!);
        Assert.Equal("Ada", (string?)published.Body["published_by"]);
        var detail = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/notes")).Body;
        Assert.Equal(3, (int)detail["serving"]!);
        Assert.Equal("from the screen", (string?)detail["versions"]![0]!["note"]);
        Assert.Equal(AppApprovals.ContentHash(bundle, manifestHash), (string?)detail["versions"]![0]!["hash"]);   // the hash the check showed is the hash of the version
        var acme = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "acme")!;
        Assert.Equal(3, (int)acme["app_version"]!);
        Assert.Equal(4, (int)(await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(next))).Body["next_version"]!);

        // a name that is not the manifest's, a manifest that does not read, a folder over the limit: refused in the store's words
        var named = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/other", Body(next));
        Assert.Equal(HttpStatusCode.BadRequest, named.Status);
        Assert.Contains("The manifest is named 'notes' and the call names 'other'", (string)named.Body["error"]!);
        var invalid = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes", "{\"manifest\": \"name: [oops\"}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        var traversal = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes", "{\"manifest\": \"" + next.ManifestYaml.Replace("\n", "\\n") + "\", \"files\": {\"../escape.html\": \"PGI+PC9iPg==\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, traversal.Status);
        Assert.Contains("is not a path inside the app", (string)traversal.Body["error"]!);
        Assert.Equal(3, Store.AppVersions("notes").Count);

        // only a publisher or an administrator checks and publishes; a viewer, an approver and an operator are refused and nothing is stored
        foreach (var role in new[] { ServerRole.Viewer, ServerRole.Approver, ServerRole.Operator, ServerRole.None })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(role, HttpMethod.Post, "/api/v1/apps/check", Body(next))).Status);
            var refused = await CallAsync(role, HttpMethod.Put, "/api/v1/apps/notes", Body(Package("notes", "1.9.0", Models)));
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Contains("publisher", (string)refused.Body["error"]!);
        }
        Assert.Equal(3, Store.AppVersions("notes").Count);

        var entries = audit.Read(100);
        Assert.Contains(entries, a => a.Command == "app publish" && a.Target == "notes" && a.Outcome == "version 3" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app publish" && a.Target == "other" && a.Outcome == "refused" && a.UserName == "Ada");
    }

    /// <summary>The samples a tester picks in the screen's folder chooser pass the check with nothing to fix, and the package a browser would send for a folder (every file but app.yaml, under its path) is the package the command line makes for it.</summary>
    [Fact]
    public async Task The_sample_apps_pass_the_check_a_screen_runs_and_hash_as_the_command_line_does()
    {
        Seed();
        var folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "DbDataGateway", "templates", "apps"))) folder = Path.GetDirectoryName(folder);
        var samples = Path.Combine(folder ?? throw new DirectoryNotFoundException("DbDataGateway/templates/apps"), "DbDataGateway", "templates", "apps");
        foreach (var sample in new[] { "territory-notes", "front-portal", "territory-views" })
        {
            var package = AppPackage.FromFolder(Path.Combine(samples, sample));
            var check = await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(package));
            Assert.Equal(HttpStatusCode.OK, check.Status);
            Assert.True((bool)check.Body["ok"]!, sample + ": " + check.Body["diagnostics"]);
            Assert.Empty(check.Body["diagnostics"]!.AsArray());
            Assert.Equal(sample, (string?)check.Body["name"]);
            Assert.Equal(package.Files.Count, (int)check.Body["files"]!);
            var (bundle, manifestHash) = AppApprovalStore.Hashes(package);
            Assert.Equal(AppApprovals.ContentHash(bundle, manifestHash), (string?)check.Body["hash"]);
        }
    }

    [Fact]
    public async Task A_trusted_version_is_refused_without_an_approval_and_is_approved_from_the_screen_for_the_origins_the_manifest_asks_for()
    {
        Seed();
        var audit = new StateAuditLog(State);
        var ledger = Package("ledger", "1.0.0", "trust: trusted\negress:\n  connect: [https://api.example.com]\n  img: [https://cdn.example.com]\n");

        // the check says it asks to be trusted, no approval stands, and which origins it asks for (what the approver chooses from)
        var check = (await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(ledger))).Body;
        Assert.Equal("trusted", (string?)check["trust"]);
        Assert.Null(check["approval_id"]);
        var asked = check["about"]!["egress"]!.AsArray().ToDictionary(e => (string)e!["directive"]!, e => string.Join(",", e!["origins"]!.AsArray().Select(o => (string)o!)));
        Assert.Equal("https://api.example.com", asked["connect"]);
        Assert.Equal("https://cdn.example.com", asked["img"]);

        // publishing it is refused, naming the hash an approver has to approve; nothing is stored and the refusal is audited
        var refused = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/apps/ledger", Body(ledger));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("asks to be trusted and has no approval for these exact files", (string)refused.Body["error"]!);
        Assert.Contains((string)check["hash"]!, (string)refused.Body["error"]!);
        Assert.Empty(Store.AppVersions("ledger"));

        // an origin the manifest did not ask for, a malformed list, and an approver who is no approver at the identity home: refused in words, no approval made
        var wider = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", Body(ledger, by: "Grace Hopper", egress: "connect=https://evil.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, wider.Status);
        Assert.Contains("https://evil.example.com, which 'ledger' does not ask for", (string)wider.Body["error"]!);
        var malformed = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", Body(ledger, by: "Grace Hopper", egress: "nonsense"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Contains("is not <directive>=<origin>", (string)malformed.Body["error"]!);
        var notApprover = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", Body(ledger, by: "Bob Babbage", egress: "connect=https://api.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, notApprover.Status);
        Assert.Contains("Approver", (string)notApprover.Body["error"]!);
        Assert.DoesNotContain(new AppApprovalStore(Definitions).All(), a => a.App == "ledger");

        // the publisher role alone cannot approve; the approver role approves these exact files for the origin chosen (not the other one the manifest asked for)
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/app-approvals", Body(ledger, by: "Grace Hopper"))).Status);
        var approved = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", Body(ledger, note: "reviewed the origins", by: "Grace Hopper", egress: "connect=https://api.example.com"));
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal("Grace Hopper", (string?)approved.Body["approved_by"]);
        Assert.Equal("Ada", (string?)approved.Body["author"]);
        var listed = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-approvals")).Body["approvals"]!.AsArray().Single(a => (string?)a!["app"] == "ledger")!;
        var egress = listed["egress"]!.AsArray().Single()!;
        Assert.Equal("connect", (string?)egress["directive"]);
        Assert.Equal(["https://api.example.com"], egress["origins"]!.AsArray().Select(o => (string)o!).ToArray());

        // the check now says an approval stands, and the publish goes through and runs as asked
        var standing = (await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(ledger))).Body;
        Assert.Equal((string?)approved.Body["id"], (string?)standing["approval_id"]);
        var published = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/apps/ledger", Body(ledger));
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Store.Save(new AppInstance("books", "ledger"), "ada-laptop");
        var books = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "books")!;
        Assert.True((bool)books["runs_as_asked"]!);

        // a changed byte is a new hash: the approval does not stand for it
        var changed = Package("ledger", "1.0.0", "trust: trusted\negress:\n  connect: [https://api.example.com]\n  img: [https://cdn.example.com]\n");
        var edited = new AppPackage(changed.ManifestYaml, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<html>changed</html>") });
        Assert.Null((await CallAsync(ServerRole.Publisher, HttpMethod.Post, "/api/v1/apps/check", Body(edited))).Body["approval_id"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/apps/ledger", Body(edited))).Status);

        var entries = audit.Read(100);
        Assert.Contains(entries, a => a.Command == "app publish" && a.Target == "ledger" && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app approve" && a.Target == "ledger" && a.Outcome == "refused");
        Assert.Contains(entries, a => a.Command == "app approve" && a.Target == "ledger" && a.Outcome.Contains("Grace Hopper", StringComparison.Ordinal) && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app publish" && a.Target == "ledger" && a.Outcome == "version 1");
    }

    private async Task<JsonArray> SetsAsync() => (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-navigation")).Body["navigation"]!.AsArray();

    private static string[] Members(JsonNode set) => set["entries"]!.AsArray().Select(e => (string)e!["instance"]!).ToArray();

    [Fact]
    public async Task A_navigation_set_is_made_reordered_renamed_and_removed_from_the_screen_and_each_refusal_of_the_store_is_a_message()
    {
        Seed();
        var audit = new StateAuditLog(State);

        // made: a titled, ordered list of instances, with the label a menu shows; the instances know which sets list them
        var made = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-navigation/team", "{\"title\": \"Team menu\", \"entries\": [{\"instance\": \"pinned\", \"label\": \"Pinned notes\"}, {\"instance\": \"acme\"}]}");
        Assert.Equal(HttpStatusCode.OK, made.Status);
        Assert.Equal(1, (int)made.Body["version"]!);
        Assert.Equal(2, (int)made.Body["entries"]!);
        var team = (await SetsAsync()).Single(x => (string?)x!["name"] == "team")!;
        Assert.Equal("Team menu", (string?)team["title"]);
        Assert.Equal(["pinned", "acme"], Members(team));
        Assert.Equal("Pinned notes", (string?)team["entries"]![0]!["label"]);
        Assert.Null(team["entries"]![1]!["label"]);
        var rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.Contains("team", rows["acme"]!["navigation"]!.AsArray().Select(v => (string)v!));

        // reordered: the same name is the next version, in the new order
        var reordered = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-navigation/team", "{\"title\": \"Team menu\", \"entries\": [{\"instance\": \"acme\"}, {\"instance\": \"pinned\", \"label\": \"Pinned notes\"}]}");
        Assert.Equal(HttpStatusCode.OK, reordered.Status);
        Assert.Equal(2, (int)reordered.Body["version"]!);
        Assert.Equal(["acme", "pinned"], Members((await SetsAsync()).Single(x => (string?)x!["name"] == "team")!));

        // refused in the store's words, and nothing changes: an unknown member, a repeated one, none at all, no title, a name that is not a name, a body that is not a set
        async Task<string> Refused(string name, string body)
        {
            var refused = await CallAsync(ServerRole.Publisher, HttpMethod.Put, $"/api/v1/app-navigation/{name}", body);
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            return (string)refused.Body["error"]!;
        }
        Assert.Contains($"'ghost' is not an instance published to {DefinitionStore.Production}", await Refused("team", "{\"title\": \"T\", \"entries\": [{\"instance\": \"acme\"}, {\"instance\": \"ghost\"}]}"));
        Assert.Contains("'acme' is listed twice in the set 'team'", await Refused("team", "{\"title\": \"T\", \"entries\": [{\"instance\": \"acme\"}, {\"instance\": \"acme\"}]}"));
        Assert.Contains("at least one instance", await Refused("team", "{\"title\": \"T\", \"entries\": []}"));
        Assert.Contains("needs a title", await Refused("team", "{\"title\": \" \", \"entries\": [{\"instance\": \"acme\"}]}"));
        Assert.Contains("A navigation set's name is lower-case", await Refused("Not A Name", "{\"title\": \"T\", \"entries\": [{\"instance\": \"acme\"}]}"));
        Assert.Contains("The body is a JSON object", await Refused("team", "{\"title\": \"T\"}"));
        Assert.Equal(["acme", "pinned"], Members((await SetsAsync()).Single(x => (string?)x!["name"] == "team")!));
        Assert.Equal(2, (await SetsAsync()).Count);                                                  // main and team

        // renamed: the new name is saved and the old one retired in one call; a set that is not served, and a name that is taken, are refused with nothing changed
        var renamed = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-navigation/crew", "{\"title\": \"Crew menu\", \"rename_from\": \"team\", \"entries\": [{\"instance\": \"pinned\"}, {\"instance\": \"acme\"}]}");
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal("team", (string?)renamed.Body["renamed_from"]);
        Assert.Equal(["crew", "main"], (await SetsAsync()).Select(x => (string)x!["name"]!).Order().ToArray());
        Assert.Equal(["pinned", "acme"], Members((await SetsAsync()).Single(x => (string?)x!["name"] == "crew")!));
        Assert.Contains("'team' is not served", await Refused("other", "{\"title\": \"T\", \"rename_from\": \"team\", \"entries\": [{\"instance\": \"acme\"}]}"));
        Assert.Contains("already a navigation set named 'main'", await Refused("main", "{\"title\": \"T\", \"rename_from\": \"crew\", \"entries\": [{\"instance\": \"acme\"}]}"));
        Assert.Equal(["crew", "main"], (await SetsAsync()).Select(x => (string)x!["name"]!).Order().ToArray());
        // a rename to itself is a plain change
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-navigation/crew", "{\"title\": \"Crew\", \"rename_from\": \"crew\", \"entries\": [{\"instance\": \"acme\"}]}")).Status);
        Assert.Equal(["crew", "main"], (await SetsAsync()).Select(x => (string)x!["name"]!).Order().ToArray());

        // removed: the instances are not touched; a set that is not served is a 404
        var removed = await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/app-navigation/crew");
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.True((bool)removed.Body["removed"]!);
        Assert.Equal(["main"], (await SetsAsync()).Select(x => (string)x!["name"]!).ToArray());
        Assert.NotNull(Store.Get("acme"));
        Assert.NotNull(Store.Get("pinned"));
        var gone = await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-navigation/crew");
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        Assert.Contains("'crew' is not served", (string)gone.Body["error"]!);

        // audited under the person: each save, rename, removal and refusal
        var entries = audit.Read(200);
        Assert.Contains(entries, a => a.Command == "app nav-save" && a.Target == "team" && a.Outcome == "version 1" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app nav-save" && a.Target == "team" && a.Outcome == "version 2" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app nav-save" && a.Target == "team" && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app nav-save" && a.Target == "crew" && a.Outcome.EndsWith("renamed from team", StringComparison.Ordinal));
        Assert.Contains(entries, a => a.Command == "app nav-remove" && a.Target == "team" && a.Outcome == "retired, renamed to crew");
        Assert.Contains(entries, a => a.Command == "app nav-remove" && a.Target == "crew" && a.Outcome == "retired" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app nav-remove" && a.Target == "crew" && a.Outcome == "refused");
    }

    [Fact]
    public async Task Writing_or_removing_a_navigation_set_needs_the_publisher_or_administrator_role_and_a_refusal_changes_nothing()
    {
        Seed();
        const string body = "{\"title\": \"Changed\", \"entries\": [{\"instance\": \"acme\"}]}";
        foreach (var role in new[] { ServerRole.Viewer, ServerRole.Approver, ServerRole.Operator, ServerRole.None })
        {
            var put = await CallAsync(role, HttpMethod.Put, "/api/v1/app-navigation/main", body);
            Assert.Equal(HttpStatusCode.Forbidden, put.Status);
            Assert.Contains("publisher", (string)put.Body["error"]!);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(role, HttpMethod.Delete, "/api/v1/app-navigation/main")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(role, HttpMethod.Put, "/api/v1/app-navigation/another", body)).Status);
        }
        var main = (await SetsAsync()).Single()!;
        Assert.Equal("Main menu", (string?)main["title"]);
        Assert.Equal(["acme", "pinned"], Members(main));

        // the screen's answer says what the caller may do, so the buttons follow; a publisher and an administrator may
        Assert.False((bool)(await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-navigation")).Body["caller"]!["can_publish"]!);
        Assert.True((bool)(await CallAsync(ServerRole.Publisher, HttpMethod.Get, "/api/v1/app-navigation")).Body["caller"]!["can_publish"]!);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-navigation/main", body)).Status);
        Assert.Equal(["acme"], Members((await SetsAsync()).Single()!));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-navigation/main")).Status);
        Assert.Empty(await SetsAsync());
    }

    [Fact]
    public async Task An_approval_is_revoked_by_an_approver_who_is_an_approver_at_the_identity_home_and_the_instance_stops_running_as_trusted()
    {
        var (approvalId, _) = Seed();
        var path = $"/api/v1/app-approvals/{approvalId}/revoke";

        // the person holds the role on this server, but names someone who does not: refused with the home's reason, nothing changes
        var notApprover = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Bob Babbage\"}");
        Assert.Equal(HttpStatusCode.BadRequest, notApprover.Status);
        Assert.Contains("Bob Babbage", (string)notApprover.Body["error"]!);
        Assert.Contains("Approver", (string)notApprover.Body["error"]!);
        var stranger = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"nobody at all\"}");
        Assert.Equal(HttpStatusCode.BadRequest, stranger.Status);
        Assert.Contains("not a user of the identity home", (string)stranger.Body["error"]!);
        // the person's own name is the default, and Ada is no user of the home
        var unnamed = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.Status);
        Assert.Contains("'Ada'", (string)unnamed.Body["error"]!);
        Assert.Null(new AppApprovalStore(Definitions).All().Single().RevokedAt);

        var revoked = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.OK, revoked.Status);
        Assert.True((bool)revoked.Body["revoked"]!);
        Assert.Equal("Grace Hopper", (string?)revoked.Body["revoked_by"]);
        Assert.NotNull(new AppApprovalStore(Definitions).All().Single().RevokedAt);

        // the list shows it revoked; the instance no longer runs as asked; the version has no standing approval
        var approval = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-approvals")).Body["approvals"]!.AsArray().Single()!;
        Assert.True((bool)approval["revoked"]!);
        Assert.NotNull(approval["revoked_at"]);
        var safe = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "safe")!;
        Assert.False((bool)safe["runs_as_asked"]!);
        Assert.Null(safe["approval_id"]);
        Assert.Null((await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/vault")).Body["versions"]![0]!["approval_id"]);

        // revoking again changes nothing and unknown ids are refused
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Grace Hopper\"}")).Status);
        var unknown = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals/apr_nothing/revoke", "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("no approval", (string)unknown.Body["error"]!);

        var audit = new StateAuditLog(State).Read(100);
        Assert.Contains(audit, a => a.Command == "app approval-revoke" && a.Target == approvalId && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(audit, a => a.Command == "app approval-revoke" && a.Target == "vault" && a.Outcome.StartsWith(approvalId, StringComparison.Ordinal) && a.UserName == "Ada");
    }

    [Fact]
    public async Task A_person_approves_a_version_from_the_screen_but_never_their_own_work()
    {
        Seed();
        // a new trusted version written by Grace herself: she may not approve it, a different approver may
        var users = new IdentityUserStore(State);
        var options = GatewayConfiguration.LoadOptions(DataDirectory);
        options.DataDirectory = DataDirectory;
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Heidi Hollerith", "heidi@example.invalid", UserRole.Viewer).Id), GatewayStoreModule.ServerIdOf(options, State), ServerRole.Approver);
        var own = Package("ledger", "1.0.0", "trust: trusted\n");
        // published as sandboxed would be refused for trust, so it is stored the way the gate allows: approved by Heidi first, published by Grace, then a second version approved by Grace herself is refused
        var gate = new AppApprovalStore(Definitions, approvers: new InMemoryApprovers().Add("u-heidi", "Heidi Hollerith", approver: true));
        gate.Approve(own, "Grace Hopper", "Heidi Hollerith");
        Store.PublishApp(own, "Grace Hopper", approvals: gate);

        var self = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"ledger\", \"version\": 1, \"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.BadRequest, self.Status);
        Assert.Contains("is the author of", (string)self.Body["error"]!);

        var other = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"vault\", \"version\": 1, \"by\": \"Heidi Hollerith\", \"note\": \"second look\"}");
        Assert.Equal(HttpStatusCode.OK, other.Status);
        Assert.Equal("Heidi Hollerith", (string?)other.Body["approved_by"]);
        Assert.Equal("Ada", (string?)other.Body["author"]);                                         // the signed-in person is named in the audit

        // a viewer cannot approve at all
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Viewer, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"vault\", \"version\": 1, \"by\": \"Heidi Hollerith\"}")).Status);
    }

    [Fact]
    public async Task A_publisher_sends_a_build_to_the_preview_channel_and_saves_a_preview_of_an_instance_that_production_does_not_see()
    {
        Seed();
        var next = Package("notes", "2.0.0", Models);

        // the app first, then the instance of it; production keeps its own version throughout
        var published = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes/channels/preview", Body(next, note: "from a workspace"));
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal("preview", (string?)published.Body["channel"]);
        Assert.Equal("1.1.0", Store.Resolve("acme")!.Manifest.Version);
        Assert.Null(Store.Resolve("acme~preview"));   // no instance of it in the preview channel yet
        var saved = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/acme/channels/preview", "{\"app\": \"notes\"}");
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("/apps/acme~preview/", (string?)saved.Body["path"]);
        Assert.Equal("2.0.0", Store.Resolve("acme~preview")!.Manifest.Version);
        Assert.Equal("1.1.0", Store.Resolve("acme")!.Manifest.Version);
        Assert.DoesNotContain(Store.List(), i => i.Name.Contains('~'));
        var rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray();
        Assert.Equal(1, rows.Count(r => (string?)r!["name"] == "acme"));                       // the list is production's

        // a preview is of an instance that exists in production; production and the preview are told apart by the path
        var orphan = await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/app-instances/ghost/channels/preview", "{\"app\": \"notes\"}");
        Assert.Equal(HttpStatusCode.BadRequest, orphan.Status);
        Assert.Contains("no instance 'ghost' in production", (string)orphan.Body["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes/channels/prod", Body(next))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(ServerRole.Publisher, HttpMethod.Put, "/api/v1/apps/notes/channels/Bad_Name", Body(next))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Viewer, HttpMethod.Put, "/api/v1/app-instances/acme/channels/preview", "{\"app\": \"notes\"}")).Status);

        // removing the preview leaves production alone, instance first and then the app
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/apps/notes/channels/preview")).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/app-instances/acme/channels/preview")).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/apps/notes/channels/preview")).Status);
        Assert.Null(Store.Resolve("acme~preview"));
        Assert.Equal("1.1.0", Store.Resolve("acme")!.Manifest.Version);
    }

    [Fact]
    public async Task The_address_of_an_instance_is_on_the_apps_host_when_the_gateway_serves_one()
    {
        Seed();
        var routes = new GatewayApps(Definitions, () => new InMemoryApprovers(), new StateAuditLog(State), appsAddress: () => new Uri("https://apps.example.test:8818/"));
        var result = await routes.HandleAsync("GET", "app-instances", [], null, ManagementCaller.Local("operator"), CancellationToken.None);
        Assert.Equal(200, result.Status);
        var acme = result.Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "acme")!;
        Assert.Equal("https://apps.example.test:8818/apps/acme/", (string?)acme["address"]);
        Assert.Equal("https://apps.example.test:8818/", (string?)result.Body["apps_address"]);
    }
}
